using System;
using System.Collections.Generic;
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

namespace Emby.Xtream.Plugin.Tests
{
    internal static partial class Program
    {
        private const string CachedMovieCatalog = "[{\"stream_id\":3000000001,\"name\":\"Cached Movie (2000)\",\"category_id\":83,\"added\":100}]";
        private const string CachedMovieDetail = "{\"info\":{\"name\":\"Cached Movie (2000)\",\"tmdb_id\":123,\"imdb_id\":\"tt123\",\"genre\":\"Drama\",\"plot\":\"Provider plot\",\"releasedate\":\"2000-01-02\",\"video\":{\"codec_name\":\"hevc\",\"width\":3840,\"height\":2160},\"audio\":{\"codec_name\":\"aac\",\"channels\":2}}}";

        private static VodDetailInfo CachedMovieMetadata(string genre = "Drama") => new VodDetailInfo
        {
            Name = "Cached Movie (2000)", TmdbId = "123", ImdbId = "tt123", Genre = genre,
            Plot = "Provider plot", ReleaseDate = "2000-01-02",
            MediaInfo = new EpisodeMediaInfo
            {
                Video = new EpisodeVideoInfo { CodecName = "hevc", Width = 3840, Height = 2160 },
                Audio = new EpisodeAudioInfo { CodecName = "aac", Channels = 2 },
            },
        };

