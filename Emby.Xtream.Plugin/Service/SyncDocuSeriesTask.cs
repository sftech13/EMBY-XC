using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Xtream.Plugin.Service
{
    public class SyncDocuSeriesTask : IScheduledTask
    {
        private readonly ILogger _logger;

        public SyncDocuSeriesTask(ILogManager logManager)
            => _logger = logManager.GetLogger("XtreamTuner.SyncDocuSeriesTask");

        public string Name        => "XC2EMBY - Sync Docu Series";
        public string Description => "Sync documentary series STRM files from your Xtream server.";
        public string Category    => "XC2EMBY";
        public string Key         => "XtreamTunerSyncDocuSeries";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(4.5).Ticks
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var config = Plugin.Instance.Configuration;
            if (!config.SyncDocuSeries)
            {
                _logger.Info("Docu Series sync disabled - skipping.");
                return;
            }

            var svc = Plugin.Instance.StrmSyncService;
            if (svc.SeriesProgress.IsRunning)
            {
                _logger.Info("TV show/docu series sync already running - skipping scheduled run.");
                return;
            }

            var docConfig = BuildDocuSeriesConfig(config);
            progress.Report(0);
            await svc.SyncSeriesAsync(
                docConfig,
                cancellationToken,
                () =>
                {
                    config.LastDocuSeriesSyncTimestamp = docConfig.LastSeriesSyncTimestamp;
                    config.DocuSeriesEpisodeHashesJson = docConfig.SeriesEpisodeHashesJson;
                    config.StrmNamingVersion = docConfig.StrmNamingVersion;
                    Plugin.Instance.SaveConfiguration();
                },
                progress).ConfigureAwait(false);

            config.LastDocuSeriesSyncTimestamp = docConfig.LastSeriesSyncTimestamp;
            config.DocuSeriesEpisodeHashesJson = docConfig.SeriesEpisodeHashesJson;
            config.StrmNamingVersion = docConfig.StrmNamingVersion;
            Plugin.Instance.SaveConfiguration();
            progress.Report(100);
        }

        private static PluginConfiguration BuildDocuSeriesConfig(PluginConfiguration source)
        {
            return new PluginConfiguration
            {
                BaseUrl = source.BaseUrl,
                Username = source.Username,
                Password = source.Password,
                HttpUserAgent = source.HttpUserAgent,
                StrmLibraryPath = source.StrmLibraryPath,
                SyncSeries = source.SyncDocuSeries,
                SeriesRootFolderName = source.DocuSeriesRootFolderName,
                SelectedSeriesCategoryIds = source.SelectedDocuSeriesCategoryIds ?? new int[0],
                SeriesFolderMode = source.DocuSeriesFolderMode,
                SeriesFolderMappings = source.DocuSeriesFolderMappings,
                EnableContentNameCleaning = source.EnableContentNameCleaning,
                ContentRemoveTerms = source.ContentRemoveTerms,
                EnableSeriesIdFolderNaming = source.EnableSeriesIdFolderNaming,
                EnableSeriesMetadataLookup = source.EnableSeriesMetadataLookup,
                TvdbFolderIdOverrides = source.TvdbFolderIdOverrides,
                EnableNfoFiles = source.EnableNfoFiles,
                SmartSkipExisting = source.SmartSkipExisting,
                SyncParallelism = source.SyncParallelism,
                CleanupOrphans = source.CleanupOrphans,
                OrphanSafetyThreshold = source.OrphanSafetyThreshold,
                LastSeriesSyncTimestamp = source.LastDocuSeriesSyncTimestamp,
                SeriesEpisodeHashesJson = source.DocuSeriesEpisodeHashesJson,
                StrmNamingVersion = source.StrmNamingVersion,
            };
        }
    }
}
