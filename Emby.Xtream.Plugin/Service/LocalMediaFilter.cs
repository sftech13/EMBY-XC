using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace Emby.Xtream.Plugin.Service
{
    internal sealed class LocalMediaFilter
    {
        private static readonly Regex StripYear = new Regex(@"\b(19|20)\d{2}\b", RegexOptions.Compiled);
        private static readonly Regex StripNonAlpha = new Regex(@"[^a-z0-9\s]", RegexOptions.Compiled);
        private static readonly Regex CollapseSpace = new Regex(@"\s+", RegexOptions.Compiled);

        private readonly HashSet<string> _movieTmdbIds;
        private readonly HashSet<string> _movieTitles;
        private readonly HashSet<string> _seriesTmdbIds;
        private readonly HashSet<string> _seriesTitles;

        private LocalMediaFilter(
            HashSet<string> movieTmdbIds, HashSet<string> movieTitles,
            HashSet<string> seriesTmdbIds, HashSet<string> seriesTitles)
        {
            _movieTmdbIds = movieTmdbIds;
            _movieTitles = movieTitles;
            _seriesTmdbIds = seriesTmdbIds;
            _seriesTitles = seriesTitles;
        }

        internal static LocalMediaFilter Build(ILogger logger)
        {
            var movieTmdbIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var movieTitles = new HashSet<string>(StringComparer.Ordinal);
            var seriesTmdbIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seriesTitles = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                var host = Plugin.Instance?.ApplicationHost;
                if (host == null)
                {
                    logger.Warn("LocalMediaFilter: ApplicationHost not available");
                    return new LocalMediaFilter(movieTmdbIds, movieTitles, seriesTmdbIds, seriesTitles);
                }

                var libraryManager = host.Resolve<ILibraryManager>();
                if (libraryManager == null)
                {
                    logger.Warn("LocalMediaFilter: ILibraryManager could not be resolved");
                    return new LocalMediaFilter(movieTmdbIds, movieTitles, seriesTmdbIds, seriesTitles);
                }

                var movies = libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Movie" },
                    Recursive = true,
                });
                foreach (var item in movies)
                {
                    string id;
                    if (item.ProviderIds != null && item.ProviderIds.TryGetValue("Tmdb", out id) && !string.IsNullOrEmpty(id))
                        movieTmdbIds.Add(id);
                    var norm = NormalizeTitle(item.Name);
                    if (!string.IsNullOrEmpty(norm))
                        movieTitles.Add(norm);
                }

                var series = libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Series" },
                    Recursive = true,
                });
                foreach (var item in series)
                {
                    string id;
                    if (item.ProviderIds != null && item.ProviderIds.TryGetValue("Tmdb", out id) && !string.IsNullOrEmpty(id))
                        seriesTmdbIds.Add(id);
                    var norm = NormalizeTitle(item.Name);
                    if (!string.IsNullOrEmpty(norm))
                        seriesTitles.Add(norm);
                }

                logger.Info("Local media filter: {0} movies ({1} TMDB IDs), {2} series ({3} TMDB IDs) from Emby library",
                    movies.Length, movieTmdbIds.Count, series.Length, seriesTmdbIds.Count);
            }
            catch (Exception ex)
            {
                logger.Warn("Local media filter: failed to query Emby library — {0}", ex.Message);
            }

            return new LocalMediaFilter(movieTmdbIds, movieTitles, seriesTmdbIds, seriesTitles);
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

        internal static string NormalizeTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return string.Empty;
            var s = title.ToLowerInvariant();
            s = StripYear.Replace(s, " ");
            s = StripNonAlpha.Replace(s, " ");
            s = CollapseSpace.Replace(s, " ");
            return s.Trim();
        }
    }
}