        private static async Task VodCacheSurvivesRestartAndKeepsLocalChecksAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(Path.Combine(root, "Media"));
                config.SmartSkipExisting = true;
                config.EnableNfoFiles = true;
                config.EnableLocalMediaFilter = true;
                var cacheRoot = Path.Combine(root, "VodCache");
                var seriesSentinel = Path.Combine(root, "series-details-v1", "keep.json");
                Directory.CreateDirectory(Path.GetDirectoryName(seriesSentinel));
                File.WriteAllText(seriesSentinel, "existing series cache");
                const string catalog = "[{\"stream_id\":3000000001,\"name\":\"Cached Movie (2000)\",\"category_id\":83,\"added\":100},{\"stream_id\":3000000002,\"name\":\"Cached Movie (2000)\",\"category_id\":83,\"added\":100}]";
                var builds = 0;
                string ownerUrl = null;
                var oldTime = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                for (var run = 0; run < 4; run++)
                {
                    var handler = run == 0 ? new QueueHandler(JsonResponse(catalog), JsonResponse(CachedMovieDetail), JsonResponse(CachedMovieDetail))
                        : new QueueHandler(JsonResponse(catalog));
                    var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler), vodDetailCachePath: cacheRoot);
                    var local = run == 2;
                    service.LocalMediaFilterFactory = (logger, path) =>
                    {
                        builds++;
                        return new LocalMediaFilter(new HashSet<string>(local ? new[] {"123"} : new string[0]),
                            new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), new HashSet<string>());
                    };
                    await service.SyncMoviesAsync(config, CancellationToken.None);
                    Assert(service.GetSyncHistory().Single().Success && handler.Requests.Count == (run == 0 ? 3 : 1),
                        "warm VOD metadata must avoid requests for duplicate records that cannot own the existing STRM");
                    var files = Directory.GetFiles(config.StrmLibraryPath, "*.strm", SearchOption.AllDirectories);
                    Assert(files.Length == (local ? 0 : 1), "cached metadata must still remove newly local movie duplicates and restore missing STRMs");
                    if (!local)
                    {
                        var nfo = Path.ChangeExtension(files.Single(), ".nfo");
                        Assert(File.ReadAllText(nfo).Contains("Provider plot") && File.ReadAllText(nfo).Contains("hevc"),
                            "cached metadata must retain provider descriptions and codecs for NFO restoration");
                        if (run == 0)
                        {
                            ownerUrl = File.ReadAllText(files.Single());
                            File.SetLastWriteTimeUtc(files.Single(), oldTime);
                            File.SetLastWriteTimeUtc(nfo, oldTime);
                        }
                        if (run == 1)
                            Assert(File.ReadAllText(files.Single()) == ownerUrl && File.GetLastWriteTimeUtc(files.Single()) == oldTime && File.GetLastWriteTimeUtc(nfo) == oldTime,
                                "warm cache reads must preserve deterministic ownership and unchanged STRM/NFO modification times");
                    }
                }
                Assert(builds == 4 && File.ReadAllText(seriesSentinel) == "existing series cache", "VOD reuse must recheck local media and never touch the series cache");
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task VodCacheIsSharedBetweenMoviesAndDocumentariesAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(Path.Combine(root, "Media"));
                config.SmartSkipExisting = true;
                config.EnableNfoFiles = true;
                config.EnableGenreBasedLibraryRouting = true;
                config.SyncDocumentaries = true;
                const string catalog = "[{\"stream_id\":3000000001,\"name\":\"Drama\",\"category_id\":83},{\"stream_id\":3000000002,\"name\":\"Documentary\",\"category_id\":83}]";
                var handler = new QueueHandler(JsonResponse(catalog), JsonResponse(CachedMovieDetail),
                    JsonResponse(CachedMovieDetail.Replace("Drama", "Documentary")), JsonResponse(catalog));
                var cacheRoot = Path.Combine(root, "Cache");
                await new StrmSyncService(CreateSilentLogger(), new HttpClient(handler), vodDetailCachePath: cacheRoot).SyncMoviesAsync(config, CancellationToken.None);
                var doc = (PluginConfiguration)typeof(SyncDocumentariesTask).GetMethod("BuildDocumentaryConfig", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] {config});
                await new StrmSyncService(CreateSilentLogger(), new HttpClient(handler), vodDetailCachePath: cacheRoot).SyncMoviesAsync(doc, CancellationToken.None, isDocumentaries: true);
                Assert(handler.Requests.Count == 4 && handler.Requests.Count(r => r.RequestUri.Query.Contains("get_vod_info")) == 2,
                    "documentary routing and NFO writes must reuse metadata fetched by Movies across service restarts");
                Assert(Directory.GetFiles(Path.Combine(config.StrmLibraryPath, "Movies"), "*.strm", SearchOption.AllDirectories).Single().Contains("Drama") &&
                    Directory.GetFiles(Path.Combine(config.StrmLibraryPath, "Documentaries"), "*.strm", SearchOption.AllDirectories).Single().Contains("Documentary"),
                    "a shared detail cache must never mix up movie/documentary genre routing");
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task UsefulUnknownGenreMetadataIsCachedAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(Path.Combine(root, "Media"));
                config.SmartSkipExisting = true;
                config.EnableNfoFiles = true;
                config.EnableGenreBasedLibraryRouting = true;
                var cache = Path.Combine(root, "Cache");
                var firstHandler = new QueueHandler(JsonResponse(CachedMovieCatalog), JsonResponse(CachedMovieDetail.Replace("\"genre\":\"Drama\",", string.Empty)));
                await new StrmSyncService(CreateSilentLogger(), new HttpClient(firstHandler), vodDetailCachePath: cache).SyncMoviesAsync(config, CancellationToken.None);
                var secondHandler = new QueueHandler(JsonResponse(CachedMovieCatalog));
                var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(secondHandler), vodDetailCachePath: cache);
                await service.SyncMoviesAsync(config, CancellationToken.None);
                Assert(secondHandler.Requests.Count == 1 && service.GetSyncHistory().Single().Success && Directory.GetFiles(config.StrmLibraryPath, "*.strm", SearchOption.AllDirectories).Length == 1,
                    "a useful response without genre must be reused while unknown genres remain in Movies");
            }
            finally { Directory.Delete(root, true); }
        }

        private static Task VodCacheIdentityAndFreshnessAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var cache = new VodDetailDiskCache(root);
                var config = CreateProviderIdConfig("unused");
                var stream = XtreamResponseParser.DeserializeVodList(CachedMovieCatalog, null).Single();
                var now = DateTime.UtcNow;
                Assert(cache.TryWrite(config, stream, CachedMovieMetadata(), now), "store valid movie detail");
                var path = cache.GetPath(config, stream.StreamId);
                var original = File.ReadAllBytes(path);
                var writeTime = File.GetLastWriteTimeUtc(path);
                VodDetailInfo detail;
                Assert(cache.TryRead(config, stream, now.AddHours(23), out detail) && detail.MediaInfo.Video.CodecName == "hevc" && detail.TmdbId == "123",
                    "full descriptive and codec metadata must survive disk serialization");
                Assert(!cache.TryRead(config, stream, now.AddHours(24), out detail) && !cache.TryRead(config, stream, now.AddMinutes(-1), out detail),
                    "movie metadata must retain its original 24-hour deadline and reject future entries");
                stream.CategoryId = 88;
                stream.CategoryIds = new[] {83,88};
                Assert(cache.TryRead(config, stream, now, out detail), "library/category ownership must not invalidate shared provider detail");
                foreach (var change in new[] {"title", "added", "extension", "icon", "rating", "tmdb"})
                {
                    var changed = XtreamResponseParser.DeserializeVodList(CachedMovieCatalog, null).Single();
                    if (change == "title") changed.Name += " changed";
                    if (change == "added") changed.Added = 101;
                    if (change == "extension") changed.ContainerExtension = "mkv";
                    if (change == "icon") changed.StreamIcon = "https://image.invalid/changed";
                    if (change == "rating") changed.Rating = "9";
                    if (change == "tmdb") changed.TmdbId = "999";
                    Assert(!cache.TryRead(config, changed, now, out detail), "catalog changes must invalidate cached movie metadata: " + change);
                }
                foreach (var scope in new[] {"provider", "username", "password"})
                {
                    var changed = CreateProviderIdConfig("unused");
                    if (scope == "provider") changed.BaseUrl = "https://another.invalid";
                    if (scope == "username") changed.Username = "another";
                    if (scope == "password") changed.Password = "another";
                    Assert(!cache.TryRead(changed, stream, now, out detail), "provider/account changes must never reuse another namespace's cached detail");
                }
                Assert(original.SequenceEqual(File.ReadAllBytes(path)) && File.GetLastWriteTimeUtc(path) == writeTime,
                    "cache hits and misses must not renew or rewrite existing entries");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }

        private static async Task ExpiredVodMetadataRefreshesAndPreservesRoutingOnFailureAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                foreach (var kind in new[] {"403", "404", "empty", "malformed", "missing-genre", "success", "disabled"})
                {
                    var folder = Path.Combine(root, kind);
                    var config = CreateProviderIdConfig(Path.Combine(folder, "Media"));
                    config.MovieRootFolderName = "Documentaries";
                    config.SmartSkipExisting = true;
                    config.EnableNfoFiles = true;
                    config.EnableGenreBasedLibraryRouting = true;
                    var cacheRoot = Path.Combine(folder, "Cache");
                    var first = new StrmSyncService(CreateSilentLogger(), new HttpClient(new QueueHandler(JsonResponse(CachedMovieCatalog), JsonResponse(CachedMovieDetail.Replace("Drama", "Documentary")))) , vodDetailCachePath: cacheRoot);
                    await first.SyncMoviesAsync(config, CancellationToken.None, isDocumentaries: true);
                    var cache = new VodDetailDiskCache(cacheRoot);
                    var stream = XtreamResponseParser.DeserializeVodList(CachedMovieCatalog, null).Single();
                    Assert(cache.TryWrite(config, stream, CachedMovieMetadata("Documentary"), DateTime.UtcNow.AddDays(kind == "disabled" ? 0 : -2)), "seed metadata for a refresh check");
                    if (kind == "disabled") config.SmartSkipExisting = false;
                    var path = Directory.GetFiles(config.StrmLibraryPath, "*.strm", SearchOption.AllDirectories).Single();
                    var nfo = Path.ChangeExtension(path, ".nfo");
                    var original = File.ReadAllText(nfo);
                    var response = kind == "403" ? new HttpResponseMessage(HttpStatusCode.Forbidden) :
                        kind == "404" ? new HttpResponseMessage(HttpStatusCode.NotFound) :
                        JsonResponse(kind == "empty" ? "{\"info\":{}}" : kind == "malformed" ? "{" :
                            kind == "missing-genre" ? CachedMovieDetail.Replace("\"genre\":\"Drama\",", string.Empty) : CachedMovieDetail.Replace("Drama", "Documentary"));
                    var handler = new QueueHandler(JsonResponse(CachedMovieCatalog), response);
                    var second = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler), vodDetailCachePath: cacheRoot);
                    await second.SyncMoviesAsync(config, CancellationToken.None, isDocumentaries: true);
                    Assert(handler.Requests.Count == 2 && second.GetSyncHistory().Single().Success && File.Exists(path) && File.ReadAllText(nfo) == original,
                        "expired/disabled cache must fetch fresh detail without reclassifying known documentaries or damaging unchanged files: " + kind);
                    if (new[] {"403", "404", "empty", "malformed"}.Contains(kind))
                        Assert(!cache.HasEntry(config, stream.StreamId), "failed or empty refreshes must invalidate old full metadata rather than renewing it");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private static Task InvalidVodCacheAndBudgetAreSafeAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var cacheRoot = Path.Combine(root, "VodCache");
                var cache = new VodDetailDiskCache(cacheRoot);
                var config = CreateProviderIdConfig("unused");
                var stream = XtreamResponseParser.DeserializeVodList(CachedMovieCatalog, null).Single();
                var now = DateTime.UtcNow;
                Assert(cache.TryWrite(config, stream, CachedMovieMetadata(), now), "seed a valid metadata entry");
                var path = cache.GetPath(config, stream.StreamId);
                var original = File.ReadAllText(path);
                VodDetailInfo detail;
                foreach (var invalid in new[] {"{", original.Replace("\"Version\":1", "\"Version\":99"), original.Replace("\"StreamId\":3000000001", "\"StreamId\":0")})
                {
                    File.WriteAllText(path, invalid);
                    Assert(!cache.TryRead(config, stream, now, out detail), "invalid schema, IDs and JSON must fall through to provider lookup");
                }
                using (var output = File.Create(path)) output.SetLength(VodDetailDiskCache.MaximumEntryBytes + 1L);
                Assert(!cache.TryRead(config, stream, now, out detail), "oversized cache files must not be loaded into memory");
                Assert(!cache.TryWrite(config, stream, new VodDetailInfo {MediaInfo = new EpisodeMediaInfo()}, now), "empty/default metadata must not become a cached success");
                Assert(cache.TryWrite(config, stream, new VodDetailInfo {MediaInfo = new EpisodeMediaInfo {VideoCodec = "hevc"}}, now), "useful codec-only responses may be cached");
                Assert(!Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "atomic cache writes must clean temporary files");
                var blocked = Path.Combine(root, "file-not-directory");
                File.WriteAllText(blocked, "file");
                Assert(!new VodDetailDiskCache(blocked).TryWrite(config, stream, CachedMovieMetadata(), now), "cache I/O failure must be nonfatal");

                var budgetRoot = Path.Combine(root, "Budget");
                Directory.CreateDirectory(budgetRoot);
                File.WriteAllText(Path.Combine(budgetRoot, "old.json"), new string('x', 200));
                File.SetLastWriteTimeUtc(Path.Combine(budgetRoot, "old.json"), now.AddDays(-8));
                File.WriteAllText(Path.Combine(budgetRoot, "new.json"), new string('x', 100));
                var sentinel = Path.Combine(root, "series-details-v1", "keep.json");
                Directory.CreateDirectory(Path.GetDirectoryName(sentinel));
                File.WriteAllText(sentinel, "keep series cache");
                Assert(new VodDetailDiskCache(budgetRoot, 150).Trim(now, CancellationToken.None) == 1 && File.Exists(Path.Combine(budgetRoot, "new.json")) && File.ReadAllText(sentinel) == "keep series cache",
                    "VOD age/size maintenance must be scoped to its own cache, never series data");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }

        private static async Task VodCacheDoesNotEagerlyPopulateKnownCatalogAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(Path.Combine(root, "Media"));
                config.SmartSkipExisting = true;
                config.EnableGenreBasedLibraryRouting = true;
                config.VodGenreCacheJson = "{\"3000000001\":{\"Name\":\"Cached Movie (2000)\",\"Genre\":\"Drama\"}}";
                var cacheRoot = Path.Combine(root, "Cache");
                var handler = new QueueHandler(JsonResponse(CachedMovieCatalog));
                await new StrmSyncService(CreateSilentLogger(), new HttpClient(handler), vodDetailCachePath: cacheRoot).SyncMoviesAsync(config, CancellationToken.None);
                Assert(handler.Requests.Count == 1 && !Directory.Exists(cacheRoot), "a new disk cache must not trigger an eager provider request for every already-classified movie");
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task VodCatalogChangesAndCacheIoStillUseFreshMetadataAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                foreach (var change in new[] {"rating", "added", "corrupt", "account", "disk-unavailable"})
                {
                    var folder = Path.Combine(root, change);
                    var config = CreateProviderIdConfig(Path.Combine(folder, "Media"));
                    config.SmartSkipExisting = true;
                    config.EnableNfoFiles = true;
                    config.EnableGenreBasedLibraryRouting = true;
                    var cacheRoot = Path.Combine(folder, "Cache");
                    await new StrmSyncService(CreateSilentLogger(), new HttpClient(new QueueHandler(JsonResponse(CachedMovieCatalog), JsonResponse(CachedMovieDetail))), vodDetailCachePath: cacheRoot)
                        .SyncMoviesAsync(config, CancellationToken.None);
                    var catalog = CachedMovieCatalog;
                    if (change == "rating") catalog = catalog.Replace("\"added\":100", "\"added\":100,\"rating\":\"9\"");
                    if (change == "added") catalog = catalog.Replace("\"added\":100", "\"added\":101");
                    if (change == "corrupt") File.WriteAllText(new VodDetailDiskCache(cacheRoot).GetPath(config, 3000000001), "{");
                    if (change == "account") config.Password = "another-password";
                    if (change == "disk-unavailable")
                    {
                        cacheRoot = Path.Combine(folder, "blocked-cache");
                        File.WriteAllText(cacheRoot, "not a directory");
                        File.Delete(Path.ChangeExtension(Directory.GetFiles(config.StrmLibraryPath, "*.strm", SearchOption.AllDirectories).Single(), ".nfo"));
                    }
                    var handler = new QueueHandler(JsonResponse(catalog), JsonResponse(CachedMovieDetail));
                    var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler), vodDetailCachePath: cacheRoot);
                    await service.SyncMoviesAsync(config, CancellationToken.None);
                    var path = Directory.GetFiles(config.StrmLibraryPath, "*.strm", SearchOption.AllDirectories).Single();
                    Assert(service.GetSyncHistory().Single().Success && handler.Requests.Count == 2 && File.ReadAllText(Path.ChangeExtension(path, ".nfo")).Contains("Provider plot"),
                        "catalog/account changes, corruption and unavailable cache I/O must still use fresh provider detail safely: " + change);
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task VodCacheRetainsOrphanThresholdAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(Path.Combine(root, "Media"));
                config.SmartSkipExisting = true;
                config.EnableNfoFiles = true;
                config.OrphanSafetyThreshold = 0.01;
                var cacheRoot = Path.Combine(root, "Cache");
                await new StrmSyncService(CreateSilentLogger(), new HttpClient(new QueueHandler(JsonResponse(CachedMovieCatalog), JsonResponse(CachedMovieDetail))), vodDetailCachePath: cacheRoot)
                    .SyncMoviesAsync(config, CancellationToken.None);
                var owner = Directory.GetFiles(config.StrmLibraryPath, "*.strm", SearchOption.AllDirectories).Single();
                File.Delete(Path.ChangeExtension(owner, ".nfo"));
                var extra = Enumerable.Range(1, 11).Select(i => Path.Combine(Path.GetDirectoryName(owner), "orphan" + i + ".strm")).ToArray();
                foreach (var path in extra) File.WriteAllText(path, "existing URL");
                var handler = new QueueHandler(JsonResponse(CachedMovieCatalog));
                await new StrmSyncService(CreateSilentLogger(), new HttpClient(handler), vodDetailCachePath: cacheRoot).SyncMoviesAsync(config, CancellationToken.None);
                Assert(handler.Requests.Count == 1 && File.Exists(Path.ChangeExtension(owner, ".nfo")) && extra.All(File.Exists),
                    "restoring NFO data from the movie cache must never bypass the configured deletion threshold");
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task VodCacheCancellationPreservesFilesAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                using (var cancellation = new CancellationTokenSource())
                {
                    var config = CreateProviderIdConfig(Path.Combine(root, "Media"));
                    config.SmartSkipExisting = true;
                    config.EnableNfoFiles = true;
                    var existing = Path.Combine(config.StrmLibraryPath, "Movies", "Existing", "Existing.strm");
                    Directory.CreateDirectory(Path.GetDirectoryName(existing));
                    File.WriteAllText(existing, "keep this URL");
                    var handler = new CancelVodMetadataHandler(cancellation);
                    var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler), vodDetailCachePath: Path.Combine(root, "Cache"));
                    var canceled = false;
                    try { await service.SyncMoviesAsync(config, cancellation.Token); }
                    catch (OperationCanceledException) { canceled = true; }
                    Assert(canceled && handler.Requests == 2 && File.ReadAllText(existing) == "keep this URL" && Directory.GetFiles(config.StrmLibraryPath, "*.strm", SearchOption.AllDirectories).Length == 1,
                        "canceling a fresh detail request must not fall through into file writes or orphan deletion");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private sealed class CancelVodMetadataHandler : HttpMessageHandler
        {
            private readonly CancellationTokenSource _cancellation;
            internal int Requests;
            internal CancelVodMetadataHandler(CancellationTokenSource cancellation) => _cancellation = cancellation;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                if (request.RequestUri.Query.Contains("get_vod_streams")) return Task.FromResult(JsonResponse(CachedMovieCatalog));
                _cancellation.Cancel();
                return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
            }
        }
    }
}
