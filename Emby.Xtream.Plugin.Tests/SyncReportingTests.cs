using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Api;
using Emby.Xtream.Plugin.Service;

namespace Emby.Xtream.Plugin.Tests
{
    internal static partial class Program
    {
        private static async Task MoviePreparationProgressIsSeparateAsync()
        {
            var root = CreateCompleteVodTestRoot();
            var handler = new PreparationReportingHandler();
            Task run = null;
            try
            {
                var config = CreateProviderIdConfig(root);
                config.EnableNfoFiles = true;
                config.EnableLocalMediaFilter = true;
                var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler));
                service.LocalMediaFilterFactory = (logger, path) => CachedTestLocalFilter(false);
                var percentages = new List<double>();
                var phases = new List<string>();
                run = service.SyncMoviesAsync(config, CancellationToken.None,
                    taskProgress: new ImmediateProgress(value =>
                    {
                        percentages.Add(value);
                        phases.Add(service.MovieProgress.Phase);
                    }));
                await handler.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var progress = service.MovieProgress;
                var wire = SyncProgressInfo.FromProgress(progress);
                Assert(progress.IsPreparing && progress.Phase == "Preparing movie metadata" && progress.Prepared == 1 && progress.PreparationTotal == 2,
                    "a blocked second metadata request must leave visible preparation progress at 1/2");
                Assert(progress.Completed == 0 && progress.Added == 0 && !Directory.GetFiles(root, "*.strm", SearchOption.AllDirectories).Any(),
                    "preparation must not fabricate completed writes or history item counts");
                Assert(wire.IsPreparing && wire.Prepared == 1 && wire.PreparationTotal == 2 && wire.Completed == 0,
                    "the progress API must transmit separate counters for dashboard and per-library panels");
                handler.Release.TrySetResult(true);
                await run.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(!progress.IsPreparing && progress.Phase == "Complete" && progress.Completed == 2 && progress.Prepared == 2,
                    "completion must retain real file statistics and leave preparation mode");
                Assert(phases.Contains("Writing STRM files") && percentages.Last() == 100 && percentages.Zip(percentages.Skip(1), (a, b) => b >= a).All(x => x),
                    "Emby's weighted task progress must advance through preparation and writing without resetting");
            }
            finally
            {
                handler.Release.TrySetResult(true);
                if (run != null) { try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
                Directory.Delete(root, true);
            }
        }

