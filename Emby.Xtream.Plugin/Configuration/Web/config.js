define(['baseView', 'loading', 'emby-input', 'emby-select', 'emby-checkbox', 'emby-button'],
function (BaseView, loading) {
    'use strict';

    var pluginId = 'ff489847-080b-475c-99fc-f448db175b56';

    // Use the CSS variable reference directly — the browser resolves it natively.
    // getPropertyValue() returns the *declared* value (e.g. "var(--x)"), not the resolved
    // color, so attempting to parse it in JS always fails. Instead we pass the CSS var
    // string as-is; inline style="color:var(--xt-accent)" is valid in all modern browsers.
    var accentColor = 'var(--xt-accent)';

    function View(view, params) {
        BaseView.apply(this, arguments);

        initializeDocumentaryPanels(view);

        // Fix spacing between path validation result and Smart Skip checkbox.
        // Direct style assignment overrides any inline styles from cached HTML.
        (function () {
            var valDiv = view.querySelector('.strmPathValidationResult');
            if (valDiv) {
                valDiv.style.cssText = 'margin-top:0.25em; margin-bottom:1.2em; font-size:0.9em;';
                var next = valDiv.nextElementSibling;
                if (next) next.style.marginTop = '';
            }
        }());

        this.loadedCategories = [];
        this.selectedCategoryIds = [];
        this.loadedVodCategories = [];
        this.selectedVodCategoryIds = [];
        this.loadedDocumentaryCategories = [];
        this.selectedDocumentaryCategoryIds = [];
        this.loadedSeriesCategories = [];
        this.selectedSeriesCategoryIds = [];
        this.loadedDocuSeriesCategories = [];
        this.selectedDocuSeriesCategoryIds = [];
        var self = this;

        view.querySelector('.xtreamConfigForm').addEventListener('submit', function (e) {
            e.preventDefault();
            saveConfig(self);
        });

        view.querySelector('.chkEnableNameCleaning').addEventListener('change', function () {
            updateNameCleaningVisibility(view);
        });

        view.querySelector('.chkEnableTmdbFolderNaming').addEventListener('change', function () {
            updateTmdbVisibility(view);
        });

        view.querySelector('.selectEpgSource').addEventListener('change', function () {
            updateEpgVisibility(view);
        });

        view.querySelector('.chkSyncMovies').addEventListener('change', function () {
            updateVodMovieVisibility(view);
        });

        view.querySelector('.chkSyncDocumentaries').addEventListener('change', function () {
            updateDocumentaryVisibility(view);
        });

        view.querySelector('.chkSyncSeries').addEventListener('change', function () {
            updateSeriesVisibility(view);
        });

        view.querySelector('.chkSyncDocuSeries').addEventListener('change', function () {
            updateDocuSeriesVisibility(view);
        });

        view.querySelector('.selMovieFolderMode').addEventListener('change', function () {
            updateFoldersVisibility(view, 'movie');
        });

        view.querySelector('.selDocumentaryFolderMode').addEventListener('change', function () {
            updateFoldersVisibility(view, 'documentary');
        });

        view.querySelector('.selSeriesFolderMode').addEventListener('change', function () {
            updateFoldersVisibility(view, 'series');
        });

        view.querySelector('.selDocuSeriesFolderMode').addEventListener('change', function () {
            updateFoldersVisibility(view, 'docuSeries');
        });

        view.querySelector('.chkAutoSyncEnabled').addEventListener('change', function () {
            updateAutoSyncVisibility(view);
        });

        view.querySelector('.selAutoSyncMode').addEventListener('change', function () {
            updateAutoSyncVisibility(view);
        });

        view.querySelector('.btnAddMovieFolder').addEventListener('click', function () {
            addFolderEntry(view, 'movie', '', '', self.loadedVodCategories);
        });

        view.querySelector('.btnAddDocumentaryFolder').addEventListener('click', function () {
            addFolderEntry(view, 'documentary', '', '', self.loadedDocumentaryCategories);
        });

        view.querySelector('.btnAddSeriesFolder').addEventListener('click', function () {
            addFolderEntry(view, 'series', '', '', self.loadedSeriesCategories);
        });

        view.querySelector('.btnAddDocuSeriesFolder').addEventListener('click', function () {
            addFolderEntry(view, 'docuSeries', '', '', self.loadedDocuSeriesCategories);
        });

        view.querySelector('.txtStrmLibraryPath').addEventListener('blur', function () {
            validateStrmPath(view);
        });

        view.querySelector('.btnBrowseStrmPath').addEventListener('click', function () {
            openBrowser(view, '.txtStrmLibraryPath');
        });
        view.querySelector('.btnBrowseMovieRoot').addEventListener('click', function () {
            openBrowser(view, '.txtMovieRootFolderName');
        });
        view.querySelector('.btnBrowseDocumentaryRoot').addEventListener('click', function () {
            openBrowser(view, '.txtDocumentaryRootFolderName');
        });
        view.querySelector('.btnBrowseSeriesRoot').addEventListener('click', function () {
            openBrowser(view, '.txtSeriesRootFolderName');
        });
        view.querySelector('.btnBrowseDocuSeriesRoot').addEventListener('click', function () {
            openBrowser(view, '.txtDocuSeriesRootFolderName');
        });

        view.querySelector('.btnCloseBrowser').addEventListener('click', function () {
            closeBrowser(view);
        });

        view.querySelector('.btnBrowserCancel').addEventListener('click', function () {
            closeBrowser(view);
        });

        view.querySelector('.btnBrowserOk').addEventListener('click', function () {
            var modal = view.querySelector('.strmBrowserModal');
            var path = (modal.querySelector('.txtBrowserCurrentPath').value || '').trim();
            var targetSel = modal.getAttribute('data-target-field') || '.txtStrmLibraryPath';
            if (path) {
                view.querySelector(targetSel).value = path;
                if (targetSel === '.txtStrmLibraryPath') validateStrmPath(view);
            }
            closeBrowser(view);
        });

        view.querySelector('.txtBrowserCurrentPath').addEventListener('keydown', function (e) {
            if (e.key === 'Enter') { e.preventDefault(); browserNavigate(view, this.value.trim() || null); }
        });

        view.querySelector('.strmBrowserModal').addEventListener('click', function (e) {
            if (e.target === this) closeBrowser(view);
        });

        view.querySelector('.btnTestConnection').addEventListener('click', function () {
            testXtreamConnection(self);
        });

        view.querySelector('.btnLoadCategories').addEventListener('click', function () {
            saveConfig(self, function () {
                loadCategories(self);
            });
        });

        view.querySelector('.btnSelectAllCategories').addEventListener('click', function () {
            toggleAllCategories(view, true);
        });

        view.querySelector('.btnDeselectAllCategories').addEventListener('click', function () {
            toggleAllCategories(view, false);
        });

        view.querySelector('.btnRefreshCache').addEventListener('click', function () {
            refreshCache(view);
        });

        view.querySelector('.btnClearCodecCache').addEventListener('click', function () {
            clearCodecCache(view);
        });

        // VOD category buttons (single mode)
        view.querySelector('.btnLoadVodCategories').addEventListener('click', function () {
            saveConfig(self, function () {
                loadVodCategories(self);
            });
        });

        view.querySelector('.btnSelectAllVodCategories').addEventListener('click', function () {
            toggleAllVodCategories(view, true);
        });

        view.querySelector('.btnDeselectAllVodCategories').addEventListener('click', function () {
            toggleAllVodCategories(view, false);
        });

        // VOD category buttons (multi mode)
        view.querySelector('.btnLoadVodCategoriesMulti').addEventListener('click', function () {
            saveConfig(self, function () {
                loadVodCategoriesMulti(self);
            });
        });

        view.querySelector('.btnLoadDocumentaryCategories').addEventListener('click', function () {
            saveConfig(self, function () {
                loadDocumentaryCategories(self);
            });
        });

        view.querySelector('.btnSelectAllDocumentaryCategories').addEventListener('click', function () {
            toggleAllDocumentaryCategories(view, true);
        });

        view.querySelector('.btnDeselectAllDocumentaryCategories').addEventListener('click', function () {
            toggleAllDocumentaryCategories(view, false);
        });

        view.querySelector('.btnLoadDocumentaryCategoriesMulti').addEventListener('click', function () {
            saveConfig(self, function () {
                loadDocumentaryCategoriesMulti(self);
            });
        });

        // Series category buttons (single mode)
        view.querySelector('.btnLoadSeriesCategories').addEventListener('click', function () {
            saveConfig(self, function () {
                loadSeriesCategories(self);
            });
        });

        view.querySelector('.btnSelectAllSeriesCategories').addEventListener('click', function () {
            toggleAllSeriesCategories(view, true);
        });

        view.querySelector('.btnDeselectAllSeriesCategories').addEventListener('click', function () {
            toggleAllSeriesCategories(view, false);
        });

        // Series category buttons (multi mode)
        view.querySelector('.btnLoadSeriesCategoriesMulti').addEventListener('click', function () {
            saveConfig(self, function () {
                loadSeriesCategoriesMulti(self);
            });
        });

        view.querySelector('.btnLoadDocuSeriesCategories').addEventListener('click', function () {
            saveConfig(self, function () {
                loadDocuSeriesCategories(self);
            });
        });

        view.querySelector('.btnSelectAllDocuSeriesCategories').addEventListener('click', function () {
            toggleAllDocuSeriesCategories(view, true);
        });

        view.querySelector('.btnDeselectAllDocuSeriesCategories').addEventListener('click', function () {
            toggleAllDocuSeriesCategories(view, false);
        });

        view.querySelector('.btnLoadDocuSeriesCategoriesMulti').addEventListener('click', function () {
            saveConfig(self, function () {
                loadDocuSeriesCategoriesMulti(self);
            });
        });

        // Sync buttons
        view.querySelector('.btnSyncMovies').addEventListener('click', function () {
            syncMovies(view);
        });

        view.querySelector('.btnSyncDocumentaries').addEventListener('click', function () {
            syncDocumentaries(view);
        });

        view.querySelector('.btnSyncSeries').addEventListener('click', function () {
            syncSeries(view);
        });

        view.querySelector('.btnSyncDocuSeries').addEventListener('click', function () {
            syncDocuSeries(view);
        });

        view.querySelector('.btnStopMovies').addEventListener('click', function () {
            stopSync(view, '.syncMoviesResult');
        });

        view.querySelector('.btnStopDocumentaries').addEventListener('click', function () {
            stopSync(view, '.syncDocumentariesResult');
        });

        view.querySelector('.btnStopSeries').addEventListener('click', function () {
            stopSync(view, '.syncSeriesResult');
        });

        view.querySelector('.btnStopDocuSeries').addEventListener('click', function () {
            stopSync(view, '.syncDocuSeriesResult');
        });

        // Delete content buttons
        view.querySelector('.btnDeleteMovies').addEventListener('click', function () {
            deleteContent(view, 'Movies');
        });

        view.querySelector('.btnDeleteDocumentaries').addEventListener('click', function () {
            deleteContent(view, 'Documentaries');
        });

        view.querySelector('.btnDeleteSeries').addEventListener('click', function () {
            deleteContent(view, 'Series');
        });

        view.querySelector('.btnDeleteDocuSeries').addEventListener('click', function () {
            deleteContent(view, 'DocuSeries');
        });

        view.querySelector('.chkCleanupOrphans').addEventListener('change', function () {
            view.querySelector('.orphanThresholdContainer').style.display = this.checked ? '' : 'none';
        });

        // Dashboard sync all button
        view.querySelector('.btnDashboardSyncAll').addEventListener('click', function () {
            dashboardSyncAll(self);
        });

        // Retry failed items button
        view.querySelector('.btnRetryFailed').addEventListener('click', function () {
            retryFailed(view);
        });

        // Download sanitized log button
        view.querySelector('.btnDownloadLog').addEventListener('click', function () {
            window.open(ApiClient.getUrl('XC2EMBY/Logs') + '?api_key=' + ApiClient.accessToken(), '_blank');
        });

        // Danger zone toggles (event delegation on form)
        view.querySelector('.xtreamConfigForm').addEventListener('click', function (e) {
            var header = e.target.closest('.danger-zone-header');
            if (!header) return;
            var zone = header.parentNode;
            zone.classList.toggle('open');
            var arrow = header.querySelector('.danger-zone-arrow');
            if (arrow) arrow.textContent = zone.classList.contains('open') ? '\u25BC' : '\u25B6';
        });

        // Category search filters
        setupCategorySearch(view, '.vodCategorySearch', '.vodCategoriesList');
        setupCategorySearch(view, '.documentaryCategorySearch', '.documentaryCategoriesList');
        setupCategorySearch(view, '.seriesCategorySearch', '.seriesCategoriesList');
        setupCategorySearch(view, '.docuSeriesCategorySearch', '.docuSeriesCategoriesList');
        setupCategorySearch(view, '.liveCategorySearch', '.categoriesList');

        // Category checkbox change — live count badge updates
        view.querySelector('.vodCategoriesContainer').addEventListener('change', function (e) {
            if (e.target.classList.contains('vodCategoryCheckbox')) {
                updateCategoryCountBadge(view, 'vod');
            }
        });
        view.querySelector('.documentaryCategoriesContainer').addEventListener('change', function (e) {
            if (e.target.classList.contains('documentaryCategoryCheckbox')) {
                updateCategoryCountBadge(view, 'documentary');
            }
        });
        view.querySelector('.seriesCategoriesContainer').addEventListener('change', function (e) {
            if (e.target.classList.contains('seriesCategoryCheckbox')) {
                updateCategoryCountBadge(view, 'series');
            }
        });
        view.querySelector('.docuSeriesCategoriesContainer').addEventListener('change', function (e) {
            if (e.target.classList.contains('docuSeriesCategoryCheckbox')) {
                updateCategoryCountBadge(view, 'docuSeries');
            }
        });
        view.querySelector('.categoriesContainer').addEventListener('change', function (e) {
            if (e.target.classList.contains('categoryCheckbox')) {
                updateCategoryCountBadge(view, 'live');
            }
        });

        // Folder mode visual cards
        initFolderModeCards(view, 'movie');
        initFolderModeCards(view, 'documentary');
        initFolderModeCards(view, 'series');
        initFolderModeCards(view, 'docuSeries');

        // Empty-state "Go to Settings" button
        var btnGoToSettings = view.querySelector('.btnGoToSettings');
        if (btnGoToSettings) {
            btnGoToSettings.addEventListener('click', function () {
                switchTab(view, 'generic');
            });
        }

        // Tab buttons
        var tabBtns = view.querySelectorAll('.tabBtn');
        for (var i = 0; i < tabBtns.length; i++) {
            tabBtns[i].addEventListener('click', function () {
                var tab = this.getAttribute('data-tab');
                switchTab(view, tab);
                if (tab === 'dashboard') {
                    loadDashboard(view);
                }
                // Auto-load categories on tab switch if not already loaded
                if (tab === 'movies' && self.loadedVodCategories.length === 0) {
                    loadVodCategories(self);
                }
                if (tab === 'documentaries' && self.loadedDocumentaryCategories.length === 0) {
                    loadDocumentaryCategories(self);
                }
                if (tab === 'series' && self.loadedSeriesCategories.length === 0) {
                    loadSeriesCategories(self);
                }
                if (tab === 'docuSeries' && self.loadedDocuSeriesCategories.length === 0) {
                    loadDocuSeriesCategories(self);
                }
                if (tab === 'liveTv' && self.loadedCategories.length === 0) {
                    loadCategories(self);
                }
            });
        }
        switchTab(view, 'dashboard');
    }

    function initializeDocumentaryPanels(view) {
        var movieSource = view.querySelector('.tabMovies');
        var docTarget = view.querySelector('.tabDocumentaries');
        if (movieSource && docTarget && !docTarget.innerHTML.trim()) {
            docTarget.innerHTML = movieSource.innerHTML
                .replace(/chkSyncMovies/g, 'chkSyncDocumentaries')
                .replace(/vodMovieSettings/g, 'documentarySettings')
                .replace(/selMovieFolderMode/g, 'selDocumentaryFolderMode')
                .replace(/movieFolderModeCards/g, 'documentaryFolderModeCards')
                .replace(/movieSingleContainer/g, 'documentarySingleContainer')
                .replace(/btnLoadVodCategoriesMulti/g, 'btnLoadDocumentaryCategoriesMulti')
                .replace(/btnLoadVodCategories/g, 'btnLoadDocumentaryCategories')
                .replace(/vodCategoriesMultiStatus/g, 'documentaryCategoriesMultiStatus')
                .replace(/vodCategoriesStatus/g, 'documentaryCategoriesStatus')
                .replace(/btnSelectAllVodCategories/g, 'btnSelectAllDocumentaryCategories')
                .replace(/btnDeselectAllVodCategories/g, 'btnDeselectAllDocumentaryCategories')
                .replace(/vodCategorySearch/g, 'documentaryCategorySearch')
                .replace(/vodCategoryCountBadge/g, 'documentaryCategoryCountBadge')
                .replace(/vodCategoriesContainer/g, 'documentaryCategoriesContainer')
                .replace(/vodCategoriesLoading/g, 'documentaryCategoriesLoading')
                .replace(/vodCategoriesList/g, 'documentaryCategoriesList')
                .replace(/movieFoldersContainer/g, 'documentaryFoldersContainer')
                .replace(/movieFoldersList/g, 'documentaryFoldersList')
                .replace(/movieMultiFolderEmptyHint/g, 'documentaryMultiFolderEmptyHint')
                .replace(/btnAddMovieFolder/g, 'btnAddDocumentaryFolder')
                .replace(/btnSyncMovies/g, 'btnSyncDocumentaries')
                .replace(/btnStopMovies/g, 'btnStopDocumentaries')
                .replace(/syncMoviesResult/g, 'syncDocumentariesResult')
                .replace(/btnDeleteMovies/g, 'btnDeleteDocumentaries')
                .replace(/deleteMoviesResult/g, 'deleteDocumentariesResult')
                .replace(/vodCategoryCheckbox/g, 'documentaryCategoryCheckbox')
                .replace(/VOD Movies/g, 'Documentary Movies')
                .replace(/VOD movies/g, 'documentary movies')
                .replace(/movies/g, 'documentaries')
                .replace(/Movies/g, 'Documentaries')
                .replace(/Documentary Documentaries/g, 'Documentaries')
                .replace(/documentary documentaries/g, 'documentaries');
        }

        var seriesSource = view.querySelector('.tabSeries');
        var docuTarget = view.querySelector('.tabDocuSeries');
        if (seriesSource && docuTarget && !docuTarget.innerHTML.trim()) {
            docuTarget.innerHTML = seriesSource.innerHTML
                .replace(/chkSyncSeries/g, 'chkSyncDocuSeries')
                .replace(/seriesSettings/g, 'docuSeriesSettings')
                .replace(/selSeriesFolderMode/g, 'selDocuSeriesFolderMode')
                .replace(/seriesFolderModeCards/g, 'docuSeriesFolderModeCards')
                .replace(/seriesSingleContainer/g, 'docuSeriesSingleContainer')
                .replace(/btnLoadSeriesCategoriesMulti/g, 'btnLoadDocuSeriesCategoriesMulti')
                .replace(/btnLoadSeriesCategories/g, 'btnLoadDocuSeriesCategories')
                .replace(/seriesCategoriesMultiStatus/g, 'docuSeriesCategoriesMultiStatus')
                .replace(/seriesCategoriesStatus/g, 'docuSeriesCategoriesStatus')
                .replace(/btnSelectAllSeriesCategories/g, 'btnSelectAllDocuSeriesCategories')
                .replace(/btnDeselectAllSeriesCategories/g, 'btnDeselectAllDocuSeriesCategories')
                .replace(/seriesCategorySearch/g, 'docuSeriesCategorySearch')
                .replace(/seriesCategoryCountBadge/g, 'docuSeriesCategoryCountBadge')
                .replace(/seriesCategoriesContainer/g, 'docuSeriesCategoriesContainer')
                .replace(/seriesCategoriesLoading/g, 'docuSeriesCategoriesLoading')
                .replace(/seriesCategoriesList/g, 'docuSeriesCategoriesList')
                .replace(/seriesFoldersContainer/g, 'docuSeriesFoldersContainer')
                .replace(/seriesFoldersList/g, 'docuSeriesFoldersList')
                .replace(/seriesMultiFolderEmptyHint/g, 'docuSeriesMultiFolderEmptyHint')
                .replace(/btnAddSeriesFolder/g, 'btnAddDocuSeriesFolder')
                .replace(/btnSyncSeries/g, 'btnSyncDocuSeries')
                .replace(/btnStopSeries/g, 'btnStopDocuSeries')
                .replace(/syncSeriesResult/g, 'syncDocuSeriesResult')
                .replace(/btnDeleteSeries/g, 'btnDeleteDocuSeries')
                .replace(/deleteSeriesResult/g, 'deleteDocuSeriesResult')
                .replace(/seriesCategoryCheckbox/g, 'docuSeriesCategoryCheckbox')
                .replace(/Series \/ TV Shows/g, 'Docu Series')
                .replace(/series\/TV shows/g, 'documentary series')
                .replace(/series categories/g, 'docu series categories')
                .replace(/TV Shows/g, 'Docu Series')
                .replace(/Docu Docu Series/g, 'Docu Series')
                .replace(/docu docu series/g, 'docu series');
        }
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        loadConfig(this);
        loadDashboard(this.view);
    };

    View.prototype.onPause = function () {};

    // emby-checkbox uses a Polymer-based custom element that initialises lazily.
    // Setting .checked on an element inside a hidden panel (display:none) does not
    // trigger a visual re-render; the element renders from its HTML attribute when
    // the panel is first shown, ignoring the earlier programmatic assignment.
    // setChecked() updates BOTH the JS property AND the HTML attribute so the
    // element renders correctly regardless of panel visibility.
    function setChecked(el, value) {
        el.checked = value;
        if (value) {
            el.setAttribute('checked', '');
        } else {
            el.removeAttribute('checked');
        }
    }

    function loadConfig(instance) {
        loading.show();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            var view = instance.view;

            view.querySelector('.txtBaseUrl').value = config.BaseUrl || '';
            view.querySelector('.txtUsername').value = config.Username || '';
            view.querySelector('.txtPassword').value = config.Password || '';
            view.querySelector('.txtHttpUserAgent').value = config.HttpUserAgent || '';

            setChecked(view.querySelector('.chkEnableLiveTv'), config.EnableLiveTv !== false);
            view.querySelector('.selOutputFormat').value = config.LiveTvOutputFormat || 'ts';
            view.querySelector('.txtTunerCount').value = config.TunerCount > 0 ? config.TunerCount : 1;
            setChecked(view.querySelector('.chkLiveTvDirectPlay'), config.EnableLiveTvDirectPlay !== false);
            setChecked(view.querySelector('.chkIncludeGroupTitle'), config.IncludeGroupTitleInM3U !== false);

            var epgVal = config.EpgSource;
            var epgNameToInt = { 'XtreamServer': '0', 'CustomUrl': '1', 'Disabled': '2' };
            view.querySelector('.selectEpgSource').value = epgNameToInt[epgVal] || (epgVal || 0).toString();
            view.querySelector('.txtCustomEpgUrl').value = config.CustomEpgUrl || '';
            view.querySelector('.txtEpgCacheMinutes').value = config.EpgCacheMinutes || 30;
            view.querySelector('.txtEpgDaysToFetch').value = config.EpgDaysToFetch || 2;
            view.querySelector('.txtM3UCacheMinutes').value = config.M3UCacheMinutes || 15;

            instance.selectedCategoryIds = config.SelectedLiveCategoryIds || [];

            // Unified name cleaning (drives both content + channel cleaning)
            var nameCleaningEnabled = !!config.EnableContentNameCleaning || !!config.EnableChannelNameCleaning;
            setChecked(view.querySelector('.chkEnableNameCleaning'), nameCleaningEnabled);
            var removeTerms = config.ContentRemoveTerms || '';
            if (!removeTerms && config.ChannelRemoveTerms) {
                removeTerms = config.ChannelRemoveTerms.split(',').map(function (t) { return t.trim(); }).filter(function (t) { return t; }).join('\n');
            }
            view.querySelector('.txtRemoveTerms').value = removeTerms;

            // Pre-parse cached categories so folder cards render correctly from the start
            var cachedVodCats = null;
            if (config.CachedVodCategories) {
                try { cachedVodCats = JSON.parse(config.CachedVodCategories); } catch (e) {}
            }
            var cachedSeriesCats = null;
            if (config.CachedSeriesCategories) {
                try { cachedSeriesCats = JSON.parse(config.CachedSeriesCategories); } catch (e) {}
            }

            // VOD Movies
            setChecked(view.querySelector('.chkSyncMovies'), !!config.SyncMovies);
            var movieMode = config.MovieFolderMode || 'single';
            if (movieMode === 'multiple') movieMode = 'custom';
            view.querySelector('.selMovieFolderMode').value = movieMode;
            loadFolderEntries(view, 'movie', config.MovieFolderMappings || '', cachedVodCats);
            instance.selectedVodCategoryIds = config.SelectedVodCategoryIds || [];

            // Documentary Movies
            setChecked(view.querySelector('.chkSyncDocumentaries'), !!config.SyncDocumentaries);
            var documentaryMode = config.DocumentaryFolderMode || 'single';
            if (documentaryMode === 'multiple') documentaryMode = 'custom';
            view.querySelector('.selDocumentaryFolderMode').value = documentaryMode;
            loadFolderEntries(view, 'documentary', config.DocumentaryFolderMappings || '', cachedVodCats);
            instance.selectedDocumentaryCategoryIds = config.SelectedDocumentaryCategoryIds || [];

            // Series
            setChecked(view.querySelector('.chkSyncSeries'), !!config.SyncSeries);
            var seriesMode = config.SeriesFolderMode || 'single';
            if (seriesMode === 'multiple') seriesMode = 'custom';
            view.querySelector('.selSeriesFolderMode').value = seriesMode;
            loadFolderEntries(view, 'series', config.SeriesFolderMappings || '', cachedSeriesCats);
            instance.selectedSeriesCategoryIds = config.SelectedSeriesCategoryIds || [];

            // Docu Series
            setChecked(view.querySelector('.chkSyncDocuSeries'), !!config.SyncDocuSeries);
            var docuSeriesMode = config.DocuSeriesFolderMode || 'single';
            if (docuSeriesMode === 'multiple') docuSeriesMode = 'custom';
            view.querySelector('.selDocuSeriesFolderMode').value = docuSeriesMode;
            loadFolderEntries(view, 'docuSeries', config.DocuSeriesFolderMappings || '', cachedSeriesCats);
            instance.selectedDocuSeriesCategoryIds = config.SelectedDocuSeriesCategoryIds || [];

            // Sync settings
            view.querySelector('.txtStrmLibraryPath').value = config.StrmLibraryPath || '/config/xtream';
            view.querySelector('.txtMovieRootFolderName').value = config.MovieRootFolderName || 'Movies';
            view.querySelector('.txtDocumentaryRootFolderName').value = config.DocumentaryRootFolderName || 'Documentaries';
            view.querySelector('.txtSeriesRootFolderName').value = config.SeriesRootFolderName || 'TV Shows';
            view.querySelector('.txtDocuSeriesRootFolderName').value = config.DocuSeriesRootFolderName || 'Docu Series';
            validateStrmPath(view);
            setChecked(view.querySelector('.chkSmartSkipExisting'), config.SmartSkipExisting !== false);
            view.querySelector('.txtSyncParallelism').value = config.SyncParallelism || 3;
            setChecked(view.querySelector('.chkCleanupOrphans'), !!config.CleanupOrphans);
            view.querySelector('.txtOrphanSafetyThreshold').value = Math.round((config.OrphanSafetyThreshold || 0.20) * 100);
            view.querySelector('.orphanThresholdContainer').style.display = config.CleanupOrphans ? '' : 'none';
            setChecked(view.querySelector('.chkEnableNfoFiles'), !!config.EnableNfoFiles);

            // Auto-sync schedule
            setChecked(view.querySelector('.chkAutoSyncEnabled'), !!config.AutoSyncEnabled);
            view.querySelector('.selAutoSyncMode').value = config.AutoSyncMode || 'interval';
            view.querySelector('.txtAutoSyncIntervalHours').value = config.AutoSyncIntervalHours || 24;
            view.querySelector('.txtAutoSyncDailyTime').value = config.AutoSyncDailyTime || '03:00';
            updateAutoSyncVisibility(view);

            // Metadata ID naming (unified)
            var metadataIdEnabled = !!config.EnableTmdbFolderNaming || !!config.EnableSeriesIdFolderNaming;
            setChecked(view.querySelector('.chkEnableTmdbFolderNaming'), metadataIdEnabled);
            var fallbackEnabled = !!config.EnableTmdbFallbackLookup || !!config.EnableSeriesMetadataLookup;
            setChecked(view.querySelector('.chkEnableTmdbFallbackLookup'), fallbackEnabled);
            view.querySelector('.txtTvdbFolderIdOverrides').value = config.TvdbFolderIdOverrides || '';

            updateTmdbVisibility(view);
            updateNameCleaningVisibility(view);
            updateEpgVisibility(view);
            updateVodMovieVisibility(view);
            updateDocumentaryVisibility(view);
            updateSeriesVisibility(view);
            updateDocuSeriesVisibility(view);
            updateFoldersVisibility(view, 'movie');
            updateFoldersVisibility(view, 'documentary');
            updateFoldersVisibility(view, 'series');
            updateFoldersVisibility(view, 'docuSeries');

            // Sync folder mode card visuals to loaded select values
            syncFolderModeCards(view, 'movie');
            syncFolderModeCards(view, 'documentary');
            syncFolderModeCards(view, 'series');
            syncFolderModeCards(view, 'docuSeries');

            // Health bar, auto-sync line, empty-state
            renderHealthBar(view, config);
            renderAutoSyncDashboardLine(view, config);
            updateDashboardEmptyState(view, config);

            loading.hide();

            // Load cached categories from config (instant, no API call)
            loadCachedCategories(instance, config);
        }).catch(function (err) {
            loading.hide();
            console.error('XC2EMBY: failed to load plugin configuration', err);
        });
    }

    function saveConfig(instance, callback) {
        loading.show();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            var view = instance.view;

            config.BaseUrl = view.querySelector('.txtBaseUrl').value.replace(/\/+$/, '');
            config.Username = view.querySelector('.txtUsername').value;
            config.Password = view.querySelector('.txtPassword').value;
            config.HttpUserAgent = view.querySelector('.txtHttpUserAgent').value;

            config.EnableLiveTv = view.querySelector('.chkEnableLiveTv').checked;
            config.LiveTvOutputFormat = view.querySelector('.selOutputFormat').value;
            config.TunerCount = Math.max(1, parseInt(view.querySelector('.txtTunerCount').value, 10) || 1);
            config.IncludeAdultChannels = false;
            config.EnableLiveTvDirectPlay = view.querySelector('.chkLiveTvDirectPlay').checked;
            config.IncludeGroupTitleInM3U = view.querySelector('.chkIncludeGroupTitle').checked;

            config.EpgSource = parseInt(view.querySelector('.selectEpgSource').value, 10);
            config.CustomEpgUrl = view.querySelector('.txtCustomEpgUrl').value.trim();
            config.EpgCacheMinutes = parseInt(view.querySelector('.txtEpgCacheMinutes').value, 10) || 30;
            config.EpgDaysToFetch = parseInt(view.querySelector('.txtEpgDaysToFetch').value, 10) || 2;
            config.M3UCacheMinutes = parseInt(view.querySelector('.txtM3UCacheMinutes').value, 10) || 15;

            config.SelectedLiveCategoryIds = getSelectedCategoryIds(instance);

            // Unified name cleaning → both backend properties
            var nameCleaningOn = view.querySelector('.chkEnableNameCleaning').checked;
            var removeTermsVal = view.querySelector('.txtRemoveTerms').value;
            config.EnableContentNameCleaning = nameCleaningOn;
            config.EnableChannelNameCleaning = nameCleaningOn;
            config.ContentRemoveTerms = removeTermsVal;
            config.ChannelRemoveTerms = removeTermsVal.split('\n').map(function (t) { return t.trim(); }).filter(function (t) { return t; }).join(',');

            // VOD Movies
            config.SyncMovies = view.querySelector('.chkSyncMovies').checked;
            config.MovieFolderMode = view.querySelector('.selMovieFolderMode').value;
            config.MovieFolderMappings = serializeFolderEntries(view, 'movie');
            config.SelectedVodCategoryIds = getSelectedVodCategoryIds(instance);

            // Documentary Movies
            config.SyncDocumentaries = view.querySelector('.chkSyncDocumentaries').checked;
            config.DocumentaryFolderMode = view.querySelector('.selDocumentaryFolderMode').value;
            config.DocumentaryFolderMappings = serializeFolderEntries(view, 'documentary');
            config.SelectedDocumentaryCategoryIds = getSelectedDocumentaryCategoryIds(instance);

            // Series
            config.SyncSeries = view.querySelector('.chkSyncSeries').checked;
            config.SeriesFolderMode = view.querySelector('.selSeriesFolderMode').value;
            config.SeriesFolderMappings = serializeFolderEntries(view, 'series');
            config.SelectedSeriesCategoryIds = getSelectedSeriesCategoryIds(instance);

            // Docu Series
            config.SyncDocuSeries = view.querySelector('.chkSyncDocuSeries').checked;
            config.DocuSeriesFolderMode = view.querySelector('.selDocuSeriesFolderMode').value;
            config.DocuSeriesFolderMappings = serializeFolderEntries(view, 'docuSeries');
            config.SelectedDocuSeriesCategoryIds = getSelectedDocuSeriesCategoryIds(instance);

            // Sync settings
            config.StrmLibraryPath = view.querySelector('.txtStrmLibraryPath').value.replace(/\/+$/, '') || '/config/xtream';
            config.MovieRootFolderName = (view.querySelector('.txtMovieRootFolderName').value || 'Movies').trim() || 'Movies';
            config.DocumentaryRootFolderName = (view.querySelector('.txtDocumentaryRootFolderName').value || 'Documentaries').trim() || 'Documentaries';
            config.SeriesRootFolderName = (view.querySelector('.txtSeriesRootFolderName').value || 'TV Shows').trim() || 'TV Shows';
            config.DocuSeriesRootFolderName = (view.querySelector('.txtDocuSeriesRootFolderName').value || 'Docu Series').trim() || 'Docu Series';
            config.SmartSkipExisting = view.querySelector('.chkSmartSkipExisting').checked;
            config.SyncParallelism = parseInt(view.querySelector('.txtSyncParallelism').value, 10) || 3;
            config.CleanupOrphans = view.querySelector('.chkCleanupOrphans').checked;
            config.OrphanSafetyThreshold = (parseInt(view.querySelector('.txtOrphanSafetyThreshold').value, 10) || 0) / 100;
            config.EnableNfoFiles = view.querySelector('.chkEnableNfoFiles').checked;

            // Auto-sync schedule
            config.AutoSyncEnabled = view.querySelector('.chkAutoSyncEnabled').checked;
            config.AutoSyncMode = view.querySelector('.selAutoSyncMode').value;
            config.AutoSyncIntervalHours = parseInt(view.querySelector('.txtAutoSyncIntervalHours').value, 10) || 24;
            config.AutoSyncDailyTime = view.querySelector('.txtAutoSyncDailyTime').value || '03:00';

            // Metadata ID naming (unified → both backend properties)
            var metadataIdOn = view.querySelector('.chkEnableTmdbFolderNaming').checked;
            config.EnableTmdbFolderNaming = metadataIdOn;
            config.EnableSeriesIdFolderNaming = metadataIdOn;
            var fallbackOn = view.querySelector('.chkEnableTmdbFallbackLookup').checked;
            config.EnableTmdbFallbackLookup = fallbackOn;
            config.EnableSeriesMetadataLookup = fallbackOn;
            config.TvdbFolderIdOverrides = view.querySelector('.txtTvdbFolderIdOverrides').value;

            ApiClient.updatePluginConfiguration(pluginId, config).then(function () {
                loading.hide();
                Dashboard.processPluginConfigurationUpdateResult();
                applyScheduleToTasks(view, config, ApiClient);
                if (typeof callback === 'function') callback();
            }).catch(function (err) {
                loading.hide();
                console.error('XC2EMBY: updatePluginConfiguration failed', err);
                Dashboard.alert('Failed to save configuration. Check the browser console for details.');
            });
        }).catch(function (err) {
            loading.hide();
            console.error('XC2EMBY: getPluginConfiguration failed', err);
            Dashboard.alert('Failed to load configuration before saving. Try a hard refresh (Ctrl+Shift+R) and try again.');
        });
    }

    function switchTab(view, tabName) {
        var panels = view.querySelectorAll('.tabPanel');
        for (var i = 0; i < panels.length; i++) {
            panels[i].style.display = 'none';
        }

        var btns = view.querySelectorAll('.tabBtn');
        for (var i = 0; i < btns.length; i++) {
            btns[i].style.opacity = '0.7';
            btns[i].style.borderBottomColor = 'transparent';
        }

        var panelMap = { dashboard: '.tabDashboard', generic: '.tabGeneric', movies: '.tabMovies', documentaries: '.tabDocumentaries', series: '.tabSeries', docuSeries: '.tabDocuSeries', liveTv: '.tabLiveTv' };
        var btnMap = { dashboard: '.tabBtnDashboard', generic: '.tabBtnGeneric', movies: '.tabBtnMovies', documentaries: '.tabBtnDocumentaries', series: '.tabBtnSeries', docuSeries: '.tabBtnDocuSeries', liveTv: '.tabBtnLiveTv' };

        var panel = view.querySelector(panelMap[tabName]);
        if (panel) panel.style.display = 'block';

        var btn = view.querySelector(btnMap[tabName]);
        if (btn) {
            btn.style.opacity = '1';
            btn.style.setProperty('border-bottom-color', accentColor);
        }

        // Hide Save button on Dashboard — nothing to save there
        var footer = view.querySelector('.stickyFooter');
        if (footer) footer.style.display = tabName === 'dashboard' ? 'none' : '';
    }

    function updateTmdbVisibility(view) {
        var enabled = view.querySelector('.chkEnableTmdbFolderNaming').checked;
        view.querySelector('.tmdbSettings').style.display = enabled ? '' : 'none';
    }

    function updateEpgVisibility(view) {
        var source = parseInt(view.querySelector('.selectEpgSource').value, 10);
        view.querySelector('.epgSettings').style.display = source !== 2 ? '' : 'none';
        view.querySelector('.epgCustomUrlSettings').style.display = source === 1 ? '' : 'none';
    }

    function updateNameCleaningVisibility(view) {
        var enabled = view.querySelector('.chkEnableNameCleaning').checked;
        view.querySelector('.nameCleaningSettings').style.display = enabled ? '' : 'none';
    }

    function updateVodMovieVisibility(view) {
        var enabled = view.querySelector('.chkSyncMovies').checked;
        view.querySelector('.vodMovieSettings').style.display = enabled ? '' : 'none';
    }

    function updateDocumentaryVisibility(view) {
        var enabled = view.querySelector('.chkSyncDocumentaries').checked;
        view.querySelector('.documentarySettings').style.display = enabled ? '' : 'none';
    }

    function updateSeriesVisibility(view) {
        var enabled = view.querySelector('.chkSyncSeries').checked;
        view.querySelector('.seriesSettings').style.display = enabled ? '' : 'none';
    }

    function updateDocuSeriesVisibility(view) {
        var enabled = view.querySelector('.chkSyncDocuSeries').checked;
        view.querySelector('.docuSeriesSettings').style.display = enabled ? '' : 'none';
    }

    function updateFoldersVisibility(view, type) {
        var ui = folderUi(type);
        var mode = view.querySelector(ui.select).value;
        var isMulti = mode === 'custom';
        view.querySelector(ui.single).style.display  = isMulti ? 'none'  : 'block';
        view.querySelector(ui.multi).style.display   = isMulti ? 'block' : 'none';
        view.querySelector(ui.list).style.display    = isMulti ? ''      : 'none';
        view.querySelector(ui.addBtn).style.display  = isMulti ? ''      : 'none';
        updateMultiFolderEmptyHints(view);
    }

    function updateMultiFolderEmptyHints(view) {
        function one(type) {
            var ui = folderUi(type);
            var mode = view.querySelector(ui.select).value;
            var list = view.querySelector(ui.list);
            var hint = view.querySelector(ui.hint);
            if (!hint || !list) return;
            var isMulti = mode === 'custom';
            var hasCards = list.querySelectorAll('.folderCard').length > 0;
            hint.style.display = (isMulti && !hasCards) ? 'block' : 'none';
        }
        one('movie');
        one('documentary');
        one('series');
        one('docuSeries');
    }

    function updateAutoSyncVisibility(v) {
        var enabled = v.querySelector('.chkAutoSyncEnabled').checked;
        v.querySelector('.autoSyncSettings').style.display = enabled ? '' : 'none';
        var mode = v.querySelector('.selAutoSyncMode').value;
        v.querySelector('.autoSyncIntervalContainer').style.display = mode === 'interval' ? '' : 'none';
        v.querySelector('.autoSyncDailyContainer').style.display    = mode === 'daily'    ? '' : 'none';
    }

    function buildTriggers(config) {
        if (!config.AutoSyncEnabled) return [];
        if (config.AutoSyncMode === 'daily') {
            var parts = (config.AutoSyncDailyTime || '03:00').split(':');
            var ticks = (parseInt(parts[0], 10) * 3600 + parseInt(parts[1] || '0', 10) * 60) * 10000000;
            return [{ Type: 'DailyTrigger', TimeOfDayTicks: ticks }];
        }
        // interval
        var hours = Math.max(1, config.AutoSyncIntervalHours || 24);
        return [{ Type: 'IntervalTrigger', IntervalTicks: hours * 36000000000 }];
    }

    function applyScheduleToTasks(view, config, apiClient) {
        apiClient.ajax({ url: apiClient.getUrl('ScheduledTasks'), type: 'GET' })
            .then(function (tasks) {
                var xtreamTasks = tasks.filter(function (t) {
                    return t.Category === 'XC2EMBY';
                });
                var triggers = buildTriggers(config);
                xtreamTasks.forEach(function (task) {
                    apiClient.ajax({
                        url: apiClient.getUrl('ScheduledTasks/' + task.Id + '/Triggers'),
                        type: 'POST',
                        contentType: 'application/json',
                        data: JSON.stringify(triggers)
                    });
                });
            });
    }

    // ---- Folder card management (for Multiple Folders mode) ----

    function folderUi(type) {
        var map = {
            movie: {
                select: '.selMovieFolderMode', single: '.movieSingleContainer', multi: '.movieFoldersContainer',
                list: '.movieFoldersList', addBtn: '.btnAddMovieFolder', hint: '.movieMultiFolderEmptyHint',
                cards: '.movieFolderModeCards'
            },
            documentary: {
                select: '.selDocumentaryFolderMode', single: '.documentarySingleContainer', multi: '.documentaryFoldersContainer',
                list: '.documentaryFoldersList', addBtn: '.btnAddDocumentaryFolder', hint: '.documentaryMultiFolderEmptyHint',
                cards: '.documentaryFolderModeCards'
            },
            series: {
                select: '.selSeriesFolderMode', single: '.seriesSingleContainer', multi: '.seriesFoldersContainer',
                list: '.seriesFoldersList', addBtn: '.btnAddSeriesFolder', hint: '.seriesMultiFolderEmptyHint',
                cards: '.seriesFolderModeCards'
            },
            docuSeries: {
                select: '.selDocuSeriesFolderMode', single: '.docuSeriesSingleContainer', multi: '.docuSeriesFoldersContainer',
                list: '.docuSeriesFoldersList', addBtn: '.btnAddDocuSeriesFolder', hint: '.docuSeriesMultiFolderEmptyHint',
                cards: '.docuSeriesFolderModeCards'
            }
        };
        return map[type];
    }

    function addFolderEntry(view, type, name, checkedIdsStr, categories) {
        var ui = folderUi(type);
        var list = view.querySelector(ui.list);

        var card = document.createElement('div');
        card.className = 'folderCard';
        card.setAttribute('data-checked-ids', checkedIdsStr || '');
        card.style.cssText = 'background:rgba(128,128,128,0.04); border:1px solid rgba(128,128,128,0.15); border-radius:8px; padding:1.2em 1.4em; margin-bottom:1em;';

        // Header: name input + remove button
        var header = document.createElement('div');
        header.style.cssText = 'display:flex; gap:0.5em; align-items:center; margin-bottom:0.5em;';

        var nameInput = document.createElement('input');
        nameInput.type = 'text';
        nameInput.className = 'folderCardName';
        nameInput.placeholder = 'e.g. Drama';
        nameInput.value = name;
        nameInput.style.cssText = 'flex:1; padding:0.5em 0.8em; background:transparent; border:1px solid rgba(128,128,128,0.25); border-radius:4px; color:inherit; font-size:1em;';

        var removeBtn = document.createElement('button');
        removeBtn.type = 'button';
        removeBtn.textContent = 'Remove';
        removeBtn.style.cssText = 'background:#c0392b; color:white; border:none; border-radius:4px; padding:0.5em 1em; cursor:pointer; font-size:0.9em;';
        removeBtn.addEventListener('click', function () {
            card.parentNode.removeChild(card);
            updateMultiFolderEmptyHints(view);
        });

        header.appendChild(nameInput);
        header.appendChild(removeBtn);
        card.appendChild(header);

        // Category checkboxes container
        var catContainer = document.createElement('div');
        catContainer.className = 'folderCardCategories';
        catContainer.style.cssText = 'max-height:300px; overflow-y:auto; border:1px solid rgba(128,128,128,0.15); border-radius:4px; padding:0.5em;';

        if (categories && categories.length > 0) {
            renderFolderCardCategories(catContainer, categories, checkedIdsStr);
        } else if (categories !== null && categories !== undefined) {
            catContainer.innerHTML = '<div style="opacity:0.5; padding:0.5em;">No categories available from server. Click Refresh Categories to try again.</div>';
        } else {
            catContainer.innerHTML = '<div style="opacity:0.5; padding:0.5em;">Loading categories...</div>';
        }

        card.appendChild(catContainer);
        list.appendChild(card);
        updateMultiFolderEmptyHints(view);
    }

    function renderFolderCardCategories(container, categories, checkedIdsStr) {
        var checkedIds = [];
        if (checkedIdsStr) {
            var parts = checkedIdsStr.split(',');
            for (var i = 0; i < parts.length; i++) {
                var n = parseInt(parts[i].trim(), 10);
                if (!isNaN(n)) checkedIds.push(n);
            }
        }

        var html = '';
        for (var i = 0; i < categories.length; i++) {
            var cat = categories[i];
            var checked = checkedIds.indexOf(cat.CategoryId) >= 0 ? ' checked' : '';
            html += '<div class="checkboxContainer" style="margin:0.3em 0; padding:0.2em 0.5em;">';
            html += '<label style="display:flex; align-items:center; cursor:pointer;">';
            html += '<input type="checkbox" class="folderCategoryCheckbox" data-category-id="' + cat.CategoryId + '"' + checked + ' style="margin-right:0.5em;" />';
            html += '<span>' + escapeHtml(cat.CategoryName) + ' <span style="opacity:0.5;">(ID: ' + cat.CategoryId + ')</span></span>';
            html += '</label>';
            html += '</div>';
        }
        container.innerHTML = html;
    }

    function clearFolderCardCategories(view, type) {
        var ui = folderUi(type);
        var cards = view.querySelectorAll(ui.list + ' .folderCard');
        for (var i = 0; i < cards.length; i++) {
            var catContainer = cards[i].querySelector('.folderCardCategories');
            catContainer.innerHTML = '<div style="opacity:0.5; padding:0.5em;">No categories available from server.</div>';
        }
    }

    function populateFolderCheckboxes(view, type, categories) {
        var ui = folderUi(type);
        var cards = view.querySelectorAll(ui.list + ' .folderCard');
        for (var i = 0; i < cards.length; i++) {
            var card = cards[i];
            var checkedIdsStr = card.getAttribute('data-checked-ids') || '';
            var catContainer = card.querySelector('.folderCardCategories');
            renderFolderCardCategories(catContainer, categories, checkedIdsStr);
        }
    }

    function loadFolderEntries(view, type, mappingsText, categories) {
        var ui = folderUi(type);
        view.querySelector(ui.list).innerHTML = '';

        if (!mappingsText) return;

        var lines = mappingsText.split('\n');
        for (var i = 0; i < lines.length; i++) {
            var line = lines[i].trim();
            if (!line) continue;
            var eqIdx = line.indexOf('=');
            if (eqIdx < 0) continue;
            var name = line.substring(0, eqIdx).trim();
            var ids = line.substring(eqIdx + 1).trim();
            addFolderEntry(view, type, name, ids, categories);
        }
    }

    function serializeFolderEntries(view, type) {
        var ui = folderUi(type);
        var cards = view.querySelectorAll(ui.list + ' .folderCard');
        var lines = [];
        for (var i = 0; i < cards.length; i++) {
            var name = cards[i].querySelector('.folderCardName').value.trim();
            if (!name) continue;

            // Check if checkboxes have been rendered (categories loaded)
            var allCheckboxes = cards[i].querySelectorAll('.folderCategoryCheckbox');
            var ids = [];
            if (allCheckboxes.length > 0) {
                // Categories loaded - read from checked checkboxes
                var checkedBoxes = cards[i].querySelectorAll('.folderCategoryCheckbox:checked');
                for (var j = 0; j < checkedBoxes.length; j++) {
                    ids.push(checkedBoxes[j].getAttribute('data-category-id'));
                }
            } else {
                // Categories not loaded yet - fall back to stored data attribute
                var storedIds = cards[i].getAttribute('data-checked-ids') || '';
                if (storedIds) {
                    var parts = storedIds.split(',');
                    for (var j = 0; j < parts.length; j++) {
                        var s = parts[j].trim();
                        if (s) ids.push(s);
                    }
                }
            }

            if (ids.length > 0) {
                lines.push(name + '=' + ids.join(','));
            }
        }
        return lines.join('\n');
    }

    function testXtreamConnection(instance) {
        var view = instance.view;
        var resultEl = view.querySelector('.connectionTestResult');
        resultEl.innerHTML = '<span style="opacity:0.5;">Testing connection...</span>';

        var url = view.querySelector('.txtBaseUrl').value.replace(/\/+$/, '');
        var user = view.querySelector('.txtUsername').value;
        var pass = view.querySelector('.txtPassword').value;

        if (!url || !user || !pass) {
            setPillResult(resultEl, false, 'Please enter server URL, username, and password.');
            return;
        }

        var testUrl = url + '/player_api.php?username=' + encodeURIComponent(user) + '&password=' + encodeURIComponent(pass);
        var xhr = new XMLHttpRequest();
        xhr.open('GET', testUrl, true);
        xhr.timeout = 10000;

        xhr.onload = function () {
            if (xhr.status >= 200 && xhr.status < 300) {
                try {
                    var resp = JSON.parse(xhr.responseText);
                    if (resp.user_info) {
                        var status = resp.user_info.status || 'unknown';
                        var msg = 'Connected as ' + user + ' — status: ' + status;
                        if (resp.user_info.active_cons !== undefined) {
                            msg += ', ' + resp.user_info.active_cons;
                            if (resp.user_info.max_connections !== undefined) {
                                msg += '/' + resp.user_info.max_connections;
                            }
                            msg += ' active streams';
                        }
                        setPillResult(resultEl, true, msg);
                    } else {
                        setPillResult(resultEl, true, 'Connection successful!');
                    }
                } catch (e) {
                    setPillResult(resultEl, true, 'Connection successful (non-JSON response).');
                }
                saveConfig(instance);
            } else {
                setPillResult(resultEl, false, 'Connection failed (HTTP ' + xhr.status + ').');
            }
        };

        xhr.onerror = function () {
            setPillResult(resultEl, false, 'Connection failed. Check URL and ensure server is reachable.');
        };

        xhr.ontimeout = function () {
            setPillResult(resultEl, false, 'Connection timed out.');
        };

        xhr.send();
    }

    // ---- Folder browser ----

    function openBrowser(view, targetFieldSel) {
        var modal = view.querySelector('.strmBrowserModal');
        modal.setAttribute('data-target-field', targetFieldSel || '.txtStrmLibraryPath');
        modal.style.display = 'flex';
        // Always start navigation at the STRM library root regardless of which field triggered
        var startPath = (view.querySelector('.txtStrmLibraryPath').value || '').trim() || null;
        browserNavigate(view, startPath);
    }

    function closeBrowser(view) {
        view.querySelector('.strmBrowserModal').style.display = 'none';
    }

    function browserNavigate(view, path) {
        var modal = view.querySelector('.strmBrowserModal');
        var listEl = modal.querySelector('.browserList');
        listEl.innerHTML = '<div style="padding:1.2em 1.5em; opacity:0.5;">Loading...</div>';
        modal.querySelector('.txtBrowserCurrentPath').value = path || '';

        var url = ApiClient.getUrl('XC2EMBY/BrowsePath');
        if (path) url += '?path=' + encodeURIComponent(path);

        ApiClient.ajax({ type: 'GET', url: url, dataType: 'json' })
            .then(function (result) { browserRenderList(view, result); })
            .catch(function () {
                listEl.innerHTML = '<div style="padding:1.2em 1.5em; color:#cc0000;">Failed to load directory.</div>';
            });
    }

    function browserRenderList(view, result) {
        var modal = view.querySelector('.strmBrowserModal');
        var listEl = modal.querySelector('.browserList');
        listEl.innerHTML = '';

        modal.querySelector('.txtBrowserCurrentPath').value = result.CurrentPath || '';

        var isRoot = !result.CurrentPath;

        if (!isRoot) {
            var upRow = createBrowserRow('', '../  Parent directory', function () {
                browserNavigate(view, result.ParentPath || null);
            });
            listEl.appendChild(upRow);
        }

        if (result.Directories && result.Directories.length > 0) {
            result.Directories.forEach(function (dir) {
                var parts = dir.replace(/\\/g, '/').split('/').filter(function (p) { return p.length > 0; });
                var name = parts.length > 0 ? parts[parts.length - 1] : dir;
                var row = createBrowserRow('\u2192', name, function () {
                    browserNavigate(view, dir);
                });
                listEl.appendChild(row);
            });
        } else if (isRoot) {
            listEl.innerHTML = '<div style="padding:1.2em 1.5em; opacity:0.5;">No accessible folders found.</div>';
        } else {
            var emptyEl = document.createElement('div');
            emptyEl.style.cssText = 'padding:1.2em 1.5em; opacity:0.5;';
            emptyEl.textContent = 'No subdirectories.';
            listEl.appendChild(emptyEl);
        }
    }

    function createBrowserRow(icon, label, onClick) {
        var row = document.createElement('div');
        row.className = 'browserRow';
        row.style.cssText = 'display:flex; align-items:center; gap:0.8em; padding:0.6em 1.5em; cursor:pointer; border-bottom:1px solid rgba(128,128,128,0.07); user-select:none;';
        var iconEl = document.createElement('span');
        iconEl.textContent = icon;
        iconEl.style.cssText = 'flex-shrink:0; width:1.2em; text-align:center; opacity:0.6;';
        var labelEl = document.createElement('span');
        labelEl.textContent = label;
        labelEl.style.cssText = 'font-size:0.9em; font-family:monospace;';
        row.appendChild(iconEl);
        row.appendChild(labelEl);
        row.addEventListener('click', onClick);
        return row;
    }

    function validateStrmPath(view) {
        var path = (view.querySelector('.txtStrmLibraryPath').value || '').trim();
        var resultEl = view.querySelector('.strmPathValidationResult');
        if (!path) {
            resultEl.innerHTML = '';
            return;
        }
        resultEl.innerHTML = '<span style="opacity:0.5;">Checking path...</span>';
        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('XC2EMBY/ValidateStrmPath'),
            contentType: 'application/json',
            data: JSON.stringify({ Path: path }),
            dataType: 'json'
        }).then(function (result) {
            setPillResult(resultEl, result.Success, result.Message);
        }).catch(function () {
            setPillResult(resultEl, false, 'Validation request failed.');
        });
    }

    // ---- Cached category loading (instant from config) ----

    function loadCachedCategories(instance, config) {
        var view = instance.view;

        // VOD categories
        var vodLoaded = false;
        if (config.CachedVodCategories) {
            try {
                var vodCats = JSON.parse(config.CachedVodCategories);
                if (vodCats && vodCats.length > 0) {
                    vodLoaded = true;
                    instance.loadedVodCategories = vodCats;
                    renderCategoryList(view, '.vodCategoriesList', vodCats, 'vodCategoryCheckbox', instance.selectedVodCategoryIds);
                    view.querySelector('.btnSelectAllVodCategories').disabled = false;
                    view.querySelector('.btnDeselectAllVodCategories').disabled = false;
                    var statusEl = view.querySelector('.vodCategoriesStatus');
                    if (statusEl) statusEl.textContent = '';
                    updateCategoryCountBadge(view, 'vod');

                    populateFolderCheckboxes(view, 'movie', vodCats);
                    instance.loadedDocumentaryCategories = vodCats;
                    renderCategoryList(view, '.documentaryCategoriesList', vodCats, 'documentaryCategoryCheckbox', instance.selectedDocumentaryCategoryIds);
                    view.querySelector('.btnSelectAllDocumentaryCategories').disabled = false;
                    view.querySelector('.btnDeselectAllDocumentaryCategories').disabled = false;
                    var docStatusEl = view.querySelector('.documentaryCategoriesStatus');
                    if (docStatusEl) docStatusEl.textContent = '';
                    updateCategoryCountBadge(view, 'documentary');
                    populateFolderCheckboxes(view, 'documentary', vodCats);
                }
            } catch (e) { /* ignore parse errors */ }
        }
        if (!vodLoaded) {
            clearFolderCardCategories(view, 'movie');
            clearFolderCardCategories(view, 'documentary');
            var vodListEl = view.querySelector('.vodCategoriesList');
            if (vodListEl && !vodListEl.innerHTML.trim()) {
                vodListEl.innerHTML = '<div style="opacity:0.5;">Click "Refresh Categories" to load.</div>';
            }
            var docListEl = view.querySelector('.documentaryCategoriesList');
            if (docListEl && !docListEl.innerHTML.trim()) {
                docListEl.innerHTML = '<div style="opacity:0.5;">Click "Refresh Categories" to load.</div>';
            }
        }

        // Series categories
        var seriesLoaded = false;
        if (config.CachedSeriesCategories) {
            try {
                var seriesCats = JSON.parse(config.CachedSeriesCategories);
                if (seriesCats && seriesCats.length > 0) {
                    seriesLoaded = true;
                    instance.loadedSeriesCategories = seriesCats;
                    renderCategoryList(view, '.seriesCategoriesList', seriesCats, 'seriesCategoryCheckbox', instance.selectedSeriesCategoryIds);
                    view.querySelector('.btnSelectAllSeriesCategories').disabled = false;
                    view.querySelector('.btnDeselectAllSeriesCategories').disabled = false;
                    var statusEl = view.querySelector('.seriesCategoriesStatus');
                    if (statusEl) statusEl.textContent = '';
                    updateCategoryCountBadge(view, 'series');

                    populateFolderCheckboxes(view, 'series', seriesCats);
                    instance.loadedDocuSeriesCategories = seriesCats;
                    renderCategoryList(view, '.docuSeriesCategoriesList', seriesCats, 'docuSeriesCategoryCheckbox', instance.selectedDocuSeriesCategoryIds);
                    view.querySelector('.btnSelectAllDocuSeriesCategories').disabled = false;
                    view.querySelector('.btnDeselectAllDocuSeriesCategories').disabled = false;
                    var docuStatusEl = view.querySelector('.docuSeriesCategoriesStatus');
                    if (docuStatusEl) docuStatusEl.textContent = '';
                    updateCategoryCountBadge(view, 'docuSeries');
                    populateFolderCheckboxes(view, 'docuSeries', seriesCats);
                }
            } catch (e) { /* ignore parse errors */ }
        }
        if (!seriesLoaded) {
            clearFolderCardCategories(view, 'series');
            clearFolderCardCategories(view, 'docuSeries');
            var seriesListEl = view.querySelector('.seriesCategoriesList');
            if (seriesListEl && !seriesListEl.innerHTML.trim()) {
                seriesListEl.innerHTML = '<div style="opacity:0.5;">Click "Refresh Categories" to load.</div>';
            }
            var docuListEl = view.querySelector('.docuSeriesCategoriesList');
            if (docuListEl && !docuListEl.innerHTML.trim()) {
                docuListEl.innerHTML = '<div style="opacity:0.5;">Click "Refresh Categories" to load.</div>';
            }
        }

        // Live TV categories
        if (config.CachedLiveCategories) {
            try {
                var liveCats = JSON.parse(config.CachedLiveCategories);
                if (liveCats && liveCats.length > 0) {
                    instance.loadedCategories = liveCats;
                    renderCategoryList(view, '.categoriesList', liveCats, 'categoryCheckbox', instance.selectedCategoryIds);
                    view.querySelector('.btnSelectAllCategories').disabled = false;
                    view.querySelector('.btnDeselectAllCategories').disabled = false;
                    updateCategoryCountBadge(view, 'live');

                }
            } catch (e) { /* ignore parse errors */ }
        }
    }

    function renderCategoryList(view, listSelector, categories, checkboxClass, selectedIds) {
        var listEl = view.querySelector(listSelector);
        if (!listEl) return;
        var html = '';
        for (var i = 0; i < categories.length; i++) {
            var cat = categories[i];
            var checked = selectedIds.indexOf(cat.CategoryId) >= 0 ? ' checked' : '';
            html += '<div class="checkboxContainer" style="margin:0.15em 0;">';
            html += '<label style="display:flex; align-items:center; cursor:pointer;">';
            html += '<input type="checkbox" class="' + checkboxClass + '" data-category-id="' + cat.CategoryId + '"' + checked + ' style="margin-right:0.5em;" />';
            html += '<span>' + escapeHtml(cat.CategoryName) + '</span>';
            html += '</label>';
            html += '</div>';
        }
        listEl.innerHTML = html;
    }

    // ---- Live TV Categories ----

    function loadCategories(instance) {
        var view = instance.view;
        var listEl = view.querySelector('.categoriesList');
        var loadingEl = view.querySelector('.categoriesLoading');

        loadingEl.style.display = 'block';
        listEl.innerHTML = '';

        var apiUrl = ApiClient.getUrl('XC2EMBY/Categories/Live');

        ApiClient.getJSON(apiUrl).then(function (categories) {
            loadingEl.style.display = 'none';
            instance.loadedCategories = categories;

            if (!categories || categories.length === 0) {
                listEl.innerHTML = '<div style="opacity:0.5;">No categories found. Check your Xtream connection settings.</div>';
                return;
            }

            var html = '';
            for (var i = 0; i < categories.length; i++) {
                var cat = categories[i];
                var checked = instance.selectedCategoryIds.indexOf(cat.CategoryId) >= 0 ? ' checked' : '';
                html += '<div class="checkboxContainer" style="margin:0.15em 0;">';
                html += '<label style="display:flex; align-items:center; cursor:pointer;">';
                html += '<input type="checkbox" class="categoryCheckbox" data-category-id="' + cat.CategoryId + '"' + checked + ' style="margin-right:0.5em;" />';
                html += '<span>' + escapeHtml(cat.CategoryName) + '</span>';
                html += '</label>';
                html += '</div>';
            }
            listEl.innerHTML = html;

            view.querySelector('.btnSelectAllCategories').disabled = false;
            view.querySelector('.btnDeselectAllCategories').disabled = false;
            updateCategoryCountBadge(view, 'live');
        }).catch(function () {
            loadingEl.style.display = 'none';
            listEl.innerHTML = '<div style="color:#cc0000;">Failed to load categories. Save your connection settings first, then try again.</div>';
        });
    }

    function toggleAllCategories(view, checked) {
        var checkboxes = view.querySelectorAll('.categoryCheckbox');
        for (var i = 0; i < checkboxes.length; i++) {
            checkboxes[i].checked = checked;
        }
        updateCategoryCountBadge(view, 'live');
    }

    function getSelectedCategoryIds(instance) {
        var view = instance.view;
        var checkboxes = view.querySelectorAll('.categoryCheckbox');
        var ids = [];
        for (var i = 0; i < checkboxes.length; i++) {
            if (checkboxes[i].checked) {
                ids.push(parseInt(checkboxes[i].getAttribute('data-category-id'), 10));
            }
        }
        if (checkboxes.length === 0) {
            return instance.selectedCategoryIds;
        }
        return ids;
    }

    // ---- VOD Categories (single mode) ----

    function loadVodCategories(instance) {
        var view = instance.view;
        var listEl = view.querySelector('.vodCategoriesList');
        var loadingEl = view.querySelector('.vodCategoriesLoading');
        var statusEl = view.querySelector('.vodCategoriesStatus');

        loadingEl.style.display = 'block';
        listEl.innerHTML = '';

        var apiUrl = ApiClient.getUrl('XC2EMBY/Categories/Vod');

        ApiClient.getJSON(apiUrl).then(function (categories) {
            loadingEl.style.display = 'none';
            instance.loadedVodCategories = categories;

            if (!categories || categories.length === 0) {
                listEl.innerHTML = '<div style="opacity:0.5;">No VOD categories found. Check your Xtream connection settings.</div>';
                return;
            }

            if (statusEl) statusEl.textContent = '';

            var html = '';
            for (var i = 0; i < categories.length; i++) {
                var cat = categories[i];
                var checked = instance.selectedVodCategoryIds.indexOf(cat.CategoryId) >= 0 ? ' checked' : '';
                html += '<div class="checkboxContainer" style="margin:0.15em 0;">';
                html += '<label style="display:flex; align-items:center; cursor:pointer;">';
                html += '<input type="checkbox" class="vodCategoryCheckbox" data-category-id="' + cat.CategoryId + '"' + checked + ' style="margin-right:0.5em;" />';
                html += '<span>' + escapeHtml(cat.CategoryName) + '</span>';
                html += '</label>';
                html += '</div>';
            }
            listEl.innerHTML = html;

            view.querySelector('.btnSelectAllVodCategories').disabled = false;
            view.querySelector('.btnDeselectAllVodCategories').disabled = false;
            updateCategoryCountBadge(view, 'vod');
        }).catch(function () {
            loadingEl.style.display = 'none';
            listEl.innerHTML = '<div style="color:#cc0000;">Failed to load VOD categories. Save your connection settings first, then try again.</div>';
        });
    }

    function toggleAllVodCategories(view, checked) {
        var checkboxes = view.querySelectorAll('.vodCategoryCheckbox');
        for (var i = 0; i < checkboxes.length; i++) {
            checkboxes[i].checked = checked;
        }
        updateCategoryCountBadge(view, 'vod');
    }

    // ---- VOD Categories (multi/folder mode) ----

    function loadVodCategoriesMulti(instance) {
        var view = instance.view;
        var statusEl = view.querySelector('.vodCategoriesMultiStatus');
        statusEl.textContent = 'Loading...';
        statusEl.style.opacity = '0.5';

        var apiUrl = ApiClient.getUrl('XC2EMBY/Categories/Vod');

        ApiClient.getJSON(apiUrl).then(function (categories) {
            instance.loadedVodCategories = categories || [];

            if (!categories || categories.length === 0) {
                statusEl.textContent = 'No VOD categories found.';
                statusEl.style.color = '#cc0000'; statusEl.style.opacity = '1';
                clearFolderCardCategories(view, 'movie');
                return;
            }

            statusEl.textContent = 'Loaded ' + categories.length + ' categories';
            statusEl.style.color = accentColor; statusEl.style.opacity = '1';
            populateFolderCheckboxes(view, 'movie', categories);
        }).catch(function () {
            statusEl.textContent = 'Failed to load categories. Save connection settings first.';
            statusEl.style.color = '#cc0000'; statusEl.style.opacity = '1';
        });
    }

    function getSelectedVodCategoryIds(instance) {
        var view = instance.view;
        var mode = view.querySelector('.selMovieFolderMode').value;

        if (mode === 'custom') {
            // Union of all checked IDs across all folder cards
            var allCheckboxes = view.querySelectorAll('.movieFoldersList .folderCategoryCheckbox');
            if (allCheckboxes.length === 0) {
                return instance.selectedVodCategoryIds;
            }
            var ids = [];
            var seen = {};
            for (var i = 0; i < allCheckboxes.length; i++) {
                if (allCheckboxes[i].checked) {
                    var id = parseInt(allCheckboxes[i].getAttribute('data-category-id'), 10);
                    if (!seen[id]) {
                        ids.push(id);
                        seen[id] = true;
                    }
                }
            }
            return ids;
        }

        // Single mode: flat checkboxes
        var checkboxes = view.querySelectorAll('.vodCategoryCheckbox');
        var ids = [];
        for (var i = 0; i < checkboxes.length; i++) {
            if (checkboxes[i].checked) {
                ids.push(parseInt(checkboxes[i].getAttribute('data-category-id'), 10));
            }
        }
        if (checkboxes.length === 0) {
            return instance.selectedVodCategoryIds;
        }
        return ids;
    }

    // ---- Documentary Categories ----

    function loadDocumentaryCategories(instance) {
        var view = instance.view;
        loadVodLikeCategories(instance, {
            list: '.documentaryCategoriesList',
            loading: '.documentaryCategoriesLoading',
            status: '.documentaryCategoriesStatus',
            checkboxClass: 'documentaryCategoryCheckbox',
            selectedIds: instance.selectedDocumentaryCategoryIds,
            selectAll: '.btnSelectAllDocumentaryCategories',
            deselectAll: '.btnDeselectAllDocumentaryCategories',
            countType: 'documentary',
            folderType: 'documentary',
            loadedSetter: function (categories) { instance.loadedDocumentaryCategories = categories; },
            emptyMessage: 'No documentary categories found. Check your Xtream connection settings.',
            failMessage: 'Failed to load documentary categories. Save your connection settings first, then try again.'
        });
    }

    function loadDocumentaryCategoriesMulti(instance) {
        loadVodLikeCategoriesMulti(instance, {
            status: '.documentaryCategoriesMultiStatus',
            folderType: 'documentary',
            loadedSetter: function (categories) { instance.loadedDocumentaryCategories = categories || []; },
            emptyMessage: 'No documentary categories found.'
        });
    }

    function toggleAllDocumentaryCategories(view, checked) {
        var checkboxes = view.querySelectorAll('.documentaryCategoryCheckbox');
        for (var i = 0; i < checkboxes.length; i++) {
            checkboxes[i].checked = checked;
        }
        updateCategoryCountBadge(view, 'documentary');
    }

    function getSelectedDocumentaryCategoryIds(instance) {
        return getSelectedVodLikeCategoryIds(instance, '.selDocumentaryFolderMode', '.documentaryFoldersList', '.documentaryCategoryCheckbox', instance.selectedDocumentaryCategoryIds);
    }

    function loadVodLikeCategories(instance, options) {
        var view = instance.view;
        var listEl = view.querySelector(options.list);
        var loadingEl = view.querySelector(options.loading);
        var statusEl = view.querySelector(options.status);

        loadingEl.style.display = 'block';
        listEl.innerHTML = '';

        ApiClient.getJSON(ApiClient.getUrl('XC2EMBY/Categories/Vod')).then(function (categories) {
            loadingEl.style.display = 'none';
            options.loadedSetter(categories);

            if (!categories || categories.length === 0) {
                listEl.innerHTML = '<div style="opacity:0.5;">' + options.emptyMessage + '</div>';
                return;
            }

            if (statusEl) statusEl.textContent = '';
            renderCategoryList(view, options.list, categories, options.checkboxClass, options.selectedIds);
            view.querySelector(options.selectAll).disabled = false;
            view.querySelector(options.deselectAll).disabled = false;
            updateCategoryCountBadge(view, options.countType);
            populateFolderCheckboxes(view, options.folderType, categories);
        }).catch(function () {
            loadingEl.style.display = 'none';
            listEl.innerHTML = '<div style="color:#cc0000;">' + options.failMessage + '</div>';
        });
    }

    function loadVodLikeCategoriesMulti(instance, options) {
        var view = instance.view;
        var statusEl = view.querySelector(options.status);
        statusEl.textContent = 'Loading...';
        statusEl.style.opacity = '0.5';

        ApiClient.getJSON(ApiClient.getUrl('XC2EMBY/Categories/Vod')).then(function (categories) {
            options.loadedSetter(categories);

            if (!categories || categories.length === 0) {
                statusEl.textContent = options.emptyMessage;
                statusEl.style.color = '#cc0000'; statusEl.style.opacity = '1';
                clearFolderCardCategories(view, options.folderType);
                return;
            }

            statusEl.textContent = 'Loaded ' + categories.length + ' categories';
            statusEl.style.color = accentColor; statusEl.style.opacity = '1';
            populateFolderCheckboxes(view, options.folderType, categories);
        }).catch(function () {
            statusEl.textContent = 'Failed to load categories. Save connection settings first.';
            statusEl.style.color = '#cc0000'; statusEl.style.opacity = '1';
        });
    }

    function getSelectedVodLikeCategoryIds(instance, modeSelector, folderListSelector, checkboxSelector, fallbackIds) {
        var view = instance.view;
        var mode = view.querySelector(modeSelector).value;

        if (mode === 'custom') {
            var allCheckboxes = view.querySelectorAll(folderListSelector + ' .folderCategoryCheckbox');
            if (allCheckboxes.length === 0) {
                return fallbackIds;
            }
            var ids = [];
            var seen = {};
            for (var i = 0; i < allCheckboxes.length; i++) {
                if (allCheckboxes[i].checked) {
                    var id = parseInt(allCheckboxes[i].getAttribute('data-category-id'), 10);
                    if (!seen[id]) {
                        ids.push(id);
                        seen[id] = true;
                    }
                }
            }
            return ids;
        }

        var checkboxes = view.querySelectorAll(checkboxSelector);
        var selected = [];
        for (var j = 0; j < checkboxes.length; j++) {
            if (checkboxes[j].checked) {
                selected.push(parseInt(checkboxes[j].getAttribute('data-category-id'), 10));
            }
        }
        return checkboxes.length === 0 ? fallbackIds : selected;
    }

    // ---- Series Categories (single mode) ----

    function loadSeriesCategories(instance) {
        var view = instance.view;
        var listEl = view.querySelector('.seriesCategoriesList');
        var loadingEl = view.querySelector('.seriesCategoriesLoading');
        var statusEl = view.querySelector('.seriesCategoriesStatus');

        loadingEl.style.display = 'block';
        listEl.innerHTML = '';

        var apiUrl = ApiClient.getUrl('XC2EMBY/Categories/Series');

        ApiClient.getJSON(apiUrl).then(function (categories) {
            loadingEl.style.display = 'none';
            instance.loadedSeriesCategories = categories;

            if (!categories || categories.length === 0) {
                listEl.innerHTML = '<div style="opacity:0.5;">No series categories found. Check your Xtream connection settings.</div>';
                return;
            }

            if (statusEl) statusEl.textContent = '';

            var html = '';
            for (var i = 0; i < categories.length; i++) {
                var cat = categories[i];
                var checked = instance.selectedSeriesCategoryIds.indexOf(cat.CategoryId) >= 0 ? ' checked' : '';
                html += '<div class="checkboxContainer" style="margin:0.15em 0;">';
                html += '<label style="display:flex; align-items:center; cursor:pointer;">';
                html += '<input type="checkbox" class="seriesCategoryCheckbox" data-category-id="' + cat.CategoryId + '"' + checked + ' style="margin-right:0.5em;" />';
                html += '<span>' + escapeHtml(cat.CategoryName) + '</span>';
                html += '</label>';
                html += '</div>';
            }
            listEl.innerHTML = html;

            view.querySelector('.btnSelectAllSeriesCategories').disabled = false;
            view.querySelector('.btnDeselectAllSeriesCategories').disabled = false;
            updateCategoryCountBadge(view, 'series');
        }).catch(function () {
            loadingEl.style.display = 'none';
            listEl.innerHTML = '<div style="color:#cc0000;">Failed to load series categories. Save your connection settings first, then try again.</div>';
        });
    }

    function toggleAllSeriesCategories(view, checked) {
        var checkboxes = view.querySelectorAll('.seriesCategoryCheckbox');
        for (var i = 0; i < checkboxes.length; i++) {
            checkboxes[i].checked = checked;
        }
        updateCategoryCountBadge(view, 'series');
    }

    // ---- Series Categories (multi/folder mode) ----

    function loadSeriesCategoriesMulti(instance) {
        var view = instance.view;
        var statusEl = view.querySelector('.seriesCategoriesMultiStatus');
        statusEl.textContent = 'Loading...';
        statusEl.style.opacity = '0.5';

        var apiUrl = ApiClient.getUrl('XC2EMBY/Categories/Series');

        ApiClient.getJSON(apiUrl).then(function (categories) {
            instance.loadedSeriesCategories = categories || [];

            if (!categories || categories.length === 0) {
                statusEl.textContent = 'No series categories found.';
                statusEl.style.color = '#cc0000'; statusEl.style.opacity = '1';
                clearFolderCardCategories(view, 'series');
                return;
            }

            statusEl.textContent = 'Loaded ' + categories.length + ' categories';
            statusEl.style.color = accentColor; statusEl.style.opacity = '1';
            populateFolderCheckboxes(view, 'series', categories);
        }).catch(function () {
            statusEl.textContent = 'Failed to load categories. Save connection settings first.';
            statusEl.style.color = '#cc0000'; statusEl.style.opacity = '1';
        });
    }

    function getSelectedSeriesCategoryIds(instance) {
        var view = instance.view;
        var mode = view.querySelector('.selSeriesFolderMode').value;

        if (mode === 'custom') {
            var allCheckboxes = view.querySelectorAll('.seriesFoldersList .folderCategoryCheckbox');
            if (allCheckboxes.length === 0) {
                return instance.selectedSeriesCategoryIds;
            }
            var ids = [];
            var seen = {};
            for (var i = 0; i < allCheckboxes.length; i++) {
                if (allCheckboxes[i].checked) {
                    var id = parseInt(allCheckboxes[i].getAttribute('data-category-id'), 10);
                    if (!seen[id]) {
                        ids.push(id);
                        seen[id] = true;
                    }
                }
            }
            return ids;
        }

        var checkboxes = view.querySelectorAll('.seriesCategoryCheckbox');
        var ids = [];
        for (var i = 0; i < checkboxes.length; i++) {
            if (checkboxes[i].checked) {
                ids.push(parseInt(checkboxes[i].getAttribute('data-category-id'), 10));
            }
        }
        if (checkboxes.length === 0) {
            return instance.selectedSeriesCategoryIds;
        }
        return ids;
    }

    // ---- Docu Series Categories ----

    function loadDocuSeriesCategories(instance) {
        loadSeriesLikeCategories(instance, {
            list: '.docuSeriesCategoriesList',
            loading: '.docuSeriesCategoriesLoading',
            status: '.docuSeriesCategoriesStatus',
            checkboxClass: 'docuSeriesCategoryCheckbox',
            selectedIds: instance.selectedDocuSeriesCategoryIds,
            selectAll: '.btnSelectAllDocuSeriesCategories',
            deselectAll: '.btnDeselectAllDocuSeriesCategories',
            countType: 'docuSeries',
            folderType: 'docuSeries',
            loadedSetter: function (categories) { instance.loadedDocuSeriesCategories = categories; },
            emptyMessage: 'No docu series categories found. Check your Xtream connection settings.',
            failMessage: 'Failed to load docu series categories. Save your connection settings first, then try again.'
        });
    }

    function loadDocuSeriesCategoriesMulti(instance) {
        loadSeriesLikeCategoriesMulti(instance, {
            status: '.docuSeriesCategoriesMultiStatus',
            folderType: 'docuSeries',
            loadedSetter: function (categories) { instance.loadedDocuSeriesCategories = categories || []; },
            emptyMessage: 'No docu series categories found.'
        });
    }

    function toggleAllDocuSeriesCategories(view, checked) {
        var checkboxes = view.querySelectorAll('.docuSeriesCategoryCheckbox');
        for (var i = 0; i < checkboxes.length; i++) {
            checkboxes[i].checked = checked;
        }
        updateCategoryCountBadge(view, 'docuSeries');
    }

    function getSelectedDocuSeriesCategoryIds(instance) {
        return getSelectedSeriesLikeCategoryIds(instance, '.selDocuSeriesFolderMode', '.docuSeriesFoldersList', '.docuSeriesCategoryCheckbox', instance.selectedDocuSeriesCategoryIds);
    }

    function loadSeriesLikeCategories(instance, options) {
        var view = instance.view;
        var listEl = view.querySelector(options.list);
        var loadingEl = view.querySelector(options.loading);
        var statusEl = view.querySelector(options.status);

        loadingEl.style.display = 'block';
        listEl.innerHTML = '';

        ApiClient.getJSON(ApiClient.getUrl('XC2EMBY/Categories/Series')).then(function (categories) {
            loadingEl.style.display = 'none';
            options.loadedSetter(categories);

            if (!categories || categories.length === 0) {
                listEl.innerHTML = '<div style="opacity:0.5;">' + options.emptyMessage + '</div>';
                return;
            }

            if (statusEl) statusEl.textContent = '';
            renderCategoryList(view, options.list, categories, options.checkboxClass, options.selectedIds);
            view.querySelector(options.selectAll).disabled = false;
            view.querySelector(options.deselectAll).disabled = false;
            updateCategoryCountBadge(view, options.countType);
            populateFolderCheckboxes(view, options.folderType, categories);
        }).catch(function () {
            loadingEl.style.display = 'none';
            listEl.innerHTML = '<div style="color:#cc0000;">' + options.failMessage + '</div>';
        });
    }

    function loadSeriesLikeCategoriesMulti(instance, options) {
        var view = instance.view;
        var statusEl = view.querySelector(options.status);
        statusEl.textContent = 'Loading...';
        statusEl.style.opacity = '0.5';

        ApiClient.getJSON(ApiClient.getUrl('XC2EMBY/Categories/Series')).then(function (categories) {
            options.loadedSetter(categories);

            if (!categories || categories.length === 0) {
                statusEl.textContent = options.emptyMessage;
                statusEl.style.color = '#cc0000'; statusEl.style.opacity = '1';
                clearFolderCardCategories(view, options.folderType);
                return;
            }

            statusEl.textContent = 'Loaded ' + categories.length + ' categories';
            statusEl.style.color = accentColor; statusEl.style.opacity = '1';
            populateFolderCheckboxes(view, options.folderType, categories);
        }).catch(function () {
            statusEl.textContent = 'Failed to load categories. Save connection settings first.';
            statusEl.style.color = '#cc0000'; statusEl.style.opacity = '1';
        });
    }

    function getSelectedSeriesLikeCategoryIds(instance, modeSelector, folderListSelector, checkboxSelector, fallbackIds) {
        return getSelectedVodLikeCategoryIds(instance, modeSelector, folderListSelector, checkboxSelector, fallbackIds);
    }

    // ---- Sync operations ----

    function renderProgressBar(resultEl, progress) {
        var total = progress.Total || 0;
        var completed = progress.Completed || 0;
        var skipped = progress.Skipped || 0;
        var failed = progress.Failed || 0;
        var phase = progress.Phase || 'Working';
        var pct = total > 0 ? Math.round((completed / total) * 100) : 0;

        resultEl.innerHTML =
            '<div style="margin:0.5em 0;">' +
                '<div style="background:rgba(128,128,128,0.2); border-radius:4px; height:20px; overflow:hidden;">' +
                    '<div style="background:' + accentColor + '; height:100%; width:' + pct + '%; transition:width 0.3s ease; border-radius:4px;"></div>' +
                '</div>' +
                '<div style="opacity:0.7; margin-top:0.4em; font-size:0.9em;">' +
                    escapeHtml(phase) + ' \u2014 ' + completed + ' / ' + total +
                    ' (' + skipped + ' skipped, ' + failed + ' failed) \u2014 ' + pct + '%' +
                '</div>' +
            '</div>';
    }

    function renderSyncResult(resultEl, result) {
        var total = result.Total || 0;
        var completed = result.Completed || total;
        var progress = {
            Phase: result.Message || 'Complete',
            Total: total,
            Completed: completed,
            Skipped: result.Skipped || 0,
            Failed: result.Failed || 0
        };
        renderProgressBar(resultEl, progress);

        var cls = result.Success ? 'success' : 'error';
        var icon = result.Success ? '\u2713' : '\u2717';
        var msg = result.Success
            ? result.Message + ' (Total: ' + total + ', Skipped: ' + (result.Skipped || 0) + ', Failed: ' + (result.Failed || 0) + ')'
            : result.Message;
        resultEl.innerHTML += '<span class="result-pill ' + cls + '">' + icon + '  ' + escapeHtml(msg) + '</span>';
    }

    function fetchAndRenderSyncProgress(view, type) {
        var resultMap = {
            Movies: '.syncMoviesResult',
            Documentaries: '.syncDocumentariesResult',
            Series: '.syncSeriesResult',
            DocuSeries: '.syncDocuSeriesResult'
        };
        var progressKey = type === 'Documentaries' ? 'Movies' : (type === 'DocuSeries' ? 'Series' : type);
        var resultEl = view.querySelector(resultMap[type]);
        return ApiClient.getJSON(ApiClient.getUrl('XC2EMBY/Sync/Status')).then(function (status) {
            var progress = status[progressKey];
            if (!progress) return;
            if (progress.IsRunning || progress.Total > 0) {
                renderProgressBar(resultEl, progress);
            }
        });
    }

    function pollSyncProgress(view, type) {
        var resultMap = {
            Movies: '.syncMoviesResult',
            Documentaries: '.syncDocumentariesResult',
            Series: '.syncSeriesResult',
            DocuSeries: '.syncDocuSeriesResult'
        };
        var resultClass = resultMap[type];
        var resultEl = view.querySelector(resultClass);
        renderProgressBar(resultEl, { Phase: 'Starting sync', Total: 0, Completed: 0, Skipped: 0, Failed: 0 });

        fetchAndRenderSyncProgress(view, type).catch(function () { });

        var intervalId = setInterval(function () {
            fetchAndRenderSyncProgress(view, type).catch(function () {
                // Ignore poll errors; the POST completion will handle cleanup
            });
        }, 150);

        return intervalId;
    }

    function syncMovies(view) {
        var resultEl = view.querySelector('.syncMoviesResult');
        var btn = view.querySelector('.btnSyncMovies');
        btn.disabled = true;
        resultEl.innerHTML = '<span style="opacity:0.5;">Starting movie sync...</span>';

        var pollId = pollSyncProgress(view, 'Movies');

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('XC2EMBY/Sync/Movies'),
            dataType: 'json'
        }).then(function (result) {
            clearInterval(pollId);
            btn.disabled = false;
            renderSyncResult(resultEl, result);
        }).catch(function () {
            clearInterval(pollId);
            btn.disabled = false;
            setPillResult(resultEl, false, 'Movie sync request failed. Check server logs for details.');
        });
    }

    function syncDocumentaries(view) {
        syncContent(view, {
            result: '.syncDocumentariesResult',
            button: '.btnSyncDocumentaries',
            progressType: 'Documentaries',
            url: 'XC2EMBY/Sync/Documentaries',
            starting: 'Starting documentary sync...',
            fail: 'Documentary sync request failed. Check server logs for details.'
        });
    }

    function syncSeries(view) {
        var resultEl = view.querySelector('.syncSeriesResult');
        var btn = view.querySelector('.btnSyncSeries');
        btn.disabled = true;
        resultEl.innerHTML = '<span style="opacity:0.5;">Starting series sync...</span>';

        var pollId = pollSyncProgress(view, 'Series');

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('XC2EMBY/Sync/Series'),
            dataType: 'json'
        }).then(function (result) {
            clearInterval(pollId);
            btn.disabled = false;
            renderSyncResult(resultEl, result);
        }).catch(function () {
            clearInterval(pollId);
            btn.disabled = false;
            setPillResult(resultEl, false, 'Series sync request failed. Check server logs for details.');
        });
    }

    function syncDocuSeries(view) {
        syncContent(view, {
            result: '.syncDocuSeriesResult',
            button: '.btnSyncDocuSeries',
            progressType: 'DocuSeries',
            url: 'XC2EMBY/Sync/DocuSeries',
            starting: 'Starting docu series sync...',
            fail: 'Docu Series sync request failed. Check server logs for details.'
        });
    }

    function syncContent(view, options) {
        var resultEl = view.querySelector(options.result);
        var btn = view.querySelector(options.button);
        btn.disabled = true;
        resultEl.innerHTML = '<span style="opacity:0.5;">' + options.starting + '</span>';

        var pollId = pollSyncProgress(view, options.progressType);
        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl(options.url),
            dataType: 'json'
        }).then(function (result) {
            clearInterval(pollId);
            btn.disabled = false;
            renderSyncResult(resultEl, result);
        }).catch(function () {
            clearInterval(pollId);
            btn.disabled = false;
            setPillResult(resultEl, false, options.fail);
        });
    }

    function stopSync(view, resultSelector) {
        var resultEl = view.querySelector(resultSelector);
        resultEl.innerHTML = '<span style="opacity:0.5;">Stopping sync...</span>';

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('XC2EMBY/Sync/Stop'),
            dataType: 'json'
        }).then(function (result) {
            setPillResult(resultEl, !!result.Success, result.Message || 'Stop requested.');
        }).catch(function () {
            setPillResult(resultEl, false, 'Stop request failed. Check server logs for details.');
        });
    }

    function deleteContent(view, type) {
        var map = {
            Movies: { label: 'movies', result: '.deleteMoviesResult', button: '.btnDeleteMovies', url: 'Movies' },
            Documentaries: { label: 'documentaries', result: '.deleteDocumentariesResult', button: '.btnDeleteDocumentaries', url: 'Documentaries' },
            Series: { label: 'TV shows', result: '.deleteSeriesResult', button: '.btnDeleteSeries', url: 'Series' },
            DocuSeries: { label: 'docu series', result: '.deleteDocuSeriesResult', button: '.btnDeleteDocuSeries', url: 'DocuSeries' }
        };
        var entry = map[type];
        var label = entry.label;
        var resultClass = entry.result;
        var btnClass = entry.button;
        var resultEl = view.querySelector(resultClass);
        var btn = view.querySelector(btnClass);

        // Inline confirm instead of window.confirm
        resultEl.innerHTML =
            '<div style="display:flex; gap:0.5em; align-items:center; flex-wrap:wrap; margin-top:0.3em;">' +
            '<span style="font-size:0.9em; opacity:0.7;">Delete ALL ' + label + '? This cannot be undone.</span>' +
            '<button type="button" class="deleteConfirmYes" style="background:#c0392b; color:white; border:none; border-radius:4px; padding:0.3em 0.8em; font-size:0.85em; cursor:pointer; font-weight:600;">Yes, delete all</button>' +
            '<button type="button" class="deleteConfirmNo button-secondary" style="font-size:0.85em; padding:0.3em 0.8em; border:1px solid rgba(128,128,128,0.3); border-radius:4px; background:transparent; color:inherit; cursor:pointer;">Cancel</button>' +
            '</div>';

        resultEl.querySelector('.deleteConfirmNo').addEventListener('click', function () {
            resultEl.innerHTML = '';
        });

        resultEl.querySelector('.deleteConfirmYes').addEventListener('click', function () {
            btn.disabled = true;
            resultEl.innerHTML = '<span style="opacity:0.5;">Deleting ' + label + '...</span>';

            ApiClient.ajax({
                type: 'DELETE',
                url: ApiClient.getUrl('XC2EMBY/Content/' + entry.url),
                dataType: 'json'
            }).then(function (result) {
                btn.disabled = false;
                setPillResult(resultEl, result.Success, result.Message);
            }).catch(function () {
                btn.disabled = false;
                setPillResult(resultEl, false, 'Delete request failed. Check server logs.');
            });
        });
    }

    function refreshCache(view) {
        var resultEl = view.querySelector('.refreshCacheResult');
        resultEl.innerHTML = '<span style="opacity:0.5;">Refreshing cache...</span>';

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('XC2EMBY/RefreshCache')
        }).then(function () {
            setPillResult(resultEl, true, 'Cache refreshed successfully!');
        }).catch(function () {
            setPillResult(resultEl, false, 'Failed to refresh cache.');
        });
    }

    function clearCodecCache(view) {
        var resultEl = view.querySelector('.clearCodecCacheResult');
        resultEl.innerHTML = '<span style="opacity:0.5;">Clearing codec cache...</span>';

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('XC2EMBY/ClearCodecCache')
        }).then(function () {
            setPillResult(resultEl, true, 'Codec cache cleared. Channels will be re-probed on next tune.');
        }).catch(function () {
            setPillResult(resultEl, false, 'Failed to clear codec cache.');
        });
    }

    // ---- Dashboard ----

    var dashboardPollId = null;

    function loadDashboard(view) {
        var apiUrl = ApiClient.getUrl('XC2EMBY/Dashboard');

        ApiClient.getJSON(apiUrl).then(function (data) {
            loadDashboard._retries = 0;
            renderDashboardStatus(view, data);

            // Auto-bust browser cache when plugin was updated.
            // localStorage remembers the last-seen version; if the server
            // reports a different one, pre-warm both resources and reload.
            var prevVer = localStorage.getItem('xtream-plugin-version');
            if (data.PluginVersion && prevVer && data.PluginVersion !== prevVer
                && !sessionStorage.getItem('xtream-cache-bust')) {
                sessionStorage.setItem('xtream-cache-bust', '1');
                var v = document.documentElement.getAttribute('data-appversion') || '';
                Promise.all([
                    fetch('configurationpage?name=xtreamconfig101&v=' + v, { cache: 'reload' }),
                    fetch('configurationpage?name=xtreamconfigjs101&v=' + v, { cache: 'reload' })
                ]).then(function () { location.reload(); });
                return;
            }
            if (data.PluginVersion) localStorage.setItem('xtream-plugin-version', data.PluginVersion);
            sessionStorage.removeItem('xtream-cache-bust');

            renderLibraryStats(view, data);
            renderDashboardHistory(view, data);

            if (data.IsRunning) {
                startDashboardProgressPolling(view);
            } else {
                stopDashboardProgressPolling();
                view.querySelector('.dashboardLiveProgress').style.display = 'none';
            }
        }).catch(function () {
            if ((loadDashboard._retries = (loadDashboard._retries || 0) + 1) <= 5) {
                setTimeout(function () { loadDashboard(view); }, 4000);
            }
        });

        loadFailedItems(view);
    }

    function loadFailedItems(view) {
        var card = view.querySelector('.dashboardFailedItemsCard');
        if (!card) return;

        ApiClient.getJSON(ApiClient.getUrl('XC2EMBY/Sync/FailedItems')).then(function (items) {
            if (!items || items.length === 0) {
                card.style.display = 'none';
                return;
            }
            card.style.display = '';
            var content = view.querySelector('.dashboardFailedItemsContent');
            var rows = items.map(function (item) {
                var time = item.FailedAt ? formatTimeAgo(new Date(item.FailedAt)) : '';
                return '<tr>' +
                    '<td style="padding:0.4em 0.6em; opacity:0.7;">' + (item.ItemType || '') + '</td>' +
                    '<td style="padding:0.4em 0.6em;">' + escHtml(item.Name || '') + '</td>' +
                    '<td style="padding:0.4em 0.6em; opacity:0.7; font-size:0.85em;">' + escHtml(item.ErrorMessage || '') + '</td>' +
                    '<td style="padding:0.4em 0.6em; opacity:0.6; font-size:0.85em; white-space:nowrap;">' + time + '</td>' +
                    '</tr>';
            }).join('');
            content.innerHTML = '<table style="width:100%; border-collapse:collapse; font-size:0.9em;">' +
                '<thead><tr>' +
                '<th style="text-align:left; padding:0.4em 0.6em; opacity:0.6; border-bottom:1px solid rgba(128,128,128,0.2);">Type</th>' +
                '<th style="text-align:left; padding:0.4em 0.6em; opacity:0.6; border-bottom:1px solid rgba(128,128,128,0.2);">Name</th>' +
                '<th style="text-align:left; padding:0.4em 0.6em; opacity:0.6; border-bottom:1px solid rgba(128,128,128,0.2);">Error</th>' +
                '<th style="text-align:left; padding:0.4em 0.6em; opacity:0.6; border-bottom:1px solid rgba(128,128,128,0.2);">When</th>' +
                '</tr></thead><tbody>' + rows + '</tbody></table>';
        }).catch(function () {
            card.style.display = 'none';
        });
    }

    function escHtml(str) {
        return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    function retryFailed(view) {
        var btn = view.querySelector('.btnRetryFailed');
        var result = view.querySelector('.retryFailedResult');
        if (btn) btn.disabled = true;
        if (result) result.textContent = 'Retrying...';

        ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('XC2EMBY/Sync/RetryFailed') })
            .then(function (data) {
                if (result) result.textContent = data.Message || 'Done.';
                loadFailedItems(view);
                loadDashboard(view);
                if (btn) btn.disabled = false;
            })
            .catch(function () {
                if (result) result.textContent = 'Retry request failed.';
                if (btn) btn.disabled = false;
            });
    }

    function renderDashboardStatus(view, data) {
        // Show plugin version from dashboard data (independent of update check)
        var versionEl = view.querySelector('.pluginVersion');
        if (versionEl && data.PluginVersion) {
            versionEl.textContent = 'v' + data.PluginVersion;
        }

        var container = view.querySelector('.dashboardStatusContent');
        var statsContainer = view.querySelector('.dashboardStatusStats');

        if (!data.LastSync) {
            container.innerHTML = '<span class="status-badge idle">No syncs yet</span>';
            statsContainer.style.display = 'none';
            return;
        }

        var last = data.LastSync;

        // Look for a companion scan (movies if last was series-only, or vice versa).
        // Movies always run before series in a combined sync, so it's in History[1+].
        var companion = null;
        if (data.History && data.History.length > 1) {
            for (var i = 1; i < data.History.length; i++) {
                var entry = data.History[i];
                if (last.WasSeriesSync && !last.WasMovieSync && entry.WasMovieSync) {
                    companion = entry;
                    break;
                }
                if (last.WasMovieSync && !last.WasSeriesSync && entry.WasSeriesSync) {
                    companion = entry;
                    break;
                }
            }
        }

        var overallSuccess = last.Success && (!companion || companion.Success);
        var badgeClass = overallSuccess ? 'success' : 'failed';
        var badgeText = overallSuccess ? 'Success' : 'Failed';

        var duration = Math.round((new Date(last.EndTime) - new Date(last.StartTime)) / 1000);
        var durationText = duration >= 60
            ? Math.floor(duration / 60) + 'm ' + (duration % 60) + 's'
            : duration + 's';

        var timeAgo = formatTimeAgo(new Date(last.EndTime));

        container.innerHTML =
            '<span class="status-badge ' + badgeClass + '">' + badgeText + '</span>' +
            '<span style="margin-left:0.8em; opacity:0.6; font-size:0.9em;">' + timeAgo + ' (' + durationText + ')</span>';

        var movieEntry = last.WasMovieSync ? last : (companion && companion.WasMovieSync ? companion : null);
        var seriesEntry = last.WasSeriesSync ? last : (companion && companion.WasSeriesSync ? companion : null);

        function statTile(value, label, color) {
            return '<div class="dashboard-stat"><div class="stat-value" style="color:' + (color || accentColor) + ';">' + value + '</div><div class="stat-label">' + label + '</div></div>';
        }
        function rowLabel(text) {
            return '<div style="font-size:0.75em; font-weight:600; opacity:0.45; text-transform:uppercase; letter-spacing:0.06em; margin-bottom:0.35em;">' + text + '</div>';
        }

        var mAdded = movieEntry ? (movieEntry.MoviesAdded || 0) : 0;
        var mDeleted = movieEntry ? (movieEntry.MoviesDeleted || 0) : 0;
        var sAdded = seriesEntry ? (seriesEntry.EpisodeAdded || 0) : 0;
        var sDeleted = seriesEntry ? (seriesEntry.EpisodeDeleted || 0) : 0;

        // Single shared 5-column grid so Movies and Episodes tiles are always the same width
        var statsHtml = '';
        if (movieEntry || seriesEntry) {
            statsHtml += '<div style="display:grid; grid-template-columns:repeat(5,1fr); gap:0.5em;">';

            if (movieEntry) {
                var movDiskTotal = (data.LibraryStats && data.LibraryStats.MovieCount) || 0;
                var movUpToDate = Math.max(0, movDiskTotal - mAdded);
                statsHtml += '<div style="grid-column:1/-1;">' + rowLabel('Movies') + '</div>';
                statsHtml +=
                    statTile(movDiskTotal, 'Total') +
                    statTile(movUpToDate, 'Up to date', '#aaa') +
                    statTile(mAdded > 0 ? '+' + mAdded : '0', 'Added', mAdded > 0 ? accentColor : '#aaa') +
                    statTile(mDeleted > 0 ? mDeleted : '0', 'Deleted', mDeleted > 0 ? '#e74c3c' : '#aaa') +
                    statTile(movieEntry.MoviesFailed, 'Failed', movieEntry.MoviesFailed > 0 ? '#cc0000' : accentColor);
            }

            if (seriesEntry) {
                var epDiskTotal = (data.LibraryStats && data.LibraryStats.EpisodeCount) || 0;
                var epUpToDate = Math.max(0, epDiskTotal - sAdded);
                statsHtml += '<div style="grid-column:1/-1;">' + rowLabel('Episodes') + '</div>';
                statsHtml +=
                    statTile(epDiskTotal, 'Total') +
                    statTile(epUpToDate, 'Up to date', '#aaa') +
                    statTile(sAdded > 0 ? '+' + sAdded : '0', 'Added', sAdded > 0 ? accentColor : '#aaa') +
                    statTile(sDeleted > 0 ? sDeleted : '0', 'Deleted', sDeleted > 0 ? '#e74c3c' : '#aaa') +
                    statTile(seriesEntry.EpisodeFailed, 'Failed', seriesEntry.EpisodeFailed > 0 ? '#cc0000' : accentColor);
            }

            statsHtml += '</div>';
        }

        // Expandable added-title lists (outside the shared grid)
        if (movieEntry && mAdded > 0 && movieEntry.AddedMovieTitles && movieEntry.AddedMovieTitles.length > 0) {
            statsHtml += '<details style="margin-top:0.3em; margin-bottom:0.4em; font-size:0.82em; opacity:0.65;">' +
                '<summary style="cursor:pointer; list-style:none;">Show added movie titles</summary>' +
                '<ul style="margin:0.3em 0 0 1em; padding:0;">' +
                movieEntry.AddedMovieTitles.map(function(t) { return '<li>' + escapeHtml(t) + '</li>'; }).join('') +
                (mAdded > movieEntry.AddedMovieTitles.length
                    ? '<li style="opacity:0.5;">\u2026and ' + (mAdded - movieEntry.AddedMovieTitles.length) + ' more</li>'
                    : '') +
                '</ul></details>';
        }
        if (seriesEntry && sAdded > 0 && seriesEntry.AddedSeriesTitles && seriesEntry.AddedSeriesTitles.length > 0) {
            statsHtml += '<details style="margin-top:0.3em; font-size:0.82em; opacity:0.65;">' +
                '<summary style="cursor:pointer; list-style:none;">Show added series titles</summary>' +
                '<ul style="margin:0.3em 0 0 1em; padding:0;">' +
                seriesEntry.AddedSeriesTitles.map(function(t) { return '<li>' + escapeHtml(t) + '</li>'; }).join('') +
                (sAdded > seriesEntry.AddedSeriesTitles.length
                    ? '<li style="opacity:0.5;">\u2026and ' + (sAdded - seriesEntry.AddedSeriesTitles.length) + ' more</li>'
                    : '') +
                '</ul></details>';
        }

        if (data.AutoSyncOn && data.NextSyncTime) {
            var delta = new Date(data.NextSyncTime) - new Date();
            if (delta > 0) {
                var hrs = Math.floor(delta / 3600000);
                var mins = Math.floor((delta % 3600000) / 60000);
                var nextText = hrs > 0 ? 'Next sync in ' + hrs + 'h ' + mins + 'm' : 'Next sync in ' + mins + 'm';
                statsHtml += '<div style="margin-top:0.6em; font-size:0.82em; opacity:0.5;">' + nextText + '</div>';
            }
        }

        if (statsHtml) {
            statsContainer.innerHTML = statsHtml;
            statsContainer.style.display = 'block';
        } else {
            statsContainer.style.display = 'none';
        }
    }

    function renderLibraryStats(view, data) {
        var container = view.querySelector('.dashboardLibraryContent');
        var stats = data.LibraryStats || {};

        function libTile(value, label, sub, extraStyle) {
            return '<div class="dashboard-stat"' + (extraStyle ? ' style="' + extraStyle + '"' : '') + '>' +
                '<div class="stat-value">' + value + '</div>' +
                '<div class="stat-label">' + label +
                    (sub ? '<br><span style="opacity:0.5; font-size:0.85em;">' + sub + '</span>' : '') +
                '</div>' +
            '</div>';
        }

        // Compact grid across the four library buckets plus Live TV.
        var html = '<div style="display:grid; grid-template-columns: repeat(3, 1fr); gap: 0.5em;">';
        html += libTile(stats.MovieCount || 0, 'Movies');
        html += libTile(stats.DocumentaryCount || 0, 'Documentaries');
        html += libTile(stats.LiveTvChannels || 0, 'Live TV');
        html += libTile(stats.SeriesCount || 0, 'Shows');
        html += libTile(stats.SeasonCount || 0, 'Seasons');
        html += libTile(stats.EpisodeCount || 0, 'Episodes');
        html += libTile(stats.DocuSeriesCount || 0, 'Docu Series');
        html += libTile(stats.DocuSeasonCount || 0, 'Docu Seasons');
        html += libTile(stats.DocuEpisodeCount || 0, 'Docu Episodes');
        html += '</div>';

        container.innerHTML = html;
    }

    function renderDashboardHistory(view, data) {
        var container = view.querySelector('.dashboardHistoryContent');
        var hasHistory = !!(data.History && data.History.length > 0);
        updateSyncCTAEmphasis(view, hasHistory);

        if (!hasHistory) {
            container.innerHTML = '<div style="opacity:0.5;">No sync history yet</div>';
            return;
        }

        var html = '<table class="dashboard-history-table">';
        html += '<thead><tr><th>Time</th><th>Status</th><th>Duration</th><th>Movies</th><th>Episodes</th></tr></thead>';
        html += '<tbody>';

        function historyMovieCol(e) {
            var finalTotal = (e.MoviesTotal || 0) - (e.MoviesDeleted || 0);
            return finalTotal +
                ' <span style="opacity:0.5;">(' +
                '<span style="color:' + accentColor + '; opacity:1;">+' + (e.MoviesAdded || 0) + '</span> ' +
                '<span style="color:#e74c3c; opacity:1;">-' + (e.MoviesDeleted || 0) + '</span>, ' +
                e.MoviesFailed + ' fail' +
                ')</span>';
        }
        function historySeriesCol(e) {
            return (e.EpisodeTotal || 0) +
                ' <span style="opacity:0.5;">(' +
                '<span style="color:' + accentColor + '; opacity:1;">+' + (e.EpisodeAdded || 0) + '</span> ' +
                '<span style="color:#e74c3c; opacity:1;">-' + (e.EpisodeDeleted || 0) + '</span>, ' +
                (e.EpisodeSkipped || 0) + ' skip, ' +
                (e.EpisodeFailed || 0) + ' fail' +
                ')</span>';
        }

        var dash = '<span style="opacity:0.3;">\u2014</span>';

        var i = 0;
        while (i < data.History.length) {
            var entry = data.History[i];
            var next = i + 1 < data.History.length ? data.History[i + 1] : null;

            // Pair a series-only entry with the following movie-only entry (or vice versa)
            var movieEntry = null, seriesEntry = null, consumed = 1;
            if (next && entry.WasSeriesSync && !entry.WasMovieSync && next.WasMovieSync && !next.WasSeriesSync) {
                seriesEntry = entry; movieEntry = next; consumed = 2;
            } else if (next && entry.WasMovieSync && !entry.WasSeriesSync && next.WasSeriesSync && !next.WasMovieSync) {
                movieEntry = entry; seriesEntry = next; consumed = 2;
            } else {
                movieEntry = entry.WasMovieSync ? entry : null;
                seriesEntry = entry.WasSeriesSync ? entry : null;
            }

            // Use the most recent entry for time/status; sum durations if paired
            var primary = entry;
            var success = entry.Success;
            if (consumed === 2) {
                success = (movieEntry ? movieEntry.Success : true) && (seriesEntry ? seriesEntry.Success : true);
            }
            var badgeClass = success ? 'success' : 'failed';
            var badgeText = success ? 'Success' : 'Failed';

            var dur = Math.round((new Date(primary.EndTime) - new Date(primary.StartTime)) / 1000);
            if (consumed === 2 && next) {
                dur += Math.round((new Date(next.EndTime) - new Date(next.StartTime)) / 1000);
            }
            var durationText = dur >= 60 ? Math.floor(dur / 60) + 'm ' + (dur % 60) + 's' : dur + 's';
            var timeStr = formatTimeAgo(new Date(primary.EndTime));

            var movieCol = movieEntry ? historyMovieCol(movieEntry) : dash;
            var seriesCol = seriesEntry ? historySeriesCol(seriesEntry) : dash;

            html += '<tr>';
            html += '<td>' + timeStr + '</td>';
            html += '<td><span class="status-badge ' + badgeClass + '">' + badgeText + '</span></td>';
            html += '<td>' + durationText + '</td>';
            html += '<td>' + movieCol + '</td>';
            html += '<td>' + seriesCol + '</td>';
            html += '</tr>';

            i += consumed;
        }

        html += '</tbody></table>';
        container.innerHTML = html;
    }

    function startDashboardProgressPolling(view) {
        stopDashboardProgressPolling();
        var progressCard = view.querySelector('.dashboardLiveProgress');
        progressCard.style.display = 'block';

        dashboardPollId = setInterval(function () {
            var apiUrl = ApiClient.getUrl('XC2EMBY/Sync/Status');
            ApiClient.getJSON(apiUrl).then(function (status) {
                var movieProg = status.Movies;
                var seriesProg = status.Series;
                var isRunning = (movieProg && movieProg.IsRunning) || (seriesProg && seriesProg.IsRunning);

                if (!isRunning) {
                    stopDashboardProgressPolling();
                    progressCard.style.display = 'none';
                    loadDashboard(view);
                    return;
                }

                var active = (movieProg && movieProg.IsRunning) ? movieProg : seriesProg;
                var total = active.Total || 0;
                var completed = active.Completed || 0;
                var pct = total > 0 ? Math.round((completed / total) * 100) : 0;

                view.querySelector('.dashboardProgressPhase').textContent = active.Phase || 'Working...';
                view.querySelector('.dashboardProgressBarFill').style.width = pct + '%';
                view.querySelector('.dashboardProgressDetail').textContent =
                    completed + ' / ' + total + ' (' + active.Skipped + ' skipped, ' + active.Failed + ' failed) \u2014 ' + pct + '%';
            }).catch(function () { });
        }, 500);
    }

    function stopDashboardProgressPolling() {
        if (dashboardPollId) {
            clearInterval(dashboardPollId);
            dashboardPollId = null;
        }
    }

    function dashboardSyncAll(instance) {
        var view = instance.view;
        var btn = view.querySelector('.btnDashboardSyncAll');
        var resultEl = view.querySelector('.dashboardSyncAllResult');
        btn.disabled = true;
        resultEl.innerHTML = '<span style="opacity:0.5;">Starting sync...</span>';

        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            var jobs = [];
            if (config.SyncMovies) jobs.push({ label: 'Movies', url: 'XC2EMBY/Sync/Movies' });
            if (config.SyncDocumentaries) jobs.push({ label: 'Documentaries', url: 'XC2EMBY/Sync/Documentaries' });
            if (config.SyncSeries) jobs.push({ label: 'TV Shows', url: 'XC2EMBY/Sync/Series' });
            if (config.SyncDocuSeries) jobs.push({ label: 'Docu Series', url: 'XC2EMBY/Sync/DocuSeries' });

            if (jobs.length === 0) {
                btn.disabled = false;
                setPillResult(resultEl, false, 'Nothing to sync. Enable a sync section first.');
                return;
            }

            startDashboardProgressPolling(view);

            var parts = [];
            var overallSuccess = true;
            var chain = Promise.resolve();
            jobs.forEach(function (job) {
                chain = chain.then(function () {
                    resultEl.innerHTML = '<span style="opacity:0.5;">' + escapeHtml(parts.join(' | ')) + (parts.length ? ' \u2014 ' : '') + 'Starting ' + escapeHtml(job.label) + ' sync...</span>';
                    return ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl(job.url), dataType: 'json' }).then(function (result) {
                        overallSuccess = overallSuccess && result.Success;
                        parts.push(result.Success
                            ? job.label + ': ' + result.Total + ' total, ' + result.Skipped + ' skipped, ' + result.Failed + ' failed'
                            : job.label + ' failed: ' + result.Message);
                    });
                });
            });

            chain.then(function () {
                stopDashboardProgressPolling();
                view.querySelector('.dashboardLiveProgress').style.display = 'none';
                btn.disabled = false;
                setPillResult(resultEl, overallSuccess, parts.join(' | '));
                loadDashboard(view);
            }).catch(function () {
                stopDashboardProgressPolling();
                view.querySelector('.dashboardLiveProgress').style.display = 'none';
                btn.disabled = false;
                setPillResult(resultEl, false, 'Sync request failed. Check server logs.');
                loadDashboard(view);
            });
        }).catch(function () {
            btn.disabled = false;
            setPillResult(resultEl, false, 'Failed to load config.');
        });
    }

    function formatTimeAgo(date) {
        var now = new Date();
        var diff = Math.round((now - date) / 1000);
        if (diff < 60) return 'just now';
        if (diff < 3600) return Math.floor(diff / 60) + 'm ago';
        if (diff < 86400) return Math.floor(diff / 3600) + 'h ago';
        return date.toLocaleDateString() + ' ' + date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    }

    function setupCategorySearch(view, inputSelector, listSelector) {
        var input = view.querySelector(inputSelector);
        if (!input) return;
        input.addEventListener('input', function () {
            var filter = input.value.toLowerCase();
            var items = view.querySelectorAll(listSelector + ' .checkboxContainer');
            for (var i = 0; i < items.length; i++) {
                var text = items[i].textContent.toLowerCase();
                items[i].style.display = text.indexOf(filter) >= 0 ? '' : 'none';
            }
        });
    }

    function escapeHtml(text) {
        var div = document.createElement('div');
        div.appendChild(document.createTextNode(text));
        return div.innerHTML;
    }

    // ---- UX helpers ----

    function setPillResult(el, isSuccess, message) {
        var cls = isSuccess ? 'success' : 'error';
        var icon = isSuccess ? '\u2713' : '\u2717';
        el.innerHTML = '<span class="result-pill ' + cls + '">' + icon + '  ' + escapeHtml(message) + '</span>';
    }


    function updateCategoryCountBadge(view, type) {
        var map = {
            vod:    { badge: '.vodCategoryCountBadge',    checkbox: '.vodCategoryCheckbox' },
            documentary: { badge: '.documentaryCategoryCountBadge', checkbox: '.documentaryCategoryCheckbox' },
            series: { badge: '.seriesCategoryCountBadge', checkbox: '.seriesCategoryCheckbox' },
            docuSeries: { badge: '.docuSeriesCategoryCountBadge', checkbox: '.docuSeriesCategoryCheckbox' },
            live:   { badge: '.liveCategoryCountBadge',   checkbox: '.categoryCheckbox' }
        };
        var entry = map[type];
        if (!entry) return;
        var badge = view.querySelector(entry.badge);
        if (!badge) return;
        var total    = view.querySelectorAll(entry.checkbox).length;
        var selected = view.querySelectorAll(entry.checkbox + ':checked').length;
        if (total === 0) { badge.style.display = 'none'; return; }
        badge.querySelector('.countSelected').textContent = selected;
        badge.querySelector('.countTotal').textContent    = total;
        badge.style.display = '';
        badge.classList.toggle('zero-selected', selected === 0);
    }

    function renderHealthBar(view, config) {
        var xtreamItem      = view.querySelector('.healthItemXtream');
        var syncItem        = view.querySelector('.healthItemLastSync');
        if (!xtreamItem) return;

        // Xtream dot
        var xtreamOk = !!(config.BaseUrl && config.Username);
        setHealthDot(xtreamItem, xtreamOk ? 'ok' : 'grey');
        xtreamItem.querySelector('.healthLabel').textContent = xtreamOk
            ? 'XC2EMBY: Connected (' + config.Username + ')'
            : 'XC2EMBY: Not configured';

        // Last sync dot — prefer SyncHistoryJson[0].EndTime (updated on every sync),
        // fall back to LastMovieSyncTimestamp (may be Unix epoch int or ISO string).
        var syncLabel = 'Last Sync: Never';
        var syncOk = false;
        var syncDate = null;

        // Primary: SyncHistoryJson[0].EndTime (ISO string, always current)
        try {
            var hist = config.SyncHistoryJson ? JSON.parse(config.SyncHistoryJson) : null;
            if (hist && hist.length > 0) {
                var et = new Date(hist[0].EndTime);
                if (!isNaN(et.getTime())) syncDate = et;
            }
        } catch (e) { /* ignore parse errors */ }

        // Fallback: LastMovieSyncTimestamp (numeric Unix epoch seconds or ISO string)
        if (!syncDate) {
            var lastTs = config.LastMovieSyncTimestamp;
            if (lastTs && /^\d+$/.test(String(lastTs))) {
                var epoch = parseInt(lastTs, 10);
                if (epoch > 0) syncDate = new Date(epoch * 1000);
            } else if (lastTs && String(lastTs).indexOf('0001') !== 0) {
                var parsed = new Date(lastTs);
                if (!isNaN(parsed.getTime())) syncDate = parsed;
            }
        }

        if (syncDate) {
            syncOk = true;
            syncLabel = 'Last Sync: ' + formatTimeAgo(syncDate);
        }
        setHealthDot(syncItem, syncOk ? 'ok' : 'grey');
        syncItem.querySelector('.healthLabel').textContent = syncLabel;
    }

    function setHealthDot(itemEl, status) {
        var dot = itemEl.querySelector('.healthDot');
        if (!dot) return;
        var colours = { ok: accentColor, error: '#cc0000', grey: '#888' };
        dot.style.background = colours[status] || colours.grey;
    }

    function updateDashboardEmptyState(view, config) {
        var unconfigured = view.querySelector('.dashboardEmptyStateUnconfigured');
        var noCategories = view.querySelector('.dashboardEmptyStateNoCategories');
        var grid         = view.querySelector('.dashboard-grid');
        if (!unconfigured || !grid) return;

        var isConfigured = !!(config.BaseUrl && config.Username);
        var hasContent   = !!(config.SyncMovies || config.SyncDocumentaries || config.SyncSeries || config.SyncDocuSeries || config.EnableLiveTv);

        if (!isConfigured) {
            unconfigured.style.display = '';
            noCategories.style.display = 'none';
            grid.style.display = 'none';
        } else if (!hasContent) {
            unconfigured.style.display = 'none';
            noCategories.style.display = '';
            grid.style.display = '';
        } else {
            unconfigured.style.display = 'none';
            noCategories.style.display = 'none';
            grid.style.display = '';
        }
    }

    function renderAutoSyncDashboardLine(view, config) {
        var el = view.querySelector('.dashboardAutoSyncLine');
        if (!el) return;
        if (!config.AutoSyncEnabled) {
            el.innerHTML = 'Auto-sync: Off \u00a0\u2014\u00a0<a class="lnkGoToAutoSync" href="#" style="color:inherit; text-decoration:underline;">Enable in Settings</a>';
        } else {
            var summary = '';
            var nextDate = null;
            var now = new Date();

            if (config.AutoSyncMode === 'daily') {
                var parts = (config.AutoSyncDailyTime || '03:00').split(':');
                var hour = parseInt(parts[0], 10) || 3;
                var minute = parseInt(parts[1] || '0', 10) || 0;
                nextDate = new Date(now.getFullYear(), now.getMonth(), now.getDate(), hour, minute, 0, 0);
                if (nextDate <= now) nextDate.setDate(nextDate.getDate() + 1);
                summary = 'Auto-sync: Daily at ' + (config.AutoSyncDailyTime || '03:00');
            } else {
                var interval = Math.max(1, config.AutoSyncIntervalHours || 24);
                summary = 'Auto-sync: Every ' + interval + 'h';

                try {
                    var hist = config.SyncHistoryJson ? JSON.parse(config.SyncHistoryJson) : null;
                    if (hist && hist.length > 0 && hist[0].EndTime) {
                        var parsedHistoryDate = new Date(hist[0].EndTime);
                        if (!isNaN(parsedHistoryDate.getTime())) {
                            nextDate = new Date(parsedHistoryDate.getTime() + interval * 3600000);
                        }
                    }
                } catch (e) { /* ignore parse errors */ }

                if (!nextDate) {
                    var lastTs = config.LastMovieSyncTimestamp;
                    if (lastTs) {
                        var lastDate = typeof lastTs === 'number' ? new Date(lastTs * 1000) : new Date(lastTs);
                        if (!isNaN(lastDate.getTime())) {
                            nextDate = new Date(lastDate.getTime() + interval * 3600000);
                        }
                    }
                }
            }

            if (nextDate) {
                var diff = nextDate - now;
                if (diff > 0) {
                    var h = Math.floor(diff / 3600000);
                    var m = Math.floor((diff % 3600000) / 60000);
                    summary += ' \u2014 next run in ' + (h > 0 ? h + 'h ' : '') + m + 'm';
                } else {
                    summary += ' \u2014 overdue';
                }
            }

            el.textContent = summary;
        }
        var link = el.querySelector('.lnkGoToAutoSync');
        if (link) {
            link.addEventListener('click', function (e) {
                e.preventDefault();
                switchTab(view, 'generic');
            });
        }
    }

    function updateSyncCTAEmphasis(view, hasHistory) {
        var btn  = view.querySelector('.btnDashboardSyncAll');
        var hint = view.querySelector('.syncNoCTAHint');
        if (!btn) return;
        if (!hasHistory) {
            btn.classList.add('block');
            if (hint) hint.style.display = '';
        } else {
            btn.classList.remove('block');
            if (hint) hint.style.display = 'none';
        }
    }

    function initFolderModeCards(view, type) {
        var ui = folderUi(type);
        var container = view.querySelector(ui.cards);
        var select    = view.querySelector(ui.select);
        if (!container || !select) return;

        var cards = container.querySelectorAll('.folder-mode-card');

        function activateCard(val) {
            for (var i = 0; i < cards.length; i++) {
                cards[i].classList.toggle('active', cards[i].getAttribute('data-mode') === val);
            }
        }

        for (var i = 0; i < cards.length; i++) {
            (function (card) {
                card.addEventListener('click', function () {
                    select.value = card.getAttribute('data-mode');
                    activateCard(select.value);
                    var evt;
                    if (typeof Event === 'function') {
                        evt = new Event('change', { bubbles: true });
                    } else {
                        evt = document.createEvent('Event');
                        evt.initEvent('change', true, true);
                    }
                    select.dispatchEvent(evt);
                });
            })(cards[i]);
        }

        activateCard(select.value);
    }

    function syncFolderModeCards(view, type) {
        var ui = folderUi(type);
        var container = view.querySelector(ui.cards);
        var select    = view.querySelector(ui.select);
        if (!container || !select) return;
        var val   = select.value;
        var cards = container.querySelectorAll('.folder-mode-card');
        for (var i = 0; i < cards.length; i++) {
            cards[i].classList.toggle('active', cards[i].getAttribute('data-mode') === val);
        }
    }

    return View;
});
