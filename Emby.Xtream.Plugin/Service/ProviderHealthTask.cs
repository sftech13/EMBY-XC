using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Xtream.Plugin.Service
{
    public class ProviderHealthTask : IScheduledTask
    {
        private readonly ILogger _logger;

        public ProviderHealthTask(ILogManager logManager)
            => _logger = logManager.GetLogger("XtreamTuner.ProviderHealthTask");

        public string Name        => "XC2EMBY - Provider Health Check";
        public string Description => "Pings the IPTV provider to verify it is reachable.";
        public string Category    => "XC2EMBY";
        public string Key         => "XtreamProviderHealthCheck";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerInterval,
                IntervalTicks = TimeSpan.FromMinutes(5).Ticks
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            progress.Report(0);
            await Plugin.Instance.ProviderHealth.CheckAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(100);
        }
    }
}
