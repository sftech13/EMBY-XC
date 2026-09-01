using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client;
using Emby.Xtream.Plugin.Client.Models;
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

        public XtreamListingsProvider() { _instance = this; }

        public void InvalidateCache()
        {
            // The single shared snapshot is owned and invalidated by LiveTvService.
        }

        public async Task<List<ProgramInfo>> GetProgramsAsync(
            ListingsProviderInfo info,
            string channelId,
            DateTimeOffset startDateUtc,
            DateTimeOffset endDateUtc,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(channelId))
                return new List<ProgramInfo>();

            var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot == null)
                return new List<ProgramInfo>();

            var programs = new List<ProgramInfo>();
            var shiftHours = ClampEpgTimeShiftHours(
                Plugin.InstanceOrNull?.Configuration?.EpgTimeShiftHours ?? 0);

            if (!snapshot.ProgramsByChannel.TryGetValue(channelId, out var channelProgs))
                channelProgs = new List<EpgProgram>();

            foreach (var prog in channelProgs)
            {
                if (prog.StartTimestamp == 0 || prog.StopTimestamp == 0)
                    continue;

                var start = ShiftEpgTimestamp(prog.StartTimestamp, shiftHours);
                var stop = ShiftEpgTimestamp(prog.StopTimestamp, shiftHours);

                if (stop <= startDateUtc || start >= endDateUtc)
                    continue;

                var title        = StripEpgQualifiers(prog.Title ?? "Unknown");
                var rawSubTitle  = prog.SubTitle;
                var episodeTitle = StripEpgQualifiers(rawSubTitle);
                var genres       = prog.Categories != null
                    ? new List<string>(prog.Categories)
                    : new List<string>();
                var isMovie      = genres.Any(c =>
                    c.IndexOf("movie", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    c.IndexOf("film", StringComparison.OrdinalIgnoreCase) >= 0);
                var isSports     = genres.Any(c =>
                    c.IndexOf("sport", StringComparison.OrdinalIgnoreCase) >= 0);
                var isNews       = genres.Any(c =>
                    c.IndexOf("news", StringComparison.OrdinalIgnoreCase) >= 0);
                var seriesKey    = XtreamUrlBuilder.NormalizeGuideKey(title);
                var episodeKey   = XtreamUrlBuilder.NormalizeGuideKey(episodeTitle);

                // ShowId drives PresentationUniqueKey → "Other Showings".
                // Scope it to series+episode when a sub-title is available so that
                // "Other Showings" finds only airings of that specific episode, not
                // every episode of the same series (e.g. only "Lizzo" guest airings of
                // The Drew Barrymore Show, not Charlize Theron/Belle Burden/etc.).
                // When no sub-title, use a unique key per airing (channelId + start)
                // so "Other Showings" never fires — null causes Emby to bucket all
                // null-ShowId programs together, linking completely unrelated programs.
                var showId = (!string.IsNullOrEmpty(seriesKey) && !string.IsNullOrEmpty(episodeKey))
                    ? seriesKey + "::" + episodeKey
                    : channelId + "::" + prog.StartTimestamp.ToString(CultureInfo.InvariantCulture);

                var program = new ProgramInfo
                {
                    ChannelId      = channelId,
                    Id             = string.Format(CultureInfo.InvariantCulture, "{0}_{1}", channelId, prog.StartTimestamp),
                    ShowId         = showId,
                    Name           = title,
                    Overview       = prog.Description,
                    StartDate      = start,
                    EndDate        = stop,
                    Genres         = genres,
                    ImageUrl       = prog.ImageUrl,
                    EpisodeTitle   = episodeTitle,
                    IsMovie        = isMovie,
                    IsSports       = isSports,
                    IsNews         = isNews,
                    IsSeries       = !isMovie,
                    IsLive         = prog.IsLive,
                    IsNew          = prog.IsNew,
                    IsRepeat       = prog.IsPreviouslyShown,
                    IsPremiere     = prog.IsPremiere,
                    OfficialRating = prog.Rating,
                    // SeriesId feeds SeriesPresentationUniqueKey — used by series timers
                    // to match all episodes of a show. Keep it series-level only.
                    SeriesId       = seriesKey,
                };

                // Season/episode from xmltv_ns: "S.E.part" (all 0-based)
                if (!string.IsNullOrEmpty(prog.EpisodeNumXmltvNs))
                {
                    var parts = prog.EpisodeNumXmltvNs.Split('.');
                    if (parts.Length >= 1 && int.TryParse(parts[0].Trim(), out var s))
                        program.SeasonNumber = s + 1;
                    if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out var ep))
                        program.EpisodeNumber = ep + 1;
                }

                // Fallback: onscreen "S01 E01" or "S1E1" when xmltv_ns is absent
                if (program.SeasonNumber == null)
                {
                    TryParseOnscreenEpisode(prog.EpisodeNumOnscreen, program);
                }

                program.ProductionYear = prog.ProductionYear;

                programs.Add(program);
            }

            return programs;
        }

        internal static double ClampEpgTimeShiftHours(double hours)
        {
            if (double.IsNaN(hours) || double.IsInfinity(hours)) return 0;
            return Math.Max(-12, Math.Min(12, hours));
        }

        internal static DateTimeOffset ShiftEpgTimestamp(long unixTimestamp, double hours)
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixTimestamp)
                .AddHours(ClampEpgTimeShiftHours(hours));
        }

        internal static DateTimeOffset GetEpgSourceBoundary(
            DateTimeOffset displayedBoundary,
            double hours)
        {
            return displayedBoundary.AddHours(-ClampEpgTimeShiftHours(hours));
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
            var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot == null)
                return new List<ChannelInfo>();

            var xmltvIds = new HashSet<string>(
                snapshot.DisplayNamesByChannel.Keys,
                StringComparer.OrdinalIgnoreCase);
            var sourceAliases = await GetSourceAliasesByXmltvIdAsync(xmltvIds, cancellationToken).ConfigureAwait(false);

            return snapshot.DisplayNamesByChannel
                .Select(entry =>
                {
                    var id = entry.Key;
                    var names = new List<string>(entry.Value);
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

        private static async Task<XmltvSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            var liveTvService = Plugin.Instance?.LiveTvService;
            return liveTvService == null
                ? null
                : await liveTvService.GetXmltvSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        // Called by XtreamTunerHost during channel build to normalize EpgChannelId → XMLTV id.
        internal async Task<HashSet<string>> GetXmltvChannelIdsAsync(CancellationToken cancellationToken)
        {
            var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return snapshot == null
                ? null
                : new HashSet<string>(snapshot.DisplayNamesByChannel.Keys, StringComparer.OrdinalIgnoreCase);
        }

        internal async Task<Dictionary<string, string[]>> GetXmltvChannelAliasesAsync(CancellationToken cancellationToken)
        {
            var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot == null)
                return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            var xmltvIds = new HashSet<string>(
                snapshot.DisplayNamesByChannel.Keys,
                StringComparer.OrdinalIgnoreCase);
            var sourceAliases = await GetSourceAliasesByXmltvIdAsync(xmltvIds, cancellationToken).ConfigureAwait(false);

            return snapshot.DisplayNamesByChannel
                .Select(entry =>
                {
                    var id = entry.Key;
                    var names = new List<string>(entry.Value);
                    if (sourceAliases.TryGetValue(id, out var aliases))
                        names.AddRange(aliases);

                    return new
                    {
                        Id = id,
                        Aliases = BuildGuideNameAliases(names).ToArray()
                    };
                })
                .Where(x => !string.IsNullOrEmpty(x.Id))
                .ToDictionary(x => x.Id, x => x.Aliases, StringComparer.OrdinalIgnoreCase);
        }

        internal async Task<Dictionary<string, int>> GetXmltvProgramCountsAsync(CancellationToken cancellationToken)
        {
            var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot == null)
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            return snapshot.ProgramsByChannel.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.Count,
                StringComparer.OrdinalIgnoreCase);
        }

        private async Task<Dictionary<string, List<string>>> GetSourceAliasesByXmltvIdAsync(
            HashSet<string> xmltvIds,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var liveTvService = Plugin.Instance?.LiveTvService;
                if (liveTvService == null)
                    return result;

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

    }
}
