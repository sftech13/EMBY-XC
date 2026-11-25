using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;

namespace Emby.Xtream.Plugin.Service
{
    internal class XtreamLiveStream : ILiveStream, IDisposable
    {
        // Keep chunks below the large-object-heap threshold. Shared viewers get a bounded
        // 64 MiB queue for transient startup/probing pauses (up to 320 MiB for five viewers).
        // A sole viewer uses upstream backpressure instead of being disconnected at this cap.
        private const int ReadBufferSize = 64 * 1024;
        private const int MaxSubscriberBufferBytes = 64 * 1024 * 1024;
        private const int MaxReconnectAttempts = 3;

        private static readonly TimeSpan NoSubscriberGracePeriod = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ReadStallTimeout = TimeSpan.FromSeconds(15);
        internal TimeSpan HandoffGracePeriod { get; set; } = TimeSpan.FromSeconds(2);

        private readonly HttpClient _httpClient;
        private readonly string _upstreamUrl;
        private readonly string _localApiUrl;
        private readonly string _upstreamUserAgent;
        private readonly ILogger _logger;
        private readonly Action<string, int> _onReadStall;
        private readonly object _fanoutLock = new object();
        private readonly Dictionary<long, FanoutSubscriber> _subscribers =
            new Dictionary<long, FanoutSubscriber>();

        private CancellationTokenSource _producerCancellation;
        private Task _producerTask;
        private int _producerGeneration;
        private long _nextSubscriberId;
        private bool _closeRequested;
        private volatile bool _disposed;

        public XtreamLiveStream(MediaSourceInfo mediaSource, string tunerHostId, HttpClient httpClient,
            ILogger logger = null, Action<string, int> onReadStall = null, string localApiUrl = null)
            : this(mediaSource, tunerHostId, httpClient, typeof(ILiveStream), logger, onReadStall, localApiUrl)
        {
        }

        internal XtreamLiveStream(
            MediaSourceInfo mediaSource,
            string tunerHostId,
            HttpClient httpClient,
            Type runtimeLiveStreamContract,
            ILogger logger = null,
            Action<string, int> onReadStall = null,
            string localApiUrl = null)
        {
            MediaSource = mediaSource;
            _httpClient = httpClient;
            _upstreamUrl = mediaSource.Path;
            _localApiUrl = localApiUrl;
            if (mediaSource.RequiredHttpHeaders != null &&
                mediaSource.RequiredHttpHeaders.TryGetValue("User-Agent", out var upstreamUserAgent))
                _upstreamUserAgent = upstreamUserAgent;
            _logger = logger;
            _onReadStall = onReadStall;
            UniqueId = Guid.NewGuid().ToString("N");
            TunerHostId = tunerHostId;
            OriginalStreamId = mediaSource.Id;
            DateOpened = DateTimeOffset.UtcNow;
            // Emby 4.10 registers even the first viewer through AddConsumer. Starting
            // at one there double-counts that viewer, so its single RemoveConsumer
            // leaves the stream permanently open and eventually exhausts TunerCount.
            // Emby 4.8/4.9 has no AddConsumer interface method and expects the newly
            // opened stream to report its first consumer immediately.
            _consumerCount = GetInitialConsumerCount(runtimeLiveStreamContract);
        }

        internal static int GetInitialConsumerCount(Type liveStreamContract)
        {
            return liveStreamContract != null &&
                   liveStreamContract.GetMethod("AddConsumer", new[] { typeof(string) }) != null
                ? 0
                : 1;
        }

        // Emby 4.10 added AddConsumer(string)/RemoveConsumer(string) to ILiveStream and made
        // ConsumerCount read-only. Declaring these as virtual (not explicit interface impl) lets
        // the CLR find them via vtable name+signature fallback on 4.10 without requiring
        // compile-time knowledge of the 4.10 interface — so the same binary works on 4.8/4.9
        // (where ILiveStream has no AddConsumer slot) and on 4.10 (where it does).
        private int _consumerCount;
        public int ConsumerCount
        {
            get => Volatile.Read(ref _consumerCount);
            set => Interlocked.Exchange(ref _consumerCount, Math.Max(0, value));
        }

