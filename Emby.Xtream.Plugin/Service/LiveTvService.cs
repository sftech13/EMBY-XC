using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client;
using Emby.Xtream.Plugin.Client.Models;
using MediaBrowser.Model.Logging;
using STJ = System.Text.Json;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Service for generating M3U playlists and XMLTV EPG files for Live TV.
    /// </summary>
    public class LiveTvService : IDisposable
    {
        private static readonly STJ.JsonSerializerOptions JsonOptions = new STJ.JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
        };

        private readonly ILogger _logger;
        private readonly SemaphoreSlim _m3uLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _epgLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _xmltvLock = new SemaphoreSlim(1, 1);
        private readonly object _perChannelEpgLock = new object();

        // A failed bulk fetch should be retried soon, but not once per concurrent
        // GetChannels/GetPrograms request. This is intentionally independent of the
        // normal cache TTL, which can be much longer.
        private static readonly TimeSpan XmltvFailureRetryDelay = TimeSpan.FromMinutes(5);

        private Dictionary<int, (List<EpgProgram> Programs, DateTime CacheTime)> _perChannelEpgCache
            = new Dictionary<int, (List<EpgProgram>, DateTime)>();

        // One compact, forward-only XMLTV snapshot is shared by the guide provider and
        // epg.xml endpoint. Never retain the source XML DOM: a large provider feed can
        // expand to many gigabytes as XDocument/XElement objects.
        private volatile XmltvSnapshot _xmltvSnapshot;
        private long _xmltvCacheTimeTicks = DateTime.MinValue.Ticks;
        private volatile bool _xmltvFailed;
        private long _xmltvFailTimeTicks = DateTime.MinValue.Ticks;
        private volatile Dictionary<int, string> _epgChannelIdByStreamId = new Dictionary<int, string>();

        private string _cachedM3U;
        private string _cachedEpgXml;
        private DateTime _m3uCacheTime = DateTime.MinValue;
        private DateTime _epgCacheTime = DateTime.MinValue;
        private bool _disposed;

        public LiveTvService(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Gets the M3U playlist for Live TV channels.
        /// </summary>
        public async Task<string> GetM3UPlaylistAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;

            await _m3uLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_cachedM3U != null && DateTime.UtcNow - _m3uCacheTime < TimeSpan.FromMinutes(config.CacheDurationMinutes))
                {
                    _logger.Debug("Returning cached M3U playlist");
                    return _cachedM3U;
                }

                _logger.Info("Generating M3U playlist");
                var channelsTask = GetFilteredChannelsAsync(cancellationToken);
                var categoriesTask = GetLiveCategoriesAsync(cancellationToken);
                Dictionary<int, string> categoryMap;
                try
                {
                    await Task.WhenAll(channelsTask, categoriesTask).ConfigureAwait(false);
                    categoryMap = categoriesTask.Result.ToDictionary(c => c.CategoryId, c => c.CategoryName);
                }
                catch (Exception ex)
                {
                    _logger.Warn("Failed to fetch live categories for M3U group-title; categories will be omitted: {0}", ex.Message);
                    await channelsTask.ConfigureAwait(false);
                    categoryMap = new Dictionary<int, string>();
                }

                var channels = channelsTask.Result;
                var m3u = GenerateM3U(channels, config, categoryMap);

                _cachedM3U = m3u;
                _m3uCacheTime = DateTime.UtcNow;

                return m3u;
            }
            finally
            {
                _m3uLock.Release();
            }
        }

        /// <summary>
        /// Gets the XMLTV EPG for Live TV channels.
        /// </summary>
        public async Task<string> GetXmltvEpgAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;

            await _epgLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_cachedEpgXml != null && DateTime.UtcNow - _epgCacheTime < TimeSpan.FromMinutes(config.CacheDurationMinutes))
                {
                    _logger.Debug("Returning cached XMLTV EPG");
                    return _cachedEpgXml;
                }

                _logger.Info("Generating XMLTV EPG");
                var channels = await GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);
                var epgXml = await GenerateXmltvAsync(channels, config, cancellationToken).ConfigureAwait(false);

                // A large XML string uses two bytes per character and is already backed by
                // the compact program snapshot. Do not pin another very large copy for the
                // full cache TTL; ordinary-sized endpoint responses still benefit from cache.
                if (epgXml.Length <= 16 * 1024 * 1024)
                {
                    _cachedEpgXml = epgXml;
                    _epgCacheTime = DateTime.UtcNow;
                }
                else
                {
                    _cachedEpgXml = null;
                    _epgCacheTime = DateTime.MinValue;
                    _logger.Info("Generated XMLTV response is {0} MB; skipping string cache to bound memory",
                        epgXml.Length / (1024 * 1024));
                }

                return epgXml;
            }
            finally
            {
                _epgLock.Release();
            }
        }

        /// <summary>
        /// Gets the live TV categories from the Xtream API.
        /// </summary>
        public async Task<List<Category>> GetLiveCategoriesAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action=get_live_categories",
                config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty));

            var httpClient = Plugin.CreateHttpClient();
            var json = await httpClient.GetStringAsync(url).ConfigureAwait(false);
            var categories = STJ.JsonSerializer.Deserialize<List<Category>>(json, JsonOptions)
                ?? new List<Category>();
            return categories.OrderBy(c => c.CategoryName).ToList();
        }

        /// <summary>
        /// Invalidates the M3U and EPG caches.
        /// </summary>
        public void InvalidateCache()
        {
            _cachedM3U = null;
            _cachedEpgXml = null;
            _m3uCacheTime = DateTime.MinValue;
            _epgCacheTime = DateTime.MinValue;
            lock (_perChannelEpgLock)
            {
                _perChannelEpgCache = new Dictionary<int, (List<EpgProgram>, DateTime)>();
            }
            // Keep the last known-good XMLTV snapshot while forcing a refresh. If the
            // provider is temporarily unavailable, Emby can continue using the stale
            // (but still useful) guide instead of reconciling against an empty lineup.
            Interlocked.Exchange(ref _xmltvCacheTimeTicks, DateTime.MinValue.Ticks);
            _xmltvFailed = false;
            Interlocked.Exchange(ref _xmltvFailTimeTicks, DateTime.MinValue.Ticks);
            XtreamListingsProvider.Instance?.InvalidateCache();
            _logger.Info(
                _xmltvSnapshot == null
                    ? "Live TV cache invalidated; no previous XMLTV snapshot is available"
                    : "Live TV cache invalidated; retaining last known-good XMLTV snapshot until refresh succeeds");
        }

        /// <summary>
        /// Gets filtered channels from the Xtream API, applying category filters,
        /// adult filtering, and channel overrides.
        /// </summary>
        internal async Task<List<LiveStreamInfo>> GetFilteredChannelsAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;

            // Always fetch the full channel list so the global `num` field is preserved.
            // Per-category fetches reset `num` to 1 within each category, causing duplicate
            // channel numbers in the guide. Filtering to selected categories is done in-memory.
            var allChannels = await FetchAllChannelsAsync(cancellationToken).ConfigureAwait(false);
            var fetchedCount = allChannels.Count;

            if (config.SelectedLiveCategoryIds != null && config.SelectedLiveCategoryIds.Length > 0)
            {
                var selectedIds = new HashSet<int>(config.SelectedLiveCategoryIds);
                var filteredChannels = allChannels
                    .Where(c => c.CategoryId.HasValue && selectedIds.Contains(c.CategoryId.Value))
                    .ToList();

                // Xtream providers can renumber category IDs over time. If saved filters no longer
                // match any live channels, failing closed makes the entire tuner disappear. Fall
                // back to the full channel list so Live TV still works and the user can refresh
                // categories/reselect filters from the current provider state.
                if (filteredChannels.Count == 0 && fetchedCount > 0)
                {
                    _logger.Warn(
                        "Selected live category filter matched 0 of {0} fetched channels; ignoring stale filter selection",
                        fetchedCount);
                }
                else
                {
                    allChannels = filteredChannels;
                }
            }

            // Filter adult channels
            if (!config.IncludeAdultChannels)
            {
                allChannels = allChannels.Where(c => !c.IsAdultChannel).ToList();
            }

            // Channel hash: detect changes and log accordingly
            var newHash = StrmSyncService.ComputeChannelListHash(allChannels);
            if (newHash != config.LastChannelListHash)
            {
                _logger.Info("Channel list changed (hash {0} → {1}), invalidating cache",
                    string.IsNullOrEmpty(config.LastChannelListHash) ? "(none)" : config.LastChannelListHash.Substring(0, 8),
                    newHash.Substring(0, 8));
                config.LastChannelListHash = newHash;
                Plugin.Instance.SaveConfiguration();
            }
            else
            {
                _logger.Debug("Channel list unchanged (hash {0})", newHash.Substring(0, 8));
            }

            _logger.Info("Fetched {0} Live TV channels", allChannels.Count);
            return allChannels;
        }

        private async Task<List<LiveStreamInfo>> FetchAllChannelsAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action=get_live_streams",
                config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty));

            var httpClient = Plugin.CreateHttpClient(30);
            var json = await httpClient.GetStringAsync(url).ConfigureAwait(false);
            return STJ.JsonSerializer.Deserialize<List<LiveStreamInfo>>(json, JsonOptions)
                ?? new List<LiveStreamInfo>();
        }

        private static string GenerateM3U(
            List<LiveStreamInfo> channels,
            PluginConfiguration config,
            Dictionary<int, string> categoryNames)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#EXTM3U");

            foreach (var channel in channels.OrderBy(c => c.Num))
            {
                var cleanName = ChannelNameCleaner.CleanChannelName(
                    channel.Name,
                    config.ChannelRemoveTerms,
                    config.EnableChannelNameCleaning);

                var epgId = !string.IsNullOrEmpty(channel.EpgChannelId)
                    ? channel.EpgChannelId
                    : channel.StreamId.ToString(CultureInfo.InvariantCulture);

                var extinf = new StringBuilder();
                extinf.Append("#EXTINF:-1");
                extinf.AppendFormat(CultureInfo.InvariantCulture, " tvg-id=\"{0}\"", EscapeAttribute(epgId));
                extinf.AppendFormat(CultureInfo.InvariantCulture, " tvg-name=\"{0}\"", EscapeAttribute(cleanName));
                extinf.AppendFormat(CultureInfo.InvariantCulture, " tvg-chno=\"{0}\"", channel.Num);

                if (!string.IsNullOrEmpty(channel.StreamIcon))
                {
                    extinf.AppendFormat(CultureInfo.InvariantCulture, " tvg-logo=\"{0}\"", EscapeAttribute(channel.StreamIcon));
                }

                if (config.IncludeGroupTitleInM3U
                    && channel.CategoryId.HasValue
                    && categoryNames.TryGetValue(channel.CategoryId.Value, out var groupTitle)
                    && !string.IsNullOrEmpty(groupTitle))
                {
                    extinf.AppendFormat(CultureInfo.InvariantCulture, " group-title=\"{0}\"", EscapeAttribute(groupTitle));
                }

                extinf.AppendFormat(CultureInfo.InvariantCulture, ",{0}", cleanName);

                sb.AppendLine(extinf.ToString());
                sb.AppendLine(BuildStreamUrl(config, channel));
            }

            return sb.ToString();
        }

        internal static string BuildStreamUrl(PluginConfiguration config, LiveStreamInfo channel)
        {
            var extension = string.Equals(config.LiveTvOutputFormat, "ts", StringComparison.OrdinalIgnoreCase) ? "ts" : "m3u8";
            return string.Format(CultureInfo.InvariantCulture,
                "{0}/live/{1}/{2}/{3}.{4}",
                config.BaseUrl, config.Username, config.Password, channel.StreamId, extension);
        }

        private async Task<string> GenerateXmltvAsync(List<LiveStreamInfo> channels, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<tv generator-info-name=\"XC2EMBY\">");

            // Channel definitions
            foreach (var channel in channels.OrderBy(c => c.Num))
            {
                var cleanName = ChannelNameCleaner.CleanChannelName(
                    channel.Name,
                    config.ChannelRemoveTerms,
                    config.EnableChannelNameCleaning);

                var channelId = !string.IsNullOrEmpty(channel.EpgChannelId)
                    ? channel.EpgChannelId
                    : channel.StreamId.ToString(CultureInfo.InvariantCulture);

                sb.AppendFormat(CultureInfo.InvariantCulture, "  <channel id=\"{0}\">\n", EscapeXml(channelId));
                sb.AppendFormat(CultureInfo.InvariantCulture, "    <display-name>{0}</display-name>\n", EscapeXml(cleanName));
                if (!string.IsNullOrEmpty(channel.StreamIcon))
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture, "    <icon src=\"{0}\" />\n", EscapeXml(channel.StreamIcon));
                }
                sb.AppendLine("  </channel>");
            }

            // Fetch EPG data if enabled
            if (config.EpgSource != EpgSourceMode.Disabled)
            {
                var epgData = await FetchEpgDataAsync(channels, config, cancellationToken).ConfigureAwait(false);

                foreach (var program in epgData.OrderBy(p => p.StartTimestamp))
                {
                    var startStr = FormatXmltvTime(program.StartTimestamp);
                    var stopStr = FormatXmltvTime(program.StopTimestamp);
                    var channelId = !string.IsNullOrEmpty(program.ChannelId)
                        ? program.ChannelId
                        : program.EpgId;

                    sb.AppendFormat(CultureInfo.InvariantCulture,
                        "  <programme start=\"{0}\" stop=\"{1}\" channel=\"{2}\">\n",
                        startStr, stopStr, EscapeXml(channelId));
                    var titleText = program.IsPlainText ? program.Title : DecodeBase64(program.Title);
                    sb.AppendFormat(CultureInfo.InvariantCulture,
                        "    <title>{0}</title>\n", EscapeXml(titleText));
                    var desc = program.IsPlainText ? program.Description : DecodeBase64(program.Description);
                    if (!string.IsNullOrEmpty(desc))
                    {
                        sb.AppendFormat(CultureInfo.InvariantCulture,
                            "    <desc>{0}</desc>\n", EscapeXml(desc));
                    }
                    if (!string.IsNullOrEmpty(program.SubTitle))
                        sb.AppendFormat(CultureInfo.InvariantCulture,
                            "    <sub-title>{0}</sub-title>\n", EscapeXml(program.SubTitle));
                    if (program.Categories != null)
                    {
                        foreach (var cat in program.Categories)
                            sb.AppendFormat(CultureInfo.InvariantCulture,
                                "    <category lang=\"en\">{0}</category>\n", EscapeXml(cat));
                    }
                    if (!string.IsNullOrEmpty(program.EpisodeNumOnscreen))
                        sb.AppendFormat(CultureInfo.InvariantCulture,
                            "    <episode-num system=\"onscreen\">{0}</episode-num>\n", EscapeXml(program.EpisodeNumOnscreen));
                    if (!string.IsNullOrEmpty(program.ImageUrl))
                        sb.AppendFormat(CultureInfo.InvariantCulture,
                            "    <icon src=\"{0}\" />\n", EscapeXml(program.ImageUrl));
                    if (!string.IsNullOrEmpty(program.Rating))
                        sb.AppendFormat(CultureInfo.InvariantCulture,
                            "    <rating><value>{0}</value></rating>\n", EscapeXml(program.Rating));
                    if (program.IsLive) sb.AppendLine("    <live />");
                    if (program.IsNew) sb.AppendLine("    <new />");
                    if (program.IsPreviouslyShown) sb.AppendLine("    <previously-shown />");
                    if (program.IsPremiere) sb.AppendLine("    <premiere />");
                    sb.AppendLine("  </programme>");
                }
            }

            sb.AppendLine("</tv>");
            return sb.ToString();
        }

        private async Task<List<EpgProgram>> FetchEpgDataAsync(
            List<LiveStreamInfo> channels,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var allPrograms = new List<EpgProgram>();

            var now = DateTimeOffset.UtcNow;
            var endTime = now.AddDays(config.EpgDaysToFetch);

            List<Task<List<EpgProgram>>> taskList;
            var semaphore = new SemaphoreSlim(5);
            try
            {
                taskList = channels.Select(async channel =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var epgListings = await FetchEpgForChannelAsync(channel.StreamId, cancellationToken).ConfigureAwait(false);

                        if (epgListings == null || epgListings.Listings == null)
                        {
                            return new List<EpgProgram>();
                        }

                        // Warm the per-channel cache so GetProgramsInternal finds hits without re-fetching.
                        lock (_perChannelEpgLock)
                        {
                            _perChannelEpgCache[channel.StreamId] = (epgListings.Listings, DateTime.UtcNow);
                        }

                        var channelId = !string.IsNullOrEmpty(channel.EpgChannelId)
                            ? channel.EpgChannelId
                            : channel.StreamId.ToString(CultureInfo.InvariantCulture);

                        foreach (var program in epgListings.Listings)
                        {
                            if (string.IsNullOrEmpty(program.ChannelId))
                            {
                                program.ChannelId = channelId;
                            }
                        }

                        var nowUnix = now.ToUnixTimeSeconds();
                        var endUnix = endTime.ToUnixTimeSeconds();
                        return epgListings.Listings
                            .Where(p => p.StopTimestamp > nowUnix && p.StartTimestamp < endUnix)
                            .ToList();
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug("Failed to fetch EPG for channel {0}: {1}", channel.StreamId, ex.Message);
                        return new List<EpgProgram>();
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }).ToList();

                var results = await Task.WhenAll(taskList).ConfigureAwait(false);
                foreach (var result in results)
                {
                    allPrograms.AddRange(result);
                }
            }
            finally
            {
                semaphore.Dispose();
            }

            _logger.Info("Fetched {0} EPG programs for {1} channels", allPrograms.Count, channels.Count);
            return allPrograms;
        }

        /// <summary>
        /// Fetches EPG data for a single channel, with per-channel caching.
        /// Tries the bulk XMLTV endpoint first (preserves Live/Repeat/New/Premiere flags);
        /// falls back to per-channel JSON (get_simple_data_table) when XMLTV is unavailable.
        /// </summary>
        internal async Task<List<EpgProgram>> FetchEpgForChannelCachedAsync(int streamId, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            var cacheTtl = TimeSpan.FromMinutes(config.CacheDurationMinutes);

            // 1. Check per-channel cache (fastest path)
            lock (_perChannelEpgLock)
            {
                (List<EpgProgram> Programs, DateTime CacheTime) entry;
                if (_perChannelEpgCache.TryGetValue(streamId, out entry)
                    && DateTime.UtcNow - entry.CacheTime < cacheTtl)
                {
                    return entry.Programs;
                }
            }

            // 2. Try XMLTV bulk cache if available and fresh
            var xmltvCacheFresh = _xmltvSnapshot != null && DateTime.UtcNow - new DateTime(Interlocked.Read(ref _xmltvCacheTimeTicks), DateTimeKind.Utc) < cacheTtl;
            if (xmltvCacheFresh)
            {
                var programs = PopulateFromXmltvCache(streamId);
                if (programs != null) return programs;
            }

            // 3. If XMLTV cache is stale, try fetching it — but briefly throttle retries
            //    after a failure so concurrent callers do not hammer a down server.
            var xmltvRetryAllowed = !_xmltvFailed
                || (DateTime.UtcNow - new DateTime(Interlocked.Read(ref _xmltvFailTimeTicks), DateTimeKind.Utc))
                    >= XmltvFailureRetryDelay;
            if (!xmltvCacheFresh && xmltvRetryAllowed)
            {
                var xmltvOk = await TryFetchXmltvEpgAsync(cancellationToken).ConfigureAwait(false);
                if (xmltvOk)
                {
                    var programs = PopulateFromXmltvCache(streamId);
                    if (programs != null) return programs;
                }
            }

            // A failed refresh must not discard guide rows that were loaded by the
            // previous successful bulk fetch.
            var stalePrograms = PopulateFromXmltvCache(streamId);
            if (stalePrograms != null)
                return stalePrograms;

            // 4. Fall back to per-channel JSON (get_simple_data_table) — Xtream server only.
            //    Custom URL mode does not fall back: if the user's URL failed, return empty so
            //    GetProgramsInternal shows a dummy placeholder rather than silently using a
            //    different source.
            if (Plugin.Instance.Configuration.EpgSource == EpgSourceMode.CustomUrl)
            {
                _logger.Debug("FetchEpgForChannelCachedAsync: custom URL failed, returning empty for stream {0}", streamId);
                return new List<EpgProgram>();
            }

            _logger.Debug("FetchEpgForChannelCachedAsync: using JSON fallback for stream {0}", streamId);
            var epgListings = await FetchEpgForChannelAsync(streamId, cancellationToken).ConfigureAwait(false);
            var jsonPrograms = epgListings?.Listings ?? new List<EpgProgram>();

            lock (_perChannelEpgLock)
            {
                _perChannelEpgCache[streamId] = (jsonPrograms, DateTime.UtcNow);
            }

            return jsonPrograms;
        }

        /// <summary>
        /// Looks up programs for streamId in the XMLTV cache, populates _perChannelEpgCache, and returns them.
        /// Returns null if the channel is not present in the XMLTV data.
        /// </summary>
        private List<EpgProgram> PopulateFromXmltvCache(int streamId)
        {
            string epgChannelId;
            if (!_epgChannelIdByStreamId.TryGetValue(streamId, out epgChannelId))
            {
                epgChannelId = streamId.ToString(CultureInfo.InvariantCulture);
            }

            List<EpgProgram> xmltvPrograms;
            var snapshot = _xmltvSnapshot;
            if (snapshot == null || !snapshot.ProgramsByChannel.TryGetValue(epgChannelId, out xmltvPrograms))
                return null;

            lock (_perChannelEpgLock)
            {
                _perChannelEpgCache[streamId] = (xmltvPrograms, DateTime.UtcNow);
            }

            return xmltvPrograms;
        }

        /// <summary>
        /// Streams the full XMLTV EPG from /xmltv.php into a bounded snapshot.
        /// Builds the stream_id ↔ epg_channel_id mapping from the channel list.
        /// Returns true on success, false if the fetch failed (sets _xmltvFailed).
        /// </summary>
        private async Task<bool> TryFetchXmltvEpgAsync(CancellationToken cancellationToken)
        {
            await _xmltvLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var config = Plugin.Instance.Configuration;
                var cacheTtl = TimeSpan.FromMinutes(config.CacheDurationMinutes);

                // Already fresh?
                if (_xmltvSnapshot != null && DateTime.UtcNow - new DateTime(Interlocked.Read(ref _xmltvCacheTimeTicks), DateTimeKind.Utc) < cacheTtl)
                    return true;

                // Re-check after taking the lock. Several Emby guide calls can reach
                // this method together; without this guard they each retry the same
                // failed endpoint in sequence.
                var lastFailureUtc = new DateTime(
                    Interlocked.Read(ref _xmltvFailTimeTicks),
                    DateTimeKind.Utc);
                if (_xmltvFailed && DateTime.UtcNow - lastFailureUtc < XmltvFailureRetryDelay)
                    return false;

                string url;
                if (config.EpgSource == EpgSourceMode.CustomUrl && !string.IsNullOrWhiteSpace(config.CustomEpgUrl))
                {
                    url = config.CustomEpgUrl;
                    _logger.Info("Fetching bulk XMLTV EPG from custom URL");
                }
                else
                {
                    url = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}/xmltv.php?username={1}&password={2}",
                        config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty));
                    _logger.Info("Fetching bulk XMLTV EPG from {0}/xmltv.php", config.BaseUrl);
                }

                try
                {
                    // Build stream_id → epg_channel_id mapping from the channel list
                    var channels = await GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);
                    var mapping = new Dictionary<int, string>(channels.Count);
                    var includedEpgIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var ch in channels)
                    {
                        var epgId = !string.IsNullOrEmpty(ch.EpgChannelId)
                            ? ch.EpgChannelId
                            : ch.StreamId.ToString(CultureInfo.InvariantCulture);
                        mapping[ch.StreamId] = epgId;
                        includedEpgIds.Add(epgId);

                        // The provider sometimes adds a numeric suffix to JSON epg_channel_id
                        // while omitting it in XMLTV (CBSKCBS.us7 -> CBSKCBS.us).
                        var stripped = epgId.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                        if (!string.IsNullOrEmpty(stripped)) includedEpgIds.Add(stripped);
                    }

                    var now = DateTimeOffset.UtcNow;
                    var filterEndUnix = now.AddDays(config.EpgDaysToFetch).ToUnixTimeSeconds();

                    XmltvSnapshot loadedSnapshot;
                    var epgHttpClient = Plugin.CreateHttpClient(180);
                    using (var stream = await epgHttpClient.GetStreamAsync(url).ConfigureAwait(false))
                    {
                        loadedSnapshot = XmltvParser.Parse(
                            stream,
                            now.ToUnixTimeSeconds(),
                            filterEndUnix,
                            includedEpgIds);
                    }

                    if (loadedSnapshot.DisplayNamesByChannel.Count == 0 || loadedSnapshot.ProgramCount == 0)
                    {
                        throw new InvalidOperationException(
                            "XMLTV response contained no usable channels or programmes");
                    }

                    var actualXmltvIds = new HashSet<string>(
                        loadedSnapshot.DisplayNamesByChannel.Keys,
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var ch in channels)
                    {
                        if (mapping.TryGetValue(ch.StreamId, out var rawId))
                            mapping[ch.StreamId] = XtreamListingsProvider.ResolveToXmltvId(rawId, actualXmltvIds);
                    }

                    _xmltvSnapshot = loadedSnapshot;
                    _epgChannelIdByStreamId = mapping;
                    Interlocked.Exchange(ref _xmltvCacheTimeTicks, DateTime.UtcNow.Ticks);
                    _xmltvFailed = false;

                    _logger.Info(
                        "XMLTV EPG streamed: {0} programs for {1} selected channels ({2} XMLTV channel definitions)",
                        loadedSnapshot.ProgramCount,
                        loadedSnapshot.ProgramsByChannel.Count,
                        loadedSnapshot.DisplayNamesByChannel.Count);
                    return true;
                }
                catch (Exception ex)
                {
                    _xmltvFailed = true;
                    Interlocked.Exchange(ref _xmltvFailTimeTicks, DateTime.UtcNow.Ticks);
                    var isCustom = config.EpgSource == EpgSourceMode.CustomUrl;
                    var hasStaleSnapshot = _xmltvSnapshot != null;
                    _logger.Warn(
                        isCustom
                            ? (hasStaleSnapshot
                                ? "Custom EPG URL fetch failed; serving last known-good guide: {0}"
                                : "Custom EPG URL fetch failed; refusing to return an empty guide: {0}")
                            : (hasStaleSnapshot
                                ? "XMLTV EPG fetch failed; serving last known-good guide: {0}"
                                : "XMLTV EPG fetch failed; no prior snapshot is available: {0}"),
                        ex.Message);
                    return false;
                }
            }
            finally
            {
                _xmltvLock.Release();
            }
        }

        /// <summary>
        /// Returns the compact XMLTV snapshot, fetching if stale. The source stream is parsed
        /// once and discarded so guide refreshes cannot retain a multi-gigabyte XML DOM.
        /// </summary>
        internal async Task<XmltvSnapshot> GetXmltvSnapshotAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            if (config.EpgSource == EpgSourceMode.Disabled)
                return null;

            var fresh = _xmltvSnapshot != null
                && DateTime.UtcNow - new DateTime(Interlocked.Read(ref _xmltvCacheTimeTicks), DateTimeKind.Utc)
                    < TimeSpan.FromMinutes(config.CacheDurationMinutes);

            if (!fresh)
            {
                var retryAllowed = !_xmltvFailed
                    || (DateTime.UtcNow - new DateTime(Interlocked.Read(ref _xmltvFailTimeTicks), DateTimeKind.Utc))
                        >= XmltvFailureRetryDelay;
                if (retryAllowed)
                    await TryFetchXmltvEpgAsync(cancellationToken).ConfigureAwait(false);
            }

            var snapshot = _xmltvSnapshot;
            if (snapshot == null && _xmltvFailed)
            {
                // Returning an empty channel/program list tells Emby that the guide is
                // legitimately empty and can erase previously imported rows. A failed
                // refresh must instead fail closed so Emby retains its existing guide.
                throw new InvalidOperationException(
                    "The XMLTV guide could not be refreshed and no last known-good snapshot is available.");
            }

            return snapshot;
        }

        private async Task<EpgListings> FetchEpgForChannelAsync(int streamId, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action=get_simple_data_table&stream_id={3}",
                config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty), streamId);

            var httpClient = Plugin.CreateHttpClient();
            var json = await httpClient.GetStringAsync(url).ConfigureAwait(false);
            return STJ.JsonSerializer.Deserialize<EpgListings>(json, JsonOptions);
        }

        private static string FormatXmltvTime(long unixTimestamp)
        {
            var dt = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).UtcDateTime;
            return dt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " +0000";
        }

        private static string EscapeXml(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        internal static string DecodeBase64(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch (FormatException)
            {
                return value;
            }
        }

        private static string EscapeAttribute(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\"", "&quot;")
                .Replace("&", "&amp;");
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _m3uLock.Dispose();
                    _epgLock.Dispose();
                    _xmltvLock.Dispose();
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
        }
    }
}
