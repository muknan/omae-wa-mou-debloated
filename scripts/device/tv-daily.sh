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
#   - WIFI_IPV6_ROUTER_FIX: Google's remote config resets the two switches tv-apply.sh sets (about 10 minutes after
#     every boot, and now and then later); they're checked every minute for the first half hour after a boot and
#     every 10 minutes after that, and set again, so the Wi-Fi drop-outs they prevent don't come back in between.
#   - DAILY_REFRESH: on the first check at or after DAILY_REFRESH_HOUR each day it refreshes. While the TV is in
#     standby it is asleep too, so a 4:00 refresh happens within a minute of switching the TV on. It skips any
#     check where something is playing (music, video), so it never interrupts, and waits until the clock has been
#     set from the network (after a cold boot it starts at the firmware's build date).
#   - PANEL_60HZ_FIX: on MediaTek TVs (the Xiaomi test TV) a 25 fps video leaves the panel running at 50 Hz after
#     it ends, and the home screen judders (Android still draws for 60) until a restart. Between the minute checks
#     it looks every 2 seconds whether the home screen is back in front (a file read). When it is, it asks
#     SurfaceFlinger once, and if the panel is at 50 Hz it starts the wallpaper plugin's PanelResetActivity (a
#     silent 24 fps clip in two pixels, under a second), which puts it back to 60 Hz. At most two tries each time.
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
# Pauses without starting a command each time: `read -t` on the output of one sleeping co-process, which never
# writes. A stop signal (kill) interrupts the read, so the script ends right away instead of after the pause.
# Single quotes: $napper must be read when the signal comes, not now.
sleep 2147483 |&
napper=$!
exec 3<&p
trap 'kill "$napper" 2>/dev/null; rm -f $DIR/tv-daily.pid; exit 0' TERM INT
nap() {
    read -r u0 _ < /proc/uptime
    read -t "$1" -u3 _ 2>/dev/null
    read -r u1 _ < /proc/uptime
    # The co-process gone (after 24 days, or killed): read returns at once. Sleep instead, never a busy loop, and
    # start a new one.
    if [ $(( ${u1%%.*} - ${u0%%.*} + 1 )) -lt "$1" ]; then
        sleep "$1"
        sleep 2147483 |&
        napper=$!
        exec 3<&p
    fi
}

log() { echo "$(date '+%F %T') $*" >> $LOG; }
# The minute loop reads files with the shell's own `read` and avoids starting commands: on the test TV every
# external command (even `sleep 0`) costs ~25-30 ms of CPU, which added up to ~1 s a minute.
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

# Panel back to 60 Hz (PANEL_60HZ_FIX). Every 2 s: is the home screen in front (its process priority 0)? Only when it
# has just come back from behind something else does it ask SurfaceFlinger (~60 ms), 2 s later so the app before it
# has let go of its video. "VSYNC period" other than 16666666 ns with the display set to 60 Hz = the panel is stuck.
RESET=io.github.muknan.wallhavenshuffle/.PanelResetActivity
hpkg=; hpid=; away=0; due=; tries=0; quiet=0; reset_ok=
# Also stuck: Android drawing the whole screen below 60 fps ("activeMode=20.00 Hz (60.00 Hz)"), which a video's
# frame-rate request can leave behind; the reset clears that too. $why says which.
panel_stuck() {
    s=$(dumpsys SurfaceFlinger 2>/dev/null | grep -m3 -E 'VSYNC period|activeMode=|refresh-rate')
    case "$s" in *"VSYNC period:"*"refresh-rate"*"60.00 Hz"*) ;; *) return 1 ;; esac   # can't tell, or not 60 Hz
    why=
    case "$s" in *"VSYNC period:"*" 16666666 ns"*) ;; *) why="the panel at 50 Hz" ;; esac
    case "$s" in *"activeMode=60.00 Hz"*) ;; *"activeMode="*) why="${why:+$why and }the screen drawn below 60 fps" ;; esac
    [ -n "$why" ]
}
panel_check() {
    [ -n "$hpid" ] || return
    a=; read -r a 2>/dev/null < /proc/"$hpid"/oom_score_adj
    [ -n "$a" ] || { hpid=; return; }                             # home restarted: found again at the minute check
    read -r u _ < /proc/uptime; u=${u%%.*}
    if [ "$a" != 0 ]; then
        [ "$u" -ge "$quiet" ] && away=1                            # (not our own reset screen in front)
        return
    fi
    if [ "$away" = 1 ]; then away=0; due=$((u + 2)); tries=0; fi
    [ -n "$due" ] && [ "$u" -ge "$due" ] || return
    due=
    if ! panel_stuck; then
        [ "$tries" -gt 0 ] && log "panel at 60 Hz again"
        tries=0; return
    fi
    if [ "$tries" -ge 2 ]; then log "still $why after 2 resets; leaving it until the home screen comes back again"; return; fi
    [ "$tries" = 0 ] && log "home screen came back with $why (left by a video): resetting it to 60 Hz"
    am start -n "$RESET" >/dev/null 2>&1
    tries=$((tries + 1)); quiet=$((u + 8)); due=$((u + 6))        # look again once it has closed
}

