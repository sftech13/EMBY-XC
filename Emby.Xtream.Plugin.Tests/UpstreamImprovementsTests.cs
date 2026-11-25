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
using System.Xml.Linq;
using Emby.Xtream.Plugin.Client.Models;
using Emby.Xtream.Plugin.Service;

namespace Emby.Xtream.Plugin.Tests
{
    internal static partial class Program
    {
        private static JsonSerializerOptions ProviderTestOptions() => new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new ProviderMetadataStringConverter(), new FlexibleNullableInt32Converter() },
        };

        private static Task EpisodeNumberingKeepsSpecialsAsync()
        {
            foreach (var sample in new[] { "0", "\"0\"" })
            {
                var episode = JsonSerializer.Deserialize<EpisodeInfo>("{\"id\":3000000001,\"episode_num\":" + sample + "}", ProviderTestOptions());
                Assert(EpisodeNumbering.Season("0", episode) == 0 && EpisodeNumbering.Number(episode) == 0, "explicit specials and episode zero must survive");
                var cacheOptions = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault };
                var restored = JsonSerializer.Deserialize<EpisodeInfo>(JsonSerializer.Serialize(episode, cacheOptions));
                Assert(restored.EpisodeNumberWasSpecified && restored.EpisodeNum == 0, "disk cache serialization must not discard a real zero");
            }
            foreach (var sample in new[] { "{}", "{\"episode_num\":null}", "{\"episode_num\":{\"bad\":1}}", "{\"episode_num\":[]}", "{\"episode_num\":-2}" })
            {
                var episode = JsonSerializer.Deserialize<EpisodeInfo>(sample, ProviderTestOptions());
                Assert(EpisodeNumbering.Season("5", episode) == 5 && EpisodeNumbering.Number(episode) == 1, "missing numbers must not turn normal seasons into specials");
            }
            Assert(EpisodeNumbering.Season("invalid", new EpisodeInfo()) == 1, "unknown season needs conservative fallback");
            Assert(EpisodeNumbering.Season("0", new EpisodeInfo { Season = 3 }) == 3, "positive episode season takes precedence over a bad map key");
            return Task.CompletedTask;
        }

        private static async Task SelectiveSpecialsMigrationAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var media = Path.Combine(root, "Media");
                var config = CreateProviderIdConfig(media);
                config.SmartSkipExisting = true;
                config.EnableNfoFiles = true;
                config.CleanupOrphans = false;
                config.LastMovieSyncTimestamp = 123;
                config.LastSeriesSyncTimestamp = 100;
                config.SeriesFastPathSettingsHash = "previous-layout-settings";
                config.LastSeriesFullVerificationUtcTicks = DateTime.UtcNow.Ticks;
                config.SeriesEpisodeHashesJson = "{\"3000000001\":\"old-layout\"}";
                var series = XtreamResponseParser.DeserializeSeriesList(CacheSeriesCatalog, null).Single();
                var detail = JsonSerializer.Deserialize<SeriesDetailInfo>("{\"info\":{\"name\":\"Cache Show\",\"tmdb_id\":\"123\"},\"episodes\":{\"0\":[{\"id\":3000000002,\"season\":0,\"episode_num\":0,\"title\":\"Special\",\"container_extension\":\"mkv\"}],\"1\":[{\"id\":3000000003,\"season\":1,\"episode_num\":2,\"title\":\"Regular\",\"container_extension\":\"mkv\"}]}}");
                var cacheRoot = Path.Combine(root, "Cache");
                var cache = new SeriesDetailDiskCache(cacheRoot);
                Assert(cache.TryWrite(config, series, detail, DateTime.UtcNow), "seed existing valid cache");
                var cacheFile = cache.GetPath(config, series.SeriesId);
                var cacheBytes = File.ReadAllBytes(cacheFile);
                var cacheTime = File.GetLastWriteTimeUtc(cacheFile);
                var show = Path.Combine(media, "TV Shows", "Cache Show");
                var oldSpecial = Path.Combine(show, "Season 01", "Cache Show - S01E01 - Special.strm");
                var regular = Path.Combine(show, "Season 01", "Cache Show - S01E02 - Regular.strm");
                Directory.CreateDirectory(Path.GetDirectoryName(regular));
                File.WriteAllText(oldSpecial, "https://provider.invalid/series/u/p/3000000002.mkv");
                File.WriteAllText(regular, "https://provider.invalid/series/u/p/3000000003.mkv");
                NfoWriter.WriteEpisodeNfo(Path.ChangeExtension(oldSpecial, ".nfo"), "Special", 1, 1, OwnershipEpisode());
                EnrichNfo(Path.ChangeExtension(oldSpecial, ".nfo"));
                NfoWriter.WriteEpisodeNfo(Path.ChangeExtension(regular, ".nfo"), "Regular", 1, 2, OwnershipEpisode());
                File.SetLastWriteTimeUtc(regular, OldNfoTime);
                File.SetLastWriteTimeUtc(Path.ChangeExtension(regular, ".nfo"), OldNfoTime);
                for (var run = 0; run < 2; run++)
                {
                    config.CleanupOrphans = run == 1;
                    var handler = new QueueHandler(JsonResponse(CacheSeriesCatalog));
                    var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler), cacheRoot, nfoOwnershipPath: Path.Combine(root, "State"));
                    await service.SyncSeriesAsync(config, CancellationToken.None);
                    Assert(service.GetSyncHistory().Single().Success && handler.Requests.Count == 1, "layout migration must reuse existing detail cache even on a later cleanup run");
                }
                var special = Path.Combine(show, "Season 00", "Cache Show - S00E00 - Special.strm");
                var metadata = XElement.Load(Path.ChangeExtension(special, ".nfo"));
                Assert(File.Exists(special) && !File.Exists(oldSpecial), "only the proven superseded special path should be cleaned");
                Assert(metadata.Element("season")?.Value == "0" && metadata.Element("episode")?.Value == "0" && metadata.Element("uniqueid")?.Value == "98765", "specials must keep enrichment with corrected NFO identity");
                Assert(File.GetLastWriteTimeUtc(regular) == OldNfoTime && File.GetLastWriteTimeUtc(Path.ChangeExtension(regular, ".nfo")) == OldNfoTime, "ordinary episodes must not be regenerated");
                Assert(cacheBytes.SequenceEqual(File.ReadAllBytes(cacheFile)) && cacheTime == File.GetLastWriteTimeUtc(cacheFile) && config.LastMovieSyncTimestamp == 123,
                    "layout correction cannot clear/rewrite detail caches or reset unrelated watermarks");
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task TolerantOptionalProviderMetadataAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(root);
                config.EnableNfoFiles = true;
                var weird = "{\"info\":{\"name\":\"Cache Show\",\"plot\":[],\"cast\":{},\"releaseDate\":1955,\"rating\":false,\"category_id\":[]},\"episodes\":{\"2\":[{\"id\":3000000002,\"episode_num\":0,\"title\":\"Special\",\"rating\":{},\"duration\":[],\"info\":{\"plot\":123,\"releaseDate\":1955,\"director\":[],\"duration_secs\":[],\"width\":{},\"rating\":true}}]}}";
                var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(new QueueHandler(JsonResponse(CacheSeriesCatalog), JsonResponse(weird))));
                await service.SyncSeriesAsync(config, CancellationToken.None);
                Assert(service.GetSyncHistory().Single().Success, "optional malformed fields must not abort a valid episode sync");
                var file = Directory.GetFiles(root, "*.strm", SearchOption.AllDirectories).Single();
                Assert(file.Contains("Season 02") && file.Contains("S02E00"), "map-season and episode zero must agree in path and metadata");
                Assert(XElement.Load(Path.ChangeExtension(file, ".nfo")).Element("plot")?.Value == "123", "numeric descriptive metadata should remain usable");
                foreach (var bad in new[] { "[]", "{}", "null", "true", "0", "9223372036854775808" })
                    AssertJsonRejected(() => JsonSerializer.Deserialize<EpisodeInfo>("{\"id\":" + bad + "}", ProviderTestOptions()));
                AssertJsonRejected(() => XtreamResponseParser.DeserializeVodList("[{\"stream_id\":1,\"name\":[]}]", ProviderTestOptions()));
                AssertJsonRejected(() => XtreamResponseParser.DeserializeSeriesList("[{\"series_id\":1,\"name\":null}]", ProviderTestOptions()));
                var optionalName = JsonSerializer.Deserialize<SeriesDetailInfo>(weird.Replace("\"name\":\"Cache Show\"", "\"name\":null"), ProviderTestOptions());
                Assert(optionalName.Episodes["2"].Single().Id == 3000000002, "an optional detail-info name must not be mistaken for missing catalog identity");
                var movieRoot = Path.Combine(root, "MovieTest");
                var movieConfig = CreateProviderIdConfig(movieRoot);
                movieConfig.EnableNfoFiles = true;
                var movie = new StrmSyncService(CreateSilentLogger(), new HttpClient(new QueueHandler(
                    JsonResponse("[{\"stream_id\":3000000005,\"name\":\"Movie\",\"category_id\":83}]"),
                    JsonResponse("{\"info\":{\"plot\":{},\"description\":123,\"cast\":[],\"releasedate\":1955,\"genre\":[{},\"Drama\"],\"video_codec\":true,\"backdrop_path\":[{},\"https://art.invalid/backdrop.jpg\"]}}"))));
                await movie.SyncMoviesAsync(movieConfig, CancellationToken.None);
                Assert(movie.GetSyncHistory().Single().Success, "malformed optional movie fields must not lose useful descriptive metadata");
                var nfo = XElement.Load(Directory.GetFiles(movieRoot, "*.nfo", SearchOption.AllDirectories).Single());
                Assert(nfo.Element("plot")?.Value == "123" && nfo.Element("genre")?.Value == "Drama" && nfo.Element("fileinfo") == null,
                    "structured metadata and boolean codecs must be omitted, not invented as JSON/codec text");
            }
            finally { Directory.Delete(root, true); }
        }

        private static Task AmbiguousLegacyEpisodeCacheIsRefreshedAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(root);
                var cache = new SeriesDetailDiskCache(Path.Combine(root, "Cache"));
                var series = XtreamResponseParser.DeserializeSeriesList(CacheSeriesCatalog, null).Single();
                var detail = JsonSerializer.Deserialize<SeriesDetailInfo>(CacheSeriesDetail);
                detail.Episodes["1"][0].EpisodeNum = 0;
                Assert(cache.TryWrite(config, series, detail, DateTime.UtcNow), "seed an explicit zero");
                SeriesDetailInfo restored;
                Assert(cache.TryRead(config, series, DateTime.UtcNow, out restored) && restored.Episodes["1"][0].EpisodeNum == 0,
                    "new cache entries must preserve explicit zero");
                var path = cache.GetPath(config, series.SeriesId);
                var json = File.ReadAllText(path);
                Assert(json.Contains("\"episode_num\":0,"), "fixture must contain an explicit zero");
                File.WriteAllText(path, json.Replace("\"episode_num\":0,", ""));
                var legacy = File.ReadAllBytes(path);
                Assert(!cache.TryRead(config, series, DateTime.UtcNow, out restored), "an old cache that omitted zero cannot safely decide the episode identity");
                Assert(legacy.SequenceEqual(File.ReadAllBytes(path)), "requesting a selective refresh must not clear or rewrite the old cache");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }

        private static async Task SpecialsMigrationHonorsDeletionGuardsAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var media = Path.Combine(root, "Media");
                var config = CreateProviderIdConfig(media);
                config.SmartSkipExisting = true;
                config.OrphanSafetyThreshold = 0.01;
                var series = XtreamResponseParser.DeserializeSeriesList(CacheSeriesCatalog, null).Single();
                var detail = JsonSerializer.Deserialize<SeriesDetailInfo>(CacheSeriesDetail);
                detail.Episodes.Clear();
                detail.Episodes["0"] = new List<EpisodeInfo> { new EpisodeInfo { Id = 3000000002, Title = "Special", EpisodeNum = 0, ContainerExtension = "mkv" } };
                detail.Episodes["1"] = Enumerable.Range(1, 12).Select(number => new EpisodeInfo
                { Id = 3000000010 + number, Title = "Regular " + number, Season = 1, EpisodeNum = number, ContainerExtension = "mkv" }).ToList();
                var show = Path.Combine(media, "TV Shows", "Cache Show");
                var season = Path.Combine(show, "Season 01");
                Directory.CreateDirectory(season);
                foreach (var e in detail.Episodes["1"])
                    File.WriteAllText(Path.Combine(season, $"Cache Show - S01E{e.EpisodeNum:D2} - {e.Title}.strm"), $"https://provider.invalid/series/u/p/{e.Id}.mkv");
                var old = Path.Combine(season, "Cache Show - S01E01 - Special.strm");
                File.WriteAllText(old, "https://provider.invalid/series/u/p/3000000002.mkv");
                var cacheRoot = Path.Combine(root, "Cache");
                Assert(new SeriesDetailDiskCache(cacheRoot).TryWrite(config, series, detail, DateTime.UtcNow), "seed special and normal cached metadata");
                for (var run = 0; run < 2; run++)
                {
                    if (run == 1) { config.OrphanSafetyThreshold = 1; config.EnableOrphanPreview = true; }
                    var handler = new QueueHandler(JsonResponse(CacheSeriesCatalog));
                    var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler), cacheRoot);
                    await service.SyncSeriesAsync(config, CancellationToken.None);
                    Assert(service.GetSyncHistory().Single().Success && handler.Requests.Count == 1 && File.Exists(old),
                        "threshold and preview must retain old paths without playback-validation downloads");
                }
                var canonical = Path.Combine(show, "Season 00", "Cache Show - S00E00 - Special.strm");
                var pending = JsonSerializer.Deserialize<List<string>>(config.PendingOrphansJson);
                Assert(pending.Count == 1 && Path.GetFileName(pending[0]) == Path.GetFileName(old), "preview must stage the exact superseded path");
                Assert(StrmSyncService.HasCanonicalReplacement(old, media, config, pending), "manual preview commit can verify a real surviving same-URL replacement");
                pending.Add(Path.GetRelativePath(media, canonical));
                Assert(!StrmSyncService.HasCanonicalReplacement(old, media, config, pending), "two paths both staged for deletion cannot justify deleting one another");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void SeedFailure(StrmSyncService service, FailedSyncItem failure)
        {
            var field = typeof(StrmSyncService).GetField("_failedItems", BindingFlags.Instance | BindingFlags.NonPublic);
            ((List<FailedSyncItem>)field.GetValue(service)).Add(failure);
        }

        private static async Task RetryUsesNormalSeriesWriterAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(root);
                config.SyncSeries = true;
                config.EnableNfoFiles = true;
                config.EnableSeriesIdFolderNaming = true;
                config.SeriesFolderMode = "custom";
                config.SeriesFolderMappings = "Mapped=161";
                config.SeriesRootFolderName = "My TV";
                var handler = new QueueHandler(JsonResponse("[{\"category_id\":161,\"category_name\":\"TV\"}]"), JsonResponse(CacheSeriesCatalog), JsonResponse(CacheSeriesDetail));
                var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler), nfoOwnershipPath: Path.Combine(root, "State"));
                SeedFailure(service, new FailedSyncItem { ItemType = "Series", StreamId = 3000000001, Name = "Cache Show", CategoryId = 161 });
                SeedFailure(service, new FailedSyncItem { ItemType = "Movie", StreamId = 99, Name = "Disabled movie" });
                await service.RetryFailedAsync(config, CancellationToken.None);
                var paths = Directory.GetFiles(root, "*.strm", SearchOption.AllDirectories);
                Assert(paths.Length == 2 && paths.All(path => path.Contains("My TV") && path.Contains("Mapped") && path.Contains("[tmdbid=123]")), "retry must use current mappings, root and metadata-ID naming");
                Assert(service.FailedItems.Count == 1 && service.FailedItems[0].StreamId == 99, "a successful series retry cannot clear another library's failure");
                Assert(!Directory.Exists(Path.Combine(root, "My TV", "Cache Show")), "retry cannot create a separate old-style folder");
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task RetryKeepsDocumentaryRoutingAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(root);
                config.SyncDocuSeries = true;
                config.SyncDocumentaries = true;
                config.SelectedDocuSeriesCategoryIds = new[] {161};
                config.SelectedDocumentaryCategoryIds = new[] {83};
                config.DocuSeriesRootFolderName = "My Docs";
                config.DocumentaryRootFolderName = "Doc Movies";
                var handler = new QueueHandler(JsonResponse(CacheSeriesCatalog), JsonResponse(CacheSeriesDetail),
                    JsonResponse("[{\"stream_id\":3000000005,\"name\":\"Doc Movie\",\"category_id\":83,\"added\":101}]"));
                var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler));
                SeedFailure(service, new FailedSyncItem { ItemType = "Series", ContentType = "DocuSeries", StreamId = 3000000001, Name = "Cache Show" });
                SeedFailure(service, new FailedSyncItem { ItemType = "Movie", ContentType = "Documentaries", StreamId = 3000000005, Name = "Doc Movie" });
                await service.RetryFailedAsync(config, CancellationToken.None);
                Assert(service.FailedItems.Count == 0 && Directory.Exists(Path.Combine(root, "My Docs")) && Directory.Exists(Path.Combine(root, "Doc Movies")), "documentary retries must go to their own configured roots");
                Assert(config.LastDocuSeriesSyncTimestamp == 100 && config.LastDocumentarySyncTimestamp == 101 && config.LastSeriesSyncTimestamp == 0 && config.LastMovieSyncTimestamp == 0,
                    "retry checkpoints must be copied back to the correct library only");
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task RetryRefreshesOnlyFailedSeriesAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(Path.Combine(root, "Media"));
                config.SyncSeries = true;
                config.SmartSkipExisting = true;
                var cache = Path.Combine(root, "Cache");
                var first = new StrmSyncService(CreateSilentLogger(), new HttpClient(new QueueHandler(
                    JsonResponse(CacheSeriesCatalog), JsonResponse(CacheSeriesDetail))), cache);
                await first.SyncSeriesAsync(config, CancellationToken.None);
                var handler = new QueueHandler(JsonResponse(CacheSeriesCatalog), JsonResponse(CacheSeriesDetail));
                var retry = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler), cache);
                SeedFailure(retry, new FailedSyncItem { ItemType = "Series", StreamId = 3000000001, Name = "Cache Show" });
                await retry.RetryFailedAsync(config, CancellationToken.None);
                Assert(handler.Requests.Count == 2 && retry.FailedItems.Count == 0,
                    "failed IDs must bypass both filename-only and per-series detail-cache skips without clearing the cache globally");
            }
            finally { Directory.Delete(root, true); }
        }

        private static async Task RetryWaitsForActiveSyncAsync()
        {
            var root = CreateCompleteVodTestRoot();
            using (var handler = new BlockingSyncCatalogHandler(false))
            using (var client = new HttpClient(handler))
            {
                Task active = null;
                Task retry = null;
                try
                {
                    var config = CreateProviderIdConfig(root);
                    config.SyncMovies = true;
                    var service = new StrmSyncService(CreateSilentLogger(), client);
                    active = service.SyncMoviesAsync(config, CancellationToken.None);
                    await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    SeedFailure(service, new FailedSyncItem { ItemType = "Movie", StreamId = 1, Name = "Queued Movie" });
                    retry = service.RetryFailedAsync(config, CancellationToken.None);
                    Assert(!retry.IsCompleted && handler.CatalogRequests == 1 && service.MovieProgress.IsRunning,
                        "a retry must wait rather than overlap and replace an active movie sync's progress");
                    handler.Release.TrySetResult(true);
                    await Task.WhenAll(active, retry).WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(handler.CatalogRequests == 2 && service.FailedItems.Count == 0, "queued retry must run after the normal sync releases its slot");
                }
                finally
                {
                    handler.Release.TrySetResult(true);
                    if (active != null && !active.IsCompleted) await active;
                    if (retry != null && !retry.IsCompleted) await retry;
                    Directory.Delete(root, true);
                }
            }
        }

        private static async Task RetryFailureAndCancellationStaySafeAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                foreach (var response in new[] { "[]", "false", CacheSeriesCatalog })
                {
                    var config = CreateProviderIdConfig(root);
                    config.SyncSeries = true;
                    var handler = new QueueHandler(JsonResponse(response), new HttpResponseMessage(HttpStatusCode.Forbidden));
                    var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(handler));
                    SeedFailure(service, new FailedSyncItem { ItemType = "Series", StreamId = 3000000001, Name = "Cache Show" });
                    try { await service.RetryFailedAsync(config, CancellationToken.None); }
                    catch (Exception error) when (error is JsonException || error is InvalidOperationException || error is HttpRequestException) { }
                    Assert(service.FailedItems.Count > 0, "missing catalogs or failed detail cannot falsely count as a successful retry");
                }
                var disabled = new StrmSyncService(CreateSilentLogger(), new HttpClient(new QueueHandler()));
                SeedFailure(disabled, new FailedSyncItem { ItemType = "Movie", StreamId = 1, Name = "Disabled" });
                await disabled.RetryFailedAsync(CreateProviderIdConfig(root), CancellationToken.None);
                Assert(disabled.FailedItems.Count == 1, "disabled library failures must stay pending");
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel();
                    var threw = false;
                    try { await disabled.RetryFailedAsync(CreateProviderIdConfig(root), canceled.Token); }
                    catch (OperationCanceledException) { threw = true; }
                    Assert(threw && disabled.FailedItems.Count == 1, "retry cancellation must propagate without clearing failures");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private static Task OwnedPlaybackUrlIsStrictAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(root);
                var file = Path.Combine(root, "Movie.strm");
                foreach (var url in new[] { "https://provider.invalid/movie/u/p/9223372036854775807.mkv", "https://provider.invalid/series/u/p/1.mp4", "https://provider.invalid//movie/u/p/1.mkv" })
                {
                    File.WriteAllText(file, url);
                    Assert(OwnedContentFiles.IsOwnedStrm(file, root, config), "valid 64-bit provider playback URL should establish ownership");
                }
                foreach (var url in new[] { "https://other.invalid/movie/u/p/1.mkv", "https://provider.invalid.evil/movie/u/p/1.mkv", "https://provider.invalid/movie/other/p/1.mkv", "https://provider.invalid/movie/u/wrong/1.mkv", "https://provider.invalid/media/1.mkv", "https://provider.invalid/movie/u/p/0.mkv", "https://provider.invalid/movie/u/p/9223372036854775808.mkv", "http://provider.invalid/movie/u/p/1.mkv", "https://provider.invalid/movie/u/p/1.mkv\nnotes" })
                {
                    File.WriteAllText(file, url);
                    Assert(!OwnedContentFiles.IsOwnedStrm(file, root, config), "foreign/invalid URL cannot establish ownership: " + url);
                }
                var note = Path.Combine(root, "notes.txt");
                File.WriteAllText(note, "https://provider.invalid/movie/u/p/1.mkv");
                Assert(!OwnedContentFiles.DeleteStrm(note, root, config) && File.Exists(note), "a URL in a non-STRM file does not authorize deletion");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }

        private static Task OwnedDeletionPreservesForeignFilesAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(root);
                var folder = Path.Combine(root, "Mixed");
                Directory.CreateDirectory(folder);
                var owned = Path.Combine(folder, "Owned.strm");
                var foreign = Path.Combine(folder, "Foreign.strm");
                File.WriteAllText(owned, "https://provider.invalid/movie/u/p/1.mkv");
                File.WriteAllText(Path.ChangeExtension(owned, ".nfo"), "<movie />");
                File.WriteAllText(foreign, "https://other.invalid/movie/u/p/1.mkv");
                File.WriteAllText(Path.Combine(folder, "custom.nfo"), "<movie><plot>mine</plot></movie>");
                File.WriteAllText(Path.Combine(folder, "Local.mkv"), "local media");
                File.WriteAllText(Path.Combine(folder, "tvshow.nfo"), "<tvshow />");
                Assert(!OwnedContentFiles.DeleteStrm(foreign, root, config), "direct deletion must recheck ownership at commit");
                Assert(OwnedContentFiles.DeleteOwnedTree(folder, root, config) == 1, "only the owned stream should be removed");
                Assert(!File.Exists(owned) && !File.Exists(Path.ChangeExtension(owned, ".nfo")), "associated owned sidecar should be removed with its stream");
                Assert(File.Exists(foreign) && File.Exists(Path.Combine(folder, "custom.nfo")) && File.Exists(Path.Combine(folder, "Local.mkv")) && File.Exists(Path.Combine(folder, "tvshow.nfo")), "foreign stream, local media and unrelated metadata must survive");
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        }

        private static Task OwnedDeletionRejectsEscapesAsync()
        {
            var root = CreateCompleteVodTestRoot();
            var outside = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(root);
                var file = Path.Combine(outside, "outside.strm");
                File.WriteAllText(file, "https://provider.invalid/movie/u/p/1.mkv");
                Assert(!OwnedContentFiles.DeleteStrm(file, root, config) && File.Exists(file), "a valid URL outside the root must never be deleted");
                Assert(!OwnedContentFiles.DeleteStrm(Path.Combine(root, "..", Path.GetFileName(outside), "outside.strm"), root, config), "traversal must not escape the configured root");
                var link = Path.Combine(root, "linked");
                Directory.CreateSymbolicLink(link, outside);
                Assert(!OwnedContentFiles.DeleteStrm(Path.Combine(link, "outside.strm"), root, config), "a symlink below the root is not ownership authority");
                OwnedContentFiles.DeleteOwnedTree(root, root, config);
                Assert(File.Exists(file), "recursive manual deletion cannot follow symlinks");
                Directory.Delete(link);
            }
            finally { Directory.Delete(root, true); Directory.Delete(outside, true); }
            return Task.CompletedTask;
        }

        private static async Task AutomaticCleanupKeepsForeignFilesAsync()
        {
            var root = CreateCompleteVodTestRoot();
            try
            {
                var config = CreateProviderIdConfig(root);
                var folder = Path.Combine(root, "Movies", "Foreign");
                Directory.CreateDirectory(folder);
                var foreign = Path.Combine(folder, "Foreign.strm");
                File.WriteAllText(foreign, "https://other.invalid/movie/u/p/1.mkv");
                var metadata = Path.Combine(root, "Movies", "MetadataOnly");
                Directory.CreateDirectory(metadata);
                File.WriteAllText(Path.Combine(metadata, "custom.nfo"), "unrelated metadata");
                var service = new StrmSyncService(CreateSilentLogger(), new HttpClient(new QueueHandler(JsonResponse("[{\"stream_id\":2,\"name\":\"Owned\",\"category_id\":83}]"))));
                await service.SyncMoviesAsync(config, CancellationToken.None);
                Assert(service.GetSyncHistory().Single().Success && File.Exists(foreign) && File.Exists(Path.Combine(metadata, "custom.nfo")), "automatic cleanup cannot treat foreign STRMs or unproven standalone NFOs as orphaned plugin output");
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
