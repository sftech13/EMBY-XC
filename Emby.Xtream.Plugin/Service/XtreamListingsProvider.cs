using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Provides EPG guide data to Emby by fetching and parsing the XMLTV endpoint directly.
    /// Registered at startup by <see cref="XtreamServerEntryPoint"/>.
    /// Tuner channels link to this provider via <see cref="ChannelInfo.ListingsChannelId"/>.
    /// </summary>
    public class XtreamListingsProvider : IListingsProvider
    {
        public const string ProviderType = "xtream-epg";

        private static volatile XtreamListingsProvider _instance;
        public static XtreamListingsProvider Instance => _instance;

        public string Name => "XC2EMBY EPG";
        public string Type => ProviderType;
        public string SetupUrl => string.Empty;

        private XDocument _cachedXml;
        private DateTimeOffset _cacheExpiry = DateTimeOffset.MinValue;
        private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);

        public XtreamListingsProvider() { _instance = this; }

        public async Task<List<ProgramInfo>> GetProgramsAsync(
            ListingsProviderInfo info,
            string channelId,
            DateTimeOffset startDateUtc,
            DateTimeOffset endDateUtc,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(channelId))
                return new List<ProgramInfo>();

            var doc = await GetCachedXmlAsync(cancellationToken).ConfigureAwait(false);
            if (doc == null)
                return new List<ProgramInfo>();

            var programs = new List<ProgramInfo>();

            foreach (var prog in doc.Descendants("programme"))
            {
                if (!string.Equals(prog.Attribute("channel")?.Value, channelId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var startStr = prog.Attribute("start")?.Value;
                var stopStr = prog.Attribute("stop")?.Value;

                if (!TryParseXmltvDate(startStr, out var start) ||
                    !TryParseXmltvDate(stopStr, out var stop))
                    continue;

                if (stop <= startDateUtc || start >= endDateUtc)
                    continue;

                var title        = StripEpgQualifiers(prog.Element("title")?.Value ?? "Unknown");
                var rawSubTitle  = prog.Element("sub-title")?.Value;
                var episodeTitle = StripEpgQualifiers(rawSubTitle);
                var genres       = prog.Elements("category").Select(e => e.Value).ToList();
                var isMovie      = genres.Any(c =>
                    c.IndexOf("movie", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    c.IndexOf("film", StringComparison.OrdinalIgnoreCase) >= 0);
                var isSports     = genres.Any(c =>
                    c.IndexOf("sport", StringComparison.OrdinalIgnoreCase) >= 0);
                var isNews       = genres.Any(c =>
                    c.IndexOf("news", StringComparison.OrdinalIgnoreCase) >= 0);
                var seriesKey    = NormalizeGuideKey(title);
                var episodeKey   = NormalizeGuideKey(episodeTitle);

                // ShowId drives PresentationUniqueKey → "Other Showings".
                // Scope it to series+episode when a sub-title is available so that
                // "Other Showings" finds only airings of that specific episode, not
                // every episode of the same series (e.g. only "Lizzo" guest airings of
                // The Drew Barrymore Show, not Charlize Theron/Belle Burden/etc.).
                // Without a sub-title, fall back to series key so cross-channel
                // matching still works for movies and one-off specials.
                var showId = (!string.IsNullOrEmpty(seriesKey) && !string.IsNullOrEmpty(episodeKey))
                    ? seriesKey + "::" + episodeKey
                    : seriesKey ?? title.ToLowerInvariant();

                var program = new ProgramInfo
                {
                    ChannelId      = channelId,
                    Id             = string.Format(CultureInfo.InvariantCulture, "{0}_{1}", channelId, startStr),
                    ShowId         = showId,
                    Name           = title,
                    Overview       = prog.Element("desc")?.Value,
                    StartDate      = start,
                    EndDate        = stop,
                    Genres         = genres,
                    ImageUrl       = prog.Element("icon")?.Attribute("src")?.Value,
                    EpisodeTitle   = episodeTitle,
                    IsMovie        = isMovie,
                    IsSports       = isSports,
                    IsNews         = isNews,
                    IsSeries       = !isMovie,
                    IsLive         = prog.Element("live") != null,
                    IsNew          = prog.Element("new") != null,
                    IsRepeat       = prog.Element("previously-shown") != null,
                    IsPremiere     = prog.Element("premiere") != null,
                    OfficialRating = prog.Element("rating")?.Element("value")?.Value,
                    // SeriesId feeds SeriesPresentationUniqueKey — used by series timers
                    // to match all episodes of a show. Keep it series-level only.
                    SeriesId       = seriesKey,
                };

                // Season/episode from xmltv_ns: "S.E.part" (all 0-based)
                var epNum = prog.Elements("episode-num")
                    .FirstOrDefault(e => e.Attribute("system")?.Value == "xmltv_ns");
                if (epNum != null)
                {
                    var parts = epNum.Value.Split('.');
                    if (parts.Length >= 1 && int.TryParse(parts[0].Trim(), out var s))
                        program.SeasonNumber = s + 1;
                    if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out var ep))
                        program.EpisodeNumber = ep + 1;
                }

                // Fallback: onscreen "S01 E01" or "S1E1" when xmltv_ns is absent
                if (program.SeasonNumber == null)
                {
                    var onscreen = prog.Elements("episode-num")
                        .FirstOrDefault(e => string.Equals(
                            e.Attribute("system")?.Value, "onscreen", StringComparison.OrdinalIgnoreCase));
                    if (onscreen != null)
                        TryParseOnscreenEpisode(onscreen.Value, program);
                }

                // Production year from <date>YYYY...</date>
                var dateVal = prog.Element("date")?.Value;
                if (!string.IsNullOrEmpty(dateVal) && dateVal.Length >= 4 &&
                    int.TryParse(dateVal.Substring(0, 4), out var year))
                    program.ProductionYear = year;

                programs.Add(program);
            }

            return programs;
        }

        private static string BuildShowId(string channelId, string showKey)
        {
            if (string.IsNullOrEmpty(showKey))
                showKey = "unknown";

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}::{1}",
                channelId ?? string.Empty,
                showKey);
        }

        private static readonly Regex OnscreenEpRx =
            new Regex(@"[Ss](\d+)\s*[Ee](\d+)", RegexOptions.Compiled);

        // Strip Unicode Modifier Letter characters that EPG providers append to titles:
        // ᴺᵉʷ = NEW, ᴸᶦᵛᵉ = LIVE, etc. Structured <new /> and <live /> tags still drive flags.
        private static readonly Regex EpgQualifierRx =
            new Regex(@"\s*[\p{Lm}]+", RegexOptions.Compiled);

        private static void TryParseOnscreenEpisode(string value, ProgramInfo program)
        {
            if (string.IsNullOrEmpty(value)) return;
            var m = OnscreenEpRx.Match(value);
            if (!m.Success) return;
            if (int.TryParse(m.Groups[1].Value, out var s)) program.SeasonNumber = s;
            if (int.TryParse(m.Groups[2].Value, out var ep)) program.EpisodeNumber = ep;
        }

        private static string StripEpgQualifiers(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            var stripped = EpgQualifierRx.Replace(value, string.Empty).Trim();
            return string.IsNullOrEmpty(stripped) ? value : stripped;
        }

        private static string NormalizeGuideKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");
        }

        public Task Validate(
            ListingsProviderInfo info,
            bool validateLogin,
            bool validateListings) => Task.CompletedTask;

        public Task<List<NameIdPair>> GetLineups(
            ListingsProviderInfo info,
            string country,
            string location,
            CancellationToken cancellationToken) =>
            Task.FromResult(new List<NameIdPair>
            {
                new NameIdPair { Name = "Xtream Codes", Id = "xc" }
            });

        public async Task<List<ChannelInfo>> GetChannels(
            ListingsProviderInfo info,
            CancellationToken cancellationToken)
        {
            var doc = await GetCachedXmlAsync(cancellationToken).ConfigureAwait(false);
            if (doc == null)
                return new List<ChannelInfo>();

            var sourceAliases = await GetSourceAliasesByXmltvIdAsync(doc, cancellationToken).ConfigureAwait(false);

            return doc.Descendants("channel")
                .Select(c =>
                {
                    var id = c.Attribute("id")?.Value ?? string.Empty;
                    var names = c.Elements("display-name")
                        .Select(e => e.Value)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .ToList();
                    if (sourceAliases.TryGetValue(id, out var aliases))
                        names.AddRange(aliases);
                    var name = names.FirstOrDefault() ?? string.Empty;

                    return new ChannelInfo
                    {
                        Id = id,
                        Name = name,
                        AlternateNames = BuildGuideNameAliases(names).ToArray(),
                    };
                })
                .Where(c => !string.IsNullOrEmpty(c.Id))
                .ToList();
        }

        private async Task<XDocument> GetCachedXmlAsync(CancellationToken cancellationToken)
        {
            if (_cachedXml != null && DateTimeOffset.UtcNow < _cacheExpiry)
                return _cachedXml;

            await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_cachedXml != null && DateTimeOffset.UtcNow < _cacheExpiry)
                    return _cachedXml;

                var url = GetXmltvUrl();
                if (string.IsNullOrEmpty(url))
                    return _cachedXml;

                using (var httpClient = Plugin.CreateHttpClient(180))
                using (var stream = await httpClient.GetStreamAsync(url).ConfigureAwait(false))
                {
                    _cachedXml = XDocument.Load(stream);
                }

                var cfg = Plugin.Instance.Configuration;
                var hours = cfg.EpgCacheMinutes > 0 ? cfg.EpgCacheMinutes / 60.0 : 0.5;
                _cacheExpiry = DateTimeOffset.UtcNow.AddHours(hours);
                return _cachedXml;
            }
            catch
            {
                return _cachedXml;
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        // Called by XtreamTunerHost during channel build to normalize EpgChannelId → XMLTV id.
        internal async Task<HashSet<string>> GetXmltvChannelIdsAsync(CancellationToken cancellationToken)
        {
            var doc = await GetCachedXmlAsync(cancellationToken).ConfigureAwait(false);
            if (doc == null) return null;
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in doc.Descendants("channel"))
            {
                var id = ch.Attribute("id")?.Value;
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }
            return ids;
        }

        internal async Task<Dictionary<string, string[]>> GetXmltvChannelAliasesAsync(CancellationToken cancellationToken)
        {
            var doc = await GetCachedXmlAsync(cancellationToken).ConfigureAwait(false);
            if (doc == null)
                return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            var sourceAliases = await GetSourceAliasesByXmltvIdAsync(doc, cancellationToken).ConfigureAwait(false);

            return doc.Descendants("channel")
                .Select(c =>
                {
                    var id = c.Attribute("id")?.Value ?? string.Empty;
                    var names = c.Elements("display-name")
                        .Select(e => e.Value)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .ToList();
                    if (sourceAliases.TryGetValue(id, out var aliases))
                        names.AddRange(aliases);

                    return new
                    {
                        Id = id,
                        Aliases = BuildGuideNameAliases(names).ToArray()
                    };
                })
                .Where(x => !string.IsNullOrEmpty(x.Id))
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Aliases, StringComparer.OrdinalIgnoreCase);
        }

        internal async Task<Dictionary<string, int>> GetXmltvProgramCountsAsync(CancellationToken cancellationToken)
        {
            var doc = await GetCachedXmlAsync(cancellationToken).ConfigureAwait(false);
            if (doc == null)
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            return doc.Descendants("programme")
                .Select(p => p.Attribute("channel")?.Value)
                .Where(id => !string.IsNullOrEmpty(id))
                .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        }

        private async Task<Dictionary<string, List<string>>> GetSourceAliasesByXmltvIdAsync(
            XDocument doc,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var liveTvService = Plugin.Instance?.LiveTvService;
                if (liveTvService == null)
                    return result;

                var xmltvIds = new HashSet<string>(
                    doc.Descendants("channel")
                        .Select(c => c.Attribute("id")?.Value)
                        .Where(id => !string.IsNullOrEmpty(id)),
                    StringComparer.OrdinalIgnoreCase);

                var cfg = Plugin.Instance.Configuration;
                var channels = await liveTvService.GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);
                foreach (var channel in channels)
                {
                    if (string.IsNullOrEmpty(channel.EpgChannelId))
                        continue;

                    var id = ResolveToXmltvId(channel.EpgChannelId, xmltvIds);
                    if (string.IsNullOrEmpty(id))
                        continue;

                    if (!result.TryGetValue(id, out var aliases))
                    {
                        aliases = new List<string>();
                        result[id] = aliases;
                    }

                    AddAlias(aliases, channel.Name);
                    AddAlias(aliases, ChannelNameCleaner.CleanChannelName(
                        channel.Name, cfg.ChannelRemoveTerms, cfg.EnableChannelNameCleaning));
                }
            }
            catch
            {
                return result;
            }

            return result;
        }

        private static void AddAlias(List<string> aliases, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            var trimmed = Regex.Replace(value.Trim(), @"\s+", " ");
            if (!aliases.Any(a => string.Equals(a, trimmed, StringComparison.OrdinalIgnoreCase)))
                aliases.Add(trimmed);
        }

        private static IEnumerable<string> BuildGuideNameAliases(IEnumerable<string> names)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in names ?? Enumerable.Empty<string>())
            {
                foreach (var alias in ExpandGuideNameAliases(raw))
                {
                    if (seen.Add(alias))
                        yield return alias;
                }
            }
        }

        private static IEnumerable<string> ExpandGuideNameAliases(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                yield break;

            var trimmed = Regex.Replace(name.Trim(), @"\s+", " ");
            yield return trimmed;

            var withoutQuality = Regex.Replace(
                trimmed,
                @"\s+(?:4K|UHD|FHD|HD|SD)\b",
                string.Empty,
                RegexOptions.IgnoreCase).Trim();

            if (!string.IsNullOrEmpty(withoutQuality) &&
                !string.Equals(withoutQuality, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                yield return withoutQuality;
            }
        }

        // Maps an Xtream JSON epg_channel_id to the canonical XMLTV channel id.
        // The JSON API sometimes appends a numeric provider suffix (e.g. "CBSKCBS.us7")
        // that the XMLTV feed omits (e.g. "CBSKCBS.us").  We strip trailing digits as
        // a fallback so the tuner channel links to the correct listing.
        internal static string ResolveToXmltvId(string epgChannelId, HashSet<string> xmltvIds)
        {
            if (string.IsNullOrEmpty(epgChannelId) || xmltvIds == null)
                return epgChannelId;

            if (xmltvIds.Contains(epgChannelId))
                return epgChannelId;

            // Strip trailing digits: "CBSKCBS.us7" → "CBSKCBS.us"
            var stripped = epgChannelId.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            if (!string.IsNullOrEmpty(stripped) && stripped != epgChannelId && xmltvIds.Contains(stripped))
                return stripped;

            return epgChannelId;
        }

        private static string GetXmltvUrl()
        {
            try
            {
                var cfg = Plugin.Instance.Configuration;
                if (cfg.EpgSource == EpgSourceMode.Disabled)
                    return null;

                if (cfg.EpgSource == EpgSourceMode.CustomUrl && !string.IsNullOrWhiteSpace(cfg.CustomEpgUrl))
                    return cfg.CustomEpgUrl;

                if (string.IsNullOrEmpty(cfg.BaseUrl) || string.IsNullOrEmpty(cfg.Username))
                    return null;

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/xmltv.php?username={1}&password={2}",
                    cfg.BaseUrl.TrimEnd('/'),
                    Uri.EscapeDataString(cfg.Username ?? string.Empty),
                    Uri.EscapeDataString(cfg.Password ?? string.Empty));
            }
            catch
            {
                return null;
            }
        }

        // XMLTV format: "20260413120000 +0000"  (no colon in offset)
        private static readonly Regex XmltvDateRx =
            new Regex(@"^(\d{14})\s*([+-]\d{4})?$", RegexOptions.Compiled);

        private static bool TryParseXmltvDate(string input, out DateTimeOffset result)
        {
            result = default(DateTimeOffset);
            if (string.IsNullOrEmpty(input)) return false;

            var m = XmltvDateRx.Match(input.Trim());
            if (!m.Success) return false;

            if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return false;

            TimeSpan offset = TimeSpan.Zero;
            if (m.Groups[2].Success)
            {
                var off = m.Groups[2].Value;
                int sign = off[0] == '+' ? 1 : -1;
                int hours = int.Parse(off.Substring(1, 2));
                int mins = int.Parse(off.Substring(3, 2));
                offset = new TimeSpan(sign * hours, sign * mins, 0);
            }

            result = new DateTimeOffset(dt, offset).ToUniversalTime();
            return true;
        }
    }
}
