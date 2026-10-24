#!/system/bin/sh
# tv-daily.sh - the small background helper: runs tv-refresh.sh once a day (like pressing the refresh button
# yourself), puts streaming apps left in the background to sleep, and keeps helper apps' notifications hidden.
#
# Started by tv-apply.sh as a tiny background shell process, the same way Key Mapper's Expert Mode runs.
# It wakes every minute:
#   - IDLE_TRIM_MINUTES: a background-blocked app (the list tv-apply.sh wrote to tv-apply.blocked) that has sat in
#     the background that long is force-stopped, as at boot. Never the app on screen, never one playing or running
#     a foreground service. Two streaming apps left in memory take ~300 MB of a 2 GB TV, and the launcher then
#     stutters while the system swaps.
#   - QUIET_APPS: helper apps re-post their permanent notifications whenever they restart or you pause/resume
#     key maps, which undoes the snooze. Any that came back are hidden again, so a badge lasts a minute at most.
#   - WIFI_IPV6_ROUTER_FIX: Google's remote config can reset the two switches tv-apply.sh sets; every 10 minutes
#     they're checked and set again, so the Wi-Fi drop-outs they prevent don't come back in between.
#   - DAILY_REFRESH: on the first check at or after DAILY_REFRESH_HOUR each day it refreshes. While the TV is in
#     standby it is asleep too, so a 4:00 refresh happens within a minute of switching the TV on. It skips any
#     check where something is playing (music, video), so it never interrupts, and waits until the clock has been
#     set from the network (after a cold boot it starts at the firmware's build date).
# Settings are re-read from tv-apply.conf at every check; with everything switched off it exits by itself.
DIR=/data/local/tmp
LOG=$DIR/tv-apply.log
STAMP=$DIR/tv-daily.last
IDLE=$DIR/tv-daily.idle          # one file per app in the background: the uptime when it was first seen there
INTERVAL=${DAILY_INTERVAL:-60}
BUILT=$(getprop ro.build.date.utc)

# One instance only: if another live copy owns the pid file, leave.
old=$(cat $DIR/tv-daily.pid 2>/dev/null)
if [ -n "$old" ] && [ "$old" != "$$" ] && grep -q tv-daily /proc/"$old"/cmdline 2>/dev/null; then
    exit 0
fi
echo $$ > $DIR/tv-daily.pid
mkdir -p $IDLE
# Sleep in the background and wait for it, so a stop signal (kill) ends the script right away instead of after
# the current sleep.
trap 'kill "$napper" 2>/dev/null; rm -f $DIR/tv-daily.pid; exit 0' TERM INT

log() { echo "$(date '+%F %T') $*" >> $LOG; }
uptime_s() { cut -d. -f1 /proc/uptime; }
# The app on screen (its package).
top_app() { dumpsys activity activities 2>/dev/null | grep -m1 -E 'topResumedActivity|mResumedActivity' | sed 's/.* u0 //; s/\/.*//'; }
# Apps busy in the background: playing something, or running a foreground service (music, a connected VPN).
busy_apps() {
    { dumpsys activity services 2>/dev/null |
        awk '/\* ServiceRecord\{/ { s = $0; sub(/.* u0 /, "", s); sub(/\/.*/, "", s); pkg = s } /isForeground=true/ { print pkg }'
      dumpsys media_session 2>/dev/null |
        awk '/^ *package=/ { pkg = $0; sub(/.*package=/, "", pkg) } /state=PlaybackState \{state=(PLAYING|3[,)])/ { print pkg }'
    } | sort -u | tr '\n' ' '
}

