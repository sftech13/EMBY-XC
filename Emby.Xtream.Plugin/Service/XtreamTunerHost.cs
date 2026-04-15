using System;
using System.Collections.Generic;
using System.Globalization;
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

#pragma warning disable CS0612 // SupportsProbing and AnalyzeDurationMs are obsolete but still functional
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

        public override string Name => "Xtream Tuner";
        public override string Type => TunerType;
        public override bool IsSupported => true;
        public override string SetupUrl => null;
        protected override bool UseTunerHostIdAsPrefix => false;

        public override TunerHostInfo GetDefaultConfiguration()
        {
            return new TunerHostInfo
            {
                Type = Type,
                TunerCount = 1
            };
        }

        public override bool SupportsGuideData(TunerHostInfo tuner)
        {
            return Plugin.Instance.Configuration.EpgSource != EpgSourceMode.Disabled;
        }

        protected override async Task<List<ProgramInfo>> GetProgramsInternal(
            TunerHostInfo tuner, string tunerChannelId,
            DateTimeOffset startDateUtc, DateTimeOffset endDateUtc,
            CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;

            int streamId;
            if (_tunerChannelIdToStreamId.TryGetValue(tunerChannelId, out streamId))
            {
                // Translated station ID → stream ID via mapping
            }
            else if (!int.TryParse(tunerChannelId, NumberStyles.None, CultureInfo.InvariantCulture, out streamId))
            {
                Logger.Warn("GetProgramsInternal: cannot parse tunerChannelId '{0}'", tunerChannelId);
                return new List<ProgramInfo>();
            }

            var liveTvService = Plugin.Instance.LiveTvService;
            List<Client.Models.EpgProgram> programs;
            try
            {
                programs = await liveTvService.FetchEpgForChannelCachedAsync(streamId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warn("GetProgramsInternal: failed to fetch EPG for stream {0}: {1}", streamId, ex.Message);
                programs = new List<EpgProgram>();
            }

            var startUnix = startDateUtc.ToUnixTimeSeconds();
            var endUnix = endDateUtc.ToUnixTimeSeconds();

            const long MinTimestamp = 946684800L;   // 2000-01-01
            const long MaxTimestamp = 4102444800L;  // 2100-01-01

            var result = new List<ProgramInfo>();
            foreach (var p in programs)
            {
                if (p.StopTimestamp <= startUnix || p.StartTimestamp >= endUnix)
                {
                    continue;
                }

                if (p.StartTimestamp < MinTimestamp || p.StartTimestamp > MaxTimestamp
                    || p.StopTimestamp < MinTimestamp || p.StopTimestamp > MaxTimestamp)
                {
                    Logger.Debug("GetProgramsInternal: skipping program with out-of-range timestamps " +
                        "(start={0}, stop={1}) on channel {2}", p.StartTimestamp, p.StopTimestamp, streamId);
                    continue;
                }

                // Skip zero-duration or reversed programs — Emby's GetProgram throws when
                // EndDate <= StartDate, which causes the entire channel to be rejected.
                if (p.StopTimestamp <= p.StartTimestamp)
                {
                    Logger.Warn("GetProgramsInternal: skipping zero-duration or reversed program " +
                        "(start={0}, stop={1}, title='{2}') on channel {3}",
                        p.StartTimestamp, p.StopTimestamp,
                        p.IsPlainText ? (p.Title ?? string.Empty) : "(base64)", streamId);
                    continue;
                }

                var title = p.IsPlainText ? p.Title : LiveTvService.DecodeBase64(p.Title);
                var description = p.IsPlainText ? p.Description : LiveTvService.DecodeBase64(p.Description);
                try
                {
                    result.Add(BuildProgramInfo(p, streamId, tunerChannelId, title, description));
                }
                catch (Exception ex)
                {
                    Logger.Warn("GetProgramsInternal: skipping program on channel {0} " +
                        "(start={1}, stop={2}, title='{3}'): {4}",
                        streamId, p.StartTimestamp, p.StopTimestamp,
                        p.IsPlainText ? p.Title : "(base64)", ex.Message);
                }
            }

            // No EPG data — return a dummy entry spanning the requested window so the channel
            // row stays visible and clickable in the guide (matches M3U tuner behaviour).
            if (result.Count == 0)
            {
                var channelName = _cachedChannels?.Find(c => c.TunerChannelId == tunerChannelId)?.Name;
                if (!string.IsNullOrEmpty(channelName))
                {
                    result.Add(new ProgramInfo
                    {
                        Id = string.Format(CultureInfo.InvariantCulture, "xtream_dummy_{0}_{1}", streamId, startDateUtc.ToUnixTimeSeconds()),
                        ChannelId = tunerChannelId,
                        StartDate = startDateUtc.UtcDateTime,
                        EndDate = endDateUtc.UtcDateTime,
                        Name = channelName,
                        Genres = new List<string>(),
                    });
                    Logger.Debug("GetProgramsInternal: no EPG for channel {0}, returning dummy entry", streamId);
                }
            }

            if (result.Count > 0 && result.Count <= 15)
            {
                // Low program count — log first entry to help diagnose EPG quality issues.
                var first = result[0];
                Logger.Debug("GetProgramsInternal: channel {0} first program: start={1:u}, end={2:u}, name='{3}'",
                    streamId, first.StartDate, first.EndDate, first.Name);
            }

            Logger.Debug("GetProgramsInternal: returning {0} programs for channel {1}", result.Count, streamId);
            return result;
        }

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

            return new ProgramInfo
            {
                Id = string.Format(CultureInfo.InvariantCulture, "xtream_epg_{0}_{1}", streamId, p.StartTimestamp),
                ChannelId = tunerChannelId,
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
                // Always set SeriesId to the show title (even for non-series programs).
                // Emby's "Other Showings" queries WHERE SeriesPresentationUniqueKey = <key>.
                // If key is NULL, Emby uses IS NULL which matches ALL null-key programs, causing
                // completely unrelated shows (SIGN OFF, GLORY Kickboxing) to appear as Other
                // Showings of every news/live program. A non-null title-based key scopes each
                // show to its own airings only.
                SeriesId = !string.IsNullOrEmpty(title) ? title.ToLowerInvariant() : null,
            };
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

                // Only fetch categories if group-title tagging is enabled — saves one API call.
                var categoriesFetch = config.IncludeGroupTitleInM3U
                    ? FetchCategoryMapAsync(liveTvService)
                    : Task.FromResult(new Dictionary<int, string>());

                await Task.WhenAll(channelsFetch, categoriesFetch).ConfigureAwait(false);

                var channels = channelsFetch.Result;
                var categoryMap = categoriesFetch.Result;

                var newIdMap = new Dictionary<string, int>(channels.Count);
                var result = new List<ChannelInfo>(channels.Count);

                foreach (var channel in channels)
                {
                    var cleanName = ChannelNameCleaner.CleanChannelName(
                        channel.Name, config.ChannelRemoveTerms, config.EnableChannelNameCleaning);
                    var streamIdStr = channel.StreamId.ToString(CultureInfo.InvariantCulture);

                    string[] tags = null;
                    if (config.IncludeGroupTitleInM3U
                        && channel.CategoryId.HasValue
                        && categoryMap.TryGetValue(channel.CategoryId.Value, out var groupTitle)
                        && !string.IsNullOrEmpty(groupTitle))
                    {
                        tags = new[] { groupTitle };
                    }

                    newIdMap[streamIdStr] = channel.StreamId;
                    result.Add(new ChannelInfo
                    {
                        Id = CreateEmbyChannelId(tuner, streamIdStr),
                        TunerChannelId = streamIdStr,
                        Name = cleanName,
                        Number = channel.Num.ToString(CultureInfo.InvariantCulture),
                        ImageUrl = string.IsNullOrEmpty(channel.StreamIcon) ? null : channel.StreamIcon,
                        ChannelType = ChannelType.TV,
                        TunerHostId = tuner.Id,
                        Tags = tags,
                    });
                }

                _tunerChannelIdToStreamId = newIdMap;
                _cachedChannels = result;
                _cacheTime = DateTime.UtcNow;
                Logger.Info("Channel cache refreshed: {0} channels", result.Count);
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

        public new void ClearCaches()
        {
            _cachedChannels = null;
            _cacheTime = DateTime.MinValue;
            _tunerChannelIdToStreamId = new Dictionary<string, int>();
            Logger.Info("Xtream tuner caches cleared");
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
            var cached   = StreamProbeService.GetCachedInfo(streamId);
            var streams  = cached != null ? BuildMediaStreamsFromCache(cached) : new List<MediaStream>();
            var hasCached = streams.Count > 0;

            // Direct play: the client connects straight to the IPTV URL — no ffmpeg pipeline,
            // no transcoder startup delay. Falls back to direct-stream/transcode automatically
            // if the client can't handle the format.
            var directPlay = config.EnableLiveTvDirectPlay;

            var mediaSource = new MediaSourceInfo
            {
                Id                   = sourceId,
                Path                 = streamUrl,
                Protocol             = MediaProtocol.Http,
                Container            = "mpegts",
                IsRemote             = true,
                IsInfiniteStream     = true,
                SupportsDirectPlay   = directPlay,
                SupportsDirectStream = true,
                SupportsTranscoding  = true,
                // RequiresOpening/Closing drive the XtreamLiveStream proxy pipeline.
                // Not needed when direct-play is on and the client plays the URL itself.
                RequiresOpening      = !directPlay,
                RequiresClosing      = !directPlay,
                WallClockStart       = DateTime.UtcNow,
                // Cached entries: skip Emby's pre-playback probe entirely — we already
                // have codec data (including DisplayTitle) from our background ffprobe
                // cache, so the OSD info panel will show correct values with zero wait.
                // First tune (no cache): allow a quick 500 ms probe while the background
                // ffprobe runs concurrently to populate the cache for next time.
                SupportsProbing   = !hasCached,
                AnalyzeDurationMs = hasCached ? 0 : 500,
                MediaStreams       = streams,
            };

            if (hasCached)
                mediaSource.DefaultAudioStreamIndex = 1;

            if (!string.IsNullOrEmpty(userAgent))
            {
                mediaSource.RequiredHttpHeaders = new Dictionary<string, string>
                {
                    ["User-Agent"] = userAgent
                };
            }

            // If no cache yet, fire background ffprobe so the next tune will be fast.
            if (!hasCached)
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
