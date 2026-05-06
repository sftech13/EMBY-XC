using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Logging;
using ITunerHost = MediaBrowser.Controller.LiveTv.ITunerHost;
using IListingsProvider = MediaBrowser.Controller.LiveTv.IListingsProvider;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Registers XtreamListingsProvider at startup and ensures exactly one tuner host config
    /// and one listing provider config exist so Emby can route channels and EPG.
    /// XtreamTunerHost is auto-discovered by Emby's DI — only the listings provider
    /// needs explicit registration via AddParts.
    /// </summary>
    public class XtreamServerEntryPoint : IServerEntryPoint
    {
        private static volatile XtreamServerEntryPoint _instance;
        public static XtreamServerEntryPoint Instance => _instance;

        private readonly ILiveTvManager _liveTvManager;
        private readonly IServerApplicationHost _appHost;
        private readonly ILogger _logger;

        public XtreamServerEntryPoint(ILiveTvManager liveTvManager, IServerApplicationHost appHost, ILogManager logManager)
        {
            _liveTvManager = liveTvManager;
            _appHost       = appHost;
            _logger        = logManager.GetLogger("XtreamTuner.EntryPoint");
        }

        public void Run()
        {
            _instance = this;

            // Clear persistent caches when the plugin version changes (install or update).
            // In-memory caches are already gone after a restart; the Emby channel cache
            // files in data/livetv/ survive restarts and must be cleared explicitly so
            // Emby re-fetches the channel list with the updated plugin code.
            ClearCachesIfVersionChanged();

            // Remove stale tuner/provider configs from other plugins that no longer exist.
            // Left-over configs cause NullReferenceExceptions in RefreshEmbyChannelsInternal
            // because Emby tries to invoke the ITunerHost/IListingsProvider but finds null.
            RemoveOrphanedConfigs();

            // Register XtreamListingsProvider implementation (not DI-auto-discovered)
            EnsureListingsProviderRegistered();

            // Ensure exactly one xtream-tuner config entry
            EnsureTunerHostConfig();

            // Ensure exactly one xtream-epg listing config entry
            EnsureListingsConfig();
        }

        private void ClearCachesIfVersionChanged()
        {
            try
            {
                var currentVersion = GetType().Assembly.GetName().Version?.ToString() ?? string.Empty;
                var config = Plugin.Instance.Configuration;

                if (string.Equals(config.LastInstalledVersion, currentVersion, StringComparison.Ordinal))
                    return;

                _logger.Info("XC2EMBY version changed {0} → {1}",
                    config.LastInstalledVersion, currentVersion);

                // Do NOT call ClearCaches() here. In-memory caches are already empty after
                // a restart. The persistent Emby channel cache files must survive startup so
                // recording timers that fire immediately after restart can find the tuner.
                // Use the Refresh Cache button in the plugin UI for explicit cache clearing.

                config.LastInstalledVersion = currentVersion;
                Plugin.Instance.SaveConfiguration();
            }
            catch (Exception ex)
            {
                _logger.Warn("ClearCachesIfVersionChanged failed: {0}", ex.Message);
            }
        }

        // ── Cleanup orphaned configs ──────────────────────────────────────────

        private static readonly HashSet<string> KnownTunerTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { XtreamTunerHost.TunerType };

        private static readonly HashSet<string> KnownListingTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { XtreamListingsProvider.ProviderType };

        private void RemoveOrphanedConfigs()
        {
            try
            {
                var registered = GetExisting<ITunerHost>("_tunerHosts");
                var registeredTypes = new HashSet<string>(
                    registered.Select(t => t.Type), StringComparer.OrdinalIgnoreCase);

                // Remove any tuner host config whose Type has no registered ITunerHost.
                var staleTuners = (_liveTvManager.GetTunerHostInfos(null) ?? new List<TunerHostInfo>())
                    .Where(t => !registeredTypes.Contains(t.Type))
                    .ToList();

                foreach (var stale in staleTuners)
                {
                    _logger.Info("Removing orphaned tuner config: Type={0}, Id={1}", stale.Type, stale.Id);
                    try { _liveTvManager.DeleteTunerHost(stale.Id); } catch { }
                }

                // Remove any listing provider config whose Type has no registered IListingsProvider.
                var registeredProviders = GetExisting<IListingsProvider>("_listingProviders");
                var registeredProviderTypes = new HashSet<string>(
                    registeredProviders.Select(p => p.Type), StringComparer.OrdinalIgnoreCase);

                var staleProviders = GetListingProviderInfos()
                    .Where(p => !registeredProviderTypes.Contains(p.Type))
                    .ToList();

                foreach (var stale in staleProviders)
                {
                    _logger.Info("Removing orphaned listing provider config: Type={0}, Id={1}", stale.Type, stale.Id);
                    try { _liveTvManager.DeleteListingsProvider(stale.Id); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("RemoveOrphanedConfigs failed: {0}", ex.Message);
            }
        }

        // ── Registration ──────────────────────────────────────────────────────

        private void EnsureListingsProviderRegistered()
        {
            try
            {
                var existingListings = GetExisting<IListingsProvider>("_listingProviders");
                if (existingListings.OfType<XtreamListingsProvider>().Any())
                {
                    _logger.Info("XtreamListingsProvider already registered");
                    return;
                }

                var existingTuners = GetExisting<ITunerHost>("_tunerHosts");
                _liveTvManager.AddParts(
                    existingTuners,
                    existingListings.Append(new XtreamListingsProvider()));

                _logger.Info("AddParts called with XtreamListingsProvider: {0} tuners, {1}+1 listing providers",
                    existingTuners.Count(), existingListings.Count());
            }
            catch (Exception ex)
            {
                _logger.Warn("EnsureListingsProviderRegistered failed: {0}", ex.Message);
            }
        }

        // ── Tuner host config ─────────────────────────────────────────────────

        private void EnsureTunerHostConfig()
        {
            try
            {
                var tunerCount = Math.Max(1, Plugin.Instance.Configuration.TunerCount);
                var existing = _liveTvManager.GetTunerHostInfos(XtreamTunerHost.TunerType) ?? new List<TunerHostInfo>();

                if (existing.Count > 0)
                {
                    // Sync TunerCount if the plugin config changed.
                    var current = existing[0];
                    if (current.TunerCount != tunerCount)
                    {
                        current.TunerCount = tunerCount;
                        _liveTvManager.SaveTunerHost(current, CancellationToken.None)
                            .GetAwaiter().GetResult();
                        _logger.Info("Updated tuner host TunerCount to {0}", tunerCount);
                    }
                    else
                    {
                        _logger.Debug("Tuner host config already exists, skipping");
                    }
                    return;
                }

                var info = new TunerHostInfo
                {
                    Type         = XtreamTunerHost.TunerType,
                    FriendlyName = "XC2EMBY",
                    TunerCount   = tunerCount,
                };
                _liveTvManager.SaveTunerHost(info, CancellationToken.None)
                    .GetAwaiter().GetResult();

                _logger.Info("Created xtream tuner host config (TunerCount={0})", tunerCount);
            }
            catch (Exception ex)
            {
                _logger.Warn("EnsureTunerHostConfig failed: {0}", ex.Message);
            }
        }

        // ── Listings provider config ──────────────────────────────────────────

        private void EnsureListingsConfig()
        {
            try
            {
                var infos = GetListingProviderInfos();
                var existing = infos.FirstOrDefault(p =>
                    string.Equals(p.Type, XtreamListingsProvider.ProviderType, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    if (!existing.EnableAllTuners)
                    {
                        existing.EnableAllTuners = true;
                        _liveTvManager.SaveListingProvider(existing, false, false, CancellationToken.None)
                            .GetAwaiter().GetResult();
                        _logger.Info("Updated xtream-epg listing provider config to enable all tuners");
                    }

                    _logger.Debug("Listings provider config already exists, skipping");
                    return;
                }

                var info = new ListingsProviderInfo
                {
                    Type            = XtreamListingsProvider.ProviderType,
                    Name            = "XC2EMBY EPG",
                    EnableAllTuners = true,
                };
                _liveTvManager.SaveListingProvider(info, false, false, CancellationToken.None)
                    .GetAwaiter().GetResult();

                _logger.Info("Created xtream-epg listing provider config");
            }
            catch (Exception ex)
            {
                _logger.Warn("EnsureListingsConfig failed: {0}", ex.Message);
            }
        }

        internal string GetListingsProviderId()
        {
            try
            {
                return GetListingProviderInfos()
                    .FirstOrDefault(p => string.Equals(
                        p.Type,
                        XtreamListingsProvider.ProviderType,
                        StringComparison.OrdinalIgnoreCase))
                    ?.Id;
            }
            catch
            {
                return null;
            }
        }

        internal List<string> GetTunerHostIds()
        {
            try
            {
                return (_liveTvManager.GetTunerHostInfos(XtreamTunerHost.TunerType) ?? new List<TunerHostInfo>())
                    .Where(t => !string.IsNullOrEmpty(t.Id))
                    .Select(t => t.Id)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        // ── Called externally when credentials change ─────────────────────────

        internal void EnsureListingsConfigUpdated()
        {
            // With XtreamListingsProvider, credentials are read from Plugin.Configuration
            // at fetch time — no need to update a stored URL. Just clear caches.
            XtreamTunerHost.Instance?.ClearCaches();
        }

        // ── Guide refresh ─────────────────────────────────────────────────────

        internal GuideLogoCleanupResult TriggerGuideRefresh()
        {
            var config = Plugin.Instance.Configuration;
            var logoCleanup = config.ClearLiveTvLogoCacheOnRefresh
                ? ClearGuideLogos()
                : new GuideLogoCleanupResult { Success = true, Skipped = true };

            try
            {
                var infos = GetListingProviderInfos();
                var info = infos.FirstOrDefault(p =>
                    string.Equals(p.Type, XtreamListingsProvider.ProviderType, StringComparison.OrdinalIgnoreCase));
                if (info == null)
                {
                    _logger.Warn("TriggerGuideRefresh: xtream-epg listing provider not found");
                    logoCleanup.GuideRefreshTriggered = false;
                    return logoCleanup;
                }

                // startRefresh=true causes Emby to re-fetch listings and rebuild the guide.
                _liveTvManager.SaveListingProvider(info, false, true, CancellationToken.None)
                    .GetAwaiter().GetResult();

                logoCleanup.GuideRefreshTriggered = true;
                _logger.Info(
                    "Guide refresh triggered after cache clear; logo cleanup skipped={0}, scanned {1} channel(s), removed {2} logo(s), {3} failed",
                    logoCleanup.Skipped,
                    logoCleanup.ChannelsScanned,
                    logoCleanup.LogosDeleted,
                    logoCleanup.Failed);
            }
            catch (Exception ex)
            {
                _logger.Warn("TriggerGuideRefresh failed: {0}", ex.Message);
                logoCleanup.GuideRefreshTriggered = false;
            }

            return logoCleanup;
        }

        internal GuideLogoCleanupResult ClearGuideLogos()
        {
            var result = new GuideLogoCleanupResult();

            try
            {
                var channels = GetInternalLiveTvChannels();
                result.ChannelsScanned = channels.Count;

                foreach (var channel in channels)
                {
                    try
                    {
                        if (!channel.HasImage(ImageType.Primary, 0))
                            continue;

                        channel.DeleteImage(ImageType.Primary, 0);
                        result.LogosDeleted++;
                    }
                    catch
                    {
                        result.Failed++;
                    }
                }

                result.Success = result.Failed == 0;
                _logger.Info(
                    "Guide logo cleanup completed: scanned {0} channel(s), removed {1} logo(s), {2} failed",
                    result.ChannelsScanned,
                    result.LogosDeleted,
                    result.Failed);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = ex.Message;
                _logger.Warn("Guide logo cleanup failed: {0}", ex.Message);
            }

            return result;
        }

        // ── Reflection helpers ────────────────────────────────────────────────

        private List<BaseItem> GetInternalLiveTvChannels()
        {
            var method = _liveTvManager.GetType().GetMethod(
                "GetInternalChannels",
                BindingFlags.Public | BindingFlags.Instance);

            if (method == null)
                return new List<BaseItem>();

            var task = method.Invoke(_liveTvManager, new object[]
            {
                new InternalItemsQuery(),
                true,
                CancellationToken.None
            }) as System.Threading.Tasks.Task;

            if (task == null)
                return new List<BaseItem>();

            task.GetAwaiter().GetResult();

            var result = task.GetType().GetProperty("Result")?.GetValue(task);
            var items = result?.GetType().GetProperty("Items")?.GetValue(result) as IEnumerable;
            if (items == null)
                return new List<BaseItem>();

            return items.OfType<BaseItem>().ToList();
        }

        private IEnumerable<T> GetExisting<T>(string fieldName)
        {
            var type = _liveTvManager.GetType();
            System.Reflection.FieldInfo field = null;
            while (type != null && field == null)
            {
                field = type.GetField(fieldName,
                    BindingFlags.NonPublic | BindingFlags.Instance);
                type = type.BaseType;
            }
            if (field == null)
                _logger.Warn("GetExisting: field '{0}' not found on {1}", fieldName, _liveTvManager.GetType().Name);
            var result = (field?.GetValue(_liveTvManager) as IEnumerable<T>) ?? Enumerable.Empty<T>();
            _logger.Info("GetExisting<{0}>('{1}'): {2} items", typeof(T).Name, fieldName, result.Count());
            return result;
        }

        private List<ListingsProviderInfo> GetListingProviderInfos()
        {
            try
            {
                var getConfig = _liveTvManager.GetType()
                    .GetMethod("GetConfiguration",
                        BindingFlags.NonPublic | BindingFlags.Instance,
                        null, Type.EmptyTypes, null);

                var opts = getConfig?.Invoke(_liveTvManager, null);
                if (opts == null) return new List<ListingsProviderInfo>();

                var prop = opts.GetType().GetProperty("ListingProviders");
                var providers = prop?.GetValue(opts) as IEnumerable<ListingsProviderInfo>;
                return providers?.ToList() ?? new List<ListingsProviderInfo>();
            }
            catch { return new List<ListingsProviderInfo>(); }
        }

        public void Dispose() { }
    }

    internal class GuideLogoCleanupResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int ChannelsScanned { get; set; }
        public int LogosDeleted { get; set; }
        public int Failed { get; set; }
        public bool Skipped { get; set; }
        public bool GuideRefreshTriggered { get; set; }
    }
}
