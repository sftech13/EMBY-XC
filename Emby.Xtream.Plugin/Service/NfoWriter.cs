using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Emby.Xtream.Plugin.Client.Models;

namespace Emby.Xtream.Plugin.Service
{
    internal static class NfoWriter
    {
        public static bool WriteMovieNfo(
            string nfoPath,
            string title,
            VodDetailInfo detail,
            int? titleYear)
        {
            detail = detail ?? new VodDetailInfo();
            var displayTitle = First(title, detail.Name);
            if (string.IsNullOrWhiteSpace(displayTitle)) return false;

            var sb = BeginDocument("movie");
            AppendElement(sb, "title", displayTitle);
            var year = titleYear ?? ParseYear(detail.ReleaseDate);
            if (year.HasValue) AppendElement(sb, "year", year.Value.ToString(CultureInfo.InvariantCulture));
            AppendElement(sb, "premiered", NormalizeDate(detail.ReleaseDate));
            AppendElement(sb, "plot", detail.Plot);
            AppendElement(sb, "runtime", GetRuntimeMinutes(detail.DurationSecs, detail.Duration));
            AppendElement(sb, "rating", detail.Rating);
            AppendUniqueId(sb, "tmdb", detail.TmdbId, true);
            AppendUniqueId(sb, "imdb", detail.ImdbId, string.IsNullOrWhiteSpace(detail.TmdbId));
            AppendList(sb, "genre", detail.Genre);
            AppendList(sb, "director", detail.Director);
            AppendActors(sb, detail.Cast);
            AppendElement(sb, "thumb", detail.ImageUrl);
            AppendFanart(sb, detail.BackdropUrl);
            AppendElement(sb, "trailer", detail.Trailer);
            AppendFileInfo(sb, detail.MediaInfo);
            EndDocument(sb, "movie");
            return WriteIfChanged(nfoPath, sb.ToString());
        }

        // Retained for failed-item retry paths and compatibility with older callers.
        public static bool WriteMovieNfo(string nfoPath, string title, string tmdbId, int? year)
            => WriteMovieNfo(nfoPath, title, new VodDetailInfo { TmdbId = tmdbId }, year);

        public static bool WriteShowNfo(
            string nfoPath,
            string title,
            SeriesInfo info,
            string tvdbId,
            string tmdbId)
        {
            info = info ?? new SeriesInfo();
            var displayTitle = First(title, info.Name);
            if (string.IsNullOrWhiteSpace(displayTitle)) return false;

            tvdbId = First(tvdbId, info.TvdbId, info.TvdbIdAlt);
            tmdbId = First(tmdbId, info.TmdbId, info.TmdbIdAlt);
            var imdbId = First(info.ImdbId, info.ImdbIdAlt);
            var releaseDate = First(info.ReleaseDate, info.ReleaseDateAlt);

            var sb = BeginDocument("tvshow");
            AppendElement(sb, "title", displayTitle);
            var year = ParseYear(First(info.Year, releaseDate));
            if (year.HasValue) AppendElement(sb, "year", year.Value.ToString(CultureInfo.InvariantCulture));
            AppendElement(sb, "premiered", NormalizeDate(releaseDate));
            AppendElement(sb, "plot", info.Plot);
            AppendElement(sb, "runtime", NormalizeRuntime(info.EpisodeRunTime));
            AppendElement(sb, "rating", info.Rating);
            AppendUniqueId(sb, "tvdb", tvdbId, true);
            AppendUniqueId(sb, "tmdb", tmdbId, string.IsNullOrWhiteSpace(tvdbId));
            AppendUniqueId(sb, "imdb", imdbId, string.IsNullOrWhiteSpace(tvdbId) && string.IsNullOrWhiteSpace(tmdbId));
            AppendList(sb, "genre", info.Genre);
            AppendList(sb, "director", info.Director);
            AppendActors(sb, info.Cast);
            AppendElement(sb, "thumb", info.Cover);
            AppendElement(sb, "trailer", info.YoutubeTrailer);
            EndDocument(sb, "tvshow");
            return WriteIfChanged(nfoPath, sb.ToString());
        }

