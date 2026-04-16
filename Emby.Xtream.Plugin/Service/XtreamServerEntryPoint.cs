using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.LiveTv;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Registers XtreamListingsProvider at startup and ensures one tuner host config
    /// and one listing provider config exist so Emby can route channels and EPG.
    /// XtreamTunerHost is auto-discovered by Emby's DI — only the listings provider
    /// needs explicit registration.
    /// </summary>
    public class XtreamServerEntryPoint : IServerEntryPoint
    {
        private readonly ILiveTvManager _liveTvManager;
        private readonly IServerApplicationHost _appHost;

        public XtreamServerEntryPoint(ILiveTvManager liveTvManager, IServerApplicationHost appHost)
        {
            _liveTvManager = liveTvManager;
            _appHost       = appHost;
        }

        public void Run()
        {
            // Register XtreamListingsProvider implementation (not DI-auto-discovered)
            EnsureListingsProviderRegistered();

            // Ensure exactly one xtream tuner config entry
            EnsureTunerHostConfig();

            // Ensure exactly one xtream-epg listing config entry
            EnsureListingsConfig();
        }

        // ── Registration ──────────────────────────────────────────────────────

        private void EnsureListingsProviderRegistered()
        {
            var existingListings = GetExisting<IListingsProvider>("_listingProviders");
            if (existingListings.OfType<XtreamListingsProvider>().Any())
                return;

            // Must preserve tuner hosts; only append our listing provider
            var existingTuners = GetExisting<ITunerHost>("_tunerHosts");
            System.Console.Error.WriteLine(
                "[Xtream] AddParts: tuners={0}, listings={1}+1",
                existingTuners.Count(),
                existingListings.Count());
            _liveTvManager.AddParts(
                existingTuners,
                existingListings.Append(new XtreamListingsProvider()));
        }

        // ── Tuner host config ─────────────────────────────────────────────────

        private void EnsureTunerHostConfig()
        {
            try
            {
                var existing = _liveTvManager.GetTunerHostInfos(XtreamTunerHost.TunerType) ?? new List<TunerHostInfo>();
                if (existing.Count > 0)
                {
                    System.Console.Error.WriteLine("[Xtream] Tuner host config already exists, skipping");
                    return;
                }

                var info = new TunerHostInfo
                {
                    Type         = XtreamTunerHost.TunerType,
                    FriendlyName = "Xtream Tuner",
                    TunerCount   = 1,
                };
                _liveTvManager.SaveTunerHost(info, CancellationToken.None)
                    .GetAwaiter().GetResult();

                System.Console.Error.WriteLine("[Xtream] Created xtream tuner host config");
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine("[Xtream] EnsureTunerHostConfig failed: " + ex.Message);
            }
        }

        // ── Listings provider config ──────────────────────────────────────────

        private void EnsureListingsConfig()
        {
            try
            {
                var infos = GetListingProviderInfos();
                if (infos.Any(p => string.Equals(p.Type, XtreamListingsProvider.ProviderType, StringComparison.OrdinalIgnoreCase)))
                {
                    System.Console.Error.WriteLine("[Xtream] Listings provider config already exists, skipping");
                    return;
                }

                var info = new ListingsProviderInfo
                {
                    Type            = XtreamListingsProvider.ProviderType,
                    Name            = "Xtream EPG",
                    EnableAllTuners = true,
                };
                _liveTvManager.SaveListingProvider(info, false, false, CancellationToken.None)
                    .GetAwaiter().GetResult();

                System.Console.Error.WriteLine("[Xtream] Created xtream-epg listing provider config");
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine("[Xtream] EnsureListingsConfig failed: " + ex.Message);
            }
        }

        // ── Reflection helpers ────────────────────────────────────────────────

        private List<ListingsProviderInfo> GetListingProviderInfos()
        {
            try
            {
                var getConfig = _liveTvManager.GetType()
                    .GetMethod("GetConfiguration",
                        BindingFlags.NonPublic | BindingFlags.Instance,
                        null, Type.EmptyTypes, null);

                var opts = getConfig?.Invoke(_liveTvManager, null);
                if (opts == null)
                    return new List<ListingsProviderInfo>();

                var prop = opts.GetType().GetProperty("ListingProviders");
                var providers = prop?.GetValue(opts) as IEnumerable<ListingsProviderInfo>;
                if (providers == null)
                    return new List<ListingsProviderInfo>();

                return providers.ToList();
            }
            catch { return new List<ListingsProviderInfo>(); }
        }

        private IEnumerable<T> GetExisting<T>(string fieldName)
        {
            // Walk up the type hierarchy to find the field (handles base classes and proxies)
            var type = _liveTvManager.GetType();
            FieldInfo field = null;
            while (type != null && field == null)
            {
                field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
                type = type.BaseType;
            }

            var value = field?.GetValue(_liveTvManager) as IEnumerable<T>;
            System.Console.Error.WriteLine(
                "[Xtream] GetExisting<{0}>('{1}'): field={2}, value={3}",
                typeof(T).Name, fieldName,
                field == null ? "NOT FOUND" : "found",
                value == null ? "null" : "got " + value.Count() + " items");

            return value ?? Enumerable.Empty<T>();
        }

        public void Dispose() { }
    }
}
