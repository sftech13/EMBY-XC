using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client.Models;
using Emby.Xtream.Plugin.Service;
using MediaBrowser.Model.Logging;

namespace Emby.Xtream.Plugin.Tests
{
    internal static partial class Program
    {
        private static Task ProviderCatalogRetainsLargeIdsAsync()
        {
            // Reproduce the observed 26,899 -> 23 collapse: 26,877 IDs exceed
            // Int32.MaxValue, while 22 legacy IDs still fit. Mix JSON numbers/strings.
            var ids = Enumerable.Range(0, 26877).Select(i => 2378700005L + i)
                .Concat(Enumerable.Range(1, 22).Select(i => (long)i)).ToArray();
            var json = JsonSerializer.Serialize(ids.Select((id, index) => new
            {
                series_id = index % 2 == 0 ? (object)id : id.ToString(CultureInfo.InvariantCulture),
                name = "Show " + index,
            }));
            var records = XtreamResponseParser.DeserializeSeriesList(json, null);
            Assert(records.Count == 26899 && records.Select(s => s.SeriesId).Distinct().Count() == 26899,
                "all provider series must remain distinct rather than collapsing oversized IDs to zero");
            Assert(records[0].SeriesId == 2378700005L && records.All(s => s.SeriesId > 0),
                "the real failing ID must survive parsing exactly");
            var dictionary = XtreamResponseParser.DeserializeSeriesList(
                "{\"a\":{\"series_id\":2378700005},\"b\":{\"series_id\":\"2675190005\"}}", null);
            Assert(dictionary.Select(s => s.SeriesId).SequenceEqual(new[] {2378700005L, 2675190005L}),
                "dictionary-shaped series catalogs must preserve oversized IDs too");
            var vod = XtreamResponseParser.DeserializeVodList(
                "[{\"stream_id\":2147483647},{\"stream_id\":2147483648},{\"stream_id\":\"9223372036854775807\"}]", null);
            Assert(vod.Select(s => s.StreamId).SequenceEqual(new[] {2147483647L, 2147483648L, long.MaxValue}),
                "movie identifiers must support the full positive signed 64-bit range");
            return Task.CompletedTask;
        }

