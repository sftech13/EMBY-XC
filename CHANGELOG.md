# Changelog

All notable changes to XC2EMBY are listed here, newest first.

---

## v1.1.108
- Centralized STRM writes across Movies, Documentaries, TV Shows, DocuSeries, and retry operations: existing URLs are normalized and compared before writing, so unchanged files retain their timestamps and do not trigger Emby filesystem activity.
- Removed timestamp/hash-only series fast paths that could leave stale URLs after a provider extension, base URL, or credential change; episode hashes remain persisted as catalog delta state.
- NFO generation and patching now runs only for newly added or changed STRM items and reports whether it actually changed a sidecar.
- Added separate changed-file tracking and queue exactly one Emby library scan after a successful content sync changes the filesystem; unchanged syncs do not request a scan.

## v1.1.107
- Double the provider-reported connection limit when deriving XC2EMBY tuner capacity, leaving room for brief channel-change overlap.
- Prevent live-stream consumer counts from becoming negative during duplicate removals, which could leave stale tuner registrations until restart.

## v1.1.106
- Fixed plugin failing to load on Emby 4.9.x after v1.1.104 switched to direct 4.10.0.17 DLL references: compiling against `MediaBrowser.Controller 4.10.0.17` with `Private=false` hard-bakes that exact version into the plugin manifest — Emby's binding redirects only satisfy old→new (4.8→4.9), not new→old (4.10→4.9), so the plugin failed at assembly load time on 4.9. Reverted csproj to the `mediabrowser.server.core 4.8.0.80` NuGet package so the compiled manifest references `4.8.x`, which is satisfied by both 4.9 and 4.10 via standard binding redirects. Changed `AddConsumer`/`RemoveConsumer` from explicit interface implementations (which required 4.10 DLLs to compile) to `public virtual` methods — virtual methods live in the vtable and the .NET 8 CLR finds them via name+signature fallback when resolving new interface slots on 4.10, while on 4.9 (where `ILiveStream` has no `AddConsumer` slot) they are harmless extra virtual methods

## v1.1.105
- Added provider health gate to all four scheduled sync tasks: if the health monitor marked the provider unreachable within the last 15 minutes, the sync is skipped with a warning instead of firing thousands of API requests against a down server
- Added orphan-cleanup guard in series sync: if more than 40% of series returned HTTP errors from the provider during a run (e.g. 429 rate-limit flood during an outage), orphan cleanup is skipped for that run — prevents the "empty writtenPaths during degraded sync → entire library flagged orphaned" scenario; cleanup resumes automatically on the next successful run

## v1.1.104
- Fixed `TypeLoadException` on Emby 4.10 (second attempt): v1.1.103 added `AddConsumer`/`RemoveConsumer` as plain public methods, but the C# compiler does not emit `.override` (MethodImpl) entries for plain public methods — it only does so for explicit or virtual interface implementations. Switched to explicit interface implementation (`void ILiveStream.AddConsumer(string id)`), which forces the required MethodImpl table entry. Also replaced the 4.8 NuGet SDK reference with direct references to the Emby 4.10.0.17 DLLs (`libs/emby410/`) so the compiler has the correct interface definition to generate against; bumped `System.Memory` to 4.6.3 to match the 4.10 DLL's dependency

## v1.1.103
- Fixed `TypeLoadException` on Emby 4.10: `ILiveStream` gained `AddConsumer(string id)` and `RemoveConsumer(string id)` in 4.10.0.x and changed `ConsumerCount` to read-only; `XtreamLiveStream` now implements both methods (backed by an interlocked counter) so the plugin loads on 4.10 while remaining compatible with 4.8/4.9

## v1.1.102
- Added orphaned-category detection for Live TV categories: when a provider renumbers a category's ID (e.g. Peacock 24 → 102), the old ID silently vanishes from future category lists while staying selected. "Refresh Categories" now diffs the previous and fresh category lists, flags any selected ID that disappeared, and — when a category with the same name reappears under a new ID — suggests it as the replacement with a one-click "Use ... instead" button on the config page
- Fixed deserializing `CachedLiveCategories` into the wrong DTO: `Category` carries Xtream-API snake_case `JsonPropertyName` attributes (`category_id`/`category_name`) that don't match the cache's plain `CategoryId`/`CategoryName` shape, silently zeroing every entry; added a dedicated `CachedCategoryEntry` DTO for the cache format

## v1.1.101
- Fixed `ProviderHealthMonitor` disposing the shared `HttpClient` — `using` wrapper removed; static shared instance must never be disposed
- Fixed `XtreamLiveStream.Dispose()` calling `_httpClient?.Dispose()` — every stream stop was destroying the shared client for all subsequent API calls
- Added `ReachabilityChanged` event to `ProviderHealthMonitor`; fires only on reachable ↔ unreachable state transitions, never on repeated same-state checks
- Added `BroadcastProviderStatusAsync` in `Plugin.cs`: sends a popup to all active Emby sessions when the provider goes down or recovers
- Added `GET /XC2EMBY/TestNotification` admin endpoint for manually triggering a test notification
- Added `TriggerEmbyGuideRefresh()`: after a cache refresh, runs Emby's native `RefreshGuide` scheduled task to fully reconcile channel additions and removals in Emby's database

