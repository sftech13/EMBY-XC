using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Emby.Xtream.Plugin.Client.Models;

namespace Emby.Xtream.Plugin.Client
{
    internal static class XmltvParser
    {
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
