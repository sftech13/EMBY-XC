using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Xtream.Plugin.Service
{
    public class SyncSeriesTask : IScheduledTask
    {
        private readonly ILogger _logger;

        public SyncSeriesTask(ILogManager logManager)
            => _logger = logManager.GetLogger("XtreamTuner.SyncSeriesTask");

        public string Name        => "XC2EMBY - Sync TV Shows";
        public string Description => "Sync TV show STRM files from your Xtream server.";
        public string Category    => "XC2EMBY";
        public string Key         => "XtreamTunerSyncSeries";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // One-time default (first install). Plugin UI overrides this via Emby's trigger API.
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks  // 04:00 (offset from movies)
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (GetConfigurationForRun() == null) return;

            progress.Report(0);
            await Plugin.Instance.StrmSyncService.SyncSeriesWhenReadyAsync(
                GetConfigurationForRun,
                cancellationToken,
                () => Plugin.Instance.SaveConfiguration(),
                progress).ConfigureAwait(false);
            progress.Report(100);
        }

        private PluginConfiguration GetConfigurationForRun()
        {
            var config = Plugin.Instance.Configuration;
            if (!config.AutoSyncEnabled)
            {
                _logger.Info("Automatic sync disabled — skipping scheduled series sync.");
                return null;
            }
            if (!config.SyncSeries)
            {
                _logger.Info("Series sync disabled — skipping.");
                return null;
            }

            var health = Plugin.Instance.ProviderHealth;
            var lastChecked = health.LastChecked;
            if (lastChecked.HasValue &&
                (DateTime.UtcNow - lastChecked.Value).TotalMinutes < 15 &&
                !health.IsReachable)
            {
                _logger.Warn("Skipping scheduled series sync — provider is unreachable (last error: {0}). Will retry at next scheduled time.", health.LastError ?? "unknown");
                return null;
            }
            return config;
        }
    }
}
