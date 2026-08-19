using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using Emby.Xtream.Plugin.Client.Models;

namespace Emby.Xtream.Plugin.Client
{
    /// <summary>
    /// Compact result of one forward-only XMLTV pass. Unlike XDocument, this only
    /// retains fields that XC2EMBY actually uses after the HTTP response is closed.
    /// </summary>
    internal sealed class XmltvSnapshot
    {
        internal Dictionary<string, List<EpgProgram>> ProgramsByChannel { get; }
            = new Dictionary<string, List<EpgProgram>>(StringComparer.OrdinalIgnoreCase);

        internal Dictionary<string, List<string>> DisplayNamesByChannel { get; }
            = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        internal int ProgramCount { get; set; }
    }

    /// <summary>
    /// Streaming XMLTV parser. It deliberately avoids XDocument because a large
    /// provider feed expands to many times its wire size when represented as a DOM.
    /// </summary>
    internal static class XmltvParser
    {
        internal static XmltvSnapshot Parse(
            Stream xmlStream,
            long? filterStartUnix,
            long? filterEndUnix,
            HashSet<string> includedChannelIds = null)
        {
            var snapshot = new XmltvSnapshot();
            var settings = new XmlReaderSettings
            {
                IgnoreWhitespace = true,
                IgnoreComments = true,
                DtdProcessing = DtdProcessing.Ignore,
                CloseInput = false,
            };

            using (var reader = XmlReader.Create(xmlStream, settings))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element)
                        continue;

