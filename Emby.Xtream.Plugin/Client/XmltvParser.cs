using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Emby.Xtream.Plugin.Client.Models;

namespace Emby.Xtream.Plugin.Client
{
    /// <summary>
    /// Streaming XMLTV parser. Parses &lt;programme&gt; elements from an XMLTV feed
    /// and extracts title, description, and status flags (live, new, repeat, premiere).
    /// Uses XmlReader to avoid loading the entire document into memory.
    /// </summary>
    internal static class XmltvParser
    {
        /// <summary>
        /// Parses an XMLTV stream and returns programs grouped by XMLTV channel ID.
        /// </summary>
        /// <param name="xmlStream">The XMLTV XML stream to parse.</param>
        /// <param name="filterStartUnix">Exclude programs that stop at or before this Unix timestamp.</param>
        /// <param name="filterEndUnix">Exclude programs that start at or after this Unix timestamp.</param>
        internal static Dictionary<string, List<EpgProgram>> Parse(
            Stream xmlStream,
            long? filterStartUnix,
            long? filterEndUnix)
        {
            var result = new Dictionary<string, List<EpgProgram>>(StringComparer.OrdinalIgnoreCase);

            var settings = new XmlReaderSettings
            {
                IgnoreWhitespace = true,
                IgnoreComments = true,
                DtdProcessing = DtdProcessing.Ignore,
            };

            using (var reader = XmlReader.Create(xmlStream, settings))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (!string.Equals(reader.Name, "programme", StringComparison.OrdinalIgnoreCase)) continue;

                    var program = ParseProgramme(reader, filterStartUnix, filterEndUnix);
                    if (program == null) continue;

                    List<EpgProgram> list;
                    if (!result.TryGetValue(program.ChannelId, out list))
                    {
                        list = new List<EpgProgram>();
                        result[program.ChannelId] = list;
                    }
                    list.Add(program);
                }
            }

