# Optional Linux monitoring integration

XC2EMBY's channel-specific HLS fallback works inside the plugin. These optional
scripts integrate its diagnostics with an existing systemd/VPN/BOSS setup.
They do not change routes, rotate a VPN, or contain provider credentials.

## Stream event forwarding

`emby-iptv-stream-monitor.sh` follows Emby's current log (default
`/var/lib/emby/logs/embyserver.txt`, override with `EMBY_LOG`) across log rotation.
It starts at EOF so installing or restarting it never replays historical failures.

Confirmed watchdog failures are forwarded to the `emby-iptv-stream-monitor`
journal tag as `stream-failure stream=xtream_live_ID reason=read-stall`, but only
when the plugin explicitly identifies a read stall with a positive active-viewer
count. Generic operation cancellations and stalls with no viewers are ignored.
EOF, TLS, request timeout and HTTP 5xx transport classifications remain available.
HTTP 404 is forwarded separately under `emby-iptv-lineup-monitor`; a stale
lineup is not evidence that a VPN should rotate.

Existing VPN monitors can consume these events using their existing window,
threshold, multi-channel requirement and cooldown. Do not lower a multi-channel
VPN safeguard just because a single channel stalls repeatedly.

## BOSS recovery queue

`boss-resync.sh` acquires the **same flock file** as the site's regular refresh.
If busy, it waits up to 45 minutes. After acquiring it, it counts confirmed
404 events from the previous three minutes and after the latest successful
channel/cache refresh. At least six fresh 404 events are required to run the
site script in `--boss-only` mode. Otherwise it exits without another refresh.
Journal errors fail the operation instead of pretending the signal recovered.
The recovery command has a ten-minute timeout; allow at least 56 minutes in
the enclosing systemd unit for lock wait, command execution and termination.

Set these environment variables in the site's service:

| Variable | Purpose/default |
| --- | --- |
| `REFRESH_SCRIPT` | Required absolute path to the site's refresh script |
| `REFRESH_LOCK_FILE` | Same lock as regular refresh; default `$HOME/.cache/iptvrefresh.lock` |
| `LAST_CACHE_SUCCESS_FILE` | Atomic epoch-seconds marker; default `$HOME/.cache/iptvrefresh-last-cache-success` |
| `LOCK_WAIT_SECONDS` | 2700 |
| `REFRESH_TIMEOUT_SECONDS` | 600 |
| `LINEUP_404_WINDOW_SECONDS` | 180 |
| `LINEUP_404_THRESHOLD` | 6 |

The service user must be able to read the journal and the lock/marker directory.
The queue never deletes a lock. It passes its held descriptor 9 to the child as
`IPTV_REFRESH_LOCK_FD=9`. The site refresh script must validate and reuse it:

```bash
if [[ ${IPTV_REFRESH_LOCK_FD:-} != 9 || ! /proc/$$/fd/9 -ef "$LOCK_FILE" ]]; then
    exec 9>"$LOCK_FILE"
fi
if ! flock -n 9; then
    echo "Another refresh is already running"
    exit 0
fi
```

Both scheduled and recovery refreshes should update `LAST_CACHE_SUCCESS_FILE`
atomically with `date +%s` **only after** BOSS succeeded and the XC2EMBY cache
refresh returned a successful HTTP response. Keep the marker in the same
user-owned cache directory and use a same-directory temporary file plus rename.
All other calls continue acquiring the usual lock. Run `--boss-only` without
sudo or daemonizing the child so the lock remains held through completion.

The site's private refresh script, API keys, credentials, systemd units and VPN
configuration are deliberately not part of this public integration.

## Offline regression checks

```bash
bash -n contrib/monitoring/boss-resync.sh
bash -n contrib/monitoring/emby-iptv-stream-monitor.sh
python3 contrib/monitoring/test_monitoring.py -v
```

The tests use temporary locks and simulated journal events. No BOSS refresh,
VPN rotation, production journal write, or provider request is made.