        public static bool WriteShowNfo(string nfoPath, string title, string tvdbId, string tmdbId)
            => WriteShowNfo(nfoPath, title, null, tvdbId, tmdbId);

        public static bool WriteEpisodeNfo(
            string nfoPath,
            string title,
            int season,
            int episodeNum,
            EpisodeInfo episode)
        {
            episode = episode ?? new EpisodeInfo();
            var info = episode.Info;
            var displayTitle = First(title, episode.Title);
            var plot = First(info?.Plot, episode.Plot);
            var releaseDate = info?.ReleaseDate;
            var duration = First(info?.Duration, episode.Duration);
            var rating = First(info?.Rating, episode.Rating);

            var sb = BeginDocument("episodedetails");
            sb.AppendLine("  <lockdata>false</lockdata>");
            AppendElement(sb, "title", displayTitle);
            AppendElement(sb, "season", season.ToString(CultureInfo.InvariantCulture));
            AppendElement(sb, "episode", episodeNum.ToString(CultureInfo.InvariantCulture));
            AppendElement(sb, "aired", NormalizeDate(releaseDate));
            AppendElement(sb, "plot", plot);
            AppendElement(sb, "runtime", GetRuntimeMinutes(info?.DurationSecs, duration));
            AppendElement(sb, "rating", rating);
            AppendList(sb, "director", info?.Director);
            AppendActors(sb, info?.Cast);
            AppendElement(sb, "thumb", info?.MovieImage);
            AppendElement(sb, "trailer", info?.YoutubeTrailer);
            AppendFileInfo(sb, info);
            EndDocument(sb, "episodedetails");
            return WriteIfChanged(nfoPath, sb.ToString());
        }

        // Compatibility overload used by older retry/test callers.
        public static bool WriteEpisodeNfo(
            string nfoPath,
            string title,
            int season,
            int episodeNum,
            EpisodeMediaInfo info)
            => WriteEpisodeNfo(
                nfoPath,
                title,
                season,
                episodeNum,
                new EpisodeInfo { Season = season, EpisodeNum = episodeNum, Title = title, Info = info });

        private static StringBuilder BeginDocument(string root)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append('<').Append(root).AppendLine(">");
            return sb;
        }

        private static void EndDocument(StringBuilder sb, string root)
            => sb.Append("</").Append(root).AppendLine(">");

        private static void AppendElement(StringBuilder sb, string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            sb.Append("  <").Append(name).Append('>')
                .Append(EscapeXml(value.Trim()))
                .Append("</").Append(name).AppendLine(">");
        }

