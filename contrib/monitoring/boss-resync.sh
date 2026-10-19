#!/usr/bin/env bash
# Optional site integration: queue a lineup recovery behind the normal refresh
# lock, then check whether another BOSS + XC2EMBY cache refresh is still needed.
set -euo pipefail

: "${REFRESH_SCRIPT:?Set REFRESH_SCRIPT to the local BOSS/cache refresh script}"
: "${REFRESH_LOCK_FILE:=$HOME/.cache/iptvrefresh.lock}"
: "${LAST_CACHE_SUCCESS_FILE:=$HOME/.cache/iptvrefresh-last-cache-success}"
: "${LOCK_WAIT_SECONDS:=2700}"
: "${REFRESH_TIMEOUT_SECONDS:=600}"
: "${LINEUP_404_WINDOW_SECONDS:=180}"
: "${LINEUP_404_THRESHOLD:=6}"

[[ -f "$REFRESH_SCRIPT" && "$REFRESH_SCRIPT" == /* ]] || {
    echo 'REFRESH_SCRIPT must name an existing absolute path' >&2
    exit 1
}
for value in "$LOCK_WAIT_SECONDS" "$REFRESH_TIMEOUT_SECONDS" "$LINEUP_404_WINDOW_SECONDS" "$LINEUP_404_THRESHOLD"; do
    [[ "$value" =~ ^[0-9]+$ && "$value" -gt 0 ]] || {
        echo 'Recovery timeouts and thresholds must be positive integers' >&2
        exit 1
    }
done

exec 9>"$REFRESH_LOCK_FILE"
if ! flock -n 9; then
    echo 'Another IPTV refresh holds the lock; waiting to recheck recent 404 failures'
    if ! flock -w "$LOCK_WAIT_SECONDS" 9; then
        echo 'Timed out waiting for the running refresh; no lock was removed' >&2
        exit 75
    fi
fi

now=$(date +%s)
since=$((now - LINEUP_404_WINDOW_SECONDS))
last_cache=0
if [[ -r "$LAST_CACHE_SUCCESS_FILE" ]]; then
    read -r last_cache < "$LAST_CACHE_SUCCESS_FILE" || true
fi
[[ "$last_cache" =~ ^[0-9]+$ ]] || last_cache=0
# A future marker indicates a clock change; do not suppress new failures.
if (( last_cache <= now && last_cache > since )); then
    since=$last_cache
fi

# Count only actual recent 404 events, excluding anything already covered by
# the successful cache refresh that just released the lock. Journal failures
# are errors, not evidence that the channel has recovered.
events=$(journalctl --identifier=emby-iptv-lineup-monitor \
    --since "@$since" --no-pager --output=json)
count=$(jq -s --argjson since "$since" '
    [.[] | select(
        ((.__REALTIME_TIMESTAMP | tonumber) > ($since * 1000000)) and
        ((.MESSAGE // "") | test("^lineup-404 stream=xtream_live_[0-9]+ reason=http-404$"))
    )] | length
' <<<"$events")

if (( count < LINEUP_404_THRESHOLD )); then
    echo "No additional refresh needed: $count recent 404s since the last successful cache refresh (threshold $LINEUP_404_THRESHOLD)"
    exit 0
fi

echo "404 failures persist after acquiring the refresh lock ($count events); refreshing BOSS and XC2EMBY cache"
# The local script must reuse descriptor 9 (rather than reopening the lock)
# when IPTV_REFRESH_LOCK_FD=9, and still verify it with flock. This maintains
# one lock continuously from the decision through the completed refresh.
export IPTV_REFRESH_LOCK_FD=9
exec timeout --signal=TERM --kill-after=15 "$REFRESH_TIMEOUT_SECONDS" bash "$REFRESH_SCRIPT" --boss-only
