using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Emby writes multi-episode NFOs as sibling episodedetails elements, not a
    /// single XML document. Keep source spans so updating one block cannot
    /// reformat or discard its siblings, comments, declaration or whitespace.
    /// </summary>
    internal sealed class NfoMetadataDocument
    {
        private sealed class Block
        {
            internal XElement Metadata;
            internal int Start;
            internal int Length;
        }

        private readonly string _source;
        private readonly List<Block> _blocks = new List<Block>();

        private NfoMetadataDocument(string source) { _source = source; }

        private static XmlReaderSettings Settings(ConformanceLevel conformance) => new XmlReaderSettings
        {
            ConformanceLevel = conformance,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 8 * 1024 * 1024,
        };

        internal static string EpisodeIdentity(int season, int episode)
            => season.ToString(CultureInfo.InvariantCulture) + ":" + episode.ToString(CultureInfo.InvariantCulture);

        private static string EpisodeIdentity(XElement metadata)
        {
            int season, episode;
            if (metadata.Name != "episodedetails" || metadata.Elements("season").Count() != 1 ||
                metadata.Elements("episode").Count() != 1 ||
                !int.TryParse((string)metadata.Element("season"), NumberStyles.Integer, CultureInfo.InvariantCulture, out season) ||
                !int.TryParse((string)metadata.Element("episode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out episode) ||
                season < 0 || episode < 0) return null;
            return EpisodeIdentity(season, episode);
        }

        internal static bool SameEpisode(XElement left, XElement right)
            => EpisodeIdentity(left) != null && EpisodeIdentity(left) == EpisodeIdentity(right);

        internal static NfoMetadataDocument Parse(string source)
        {
            if (source.Length > 8 * 1024 * 1024) throw new XmlException("NFO exceeds size limit");
            var document = new NfoMetadataDocument(source);
            var offset = source.Length > 0 && source[0] == '\uFEFF' ? 1 : 0;
            // Fragment readers disallow declarations. Validate a leading one as
            // XML before removing it from the parsing view (never from output).
            if (source.Substring(offset).StartsWith("<?xml", StringComparison.Ordinal) &&
                source.Length > offset + 5 && char.IsWhiteSpace(source[offset + 5]))
            {
                var end = source.IndexOf("?>", offset, StringComparison.Ordinal);
                if (end < 0) throw new XmlException("Unterminated XML declaration");
                using (var declaration = XmlReader.Create(new StringReader(source.Substring(offset, end + 2 - offset) + "<nfo/>"), Settings(ConformanceLevel.Document)))
                    while (declaration.Read()) { }
                offset = end + 2;
            }
            var body = source.Substring(offset);
            var lineStarts = new List<int> { 0 };
            for (var i = 0; i < body.Length; i++)
            {
                if (body[i] == '\r')
                {
                    if (i + 1 < body.Length && body[i + 1] == '\n') i++;
                    lineStarts.Add(i + 1);
                }
                else if (body[i] == '\n') lineStarts.Add(i + 1);
            }
            using (var reader = XmlReader.Create(new StringReader(body), Settings(ConformanceLevel.Fragment)))
            {
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        var start = TagStart(reader, body, lineStarts);
                        XElement metadata;
                        using (var subtree = reader.ReadSubtree())
                            metadata = XElement.Load(subtree, LoadOptions.PreserveWhitespace);
                        var endTag = TagStart(reader, body, lineStarts);
                        var end = TagEnd(body, endTag);
                        document._blocks.Add(new Block { Metadata = metadata, Start = offset + start, Length = end - start });
                    }
                    else if (reader.NodeType != XmlNodeType.Comment && reader.NodeType != XmlNodeType.ProcessingInstruction &&
                        reader.NodeType != XmlNodeType.Whitespace && reader.NodeType != XmlNodeType.SignificantWhitespace)
                        throw new XmlException("Unexpected content outside NFO metadata");
                }
            }
            if (document._blocks.Count == 0) throw new XmlException("NFO has no metadata root");
            return document;
        }

        private static int TagStart(XmlReader reader, string body, List<int> lineStarts)
        {
            var info = (IXmlLineInfo)reader;
            var position = lineStarts[info.LineNumber - 1] + info.LinePosition - 1;
            return body.LastIndexOf('<', position);
        }

        private static int TagEnd(string body, int start)
        {
            char quote = '\0';
            for (var i = start; i < body.Length; i++)
            {
                var c = body[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; }
                else if (c == '\'' || c == '"') quote = c;
                else if (c == '>') return i + 1;
            }
            throw new XmlException("Unterminated metadata element");
        }

        internal XElement Select(XElement desired)
        {
            if (_blocks.Any(b => b.Metadata.Name != desired.Name)) return null;
            if (desired.Name != "episodedetails") return _blocks.Count == 1 ? _blocks[0].Metadata : null;
            var identities = _blocks.Select(b => EpisodeIdentity(b.Metadata)).ToArray();
            if (identities.Any(id => id == null) || identities.Distinct(StringComparer.Ordinal).Count() != identities.Length)
                return null;
            return _blocks.SingleOrDefault(b => SameEpisode(b.Metadata, desired))?.Metadata;
        }

        internal string Replace(XElement selected)
        {
            var block = _blocks.Single(b => ReferenceEquals(b.Metadata, selected));
            return _source.Substring(0, block.Start) + selected.ToString(SaveOptions.DisableFormatting) +
                _source.Substring(block.Start + block.Length);
        }
    }
}
