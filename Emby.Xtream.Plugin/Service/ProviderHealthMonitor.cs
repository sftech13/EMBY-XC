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
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(8));
                    using (var client = Plugin.CreateHttpClient(10))
                    {
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
                }
            }
            catch (Exception ex)
            {
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
