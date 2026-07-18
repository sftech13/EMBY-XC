using System;
using System.Globalization;
using System.IO;
using System.Text;
using Emby.Xtream.Plugin.Client.Models;

namespace Emby.Xtream.Plugin.Service
{
    internal static class NfoWriter
    {
        /// <summary>Writes a Kodi-compatible movie NFO when its generated content changed.</summary>
        public static bool WriteMovieNfo(string nfoPath, string title, string tmdbId, int? year)
        {
            if (string.IsNullOrEmpty(tmdbId)) return false;
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.AppendLine("<movie>");
            sb.AppendFormat("  <title>{0}</title>", EscapeXml(title)).AppendLine();
            if (year.HasValue)
                sb.AppendFormat("  <year>{0}</year>", year.Value).AppendLine();
            sb.AppendFormat("  <uniqueid type=\"tmdb\" default=\"true\">{0}</uniqueid>", tmdbId).AppendLine();
            sb.AppendLine("</movie>");

            return WriteIfChanged(nfoPath, sb.ToString());
        }

        /// <summary>Writes a tvshow.nfo when its generated content changed.</summary>
        public static bool WriteShowNfo(string nfoPath, string title, string tvdbId, string tmdbId)
        {
            if (string.IsNullOrEmpty(tvdbId) && string.IsNullOrEmpty(tmdbId)) return false;
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.AppendLine("<tvshow>");
            sb.AppendFormat("  <title>{0}</title>", EscapeXml(title)).AppendLine();
            if (!string.IsNullOrEmpty(tvdbId))
                sb.AppendFormat("  <uniqueid type=\"tvdb\" default=\"true\">{0}</uniqueid>", tvdbId).AppendLine();
            if (!string.IsNullOrEmpty(tmdbId))
            {
                var defaultAttr = string.IsNullOrEmpty(tvdbId) ? " default=\"true\"" : "";
                sb.AppendFormat("  <uniqueid type=\"tmdb\"{0}>{1}</uniqueid>", defaultAttr, tmdbId).AppendLine();
            }
            sb.AppendLine("</tvshow>");

            return WriteIfChanged(nfoPath, sb.ToString());
        }

        /// <summary>
        /// Writes or patches an episode NFO with stream details from the XC API.
        /// If the file doesn't exist, writes a minimal NFO with streamdetails.
        /// If the file exists with empty &lt;streamdetails /&gt;, patches that tag in-place.
        /// </summary>
        public static bool WriteEpisodeNfo(string nfoPath, string title, int season, int episodeNum, EpisodeMediaInfo info)
        {
            var streamDetailsXml = BuildStreamDetailsXml(info);
            if (streamDetailsXml == null) return false;

            if (File.Exists(nfoPath))
            {
                var content = File.ReadAllText(nfoPath, Encoding.UTF8);
                var emptyDetails = content.IndexOf("<streamdetails />", StringComparison.Ordinal) >= 0 ||
                                   content.IndexOf("<streamdetails/>", StringComparison.Ordinal) >= 0;
                if (emptyDetails)
                {
                    content = content.Replace("<streamdetails />", streamDetailsXml)
                                     .Replace("<streamdetails/>", streamDetailsXml);
                    return WriteIfChanged(nfoPath, content);
                }

                var start = content.IndexOf("<streamdetails", StringComparison.Ordinal);
                var endTag = "</streamdetails>";
                var end = start >= 0
                    ? content.IndexOf(endTag, start, StringComparison.Ordinal)
                    : -1;
                if (start >= 0 && end >= 0)
                {
                    var updated = content.Substring(0, start) + streamDetailsXml +
                                  content.Substring(end + endTag.Length);
                    return WriteIfChanged(nfoPath, updated);
                }
                return false;
            }

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.AppendLine("<episodedetails>");
            sb.AppendLine("  <lockdata>false</lockdata>");
            if (!string.IsNullOrEmpty(title))
                sb.AppendFormat("  <title>{0}</title>", EscapeXml(title)).AppendLine();
            sb.AppendFormat("  <season>{0}</season>", season).AppendLine();
            sb.AppendFormat("  <episode>{0}</episode>", episodeNum).AppendLine();
            sb.AppendLine("  <fileinfo>");
            sb.AppendLine("    " + streamDetailsXml.Replace("\n", "\n    ").TrimEnd());
            sb.AppendLine("  </fileinfo>");
            sb.AppendLine("</episodedetails>");
            File.WriteAllText(nfoPath, sb.ToString(), Encoding.UTF8);
            return true;
        }

