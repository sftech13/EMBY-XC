using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client.Models;
using Emby.Xtream.Plugin.Service;

namespace Emby.Xtream.Plugin.Tests
{
    internal static partial class Program
    {
        private static async Task WeeklyVerificationReusesExistingCacheAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                foreach (var documentary in new[] {false, true})
                {
                    var folder = Path.Combine(root, documentary.ToString());
                    var config = CreateProviderIdConfig(Path.Combine(folder, "Media"));
                    config.SmartSkipExisting = true;
                    config.EnableLocalMediaFilter = true;
                    config.EnableNfoFiles = true;
                    if (documentary) config.SeriesRootFolderName = "DocuSeries";
                    var cacheRoot = Path.Combine(folder, "Cache");
                    var first = new StrmSyncService(CreateSilentLogger(), new HttpClient(new QueueHandler(JsonResponse(CacheSeriesCatalog), JsonResponse(CacheSeriesDetail))), cacheRoot);
                    first.LocalMediaFilterFactory = (logger, path) => CachedTestLocalFilter(false);
                    await first.SyncSeriesAsync(config, CancellationToken.None, isDocuSeries: documentary);

                    var cache = new SeriesDetailDiskCache(cacheRoot);
                    var series = XtreamResponseParser.DeserializeSeriesList(CacheSeriesCatalog, null).Single();
                    SeriesDetailInfo detail;
                    long fetched;
                    Assert(cache.TryRead(config, series, DateTime.UtcNow, out detail, out fetched), "the existing v1 cache must be readable");
                    var path = cache.GetPath(config, series.SeriesId);
                    var bytes = File.ReadAllBytes(path);
                    var writeTime = File.GetLastWriteTimeUtc(path);
                    config.LastSeriesFullVerificationUtcTicks = DateTime.UtcNow.AddDays(-8).Ticks;

                    var handler = new QueueHandler(JsonResponse(CacheSeriesCatalog));
                    var second = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler), cacheRoot);
                    var localChecks = 0;
                    second.LocalMediaFilterFactory = (logger, rootPath) => { localChecks++; return CachedTestLocalFilter(false); };
                    await second.SyncSeriesAsync(config, CancellationToken.None, isDocuSeries: documentary);
                    Assert(second.GetSyncHistory().Single().Success && handler.Requests.Count == 1 && localChecks == 1,
                        "a due weekly marker must still reuse previously built valid details and recheck local media");
                    Assert(bytes.SequenceEqual(File.ReadAllBytes(path)) && writeTime == File.GetLastWriteTimeUtc(path),
                        "the fix must neither rebuild nor rewrite the user's existing cache");
                    Assert(config.LastSeriesFullVerificationUtcTicks == fetched,
                        "verification must use the actual cache fetch time, never pretend the cache was fetched now");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task CacheHitsNeverExtendVerificationDeadlineAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(Path.Combine(root, "Media"));
                config.SmartSkipExisting = true;
                config.EnableLocalMediaFilter = true;
                config.LastSeriesFullVerificationUtcTicks = DateTime.UtcNow.AddDays(-8).Ticks;
                var cache = new SeriesDetailDiskCache(Path.Combine(root, "Cache"));
                var series = XtreamResponseParser.DeserializeSeriesList(CacheSeriesCatalog, null).Single();
                var fetched = DateTime.UtcNow.AddDays(-6);
                Assert(cache.TryWrite(config, series, JsonSerializer.Deserialize<SeriesDetailInfo>(CacheSeriesDetail), fetched), "seed a valid six-day-old cache");
                var original = File.ReadAllBytes(cache.GetPath(config, series.SeriesId));
                for (var run = 0; run < 2; run++)
                {
                    var handler = new QueueHandler(JsonResponse(CacheSeriesCatalog));
                    var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler), Path.Combine(root, "Cache"));
                    service.LocalMediaFilterFactory = (logger, path) => CachedTestLocalFilter(false);
                    await service.SyncSeriesAsync(config, CancellationToken.None);
                    Assert(handler.Requests.Count == 1 && config.LastSeriesFullVerificationUtcTicks == fetched.Ticks,
                        "repeated reuse must not slide the weekly deadline forward");
                }
                SeriesDetailInfo detail;
                Assert(!cache.TryRead(config, series, fetched.AddDays(7), out detail) &&
                    StrmSyncService.IsSeriesFullVerificationDue(config.LastSeriesFullVerificationUtcTicks, fetched.AddDays(7)),
                    "both entry freshness and the file fast-path deadline must expire at the original seven-day limit");
                Assert(original.SequenceEqual(File.ReadAllBytes(cache.GetPath(config, series.SeriesId))), "reuse must not renew the stored fetched timestamp");
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task FullColdRunCountsAsVerificationAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(Path.Combine(root, "Media"));
                config.SmartSkipExisting = true;
                config.EnableLocalMediaFilter = true;
                config.LastSeriesFullVerificationUtcTicks = DateTime.UtcNow.AddDays(-6).Ticks;
                var before = DateTime.UtcNow;
                var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(new QueueHandler(JsonResponse(CacheSeriesCatalog), JsonResponse(CacheSeriesDetail))), Path.Combine(root, "Cache"));
                service.LocalMediaFilterFactory = (logger, path) => CachedTestLocalFilter(false);
                await service.SyncSeriesAsync(config, CancellationToken.None);
                Assert(config.LastSeriesFullVerificationUtcTicks >= before.Ticks && config.LastSeriesFullVerificationUtcTicks <= DateTime.UtcNow.Ticks,
                    "a successful full cold-cache fetch must count even if the old weekly marker was not due at run start");
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task FilenameFastSkipDoesNotRenewVerificationAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(Path.Combine(root, "Media"));
                config.SmartSkipExisting = true;
                var cacheRoot = Path.Combine(root, "Cache");
                await new StrmSyncService(CreateSilentLogger(), new HttpClient(new QueueHandler(JsonResponse(CacheSeriesCatalog), JsonResponse(CacheSeriesDetail))), cacheRoot)
                    .SyncSeriesAsync(config, CancellationToken.None);
                var original = DateTime.UtcNow.AddDays(-6).Ticks;
                config.LastSeriesFullVerificationUtcTicks = original;
                var handler = new QueueHandler(JsonResponse(CacheSeriesCatalog));
                await new StrmSyncService(CreateSilentLogger(), new HttpClient(handler), cacheRoot).SyncSeriesAsync(config, CancellationToken.None);
                Assert(handler.Requests.Count == 1 && config.LastSeriesFullVerificationUtcTicks == original,
                    "a filename-only fast skip does not constitute another provider-detail verification");
            }
            finally { Directory.Delete(root, true); }
        }

        private const string VerificationPairCatalog = "[{\"series_id\":3000000001,\"name\":\"Cache Show\",\"category_id\":161,\"last_modified\":100},{\"series_id\":3000000004,\"name\":\"Second Show\",\"category_id\":161,\"last_modified\":100}]";

        private static async Task IncompleteCoverageKeepsVerificationCheckpointAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(Path.Combine(root, "Media"));
                config.SmartSkipExisting = true;
                config.EnableLocalMediaFilter = true;
                var original = DateTime.UtcNow.AddDays(-8).Ticks;
                config.LastSeriesFullVerificationUtcTicks = original;
                var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(new QueueHandler(
                    JsonResponse(VerificationPairCatalog), JsonResponse(CacheSeriesDetail), JsonResponse("{\"episodes\":[]}"))), Path.Combine(root, "Cache"));
                service.LocalMediaFilterFactory = (logger, path) => CachedTestLocalFilter(false);
                await service.SyncSeriesAsync(config, CancellationToken.None);
                Assert(config.LastSeriesFullVerificationUtcTicks == original,
                    "partial detail coverage must never mark the entire catalog freshly verified");
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task VerificationFetchesOnlyExpiredEntriesAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(Path.Combine(root, "Media"));
                config.SmartSkipExisting = true;
                config.EnableLocalMediaFilter = true;
                config.LastSeriesFullVerificationUtcTicks = DateTime.UtcNow.AddDays(-8).Ticks;
                var cacheRoot = Path.Combine(root, "Cache");
                var cache = new SeriesDetailDiskCache(cacheRoot);
                var series = XtreamResponseParser.DeserializeSeriesList(VerificationPairCatalog, null);
                var recent = DateTime.UtcNow.AddDays(-1);
                Assert(cache.TryWrite(config, series[0], JsonSerializer.Deserialize<SeriesDetailInfo>(CacheSeriesDetail), recent), "seed the recent entry");
                Assert(cache.TryWrite(config, series[1], JsonSerializer.Deserialize<SeriesDetailInfo>(CacheSeriesDetail), DateTime.UtcNow.AddDays(-8)), "seed the expired entry");
                var original = File.ReadAllBytes(cache.GetPath(config, series[0].SeriesId));
                var handler = new QueueHandler(JsonResponse(VerificationPairCatalog), JsonResponse(CacheSeriesDetail));
                var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler), cacheRoot);
                service.LocalMediaFilterFactory = (logger, path) => CachedTestLocalFilter(false);
                await service.SyncSeriesAsync(config, CancellationToken.None);
                Assert(service.GetSyncHistory().Single().Success && handler.Requests.Count == 2 &&
                    handler.Requests.Last().RequestUri.Query.Contains("series_id=3000000004"),
                    "a weekly check must fetch only the expired record, not rebuild every fresh entry");
                Assert(config.LastSeriesFullVerificationUtcTicks == recent.Ticks && original.SequenceEqual(File.ReadAllBytes(cache.GetPath(config, series[0].SeriesId))),
                    "mixed-age coverage must retain the oldest real verification time and preserve recent entry bytes");
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
