using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using STJ = System.Text.Json;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Runs ffprobe against a live stream URL in the background on first tune,
    /// caches the detected video/audio codecs per stream ID, and supplies that
    /// info to CreateMediaSourceInfo on all subsequent tunes so Emby can skip
    /// its own probe — the same pattern TiviMate uses to cut channel-switch delay.
    /// </summary>
    internal static class StreamProbeService
    {
        private static ConcurrentDictionary<int, StreamCodecInfo> _cache =
            new ConcurrentDictionary<int, StreamCodecInfo>();

        // Share the actual task, not just an in-flight flag, so a cold tune can wait
        // briefly for the same probe already started by the channel details page.
        private static readonly ConcurrentDictionary<int, Lazy<Task<StreamCodecInfo>>> _inFlight =
            new ConcurrentDictionary<int, Lazy<Task<StreamCodecInfo>>>();

        private static volatile bool _loaded;
        private static readonly object _loadLock = new object();

        // Cached path to ffprobe binary. null = not searched yet; "" = not found.
        private static string _ffprobePath;

        // Cache entries older than this are treated as stale and re-probed on next tune.
        private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);

        // Keep fast startup by using a still-valid cache entry immediately, but refresh
        // actively used channels in the background once per day. This catches provider
        // codec/bitrate changes without blocking playback or probing on every tune.
        private static readonly TimeSpan BackgroundRefreshInterval = TimeSpan.FromDays(1);

        internal const int ProbeSampleSeconds = 3;
        internal const int CurrentProbeVersion = 2;
        internal static readonly TimeSpan FirstTuneProbeWait = TimeSpan.FromSeconds(5);

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns cached codec info for the given stream, or null if not yet probed
        /// or if the cached entry has expired (older than 30 days).
        /// </summary>
        public static StreamCodecInfo GetCachedInfo(int streamId)
        {
            EnsureLoaded();
            StreamCodecInfo info;
            if (!_cache.TryGetValue(streamId, out info)) return null;

            // Treat zero CachedAt (legacy entries) as expired so they get re-probed.
            if (info.CachedAt == 0) return null;

            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(info.CachedAt);
            if (age > CacheTtl) return null;

            return info;
        }

        /// <summary>
        /// Clears all cached codec entries from memory and config.
        /// Next tune to any channel will re-probe from scratch.
        /// </summary>
        public static void ClearCache()
        {
            _cache.Clear();
            SaveToConfig();
        }

        /// <summary>
        /// Fires a background ffprobe task for the stream if one is not already running.
        /// On completion the result is stored in the cache and persisted to plugin config.
        /// </summary>
        public static void StartBackgroundProbe(int streamId, string url, ILogger logger)
        {
            EnsureLoaded();
            StreamCodecInfo existing;
            if (_cache.TryGetValue(streamId, out existing) &&
                !NeedsBackgroundRefresh(existing, DateTimeOffset.UtcNow))
            {
                return;
            }

            // ProbeAndCacheAsync catches probe failures, so this intentionally
            // fire-and-forgets without creating an unobserved faulted task.
            _ = GetOrStartProbeTask(streamId, url, logger);
        }

        /// <summary>
        /// Returns cached metadata immediately, or waits a short bounded interval for
        /// the one shared probe task on a channel's first tune. The probe keeps running
        /// in the background after a caller timeout so the next tune can use its result.
        /// </summary>
        public static async Task<StreamCodecInfo> GetOrProbeAsync(
            int streamId,
            string url,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var cached = GetCachedInfo(streamId);
            if (cached != null && cached.ProbeVersion >= CurrentProbeVersion)
                return cached;

            logger?.Info(
                "[XtreamProbe] No current media probe for stream {0}; waiting up to {1:0.#}s before first tune",
                streamId,
                FirstTuneProbeWait.TotalSeconds);

            var probeTask = GetOrStartProbeTask(streamId, url, logger);
            var result = await AwaitProbeAsync(
                probeTask,
                FirstTuneProbeWait,
                cancellationToken).ConfigureAwait(false);
            if (result == null && !probeTask.IsCompleted)
            {
                logger?.Warn(
                    "[XtreamProbe] First-tune wait expired for stream {0}; using fallback media metadata while the shared probe continues",
                    streamId);
            }

            // Cover the narrow race where the probe populated the cache immediately
            // after the bounded wait completed.
            return result ?? GetCachedInfo(streamId) ?? cached;
        }

        internal static async Task<StreamCodecInfo> AwaitProbeAsync(
            Task<StreamCodecInfo> probeTask,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (probeTask == null)
                return null;

            using (var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var delayTask = Task.Delay(timeout, delayCancellation.Token);
                var completed = await Task.WhenAny(probeTask, delayTask).ConfigureAwait(false);
                if (completed == probeTask)
                {
                    delayCancellation.Cancel();
                    return await probeTask.ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
        }

        private static Task<StreamCodecInfo> GetOrStartProbeTask(
            int streamId,
            string url,
            ILogger logger)
        {
            var lazy = _inFlight.GetOrAdd(
                streamId,
                _ => new Lazy<Task<StreamCodecInfo>>(
                    () => Task.Run(() => ProbeAndCacheAsync(streamId, url, logger)),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            return AwaitAndReleaseProbeAsync(streamId, lazy);
        }

        private static async Task<StreamCodecInfo> AwaitAndReleaseProbeAsync(
            int streamId,
            Lazy<Task<StreamCodecInfo>> lazy)
        {
            try
            {
                return await lazy.Value.ConfigureAwait(false);
            }
            finally
            {
                Lazy<Task<StreamCodecInfo>> current;
                if (_inFlight.TryGetValue(streamId, out current) && ReferenceEquals(current, lazy))
                    _inFlight.TryRemove(streamId, out current);
            }
        }

        private static async Task<StreamCodecInfo> ProbeAndCacheAsync(
            int streamId,
            string url,
            ILogger logger)
        {
            try
            {
                var info = await ProbeAsync(url, logger).ConfigureAwait(false);
                if (info == null)
                {
                    logger?.Debug("[XtreamProbe] No codec info returned for stream {0}", streamId);
                    return null;
                }

                info.ProbeVersion = CurrentProbeVersion;
                info.CachedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _cache[streamId] = info;
                SaveToConfig();
                logger?.Info(
                    "[XtreamProbe] Cached media for stream {0}: video={1} {2}x{3} {4:0.##}fps bitrate={5} audio={6}",
                    streamId,
                    info.VideoCodec ?? "?",
                    info.VideoWidth,
                    info.VideoHeight,
                    info.AverageFrameRate > 0 ? info.AverageFrameRate : info.RealFrameRate,
                    FormatBitRate(info.ContainerBitRate > 0 ? info.ContainerBitRate : info.VideoBitRate),
                    info.AudioCodec ?? "?");
                return info;
            }
            catch (Exception ex)
            {
                logger?.Warn("[XtreamProbe] Probe failed for stream {0}: {1}", streamId, ex.Message);
                return null;
            }
        }

        // ── Config persistence ────────────────────────────────────────────────

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_loadLock)
            {
                if (_loaded) return;
                try
                {
                    var json = Plugin.Instance?.Configuration?.StreamCodecCacheJson;
                    if (!string.IsNullOrEmpty(json))
                    {
                        var dict = STJ.JsonSerializer.Deserialize<Dictionary<string, StreamCodecInfo>>(json);
                        if (dict != null)
                        {
                            var newCache = new ConcurrentDictionary<int, StreamCodecInfo>();
                            foreach (var kv in dict)
                            {
                                int id;
                                if (int.TryParse(kv.Key, out id))
                                    newCache[id] = kv.Value;
                            }
                            _cache = newCache;
                        }
                    }
                }
                catch { }
                finally
                {
                    _loaded = true;
                }
            }
        }

        private static void SaveToConfig()
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) return;

                // Use string keys so JSON is compatible across all STJ versions.
                var dict = new Dictionary<string, StreamCodecInfo>();
                foreach (var kv in _cache)
                    dict[kv.Key.ToString()] = kv.Value;

                config.StreamCodecCacheJson = STJ.JsonSerializer.Serialize(dict);
                Plugin.Instance.SaveConfiguration();
            }
            catch { }
        }

        // ── ffprobe ──────────────────────────────────────────────────────────

        private static async Task<StreamCodecInfo> ProbeAsync(string url, ILogger logger)
        {
            var ffprobe = await FindFfprobeAsync(logger).ConfigureAwait(false);
            if (string.IsNullOrEmpty(ffprobe))
            {
                logger?.Warn("[XtreamProbe] ffprobe not found — install ffprobe to enable codec auto-detection");
                return null;
            }

            // Sample only a few seconds. Packet sizes provide an observed container
            // bitrate when MPEG-TS omits format/stream bit_rate, which is common for
            // live IPTV. This remains a background operation and never delays tuning.
            var args = string.Format(
                CultureInfo.InvariantCulture,
                "-v quiet -print_format json -show_streams -show_format -show_packets" +
                " -show_entries stream=codec_type,codec_name,width,height,color_transfer,channels,avg_frame_rate,r_frame_rate,bit_rate:stream_tags=language:format=bit_rate:packet=size" +
                " -read_intervals %+{0} -analyzeduration 3000000 -probesize 2000000 -i \"{1}\"",
                ProbeSampleSeconds,
                url.Replace("\"", "\\\""));

            string output;
            using (var proc = new Process())
            {
                proc.StartInfo = new ProcessStartInfo
                {
                    FileName               = ffprobe,
                    Arguments              = args,
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };

                proc.Start();
                var readTask = proc.StandardOutput.ReadToEndAsync();

                // Hard 15-second timeout so a dead stream doesn't hang forever.
                await Task.WhenAny(readTask, Task.Delay(15000)).ConfigureAwait(false);

                if (!proc.HasExited)
                {
                    try { proc.Kill(); } catch { }
                }

                output = readTask.IsCompleted ? readTask.Result : string.Empty;
            }

            return ParseOutput(output);
        }

        internal static StreamCodecInfo ParseOutput(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                using (var doc = STJ.JsonDocument.Parse(json))
                {
                    STJ.JsonElement streamsEl;
                    if (!doc.RootElement.TryGetProperty("streams", out streamsEl)) return null;

                    string videoCodec = null, colorTransfer = null, audioCodec = null, audioLang = null;
                    int videoWidth = 0, videoHeight = 0, audioChannels = 0;
                    int videoBitRate = 0, audioBitRate = 0, containerBitRate = 0;
                    float averageFrameRate = 0, realFrameRate = 0;

                    foreach (var stream in streamsEl.EnumerateArray())
                    {
                        STJ.JsonElement typeEl;
                        if (!stream.TryGetProperty("codec_type", out typeEl)) continue;
                        var type = typeEl.GetString();

                        STJ.JsonElement el;
                        if (type == "video" && videoCodec == null)
                        {
                            if (stream.TryGetProperty("codec_name", out el))
                                videoCodec = el.GetString();
                            if (stream.TryGetProperty("width", out el))
                                videoWidth = el.TryGetInt32(out var w) ? w : 0;
                            if (stream.TryGetProperty("height", out el))
                                videoHeight = el.TryGetInt32(out var h) ? h : 0;
                            if (stream.TryGetProperty("color_transfer", out el))
                                colorTransfer = el.GetString();
                            if (stream.TryGetProperty("avg_frame_rate", out el))
                                averageFrameRate = ParseFrameRate(el);
                            if (stream.TryGetProperty("r_frame_rate", out el))
                                realFrameRate = ParseFrameRate(el);
                            if (stream.TryGetProperty("bit_rate", out el))
                                videoBitRate = ParsePositiveInt32(el);
                        }
                        else if (type == "audio" && audioCodec == null)
                        {
                            if (stream.TryGetProperty("codec_name", out el))
                                audioCodec = el.GetString();
                            if (stream.TryGetProperty("channels", out el))
                                audioChannels = el.TryGetInt32(out var ch) ? ch : 0;
                            if (stream.TryGetProperty("bit_rate", out el))
                                audioBitRate = ParsePositiveInt32(el);
                            // Language is nested under tags → language
                            STJ.JsonElement tagsEl;
                            if (stream.TryGetProperty("tags", out tagsEl))
                            {
                                STJ.JsonElement langEl;
                                if (tagsEl.TryGetProperty("language", out langEl))
                                    audioLang = langEl.GetString();
                            }
                        }
                    }

                    STJ.JsonElement formatEl;
                    STJ.JsonElement bitRateEl;
                    if (doc.RootElement.TryGetProperty("format", out formatEl) &&
                        formatEl.TryGetProperty("bit_rate", out bitRateEl))
                    {
                        containerBitRate = ParsePositiveInt32(bitRateEl);
                    }

                    // Live MPEG-TS normally has no declared bitrate. Sum the packet
                    // sizes from the fixed media-time sample to obtain a close observed
                    // rate without downloading or retaining the video payload ourselves.
                    long packetBytes = 0;
                    STJ.JsonElement packetsEl;
                    if (doc.RootElement.TryGetProperty("packets", out packetsEl) &&
                        packetsEl.ValueKind == STJ.JsonValueKind.Array)
                    {
                        foreach (var packet in packetsEl.EnumerateArray())
                        {
                            STJ.JsonElement sizeEl;
                            if (packet.TryGetProperty("size", out sizeEl))
                                packetBytes += ParsePositiveInt64(sizeEl);
                        }
                    }

                    if (containerBitRate <= 0 && packetBytes > 0)
                    {
                        var observed = packetBytes * 8L / ProbeSampleSeconds;
                        if (observed >= 64_000 && observed <= 500_000_000)
                            containerBitRate = (int)observed;
                    }

                    if (videoBitRate <= 0 && containerBitRate > 0)
                    {
                        // Emby needs a video bitrate to choose a sensible transcode
                        // target. Using total minus known audio is conservative and is
                        // much safer than allowing an unknown stream to inherit the
                        // client's full (for example 200 Mbps) ceiling.
                        videoBitRate = Math.Max(1, containerBitRate - Math.Max(0, audioBitRate));
                    }

                    if (videoCodec == null && audioCodec == null) return null;
                    return new StreamCodecInfo
                    {
                        VideoCodec    = videoCodec,
                        ColorTransfer = colorTransfer,
                        VideoWidth    = videoWidth,
                        VideoHeight   = videoHeight,
                        VideoBitRate  = videoBitRate,
                        ContainerBitRate = containerBitRate,
                        AverageFrameRate = averageFrameRate,
                        RealFrameRate = realFrameRate,
                        AudioCodec    = audioCodec,
                        AudioChannels = audioChannels,
                        AudioBitRate  = audioBitRate,
                        AudioLanguage = audioLang,
                    };
                }
            }
            catch { return null; }
        }

        internal static bool NeedsBackgroundRefresh(StreamCodecInfo info, DateTimeOffset now)
        {
            if (info == null || info.CachedAt <= 0 || info.ProbeVersion < CurrentProbeVersion)
                return true;
            var cachedAt = DateTimeOffset.FromUnixTimeSeconds(info.CachedAt);
            return now - cachedAt >= BackgroundRefreshInterval;
        }

        private static int ParsePositiveInt32(STJ.JsonElement value)
        {
            var parsed = ParsePositiveInt64(value);
            return parsed > int.MaxValue ? int.MaxValue : (int)parsed;
        }

        private static long ParsePositiveInt64(STJ.JsonElement value)
        {
            long parsed;
            if (value.ValueKind == STJ.JsonValueKind.Number && value.TryGetInt64(out parsed))
                return Math.Max(0, parsed);
            if (value.ValueKind == STJ.JsonValueKind.String &&
                long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out parsed))
                return Math.Max(0, parsed);
            return 0;
        }

        private static float ParseFrameRate(STJ.JsonElement value)
        {
            var text = value.ValueKind == STJ.JsonValueKind.String
                ? value.GetString()
                : value.GetRawText();
            if (string.IsNullOrWhiteSpace(text) || text == "0/0") return 0;

            var parts = text.Split('/');
            double numerator;
            double denominator = 1;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out numerator))
                return 0;
            if (parts.Length > 1 &&
                (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out denominator) || denominator == 0))
                return 0;

            var fps = numerator / denominator;
            return fps > 0 && fps <= 1000 ? (float)fps : 0;
        }

        private static string FormatBitRate(int bitRate)
        {
            if (bitRate <= 0) return "?";
            return (bitRate / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "Mbps";
        }

        private static string GetVideoRange(string colorTransfer)
        {
            if (string.Equals(colorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase))
                return "HDR";
            if (string.Equals(colorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase))
                return "HLG";
            return "SDR";
        }

        private static async Task<string> FindFfprobeAsync(ILogger logger)
        {
            if (_ffprobePath != null)
                return string.IsNullOrEmpty(_ffprobePath) ? null : _ffprobePath;

            // Check well-known absolute paths first (fastest, no subprocess needed).
            var candidates = new[]
            {
                "/opt/emby-server/bin/ffprobe",   // standard Emby install (deb/rpm)
                "/usr/bin/ffprobe",
                "/usr/local/bin/ffprobe",
                "/usr/lib/emby-server/bin/ffprobe",
                "/usr/share/emby-server/bin/ffprobe",
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    _ffprobePath = path;
                    logger?.Info("[XtreamProbe] Found ffprobe at {0}", path);
                    return path;
                }
            }

            // Fall back to PATH — use async read + timeout so we don't block the thread-pool.
            try
            {
                using (var p = Process.Start(new ProcessStartInfo
                {
                    FileName               = "ffprobe",
                    Arguments              = "-version",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                }))
                {
                    if (p != null)
                    {
                        var readTask = p.StandardOutput.ReadToEndAsync();
                        await Task.WhenAny(readTask, Task.Delay(3000)).ConfigureAwait(false);
                        if (!p.HasExited) { try { p.Kill(); } catch { } }
                        if (p.ExitCode == 0)
                        {
                            _ffprobePath = "ffprobe";
                            logger?.Info("[XtreamProbe] Found ffprobe in PATH");
                            return "ffprobe";
                        }
                    }
                }
            }
            catch { }

            _ffprobePath = string.Empty; // mark not found so we don't search again
            return null;
        }
    }

    internal class StreamCodecInfo
    {
        /// <summary>Cache schema/probe generation used to refresh older entries once.</summary>
        public int ProbeVersion { get; set; }

        public string VideoCodec  { get; set; }
        public string ColorTransfer { get; set; }
        public int    VideoWidth  { get; set; }
        public int    VideoHeight { get; set; }
        public int    VideoBitRate { get; set; }
        public int    ContainerBitRate { get; set; }
        public float  AverageFrameRate { get; set; }
        public float  RealFrameRate { get; set; }

        public string AudioCodec    { get; set; }
        public int    AudioChannels { get; set; }
        public int    AudioBitRate   { get; set; }
        public string AudioLanguage { get; set; }

        /// <summary>Unix seconds (UTC) when this entry was probed.</summary>
        public long CachedAt { get; set; }
    }
}
