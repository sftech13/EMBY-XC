using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client.Models;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using STJ = System.Text.Json;

#pragma warning disable CS0612 // SupportsProbing is obsolete but still functional in Emby 4.8
namespace Emby.Xtream.Plugin.Service
{
    public class XtreamTunerHost : BaseTunerHost
    {
        internal const string TunerType = "xtream-tuner";

        // Channels change rarely — keep the cache warm for 6 hours.
        // Stale cache is served immediately while a background refresh runs,
        // so Emby never blocks on an API call after the first load.
        private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);

        private static readonly STJ.JsonSerializerOptions JsonOptions = new STJ.JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
        };

        private static volatile XtreamTunerHost _instance;

        private readonly IServerApplicationHost _applicationHost;

        private volatile Dictionary<string, int> _tunerChannelIdToStreamId = new Dictionary<string, int>();
        private List<ChannelInfo> _cachedChannels;
        private DateTime _cacheTime = DateTime.MinValue;
        private volatile bool _isRefreshing;
        public int CachedChannelCount => _cachedChannels?.Count ?? 0;

        public XtreamTunerHost(IServerApplicationHost applicationHost)
            : base(applicationHost)
        {
            _instance = this;
            _applicationHost = applicationHost;
        }

        public static XtreamTunerHost Instance => _instance;

        public IServerApplicationHost ApplicationHost => _applicationHost;

        public override string Name => "XC2EMBY";
        public override string Type => TunerType;
        public override bool IsSupported => true;
        public override string SetupUrl => null;
        protected override bool UseTunerHostIdAsPrefix => false;
        protected override string LegacyChannelIdPrefix => string.Empty;

        public override TunerHostInfo GetDefaultConfiguration()
        {
            var count = Math.Max(1, Plugin.InstanceOrNull?.Configuration?.TunerCount ?? 1);
            return new TunerHostInfo
            {
                Type = Type,
                TunerCount = count,
            };
        }

        // EPG is provided entirely by XtreamListingsProvider — no GetProgramsInternal override.
        public override bool SupportsGuideData(TunerHostInfo tuner) => true;

        /// <summary>
        /// Converts a single <see cref="EpgProgram"/> into a <see cref="ProgramInfo"/> ready for
        /// Emby. Extracted as an internal static so it can be unit-tested without Emby DI.
        /// </summary>
        internal static ProgramInfo BuildProgramInfo(
            EpgProgram p, int streamId, string tunerChannelId,
            string title, string description)
        {
            var cats = p.Categories;
            var episodeTitle = string.IsNullOrEmpty(p.SubTitle) ? null : p.SubTitle;

            var isMovie = cats != null && cats.Exists(c =>
                c.IndexOf("movie", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                c.IndexOf("film", System.StringComparison.OrdinalIgnoreCase) >= 0);
            var isSports = cats != null && cats.Exists(c =>
                c.IndexOf("sport", System.StringComparison.OrdinalIgnoreCase) >= 0);

            // Only treat as a series episode when the EPG provides an episode subtitle
            // AND the program is not flagged live. Live events (news, sports) are
            // one-off broadcasts even if they have episode-like subtitles, and marking
            // them IsSeries causes Emby to cross-link unrelated live programs as
            // "Other Airings" of each other.
            var isSeries = !isMovie && !isSports && !p.IsLive && episodeTitle != null;
            var seriesKey = NormalizeGuideKey(title);

            return new ProgramInfo
            {
                Id = string.Format(CultureInfo.InvariantCulture, "xtream_epg_{0}_{1}", streamId, p.StartTimestamp),
                ChannelId = tunerChannelId,
                ShowId = BuildShowId(tunerChannelId, seriesKey),
                StartDate = DateTimeOffset.FromUnixTimeSeconds(p.StartTimestamp).UtcDateTime,
                EndDate = DateTimeOffset.FromUnixTimeSeconds(p.StopTimestamp).UtcDateTime,
                Name = string.IsNullOrEmpty(title) ? "Unknown" : title,
                Overview = string.IsNullOrEmpty(description) ? null : description,
                EpisodeTitle = episodeTitle,
                IsLive = p.IsLive,
                IsRepeat = p.IsPreviouslyShown,
                IsPremiere = p.IsNew || p.IsPremiere,
                ImageUrl = IsValidHttpUrl(p.ImageUrl) ? p.ImageUrl : null,
                Genres = cats ?? new List<string>(),
                IsSports = isSports,
                IsNews = cats != null && cats.Exists(c =>
                    c.IndexOf("news", System.StringComparison.OrdinalIgnoreCase) >= 0),
                IsMovie = isMovie,
                IsKids = cats != null && cats.Exists(c =>
                    c.IndexOf("children", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    c.IndexOf("kids", System.StringComparison.OrdinalIgnoreCase) >= 0),
                IsSeries = isSeries,
                // Keep SeriesId non-null so Emby can build series timers; ShowId above
                // separately feeds the non-series "Other Showings" query.
                SeriesId = seriesKey,
            };
        }

        private static string BuildShowId(string channelId, string showKey)
        {
            if (string.IsNullOrEmpty(showKey))
                showKey = "unknown";

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}::{1}",
                channelId ?? string.Empty,
                showKey);
        }

        private static string NormalizeGuideKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return string.Join(" ", value.Trim().ToLowerInvariant().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        protected override async Task<List<ChannelInfo>> GetChannelsInternal(
            TunerHostInfo tuner, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;

            if (!config.EnableLiveTv)
                return new List<ChannelInfo>();

            var cached = _cachedChannels;
            if (cached != null)
            {
                var age = DateTime.UtcNow - _cacheTime;
                if (age < CacheDuration)
                {
                    // Cache is fresh — return immediately.
                    Logger.Debug("Returning cached channel list ({0} channels, age {1:0}m)", cached.Count, age.TotalMinutes);
                    return cached;
                }

                // Cache is stale — return it immediately so Emby doesn't block,
                // and kick off a background refresh.
                if (!_isRefreshing)
                {
                    Logger.Info("Channel cache stale ({0:0}m old), refreshing in background", age.TotalMinutes);
                    _ = Task.Run(() => RefreshChannelCacheAsync(tuner));
                }
                return cached;
            }

            // No cache at all (first run or after ClearCaches) — must fetch synchronously.
            Logger.Info("Channel cache cold, fetching from Xtream API");
            await RefreshChannelCacheAsync(tuner).ConfigureAwait(false);
            return _cachedChannels ?? new List<ChannelInfo>();
        }

        private async Task RefreshChannelCacheAsync(TunerHostInfo tuner)
        {
            if (_isRefreshing) return;
            _isRefreshing = true;
            try
            {
                var config = Plugin.Instance.Configuration;
                var liveTvService = Plugin.Instance.LiveTvService;

                var channelsFetch = FetchChannelsWithFallbackAsync(liveTvService, config);

                // Always fetch categories so we can set Tags on ChannelInfo.
                // Tags drive Emby's guide-tagids filter — without them the guide is blank
                // for users who have category filters saved from their M3U setup.
                var categoriesFetch = FetchCategoryMapAsync(liveTvService);

                // Pre-load XMLTV channel IDs so we can normalize EpgChannelId values.
                // The JSON API epg_channel_id sometimes has a numeric suffix (e.g. "CBSKCBS.us7")
                // that the XMLTV feed omits ("CBSKCBS.us"). Without this, ListingsChannelId
                // won't match any listing channel and the guide stays blank.
                var xmltvIdsFetch = XtreamListingsProvider.Instance != null
                    ? XtreamListingsProvider.Instance.GetXmltvChannelIdsAsync(CancellationToken.None)
                    : Task.FromResult<HashSet<string>>(null);
                var xmltvAliasesFetch = XtreamListingsProvider.Instance != null
                    ? XtreamListingsProvider.Instance.GetXmltvChannelAliasesAsync(CancellationToken.None)
                    : Task.FromResult(new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase));
                var xmltvProgramCountsFetch = XtreamListingsProvider.Instance != null
                    ? XtreamListingsProvider.Instance.GetXmltvProgramCountsAsync(CancellationToken.None)
                    : Task.FromResult(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

                await Task.WhenAll(channelsFetch, categoriesFetch, xmltvIdsFetch, xmltvAliasesFetch, xmltvProgramCountsFetch).ConfigureAwait(false);

                var channels = channelsFetch.Result;
                var categoryMap = categoriesFetch.Result;
                var xmltvIds = xmltvIdsFetch.Result;
                var xmltvAliases = xmltvAliasesFetch.Result;
                var xmltvProgramCounts = xmltvProgramCountsFetch.Result;
                var listingsProviderId = XtreamServerEntryPoint.Instance?.GetListingsProviderId();

                var excludedCategories = new HashSet<string>(
                    config.ExcludedLiveCategories ?? new System.Collections.Generic.List<string>(),
                    StringComparer.OrdinalIgnoreCase);

                var newIdMap = new Dictionary<string, int>(channels.Count);
                var result = new List<ChannelInfo>(channels.Count);
                var listingsIdUseCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var missingEpgId = 0;
                var missingXmltvId = 0;
                var noPrograms = 0;
                var diagnosticSamples = new List<string>();

                foreach (var channel in channels)
                {
                    var cleanName = ChannelNameCleaner.CleanChannelName(
                        channel.Name, config.ChannelRemoveTerms, config.EnableChannelNameCleaning);
                    var streamIdStr = channel.StreamId.ToString(CultureInfo.InvariantCulture);

                    newIdMap[streamIdStr] = channel.StreamId;

                    // ListingsChannelId must match the XMLTV <channel id="..."> value.
                    // The JSON API epg_channel_id may differ (e.g. "CBSKCBS.us7" vs "CBSKCBS.us"),
                    // so we resolve it against the actual XMLTV channel list, stripping trailing
                    // digits as a fallback.
                    var rawListingsId = !string.IsNullOrWhiteSpace(channel.EpgChannelId)
                        ? channel.EpgChannelId.Trim()
                        : null;
                    var resolvedListingsId = !string.IsNullOrEmpty(rawListingsId)
                        ? XtreamListingsProvider.ResolveToXmltvId(rawListingsId, xmltvIds)
                        : null;

                    if (string.IsNullOrEmpty(rawListingsId))
                    {
                        missingEpgId++;
                        AddGuideDiagnosticSample(diagnosticSamples, cleanName, streamIdStr, null, null, "missing epg_channel_id");
                    }
                    else if (!XmltvIdExists(resolvedListingsId, xmltvIds))
                    {
                        missingXmltvId++;
                        AddGuideDiagnosticSample(diagnosticSamples, cleanName, streamIdStr, rawListingsId, resolvedListingsId, "epg_channel_id not in XMLTV");
                    }

                    var listingsId = XmltvIdExists(resolvedListingsId, xmltvIds)
                        ? ResolveDuplicateListingsId(resolvedListingsId, xmltvIds, listingsIdUseCounts)
                        : null;

                    if (!string.IsNullOrEmpty(listingsId) &&
                        xmltvProgramCounts != null &&
                        (!xmltvProgramCounts.TryGetValue(listingsId, out var programCount) || programCount == 0))
                    {
                        noPrograms++;
                        AddGuideDiagnosticSample(diagnosticSamples, cleanName, streamIdStr, rawListingsId, listingsId, "XMLTV channel has no programmes");
                    }

                    string[] alternateNames = null;
                    if (!string.IsNullOrEmpty(listingsId) &&
                        xmltvAliases != null &&
                        xmltvAliases.TryGetValue(listingsId, out var aliases))
                    {
                        alternateNames = aliases
                            .Where(n => !string.IsNullOrWhiteSpace(n) &&
                                        !string.Equals(n, cleanName, StringComparison.OrdinalIgnoreCase))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                    }

                    // Tags drive Emby's guide-tagids category filter. All categories are
                    // included by default; only skip if the admin has explicitly excluded it.
                    string[] tags = null;
                    if (channel.CategoryId.HasValue &&
                        categoryMap.TryGetValue(channel.CategoryId.Value, out var catName) &&
                        !string.IsNullOrEmpty(catName) &&
                        !excludedCategories.Contains(catName))
                    {
                        tags = new string[] { catName };
                    }

                    result.Add(new ChannelInfo
                    {
                        Id = CreateEmbyChannelId(tuner, streamIdStr),
                        TunerChannelId = streamIdStr,
                        Name = cleanName,
                        Number = channel.Num.ToString(CultureInfo.InvariantCulture),
                        ImageUrl = string.IsNullOrEmpty(channel.StreamIcon) ? null : channel.StreamIcon,
                        ChannelType = ChannelType.TV,
                        TunerHostId = tuner.Id,
                        ListingsProviderId = listingsProviderId,
                        ListingsChannelId = listingsId,
                        ListingsChannelName = cleanName,
                        AlternateNames = alternateNames,
                        Tags = tags,
                    });
                }

                _tunerChannelIdToStreamId = newIdMap;
                _cachedChannels = result;
                _cacheTime = DateTime.UtcNow;
                Logger.Info("Channel cache refreshed: {0} channels", result.Count);
                Logger.Info("Guide mapping diagnostics: missing epg_channel_id={0}, epg_channel_id not in XMLTV={1}, XMLTV channel has no programmes={2}",
                    missingEpgId, missingXmltvId, noPrograms);
                foreach (var sample in diagnosticSamples)
                    Logger.Info("Guide mapping diagnostic sample: {0}", sample);

                WritePersistentChannelCache(tuner, result, config);
            }
            catch (Exception ex)
            {
                Logger.Warn("Channel cache refresh failed: {0}", ex.Message);
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        private static bool XmltvIdExists(string id, HashSet<string> xmltvIds)
        {
            return !string.IsNullOrEmpty(id) && (xmltvIds == null || xmltvIds.Contains(id));
        }

        private static void AddGuideDiagnosticSample(
            List<string> samples,
            string channelName,
            string streamId,
            string epgChannelId,
            string resolvedId,
            string reason)
        {
            if (samples == null || samples.Count >= 25)
                return;

            samples.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} (stream {1}) epg_channel_id={2}, resolved={3}: {4}",
                channelName ?? string.Empty,
                streamId ?? string.Empty,
                epgChannelId ?? "(empty)",
                resolvedId ?? "(none)",
                reason ?? string.Empty));
        }

        private static string ResolveDuplicateListingsId(
            string listingsId,
            HashSet<string> xmltvIds,
            Dictionary<string, int> listingsIdUseCounts)
        {
            if (string.IsNullOrEmpty(listingsId) || listingsIdUseCounts == null)
                return listingsId;

            if (!listingsIdUseCounts.TryGetValue(listingsId, out var useCount))
            {
                listingsIdUseCounts[listingsId] = 1;
                return listingsId;
            }

            listingsIdUseCounts[listingsId] = useCount + 1;
            if (xmltvIds == null)
                return listingsId;

            var nextId = listingsId + (useCount + 1).ToString(CultureInfo.InvariantCulture);
            return xmltvIds.Contains(nextId) ? nextId : listingsId;
        }

        private async Task<List<Client.Models.LiveStreamInfo>> FetchChannelsWithFallbackAsync(
            LiveTvService liveTvService, PluginConfiguration config)
        {
            try
            {
                return await liveTvService.GetFilteredChannelsAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is TaskCanceledException) && !(ex is OperationCanceledException))
            {
                Logger.Warn("LiveTvService channel fetch failed, falling back to direct API: {0}", ex.Message);
                return await FetchAllChannelsDirectAsync(config).ConfigureAwait(false);
            }
        }

        private async Task<Dictionary<int, string>> FetchCategoryMapAsync(LiveTvService liveTvService)
        {
            try
            {
                var cats = await liveTvService.GetLiveCategoriesAsync(CancellationToken.None).ConfigureAwait(false);
                return cats.ToDictionary(c => c.CategoryId, c => c.CategoryName);
            }
            catch (Exception ex)
            {
                Logger.Warn("Failed to fetch live categories: {0}", ex.Message);
                return new Dictionary<int, string>();
            }
        }

        private static async Task<List<Client.Models.LiveStreamInfo>> FetchAllChannelsDirectAsync(PluginConfiguration config)
        {
            using (var httpClient = Plugin.CreateHttpClient(30))
            {
                var url = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/player_api.php?username={1}&password={2}&action=get_live_streams",
                    config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty));

                var json = await httpClient.GetStringAsync(url).ConfigureAwait(false);
                return STJ.JsonSerializer.Deserialize<List<Client.Models.LiveStreamInfo>>(json, JsonOptions)
                    ?? new List<Client.Models.LiveStreamInfo>();
            }
        }

        protected override Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(
            TunerHostInfo tuner, MediaBrowser.Controller.Entities.BaseItem dbChannel,
            ChannelInfo tunerChannel, CancellationToken cancellationToken)
        {
            if (!TryResolveStreamId(tunerChannel, out int streamId))
            {
                return Task.FromResult(new List<MediaSourceInfo>());
            }

            var config = Plugin.Instance.Configuration;
            var streamUrl = BuildStreamUrl(config, streamId);
            var mediaSource = CreateMediaSourceInfo(streamId, streamUrl, config.HttpUserAgent);

            return Task.FromResult(new List<MediaSourceInfo> { mediaSource });
        }

        protected override Task<ILiveStream> GetChannelStream(
            TunerHostInfo tuner, MediaBrowser.Controller.Entities.BaseItem dbChannel,
            ChannelInfo tunerChannel, string mediaSourceId,
            CancellationToken cancellationToken)
        {
            if (!TryResolveStreamId(tunerChannel, out int streamId))
            {
                throw new System.IO.FileNotFoundException(
                    string.Format("Channel {0} not found in Xtream tuner", tunerChannel?.Id));
            }

            var config = Plugin.Instance.Configuration;
            var streamUrl = BuildStreamUrl(config, streamId);
            var mediaSource = CreateMediaSourceInfo(streamId, streamUrl, config.HttpUserAgent);

            var httpClient = Plugin.CreateHttpClient();
            ILiveStream liveStream = new XtreamLiveStream(mediaSource, tuner.Id, httpClient, Logger);

            Logger.Info("Opening live stream for channel {0} (stream {1})",
                tunerChannel?.Name ?? tunerChannel?.Id, streamId);

            return Task.FromResult(liveStream);
        }

        // Writes a persistent channel cache file in Emby's standard tuner cache format.
        // Emby reads this file at startup to find which tuner owns each channel — without
        // it, recording timers that fire before our in-memory cache is loaded get
        // "Tuner not found" because the channel lookup is a synchronous file read.
        private void WritePersistentChannelCache(TunerHostInfo tuner, List<ChannelInfo> channels, PluginConfiguration config)
        {
            try
            {
                if (string.IsNullOrEmpty(tuner?.Id))
                    return;

                var liveTvPath = ResolveEmbyLiveTvDataPath(createIfMissing: true);
                if (string.IsNullOrWhiteSpace(liveTvPath))
                {
                    Logger.Warn("Unable to locate Emby Live TV data path; persistent tuner cache was not written");
                    return;
                }

                var ua = config?.HttpUserAgent ?? string.Empty;
                var headers = string.IsNullOrEmpty(ua)
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { ["User-Agent"] = ua };

                var entries = new System.Text.StringBuilder();
                entries.Append('[');
                var first = true;
                foreach (var c in channels)
                {
                    if (!first) entries.Append(',');
                    first = false;

                    int streamId;
                    int.TryParse(c.TunerChannelId, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out streamId);
                    var streamUrl = streamId > 0 ? BuildStreamUrl(config, streamId) : string.Empty;

                    entries.Append(STJ.JsonSerializer.Serialize(new
                    {
                        TvgId    = c.ListingsChannelId ?? string.Empty,
                        EpgUrls  = new string[0],
                        HttpRequestHeaders = headers,
                        Name     = c.Name ?? string.Empty,
                        AlternateNames = c.AlternateNames ?? new string[0],
                        Number   = c.Number ?? string.Empty,
                        Id       = c.Id ?? string.Empty,
                        Path     = streamUrl,
                        TunerChannelId = c.TunerChannelId ?? string.Empty,
                        TunerHostId    = c.TunerHostId ?? string.Empty,
                        ChannelType = "TV",
                        ImageUrl    = c.ImageUrl ?? string.Empty,
                        Tags        = c.Tags ?? new string[0],
                        EpgShift    = "PT0S",
                    }));
                }
                entries.Append(']');

                var cacheFile = Path.Combine(liveTvPath, "tuner_" + tuner.Id + "_channels");
                var tmp = cacheFile + ".tmp";
                File.WriteAllText(tmp, entries.ToString());
                if (File.Exists(cacheFile)) File.Delete(cacheFile);
                File.Move(tmp, cacheFile);

                Logger.Info("Wrote persistent channel cache: {0} channels → {1}", channels.Count, cacheFile);
            }
            catch (Exception ex)
            {
                Logger.Warn("WritePersistentChannelCache failed: {0}", ex.Message);
            }
        }

        public new void ClearCaches()
        {
            _cachedChannels = null;
            _cacheTime = DateTime.MinValue;
            _tunerChannelIdToStreamId = new Dictionary<string, int>();
            ClearPersistentEmbyChannelCaches();
            Logger.Info("Xtream tuner caches cleared");
        }

        private void ClearPersistentEmbyChannelCaches()
        {
            try
            {
                var liveTvPath = ResolveEmbyLiveTvDataPath(createIfMissing: false);
                if (string.IsNullOrWhiteSpace(liveTvPath))
                    return;

                var tunerIds = XtreamServerEntryPoint.Instance?.GetTunerHostIds();
                if (tunerIds == null || tunerIds.Count == 0)
                    return;

                var deleted = 0;
                foreach (var file in Directory.EnumerateFiles(liveTvPath, "tuner_*_channels"))
                {
                    string contents;
                    try
                    {
                        contents = File.ReadAllText(file);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!tunerIds.Any(id =>
                        !string.IsNullOrEmpty(id) &&
                        contents.IndexOf("\"TunerHostId\":\"" + id + "\"", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        continue;
                    }

                    try
                    {
                        File.Delete(file);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("Failed to delete Emby tuner channel cache {0}: {1}", file, ex.Message);
                    }
                }

                if (deleted > 0)
                    Logger.Info("Deleted {0} Emby tuner channel cache file(s) for XC2EMBY", deleted);
            }
            catch (Exception ex)
            {
                Logger.Warn("Failed to clear persistent Emby tuner channel caches: {0}", ex.Message);
            }
        }

        private string ResolveEmbyLiveTvDataPath(bool createIfMissing)
        {
            var applicationPaths = Plugin.Instance?.ApplicationPaths;
            if (applicationPaths == null)
                return null;

            var candidates = new List<string>();

            AddLiveTvPathCandidate(candidates, applicationPaths.DataPath, "livetv");
            AddLiveTvPathCandidate(candidates, applicationPaths.DataPath, "data", "livetv");

            var programDataPath = GetApplicationPathProperty(applicationPaths, "ProgramDataPath");
            AddLiveTvPathCandidate(candidates, programDataPath, "data", "livetv");
            AddLiveTvPathCandidate(candidates, programDataPath, "livetv");

            var logPath = GetApplicationPathProperty(applicationPaths, "LogDirectoryPath");
            var programDataFromLogPath = string.IsNullOrWhiteSpace(logPath)
                ? null
                : Directory.GetParent(logPath)?.FullName;
            AddLiveTvPathCandidate(candidates, programDataFromLogPath, "data", "livetv");
            AddLiveTvPathCandidate(candidates, programDataFromLogPath, "livetv");

            foreach (var candidate in candidates)
            {
                if (Directory.Exists(candidate))
                    return candidate;
            }

            if (!createIfMissing)
                return null;

            foreach (var candidate in candidates)
            {
                var parent = Path.GetDirectoryName(candidate);
                if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
                    continue;

                Directory.CreateDirectory(candidate);
                return candidate;
            }

            return null;
        }

        private static void AddLiveTvPathCandidate(List<string> candidates, string root, params string[] parts)
        {
            if (string.IsNullOrWhiteSpace(root))
                return;

            var pathParts = new List<string> { root };
            pathParts.AddRange(parts.Where(part => !string.IsNullOrWhiteSpace(part)));
            var path = Path.Combine(pathParts.ToArray());

            if (!candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
                candidates.Add(path);
        }

        private static string GetApplicationPathProperty(object applicationPaths, string propertyName)
        {
            try
            {
                return applicationPaths
                    ?.GetType()
                    .GetProperty(propertyName)
                    ?.GetValue(applicationPaths) as string;
            }
            catch
            {
                return null;
            }
        }

        private bool TryResolveStreamId(ChannelInfo tunerChannel, out int streamId)
        {
            streamId = 0;
            if (tunerChannel == null) return false;

            var id = tunerChannel.TunerChannelId ?? tunerChannel.Id;

            // Check authoritative mapping first (handles station ID → stream ID translation)
            if (_tunerChannelIdToStreamId.TryGetValue(id, out streamId))
                return true;

            // Fallback: parse directly (before channel list is loaded)
            return int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out streamId);
        }

        private string BuildStreamUrl(PluginConfiguration config, int streamId)
        {
            var extension = string.Equals(config.LiveTvOutputFormat, "ts", StringComparison.OrdinalIgnoreCase)
                ? "ts" : "m3u8";
            return string.Format(CultureInfo.InvariantCulture,
                "{0}/live/{1}/{2}/{3}.{4}",
                config.BaseUrl, config.Username, config.Password, streamId, extension);
        }

        private MediaSourceInfo CreateMediaSourceInfo(int streamId, string streamUrl, string userAgent = null)
        {
            var config   = Plugin.Instance.Configuration;
            var sourceId = "xtream_live_" + streamId.ToString(CultureInfo.InvariantCulture);
            var isTsOutput = string.Equals(config.LiveTvOutputFormat, "ts", StringComparison.OrdinalIgnoreCase);
            var cached = StreamProbeService.GetCachedInfo(streamId);
            var streams = cached != null ? BuildMediaStreamsFromCache(cached) : new List<MediaStream>();

            // Direct play: the client connects straight to the IPTV URL — no ffmpeg pipeline,
            // no transcoder startup delay. Falls back to direct-stream/transcode automatically
            // if the client can't handle the format.
            var directPlay = config.EnableLiveTvDirectPlay;

            var mediaSource = new MediaSourceInfo
            {
                Id                   = sourceId,
                Path                 = streamUrl,
                Protocol             = MediaProtocol.Http,
                Container            = isTsOutput ? "mpegts" : "hls",
                IsRemote             = true,
                IsInfiniteStream     = true,
                SupportsDirectPlay   = directPlay,
                SupportsDirectStream = true,
                SupportsTranscoding  = true,
                // RequiresOpening/Closing must always be true so Emby calls GetChannelStream()
                // and has a valid ILiveStream for recording. Direct play still works: Open() is
                // a no-op and the client connects to Path directly when SupportsDirectPlay=true.
                RequiresOpening      = true,
                RequiresClosing      = true,
                WallClockStart       = DateTime.UtcNow,
                // Probing disabled: when Emby probes it hard-codes an AudioStreamIndex in every
                // subsequent HLS segment request. Live TS segments are sometimes audio-less
                // (network glitch, GOP boundary), causing ffmpeg to fail with "Audio stream
                // index '0' not found". Without probe info Emby uses permissive stream mapping
                // that tolerates audio-less segments gracefully.
                SupportsProbing = false,
                MediaStreams = streams,
            };

            if (cached != null && !string.IsNullOrEmpty(cached.AudioCodec))
                mediaSource.DefaultAudioStreamIndex = 1;

            if (!string.IsNullOrEmpty(userAgent))
            {
                mediaSource.RequiredHttpHeaders = new Dictionary<string, string>
                {
                    ["User-Agent"] = userAgent
                };
            }

            // Fire background ffprobe so first tune populates codec metadata for later tunes.
            StreamProbeService.StartBackgroundProbe(streamId, streamUrl, Logger);

            return mediaSource;
        }

        private static List<MediaStream> BuildMediaStreamsFromCache(StreamCodecInfo info)
        {
            var streams = new List<MediaStream>();
            if (!string.IsNullOrEmpty(info.VideoCodec))
            {
                var vs = new MediaStream
                {
                    Type         = MediaStreamType.Video,
                    Codec        = info.VideoCodec,
                    Index        = 0,
                    IsDefault    = true,
                    IsInterlaced = false,
                    PixelFormat  = "yuv420p",
                };
                if (info.VideoWidth  > 0) vs.Width  = info.VideoWidth;
                if (info.VideoHeight > 0) vs.Height = info.VideoHeight;
                // DisplayTitle is what the OSD info panel concatenates directly into HTML —
                // if it is null the JS produces the literal string "undefined" on screen.
                vs.DisplayTitle = BuildVideoDisplayTitle(info.VideoCodec, info.VideoWidth, info.VideoHeight);
                streams.Add(vs);
            }
            if (!string.IsNullOrEmpty(info.AudioCodec))
            {
                var lang = string.IsNullOrEmpty(info.AudioLanguage) ? "und" : info.AudioLanguage;
                var as_ = new MediaStream
                {
                    Type      = MediaStreamType.Audio,
                    Codec     = info.AudioCodec,
                    Index     = 1,
                    IsDefault = true,
                    Language  = lang,
                };
                if (info.AudioChannels > 0) as_.Channels = info.AudioChannels;
                as_.DisplayTitle = BuildAudioDisplayTitle(info.AudioCodec, info.AudioChannels);
                streams.Add(as_);
            }
            return streams;
        }

        /// <summary>
        /// Builds a human-readable display title for a video stream, e.g. "H264 1080p".
        /// Matches the format Emby uses for probed streams so the OSD info panel shows
        /// something meaningful instead of the literal string "undefined".
        /// </summary>
        private static string BuildVideoDisplayTitle(string codec, int width, int height)
        {
            var codecLabel = VideoCodecLabel(codec);
            var res        = VideoResolutionLabel(width, height);
            if (string.IsNullOrEmpty(res)) return codecLabel;
            return codecLabel + " " + res;
        }

        private static string VideoCodecLabel(string codec)
        {
            switch ((codec ?? string.Empty).ToLowerInvariant())
            {
                case "h264":        return "H264";
                case "h265":
                case "hevc":        return "HEVC";
                case "av1":         return "AV1";
                case "vp9":         return "VP9";
                case "vp8":         return "VP8";
                case "mpeg2video":  return "MPEG2";
                case "mpeg4":       return "MPEG4";
                default:            return (codec ?? "Video").ToUpperInvariant();
            }
        }

        private static string VideoResolutionLabel(int width, int height)
        {
            if (width <= 0 && height <= 0) return string.Empty;
            if (width >= 3800 || height >= 2000) return "4K";
            if (width >= 2500 || height >= 1400) return "1440p";
            if (width >= 1800 || height >= 1000) return "1080p";
            if (width >= 1200 || height >= 700)  return "720p";
            if (width >= 700  || height >= 400)  return "480p";
            return "SD";
        }

        /// <summary>
        /// Builds a human-readable display title for an audio stream, e.g. "AC3 5.1".
        /// </summary>
        private static string BuildAudioDisplayTitle(string codec, int channels)
        {
            var codecLabel    = AudioCodecLabel(codec);
            var channelLabel  = AudioChannelLabel(channels);
            if (string.IsNullOrEmpty(channelLabel)) return codecLabel;
            return codecLabel + " " + channelLabel;
        }

        private static string AudioCodecLabel(string codec)
        {
            switch ((codec ?? string.Empty).ToLowerInvariant())
            {
                case "ac3":    return "AC3";
                case "eac3":   return "EAC3";
                case "dts":    return "DTS";
                case "truehd": return "TrueHD";
                case "aac":    return "AAC";
                case "mp3":    return "MP3";
                case "flac":   return "FLAC";
                case "opus":   return "Opus";
                case "vorbis": return "Vorbis";
                default:       return (codec ?? "Audio").ToUpperInvariant();
            }
        }

        private static string AudioChannelLabel(int channels)
        {
            switch (channels)
            {
                case 1:  return "Mono";
                case 2:  return "Stereo";
                case 6:  return "5.1";
                case 8:  return "7.1";
                default: return channels > 0
                    ? channels.ToString(System.Globalization.CultureInfo.InvariantCulture) + "ch"
                    : string.Empty;
            }
        }

        private static bool IsValidHttpUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }
    }
}
