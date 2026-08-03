using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client.Models;
using Emby.Xtream.Plugin.Service;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Emby.Xtream.Plugin.Api
{
    // Emby calls these internally via its Live TV subsystem — must remain unauthenticated
    [Route("/XC2EMBY/Epg", "GET", Summary = "Gets XMLTV EPG data for Live TV channels")]
    public class GetEpgXml : IReturnVoid
    {
    }

    [Route("/XC2EMBY/LiveTv", "GET", Summary = "Gets M3U playlist for Live TV channels")]
    public class GetM3UPlaylist : IReturnVoid
    {
    }

    // All remaining endpoints require admin — non-admin users must not be able to trigger syncs or read config
    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Categories/Live", "GET", Summary = "Gets Live TV categories from Xtream API")]
    public class GetLiveCategories : IReturn<List<Category>>
    {
    }

    // A selected live category ID that no longer appears in the provider's category
    // list, with a same-name match in the fresh list suggested as the likely replacement.
    public class OrphanedLiveCategory
    {
        public int OldId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int? SuggestedId { get; set; }
        public string SuggestedName { get; set; } = string.Empty;
    }

    // Matches the plain {CategoryId, CategoryName} shape written to CachedLiveCategories.
    // Category itself can't be reused here — its JsonPropertyName attributes target the
    // Xtream API's snake_case fields (category_id/category_name), not this cache's shape.
    public class CachedCategoryEntry
    {
        public int CategoryId { get; set; }
        public string CategoryName { get; set; } = string.Empty;
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/RefreshCache", "POST", Summary = "Invalidates M3U and EPG caches")]
    public class RefreshCache : IReturnVoid
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/RefreshLogos", "POST", Summary = "Removes Live TV channel logos and refreshes guide data")]
    public class RefreshLogos : IReturn<RefreshLogosResult>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/ClearCodecCache", "POST", Summary = "Clears per-channel codec cache so all channels are re-probed on next tune")]
    public class ClearCodecCache : IReturnVoid
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Categories/Vod", "GET", Summary = "Gets VOD movie categories from Xtream API")]
    public class GetVodCategories : IReturn<List<Category>>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Categories/Series", "GET", Summary = "Gets Series categories from Xtream API")]
    public class GetSeriesCategories : IReturn<List<Category>>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Sync/Movies", "POST", Summary = "Triggers VOD movie STRM sync")]
    public class SyncMovies : IReturn<SyncResult>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Sync/Documentaries", "POST", Summary = "Triggers documentary movie STRM sync")]
    public class SyncDocumentaries : IReturn<SyncResult>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Sync/Series", "POST", Summary = "Triggers series STRM sync")]
    public class SyncSeries : IReturn<SyncResult>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Sync/DocuSeries", "POST", Summary = "Triggers documentary series STRM sync")]
    public class SyncDocuSeries : IReturn<SyncResult>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Sync/Stop", "POST", Summary = "Stops the active STRM sync")]
    public class StopSync : IReturn<SyncResult>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Sync/Status", "GET", Summary = "Gets current sync progress")]
    public class GetSyncStatus : IReturn<SyncStatusResult>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Dashboard", "GET", Summary = "Gets dashboard data including sync history and library stats")]
    public class GetDashboard : IReturn<DashboardResult>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/ProviderHealth", "GET", Summary = "Returns the last known provider health state")]
    public class GetProviderHealth : IReturn<ProviderHealthResult>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/TestNotification", "GET", Summary = "Sends a test service disruption notification to all active sessions")]
    public class TestNotification : IReturnVoid
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Content/Movies", "DELETE", Summary = "Deletes all movie STRM content")]
    public class DeleteMovieContent : IReturn<DeleteContentResult>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Content/Documentaries", "DELETE", Summary = "Deletes all documentary movie STRM content")]
    public class DeleteDocumentaryContent : IReturn<DeleteContentResult>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Content/Series", "DELETE", Summary = "Deletes all series STRM content")]
    public class DeleteSeriesContent : IReturn<DeleteContentResult>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Content/DocuSeries", "DELETE", Summary = "Deletes all documentary series STRM content")]
    public class DeleteDocuSeriesContent : IReturn<DeleteContentResult>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Content/Items", "GET", Summary = "Lists top-level STRM item folders for a content type")]
    public class ListContentItems : IReturn<ListContentItemsResult>
    {
        public string Type { get; set; }
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Content/Items", "DELETE", Summary = "Deletes selected STRM item folders by name")]
    public class DeleteContentItems : IReturn<DeleteContentResult>
    {
        public string Type { get; set; }
        public List<string> Names { get; set; }
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/WritablePaths", "GET", Summary = "Returns writable mount points available to Emby")]
    public class GetWritablePaths : IReturn<List<string>>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/BrowsePath", "GET", Summary = "Lists subdirectories at the given path, or writable mounts if no path given")]
    public class BrowsePath : IReturn<BrowsePathResult>
    {
        public string Path { get; set; }
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/ValidateStrmPath", "POST", Summary = "Validates that the STRM library path is writable")]
    public class ValidateStrmPath : IReturn<TestConnectionResult>
    {
        public string Path { get; set; }
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/TestConnection", "POST", Summary = "Tests connection to Xtream server")]
    public class TestXtreamConnection : IReturn<TestConnectionResult>
    {
        // Optional: pass current form values so the test works before the user saves.
        // If omitted, the server falls back to the saved plugin configuration.
        public string BaseUrl  { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/CheckUpdate", "GET", Summary = "Checks GitHub for a newer plugin release")]
    public class CheckForUpdate : IReturn<UpdateCheckResult>
    {
        public bool? Beta { get; set; }
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Sync/FailedItems", "GET", Summary = "Returns items that failed during the last sync")]
    public class GetFailedItems : IReturn<List<FailedSyncItem>>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Sync/RetryFailed", "POST", Summary = "Retries all items that failed during the last sync")]
    public class RetryFailed : IReturn<SyncResult>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Sync/ClearFailedItems", "POST", Summary = "Clears failed sync items")]
    public class ClearFailedItems : IReturn<SyncResult>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Sync/ClearHistory", "POST", Summary = "Clears sync history")]
    public class ClearSyncHistory : IReturn<SyncResult>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Sync/PendingOrphans", "GET", Summary = "Returns staged orphan paths waiting for review")]
    public class GetPendingOrphans : IReturn<PendingOrphansResult>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Sync/PendingOrphans", "POST", Summary = "Deletes all staged orphan files")]
    public class CommitPendingOrphans : IReturn<SyncResult>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Sync/PendingOrphans", "DELETE", Summary = "Dismisses staged orphans without deleting")]
    public class DismissPendingOrphans : IReturn<SyncResult>
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/Logs", "GET", Summary = "Downloads sanitized plugin logs")]
    public class GetSanitizedLogs : IReturnVoid
    {
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/GuideDiagnostics", "GET", Summary = "Returns Live TV channel to XMLTV guide mapping diagnostics")]
    public class GetGuideDiagnostics : IReturn<GuideDiagnosticsResult>
    {
        public bool ProblemsOnly { get; set; }
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/InstallUpdate", "POST", Summary = "Downloads and installs the latest plugin update")]
    public class InstallUpdate : IReturn<InstallUpdateResult>
    {
        public bool? Beta { get; set; }
    }

    [Authenticated(Roles = "Admin")]
    [Route("/XC2EMBY/RestartEmby", "POST", Summary = "Restarts the Emby server")]
    public class RestartEmby : IReturnVoid
    {
    }

    public class TestConnectionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int MaxConnections { get; set; }
    }

    public class ProviderHealthResult
    {
        public bool IsReachable { get; set; }
        public DateTime? LastChecked { get; set; }
        public string LastError { get; set; }
        public int ConsecutiveFailures { get; set; }
    }

    public class RefreshLogosResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int ChannelsScanned { get; set; }
        public int LogosDeleted { get; set; }
        public int Failed { get; set; }
    }

    public class BrowsePathResult
    {
        public string CurrentPath { get; set; }
        public string ParentPath { get; set; }
        public List<string> Directories { get; set; }
    }

    public class InstallUpdateResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class DeleteContentResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int DeletedFolders { get; set; }
    }

    public class PendingOrphansResult
    {
        public bool Success { get; set; }
        public int Count { get; set; }
        public List<string> Paths { get; set; }
    }

    public class ListContentItemsResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public List<string> Names { get; set; }
    }

    public class SyncResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int Total { get; set; }
        public int Completed { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
    }

    public class SyncStatusResult
    {
        public SyncProgressInfo Movies { get; set; }
        public SyncProgressInfo Documentaries { get; set; }
        public SyncProgressInfo Series { get; set; }
        public SyncProgressInfo DocuSeries { get; set; }
    }

    public class SyncProgressInfo
    {
        public string Phase { get; set; }
        public int Total { get; set; }
        public int Completed { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public bool IsRunning { get; set; }
    }

    public class DashboardResult
    {
        public string PluginVersion { get; set; }
        public SyncHistoryEntry LastSync { get; set; }
        public List<SyncHistoryEntry> History { get; set; }
        public bool IsRunning { get; set; }
        public LibraryStats LibraryStats { get; set; }
        public bool AutoSyncOn { get; set; }
        public DateTime? NextSyncTime { get; set; }
        public bool SyncMovies { get; set; }
        public bool SyncDocumentaries { get; set; }
        public bool SyncSeries { get; set; }
        public bool SyncDocuSeries { get; set; }
    }

    public class LibraryStats
    {
        public int MovieFolders   { get; set; }
        public int MovieCount     { get; set; }
        public int DocumentaryFolders { get; set; }
        public int DocumentaryCount { get; set; }
        public int SeriesFolders  { get; set; }
        public int SeriesCount    { get; set; }
        public int SeasonCount    { get; set; }
        public int EpisodeCount   { get; set; }
        public int DocuSeriesFolders { get; set; }
        public int DocuSeriesCount { get; set; }
        public int DocuSeasonCount { get; set; }
        public int DocuEpisodeCount { get; set; }
        public int LiveTvChannels { get; set; }
    }

    public class GuideDiagnosticsResult
    {
        public int ChannelCount { get; set; }
        public int MappedCount { get; set; }
        public int MissingEpgChannelIdCount { get; set; }
        public int MissingXmltvChannelCount { get; set; }
        public int NoProgrammesCount { get; set; }
        public List<GuideDiagnosticItem> Items { get; set; }
    }

    public class GuideDiagnosticItem
    {
        public int Number { get; set; }
        public string Name { get; set; }
        public int StreamId { get; set; }
        public string EpgChannelId { get; set; }
        public string ResolvedXmltvId { get; set; }
        public bool XmltvChannelExists { get; set; }
        public int ProgrammeCount { get; set; }
        public string Status { get; set; }
    }

    public class XtreamTunerApi : BaseApiService
    {
        public async Task<object> Get(GetEpgXml request)
        {
            var liveTvService = Plugin.Instance.LiveTvService;
            var xml = await liveTvService.GetXmltvEpgAsync(CancellationToken.None).ConfigureAwait(false);

            var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            return ResultFactory.GetResult(Request, stream, "application/xml", new Dictionary<string, string>());
        }

        public async Task<object> Get(GetM3UPlaylist request)
        {
            var liveTvService = Plugin.Instance.LiveTvService;
            var m3u = await liveTvService.GetM3UPlaylistAsync(CancellationToken.None).ConfigureAwait(false);

            var stream = new MemoryStream(Encoding.UTF8.GetBytes(m3u));
            return ResultFactory.GetResult(Request, stream, "audio/x-mpegurl", new Dictionary<string, string>());
        }

        public async Task<object> Get(GetLiveCategories request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrEmpty(config.BaseUrl) ||
                string.IsNullOrEmpty(config.Username) || string.IsNullOrEmpty(config.Password))
            {
                return new List<Category>();
            }

            var liveTvService = Plugin.Instance.LiveTvService;
            var categories = await liveTvService.GetLiveCategoriesAsync(CancellationToken.None).ConfigureAwait(false);

            DetectOrphanedLiveCategories(config, categories);

            // Cache for instant UI loading
            config.CachedLiveCategories = System.Text.Json.JsonSerializer.Serialize(
                    categories.Select(c => new { c.CategoryId, c.CategoryName }).ToList());
            Plugin.Instance.SaveConfiguration();

            return categories;
        }

        // Providers occasionally renumber a category's ID while keeping its name
        // (e.g. Peacock 24 -> 102). The old ID then silently vanishes from future
        // category lists, leaving its checkbox selected but orphaned. Compare the
        // previous cached list against the fresh one so the UI can flag it and,
        // when a category with the same name reappears under a new ID, suggest it.
        private static void DetectOrphanedLiveCategories(PluginConfiguration config, List<Category> freshCategories)
        {
            var selectedIds = config.SelectedLiveCategoryIds ?? Array.Empty<int>();
            if (selectedIds.Length == 0 || string.IsNullOrEmpty(config.CachedLiveCategories))
            {
                config.OrphanedLiveCategories = string.Empty;
                return;
            }

            List<CachedCategoryEntry> previousCategories;
            try
            {
                previousCategories = System.Text.Json.JsonSerializer.Deserialize<List<CachedCategoryEntry>>(config.CachedLiveCategories)
                    ?? new List<CachedCategoryEntry>();
            }
            catch
            {
                config.OrphanedLiveCategories = string.Empty;
                return;
            }

            var freshIds = new HashSet<int>(freshCategories.Select(c => c.CategoryId));
            var freshNameToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in freshCategories)
            {
                if (!freshNameToId.ContainsKey(c.CategoryName))
                    freshNameToId[c.CategoryName] = c.CategoryId;
            }

            var previousNameById = previousCategories
                .GroupBy(c => c.CategoryId)
                .ToDictionary(g => g.Key, g => g.First().CategoryName);

            var orphaned = new List<OrphanedLiveCategory>();
            foreach (var id in selectedIds)
            {
                if (freshIds.Contains(id)) continue;
                if (!previousNameById.TryGetValue(id, out var name)) continue;

                var orphan = new OrphanedLiveCategory { OldId = id, Name = name };
                if (freshNameToId.TryGetValue(name, out var suggestedId))
                {
                    orphan.SuggestedId = suggestedId;
                    orphan.SuggestedName = name;
                }
                orphaned.Add(orphan);
            }

            config.OrphanedLiveCategories = orphaned.Count > 0
                ? System.Text.Json.JsonSerializer.Serialize(orphaned)
                : string.Empty;
        }

        public async Task<object> Get(GetVodCategories request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrEmpty(config.BaseUrl) ||
                string.IsNullOrEmpty(config.Username) || string.IsNullOrEmpty(config.Password))
            {
                return new List<Category>();
            }

            var url = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action=get_vod_categories",
                config.BaseUrl, Uri.EscapeDataString(config.Username), Uri.EscapeDataString(config.Password));

            var httpClient = Plugin.CreateHttpClient();
            var json = await httpClient.GetStringAsync(url).ConfigureAwait(false);
            var jsonOptions = new System.Text.Json.JsonSerializerOptions
            {
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
                PropertyNameCaseInsensitive = true,
            };
            var categories = XtreamResponseParser.DeserializeCategories(json, jsonOptions);
            var sorted = categories.OrderBy(c => c.CategoryName).ToList();

            // Fallback: derive categories from VOD stream list when server returns empty
            if (sorted.Count == 0)
            {
                var streamsUrl = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0}/player_api.php?username={1}&password={2}&action=get_vod_streams",
                    config.BaseUrl, Uri.EscapeDataString(config.Username), Uri.EscapeDataString(config.Password));

                var streamsJson = await httpClient.GetStringAsync(streamsUrl).ConfigureAwait(false);
                var vodStreams = System.Text.Json.JsonSerializer.Deserialize<List<VodStreamInfo>>(streamsJson, jsonOptions)
                    ?? new List<VodStreamInfo>();

                sorted = vodStreams
                    .Where(s => s.CategoryId.HasValue)
                    .GroupBy(s => s.CategoryId.Value)
                    .Select(g => new Category
                    {
                        CategoryId = g.Key,
                        CategoryName = "Category " + g.Key,
                    })
                    .OrderBy(c => c.CategoryName)
                    .ToList();
            }

            // Cache for instant UI loading
            config.CachedVodCategories = System.Text.Json.JsonSerializer.Serialize(
                sorted.Select(c => new { c.CategoryId, c.CategoryName }).ToList());
            Plugin.Instance.SaveConfiguration();

            return sorted;
        }

        public async Task<object> Get(GetSeriesCategories request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrEmpty(config.BaseUrl) ||
                string.IsNullOrEmpty(config.Username) || string.IsNullOrEmpty(config.Password))
            {
                return new List<Category>();
            }

            var jsonOptions = new System.Text.Json.JsonSerializerOptions
            {
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
                PropertyNameCaseInsensitive = true,
            };

            var httpClient2 = Plugin.CreateHttpClient();
            var url2 = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action=get_series_categories",
                config.BaseUrl, Uri.EscapeDataString(config.Username), Uri.EscapeDataString(config.Password));

            var json2 = await httpClient2.GetStringAsync(url2).ConfigureAwait(false);
            var categories2 = XtreamResponseParser.DeserializeCategories(json2, jsonOptions);
            var sorted2 = categories2.OrderBy(c => c.CategoryName).ToList();

            // Fallback: derive categories from series list when server returns empty
            if (sorted2.Count == 0)
            {
                var seriesUrl = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0}/player_api.php?username={1}&password={2}&action=get_series",
                    config.BaseUrl, Uri.EscapeDataString(config.Username), Uri.EscapeDataString(config.Password));

                var seriesJson = await httpClient2.GetStringAsync(seriesUrl).ConfigureAwait(false);
                var seriesList = XtreamResponseParser.DeserializeSeriesList(seriesJson, jsonOptions);

                sorted2 = seriesList
                    .Where(s => s.CategoryId.HasValue)
                    .GroupBy(s => s.CategoryId.Value)
                    .Select(g => new Category
                    {
                        CategoryId = g.Key,
                        CategoryName = g.FirstOrDefault(s => !string.IsNullOrEmpty(s.CategoryName))?.CategoryName
                            ?? "Category " + g.Key,
                    })
                    .OrderBy(c => c.CategoryName)
                    .ToList();
            }

            // Cache for instant UI loading
            config.CachedSeriesCategories = System.Text.Json.JsonSerializer.Serialize(
                sorted2.Select(c => new { c.CategoryId, c.CategoryName }).ToList());
            Plugin.Instance.SaveConfiguration();

            return sorted2;
        }

        public async Task<object> Post(SyncMovies request)
        {
            var config = Plugin.Instance.Configuration;
            var syncService = Plugin.Instance.StrmSyncService;
            var result = new SyncResult();

            if (!config.SyncMovies)
            {
                result.Success = false;
                result.Message = "Movie sync is not enabled. Enable it in Settings first.";
                return result;
            }

            if (syncService.MovieProgress.IsRunning)
            {
                result.Success = false;
                result.Message = "Movie sync is already running.";
                return result;
            }

            try
            {
                await syncService.SyncMoviesAsync(
                    config,
                    CancellationToken.None,
                    () => Plugin.Instance.SaveConfiguration()).ConfigureAwait(false);
                var progress = syncService.MovieProgress;
                if (!string.IsNullOrEmpty(progress.AbortReason))
                {
                    result.Success = false;
                    result.Message = progress.AbortReason;
                    result.Total = progress.Total;
                    result.Completed = progress.Completed;
                    result.Skipped = progress.Skipped;
                    result.Failed = progress.Failed;
                }
                else
                {
                    result.Success = true;
                    result.Message = "Movie sync completed.";
                    result.Total = progress.Total;
                    result.Completed = progress.Completed;
                    result.Skipped = progress.Skipped;
                    result.Failed = progress.Failed;
                }
            }
            catch (OperationCanceledException)
            {
                var progress = syncService.MovieProgress;
                result.Success = false;
                result.Message = "Movie sync stopped.";
                result.Total = progress.Total;
                result.Completed = progress.Completed;
                result.Skipped = progress.Skipped;
                result.Failed = progress.Failed;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "Movie sync failed: " + ex.Message;
            }

            return result;
        }

        public async Task<object> Post(SyncDocumentaries request)
        {
            var config = Plugin.Instance.Configuration;
            var syncService = Plugin.Instance.StrmSyncService;
            var result = new SyncResult();

            if (!config.SyncDocumentaries)
            {
                result.Success = false;
                result.Message = "Documentary sync is not enabled. Enable it in Documentary first.";
                return result;
            }

            if (syncService.DocumentariesProgress.IsRunning || syncService.MovieProgress.IsRunning)
            {
                result.Success = false;
                result.Message = "A movie/documentary sync is already running.";
                return result;
            }

            try
            {
                var docConfig = BuildDocumentaryMovieConfig(config);
                await syncService.SyncMoviesAsync(
                    docConfig,
                    CancellationToken.None,
                    () =>
                    {
                        config.LastDocumentarySyncTimestamp = docConfig.LastMovieSyncTimestamp;
                        config.StrmNamingVersion = docConfig.StrmNamingVersion;
                        config.MovieTmdbCacheJson = docConfig.MovieTmdbCacheJson;
                        Plugin.Instance.SaveConfiguration();
                    },
                    isDocumentaries: true).ConfigureAwait(false);

                config.LastDocumentarySyncTimestamp = docConfig.LastMovieSyncTimestamp;
                config.StrmNamingVersion = docConfig.StrmNamingVersion;
                config.MovieTmdbCacheJson = docConfig.MovieTmdbCacheJson;
                Plugin.Instance.SaveConfiguration();

                var progress = syncService.DocumentariesProgress;
                if (!string.IsNullOrEmpty(progress.AbortReason))
                {
                    result.Success = false;
                    result.Message = progress.AbortReason;
                }
                else
                {
                    result.Success = true;
                    result.Message = "Documentary sync completed.";
                }
                result.Total = progress.Total;
                result.Completed = progress.Completed;
                result.Skipped = progress.Skipped;
                result.Failed = progress.Failed;
            }
            catch (OperationCanceledException)
            {
                var progress = syncService.DocumentariesProgress;
                result.Success = false;
                result.Message = "Documentary sync stopped.";
                result.Total = progress.Total;
                result.Completed = progress.Completed;
                result.Skipped = progress.Skipped;
                result.Failed = progress.Failed;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "Documentary sync failed: " + ex.Message;
            }

            return result;
        }

        public async Task<object> Post(SyncSeries request)
        {
            var config = Plugin.Instance.Configuration;
            var syncService = Plugin.Instance.StrmSyncService;
            var result = new SyncResult();

            if (!config.SyncSeries)
            {
                result.Success = false;
                result.Message = "Series sync is not enabled. Enable it in Settings first.";
                return result;
            }

            if (syncService.SeriesProgress.IsRunning)
            {
                result.Success = false;
                result.Message = "Series sync is already running.";
                return result;
            }

            try
            {
                await syncService.SyncSeriesAsync(
                    config,
                    CancellationToken.None,
                    () => Plugin.Instance.SaveConfiguration()).ConfigureAwait(false);
                var progress = syncService.SeriesProgress;
                if (!string.IsNullOrEmpty(progress.AbortReason))
                {
                    result.Success = false;
                    result.Message = progress.AbortReason;
                    result.Total = progress.Total;
                    result.Completed = progress.Completed;
                    result.Skipped = progress.Skipped;
                    result.Failed = progress.Failed;
                }
                else
                {
                    result.Success = true;
                    result.Message = "Series sync completed.";
                    result.Total = progress.Total;
                    result.Completed = progress.Completed;
                    result.Skipped = progress.Skipped;
                    result.Failed = progress.Failed;
                }
            }
            catch (OperationCanceledException)
            {
                var progress = syncService.SeriesProgress;
                result.Success = false;
                result.Message = "Series sync stopped.";
                result.Total = progress.Total;
                result.Completed = progress.Completed;
                result.Skipped = progress.Skipped;
                result.Failed = progress.Failed;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "Series sync failed: " + ex.Message;
            }

            return result;
        }

        public async Task<object> Post(SyncDocuSeries request)
        {
            var config = Plugin.Instance.Configuration;
            var syncService = Plugin.Instance.StrmSyncService;
            var result = new SyncResult();

            if (!config.SyncDocuSeries)
            {
                result.Success = false;
                result.Message = "Docu Series sync is not enabled. Enable it in Docu Series first.";
                return result;
            }

            if (syncService.DocuSeriesProgress.IsRunning || syncService.SeriesProgress.IsRunning)
            {
                result.Success = false;
                result.Message = "A TV show/docu series sync is already running.";
                return result;
            }

            try
            {
                var docuConfig = BuildDocuSeriesConfig(config);
                await syncService.SyncSeriesAsync(
                    docuConfig,
                    CancellationToken.None,
                    () =>
                    {
                        config.LastDocuSeriesSyncTimestamp = docuConfig.LastSeriesSyncTimestamp;
                        config.DocuSeriesEpisodeHashesJson = docuConfig.SeriesEpisodeHashesJson;
                        config.StrmNamingVersion = docuConfig.StrmNamingVersion;
                        Plugin.Instance.SaveConfiguration();
                    },
                    isDocuSeries: true).ConfigureAwait(false);

                config.LastDocuSeriesSyncTimestamp = docuConfig.LastSeriesSyncTimestamp;
                config.DocuSeriesEpisodeHashesJson = docuConfig.SeriesEpisodeHashesJson;
                config.StrmNamingVersion = docuConfig.StrmNamingVersion;
                Plugin.Instance.SaveConfiguration();

                var progress = syncService.DocuSeriesProgress;
                if (!string.IsNullOrEmpty(progress.AbortReason))
                {
                    result.Success = false;
                    result.Message = progress.AbortReason;
                }
                else
                {
                    result.Success = true;
                    result.Message = "Docu Series sync completed.";
                }
                result.Total = progress.Total;
                result.Completed = progress.Completed;
                result.Skipped = progress.Skipped;
                result.Failed = progress.Failed;
            }
            catch (OperationCanceledException)
            {
                var progress = syncService.DocuSeriesProgress;
                result.Success = false;
                result.Message = "Docu Series sync stopped.";
                result.Total = progress.Total;
                result.Completed = progress.Completed;
                result.Skipped = progress.Skipped;
                result.Failed = progress.Failed;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "Docu Series sync failed: " + ex.Message;
            }

            return result;
        }

        public object Post(StopSync request)
        {
            var syncService = Plugin.Instance.StrmSyncService;
            var result = new SyncResult();

            if (syncService.StopActiveSync())
            {
                result.Success = true;
                result.Message = "Stop requested. The active sync will stop after the current file operation.";
            }
            else
            {
                result.Success = false;
                result.Message = "No STRM sync is currently running.";
            }

            return result;
        }

        public object Get(GetSyncStatus request)
        {
            var syncService = Plugin.Instance.StrmSyncService;
            var movieProg = syncService.MovieProgress;
            var docProg = syncService.DocumentariesProgress;
            var seriesProg = syncService.SeriesProgress;
            var docuProg = syncService.DocuSeriesProgress;

            return new SyncStatusResult
            {
                Movies = new SyncProgressInfo
                {
                    Phase = movieProg.Phase,
                    Total = movieProg.Total,
                    Completed = movieProg.Completed,
                    Skipped = movieProg.Skipped,
                    Failed = movieProg.Failed,
                    IsRunning = movieProg.IsRunning,
                },
                Documentaries = new SyncProgressInfo
                {
                    Phase = docProg.Phase,
                    Total = docProg.Total,
                    Completed = docProg.Completed,
                    Skipped = docProg.Skipped,
                    Failed = docProg.Failed,
                    IsRunning = docProg.IsRunning,
                },
                Series = new SyncProgressInfo
                {
                    Phase = seriesProg.Phase,
                    Total = seriesProg.Total,
                    Completed = seriesProg.Completed,
                    Skipped = seriesProg.Skipped,
                    Failed = seriesProg.Failed,
                    IsRunning = seriesProg.IsRunning,
                },
                DocuSeries = new SyncProgressInfo
                {
                    Phase = docuProg.Phase,
                    Total = docuProg.Total,
                    Completed = docuProg.Completed,
                    Skipped = docuProg.Skipped,
                    Failed = docuProg.Failed,
                    IsRunning = docuProg.IsRunning,
                },
            };
        }

        public object Get(GetFailedItems request)
        {
            return Plugin.Instance.StrmSyncService.FailedItems.ToList();
        }

        public async Task<object> Post(RetryFailed request)
        {
            var syncService = Plugin.Instance.StrmSyncService;
            if (syncService.MovieProgress.IsRunning || syncService.DocumentariesProgress.IsRunning ||
                syncService.SeriesProgress.IsRunning || syncService.DocuSeriesProgress.IsRunning)
                return new SyncResult { Success = false, Message = "A sync is already running." };
            if (syncService.FailedItems.Count == 0)
                return new SyncResult { Success = false, Message = "No failed items to retry." };

            await syncService.RetryFailedAsync(CancellationToken.None).ConfigureAwait(false);
            var p = syncService.RetryProgress;
            return new SyncResult
            {
                Success = true,
                Message = "Retry complete.",
                Total = p.Total,
                Completed = p.Completed,
                Failed = p.Failed
            };
        }

        public object Post(ClearFailedItems request)
        {
            Plugin.Instance.StrmSyncService.ClearFailedItems();
            return new SyncResult
            {
                Success = true,
                Message = "Failed items cleared."
            };
        }

        public object Post(ClearSyncHistory request)
        {
            Plugin.Instance.StrmSyncService.ClearSyncHistory();
            return new SyncResult
            {
                Success = true,
                Message = "Sync history cleared."
            };
        }

        public object Get(GetPendingOrphans request)
        {
            var paths = Plugin.Instance.StrmSyncService.GetPendingOrphans();
            return new PendingOrphansResult
            {
                Success = true,
                Count = paths.Count,
                Paths = paths
            };
        }

        public object Post(CommitPendingOrphans request)
        {
            var removed = Plugin.Instance.StrmSyncService.CommitPendingOrphans();
            return new SyncResult
            {
                Success = true,
                Message = removed + " orphaned file(s) deleted."
            };
        }

        public object Delete(DismissPendingOrphans request)
        {
            Plugin.Instance.StrmSyncService.ClearPendingOrphans();
            return new SyncResult
            {
                Success = true,
                Message = "Pending orphans dismissed."
            };
        }

        public async Task<object> Get(GetDashboard request)
        {
            var syncService = Plugin.Instance.StrmSyncService;
            var config = Plugin.Instance.Configuration;
            var history = syncService.GetSyncHistory();

            var movieFolders = 0;
            var movieCount = 0;
            var documentaryFolders = 0;
            var documentaryCount = 0;
            var seriesFolders = 0;
            var seriesCount = 0;
            var seasonCount = 0;
            var episodeCount = 0;
            var docuSeriesFolders = 0;
            var docuSeriesCount = 0;
            var docuSeasonCount = 0;
            var docuEpisodeCount = 0;
            var liveTvChannels = 0;

            try
            {
                var moviesRoot = Path.Combine(config.StrmLibraryPath, StrmSyncService.GetMovieRootFolderName(config));
                if (Directory.Exists(moviesRoot))
                {
                    movieFolders = Directory.GetDirectories(moviesRoot, "*", SearchOption.TopDirectoryOnly).Length;
                    movieCount = Directory.GetFiles(moviesRoot, "*.strm", SearchOption.AllDirectories).Length;
                }
            }
            catch { }

            try
            {
                var docsRoot = Path.Combine(config.StrmLibraryPath, StrmSyncService.GetDocumentaryRootFolderName(config));
                if (Directory.Exists(docsRoot))
                {
                    documentaryFolders = Directory.GetDirectories(docsRoot, "*", SearchOption.TopDirectoryOnly).Length;
                    documentaryCount = Directory.GetFiles(docsRoot, "*.strm", SearchOption.AllDirectories).Length;
                }
            }
            catch { }

            try
            {
                var showsRoot = Path.Combine(config.StrmLibraryPath, StrmSyncService.GetSeriesRootFolderName(config));
                var legacySeriesRoot = Path.Combine(config.StrmLibraryPath, "Series");
                var seriesRoot = Directory.Exists(showsRoot) ? showsRoot : legacySeriesRoot;
                if (Directory.Exists(seriesRoot))
                {
                    // In single mode: <SeriesRoot>/ShowName/Season XX/
                    // In multiple/custom mode: <SeriesRoot>/Category/ShowName/Season XX/
                    var isFlat = string.Equals(config.SeriesFolderMode, "single", StringComparison.OrdinalIgnoreCase);
                    var topDirs = Directory.GetDirectories(seriesRoot, "*", SearchOption.TopDirectoryOnly);
                    seriesFolders = topDirs.Length;
                    var seriesDirList = isFlat
                        ? topDirs
                        : topDirs.SelectMany(cat => { try { return Directory.GetDirectories(cat, "*", SearchOption.TopDirectoryOnly); } catch { return new string[0]; } }).ToArray();
                    seriesCount = seriesDirList.Length;
                    foreach (var seriesDir in seriesDirList)
                    {
                        try
                        {
                            seasonCount += Directory.GetDirectories(seriesDir, "*", SearchOption.TopDirectoryOnly).Length;
                        }
                        catch { }
                    }
                    episodeCount = Directory.GetFiles(seriesRoot, "*.strm", SearchOption.AllDirectories).Length;
                }
            }
            catch { }

            try
            {
                var docuRoot = Path.Combine(config.StrmLibraryPath, StrmSyncService.GetDocuSeriesRootFolderName(config));
                if (Directory.Exists(docuRoot))
                {
                    var isFlat = string.Equals(config.DocuSeriesFolderMode, "single", StringComparison.OrdinalIgnoreCase);
                    var topDirs = Directory.GetDirectories(docuRoot, "*", SearchOption.TopDirectoryOnly);
                    docuSeriesFolders = topDirs.Length;
                    var seriesDirList = isFlat
                        ? topDirs
                        : topDirs.SelectMany(cat => { try { return Directory.GetDirectories(cat, "*", SearchOption.TopDirectoryOnly); } catch { return new string[0]; } }).ToArray();
                    docuSeriesCount = seriesDirList.Length;
                    foreach (var seriesDir in seriesDirList)
                    {
                        try
                        {
                            docuSeasonCount += Directory.GetDirectories(seriesDir, "*", SearchOption.TopDirectoryOnly).Length;
                        }
                        catch { }
                    }
                    docuEpisodeCount = Directory.GetFiles(docuRoot, "*.strm", SearchOption.AllDirectories).Length;
                }
            }
            catch { }

            try
            {
                if (config.EnableLiveTv &&
                    !string.IsNullOrEmpty(config.BaseUrl) &&
                    !string.IsNullOrEmpty(config.Username) &&
                    !string.IsNullOrEmpty(config.Password))
                {
                    liveTvChannels = (await Plugin.Instance.LiveTvService
                        .GetFilteredChannelsAsync(CancellationToken.None)
                        .ConfigureAwait(false)).Count;
                }
            }
            catch
            {
                liveTvChannels = Emby.Xtream.Plugin.Service.XtreamTunerHost.Instance?.CachedChannelCount ?? 0;
            }

            // Compute next sync time
            DateTime? nextSyncTime = null;
            if (config.AutoSyncEnabled)
            {
                if (string.Equals(config.AutoSyncMode, "daily", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = (config.AutoSyncDailyTime ?? "03:00").Split(':');
                    int hour = 0, minute = 0;
                    if (parts.Length >= 1) int.TryParse(parts[0], out hour);
                    if (parts.Length >= 2) int.TryParse(parts[1], out minute);
                    var nextLocal = DateTime.Today.AddHours(hour).AddMinutes(minute);
                    if (nextLocal <= DateTime.Now) nextLocal = nextLocal.AddDays(1);
                    nextSyncTime = nextLocal.ToUniversalTime();
                }
                else
                {
                    var intervalHours = Math.Max(1, config.AutoSyncIntervalHours);
                    var lastEnd = history.Count > 0 ? history[0].EndTime : DateTime.UtcNow;
                    nextSyncTime = lastEnd.AddHours(intervalHours);
                }
            }

            return new DashboardResult
            {
                PluginVersion = GetPluginDisplayVersion(),
                LastSync = history.Count > 0 ? history[0] : null,
                History = history,
                IsRunning = syncService.MovieProgress.IsRunning || syncService.DocumentariesProgress.IsRunning ||
                            syncService.SeriesProgress.IsRunning || syncService.DocuSeriesProgress.IsRunning,
                AutoSyncOn = config.AutoSyncEnabled,
                NextSyncTime = nextSyncTime,
                SyncMovies = config.SyncMovies,
                SyncDocumentaries = config.SyncDocumentaries,
                SyncSeries = config.SyncSeries,
                SyncDocuSeries = config.SyncDocuSeries,
                LibraryStats = new LibraryStats
                {
                    MovieFolders   = movieFolders,
                    MovieCount     = movieCount,
                    DocumentaryFolders = documentaryFolders,
                    DocumentaryCount = documentaryCount,
                    SeriesFolders  = seriesFolders,
                    SeriesCount    = seriesCount,
                    SeasonCount    = seasonCount,
                    EpisodeCount   = episodeCount,
                    DocuSeriesFolders = docuSeriesFolders,
                    DocuSeriesCount = docuSeriesCount,
                    DocuSeasonCount = docuSeasonCount,
                    DocuEpisodeCount = docuEpisodeCount,
                    LiveTvChannels = liveTvChannels,
                },
            };
        }

        public object Get(GetProviderHealth request)
        {
            var health = Plugin.Instance.ProviderHealth;
            return new ProviderHealthResult
            {
                IsReachable        = health.IsReachable,
                LastChecked        = health.LastChecked,
                LastError          = health.LastError,
                ConsecutiveFailures = health.ConsecutiveFailures,
            };
        }

        public async Task<object> Get(TestNotification request)
        {
            await Plugin.Instance.BroadcastProviderStatusAsync(false).ConfigureAwait(false);
            return null;
        }

        public async Task<object> Get(GetGuideDiagnostics request)
        {
            var result = new GuideDiagnosticsResult
            {
                Items = new List<GuideDiagnosticItem>()
            };

            var provider = XtreamListingsProvider.Instance;
            var xmltvIds = provider != null
                ? await provider.GetXmltvChannelIdsAsync(CancellationToken.None).ConfigureAwait(false)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var programmeCounts = provider != null
                ? await provider.GetXmltvProgramCountsAsync(CancellationToken.None).ConfigureAwait(false)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var channels = await Plugin.Instance.LiveTvService
                .GetFilteredChannelsAsync(CancellationToken.None)
                .ConfigureAwait(false);

            var config = Plugin.Instance.Configuration;

            foreach (var channel in channels.OrderBy(c => c.Num).ThenBy(c => c.Name))
            {
                var rawEpgId = string.IsNullOrWhiteSpace(channel.EpgChannelId)
                    ? null
                    : channel.EpgChannelId.Trim();
                var resolvedId = !string.IsNullOrEmpty(rawEpgId)
                    ? XtreamListingsProvider.ResolveToXmltvId(rawEpgId, xmltvIds)
                    : null;
                var xmltvExists = !string.IsNullOrEmpty(resolvedId) &&
                                  (xmltvIds == null || xmltvIds.Contains(resolvedId));
                var programmeCount = 0;
                if (xmltvExists && programmeCounts != null)
                    programmeCounts.TryGetValue(resolvedId, out programmeCount);

                string status;
                if (string.IsNullOrEmpty(rawEpgId))
                {
                    status = "Missing epg_channel_id from Xtream";
                    result.MissingEpgChannelIdCount++;
                }
                else if (!xmltvExists)
                {
                    status = "epg_channel_id not found in XMLTV";
                    result.MissingXmltvChannelCount++;
                }
                else if (programmeCount <= 0)
                {
                    status = "XMLTV channel has no programmes";
                    result.NoProgrammesCount++;
                }
                else
                {
                    status = "Mapped";
                    result.MappedCount++;
                }

                var include = !request.ProblemsOnly || !string.Equals(status, "Mapped", StringComparison.OrdinalIgnoreCase);
                if (!include)
                    continue;

                result.Items.Add(new GuideDiagnosticItem
                {
                    Number = channel.Num,
                    Name = ChannelNameCleaner.CleanChannelName(
                        channel.Name, config.ChannelRemoveTerms, config.EnableChannelNameCleaning),
                    StreamId = channel.StreamId,
                    EpgChannelId = rawEpgId,
                    ResolvedXmltvId = resolvedId,
                    XmltvChannelExists = xmltvExists,
                    ProgrammeCount = programmeCount,
                    Status = status
                });
            }

            result.ChannelCount = channels.Count;
            return result;
        }

        public object Delete(DeleteMovieContent request)
        {
            return DeleteContentFolder(StrmSyncService.GetMovieRootFolderName(Plugin.Instance.Configuration));
        }

        private static string GetPluginDisplayVersion()
        {
            return typeof(Plugin).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(Plugin).Assembly.GetName().Version?.ToString()
                ?? "0.0.0";
        }

        public object Delete(DeleteDocumentaryContent request)
        {
            return DeleteContentFolder(StrmSyncService.GetDocumentaryRootFolderName(Plugin.Instance.Configuration));
        }

        public object Delete(DeleteSeriesContent request)
        {
            return DeleteContentFolder(StrmSyncService.GetSeriesRootFolderName(Plugin.Instance.Configuration));
        }

        public object Delete(DeleteDocuSeriesContent request)
        {
            return DeleteContentFolder(StrmSyncService.GetDocuSeriesRootFolderName(Plugin.Instance.Configuration));
        }

        public object Get(ListContentItems request)
        {
            var result = new ListContentItemsResult { Names = new List<string>() };
            try
            {
                var root = GetContentRoot(request.Type);
                if (root == null)
                {
                    result.Success = false;
                    result.Message = "Unknown content type: " + request.Type;
                    return result;
                }
                if (!Directory.Exists(root))
                {
                    result.Success = true;
                    result.Message = "No items found.";
                    return result;
                }
                var dirs = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly);
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                foreach (var d in dirs)
                    result.Names.Add(Path.GetFileName(d));
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = ex.Message;
            }
            return result;
        }

        public object Delete(DeleteContentItems request)
        {
            var result = new DeleteContentResult();
            try
            {
                var root = GetContentRoot(request.Type);
                if (root == null)
                {
                    result.Success = false;
                    result.Message = "Unknown content type: " + request.Type;
                    return result;
                }
                if (request.Names == null || request.Names.Count == 0)
                {
                    result.Success = true;
                    result.Message = "No items selected.";
                    return result;
                }

                int deleted = 0;
                foreach (var name in request.Names)
                {
                    // Reject names that contain path separators or traversal sequences
                    if (string.IsNullOrWhiteSpace(name) ||
                        name.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                        name.IndexOf(Path.AltDirectorySeparatorChar) >= 0 ||
                        name.Contains(".."))
                        continue;

                    var target = Path.Combine(root, name);
                    // Confirm the resolved path is a direct child of root
                    var parent = Path.GetDirectoryName(target);
                    if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (Directory.Exists(target))
                    {
                        Directory.Delete(target, true);
                        deleted++;
                    }
                }

                result.Success = true;
                result.DeletedFolders = deleted;
                result.Message = string.Format("Deleted {0} item(s).", deleted);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = ex.Message;
            }
            return result;
        }

        private string GetContentRoot(string type)
        {
            var config = Plugin.Instance.Configuration;
            string folderName;
            switch (type)
            {
                case "Movies":       folderName = StrmSyncService.GetMovieRootFolderName(config); break;
                case "Documentaries": folderName = StrmSyncService.GetDocumentaryRootFolderName(config); break;
                case "Series":       folderName = StrmSyncService.GetSeriesRootFolderName(config); break;
                case "DocuSeries":   folderName = StrmSyncService.GetDocuSeriesRootFolderName(config); break;
                default: return null;
            }
            return Path.Combine(config.StrmLibraryPath, folderName);
        }

        private static PluginConfiguration BuildDocumentaryMovieConfig(PluginConfiguration source)
        {
            var config = BuildSharedSyncConfig(source);
            config.SyncMovies = source.SyncDocumentaries;
            config.MovieRootFolderName = source.DocumentaryRootFolderName;
            config.SelectedVodCategoryIds = source.SelectedDocumentaryCategoryIds ?? new int[0];
            config.MovieFolderMode = source.DocumentaryFolderMode;
            config.MovieFolderMappings = source.DocumentaryFolderMappings;
            config.LastMovieSyncTimestamp = source.LastDocumentarySyncTimestamp;
            return config;
        }

        private static PluginConfiguration BuildDocuSeriesConfig(PluginConfiguration source)
        {
            var config = BuildSharedSyncConfig(source);
            config.SyncSeries = source.SyncDocuSeries;
            config.SeriesRootFolderName = source.DocuSeriesRootFolderName;
            config.SelectedSeriesCategoryIds = source.SelectedDocuSeriesCategoryIds ?? new int[0];
            config.SeriesFolderMode = source.DocuSeriesFolderMode;
            config.SeriesFolderMappings = source.DocuSeriesFolderMappings;
            config.LastSeriesSyncTimestamp = source.LastDocuSeriesSyncTimestamp;
            config.SeriesEpisodeHashesJson = source.DocuSeriesEpisodeHashesJson;
            return config;
        }

        private static PluginConfiguration BuildSharedSyncConfig(PluginConfiguration source)
        {
            return new PluginConfiguration
            {
                BaseUrl = source.BaseUrl,
                Username = source.Username,
                Password = source.Password,
                HttpUserAgent = source.HttpUserAgent,
                StrmLibraryPath = source.StrmLibraryPath,
                EnableContentNameCleaning = source.EnableContentNameCleaning,
                ContentRemoveTerms = source.ContentRemoveTerms,
                EnableTmdbFolderNaming = source.EnableTmdbFolderNaming,
                EnableTmdbFallbackLookup = source.EnableTmdbFallbackLookup,
                MovieTmdbCacheJson = source.MovieTmdbCacheJson,
                EnableSeriesIdFolderNaming = source.EnableSeriesIdFolderNaming,
                EnableSeriesMetadataLookup = source.EnableSeriesMetadataLookup,
                TvdbFolderIdOverrides = source.TvdbFolderIdOverrides,
                EnableNfoFiles = source.EnableNfoFiles,
                CachedVodCategories = source.CachedVodCategories,
                CachedSeriesCategories = source.CachedSeriesCategories,
                SmartSkipExisting = source.SmartSkipExisting,
                EnableLocalMediaFilter = source.EnableLocalMediaFilter,
                SyncParallelism = source.SyncParallelism,
                CleanupOrphans = source.CleanupOrphans,
                OrphanSafetyThreshold = source.OrphanSafetyThreshold,
                StrmNamingVersion = source.StrmNamingVersion,
            };
        }

        private DeleteContentResult DeleteContentFolder(string folderName)
        {
            var config = Plugin.Instance.Configuration;
            var result = new DeleteContentResult();

            try
            {
                var root = Path.Combine(config.StrmLibraryPath, folderName);
                if (!Directory.Exists(root))
                {
                    result.Success = true;
                    result.Message = folderName + " folder does not exist. Nothing to delete.";
                    return result;
                }

                var dirs = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly);
                result.DeletedFolders = dirs.Length;

                Directory.Delete(root, true);
                Directory.CreateDirectory(root);

                result.Success = true;
                result.Message = string.Format("Deleted {0} folders from {1}.", dirs.Length, folderName);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "Failed to delete " + folderName + " content: " + ex.Message;
            }

            return result;
        }

        public void Post(RefreshCache request)
        {
            Plugin.Instance.LiveTvService.InvalidateCache();
            // Mark the channel cache stale rather than nulling it. This lets active
            // streams and the guide keep working while new data fetches in the background.
            // GetChannelsInternal returns the old list immediately and fires a background
            // refresh; Emby picks up the new data on its next rescan cycle.
            // ClearCaches() (null) is intentionally NOT called here — doing so while
            // streams are active causes Emby to defer the rescan until all streams end,
            // leaving the guide blank for the entire watch session.
            XtreamTunerHost.Instance?.InvalidateChannelCacheTime();

            // Saving the tuner host already starts Emby's guide refresh. Once the
            // background channel-cache refresh completes, XtreamTunerHost requests a
            // second rescan with the fresh channel list. Do not also call
            // TriggerGuideRefresh() or TriggerEmbyGuideRefresh() here: doing so starts
            // redundant, overlapping guide rebuilds for the same cache invalidation.
            XtreamServerEntryPoint.Instance?.TriggerChannelRescan();
        }

        public object Post(RefreshLogos request)
        {
            var result = new RefreshLogosResult();

            try
            {
                Plugin.Instance.LiveTvService.InvalidateCache();
                XtreamTunerHost.Instance?.ClearCaches();

                var cleanup = XtreamServerEntryPoint.Instance?.TriggerGuideRefresh();
                if (cleanup == null)
                {
                    result.Message = "Emby guide refresh service is not available.";
                    return result;
                }

                result.ChannelsScanned = cleanup.ChannelsScanned;
                result.LogosDeleted = cleanup.LogosDeleted;
                result.Failed = cleanup.Failed;
                result.Success = cleanup.Success && cleanup.GuideRefreshTriggered;
                result.Message = cleanup.Skipped && result.Success
                    ? "Guide refresh triggered. Logo cache cleanup skipped by plugin setting."
                    : result.Success
                    ? string.Format(CultureInfo.InvariantCulture,
                        "Removed {0} logo(s) from {1} Live TV channel(s), then refreshed the guide.",
                        result.LogosDeleted, result.ChannelsScanned)
                    : string.Format(CultureInfo.InvariantCulture,
                        "Removed {0} logo(s) from {1} Live TV channel(s); {2} channel(s) failed. Guide refresh triggered: {3}.",
                        result.LogosDeleted, result.ChannelsScanned, result.Failed, cleanup.GuideRefreshTriggered);
            }
            catch (Exception ex)
            {
                result.Message = "Failed to remove Live TV logos: " + ex.Message;
            }

            return result;
        }

        public void Post(ClearCodecCache request)
        {
            StreamProbeService.ClearCache();
        }

        public object Get(GetWritablePaths request)
        {
            return EnumerateWritableMountPaths();
        }

        public object Get(BrowsePath request)
        {
            var path = string.IsNullOrWhiteSpace(request.Path) ? null : request.Path.TrimEnd('/').TrimEnd('\\');

            if (path == null)
            {
                return new BrowsePathResult
                {
                    CurrentPath = null,
                    ParentPath = null,
                    Directories = EnumerateWritableMountPaths()
                };
            }

            var dirs = new List<string>();
            try
            {
                foreach (var dir in Directory.GetDirectories(path).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    if (System.IO.Path.GetFileName(dir).StartsWith(".")) continue;
                    dirs.Add(dir);
                }
            }
            catch { }

            var parentInfo = Directory.GetParent(path);
            var parentPath = (parentInfo == null || parentInfo.FullName == "/") ? null : parentInfo.FullName;

            return new BrowsePathResult
            {
                CurrentPath = path,
                ParentPath = parentPath,
                Directories = dirs
            };
        }

        private static List<string> EnumerateWritableMountPaths()
        {
            var paths = new List<string>();

            try
            {
                if (File.Exists("/proc/mounts"))
                {
                    var skipFsTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "proc", "sysfs", "tmpfs", "devpts", "cgroup", "cgroup2",
                        "mqueue", "overlay", "nsfs", "pstore", "securityfs", "debugfs"
                    };

                    var skipPrefixes = new[] { "/proc", "/sys", "/dev", "/etc", "/run" };
                    var seen = new HashSet<string>(StringComparer.Ordinal);

                    foreach (var line in File.ReadAllLines("/proc/mounts"))
                    {
                        var parts = line.Split(' ');
                        if (parts.Length < 4) continue;

                        var fsType = parts[2];
                        var mountPoint = parts[1].Replace("\\040", " ").Replace("\\011", "\t").Replace("\\134", "\\");
                        var options = parts[3].Split(',');

                        if (skipFsTypes.Contains(fsType)) continue;
                        if (!options.Contains("rw")) continue;
                        if (!Directory.Exists(mountPoint)) continue;
                        if (skipPrefixes.Any(p => mountPoint == p || mountPoint.StartsWith(p + "/"))) continue;
                        if (!seen.Add(mountPoint)) continue;

                        if (IsWritableDirectory(mountPoint))
                            paths.Add(mountPoint);
                    }
                }
            }
            catch { }

            paths.Sort();
            return paths;
        }

        private static bool IsWritableDirectory(string path)
        {
            try
            {
                var testFile = System.IO.Path.Combine(path, ".xtream_write_test");
                File.WriteAllText(testFile, string.Empty);
                File.Delete(testFile);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public object Post(ValidateStrmPath request)
        {
            var path = (request.Path ?? string.Empty).TrimEnd('/').TrimEnd('\\');

            if (string.IsNullOrWhiteSpace(path))
            {
                return new TestConnectionResult { Success = false, Message = "Path cannot be empty." };
            }

            try
            {
                Directory.CreateDirectory(path);

                if (!IsWritableDirectory(path))
                {
                    return new TestConnectionResult { Success = false, Message = string.Format("Access denied: Emby cannot write to '{0}'.", path) };
                }

                return new TestConnectionResult { Success = true, Message = "Path is valid and writable." };
            }
            catch (Exception ex)
            {
                return new TestConnectionResult { Success = false, Message = string.Format("Invalid path: {0}", ex.Message) };
            }
        }

        public async Task<object> Post(TestXtreamConnection request)
        {
            var config = Plugin.Instance.Configuration;
            var result = new TestConnectionResult();

            // Use values from the request body first (pre-save test), fall back to saved config.
            var baseUrl  = !string.IsNullOrEmpty(request?.BaseUrl)  ? request.BaseUrl.TrimEnd('/')  : config.BaseUrl;
            var username = !string.IsNullOrEmpty(request?.Username) ? request.Username : config.Username;
            var password = !string.IsNullOrEmpty(request?.Password) ? request.Password : config.Password;

            if (string.IsNullOrEmpty(baseUrl) ||
                string.IsNullOrEmpty(username) ||
                string.IsNullOrEmpty(password))
            {
                result.Success = false;
                result.Message = "Please configure server URL, username, and password first.";
                return result;
            }

            try
            {
                var url = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0}/player_api.php?username={1}&password={2}",
                    baseUrl, Uri.EscapeDataString(username), Uri.EscapeDataString(password));

                string response;
                using (var handler = new System.Net.Http.HttpClientHandler { AllowAutoRedirect = true })
                using (var testHttpClient = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) })
                {
                    response = await testHttpClient.GetStringAsync(url).ConfigureAwait(false);
                }

                try
                {
                    using (var doc = System.Text.Json.JsonDocument.Parse(response))
                    {
                        if (doc.RootElement.TryGetProperty("user_info", out var userInfo))
                        {
                            var auth = 0;
                            if (userInfo.TryGetProperty("auth", out var authEl))
                            {
                                if (authEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                                    auth = authEl.GetInt32();
                                else if (authEl.ValueKind == System.Text.Json.JsonValueKind.String
                                         && int.TryParse(authEl.GetString(), out var n))
                                    auth = n;
                            }

                            string status = null;
                            if (userInfo.TryGetProperty("status", out var statusEl))
                                status = statusEl.GetString();

                            if (auth == 1)
                            {
                                result.Success = true;

                                var details = new System.Text.StringBuilder("Connection successful!");

                                if (!string.IsNullOrEmpty(status))
                                    details.Append(" · ").Append(status);

                                if (userInfo.TryGetProperty("exp_date", out var expEl))
                                {
                                    string expStr = expEl.ValueKind == System.Text.Json.JsonValueKind.String
                                        ? expEl.GetString()
                                        : (expEl.ValueKind == System.Text.Json.JsonValueKind.Number ? expEl.GetRawText() : null);
                                    if (expStr != null && long.TryParse(expStr, out var expUnix) && expUnix > 0)
                                    {
                                        var expDate = DateTimeOffset.FromUnixTimeSeconds(expUnix);
                                        details.Append(" · Expires ").Append(expDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                                    }
                                }

                                if (userInfo.TryGetProperty("max_connections", out var maxEl))
                                {
                                    var maxStr = maxEl.ValueKind == System.Text.Json.JsonValueKind.String ? maxEl.GetString() : (maxEl.ValueKind == System.Text.Json.JsonValueKind.Number ? maxEl.GetRawText() : null);
                                    if (!string.IsNullOrEmpty(maxStr) && int.TryParse(maxStr, out var maxInt) && maxInt > 0)
                                    {
                                        result.MaxConnections = maxInt;
                                        details.Append(" · ").Append(maxInt).Append(" max connections");
                                    }
                                }

                                result.Message = details.ToString();
                            }
                            else
                            {
                                result.Success = false;
                                result.Message = string.Format(
                                    CultureInfo.InvariantCulture,
                                    "Authentication failed: account status is '{0}'.",
                                    status ?? "unknown");
                            }
                        }
                        else
                        {
                            result.Success = false;
                            result.Message = "Server responded but returned an unexpected format. Verify the server URL.";
                        }
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    result.Success = false;
                    result.Message = "Server did not return a valid Xtream API response. Verify the server URL.";
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "Connection failed: " + ex.Message;
            }

            return result;
        }

        public async Task<object> Get(CheckForUpdate request)
        {
            // Always invalidate before a user-initiated check so the dashboard
            // reflects releases published since the last page load.
            UpdateChecker.InvalidateCache();
            return await UpdateChecker.CheckForUpdateAsync(request.Beta).ConfigureAwait(false);
        }

        public async Task<object> Post(InstallUpdate request)
        {
            var result = new InstallUpdateResult();

            try
            {
                // Force a fresh lookup for the requested channel so install uses the same
                // release selection logic as the explicit "check for update" action.
                UpdateChecker.InvalidateCache();
                var checkResult = await UpdateChecker.CheckForUpdateAsync(request.Beta).ConfigureAwait(false);

                if (!checkResult.UpdateAvailable)
                {
                    result.Message = "No update available.";
                    return result;
                }

                if (string.IsNullOrEmpty(checkResult.DownloadUrl))
                {
                    result.Message = "No DLL download URL found in the release.";
                    return result;
                }

                // Determine current plugin DLL path
                var currentDll = typeof(Plugin).Assembly.Location;
                if (string.IsNullOrEmpty(currentDll) || !File.Exists(currentDll))
                {
                    // Fallback for Docker/single-file: use Emby's PluginsPath
                    var pluginsDir = Plugin.Instance.PluginPaths.PluginsPath;
                    if (!string.IsNullOrEmpty(pluginsDir))
                    {
                        currentDll = Path.Combine(pluginsDir, "XC2EMBY.Plugin.dll");
                    }
                }

                if (string.IsNullOrEmpty(currentDll) || !File.Exists(currentDll))
                {
                    result.Message = "Could not determine plugin DLL path.";
                    return result;
                }

                var tempPath = currentDll + ".temp";
                var bakPath = currentDll + ".bak";

                // Download the new DLL
                byte[] dllBytes;
                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Emby-Xtream-Plugin/1.0");
                    httpClient.Timeout = TimeSpan.FromSeconds(60);
                    dllBytes = await httpClient.GetByteArrayAsync(checkResult.DownloadUrl).ConfigureAwait(false);
                }

                if (dllBytes.Length < 1024)
                {
                    result.Message = "Downloaded file is too small (" + dllBytes.Length + " bytes). Aborting.";
                    return result;
                }

                // Atomic replacement with backup
                File.WriteAllBytes(tempPath, dllBytes);

                try
                {
                    // Back up current DLL
                    if (File.Exists(bakPath))
                        File.Delete(bakPath);
                    File.Move(currentDll, bakPath);

                    // Move new DLL into place
                    File.Move(tempPath, currentDll);

                    // Clean up backup on success
                    try { File.Delete(bakPath); } catch { }
                }
                catch
                {
                    // Restore backup on failure
                    try
                    {
                        if (File.Exists(bakPath) && !File.Exists(currentDll))
                            File.Move(bakPath, currentDll);
                    }
                    catch { }

                    try { File.Delete(tempPath); } catch { }
                    throw;
                }

                UpdateChecker.UpdateInstalled = true;
                UpdateChecker.InvalidateCache();

                // Persist installed version so banner stays hidden after restart
                try
                {
                    var config = Plugin.Instance.Configuration;
                    config.LastInstalledVersion = checkResult.LatestVersion;
                    Plugin.Instance.SaveConfiguration();
                }
                catch { }

                // Notify Emby that a restart is needed
                try
                {
                    var appHost = Plugin.Instance.ApplicationHost;
                    var notifyMethod = appHost.GetType().GetMethod("NotifyPendingRestart",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (notifyMethod != null)
                        notifyMethod.Invoke(appHost, null);
                }
                catch { }

                result.Success = true;
                result.Message = "Update installed successfully (" + dllBytes.Length + " bytes). Restart Emby to apply.";
            }
            catch (Exception ex)
            {
                result.Message = "Install failed: " + ex.Message;
            }

            return result;
        }

        public void Post(RestartEmby request)
        {
            try
            {
                var appHost = Plugin.Instance.ApplicationHost;
                var restartMethod = appHost.GetType().GetMethod("Restart",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (restartMethod != null)
                {
                    restartMethod.Invoke(appHost, null);
                }
            }
            catch { }
        }

        private static string StripNonAscii(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                if (c >= 32 && c <= 126) sb.Append(c);
            return sb.ToString();
        }

        public object Get(GetSanitizedLogs request)
        {
            var config = Plugin.Instance.Configuration;
            var logDir = Plugin.Instance.PluginPaths.LogDirectoryPath;
            var keywords = new[] { "XC2EMBY", "XtreamTuner", "LiveTv" };

            // Collect lines per file so we can label sections
            var sections = new List<(string FileName, List<string> Lines)>();

            try
            {
                var logFiles = Directory.GetFiles(logDir, "*.txt")
                    .Concat(Directory.GetFiles(logDir, "*.log"))
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .Take(5)
                    .ToArray();

                foreach (var logFile in logFiles)
                {
                    var fileLines = new List<string>();
                    try
                    {
                        using (var reader = new StreamReader(logFile, Encoding.UTF8))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                foreach (var kw in keywords)
                                {
                                    if (line.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        fileLines.Add(line);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                    if (fileLines.Count > 0)
                        sections.Add((Path.GetFileName(logFile), fileLines));
                }
            }
            catch { }

            // Sanitize all lines and strip non-ASCII (Emby injects Unicode delimiters around HTTP values)
            var allSanitized = new List<(string File, string Line)>();
            foreach (var section in sections)
                foreach (var raw in section.Lines)
                {
                    var cleaned = StripNonAscii(raw);
                    var sanitized = LogSanitizer.SanitizeLine(cleaned, config.Username, config.Password);
                    allSanitized.Add((section.FileName, sanitized));
                }

            // Classify by severity for the summary
            int errorCount = 0, warnCount = 0, infoCount = 0;
            foreach (var (_, line) in allSanitized)
            {
                if (line.IndexOf(" Error ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("|ERROR|", StringComparison.OrdinalIgnoreCase) >= 0)
                    errorCount++;
                else if (line.IndexOf(" Warn ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         line.IndexOf("|WARN|", StringComparison.OrdinalIgnoreCase) >= 0)
                    warnCount++;
                else
                    infoCount++;
            }

            var out_ = new StringBuilder();

            // ── Header ────────────────────────────────────────────────────────────
            out_.AppendLine("================================================================");
            out_.AppendLine("  XC2EMBY Plugin - Sanitized Log Export");
            out_.AppendLine("  Version  : " + Plugin.Instance.Version);
            out_.AppendLine("  Exported : " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC");
            out_.AppendLine("  Lines    : " + allSanitized.Count + "  (Errors: " + errorCount + "  Warnings: " + warnCount + "  Info: " + infoCount + ")");
            out_.AppendLine("  Files    : " + (sections.Count > 0 ? string.Join(", ", sections.ConvertAll(s => s.FileName)) : "none"));
            out_.AppendLine();
            out_.AppendLine("  Credentials, IPs, and provider hostnames have been redacted.");
            out_.AppendLine("  Safe to share for support purposes.");
            out_.AppendLine("================================================================");
            out_.AppendLine();

            if (allSanitized.Count == 0)
            {
                out_.AppendLine("No XC2EMBY-related log entries found in the last 5 log files.");
                out_.AppendLine("Try reproducing the issue then downloading again.");
            }
            else
            {
                // ── Errors & Warnings first ───────────────────────────────────────
                var problems = allSanitized.Where(x =>
                    x.Line.IndexOf(" Error ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    x.Line.IndexOf("|ERROR|", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    x.Line.IndexOf(" Warn ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    x.Line.IndexOf("|WARN|", StringComparison.OrdinalIgnoreCase) >= 0).ToList();

                if (problems.Count > 0)
                {
                    out_.AppendLine("=== ERRORS & WARNINGS (" + problems.Count + ") ===================================");
                    out_.AppendLine();
                    foreach (var (_, line) in problems)
                        out_.AppendLine(line);
                    out_.AppendLine();
                }

                // ── Full log by file ─────────────────────────────────────────────
                out_.AppendLine("=== FULL LOG (" + allSanitized.Count + " lines) ====================================");
                string currentFile = null;
                foreach (var (file, line) in allSanitized)
                {
                    if (file != currentFile)
                    {
                        out_.AppendLine();
                        out_.AppendLine("-- " + file + " --------------------------------------------------");
                        currentFile = file;
                    }
                    out_.AppendLine(line);
                }
            }

            // ── Footer ────────────────────────────────────────────────────────────
            out_.AppendLine();
            out_.AppendLine("================================================================");
            out_.AppendLine("  End of log export");
            out_.AppendLine("================================================================");

            var filename = "xc2emby-log-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".txt";
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(out_.ToString()));
            var headers = new Dictionary<string, string>
            {
                { "Content-Disposition", "attachment; filename=\"" + filename + "\"" },
            };
            return ResultFactory.GetResult(Request, stream, "text/plain; charset=utf-8", headers);
        }
    }
}