PANEL_60HZ_FIX=1
POLL=2
ticks=0
while true; do
    waited=0
    while [ "$waited" -lt "$INTERVAL" ]; do
        step=$POLL; [ $((INTERVAL - waited)) -lt "$step" ] && step=$((INTERVAL - waited))
        nap "$step"; waited=$((waited + step))
        [ "$PANEL_60HZ_FIX" = "1" ] && [ -n "$reset_ok" ] && panel_check
    done
    ticks=$((ticks + 1))
    PANEL_60HZ_FIX=1
    DAILY_REFRESH=1
    DAILY_REFRESH_HOUR=4
    QUIET_APPS="io.github.sds100.keymapper com.tpn.adbautoenable"
    IDLE_TRIM_MINUTES=10
    WIFI_IPV6_ROUTER_FIX=1
    # shellcheck source=/dev/null
    [ -f $DIR/tv-apply.conf ] && . $DIR/tv-apply.conf
    [ -f $DIR/tv-refresh.sh ] || DAILY_REFRESH=0
    read -r up _ < /proc/uptime; now=${up%%.*}                  # seconds since boot
    # Panel fix: the plugin's reset screen installed? (at the start, then hourly) Which process is the home screen?
    if [ "$PANEL_60HZ_FIX" = "1" ]; then
        if [ "$ticks" = 1 ] || [ $((ticks % 60)) -eq 0 ]; then
            reset_ok=; cmd package resolve-activity --brief --components -n "$RESET" 2>/dev/null | grep -q PanelResetActivity && reset_ok=1
        fi
        if [ -n "$reset_ok" ] && [ -z "$hpid" ]; then
            [ -n "$hpkg" ] || hpkg=$(cmd role get-role-holders android.app.role.HOME 2>/dev/null)
            hpid=; [ -n "$hpkg" ] && hpid=$(pidof "$hpkg" 2>/dev/null); hpid=${hpid%% *}
        fi
    fi
    # Keep the volume hold-to-repeat helper running (it can end, e.g. if killed to free memory).
    if [ "${VOLUME_HOLD_REPEAT:-1}" = "1" ] && [ -f $DIR/tv-volhold.sh ]; then
        vp=; vc=; read -r vp 2>/dev/null < $DIR/tv-volhold.pid
        [ -n "$vp" ] && read -r vc 2>/dev/null < /proc/"$vp"/cmdline
        case "$vc" in
            *tv-volhold*) ;;
            *)  un=; read -r un 2>/dev/null < $DIR/tv-volhold.unsupported
                if [ "$un" != "$(getprop ro.build.fingerprint)" ]; then
                    setsid nohup sh $DIR/tv-volhold.sh </dev/null >/dev/null 2>&1 &
                    log "restarted volume hold-to-repeat helper"
                fi ;;
        esac
    fi
    if [ "$DAILY_REFRESH" != "1" ] && [ -z "$QUIET_APPS" ] && [ "${IDLE_TRIM_MINUTES:-0}" -le 0 ] &&
        [ "$WIFI_IPV6_ROUTER_FIX" != "1" ] && [ "${VOLUME_HOLD_REPEAT:-1}" != "1" ] && [ "$PANEL_60HZ_FIX" != "1" ]; then
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

    # Every minute for the first half hour after a boot (Google's config resets them ~10 minutes in), then every 10.
    if [ "$WIFI_IPV6_ROUTER_FIX" = "1" ] && { [ "$now" -lt 1800 ] || [ $((ticks % 10)) -eq 0 ]; }; then
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
        # One pidof for all of them (empty, the usual case, when none runs); names from /proc, as " package=pid ...".
        blocked=; read -r blocked < $DIR/tv-apply.blocked
        running=" "
        for pid in $(pidof $blocked 2>/dev/null); do
            n=; read -r n 2>/dev/null < /proc/"$pid"/cmdline        # (no newline at the end: read says "end of file" but has the name)
            [ -n "$n" ] && running="$running$n=$pid "
        done
        for f in "$IDLE"/*; do                                    # forget the ones that stopped
            [ -f "$f" ] || continue
            case "$running" in *" ${f##*/}="*) ;; *) rm -f "$f" ;; esac
        done
        for entry in $running; do
            p=${entry%%=*}; pid=${entry#*=}
            adj=; read -r adj 2>/dev/null < /proc/"$pid"/oom_score_adj
            case "$adj" in ''|*[!0-9-]*) continue ;; esac
            if [ "$adj" -lt 700 ]; then [ -f "$IDLE/$p" ] && rm -f "$IDLE/$p"; continue; fi
            [ -f "$IDLE/$p" ] || { echo "$now" > "$IDLE/$p"; continue; }
            since=; read -r since 2>/dev/null < "$IDLE/$p"
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
    set -- $(date '+%s %F %H')                                   # one date call: seconds, day, hour
    [ "$1" -gt $((${BUILT:-0} + 172800)) ] || continue           # the clock isn't set yet (it starts at the build date)
    today=$2; hour=${3#0}
    last=; read -r last 2>/dev/null < $STAMP
    [ "$last" = "$today" ] && continue
    [ "${hour:-0}" -lt "$DAILY_REFRESH_HOUR" ] && continue
    if dumpsys media_session 2>/dev/null | grep -qE 'state=PlaybackState \{state=(PLAYING|3[,)])'; then
        continue   # something is playing: try again at the next check
    fi
    echo "$today" > $STAMP
    log "daily refresh"
    sh $DIR/tv-refresh.sh
done
