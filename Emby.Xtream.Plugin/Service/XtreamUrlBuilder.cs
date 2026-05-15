using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Shared helpers for building Xtream stream URLs and normalising EPG guide keys.
    /// Centralises logic that was previously duplicated across XtreamTunerHost and
    /// XtreamListingsProvider.
    /// </summary>
    internal static class XtreamUrlBuilder
    {
        private static readonly Regex WhitespaceRegex = new Regex(@"\s+", RegexOptions.Compiled);

        /// <summary>
        /// Builds a live-stream URL from the plugin configuration and a stream ID.
        /// </summary>
        public static string BuildStreamUrl(PluginConfiguration config, int streamId)
        {
            var extension = string.Equals(config.LiveTvOutputFormat, "ts", StringComparison.OrdinalIgnoreCase)
                ? "ts" : "m3u8";
            return string.Format(CultureInfo.InvariantCulture,
                "{0}/live/{1}/{2}/{3}.{4}",
                config.BaseUrl, config.Username, config.Password, streamId, extension);
        }

        /// <summary>
        /// Normalises a guide title or channel name for fuzzy matching:
        /// trims, lower-cases, and collapses all internal whitespace to a single space.
        /// Returns null for null/whitespace input.
        /// </summary>
        public static string NormalizeGuideKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            return WhitespaceRegex.Replace(value.Trim().ToLowerInvariant(), " ");
        }
    }
}
