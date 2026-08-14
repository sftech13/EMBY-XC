# Changelog

All notable changes to XC2EMBY are listed here, newest first.

---

## v1.1.125
- Reworked proxied Live TV into one upstream connection with bounded per-viewer fan-out buffers, so viewers of the same channel share one provider slot without a slow or disconnected client blocking the others. Added upstream-stall reconnect handling and close/handoff race protection.
- Replaced the retained XMLTV `XDocument`, XML node index, and duplicate parsed cache with one forward-only streaming snapshot. Only selected channels and the configured guide window are kept; a real 116,974-program feed now parses with about 197 MB peak memory in an isolated test.
- Large generated `epg.xml` strings are no longer cached when they exceed 16 MB, preventing another full guide copy from remaining pinned for the cache TTL.
- Replaced XC2EMBY's delayed global Emby library scan with coalesced recursive refreshes limited to the Movies, Documentaries, TV Shows, and DocuSeries roots that actually changed. Unrelated local libraries are no longer scanned after a STRM sync.

## v1.1.124
- TV Shows and DocuSeries sync now fail closed when no categories are selected instead of treating an empty selection as an unfiltered full-catalog request.
- Hardened Xtream JSON parsing for providers that inconsistently encode series, episode, VOD, Live TV, and EPG numeric values as either strings or numbers.
- Added a continuous-integration workflow that compile-checks every push to `main` and every pull request.

## v1.1.123
- Added deterministic episode-path ownership for duplicate series records when metadata-ID folder naming is disabled. Mirrored provider entries can no longer race and alternate the stream ID stored in the same STRM file.
- Duplicate resolution prefers the record with the most complete episode-path set, then the newest provider timestamp, then the lowest series ID. Only competing episode paths are suppressed, so unique episodes from either record remain available.
- Duplicate-series detail responses fetched during ownership preflight are reused by the main sync, avoiding duplicate API requests.

## v1.1.122
- Series-detail SSL, timeout, and other provider failures now protect every existing STRM for only the affected series while allowing orphan cleanup to continue for the rest of a successfully fetched XC series catalog. Main catalog failures, filesystem/write failures, and invalid target paths still block all cleanup.
- Per-series Smart Skip fingerprints are now committed only after every episode in that series finishes processing successfully, ensuring a partial file-processing failure forces full verification on the next run.

## v1.1.121
- Added privacy-safe STRM URL change diagnostics that report aggregate endpoint, credential, stream-ID, extension, and other change counts without logging URLs or secrets.
- Restored **Smart Skip Existing** for series using per-series SHA-256 fingerprints covering episode IDs/extensions and the connection settings embedded in STRM URLs. Expected files are still checked for existence, local-media filtering still runs, and any provider or connection change falls back to a full content comparison.
- Successful per-series Smart Skip checkpoints are now retained when unrelated provider records fail, while orphan cleanup and the global catalog timestamp remain blocked for safety.

## v1.1.120
- Fixed **Refresh Channel & EPG Cache** starting redundant, overlapping Emby guide rebuilds. The tuner rescan now owns the refresh sequence instead of also launching two explicit guide refresh paths, reducing each cache invalidation from three guide jobs to the required two-stage stale/fresh channel rescan and avoiding unnecessary memory pressure on large guides.

## v1.1.119
- Fixed provider health checks disposing XC2EMBY's shared 10-second HTTP client after their first request. Repeated health checks, category requests, and scheduled-sync reachability gates now continue using the shared client instead of failing with `ObjectDisposedException`.

## v1.1.118
- Provider health checks now defer while any movie, documentary, TV-show, DocuSeries, or failed-item retry sync is active. Deferred checks preserve the last known status and failure count instead of reporting false outages when the local Xtream server is busy serving synchronization requests.
- Provider HTTP retry handling now retries only HTTP 408/429/5xx responses and genuine timeout/connection/SSL failures. Ordinary 4xx responses such as stale series-detail 404s return immediately instead of consuming the 2/5/10-second transient backoff.

## v1.1.117
- Successful movie, documentary, TV-show, and DocuSeries orphan cleanup now performs a safe bottom-up metadata sweep. Empty directories and directories containing only generated NFO files are removed, including historical leftovers whose STRM was deleted by an older plugin version.
- Metadata cleanup remains inside the configured content root, skips symbolic links/reparse points, and preserves any directory containing a STRM, artwork, media, or another live subdirectory.

