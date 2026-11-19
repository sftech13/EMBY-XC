using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Emby.Xtream.Plugin.Client.Models;

namespace Emby.Xtream.Plugin.Service
{
    internal static class NfoWriter
    {
        public static bool WriteMovieNfo(
            string nfoPath,
            string title,
            VodDetailInfo detail,
            int? titleYear,
            NfoOwnershipState ownership = null)
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
            return WriteIfChanged(nfoPath, sb.ToString(), ownership);
        }

        // Retained for failed-item retry paths and compatibility with older callers.
        public static bool WriteMovieNfo(string nfoPath, string title, string tmdbId, int? year)
            => WriteMovieNfo(nfoPath, title, new VodDetailInfo { TmdbId = tmdbId }, year);

        public static bool WriteShowNfo(
            string nfoPath,
            string title,
            SeriesInfo info,
            string tvdbId,
            string tmdbId,
            NfoOwnershipState ownership = null)
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
            return WriteIfChanged(nfoPath, sb.ToString(), ownership);
        }

        public static bool WriteShowNfo(string nfoPath, string title, string tvdbId, string tmdbId)
            => WriteShowNfo(nfoPath, title, null, tvdbId, tmdbId);

        public static bool WriteEpisodeNfo(
            string nfoPath,
            string title,
            int season,
            int episodeNum,
            EpisodeInfo episode,
            NfoOwnershipState ownership = null)
        {
            episode = episode ?? new EpisodeInfo();
            var fingerprint = EpisodeMetadataFingerprint(title, season, episodeNum, episode);
            if (ownership?.MatchesEpisode(nfoPath, fingerprint) == true && File.Exists(nfoPath)) return false;
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
            return WriteIfChanged(nfoPath, sb.ToString(), ownership, fingerprint);
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

        internal static string EpisodeMetadataFingerprint(string title, int season, int number, EpisodeInfo episode)
        {
            // Use the same rendered fields as the writer, including codec details,
            // cast/director, artwork and actual normalized season/episode identity.
            // This deliberately excludes STRM connection settings and sibling episodes.
            var info = episode?.Info;
            var value = System.Text.Json.JsonSerializer.Serialize(new
            {
                Title = First(title, episode?.Title)?.Trim(), season, number,
                Plot = First(info?.Plot, episode?.Plot),
                Aired = NormalizeDate(info?.ReleaseDate),
                Runtime = GetRuntimeMinutes(info?.DurationSecs, First(info?.Duration, episode?.Duration)),
                Rating = First(info?.Rating, episode?.Rating),
                Director = info?.Director, Cast = info?.Cast,
                Thumb = info?.MovieImage, Trailer = info?.YoutubeTrailer,
                Streams = BuildStreamDetailsXml(info),
            });
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty);
        }

        private static XElement ParseMetadata(string content)
        {
            using (var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 8 * 1024 * 1024,
            }))
                return XElement.Load(reader, LoadOptions.PreserveWhitespace);
        }

        private static string SemanticValue(XElement element)
        {
            // Compare meaningful XML, not pretty printing, attribute order or
            // element ordering. Text within leaf elements remains significant.
            return new XElement(element.Name,
                element.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal)
                    .Select(a => new XAttribute(a.Name, a.Value)),
                element.HasElements
                    ? (object)element.Elements().Select(SemanticValue).OrderBy(v => v, StringComparer.Ordinal)
                        .Select(v => XElement.Parse(v))
                    : element.Value.Trim()).ToString(SaveOptions.DisableFormatting);
        }

        private static string Identity(XElement element)
        {
            var name = element.Name.LocalName;
            if (name == "uniqueid") return (string)element.Attribute("type") ?? string.Empty;
            if (name == "actor") return element.Element("name")?.Value.Trim() ?? string.Empty;
            if (name == "thumb") return ((string)element.Attribute("aspect") ?? string.Empty) + "|" + element.Value.Trim();
            if (name == "genre" || name == "director") return element.Value.Trim();
            return string.Empty;
        }

        private static void MergeMetadata(XElement current, XElement previous, XElement desired)
        {
            foreach (var group in desired.Elements().GroupBy(e => e.Name))
            {
                var root = current.AncestorsAndSelf().Last();
                var locks = (root.Element("lockedfields")?.Value ?? string.Empty)
                    .Split(new[] { '|', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                var field = group.Key.LocalName;
                var property = field == "title" ? "Name" : field == "plot" ? "Overview" :
                    field == "rating" ? "CommunityRating" : field == "year" ? "ProductionYear" :
                    field == "aired" || field == "premiered" ? "PremiereDate" :
                    field == "genre" ? "Genres" : field == "actor" || field == "director" ? "People" :
                    field == "thumb" || field == "fanart" ? "Images" : field;
                if (locks.Any(value => string.Equals(value.Trim(), property, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value.Trim(), field, StringComparison.OrdinalIgnoreCase))) continue;
                var incoming = group.ToList();
                var existing = current.Elements(group.Key).ToList();
                var old = previous?.Elements(group.Key).ToList() ?? new List<XElement>();
                // An enriched/probed media block belongs to Emby in its entirety.
                // A still-exact provider block may accept a real provider update.
                if (group.Key.LocalName == "lockdata" && existing.Count > 0) continue;
                if (group.Key.LocalName == "fileinfo" && existing.Count > 0 &&
                    (existing.Count != 1 || old.Count != 1 || SemanticValue(existing[0]) != SemanticValue(old[0]))) continue;
                foreach (var next in incoming)
                {
                    var identity = Identity(next);
                    var candidates = existing.Where(e => Identity(e) == identity).ToList();
                    var prior = old.FirstOrDefault(e => Identity(e) == identity);
                    if (candidates.Count == 0)
                    {
                        // Do not undo an external deletion of a formerly generated
                        // field. New provider fields/IDs may be added, never remove
                        // an external ID, artwork entry, actor or genre.
                        if (prior == null)
                        {
                            var addition = new XElement(next);
                            if (addition.Name.LocalName == "uniqueid" &&
                                current.Elements("uniqueid").Any(e => string.Equals((string)e.Attribute("default"), "true", StringComparison.OrdinalIgnoreCase)))
                                addition.Attribute("default")?.Remove();
                            current.Add(addition);
                        }
                        continue;
                    }
                    if (candidates.Count != 1 || prior == null) continue;
                    var target = candidates[0];
                    if (SemanticValue(prior) == SemanticValue(next)) continue;
                    if (next.HasElements && target.HasElements && prior.HasElements)
                    {
                        MergeMetadata(target, prior, next);
                    }
                    else if (!target.HasElements && !prior.HasElements &&
                        target.Value.Trim() == prior.Value.Trim())
                    {
                        // Change only a scalar still holding our old value. Leave
                        // attributes added by Emby untouched, even on owned fields.
                        target.Value = next.Value;
                        foreach (var attribute in next.Attributes())
                            if ((string)target.Attribute(attribute.Name) == (string)prior.Attribute(attribute.Name))
                                target.SetAttributeValue(attribute.Name, attribute.Value);
                    }
                }
            }
        }

        private static bool WriteIfChanged(string path, string content, NfoOwnershipState ownership, string episodeFingerprint = null)
        {
            var desired = ParseMetadata(content);
            var generated = SemanticValue(desired);
            var previous = ownership?.GetPrevious(path);
            string expectedOriginal = null;
            if (File.Exists(path))
            {
                // The baseline is per file/episode. An unchanged provider result
                // must not even read an Emby-enriched file, much less rewrite it.
                if (string.Equals(previous, generated, StringComparison.Ordinal))
                {
                    ownership?.Record(path, generated, episodeFingerprint);
                    return false;
                }
                if (new FileInfo(path).Length > 8 * 1024 * 1024)
                {
                    ownership?.Warn("Oversized existing NFO preserved without replacement");
                    return false;
                }
                var original = File.ReadAllText(path, Encoding.UTF8);
                XElement current;
                try { current = ParseMetadata(original); }
                catch (XmlException)
                {
                    ownership?.Warn("Malformed existing NFO preserved; manual repair required");
                    return false;
                }
                if (current.Name != desired.Name)
                {
                    ownership?.Warn("Existing NFO with unexpected root preserved without replacement");
                    return false;
                }
                var locked = current.Element("lockdata")?.Value.Trim();
                if (string.Equals(locked, "true", StringComparison.OrdinalIgnoreCase) || locked == "1")
                    return false;
                // Upgrade/first encounter: no evidence of field ownership exists.
                // Adopt the provider baseline without touching the library file.
                if (previous == null)
                {
                    ownership?.Record(path, generated, episodeFingerprint);
                    return false;
                }
                XElement baseline;
                try { baseline = ParseMetadata(previous); }
                catch (XmlException)
                {
                    ownership?.Record(path, generated, episodeFingerprint);
                    return false;
                }
                if (baseline.Name != desired.Name)
                {
                    ownership?.Record(path, generated, episodeFingerprint);
                    return false;
                }
                var before = SemanticValue(current);
                MergeMetadata(current, baseline, desired);
                if (before == SemanticValue(current))
                {
                    ownership?.Record(path, generated, episodeFingerprint);
                    return false;
                }
                // Don't overwrite a concurrent save detected during our merge.
                if (original != File.ReadAllText(path, Encoding.UTF8))
                {
                    ownership?.Warn("Concurrent NFO save detected; metadata update deferred");
                    return false;
                }
                content = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + Environment.NewLine + current;
                expectedOriginal = original;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            if (!WriteAtomically(path, content, directory, expectedOriginal))
            {
                ownership?.Warn("Concurrent NFO save detected at commit; metadata update deferred");
                return false;
            }
            ownership?.Record(path, generated, episodeFingerprint);
            return true;
        }

        internal static bool WriteAtomically(string path, string content, string directory, string expectedOriginal)
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

                if (expectedOriginal != null)
                {
                    if (!File.Exists(path) || File.ReadAllText(path, Encoding.UTF8) != expectedOriginal) return false;
                    try
                    {
                        File.Replace(tempPath, path, null);
                    }
                    catch (FileNotFoundException) when (!File.Exists(path))
                    {
                        // A concurrent removal is not permission to replace it.
                        return false;
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
                        // Never replace a file created by another writer while
                        // this was meant to be a missing-NFO restoration.
                        return false;
                    }
                }
                return true;
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
