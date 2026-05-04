using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Emby.Xtream.Plugin.Service;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.Xtream.Plugin
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        private static volatile Plugin _instance;
        private const string LegacyConfigFileName = "Emby.Xtream.Plugin.xml";
        private const string CurrentConfigFileName = "XC2EMBY.Plugin.xml";
        private readonly IApplicationHost _applicationHost;
        private readonly IApplicationPaths _applicationPaths;
        private LiveTvService _liveTvService;
        private StrmSyncService _strmSyncService;

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogManager logManager, IApplicationHost applicationHost)
            : base(applicationPaths, xmlSerializer)
        {
            _instance = this;
            _applicationHost = applicationHost;
            _applicationPaths = applicationPaths;
            _liveTvService = new LiveTvService(logManager.GetLogger("XtreamTuner.LiveTv"));
            _strmSyncService = new StrmSyncService(logManager.GetLogger("XtreamTuner.StrmSync"));
            TryMigrateLegacyConfiguration(logManager.GetLogger("XtreamTuner.Plugin"));

            // Pre-warm the channel cache in the background so the first guide load is instant.
            // Configuration must NOT be accessed here — DI isn't fully wired at construction time.
            // The check is deferred into the lambda which runs after startup completes.
            _ = Task.Run(async () =>
            {
                // Small delay to let Emby finish its own startup before we hit the network.
                await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);

                try
                {
                    var cfg = Plugin.InstanceOrNull?.Configuration;
                    if (cfg != null && cfg.EnableLiveTv &&
                        !string.IsNullOrEmpty(cfg.BaseUrl) &&
                        !string.IsNullOrEmpty(cfg.Username) &&
                        !string.IsNullOrEmpty(cfg.Password))
                    {
                        await _liveTvService.GetFilteredChannelsAsync(System.Threading.CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch { /* best-effort pre-warm, ignore errors */ }
            });
        }

        public override string Name => "XC2EMBY";

        public override string Description =>
            "XC2EMBY — Xtream-compatible Live TV tuner with EPG, category filtering, and pre-populated media info.";

        public override Guid Id => Guid.Parse("ff489847-080b-475c-99fc-f448db175b56");

        public static Plugin Instance => _instance ?? throw new InvalidOperationException("Plugin not initialized");

        /// <summary>Returns the current instance, or null if the plugin has not been initialised (e.g. during unit tests).</summary>
        internal static Plugin InstanceOrNull => _instance;

        public IApplicationHost ApplicationHost => _applicationHost;

        public new IApplicationPaths ApplicationPaths => _applicationPaths;

        /// <summary>
        /// Creates an HttpClient configured with the plugin's User-Agent setting.
        /// </summary>
        public static HttpClient CreateHttpClient(int timeoutSeconds = 10)
        {
            var handler = new System.Net.Http.HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            var ua = _instance?.Configuration?.HttpUserAgent;
            if (!string.IsNullOrEmpty(ua))
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);
            return client;
        }

        public LiveTvService LiveTvService => _liveTvService;

        public StrmSyncService StrmSyncService => _strmSyncService;

        public Stream GetThumbImage()
        {
            return GetType().Assembly.GetManifestResourceStream("Emby.Xtream.Plugin.thumb.png");
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = GetHtmlPageName(),
                    EmbeddedResourcePath = "Emby.Xtream.Plugin.Configuration.Web.config.html",
                    IsMainConfigPage = true,
                    EnableInMainMenu = true,
                    MenuIcon = "live_tv",
                },
                new PluginPageInfo
                {
                    Name = "xtreamconfigjs",
                    EmbeddedResourcePath = "Emby.Xtream.Plugin.Configuration.Web.config.js",
                },
                new PluginPageInfo
                {
                    Name = GetJsPageName(),
                    EmbeddedResourcePath = "Emby.Xtream.Plugin.Configuration.Web.config.js",
                },
            };
        }

        /// <summary>
        /// Returns a stable page name for config.html. Must never change between versions —
        /// if it did, the Emby SPA would navigate to a stale URL after a banner install and
        /// show "error processing request" because the old page name no longer exists in the
        /// new DLL. Emby appends ?v=&lt;ServerVersion&gt; for cache-busting.
        /// </summary>
        private static string GetHtmlPageName()
        {
            return "xtreamconfig101";
        }

        /// <summary>
        /// Returns a stable JS page name. Emby appends ?v=&lt;ServerVersion&gt; automatically,
        /// which provides sufficient cache-busting across plugin updates.
        /// </summary>
        private static string GetJsPageName()
        {
            return "xtreamconfigjs101";
        }

        private void TryMigrateLegacyConfiguration(ILogger logger)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_applicationPaths?.PluginsPath))
                {
                    return;
                }

                var configDir = Path.Combine(_applicationPaths.PluginsPath, "configurations");
                var legacyPath = Path.Combine(configDir, LegacyConfigFileName);
                var currentPath = Path.Combine(configDir, CurrentConfigFileName);

                if (!File.Exists(legacyPath))
                {
                    return;
                }

                var legacy = LoadConfigurationFile(legacyPath);
                if (legacy == null || !HasConnectionSettings(legacy))
                {
                    return;
                }

                var current = Configuration ?? new PluginConfiguration();
                if (!ShouldMigrateFromLegacy(current, legacy))
                {
                    return;
                }

                CopyConfiguration(legacy, current);
                SaveConfiguration();

                try
                {
                    File.Copy(legacyPath, currentPath, true);
                }
                catch
                {
                    // SaveConfiguration already persisted the in-memory state; best-effort mirror only.
                }

                logger.Info("Migrated plugin configuration from {0} to {1}", LegacyConfigFileName, CurrentConfigFileName);
            }
            catch (Exception ex)
            {
                logger.Warn("Legacy config migration skipped: {0}", ex.Message);
            }
        }

        private static PluginConfiguration LoadConfigurationFile(string path)
        {
            var serializer = new XmlSerializer(typeof(PluginConfiguration));
            using (var stream = File.OpenRead(path))
            {
                return serializer.Deserialize(stream) as PluginConfiguration;
            }
        }

        private static bool HasConnectionSettings(PluginConfiguration config)
        {
            return config != null &&
                !string.IsNullOrWhiteSpace(config.BaseUrl) &&
                !string.IsNullOrWhiteSpace(config.Username) &&
                !string.IsNullOrWhiteSpace(config.Password);
        }

        private static bool ShouldMigrateFromLegacy(PluginConfiguration current, PluginConfiguration legacy)
        {
            if (!HasConnectionSettings(current))
            {
                return true;
            }

            var currentLooksFresh =
                string.IsNullOrWhiteSpace(current.LastChannelListHash) &&
                IsEmptyJsonObject(current.StreamCodecCacheJson);

            var legacyLooksEstablished =
                !string.IsNullOrWhiteSpace(legacy.LastChannelListHash) ||
                !IsEmptyJsonObject(legacy.StreamCodecCacheJson) ||
                !string.IsNullOrWhiteSpace(legacy.CachedLiveCategories);

            if (currentLooksFresh && legacyLooksEstablished)
            {
                return true;
            }

            return string.Equals(current.BaseUrl, legacy.BaseUrl, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(current.Username, legacy.Username, StringComparison.Ordinal) &&
                !string.Equals(current.Password, legacy.Password, StringComparison.Ordinal) &&
                currentLooksFresh &&
                legacyLooksEstablished;
        }

        private static bool IsEmptyJsonObject(string value)
        {
            return string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "{}", StringComparison.Ordinal);
        }

        private static void CopyConfiguration(PluginConfiguration source, PluginConfiguration target)
        {
            target.BaseUrl = source.BaseUrl;
            target.Username = source.Username;
            target.Password = source.Password;
            target.HttpUserAgent = source.HttpUserAgent;
            target.EnableLiveTv = source.EnableLiveTv;
            target.LiveTvOutputFormat = source.LiveTvOutputFormat;
            target.EnableLiveTvDirectPlay = source.EnableLiveTvDirectPlay;
            target.TunerCount = source.TunerCount > 0 ? source.TunerCount : 1;
            target.StreamCodecCacheJson = source.StreamCodecCacheJson;
            target.EpgSource = source.EpgSource;
            target.CustomEpgUrl = source.CustomEpgUrl;
            target.EpgCacheMinutes = source.EpgCacheMinutes;
            target.EpgDaysToFetch = source.EpgDaysToFetch;
            target.M3UCacheMinutes = source.M3UCacheMinutes;
            target.SelectedLiveCategoryIds = source.SelectedLiveCategoryIds ?? new int[0];
            target.IncludeAdultChannels = source.IncludeAdultChannels;
            target.IncludeGroupTitleInM3U = source.IncludeGroupTitleInM3U;
            target.ChannelRemoveTerms = source.ChannelRemoveTerms;
            target.EnableChannelNameCleaning = source.EnableChannelNameCleaning;
            target.SyncMovies = source.SyncMovies;
            target.StrmLibraryPath = source.StrmLibraryPath;
            target.MovieRootFolderName = string.IsNullOrWhiteSpace(source.MovieRootFolderName) ? target.MovieRootFolderName : source.MovieRootFolderName;
            target.SelectedVodCategoryIds = source.SelectedVodCategoryIds ?? new int[0];
            target.MovieFolderMode = source.MovieFolderMode;
            target.MovieFolderMappings = source.MovieFolderMappings;
            target.SyncDocumentaries = source.SyncDocumentaries;
            target.DocumentaryRootFolderName = string.IsNullOrWhiteSpace(source.DocumentaryRootFolderName) ? target.DocumentaryRootFolderName : source.DocumentaryRootFolderName;
            target.SelectedDocumentaryCategoryIds = source.SelectedDocumentaryCategoryIds ?? new int[0];
            target.DocumentaryFolderMode = source.DocumentaryFolderMode;
            target.DocumentaryFolderMappings = source.DocumentaryFolderMappings;
            target.SyncSeries = source.SyncSeries;
            target.SeriesRootFolderName = string.IsNullOrWhiteSpace(source.SeriesRootFolderName) ? target.SeriesRootFolderName : source.SeriesRootFolderName;
            target.SelectedSeriesCategoryIds = source.SelectedSeriesCategoryIds ?? new int[0];
            target.SeriesFolderMode = source.SeriesFolderMode;
            target.SeriesFolderMappings = source.SeriesFolderMappings;
            target.SyncDocuSeries = source.SyncDocuSeries;
            target.DocuSeriesRootFolderName = string.IsNullOrWhiteSpace(source.DocuSeriesRootFolderName) ? target.DocuSeriesRootFolderName : source.DocuSeriesRootFolderName;
            target.SelectedDocuSeriesCategoryIds = source.SelectedDocuSeriesCategoryIds ?? new int[0];
            target.DocuSeriesFolderMode = source.DocuSeriesFolderMode;
            target.DocuSeriesFolderMappings = source.DocuSeriesFolderMappings;
            target.EnableContentNameCleaning = source.EnableContentNameCleaning;
            target.ContentRemoveTerms = source.ContentRemoveTerms;
            target.EnableTmdbFolderNaming = source.EnableTmdbFolderNaming;
            target.EnableTmdbFallbackLookup = source.EnableTmdbFallbackLookup;
            target.MovieTmdbCacheJson = source.MovieTmdbCacheJson;
            target.EnableSeriesIdFolderNaming = source.EnableSeriesIdFolderNaming;
            target.EnableSeriesMetadataLookup = source.EnableSeriesMetadataLookup;
            target.TvdbFolderIdOverrides = source.TvdbFolderIdOverrides;
            target.EnableNfoFiles = source.EnableNfoFiles;
            target.CachedVodCategories = source.CachedVodCategories;
            target.CachedSeriesCategories = source.CachedSeriesCategories;
            target.CachedLiveCategories = source.CachedLiveCategories;
            target.LastInstalledVersion = source.LastInstalledVersion;
            target.UseBetaChannel = source.UseBetaChannel;
            target.SmartSkipExisting = source.SmartSkipExisting;
            target.EnableLocalMediaFilter = source.EnableLocalMediaFilter;
            target.SyncParallelism = source.SyncParallelism;
            target.CleanupOrphans = source.CleanupOrphans;
            target.OrphanSafetyThreshold = source.OrphanSafetyThreshold;
            target.AutoSyncEnabled = source.AutoSyncEnabled;
            target.AutoSyncMode = source.AutoSyncMode;
            target.AutoSyncIntervalHours = source.AutoSyncIntervalHours;
            target.AutoSyncDailyTime = source.AutoSyncDailyTime;
            target.LastChannelListHash = source.LastChannelListHash;
            target.LastMovieSyncTimestamp = source.LastMovieSyncTimestamp;
            target.LastDocumentarySyncTimestamp = source.LastDocumentarySyncTimestamp;
            target.LastSeriesSyncTimestamp = source.LastSeriesSyncTimestamp;
            target.LastDocuSeriesSyncTimestamp = source.LastDocuSeriesSyncTimestamp;
            target.StrmNamingVersion = source.StrmNamingVersion;
            target.SyncHistoryJson = source.SyncHistoryJson;
            target.SeriesEpisodeHashesJson = source.SeriesEpisodeHashesJson;
            target.DocuSeriesEpisodeHashesJson = source.DocuSeriesEpisodeHashesJson;
        }
    }
}
