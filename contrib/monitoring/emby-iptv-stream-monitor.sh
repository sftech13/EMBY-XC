#!/usr/bin/env bash
set -uo pipefail

EMBY_LOG=${EMBY_LOG:-/var/lib/emby/logs/embyserver.txt}
JOURNAL_TAG=emby-iptv-stream-monitor
LINEUP_JOURNAL_TAG=emby-iptv-lineup-monitor
RETRY_SECONDS=3
TEST_MODE=0

emit_event() {
    local stream_id=$1
    local reason=$2

    if (( TEST_MODE == 1 )); then
        printf 'stream-failure stream=%s reason=%s\n' "$stream_id" "$reason"
    else
        /usr/bin/logger -t "$JOURNAL_TAG" -- \
            "stream-failure stream=$stream_id reason=$reason"
    fi
}

emit_lineup_event() {
    local stream_id=$1

    if (( TEST_MODE == 1 )); then
        printf 'lineup-404 stream=%s reason=http-404\n' "$stream_id"
    else
        /usr/bin/logger -t "$LINEUP_JOURNAL_TAG" -- \
            "lineup-404 stream=$stream_id reason=http-404"
    fi
}

process_line() {
    local line=$1
    local stream_id
    local reason=""

    # Forward transport failures and stale-lineup failures to separate journal
    # tags. HTTP 404 must never enter the VPN-rotation signal: it instead feeds
    # the lightweight BOSS-resync detector.
    [[ "$line" == *'XtreamTunerHost: [live-fanout] Upstream interrupted for stream xtream_live_'* ]] || return 0

    stream_id=$(
        /usr/bin/sed -nE \
            's/.*stream (xtream_live_[0-9]+):.*/\1/p' \
            <<<"$line"
    )
    [[ "$stream_id" =~ ^xtream_live_[0-9]+$ ]] || return 0

    case "$line" in
        *'Xtream live upstream read stalled: reason=read-stall '*' activeViewers='*)
            # Only the plugin's explicit watchdog classification qualifies.
            # Generic cancellations and stalls with no viewers stay ignored.
            [[ "$line" =~ activeViewers=([1-9][0-9]*)([\;[:space:]]|$) ]] || return 0
            reason=read-stall
            ;;
        *'Response status code does not indicate success: 404'*)
            emit_lineup_event "$stream_id"
            return 0
            ;;
        *'Xtream live source ended unexpectedly'*)
            reason=eof
            ;;
        *'The SSL connection could not be established'*)
            reason=tls
            ;;
        *'HttpClient.Timeout of '*" seconds elapsing"*)
            reason=timeout
            ;;
        *'Response status code does not indicate success: 5'*)
            reason=http-5xx
            ;;
        *)
            return 0
            ;;
    esac


    emit_event "$stream_id" "$reason"
}

read_lines() {
    local line
    while IFS= read -r line; do
        process_line "$line"
    done
}

if [[ ${1:-} == "--test-input" ]]; then
    [[ $# -eq 2 && -r $2 ]] || {
        echo "Usage: $0 --test-input FILE" >&2
        exit 2
    }
    TEST_MODE=1
    read_lines < "$2"
    exit 0
fi

if [[ $# -ne 0 ]]; then
    echo "Usage: $0 [--test-input FILE]" >&2
    exit 2
fi

/usr/bin/logger -t "$JOURNAL_TAG" -- "watching $EMBY_LOG"

while true; do
    if [[ ! -r "$EMBY_LOG" ]]; then
        /usr/bin/logger -t "$JOURNAL_TAG" -- \
            "Emby log is unavailable; retrying in ${RETRY_SECONDS}s"
        /usr/bin/sleep "$RETRY_SECONDS"
        continue
    fi

    # Start at EOF so service installation never replays old incidents.
    # --follow=name and --retry survive Emby's daily log replacement.
    /usr/bin/tail --lines=0 --follow=name --retry "$EMBY_LOG" | read_lines
    /usr/bin/logger -t "$JOURNAL_TAG" -- \
        "Emby log follower exited; restarting in ${RETRY_SECONDS}s"
    /usr/bin/sleep "$RETRY_SECONDS"
done
