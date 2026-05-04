using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Xtream.Plugin.Service
{
    public class SyncDocumentariesTask : IScheduledTask
    {
        private readonly ILogger _logger;

        public SyncDocumentariesTask(ILogManager logManager)
            => _logger = logManager.GetLogger("XtreamTuner.SyncDocumentariesTask");

        public string Name        => "XC2EMBY - Sync Documentaries";
        public string Description => "Sync documentary movie STRM files from your Xtream server.";
        public string Category    => "XC2EMBY";
        public string Key         => "XtreamTunerSyncDocumentaries";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(3.5).Ticks
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var config = Plugin.Instance.Configuration;
            if (!config.SyncDocumentaries)
            {
                _logger.Info("Documentary sync disabled - skipping.");
                return;
            }

            var svc = Plugin.Instance.StrmSyncService;
            if (svc.DocumentariesProgress.IsRunning || svc.MovieProgress.IsRunning)
            {
                _logger.Info("Movie/documentary sync already running - skipping scheduled run.");
                return;
            }

            var docConfig = BuildDocumentaryConfig(config);
            progress.Report(0);
            await svc.SyncMoviesAsync(
                docConfig,
                cancellationToken,
                () =>
                {
                    config.LastDocumentarySyncTimestamp = docConfig.LastMovieSyncTimestamp;
                    config.StrmNamingVersion = docConfig.StrmNamingVersion;
                    config.MovieTmdbCacheJson = docConfig.MovieTmdbCacheJson;
                    Plugin.Instance.SaveConfiguration();
                },
                progress,
                isDocumentaries: true).ConfigureAwait(false);

            config.LastDocumentarySyncTimestamp = docConfig.LastMovieSyncTimestamp;
            config.StrmNamingVersion = docConfig.StrmNamingVersion;
            config.MovieTmdbCacheJson = docConfig.MovieTmdbCacheJson;
            Plugin.Instance.SaveConfiguration();
            progress.Report(100);
        }

        private static PluginConfiguration BuildDocumentaryConfig(PluginConfiguration source)
        {
            return new PluginConfiguration
            {
                BaseUrl = source.BaseUrl,
                Username = source.Username,
                Password = source.Password,
                HttpUserAgent = source.HttpUserAgent,
                StrmLibraryPath = source.StrmLibraryPath,
                SyncMovies = source.SyncDocumentaries,
                MovieRootFolderName = source.DocumentaryRootFolderName,
                SelectedVodCategoryIds = source.SelectedDocumentaryCategoryIds ?? new int[0],
                MovieFolderMode = source.DocumentaryFolderMode,
                MovieFolderMappings = source.DocumentaryFolderMappings,
                EnableContentNameCleaning = source.EnableContentNameCleaning,
                ContentRemoveTerms = source.ContentRemoveTerms,
                EnableTmdbFolderNaming = source.EnableTmdbFolderNaming,
                EnableTmdbFallbackLookup = source.EnableTmdbFallbackLookup,
                MovieTmdbCacheJson = source.MovieTmdbCacheJson,
                EnableNfoFiles = source.EnableNfoFiles,
                SmartSkipExisting = source.SmartSkipExisting,
                EnableLocalMediaFilter = source.EnableLocalMediaFilter,
                SyncParallelism = source.SyncParallelism,
                CleanupOrphans = source.CleanupOrphans,
                OrphanSafetyThreshold = source.OrphanSafetyThreshold,
                LastMovieSyncTimestamp = source.LastDocumentarySyncTimestamp,
                StrmNamingVersion = source.StrmNamingVersion,
            };
        }
    }
}