        public virtual void AddConsumer(string id) => Interlocked.Increment(ref _consumerCount);

        public virtual void RemoveConsumer(string id)
        {
            // Emby can issue a duplicate removal while a client is rapidly changing channels.
            // Never expose -1: Emby's close path relies on the count reaching zero.
            int current;
            do
            {
                current = Volatile.Read(ref _consumerCount);
                if (current == 0)
                    return;
            }
            while (Interlocked.CompareExchange(ref _consumerCount, current - 1, current) != current);
        }

        public string OriginalStreamId { get; set; }
        public string TunerHostId { get; }
        public bool EnableStreamSharing => true;
        public MediaSourceInfo MediaSource { get; set; }
        public string UniqueId { get; }
        public DateTimeOffset DateOpened { get; }
        // Provider HLS is a playlist rather than one continuous byte stream. Let Emby or
        // the client consume that URL directly; the MPEG-TS fan-out copier is not suitable
        // for repeatedly refreshed HLS manifests.
        public bool SupportsCopyTo => !string.Equals(
            MediaSource?.Container,
            "hls",
            StringComparison.OrdinalIgnoreCase);

        // The upstream connection is deferred until Emby creates the first response writer.
        public Task Open(CancellationToken openCancellationToken)
        {
            openCancellationToken.ThrowIfCancellationRequested();
            if (_localApiUrl != null && SupportsCopyTo)
            {
                // Capture the upstream independently: the producer must never request
                // its own relay URL after Emby chooses remuxing/transcoding.
                MediaSource.Path = LiveStreamRouting.BuildLocalRelayUrl(_localApiUrl, UniqueId, MediaSource.Container);
                MediaSource.SupportsDirectPlay = false;
                MediaSource.Protocol = MediaProtocol.Http;
                MediaSource.IsRemote = false;
                _logger?.Info("[live-routing] Shared local relay ready stream={0} instance={1}; provider connection deferred", OriginalStreamId, UniqueId);
            }
            _logger?.Info("[XtreamLiveStream] Open called (fan-out connection deferred)");
            return Task.CompletedTask;
        }

        public Task Close()
        {
            var disposeNow = false;
            lock (_fanoutLock)
            {
                if (_disposed)
                    return Task.CompletedTask;

                _closeRequested = true;
                disposeNow = _subscribers.Count == 0;
                if (!disposeNow)
                {
                    // Emby's consumer count can briefly reach zero during a client handoff
                    // even while one or more HTTP response writers are still active. Let the
                    // writers, rather than the advisory consumer count, own final disposal.
                    _logger?.Info(
                        "[live-fanout] Close deferred for stream {0}; active viewers={1}",
                        OriginalStreamId,
                        _subscribers.Count);
                }
            }

            if (disposeNow)
                Dispose();
            return Task.CompletedTask;
        }

        private FanoutSubscriber Subscribe(string sink)
        {
            lock (_fanoutLock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(XtreamLiveStream));

                if (_subscribers.Count == 1)
                    foreach (var existing in _subscribers.Values)
                        existing.ObserveSoleViewer();

                var subscriber = new FanoutSubscriber(
                    Interlocked.Increment(ref _nextSubscriberId),
                    MaxSubscriberBufferBytes, sink);
                _subscribers.Add(subscriber.Id, subscriber);
                foreach (var existing in _subscribers.Values)
                    existing.NotifyTopologyChanged();
                // A real response writer arriving after Close() is a player handoff/probe
                // race. Its presence is stronger evidence than Emby's transient count.
                _closeRequested = false;
                StartProducerLocked();
                _logger?.Info("[live-fanout] Viewer joined stream {0}; viewers={1}; responseConsumer={2} instance={3} sink={4} embyConsumers={5}",
                    OriginalStreamId, _subscribers.Count, subscriber.Id, UniqueId, sink, ConsumerCount);
                return subscriber;
            }
        }

