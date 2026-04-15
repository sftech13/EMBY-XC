# Contributing to Xtream Tuner

## Current Architecture

### Startup and DI

Emby scans the assembly and may instantiate public service classes before `Plugin` itself is fully ready.

Rules:
- `Plugin.Instance` can be `null` during service construction
- `Plugin.Instance.Configuration` must not be touched in constructors
- defer config access to runtime methods

### Configuration Persistence

`PluginConfiguration` is serialized by Emby automatically. The plugin uses it for:
- connection settings
- M3U and EPG cache settings
- codec cache persistence
- sync timestamps and naming-version upgrades
- sync history
- auto-sync settings
- stored category caches

### Live TV Model

The current plugin streams directly from Xtream endpoints:
- live playback path: `{BaseUrl}/live/{username}/{password}/{streamId}.{ts|m3u8}`
- M3U generation comes from the same direct Xtream source
- there is no current Dispatcharr runtime dependency in the code

### Guide Data Model

`EpgSource` controls guide behavior:
- `XtreamServer`: try bulk XMLTV first, then per-channel Xtream JSON fallback
- `CustomUrl`: use the configured XMLTV URL only, with no Xtream fallback
- `Disabled`: tuner reports no guide support

`GetProgramsInternal` resolves `ChannelInfo.TunerChannelId` back to the Xtream `streamId`, fetches cached guide data, and creates `ProgramInfo` entries for Emby. If no guide data exists, it returns a placeholder row so the channel still shows in the grid.

### Channel Caching

`XtreamTunerHost` keeps a warm in-memory channel cache.

Behavior:
- cold start fetches channels synchronously
- fresh cache is returned immediately
- stale cache is returned immediately and refreshed in the background

### Codec Caching and Probing

The plugin uses a two-step strategy for faster live playback:

1. first tune can allow a short Emby probe window
2. background `ffprobe` runs and stores codec info in `StreamCodecCacheJson`
3. later tunes reuse cached codec data and can skip Emby's full probe

If you change this area, preserve these expectations:
- cached codec entries should populate `MediaStreams`
- later tunes should not regress into unnecessary probing
- `ClearCodecCache` must leave the next tune able to re-probe cleanly

### STRM Sync Behavior

Movie and series sync share a few core rules:
- output root is `StrmLibraryPath`
- smart skip avoids rewriting already-correct files
- orphan cleanup is optional and capped by `OrphanSafetyThreshold`
- sync progress is reported live
- sync history is persisted
- failed items can be retried later
- trial syncs preview up to 30 items without advancing timestamps

Series sync also uses:
- `LastSeriesSyncTimestamp` for delta sync
- `SeriesEpisodeHashesJson` to skip unchanged episode trees even when provider timestamps are noisy

### Metadata Helpers

Current metadata helpers in code:
- movie folder naming with `tmdbid`
- series folder naming with `tvdbid` or `tmdbid`
- Emby-provider fallback lookup for missing IDs
- optional movie NFO files
- optional `tvshow.nfo`

### Auto-Sync

Auto-sync supports:
- interval mode
- daily time mode

If you extend scheduling, keep the dashboard and saved config fields aligned with runtime behavior.

### Browser Cache Gotcha

If Emby's guide looks empty even though channels exist, a stale `guide-tagids` value in browser localStorage can hide every row. Clearing that localStorage key fixes the UI filter state.

---

## Docs Policy

- `README.md` is the current feature reference.
- `docs/architecture` and some ADRs include historical notes from earlier experiments. Update them when behavior changes if they describe current runtime paths.
- When a document is intentionally historical, label it clearly instead of rewriting the past.

---

## Workflow

### Keep Changes Scoped

Prefer one concern per branch or PR.

### Avoid Tangled Worktrees

If you are switching topics, commit or stash first.

### Build

```bash
cd Emby.Xtream.Plugin
bash build.sh
```

Output:
- `Emby.Xtream.Plugin/out/Emby.Xtream.Plugin.dll`

Requires .NET SDK 6.0+.
