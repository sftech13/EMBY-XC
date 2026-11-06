using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Service;

namespace Emby.Xtream.Plugin.Tests
{
    internal static partial class Program
    {
        private static TaskCompletionSource<bool> QueueSignal() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task SyncQueueWaitsInsteadOfSkippingAsync()
        {
            var queue = new SyncRunQueue();
            var release = QueueSignal();
            var waiting = QueueSignal();
            var order = new List<string>();
            var checkpoint = 0;
            var resumed = 0;
            var first = queue.RunAsync(async () =>
            {
                order.Add("movies");
                await release.Task;
                checkpoint = 42;
                order.Add("movies saved");
            }, CancellationToken.None);
            var second = queue.RunAsync(() =>
            {
                Assert(checkpoint == 42, "the queued run must read its predecessor's completed checkpoint");
                order.Add("documentaries");
                return Task.CompletedTask;
            }, CancellationToken.None, () => waiting.TrySetResult(true), () => resumed++);

            try
            {
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert(!second.IsCompleted && order.Count == 1, "a busy pair must wait, not skip or overlap");
            }
            finally { release.TrySetResult(true); }
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
            Assert(string.Join(",", order) == "movies,movies saved,documentaries" && resumed == 1,
                "the queued job must actually execute exactly once after its predecessor saves");
        }

        private static async Task SyncQueueCancellationDoesNotStartWorkAsync()
        {
            var queue = new SyncRunQueue();
            var release = QueueSignal();
            var first = queue.RunAsync(() => release.Task, CancellationToken.None);
            using (var cancellation = new CancellationTokenSource())
            {
                var called = false;
                var second = queue.RunAsync(() =>
                {
                    called = true;
                    return Task.CompletedTask;
                }, cancellation.Token);
                cancellation.Cancel();
                try
                {
                    await ExpectQueueCancellationAsync(second);
                    Assert(!called && !first.IsCompleted,
                        "canceling a waiter must neither start its work nor interrupt the active sync");
                }
                finally { release.TrySetResult(true); }
            }
            await first;
            var nextCalled = false;
            await queue.RunAsync(() => { nextCalled = true; return Task.CompletedTask; }, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert(nextCalled, "a canceled waiter must not leak the pair's execution slot");
        }

        private static async Task SyncQueuePreCanceledWorkNeverStartsAsync()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                var called = false;
                var queue = new SyncRunQueue();
                await ExpectQueueCancellationAsync(queue.RunAsync(() =>
                {
                    called = true;
                    return Task.CompletedTask;
                }, cancellation.Token));
                Assert(!called, "an already canceled scheduled task must not start configuration reads or file work");
                await queue.RunAsync(() => Task.CompletedTask, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(2));
            }
        }

        private static async Task SyncQueueFailureReleasesWaitingRunAsync()
        {
            foreach (var canceled in new[] { false, true })
            {
                var queue = new SyncRunQueue();
                var release = QueueSignal();
                var first = queue.RunAsync(async () =>
                {
                    await release.Task;
                    if (canceled) throw new OperationCanceledException();
                    throw new InvalidOperationException("simulated provider failure");
                }, CancellationToken.None);
                var ran = false;
                var second = queue.RunAsync(() => { ran = true; return Task.CompletedTask; }, CancellationToken.None);
                release.TrySetResult(true);
                try { await first; }
                catch (OperationCanceledException) when (canceled) { }
                catch (InvalidOperationException) when (!canceled) { }
                await second.WaitAsync(TimeSpan.FromSeconds(2));
                Assert(ran, "a failed or canceled predecessor must release its queued documentary job");
            }
        }

        private static async Task SyncQueuePairsRemainIndependentAsync()
        {
            var movies = new SyncRunQueue();
            var series = new SyncRunQueue();
            var release = QueueSignal();
            var active = movies.RunAsync(() => release.Task, CancellationToken.None);
            var seriesRan = false;
            try
            {
                await series.RunAsync(() => { seriesRan = true; return Task.CompletedTask; }, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(2));
                Assert(seriesRan && !active.IsCompleted,
                    "the existing independent movie and series lanes must not become a global provider lock");
            }
            finally { release.TrySetResult(true); }
            await active;
        }

        private static Task MovieAndDocumentaryRunsShareQueueAsync() => PairedServiceRunsShareQueueAsync(false);
        private static Task SeriesAndDocuSeriesRunsShareQueueAsync() => PairedServiceRunsShareQueueAsync(true);

