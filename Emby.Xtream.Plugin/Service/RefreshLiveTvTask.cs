using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Xtream.Plugin.Service
{
    public class RefreshLiveTvTask : IScheduledTask
    {
        private readonly ILogger _logger;

        public RefreshLiveTvTask(ILogManager logManager)
            => _logger = logManager.GetLogger("XtreamTuner.RefreshLiveTvTask");

        public string Name        => "XC2EMBY - Refresh Live TV";
        public string Description => "Refreshes the channel list and guide data from your Xtream server.";
        public string Category    => "XC2EMBY";
        public string Key         => "XtreamTunerRefreshLiveTV";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerInterval,
                IntervalTicks = TimeSpan.FromHours(4).Ticks
            };
        }

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var config = Plugin.Instance.Configuration;
            if (!config.EnableLiveTv)
            {
                _logger.Info("Live TV disabled — skipping scheduled guide refresh.");
                progress.Report(100);
                return Task.CompletedTask;
            }

            progress.Report(10);
            Plugin.Instance.LiveTvService.InvalidateCache();

            // Match Emby's manual "Refresh Guide" action: make the XC2EMBY
            // caches cold, then queue Emby's one built-in RefreshGuide task.
            // That task owns both channel reconciliation and guide rebuilding.
            // Starting a tuner save and a listing-provider save here used to
            // cancel/restart the first guide job after the background fetch.
            progress.Report(40);
            XtreamTunerHost.Instance?.ClearCaches();

            progress.Report(70);
            var refreshResult = XtreamServerEntryPoint.Instance?.TriggerGuideRefresh();
            var refreshQueued = refreshResult?.GuideRefreshTriggered == true;

            progress.Report(100);
            if (refreshQueued)
                _logger.Info("Scheduled Live TV cache invalidation complete; Emby RefreshGuide task queued.");
            else
                _logger.Warn("Scheduled Live TV cache invalidation completed, but Emby RefreshGuide could not be queued.");
            return Task.CompletedTask;
        }
    }
}