        private static async Task InvalidProviderCatalogProtectsFilesAsync()
        {
            foreach (var value in new[] {"0", "-1", "9223372036854775808", "1.5", "null", "true", "\"bad\"", "\"\""})
            {
                AssertJsonRejected(() => XtreamResponseParser.DeserializeSeriesList("[{\"series_id\":" + value + "}]", null));
                AssertJsonRejected(() => XtreamResponseParser.DeserializeVodList("[{\"stream_id\":" + value + "}]", null));
                AssertJsonRejected(() => JsonSerializer.Deserialize<EpisodeInfo>("{\"id\":" + value + "}"));
            }
            foreach (var bad in new[] {"[{\"name\":\"missing ID\"}]", "[null]", "false", "null", "{\"error\":\"unavailable\"}", "{", ""})
            {
                AssertJsonRejected(() => XtreamResponseParser.DeserializeSeriesList(bad, null));
                AssertJsonRejected(() => XtreamResponseParser.DeserializeVodList(bad, null));
            }
            AssertJsonRejected(() => XtreamResponseParser.ValidateEpisodeIds(
                JsonSerializer.Deserialize<SeriesDetailInfo>("{\"episodes\":{\"1\":[{\"title\":\"missing ID\"}]}}")));
            Assert(XtreamResponseParser.DeserializeSeriesList("[]", null).Count == 0,
                "a genuinely empty array must remain distinct from a malformed response");

            var root = Path.Combine(Path.GetTempPath(), "xc2emby-invalid-id-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                foreach (var series in new[] {false, true})
                {
                    var config = CreateProviderIdConfig(root);
                    var folder = series ? "TV Shows" : "Movies";
                    var existing = Path.Combine(root, folder, "Existing", "Existing.strm");
                    Directory.CreateDirectory(Path.GetDirectoryName(existing));
                    const string original = "https://provider.invalid/series/u/p/3000000001.mkv";
                    File.WriteAllText(existing, original);
                    config.LastMovieSyncTimestamp = 100;
                    config.LastSeriesSyncTimestamp = 200;
                    config.SeriesEpisodeHashesJson = "{\"42\":\"checkpoint\"}";
                    config.SeriesCategorySnapshotJson = "{\"SelectedCategoryIds\":[161],\"Series\":{}}";
                    var snapshot = config.SeriesCategorySnapshotJson;
                    var handler = new QueueHandler(JsonResponse("[{\"name\":\"missing ID\"}]"));
                    var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler));
                    var rejected = false;
                    try
                    {
                        if (series) await service.SyncSeriesAsync(config, CancellationToken.None);
                        else await service.SyncMoviesAsync(config, CancellationToken.None);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is HttpRequestException || ex is JsonException)
                    {
                        rejected = true;
                    }
                    Assert(rejected && handler.Requests.Count == 1,
                        "invalid catalogs must stop without making any detail or playback requests");
                    Assert(File.ReadAllText(existing) == original && Directory.GetFiles(Path.Combine(root, folder), "*.strm", SearchOption.AllDirectories).Length == 1,
                        "invalid input must preserve existing STRMs even with orphan cleanup enabled at 100 percent");
                    Assert(config.LastMovieSyncTimestamp == 100 && config.LastSeriesSyncTimestamp == 200 &&
                           config.SeriesCategorySnapshotJson == snapshot && config.SeriesEpisodeHashesJson == "{\"42\":\"checkpoint\"}",
                        "failed catalogs must not advance timestamps or replace ownership/episode checkpoints");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task LargeProviderIdsSyncEndToEndAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "xc2emby-large-id-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var config = CreateProviderIdConfig(root);
                var handler = new QueueHandler(
                    JsonResponse("[{\"series_id\":2378700005,\"name\":\"First Show\",\"category_id\":161},{\"series_id\":\"2675190005\",\"name\":\"Second Show\",\"category_id\":161}]"),
                    JsonResponse("{\"episodes\":{\"1\":[{\"id\":3000000001,\"season\":1,\"episode_num\":1,\"container_extension\":\"mkv\"}]}}"),
                    JsonResponse("{\"episodes\":{\"1\":[{\"id\":\"3000000002\",\"season\":1,\"episode_num\":1,\"container_extension\":\"mkv\"}]}}"));
                var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler));
                await service.SyncSeriesAsync(config, CancellationToken.None);
                var urls = Directory.GetFiles(root, "*.strm", SearchOption.AllDirectories).Select(File.ReadAllText).OrderBy(s => s).ToArray();
                Assert(urls.Length == 2 && urls[0].EndsWith("/3000000001.mkv") && urls[1].EndsWith("/3000000002.mkv"),
                    "distinct large-ID shows must generate distinct episode playback URLs");
                Assert(handler.Requests.Any(r => r.RequestUri.Query.Contains("series_id=2378700005")) &&
                       handler.Requests.Any(r => r.RequestUri.Query.Contains("series_id=2675190005")) &&
                       !handler.Requests.Any(r => r.RequestUri.Query.Contains("series_id=0")),
                    "series-detail requests must retain the original large provider IDs");
                var snapshot = StrmSyncService.DeserializeSeriesCategorySnapshot(config.SeriesCategorySnapshotJson);
                Assert(snapshot.Series.Values.Select(s => s.SeriesId).OrderBy(s => s).SequenceEqual(new[] {2378700005L,2675190005L}),
                    "ownership checkpoints must preserve the full series IDs");

