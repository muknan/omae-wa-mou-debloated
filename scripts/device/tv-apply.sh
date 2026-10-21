#!/system/bin/sh
# tv-apply.sh - re-applies tweaks the firmware forgets on cold boot and keeps background apps asleep.
#
# Runs as the ADB shell user from /data/local/tmp/tv-apply.sh. Called by:
#   - the Termux boot agent at every cold boot (scripts/termux/10-tv-agent.sh)
#   - tv-refresh.sh (e.g. from a Key Mapper button)
#   - you, any time:  adb shell sh /data/local/tmp/tv-apply.sh
#
# Settings come from the CONFIG block below, and /data/local/tmp/tv-apply.conf overrides them if it exists
# (the setup wizard writes that file, so re-running the wizard never needs you to edit this script).

# ============================== CONFIG ==============================
# Animation speed (1.0 = stock, 0.5 = twice as fast, 0 = off). Feels snappier; it doesn't make the CPU faster.
ANIM_SCALE=0.5

# 1 = every app YOU installed is treated as "background-blocked" automatically (new installs included).
INCLUDE_USER_APPS=1

# Preinstalled apps that are just streaming apps on your TV (system apps aren't covered by INCLUDE_USER_APPS).
PREINSTALLED_APPS="com.amazon.amazonvideo.livingroom com.netflix.ninja com.google.android.youtube.tv"

# Apps that should be stopped at boot/refresh but may run in the background once YOU start them
# (music players, VPNs). They get no permanent background block.
STOP_ONLY="com.spotify.tv.android com.surfshark.vpnclient.android"

# Apps never touched (besides the launcher, keyboard and accessibility apps, which are protected automatically).
# Keep the boot agent and adb-auto-enable here: force-stopping them breaks the next boot.
EXTRA_PROTECT="com.termux com.termux.boot com.tpn.adbautoenable"

# Xiaomi firmware only: it restores animation scales from this hidden key at boot. Harmless elsewhere.
XIAOMI_ANIM_KEY=1

# 1 = remove Netflix's power-management exemption (some firmwares whitelist it). 0 = leave it.
UNWHITELIST_NETFLIX=1

# Helper apps whose permanent notifications get hidden (they'd show up as a badge on the home screen).
# They're snoozed for 30 days, re-done every run and checked every minute by tv-daily.sh (apps re-post them when
# they restart, which undoes the snooze); the apps themselves keep working normally.
QUIET_APPS="io.github.sds100.keymapper com.tpn.adbautoenable"

# 1 = refresh once a day (like pressing the refresh button), from this hour on. Never while something plays.
DAILY_REFRESH=1
DAILY_REFRESH_HOUR=4

# 1 = start Key Mapper's Expert Mode if it isn't running (needed for Key Mapper "Execute with ADB" actions on
# TVs where its own auto-start can't pair). 0 = don't.
START_KEYMAPPER_EXPERT_MODE=1
# ====================================================================
CONF=/data/local/tmp/tv-apply.conf
# shellcheck source=/dev/null
[ -f "$CONF" ] && . "$CONF"

LOG=/data/local/tmp/tv-apply.log
log() { echo "$(date '+%F %T') $*" >> "$LOG"; }
[ -f "$LOG" ] && tail -n 300 "$LOG" > "$LOG.tmp" && mv "$LOG.tmp" "$LOG"

PROTECT="$EXTRA_PROTECT"
SDK=$(getprop ro.build.version.sdk)
HOME_APP=$(cmd role get-role-holders android.app.role.HOME 2>/dev/null)
[ -z "$HOME_APP" ] && HOME_APP=$(cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.HOME 2>/dev/null | tail -n 1 | sed 's/\/.*//')
PROTECT="$PROTECT $HOME_APP"
PROTECT="$PROTECT $(settings get secure default_input_method | sed 's/\/.*//')"
PROTECT="$PROTECT $(settings get secure enabled_accessibility_services | tr ':' '\n' | sed 's/\/.*//')"
# Launcher wallpaper plugins (e.g. Projectivy's Booru/Wallhaven provider) - the launcher calls them in the background.
PROTECT="$PROTECT $(cmd package query-services --brief -a tv.projectivy.plugin.WALLPAPER_PROVIDER 2>/dev/null | grep -oE '^ *[a-z0-9_.]+/' | tr -d ' /')"
# shellcheck disable=SC2086,SC2116  # deliberate: collapses newlines into single spaces for the matching below
PROTECT=$(echo $PROTECT)

listed() { case " $2 " in *" $1 "*) return 0;; esac; return 1; }

USER_APPS=""
[ "$INCLUDE_USER_APPS" = "1" ] && USER_APPS=$(pm list packages -3 | sed 's/package://')
BG_BLOCK=""
for p in $USER_APPS $PREINSTALLED_APPS; do
    pm path "$p" >/dev/null 2>&1 || continue
    listed "$p" "$PROTECT" && continue
    listed "$p" "$STOP_ONLY" && continue
    listed "$p" "$BG_BLOCK" && continue
    BG_BLOCK="$BG_BLOCK $p"
done