        private static void AppendUniqueId(StringBuilder sb, string type, string value, bool isDefault)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            sb.Append("  <uniqueid type=\"").Append(EscapeXml(type)).Append('"');
            if (isDefault) sb.Append(" default=\"true\"");
            sb.Append('>').Append(EscapeXml(value.Trim())).AppendLine("</uniqueid>");
        }

        private static void AppendList(StringBuilder sb, string element, string values)
        {
            if (string.IsNullOrWhiteSpace(values)) return;
            foreach (var value in SplitValues(values))
                AppendElement(sb, element, value);
        }

        private static void AppendActors(StringBuilder sb, string cast)
        {
            if (string.IsNullOrWhiteSpace(cast)) return;
            foreach (var actor in SplitValues(cast))
            {
                sb.AppendLine("  <actor>");
                sb.Append("    <name>").Append(EscapeXml(actor)).AppendLine("</name>");
                sb.AppendLine("  </actor>");
            }
        }

        private static IEnumerable<string> SplitValues(string values)
            => (values ?? string.Empty)
                .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);

        private static void AppendFanart(StringBuilder sb, string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            sb.AppendLine("  <fanart>");
            sb.Append("    <thumb>").Append(EscapeXml(url.Trim())).AppendLine("</thumb>");
            sb.AppendLine("  </fanart>");
        }

        private static void AppendFileInfo(StringBuilder sb, EpisodeMediaInfo info)
        {
            var streamDetails = BuildStreamDetailsXml(info);
            if (streamDetails == null) return;
            sb.AppendLine("  <fileinfo>");
            foreach (var line in streamDetails.Split('\n'))
                sb.Append("    ").AppendLine(line);
            sb.AppendLine("  </fileinfo>");
        }

        private static string BuildStreamDetailsXml(EpisodeMediaInfo info)
        {
            if (info == null) return null;

            var videoCodec = First(info.Video?.CodecName, info.VideoCodec, info.VideoCodecName);
            var audioCodec = First(info.Audio?.CodecName, info.AudioCodec, info.AudioCodecName);
            var width = info.Video?.Width ?? info.Width;
            var height = info.Video?.Height ?? info.Height;
            if ((!width.HasValue || !height.HasValue) && !string.IsNullOrWhiteSpace(info.Resolution))
            {
                var match = Regex.Match(info.Resolution, @"(?<w>\d{3,5})\s*[xX]\s*(?<h>\d{3,5})");
                int parsed;
                if (!width.HasValue && match.Success && int.TryParse(match.Groups["w"].Value, out parsed)) width = parsed;
                if (!height.HasValue && match.Success && int.TryParse(match.Groups["h"].Value, out parsed)) height = parsed;
            }

            if (string.IsNullOrWhiteSpace(videoCodec) && string.IsNullOrWhiteSpace(audioCodec))
                return null;

            var sb = new StringBuilder();
            sb.AppendLine("<streamdetails>");
            if (!string.IsNullOrWhiteSpace(videoCodec))
            {
                sb.AppendLine("  <video>");
                AppendNestedElement(sb, "codec", videoCodec, 4);
                AppendNestedElement(sb, "micodec", videoCodec, 4);
                if (width.HasValue) AppendNestedElement(sb, "width", width.Value.ToString(CultureInfo.InvariantCulture), 4);
                if (height.HasValue) AppendNestedElement(sb, "height", height.Value.ToString(CultureInfo.InvariantCulture), 4);
                AppendNestedElement(sb, "aspect", info.Video?.DisplayAspectRatio, 4);
                var frameRate = First(info.Video?.RFrameRate, info.RFrameRate, info.FrameRate);
                var fps = ParseFrameRate(frameRate);
                if (fps > 0) AppendNestedElement(sb, "framerate", fps.ToString("F6", CultureInfo.InvariantCulture), 4);
                AppendNestedElement(sb, "bitrate", First(info.Video?.BitRate, info.Bitrate?.ToString(CultureInfo.InvariantCulture)), 4);
                AppendNestedElement(sb, "scantype", info.Video?.FieldOrder, 4);
                AppendNestedElement(sb, "default", "True", 4);
                AppendNestedElement(sb, "forced", "False", 4);
                if (info.DurationSecs.HasValue)
                {
                    AppendNestedElement(sb, "duration", (info.DurationSecs.Value / 60).ToString(CultureInfo.InvariantCulture), 4);
                    AppendNestedElement(sb, "durationinseconds", info.DurationSecs.Value.ToString(CultureInfo.InvariantCulture), 4);
                }
                sb.AppendLine("  </video>");
            }

            if (!string.IsNullOrWhiteSpace(audioCodec))
            {
                sb.AppendLine("  <audio>");
                AppendNestedElement(sb, "codec", audioCodec, 4);
                AppendNestedElement(sb, "micodec", audioCodec, 4);
                AppendNestedElement(sb, "bitrate", First(info.Audio?.BitRate, info.AudioBitRate), 4);
                string language = null;
                if (info.Audio?.Tags != null) info.Audio.Tags.TryGetValue("language", out language);
                AppendNestedElement(sb, "language", language, 4);
                var channels = info.Audio?.Channels ?? info.Channels;
                if (channels.HasValue) AppendNestedElement(sb, "channels", channels.Value.ToString(CultureInfo.InvariantCulture), 4);
                AppendNestedElement(sb, "samplingrate", First(info.Audio?.SampleRate, info.SampleRate), 4);
                AppendNestedElement(sb, "default", "True", 4);
                AppendNestedElement(sb, "forced", "False", 4);
                sb.AppendLine("  </audio>");
            }

            sb.Append("</streamdetails>");
            return sb.ToString();
        }

        private static void AppendNestedElement(StringBuilder sb, string name, string value, int spaces)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            sb.Append(' ', spaces).Append('<').Append(name).Append('>')
                .Append(EscapeXml(value.Trim()))
                .Append("</").Append(name).AppendLine(">");
        }

        private static int? ParseYear(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var match = Regex.Match(value, @"(?:^|\D)(?<year>(?:19|20)\d{2})(?:\D|$)");
            int year;
            return match.Success && int.TryParse(match.Groups["year"].Value, out year) ? (int?)year : null;
        }

        private static string NormalizeDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            DateTime parsed;
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
                return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return value.Trim();
        }

        private static string GetRuntimeMinutes(int? durationSecs, string duration)
        {
            if (durationSecs.HasValue && durationSecs.Value > 0)
                return Math.Max(1, durationSecs.Value / 60).ToString(CultureInfo.InvariantCulture);
            return NormalizeRuntime(duration);
        }

        private static string NormalizeRuntime(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            int minutes;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes) && minutes > 0)
                return minutes.ToString(CultureInfo.InvariantCulture);
            TimeSpan duration;
            if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out duration) && duration.TotalMinutes > 0)
                return Math.Max(1, (int)Math.Round(duration.TotalMinutes)).ToString(CultureInfo.InvariantCulture);
            return null;
        }

        private static double ParseFrameRate(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            var slash = value.IndexOf('/');
            if (slash > 0)
            {
                double numerator;
                double denominator;
                if (double.TryParse(value.Substring(0, slash), NumberStyles.Any, CultureInfo.InvariantCulture, out numerator) &&
                    double.TryParse(value.Substring(slash + 1), NumberStyles.Any, CultureInfo.InvariantCulture, out denominator) &&
                    denominator != 0)
                    return numerator / denominator;
            }
            double fps;
            return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out fps) ? fps : 0;
        }

        private static string First(params string[] values)
            => values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        private static bool WriteIfChanged(string path, string content)
        {
            if (File.Exists(path) && string.Equals(File.ReadAllText(path, Encoding.UTF8), content, StringComparison.Ordinal))
                return false;

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            WriteAtomically(path, content, directory);
            return true;
        }

        private static void WriteAtomically(string path, string content, string directory)
        {
            // Keep the temporary file beside the destination so the final rename is
            // atomic even when library roots are mounted on another filesystem.
            var tempDirectory = string.IsNullOrWhiteSpace(directory)
                ? Directory.GetCurrentDirectory()
                : directory;
            var tempPath = Path.Combine(
                tempDirectory,
                "." + Path.GetFileName(path) + ".xc2emby-" + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                File.WriteAllText(tempPath, content, Encoding.UTF8);

                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tempPath, path, null);
                    }
                    catch (FileNotFoundException) when (!File.Exists(path))
                    {
                        // The destination disappeared after the existence check.
                        File.Move(tempPath, path);
                    }
                }
                else
                {
                    try
                    {
                        File.Move(tempPath, path);
                    }
                    catch (IOException) when (File.Exists(path))
                    {
                        // Another writer created the destination before our move.
                        File.Replace(tempPath, path, null);
                    }
                }
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        private static string EscapeXml(string value)
            => (value ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
    }
}