                var canonical = Directory.GetFiles(root, "*.strm", SearchOption.AllDirectories).Single(p => File.ReadAllText(p).EndsWith("/3000000001.mkv"));
                var old = Path.Combine(Path.GetDirectoryName(canonical), "old-name.strm");
                File.Copy(canonical, old);
                var paths = new ConcurrentDictionary<long, ConcurrentDictionary<string, byte>>();
                paths[3000000001L] = new ConcurrentDictionary<string, byte>();
                paths[3000000001L][canonical] = 0;
                Assert(StrmSyncService.FindSupersededEpisodePaths(new[] {old}, paths).SequenceEqual(new[] {old}),
                    "superseded-path matching must retain 64-bit playback IDs");
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task LargeVodIdsSyncEndToEndAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "xc2emby-large-vod-id-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var handler = new QueueHandler(JsonResponse("[{\"stream_id\":3000000001,\"name\":\"First Movie\",\"container_extension\":\"mkv\"},{\"stream_id\":\"3000000002\",\"name\":\"Second Movie\",\"container_extension\":\"mkv\"}]"));
                var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler));
                await service.SyncMoviesAsync(CreateProviderIdConfig(root), CancellationToken.None);
                var urls = Directory.GetFiles(root, "*.strm", SearchOption.AllDirectories).Select(File.ReadAllText).OrderBy(s => s).ToArray();
                Assert(urls.Length == 2 && urls[0].EndsWith("/3000000001.mkv") && urls[1].EndsWith("/3000000002.mkv"),
                    "large movie IDs must survive catalog parsing and STRM writing without collision");
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task InvalidEpisodeIdsPreserveExistingFilesAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "xc2emby-invalid-episode-id-" + Guid.NewGuid().ToString("N"));
            try
            {
                var path = Path.Combine(root, "TV Shows", "First Show", "Season 01", "Existing.strm");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                const string url = "https://provider.invalid/series/u/p/3000000001.mkv";
                File.WriteAllText(path, url);
                var config = CreateProviderIdConfig(root);
                config.LastSeriesSyncTimestamp = 100;
                var handler = new QueueHandler(
                    JsonResponse("[{\"series_id\":2378700005,\"name\":\"First Show\",\"last_modified\":\"200\"}]"),
                    JsonResponse("{\"episodes\":{\"1\":[{\"title\":\"missing ID\",\"episode_num\":1}]}}"),
                    MediaResponse(HttpStatusCode.PartialContent));
                var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler));
                await service.SyncSeriesAsync(config, CancellationToken.None);
                Assert(File.ReadAllText(path) == url && Directory.GetFiles(root, "*.strm", SearchOption.AllDirectories).Length == 1,
                    "an invalid episode identifier must protect working existing files rather than replace them with a zero-ID stream");
                Assert(config.LastSeriesSyncTimestamp == 100 && service.FailedItems.Single().StreamId == 2378700005L,
                    "invalid detail must preserve the sync timestamp and retain the full series ID for retry");
                Assert(handler.Requests.Count == 3 && handler.Requests.Last().Headers.Range != null,
                    "detail failure must use the established individual playback-URL validation safeguard");
            }
            finally { Directory.Delete(root, true); }
        }

        private static Task ProviderIdPersistenceIsCompatibleAsync()
        {
            var legacy = JsonSerializer.Deserialize<EpisodePlaybackValidationState>("{\"EpisodeId\":42,\"ConsecutiveDefinitiveFailures\":1}");
            Assert(legacy.EpisodeId == 42 && legacy.ConsecutiveDefinitiveFailures == 1,
                "old numeric validation state must load without resetting its safeguards");
            legacy.EpisodeId = 3000000001L;
            var restored = JsonSerializer.Deserialize<EpisodePlaybackValidationState>(JsonSerializer.Serialize(legacy));
            Assert(restored.EpisodeId == 3000000001L && restored.ConsecutiveDefinitiveFailures == 1,
                "large episode IDs and failure counts must persist through a restart");
            var failed = new FailedSyncItem {ItemType="Series", StreamId=2378700005L};
            Assert(JsonSerializer.Deserialize<FailedSyncItem>(JsonSerializer.Serialize(failed)).StreamId == failed.StreamId,
                "retry records must preserve the provider ID rather than retrying zero");
            return Task.CompletedTask;
        }

        private static PluginConfiguration CreateProviderIdConfig(string root) => new PluginConfiguration
        {
            BaseUrl="https://provider.invalid", Username="u", Password="p", StrmLibraryPath=root,
            SelectedSeriesCategoryIds=new[] {161}, SelectedVodCategoryIds=new[] {83},
            SyncParallelism=1, SmartSkipExisting=false, CleanupOrphans=true, OrphanSafetyThreshold=1,
            StrmNamingVersion=StrmSyncService.CurrentStrmNamingVersion,
        };

        private static HttpResponseMessage JsonResponse(string json) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

        private static void AssertJsonRejected(Action action)
        {
            try { action(); }
            catch (JsonException) { return; }
            throw new InvalidOperationException("Invalid provider identity/shape was accepted instead of protecting the catalog.");
        }

        private static ILogger CreateSilentLogger() => DispatchProxy.Create<ILogger, SilentLogProxy>();

        public class SilentLogProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
                => targetMethod.ReturnType == typeof(void) ? null :
                    targetMethod.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }
}
