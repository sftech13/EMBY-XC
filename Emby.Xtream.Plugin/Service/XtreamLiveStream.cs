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

namespace Emby.Xtream.Plugin.Service
{
    internal class XtreamLiveStream : ILiveStream, IDisposable
    {
        // Keep chunks below the large-object-heap threshold. A 16 MiB queue gives each
        // viewer several seconds of protection from a bursty MPEG-TS source while keeping
        // memory strictly bounded (80 MiB for five simultaneous viewers of one channel).
        private const int ReadBufferSize = 64 * 1024;
        private const int MaxSubscriberBufferBytes = 16 * 1024 * 1024;
        private const int MaxReconnectAttempts = 3;

        private static readonly TimeSpan NoSubscriberGracePeriod = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ReadStallTimeout = TimeSpan.FromSeconds(15);

        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly object _fanoutLock = new object();
        private readonly Dictionary<long, FanoutSubscriber> _subscribers =
            new Dictionary<long, FanoutSubscriber>();

        private CancellationTokenSource _producerCancellation;
        private Task _producerTask;
        private int _producerGeneration;
        private long _nextSubscriberId;
        private bool _closeRequested;
        private volatile bool _disposed;

        public XtreamLiveStream(MediaSourceInfo mediaSource, string tunerHostId, HttpClient httpClient, ILogger logger = null)
        {
            MediaSource = mediaSource;
            _httpClient = httpClient;
            _logger = logger;
            UniqueId = Guid.NewGuid().ToString("N");
            TunerHostId = tunerHostId;
            OriginalStreamId = mediaSource.Id;
            DateOpened = DateTimeOffset.UtcNow;
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
        public bool SupportsCopyTo => true;

        // The upstream connection is deferred until Emby creates the first response writer.
        public Task Open(CancellationToken openCancellationToken)
        {
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

        private FanoutSubscriber Subscribe()
        {
            lock (_fanoutLock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(XtreamLiveStream));

                var subscriber = new FanoutSubscriber(
                    Interlocked.Increment(ref _nextSubscriberId),
                    MaxSubscriberBufferBytes);
                _subscribers.Add(subscriber.Id, subscriber);
                // A real response writer arriving after Close() is a player handoff/probe
                // race. Its presence is stronger evidence than Emby's transient count.
                _closeRequested = false;
                StartProducerLocked();
                _logger?.Info("[live-fanout] Viewer joined stream {0}; viewers={1}", OriginalStreamId, _subscribers.Count);
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
                disposeAfterUnsubscribe = remaining == 0 && _closeRequested && !_disposed;
            }

            subscriber.Complete(null, true);
            if (removed)
                _logger?.Info("[live-fanout] Viewer left stream {0}; viewers={1}", OriginalStreamId, remaining);

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
                        using (var response = await OpenUpstreamResponseAsync(MediaSource.Path, cancellationToken).ConfigureAwait(false))
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
                                        readCancellation.CancelAfter(ReadStallTimeout);
                                        bytesRead = await stream.ReadAsync(
                                            chunk.Buffer,
                                            0,
                                            ReadBufferSize,
                                            readCancellation.Token).ConfigureAwait(false);
                                        readCancellation.CancelAfter(Timeout.Infinite);
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
                                    Broadcast(chunk);
                                    chunk.Release();
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
                            ex.Message,
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

        private int GetSubscriberCount()
        {
            lock (_fanoutLock)
                return _subscribers.Count;
        }

        private void Broadcast(SharedChunk chunk)
        {
            FanoutSubscriber[] subscribers;
            lock (_fanoutLock)
            {
                subscribers = new FanoutSubscriber[_subscribers.Count];
                _subscribers.Values.CopyTo(subscribers, 0);
            }

            foreach (var subscriber in subscribers)
            {
                if (subscriber.TryEnqueue(chunk))
                    continue;

                var removed = false;
                lock (_fanoutLock)
                    removed = _subscribers.Remove(subscriber.Id);

                if (removed)
                {
                    _logger?.Warn(
                        "[live-fanout] Dropped slow viewer {0} from stream {1} after its {2} MiB buffer filled",
                        subscriber.Id,
                        OriginalStreamId,
                        MaxSubscriberBufferBytes / (1024 * 1024));
                }
            }
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
                var response = await _httpClient.GetAsync(
                    currentUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

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
            var subscriber = Subscribe();
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
            var subscriber = Subscribe();
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

        private sealed class SharedChunk
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

        private sealed class FanoutSubscriber
        {
            private readonly object _sync = new object();
            private readonly Queue<SharedChunk> _queue = new Queue<SharedChunk>();
            private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
            private readonly int _maxBufferBytes;

            private int _queuedBytes;
            private bool _completed;
            private Exception _completionError;

            public FanoutSubscriber(long id, int maxBufferBytes)
            {
                Id = id;
                _maxBufferBytes = maxBufferBytes;
            }

            public long Id { get; }

            public bool TryEnqueue(SharedChunk chunk)
            {
                lock (_sync)
                {
                    if (_completed)
                        return false;

                    if (_queuedBytes + chunk.Count > _maxBufferBytes)
                    {
                        _completed = true;
                        _completionError = new IOException("Live TV viewer buffer filled");
                        ReleaseQueuedChunksLocked();
                        _signal.Release();
                        return false;
                    }

                    chunk.AddReference();
                    _queue.Enqueue(chunk);
                    _queuedBytes += chunk.Count;
                    _signal.Release();
                    return true;
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
                            var chunk = _queue.Dequeue();
                            _queuedBytes -= chunk.Count;
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
                    _signal.Release();
                }
            }

            private void ReleaseQueuedChunksLocked()
            {
                while (_queue.Count > 0)
                    _queue.Dequeue().Release();
                _queuedBytes = 0;
            }
        }
    }
}
