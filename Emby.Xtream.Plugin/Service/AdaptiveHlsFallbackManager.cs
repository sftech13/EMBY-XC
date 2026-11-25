using System;
using System.Collections.Generic;
using System.Globalization;
using MediaBrowser.Model.Logging;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Optional provider-direct HLS fallback, driven only by confirmed upstream read
    /// stalls. Shared MPEG-TS relay mode never silently changes transport. Playback
    /// stops are not evidence of stream failure and are deliberately not monitored.
    /// </summary>
    internal sealed class AdaptiveHlsFallbackManager
    {
        internal const int UpstreamStallThreshold = 3;
        internal static readonly TimeSpan UpstreamStallWindow = TimeSpan.FromMinutes(5);

        private static readonly AdaptiveHlsFallbackManager _instance =
            new AdaptiveHlsFallbackManager();

        private readonly object _sync = new object();
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
            if (activeViewers <= 0 || config == null || config.EnableSharedLiveTvRelay || !config.EnableAdaptiveHlsFallback ||
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

        internal string GetEffectiveOutputFormat(
            int streamId,
            PluginConfiguration config,
            ILogger logger,
            DateTime utcNow,
            out bool adaptiveOverride)
        {
            adaptiveOverride = false;
            var configuredFormat = NormalizeOutputFormat(config?.LiveTvOutputFormat);
            if (config == null || config.EnableSharedLiveTvRelay || !config.EnableAdaptiveHlsFallback || !IsMpegTs(configuredFormat))
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
                _hlsOverrides.Clear();
                _upstreamStalls.Clear();
            }
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

    }
}