            return result;
        }

        /// <summary>
        /// Derives program data from an already-loaded XDocument instead of re-fetching the stream.
        /// Called by LiveTvService after XDocument.Load() so both caches share one HTTP download.
        /// </summary>
        internal static Dictionary<string, List<EpgProgram>> ParseDocument(
            XDocument doc,
            long? filterStartUnix,
            long? filterEndUnix)
        {
            var result = new Dictionary<string, List<EpgProgram>>(StringComparer.OrdinalIgnoreCase);

            foreach (var prog in doc.Descendants("programme"))
            {
                var channelAttr = prog.Attribute("channel")?.Value;
                if (string.IsNullOrEmpty(channelAttr)) continue;

                var startAttr = prog.Attribute("start")?.Value;
                var stopAttr = prog.Attribute("stop")?.Value;
                if (string.IsNullOrEmpty(startAttr) || string.IsNullOrEmpty(stopAttr)) continue;

                var startUnix = ParseXmltvTimestamp(startAttr);
                var stopUnix = ParseXmltvTimestamp(stopAttr);
                if (startUnix == 0 && stopUnix == 0) continue;

                if (filterEndUnix.HasValue && startUnix >= filterEndUnix.Value) continue;
                if (filterStartUnix.HasValue && stopUnix <= filterStartUnix.Value) continue;

                var cats = prog.Elements("category")
                    .Select(c => c.Value)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .ToList();

                var onscreen = prog.Elements("episode-num")
                    .FirstOrDefault(e => string.Equals(
                        e.Attribute("system")?.Value, "onscreen", StringComparison.OrdinalIgnoreCase));

                var ratingEl = prog.Element("rating");

                var program = new EpgProgram
                {
                    ChannelId         = channelAttr,
                    StartTimestamp    = startUnix,
                    StopTimestamp     = stopUnix,
                    IsPlainText       = true,
                    Title             = prog.Element("title")?.Value,
                    Description       = prog.Element("desc")?.Value,
                    SubTitle          = prog.Element("sub-title")?.Value,
                    IsLive            = prog.Element("live") != null,
                    IsNew             = prog.Element("new") != null,
                    IsPreviouslyShown = prog.Element("previously-shown") != null,
                    IsPremiere        = prog.Element("premiere") != null,
                    Categories        = cats.Count > 0 ? cats : null,
                    ImageUrl          = prog.Element("icon")?.Attribute("src")?.Value,
                    EpisodeNumOnscreen = onscreen?.Value,
                    Rating            = ratingEl?.Element("value")?.Value?.Trim(),
                };

                List<EpgProgram> list;
                if (!result.TryGetValue(channelAttr, out list))
                {
                    list = new List<EpgProgram>();
                    result[channelAttr] = list;
                }
                list.Add(program);
            }

            return result;
        }

        private static EpgProgram ParseProgramme(
            XmlReader reader,
            long? filterStartUnix,
            long? filterEndUnix)
        {
            var startAttr = reader.GetAttribute("start");
            var stopAttr = reader.GetAttribute("stop");
            var channelAttr = reader.GetAttribute("channel");

            if (string.IsNullOrEmpty(startAttr) || string.IsNullOrEmpty(stopAttr) || string.IsNullOrEmpty(channelAttr))
                return null;

            var startUnix = ParseXmltvTimestamp(startAttr);
            var stopUnix = ParseXmltvTimestamp(stopAttr);

            if (startUnix == 0 && stopUnix == 0)
                return null;

            // Apply date range filter
            if (filterEndUnix.HasValue && startUnix >= filterEndUnix.Value)
                return null;
            if (filterStartUnix.HasValue && stopUnix <= filterStartUnix.Value)
                return null;

            var program = new EpgProgram
            {
                ChannelId = channelAttr,
                StartTimestamp = startUnix,
                StopTimestamp = stopUnix,
                IsPlainText = true,
            };

            if (reader.IsEmptyElement)
                return program;

            // Read child elements at depth + 1; break when we return to the parent's end element
            var depth = reader.Depth;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                    break;
                if (reader.NodeType != XmlNodeType.Element) continue;

                var name = reader.Name;

                if (string.Equals(name, "title", StringComparison.OrdinalIgnoreCase))
                {
                    if (!reader.IsEmptyElement)
                        program.Title = ReadText(reader);
                }
                else if (string.Equals(name, "desc", StringComparison.OrdinalIgnoreCase))
                {
                    if (!reader.IsEmptyElement)
                        program.Description = ReadText(reader);
                }
                else if (string.Equals(name, "live", StringComparison.OrdinalIgnoreCase))
                {
                    program.IsLive = true;
                }
                else if (string.Equals(name, "new", StringComparison.OrdinalIgnoreCase))
                {
                    program.IsNew = true;
                }
                else if (string.Equals(name, "previously-shown", StringComparison.OrdinalIgnoreCase))
                {
                    program.IsPreviouslyShown = true;
                }
                else if (string.Equals(name, "premiere", StringComparison.OrdinalIgnoreCase))
                {
                    program.IsPremiere = true;
                }
                else if (string.Equals(name, "category", StringComparison.OrdinalIgnoreCase))
                {
                    if (!reader.IsEmptyElement)
                    {
                        var cat = ReadText(reader);
                        if (!string.IsNullOrWhiteSpace(cat))
                        {
                            if (program.Categories == null)
                                program.Categories = new List<string>();
                            program.Categories.Add(cat);
                        }
                    }
                }
                else if (string.Equals(name, "sub-title", StringComparison.OrdinalIgnoreCase))
                {
                    if (!reader.IsEmptyElement)
                        program.SubTitle = ReadText(reader);
                }
                else if (string.Equals(name, "icon", StringComparison.OrdinalIgnoreCase))
                {
                    var src = reader.GetAttribute("src");
                    if (!string.IsNullOrEmpty(src))
                        program.ImageUrl = src;
                }
                else if (string.Equals(name, "episode-num", StringComparison.OrdinalIgnoreCase))
                {
                    var system = reader.GetAttribute("system");
                    if (string.Equals(system, "onscreen", StringComparison.OrdinalIgnoreCase)
                        && !reader.IsEmptyElement
                        && string.IsNullOrEmpty(program.EpisodeNumOnscreen))
                    {
                        program.EpisodeNumOnscreen = ReadText(reader);
                    }
                }
                else if (string.Equals(name, "rating", StringComparison.OrdinalIgnoreCase))
                {
                    // <rating system="MPAA"><value>TV-14</value></rating>
                    var depth2 = reader.Depth;
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth2)
                            break;
                        if (reader.NodeType == XmlNodeType.Element
                            && string.Equals(reader.Name, "value", StringComparison.OrdinalIgnoreCase)
                            && !reader.IsEmptyElement)
                        {
                            var val = ReadText(reader);
                            if (!string.IsNullOrWhiteSpace(val) && string.IsNullOrEmpty(program.Rating))
                                program.Rating = val.Trim();
                        }
                    }
                }
            }

            return program;
        }

        /// <summary>
        /// Reads text/CDATA content from the current element, leaving the reader
        /// positioned at the element's end tag. Called only when IsEmptyElement is false.
        /// </summary>
        private static string ReadText(XmlReader reader)
        {
            var sb = new StringBuilder();
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement) break;
                if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA)
                    sb.Append(reader.Value);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Parses an XMLTV timestamp ("YYYYMMDDHHmmss +HHMM") to a Unix timestamp.
        /// Returns 0 on parse failure.
        /// </summary>
        internal static long ParseXmltvTimestamp(string value)
        {
            if (string.IsNullOrEmpty(value))
                return 0;

            value = value.Trim();
            var spaceIdx = value.IndexOf(' ');
            string datePart, tzPart;
            if (spaceIdx > 0)
            {
                datePart = value.Substring(0, spaceIdx);
                tzPart = value.Substring(spaceIdx + 1).Trim();
            }
            else if (value.Length > 14)
            {
                // Handle "20260513120000+0100" — timezone immediately follows digits, no space.
                var tzIdx = value.IndexOf('+', 14);
                if (tzIdx < 0) tzIdx = value.IndexOf('-', 14);
                if (tzIdx > 0)
                {
                    datePart = value.Substring(0, tzIdx);
                    tzPart = value.Substring(tzIdx);
                }
                else
                {
                    datePart = value;
                    tzPart = null;
                }
            }
            else
            {
                datePart = value;
                tzPart = null;
            }

            if (datePart.Length < 14)
                return 0;

            int year, month, day, hour, minute, second;
            if (!int.TryParse(datePart.Substring(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out year)) return 0;
            if (!int.TryParse(datePart.Substring(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out month)) return 0;
            if (!int.TryParse(datePart.Substring(6, 2), NumberStyles.None, CultureInfo.InvariantCulture, out day)) return 0;
            if (!int.TryParse(datePart.Substring(8, 2), NumberStyles.None, CultureInfo.InvariantCulture, out hour)) return 0;
            if (!int.TryParse(datePart.Substring(10, 2), NumberStyles.None, CultureInfo.InvariantCulture, out minute)) return 0;
            if (!int.TryParse(datePart.Substring(12, 2), NumberStyles.None, CultureInfo.InvariantCulture, out second)) return 0;

            int offsetMinutes = 0;
            if (!string.IsNullOrEmpty(tzPart) && tzPart.Length >= 5)
            {
                int sign = tzPart[0] == '-' ? -1 : 1;
                int tzHour, tzMin;
                if (int.TryParse(tzPart.Substring(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out tzHour)
                    && int.TryParse(tzPart.Substring(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out tzMin))
                {
                    offsetMinutes = sign * (tzHour * 60 + tzMin);
                }
            }

            try
            {
                var dt = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
                dt = dt.AddMinutes(-offsetMinutes);
                return new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeSeconds();
            }
            catch (ArgumentOutOfRangeException)
            {
                return 0;
            }
        }
    }
}