        private static async Task SeriesPhaseLeavesLocalScanAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                StrmSyncService service = null;
                var phaseDuringFetch = string.Empty;
                var handler = new SeriesReportingHandler(() => phaseDuringFetch = service.SeriesProgress.Phase);
                service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler));
                var config = CreateProviderIdConfig(root);
                config.EnableLocalMediaFilter = true;
                service.LocalMediaFilterFactory = (logger, path) =>
                {
                    Assert(service.SeriesProgress.Phase == "Scanning Emby library", "local scanning must be labeled only while it is happening");
                    return CachedTestLocalFilter(false);
                };
                await service.SyncSeriesAsync(config, CancellationToken.None);
                Assert(phaseDuringFetch == "Processing series metadata and STRM files",
                    "series detail requests must not retain the completed local-library-scan label");
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task FatalSyncFailureHistoryHasReasonAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                foreach (var series in new[] {false,true})
                {
                    var config = CreateProviderIdConfig(Path.Combine(root, series.ToString()));
                    var handler = new QueueHandler(new HttpResponseMessage(HttpStatusCode.Forbidden));
                    var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler));
                    var failed = false;
                    try
                    {
                        if (series) await service.SyncSeriesAsync(config, CancellationToken.None);
                        else await service.SyncMoviesAsync(config, CancellationToken.None);
                    }
                    catch (InvalidOperationException) { failed = true; }
                    var entry = service.GetSyncHistory().Single();
                    Assert(failed && !entry.Success && entry.MoviesFailed == 0 && entry.SeriesFailed == 0 && entry.EpisodeFailed == 0,
                        "a catalog failure must remain a sync failure without inventing individual item failures");
                    Assert(entry.FailureReason.Contains(series ? "161" : "83") && entry.FailureReason.Contains("403"),
                        "history must preserve both the category and the underlying HTTP failure");
                    var restored = JsonSerializer.Deserialize<SyncHistoryEntry>(JsonSerializer.Serialize(entry));
                    Assert(restored.FailureReason == entry.FailureReason, "the recorded reason must survive persisted-history serialization");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task AbortedAndIncompleteSyncsHaveReasonsAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(root);
                config.MovieFolderMode = "custom";
                config.MovieFolderMappings = string.Empty;
                var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(new QueueHandler()));
                await service.SyncMoviesAsync(config, CancellationToken.None);
                Assert(!service.GetSyncHistory().Single().Success && service.GetSyncHistory().Single().FailureReason.Contains("Multiple Folders"),
                    "configuration aborts must record their actionable reason");
                config.MovieFolderMode = "single";
                service = new StrmSyncService(CreateSilentLogger(), new HttpClient(new QueueHandler(JsonResponse("[{\"stream_id\":1,\"name\":\"???\",\"category_id\":83}]"))));
                await service.SyncMoviesAsync(config, CancellationToken.None);
                var entry = service.GetSyncHistory().Single();
                Assert(!entry.Success && entry.MoviesFailed == 1 && entry.FailureReason.Contains("1 item(s) failed"),
                    "incomplete item processing must not leave history's reason empty");
            }
            finally { Directory.Delete(root, true); }
        }

        private static Task SyncFailureReasonsArePrivateAndReadableAsync()
        {
            var config = new PluginConfiguration { BaseUrl = "https://secret-provider.invalid", Username = "sensitive-user", Password = "sensitive-password" };
            var error = new InvalidOperationException("VOD category 82 could not be loaded; sync aborted to preserve the existing library",
                new TaskCanceledException("The configured HttpClient.Timeout of 30 seconds elapsed. URL https://secret-provider.invalid/player_api.php?username=sensitive-user&password=sensitive-password X-Emby-Token=abc123 password=sensitive-password"));
            var reason = SyncReporting.DescribeFailure(error, "Fetching VOD streams", config);
            Assert(reason.Contains("82") && reason.Contains("30 seconds") && reason.Contains("Fetching VOD streams"),
                "failure reasons must retain the useful stage, category and inner timeout detail");
            Assert(!reason.Contains("secret-provider.invalid") && !reason.Contains("sensitive-user") && !reason.Contains("sensitive-password") && !reason.Contains("abc123"),
                "persisted history must never store provider URLs, credentials or Emby tokens");
            var shortCredentials = new PluginConfiguration { Username = "u", Password = "p" };
            Assert(SyncReporting.SanitizeFailureReason("VOD category 82 could not be loaded", shortCredentials).Contains("could not be loaded"),
                "short credentials must not destroy unrelated words in an error explanation");
            Assert(SyncReporting.SanitizeFailureReason(new string('x', 10000), config).Length <= 3003,
                "provider errors must be bounded before persisting history");
            Assert(!SyncReporting.SanitizeFailureReason("password=top secret", new PluginConfiguration {Password = "top secret"}).Contains("secret"),
                "credentials containing spaces must be removed before generic query-field redaction");
            return Task.CompletedTask;
        }

        private static Task ReportingKeepsLegacyHistoryAndProgressAsync()
        {
            Assert(JsonSerializer.Deserialize<SyncHistoryEntry>("{\"Success\":false}").FailureReason == string.Empty,
                "old history must load without inventing a cause that was never recorded");
            var progress = new SyncProgress { Total = 10, Completed = 5 };
            Assert(SyncReporting.GetTaskPercent(progress) == 50, "legacy/series progress must retain its existing scale");
            progress.Phase = "Classifying VOD genres";
            Assert(SyncReporting.GetTaskPercent(progress) == 5, "genre classification must reserve the remaining task budget");
            progress.IsPreparing = true;
            progress.PreparationTotal = 100;
            progress.Prepared = 50;
            Assert(SyncReporting.GetTaskPercent(progress) == 35, "preparation must have its own weighted task progress");
            progress.IsPreparing = false;
            progress.Phase = "Writing STRM files";
            Assert(SyncReporting.GetTaskPercent(progress) == 80, "writing must resume above the preparation portion");
            return Task.CompletedTask;
        }

        private sealed class ImmediateProgress : IProgress<double>
        {
            private readonly Action<double> _report;
            internal ImmediateProgress(Action<double> report) => _report = report;
            public void Report(double value) => _report(value);
        }

        private sealed class PreparationReportingHandler : HttpMessageHandler
        {
            internal readonly TaskCompletionSource<bool> Blocked = QueueSignal();
            internal readonly TaskCompletionSource<bool> Release = QueueSignal();
            private int _details;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri.Query.Contains("get_vod_streams"))
                    return JsonResponse("[{\"stream_id\":1,\"name\":\"First\",\"category_id\":83},{\"stream_id\":2,\"name\":\"Second\",\"category_id\":83}]");
                if (Interlocked.Increment(ref _details) == 2)
                {
                    Blocked.TrySetResult(true);
                    await Release.Task.WaitAsync(cancellationToken);
                }
                return JsonResponse("{\"info\":{\"tmdb_id\":123,\"plot\":\"Provider metadata\"}}");
            }
        }

        private sealed class SeriesReportingHandler : HttpMessageHandler
        {
            private readonly Action _onDetail;
            internal SeriesReportingHandler(Action onDetail) => _onDetail = onDetail;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri.Query.Contains("get_series_info"))
                {
                    _onDetail();
                    return Task.FromResult(JsonResponse(CacheSeriesDetail));
                }
                return Task.FromResult(JsonResponse(CacheSeriesCatalog));
            }
        }
    }
}
