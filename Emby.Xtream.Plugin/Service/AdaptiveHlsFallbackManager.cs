using System;
using System.Collections.Generic;
using System.Globalization;
using MediaBrowser.Model.Logging;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Detects repeated short Live TV sessions from one device and temporarily changes
    /// only the affected Xtream channel from MPEG-TS to provider HLS. State is deliberately
    /// in memory: overrides expire automatically and an Emby restart returns every channel
    /// to the configured default.
    /// </summary>
    internal sealed class AdaptiveHlsFallbackManager
    {
        internal const int QualifyingStopThreshold = 2;
        internal static readonly TimeSpan DetectionWindow = TimeSpan.FromMinutes(30);
        internal static readonly TimeSpan MinimumSessionLength = TimeSpan.FromSeconds(45);
        internal static readonly TimeSpan MaximumSessionLength = TimeSpan.FromMinutes(30);
        internal const int UpstreamStallThreshold = 3;
        internal static readonly TimeSpan UpstreamStallWindow = TimeSpan.FromMinutes(5);

        private static readonly AdaptiveHlsFallbackManager _instance =
            new AdaptiveHlsFallbackManager();

        private readonly object _sync = new object();
        private readonly Dictionary<string, PlaybackStart> _activePlaybacks =
            new Dictionary<string, PlaybackStart>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<DateTime>> _qualifyingStops =
            new Dictionary<string, List<DateTime>>(StringComparer.Ordinal);
        private readonly Dictionary<int, DateTime> _hlsOverrides =
            new Dictionary<int, DateTime>();
        private readonly Dictionary<int, List<DateTime>> _upstreamStalls =
            new Dictionary<int, List<DateTime>>();

        public static AdaptiveHlsFallbackManager Instance => _instance;

        internal bool RecordUpstreamReadStall(
            string mediaSourceId, int activeViewers, PluginConfiguration config,
            ILogger logger, DateTime utcNow)
        {
            int streamId;
            if (activeViewers <= 0 || config == null || !config.EnableAdaptiveHlsFallback ||
                !IsMpegTs(config.LiveTvOutputFormat) ||
                !TryParseStreamId(mediaSourceId, out streamId) ||
                mediaSourceId.EndsWith("_adaptive_hls", StringComparison.OrdinalIgnoreCase))
                return false;

            var now = EnsureUtc(utcNow);
            DateTime overrideUntil;
            lock (_sync)
            {
                // Prune all channel evidence so occasional tunes cannot accumulate
                // an unbounded list of inactive stream IDs over server uptime.
                var empty = new List<int>();
                foreach (var pair in _upstreamStalls)
                {
                    pair.Value.RemoveAll(value => value > now || now - value > UpstreamStallWindow);
                    if (pair.Value.Count == 0) empty.Add(pair.Key);
                }
                foreach (var id in empty) _upstreamStalls.Remove(id);

                DateTime existing;
                if (_hlsOverrides.TryGetValue(streamId, out existing) && existing > now)
                    return false;
                List<DateTime> stalls;
                if (!_upstreamStalls.TryGetValue(streamId, out stalls))
                    _upstreamStalls[streamId] = stalls = new List<DateTime>();
                stalls.Add(now);
                if (stalls.Count < UpstreamStallThreshold)
                    return false;
                overrideUntil = now.AddMinutes(Clamp(config.AdaptiveHlsOverrideMinutes, 5, 1440));
                _hlsOverrides[streamId] = overrideUntil;
                _upstreamStalls.Remove(streamId);
            }

            logger?.Warn(
                "[adaptive-hls] Stream {0} had {1} confirmed upstream read stalls within {2} minutes; using HLS on the next tune until {3:u}",
                streamId, UpstreamStallThreshold, (int)UpstreamStallWindow.TotalMinutes, overrideUntil);
            return true;
        }

        internal void RecordPlaybackStarted(
            string deviceId,
            string playSessionId,
            string mediaSourceId,
            bool isAutomated,
            DateTime utcNow)
        {
            int streamId;
            if (isAutomated || !TryParseStreamId(mediaSourceId, out streamId))
                return;

            var normalizedDeviceId = NormalizeDeviceId(deviceId);
            var playbackKey = BuildPlaybackKey(normalizedDeviceId, playSessionId, streamId);
            lock (_sync)
            {
                PrunePlaybackEvidenceLocked(EnsureUtc(utcNow));
                _activePlaybacks[playbackKey] = new PlaybackStart
                {
                    DeviceId = normalizedDeviceId,
                    StreamId = streamId,
                    StartedUtc = EnsureUtc(utcNow),
                };
            }
        }

        internal bool RecordPlaybackStopped(
            string deviceId,
            string playSessionId,
            string mediaSourceId,
            bool isAutomated,
            PluginConfiguration config,
            ILogger logger,
            DateTime utcNow)
        {
            int streamId;
            if (isAutomated || !TryParseStreamId(mediaSourceId, out streamId))
            {
                return false;
            }

            var now = EnsureUtc(utcNow);
            var normalizedDeviceId = NormalizeDeviceId(deviceId);
            var playbackKey = BuildPlaybackKey(normalizedDeviceId, playSessionId, streamId);
            DateTime overrideUntil = DateTime.MinValue;

            lock (_sync)
            {
                PrunePlaybackEvidenceLocked(now);
                PlaybackStart started;
                if (!_activePlaybacks.TryGetValue(playbackKey, out started))
                    return false;

                _activePlaybacks.Remove(playbackKey);
                if (started.StreamId != streamId)
                    return false;
                if (config == null || !config.EnableAdaptiveHlsFallback ||
                    !IsMpegTs(config.LiveTvOutputFormat))
                {
                    return false;
                }

                var duration = now - started.StartedUtc;
                if (duration < MinimumSessionLength || duration > MaximumSessionLength)
                    return false;

                var stopKey = started.DeviceId + "\n" + streamId.ToString(CultureInfo.InvariantCulture);
                List<DateTime> stops;
                if (!_qualifyingStops.TryGetValue(stopKey, out stops))
                {
                    stops = new List<DateTime>();
                    _qualifyingStops[stopKey] = stops;
                }

                stops.RemoveAll(value => now - value > DetectionWindow || value > now);
                stops.Add(now);
                if (stops.Count < QualifyingStopThreshold)
                    return false;

                var overrideMinutes = Clamp(config.AdaptiveHlsOverrideMinutes, 5, 1440);
                overrideUntil = now.AddMinutes(overrideMinutes);
                _hlsOverrides[streamId] = overrideUntil;
                stops.Clear();
            }

            logger?.Warn(
                "[adaptive-hls] Stream {0} stopped {1} times on one device within {2} minutes; using HLS until {3:u}",
                streamId,
                QualifyingStopThreshold,
                (int)DetectionWindow.TotalMinutes,
                overrideUntil);
            return true;
        }

        internal string GetEffectiveOutputFormat(
            int streamId,
            PluginConfiguration config,
            ILogger logger,
            DateTime utcNow,
            out bool adaptiveOverride)
        {
            adaptiveOverride = false;
            var configuredFormat = NormalizeOutputFormat(config?.LiveTvOutputFormat);
            if (config == null || !config.EnableAdaptiveHlsFallback || !IsMpegTs(configuredFormat))
                return configuredFormat;

            var now = EnsureUtc(utcNow);
            var expired = false;
            lock (_sync)
            {
                DateTime until;
                if (!_hlsOverrides.TryGetValue(streamId, out until))
                    return configuredFormat;

                if (until <= now)
                {
                    _hlsOverrides.Remove(streamId);
                    expired = true;
                }
                else
                {
                    adaptiveOverride = true;
                    return "m3u8";
                }
            }

            if (expired)
                logger?.Info("[adaptive-hls] HLS override expired for stream {0}; returning to MPEG-TS", streamId);
            return configuredFormat;
        }

        internal static bool TryParseStreamId(string mediaSourceId, out int streamId)
        {
            streamId = 0;
            const string prefix = "xtream_live_";
            if (string.IsNullOrWhiteSpace(mediaSourceId) ||
                !mediaSourceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var start = prefix.Length;
            var end = start;
            while (end < mediaSourceId.Length && char.IsDigit(mediaSourceId[end]))
                end++;

            return end > start && int.TryParse(
                mediaSourceId.Substring(start, end - start),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out streamId) && streamId > 0;
        }

        internal void ResetForTests()
        {
            lock (_sync)
            {
                _activePlaybacks.Clear();
                _qualifyingStops.Clear();
                _hlsOverrides.Clear();
                _upstreamStalls.Clear();
            }
        }

        private static string BuildPlaybackKey(string deviceId, string playSessionId, int streamId)
        {
            if (!string.IsNullOrWhiteSpace(playSessionId))
                return "session:" + playSessionId;
            return "device:" + deviceId + ":stream:" + streamId.ToString(CultureInfo.InvariantCulture);
        }

        private static string NormalizeDeviceId(string deviceId)
        {
            return string.IsNullOrWhiteSpace(deviceId) ? "unknown-device" : deviceId.Trim();
        }

        private static string NormalizeOutputFormat(string value)
        {
            return IsMpegTs(value) ? "ts" : "m3u8";
        }

        private static bool IsMpegTs(string value)
        {
            return string.Equals(value, "ts", StringComparison.OrdinalIgnoreCase);
        }

        private static DateTime EnsureUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
                return value;
            return value.ToUniversalTime();
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            if (value < minimum) return minimum;
            if (value > maximum) return maximum;
            return value;
        }

        private void PrunePlaybackEvidenceLocked(DateTime now)
        {
            var oldestActive = now - MaximumSessionLength - DetectionWindow;
            var stalePlaybackKeys = new List<string>();
            foreach (var pair in _activePlaybacks)
            {
                if (pair.Value.StartedUtc < oldestActive || pair.Value.StartedUtc > now)
                    stalePlaybackKeys.Add(pair.Key);
            }
            foreach (var key in stalePlaybackKeys)
                _activePlaybacks.Remove(key);

            var emptyStopKeys = new List<string>();
            foreach (var pair in _qualifyingStops)
            {
                pair.Value.RemoveAll(value => now - value > DetectionWindow || value > now);
                if (pair.Value.Count == 0)
                    emptyStopKeys.Add(pair.Key);
            }
            foreach (var key in emptyStopKeys)
                _qualifyingStops.Remove(key);
        }

        private sealed class PlaybackStart
        {
            public string DeviceId { get; set; }
            public int StreamId { get; set; }
            public DateTime StartedUtc { get; set; }
        }
    }
}
