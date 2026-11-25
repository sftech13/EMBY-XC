using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Service;
using MediaBrowser.Model.Dto;

namespace Emby.Xtream.Plugin.Tests
{
    internal static partial class Program
    {
        // Exercise the real broadcaster with tiny queues and no provider connections.
        // Join/leave perform the same topology notifications as response subscriptions.
        private sealed class HandoffRig : IDisposable
        {
            private readonly HttpClient _client = new HttpClient(new QueueHandler());
            internal readonly XtreamLiveStream Stream;
            private readonly Dictionary<long, XtreamLiveStream.FanoutSubscriber> _viewers;
            private readonly object _gate;
            private readonly List<XtreamLiveStream.FanoutSubscriber> _all = new List<XtreamLiveStream.FanoutSubscriber>();
            private long _next;
            internal readonly CancellationTokenSource Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            internal HandoffRig()
            {
                Stream = new XtreamLiveStream(new MediaSourceInfo { Id = "handoff_test" }, "test", _client);
                Stream.HandoffGracePeriod = TimeSpan.FromMilliseconds(120);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                _viewers = (Dictionary<long, XtreamLiveStream.FanoutSubscriber>)typeof(XtreamLiveStream).GetField("_subscribers", flags).GetValue(Stream);
                _gate = typeof(XtreamLiveStream).GetField("_fanoutLock", flags).GetValue(Stream);
            }

            internal XtreamLiveStream.FanoutSubscriber Join(int capacity = 8)
            {
                var viewer = new XtreamLiveStream.FanoutSubscriber(++_next, capacity, "pipe");
                lock (_gate)
                {
                    if (_viewers.Count == 1)
                        foreach (var old in _viewers.Values) old.ObserveSoleViewer();
                    _viewers.Add(viewer.Id, viewer);
                    foreach (var item in _viewers.Values) item.NotifyTopologyChanged();
                }
                _all.Add(viewer);
                return viewer;
            }

            internal bool Contains(XtreamLiveStream.FanoutSubscriber viewer)
            {
                lock (_gate) return _viewers.ContainsKey(viewer.Id);
            }

