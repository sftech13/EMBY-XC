using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Emby.Xtream.Plugin.Service;

namespace Emby.Xtream.Plugin.Tests
{
    internal static partial class Program
    {
        private static Task NfoTemporaryNamesStayShortAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                foreach (var name in new[] { new string('a', 239) + ".nfo", new string('\u00e9', 120) + ".nfo" })
                {
                    Assert(Encoding.UTF8.GetByteCount(name) <= 255, "the destination itself must fit the filesystem component limit");
                    var path = Path.Combine(root, name);
                    Assert(NfoWriter.WriteMovieNfo(path, "Long filename", new VodDetailInfo { Plot = "Original" }, 1955),
                        "a valid destination must not fail because its temporary filename is longer");
                    var original = File.ReadAllText(path);
                    var updated = "<movie><title>Updated atomically</title></movie>";
                    Assert(NfoWriter.WriteAtomically(path, updated, root, original), "atomic replacement must also work at the filename limit");
                    Assert(XElement.Load(path).Element("title")?.Value == "Updated atomically", "replacement must remain a complete XML file");
                    File.SetLastWriteTimeUtc(path, OldNfoTime);
                    var bytes = File.ReadAllBytes(path);
                    Assert(!NfoWriter.WriteAtomically(path, "not committed", root, original), "short temp names must retain the concurrent-save guard");
                    Assert(bytes.SequenceEqual(File.ReadAllBytes(path)) && File.GetLastWriteTimeUtc(path) == OldNfoTime,
                        "a deferred commit cannot change the destination or its timestamp");
                    Assert(!Directory.GetFiles(root, "*.tmp").Any(), "successful and deferred writes must clean short temporary files");
                }
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }

        private static async Task SharedViewerJoinPreservesRecoveringQueueAsync()
        {
            using (var client = new HttpClient(new QueueHandler()))
            using (var stream = new XtreamLiveStream(new MediaBrowser.Model.Dto.MediaSourceInfo { Id = "xtream_live_test" }, "test", client))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var viewers = (Dictionary<long, XtreamLiveStream.FanoutSubscriber>)typeof(XtreamLiveStream).GetField("_subscribers", flags).GetValue(stream);
                var gate = typeof(XtreamLiveStream).GetField("_fanoutLock", flags).GetValue(stream);
                var broadcast = typeof(XtreamLiveStream).GetMethod("BroadcastAsync", flags);
                var existing = new XtreamLiveStream.FanoutSubscriber(1, 2);
                var joining = new XtreamLiveStream.FanoutSubscriber(2, 2);
                var queued = CreateLiveChunk(2);
                Assert(existing.TryEnqueue(queued), "existing viewer's tiny queue must be full");
                queued.Release();
                lock (gate) viewers.Add(existing.Id, existing);
                var incoming = CreateLiveChunk(1);
                Task sending = null;
                try
                {
                    sending = (Task)broadcast.Invoke(stream, new object[] { incoming, timeout.Token });
                    Assert(!sending.IsCompleted, "a full single viewer must initially backpressure");
                    lock (gate) viewers.Add(joining.Id, joining);
                    var next = await joining.DequeueAsync(timeout.Token);
                    Assert(next.Count == 1, "the new viewer gets the current chunk before the old queue drains");
                    next.Release();
                    lock (gate)
                        Assert(viewers.ContainsKey(existing.Id) && viewers.ContainsKey(joining.Id),
                            "joining another response must not immediately evict the full older queue");
                    Assert(!sending.IsCompleted, "the old response has a bounded chance to drain");
                    var first = await existing.DequeueAsync(timeout.Token);
                    Assert(first.Count == 2, "the original queued bytes must not be skipped");
                    first.Release();
                    await sending.WaitAsync(timeout.Token);
                    next = await existing.DequeueAsync(timeout.Token);
                    Assert(next.Count == 1, "the recovering old response gets the current chunk exactly once");
                    next.Release();
                    Assert(joining.DescribeQueue().Contains("enqueuedBytes=1 "), "retrying topology must not duplicate data to the new response");
                }
                finally
                {
                    timeout.Cancel();
                    if (sending != null && !sending.IsCompleted)
                    {
                        try { await sending; }
                        catch (OperationCanceledException) { }
                    }
                    incoming.Release();
                    existing.Complete(null, true);
                    joining.Complete(null, true);
                }
            }
        }
    }
}
