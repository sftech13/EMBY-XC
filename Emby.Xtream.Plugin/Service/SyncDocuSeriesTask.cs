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
            if (GetConfigurationForRun() == null) return;

            PluginConfiguration config = null;
            PluginConfiguration docConfig = null;
            Action saveConfig = () =>
            {
                config.LastDocuSeriesSyncTimestamp = docConfig.LastSeriesSyncTimestamp;
                config.DocuSeriesEpisodeHashesJson = docConfig.SeriesEpisodeHashesJson;
                config.DocuSeriesCategorySnapshotJson = docConfig.SeriesCategorySnapshotJson;
                config.DocuSeriesFastPathSettingsHash = docConfig.SeriesFastPathSettingsHash;
                config.LastDocuSeriesFullVerificationUtcTicks = docConfig.LastSeriesFullVerificationUtcTicks;
                config.DocuSeriesPlaybackValidationJson = docConfig.SeriesPlaybackValidationJson;
                config.DocuSeriesCatalogObservationJson = docConfig.SeriesCatalogObservationJson;
                config.PendingOrphansJson = docConfig.PendingOrphansJson;
                config.StrmNamingVersion = docConfig.StrmNamingVersion;
                Plugin.Instance.SaveConfiguration();
            };
            progress.Report(0);
            await Plugin.Instance.StrmSyncService.SyncSeriesWhenReadyAsync(
                () =>
                {
                    config = GetConfigurationForRun();
                    docConfig = config == null ? null : BuildDocuSeriesConfig(config);
                    return docConfig;
                },
                cancellationToken,
                saveConfig,
                progress,
                isDocuSeries: true,
                onCompleted: saveConfig).ConfigureAwait(false);

            if (docConfig == null) return;
            progress.Report(100);
        }

        private PluginConfiguration GetConfigurationForRun()
        {
            var config = Plugin.Instance.Configuration;
            if (!config.AutoSyncEnabled)
            {
                _logger.Info("Automatic sync disabled — skipping scheduled docu-series sync.");
                return null;
            }
            if (!config.SyncDocuSeries)
            {
                _logger.Info("Docu Series sync disabled - skipping.");
                return null;
            }

            var health = Plugin.Instance.ProviderHealth;
            var lastChecked = health.LastChecked;
            if (lastChecked.HasValue &&
                (DateTime.UtcNow - lastChecked.Value).TotalMinutes < 15 &&
                !health.IsReachable)
            {
                _logger.Warn("Skipping scheduled docu-series sync — provider is unreachable (last error: {0}). Will retry at next scheduled time.", health.LastError ?? "unknown");
                return null;
            }
            return config;
        }

        private static PluginConfiguration BuildDocuSeriesConfig(PluginConfiguration source)
        {
            return new PluginConfiguration
            {
                BaseUrl = source.BaseUrl,
                Username = source.Username,
                Password = source.Password,
                HttpUserAgent = source.HttpUserAgent,
                TunerCount = source.TunerCount,
                StrmLibraryPath = source.StrmLibraryPath,
                SyncSeries = source.SyncDocuSeries,
                SeriesRootFolderName = source.DocuSeriesRootFolderName,
                SelectedSeriesCategoryIds = source.EnableGenreBasedLibraryRouting
                    ? source.SelectedSeriesCategoryIds ?? new int[0]
                    : source.SelectedDocuSeriesCategoryIds ?? new int[0],
                SeriesFolderMode = source.DocuSeriesFolderMode,
                SeriesFolderMappings = source.DocuSeriesFolderMappings,
                EnableContentNameCleaning = source.EnableContentNameCleaning,
                ContentRemoveTerms = source.ContentRemoveTerms,
                EnableTmdbFolderNaming = source.EnableTmdbFolderNaming,
                EnableTmdbFallbackLookup = source.EnableTmdbFallbackLookup,
                EnableSeriesIdFolderNaming = source.EnableSeriesIdFolderNaming,
                EnableSeriesMetadataLookup = source.EnableSeriesMetadataLookup,
                TvdbFolderIdOverrides = source.TvdbFolderIdOverrides,
                EnableGenreBasedLibraryRouting = source.EnableGenreBasedLibraryRouting,
                EnableNfoFiles = source.EnableNfoFiles,
                CachedSeriesCategories = source.CachedSeriesCategories,
                SmartSkipExisting = source.SmartSkipExisting,
                EnableLocalMediaFilter = source.EnableLocalMediaFilter,
                SyncParallelism = source.SyncParallelism,
                CleanupOrphans = source.CleanupOrphans,
                OrphanSafetyThreshold = source.OrphanSafetyThreshold,
                EnableOrphanPreview = source.EnableOrphanPreview,
                PendingOrphansJson = source.PendingOrphansJson,
                LastSeriesSyncTimestamp = source.LastDocuSeriesSyncTimestamp,
                SeriesEpisodeHashesJson = source.DocuSeriesEpisodeHashesJson,
                SeriesCategorySnapshotJson = source.DocuSeriesCategorySnapshotJson,
                SeriesFastPathSettingsHash = source.DocuSeriesFastPathSettingsHash,
                LastSeriesFullVerificationUtcTicks = source.LastDocuSeriesFullVerificationUtcTicks,
                SeriesPlaybackValidationJson = source.DocuSeriesPlaybackValidationJson,
                SeriesCatalogObservationJson = source.DocuSeriesCatalogObservationJson,
                StrmNamingVersion = source.StrmNamingVersion,
            };
        }
    }
}
