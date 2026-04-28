using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

        // Tracks streams currently being probed so we don't fire duplicates.
        private static readonly ConcurrentDictionary<int, bool> _inFlight =
            new ConcurrentDictionary<int, bool>();

        private static volatile bool _loaded;
        private static readonly object _loadLock = new object();

        // Cached path to ffprobe binary. null = not searched yet; "" = not found.
        private static string _ffprobePath;

        // Cache entries older than this are treated as stale and re-probed on next tune.
        private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);

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
            if (!_inFlight.TryAdd(streamId, true)) return; // already in flight

            Task.Run(async () =>
            {
                try
                {
                    var info = await ProbeAsync(url, logger).ConfigureAwait(false);
                    if (info != null)
                    {
                        info.CachedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        _cache[streamId] = info;
                        SaveToConfig();
                        logger?.Info("[XtreamProbe] Cached codecs for stream {0}: video={1} audio={2}",
                            streamId, info.VideoCodec ?? "?", info.AudioCodec ?? "?");
                    }
                    else
                    {
                        logger?.Debug("[XtreamProbe] No codec info returned for stream {0}", streamId);
                    }
                }
                catch (Exception ex)
                {
                    logger?.Warn("[XtreamProbe] Probe failed for stream {0}: {1}", streamId, ex.Message);
                }
                finally
                {
                    bool dummy;
                    _inFlight.TryRemove(streamId, out dummy);
                }
            });
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
                _loaded = true;
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
            var ffprobe = FindFfprobe(logger);
            if (string.IsNullOrEmpty(ffprobe))
            {
                logger?.Warn("[XtreamProbe] ffprobe not found — install ffprobe to enable codec auto-detection");
                return null;
            }

            // Short analyzeduration/probesize so we get codec info quickly without
            // buffering the whole stream; 2–3 s is usually enough for MPEG-TS.
            var args = string.Format(
                "-v quiet -print_format json -show_streams" +
                " -analyzeduration 3000000 -probesize 2000000 -i \"{0}\"",
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

            var info = ParseOutput(output);
            if (info != null && !string.IsNullOrEmpty(info.VideoCodec))
            {
                // ATSC A53 Part 4 CC is embedded in H264 SEI NAL units — not visible via
                // -show_streams (reports closed_captions:0). Frame-level probe is required.
                info.HasA53ClosedCaptions = await CheckA53CcAsync(url, ffprobe, logger)
                    .ConfigureAwait(false);
            }
            return info;
        }

        private static async Task<bool> CheckA53CcAsync(string url, string ffprobe, ILogger logger)
        {
            // Read first 30 video frames' side_data to detect ATSC A53 CC.
            var args = string.Format(
                "-v quiet -print_format json -show_frames -select_streams v:0 -read_intervals \"%+#30\"" +
                " -analyzeduration 3000000 -probesize 2000000 -i \"{0}\"",
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

                await Task.WhenAny(readTask, Task.Delay(12000)).ConfigureAwait(false);

                if (!proc.HasExited)
                {
                    try { proc.Kill(); } catch { }
                }

                output = readTask.IsCompleted ? readTask.Result : string.Empty;
            }

            var hasCC = !string.IsNullOrEmpty(output) &&
                        output.IndexOf("ATSC A53 Part 4 Closed Captions",
                            StringComparison.OrdinalIgnoreCase) >= 0;
            if (hasCC)
                logger?.Info("[XtreamProbe] Detected ATSC A53 CC in stream {0}", url);
            return hasCC;
        }

        private static StreamCodecInfo ParseOutput(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                using (var doc = STJ.JsonDocument.Parse(json))
                {
                    STJ.JsonElement streamsEl;
                    if (!doc.RootElement.TryGetProperty("streams", out streamsEl)) return null;

                    string videoCodec = null, audioCodec = null, audioLang = null;
                    int videoWidth = 0, videoHeight = 0, audioChannels = 0;

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
                        }
                        else if (type == "audio" && audioCodec == null)
                        {
                            if (stream.TryGetProperty("codec_name", out el))
                                audioCodec = el.GetString();
                            if (stream.TryGetProperty("channels", out el))
                                audioChannels = el.TryGetInt32(out var ch) ? ch : 0;
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

                    if (videoCodec == null && audioCodec == null) return null;
                    return new StreamCodecInfo
                    {
                        VideoCodec    = videoCodec,
                        VideoWidth    = videoWidth,
                        VideoHeight   = videoHeight,
                        AudioCodec    = audioCodec,
                        AudioChannels = audioChannels,
                        AudioLanguage = audioLang,
                    };
                }
            }
            catch { return null; }
        }

        private static string FindFfprobe(ILogger logger)
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

            // Fall back to PATH by trying to run it.
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
                        p.WaitForExit(3000);
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
        public string VideoCodec  { get; set; }
        public int    VideoWidth  { get; set; }
        public int    VideoHeight { get; set; }

        public string AudioCodec    { get; set; }
        public int    AudioChannels { get; set; }
        public string AudioLanguage { get; set; }

        /// <summary>True when ATSC A53 Part 4 CC was detected in H264 SEI frame side data.</summary>
        public bool HasA53ClosedCaptions { get; set; }

        /// <summary>Unix seconds (UTC) when this entry was probed.</summary>
        public long CachedAt { get; set; }
    }
}