        private void Unsubscribe(FanoutSubscriber subscriber)
        {
            if (subscriber == null)
                return;

            var removed = false;
            var remaining = 0;
            var disposeAfterUnsubscribe = false;
            lock (_fanoutLock)
            {
                removed = _subscribers.Remove(subscriber.Id);
                remaining = _subscribers.Count;
                foreach (var existing in _subscribers.Values)
                    existing.NotifyTopologyChanged();
                disposeAfterUnsubscribe = remaining == 0 && _closeRequested && !_disposed;
            }

            subscriber.Complete(null, true);
            if (removed)
                _logger?.Info("[live-fanout] Viewer left stream {0}; viewers={1}; responseConsumer={2} instance={3}; {4}",
                    OriginalStreamId, remaining, subscriber.Id, UniqueId, subscriber.DescribeQueue());

            if (disposeAfterUnsubscribe)
                Dispose();
        }

        private void StartProducerLocked()
        {
            if (_disposed || (_producerTask != null && !_producerTask.IsCompleted))
                return;

            _producerCancellation?.Dispose();
            _producerCancellation = new CancellationTokenSource();
            var generation = ++_producerGeneration;
            var token = _producerCancellation.Token;
            _producerTask = Task.Run(() => RunProducerAsync(generation, token));
        }

