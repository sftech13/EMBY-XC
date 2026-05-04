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
        private static readonly Regex StripParens = new Regex(@"\([^)]*\)", RegexOptions.Compiled);
        private static readonly Regex StripNonAlpha = new Regex(@"[^a-z0-9\s]", RegexOptions.Compiled);
        private static readonly Regex CollapseSpace = new Regex(@"\s+", RegexOptions.Compiled);

        private readonly HashSet<string> _movieTmdbIds;
        private readonly HashSet<string> _movieTitles;
        private readonly HashSet<string> _seriesTmdbIds;
        private readonly HashSet<string> _seriesTitles;
        private readonly HashSet<string> _episodeTmdbKeys;
        private readonly HashSet<string> _episodeTitleKeys;

        private LocalMediaFilter(
            HashSet<string> movieTmdbIds, HashSet<string> movieTitles,
            HashSet<string> seriesTmdbIds, HashSet<string> seriesTitles,
            HashSet<string> episodeTmdbKeys, HashSet<string> episodeTitleKeys)
        {
            _movieTmdbIds = movieTmdbIds;
            _movieTitles = movieTitles;
            _seriesTmdbIds = seriesTmdbIds;
            _seriesTitles = seriesTitles;
            _episodeTmdbKeys = episodeTmdbKeys;
            _episodeTitleKeys = episodeTitleKeys;
        }

        internal static LocalMediaFilter Build(ILogger logger, string strmLibraryPath)
        {
            var movieTmdbIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var movieTitles = new HashSet<string>(StringComparer.Ordinal);
            var seriesTmdbIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seriesTitles = new HashSet<string>(StringComparer.Ordinal);
            var episodeTmdbKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var episodeTitleKeys = new HashSet<string>(StringComparer.Ordinal);
            var excludedRootPath = NormalizePath(strmLibraryPath);

            try
            {
                var host = Plugin.Instance?.ApplicationHost;
                if (host == null)
                {
                    logger.Warn("LocalMediaFilter: ApplicationHost not available");
                    return new LocalMediaFilter(movieTmdbIds, movieTitles, seriesTmdbIds, seriesTitles, episodeTmdbKeys, episodeTitleKeys);
                }

                var libraryManager = host.Resolve<ILibraryManager>();
                if (libraryManager == null)
                {
                    logger.Warn("LocalMediaFilter: ILibraryManager could not be resolved");
                    return new LocalMediaFilter(movieTmdbIds, movieTitles, seriesTmdbIds, seriesTitles, episodeTmdbKeys, episodeTitleKeys);
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
                    AddTitleKeys(movieTitles, item.Name, item.ProductionYear);
                }

                var series = libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Series" },
                    Recursive = true,
                });
                foreach (var item in series)
                {
                    if (IsUnderRoot(item, excludedRootPath))
                        continue;

                    string id;
                    if (TryGetProviderId(item.ProviderIds, "Tmdb", out id))
                        seriesTmdbIds.Add(id);
                    AddTitleKeys(seriesTitles, item.Name, item.ProductionYear);
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

                logger.Info("Local media filter: {0} local movies ({1} TMDB IDs), {2} local series ({3} TMDB IDs), {4} local episode keys from Emby library; excluded STRM root '{5}'",
                    movieTitles.Count, movieTmdbIds.Count, seriesTitles.Count, seriesTmdbIds.Count, episodeTmdbKeys.Count + episodeTitleKeys.Count, excludedRootPath ?? string.Empty);

                if (movies.Length == 0)
                    logger.Warn("Local media filter: 0 movies returned — library may not be indexed yet; filter will not block any movies this run");
            }
            catch (Exception ex)
            {
                logger.Warn("Local media filter: failed to query Emby library — {0}", ex.Message);
            }

            return new LocalMediaFilter(movieTmdbIds, movieTitles, seriesTmdbIds, seriesTitles, episodeTmdbKeys, episodeTitleKeys);
        }

        internal bool ContainsMovie(string tmdbId, string cleanedName)
        {
            if (!string.IsNullOrEmpty(tmdbId) && _movieTmdbIds.Contains(tmdbId))
                return true;
            var norm = NormalizeTitle(cleanedName);
            return !string.IsNullOrEmpty(norm) && _movieTitles.Contains(norm);
        }

        internal bool ContainsSeries(string tmdbId, string cleanedName)
        {
            if (!string.IsNullOrEmpty(tmdbId) && _seriesTmdbIds.Contains(tmdbId))
                return true;
            var norm = NormalizeTitle(cleanedName);
            return !string.IsNullOrEmpty(norm) && _seriesTitles.Contains(norm);
        }

        internal bool ContainsEpisode(string tmdbId, string cleanedName, int seasonNum, int episodeNum)
        {
            if (seasonNum < 0 || episodeNum <= 0)
                return false;

            if (!string.IsNullOrEmpty(tmdbId) && _episodeTmdbKeys.Contains(BuildEpisodeKey(tmdbId, seasonNum, episodeNum)))
                return true;

            var norm = NormalizeTitle(cleanedName);
            return !string.IsNullOrEmpty(norm) && _episodeTitleKeys.Contains(BuildEpisodeKey(norm, seasonNum, episodeNum));
        }

        internal static string NormalizeTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return string.Empty;
            var s = title.ToLowerInvariant();
            s = StripParens.Replace(s, " ");    // remove (US), (UK), (2010), etc.
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

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            return path.Trim()
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        }
    }
}
