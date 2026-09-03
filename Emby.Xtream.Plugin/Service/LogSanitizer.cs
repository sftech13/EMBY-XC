using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Emby.Xtream.Plugin.Service
{
    public static class LogSanitizer
    {
        private static readonly Regex IpRegex = new Regex(
            @"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}",
            RegexOptions.Compiled);

        private static readonly Regex BracketedIpv6Regex = new Regex(
            @"\[(?<address>[0-9a-f:.]+(?:%[0-9a-z_.-]+)?)\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex LoggedSourceIpRegex = new Regex(
            @"(?<prefix>\b(?:Source|Remote|Client)\s+Ip:\s*)(?<address>\[[^\]]+\]|[^,\s]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex VersionContextRegex = new Regex(
            @"(?:Version[= ]|version |→ |-> )\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}",
            RegexOptions.Compiled);

        private static readonly Regex XtreamCredRegex = new Regex(
            @"/(live|movie|series)/[^/]+/[^/]+/",
            RegexOptions.Compiled);

        private static readonly Regex EmailRegex = new Regex(
            @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}",
            RegexOptions.Compiled);

        private static readonly Regex ProviderHostRegex = new Regex(
            @"(https?://)([^/:]+)(:\d+)?(/player_api\.php|/live/|/movie/|/series/)",
            RegexOptions.Compiled);

        private static readonly Regex EmbyTokenRegex = new Regex(
            @"X-Emby-Token=[a-zA-Z0-9]+",
            RegexOptions.Compiled);

        /// <summary>
        /// Sanitizes a single log line by redacting PII: known credentials, IP addresses,
        /// Xtream URL credentials, emails, and provider hostnames.
        /// </summary>
        public static string SanitizeLine(string line,
            string username, string password)
        {
            if (string.IsNullOrEmpty(line)) return line;

            var s = line;

            // Redact specific config values if non-empty
            if (!string.IsNullOrEmpty(username))
                s = s.Replace(username, "<redacted>");
            if (!string.IsNullOrEmpty(password))
                s = s.Replace(password, "<redacted>");

            // Redact IP addresses, but preserve version numbers (e.g. Version=1.2.0.0)
            // Replace version patterns with placeholders first, then redact IPs, then restore
            var versionMatches = VersionContextRegex.Matches(s);
            for (int i = versionMatches.Count - 1; i >= 0; i--)
            {
                var vm = versionMatches[i];
                s = s.Substring(0, vm.Index) + "\x1FVER" + i + "\x00" + s.Substring(vm.Index + vm.Length);
            }
            s = BracketedIpv6Regex.Replace(s, RedactBracketedIpv6);
            s = LoggedSourceIpRegex.Replace(s, RedactLoggedSourceIp);
            s = IpRegex.Replace(s, "<ip-redacted>");
            for (int i = 0; i < versionMatches.Count; i++)
            {
                s = s.Replace("\x1FVER" + i + "\x00", versionMatches[i].Value);
            }

            // Redact Xtream credentials in URLs: /live/user/pass/, /movie/user/pass/, /series/user/pass/
            s = XtreamCredRegex.Replace(s, "/$1/<user>/<pass>/");

            // Redact email patterns
            s = EmailRegex.Replace(s, "<email-redacted>");

            // Redact hostnames in stream URLs
            s = ProviderHostRegex.Replace(s, "$1<provider-host>$3$4");

            // Redact Emby auth tokens from HTTP request URLs
            s = EmbyTokenRegex.Replace(s, "X-Emby-Token=<token-redacted>");

            return s;
        }

        private static string RedactBracketedIpv6(Match match)
        {
            IPAddress address;
            return TryParseIp(match.Groups["address"].Value, out address) &&
                   address.AddressFamily == AddressFamily.InterNetworkV6
                ? "[<ip-redacted>]"
                : match.Value;
        }

        private static string RedactLoggedSourceIp(Match match)
        {
            var raw = match.Groups["address"].Value.Trim('[', ']');
            IPAddress address;
            return TryParseIp(raw, out address)
                ? match.Groups["prefix"].Value + "<ip-redacted>"
                : match.Value;
        }

        private static bool TryParseIp(string value, out IPAddress address)
        {
            // Zone identifiers are useful locally but still identify the host. Strip
            // them before parsing so addresses such as fe80::1%eth0 are redacted.
            var zoneIndex = value == null ? -1 : value.IndexOf('%');
            if (zoneIndex >= 0)
                value = value.Substring(0, zoneIndex);
            return IPAddress.TryParse(value, out address);
        }
    }
}
