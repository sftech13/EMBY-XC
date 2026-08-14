using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;
using MetadataRefreshOptions = MediaBrowser.Controller.Providers.MetadataRefreshOptions;
using STJ = System.Text.Json;

namespace Emby.Xtream.Plugin.Service
{
    public class SyncProgress
    {
        public volatile string Phase = string.Empty;
        public int Total;
        public int Completed;
        public int Skipped;
        public int Failed;
        public int Added;
        public int Changed;
        public int NfoChanged;
        public int Deleted;
        public volatile bool IsRunning;

        /// <summary>Set when sync exits early (e.g. invalid folder configuration).</summary>
        public volatile string AbortReason = string.Empty;
    }

    public class SyncHistoryEntry
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public bool Success { get; set; }
        public int MoviesTotal { get; set; }
        public int MoviesCompleted { get; set; }
        public int MoviesAdded { get; set; }
        public int MoviesSkipped { get; set; }
        public int MoviesFailed { get; set; }
        public int MoviesDeleted { get; set; }
        public int SeriesTotal { get; set; }
        public int SeriesCompleted { get; set; }
        public int SeriesAdded { get; set; }
        public int SeriesSkipped { get; set; }
        public int SeriesFailed { get; set; }
        public int SeriesDeleted { get; set; }
        public int EpisodeTotal { get; set; }
        public int EpisodeAdded { get; set; }
        public int EpisodeSkipped { get; set; }
        public int EpisodeFailed { get; set; }
        public int EpisodeDeleted { get; set; }
        public bool WasMovieSync { get; set; }
        public bool WasDocumentarySync { get; set; }
        public bool WasSeriesSync { get; set; }
        public bool WasDocuSeriesSync { get; set; }
        public List<string> AddedMovieTitles { get; set; } = new List<string>();
        public List<string> AddedSeriesTitles { get; set; } = new List<string>();
    }

    public class FailedSyncItem
    {
        public string ItemType { get; set; }   // "Movie" | "Series"
        public int StreamId { get; set; }
        public string Name { get; set; }
        public int? CategoryId { get; set; }
        public string TmdbId { get; set; }
        public string ContainerExtension { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime FailedAt { get; set; } = DateTime.UtcNow;
    }

    internal sealed class MovieSyncCandidate
    {
        public VodStreamInfo Movie { get; set; }
        public string CleanedName { get; set; }
        public string FolderName { get; set; }
        public string MovieDirectory { get; set; }
        public string StrmPath { get; set; }
        public string StreamUrl { get; set; }
        public string TmdbId { get; set; }
        public bool IsLocallyFiltered { get; set; }
    }

    public class StrmSyncService
    {
        private sealed class NonRetryableProviderHttpException : HttpRequestException
        {
            public NonRetryableProviderHttpException(
                string operation,
                int statusCode,
                string reasonPhrase)
                : base(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} failed: HTTP {1}{2}",
                    operation,
                    statusCode,
                    string.IsNullOrWhiteSpace(reasonPhrase)
                        ? string.Empty
                        : " (" + reasonPhrase + ")"))
            {
            }
        }

        internal enum StrmWriteResult
        {
            Unchanged,
            Added,
            Changed,
        }

        [Flags]
        internal enum StreamUrlChangeKind
        {
            None = 0,
            Endpoint = 1,
            Credentials = 2,
            StreamId = 4,
            Extension = 8,
            Other = 16,
        }

        internal sealed class StreamUrlChangeStats
        {
            public int Total;
            public int Endpoint;
            public int Credentials;
            public int StreamId;
            public int Extension;
            public int Other;

            public void Record(string currentUrl, string intendedUrl)
            {
                var kind = ClassifyStreamUrlChange(currentUrl, intendedUrl);
                Interlocked.Increment(ref Total);
                if ((kind & StreamUrlChangeKind.Endpoint) != 0) Interlocked.Increment(ref Endpoint);
                if ((kind & StreamUrlChangeKind.Credentials) != 0) Interlocked.Increment(ref Credentials);
                if ((kind & StreamUrlChangeKind.StreamId) != 0) Interlocked.Increment(ref StreamId);
                if ((kind & StreamUrlChangeKind.Extension) != 0) Interlocked.Increment(ref Extension);
                if ((kind & StreamUrlChangeKind.Other) != 0) Interlocked.Increment(ref Other);
            }
        }

        private sealed class StreamUrlParts
        {
            public Uri Uri { get; set; }
            public string Prefix { get; set; }
            public string Kind { get; set; }
            public string Username { get; set; }
            public string Password { get; set; }
            public string StreamId { get; set; }
            public string Extension { get; set; }
        }

        private sealed class SeriesPathOwnershipPlan
        {
            public Dictionary<int, SeriesDetailInfo> PrefetchedDetails { get; } =
                new Dictionary<int, SeriesDetailInfo>();

            public Dictionary<string, int> EpisodeOwners { get; } =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            public Dictionary<string, int> FolderOwners { get; } =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            public int DuplicateFolderCount { get; set; }
            public int CompetingPathCount { get; set; }
        }

        private sealed class SeriesFolderCandidate
        {
            public SeriesInfo Series { get; set; }
            public string SeriesName { get; set; }
            public string SeriesDirectory { get; set; }
        }

        private static readonly STJ.JsonSerializerOptions JsonOptions = new STJ.JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
        };

        private static readonly Regex InvalidFileCharsRegex = new Regex(
            @"[<>:""/\\|?*\x00-\x1F]",
            RegexOptions.Compiled);

        private static readonly Regex YearInTitleRegex = new Regex(
            @"\((\d{4})\)\s*$",
            RegexOptions.Compiled);

        private const int MaxHistoryEntries = 10;
        private static readonly HttpClient SharedHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly object _sharedClientHeaderLock = new object();
        private static readonly TimeSpan[] ProviderRetryDelays =
        {
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
        };

        // Increment when naming logic changes so existing installs force a full re-sync on next run.
        internal const int CurrentStrmNamingVersion = 1;

        private static void ApplyUserAgentToSharedClient()
        {
            var ua = Plugin.InstanceOrNull?.Configuration?.HttpUserAgent;
            lock (_sharedClientHeaderLock)
            {
                SharedHttpClient.DefaultRequestHeaders.Remove("User-Agent");
                if (!string.IsNullOrEmpty(ua))
                    SharedHttpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);
            }
        }

        private async Task<string> GetProviderStringWithRetryAsync(
            string url,
            string operation,
            CancellationToken cancellationToken)
        {
            Exception lastError = null;

            for (var attempt = 0; attempt <= ProviderRetryDelays.Length; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    using (var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        var statusCode = (int)response.StatusCode;
                        var retryableStatus =
                            statusCode == 408 ||
                            statusCode == 429 ||
                            statusCode >= 500;
                        if (retryableStatus && attempt < ProviderRetryDelays.Length)
                        {
                            _logger.Warn(
                                "{0} returned HTTP {1}; retry {2}/{3} in {4} seconds",
                                operation,
                                statusCode,
                                attempt + 1,
                                ProviderRetryDelays.Length,
                                (int)ProviderRetryDelays[attempt].TotalSeconds);
                            await Task.Delay(ProviderRetryDelays[attempt], cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        if (!response.IsSuccessStatusCode)
                        {
                            if (retryableStatus)
                            {
                                throw new HttpRequestException(string.Format(
                                    CultureInfo.InvariantCulture,
                                    "{0} failed after transient retries: HTTP {1}{2}",
                                    operation,
                                    statusCode,
                                    string.IsNullOrWhiteSpace(response.ReasonPhrase)
                                        ? string.Empty
                                        : " (" + response.ReasonPhrase + ")"));
                            }

                            // Ordinary 4xx responses are provider data decisions,
                            // not connection failures. Return them immediately so a
                            // stale 404 does not consume the 2/5/10-second backoff.
                            throw new NonRetryableProviderHttpException(
                                operation,
                                statusCode,
                                response.ReasonPhrase);
                        }

                        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (NonRetryableProviderHttpException ex)
                {
                    throw new HttpRequestException(ex.Message, ex);
                }
                catch (Exception ex) when (
                    attempt < ProviderRetryDelays.Length &&
                    (ex is HttpRequestException || ex is TaskCanceledException))
                {
                    lastError = ex;
                    _logger.Warn(
                        "{0} failed transiently: {1}; retry {2}/{3} in {4} seconds",
                        operation,
                        ex.Message,
                        attempt + 1,
                        ProviderRetryDelays.Length,
                        (int)ProviderRetryDelays[attempt].TotalSeconds);
                    await Task.Delay(ProviderRetryDelays[attempt], cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    break;
                }
            }

            throw new HttpRequestException(
                operation + " failed: " + (lastError?.Message ?? "provider request failed"),
                lastError);
        }

        private static int ProtectExistingSeriesFiles(
            string strmLibraryPath,
            string subFolder,
            string seriesName,
            HashSet<string> validPaths)
        {
            var parent = Path.Combine(strmLibraryPath, subFolder);
            if (!Directory.Exists(parent)) return 0;

            var protectedCount = 0;
            foreach (var seriesDirectory in Directory.GetDirectories(parent, seriesName + "*", SearchOption.TopDirectoryOnly))
            {
                var folderName = Path.GetFileName(seriesDirectory);
                if (!string.Equals(folderName, seriesName, StringComparison.OrdinalIgnoreCase) &&
                    !folderName.StartsWith(seriesName + " [", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var strmPath in Directory.GetFiles(seriesDirectory, "*.strm", SearchOption.AllDirectories))
                {
                    lock (validPaths)
                    {
                        if (validPaths.Add(strmPath))
                            protectedCount++;
                    }
                }
            }
            return protectedCount;
        }

        private readonly ILogger _logger;
        private readonly TmdbLookupService _tmdbLookupService;
        private readonly HttpClient _httpClient;
        private List<SyncHistoryEntry> _syncHistory;
        private readonly object _historyLock = new object();
        private readonly List<FailedSyncItem> _failedItems = new List<FailedSyncItem>();
        private readonly object _failedItemsLock = new object();
        private readonly SemaphoreSlim _seriesWriteGate = new SemaphoreSlim(1, 1);
        private readonly object _activeSyncLock = new object();
        private CancellationTokenSource _activeSyncCancellation;
        private readonly object _libraryScanLock = new object();
        private Timer _libraryScanTimer;
        private bool _libraryScanPending;
        private bool _targetedLibraryRefreshRunning;
        private readonly HashSet<string> _pendingLibraryRefreshPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _catalogObservationLock = new object();
        private readonly Dictionary<string, CatalogObservation> _catalogObservations =
            new Dictionary<string, CatalogObservation>(StringComparer.OrdinalIgnoreCase);

        // Automatic content tasks are normally staggered by 30 minutes. A quiet
        // period combines the complete sequence into one Emby library scan.
        private static readonly TimeSpan LibraryScanQuietPeriod = TimeSpan.FromMinutes(90);
        private static readonly TimeSpan ActiveSyncScanRetryDelay = TimeSpan.FromMinutes(15);
        private const double LargeOrphanRatio = 0.20;

        private sealed class CatalogObservation
        {
            public string Fingerprint;
            public int ConsecutiveCompleteRuns;
        }

        private SyncProgress _movieProgress = new SyncProgress();
        private SyncProgress _documentariesProgress = new SyncProgress();
        private SyncProgress _docuSeriesProgress = new SyncProgress();
        private SyncProgress _seriesProgress = new SyncProgress();
        private SyncProgress _episodeProgress = new SyncProgress();
        private SyncProgress _retryProgress = new SyncProgress();

        private static void ReportTaskProgress(SyncProgress syncProgress, IProgress<double> taskProgress)
        {
            if (taskProgress == null) return;
            var total = Volatile.Read(ref syncProgress.Total);
            if (total <= 0) return;
            var completed = Volatile.Read(ref syncProgress.Completed);
            var pct = Math.Min(100.0, (double)completed / total * 100.0);
            taskProgress.Report(pct);
        }

        private int ObserveCompleteCatalog(string contentRoot, IEnumerable<string> catalogKeys)
        {
            var ordered = catalogKeys
                .Where(k => !string.IsNullOrEmpty(k))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();
            string fingerprint;
            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(string.Join("\n", ordered));
                fingerprint = Convert.ToBase64String(sha.ComputeHash(bytes));
            }

            lock (_catalogObservationLock)
            {
                CatalogObservation observation;
                if (!_catalogObservations.TryGetValue(contentRoot, out observation) ||
                    !string.Equals(observation.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    observation = new CatalogObservation
                    {
                        Fingerprint = fingerprint,
                        ConsecutiveCompleteRuns = 1,
                    };
                    _catalogObservations[contentRoot] = observation;
                }
                else
                {
                    observation.ConsecutiveCompleteRuns++;
                }

                _logger.Info(
                    "Complete catalog observation for {0}: {1} consecutive identical run(s)",
                    contentRoot,
                    observation.ConsecutiveCompleteRuns);
                return observation.ConsecutiveCompleteRuns;
            }
        }

        internal static string NormalizeStreamUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var normalized = value.Trim().TrimStart('\uFEFF');
            Uri uri;
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out uri))
                return normalized;

            var builder = new UriBuilder(uri)
            {
                Scheme = uri.Scheme.ToLowerInvariant(),
                Host = uri.Host.ToLowerInvariant(),
            };
            if ((builder.Scheme == Uri.UriSchemeHttp && builder.Port == 80) ||
                (builder.Scheme == Uri.UriSchemeHttps && builder.Port == 443))
                builder.Port = -1;

            return builder.Uri.AbsoluteUri;
        }

        private static bool TryParseStreamUrl(string value, out StreamUrlParts parts)
        {
            parts = null;
            Uri uri;
            if (!Uri.TryCreate(value?.Trim().TrimStart('\uFEFF'), UriKind.Absolute, out uri))
                return false;

            var segments = uri.AbsolutePath
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString)
                .ToArray();
            var kindIndex = Array.FindLastIndex(
                segments,
                segment => string.Equals(segment, "series", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(segment, "movie", StringComparison.OrdinalIgnoreCase));
            if (kindIndex < 0 || segments.Length < kindIndex + 4)
                return false;

            var streamFile = segments[segments.Length - 1];
            var extensionIndex = streamFile.LastIndexOf('.');
            parts = new StreamUrlParts
            {
                Uri = uri,
                Prefix = string.Join("/", segments.Take(kindIndex)),
                Kind = segments[kindIndex],
                Username = segments[kindIndex + 1],
                Password = segments[kindIndex + 2],
                StreamId = extensionIndex > 0 ? streamFile.Substring(0, extensionIndex) : streamFile,
                Extension = extensionIndex > 0 ? streamFile.Substring(extensionIndex + 1) : string.Empty,
            };
            return true;
        }

        internal static StreamUrlChangeKind ClassifyStreamUrlChange(string currentUrl, string intendedUrl)
        {
            if (string.Equals(
                NormalizeStreamUrl(currentUrl),
                NormalizeStreamUrl(intendedUrl),
                StringComparison.Ordinal))
                return StreamUrlChangeKind.None;

            StreamUrlParts current;
            StreamUrlParts intended;
            if (!TryParseStreamUrl(currentUrl, out current) ||
                !TryParseStreamUrl(intendedUrl, out intended))
                return StreamUrlChangeKind.Other;

            var result = StreamUrlChangeKind.None;
            if (!string.Equals(current.Uri.Scheme, intended.Uri.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(current.Uri.Host, intended.Uri.Host, StringComparison.OrdinalIgnoreCase) ||
                current.Uri.Port != intended.Uri.Port)
                result |= StreamUrlChangeKind.Endpoint;
            if (!string.Equals(current.Username, intended.Username, StringComparison.Ordinal) ||
                !string.Equals(current.Password, intended.Password, StringComparison.Ordinal))
                result |= StreamUrlChangeKind.Credentials;
            if (!string.Equals(current.StreamId, intended.StreamId, StringComparison.Ordinal))
                result |= StreamUrlChangeKind.StreamId;
            if (!string.Equals(current.Extension, intended.Extension, StringComparison.OrdinalIgnoreCase))
                result |= StreamUrlChangeKind.Extension;
            if (!string.Equals(current.Prefix, intended.Prefix, StringComparison.Ordinal) ||
                !string.Equals(current.Kind, intended.Kind, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(current.Uri.Query, intended.Uri.Query, StringComparison.Ordinal) ||
                !string.Equals(current.Uri.Fragment, intended.Uri.Fragment, StringComparison.Ordinal))
                result |= StreamUrlChangeKind.Other;

            return result == StreamUrlChangeKind.None ? StreamUrlChangeKind.Other : result;
        }

        internal static StrmWriteResult WriteStrmIfChanged(
            string path,
            string intendedUrl,
            StreamUrlChangeStats changeStats = null)
        {
            if (File.Exists(path))
            {
                var currentUrl = File.ReadAllText(path);
                if (string.Equals(
                    NormalizeStreamUrl(currentUrl),
                    NormalizeStreamUrl(intendedUrl),
                    StringComparison.Ordinal))
                    return StrmWriteResult.Unchanged;

                changeStats?.Record(currentUrl, intendedUrl);
                File.WriteAllText(path, intendedUrl);
                return StrmWriteResult.Changed;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, intendedUrl);
            return StrmWriteResult.Added;
        }

        private void LogStreamUrlChangeSummary(string contentType, StreamUrlChangeStats stats)
        {
            if (stats == null || Volatile.Read(ref stats.Total) == 0) return;

            _logger.Info(
                "{0} STRM URL changes (privacy-safe; categories may overlap): total={1}, endpoint={2}, credentials={3}, stream-id={4}, extension={5}, other={6}",
                contentType,
                Volatile.Read(ref stats.Total),
                Volatile.Read(ref stats.Endpoint),
                Volatile.Read(ref stats.Credentials),
                Volatile.Read(ref stats.StreamId),
                Volatile.Read(ref stats.Extension),
                Volatile.Read(ref stats.Other));
        }

        private void CompleteSyncAndCoalesceLibraryScan(
            string contentType,
            string changedLibraryPath,
            params SyncProgress[] progressItems)
        {
            var changed = progressItems != null && progressItems.Any(p => p != null &&
                (Volatile.Read(ref p.Added) > 0 ||
                 Volatile.Read(ref p.Changed) > 0 ||
                 Volatile.Read(ref p.NfoChanged) > 0 ||
                 Volatile.Read(ref p.Deleted) > 0));

            lock (_libraryScanLock)
            {
                if (changed)
                {
                    _libraryScanPending = true;
                    if (!string.IsNullOrWhiteSpace(changedLibraryPath))
                        _pendingLibraryRefreshPaths.Add(Path.GetFullPath(changedLibraryPath));
                    _logger.Info(
                        "{0} sync changed files; a targeted Emby refresh is pending after a {1}-minute sync quiet period",
                        contentType,
                        (int)LibraryScanQuietPeriod.TotalMinutes);
                }

                // An unchanged task still advances the automatic sequence, so it
                // postpones a pending scan until that task has also completed.
                if (!_libraryScanPending) return;

                if (_libraryScanTimer == null)
                    _libraryScanTimer = new Timer(FlushCoalescedLibraryScan, null, Timeout.Infinite, Timeout.Infinite);

                _libraryScanTimer.Change(LibraryScanQuietPeriod, Timeout.InfiniteTimeSpan);
            }
        }

        private void FlushCoalescedLibraryScan(object state)
        {
            lock (_libraryScanLock)
            {
                if (!_libraryScanPending) return;

                if (_targetedLibraryRefreshRunning ||
                    _movieProgress.IsRunning ||
                    _documentariesProgress.IsRunning ||
                    _seriesProgress.IsRunning ||
                    _docuSeriesProgress.IsRunning ||
                    _retryProgress.IsRunning)
                {
                    _logger.Info(
                        "Targeted Emby refresh remains pending because an XC2EMBY sync or refresh is still running; checking again in {0} minutes",
                        (int)ActiveSyncScanRetryDelay.TotalMinutes);
                    _libraryScanTimer.Change(ActiveSyncScanRetryDelay, Timeout.InfiniteTimeSpan);
                    return;
                }

                try
                {
                    var host = Plugin.InstanceOrNull?.ApplicationHost;
                    var libraryManager = host?.Resolve<MediaBrowser.Controller.Library.ILibraryManager>();
                    if (libraryManager == null)
                    {
                        _logger.Warn("XC2EMBY changed files, but ILibraryManager was unavailable; the targeted refresh remains pending");
                        _libraryScanTimer.Change(ActiveSyncScanRetryDelay, Timeout.InfiniteTimeSpan);
                        return;
                    }

                    if (libraryManager.IsScanRunning)
                    {
                        _logger.Info(
                            "Targeted Emby refresh is waiting for the active library scan; checking again in {0} minutes",
                            (int)ActiveSyncScanRetryDelay.TotalMinutes);
                        _libraryScanTimer.Change(ActiveSyncScanRetryDelay, Timeout.InfiniteTimeSpan);
                        return;
                    }

                    var refreshPaths = _pendingLibraryRefreshPaths.ToArray();
                    if (refreshPaths.Length == 0)
                    {
                        _libraryScanPending = false;
                        return;
                    }

                    _pendingLibraryRefreshPaths.Clear();
                    _libraryScanPending = false;
                    _targetedLibraryRefreshRunning = true;
                    _logger.Info(
                        "XC2EMBY sync sequence is quiet; starting targeted Emby refresh for {0} changed library root(s): {1}",
                        refreshPaths.Length,
                        string.Join(", ", refreshPaths.Select(Path.GetFileName)));

                    _ = Task.Run(() => RunTargetedLibraryRefreshAsync(libraryManager, refreshPaths));
                }
                catch (Exception ex)
                {
                    _logger.Warn("The targeted Emby refresh could not be started and remains pending: {0}", ex.Message);
                    _libraryScanTimer.Change(ActiveSyncScanRetryDelay, Timeout.InfiniteTimeSpan);
                }
            }
        }

        private async Task RunTargetedLibraryRefreshAsync(
            MediaBrowser.Controller.Library.ILibraryManager libraryManager,
            string[] refreshPaths)
        {
            var failedPaths = new List<string>();
            try
            {
                foreach (var path in refreshPaths)
                {
                    try
                    {
                        var folder = libraryManager.FindByPath(path, true) as Folder;
                        if (folder == null)
                        {
                            failedPaths.Add(path);
                            _logger.Warn("Targeted Emby refresh could not find a configured library folder for {0}", path);
                            continue;
                        }

                        var options = new MetadataRefreshOptions(BaseItem.FileSystem)
                        {
                            EnableRemoteContentProbe = false,
                            EnableSubtitleDownloading = false,
                            EnableThumbnailImageExtraction = false,
                        };

                        await folder.ValidateChildren(
                            new Progress<double>(),
                            CancellationToken.None,
                            options,
                            true).ConfigureAwait(false);
                        _logger.Info("Targeted Emby refresh completed for {0}", path);
                    }
                    catch (Exception ex)
                    {
                        failedPaths.Add(path);
                        _logger.Warn("Targeted Emby refresh failed for {0}: {1}", path, ex.Message);
                    }
                }
            }
            finally
            {
                lock (_libraryScanLock)
                {
                    _targetedLibraryRefreshRunning = false;
                    foreach (var failedPath in failedPaths)
                        _pendingLibraryRefreshPaths.Add(failedPath);

                    if (failedPaths.Count > 0)
                    {
                        _libraryScanPending = true;
                        _libraryScanTimer.Change(ActiveSyncScanRetryDelay, Timeout.InfiniteTimeSpan);
                    }
                    else if (_libraryScanPending || _pendingLibraryRefreshPaths.Count > 0)
                    {
                        _libraryScanPending = true;
                        _libraryScanTimer.Change(LibraryScanQuietPeriod, Timeout.InfiniteTimeSpan);
                    }
                }
            }
        }

        public StrmSyncService(ILogger logger, HttpClient httpClient = null)
        {
            _logger = logger;
            _tmdbLookupService = new TmdbLookupService(logger);
            _httpClient = httpClient ?? SharedHttpClient;
        }

        /// <summary>
        /// Computes a stable hash of a series' episode URLs for change detection.
        /// The hash covers episode ID + extension for each episode, sorted by season+episode
        /// to be order-independent of the JSON layout.
        /// </summary>
        internal static string ComputeSeriesEpisodeHash(Dictionary<string, List<EpisodeInfo>> episodes)
        {
            var sb = new StringBuilder();
            foreach (var seasonEntry in episodes.OrderBy(e => e.Key))
            {
                foreach (var ep in seasonEntry.Value.OrderBy(e => e.Season).ThenBy(e => e.EpisodeNum))
                {
                    sb.Append(ep.Id);
                    sb.Append('.');
                    sb.Append(ep.ContainerExtension ?? "mp4");
                    sb.Append('|');
                }
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        /// <summary>
        /// Combines the provider episode identity with the connection settings that are
        /// embedded in each STRM. Only the SHA-256 result is persisted; credentials are
        /// never written to diagnostics or stored separately.
        /// </summary>
        internal static string ComputeSeriesSyncFingerprint(
            Dictionary<string, List<EpisodeInfo>> episodes,
            PluginConfiguration config)
        {
            var episodeHash = ComputeSeriesEpisodeHash(episodes);
            var baseUrl = NormalizeStreamUrl(config?.BaseUrl ?? string.Empty);
            var username = config?.Username ?? string.Empty;
            var password = config?.Password ?? string.Empty;
            var value = string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1}|{2}:{3}|{4}:{5}|{6}:{7}",
                episodeHash.Length, episodeHash,
                baseUrl.Length, baseUrl,
                username.Length, username,
                password.Length, password);

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        /// <summary>
        /// Computes a stable hash of the channel list for change detection.
        /// </summary>
        internal static string ComputeChannelListHash(List<LiveStreamInfo> channels)
        {
            var sorted = channels.OrderBy(c => c.StreamId);
            var sb = new StringBuilder();
            foreach (var c in sorted)
            {
                sb.Append(c.StreamId);
                sb.Append(':');
                sb.Append(c.Name ?? string.Empty);
                sb.Append(':');
                sb.Append(c.EpgChannelId ?? string.Empty);
                sb.Append(':');
                sb.Append(c.CategoryId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                sb.Append('|');
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        internal static Dictionary<string, string> DeserializeEpisodeHashes(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, string>();
            try
            {
                return STJ.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                       ?? new Dictionary<string, string>();
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }

        internal static string SerializeEpisodeHashes(ConcurrentDictionary<string, string> hashes)
        {
            if (hashes == null || hashes.IsEmpty)
                return string.Empty;
            return STJ.JsonSerializer.Serialize(hashes);
        }

        internal static ConcurrentDictionary<string, string> DeserializeMovieTmdbCache(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var values = STJ.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                             ?? new Dictionary<string, string>();
                return new ConcurrentDictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        internal static string SerializeMovieTmdbCache(ConcurrentDictionary<string, string> cache)
        {
            if (cache == null || cache.IsEmpty)
                return string.Empty;

            return STJ.JsonSerializer.Serialize(cache);
        }

        internal static string GetMovieRootFolderName(PluginConfiguration config)
        {
            var value = config?.MovieRootFolderName;
            if (string.IsNullOrWhiteSpace(value)) return "Movies";
            value = value.Trim();
            return Path.IsPathRooted(value) ? value : SanitizeFileName(value);
        }

        internal static string GetSeriesRootFolderName(PluginConfiguration config)
        {
            var value = config?.SeriesRootFolderName;
            if (string.IsNullOrWhiteSpace(value)) return "TV Shows";
            value = value.Trim();
            return Path.IsPathRooted(value) ? value : SanitizeFileName(value);
        }

        internal static string GetDocumentaryRootFolderName(PluginConfiguration config)
        {
            var value = config?.DocumentaryRootFolderName;
            if (string.IsNullOrWhiteSpace(value)) return "Documentaries";
            value = value.Trim();
            return Path.IsPathRooted(value) ? value : SanitizeFileName(value);
        }

        internal static string GetDocuSeriesRootFolderName(PluginConfiguration config)
        {
            var value = config?.DocuSeriesRootFolderName;
            if (string.IsNullOrWhiteSpace(value)) return "Docu Series";
            value = value.Trim();
            return Path.IsPathRooted(value) ? value : SanitizeFileName(value);
        }

        public SyncProgress MovieProgress        => _movieProgress;
        public SyncProgress DocumentariesProgress => _documentariesProgress;
        public SyncProgress DocuSeriesProgress   => _docuSeriesProgress;
        public SyncProgress SeriesProgress       => _seriesProgress;
        public SyncProgress RetryProgress        => _retryProgress;
        public bool IsAnySyncRunning =>
            _movieProgress.IsRunning ||
            _documentariesProgress.IsRunning ||
            _seriesProgress.IsRunning ||
            _docuSeriesProgress.IsRunning ||
            _retryProgress.IsRunning;

        public bool StopActiveSync()
        {
            lock (_activeSyncLock)
            {
                if (_activeSyncCancellation == null || _activeSyncCancellation.IsCancellationRequested)
                    return false;

                _activeSyncCancellation.Cancel();
                return true;
            }
        }

        private CancellationTokenSource BeginSyncCancellation(CancellationToken cancellationToken)
        {
            var syncCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_activeSyncLock)
            {
                _activeSyncCancellation = syncCancellation;
            }

            return syncCancellation;
        }

        private void EndSyncCancellation(CancellationTokenSource syncCancellation)
        {
            lock (_activeSyncLock)
            {
                if (ReferenceEquals(_activeSyncCancellation, syncCancellation))
                    _activeSyncCancellation = null;
            }

            syncCancellation.Dispose();
        }

        public IReadOnlyList<FailedSyncItem> FailedItems
        {
            get { lock (_failedItemsLock) { return _failedItems.ToList(); } }
        }

        public void ClearFailedItems()
        {
            lock (_failedItemsLock)
            {
                _failedItems.Clear();
            }
        }

        // Lazy-loaded from PluginConfiguration.SyncHistoryJson so history survives restarts.
        // Must be called inside _historyLock.
        private List<SyncHistoryEntry> GetOrLoadHistory()
        {
            if (_syncHistory != null) return _syncHistory;

            _syncHistory = new List<SyncHistoryEntry>();
            try
            {
                var json = Plugin.InstanceOrNull?.Configuration?.SyncHistoryJson;
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var loaded = STJ.JsonSerializer.Deserialize<List<SyncHistoryEntry>>(json, JsonOptions);
                    if (loaded != null) _syncHistory.AddRange(loaded);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Failed to load sync history from config: {0}", ex.Message);
            }

            return _syncHistory;
        }

        public List<SyncHistoryEntry> GetSyncHistory()
        {
            lock (_historyLock)
            {
                return new List<SyncHistoryEntry>(GetOrLoadHistory());
            }
        }

        public void ClearSyncHistory()
        {
            lock (_historyLock)
            {
                GetOrLoadHistory().Clear();
            }

            try
            {
                Plugin.Instance.Configuration.SyncHistoryJson = string.Empty;
                Plugin.Instance.SaveConfiguration();
            }
            catch (Exception ex)
            {
                _logger.Debug("Failed to clear sync history: {0}", ex.Message);
            }
        }

        /// <summary>
        /// Checks whether the stored STRM naming version is current. If not, resets sync timestamps
        /// so the next run performs a full re-sync and regenerates files with corrected names.
        /// Returns true when a version upgrade was applied (timestamps were reset), false otherwise.
        /// </summary>
        internal bool CheckAndUpgradeNamingVersion(PluginConfiguration config, Action saveConfig)
        {
            if (config.StrmNamingVersion >= CurrentStrmNamingVersion)
                return false;

            _logger.Info("STRM naming version upgraded ({0} → {1}); resetting sync timestamps for full re-sync",
                config.StrmNamingVersion, CurrentStrmNamingVersion);

            config.StrmNamingVersion = CurrentStrmNamingVersion;
            config.LastMovieSyncTimestamp = 0;
            config.LastDocumentarySyncTimestamp = 0;
            config.LastSeriesSyncTimestamp = 0;
            config.LastDocuSeriesSyncTimestamp = 0;
            config.SeriesEpisodeHashesJson = string.Empty;
            config.DocuSeriesEpisodeHashesJson = string.Empty;
            saveConfig?.Invoke();
            return true;
        }

        public async Task SyncMoviesAsync(PluginConfiguration config, CancellationToken cancellationToken, Action saveConfig = null, IProgress<double> taskProgress = null, bool isDocumentaries = false)
        {
            ApplyUserAgentToSharedClient();
            CheckAndUpgradeNamingVersion(config, saveConfig);
            var mp = new SyncProgress { IsRunning = true, Phase = "Starting movie sync" };
            if (isDocumentaries) _documentariesProgress = mp; else _movieProgress = mp;
            lock (_failedItemsLock) { _failedItems.Clear(); }
            var movieSyncStart = DateTime.UtcNow;
            var movieSyncSuccess = true;
            var addedMovieTitles = new List<string>();
            var syncCancellation = BeginSyncCancellation(cancellationToken);
            cancellationToken = syncCancellation.Token;

            try
            {
                EnsureStrmLibraryPath(config.StrmLibraryPath);

                var folderMappings = FolderMappingParser.Parse(config.MovieFolderMappings);
                if (string.Equals(config.MovieFolderMode, "custom", StringComparison.OrdinalIgnoreCase) &&
                    folderMappings.Count == 0)
                {
                    movieSyncSuccess = false;
                    mp.AbortReason =
                        "Multiple Folders mode is on but no categories are assigned to any folder. " +
                        "Click + Add Folder, name it, use Refresh Categories, tick the VOD categories for that folder, then save plugin settings. " +
                        "Or switch back to Single Folder to use the flat category list.";
                    mp.Phase = "Configuration needed";
                    _logger.Warn("Movie sync aborted: {0}", mp.AbortReason);
                    return;
                }

                var categoryNames = new Dictionary<int, string>();

                // Fetch category names if needed for folder organization
                if (!string.Equals(config.MovieFolderMode, "single", StringComparison.OrdinalIgnoreCase))
                {
                    mp.Phase = "Fetching VOD categories";
                    var categories = await FetchCategoriesAsync("get_vod_categories", config, cancellationToken).ConfigureAwait(false);
                    foreach (var cat in categories)
                    {
                        categoryNames[cat.CategoryId] = cat.CategoryName;
                    }
                }

                mp.Phase = "Fetching VOD streams";
                var allStreams = await FetchVodStreamsAsync(config.SelectedVodCategoryIds, config, cancellationToken).ConfigureAwait(false);

                // Delta sync: split into new (not yet synced) and existing
                var lastMovieTs = config.LastMovieSyncTimestamp;
                var newStreams = lastMovieTs > 0
                    ? allStreams.Where(m => m.Added > lastMovieTs).ToList()
                    : allStreams;
                var existingStreams = lastMovieTs > 0
                    ? allStreams.Where(m => m.Added <= lastMovieTs).ToList()
                    : new List<VodStreamInfo>();

                _logger.Info("Delta movie sync: {0} new, {1} existing (since timestamp {2})",
                    newStreams.Count, existingStreams.Count, lastMovieTs);

                mp.Total = allStreams.Count;
                mp.Phase = "Writing STRM files";

                // Log TMDB statistics
                if (config.EnableTmdbFolderNaming)
                {
                    var withTmdb = allStreams.Count(m => IsValidTmdbId(m.TmdbId));
                    var without = allStreams.Count - withTmdb;
                    var pct = allStreams.Count > 0 ? (int)(100.0 * withTmdb / allStreams.Count) : 0;
                    _logger.Info("TMDB IDs available: {0}/{1} movies ({2}%){3}",
                        withTmdb, allStreams.Count, pct,
                        config.EnableTmdbFallbackLookup
                            ? string.Format(CultureInfo.InvariantCulture, " — TMDB fallback lookup enabled for {0} movies without IDs", without)
                            : string.Empty);
                }

                _logger.Info("Starting movie STRM sync for {0} streams", allStreams.Count);

                LocalMediaFilter localFilter = null;
                if (config.EnableLocalMediaFilter)
                {
                    mp.Phase = "Scanning Emby library";
                    localFilter = LocalMediaFilter.Build(_logger, config.StrmLibraryPath);
                }

                var enrichMovieTmdbIds = config.EnableLocalMediaFilter || config.EnableTmdbFolderNaming;
                var movieTmdbCache = DeserializeMovieTmdbCache(config.MovieTmdbCacheJson);
                var movieTmdbCacheChanged = 0;
                var movieTmdbCacheUnsavedAdds = 0;
                var movieTmdbCacheSaveLock = new object();
                if (enrichMovieTmdbIds)
                {
                    var listIds = allStreams.Count(m => IsValidTmdbId(m.TmdbId));
                    var cachedIds = allStreams.Count(m => movieTmdbCache.ContainsKey(m.StreamId.ToString(CultureInfo.InvariantCulture)));
                    _logger.Info("Movie TMDB detail cache: {0}/{1} cached, {2}/{1} present in VOD list",
                        cachedIds, allStreams.Count, listIds);
                }

                var writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var locallyFilteredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var preparedMovies = new ConcurrentBag<MovieSyncCandidate>();
                var urlChangeStats = new StreamUrlChangeStats();
                var moviesRoot = Path.Combine(config.StrmLibraryPath, GetMovieRootFolderName(config));
                var semaphore = new SemaphoreSlim(config.SyncParallelism);

                // Resolve names, provider IDs, target paths, and local-media matches
                // before writing anything. Several Xtream records can resolve to the
                // same filesystem path. A two-phase pass lets a local-media match
                // suppress the entire path and prevents those records racing to
                // rewrite the same STRM with different stream IDs.
                var prepareTasks = allStreams.Select(async movie =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var cleanedName = config.EnableContentNameCleaning
                            ? ContentNameCleaner.CleanContentName(movie.Name, config.ContentRemoveTerms)
                            : movie.Name;
                        var movieName = SanitizeFileName(cleanedName);
                        if (string.IsNullOrWhiteSpace(movieName))
                        {
                            Interlocked.Increment(ref mp.Failed);
                            Interlocked.Increment(ref mp.Completed);
                            ReportTaskProgress(mp, taskProgress);
                            return;
                        }

                        var providerTmdbId = IsValidTmdbId(movie.TmdbId) ? movie.TmdbId.Trim() : null;
                        string providerImdbId = null;
                        if (string.IsNullOrEmpty(providerTmdbId) && enrichMovieTmdbIds)
                        {
                            var cacheKey = movie.StreamId.ToString(CultureInfo.InvariantCulture);
                            string cachedTmdbId;
                            if (movieTmdbCache.TryGetValue(cacheKey, out cachedTmdbId) && IsValidTmdbId(cachedTmdbId))
                            {
                                providerTmdbId = cachedTmdbId.Trim();
                            }
                            else
                            {
                                var vodDetail = await FetchVodDetailAsync(movie.StreamId, config, cancellationToken).ConfigureAwait(false);
                                providerTmdbId = vodDetail?[0];
                                providerImdbId = vodDetail?[1];
                                if (IsValidTmdbId(providerTmdbId))
                                {
                                    movieTmdbCache[cacheKey] = providerTmdbId.Trim();
                                    Interlocked.Exchange(ref movieTmdbCacheChanged, 1);
                                    if (Interlocked.Increment(ref movieTmdbCacheUnsavedAdds) >= 500)
                                    {
                                        lock (movieTmdbCacheSaveLock)
                                        {
                                            if (Volatile.Read(ref movieTmdbCacheUnsavedAdds) >= 500)
                                            {
                                                config.MovieTmdbCacheJson = SerializeMovieTmdbCache(movieTmdbCache);
                                                saveConfig?.Invoke();
                                                Interlocked.Exchange(ref movieTmdbCacheUnsavedAdds, 0);
                                                _logger.Info("Movie TMDB detail cache checkpoint: {0} entries", movieTmdbCache.Count);
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        // Determine TMDB ID for folder naming
                        string tmdbId = null;
                        if (config.EnableTmdbFolderNaming)
                        {
                            if (IsValidTmdbId(providerTmdbId))
                            {
                                tmdbId = providerTmdbId.Trim();
                            }
                            else if (config.EnableTmdbFallbackLookup)
                            {
                                var yearMatch2 = YearInTitleRegex.Match(cleanedName);
                                int? yearForLookup = null;
                                if (yearMatch2.Success)
                                {
                                    int y;
                                    if (int.TryParse(yearMatch2.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out y))
                                    {
                                        yearForLookup = y;
                                    }
                                }

                                try
                                {
                                    tmdbId = await _tmdbLookupService.LookupTmdbIdAsync(cleanedName, yearForLookup, cancellationToken).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    _logger.Debug("TMDB fallback error for '{0}': {1}", cleanedName, ex.Message);
                                }
                            }
                        }

                        var folderName = BuildMovieFolderName(cleanedName, tmdbId);
                        if (string.IsNullOrWhiteSpace(folderName))
                        {
                            Interlocked.Increment(ref mp.Failed);
                            Interlocked.Increment(ref mp.Completed);
                            ReportTaskProgress(mp, taskProgress);
                            return;
                        }

                        var subFolder = BuildContentFolderPath(
                            config.MovieFolderMode, movie.CategoryId, categoryNames, folderMappings, GetMovieRootFolderName(config));

                        if (subFolder == null)
                        {
                            Interlocked.Increment(ref mp.Skipped);
                            Interlocked.Increment(ref mp.Completed);
                            ReportTaskProgress(mp, taskProgress);
                            return;
                        }

                        var movieDir = Path.Combine(config.StrmLibraryPath, subFolder, folderName);
                        var strmPath = Path.Combine(movieDir, folderName + ".strm");

                        var ext = !string.IsNullOrEmpty(movie.ContainerExtension)
                            ? movie.ContainerExtension
                            : "mp4";

                        var streamUrl = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}/movie/{1}/{2}/{3}.{4}",
                            config.BaseUrl, config.Username, config.Password, movie.StreamId, ext);

                        preparedMovies.Add(new MovieSyncCandidate
                        {
                            Movie = movie,
                            CleanedName = cleanedName,
                            FolderName = folderName,
                            MovieDirectory = movieDir,
                            StrmPath = strmPath,
                            StreamUrl = streamUrl,
                            TmdbId = tmdbId,
                            IsLocallyFiltered =
                                localFilter != null &&
                                localFilter.ContainsMovie(providerTmdbId, providerImdbId, cleanedName),
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Failed to write STRM for movie '{0}': [{1}] {2}", movie.Name, ex.GetType().Name, ex.Message);
                        lock (_failedItemsLock)
                        {
                            _failedItems.Add(new FailedSyncItem
                            {
                                ItemType = "Movie",
                                StreamId = movie.StreamId,
                                Name = movie.Name,
                                CategoryId = movie.CategoryId,
                                TmdbId = IsValidTmdbId(movie.TmdbId) ? movie.TmdbId : null,
                                ContainerExtension = movie.ContainerExtension,
                                ErrorMessage = ex.Message
                            });
                        }
                        Interlocked.Increment(ref mp.Failed);
                        Interlocked.Increment(ref mp.Completed);
                        ReportTaskProgress(mp, taskProgress);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(prepareTasks).ConfigureAwait(false);

                var pathGroups = preparedMovies
                    .GroupBy(m => m.StrmPath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var duplicatePathRecords = pathGroups.Sum(g => Math.Max(0, g.Count() - 1));
                if (duplicatePathRecords > 0)
                {
                    _logger.Info(
                        "Movie path deduplication: collapsed {0} duplicate provider record(s) across {1} target path(s)",
                        duplicatePathRecords,
                        pathGroups.Count(g => g.Count() > 1));
                }

                int filterDeletedMovies = 0;
                var writeTasks = pathGroups.Select(async group =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var candidates = group
                            .OrderBy(m => m.Movie.StreamId)
                            .ToList();

                        // If any provider record for this path matches local media,
                        // suppress every record for the path. This avoids one record
                        // deleting the STRM while another recreates it in the same run.
                        var filteredCandidate = candidates.FirstOrDefault(m => m.IsLocallyFiltered);
                        if (filteredCandidate != null)
                        {
                            lock (locallyFilteredPaths)
                            {
                                locallyFilteredPaths.Add(filteredCandidate.StrmPath);
                            }

                            var strmRemoved = false;
                            if (File.Exists(filteredCandidate.StrmPath))
                            {
                                try
                                {
                                    File.Delete(filteredCandidate.StrmPath);
                                    strmRemoved = true;
                                    Interlocked.Increment(ref filterDeletedMovies);
                                    _logger.Info(
                                        "Local media filter: removed duplicate STRM for '{0}' — local file now in library",
                                        filteredCandidate.CleanedName);
                                }
                                catch (Exception delEx)
                                {
                                    _logger.Warn(
                                        "Local media filter: could not remove STRM for '{0}': {1}",
                                        filteredCandidate.CleanedName,
                                        delEx.Message);
                                }
                            }

                            // Remove generated metadata after a successful STRM
                            // deletion, or when a previous run already removed the
                            // STRM but left metadata behind.
                            if (strmRemoved || !File.Exists(filteredCandidate.StrmPath))
                            {
                                try
                                {
                                    DeleteMatchingNfo(filteredCandidate.StrmPath);
                                    PruneOrphanDirectories(
                                        Path.GetDirectoryName(filteredCandidate.StrmPath),
                                        moviesRoot);
                                }
                                catch (Exception metadataEx)
                                {
                                    _logger.Warn(
                                        "Local media filter: could not remove generated metadata for '{0}': {1}",
                                        filteredCandidate.CleanedName,
                                        metadataEx.Message);
                                }
                            }

                            _logger.Debug(
                                "Local media filter: skipping movie path '{0}' ({1} provider record(s))",
                                filteredCandidate.CleanedName,
                                candidates.Count);
                            Interlocked.Add(ref mp.Skipped, candidates.Count);
                            Interlocked.Add(ref mp.Completed, candidates.Count);
                            ReportTaskProgress(mp, taskProgress);
                            return;
                        }

                        // Prefer the provider record already stored in the STRM. If
                        // none matches, use the lowest stream ID for deterministic
                        // ownership so duplicate catalog entries cannot oscillate.
                        var owner = candidates[0];
                        if (File.Exists(owner.StrmPath))
                        {
                            try
                            {
                                var currentUrl = File.ReadAllText(owner.StrmPath);
                                var existingOwner = candidates.FirstOrDefault(m =>
                                    string.Equals(
                                        NormalizeStreamUrl(currentUrl),
                                        NormalizeStreamUrl(m.StreamUrl),
                                        StringComparison.OrdinalIgnoreCase));
                                if (existingOwner != null)
                                    owner = existingOwner;
                            }
                            catch (Exception ex)
                            {
                                _logger.Debug(
                                    "Could not inspect existing duplicate movie path '{0}': {1}",
                                    owner.StrmPath,
                                    ex.Message);
                            }
                        }

                        if (candidates.Count > 1)
                            Interlocked.Add(ref mp.Skipped, candidates.Count - 1);

                        var strmResult = WriteStrmIfChanged(owner.StrmPath, owner.StreamUrl, urlChangeStats);
                        if (strmResult == StrmWriteResult.Added)
                            Interlocked.Increment(ref mp.Added);
                        else if (strmResult == StrmWriteResult.Changed)
                            Interlocked.Increment(ref mp.Changed);
                        else
                            Interlocked.Increment(ref mp.Skipped);
                        lock (writtenPaths) { writtenPaths.Add(owner.StrmPath); }

                        if (strmResult == StrmWriteResult.Added)
                        {
                            lock (addedMovieTitles)
                            {
                                if (addedMovieTitles.Count < 20)
                                    addedMovieTitles.Add(owner.CleanedName);
                            }
                        }

                        if (config.EnableNfoFiles && strmResult != StrmWriteResult.Unchanged)
                        {
                            var nfoPath = Path.Combine(
                                owner.MovieDirectory,
                                owner.FolderName + ".nfo");
                            var yearMatch = YearInTitleRegex.Match(owner.CleanedName);
                            int? nfoYear = null;
                            if (yearMatch.Success)
                            {
                                int y;
                                if (int.TryParse(
                                    yearMatch.Groups[1].Value,
                                    NumberStyles.None,
                                    CultureInfo.InvariantCulture,
                                    out y))
                                    nfoYear = y;
                            }

                            try
                            {
                                if (NfoWriter.WriteMovieNfo(
                                    nfoPath,
                                    owner.CleanedName,
                                    owner.TmdbId,
                                    nfoYear))
                                    Interlocked.Increment(ref mp.NfoChanged);
                            }
                            catch (Exception ex)
                            {
                                _logger.Debug(
                                    "NFO write failed for '{0}': {1}",
                                    owner.Movie.Name,
                                    ex.Message);
                            }
                        }

                        Interlocked.Add(ref mp.Completed, candidates.Count);
                        ReportTaskProgress(mp, taskProgress);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        var failedCandidate = group.First();
                        _logger.Error(
                            "Failed to write STRM for movie path '{0}': [{1}] {2}",
                            failedCandidate.CleanedName,
                            ex.GetType().Name,
                            ex.Message);
                        lock (_failedItemsLock)
                        {
                            _failedItems.Add(new FailedSyncItem
                            {
                                ItemType = "Movie",
                                StreamId = failedCandidate.Movie.StreamId,
                                Name = failedCandidate.Movie.Name,
                                CategoryId = failedCandidate.Movie.CategoryId,
                                TmdbId = IsValidTmdbId(failedCandidate.Movie.TmdbId)
                                    ? failedCandidate.Movie.TmdbId
                                    : null,
                                ContainerExtension = failedCandidate.Movie.ContainerExtension,
                                ErrorMessage = ex.Message,
                            });
                        }
                        var groupCount = group.Count();
                        if (groupCount > 1)
                            Interlocked.Add(ref mp.Skipped, groupCount - 1);
                        Interlocked.Increment(ref mp.Failed);
                        Interlocked.Add(ref mp.Completed, groupCount);
                        ReportTaskProgress(mp, taskProgress);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(writeTasks).ConfigureAwait(false);

                var movieProviderComplete = Volatile.Read(ref mp.Failed) == 0;
                if (!movieProviderComplete)
                {
                    movieSyncSuccess = false;
                    mp.Phase = "Incomplete provider data";
                    _logger.Warn(
                        "Movie sync is incomplete because {0} item(s) failed; orphan cleanup and sync timestamp updates are blocked",
                        mp.Failed);
                }

                if (Volatile.Read(ref movieTmdbCacheChanged) != 0)
                {
                    lock (movieTmdbCacheSaveLock)
                    {
                        config.MovieTmdbCacheJson = SerializeMovieTmdbCache(movieTmdbCache);
                        saveConfig?.Invoke();
                        Interlocked.Exchange(ref movieTmdbCacheUnsavedAdds, 0);
                    }
                    _logger.Info("Movie TMDB detail cache updated: {0} entries", movieTmdbCache.Count);
                }

                // Cleanup orphans
                cancellationToken.ThrowIfCancellationRequested();
                var stableCatalogRuns = movieProviderComplete
                    ? ObserveCompleteCatalog(
                        moviesRoot,
                        allStreams.Select(m =>
                            m.StreamId.ToString(CultureInfo.InvariantCulture) + "." +
                            (m.ContainerExtension ?? "mp4")))
                    : 0;
                if (config.CleanupOrphans && movieProviderComplete)
                {
                    mp.Phase = "Cleaning up orphaned files";
                    var orphans = CollectOrphans(
                        moviesRoot,
                        writtenPaths,
                        config.OrphanSafetyThreshold,
                        stableCatalogRuns,
                        locallyFilteredPaths);
                    if (config.EnableOrphanPreview && orphans.Count > 0)
                    {
                        StagePendingOrphans(config, orphans);
                        _logger.Info("{0} orphaned file(s) staged for review", orphans.Count);
                    }
                    else
                    {
                        mp.Deleted = DeleteOrphans(orphans, moviesRoot);
                    }

                    var metadataOnlyDirectories = PruneMetadataOnlyDirectories(
                        moviesRoot,
                        cancellationToken);
                    if (metadataOnlyDirectories > 0)
                        Interlocked.Increment(ref mp.NfoChanged);
                }
                mp.Deleted += filterDeletedMovies;

                // Persist the highest Added timestamp seen so next sync can delta from here
                cancellationToken.ThrowIfCancellationRequested();
                if (movieProviderComplete && allStreams.Count > 0)
                {
                    var maxAdded = allStreams.Max(m => m.Added);
                    if (maxAdded > config.LastMovieSyncTimestamp)
                    {
                        config.LastMovieSyncTimestamp = (long)(maxAdded ?? 0);
                        saveConfig?.Invoke();
                    }
                }

                LogStreamUrlChangeSummary(isDocumentaries ? "Documentary" : "Movie", urlChangeStats);
                _logger.Info("Movie STRM sync completed: {0} added, {1} changed, {2} skipped, {3} deleted, {4} failed",
                    mp.Added, mp.Changed, mp.Skipped, mp.Deleted, mp.Failed);
                CompleteSyncAndCoalesceLibraryScan(
                    isDocumentaries ? "Documentary" : "Movie",
                    moviesRoot,
                    mp);
            }
            catch (OperationCanceledException)
            {
                _logger.Info("Movie STRM sync stopped by user request.");
                mp.Phase = "Stopped";
                movieSyncSuccess = false;
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error("Movie sync failed: {0}", ex.Message);
                mp.Phase = "Failed: " + ex.Message;
                movieSyncSuccess = false;
                throw;
            }
            finally
            {
                mp.IsRunning = false;
                if (string.IsNullOrEmpty(mp.AbortReason) && movieSyncSuccess)
                {
                    mp.Phase = "Complete";
                }

                AddHistoryEntry(new SyncHistoryEntry
                {
                    StartTime = movieSyncStart,
                    EndTime = DateTime.UtcNow,
                    Success = movieSyncSuccess,
                    WasMovieSync = !isDocumentaries,
                    WasDocumentarySync = isDocumentaries,
                    MoviesTotal = mp.Total,
                    MoviesCompleted = mp.Completed,
                    MoviesAdded = mp.Added,
                    MoviesSkipped = mp.Skipped,
                    MoviesFailed = mp.Failed,
                    MoviesDeleted = mp.Deleted,
                    AddedMovieTitles = addedMovieTitles,
                });
                EndSyncCancellation(syncCancellation);
            }
        }

        public async Task SyncSeriesAsync(PluginConfiguration config, CancellationToken cancellationToken, Action saveConfig = null, IProgress<double> taskProgress = null, bool isDocuSeries = false)
        {
            await _seriesWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await SyncSeriesCoreAsync(
                    config,
                    cancellationToken,
                    saveConfig,
                    taskProgress,
                    isDocuSeries).ConfigureAwait(false);
            }
            finally
            {
                _seriesWriteGate.Release();
            }
        }

        private async Task SyncSeriesCoreAsync(PluginConfiguration config, CancellationToken cancellationToken, Action saveConfig, IProgress<double> taskProgress, bool isDocuSeries)
        {
            ApplyUserAgentToSharedClient();
            CheckAndUpgradeNamingVersion(config, saveConfig);
            var sp = new SyncProgress { IsRunning = true, Phase = "Starting series sync" };
            var ep = new SyncProgress { IsRunning = true };
            if (isDocuSeries) { _docuSeriesProgress = sp; } else { _seriesProgress = sp; }
            _episodeProgress = ep;
            lock (_failedItemsLock) { _failedItems.RemoveAll(i => i.ItemType == "Series"); }
            var seriesSyncStart = DateTime.UtcNow;
            var seriesSyncSuccess = true;
            var addedSeriesTitles = new List<string>();
            var syncCancellation = BeginSyncCancellation(cancellationToken);
            cancellationToken = syncCancellation.Token;

            try
            {
                EnsureStrmLibraryPath(config.StrmLibraryPath);

                var folderMappings = FolderMappingParser.Parse(config.SeriesFolderMappings);
                if (string.Equals(config.SeriesFolderMode, "custom", StringComparison.OrdinalIgnoreCase) &&
                    folderMappings.Count == 0)
                {
                    seriesSyncSuccess = false;
                    sp.AbortReason =
                        "Multiple Folders mode is on but no categories are assigned to any folder. " +
                        "Click + Add Folder, name it, use Refresh Categories, tick the series categories for that folder, then save plugin settings. " +
                        "Or switch back to Single Folder to use the flat category list.";
                    sp.Phase = "Configuration needed";
                    _logger.Warn("Series sync aborted: {0}", sp.AbortReason);
                    return;
                }

                if (config.SelectedSeriesCategoryIds == null || config.SelectedSeriesCategoryIds.Length == 0)
                {
                    // FetchSeriesListAsync intentionally treats an empty array as an
                    // unfiltered catalog for internal discovery callers. A user-initiated
                    // TV/DocuSeries sync must instead fail closed so clearing every category
                    // cannot download and write the provider's complete series catalog.
                    seriesSyncSuccess = false;
                    sp.AbortReason = (isDocuSeries ? "DocuSeries" : "Series") +
                        " sync skipped: select at least one category.";
                    sp.Phase = "Configuration needed";
                    _logger.Warn(sp.AbortReason);
                    return;
                }

                var categoryNames = new Dictionary<int, string>();

                if (!string.Equals(config.SeriesFolderMode, "single", StringComparison.OrdinalIgnoreCase))
                {
                    sp.Phase = "Fetching series categories";
                    var categories = await FetchSeriesCategoriesWithFallbackAsync(config, cancellationToken).ConfigureAwait(false);
                    foreach (var cat in categories)
                    {
                        categoryNames[cat.CategoryId] = cat.CategoryName;
                    }
                }

                // Parse TVDb overrides once before the loop
                var tvdbOverrides = config.EnableSeriesIdFolderNaming
                    ? ParseTvdbOverrides(config.TvdbFolderIdOverrides)
                    : null;

                sp.Phase = "Fetching series list";
                var allSeries = await FetchSeriesListAsync(config.SelectedSeriesCategoryIds, config, cancellationToken).ConfigureAwait(false);

                // When metadata-ID folder naming is disabled, separate provider records can
                // resolve to the same series folder. Resolve ownership before the parallel
                // writer starts so duplicate mirrors cannot race and alternate STRM URLs.
                var pathOwnership = new SeriesPathOwnershipPlan();
                if (!config.EnableSeriesIdFolderNaming)
                {
                    sp.Phase = "Resolving duplicate series paths";
                    pathOwnership = await BuildSeriesPathOwnershipPlanAsync(
                        allSeries,
                        config,
                        categoryNames,
                        folderMappings,
                        cancellationToken).ConfigureAwait(false);
                }

                // Delta sync: split into changed and unchanged using LastModified timestamp
                var lastSeriesTs = config.LastSeriesSyncTimestamp;
                long maxSeriesTs = lastSeriesTs;

                sp.Total = allSeries.Count;
                sp.Phase = "Writing STRM files";

                int deltaNew = 0, deltaExisting = 0;
                if (lastSeriesTs > 0)
                {
                    foreach (var s in allSeries)
                    {
                        long lm;
                        if (long.TryParse(s.LastModified, NumberStyles.None, CultureInfo.InvariantCulture, out lm) && lm > lastSeriesTs)
                            deltaNew++;
                        else
                            deltaExisting++;
                    }
                    _logger.Info("Delta series sync: {0} changed, {1} unchanged (since timestamp {2})",
                        deltaNew, deltaExisting, lastSeriesTs);
                }
                else
                {
                    _logger.Info("Starting series STRM sync for {0} series", allSeries.Count);
                }

                LocalMediaFilter localSeriesFilter = null;
                if (config.EnableLocalMediaFilter)
                {
                    sp.Phase = "Scanning Emby library";
                    localSeriesFilter = LocalMediaFilter.Build(_logger, config.StrmLibraryPath);
                }

                var writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var locallyFilteredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var intendedEpisodeKeys = new ConcurrentBag<string>();
                var semaphore = new SemaphoreSlim(config.SyncParallelism);
                int filterDeletedEpisodes = 0;

                // Each saved value combines episode IDs/extensions with a hash of the
                // connection settings embedded in STRM URLs. Smart Skip therefore remains
                // safe across provider and configuration changes without persisting secrets.
                var storedHashes = DeserializeEpisodeHashes(config.SeriesEpisodeHashesJson);
                var updatedHashes = new ConcurrentDictionary<string, string>(storedHashes);
                var urlChangeStats = new StreamUrlChangeStats();
                int smartSkippedSeries = 0;
                int smartSkippedEpisodes = 0;
                int noFolderSkippedCount = 0;
                int providerErrorSkippedCount = 0;
                int protectedDetailFailureCount = 0;
                int unsafeProcessingFailureCount = 0;
                int duplicatePathSkippedCount = 0;

                var tasks = allSeries.Select(async series =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var cleanedName = config.EnableContentNameCleaning
                            ? ContentNameCleaner.CleanContentName(series.Name, config.ContentRemoveTerms)
                            : series.Name;
                        var seriesName = SanitizeFileName(cleanedName);
                        if (string.IsNullOrWhiteSpace(seriesName))
                        {
                            Interlocked.Increment(ref unsafeProcessingFailureCount);
                            Interlocked.Increment(ref sp.Failed);
                            Interlocked.Increment(ref sp.Completed);
                            ReportTaskProgress(sp, taskProgress);
                            return;
                        }

                        var subFolder = BuildContentFolderPath(
                            config.SeriesFolderMode, series.CategoryId, categoryNames, folderMappings, GetSeriesRootFolderName(config));

                        if (subFolder == null)
                        {
                            Interlocked.Increment(ref noFolderSkippedCount);
                            Interlocked.Increment(ref sp.Skipped);
                            Interlocked.Increment(ref sp.Completed);
                            ReportTaskProgress(sp, taskProgress);
                            return;
                        }

                        // Fetch series detail (needed for episodes + TMDB ID)
                        SeriesDetailInfo detail;
                        try
                        {
                            if (!pathOwnership.PrefetchedDetails.TryGetValue(series.SeriesId, out detail))
                            {
                                detail = await FetchSeriesDetailAsync(series.SeriesId, config, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            // HTTP 4xx from the provider means the series has no episodes or
                            // doesn't exist on the server (e.g. 454 "no content"). Skip quietly
                            // rather than counting as a failure, since this is a provider data
                            // issue and not something retrying will fix.
                            var httpEx = ex as System.Net.Http.HttpRequestException;
                            var msg = ex.Message;
                            bool isClientError = httpEx != null && (
                                msg.Contains(" 4") ||   // catches "4xx" in the status description
                                msg.Contains(": 4"));   // "success: 4xx" format
                            var isNotFound = isClientError && msg.Contains("404");
                            if (isNotFound)
                            {
                                var protectedCount = ProtectExistingSeriesFiles(
                                    config.StrmLibraryPath,
                                    subFolder,
                                    seriesName,
                                    writtenPaths);
                                _logger.Warn(
                                    "Series '{0}' (id={1}) is listed by the provider but its detail endpoint returned 404; " +
                                    "protected {2} existing episode file(s) and skipped this stale catalog entry",
                                    series.Name,
                                    series.SeriesId,
                                    protectedCount);
                                Interlocked.Increment(ref providerErrorSkippedCount);
                                Interlocked.Increment(ref sp.Skipped);
                                Interlocked.Increment(ref sp.Completed);
                                ReportTaskProgress(sp, taskProgress);
                                return;
                            }
                            if (isClientError)
                            {
                                var protectedCount = ProtectExistingSeriesFiles(
                                    config.StrmLibraryPath,
                                    subFolder,
                                    seriesName,
                                    writtenPaths);
                                _logger.Warn(
                                    "Series '{0}' (id={1}) could not be validated: provider returned {2}; protected {3} existing episode file(s)",
                                    series.Name,
                                    series.SeriesId,
                                    msg,
                                    protectedCount);
                                Interlocked.Increment(ref providerErrorSkippedCount);
                            }
                            else
                            {
                                var protectedCount = ProtectExistingSeriesFiles(
                                    config.StrmLibraryPath,
                                    subFolder,
                                    seriesName,
                                    writtenPaths);
                                _logger.Error(
                                    "Failed to fetch detail for series '{0}' (id={1}): [{2}] {3}; protected {4} existing episode file(s)",
                                    series.Name,
                                    series.SeriesId,
                                    ex.GetType().Name,
                                    msg,
                                    protectedCount);
                            }
                            lock (_failedItemsLock)
                            {
                                _failedItems.Add(new FailedSyncItem
                                {
                                    ItemType = "Series",
                                    StreamId = series.SeriesId,
                                    Name = series.Name,
                                    CategoryId = series.CategoryId,
                                    ErrorMessage = msg
                                });
                            }
                            Interlocked.Increment(ref protectedDetailFailureCount);
                            Interlocked.Increment(ref sp.Failed);
                            Interlocked.Increment(ref sp.Completed);
                            ReportTaskProgress(sp, taskProgress);
                            return;
                        }

                        if (detail == null || detail.Episodes == null || detail.Episodes.Count == 0)
                        {
                            var protectedCount = ProtectExistingSeriesFiles(
                                config.StrmLibraryPath,
                                subFolder,
                                seriesName,
                                writtenPaths);
                            _logger.Warn(
                                "Series '{0}' (id={1}) returned no episode detail; protected {2} existing episode file(s) and skipped this empty provider record",
                                series.Name,
                                series.SeriesId,
                                protectedCount);
                            Interlocked.Increment(ref providerErrorSkippedCount);
                            Interlocked.Increment(ref sp.Skipped);
                            Interlocked.Increment(ref sp.Completed);
                            ReportTaskProgress(sp, taskProgress);
                            return;
                        }

                        // Build series folder name with metadata ID
                        var folderName = seriesName;
                        var providerTmdbId = detail.Info != null ? detail.Info.TmdbId : null;
                        if (config.EnableSeriesIdFolderNaming)
                        {
                            int? autoTvdbId = null;

                            // Only do TVDb lookup if no override and no provider TMDB
                            if (config.EnableSeriesMetadataLookup &&
                                (tvdbOverrides == null || !tvdbOverrides.ContainsKey(seriesName)) &&
                                !IsValidTmdbId(providerTmdbId))
                            {
                                var yearMatch = YearInTitleRegex.Match(cleanedName);
                                int? yearForLookup = null;
                                if (yearMatch.Success)
                                {
                                    int y;
                                    if (int.TryParse(yearMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out y))
                                    {
                                        yearForLookup = y;
                                    }
                                }

                                try
                                {
                                    autoTvdbId = await _tmdbLookupService.LookupSeriesTvdbIdAsync(cleanedName, yearForLookup, cancellationToken).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    _logger.Debug("TVDb lookup error for '{0}': {1}", cleanedName, ex.Message);
                                }
                            }

                            folderName = BuildSeriesFolderName(seriesName, providerTmdbId, autoTvdbId, tvdbOverrides);
                        }

                        var seriesDir = Path.Combine(config.StrmLibraryPath, subFolder, folderName);
                        var isNewSeries = !Directory.Exists(seriesDir);

                        // Track max LastModified for delta state
                        long seriesLm = 0;
                        long.TryParse(series.LastModified, NumberStyles.None, CultureInfo.InvariantCulture, out seriesLm);
                        if (seriesLm > 0)
                        {
                            lock (_historyLock)
                            {
                                if (seriesLm > maxSeriesTs) maxSeriesTs = seriesLm;
                            }
                        }

                        var currentEpHash = ComputeSeriesSyncFingerprint(detail.Episodes, config);
                        var epHashKey = series.SeriesId.ToString(CultureInfo.InvariantCulture);
                        string storedEpHash;
                        var canSmartSkip = config.SmartSkipExisting &&
                            storedHashes.TryGetValue(epHashKey, out storedEpHash) &&
                            string.Equals(storedEpHash, currentEpHash, StringComparison.Ordinal);
                        if (canSmartSkip)
                            Interlocked.Increment(ref smartSkippedSeries);
                        var seriesFilesChanged = false;

                        foreach (var seasonEntry in detail.Episodes)
                        {
                            foreach (var episode in seasonEntry.Value)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var seasonNum = episode.Season > 0 ? episode.Season : 1;
                                var episodeNum = episode.EpisodeNum > 0 ? episode.EpisodeNum : 1;
                                var seasonFolder = string.Format(CultureInfo.InvariantCulture, "Season {0:D2}", seasonNum);
                                var seasonDir = Path.Combine(seriesDir, seasonFolder);

                                // Some providers embed the series name + episode code in the title
                                // (e.g. "EN - American Gigolo - S01E01", "Yago - S01E33 - Episode 33").
                                // Strip the duplicate portion so the filename doesn't read
                                // "Show - S01E01 - EN - Show - S01E01".
                                var rawEpisodeTitle = StripEpisodeTitleDuplicate(
                                    episode.Title, seriesName, seasonNum, episodeNum);
                                var fileName = BuildEpisodeStrmFileName(seriesName, episode);

                                var strmPath = Path.Combine(seasonDir, fileName);

                                if (localSeriesFilter != null &&
                                    localSeriesFilter.ContainsEpisode(providerTmdbId ?? series.TmdbId, cleanedName, seasonNum, episodeNum))
                                {
                                    if (File.Exists(strmPath))
                                    {
                                        try
                                        {
                                            File.Delete(strmPath);
                                            var strmDir = Path.GetDirectoryName(strmPath);
                                            if (!string.IsNullOrEmpty(strmDir) && Directory.Exists(strmDir) &&
                                                Directory.GetFileSystemEntries(strmDir).Length == 0)
                                                Directory.Delete(strmDir);
                                            Interlocked.Increment(ref filterDeletedEpisodes);
                                            _logger.Info("Local media filter: removed duplicate STRM for '{0}' S{1:D2}E{2:D2} — local file now in library",
                                                cleanedName, seasonNum, episodeNum);
                                        }
                                        catch (Exception delEx)
                                        {
                                            _logger.Warn("Local media filter: could not remove STRM for '{0}' S{1:D2}E{2:D2}: {2}",
                                                cleanedName, seasonNum, episodeNum, delEx.Message);
                                            lock (locallyFilteredPaths) { locallyFilteredPaths.Add(strmPath); }
                                        }
                                    }
                                    _logger.Debug("Local media filter: skipping episode '{0}' S{1:D2}E{2:D2} (already in library)",
                                        cleanedName, seasonNum, episodeNum);
                                    Interlocked.Increment(ref ep.Total);
                                    Interlocked.Increment(ref ep.Skipped);
                                    continue;
                                }

                                var ext = !string.IsNullOrEmpty(episode.ContainerExtension)
                                    ? episode.ContainerExtension
                                    : "mp4";
                                intendedEpisodeKeys.Add(
                                    episode.Id.ToString(CultureInfo.InvariantCulture) + "." + ext);

                                int pathOwnerId;
                                if (pathOwnership.EpisodeOwners.TryGetValue(strmPath, out pathOwnerId) &&
                                    pathOwnerId != series.SeriesId)
                                {
                                    // The preferred mirror owns this exact destination. Count
                                    // the provider item as handled and protect the shared path
                                    // from orphan cleanup without allowing a competing rewrite.
                                    lock (writtenPaths)
                                    {
                                        writtenPaths.Add(strmPath);
                                    }
                                    Interlocked.Increment(ref duplicatePathSkippedCount);
                                    Interlocked.Increment(ref ep.Total);
                                    Interlocked.Increment(ref ep.Skipped);
                                    continue;
                                }

                                var streamUrl = string.Format(
                                    CultureInfo.InvariantCulture,
                                    "{0}/series/{1}/{2}/{3}.{4}",
                                    config.BaseUrl, config.Username, config.Password, episode.Id, ext);

                                StrmWriteResult strmResult;
                                if (canSmartSkip && File.Exists(strmPath))
                                {
                                    // The episode identity/extension and connection settings
                                    // are unchanged, and the expected path still exists.
                                    strmResult = StrmWriteResult.Unchanged;
                                    Interlocked.Increment(ref smartSkippedEpisodes);
                                }
                                else
                                {
                                    strmResult = WriteStrmIfChanged(strmPath, streamUrl, urlChangeStats);
                                }
                                if (strmResult == StrmWriteResult.Added)
                                {
                                    Interlocked.Increment(ref ep.Added);
                                    seriesFilesChanged = true;
                                }
                                else if (strmResult == StrmWriteResult.Changed)
                                {
                                    Interlocked.Increment(ref ep.Changed);
                                    seriesFilesChanged = true;
                                }
                                else
                                    Interlocked.Increment(ref ep.Skipped);

                                if (config.EnableNfoFiles && episode.Info != null &&
                                    strmResult != StrmWriteResult.Unchanged)
                                {
                                    var nfoPath = Path.ChangeExtension(strmPath, ".nfo");
                                    try
                                    {
                                        if (NfoWriter.WriteEpisodeNfo(nfoPath, rawEpisodeTitle, seasonNum, episodeNum, episode.Info))
                                            Interlocked.Increment(ref ep.NfoChanged);
                                    }
                                    catch (Exception ex) { _logger.Warn("WriteEpisodeNfo failed for '{0}': {1}", nfoPath, ex.Message); }
                                }

                                Interlocked.Increment(ref ep.Total);

                                lock (writtenPaths)
                                {
                                    writtenPaths.Add(strmPath);
                                }
                            }
                        }

                        int folderOwnerId;
                        var ownsSeriesFolder =
                            !pathOwnership.FolderOwners.TryGetValue(seriesDir, out folderOwnerId) ||
                            folderOwnerId == series.SeriesId;
                        if (config.EnableNfoFiles && seriesFilesChanged && ownsSeriesFolder)
                        {
                            var showNfoPath = Path.Combine(seriesDir, "tvshow.nfo");
                            var tvdbIdMatch = Regex.Match(folderName, @"\[tvdbid=(\d+)\]");
                            var tmdbIdMatch = Regex.Match(folderName, @"\[tmdbid=(\d+)\]");
                            var showTvdbId = tvdbIdMatch.Success ? tvdbIdMatch.Groups[1].Value : null;
                            var showTmdbId = tmdbIdMatch.Success ? tmdbIdMatch.Groups[1].Value : null;
                            if (showTmdbId == null && detail?.Info?.TmdbId != null)
                                showTmdbId = detail.Info.TmdbId.ToString();
                            try
                            {
                                if (NfoWriter.WriteShowNfo(showNfoPath, seriesName, showTvdbId, showTmdbId))
                                    Interlocked.Increment(ref sp.NfoChanged);
                            }
                            catch (Exception ex) { _logger.Debug("Show NFO write failed for '{0}': {1}", seriesName, ex.Message); }
                        }

                        if (isNewSeries)
                        {
                            Interlocked.Increment(ref sp.Added);
                            lock (addedSeriesTitles)
                            {
                                if (addedSeriesTitles.Count < 20) addedSeriesTitles.Add(cleanedName);
                            }
                        }

                        // Commit the Smart Skip checkpoint only after every episode in
                        // this series completed successfully. A later file-processing
                        // exception must leave the previous fingerprint in place so the
                        // next run performs a full verification.
                        updatedHashes[epHashKey] = currentEpHash;
                        Interlocked.Increment(ref sp.Completed);
                        ReportTaskProgress(sp, taskProgress);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref unsafeProcessingFailureCount);
                        _logger.Error("Failed to write STRM for series '{0}' (id={1}): [{2}] {3}", series.Name, series.SeriesId, ex.GetType().Name, ex.Message);
                        lock (_failedItemsLock)
                        {
                            _failedItems.Add(new FailedSyncItem
                            {
                                ItemType = "Series",
                                StreamId = series.SeriesId,
                                Name = series.Name,
                                CategoryId = series.CategoryId,
                                ErrorMessage = ex.Message
                            });
                        }
                        Interlocked.Increment(ref sp.Failed);
                        Interlocked.Increment(ref sp.Completed);
                        ReportTaskProgress(sp, taskProgress);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);

                var protectedDetailFailures = Volatile.Read(ref protectedDetailFailureCount);
                var unsafeProcessingFailures = Volatile.Read(ref unsafeProcessingFailureCount);
                var seriesFullyValidated =
                    protectedDetailFailures == 0 &&
                    unsafeProcessingFailures == 0 &&
                    Volatile.Read(ref sp.Failed) == 0;
                var cleanupSafe = unsafeProcessingFailures == 0;
                if (!seriesFullyValidated)
                {
                    seriesSyncSuccess = false;
                    sp.Phase = "Incomplete provider data";
                    if (cleanupSafe)
                    {
                        _logger.Warn(
                            "Series sync is incomplete because {0} series detail request(s) failed; " +
                            "their existing files are protected, cleanup may continue for the rest of the fetched catalog, " +
                            "and the catalog timestamp remains blocked",
                            protectedDetailFailures);
                    }
                    else
                    {
                        _logger.Warn(
                            "Series sync is incomplete because {0} unsafe processing failure(s) occurred; " +
                            "orphan cleanup and the catalog timestamp are blocked",
                            unsafeProcessingFailures);
                    }
                }

                // Cleanup orphans
                cancellationToken.ThrowIfCancellationRequested();
                var showsRoot = Path.Combine(config.StrmLibraryPath, GetSeriesRootFolderName(config));
                var stableCatalogRuns = cleanupSafe
                    ? ObserveCompleteCatalog(showsRoot, intendedEpisodeKeys)
                    : 0;
                if (config.CleanupOrphans && cleanupSafe)
                {
                    sp.Phase = "Cleaning up orphaned files";
                    var orphans = CollectOrphans(
                        showsRoot,
                        writtenPaths,
                        config.OrphanSafetyThreshold,
                        stableCatalogRuns,
                        locallyFilteredPaths);
                    if (config.EnableOrphanPreview && orphans.Count > 0)
                    {
                        StagePendingOrphans(config, orphans);
                        _logger.Info("{0} orphaned file(s) staged for review", orphans.Count);
                    }
                    else
                    {
                        var deleted = DeleteOrphans(orphans, showsRoot);
                        sp.Deleted = deleted;
                        ep.Deleted = deleted;
                    }

                    var metadataOnlyDirectories = PruneMetadataOnlyDirectories(
                        showsRoot,
                        cancellationToken);
                    if (metadataOnlyDirectories > 0)
                        Interlocked.Increment(ref sp.NfoChanged);
                }
                ep.Deleted += filterDeletedEpisodes;

                // Persist the highest LastModified timestamp seen
                cancellationToken.ThrowIfCancellationRequested();
                if (seriesFullyValidated && maxSeriesTs > config.LastSeriesSyncTimestamp)
                {
                    config.LastSeriesSyncTimestamp = maxSeriesTs;
                    saveConfig?.Invoke();
                }

                // Persist successful per-series fingerprints even when another provider
                // record failed. Failed records retain their previous fingerprints and
                // will be fully checked on their next successful response.
                config.SeriesEpisodeHashesJson = SerializeEpisodeHashes(updatedHashes);
                saveConfig?.Invoke();

                if (noFolderSkippedCount > 0)
                    _logger.Warn("Series skip: {0} series had no matching folder mapping (check Series Folder Mode settings)", noFolderSkippedCount);
                if (providerErrorSkippedCount > 0)
                    _logger.Warn(
                        "Series provider skips: {0} stale 404 or empty-detail record(s) were protected",
                        providerErrorSkippedCount);
                if (Volatile.Read(ref smartSkippedSeries) > 0)
                    _logger.Info(
                        "Series Smart Skip: {0} series fingerprints unchanged; skipped reading {1} existing STRM file(s)",
                        Volatile.Read(ref smartSkippedSeries),
                        Volatile.Read(ref smartSkippedEpisodes));
                if (Volatile.Read(ref duplicatePathSkippedCount) > 0)
                    _logger.Info(
                        "Series duplicate-path protection: skipped {0} competing episode write(s) across {1} duplicate folder(s); {2} shared path(s) had deterministic owners",
                        Volatile.Read(ref duplicatePathSkippedCount),
                        pathOwnership.DuplicateFolderCount,
                        pathOwnership.CompetingPathCount);
                ep.Failed = sp.Failed;
                LogStreamUrlChangeSummary(isDocuSeries ? "DocuSeries" : "Series", urlChangeStats);
                _logger.Info("Series STRM sync completed: {0} episodes added, {1} changed, {2} skipped, {3} deleted, {4} failed",
                    ep.Added, ep.Changed, ep.Skipped, ep.Deleted, sp.Failed);
                CompleteSyncAndCoalesceLibraryScan(
                    isDocuSeries ? "DocuSeries" : "Series",
                    showsRoot,
                    sp,
                    ep);
            }
            catch (OperationCanceledException)
            {
                _logger.Info("Series STRM sync stopped by user request.");
                sp.Phase = "Stopped";
                seriesSyncSuccess = false;
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error("Series sync failed: {0}", ex.Message);
                sp.Phase = "Failed: " + ex.Message;
                seriesSyncSuccess = false;
                throw;
            }
            finally
            {
                sp.IsRunning = false;
                if (string.IsNullOrEmpty(sp.AbortReason) && seriesSyncSuccess)
                {
                    sp.Phase = "Complete";
                }

                AddHistoryEntry(new SyncHistoryEntry
                {
                    StartTime = seriesSyncStart,
                    EndTime = DateTime.UtcNow,
                    Success = seriesSyncSuccess,
                    WasSeriesSync = !isDocuSeries,
                    WasDocuSeriesSync = isDocuSeries,
                    SeriesTotal = sp.Total,
                    SeriesCompleted = sp.Completed,
                    SeriesAdded = sp.Added,
                    SeriesSkipped = sp.Skipped,
                    SeriesFailed = sp.Failed,
                    SeriesDeleted = sp.Deleted,
                    EpisodeTotal = ep.Total,
                    EpisodeAdded = ep.Added,
                    EpisodeSkipped = ep.Skipped,
                    EpisodeFailed = sp.Failed,
                    EpisodeDeleted = ep.Deleted,
                    AddedSeriesTitles = addedSeriesTitles,
                });
                EndSyncCancellation(syncCancellation);
            }
        }

        private void EnsureStrmLibraryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("STRM Library Path is not configured. Set it in the plugin settings.");
            }

            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    string.Format("Cannot create STRM Library Path '{0}': {1}. Check the path is valid and Emby has write permission.", path, ex.Message), ex);
            }
        }

        public async Task RetryFailedAsync(CancellationToken cancellationToken)
        {
            await _seriesWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await RetryFailedCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _seriesWriteGate.Release();
            }
        }

        private async Task RetryFailedCoreAsync(CancellationToken cancellationToken)
        {
            List<FailedSyncItem> items;
            lock (_failedItemsLock) { items = _failedItems.ToList(); }
            if (items.Count == 0) return;

            var config = Plugin.Instance.Configuration;
            _retryProgress = new SyncProgress { IsRunning = true, Phase = "Retrying failed items", Total = items.Count };

            try
            {
                var semaphore = new SemaphoreSlim(config.SyncParallelism);
                var categoryNames = new Dictionary<int, string>();
                var folderMappings = FolderMappingParser.Parse(config.MovieFolderMappings);
                var writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var succeeded = new List<FailedSyncItem>();
                var succeededLock = new object();

                var tasks = items.Select(async item =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (item.ItemType == "Movie")
                            await RetryMovieItemAsync(item, config, categoryNames, folderMappings, writtenPaths, cancellationToken).ConfigureAwait(false);
                        else if (item.ItemType == "Series")
                            await RetrySeriesItemAsync(item, config, cancellationToken).ConfigureAwait(false);

                        lock (succeededLock) { succeeded.Add(item); }
                        Interlocked.Increment(ref _retryProgress.Completed);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Retry still failed for '{0}': {1}", item.Name, ex.Message);
                        Interlocked.Increment(ref _retryProgress.Failed);
                        Interlocked.Increment(ref _retryProgress.Completed);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);

                lock (_failedItemsLock)
                {
                    foreach (var s in succeeded)
                        _failedItems.Remove(s);
                }
                CompleteSyncAndCoalesceLibraryScan("Retry", config.StrmLibraryPath, _retryProgress);
            }
            finally
            {
                _retryProgress.IsRunning = false;
                _retryProgress.Phase = "Retry complete";
            }
        }

        private async Task RetryMovieItemAsync(
            FailedSyncItem item,
            PluginConfiguration config,
            Dictionary<int, string> categoryNames,
            Dictionary<int, string> folderMappings,
            HashSet<string> writtenPaths,
            CancellationToken cancellationToken)
        {
            var cleanedName = config.EnableContentNameCleaning
                ? ContentNameCleaner.CleanContentName(item.Name, config.ContentRemoveTerms)
                : item.Name;
            var folderName = BuildMovieFolderName(cleanedName, item.TmdbId);
            if (string.IsNullOrWhiteSpace(folderName)) return;

            var subFolder = BuildContentFolderPath(
                config.MovieFolderMode, item.CategoryId, categoryNames, folderMappings, GetMovieRootFolderName(config));
            if (subFolder == null) return;

            var movieDir = Path.Combine(config.StrmLibraryPath, subFolder, folderName);
            var strmPath = Path.Combine(movieDir, folderName + ".strm");
            var ext = !string.IsNullOrEmpty(item.ContainerExtension) ? item.ContainerExtension : "mp4";
            var streamUrl = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/movie/{1}/{2}/{3}.{4}",
                config.BaseUrl, config.Username, config.Password, item.StreamId, ext);

            var strmResult = WriteStrmIfChanged(strmPath, streamUrl);
            if (strmResult == StrmWriteResult.Added)
                Interlocked.Increment(ref _retryProgress.Added);
            else if (strmResult == StrmWriteResult.Changed)
                Interlocked.Increment(ref _retryProgress.Changed);
            else
                Interlocked.Increment(ref _retryProgress.Skipped);

            lock (writtenPaths) { writtenPaths.Add(strmPath); }

            if (config.EnableNfoFiles && !string.IsNullOrEmpty(item.TmdbId) &&
                strmResult != StrmWriteResult.Unchanged)
            {
                var nfoPath = Path.Combine(movieDir, folderName + ".nfo");
                var yearMatch = YearInTitleRegex.Match(cleanedName);
                int? nfoYear = null;
                if (yearMatch.Success)
                {
                    int y;
                    if (int.TryParse(yearMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out y))
                        nfoYear = y;
                }
                try
                {
                    if (NfoWriter.WriteMovieNfo(nfoPath, cleanedName, item.TmdbId, nfoYear))
                        Interlocked.Increment(ref _retryProgress.NfoChanged);
                }
                catch (Exception ex) { _logger.Debug("NFO write failed on retry for '{0}': {1}", item.Name, ex.Message); }
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private async Task RetrySeriesItemAsync(
            FailedSyncItem item,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var detail = await FetchSeriesDetailAsync(item.StreamId, config, cancellationToken).ConfigureAwait(false);
            if (detail == null || detail.Episodes == null || detail.Episodes.Count == 0) return;

            var cleanedName = config.EnableContentNameCleaning
                ? ContentNameCleaner.CleanContentName(item.Name, config.ContentRemoveTerms)
                : item.Name;

            var seriesName = SanitizeFileName(cleanedName);
            var seriesDir = Path.Combine(config.StrmLibraryPath, GetSeriesRootFolderName(config), seriesName);
            Directory.CreateDirectory(seriesDir);

            foreach (var kvp in detail.Episodes)
            {
                int seasonNum;
                if (!int.TryParse(kvp.Key, out seasonNum) || seasonNum < 1) seasonNum = 1;
                var episodes = kvp.Value;
                if (episodes == null) continue;

                var seasonDir = Path.Combine(seriesDir, string.Format(CultureInfo.InvariantCulture, "Season {0:D2}", seasonNum));
                Directory.CreateDirectory(seasonDir);

                foreach (var ep in episodes)
                {
                    if (ep == null) continue;
                    var episodeNum = ep.EpisodeNum > 0 ? ep.EpisodeNum : 1;
                    var rawTitle = StripEpisodeTitleDuplicate(ep.Title, seriesName, seasonNum, episodeNum);
                    var epTitle = !string.IsNullOrWhiteSpace(rawTitle) ? " - " + SanitizeFileName(rawTitle) : string.Empty;
                    var fileNameBase = string.Format(CultureInfo.InvariantCulture,
                        "{0} - S{1:D2}E{2:D2}{3}", seriesName, seasonNum, episodeNum, epTitle);
                    if (fileNameBase.Length > 240) fileNameBase = fileNameBase.Substring(0, 240);
                    var epPath = Path.Combine(seasonDir, fileNameBase + ".strm");

                    var ext = !string.IsNullOrEmpty(ep.ContainerExtension) ? ep.ContainerExtension : "mp4";
                    var epUrl = string.Format(CultureInfo.InvariantCulture,
                        "{0}/series/{1}/{2}/{3}.{4}",
                        config.BaseUrl, config.Username, config.Password, ep.Id, ext);

                    var strmResult = WriteStrmIfChanged(epPath, epUrl);
                    if (strmResult == StrmWriteResult.Added)
                        Interlocked.Increment(ref _retryProgress.Added);
                    else if (strmResult == StrmWriteResult.Changed)
                        Interlocked.Increment(ref _retryProgress.Changed);
                    else
                        Interlocked.Increment(ref _retryProgress.Skipped);

                    if (config.EnableNfoFiles && ep.Info != null &&
                        strmResult != StrmWriteResult.Unchanged)
                    {
                        var nfoPath = Path.ChangeExtension(epPath, ".nfo");
                        try
                        {
                            if (NfoWriter.WriteEpisodeNfo(nfoPath, rawTitle, seasonNum, episodeNum, ep.Info))
                                Interlocked.Increment(ref _retryProgress.NfoChanged);
                        }
                        catch { }
                    }
                }
            }
        }

        private void AddHistoryEntry(SyncHistoryEntry entry)
        {
            string historyJson;
            lock (_historyLock)
            {
                var history = GetOrLoadHistory();
                history.Insert(0, entry);
                while (history.Count > MaxHistoryEntries)
                {
                    history.RemoveAt(history.Count - 1);
                }
                historyJson = STJ.JsonSerializer.Serialize(_syncHistory, JsonOptions);
            }

            try
            {
                Plugin.Instance.Configuration.SyncHistoryJson = historyJson;
                Plugin.Instance.SaveConfiguration();
            }
            catch (Exception ex)
            {
                _logger.Debug("Failed to persist sync history: {0}", ex.Message);
            }
        }

        internal static string BuildMovieFolderName(string cleanedName, string tmdbId)
        {
            var sanitized = SanitizeFileName(cleanedName);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return string.Empty;
            }

            if (IsValidTmdbId(tmdbId))
            {
                return sanitized + " [tmdbid=" + tmdbId.Trim() + "]";
            }

            return sanitized;
        }

        private static bool IsValidTmdbId(string tmdbId)
        {
            if (string.IsNullOrWhiteSpace(tmdbId))
            {
                return false;
            }

            int id;
            return int.TryParse(tmdbId, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
        }

        internal static Dictionary<string, int> ParseTvdbOverrides(string config)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(config))
            {
                return result;
            }

            var lines = config.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    continue;
                }

                var eqIdx = trimmed.IndexOf('=');
                if (eqIdx <= 0)
                {
                    continue;
                }

                var folderName = trimmed.Substring(0, eqIdx).Trim();
                var idStr = trimmed.Substring(eqIdx + 1).Trim();

                int tvdbId;
                if (!string.IsNullOrEmpty(folderName) &&
                    int.TryParse(idStr, NumberStyles.None, CultureInfo.InvariantCulture, out tvdbId) &&
                    tvdbId > 0)
                {
                    result[folderName] = tvdbId;
                }
            }

            return result;
        }

        internal static string BuildSeriesFolderName(
            string sanitizedName, string tmdbId,
            int? autoTvdbId, Dictionary<string, int> tvdbOverrides)
        {
            if (string.IsNullOrWhiteSpace(sanitizedName))
            {
                return string.Empty;
            }

            // Priority 1: manual TVDb override
            int overrideId;
            if (tvdbOverrides != null && tvdbOverrides.TryGetValue(sanitizedName, out overrideId))
            {
                return sanitizedName + " [tvdbid=" + overrideId.ToString(CultureInfo.InvariantCulture) + "]";
            }

            // Priority 2: Xtream provider TMDB ID
            if (IsValidTmdbId(tmdbId))
            {
                return sanitizedName + " [tmdbid=" + tmdbId.Trim() + "]";
            }

            // Priority 3: auto TVDb lookup
            if (autoTvdbId.HasValue && autoTvdbId.Value > 0)
            {
                return sanitizedName + " [tvdbid=" + autoTvdbId.Value.ToString(CultureInfo.InvariantCulture) + "]";
            }

            // Priority 4: no ID
            return sanitizedName;
        }

        /// <summary>
        /// Strips provider-embedded series name + episode code prefixes from an episode title.
        /// Handles two patterns:
        ///   1. "{CleanedSeriesName} - SxxExx" at start/anywhere (full name match)
        ///   2. "AnyPrefix - SxxExx - ..." where SxxExx matches the exact season/episode numbers
        /// Returns the human-readable remainder, or empty string if the title was only a prefix.
        /// </summary>
        internal static string StripEpisodeTitleDuplicate(string episodeTitle, string seriesName, int seasonNum, int episodeNum)
        {
            var result = episodeTitle?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(result))
                return result;

            // Pass 1: strip "{seriesName} - SxxExx" pattern (handles clean name match including year)
            if (!string.IsNullOrEmpty(seriesName))
            {
                result = Regex.Replace(
                    result,
                    @"[\s\-]*" + Regex.Escape(seriesName) + @"[\s\-]*S\d+E\d+[\s\-]*",
                    string.Empty,
                    RegexOptions.IgnoreCase).Trim('-', ' ');
            }

            // Pass 2: if the exact episode code still appears (e.g. provider used a short series
            // name without the year), strip everything up to and including that code.
            // "Yago - S01E33 - Episode 33" → "Episode 33"
            var episodeCode = string.Format(CultureInfo.InvariantCulture, "S{0:D2}E{1:D2}", seasonNum, episodeNum);
            var codeIdx = result.IndexOf(episodeCode, StringComparison.OrdinalIgnoreCase);
            if (codeIdx >= 0)
            {
                result = result.Substring(codeIdx + episodeCode.Length).Trim('-', ' ');
            }

            return result;
        }

        internal static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var result = InvalidFileCharsRegex.Replace(name, string.Empty);
            // Remove leading/trailing dots and spaces (invalid on Windows)
            result = result.Trim('.', ' ');
            // Collapse multiple spaces
            result = Regex.Replace(result, @"\s{2,}", " ");
            return result;
        }

        private static string BuildContentFolderPath(
            string folderMode,
            int? categoryId,
            Dictionary<int, string> categoryNames,
            Dictionary<int, string> folderMappings,
            string rootFolder)
        {
            if (string.Equals(folderMode, "single", StringComparison.OrdinalIgnoreCase))
            {
                return rootFolder;
            }

            if (string.Equals(folderMode, "custom", StringComparison.OrdinalIgnoreCase) && categoryId.HasValue)
            {
                string mappedFolder;
                if (folderMappings.TryGetValue(categoryId.Value, out mappedFolder))
                {
                    return Path.Combine(rootFolder, SanitizeFileName(mappedFolder));
                }
                return null;
            }

            if (string.Equals(folderMode, "multiple", StringComparison.OrdinalIgnoreCase) && categoryId.HasValue)
            {
                string categoryName;
                if (categoryNames.TryGetValue(categoryId.Value, out categoryName) &&
                    !string.IsNullOrWhiteSpace(categoryName))
                {
                    return Path.Combine(rootFolder, SanitizeFileName(categoryName));
                }
                return null;
            }

            return rootFolder;
        }

        private async Task<List<Category>> FetchCategoriesAsync(string action, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action={3}",
                config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty), action);

            var json = await GetProviderStringWithRetryAsync(
                url, action, cancellationToken).ConfigureAwait(false);
            return XtreamResponseParser.DeserializeCategories(json, JsonOptions);
        }

        private async Task<List<Category>> FetchSeriesCategoriesWithFallbackAsync(
            PluginConfiguration config, CancellationToken cancellationToken)
        {
            var categories = await FetchCategoriesAsync("get_series_categories", config, cancellationToken).ConfigureAwait(false);
            if (categories.Count > 0)
            {
                return categories;
            }

            // Fallback: derive categories from series list
            _logger.Info("get_series_categories returned empty, deriving from series list");
            var seriesList = await FetchSeriesListAsync(null, config, cancellationToken).ConfigureAwait(false);
            return seriesList
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

        private async Task<List<VodStreamInfo>> FetchVodStreamsAsync(
            int[] categoryIds, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var allStreams = new List<VodStreamInfo>();

            if (categoryIds == null || categoryIds.Length == 0)
            {
                // Fetch all VOD streams
                var url = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/player_api.php?username={1}&password={2}&action=get_vod_streams",
                    config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty));

                var json = await GetProviderStringWithRetryAsync(
                    url, "VOD catalog", cancellationToken).ConfigureAwait(false);
                allStreams = STJ.JsonSerializer.Deserialize<List<VodStreamInfo>>(json, JsonOptions)
                    ?? new List<VodStreamInfo>();
            }
            else
            {
                var semaphore = new SemaphoreSlim(config.SyncParallelism);
                var tasks = categoryIds.Select(async catId =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var url = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}/player_api.php?username={1}&password={2}&action=get_vod_streams&category_id={3}",
                            config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty), catId);

                        var json = await GetProviderStringWithRetryAsync(
                            url,
                            "VOD category " + catId.ToString(CultureInfo.InvariantCulture),
                            cancellationToken).ConfigureAwait(false);
                        var streams = STJ.JsonSerializer.Deserialize<List<VodStreamInfo>>(json, JsonOptions)
                            ?? new List<VodStreamInfo>();

                        // Override category_id to match the requested category.
                        // The Xtream API can return cross-listed movies whose primary
                        // category_id differs from the category we queried. Without
                        // this, custom folder mapping skips them as unmapped.
                        foreach (var s in streams)
                        {
                            s.CategoryId = catId;
                        }

                        return streams;
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            "VOD category " + catId.ToString(CultureInfo.InvariantCulture) +
                            " could not be loaded; sync aborted to preserve the existing library",
                            ex);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var result in results)
                {
                    allStreams.AddRange(result);
                }

                // Deduplicate by StreamId (first occurrence wins, keeping its assigned category)
                allStreams = allStreams.GroupBy(s => s.StreamId).Select(g => g.First()).ToList();
            }

            return allStreams;
        }

        // Returns [0]=tmdbId, [1]=imdbId; either element may be null.
        private async Task<string[]> FetchVodDetailAsync(
            int streamId, PluginConfiguration config, CancellationToken cancellationToken)
        {
            try
            {
                var url = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/player_api.php?username={1}&password={2}&action=get_vod_info&vod_id={3}",
                    config.BaseUrl,
                    Uri.EscapeDataString(config.Username ?? string.Empty),
                    Uri.EscapeDataString(config.Password ?? string.Empty),
                    streamId);

                var json = await GetProviderStringWithRetryAsync(
                    url,
                    "VOD detail " + streamId.ToString(CultureInfo.InvariantCulture),
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                string tmdbId = null;
                string imdbId = null;

                using (var doc = STJ.JsonDocument.Parse(json))
                {
                    STJ.JsonElement info;
                    if (!doc.RootElement.TryGetProperty("info", out info) ||
                        info.ValueKind != STJ.JsonValueKind.Object)
                    {
                        return null;
                    }

                    STJ.JsonElement imdbVal;
                    foreach (var imdbKey in new[] { "imdb_id", "imdb_code", "imdb" })
                    {
                        if (info.TryGetProperty(imdbKey, out imdbVal))
                        {
                            var raw = imdbVal.ValueKind == STJ.JsonValueKind.String
                                ? imdbVal.GetString()
                                : imdbVal.ToString();
                            if (!string.IsNullOrWhiteSpace(raw) && raw.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                            {
                                imdbId = raw.Trim();
                                break;
                            }
                        }
                    }

                    foreach (var key in new[] { "tmdb_id", "tmdb", "tmdbid" })
                    {
                        STJ.JsonElement value;
                        if (!info.TryGetProperty(key, out value))
                            continue;

                        var id = value.ValueKind == STJ.JsonValueKind.String
                            ? value.GetString()
                            : value.ToString();

                        if (IsValidTmdbId(id))
                        {
                            tmdbId = id.Trim();
                            break;
                        }
                    }
                }

                return new[] { tmdbId, imdbId };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Debug("VOD detail lookup failed for stream {0}: {1}", streamId, ex.Message);
            }

            return null;
        }

        // ── Populate Episode Media Streams ───────────────────────────────────────

        /// <summary>
        /// Queries Emby's library for all STRM episodes with no MediaStreams rows,
        /// fetches real per-episode codec info from the XC API, and writes it directly
        /// to Emby's MediaStreams2 table via IItemRepository. Once populated Emby skips
        /// ffprobe entirely at playback time.
        /// </summary>
        internal async Task PopulateEpisodeMediaStreamsAsync(
            PluginConfiguration config,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            var host = Plugin.Instance?.ApplicationHost;
            if (host == null) { _logger.Warn("PopulateMediaStreams: ApplicationHost unavailable"); return; }

            var libraryManager = host.Resolve<MediaBrowser.Controller.Library.ILibraryManager>();
            var itemRepo = host.Resolve<MediaBrowser.Controller.Persistence.IItemRepository>();
            if (libraryManager == null || itemRepo == null)
            {
                _logger.Warn("PopulateMediaStreams: required library services unavailable");
                return;
            }

            var strmRoot = string.IsNullOrWhiteSpace(config.StrmLibraryPath) ? null :
                config.StrmLibraryPath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrEmpty(strmRoot))
            {
                _logger.Warn("PopulateMediaStreams: STRM library path not configured");
                return;
            }

            _logger.Info("PopulateMediaStreams: scanning library for unprobed STRM episodes...");
            progress?.Report(1);

            // Build index: XC stream ID → Emby item (only episodes with no MediaStreams rows)
            var episodes = libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Episode" },
                Recursive = true,
            });

            var streamIdIndex = new Dictionary<int, MediaBrowser.Controller.Entities.BaseItem>();
            int alreadyPopulated = 0;

            foreach (var item in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(item.Path)) continue;
                var normPath = item.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!normPath.StartsWith(strmRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    !normPath.StartsWith(strmRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(normPath, strmRoot, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(Path.GetExtension(item.Path), ".strm", StringComparison.OrdinalIgnoreCase)) continue;

                // Skip only when both Width and RunTimeTicks are populated
                if (item.Width > 0 && item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0)
                { alreadyPopulated++; continue; }

                if (!File.Exists(item.Path)) continue;
                string strmContent;
                try { strmContent = File.ReadAllText(item.Path).Trim(); }
                catch { continue; }

                var streamId = ParseSeriesStreamId(strmContent);
                if (streamId > 0 && !streamIdIndex.ContainsKey(streamId))
                    streamIdIndex[streamId] = item;
            }

            _logger.Info("PopulateMediaStreams: {0} already have streams, {1} need population",
                alreadyPopulated, streamIdIndex.Count);

            if (streamIdIndex.Count == 0) { progress?.Report(100); return; }

            // Fetch all series from XC (no category filter = all)
            List<SeriesInfo> allSeries;
            try
            {
                allSeries = await FetchSeriesListAsync(new int[0], config, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error("PopulateMediaStreams: failed to fetch series list — {0}", ex.Message);
                return;
            }

            _logger.Info("PopulateMediaStreams: processing {0} series to match {1} episodes",
                allSeries.Count, streamIdIndex.Count);

            int populated = 0, processed = 0, total = Math.Max(1, allSeries.Count);
            var parallelism = Math.Max(1, Math.Min(config.SyncParallelism, 5));
            var sem = new SemaphoreSlim(parallelism, parallelism);

            var tasks = allSeries.Select(async series =>
            {
                await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    SeriesDetailInfo detail;
                    try { detail = await FetchSeriesDetailAsync(series.SeriesId, config, cancellationToken).ConfigureAwait(false); }
                    catch { return; }

                    if (detail?.Episodes == null) return;

                    foreach (var seasonEps in detail.Episodes.Values)
                    {
                        if (seasonEps == null) continue;
                        foreach (var ep in seasonEps)
                        {
                            if (ep == null) continue;
                            MediaBrowser.Controller.Entities.BaseItem item;
                            if (!streamIdIndex.TryGetValue(ep.Id, out item)) continue;
                            if (ep.Info == null) continue;

                            var streams = BuildMediaStreams(ep.Info);
                            if (streams.Count == 0) continue;

                            try
                            {
                                itemRepo.SaveMediaStreams(item.InternalId, streams, cancellationToken);

                                if (ep.Info.Video?.Width > 0)  item.Width  = ep.Info.Video.Width.Value;
                                if (ep.Info.Video?.Height > 0) item.Height = ep.Info.Video.Height.Value;
                                if (!string.IsNullOrEmpty(ep.ContainerExtension))
                                    item.Container = ep.ContainerExtension;
                                if (ep.Info.DurationSecs.HasValue && ep.Info.DurationSecs.Value > 0)
                                    item.RunTimeTicks = (long)ep.Info.DurationSecs.Value * TimeSpan.TicksPerSecond;

                                itemRepo.SaveItem(item, cancellationToken);
                                Interlocked.Increment(ref populated);
                            }
                            catch (Exception ex)
                            {
                                _logger.Warn("PopulateMediaStreams: failed for '{0}': {1}", item.Path, ex.Message);
                            }
                        }
                    }
                }
                finally
                {
                    var p = Interlocked.Increment(ref processed);
                    progress?.Report(1 + p * 98.0 / total);
                    sem.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);
            sem.Dispose();

            _logger.Info("PopulateMediaStreams: complete — {0}/{1} episodes populated from XC API",
                populated, streamIdIndex.Count);
            progress?.Report(100);
        }

        private static int ParseSeriesStreamId(string strmContent)
        {
            var m = Regex.Match(strmContent,
                @"/series/[^/]+/[^/]+/(\d+)\.", RegexOptions.IgnoreCase);
            if (!m.Success) return 0;
            int id;
            return int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out id) ? id : 0;
        }

        private static List<MediaBrowser.Model.Entities.MediaStream> BuildMediaStreams(EpisodeMediaInfo info)
        {
            var streams = new List<MediaBrowser.Model.Entities.MediaStream>();
            int idx = 0;

            if (info.Video != null && !string.IsNullOrEmpty(info.Video.CodecName))
            {
                var fps = ParseFrameRateValue(info.Video.RFrameRate);
                streams.Add(new MediaBrowser.Model.Entities.MediaStream
                {
                    Type         = MediaBrowser.Model.Entities.MediaStreamType.Video,
                    Index        = idx++,
                    Codec        = info.Video.CodecName,
                    Width        = info.Video.Width,
                    Height       = info.Video.Height,
                    AverageFrameRate = fps > 0 ? (float?)fps : null,
                    RealFrameRate    = fps > 0 ? (float?)fps : null,
                    IsDefault    = true,
                    IsForced     = false,
                    IsExternal   = false,
                });
            }

            if (info.Audio != null && !string.IsNullOrEmpty(info.Audio.CodecName))
            {
                string lang = null;
                if (info.Audio.Tags != null) info.Audio.Tags.TryGetValue("language", out lang);

                int? sampleRate = null;
                if (!string.IsNullOrEmpty(info.Audio.SampleRate))
                {
                    int sr;
                    if (int.TryParse(info.Audio.SampleRate, NumberStyles.None, CultureInfo.InvariantCulture, out sr))
                        sampleRate = sr;
                }

                int? bitRate = null;
                if (!string.IsNullOrEmpty(info.Audio.BitRate))
                {
                    int br;
                    if (int.TryParse(info.Audio.BitRate, NumberStyles.None, CultureInfo.InvariantCulture, out br))
                        bitRate = br;
                }

                streams.Add(new MediaBrowser.Model.Entities.MediaStream
                {
                    Type       = MediaBrowser.Model.Entities.MediaStreamType.Audio,
                    Index      = idx++,
                    Codec      = info.Audio.CodecName,
                    Channels   = info.Audio.Channels,
                    SampleRate = sampleRate,
                    BitRate    = bitRate,
                    Language   = lang,
                    IsDefault  = true,
                    IsForced   = false,
                    IsExternal = false,
                });
            }

            return streams;
        }

        private static double ParseFrameRateValue(string rFrameRate)
        {
            if (string.IsNullOrEmpty(rFrameRate)) return 0;
            var slash = rFrameRate.IndexOf('/');
            if (slash > 0)
            {
                double num, den;
                if (double.TryParse(rFrameRate.Substring(0, slash), NumberStyles.Any, CultureInfo.InvariantCulture, out num) &&
                    double.TryParse(rFrameRate.Substring(slash + 1), NumberStyles.Any, CultureInfo.InvariantCulture, out den) &&
                    den != 0)
                    return num / den;
            }
            double fps;
            return double.TryParse(rFrameRate, NumberStyles.Any, CultureInfo.InvariantCulture, out fps) ? fps : 0;
        }

        private async Task<SeriesPathOwnershipPlan> BuildSeriesPathOwnershipPlanAsync(
            List<SeriesInfo> allSeries,
            PluginConfiguration config,
            Dictionary<int, string> categoryNames,
            Dictionary<int, string> folderMappings,
            CancellationToken cancellationToken)
        {
            var plan = new SeriesPathOwnershipPlan();
            var candidates = new List<SeriesFolderCandidate>();

            foreach (var series in allSeries)
            {
                var cleanedName = config.EnableContentNameCleaning
                    ? ContentNameCleaner.CleanContentName(series.Name, config.ContentRemoveTerms)
                    : series.Name;
                var seriesName = SanitizeFileName(cleanedName);
                if (string.IsNullOrWhiteSpace(seriesName)) continue;

                var subFolder = BuildContentFolderPath(
                    config.SeriesFolderMode,
                    series.CategoryId,
                    categoryNames,
                    folderMappings,
                    GetSeriesRootFolderName(config));
                if (subFolder == null) continue;

                candidates.Add(new SeriesFolderCandidate
                {
                    Series = series,
                    SeriesName = seriesName,
                    SeriesDirectory = Path.Combine(config.StrmLibraryPath, subFolder, seriesName),
                });
            }

            var duplicateGroups = candidates
                .GroupBy(c => c.SeriesDirectory, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Select(c => c.Series.SeriesId).Distinct().Count() > 1)
                .ToList();
            if (duplicateGroups.Count == 0) return plan;

            plan.DuplicateFolderCount = duplicateGroups.Count;
            var duplicateCandidates = duplicateGroups.SelectMany(g => g)
                .GroupBy(c => c.Series.SeriesId)
                .Select(g => g.First())
                .ToList();
            var fetchedDetails = new ConcurrentDictionary<int, SeriesDetailInfo>();
            var fetchSemaphore = new SemaphoreSlim(Math.Max(1, config.SyncParallelism));
            var fetchTasks = duplicateCandidates.Select(async candidate =>
            {
                await fetchSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var detail = await FetchSeriesDetailAsync(
                        candidate.Series.SeriesId,
                        config,
                        cancellationToken).ConfigureAwait(false);
                    if (detail != null && detail.Episodes != null && detail.Episodes.Count > 0)
                        fetchedDetails[candidate.Series.SeriesId] = detail;
                }
                catch (Exception ex)
                {
                    _logger.Warn(
                        "Duplicate-path preflight could not inspect series '{0}' (id={1}): {2}; normal provider error handling will retry it",
                        candidate.Series.Name,
                        candidate.Series.SeriesId,
                        ex.Message);
                }
                finally
                {
                    fetchSemaphore.Release();
                }
            });
            await Task.WhenAll(fetchTasks).ConfigureAwait(false);
            fetchSemaphore.Dispose();

            foreach (var item in fetchedDetails)
                plan.PrefetchedDetails[item.Key] = item.Value;

            foreach (var group in duplicateGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolved = group
                    .Where(c => fetchedDetails.ContainsKey(c.Series.SeriesId))
                    .ToList();
                if (resolved.Count != group.Count())
                {
                    _logger.Warn(
                        "Duplicate series folder '{0}' could not be fully resolved because one or more detail requests failed; no ownership rule was applied",
                        group.Key);
                    continue;
                }

                var pathsBySeries = new Dictionary<int, HashSet<string>>();
                foreach (var candidate in resolved)
                {
                    var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var detail = fetchedDetails[candidate.Series.SeriesId];
                    foreach (var season in detail.Episodes.Values)
                    {
                        foreach (var episode in season)
                        {
                            var seasonNum = episode.Season > 0 ? episode.Season : 1;
                            var seasonFolder = string.Format(
                                CultureInfo.InvariantCulture,
                                "Season {0:D2}",
                                seasonNum);
                            paths.Add(Path.Combine(
                                candidate.SeriesDirectory,
                                seasonFolder,
                                BuildEpisodeStrmFileName(candidate.SeriesName, episode)));
                        }
                    }
                    pathsBySeries[candidate.Series.SeriesId] = paths;
                }

                var rank = resolved
                    .OrderByDescending(c => pathsBySeries[c.Series.SeriesId].Count)
                    .ThenByDescending(c => ParseProviderTimestamp(c.Series.LastModified))
                    .ThenBy(c => c.Series.SeriesId)
                    .Select((candidate, index) => new { candidate.Series.SeriesId, Index = index })
                    .ToDictionary(x => x.SeriesId, x => x.Index);
                plan.FolderOwners[group.Key] = rank.OrderBy(x => x.Value).First().Key;

                var pathCandidates = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in pathsBySeries)
                {
                    foreach (var path in pair.Value)
                    {
                        List<int> owners;
                        if (!pathCandidates.TryGetValue(path, out owners))
                        {
                            owners = new List<int>();
                            pathCandidates[path] = owners;
                        }
                        owners.Add(pair.Key);
                    }
                }

                foreach (var collision in pathCandidates.Where(p => p.Value.Count > 1))
                {
                    plan.EpisodeOwners[collision.Key] = collision.Value
                        .OrderBy(id => rank[id])
                        .First();
                    plan.CompetingPathCount++;
                }
            }

            _logger.Info(
                "Series duplicate-path preflight: {0} duplicate folder(s), {1} competing episode path(s); ownership is deterministic by completeness, freshness, then series ID",
                plan.DuplicateFolderCount,
                plan.CompetingPathCount);
            return plan;
        }

        private static long ParseProviderTimestamp(string value)
        {
            long result;
            return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result)
                ? result
                : 0;
        }

        private static string BuildEpisodeStrmFileName(string seriesName, EpisodeInfo episode)
        {
            var seasonNum = episode.Season > 0 ? episode.Season : 1;
            var episodeNum = episode.EpisodeNum > 0 ? episode.EpisodeNum : 1;
            var rawEpisodeTitle = StripEpisodeTitleDuplicate(
                episode.Title,
                seriesName,
                seasonNum,
                episodeNum);
            var episodeTitle = !string.IsNullOrWhiteSpace(rawEpisodeTitle)
                ? " - " + SanitizeFileName(rawEpisodeTitle)
                : string.Empty;
            var fileNameBase = string.Format(
                CultureInfo.InvariantCulture,
                "{0} - S{1:D2}E{2:D2}{3}",
                seriesName,
                seasonNum,
                episodeNum,
                episodeTitle);
            if (fileNameBase.Length > 240)
                fileNameBase = fileNameBase.Substring(0, 240);
            return fileNameBase + ".strm";
        }

        private async Task<List<SeriesInfo>> FetchSeriesListAsync(
            int[] categoryIds, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var allSeries = new List<SeriesInfo>();

            if (categoryIds == null || categoryIds.Length == 0)
            {
                var url = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/player_api.php?username={1}&password={2}&action=get_series",
                    config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty));

                var json = await GetProviderStringWithRetryAsync(
                    url, "series catalog", cancellationToken).ConfigureAwait(false);
                allSeries = XtreamResponseParser.DeserializeSeriesList(json, JsonOptions);
            }
            else
            {
                var semaphore = new SemaphoreSlim(config.SyncParallelism);
                var tasks = categoryIds.Select(async catId =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var url = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}/player_api.php?username={1}&password={2}&action=get_series&category_id={3}",
                            config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty), catId);

                        var json = await GetProviderStringWithRetryAsync(
                            url,
                            "series category " + catId.ToString(CultureInfo.InvariantCulture),
                            cancellationToken).ConfigureAwait(false);
                        var series = XtreamResponseParser.DeserializeSeriesList(json, JsonOptions);

                        // Override category_id to match the requested category (same
                        // cross-listing issue as VOD streams — see FetchVodStreamsAsync).
                        foreach (var s in series)
                        {
                            s.CategoryId = catId;
                        }

                        return series;
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            "Series category " + catId.ToString(CultureInfo.InvariantCulture) +
                            " could not be loaded; sync aborted to preserve the existing library",
                            ex);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var result in results)
                {
                    allSeries.AddRange(result);
                }

                allSeries = allSeries.GroupBy(s => s.SeriesId).Select(g => g.First()).ToList();
            }

            return allSeries;
        }

        private async Task<SeriesDetailInfo> FetchSeriesDetailAsync(
            int seriesId, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action=get_series_info&series_id={3}",
                config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty), seriesId);

            var json = await GetProviderStringWithRetryAsync(
                url,
                "series detail " + seriesId.ToString(CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(false);
            // Some providers return [] or false/null when a series has no detail.
            // Treat anything that isn't a JSON object as empty rather than throwing.
            var trimmed = json == null ? string.Empty : json.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] != '{')
                return null;
            return STJ.JsonSerializer.Deserialize<SeriesDetailInfo>(json, JsonOptions);
        }

        // Collects orphaned STRM paths under rootPath respecting the safety threshold.
        // Returns empty list if safety threshold blocks cleanup.
        private List<string> CollectOrphans(
            string rootPath,
            HashSet<string> validPaths,
            double safetyThreshold,
            int consecutiveCompleteCatalogRuns,
            HashSet<string> locallyFilteredPaths = null)
        {
            if (!Directory.Exists(rootPath)) return new List<string>();

            var existingStrms = Directory.GetFiles(rootPath, "*.strm", SearchOption.AllDirectories);
            var orphans = new List<string>();
            foreach (var s in existingStrms)
            {
                if (!validPaths.Contains(s) && (locallyFilteredPaths == null || !locallyFilteredPaths.Contains(s)))
                    orphans.Add(s);
            }

            if (orphans.Count > 0)
            {
                var thresholdTotal = existingStrms.Count(s =>
                    locallyFilteredPaths == null || !locallyFilteredPaths.Contains(s));
                if (thresholdTotal > 10)
                {
                    double ratio = (double)orphans.Count / thresholdTotal;
                    if (safetyThreshold > 0 && ratio > safetyThreshold)
                    {
                        _logger.Warn(
                            "Orphan cleanup skipped: {0}/{1} ({2:P0}) exceeds safety threshold {3:P0}",
                            orphans.Count, thresholdTotal, ratio, safetyThreshold);
                        return new List<string>();
                    }

                    if (ratio > LargeOrphanRatio && consecutiveCompleteCatalogRuns < 2)
                    {
                        _logger.Warn(
                            "Large orphan cleanup skipped: {0}/{1} ({2:P0}) requires two consecutive identical complete catalogs; observed {3}",
                            orphans.Count,
                            thresholdTotal,
                            ratio,
                            consecutiveCompleteCatalogRuns);
                        return new List<string>();
                    }
                }
            }
            return orphans;
        }

        private int DeleteOrphans(IEnumerable<string> orphanPaths, string rootPath)
        {
            var removed = 0;
            foreach (var strmFile in orphanPaths)
            {
                try
                {
                    File.Delete(strmFile);
                    removed++;
                    DeleteMatchingNfo(strmFile);
                    PruneOrphanDirectories(Path.GetDirectoryName(strmFile), rootPath);
                }
                catch (Exception ex)
                {
                    _logger.Debug("Failed to delete orphan '{0}': {1}", strmFile, ex.Message);
                }
            }
            if (removed > 0)
                _logger.Info("Removed {0} orphaned STRM files from {1}", removed, rootPath);
            return removed;
        }

        private static void DeleteMatchingNfo(string strmPath)
        {
            var nfoPath = Path.ChangeExtension(strmPath, ".nfo");
            if (File.Exists(nfoPath))
                File.Delete(nfoPath);
        }

        private static void PruneOrphanDirectories(string startDirectory, string rootPath)
        {
            var root = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var directory = startDirectory;

            while (!string.IsNullOrEmpty(directory) &&
                   !string.Equals(
                       Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                       root,
                       StringComparison.OrdinalIgnoreCase) &&
                   Directory.Exists(directory))
            {
                var entries = Directory.GetFileSystemEntries(directory);
                var containsSubdirectory = entries.Any(Directory.Exists);
                var containsNonNfoFile = entries.Any(path =>
                    File.Exists(path) &&
                    !string.Equals(Path.GetExtension(path), ".nfo", StringComparison.OrdinalIgnoreCase));

                // Preserve directories that still contain media, artwork, or child
                // seasons. If only generated NFO metadata remains, remove it.
                if (containsSubdirectory || containsNonNfoFile)
                    break;

                foreach (var nfoPath in entries.Where(File.Exists))
                    File.Delete(nfoPath);

                Directory.Delete(directory);
                directory = Path.GetDirectoryName(directory);
            }
        }

        private int PruneMetadataOnlyDirectories(
            string rootPath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                return 0;

            var root = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootPrefix = root + Path.DirectorySeparatorChar;
            var directories = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);

            // Discover directories without following symbolic links/reparse points.
            // Every candidate must remain below the configured content root.
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parent = pending.Pop();
                string[] children;
                try
                {
                    children = Directory.GetDirectories(parent);
                }
                catch (Exception ex)
                {
                    _logger.Debug(
                        "Metadata cleanup could not enumerate '{0}': {1}",
                        parent,
                        ex.Message);
                    continue;
                }

                foreach (var child in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fullChild = Path.GetFullPath(child)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (!fullChild.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Warn(
                            "Metadata cleanup skipped path outside content root: {0}",
                            fullChild);
                        continue;
                    }

                    try
                    {
                        if ((File.GetAttributes(fullChild) & FileAttributes.ReparsePoint) != 0)
                        {
                            _logger.Debug(
                                "Metadata cleanup skipped symbolic link/reparse point '{0}'",
                                fullChild);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(
                            "Metadata cleanup could not inspect '{0}': {1}",
                            fullChild,
                            ex.Message);
                        continue;
                    }

                    directories.Add(fullChild);
                    pending.Push(fullChild);
                }
            }

            var removedDirectories = 0;
            var removedNfoFiles = 0;
            foreach (var directory in directories.OrderByDescending(path => path.Length))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!Directory.Exists(directory))
                        continue;

                    var entries = Directory.GetFileSystemEntries(directory);
                    var containsAnythingExceptNfo = false;
                    foreach (var path in entries)
                    {
                        if (!File.Exists(path) ||
                            !string.Equals(
                                Path.GetExtension(path),
                                ".nfo",
                                StringComparison.OrdinalIgnoreCase) ||
                            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        {
                            containsAnythingExceptNfo = true;
                            break;
                        }
                    }
                    if (containsAnythingExceptNfo)
                        continue;

                    foreach (var nfoPath in entries)
                    {
                        File.Delete(nfoPath);
                        removedNfoFiles++;
                    }

                    Directory.Delete(directory);
                    removedDirectories++;
                }
                catch (Exception ex)
                {
                    _logger.Debug(
                        "Metadata cleanup could not remove '{0}': {1}",
                        directory,
                        ex.Message);
                }
            }

            if (removedDirectories > 0)
            {
                _logger.Info(
                    "Metadata cleanup removed {0} NFO-only/empty director{1} and {2} generated NFO file(s) from {3}",
                    removedDirectories,
                    removedDirectories == 1 ? "y" : "ies",
                    removedNfoFiles,
                    root);
            }

            return removedDirectories;
        }

        // Stages orphan full paths as relative paths in PendingOrphansJson.
        // Appends to any existing staged orphans (across multiple content-type syncs).
        private static void StagePendingOrphans(PluginConfiguration config, IEnumerable<string> fullPaths)
        {
            var existing = new List<string>();
            if (!string.IsNullOrEmpty(config.PendingOrphansJson))
            {
                try { existing = STJ.JsonSerializer.Deserialize<List<string>>(config.PendingOrphansJson, JsonOptions) ?? new List<string>(); }
                catch { existing = new List<string>(); }
            }
            var root = (config.StrmLibraryPath ?? string.Empty)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
            foreach (var full in fullPaths)
            {
                var rel = !string.IsNullOrEmpty(root) && full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    ? full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    : full;
                set.Add(rel);
            }
            config.PendingOrphansJson = STJ.JsonSerializer.Serialize(new List<string>(set), JsonOptions);
        }

        public List<string> GetPendingOrphans()
        {
            var config = Plugin.InstanceOrNull?.Configuration;
            if (config == null || string.IsNullOrEmpty(config.PendingOrphansJson))
                return new List<string>();
            try { return STJ.JsonSerializer.Deserialize<List<string>>(config.PendingOrphansJson, JsonOptions) ?? new List<string>(); }
            catch { return new List<string>(); }
        }

        public int CommitPendingOrphans()
        {
            var config = Plugin.InstanceOrNull?.Configuration;
            if (config == null) return 0;
            var paths = GetPendingOrphans();
            var root = (config.StrmLibraryPath ?? string.Empty)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var removed = 0;
            foreach (var rel in paths)
            {
                var full = string.IsNullOrEmpty(root)
                    ? rel
                    : Path.Combine(root, rel.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!File.Exists(full)) { continue; }
                try
                {
                    File.Delete(full);
                    removed++;
                    DeleteMatchingNfo(full);

                    // Staged paths are relative to the common STRM root. Stop
                    // pruning at the content root (Movies, TV Shows, etc.).
                    var relative = rel.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var firstSeparator = relative.IndexOfAny(new[]
                    {
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar,
                    });
                    var contentRootName = firstSeparator >= 0
                        ? relative.Substring(0, firstSeparator)
                        : relative;
                    var contentRoot = string.IsNullOrEmpty(root)
                        ? root
                        : Path.Combine(root, contentRootName);
                    PruneOrphanDirectories(Path.GetDirectoryName(full), contentRoot);
                }
                catch (Exception ex)
                {
                    _logger.Debug("Failed to commit orphan deletion '{0}': {1}", full, ex.Message);
                }
            }
            config.PendingOrphansJson = string.Empty;
            Plugin.Instance.SaveConfiguration();
            _logger.Info("Committed deletion of {0} staged orphan(s)", removed);
            if (removed > 0)
                PatchHistoryWithOrphanDeletes(config, paths);
            return removed;
        }

        private void PatchHistoryWithOrphanDeletes(PluginConfiguration config, List<string> deletedPaths)
        {
            int moviesDeleted = 0, docsDeleted = 0, seriesDeleted = 0, docuDeleted = 0;
            var movieRoot  = (config.MovieRootFolderName       ?? string.Empty).Trim().TrimEnd('/', '\\');
            var docRoot    = (config.DocumentaryRootFolderName ?? string.Empty).Trim().TrimEnd('/', '\\');
            var seriesRoot = (config.SeriesRootFolderName      ?? string.Empty).Trim().TrimEnd('/', '\\');
            var docuRoot   = (config.DocuSeriesRootFolderName  ?? string.Empty).Trim().TrimEnd('/', '\\');
            var sep        = Path.DirectorySeparatorChar.ToString();

            foreach (var rel in deletedPaths)
            {
                var norm = rel.TrimStart('/', '\\');
                if (!string.IsNullOrEmpty(movieRoot)  && norm.StartsWith(movieRoot  + sep, StringComparison.OrdinalIgnoreCase)) { moviesDeleted++; continue; }
                if (!string.IsNullOrEmpty(docRoot)    && norm.StartsWith(docRoot    + sep, StringComparison.OrdinalIgnoreCase)) { docsDeleted++;   continue; }
                if (!string.IsNullOrEmpty(seriesRoot) && norm.StartsWith(seriesRoot + sep, StringComparison.OrdinalIgnoreCase)) { seriesDeleted++; continue; }
                if (!string.IsNullOrEmpty(docuRoot)   && norm.StartsWith(docuRoot   + sep, StringComparison.OrdinalIgnoreCase)) { docuDeleted++; }
            }

            if (moviesDeleted == 0 && docsDeleted == 0 && seriesDeleted == 0 && docuDeleted == 0)
                return;

            string newJson;
            lock (_historyLock)
            {
                var history = GetOrLoadHistory();
                bool needMovies = moviesDeleted > 0, needDocs = docsDeleted > 0,
                     needSeries = seriesDeleted > 0, needDocu  = docuDeleted  > 0;

                foreach (var entry in history)
                {
                    if (needMovies && entry.WasMovieSync)        { entry.MoviesDeleted  += moviesDeleted; needMovies = false; }
                    if (needDocs   && entry.WasDocumentarySync)  { entry.MoviesDeleted  += docsDeleted;   needDocs   = false; }
                    if (needSeries && entry.WasSeriesSync)       { entry.EpisodeDeleted += seriesDeleted; needSeries = false; }
                    if (needDocu   && entry.WasDocuSeriesSync)   { entry.EpisodeDeleted += docuDeleted;   needDocu   = false; }
                    if (!needMovies && !needDocs && !needSeries && !needDocu) break;
                }

                newJson = STJ.JsonSerializer.Serialize(_syncHistory, JsonOptions);
            }

            try
            {
                Plugin.Instance.Configuration.SyncHistoryJson = newJson;
                Plugin.Instance.SaveConfiguration();
            }
            catch (Exception ex)
            {
                _logger.Debug("Failed to persist orphan delete counts to history: {0}", ex.Message);
            }
        }

        public void ClearPendingOrphans()
        {
            var config = Plugin.InstanceOrNull?.Configuration;
            if (config == null) return;
            config.PendingOrphansJson = string.Empty;
            Plugin.Instance.SaveConfiguration();
        }
    }
}
