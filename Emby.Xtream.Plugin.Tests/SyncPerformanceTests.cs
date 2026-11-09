using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
        private const string CacheSeriesCatalog = "[{\"series_id\":3000000001,\"name\":\"Cache Show\",\"category_id\":161,\"last_modified\":100}]";
        private const string CacheSeriesDetail = "{\"info\":{\"name\":\"Cache Show\",\"tmdb_id\":\"123\",\"plot\":\"Provider plot\"},\"episodes\":{\"1\":[{\"id\":3000000002,\"season\":1,\"episode_num\":1,\"title\":\"One\",\"container_extension\":\"mkv\"},{\"id\":3000000003,\"season\":1,\"episode_num\":2,\"title\":\"Two\",\"container_extension\":\"mkv\"}]}}";
        private const string PartialVodCatalog = "[{\"stream_id\":1,\"name\":\"Included\",\"category_id\":83,\"category_ids\":[83]}]";

        private static LocalMediaFilter CachedTestLocalFilter(bool episodeOneLocal) => new LocalMediaFilter(
            new HashSet<string>(), new HashSet<string>(), new HashSet<string>(),
            new HashSet<string>(episodeOneLocal ? new[] {"123|S1|E1"} : new string[0]), new HashSet<string>());

        private static Task CachedDetailsReapplyLocalFilterAsync() => CachedDetailsReapplyLocalFilterCoreAsync(false);
        private static Task CachedDocuDetailsReapplyLocalFilterAsync() => CachedDetailsReapplyLocalFilterCoreAsync(true);

        private static async Task CachedDetailsReapplyLocalFilterCoreAsync(bool documentary)
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var media = Path.Combine(root, "Media");
                var cache = Path.Combine(root, "Cache");
                var config = CreateProviderIdConfig(media);
                config.SmartSkipExisting = true;
                config.EnableLocalMediaFilter = true;
                config.EnableNfoFiles = true;
                if (documentary) config.SeriesRootFolderName = "DocuSeries";
                var localBuilds = 0;
                string unchangedNfo = null;
                var oldTime = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                for (var run = 0; run < 3; run++)
                {
                    var handler = run == 0
                        ? new QueueHandler(JsonResponse(CacheSeriesCatalog), JsonResponse(CacheSeriesDetail))
                        : new QueueHandler(JsonResponse(CacheSeriesCatalog));
                    // Recreate the service to prove reuse survives server restarts.
                    var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler), cache);
                    var episodeOneLocal = run == 1;
                    service.LocalMediaFilterFactory = (logger, path) =>
                    {
                        localBuilds++;
                        return CachedTestLocalFilter(episodeOneLocal);
                    };
                    await service.SyncSeriesAsync(config, CancellationToken.None, isDocuSeries: documentary);
                    Assert(service.GetSyncHistory().Single().Success && handler.Requests.Count == (run == 0 ? 2 : 1),
                        "warm cached details must avoid provider detail requests, even with local filtering and NFOs enabled");
                    var files = Directory.GetFiles(media, "*.strm", SearchOption.AllDirectories);
                    Assert(files.Length == (run == 1 ? 1 : 2),
                        "cached episodes must remove newly local duplicates and restore STRMs when local copies disappear");
                    Assert(files.Any(p => File.ReadAllText(p).EndsWith("/3000000003.mkv")),
                        "an unrelated episode must never be removed by the local filter");
                    if (run == 0)
                    {
                        unchangedNfo = Path.ChangeExtension(files.Single(p => File.ReadAllText(p).EndsWith("/3000000003.mkv")), ".nfo");
                        File.SetLastWriteTimeUtc(unchangedNfo, oldTime);
                    }
                    else Assert(File.GetLastWriteTimeUtc(unchangedNfo) == oldTime,
                        "unchanged cached episode NFOs must not be rewritten");
                }
                Assert(localBuilds == 3, "the current local library must be checked on every cached sync");
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task CachedSeriesOrphanThresholdIsPreservedAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(Path.Combine(root, "Media"));
                config.SmartSkipExisting = true;
                config.EnableLocalMediaFilter = true;
                config.OrphanSafetyThreshold = 0.01;
                var cache = Path.Combine(root, "Cache");
                var first = new StrmSyncService(CreateSilentLogger(), new HttpClient(new QueueHandler(JsonResponse(CacheSeriesCatalog), JsonResponse(CacheSeriesDetail))), cache);
                first.LocalMediaFilterFactory = (logger, path) => CachedTestLocalFilter(false);
                await first.SyncSeriesAsync(config, CancellationToken.None);
                var existing = Directory.GetFiles(config.StrmLibraryPath, "*.strm", SearchOption.AllDirectories).First();
                var orphans = Enumerable.Range(1, 10).Select(i => Path.Combine(Path.GetDirectoryName(existing), "extra" + i + ".strm")).ToArray();
                for (var i = 0; i < orphans.Length; i++)
                    File.WriteAllText(orphans[i], "https://provider.invalid/series/u/p/" + (900 + i) + ".mkv");
                var handler = new QueueHandler(JsonResponse(CacheSeriesCatalog));
                var second = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler), cache);
                second.LocalMediaFilterFactory = (logger, path) => CachedTestLocalFilter(false);
                await second.SyncSeriesAsync(config, CancellationToken.None);
                Assert(handler.Requests.Count == 1 && orphans.All(File.Exists) && Directory.GetFiles(config.StrmLibraryPath, "*.strm", SearchOption.AllDirectories).Length == 12,
                    "cache reuse must not bypass the configured orphan threshold or delete unvalidated playback URLs");
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task CachedDuplicateSeriesOwnershipIsStableAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                const string catalog = "[{\"series_id\":3000000001,\"name\":\"Cache Show\",\"category_id\":161,\"last_modified\":100},{\"series_id\":3000000004,\"name\":\"Cache Show\",\"category_id\":161,\"last_modified\":100}]";
                var config = CreateProviderIdConfig(Path.Combine(root, "Media"));
                config.SmartSkipExisting = true;
                config.EnableLocalMediaFilter = true;
                var cache = Path.Combine(root, "Cache");
                string[] original = null;
                for (var run = 0; run < 2; run++)
                {
                    var handler = run == 0 ? new QueueHandler(JsonResponse(catalog), JsonResponse(CacheSeriesDetail),
                        JsonResponse(CacheSeriesDetail.Replace("3000000002", "3000000005").Replace("3000000003", "3000000006"))) : new QueueHandler(JsonResponse(catalog));
                    var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler), cache);
                    service.LocalMediaFilterFactory = (logger, path) => CachedTestLocalFilter(false);
                    await service.SyncSeriesAsync(config, CancellationToken.None);
                    var urls = Directory.GetFiles(config.StrmLibraryPath, "*.strm", SearchOption.AllDirectories).Select(File.ReadAllText).OrderBy(s => s).ToArray();
                    if (run == 0) original = urls;
                    Assert(handler.Requests.Count == (run == 0 ? 3 : 1) && urls.Length == 2 && urls.SequenceEqual(original),
                        "cached duplicate-path preflight must preserve deterministic episode ownership without extra detail requests");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private static Task SeriesDiskCacheValidationAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var cache = new SeriesDetailDiskCache(root);
                var config = CreateProviderIdConfig("unused");
                var series = XtreamResponseParser.DeserializeSeriesList(CacheSeriesCatalog, null).Single();
                var detail = JsonSerializer.Deserialize<SeriesDetailInfo>(CacheSeriesDetail);
                var now = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
                Assert(cache.TryWrite(config, series, detail, now), "valid details must be cached atomically");
                SeriesDetailInfo read;
                Assert(cache.TryRead(config, series, now.AddHours(1), out read) && read.Episodes["1"][0].Id == 3000000002 && read.Info.TmdbIdAlt == "123",
                    "large episode IDs and provider metadata must round-trip, including info without a series_id");
                Assert(!cache.TryRead(config, series, now.AddDays(7), out read) && !cache.TryRead(config, series, now.AddMinutes(-1), out read),
                    "expired and future-dated entries must force fresh validation");
                series.Plot = "changed without a timestamp change";
                Assert(!cache.TryRead(config, series, now, out read), "any catalog metadata change must invalidate the cache");
                series.Plot = string.Empty;
                series.LastModified = "0";
                Assert(!cache.TryRead(config, series, now, out read) && !cache.TryWrite(config, series, detail, now),
                    "missing/unreliable timestamps must never authorize cached details");
                series.LastModified = "100";
                foreach (var change in new[] {"account", "password", "provider"})
                {
                    var other = CreateProviderIdConfig("unused");
                    if (change == "account") other.Username = "another";
                    else if (change == "password") other.Password = "another";
                    else other.BaseUrl = "https://another.invalid";
                    Assert(!cache.TryRead(other, series, now, out read), "different provider credentials must not share cached episodes");
                }
                var path = cache.GetPath(config, series.SeriesId);
                var original = File.ReadAllText(path);
                foreach (var invalid in new[] {"{", original.Replace("\"Version\":1", "\"Version\":99"), original.Replace("\"id\":3000000002", "\"id\":0")})
                {
                    File.WriteAllText(path, invalid);
                    Assert(!cache.TryRead(config, series, now, out read), "corrupt/schema-mismatched/invalid-episode cache data must be ignored");
                }
                using (var stream = File.Create(path)) stream.SetLength(SeriesDetailDiskCache.MaximumEntryBytes + 1L);
                Assert(!cache.TryRead(config, series, now, out read), "oversized entries must not be loaded into memory");
                Assert(!cache.TryWrite(config, series, new SeriesDetailInfo(), now), "empty detail responses must never be cached");
                Assert(!Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "cache updates must not leave partial files");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }

        private static Task SeriesDiskCacheBudgetAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var now = DateTime.UtcNow;
                foreach (var item in new[] {("expired.json", 50, 15), ("old.json", 100, 2), ("new.json", 200, 0), ("abandoned.tmp", 20, 3)})
                {
                    var path = Path.Combine(root, item.Item1);
                    File.WriteAllText(path, new string('x', item.Item2));
                    File.SetLastWriteTimeUtc(path, now.AddDays(-item.Item3));
                }
                File.WriteAllText(Path.Combine(root, "unrelated.txt"), "leave alone");
                var cache = new SeriesDetailDiskCache(root, 250);
                Assert(cache.Trim(now, CancellationToken.None) == 3 && File.Exists(Path.Combine(root, "new.json")) && File.Exists(Path.Combine(root, "unrelated.txt")),
                    "cache maintenance must evict expired, abandoned and oldest excess entries without touching unrelated files");
                var blocked = Path.Combine(root, "not-a-directory");
                File.WriteAllText(blocked, "file");
                var unavailable = new SeriesDetailDiskCache(blocked);
                var series = XtreamResponseParser.DeserializeSeriesList(CacheSeriesCatalog, null).Single();
                Assert(!unavailable.TryWrite(CreateProviderIdConfig("unused"), series, JsonSerializer.Deserialize<SeriesDetailInfo>(CacheSeriesDetail), now),
                    "an unavailable disk cache must fail softly rather than failing the sync");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }

        private static async Task SeriesCacheRefreshGuardsAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                foreach (var reason in new[] {"timestamp", "metadata", "weekly", "corrupt", "no-timestamp", "disabled", "account", "disk-unavailable"})
                {
                    var folder = Path.Combine(root, reason);
                    var cacheRoot = Path.Combine(folder, "Cache");
                    var config = CreateProviderIdConfig(Path.Combine(folder, "Media"));
                    config.SmartSkipExisting = true;
                    config.EnableLocalMediaFilter = true;
                    var first = new StrmSyncService(CreateSilentLogger(), new HttpClient(new QueueHandler(JsonResponse(CacheSeriesCatalog), JsonResponse(CacheSeriesDetail))), cacheRoot);
                    first.LocalMediaFilterFactory = (logger, path) => CachedTestLocalFilter(false);
                    await first.SyncSeriesAsync(config, CancellationToken.None);
                    var catalog = CacheSeriesCatalog;
                    if (reason == "timestamp") catalog = catalog.Replace("modified\":100", "modified\":101");
                    if (reason == "metadata") catalog = catalog.Replace("\"last_modified\"", "\"plot\":\"new plot\",\"last_modified\"");
                    if (reason == "weekly") config.LastSeriesFullVerificationUtcTicks = DateTime.UtcNow.AddDays(-8).Ticks;
                    if (reason == "corrupt") File.WriteAllText(new SeriesDetailDiskCache(cacheRoot).GetPath(config, 3000000001), "{");
                    if (reason == "no-timestamp") catalog = catalog.Replace("modified\":100", "modified\":0");
                    if (reason == "disabled") config.SmartSkipExisting = false;
                    if (reason == "account") config.Username = "another";
                    if (reason == "disk-unavailable")
                    {
                        cacheRoot = Path.Combine(folder, "not-directory");
                        File.WriteAllText(cacheRoot, "file");
                    }
                    var freshDetail = reason == "timestamp"
                        ? CacheSeriesDetail.Substring(0, CacheSeriesDetail.Length - 3) + ",{\"id\":3000000007,\"season\":1,\"episode_num\":3,\"title\":\"New episode\"}]}}"
                        : CacheSeriesDetail;
                    var handler = new QueueHandler(JsonResponse(catalog), JsonResponse(freshDetail));
                    var second = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler), cacheRoot);
                    second.LocalMediaFilterFactory = (logger, path) => CachedTestLocalFilter(false);
                    await second.SyncSeriesAsync(config, CancellationToken.None);
                    Assert(handler.Requests.Count == 2 && second.GetSyncHistory().Single().Success,
                        "a fresh detail request is required and must remain functional: " + reason);
                    Assert(Directory.GetFiles(config.StrmLibraryPath, "*.strm", SearchOption.AllDirectories).Length == (reason == "timestamp" ? 3 : 2),
                        "a changed provider record must discover new episodes instead of retaining the older cached list");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task FreshSeriesFailuresNeverUseStaleCacheAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                foreach (var failure in new[] {"404", "403", "empty", "invalid"})
                {
                    var folder = Path.Combine(root, failure);
                    var config = CreateProviderIdConfig(Path.Combine(folder, "Media"));
                    config.SmartSkipExisting = true;
                    config.EnableLocalMediaFilter = true;
                    var cacheRoot = Path.Combine(folder, "Cache");
                    var first = new StrmSyncService(CreateSilentLogger(), new HttpClient(new QueueHandler(JsonResponse(CacheSeriesCatalog), JsonResponse(CacheSeriesDetail))), cacheRoot);
                    first.LocalMediaFilterFactory = (logger, path) => CachedTestLocalFilter(false);
                    await first.SyncSeriesAsync(config, CancellationToken.None);
                    config.LastSeriesFullVerificationUtcTicks = DateTime.UtcNow.AddDays(-8).Ticks;
                    var response = failure == "404" ? new HttpResponseMessage(HttpStatusCode.NotFound) :
                        failure == "403" ? new HttpResponseMessage(HttpStatusCode.Forbidden) :
                        JsonResponse(failure == "empty" ? "{\"episodes\":[]}" : CacheSeriesDetail.Replace("3000000002", "0"));
                    var handler = new QueueHandler(JsonResponse(CacheSeriesCatalog), response,
                        MediaResponse(HttpStatusCode.PartialContent), MediaResponse(HttpStatusCode.PartialContent));
                    var second = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler), cacheRoot);
                    second.LocalMediaFilterFactory = (logger, path) => CachedTestLocalFilter(false);
                    await second.SyncSeriesAsync(config, CancellationToken.None);
                    Assert(handler.Requests.Count(r => r.RequestUri.Query.Contains("action=get_series_info")) == 1 && Directory.GetFiles(config.StrmLibraryPath, "*.strm", SearchOption.AllDirectories).Length == 2,
                        "a metadata failure must preserve working episodes rather than delete them: " + failure);
                    Assert(!File.Exists(new SeriesDetailDiskCache(cacheRoot).GetPath(config, 3000000001)),
                        "a failed fresh check must invalidate, not silently reuse, older cached metadata");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task MissingVodCategoriesOnlyAreVerifiedAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var source = CreateProviderIdConfig(root);
                source.PreferCompleteVodCatalog = true;
                source.SelectedVodCategoryIds = new[] {83,88};
                source.EnableGenreBasedLibraryRouting = true;
                source.SyncDocumentaries = true;
                source.VodGenreCacheJson = "{\"1\":{\"Name\":\"Drama\",\"Genre\":\"Drama\"},\"2\":{\"Name\":\"Documentary\",\"Genre\":\"Documentary\"}}";
                const string catalog = "[{\"stream_id\":1,\"name\":\"Drama\",\"category_id\":83,\"category_ids\":[83]},{\"stream_id\":2,\"name\":\"Documentary\",\"category_id\":83,\"category_ids\":[83]}]";
                var handler = new QueueHandler(JsonResponse(catalog), JsonResponse("[]"), JsonResponse(catalog), JsonResponse("[]"));
                var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler));
                await service.SyncMoviesAsync(source, CancellationToken.None);
                var doc = (PluginConfiguration)typeof(SyncDocumentariesTask).GetMethod("BuildDocumentaryConfig", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] {source});
                await service.SyncMoviesAsync(doc, CancellationToken.None, isDocumentaries: true);
                Assert(handler.Requests.Count == 4 && handler.Requests.Count(r => r.RequestUri.Query.Contains("category_id=88")) == 2 && !handler.Requests.Any(r => r.RequestUri.Query.Contains("category_id=83")),
                    "each fresh complete catalog must query only its missing category, not refetch represented categories");
                Assert(Directory.GetFiles(Path.Combine(root, "Movies"), "*.strm", SearchOption.AllDirectories).Single().Contains("Drama") &&
                    Directory.GetFiles(Path.Combine(root, "Documentaries"), "*.strm", SearchOption.AllDirectories).Single().Contains("Documentary"),
                    "explicit empty-category checks must preserve the ordinary movie/documentary genre separation");
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task SupplementalVodOwnershipIsPreservedAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(root);
                config.PreferCompleteVodCatalog = true;
                config.SelectedVodCategoryIds = new[] {88,83};
                config.MovieFolderMode = "custom";
                config.MovieFolderMappings = "Preferred=88\nOther=83";
                var handler = new QueueHandler(
                    JsonResponse("[{\"category_id\":88,\"category_name\":\"First\"},{\"category_id\":83,\"category_name\":\"Second\"}]"),
                    JsonResponse(PartialVodCatalog),
                    JsonResponse("{\"1\":{\"stream_id\":1,\"name\":\"Included\",\"category_id\":99}}"));
                await new StrmSyncService(CreateSilentLogger(), new HttpClient(handler)).SyncMoviesAsync(config, CancellationToken.None);
                Assert(Directory.GetFiles(root, "*.strm", SearchOption.AllDirectories).Single().Contains(Path.Combine("Movies", "Preferred")) && handler.Requests.Count == 3,
                    "a cross-listed supplemental record must retain first-selected ownership and valid dictionary catalogs must remain supported");
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task FailedVodVerificationPreservesEverythingAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                foreach (var invalid in new[] {"403", "{}", "null", "{", "[{\"stream_id\":0}]"})
                {
                    var config = CreateProviderIdConfig(root);
                    config.PreferCompleteVodCatalog = true;
                    config.SelectedVodCategoryIds = new[] {83,88};
                    config.LastMovieSyncTimestamp = 999;
                    var path = Path.Combine(root, "Movies", "Existing", "Existing.strm");
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, "keep this URL");
                    var handler = new QueueHandler(JsonResponse(PartialVodCatalog), invalid == "403" ? new HttpResponseMessage(HttpStatusCode.Forbidden) : JsonResponse(invalid));
                    var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler));
                    var failed = false;
                    try { await service.SyncMoviesAsync(config, CancellationToken.None); }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is JsonException) { failed = true; }
                    Assert(failed && handler.Requests.Count == 2 && File.ReadAllText(path) == "keep this URL" && Directory.GetFiles(root, "*.strm", SearchOption.AllDirectories).Length == 1 && config.LastMovieSyncTimestamp == 999 && !service.GetSyncHistory().Single().Success,
                        "failed/ambiguous missing-category verification must abort before any writes, deletion or checkpoint updates");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task MissingVodVerificationConcurrencyAndCancellationAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                foreach (var cancel in new[] {false,true})
                using (var cancellation = new CancellationTokenSource())
                {
                    var config = CreateProviderIdConfig(Path.Combine(root, cancel.ToString()));
                    config.PreferCompleteVodCatalog = true;
                    config.SelectedVodCategoryIds = new[] {83,84,85,86,87,88};
                    config.SyncParallelism = 2;
                    var handler = new MissingVodVerificationHandler(cancel ? cancellation : null);
                    var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler));
                    var canceled = false;
                    try { await service.SyncMoviesAsync(config, cancellation.Token); }
                    catch (OperationCanceledException) { canceled = true; }
                    Assert(canceled == cancel && handler.Peak <= 2 && !handler.RequestedRepresentedCategory,
                        "missing-category checks must honor configured concurrency, cancellation and represented-category reuse");
                    if (cancel) Assert(!Directory.GetFiles(config.StrmLibraryPath, "*.strm", SearchOption.AllDirectories).Any(), "canceling verification must precede file writes");
                    else Assert(handler.Requests == 6 && handler.Peak == 2, "only five absent categories should supplement the one complete response");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private sealed class MissingVodVerificationHandler : HttpMessageHandler
        {
            private readonly CancellationTokenSource _cancellation;
            private int _active;
            internal int Requests;
            internal int Peak;
            internal bool RequestedRepresentedCategory;
            internal MissingVodVerificationHandler(CancellationTokenSource cancellation) => _cancellation = cancellation;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Requests);
                if (!request.RequestUri.Query.Contains("category_id=")) return JsonResponse(PartialVodCatalog);
                if (request.RequestUri.Query.Contains("category_id=83")) RequestedRepresentedCategory = true;
                var active = Interlocked.Increment(ref _active);
                Peak = Math.Max(Peak, active);
                try
                {
                    if (_cancellation != null) _cancellation.Cancel();
                    await Task.Delay(20, cancellationToken);
                    return JsonResponse("[]");
                }
                finally { Interlocked.Decrement(ref _active); }
            }
        }
    }
}
