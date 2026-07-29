using System;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;

namespace Emby.Xtream.Plugin.Service
{
    public class UpdateCheckResult
    {
        public bool UpdateAvailable { get; set; }
        public bool UpdateInstalled { get; set; }
        public string CurrentVersion { get; set; }
        public string LatestVersion { get; set; }
        public string ReleaseUrl { get; set; }
        public string ReleaseNotes { get; set; }
        public string PublishedAt { get; set; }
        public string DownloadUrl { get; set; }
        public string Error { get; set; }
        public bool IsPreRelease { get; set; }
    }

    public static class UpdateChecker
    {
        private const string GitHubApiUrl = "https://api.github.com/repos/suthar26/EMBY-XC/releases/latest";
        private const string GitHubAllReleasesUrl = "https://api.github.com/repos/suthar26/EMBY-XC/releases";
        private const string DllAssetName = "XC2EMBY.Plugin.dll";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

        private static UpdateCheckResult _cachedResult;
        private static DateTime _cacheTime = DateTime.MinValue;
        private static readonly object _cacheLock = new object();
        private static bool _updateInstalled;
        private static bool? _cachedForBetaChannel;

        public static bool UpdateInstalled
        {
            get { return _updateInstalled; }
            set { _updateInstalled = value; }
        }

        public static void InvalidateCache()
        {
            lock (_cacheLock)
            {
                _cachedResult = null;
                _cacheTime = DateTime.MinValue;
                _cachedForBetaChannel = null;
            }
        }