## v1.1.98
- Added live provider health monitoring: `ProviderHealthMonitor` + `ProviderHealthTask` (5-minute scheduled check), startup check 15 seconds after Emby starts, live status dot in the plugin header polled every 2 minutes
- Session broadcast notifications on provider state transitions: "Service Disruption" / "Service Restored" popups sent to all active Emby sessions; fires only on reachable→unreachable and unreachable→reachable transitions
- Admin test endpoint: `GET /XC2EMBY/TestNotification?api_key=<key>` to verify notifications work
- Health status dot uses fixed semantic colors (green/red) instead of inheriting the Emby theme accent color
- Test Connection enriched with provider account status, expiry date, and max connections; Max Streams field removed from UI and auto-populated from provider on test
- Dashboard: all enabled content types always shown regardless of sync history; Last Sync block shows only change stats (Added/Skipped/Deleted/Failed); fixed series title list showing incorrect "…and N more" count
- Fixed guide refresh triggering on every settings save due to `EpgSourceMode` enum string/int mismatch
- UI: "Xtream Connection" section renamed to "Provider"; tab bar padding reduced to prevent cutoff on 7-tab layout; EPG source option renamed to "Provider (built-in)"

## v1.1.81
- Settings UI: Library Paths fields now show relative folder names when the path is under `StrmLibraryPath`, making config display cleaner

## v1.1.80
- Fixed token redaction ordering so longer tokens are matched before shorter prefixes
- Demoted migration warning log to Info level

## v1.1.79
- Sanitized log download now redacts Emby auth tokens (API keys, access tokens) from exported logs

## v1.1.78
- Fixed sanitized log export encoding: output is ASCII-only with non-ASCII characters stripped to prevent encoding errors in log viewers

## v1.1.77
- Fixed sanitized log UTF-8 encoding issue
- Fixed version number strings being false-positively redacted as credentials

## v1.1.76
- Sanitized log export now produces a human-readable format with clear section headers and line wrapping

## v1.1.75
- Removed Delete All buttons from the Danger Zone UI section to prevent accidental mass deletion

## v1.1.74
- When local media filter matches a STRM file, the duplicate STRM is now deleted from disk rather than left as an orphan

## v1.1.73
- Merged channel and EPG caches for more consistent refresh behavior
- Fixed button centering in Quick Actions
- Fixed orphan count not being recorded in commit history correctly

## v1.1.70
- Fixed skip condition that missed episodes with missing `RunTimeTicks` — those episodes were incorrectly treated as complete

## v1.1.69
- Populated `RunTimeTicks` on episode `MediaSourceInfo` to prevent Emby from marking episodes as played prematurely

## v1.1.68
- Fixed `MissingMethodException` in `PopulateMediaStreams` on Emby 4.9

## v1.1.67
- Added "Populate Episode Media Streams" scheduled task to backfill stream details (codec, resolution, audio) for existing STRM episodes without reprobing

## v1.1.66
- Fixed crash when XC API returns non-object values in episode `info`, `audio`, or `video` fields — fields are now defensively parsed

## v1.1.65
- Episode `.nfo` sidecar files now include `<streamdetails>` from the XC API (codec, resolution, audio codec, channels, language), preventing Emby from launching ffprobe probe storms against STRM episode files

## v1.1.64
- Live channels with no cached codec data now return default `H264`/`AC3` stream stubs so Emby's OSD shows something rather than nothing on first tune

## v1.1.63
- Removed confirmed dead code: `XmltvParser.Parse`, `LocalMediaFilter` series path stub, `SeasonInfo` class, `config.js escapeHtml` stub

## v1.1.62
- Removed additional confirmed dead code after audit pass

## v1.1.61
- Consolidated XMLTV fetch: a single download is now shared between the guide provider and the `/epg.xml` endpoint, eliminating redundant provider requests

## v1.1.60
- Live TV category fetch now falls back to `CachedLiveCategories` when `GetLiveCategoriesAsync` throws, preventing the category UI from showing empty on transient errors

## v1.1.59
- Fixed logo cleanup scanning 0 channels due to an incorrect channel collection being iterated

## v1.1.58
- Fixed channel logo cache not being persisted to disk after `DeleteImage` calls — logos were re-downloaded on every Emby restart

## v1.1.57
- Fixed EPG cache not being invalidated when a forced refresh was requested

## v1.1.56
- Added `RefreshLiveTvTask` scheduled task (every 4 hours): invalidates the channel cache, triggers channel rescan, and refreshes the EPG — equivalent to clicking Refresh Cache manually

