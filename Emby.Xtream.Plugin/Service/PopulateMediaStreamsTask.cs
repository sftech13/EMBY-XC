using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Xtream.Plugin.Service
{
    public class PopulateMediaStreamsTask : IScheduledTask
    {
        private readonly ILogger _logger;

        public PopulateMediaStreamsTask(ILogManager logManager)
            => _logger = logManager.GetLogger("XtreamTuner.PopulateMediaStreams");

        public string Name        => "XC2EMBY - Populate Episode Media Streams";
        public string Description => "Writes per-episode codec info (video/audio) from your Xtream provider directly into Emby's database. Run this once after syncing to prevent Emby from probing STRM episode streams at playback time.";
        public string Category    => "XC2EMBY";
        public string Key         => "XtreamTunerPopulateMediaStreams";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield break; // manual-only task, no scheduled trigger
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var config = Plugin.Instance.Configuration;
            if (string.IsNullOrEmpty(config.BaseUrl) ||
                string.IsNullOrEmpty(config.Username) ||
                string.IsNullOrEmpty(config.Password))
            {
                _logger.Warn("PopulateMediaStreams: Xtream connection not configured — skipping.");
                return;
            }

            var svc = Plugin.Instance.StrmSyncService;
            progress.Report(0);
            await svc.PopulateEpisodeMediaStreamsAsync(config, progress, cancellationToken)
                     .ConfigureAwait(false);
            progress.Report(100);
        }
    }
}
