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

            progress.Report(30);
            XtreamTunerHost.Instance?.InvalidateChannelCacheTime();

            progress.Report(50);
            XtreamServerEntryPoint.Instance?.TriggerChannelRescan();

            progress.Report(80);
            XtreamServerEntryPoint.Instance?.TriggerGuideRefresh();

            progress.Report(100);
            _logger.Info("Scheduled Live TV refresh complete.");
            return Task.CompletedTask;
        }
    }
}