## v1.1.116
- Movie sync now resolves and groups provider records by their final STRM path before writing. Local-media matches suppress the entire path, preventing another cross-listed/duplicate record from recreating a filtered STRM during the same run.
- Duplicate movie paths now have one stable owner. The existing stream URL is preserved when it still belongs to a current provider record; otherwise the lowest stream ID wins deterministically, eliminating repeated URL rewrites caused by parallel records racing for one file.
- Local-media movie removal now deletes the matching generated NFO and prunes metadata-only directories using the same safe content-root boundary as orphan cleanup.

## v1.1.115
- Treats series records that return a successful but empty/no-episode detail payload like stale 404 records: existing episode STRMs are protected and the record is reported as a protected skip instead of permanently failing every sync.

## v1.1.114
- Treats series-list entries whose detail endpoint consistently returns HTTP 404 as stale provider records: existing episode STRMs are explicitly protected from orphan cleanup and the record is reported as a protected skip. Transient SSL/connection/429/5xx failures still mark the run incomplete and block cleanup.
- Provider request errors no longer misleadingly say "after transient retries" for non-retryable HTTP responses such as 404.

## v1.1.113
- Orphan cleanup now removes the matching movie or episode NFO with each deleted STRM, removes generated `tvshow.nfo` metadata after the final episode is gone, and prunes empty season/title directories without crossing the configured content root. Staged-orphan commits use the same cleanup behavior.
- Added transient provider retries with 2/5/10-second backoff for HTTP 429/5xx, timeouts, connection failures, and temporary SSL failures.
- Category fetches now fail closed instead of turning a failed category into an empty catalog. Incomplete movie/series runs block orphan cleanup and do not advance sync timestamps or episode hashes.
- Any unresolved series-detail failure now preserves the existing library by blocking cleanup for the entire content run, marks Sync History as failed, and reports the actual failed-series count.
- Large orphan sets above 20% require two consecutive identical complete provider catalogs before deletion, in addition to the configured percentage threshold.
- Provider health checks now validate the enabled VOD and series category endpoints after authentication.
- Fixed VOD Sync History totals becoming negative after large orphan cleanups; orphan deletions are no longer subtracted from the provider catalog total.

## v1.1.112
- Coalesced STRM sync refreshes: Movies, Documentaries, TV Shows, DocuSeries, and retry operations no longer queue an immediate global Emby scan independently. File changes now set one pending scan, sync completions reset a 90-minute quiet window, and the scan waits until no XC2EMBY sync is running.

## v1.1.111
- Fixed Auto-Sync saves leaving XC2EMBY's Emby scheduled-task triggers empty: the settings page now handles both direct-array and wrapped Emby scheduled-task responses, waits for all four STRM-sync schedules to persist before completing or reloading, and reports malformed responses, missing tasks, or failed trigger updates.
- Limited Auto-Sync schedule updates to Movies, Documentaries, TV Shows, and DocuSeries so saving plugin settings cannot overwrite unrelated XC2EMBY health, guide-refresh, or manual-task triggers.

## v1.1.110
- Removed the experimental same-channel session isolation setting after Emby 4.9 testing showed that it either invalidated playback source negotiation or continued sharing the original stream. Shared-stream behavior is restored unchanged.
- Added HDR range detection to the live-stream codec probe: SMPTE ST 2084/PQ is reported as HDR and ARIB STD-B67 is reported as HLG. Clear the codec probe cache once to refresh existing channel metadata.

## v1.1.109
- Added an opt-in **Isolate Same-Channel Playback Sessions** setting. Shared mode remains the default to minimize provider connections; isolated mode gives each playback a unique Emby media-source identity so stopping one device cannot close another device watching the same channel.
- Preserved the playback-offer media-source ID through Emby's live-stream opening handoff while keeping provider URLs, channel lookup, codec probing, and codec caching keyed by the original numeric Xtream stream ID.
- Added playback-mode and media-source identity logging for diagnosing shared versus isolated session behavior without logging provider credentials.

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