        private static async Task PairedServiceRunsShareQueueAsync(bool series)
        {
            var root = CreateCompleteVodTestRoot();
            var handler = new BlockingSyncCatalogHandler(series);
            using (var client = new HttpClient(handler))
            {
                Task first = null;
                Task second = null;
                Task afterFinalCopy = null;
                try
                {
                    var source = CreateProviderIdConfig(root);
                    source.SyncDocumentaries = true;
                    source.SyncDocuSeries = true;
                    source.SelectedDocumentaryCategoryIds = new[] {83};
                    source.SelectedDocuSeriesCategoryIds = new[] {161};
                    var service = new StrmSyncService(CreateSilentLogger(), client);
                    var checkpoints = 0;
                    first = series
                        ? service.SyncSeriesAsync(source, CancellationToken.None, () => checkpoints++)
                        : service.SyncMoviesAsync(source, CancellationToken.None, () => checkpoints++);
                    await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

                    var factoryCalled = false;
                    Func<PluginConfiguration> build = () =>
                    {
                        factoryCalled = true;
                        Assert(checkpoints > 0, "queued configuration must be built after the active run saves its checkpoint");
                        Assert(!(series ? service.SeriesProgress.IsRunning : service.MovieProgress.IsRunning),
                            "a queued run must not replace the active pair's progress");
                        return (PluginConfiguration)(series ? typeof(SyncDocuSeriesTask) : typeof(SyncDocumentariesTask))
                            .GetMethod(series ? "BuildDocuSeriesConfig" : "BuildDocumentaryConfig", BindingFlags.NonPublic | BindingFlags.Static)
                            .Invoke(null, new object[] {source});
                    };
                    var finalCopyFinished = false;
                    Action finishCopy = () =>
                    {
                        Func<PluginConfiguration> next = () =>
                        {
                            Assert(finalCopyFinished, "the next job must not read settings before the final documentary checkpoint copy finishes");
                            return null;
                        };
                        afterFinalCopy = series
                            ? service.SyncSeriesWhenReadyAsync(next, CancellationToken.None)
                            : service.SyncMoviesWhenReadyAsync(next, CancellationToken.None);
                        Assert(!afterFinalCopy.IsCompleted, "the documentary completion callback must still own its pair's execution slot");
                        finalCopyFinished = true;
                    };
                    second = series
                        ? service.SyncSeriesWhenReadyAsync(build, CancellationToken.None, isDocuSeries: true, onCompleted: finishCopy)
                        : service.SyncMoviesWhenReadyAsync(build, CancellationToken.None, isDocumentaries: true, onCompleted: finishCopy);
                    Assert(!factoryCalled && !second.IsCompleted && handler.CatalogRequests == 1,
                        "a scheduled documentary run must wait behind a public/manual run before building configuration or requesting catalogs");

                    handler.Release.TrySetResult(true);
                    await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
                    await afterFinalCopy.WaitAsync(TimeSpan.FromSeconds(2));
                    Assert(factoryCalled && handler.CatalogRequests == 2,
                        "both the original and queued sync must fetch and process their own catalog");
                    Assert(Directory.GetFiles(Path.Combine(root, series ? source.DocuSeriesRootFolderName : source.DocumentaryRootFolderName), "*.strm", SearchOption.AllDirectories).Length == 1,
                        "the queued documentary job must generate real output instead of being reported complete without running");
                    Assert(service.GetSyncHistory().Count == 2,
                        "both real runs must have their own persisted-history entries");
                }
                finally
                {
                    handler.Release.TrySetResult(true);
                    foreach (var run in new[] {first, second, afterFinalCopy})
                    {
                        if (run != null)
                        {
                            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
                            catch { }
                        }
                    }
                    Directory.Delete(root, true);
                }
            }
        }

        private static async Task QueuedSyncCanBeDisabledBeforeStartingAsync()
        {
            var root = CreateCompleteVodTestRoot();
            var handler = new BlockingSyncCatalogHandler(false);
            using (var client = new HttpClient(handler))
            using (var cancellation = new CancellationTokenSource())
            {
                Task active = null;
                Task skipped = null;
                Task canceled = null;
                try
                {
                    var service = new StrmSyncService(CreateSilentLogger(), client);
                    active = service.SyncMoviesAsync(CreateProviderIdConfig(root), CancellationToken.None);
                    await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    var enabled = true;
                    var eligibilityChecked = false;
                    skipped = service.SyncMoviesWhenReadyAsync(() =>
                    {
                        eligibilityChecked = true;
                        Assert(!enabled, "queued settings/health eligibility must be rechecked after the wait");
                        return null;
                    }, CancellationToken.None, isDocumentaries: true);
                    var canceledFactoryCalled = false;
                    canceled = service.SyncMoviesWhenReadyAsync(() =>
                    {
                        canceledFactoryCalled = true;
                        return CreateProviderIdConfig(root);
                    }, cancellation.Token, isDocumentaries: true);
                    cancellation.Cancel();
                    await ExpectQueueCancellationAsync(canceled);
                    Assert(!canceledFactoryCalled && !eligibilityChecked,
                        "waiting jobs must not read configuration or start a canceled sync");
                    enabled = false;
                    handler.Release.TrySetResult(true);
                    await Task.WhenAll(active, skipped).WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(eligibilityChecked && handler.CatalogRequests == 1 && service.GetSyncHistory().Count == 1,
                        "disabling a queued job must prevent its fetch, file work and false sync-history entry");
                }
                finally
                {
                    handler.Release.TrySetResult(true);
                    foreach (var run in new[] {active, skipped, canceled})
                    {
                        if (run != null)
                        {
                            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
                            catch { }
                        }
                    }
                    Directory.Delete(root, true);
                }
            }
        }

        private static async Task ExpectQueueCancellationAsync(Task task)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { return; }
            throw new InvalidOperationException("The queued task must be canceled, not silently completed.");
        }

        private sealed class BlockingSyncCatalogHandler : HttpMessageHandler
        {
            private readonly bool _series;
            internal readonly TaskCompletionSource<bool> Started = QueueSignal();
            internal readonly TaskCompletionSource<bool> Release = QueueSignal();
            internal int CatalogRequests;

            internal BlockingSyncCatalogHandler(bool series) => _series = series;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri.Query.Contains("action=get_series_info"))
                    return JsonResponse("{\"episodes\":{\"1\":[{\"id\":1,\"season\":1,\"episode_num\":1,\"container_extension\":\"mkv\"}]}}");

                if (Interlocked.Increment(ref CatalogRequests) == 1)
                {
                    Started.TrySetResult(true);
                    await Release.Task.WaitAsync(cancellationToken);
                }
                return JsonResponse(_series
                    ? "[{\"series_id\":1,\"name\":\"Queued Show\",\"category_id\":161,\"last_modified\":123}]"
                    : "[{\"stream_id\":1,\"name\":\"Queued Movie\",\"category_id\":83,\"added\":123}]");
            }
        }
    }
}
