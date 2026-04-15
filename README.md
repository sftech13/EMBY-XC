<p align="center">
  <img src="logo.svg" width="180" alt="Xtream Tuner" />
</p>

<h1 align="center">Xtream Tuner</h1>

<p align="center">
  An Emby Server plugin for Xtream-compatible providers with Live TV, XMLTV guide support, movie and series STRM sync, metadata helpers, and a built-in admin dashboard.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Emby-4.8%2B-52B54B?style=flat-square&logo=emby" alt="Emby 4.8+" />
  <img src="https://img.shields.io/badge/.NET-Standard%202.0-512BD4?style=flat-square" alt=".NET Standard 2.0" />
  <img src="https://img.shields.io/badge/License-MIT-blue?style=flat-square" alt="MIT License" />
</p>

---

## What It Does

Xtream Tuner currently focuses on direct Xtream integration. The codebase no longer depends on Dispatcharr for streaming or guide data.

### Live TV

- Generates an Emby-compatible M3U playlist from Xtream live streams
- Supports `ts` and `m3u8` live stream URLs
- Filters channels by selected categories
- Optionally hides adult channels
- Optionally includes category names as M3U `group-title` tags
- Cleans channel names before they reach Emby
- Keeps a warm channel cache so guide loads do not block on the provider every time

### Guide Data

- Supports three guide modes:
  - Xtream server EPG
  - custom XMLTV URL
  - disabled
- Caches generated XMLTV output and per-channel EPG responses
- Uses bulk XMLTV first when available
- Falls back to Xtream `get_simple_data_table` per channel when Xtream XMLTV fails
- Intentionally does not fall back when a custom XMLTV URL fails
- Returns a placeholder guide row when a channel has no EPG so the channel still appears in Emby

### Live Stream Playback

- Uses direct Xtream live URLs instead of a proxy layer
- Optional Live TV direct play toggle
- Runs background `ffprobe` on first tune to learn codecs and resolution
- Caches codec metadata for later tunes so Emby can skip repeated probing
- Exposes a Live TV settings action to clear the codec cache and re-probe channels
- Passes a custom HTTP `User-Agent` to provider requests when configured

### Movies

- Syncs VOD movies into `.strm` files
- Supports single-folder, per-category, and custom folder-mapping layouts
- Supports smart skip for already-correct files
- Supports orphan cleanup with a safety threshold
- Supports TMDb folder naming like `Movie Name [tmdbid=123]`
- Supports TMDb fallback lookup through Emby's provider stack
- Optionally writes movie `.nfo` sidecars
- Supports trial sync previews for up to 30 movies
- Supports one-click deletion of synced movie content from the Movies tab

### Series

- Syncs Xtream series into `Show/Season XX/Episode.strm`
- Fetches per-series detail to build season and episode structure
- Cleans duplicate provider text out of episode titles
- Supports single-folder, per-category, and custom folder-mapping layouts
- Supports smart skip by timestamp and by episode-hash change detection
- Supports TVDb override mapping for specific shows
- Supports series folder naming with TVDb or TMDb IDs
- Supports TVDb fallback lookup through Emby's provider stack
- Optionally writes `tvshow.nfo`
- Supports trial sync previews for up to 30 series
- Supports one-click deletion of synced series content from the Series tab

### Dashboard And Operations

- Built-in dashboard with sync status, history, and library counts
- Real-time progress for running sync jobs
- Retry flow for failed sync items
- Sanitized log download endpoint
- Auto-sync on an interval or at a daily clock time
- Manual cache refresh for M3U and EPG output

---

## Installation

### Step 1: Download The Plugin

Download `Emby.Xtream.Plugin.dll` from the [latest release](../../releases/latest).

> Only the DLL is needed.

<details>
<summary><strong>Build from source</strong></summary>

Requires .NET SDK 6.0+:

```bash
git clone https://github.com/sftech13/EMBY-XC.git
cd EMBY-XC/Emby.Xtream.Plugin
bash build.sh
```

The compiled DLL will be at `Emby.Xtream.Plugin/out/Emby.Xtream.Plugin.dll`.

</details>

### Step 2: Install The Plugin

Copy `Emby.Xtream.Plugin.dll` to your Emby plugins directory and restart Emby.

**Docker**
```bash
docker cp Emby.Xtream.Plugin.dll emby:/config/plugins/
docker restart emby
```

**Linux**
```bash
cp Emby.Xtream.Plugin.dll /var/lib/emby/plugins/
systemctl restart emby-server
```

### Step 3: Configure Xtream Access

1. Open Emby.
2. Go to `Settings > Plugins > Xtream Tuner`.
3. Fill in:
   - `Server URL`
   - `Username`
   - `Password`
   - optional `HTTP User-Agent`
4. Click `Test Connection`.
5. Save.

### Step 4: Set Up Live TV

1. Open the `Live TV` tab.
2. Enable Live TV if needed.
3. Choose `ts` or `m3u8`.
4. Choose guide mode:
   - Xtream server
   - custom XMLTV URL
   - disabled
5. Refresh categories and select the ones you want.
6. Optionally enable:
   - adult channels
   - direct play
   - M3U group-title tags
7. Save.
8. Add the plugin as a tuner in Emby Live TV settings.

### Step 5: Set Up Movies Or Series Sync

1. Set `STRM Library Path`.
2. Enable Movies and/or Series.
3. Refresh categories.
4. Choose folder mode:
   - single
   - multiple
   - custom mappings
5. Optionally enable:
   - smart skip
   - orphan cleanup
   - NFO writing
   - TMDb/TVDb folder naming
   - metadata fallback lookup
6. Run a trial sync first if you want a preview.
7. Run the real sync.
8. Add the output folders as Emby libraries.

### Updating

Download the latest DLL from [Releases](../../releases/latest), replace the existing file, and restart Emby.

---

## Configuration Highlights

| Setting | Default | Notes |
|---|---|---|
| `EnableLiveTv` | On | Enables the custom tuner host |
| `LiveTvOutputFormat` | `ts` | `ts` or `m3u8` |
| `EnableLiveTvDirectPlay` | On | Lets clients play the remote URL directly when possible |
| `EpgSource` | Xtream server | Xtream, custom URL, or disabled |
| `CustomEpgUrl` | empty | Used only in custom guide mode |
| `EpgCacheMinutes` | `30` | XMLTV and per-channel guide cache TTL |
| `M3UCacheMinutes` | `15` | M3U cache TTL |
| `StrmLibraryPath` | `/config/xtream` | Base output path for Movies and Shows |
| `SmartSkipExisting` | On | Skips already-correct files |
| `CleanupOrphans` | Off | Deletes missing provider content from disk |
| `OrphanSafetyThreshold` | `20%` | Caps deletion percentage per cleanup pass |
| `EnableNfoFiles` | Off | Writes movie and show sidecar NFOs |
| `EnableTmdbFolderNaming` | Off | Adds `tmdbid` tags to movie folders |
| `EnableSeriesIdFolderNaming` | Off | Adds `tvdbid` or `tmdbid` tags to series folders |
| `EnableTmdbFallbackLookup` | Off | Uses Emby providers to find missing movie TMDb IDs |
| `EnableSeriesMetadataLookup` | Off | Uses Emby providers to find missing TVDb IDs |
| `AutoSyncEnabled` | Off | Enables scheduled sync runs |
| `AutoSyncMode` | `interval` | `interval` or `daily` |

---

## Repo Notes

- `README.md` and `CONTRIBUTING.md` describe the current plugin behavior.
- Some ADR files under `docs/decisions` are preserved as implementation history for behavior that has since been removed or revised.

## License

MIT