                    if (string.Equals(reader.Name, "channel", StringComparison.OrdinalIgnoreCase))
                    {
                        ParseChannel(reader, snapshot);
                    }
                    else if (string.Equals(reader.Name, "programme", StringComparison.OrdinalIgnoreCase))
                    {
                        var program = ParseProgramme(reader, filterStartUnix, filterEndUnix, includedChannelIds);
                        if (program == null)
                            continue;

                        if (!snapshot.ProgramsByChannel.TryGetValue(program.ChannelId, out var list))
                        {
                            list = new List<EpgProgram>();
                            snapshot.ProgramsByChannel[program.ChannelId] = list;
                        }

                        list.Add(program);
                        snapshot.ProgramCount++;
                    }
                }
            }

            return snapshot;
        }

        private static void ParseChannel(XmlReader reader, XmltvSnapshot snapshot)
        {
            var id = reader.GetAttribute("id");
            if (string.IsNullOrEmpty(id))
                return;

            if (!snapshot.DisplayNamesByChannel.TryGetValue(id, out var names))
            {
                names = new List<string>();
                snapshot.DisplayNamesByChannel[id] = names;
            }

            if (reader.IsEmptyElement)
                return;

            var depth = reader.Depth;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                    break;

                if (reader.NodeType == XmlNodeType.Element
                    && string.Equals(reader.Name, "display-name", StringComparison.OrdinalIgnoreCase)
                    && !reader.IsEmptyElement)
                {
                    var name = ReadText(reader);
                    if (!string.IsNullOrWhiteSpace(name))
                        names.Add(name);
                }
            }
        }

        private static EpgProgram ParseProgramme(
            XmlReader reader,
            long? filterStartUnix,
            long? filterEndUnix,
            HashSet<string> includedChannelIds)
        {
            var startAttr = reader.GetAttribute("start");
            var stopAttr = reader.GetAttribute("stop");
            var channelAttr = reader.GetAttribute("channel");

            if (string.IsNullOrEmpty(startAttr)
                || string.IsNullOrEmpty(stopAttr)
                || string.IsNullOrEmpty(channelAttr)
                || !IsIncludedChannel(channelAttr, includedChannelIds))
            {
                return null;
            }

            var startUnix = ParseXmltvTimestamp(startAttr);
            var stopUnix = ParseXmltvTimestamp(stopAttr);
            if (startUnix == 0 && stopUnix == 0)
                return null;

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

            var depth = reader.Depth;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                    break;
                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                var name = reader.Name;
                if (string.Equals(name, "title", StringComparison.OrdinalIgnoreCase))
                {
                    if (!reader.IsEmptyElement) program.Title = ReadText(reader);
                }
                else if (string.Equals(name, "desc", StringComparison.OrdinalIgnoreCase))
                {
                    if (!reader.IsEmptyElement) program.Description = ReadText(reader);
                }
                else if (string.Equals(name, "sub-title", StringComparison.OrdinalIgnoreCase))
                {
                    if (!reader.IsEmptyElement) program.SubTitle = ReadText(reader);
                }
                else if (string.Equals(name, "category", StringComparison.OrdinalIgnoreCase))
                {
                    if (!reader.IsEmptyElement)
                    {
                        var category = ReadText(reader);
                        if (!string.IsNullOrWhiteSpace(category))
                        {
                            if (program.Categories == null) program.Categories = new List<string>();
                            program.Categories.Add(category);
                        }
                    }
                }
                else if (string.Equals(name, "icon", StringComparison.OrdinalIgnoreCase))
                {
                    program.ImageUrl = reader.GetAttribute("src");
                }
                else if (string.Equals(name, "episode-num", StringComparison.OrdinalIgnoreCase))
                {
                    var system = reader.GetAttribute("system");
                    if (!reader.IsEmptyElement)
                    {
                        var value = ReadText(reader);
                        if (string.Equals(system, "xmltv_ns", StringComparison.OrdinalIgnoreCase))
                            program.EpisodeNumXmltvNs = value;
                        else if (string.Equals(system, "onscreen", StringComparison.OrdinalIgnoreCase))
                            program.EpisodeNumOnscreen = value;
                    }
                }
                else if (string.Equals(name, "date", StringComparison.OrdinalIgnoreCase))
                {
                    if (!reader.IsEmptyElement)
                    {
                        var value = ReadText(reader);
                        if (!string.IsNullOrEmpty(value) && value.Length >= 4
                            && int.TryParse(value.Substring(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year))
                        {
                            program.ProductionYear = year;
                        }
                    }
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
                else if (string.Equals(name, "rating", StringComparison.OrdinalIgnoreCase))
                {
                    ParseRating(reader, program);
                }
            }

            return program;
        }

        private static bool IsIncludedChannel(
            string channelId,
            HashSet<string> includedChannelIds)
        {
            if (includedChannelIds == null || includedChannelIds.Contains(channelId))
                return true;

            // Providers commonly expose multiple XMLTV definitions for one JSON
            // epg_channel_id by appending a numeric duplicate suffix, for example
            // CBSKCBS.us2. XtreamTunerHost can assign that suffixed ID to a duplicate
            // stream, so retain its programmes whenever the selected base ID is present.
            var baseId = channelId.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            return baseId.Length > 0 &&
                   !string.Equals(baseId, channelId, StringComparison.Ordinal) &&
                   includedChannelIds.Contains(baseId);
        }

        private static void ParseRating(XmlReader reader, EpgProgram program)
        {
            if (reader.IsEmptyElement)
                return;

            var depth = reader.Depth;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                    break;
                if (reader.NodeType == XmlNodeType.Element
                    && string.Equals(reader.Name, "value", StringComparison.OrdinalIgnoreCase)
                    && !reader.IsEmptyElement)
                {
                    var value = ReadText(reader);
                    if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrEmpty(program.Rating))
                        program.Rating = value.Trim();
                }
            }
        }

        private static string ReadText(XmlReader reader)
        {
            var sb = new StringBuilder();
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement)
                    break;
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

            if (!int.TryParse(datePart.Substring(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)) return 0;
            if (!int.TryParse(datePart.Substring(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month)) return 0;
            if (!int.TryParse(datePart.Substring(6, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var day)) return 0;
            if (!int.TryParse(datePart.Substring(8, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hour)) return 0;
            if (!int.TryParse(datePart.Substring(10, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minute)) return 0;
            if (!int.TryParse(datePart.Substring(12, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var second)) return 0;

            var offsetMinutes = 0;
            if (!string.IsNullOrEmpty(tzPart) && tzPart.Length >= 5)
            {
                var sign = tzPart[0] == '-' ? -1 : 1;
                if (int.TryParse(tzPart.Substring(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var tzHour)
                    && int.TryParse(tzPart.Substring(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var tzMin))
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
