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

        public string Name     => "Xtream EPG";
        public string Type     => ProviderType;
        public string SetupUrl => string.Empty;

        // ── XMLTV cache ───────────────────────────────────────────────────────

        private XDocument _cachedXml;
        private DateTimeOffset _cacheExpiry = DateTimeOffset.MinValue;
        private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);

        // ── IListingsProvider ─────────────────────────────────────────────────

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
                if (!string.Equals(prog.Attribute("channel")?.Value, channelId,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                var startStr = prog.Attribute("start")?.Value;
                var stopStr  = prog.Attribute("stop")?.Value;

                if (!TryParseXmltvDate(startStr, out var start) ||
                    !TryParseXmltvDate(stopStr,  out var stop))
                    continue;

                if (stop <= startDateUtc || start >= endDateUtc)
                    continue;

                var programId = string.Format(CultureInfo.InvariantCulture, "{0}_{1}", channelId, startStr);
                var program = new ProgramInfo
                {
                    ChannelId = channelId,
                    Id        = programId,
                    ShowId    = programId,   // unique per channel — prevents cross-channel "Other Showings"
                    Name      = prog.Element("title")?.Value ?? "Unknown",
                    Overview  = prog.Element("desc")?.Value,
                    StartDate = start,
                    EndDate   = stop,
                    Genres    = prog.Elements("category").Select(e => e.Value).ToList(),
                    ImageUrl  = prog.Element("icon")?.Attribute("src")?.Value,
                };

                programs.Add(program);
            }

            return programs;
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

            return doc.Descendants("channel")
                .Select(c => new ChannelInfo
                {
                    Id   = c.Attribute("id")?.Value ?? string.Empty,
                    Name = c.Element("display-name")?.Value ?? string.Empty,
                })
                .Where(c => !string.IsNullOrEmpty(c.Id))
                .ToList();
        }

        // ── Cache ─────────────────────────────────────────────────────────────

        private async Task<XDocument> GetCachedXmlAsync(CancellationToken cancellationToken)
        {
            if (_cachedXml != null && DateTimeOffset.UtcNow < _cacheExpiry)
                return _cachedXml;

            await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Double-checked locking
                if (_cachedXml != null && DateTimeOffset.UtcNow < _cacheExpiry)
                    return _cachedXml;

                var url = GetXmltvUrl();
                if (string.IsNullOrEmpty(url))
                    return null;

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
                return null;
            }
            finally
            {
                _cacheLock.Release();
            }
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
                    cfg.BaseUrl.TrimEnd('/'), cfg.Username, cfg.Password);
            }
            catch { return null; }
        }

        // ── XMLTV date parser ─────────────────────────────────────────────────

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
                int sign  = off[0] == '+' ? 1 : -1;
                int hours = int.Parse(off.Substring(1, 2));
                int mins  = int.Parse(off.Substring(3, 2));
                offset = new TimeSpan(sign * hours, sign * mins, 0);
            }

            result = new DateTimeOffset(dt, offset).ToUniversalTime();
            return true;
        }
    }
}
