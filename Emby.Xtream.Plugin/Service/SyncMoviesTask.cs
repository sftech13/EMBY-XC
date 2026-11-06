using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Xtream.Plugin.Service
{
    public class SyncMoviesTask : IScheduledTask
    {
        private readonly ILogger _logger;

        public SyncMoviesTask(ILogManager logManager)
            => _logger = logManager.GetLogger("XtreamTuner.SyncMoviesTask");

        public string Name        => "XC2EMBY - Sync Movies";
        public string Description => "Sync VOD movie STRM files from your Xtream server.";
        public string Category    => "XC2EMBY";
        public string Key         => "XtreamTunerSyncMovies";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // One-time default (first install). Plugin UI overrides this via Emby's trigger API.
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks  // 03:00
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (GetConfigurationForRun() == null) return;

            progress.Report(0);
            await Plugin.Instance.StrmSyncService.SyncMoviesWhenReadyAsync(
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
                _logger.Info("Automatic sync disabled — skipping scheduled movie sync.");
                return null;
            }
            if (!config.SyncMovies)
            {
                _logger.Info("Movie sync disabled — skipping.");
                return null;
            }

            var health = Plugin.Instance.ProviderHealth;
            var lastChecked = health.LastChecked;
            if (lastChecked.HasValue &&
                (DateTime.UtcNow - lastChecked.Value).TotalMinutes < 15 &&
                !health.IsReachable)
            {
                _logger.Warn("Skipping scheduled movie sync — provider is unreachable (last error: {0}). Will retry at next scheduled time.", health.LastError ?? "unknown");
                return null;
            }
            return config;
        }
    }
}
