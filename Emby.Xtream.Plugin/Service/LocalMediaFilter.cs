using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace Emby.Xtream.Plugin.Service
{
    internal sealed class LocalMediaFilter
    {
        // Matches a standalone 4-digit year in parens, e.g. (2005). Captured so we can
        // keep the year as a plain number instead of discarding it entirely.
        private static readonly Regex ExtractParenYear = new Regex(@"\((\d{4})\)", RegexOptions.Compiled);
        private static readonly Regex StripParens = new Regex(@"\([^)]*\)", RegexOptions.Compiled);
        private static readonly Regex StripNonAlpha = new Regex(@"[^a-z0-9\s]", RegexOptions.Compiled);
        private static readonly Regex CollapseSpace = new Regex(@"\s+", RegexOptions.Compiled);
        // Matches a trailing 4-digit year appended by NormalizeTitle, used for fallback lookup.
        private static readonly Regex TrailingYear = new Regex(@"\s\d{4}$", RegexOptions.Compiled);
        // Matches {tmdb-12345} in paths (Radarr/Sonarr optional TMDB tag)
        private static readonly Regex PathTmdbTag = new Regex(@"[\{\[]tmdb-(\d+)[\}\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Matches {imdb-tt12345} (Radarr file names) and [imdb-tt12345] (Sonarr folder names)
        private static readonly Regex PathImdbTag = new Regex(@"[\{\[]imdb-(tt\d+)[\}\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly HashSet<string> _movieTmdbIds;
        private readonly HashSet<string> _movieImdbIds;
        private readonly HashSet<string> _movieTitles;
        private readonly HashSet<string> _episodeTmdbKeys;
        private readonly HashSet<string> _episodeTitleKeys;

        private LocalMediaFilter(
            HashSet<string> movieTmdbIds, HashSet<string> movieImdbIds, HashSet<string> movieTitles,
            HashSet<string> episodeTmdbKeys, HashSet<string> episodeTitleKeys)
        {
            _movieTmdbIds = movieTmdbIds;
            _movieImdbIds = movieImdbIds;
            _movieTitles = movieTitles;
            _episodeTmdbKeys = episodeTmdbKeys;
            _episodeTitleKeys = episodeTitleKeys;
        }

        internal static LocalMediaFilter Build(ILogger logger, string strmLibraryPath)
        {
            var movieTmdbIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var movieImdbIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var movieTitles = new HashSet<string>(StringComparer.Ordinal);
            var episodeTmdbKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var episodeTitleKeys = new HashSet<string>(StringComparer.Ordinal);
            var excludedRootPath = NormalizePath(strmLibraryPath);

            try
            {
                var host = Plugin.Instance?.ApplicationHost;
                if (host == null)
                {
                    logger.Warn("LocalMediaFilter: ApplicationHost not available");
                    return new LocalMediaFilter(movieTmdbIds, movieImdbIds, movieTitles, episodeTmdbKeys, episodeTitleKeys);
                }

                var libraryManager = host.Resolve<ILibraryManager>();
                if (libraryManager == null)
                {
                    logger.Warn("LocalMediaFilter: ILibraryManager could not be resolved");
                    return new LocalMediaFilter(movieTmdbIds, movieImdbIds, movieTitles, episodeTmdbKeys, episodeTitleKeys);
                }

                var movies = libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Movie" },
                    Recursive = true,
                });
                foreach (var item in movies)
                {
                    if (IsUnderRoot(item, excludedRootPath))
                        continue;

                    string id;
                    if (TryGetProviderId(item.ProviderIds, "Tmdb", out id))
                        movieTmdbIds.Add(id);
                    else
                        ExtractPathTmdbId(item.Path, movieTmdbIds);

                    if (TryGetProviderId(item.ProviderIds, "Imdb", out id))
                        movieImdbIds.Add(id);
                    else
                        ExtractPathImdbId(item.Path, movieImdbIds);

                    AddTitleKeys(movieTitles, item.Name, item.ProductionYear);
                }

                var episodes = libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Episode" },
                    Recursive = true,
                });
                foreach (var item in episodes)
                {
                    if (IsUnderRoot(item, excludedRootPath))
                        continue;

                    var episode = item as Episode;
                    if (episode == null || !episode.ParentIndexNumber.HasValue || !episode.IndexNumber.HasValue)
                        continue;

                    var episodeSeries = episode.Series;
                    if (episodeSeries == null)
                        continue;

                    var seasonNum = episode.ParentIndexNumber.Value;
                    var startEpisodeNum = episode.IndexNumber.Value;
                    var endEpisodeNum = episode.IndexNumberEnd.HasValue
                        ? Math.Max(startEpisodeNum, episode.IndexNumberEnd.Value)
                        : startEpisodeNum;

                    string id;
                    if (TryGetProviderId(episodeSeries.ProviderIds, "Tmdb", out id))
                    {
                        for (var episodeNum = startEpisodeNum; episodeNum <= endEpisodeNum; episodeNum++)
                            episodeTmdbKeys.Add(BuildEpisodeKey(id, seasonNum, episodeNum));
                    }

                    var titleOnly = NormalizeTitle(episodeSeries.Name);
                    if (!string.IsNullOrEmpty(titleOnly))
                    {
                        for (var episodeNum = startEpisodeNum; episodeNum <= endEpisodeNum; episodeNum++)
                            episodeTitleKeys.Add(BuildEpisodeKey(titleOnly, seasonNum, episodeNum));
                    }

                    if (episodeSeries.ProductionYear.HasValue && episodeSeries.ProductionYear.Value > 0)
                    {
                        var withYear = NormalizeTitle(episodeSeries.Name + " " + episodeSeries.ProductionYear.Value);
                        if (!string.IsNullOrEmpty(withYear))
                        {
                            for (var episodeNum = startEpisodeNum; episodeNum <= endEpisodeNum; episodeNum++)
                                episodeTitleKeys.Add(BuildEpisodeKey(withYear, seasonNum, episodeNum));
                        }
                    }
                }

                logger.Info("Local media filter: {0} local movies ({1} TMDB, {2} IMDB), {3} local episode keys; excluded STRM root '{4}'",
                    movieTitles.Count, movieTmdbIds.Count, movieImdbIds.Count,
                    episodeTmdbKeys.Count + episodeTitleKeys.Count, excludedRootPath ?? string.Empty);

                if (movies.Length == 0)
                    logger.Warn("Local media filter: 0 movies returned — library may not be indexed yet; filter will not block any movies this run");
            }
            catch (Exception ex)
            {
                logger.Warn("Local media filter: failed to query Emby library — {0}", ex.Message);
            }

            return new LocalMediaFilter(movieTmdbIds, movieImdbIds, movieTitles, episodeTmdbKeys, episodeTitleKeys);
        }

        internal bool ContainsMovie(string tmdbId, string imdbId, string cleanedName)
        {
            if (!string.IsNullOrEmpty(tmdbId) && _movieTmdbIds.Contains(tmdbId)) return true;
            if (!string.IsNullOrEmpty(imdbId) && _movieImdbIds.Contains(imdbId)) return true;
            var norm = NormalizeTitle(cleanedName);
            if (string.IsNullOrEmpty(norm)) return false;
            if (_movieTitles.Contains(norm)) return true;
            // XC name had "(YYYY)" → norm ends with year; also try without year so
            // "The Office (2005)" still matches a local "The Office" missing year metadata.
            var noYear = TrailingYear.Replace(norm, string.Empty);
            return noYear.Length < norm.Length && _movieTitles.Contains(noYear);
        }

        internal bool ContainsEpisode(string tmdbId, string cleanedName, int seasonNum, int episodeNum)
        {
            if (seasonNum < 0 || episodeNum <= 0) return false;
            if (!string.IsNullOrEmpty(tmdbId) && _episodeTmdbKeys.Contains(BuildEpisodeKey(tmdbId, seasonNum, episodeNum)))
                return true;
            var norm = NormalizeTitle(cleanedName);
            if (string.IsNullOrEmpty(norm)) return false;
            if (_episodeTitleKeys.Contains(BuildEpisodeKey(norm, seasonNum, episodeNum))) return true;
            var noYear = TrailingYear.Replace(norm, string.Empty);
            return noYear.Length < norm.Length && _episodeTitleKeys.Contains(BuildEpisodeKey(noYear, seasonNum, episodeNum));
        }

        internal static string NormalizeTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return string.Empty;
            var s = title.ToLowerInvariant();
            s = ExtractParenYear.Replace(s, " $1 "); // (2005) → 2005; keep year as plain number
            s = StripParens.Replace(s, " ");          // strip remaining (US), (UK), etc.
            s = StripNonAlpha.Replace(s, " ");
            s = CollapseSpace.Replace(s, " ");
            return s.Trim();
        }

        private static bool TryGetProviderId(Dictionary<string, string> providerIds, string key, out string id)
        {
            id = null;
            if (providerIds == null)
                return false;

            foreach (var pair in providerIds)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(pair.Value))
                {
                    id = pair.Value.Trim();
                    return true;
                }
            }

            return false;
        }

        private static void AddTitleKeys(HashSet<string> titles, string name, int? productionYear)
        {
            var titleOnly = NormalizeTitle(name);
            if (!string.IsNullOrEmpty(titleOnly))
                titles.Add(titleOnly);

            if (productionYear.HasValue && productionYear.Value > 0)
            {
                var withYear = NormalizeTitle(name + " " + productionYear.Value);
                if (!string.IsNullOrEmpty(withYear))
                    titles.Add(withYear);
            }
        }

        private static string BuildEpisodeKey(string seriesKey, int seasonNum, int episodeNum)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}|S{1}|E{2}", seriesKey, seasonNum, episodeNum);
        }

        private static bool IsUnderRoot(BaseItem item, string rootPath)
        {
            if (item == null || string.IsNullOrEmpty(rootPath))
                return false;

            return IsUnderRoot(item.Path, rootPath) ||
                   IsUnderRoot(item.ContainingFolderPath, rootPath);
        }

        private static bool IsUnderRoot(string path, string rootPath)
        {
            var normalizedPath = NormalizePath(path);
            if (string.IsNullOrEmpty(normalizedPath) || string.IsNullOrEmpty(rootPath))
                return false;

            return string.Equals(normalizedPath, rootPath, StringComparison.OrdinalIgnoreCase) ||
                   normalizedPath.StartsWith(rootPath + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   normalizedPath.StartsWith(rootPath + System.IO.Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void ExtractPathTmdbId(string path, HashSet<string> set)
        {
            if (string.IsNullOrEmpty(path)) return;
            var m = PathTmdbTag.Match(path);
            if (m.Success) set.Add(m.Groups[1].Value);
        }

        private static void ExtractPathImdbId(string path, HashSet<string> set)
        {
            if (string.IsNullOrEmpty(path)) return;
            var m = PathImdbTag.Match(path);
            if (m.Success) set.Add(m.Groups[1].Value);
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            return path.Trim()
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        }
    }
}