ticks=0
while true; do
    sleep "$INTERVAL" &
    napper=$!
    wait "$napper"
    ticks=$((ticks + 1))
    DAILY_REFRESH=1
    DAILY_REFRESH_HOUR=4
    QUIET_APPS="io.github.sds100.keymapper com.tpn.adbautoenable"
    IDLE_TRIM_MINUTES=10
    WIFI_IPV6_ROUTER_FIX=1
    # shellcheck source=/dev/null
    [ -f $DIR/tv-apply.conf ] && . $DIR/tv-apply.conf
    [ -f $DIR/tv-refresh.sh ] || DAILY_REFRESH=0
    # Keep the volume hold-to-repeat helper running (it can end, e.g. if killed to free memory).
    if [ "${VOLUME_HOLD_REPEAT:-1}" = "1" ] && [ -f $DIR/tv-volhold.sh ]; then
        vp=$(cat $DIR/tv-volhold.pid 2>/dev/null)
        if ! grep -q tv-volhold /proc/"$vp"/cmdline 2>/dev/null &&
            [ "$(cat $DIR/tv-volhold.unsupported 2>/dev/null)" != "$(getprop ro.build.fingerprint)" ]; then
            setsid nohup sh $DIR/tv-volhold.sh </dev/null >/dev/null 2>&1 &
            log "restarted volume hold-to-repeat helper"
        fi
    fi
    if [ "$DAILY_REFRESH" != "1" ] && [ -z "$QUIET_APPS" ] && [ "${IDLE_TRIM_MINUTES:-0}" -le 0 ]; then
        log "background helper stopped (switched off or removed)"
        rm -f $DIR/tv-daily.pid
        exit 0
    fi

    if [ -n "$QUIET_APPS" ]; then
        for k in $(cmd notification list 2>/dev/null); do
            for p in $QUIET_APPS; do
                case "$k" in *"|$p|"*) cmd notification snooze --for 2592000000 "$k" >/dev/null 2>&1 ;; esac
            done
        done
    fi

    if [ "$WIFI_IPV6_ROUTER_FIX" = "1" ] && [ $((ticks % 10)) -eq 0 ]; then
        for k in ip_reachability_ignore_incompleted_ipv6_default_router_version ip_reachability_ignore_incompleted_ipv6_dns_server_version; do
            v=$(device_config get connectivity $k 2>/dev/null)
            [ -z "$v" ] || [ "$v" = "1" ] || { device_config put connectivity $k 1 >/dev/null 2>&1 && log "Wi-Fi IPv6 router fix: set $k again (was $v)"; }
        done
    fi

    # Idle trim, by Android's own priority for the app's process (oom_score_adj, a file read: no dumpsys every
    # minute). Below 700 it's in use: on screen 0, visible 100, playing or a foreground service 200, a running
    # service 500. 700 is "the app you were in last" and 900+ "cached": unused. An app is put to sleep after
    # IDLE_TRIM_MINUTES unused in a row; the moment it's in use again the count starts over. Before stopping it,
    # one last check that nothing of it is on screen or playing.
    if [ "${IDLE_TRIM_MINUTES:-0}" -gt 0 ] && [ -f $DIR/tv-apply.blocked ]; then
        now=$(uptime_s)
        for p in $(cat $DIR/tv-apply.blocked); do
            pid=$(pidof "$p") || { rm -f "$IDLE/$p"; continue; }
            adj=$(cat /proc/"${pid%% *}"/oom_score_adj 2>/dev/null)
            case "$adj" in ''|*[!0-9-]*) continue ;; esac
            if [ "$adj" -lt 700 ]; then rm -f "$IDLE/$p"; continue; fi
            [ -f "$IDLE/$p" ] || { echo "$now" > "$IDLE/$p"; continue; }
            since=$(cat "$IDLE/$p" 2>/dev/null)
            case "$since" in ''|*[!0-9]*) echo "$now" > "$IDLE/$p"; continue ;; esac
            [ $((now - since)) -ge $((IDLE_TRIM_MINUTES * 60)) ] || continue
            [ "$(top_app)" = "$p" ] && { rm -f "$IDLE/$p"; continue; }
            case " $(busy_apps) " in *" $p "*) rm -f "$IDLE/$p"; continue ;; esac
            am force-stop "$p"
            rm -f "$IDLE/$p"
            log "put $p to sleep (unused in the background for ${IDLE_TRIM_MINUTES} min; MemAvailable now $(awk '/MemAvailable/ {print $2}' /proc/meminfo) kB)"
        done
    fi

    [ "$DAILY_REFRESH" = "1" ] || continue
    [ "$(date +%s)" -gt $((${BUILT:-0} + 172800)) ] || continue   # the clock isn't set yet (it starts at the build date)
    today=$(date +%F)
    hour=$(date +%H); hour=${hour#0}
    [ "$(cat $STAMP 2>/dev/null)" = "$today" ] && continue
    [ "${hour:-0}" -lt "$DAILY_REFRESH_HOUR" ] && continue
    if dumpsys media_session 2>/dev/null | grep -qE 'state=PlaybackState \{state=(PLAYING|3[,)])'; then
        continue   # something is playing: try again at the next check
    fi
    echo "$today" > $STAMP
    log "daily refresh"
    sh $DIR/tv-refresh.sh
done
