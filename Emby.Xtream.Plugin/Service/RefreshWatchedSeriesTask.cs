using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Xtream.Plugin.Service
{
    public class RefreshWatchedSeriesTask : IScheduledTask
    {
        private readonly ILogger _logger;

        public RefreshWatchedSeriesTask(ILogManager logManager)
            => _logger = logManager.GetLogger("XtreamTuner.RefreshWatchedSeriesTask");

        public string Name        => "XC2EMBY - Refresh Watched Series";
        public string Description => "Checks series added via Catalog Search for new episodes.";
        public string Category    => "XC2EMBY";
        public string Key         => "XtreamTunerRefreshWatchedSeries";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // One-time default (first install). Plugin UI overrides this via Emby's trigger API.
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(5).Ticks  // 05:00 (offset from the other XC2EMBY tasks)
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var config = Plugin.Instance.Configuration;
            if (!config.WatchedSeriesAutoRefreshEnabled)
            {
                _logger.Info("Watched series auto-refresh disabled — skipping.");
                return;
            }

            var svc = Plugin.Instance.StrmSyncService;

            // A full category sync doesn't guard AddSingleItemAsync against this task (only
            // the Failed Items retry panel does — see AddSingleItemAsync's _retryProgressSwapGate).
            // Skip this cycle rather than write STRM files concurrently with a running sync.
            if (svc.IsAnySyncRunning)
            {
                _logger.Info("A sync is already running — skipping this watched series refresh cycle.");
                return;
            }

            progress.Report(0);
            await svc.RefreshWatchedSeriesAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(100);
        }
    }
}
