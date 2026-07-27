using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.Xtream.Plugin.Service
{
    public class ProviderHealthMonitor
    {
        private readonly ILogger _logger;
        private readonly object _lock = new object();

        private bool _isReachable;
        private DateTime? _lastChecked;
        private string _lastError;
        private int _consecutiveFailures;
        private bool? _previousIsReachable;

        public bool IsReachable       { get { lock (_lock) return _isReachable; } }
        public DateTime? LastChecked  { get { lock (_lock) return _lastChecked; } }
        public string LastError       { get { lock (_lock) return _lastError; } }
        public int ConsecutiveFailures { get { lock (_lock) return _consecutiveFailures; } }

        // Fires when reachability transitions: true = came back online, false = went offline.
        // Only fires on state change, never on repeated same-state checks.
        public event Action<bool> ReachabilityChanged;

        public ProviderHealthMonitor(ILogger logger) => _logger = logger;

        public async Task CheckAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.InstanceOrNull?.Configuration;
            if (config == null ||
                string.IsNullOrEmpty(config.BaseUrl) ||
                string.IsNullOrEmpty(config.Username) ||
                string.IsNullOrEmpty(config.Password))
            {
                lock (_lock)
                {
                    _isReachable = false;
                    _lastChecked = DateTime.UtcNow;
                    _lastError = "Not configured";
                    _previousIsReachable = false;
                }
                return;
            }

            var syncService = Plugin.InstanceOrNull?.StrmSyncService;
            if (syncService != null && syncService.IsAnySyncRunning)
            {
                // A full series sync can keep the local Xtream server busy enough
                // for this separate 10-second probe to time out. The sync itself is
                // stronger evidence of connectivity, so preserve the last known
                // state and failure count until the next idle health check.
                _logger.Debug(
                    "Provider health check deferred while an XC2EMBY content sync is running");
                return;
            }

            var url = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}",
                config.BaseUrl.TrimEnd('/'),
                Uri.EscapeDataString(config.Username),
                Uri.EscapeDataString(config.Password));

            bool? prevState = null;
            bool newState = false;

            try
            {
                // Plugin.CreateHttpClient returns a process-wide shared client.
                // Do not wrap it in using: disposing one health-check reference
                // breaks every subsequent health, category, and live-TV request.
                var client = Plugin.CreateHttpClient(10);
                var response = await client.GetStringAsync(url).ConfigureAwait(false);
                using (var doc = System.Text.Json.JsonDocument.Parse(response))
                {
                    var auth = 0;
                    if (doc.RootElement.TryGetProperty("user_info", out var userInfo) &&
                        userInfo.TryGetProperty("auth", out var authEl))
                    {
                        if (authEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                            auth = authEl.GetInt32();
                        else if (authEl.ValueKind == System.Text.Json.JsonValueKind.String &&
                                 int.TryParse(authEl.GetString(), out var n))
                            auth = n;
                    }

                    if (auth == 1)
                    {
                        // Verify the catalog routes used by synchronization, not
                        // merely the lightweight account/authentication response.
                        var apiBase = string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "{0}/player_api.php?username={1}&password={2}&action=",
                            config.BaseUrl.TrimEnd('/'),
                            Uri.EscapeDataString(config.Username),
                            Uri.EscapeDataString(config.Password));

                        if (config.SyncMovies || config.SyncDocumentaries)
                        {
                            var vodJson = await client.GetStringAsync(apiBase + "get_vod_categories").ConfigureAwait(false);
                            using (var vodDoc = System.Text.Json.JsonDocument.Parse(vodJson))
                            {
                                if (vodDoc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                                    throw new InvalidOperationException("VOD category endpoint returned incomplete data");
                            }
                        }

                        if (config.SyncSeries || config.SyncDocuSeries)
                        {
                            var seriesJson = await client.GetStringAsync(apiBase + "get_series_categories").ConfigureAwait(false);
                            using (var seriesDoc = System.Text.Json.JsonDocument.Parse(seriesJson))
                            {
                                if (seriesDoc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                                    throw new InvalidOperationException("Series category endpoint returned incomplete data");
                            }
                        }
                    }

                    lock (_lock)
                    {
                        prevState = _previousIsReachable;
                        _isReachable = auth == 1;
                        _lastChecked = DateTime.UtcNow;
                        _consecutiveFailures = auth == 1 ? 0 : _consecutiveFailures + 1;
                        _lastError = auth == 1 ? null : "Provider returned auth=0";
                        newState = _isReachable;
                        _previousIsReachable = newState;
                    }
                }
            }
            catch (Exception ex)
            {
                // Close the small race where a health request starts immediately
                // before a sync. If the probe then fails while that sync is active,
                // treat it as deferred just like the pre-request check above.
                syncService = Plugin.InstanceOrNull?.StrmSyncService;
                if (syncService != null && syncService.IsAnySyncRunning)
                {
                    _logger.Debug(
                        "Provider health failure ignored because an XC2EMBY content sync started during the probe: {0}",
                        ex.Message);
                    return;
                }

                lock (_lock)
                {
                    prevState = _previousIsReachable;
                    _isReachable = false;
                    _lastChecked = DateTime.UtcNow;
                    _consecutiveFailures++;
                    _lastError = ex.Message;
                    newState = false;
                    _previousIsReachable = newState;
                }
                _logger.Debug("Provider health check failed: {0}", ex.Message);
            }

            // Fire event outside the lock — only on actual state transitions
            if (prevState.HasValue && prevState.Value != newState)
            {
                try { ReachabilityChanged?.Invoke(newState); }
                catch { /* event handler errors must not crash the health task */ }
            }
        }
    }
}