        private static bool WriteIfChanged(string path, string content)
        {
            if (File.Exists(path) && string.Equals(File.ReadAllText(path, Encoding.UTF8), content, StringComparison.Ordinal))
                return false;

            File.WriteAllText(path, content, Encoding.UTF8);
            return true;
        }

        private static string BuildStreamDetailsXml(EpisodeMediaInfo info)
        {
            if (info == null) return null;
            var video = info.Video;
            var audio = info.Audio;
            if ((video == null || string.IsNullOrEmpty(video.CodecName)) &&
                (audio == null || string.IsNullOrEmpty(audio.CodecName)))
                return null;

            var sb = new StringBuilder();
            sb.AppendLine("<streamdetails>");

            if (video != null && !string.IsNullOrEmpty(video.CodecName))
            {
                sb.AppendLine("  <video>");
                sb.AppendFormat("    <codec>{0}</codec>", EscapeXml(video.CodecName)).AppendLine();
                sb.AppendFormat("    <micodec>{0}</micodec>", EscapeXml(video.CodecName)).AppendLine();
                if (video.Width.HasValue)
                    sb.AppendFormat("    <width>{0}</width>", video.Width.Value).AppendLine();
                if (video.Height.HasValue)
                    sb.AppendFormat("    <height>{0}</height>", video.Height.Value).AppendLine();
                if (!string.IsNullOrEmpty(video.DisplayAspectRatio))
                {
                    sb.AppendFormat("    <aspect>{0}</aspect>", EscapeXml(video.DisplayAspectRatio)).AppendLine();
                    sb.AppendFormat("    <aspectratio>{0}</aspectratio>", EscapeXml(video.DisplayAspectRatio)).AppendLine();
                }
                var fps = ParseFrameRate(video.RFrameRate);
                if (fps > 0)
                    sb.AppendFormat(CultureInfo.InvariantCulture, "    <framerate>{0:F6}</framerate>", fps).AppendLine();
                if (!string.IsNullOrEmpty(video.FieldOrder))
                    sb.AppendFormat("    <scantype>{0}</scantype>", EscapeXml(video.FieldOrder)).AppendLine();
                sb.AppendLine("    <default>True</default>");
                sb.AppendLine("    <forced>False</forced>");
                if (info.DurationSecs.HasValue)
                {
                    sb.AppendFormat("    <duration>{0}</duration>", info.DurationSecs.Value / 60).AppendLine();
                    sb.AppendFormat("    <durationinseconds>{0}</durationinseconds>", info.DurationSecs.Value).AppendLine();
                }
                sb.AppendLine("  </video>");
            }

            if (audio != null && !string.IsNullOrEmpty(audio.CodecName))
            {
                sb.AppendLine("  <audio>");
                sb.AppendFormat("    <codec>{0}</codec>", EscapeXml(audio.CodecName)).AppendLine();
                sb.AppendFormat("    <micodec>{0}</micodec>", EscapeXml(audio.CodecName)).AppendLine();
                if (!string.IsNullOrEmpty(audio.BitRate))
                    sb.AppendFormat("    <bitrate>{0}</bitrate>", EscapeXml(audio.BitRate)).AppendLine();
                string lang = null;
                if (audio.Tags != null)
                    audio.Tags.TryGetValue("language", out lang);
                if (!string.IsNullOrEmpty(lang))
                    sb.AppendFormat("    <language>{0}</language>", EscapeXml(lang)).AppendLine();
                sb.AppendLine("    <scantype>progressive</scantype>");
                if (audio.Channels.HasValue)
                    sb.AppendFormat("    <channels>{0}</channels>", audio.Channels.Value).AppendLine();
                if (!string.IsNullOrEmpty(audio.SampleRate))
                    sb.AppendFormat("    <samplingrate>{0}</samplingrate>", EscapeXml(audio.SampleRate)).AppendLine();
                sb.AppendLine("    <default>True</default>");
                sb.AppendLine("    <forced>False</forced>");
                sb.AppendLine("  </audio>");
            }

            sb.Append("</streamdetails>");
            return sb.ToString();
        }

        private static double ParseFrameRate(string rFrameRate)
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

        private static string EscapeXml(string s)
            => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
    }
}