## v1.1.55
- Fixed channel group tags never appearing in the Emby guide filter — `group-title` values were not being written to the M3U output

## v1.1.54
- Fixed `Refresh Cache` deadlock caused by `ValidateOptions` being called on the same thread — overrode `ValidateOptions` to break the lock

## v1.1.53
- Fixed `Channel Cache Duration` config field having an incorrect `min` attribute in the config UI

## v1.1.52
- Fixed a CAS (compare-and-swap) race condition in the channel cache refresh path
- Fixed fresh data not propagating to callers after a background cache refresh completed

## v1.1.51
- Fixed guide going blank during a cache refresh when active streams were in progress — old data is now served until the refresh completes

## v1.1.50
- Fixed Live TV showing 0 channels on cold start — added a timeout to the XMLTV fetch during initial channel cache build so a slow EPG source doesn't block channel registration

## v1.1.49
- Fixed XMLTV timezone parsing when the offset has no space before it (e.g., `+0000` directly after the time value)

## v1.1.48
- Fixed guide going black during active streams when the EPG cache expired
- Config page UX fixes: validation messages, field state management

## v1.1.47
- Addressed remaining P1/P2/P3 audit items: null guards, thread safety, resource cleanup, edge-case handling

## v1.1.46
- Mobile: tab bar is now horizontally scrollable instead of wrapping/overflowing on small screens
- Responsive layout fixes for narrow viewports

## v1.1.45
- Fixed critical regression: shared `HttpClient` instances were being disposed by callers, causing all subsequent HTTP requests to fail with `ObjectDisposedException`

## v1.1.44
- Full audit remediation pass: bug fixes, safety improvements, dead code removal, thread safety across multiple files

## v1.1.43
- Fixed dashboard progress bar not appearing for Documentaries and Docu Series sync types

## v1.1.42
- Fixed `Refresh Categories` button accidentally triggering an EPG/guide refresh

## v1.1.41
- All `XC2EMBY` API endpoints now require admin authentication (`[Authenticated(Roles = "Admin")]`)

## v1.1.40
- Auto-sync scheduled task triggers are now staggered per content type (Movies, Documentaries, TV Shows, Docu Series) to prevent concurrent runs at startup

## v1.1.39
- Orphan cleanup now shows a preview of items to be deleted before committing
- Title matching for local media filter and orphan detection is now year-aware to distinguish remakes from originals

## v1.1.38
- Last Sync dashboard stats broken down by content type: Movies, Documentaries, TV Shows, Docu Series — each shows its own Added/Skipped/Deleted/Failed counts

## v1.1.37
- Added selective STRM delete UI: delete per content type without deleting everything
- Cross-disabled duplicate categories between Movies/Docs and Series/DocuSeries
- Fixed 4K colon in channel name cleaning stripping incorrectly
- Added IMDB ID matching to local media filter

## v1.1.36
- Categories selected for Movies/Documentaries are now automatically disabled in Series/DocuSeries selectors (and vice versa) to prevent the same category syncing to two places

## v1.1.35
- Fixed channel name quality prefix cleaner not stripping when the separator is a colon (e.g., `UK:`)

## v1.1.33
- Fixed local media filter missing matches when library items use Radarr/Sonarr path-style ID tags (`[tmdbid=...]`, `[tvdbid=...]`)
- Fixed local media filter missing IMDB ID matches

## v1.1.32
- Fixed "Other Showings" in the Emby guide linking programs across unrelated channels — each program now gets a channel-scoped `ShowId`

## v1.1.31
- Fixed stream sharing: stopping one viewer's stream no longer drops other users watching the same channel

## v1.1.30
- Fixed channel rescan scope — rescan now correctly targets only the XC2EMBY tuner host
- Fixed EPG channel name fallback not applying when the XMLTV `display-name` was missing

## v1.1.29
- Refresh Cache button now triggers both a tuner channel rescan and an EPG/guide refresh (previously only did one or the other)

## v1.1.28
- Channel cache TTL is now controlled by the `M3U Cache (minutes)` setting rather than being hardcoded

## v1.1.25 – v1.1.27
- Guide data passthrough improvements: sub-title, categories, production year, content rating, icons, live/new/repeat/premiere flags, season/episode numbers
- Codec detection background probing and OSD display
- Various stability and UI fixes

## v1.1.24
- Release

## v1.1.23
- Release

## v1.1.21
- Release

## v1.1.20
- Release

## v1.1.19
- Release

## v1.1.18
- Release

## v1.1.17
- Release

## v1.1.15 – v1.1.16
- Fixed STRM delete logic
- Added matching logic to local media filter; STRM files are not written when a local media match is found

## v1.1.14
- Release

## v1.1.12 – v1.1.13
- Fixed local media filter year matching for same-title remakes

## v1.1.11
- Release

## v1.1.10
- Release

## v1.1.0
- Initial public release: Live TV tuner, VOD movie sync, TV show sync, documentary and docu series support, auto-sync, guide data, codec detection, dashboard