# shellcheck disable=SC2086  # deliberate word splitting: counts the words in $1
count() { set -- $1; echo $#; }
log "apply start (uptime $(cut -d' ' -f1 /proc/uptime)s, $(count "$BG_BLOCK") blocked + $(count "$STOP_ONLY") stop-only)"
# Remember which boot the start-up run belonged to (the wizard's status check compares it with the current one).
[ "$(cut -d. -f1 /proc/uptime)" -le 600 ] && cat /proc/sys/kernel/random/boot_id > /data/local/tmp/tv-apply.boot 2>/dev/null

for k in window_animation_scale transition_animation_scale animator_duration_scale; do
    settings put global "$k" "$ANIM_SCALE"
done
[ "$XIAOMI_ANIM_KEY" = "1" ] && settings put global mitv_fast_cur_anim_scale "$ANIM_SCALE"
[ "$UNWHITELIST_NETFLIX" = "1" ] && cmd deviceidle sys-whitelist -com.netflix.ninja >/dev/null 2>&1

# "restricted" standby bucket exists from Android 11; older versions get the strictest one they have.
BUCKET=restricted; [ "${SDK:-0}" -lt 30 ] && BUCKET=rare
if [ "${SDK:-0}" -ge 28 ]; then
    for p in $BG_BLOCK; do
        cmd appops set "$p" RUN_ANY_IN_BACKGROUND ignore
        am set-standby-bucket "$p" $BUCKET
    done
    for p in $STOP_ONLY; do
        pm path "$p" >/dev/null 2>&1 && am set-standby-bucket "$p" $BUCKET
    done
else
    log "Android older than 9: background limits not supported, only stopping apps"
fi

# Force-stop = fully asleep (no jobs, alarms or broadcasts) until opened. Never the app on screen.
TOP=$(dumpsys activity activities 2>/dev/null | grep -m1 -E 'topResumedActivity|mResumedActivity' | sed 's/.* u0 //; s/\/.*//')
# After start-up (refresh button, daily refresh), also leave apps alone that are busy: playing something, or
# running a foreground service (music in the background, a connected VPN). At boot everything listed is stopped.
BUSY=""
if [ "$(cut -d. -f1 /proc/uptime)" -gt 600 ]; then
    BUSY=$( { dumpsys activity services 2>/dev/null |
                awk '/\* ServiceRecord\{/ { s = $0; sub(/.* u0 /, "", s); sub(/\/.*/, "", s); pkg = s } /isForeground=true/ { print pkg }'
              dumpsys media_session 2>/dev/null |
                awk '/^ *package=/ { pkg = $0; sub(/.*package=/, "", pkg) } /state=PlaybackState \{state=(PLAYING|3[,)])/ { print pkg }'
            } | sort -u | tr '\n' ' ')
fi
for p in $BG_BLOCK $STOP_ONLY; do
    if [ "$p" = "$TOP" ] || ! pm path "$p" >/dev/null 2>&1; then continue; fi
    if listed "$p" "$BUSY"; then log "left $p running (busy: playing or a foreground service)"; continue; fi
    was=$(pidof "$p")
    am force-stop "$p"
    [ -n "$was" ] && log "force-stopped running $p"
done

# Key Mapper Expert Mode. Same steps as Key Mapper's own start.sh, but paths are read fresh (survives app
# updates), it runs under setsid (the starter kills its caller's session otherwise) and it never wipes
# /data/local/tmp (start.sh's fallback does).
KM=io.github.sds100.keymapper
if [ "$START_KEYMAPPER_EXPERT_MODE" = "1" ] && pm path $KM >/dev/null 2>&1 && ! ps -A -o NAME | grep -qx keymapper_sysbridge; then
    SRC=/storage/emulated/0/Android/data/$KM/starter
    DST=/data/local/tmp/keymapper_sysbridge_starter
    APK=$(pm path $KM | head -1 | sed 's/package://')
    VER=$(dumpsys package $KM 2>/dev/null | grep -m1 -oE 'versionCode=[0-9]+' | cut -d= -f2)
    ABI=$(getprop ro.product.cpu.abi | sed 's/armeabi-v7a/arm/; s/arm64-v8a/arm64/')
    if [ -f "$SRC" ] && cp "$SRC" "$DST" && chmod 700 "$DST"; then
        setsid "$DST" --apk="$APK" --lib="$(dirname "$APK")/lib/$ABI" --package="$KM" --version="$VER" </dev/null >/dev/null 2>&1
        sleep 2
        if ps -A -o NAME | grep -qx keymapper_sysbridge; then log "started Key Mapper Expert Mode"; else log "Key Mapper Expert Mode failed to start"; fi
    else
        log "Key Mapper Expert Mode: starter missing - open Key Mapper > Expert Mode once"
    fi
fi

# Daily refresh + re-hiding helper notifications: start the tiny background helper (tv-daily.sh) if it isn't
# running yet.
if { [ "$DAILY_REFRESH" = "1" ] || [ -n "$QUIET_APPS" ]; } && [ -f /data/local/tmp/tv-daily.sh ]; then
    dp=$(cat /data/local/tmp/tv-daily.pid 2>/dev/null)
    if [ -z "$dp" ] || ! grep -q tv-daily /proc/"$dp"/cmdline 2>/dev/null; then
        setsid nohup sh /data/local/tmp/tv-daily.sh </dev/null >/dev/null 2>&1 &
        if [ "$DAILY_REFRESH" = "1" ]; then log "started background helper (daily refresh from ${DAILY_REFRESH_HOUR}:00)"; else log "started background helper"; fi
    fi
fi

# Hide helper apps' permanent notifications (after Expert Mode starts, since Key Mapper posts when it connects).
for k in $(cmd notification list 2>/dev/null); do
    for p in $QUIET_APPS; do
        case "$k" in *"|$p|"*) cmd notification snooze --for 2592000000 "$k" >/dev/null 2>&1 && log "hid a notification from $p" ;; esac
    done
done

log "apply done (on screen: ${TOP:-none}; anim $(settings get global animator_duration_scale))"