        private async Task RunProducerAsync(int generation, CancellationToken cancellationToken)
        {
            Exception terminalError = null;
            var reconnectAttempts = 0;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (GetSubscriberCount() == 0)
                    {
                        await Task.Delay(NoSubscriberGracePeriod, cancellationToken).ConfigureAwait(false);
                        if (GetSubscriberCount() == 0)
                            return;
                    }

                    try
                    {
                        var sw = Stopwatch.StartNew();
                        using (var response = await OpenUpstreamResponseAsync(_upstreamUrl, cancellationToken).ConfigureAwait(false))
                        {
                            _logger?.Info("[stream-timing] Fanout.HttpGet={0}ms status={1}", sw.ElapsedMilliseconds, (int)response.StatusCode);
                            response.EnsureSuccessStatusCode();

                            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                            {
                                _logger?.Info("[live-fanout] Upstream connected for stream {0}", OriginalStreamId);

                                while (!cancellationToken.IsCancellationRequested)
                                {
                                    if (GetSubscriberCount() == 0)
                                    {
                                        await Task.Delay(NoSubscriberGracePeriod, cancellationToken).ConfigureAwait(false);
                                        if (GetSubscriberCount() == 0)
                                            return;
                                    }

                                    var chunk = new SharedChunk(ArrayPool<byte>.Shared.Rent(ReadBufferSize));
                                    int bytesRead;
                                    try
                                    {
                                        // A healthy live MPEG-TS source produces data continuously.
                                        // Reconnect a wedged upstream socket instead of leaving every
                                        // viewer spinning forever with an empty playback buffer.
                                        bytesRead = await ReadUpstreamAsync(
                                            stream,
                                            chunk.Buffer,
                                            readCancellation,
                                            cancellationToken,
                                            ReadStallTimeout).ConfigureAwait(false);
                                    }
                                    catch
                                    {
                                        chunk.Release();
                                        throw;
                                    }

                                    if (bytesRead == 0)
                                    {
                                        chunk.Release();
                                        throw new EndOfStreamException("Xtream live source ended unexpectedly");
                                    }

                                    chunk.Count = bytesRead;
                                    reconnectAttempts = 0;
                                    try
                                    {
                                        await BroadcastAsync(chunk, cancellationToken).ConfigureAwait(false);
                                    }
                                    finally
                                    {
                                        chunk.Release();
                                    }
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        var failureMessage = ex.Message;
                        if (ex is UpstreamReadStallException)
                        {
                            var viewers = GetSubscriberCount();
                            failureMessage += " activeViewers=" + viewers;
                            if (viewers > 0 && !cancellationToken.IsCancellationRequested)
                                _onReadStall?.Invoke(OriginalStreamId, viewers);
                        }
                        reconnectAttempts++;
                        if (reconnectAttempts > MaxReconnectAttempts)
                        {
                            terminalError = ex;
                            _logger?.ErrorException(
                                "[live-fanout] Upstream failed after reconnect attempts for stream " + OriginalStreamId,
                                ex);
                            break;
                        }

                        var delayMs = 500 * reconnectAttempts;
                        _logger?.Warn(
                            "[live-fanout] Upstream interrupted for stream {0}: {1}; reconnecting in {2}ms ({3}/{4})",
                            OriginalStreamId,
                            failureMessage,
                            delayMs,
                            reconnectAttempts,
                            MaxReconnectAttempts);
                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal disposal or shutdown.
            }
            finally
            {
                if (terminalError != null)
                    CompleteAllSubscribers(terminalError);

                CancellationTokenSource cancellationToDispose = null;
                lock (_fanoutLock)
                {
                    if (_producerGeneration == generation)
                    {
                        cancellationToDispose = _producerCancellation;
                        _producerCancellation = null;
                        _producerTask = null;

                        // A viewer can arrive in the narrow window after the no-viewer check
                        // but before this task exits. Start a fresh producer for that viewer.
                        if (!_disposed && terminalError == null && _subscribers.Count > 0)
                            StartProducerLocked();
                    }
                }

                cancellationToDispose?.Dispose();
            }
        }

        internal static async Task<int> ReadUpstreamAsync(
            Stream stream, byte[] buffer, CancellationTokenSource readCancellation,
            CancellationToken producerCancellation, TimeSpan stallTimeout)
        {
            var started = Stopwatch.GetTimestamp();
            readCancellation.CancelAfter(stallTimeout);
            try
            {
                return await stream.ReadAsync(buffer, 0, buffer.Length, readCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (
                !producerCancellation.IsCancellationRequested && readCancellation.IsCancellationRequested)
            {
                var elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                throw new UpstreamReadStallException(elapsedMs, stallTimeout.TotalMilliseconds, ex);
            }
            finally
            {
                readCancellation.CancelAfter(Timeout.Infinite);
            }
        }

        internal sealed class UpstreamReadStallException : TimeoutException
        {
            internal UpstreamReadStallException(double elapsedMs, double timeoutMs, Exception inner)
                : base(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Xtream live upstream read stalled: reason=read-stall idleMs={0:0} timeoutMs={1:0}",
                    elapsedMs, timeoutMs), inner)
            {
            }
        }

        private int GetSubscriberCount()
        {
            lock (_fanoutLock)
                return _subscribers.Count;
        }

        private async Task BroadcastAsync(SharedChunk chunk, CancellationToken cancellationToken)
        {
            // One upstream and one pending chunk throughout a handoff. Remember delivery
            // across topology changes so surviving writers never receive duplicate TS bytes.
            HashSet<long> delivered = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FanoutSubscriber[] subscribers;
                lock (_fanoutLock)
                {
                    subscribers = new FanoutSubscriber[_subscribers.Count];
                    _subscribers.Values.CopyTo(subscribers, 0);
                }
                if (subscribers.Length == 0)
                    return;

                if (subscribers.Length == 1)
                {
                    var sole = subscribers[0];
                    if (delivered != null && delivered.Contains(sole.Id))
                        return;
                    sole.ObserveSoleViewer();
                    if (await sole.EnqueueWithBackpressureAsync(chunk, () => GetSubscriberCount() == 1,
                        cancellationToken, () => LogQueueHealth(sole)).ConfigureAwait(false))
                    {
                        LogHandoffRecovery(sole);
                        return;
                    }
                    RemoveCompletedSubscriber(sole);
                    continue;
                }

                // Feed all healthy/new writers before waiting for the old full queue.
                if (delivered == null) delivered = new HashSet<long>();
                List<FanoutSubscriber> blocked = null;
                foreach (var subscriber in subscribers)
                {
                    if (delivered.Contains(subscriber.Id))
                        continue;
                    if (subscriber.TryEnqueue(chunk, false))
                    {
                        delivered.Add(subscriber.Id);
                        LogHandoffRecovery(subscriber);
                        LogQueueHealth(subscriber);
                    }
                    else if (!RemoveCompletedSubscriber(subscriber))
                    {
                        if (blocked == null) blocked = new List<FanoutSubscriber>();
                        blocked.Add(subscriber);
                    }
                }
                if (blocked == null)
                    return;

                foreach (var subscriber in blocked)
                {
                    if (subscriber.BeginHandoff(HandoffGracePeriod))
                        _logger?.Info("[live-fanout] Handoff grace started stream={0} instance={1} responseConsumer={2} graceMs={3:0}; {4}",
                            OriginalStreamId, UniqueId, subscriber.Id, HandoffGracePeriod.TotalMilliseconds, subscriber.DescribeQueue());
                }
                foreach (var subscriber in blocked)
                {
                    if (subscriber.HandoffRemaining > TimeSpan.Zero &&
                        await subscriber.EnqueueWithBackpressureAsync(chunk,
                            () => GetSubscriberCount() <= 1 || subscriber.HandoffRemaining > TimeSpan.Zero,
                            cancellationToken, () => LogQueueHealth(subscriber),
                            () => GetSubscriberCount() <= 1 ? TimeSpan.FromMilliseconds(250) : subscriber.HandoffRemaining).ConfigureAwait(false))
                    {
                        delivered.Add(subscriber.Id);
                        LogHandoffRecovery(subscriber);
                        continue;
                    }

                    string diagnostic = null;
                    lock (_fanoutLock)
                    {
                        // Recheck fullness and topology atomically. A disappearing peer
                        // must not turn the last writer into a shared-viewer eviction.
                        if (_subscribers.Count > 1 && _subscribers.ContainsKey(subscriber.Id) &&
                            subscriber.FailIfFull(chunk.Count, out diagnostic))
                            _subscribers.Remove(subscriber.Id);
                    }
                    if (diagnostic != null)
                        _logger?.Warn("[live-fanout] Dropped slow viewer {0} from stream {1} after its {2} MiB buffer filled; instance={3} reason=shared-queue-full handoffBudgetSpent={4}; {5}",
                            subscriber.Id, OriginalStreamId, MaxSubscriberBufferBytes / (1024 * 1024), UniqueId,
                            subscriber.HandoffBudgetSpent, diagnostic);
                    RemoveCompletedSubscriber(subscriber);
                }
                // Retry a recovered queue or a now-sole viewer, without recursive calls.
            }
        }

        private bool RemoveCompletedSubscriber(FanoutSubscriber subscriber)
        {
            lock (_fanoutLock)
            {
                if (!subscriber.IsCompleted)
                    return false;
                _subscribers.Remove(subscriber.Id);
                return true;
            }
        }

        private void LogHandoffRecovery(FanoutSubscriber subscriber)
        {
            if (subscriber.TakeHandoffRecovery())
                _logger?.Info("[live-fanout] Handoff queue recovered stream={0} instance={1} responseConsumer={2}; {3}",
                    OriginalStreamId, UniqueId, subscriber.Id, subscriber.DescribeQueue());
        }

        private void LogQueueHealth(FanoutSubscriber subscriber)
        {
            if (_logger != null && subscriber.TryDescribeStalledQueue(out var diagnostic))
                _logger.Info("[live-fanout] Queue health stream={0} instance={1} responseConsumer={2}; {3}",
                    OriginalStreamId, UniqueId, subscriber.Id, diagnostic);
        }

        private void CompleteAllSubscribers(Exception error)
        {
            FanoutSubscriber[] subscribers;
            lock (_fanoutLock)
            {
                subscribers = new FanoutSubscriber[_subscribers.Count];
                _subscribers.Values.CopyTo(subscribers, 0);
            }

            foreach (var subscriber in subscribers)
                subscriber.Complete(error, false);
        }

        private async Task<HttpResponseMessage> OpenUpstreamResponseAsync(string url, CancellationToken cancellationToken)
        {
            var currentUrl = url;

            for (var redirectCount = 0; redirectCount <= 10; redirectCount++)
            {
                HttpResponseMessage response;
                using (var request = new HttpRequestMessage(HttpMethod.Get, currentUrl))
                {
                    if (!string.IsNullOrWhiteSpace(_upstreamUserAgent))
                        request.Headers.TryAddWithoutValidation("User-Agent", _upstreamUserAgent);
                    response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);
                }

                if (!IsRedirect(response.StatusCode))
                    return response;

                var location = response.Headers.Location;
                if (location == null)
                    return response;

                var nextUri = location.IsAbsoluteUri
                    ? location
                    : new Uri(new Uri(currentUrl), location);

                _logger?.Info(
                    "[stream-timing] Upstream redirect {0} → {1}://{2}",
                    (int)response.StatusCode,
                    nextUri.Scheme,
                    nextUri.Host);

                response.Dispose();
                currentUrl = nextUri.ToString();
            }

            throw new HttpRequestException("Too many redirects while opening Xtream live stream");
        }

        private static bool IsRedirect(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.Moved ||
                   statusCode == HttpStatusCode.Redirect ||
                   statusCode == HttpStatusCode.RedirectMethod ||
                   statusCode == HttpStatusCode.TemporaryRedirect ||
                   (int)statusCode == 308;
        }

        public async Task CopyToAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            var subscriber = Subscribe("pipe");
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var chunk = await subscriber.DequeueAsync(cancellationToken).ConfigureAwait(false);
                    if (chunk == null)
                        break;

                    try
                    {
                        var writeBuffer = writer.GetMemory(chunk.Count);
                        chunk.Buffer.AsMemory(0, chunk.Count).CopyTo(writeBuffer);
                        writer.Advance(chunk.Count);

                        var flushResult = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                        if (flushResult.IsCompleted || flushResult.IsCanceled)
                            break;
                        subscriber.RecordWrite(chunk.Count);
                    }
                    finally
                    {
                        chunk.Release();
                    }
                }
            }
            finally
            {
                Unsubscribe(subscriber);
                await writer.CompleteAsync().ConfigureAwait(false);
            }
        }

        public async Task CopyToAsync(
            Stream writer,
            DateTimeOffset? wallClockStartTime,
            Action<SegmentedStreamSegmentInfo> onSegmentWritten,
            CancellationToken cancellationToken)
        {
            var subscriber = Subscribe("stream");
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var chunk = await subscriber.DequeueAsync(cancellationToken).ConfigureAwait(false);
                    if (chunk == null)
                        break;

                    try
                    {
                        await writer.WriteAsync(
                            chunk.Buffer,
                            0,
                            chunk.Count,
                            cancellationToken).ConfigureAwait(false);
                        subscriber.RecordWrite(chunk.Count);
                    }
                    finally
                    {
                        chunk.Release();
                    }
                }
            }
            finally
            {
                Unsubscribe(subscriber);
            }
        }

        public void Dispose()
        {
            FanoutSubscriber[] subscribers;
            lock (_fanoutLock)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _closeRequested = true;
                _producerCancellation?.Cancel();
                subscribers = new FanoutSubscriber[_subscribers.Count];
                _subscribers.Values.CopyTo(subscribers, 0);
                _subscribers.Clear();
            }

            var disposedError = new ObjectDisposedException(nameof(XtreamLiveStream));
            foreach (var subscriber in subscribers)
                subscriber.Complete(disposedError, true);
        }

        internal sealed class SharedChunk
        {
            private int _referenceCount = 1;

            public SharedChunk(byte[] buffer)
            {
                Buffer = buffer;
            }

            public byte[] Buffer { get; }
            public int Count { get; set; }

            public void AddReference()
            {
                Interlocked.Increment(ref _referenceCount);
            }

            public void Release()
            {
                if (Interlocked.Decrement(ref _referenceCount) == 0)
                    ArrayPool<byte>.Shared.Return(Buffer);
            }
        }

        internal sealed class FanoutSubscriber
        {
            private readonly object _sync = new object();
            private readonly Queue<QueuedChunk> _queue = new Queue<QueuedChunk>();
            private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
            private readonly SemaphoreSlim _spaceAvailable = new SemaphoreSlim(0, 1);
            private readonly int _maxBufferBytes;

            private int _queuedBytes;
            private bool _completed;
            private bool _producerWaitingForSpace;
            private Exception _completionError;
            private readonly long _joinedAt = Stopwatch.GetTimestamp();
            private long _lastDequeue, _lastWrite, _enqueuedBytes, _dequeuedBytes, _writtenBytes, _lastHealthLog;
            private int _peakBytes;
            private bool _handoffEligible, _handoffSpent, _handoffRecovered;
            private long _handoffDeadline;

            private readonly struct QueuedChunk
            {
                internal QueuedChunk(SharedChunk chunk) { Chunk = chunk; EnqueuedAt = Stopwatch.GetTimestamp(); }
                internal SharedChunk Chunk { get; }
                internal long EnqueuedAt { get; }
            }

            public FanoutSubscriber(long id, int maxBufferBytes, string sink = "test")
            {
                Id = id;
                _maxBufferBytes = maxBufferBytes;
                Sink = sink;
            }

            public long Id { get; }
            public string Sink { get; }
            public bool IsCompleted { get { lock (_sync) return _completed; } }
            public bool HandoffBudgetSpent { get { lock (_sync) return _handoffSpent; } }
            public TimeSpan HandoffRemaining
            {
                get
                {
                    lock (_sync)
                        return TimeSpan.FromSeconds(Math.Max(0, _handoffDeadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
                }
            }

            public void ObserveSoleViewer()
            {
                lock (_sync)
                    if (!_handoffSpent) _handoffEligible = true;
            }

            public bool BeginHandoff(TimeSpan grace)
            {
                lock (_sync)
                {
                    if (_completed || !_handoffEligible || _handoffSpent)
                        return false;
                    _handoffSpent = true;
                    _handoffDeadline = Stopwatch.GetTimestamp() + (long)(Math.Max(0, grace.TotalSeconds) * Stopwatch.Frequency);
                    return true;
                }
            }

            public bool TakeHandoffRecovery()
            {
                lock (_sync)
                {
                    if (!_handoffRecovered) return false;
                    _handoffRecovered = false;
                    return true;
                }
            }

            public void NotifyTopologyChanged()
            {
                lock (_sync) SignalProducerSpaceLocked();
            }

            public void RecordWrite(int bytes)
            {
                lock (_sync)
                {
                    _writtenBytes += bytes;
                    _lastWrite = Stopwatch.GetTimestamp();
                }
            }

            public string DescribeQueue()
            {
                lock (_sync) return DescribeQueueLocked();
            }

            private string DescribeQueueLocked()
            {
                var now = Stopwatch.GetTimestamp();
                return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "sink={0} queuedBytes={1} capacityBytes={2} peakBytes={3} oldestQueuedMs={4:0} dequeueIdleMs={5:0} writeIdleMs={6:0} enqueuedBytes={7} dequeuedBytes={8} writtenBytes={9} writeCompleted={10}",
                    Sink, _queuedBytes, _maxBufferBytes, _peakBytes,
                    _queue.Count == 0 ? 0 : (now - _queue.Peek().EnqueuedAt) * 1000.0 / Stopwatch.Frequency,
                    (now - (_lastDequeue == 0 ? _joinedAt : _lastDequeue)) * 1000.0 / Stopwatch.Frequency,
                    (now - (_lastWrite == 0 ? _joinedAt : _lastWrite)) * 1000.0 / Stopwatch.Frequency,
                    _enqueuedBytes, _dequeuedBytes, _writtenBytes, _lastWrite != 0);
            }

            public bool TryDescribeStalledQueue(out string diagnostic)
            {
                lock (_sync)
                {
                    var now = Stopwatch.GetTimestamp();
                    diagnostic = null;
                    if (now - (_lastHealthLog == 0 ? _joinedAt : _lastHealthLog) < 30L * Stopwatch.Frequency ||
                        (_queuedBytes < _maxBufferBytes / 2 &&
                         now - (_lastWrite == 0 ? _joinedAt : _lastWrite) < 5L * Stopwatch.Frequency))
                        return false;
                    _lastHealthLog = now;
                    diagnostic = DescribeQueueLocked();
                    return true;
                }
            }

            public bool TryEnqueue(SharedChunk chunk, bool failOnOverflow = true)
            {
                lock (_sync)
                {
                    if (_completed)
                        return false;

                    if (_queuedBytes + chunk.Count > _maxBufferBytes)
                    {
                        if (failOnOverflow) FailFullQueueLocked();
                        return false;
                    }

                    EnqueueLocked(chunk);
                    return true;
                }
            }

            private void EnqueueLocked(SharedChunk chunk)
            {
                chunk.AddReference();
                _queue.Enqueue(new QueuedChunk(chunk));
                _queuedBytes += chunk.Count;
                _enqueuedBytes += chunk.Count;
                _peakBytes = Math.Max(_peakBytes, _queuedBytes);
                _signal.Release();
            }

            public bool FailIfFull(int incomingBytes, out string diagnostic)
            {
                lock (_sync)
                {
                    diagnostic = null;
                    if (_completed || _queuedBytes + incomingBytes <= _maxBufferBytes)
                        return false;
                    diagnostic = DescribeQueueLocked();
                    FailFullQueueLocked();
                    return true;
                }
            }

            private void FailFullQueueLocked()
            {
                _completed = true;
                _completionError = new IOException("Live TV viewer buffer filled");
                ReleaseQueuedChunksLocked();
                SignalProducerSpaceLocked();
                _signal.Release();
            }

            public async Task<bool> EnqueueWithBackpressureAsync(
                SharedChunk chunk,
                Func<bool> continueBackpressure,
                CancellationToken cancellationToken,
                Action onWaiting = null,
                Func<TimeSpan> remainingWait = null)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lock (_sync)
                    {
                        if (_completed)
                            return false;

                        if (_queuedBytes + chunk.Count <= _maxBufferBytes)
                        {
                            _producerWaitingForSpace = false;
                            EnqueueLocked(chunk);
                            return true;
                        }

                        _producerWaitingForSpace = true;
                    }

                    if (continueBackpressure != null && !continueBackpressure())
                    {
                        lock (_sync)
                            _producerWaitingForSpace = false;
                        return false;
                    }

                    try
                    {
                        onWaiting?.Invoke();
                        // Periodically re-check subscriber topology. A second viewer
                        // must not inherit indefinite backpressure from a stalled first.
                        var wait = TimeSpan.FromMilliseconds(250);
                        if (remainingWait != null)
                        {
                            var remaining = remainingWait();
                            if (remaining < wait)
                                wait = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
                        }
                        await _spaceAvailable.WaitAsync(
                            wait,
                            cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_sync)
                            _producerWaitingForSpace = false;
                    }
                }
            }

            public async Task<SharedChunk> DequeueAsync(CancellationToken cancellationToken)
            {
                while (true)
                {
                    await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);

                    Exception error;
                    lock (_sync)
                    {
                        if (_queue.Count > 0)
                        {
                            var chunk = _queue.Dequeue().Chunk;
                            _queuedBytes -= chunk.Count;
                            _dequeuedBytes += chunk.Count;
                            _lastDequeue = Stopwatch.GetTimestamp();
                            // One freed packet is not a recovered backlog. Re-arm only
                            // after meaningful drainage, never on another join/leave.
                            if (_queuedBytes <= _maxBufferBytes / 2)
                            {
                                _handoffSpent = false;
                                _handoffEligible = false;
                                if (_handoffDeadline != 0)
                                {
                                    _handoffRecovered = true;
                                    _handoffDeadline = 0;
                                }
                            }
                            SignalProducerSpaceLocked();
                            return chunk;
                        }

                        if (!_completed)
                            continue;

                        error = _completionError;
                    }

                    if (error != null)
                        throw error;

                    return null;
                }
            }

            public void Complete(Exception error, bool discardBufferedData)
            {
                lock (_sync)
                {
                    if (_completed)
                    {
                        if (discardBufferedData)
                            ReleaseQueuedChunksLocked();
                        return;
                    }

                    _completed = true;
                    _completionError = error;
                    if (discardBufferedData)
                        ReleaseQueuedChunksLocked();
                    SignalProducerSpaceLocked();
                    _signal.Release();
                }
            }

            private void SignalProducerSpaceLocked()
            {
                if (!_producerWaitingForSpace)
                    return;

                try
                {
                    _spaceAvailable.Release();
                }
                catch (SemaphoreFullException)
                {
                    // A dequeue already signaled the single upstream producer.
                }
            }

            private void ReleaseQueuedChunksLocked()
            {
                while (_queue.Count > 0)
                    _queue.Dequeue().Chunk.Release();
                _queuedBytes = 0;
            }
        }
    }
}