            internal void Leave(XtreamLiveStream.FanoutSubscriber viewer)
            {
                typeof(XtreamLiveStream).GetMethod("Unsubscribe", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(Stream, new object[] { viewer });
            }

            internal Task Broadcast(XtreamLiveStream.SharedChunk chunk, CancellationToken? token = null)
            {
                return (Task)typeof(XtreamLiveStream).GetMethod("BroadcastAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(Stream, new object[] { chunk, token ?? Timeout.Token });
            }

            public void Dispose()
            {
                Timeout.Cancel();
                Stream.Dispose();
                foreach (var viewer in _all) viewer.Complete(null, true);
                _client.Dispose();
                Timeout.Dispose();
            }
        }

        private static XtreamLiveStream.SharedChunk FillLiveQueue(XtreamLiveStream.FanoutSubscriber viewer, int count)
        {
            var chunk = CreateLiveChunk(count);
            Assert(viewer.TryEnqueue(chunk), "test queue must accept the initial chunk");
            chunk.Release();
            return chunk;
        }

        private static int LiveChunkReferences(XtreamLiveStream.SharedChunk chunk) =>
            (int)typeof(XtreamLiveStream.SharedChunk).GetField("_referenceCount", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(chunk);

        private static async Task LiveResponseSubscriptionHandoffAsync()
        {
            using (var client = new HttpClient(new QueueHandler()))
            using (var stream = new XtreamLiveStream(new MediaSourceInfo { Id = "real_subscription_test" }, "test", client))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                // Suppress the producer only; use the real Subscribe/Broadcast/Dispose.
                // Reusing one immutable chunk fills the real 64 MiB logical queue
                // without allocating 64 MiB for this regression test.
                typeof(XtreamLiveStream).GetField("_producerTask", flags).SetValue(stream,
                    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
                var subscribe = typeof(XtreamLiveStream).GetMethod("Subscribe", flags);
                var broadcast = typeof(XtreamLiveStream).GetMethod("BroadcastAsync", flags);
                var old = (XtreamLiveStream.FanoutSubscriber)subscribe.Invoke(stream, new object[] { "pipe" });
                var buffered = CreateLiveChunk(64 * 1024);
                for (var i = 0; i < 1024; i++) Assert(old.TryEnqueue(buffered), "the production queue must retain its 64 MiB cap");
                buffered.Release();
                var incoming = CreateLiveChunk(64 * 1024);
                Task sending = null;
                try
                {
                    sending = (Task)broadcast.Invoke(stream, new object[] { incoming, timeout.Token });
                    var peer = (XtreamLiveStream.FanoutSubscriber)subscribe.Invoke(stream, new object[] { "stream" });
                    (await peer.DequeueAsync(timeout.Token)).Release();
                    Assert(!sending.IsCompleted && !old.IsCompleted && peer.Sink == "stream" && old.Sink == "pipe",
                        "a real second subscription must preserve the old queue and identify response sinks");
                    (await old.DequeueAsync(timeout.Token)).Release();
                    await sending.WaitAsync(timeout.Token);
                    Assert(old.HandoffBudgetSpent && old.HandoffRemaining > TimeSpan.Zero,
                        "one freed chunk retains the original deadline instead of renewing or prematurely ending the budget");
                    Assert(old.DescribeQueue().Contains("queuedBytes=67108864 capacityBytes=67108864"),
                        "the handoff cannot expand the original queue cap");
                }
                finally
                {
                    timeout.Cancel();
                    stream.Dispose();
                    if (sending != null)
                        try { await sending; } catch (OperationCanceledException) { }
                    Assert(LiveChunkReferences(buffered) == 0 && LiveChunkReferences(incoming) == 1,
                        "production subscription disposal returns all reused chunk references");
                    incoming.Release();
                }
            }
        }

        private static async Task LiveHandoffExpiryIsBoundedAsync()
        {
            using (var rig = new HandoffRig())
            {
                var old = rig.Join();
                var buffered = FillLiveQueue(old, 8);
                var incoming = CreateLiveChunk(1);
                try
                {
                    var sending = rig.Broadcast(incoming);
                    Assert(!sending.IsCompleted, "a sole full viewer backpressures instead of failing");
                    var timer = Stopwatch.StartNew();
                    var healthy = rig.Join();
                    var received = await healthy.DequeueAsync(rig.Timeout.Token);
                    Assert(!sending.IsCompleted && rig.Contains(old), "healthy response receives data before the grace wait completes");
                    received.Release();
                    await sending.WaitAsync(rig.Timeout.Token);
                    Assert(timer.ElapsedMilliseconds >= 80 && timer.ElapsedMilliseconds < 1500,
                        "a persistently stalled response gets a short, bounded handoff, not indefinite backpressure");
                    Assert(!rig.Contains(old) && rig.Contains(healthy), "only the persistently full response may be dropped");
                    Assert(LiveChunkReferences(buffered) == 0 && LiveChunkReferences(incoming) == 1,
                        "eviction and healthy drain release their own chunk references exactly once");
                    var failed = false;
                    try { await old.DequeueAsync(rig.Timeout.Token); }
                    catch (IOException) { failed = true; }
                    Assert(failed, "a bounded shared overflow remains an explicit response error");
                }
                finally { incoming.Release(); }
            }
        }

        private static async Task LiveHandoffPeerDepartureAsync()
        {
            using (var rig = new HandoffRig())
            {
                var old = rig.Join();
                FillLiveQueue(old, 8);
                var incoming = CreateLiveChunk(1);
                var sending = rig.Broadcast(incoming);
                try
                {
                    var peer = rig.Join();
                    (await peer.DequeueAsync(rig.Timeout.Token)).Release();
                    rig.Leave(peer);
                    await Task.Delay(220, rig.Timeout.Token);
                    Assert(!sending.IsCompleted && rig.Contains(old) && !old.IsCompleted,
                        "expiration cannot evict the last remaining writer");
                    (await old.DequeueAsync(rig.Timeout.Token)).Release();
                    await sending.WaitAsync(rig.Timeout.Token);
                    (await old.DequeueAsync(rig.Timeout.Token)).Release();
                    Assert(LiveChunkReferences(incoming) == 1, "the surviving response receives one copy");
                }
                finally
                {
                    rig.Stream.Dispose();
                    await sending.WaitAsync(rig.Timeout.Token);
                    incoming.Release();
                }
            }
        }

        private static async Task LiveHandoffCancellationAndDisposalAsync()
        {
            foreach (var cancel in new[] { true, false })
            using (var rig = new HandoffRig())
            using (var cancellation = new CancellationTokenSource())
            {
                rig.Stream.HandoffGracePeriod = TimeSpan.FromSeconds(2);
                var old = rig.Join();
                var buffered = FillLiveQueue(old, 8);
                var incoming = CreateLiveChunk(1);
                var sending = rig.Broadcast(incoming, cancellation.Token);
                try
                {
                    var peer = rig.Join();
                    (await peer.DequeueAsync(rig.Timeout.Token)).Release();
                    if (cancel) cancellation.Cancel();
                    else rig.Stream.Dispose();
                    try { await sending.WaitAsync(TimeSpan.FromSeconds(1)); }
                    catch (OperationCanceledException) when (cancel) { }
                    Assert(!old.IsCompleted || !rig.Contains(old), "disposal removes active subscriptions");
                }
                finally
                {
                    cancellation.Cancel();
                    rig.Stream.Dispose();
                    try { await sending; } catch (OperationCanceledException) { }
                    Assert(LiveChunkReferences(buffered) == 0 && LiveChunkReferences(incoming) == 1,
                        "cancellation/disposal cannot leak a queued or pending shared chunk");
                    incoming.Release();
                }
            }
        }

        private static async Task LiveHandoffBudgetDoesNotRenewAsync()
        {
            var viewer = new XtreamLiveStream.FanoutSubscriber(1, 8);
            try
            {
                FillLiveQueue(viewer, 2);
                FillLiveQueue(viewer, 6);
                viewer.ObserveSoleViewer();
                Assert(viewer.BeginHandoff(TimeSpan.FromMilliseconds(100)), "first transition gets a grace budget");
                for (var i = 0; i < 6; i++)
                {
                    viewer.ObserveSoleViewer();
                    viewer.NotifyTopologyChanged();
                    Assert(!viewer.BeginHandoff(TimeSpan.FromSeconds(2)), "joins/leaves cannot extend an already spent budget");
                }
                (await viewer.DequeueAsync(CancellationToken.None)).Release();
                Assert(!viewer.TakeHandoffRecovery() && viewer.HandoffBudgetSpent,
                    "freeing a little space cannot count as backlog recovery or reset the deadline");
                await Task.Delay(150);
                Assert(viewer.HandoffRemaining == TimeSpan.Zero, "the original deadline still expires");
                (await viewer.DequeueAsync(CancellationToken.None)).Release();
                viewer.TakeHandoffRecovery();
                viewer.ObserveSoleViewer();
                Assert(viewer.BeginHandoff(TimeSpan.FromMilliseconds(100)), "meaningful drainage permits a later independent handoff");
            }
            finally { viewer.Complete(null, true); }
        }

        private static async Task LiveHandoffChurnCannotExtendAsync()
        {
            using (var rig = new HandoffRig())
            {
                rig.Stream.HandoffGracePeriod = TimeSpan.FromMilliseconds(200);
                var old = rig.Join();
                FillLiveQueue(old, 8);
                var incoming = CreateLiveChunk(1);
                var sending = rig.Broadcast(incoming);
                try
                {
                    var peer = rig.Join();
                    (await peer.DequeueAsync(rig.Timeout.Token)).Release();
                    while (!old.HandoffBudgetSpent)
                        await Task.Delay(1, rig.Timeout.Token);
                    var timer = Stopwatch.StartNew();
                    var transitions = 0;
                    while (!sending.IsCompleted && transitions++ < 40)
                    {
                        rig.Leave(peer);
                        peer = rig.Join();
                        await Task.Delay(10, rig.Timeout.Token);
                    }
                    await sending.WaitAsync(rig.Timeout.Token);
                    Assert(!rig.Contains(old) && rig.Contains(peer) && timer.ElapsedMilliseconds < 1500,
                        "rapid transitions through one and two responses cannot renew the old backlog's fixed deadline");
                    // A peer that joined after this broadcast's snapshot starts on the
                    // next producer chunk; do not assume it received this one.
                    rig.Stream.Dispose();
                    Assert(LiveChunkReferences(incoming) == 1, "each short-lived response releases its delivered or pending reference");
                }
                finally
                {
                    rig.Stream.Dispose();
                    await sending.WaitAsync(rig.Timeout.Token);
                    incoming.Release();
                }
            }
        }

        private static async Task LiveQueueDiagnosticsTrackDrainAsync()
        {
            var viewer = new XtreamLiveStream.FanoutSubscriber(8, 8, "pipe");
            try
            {
                FillLiveQueue(viewer, 6);
                await Task.Delay(15);
                var queued = viewer.DescribeQueue();
                Assert(queued.Contains("queuedBytes=6 ") && queued.Contains("peakBytes=6 ") &&
                    !queued.Contains("oldestQueuedMs=0 "), "queue diagnostics include the actual oldest queued age and byte cap");
                var chunk = await viewer.DequeueAsync(CancellationToken.None);
                try
                {
                    var dequeued = viewer.DescribeQueue();
                    Assert(dequeued.Contains("dequeuedBytes=6 ") && dequeued.Contains("writtenBytes=0 ") &&
                        dequeued.Contains("writeCompleted=False"), "dequeue is not proof that Emby's response writer drained");
                    viewer.RecordWrite(chunk.Count);
                    Assert(viewer.DescribeQueue().Contains("writtenBytes=6 writeCompleted=True"), "completed writes have separate counters");
                }
                finally { chunk.Release(); }
            }
            finally { viewer.Complete(null, true); }
        }

        private static async Task LiveSharedOverflowAndDrainRaceAsync()
        {
            using (var rig = new HandoffRig())
            {
                var a = rig.Join();
                var b = rig.Join();
                // These have not inherited a sole-viewer backlog.
                FillLiveQueue(b, 8);
                var incoming = CreateLiveChunk(1);
                try
                {
                    var sending = rig.Broadcast(incoming);
                    await sending.WaitAsync(TimeSpan.FromSeconds(1));
                    Assert(rig.Contains(a) && !rig.Contains(b), "a steady shared slow response remains isolated without a handoff delay");
                    (await a.DequeueAsync(rig.Timeout.Token)).Release();
                }
                finally { incoming.Release(); }
            }
            var viewer = new XtreamLiveStream.FanoutSubscriber(1, 8);
            try
            {
                FillLiveQueue(viewer, 8);
                (await viewer.DequeueAsync(CancellationToken.None)).Release();
                Assert(!viewer.FailIfFull(1, out var diagnostic) && diagnostic == null && !viewer.IsCompleted,
                    "space freed at the deadline must be rechecked under the queue lock before eviction");
            }
            finally { viewer.Complete(null, true); }
        }
    }
}