        public static async Task<UpdateCheckResult> CheckForUpdateAsync(bool? betaOverride = null)
        {
            // betaOverride (from query string) takes precedence; falls back to saved config.
            // This avoids relying on Emby's in-memory config cache being up-to-date immediately
            // after updatePluginConfiguration returns.
            var useBeta = betaOverride ?? Plugin.Instance?.Configuration?.UseBetaChannel ?? false;

            lock (_cacheLock)
            {
                // Invalidate cache if the channel preference changed since last fetch
                if (_cachedResult != null && _cachedForBetaChannel.HasValue && _cachedForBetaChannel.Value != useBeta)
                {
                    _cachedResult = null;
                    _cacheTime = DateTime.MinValue;
                    _cachedForBetaChannel = null;
                }

                if (_cachedResult != null && (DateTime.UtcNow - _cacheTime) < CacheTtl)
                {
                    return _cachedResult;
                }
            }

            var currentVersion = typeof(Plugin).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(Plugin).Assembly.GetName().Version?.ToString()
                ?? "0.0.0";

            UpdateCheckResult result;
            try
            {
                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Emby-Xtream-Plugin/1.0");
                    httpClient.Timeout = TimeSpan.FromSeconds(10);

                    string releaseJson;
                    if (useBeta)
                    {
                        var allJson = await httpClient.GetStringAsync(GitHubAllReleasesUrl).ConfigureAwait(false);
                        releaseJson = ExtractFirstRelease(allJson);
                    }
                    else
                    {
                        releaseJson = await httpClient.GetStringAsync(GitHubApiUrl).ConfigureAwait(false);
                    }

                    string tagName, htmlUrl, body, publishedAt, downloadUrl;
                    bool isPreRelease;
                    TryParseReleaseInfo(releaseJson, out tagName, out htmlUrl, out body, out publishedAt, out downloadUrl, out isPreRelease);

                    result = CompareVersions(currentVersion, tagName, htmlUrl, body, publishedAt);
                    result.DownloadUrl = downloadUrl;
                    result.UpdateInstalled = _updateInstalled;
                    result.IsPreRelease = isPreRelease;

                    // Suppress update banner if this version was already installed
                    if (result.UpdateAvailable && !_updateInstalled)
                    {
                        var config = Plugin.Instance?.Configuration;
                        if (config != null &&
                            !string.IsNullOrEmpty(config.LastInstalledVersion) &&
                            string.Equals(config.LastInstalledVersion, result.LatestVersion, StringComparison.OrdinalIgnoreCase))
                        {
                            result.UpdateAvailable = false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result = new UpdateCheckResult
                {
                    CurrentVersion = currentVersion,
                    Error = "Failed to check for updates: " + ex.Message,
                };
            }

            lock (_cacheLock)
            {
                _cachedResult = result;
                _cacheTime = DateTime.UtcNow;
                _cachedForBetaChannel = useBeta;
            }

            return result;
        }

        public static UpdateCheckResult CompareVersions(string currentVersion, string tagName, string releaseUrl, string body, string publishedAt)
        {
            var result = new UpdateCheckResult
            {
                CurrentVersion = currentVersion,
                ReleaseUrl = releaseUrl ?? "",
                ReleaseNotes = body ?? "",
                PublishedAt = publishedAt ?? "",
            };

            if (string.IsNullOrEmpty(tagName))
            {
                result.Error = "No tag found in release data.";
                return result;
            }

            var versionStr = tagName.TrimStart('v', 'V');
            result.LatestVersion = versionStr;

            Version current;
            Version latest;

            if (!Version.TryParse(NormalizeVersion(currentVersion), out current))
            {
                result.Error = "Could not parse current version: " + currentVersion;
                return result;
            }

            if (!Version.TryParse(NormalizeVersion(versionStr), out latest))
            {
                result.Error = "Could not parse release tag: " + tagName;
                return result;
            }

            result.UpdateAvailable = latest > current;
            return result;
        }

        private static string NormalizeVersion(string version)
        {
            // Version.Parse requires at least major.minor; pad if needed
            if (version == null) return "0.0";
            var dashIdx = version.IndexOf('-');
            if (dashIdx >= 0) version = version.Substring(0, dashIdx);
            var parts = version.Split('.');
            if (parts.Length == 1) return version + ".0";
            return version;
        }

        private static void TryParseReleaseInfo(string json, out string tagName, out string htmlUrl, out string body, out string publishedAt, out string downloadUrl, out bool prerelease)
        {
            tagName = null;
            htmlUrl = null;
            body = null;
            publishedAt = null;
            downloadUrl = null;
            prerelease = false;

            if (string.IsNullOrEmpty(json)) return;

            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    System.Text.Json.JsonElement t;
                    if (root.TryGetProperty("tag_name", out t)) tagName = t.GetString();
                    if (root.TryGetProperty("html_url", out t)) htmlUrl = t.GetString();
                    if (root.TryGetProperty("body", out t)) body = t.GetString();
                    if (root.TryGetProperty("published_at", out t)) publishedAt = t.GetString();
                    if (root.TryGetProperty("prerelease", out t) && t.ValueKind == System.Text.Json.JsonValueKind.True)
                        prerelease = true;

                    System.Text.Json.JsonElement assets;
                    if (root.TryGetProperty("assets", out assets) && assets.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            System.Text.Json.JsonElement nameEl;
                            if (asset.TryGetProperty("name", out nameEl) &&
                                string.Equals(nameEl.GetString(), DllAssetName, StringComparison.OrdinalIgnoreCase))
                            {
                                System.Text.Json.JsonElement urlEl;
                                if (asset.TryGetProperty("browser_download_url", out urlEl))
                                {
                                    downloadUrl = urlEl.GetString();
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static int FindMatchingBrace(string json, int openIdx)
        {
            if (openIdx < 0) return -1;
            var depth = 0;
            for (var i = openIdx; i < json.Length; i++)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        /// <summary>
        /// Extracts the first JSON object from a JSON array string (e.g. from /releases endpoint).
        /// Returns "{}" if the array is empty or the input is null/empty.
        /// </summary>
        public static string ExtractFirstRelease(string json)
        {
            if (string.IsNullOrEmpty(json)) return "{}";

            var openBrace = json.IndexOf('{');
            if (openBrace < 0) return "{}";

            var closeBrace = FindMatchingBrace(json, openBrace);
            if (closeBrace < 0) return "{}";

            return json.Substring(openBrace, closeBrace - openBrace + 1);
        }

    }
}
