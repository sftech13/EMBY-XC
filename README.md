<p align="center">
  <img src="logo.png" width="240" alt="XC2EMBY" />
</p>

<h1 align="center">XC2EMBY</h1>

<p align="center">
  An Emby Server plugin that connects directly to any Xtream-compatible IPTV provider.<br/>
  Live TV with guide data, background codec detection, VOD movie/documentary sync, and TV/docu-series sync — all from one config page.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Emby-4.8%2B-52B54B?style=flat-square&logo=emby" alt="Emby 4.8+" />
  <img src="https://img.shields.io/badge/.NET-Standard%202.0-512BD4?style=flat-square" alt=".NET Standard 2.0" />
  <img src="https://img.shields.io/badge/License-MIT-blue?style=flat-square" alt="MIT License" />
</p>

---

## Table of Contents

- [Features Overview](#features-overview)
- [Installation](#installation)
- [Configuration Guide](#configuration-guide)
  - [Settings Tab](#settings-tab)
  - [Live TV Tab](#live-tv-tab)
  - [Movies Tab](#movies-tab)
  - [Documentary Tab](#documentary-tab)
  - [TV Shows Tab](#tv-shows-tab)
  - [Docu Series Tab](#docu-series-tab)
- [Dashboard](#dashboard)
- [Auto-Sync](#auto-sync)
- [Channel Name Cleaning](#channel-name-cleaning)
- [Codec Detection & OSD Display](#codec-detection--osd-display)
- [Folder Modes](#folder-modes)
- [Metadata & NFO Files](#metadata--nfo-files)
- [Local Media Filtering](#local-media-filtering)
- [Orphan Cleanup](#orphan-cleanup)
- [Update Checker](#update-checker)
- [Development & Releases](#development--releases)
- [Configuration Reference](#configuration-reference)
- [Credits](#credits)

---

## Features Overview

### Current Release: v1.1.81

**v1.1.81**
- Improved Library Paths UX in Settings. Sub-folder fields (Movies, Documentaries, TV Shows, Docu Series) now display just the folder name instead of the full path when the value is directly under the STRM root (e.g. `Movies` instead of `/media/m3u2strm/Movies`). A small computed full-path hint appears below each field and updates live as you type, so the resolved location is always visible.

**v1.1.80**
- Fixed Emby auth token (`X-Emby-Token`) still leaking in sanitized log exports. Emby wraps token values in non-ASCII Unicode delimiter characters in its HTTP request log lines, causing the sanitizer regex to miss the match. Non-ASCII stripping now runs before sanitization so the regex sees clean text. Also fixed the "Legacy config migration skipped" message appearing in the Errors & Warnings section on every restart — it is a benign informational event (the plugin works correctly regardless) and is now logged at Info level instead of Warn.

**v1.1.79**
- Fixed Emby auth tokens (`X-Emby-Token`) being exposed in the sanitized log export. Emby includes the session token as a URL query parameter in every HTTP request it logs; the token can be used to authenticate against the server. It is now redacted to `X-Emby-Token=<token-redacted>` in all exported log lines.

**v1.1.78**
- Fixed sanitized log still showing garbled characters. Emby's `ResultFactory` ignores the `charset` parameter on `text/plain` responses, so declaring UTF-8 had no effect. Replaced all Unicode box-drawing/arrow characters in the log structure (`━`, `─`, `—`) with plain ASCII equivalents (`=`, `-`). Additionally, Emby's HTTP request logger injects Unicode delimiter characters around IP addresses and hostnames in its own log lines; these are now stripped from all log content lines before writing to the export, producing clean readable output (`plex.webhop.me` instead of `plex.webhop.me`).

**v1.1.77**
- Fixed sanitized log download rendering as garbled characters on most viewers. The response now explicitly declares `charset=utf-8` so browsers and text editors correctly interpret the UTF-8 encoded output. Also fixed a false-positive where plugin version numbers in the form `X.X.X.0` (assembly version, e.g. `1.1.76.0` after an upgrade arrow in the log) were incorrectly redacted as IP addresses — the version-protection regex now also covers the `→` and `->` arrow patterns logged during plugin version changes.

**v1.1.76**
- Improved sanitized log download readability. The exported file now includes a header (plugin version, export timestamp, line/error/warning counts, which log files were included, redaction notice), an Errors & Warnings section pulled to the top for quick triage, and the full log grouped by source file with section dividers. Filename is now timestamped (`xc2emby-log-YYYYMMDD-HHmmss.txt`). Safe to share for support — all credentials, IPs, and provider hostnames remain redacted.

**v1.1.75**
- Removed "Delete All" buttons from the Danger Zone in Movies, Documentaries, TV Shows, and Docu-Series tabs. The Browse & Delete panel already includes Select All / Deselect All, making the bulk-delete button redundant and unnecessarily risky. The browse panel button label is updated to "Browse & Delete" accordingly.

**v1.1.74**
- Fixed duplicate STRM files persisting when local media (Radarr/Sonarr) arrives after the XC2EMBY sync already wrote a STRM for the same title. Previously, when the local media filter matched a movie or episode on a subsequent sync, it added the existing STRM to a protection list (preventing orphan cleanup from removing it), so the duplicate stayed on disk indefinitely. Now when the filter detects a match and a STRM already exists, the STRM is deleted immediately and its directory is cleaned up if empty. The deletion count is included in the dashboard Deleted stat. Applies to both movies and TV show episodes.

**v1.1.73**
- Fixed the Deleted count on the dashboard always showing 0 when **Review Orphans** (orphan preview) is enabled. The sync correctly staged orphans but the manual commit step never updated the sync history. `CommitPendingOrphans` now categorises each deleted path by root folder prefix (Movies, TV Shows, Documentaries, Docu-Series) and patches the Deleted counter on the most recent matching history entry, so the dashboard reflects the true count after a commit.

**v1.1.72**
- Fixed button text not centered in the plugin config UI. `button-secondary` and `[is="emby-button"]` overrides were stripping Emby's built-in flex centering without replacing it. Added `text-align: center` and `justify-content: center` to both selectors, and made `.tabBtn` explicitly `inline-flex` with centered alignment.

**v1.1.71**
- Merged EPG Cache Duration and Channel Cache Duration into a single **Cache Duration** setting. Both caches are always invalidated together when a refresh fires, so separate timers were redundant. Default changed from 30/15 minutes to 360 minutes (6 hours) to match a typical 4× daily sync schedule. Existing installs will see 360 on first load — save the config page once to persist it.

**v1.1.70**
- Fixed Populate Episode Media Streams task skipping episodes that had Width set (from a v1.1.68 run) but no RunTimeTicks. The skip condition now requires both Width and RunTimeTicks to be populated, so a second run after upgrading from v1.1.68 will fill in the missing durations.

**v1.1.69**
- Fixed episodes being prematurely marked as played when stopping playback early. The Populate Episode Media Streams task now also writes `RunTimeTicks` from the XC API's `duration_secs` field. Without a known runtime, Emby cannot calculate percentage watched and defaults to marking the episode as played on any stop event.

**v1.1.68**
- Fixed Populate Episode Media Streams task failing immediately with `MissingMethodException` on Emby 4.9.x. `IItemRepository.GetMediaStreams(MediaStreamQuery)` was removed in Emby 4.9; replaced the already-probed check with `item.Width > 0` (Emby sets Width on the item after a successful probe, so width=0 means never probed).

**v1.1.67**
- Added **"XC2EMBY - Populate Episode Media Streams"** scheduled task. Queries Emby's library for all STRM TV show episodes with no media stream data in the database, fetches real per-episode codec info (video codec, resolution, framerate, audio codec, channels, language) from the XC API's `get_series_info` endpoint, and writes it directly to Emby's `MediaStreams2` table via `IItemRepository.SaveMediaStreams`. Once populated, Emby reads stream info from its database at playback time and skips ffprobe entirely — preventing the connection storm that occurs when multiple concurrent playback requests trigger parallel probes against a provider with connection limits. Run once after a full series sync; subsequent syncs add NFO streamdetails for new episodes so they never enter the unprobed state.

**v1.1.66**
- Fixed series sync failing with JSON deserialization error for providers that return `""` (empty string) or `[]` (empty array) instead of an object for episode `info`, `info.video`, or `info.audio` fields. Added `FlexibleObjectConverter<T>` that returns `null` for any non-object JSON token on these fields, matching the same defensive pattern already used for other XC API fields.

**v1.1.65**
- Fixed connection storms on STRM episode playback caused by empty `<streamdetails />` in Emby-written episode NFOs. When Emby has no cached MediaInfo for a STRM episode, it fires ffprobe on every PlaybackInfo request — with multiple concurrent requests this generates a storm of connections through the IPTV proxy, hitting provider connection limits and causing 403 errors. Fix: when **Write NFO Files** is enabled, the plugin now writes real per-episode stream details (codec, resolution, framerate, audio channels, language) directly from the XC API's `get_series_info` response at sync time. For episodes that already exist on disk with an empty `<streamdetails />` tag, the NFO is patched in-place on next sync, preserving all Emby-scraped metadata (plot, cast, ratings, etc.).

**v1.1.64**
- Added **"XC2EMBY - Refresh Live TV"** scheduled task. Runs every 4 hours by default (configurable in Dashboard → Scheduled Tasks). Invalidates the channel and EPG caches, triggers a channel rescan so Emby re-reads channel tags from the plugin, and refreshes the guide data. Previously, channel tags (and the guide category filter) would only update after a manual "Refresh Channel & EPG Cache" button click or an overnight Emby guide refresh.

**v1.1.55**
- Fixed channel group tags (MLB, ESPN, NFL, etc.) never appearing in the Emby Live TV guide tag filter. The XC API returns `category_id` as a JSON string (e.g. `"53"`) rather than an integer. `LiveStreamInfo.CategoryId` was declared as `int?` with no converter, so System.Text.Json silently nulled it on every deserialization — all channels had `CategoryId = null`, `Tags` was never set, and Emby's `SetTags()` call was never reached. Fixed by adding `[JsonConverter(typeof(FlexibleNullableInt32Converter))]` to `LiveStreamInfo.CategoryId` (same converter already used on `Category.CategoryId`).

**v1.1.54**
- Fixed "Refresh Channel & EPG Cache" button silently deadlocking on the second click. `BaseTunerHost.ValdidateOptions` (Emby base class) calls `GetChannelsInternal()` while holding `_channelInfoLock`. The `_explicitInvalidate` callback in `RefreshChannelCacheAsync` calls `TriggerChannelRescan()` → `SaveTunerHost()` → tries to re-acquire the same lock → deadlock. Fixed by overriding `ValdidateOptions` to return `Task.CompletedTask` (same pattern as Emby's own M3U plugin). Credentials are validated via the `TestXtreamConnection` endpoint instead.

**v1.1.53**
- Fixed Channel Cache Duration input allowing values below the enforced 5-minute minimum. HTML `min` attribute updated from `1` to `5` to match code behavior.

**v1.1.52**
- Fixed two channel cache bugs that caused the guide to blank or fail to update after a cache refresh while users were actively watching.
  1. **CAS race (`_isRefreshing` stuck at 1)**: `GetChannelsInternal`'s stale path set `_isRefreshing=1` before firing `Task.Run(RefreshChannelCacheAsync)`. Inside the task, `RefreshChannelCacheAsync`'s own CAS guard saw the flag already set and exited early — skipping the `finally` block and leaving `_isRefreshing` permanently at 1. All subsequent refreshes were silently dead until Emby restarted.
  2. **Fresh data never reaching Emby's DB**: Even with the soft invalidation introduced in v1.1.51, the background refresh updated `_cachedChannels` but nothing triggered a second `RefreshChannelsScheduledTask` to propagate that data into `BaseTunerHost._channelCache` and Emby's library DB. Added `_explicitInvalidate` flag: after a successful background refresh triggered by an explicit `RefreshCache` call, `TriggerChannelRescan()` fires again so Emby immediately picks up the fresh channel list.

**v1.1.51**
- Fixed guide going blank and Live TV dropping for active users when a cache refresh is triggered while streams are playing. The refresh now uses a soft cache invalidation — the existing channel list remains available while new data fetches in the background, then swaps in atomically. Previously, `ClearCaches()` nulled the channel list before Emby could re-fetch it; Emby defers channel rescans while streams are active, so the guide stayed blank until all users stopped watching or Emby restarted.

**v1.1.50**
- Fixed Live TV guide showing 0 channels after Emby restart. `RefreshChannelCacheAsync` was blocked indefinitely waiting for the XMLTV download/parse (264 MB XML via `XDocument.Load`) to complete before building the channel list. The XMLTV tasks now have a 10-second timeout — channels are registered immediately and XMLTV ID matching is applied on the next refresh cycle once the feed is loaded.

**v1.1.49**
- Hotfix: XMLTV timestamps without a space before the timezone offset (e.g. `20260513120000+0100`) had their timezone silently ignored, shifting all guide programs by the offset amount. `ParseXmltvTimestamp` now splits on `+`/`-` at position ≥14 when no space is present, restoring correct UTC conversion for all providers.

**v1.1.48**
- Fixed guide going blank during active live streams.
- Config UX improvements.

### Live TV
- Registers as a native Emby tuner host — channels appear in Live TV just like any other tuner
- Fetches channel list from Xtream `get_live_streams` API with a 6-hour warm cache and background refresh
- Supports `ts` (MPEG-TS) and `m3u8` (HLS) stream output formats
- Optional **direct play** — clients connect straight to the Xtream URL, bypassing Emby's transcoder entirely
- Filters channels by category and optionally excludes adult content
- Optionally adds category names as M3U `group-title` tags
- Optional guide refresh action clears cached channel logos so Emby can re-fetch current artwork

### Guide Data (EPG)
- Registered as a native Emby `IListingsProvider` — guide data flows through Emby's standard Live TV pipeline
- Three guide modes: **Xtream server** XMLTV, **custom XMLTV URL**, or **disabled**
- Full XMLTV document cached in memory with configurable TTL
- Full XMLTV field passthrough: sub-title, categories, production year, content rating, icon/poster, live/new/repeat/premiere flags, and season/episode numbers (xmltv_ns or onscreen format)
- Each program gets a unique `ShowId` scoped to its channel, preventing Emby from showing irrelevant "Other Showings" across unrelated channels

### Codec Detection & OSD Display
- Runs `ffprobe` in the background on first tune to detect video codec, resolution, and audio codec
- Results cached per stream (persisted across restarts, 30-day TTL)
- On subsequent tunes, Emby skips its own probe entirely — codec and resolution appear in the player OSD immediately
- Video display: `H264 1080p`, `HEVC 4K`, etc. Audio display: `AC3 5.1`, `AAC stereo`, etc.

### VOD Movies
- Syncs Xtream VOD catalog into `.strm` files for Emby library scanning
- Two folder layout modes in the UI: single folder or custom multi-folder category mapping
- Smart delta sync — only processes items added since the last run
- Optional **Skip Local Media** filter skips XC items already present in your Emby library
- Optional TMDb folder naming (`Movie Title [tmdbid=123]`) with fallback lookup through Emby
- Optional Kodi-compatible `.nfo` sidecar files
- **Stop Sync** can cancel an active STRM write
- One-click deletion of all synced content from the Movies tab

### Documentary Movies
- Uses the same VOD movie sync engine as Movies, with its own enable switch, category selection, folder mappings, root folder, delete action, and delta timestamp
- Lets you dedicate documentary provider groups to a separate Emby library path such as `Documentaries`

### TV Shows
- Syncs Xtream series into `Show/Season XX/Episode.strm` folder structure
- Same folder modes as movies, plus TVDb/TMDb folder naming
- Episode hash detection skips unchanged series even when the provider bumps timestamps
- TVDb ID manual overrides per series name
- Optional TVDb and TMDB fallback lookups through Emby's provider stack
- Optional `tvshow.nfo` and per-episode `.nfo` sidecar files with real stream details (codec, resolution, audio) from the provider — prevents Emby from probing STRM episode streams
- **Stop Sync** can cancel an active STRM write

### Docu Series
- Uses the same series sync engine as TV Shows, with separate enable switch, category selection, folder mappings, root folder, delete action, delta timestamp, and episode hash cache
- Lets documentary series live in their own root folder such as `Docu Series`

### Dashboard & Administration
- Built-in dashboard: sync history (last 10 runs), live progress, library stats, auto-sync schedule
- Quick actions for guide refresh, enabled-library sync, and sanitized log download
- Real-time progress bars for running syncs
- Retry failed items from the last sync
- Sanitized log download (credentials redacted)
- Config page uses Emby's active theme accent color throughout — adapts automatically when you change your Emby theme

---

## Installation

### Step 1 - Get the DLL

**Option A: Download a release**

Download `XC2EMBY.Plugin.dll` from the [latest release](../../releases/latest). Only the DLL is needed.

**Option B: Build from source**

Requires .NET SDK 8.0+. The plugin targets .NET Standard 2.0 for Emby compatibility.

```bash
git clone https://github.com/sftech13/EMBY-XC.git
cd EMBY-XC
dotnet build Emby.Xtream.Plugin/Emby.Xtream.Plugin.csproj -c Release
# Output: artifacts/bin/Release/netstandard2.0/XC2EMBY.Plugin.dll
```

For a deployable release-style build:

```bash
dotnet publish Emby.Xtream.Plugin/Emby.Xtream.Plugin.csproj -c Release -o artifacts/publish --no-self-contained
# Output: artifacts/publish/XC2EMBY.Plugin.dll
```

### Step 2 - Install

Copy the DLL to your Emby plugins directory and restart Emby.

**Linux (systemd)**
```bash
sudo cp XC2EMBY.Plugin.dll /var/lib/emby/plugins/
sudo systemctl restart emby-server
```

**Docker**
```bash
docker cp XC2EMBY.Plugin.dll emby:/config/plugins/
docker restart emby
```

### Step 3 - Open the Config Page

Go to **Emby Dashboard → Plugins → XC2EMBY** to open the configuration page.

### Updating

Download the new DLL from [Releases](../../releases/latest), replace the existing file, and restart Emby. Or use the built-in **Update Checker** on the Dashboard tab.

---

## Configuration Guide

The config page tabs are ordered as: **Dashboard**, **Live TV**, **Movies**, **Documentary**, **TV Shows**, **Docu Series**, and **Settings**.

---

### Settings Tab

![Settings](docs/settings.png)

#### Xtream Connection

| Field | Description |
|---|---|
| Server URL | Full URL to your Xtream server, e.g. `http://server.example.com:8080` |
| Username | Your Xtream account username |
| Password | Your Xtream account password |
| HTTP User-Agent | Optional custom `User-Agent` header sent with all provider requests |

Click **Test Connection** to verify credentials before saving.

#### STRM Library Path

The base directory where Movies, Documentaries, TV Shows, and Docu Series folders will be created. Example: `/media/xtream` -> movies go to `/media/xtream/Movies`, series go to `/media/xtream/TV Shows`.

Use the **Browse** buttons to navigate the server filesystem for the root path and each content folder field. The path is validated automatically when it changes.

Folder fields can be either:
- a folder name under the STRM library root, such as `Movies`
- a full absolute path, such as `/media/Movies`

#### Guide (EPG)

| Setting | Options | Description |
|---|---|---|
| EPG Source | Xtream Server / Custom URL / Disabled | Where to fetch guide data from |
| Custom XMLTV URL | URL | Used only when source is set to Custom URL |
| EPG Cache (minutes) | 1–1440 | How long to cache the XMLTV document before re-fetching |
| EPG Days to Fetch | 1–30 | How many days ahead of guide data to request |
| M3U Cache (minutes) | 1–1440 | How long to cache the generated M3U playlist |

#### Channel Name Cleaning

Optional cleaning applied to all live channel names. See [Channel Name Cleaning](#channel-name-cleaning) for what it removes.

---

### Live TV Tab

![Live TV](docs/LiveTV.png)

#### Enable Live TV

Master toggle for the Xtream tuner host. When disabled, no channels appear in Emby Live TV.

#### Stream Format

- **ts** — MPEG-TS. Best hardware compatibility, recommended for most setups.
- **m3u8** — HLS. Use if your client or network works better with adaptive streaming.

#### Direct Play

When enabled, clients connect directly to the Xtream stream URL. No Emby transcoding process is started, which eliminates all transcoder overhead and startup delay. Clients fall back to direct-stream or transcode automatically if they cannot handle the format.

When disabled, all playback routes through Emby's ffmpeg pipeline.

#### Category Filtering

Click **Refresh Categories** to fetch the current category list from your provider. Check the categories you want to include in Live TV. Leave all unchecked to include everything.

- **Include Adult Channels** — includes channels your provider has flagged as adult content
- **Include Group-Title in M3U** — adds the `group-title="Category Name"` tag to M3U entries, useful for external M3U clients

#### Cache Controls

- **Refresh Channel & EPG Cache** — invalidates XC2EMBY channel/guide caches and asks Emby to refresh guide data.
- **Clear Codec Cache** — removes all background-probed codec entries. Every channel will be re-probed on next tune. Use this if codec info appears wrong or stale.
- **Clear Channel Logo Cache on Guide Refresh** — when enabled, guide refresh deletes cached Live TV channel images so Emby can re-fetch current logos.

---

### Movies Tab

#### Enable Movies

Enables VOD movie sync to `.strm` files.

#### Folder Mode

Two modes control how movies are organized on disk. See [Folder Modes](#folder-modes) for a full explanation with examples.

#### Category Selection

Click **Refresh Categories** to load available VOD categories. Select which categories to sync. An empty selection syncs all categories.

Use the search box to filter by name. **Select All** / **Deselect All** buttons are available.

> An orange badge on the category count means no categories are selected — only relevant in Multiple Folders mode where unmapped categories are skipped.

#### Metadata Options

| Option | Description |
|---|---|
| TMDB Folder Naming | Appends `[tmdbid=12345]` to movie folder names for Emby metadata matching |
| TMDB Fallback Lookup | When a movie has no TMDB ID from the provider, queries Emby's TheMovieDb provider to find one (slower) |
| Write NFO Files | Creates Kodi-compatible `.nfo` sidecars with title and metadata IDs |
| Skip Local Media | Skips XC movies already present in your Emby library, matching by TMDB ID first and normalized title/year fallback |

#### Content Name Cleaning

Optional cleaning applied to movie titles before they are used as folder and file names. See [Content Name Cleaning](#content-name-cleaning).

#### Sync Controls

- **Sync Now** — runs the movie sync, writes needed files, updates the delta sync timestamp, and runs orphan cleanup if enabled.
- **Stop Sync** — requests cancellation of the active STRM sync. The current file operation may finish before the sync stops.

---

### Documentary Tab

Identical layout to the Movies tab, but stores its own VOD category selection, folder mappings, sync timestamp, and output root. Use this for movie-style documentary categories that should land in a dedicated documentary library.

---

### TV Shows Tab

Identical layout to the Movies tab with these additions:

#### Series-Specific Metadata Options

| Option | Description |
|---|---|
| Series ID Folder Naming | Appends `[tvdbid=12345]` or `[tmdbid=12345]` to series folder names |
| TVDb Fallback Lookup | Queries Emby's TheTVDB provider to find TVDb IDs for series missing one |
| TVDb ID Overrides | Manual per-series overrides in `SeriesName=12345` format, one per line. Takes priority over all automatic lookups. |
| Write NFO Files | Creates a `tvshow.nfo` in each series folder with title and metadata IDs |

**Folder naming priority (when Series ID Folder Naming is on):**
1. Manual TVDb override (from the overrides text area)
2. Provider-supplied TMDB ID
3. Auto TVDb lookup (if fallback lookup enabled)
4. Plain title (no ID found)

---

### Docu Series Tab

Identical layout to the TV Shows tab, but stores its own series category selection, folder mappings, sync timestamp, episode hash cache, and output root. Use this for documentary series categories that should land in a dedicated docu-series library.

---

## Dashboard

![Dashboard](docs/Dashboard.png)

The Dashboard tab gives a live view of the plugin state.

### Last Sync Card

Shows the most recent sync run: timestamp, success/failed status badge, and a breakdown of totals — movies added, episodes added, skipped, deleted, and failed.

### Sync History Table

The last 10 sync runs with timestamps, counts, and status. Useful for seeing trends over time.

### Library Stats

Live counts fetched from the filesystem:
- Movie folders and total movie `.strm` files
- Series folders, season folders, and total episode `.strm` files
- Live TV channel count (from the channel cache)

### Auto-Sync Status

Shows whether auto-sync is enabled and when the next run is scheduled. See [Auto-Sync](#auto-sync).

### Controls

| Button | Action |
|---|---|
| Sync Enabled Libraries | Runs enabled Movies, Documentaries, TV Shows, and Docu Series syncs in sequence |
| Retry Failed | Re-runs only the items that failed during the last sync |
| Clear Failed Items | Clears the failed item list without retrying |
| Clear History | Clears stored sync history |
| Download Logs | Downloads a sanitized copy of the Emby log file with credentials redacted |

---

## Auto-Sync

Enables scheduled sync runs without manual intervention.

### How It Works

Auto-sync uses Emby's built-in scheduled task system. Each content type — Movies, Documentaries, TV Shows, and Docu Series — is registered as a separate Emby scheduled task and appears under **Dashboard → Scheduled Tasks** in Emby. From there you can adjust the trigger time or run the task manually.

At install, the tasks register default daily triggers staggered starting at 03:00 to avoid concurrent runs. If **Auto-Sync Enabled** is off, all scheduled task runs exit immediately without syncing — so disabling this toggle is the master switch even if Emby's scheduler fires the task.

### Modes

The mode setting controls how the **next sync time** is calculated and displayed on the plugin dashboard. It does not change how Emby fires the underlying task.

**Interval mode** — the next sync is shown as `lastSyncEndTime + intervalHours`.

**Daily mode** — the next sync is shown as the next occurrence of the configured `HH:mm` in server local time.

### Settings

| Setting | Default | Description |
|---|---|---|
| Auto-Sync Enabled | Off | Master enable — tasks exit immediately if off |
| Mode | interval | `interval` or `daily` (affects dashboard display of next run) |
| Interval (hours) | 24 | Hours between runs (interval mode only, 1–168) |
| Daily Time | 03:00 | HH:mm in server local time (daily mode only) |

Auto-sync only runs the sync types that are individually enabled: Movies, Documentaries, TV Shows, and Docu Series. It uses the same parallelism, orphan cleanup, smart-skip, and local-media filter settings as manual syncs.

---

## Channel Name Cleaning

When **Enable Channel Name Cleaning** is on, the following transformations are applied to every live channel name (in order):

1. **User-defined terms** — case-insensitive removal of each term listed in the custom remove terms box
2. **Country prefix** — removes leading `UK: `, `US| `, `DE - ` style prefixes (two-letter country code + separator)
3. **Quality separators** — replaces `| HD |` style inline separators with a space
4. **Bracketed tags** — removes `[HD]`, `(FHD)`, `[HEVC]`, `(H.264)`, etc.
5. **Inline codec labels** — removes bare `HEVC`, `H.264`, `VP9`, `AV1`
6. **Resolution suffixes** — removes trailing `1080p`, `720i`, `4K`
7. **Trailing quality tags** — removes trailing ` HD`, ` UHD`, ` 4K`
8. **Pipe cleanup** — removes leading/trailing pipes
9. **Whitespace normalization** — collapses multiple spaces, trims

**Examples:**

| Before | After |
|---|---|
| `UK: Sky Sports HD \| FHD` | `Sky Sports` |
| `┃EN┃ HBO \| 1080p` | `HBO` |
| `US - ESPN 720p (HEVC)` | `ESPN` |
| `[DE] Bundesliga HD` | `Bundesliga` |

Custom remove terms (one per line in the Settings tab) are applied first, before the automatic patterns.

---

## Content Name Cleaning

When **Enable Content Name Cleaning** is on, movie and series titles are cleaned before being used as folder and file names.

This removes box-style country code prefixes that providers embed in titles:

- Unicode box characters: `┃UK┃ Movie Title` → `Movie Title`
- Pipe-delimited: `|EN| Movie Title` → `Movie Title`
- Dash-prefix: `EN - Movie Title` → `Movie Title` (exactly two uppercase letters followed by ` - `)

Custom remove terms (one per line in the Movies/Series tab) are also applied.

---

## Codec Detection & OSD Display

On the first tune of any channel, the plugin fires a background `ffprobe` process against the stream URL. This runs asynchronously and does not block playback.

When the probe completes (up to 15 seconds), the result is cached:

| Cached Field | Example |
|---|---|
| Video codec | `h264`, `hevc` |
| Video resolution | `1920 × 1080` |
| Audio codec | `ac3`, `aac` |
| Audio channels | `6` (5.1) |
| Audio language | `eng` |

On **subsequent tunes**, Emby uses the cached data directly and skips its own probe entirely. The player OSD displays:

- **Video:** `H264 1080p`, `HEVC 4K`, `MPEG2 720p`, etc.
- **Audio:** `AC3 5.1`, `AAC stereo`, `EAC3 7.1`, etc.

The cache persists across Emby restarts (stored in the plugin config file). Entries expire after 30 days and are automatically re-probed on next tune.

**ffprobe search order:**
1. `/opt/emby-server/bin/ffprobe` (standard Emby deb/rpm install)
2. `/usr/bin/ffprobe`
3. `/usr/local/bin/ffprobe`
4. `/usr/lib/emby-server/bin/ffprobe`
5. `/usr/share/emby-server/bin/ffprobe`
6. `ffprobe` via PATH

If ffprobe is not found, codec detection is silently skipped and Emby falls back to its own short probe on every tune.

Use **Clear Codec Cache** on the Live TV tab to force fresh probes for all channels.

---

## Folder Modes

Movies, Documentaries, TV Shows, and Docu Series support two folder layout modes in the UI.

The configuration reader still accepts the older internal value `multiple` for compatibility, but the current config page uses **Single Folder** and **Multiple Folders** only. The Multiple Folders card stores custom per-folder category assignments.

### Single Folder

All content in one folder under Movies or TV Shows:

```
{StrmLibraryPath}/
  Movies/
    Movie Title (2023)/
      Movie Title (2023).strm
  TV Shows/
    Series Name/
      Season 01/
        Series Name - S01E01 - Episode Title.strm
```

### Multiple Folders

You define which categories go into which folder. Enter mappings in the text area:

```
# Lines starting with # are comments
English Movies=1001,1002,1003
4K Content=2001,2002
Foreign=3001,3002,3003
```

Each line: `FolderName=CategoryId1,CategoryId2,...`

In the UI this is managed with **+ Add Folder** and category checkboxes after **Refresh Categories**. Categories not mapped to any folder are skipped. If no folders/mappings are defined while Multiple Folders is active, the sync aborts with a configuration error rather than silently doing nothing.

Result:
```
{StrmLibraryPath}/
  Movies/
    English Movies/
      Movie Title (2023)/
        Movie Title (2023).strm
    4K Content/
      ...
```

---

## Metadata & NFO Files

### TMDb Folder Naming (Movies)

When enabled, movie folder names include the TMDb ID:

```
The Dark Knight (2008) [tmdbid=155]/
  The Dark Knight (2008) [tmdbid=155].strm
```

Emby uses this to match the folder to the correct metadata entry without ambiguity. TMDB IDs are sourced from:
1. The provider's `tmdb_id` field (if present)
2. **TMDB Fallback Lookup** — queries Emby's TheMovieDb provider (slower, runs during sync)
3. Falls back to plain title if no ID found

### Series ID Folder Naming (Series)

Same concept for series, using TVDb or TMDB IDs:

```
Breaking Bad [tvdbid=81189]/
  Season 01/
    Breaking Bad - S01E01 - Pilot.strm
```

ID priority: manual override → provider TMDB → auto TVDb lookup → plain name.

### TVDb ID Overrides (Series)

For series where automatic lookup fails or gives the wrong result, enter manual overrides in the Series tab:

```
Breaking Bad=81189
The Wire=79126
# format: Series Name=TVDbId
```

These take priority over all automatic lookups.

### NFO Sidecar Files

When **Write NFO Files** is enabled, Kodi-compatible XML sidecars are created when a metadata ID is available. Existing NFO files are never overwritten, preserving manual edits.

**Movie NFO (`<Movie Folder Name>.nfo`):**
```xml
<movie>
  <title>The Dark Knight</title>
  <year>2008</year>
  <uniqueid type="tmdb" default="true">155</uniqueid>
</movie>
```

**TV Show NFO (`tvshow.nfo`):**
```xml
<tvshow>
  <title>Breaking Bad</title>
  <uniqueid type="tvdb" default="true">81189</uniqueid>
</tvshow>
```

---

## Local Media Filtering

When **Skip Local Media** is enabled, the sync scans the current Emby library at the start of each run and builds lookup sets for existing movies, series, and episodes.

Matching order:
1. TMDB ID, when both the XC item and Emby library item have one
2. Normalized title with production year, such as `3 10 to yuma 2007`
3. Normalized title without year as a fallback

Years are preserved during matching to reduce false positives between remakes or same-name titles. For example, `3:10 to Yuma (1957)` and `3:10 to Yuma (2007)` are treated as different items when Emby has production years.

Items already under the plugin's own STRM library path are automatically excluded from the lookup set, so the filter only matches against non-STRM library content.

Skipped local matches are counted as `Skipped` in sync progress. For series, the local-media check happens before fetching per-series episode details, reducing provider API calls.

---

## Orphan Cleanup

When **Cleanup Orphans** is enabled, the sync deletes `.strm` files that exist on disk but are no longer present in the provider's catalog.

### Safety Threshold

To prevent accidental mass deletion (e.g., if a provider temporarily returns an empty catalog), a safety threshold blocks cleanup when too many files would be deleted:

- If orphans exceed **X%** of total `.strm` files **and** total files > 10 → cleanup is skipped with a warning
- Default threshold: **20%**
- Set to **0** to disable the safety check (always clean up regardless of percentage)

Empty parent directories are removed after file deletion.

### Local Media Filter Interaction

When **Skip Local Media** is also enabled, STRMs for items matched against your local library are tracked separately. The orphan safety threshold only counts provider-missing orphans (items removed from the XC catalog) — locally-filtered STRMs are excluded from the ratio calculation and deleted independently. This prevents a first-run with a large local library from triggering the safety threshold.

### Smart Skip Interaction

Smart skip and orphan cleanup work together:
- Smart skip avoids re-writing files for unchanged items
- Orphan cleanup removes files for items that have been removed from the provider
- Both can be active simultaneously

---

## Update Checker

The Dashboard tab includes a built-in update checker that queries GitHub releases.

- Checks the `sftech13/EMBY-XC` repository for new releases
- **Beta Channel** — when enabled, also checks pre-releases in addition to stable releases
- Shows available version, release notes link, and a one-click **Install Update** button
- Install downloads the new DLL and replaces the installed file atomically, then prompts for an Emby restart
- A notification banner is shown once per new version and suppressed after acknowledgement

---

## Development & Releases

Generated build files are kept out of the source tree:

| Purpose | Path |
|---|---|
| Normal build output | `artifacts/bin/<Configuration>/netstandard2.0/` |
| MSBuild intermediate files | `artifacts/obj/` |
| Release publish output | `artifacts/publish/` |

GitHub Actions builds releases when a version tag is pushed:

```bash
git tag v1.1.0-beta.1
git push origin v1.1.0-beta.1

git tag v1.1.0
git push origin v1.1.0
```

Use beta tags with letters, such as `v1.1.0-beta.1`, for test builds. Use plain numeric tags, such as `v1.1.0`, for releases. The release workflow publishes `artifacts/publish/XC2EMBY.Plugin.dll` and creates a published GitHub Release.

---

## Configuration Reference

Complete list of all configuration fields.

### Connection

| Field | Type | Default | Description |
|---|---|---|---|
| `BaseUrl` | string | `""` | Xtream server URL |
| `Username` | string | `""` | Xtream username |
| `Password` | string | `""` | Xtream password |
| `HttpUserAgent` | string | `""` | Custom User-Agent header |

### Live TV

| Field | Type | Default | Description |
|---|---|---|---|
| `EnableLiveTv` | bool | `true` | Enable/disable the tuner host |
| `LiveTvOutputFormat` | string | `"ts"` | `"ts"` or `"m3u8"` |
| `EnableLiveTvDirectPlay` | bool | `true` | Allow client-side direct URL playback |
| `TunerCount` | int | `1` | Number of tuner instances Emby can use |
| `SelectedLiveCategoryIds` | int[] | `[]` | Live categories to include (empty = all) |
| `IncludeAdultChannels` | bool | `false` | Include adult-flagged channels |
| `IncludeGroupTitleInM3U` | bool | `true` | Add `group-title` tags to M3U |
| `ExcludedLiveCategories` | string list | `[]` | Category names excluded from guide tag filtering |

### Guide / EPG

| Field | Type | Default | Description |
|---|---|---|---|
| `EpgSource` | enum | `XtreamServer` | `XtreamServer`, `CustomUrl`, or `Disabled` |
| `CustomEpgUrl` | string | `""` | Custom XMLTV endpoint URL |
| `EpgCacheMinutes` | int | `30` | XMLTV cache TTL in minutes |
| `EpgDaysToFetch` | int | `2` | Days ahead to fetch EPG data |
| `M3UCacheMinutes` | int | `15` | M3U cache TTL in minutes |

### Movies

| Field | Type | Default | Description |
|---|---|---|---|
| `SyncMovies` | bool | `false` | Enable movie sync |
| `StrmLibraryPath` | string | `"/config/xtream"` | Base output path |
| `MovieRootFolderName` | string | `"Movies"` | Root folder name under `StrmLibraryPath` |
| `SelectedVodCategoryIds` | int[] | `[]` | VOD categories to sync (empty = all) |
| `MovieFolderMode` | string | `"single"` | `"single"` or `"custom"` (`"multiple"` accepted for legacy configs) |
| `MovieFolderMappings` | string | `""` | Custom mappings (`FolderName=Cat1,Cat2`) |
| `EnableTmdbFolderNaming` | bool | `false` | Add `[tmdbid=...]` to movie folders |
| `EnableTmdbFallbackLookup` | bool | `false` | Look up missing TMDB IDs via Emby |

### Documentary Movies

| Field | Type | Default | Description |
|---|---|---|---|
| `SyncDocumentaries` | bool | `false` | Enable documentary movie sync |
| `DocumentaryRootFolderName` | string | `"Documentaries"` | Root folder name under `StrmLibraryPath` |
| `SelectedDocumentaryCategoryIds` | int[] | `[]` | VOD categories to sync as documentaries |
| `DocumentaryFolderMode` | string | `"single"` | `"single"` or `"custom"` (`"multiple"` accepted for legacy configs) |
| `DocumentaryFolderMappings` | string | `""` | Custom mappings |

### TV Shows

| Field | Type | Default | Description |
|---|---|---|---|
| `SyncSeries` | bool | `false` | Enable series sync |
| `SeriesRootFolderName` | string | `"TV Shows"` | Root folder name under `StrmLibraryPath` |
| `SelectedSeriesCategoryIds` | int[] | `[]` | Series categories to sync (empty = all) |
| `SeriesFolderMode` | string | `"single"` | `"single"` or `"custom"` (`"multiple"` accepted for legacy configs) |
| `SeriesFolderMappings` | string | `""` | Custom mappings |
| `EnableSeriesIdFolderNaming` | bool | `false` | Add `[tvdbid=...]` or `[tmdbid=...]` to series folders |
| `EnableSeriesMetadataLookup` | bool | `false` | Look up missing TVDb IDs via Emby |
| `TvdbFolderIdOverrides` | string | `""` | Manual overrides (`SeriesName=12345`) |

### Docu Series

| Field | Type | Default | Description |
|---|---|---|---|
| `SyncDocuSeries` | bool | `false` | Enable documentary series sync |
| `DocuSeriesRootFolderName` | string | `"Docu Series"` | Root folder name under `StrmLibraryPath` |
| `SelectedDocuSeriesCategoryIds` | int[] | `[]` | Series categories to sync as docu series |
| `DocuSeriesFolderMode` | string | `"single"` | `"single"` or `"custom"` (`"multiple"` accepted for legacy configs) |
| `DocuSeriesFolderMappings` | string | `""` | Custom mappings |

### Shared Sync

| Field | Type | Default | Description |
|---|---|---|---|
| `EnableNfoFiles` | bool | `false` | Write `.nfo` sidecar files |
| `EnableContentNameCleaning` | bool | `false` | Clean box-style prefixes from titles |
| `ContentRemoveTerms` | string | `""` | Custom title terms to remove (one per line) |
| `SmartSkipExisting` | bool | `true` | Skip re-writing existing unchanged STRM files |
| `EnableLocalMediaFilter` | bool | `false` | Skip XC items already present in the Emby library |
| `SyncParallelism` | int | `3` | Max concurrent sync tasks (1–10) |
| `CleanupOrphans` | bool | `false` | Delete files removed from provider |
| `OrphanSafetyThreshold` | double | `0.20` | Max orphan % before skipping cleanup |

### Auto-Sync

| Field | Type | Default | Description |
|---|---|---|---|
| `AutoSyncEnabled` | bool | `false` | Enable scheduled auto-sync |
| `AutoSyncMode` | string | `"interval"` | `"interval"` or `"daily"` |
| `AutoSyncIntervalHours` | int | `24` | Hours between runs (1–168) |
| `AutoSyncDailyTime` | string | `"03:00"` | Daily run time in HH:mm (server local time) |

### Channel Name Cleaning

| Field | Type | Default | Description |
|---|---|---|---|
| `EnableChannelNameCleaning` | bool | `true` | Remove quality tags and prefixes from channel names |
| `ChannelRemoveTerms` | string | `""` | Custom channel name terms to remove (one per line) |

### Updates

| Field | Type | Default | Description |
|---|---|---|---|
| `UseBetaChannel` | bool | `false` | Include pre-releases in update check |

---

## Credits

XC2EMBY is a fork of the original work by [@firestaerter3](https://github.com/firestaerter3). The foundation, plugin architecture, and core Xtream integration concepts originated from that project. This fork has been extended and modified significantly from that base.

---

## License

MIT
