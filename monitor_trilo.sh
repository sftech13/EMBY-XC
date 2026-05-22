#!/bin/bash
# Monitor active connections from this machine to trilo.tv upstream.
# Logs count + details every INTERVAL seconds. Designed to diagnose
# evening 403 errors caused by saturating the 5-connection provider limit.
#
# Run as a service: sudo systemctl start trilo-monitor
# View live:        tail -f /var/log/trilo_connections.log

LOG_FILE="/var/log/trilo_connections.log"
INTERVAL=30
TRILO_HOST="trilo.tv"
WARN_THRESHOLD=3   # flag when >= this many connections (limit is 5)

log() { echo "$1" >> "$LOG_FILE"; }

log "$(date '+%Y-%m-%d %H:%M:%S') | ===== Monitor started (interval=${INTERVAL}s, warn>=${WARN_THRESHOLD}) ====="

while true; do
    TIMESTAMP=$(date '+%Y-%m-%d %H:%M:%S')

    # Re-resolve each cycle in case IP changes (Cloudflare can rotate)
    TRILO_IPS=$(getent ahosts "$TRILO_HOST" 2>/dev/null | awk '{print $1}' | sort -u)

    if [ -z "$TRILO_IPS" ]; then
        log "$TIMESTAMP | DNS_FAIL: could not resolve $TRILO_HOST"
        sleep "$INTERVAL"
        continue
    fi

    IP_PATTERN=$(printf '%s|' $TRILO_IPS | sed 's/|$//')

    # Established TCP connections to trilo.tv (all household devices via IPTVBoss proxy)
    CONNS=$(ss -tn state established 2>/dev/null | grep -E "($IP_PATTERN)" || true)
    COUNT=$([ -n "$CONNS" ] && echo "$CONNS" | wc -l || echo 0)

    if [ "$COUNT" -ge "$WARN_THRESHOLD" ]; then
        log "$TIMESTAMP | count=$COUNT/5  <<< NEAR LIMIT"
        echo "$CONNS" | awk '{printf "  %s\n", $0}' >> "$LOG_FILE"
    else
        log "$TIMESTAMP | count=$COUNT/5"
    fi

    sleep "$INTERVAL"
done
