#!/system/bin/sh
# tv-keys.sh - two input helpers, both read-only listeners (nothing is intercepted):
#
# 1. The remote's Live TV button (Xiaomi: "TV+") opens the input you chose, e.g. HDMI 1 (LIVE_TV_BUTTON_INPUT).
#    On Xiaomi TVs the firmware's key policy catches this button (keycode 352, KEY_MEMO on the remote) before any app,
#    accessibility services included, so Key Mapper can't record or remap it. The firmware then opens Mi TV+ (PatchWall
#    on) or Live TV on the last input used (PatchWall off). This helper listens with getevent. When the button is
#    released it waits until the firmware's Live TV screen is in front, then hands it the input the standard Android
#    way: a passthrough-input link. Live TV takes that as an update to the screen already open: it switches input in
#    place if it opened another one, and does nothing (no re-tune, no flicker) if it's already on it. Starting a
#    second, fresh Live TV screen instead made Live TV close both: a black flash (tested on the Xiaomi TV).
#
# 2. A console on an HDMI input takes over the screen when it turns on (AUTO_SWITCH_INPUT), without HDMI-CEC.
#    With CEC on, the Xiaomi test TV's speakers started popping after a while and its volume dropped until a restart,
#    and CEC also couples power (TV on wakes the console, and so on). Without CEC the TV still notices a device
#    turning on or off: the HDMI cable's 5 V line comes and goes, and Android logs "Hotplug event:[port:1,
#    connected:true]" (system log, tag HDMI). Xiaomi's own "new source" switch sees it too but gives up when another
#    launcher (Projectivy) is the home screen. This helper reads that log line (logcat, ~0.01 s of CPU a minute) and:
#      - turned on, the TV awake and the home screen in front: opens the input (the same passthrough link);
#      - turned off, the TV awake and Live TV in front on that input: back to the home screen (a Home key press).
#    Anything else in front (an app, another input) or the TV asleep: it leaves the screen alone. It never wakes the
#    TV, never sends anything to the console and never turns anything off.
#
# LIVE_TV_BUTTON_INPUT / AUTO_SWITCH_INPUT in tv-apply.conf: the input's name in the TV's input list ("HDMI 1") or its
# id ("com.mediatek.tis/.HdmiInputService/HW2"); empty = off. LIVE_TV_KEY: the button's name in `getevent -l` (default
# KEY_MEMO). Started by tv-apply.sh, kept running by tv-daily.sh; both empty stops it.
DIR=/data/local/tmp
LOG=$DIR/tv-apply.log
LIVE_TV_BUTTON_INPUT=
LIVE_TV_KEY=KEY_MEMO
AUTO_SWITCH_INPUT=
# shellcheck source=/dev/null
[ -f $DIR/tv-apply.conf ] && . $DIR/tv-apply.conf
[ -n "$LIVE_TV_BUTTON_INPUT$AUTO_SWITCH_INPUT" ] || exit 0

# One instance only.
old=$(cat $DIR/tv-keys.pid 2>/dev/null)
if [ -n "$old" ] && [ "$old" != "$$" ] && grep -q tv-keys /proc/"$old"/cmdline 2>/dev/null; then
    exit 0
fi
echo $$ > $DIR/tv-keys.pid

# Stopping this shell (kill <pid>, as tv-apply.sh and the wizard do) stops both listeners too: everything here runs in
# this shell's own process group (it's started with setsid), so the whole group goes. Started some other way, it
# stops its own children only.
trap 'trap - TERM INT; if [ "$(cut -d" " -f5 /proc/$$/stat)" = "$$" ]; then kill -TERM 0; else pkill -P $$; fi; exit 0' TERM INT

log() { echo "$(date '+%F %T') $*" >> $LOG; }
# An input's id: as given, or looked up by its name in the TV's input list.
input_id() {
    case "$1" in
        */*) echo "$1" ;;
        *)   dumpsys tv_input 2>/dev/null | sed -n "s/.*TvInputInfo{id=\([^,]*\),.* mLabel=$1,.*/\1/p" | head -1 ;;
    esac
}
link() { echo "content://android.media.tv/passthrough/$(echo "$1" | sed 's#/#%2F#g')"; }
# The package in front.
top() { dumpsys activity activities 2>/dev/null | grep -m1 -E 'topResumedActivity|mResumedActivity' | sed 's/.* u0 //; s#/.*##'; }
# The app that opens passthrough links: the TV's Live TV app. (The shell can't read a passthrough link's type, so it
# asks by the type Live TV apps register for.)
live_app() {
    cmd package resolve-activity --brief -a android.intent.action.VIEW -d "$1" -t vnd.android.cursor.item/channel 2>/dev/null | tail -n 1 | grep / | sed 's#/.*##'
}
started=

# 1. The Live TV button.
if [ -n "$LIVE_TV_BUTTON_INPUT" ]; then
    bid=$(input_id "$LIVE_TV_BUTTON_INPUT")
    if [ -z "$bid" ]; then
        log "Live TV button: no input called '$LIVE_TV_BUTTON_INPUT' on this TV; not running"
    else
        buri=$(link "$bid")
        blive=$(live_app "$buri")
        [ -n "$blive" ] || log "Live TV button: couldn't find the Live TV app; switching without waiting for it"
        # getevent ends when an input device goes away (the Bluetooth remote disconnects when the TV sleeps), so listen
        # again whenever it does.
        (
            while true; do
                getevent -lq 2>/dev/null | while read -r _ type key state; do
                    [ "$type" = EV_KEY ] && [ "$key" = "$LIVE_TV_KEY" ] && [ "$state" = UP ] || continue
                    # Wait (up to 3 s) for the firmware's own Live TV screen, then give it the input as an update.
                    n=0
                    while [ $n -lt 15 ] && [ -n "$blive" ] && [ "$(top)" != "$blive" ]; do sleep 0.2; n=$((n + 1)); done
                    am start -a android.intent.action.VIEW -d "$buri" >/dev/null 2>&1
                done
                sleep 1
            done
        ) &
        started=1
    fi
fi

# 2. Switching to an HDMI device when it turns on, and back to the home screen when it turns off.
if [ -n "$AUTO_SWITCH_INPUT" ]; then
    aid=$(input_id "$AUTO_SWITCH_INPUT")
    # Its HDMI port: input id -> hardware device id (mHardwareInputIdMap) -> hdmi_port of that device.
    adev=; aport=
    [ -n "$aid" ] && adev=$(dumpsys tv_input 2>/dev/null | sed -n "s#^ *\([0-9]*\): $aid\$#\1#p" | head -1)
    [ -n "$adev" ] && aport=$(dumpsys tv_input 2>/dev/null | sed -n "s/.*TvInputHardwareInfo {id=$adev,.* hdmi_port=\([0-9]*\).*/\1/p" | head -1)
    if [ -z "$aport" ]; then
        log "Auto switch: no HDMI input called '$AUTO_SWITCH_INPUT' on this TV; not running"
    else
        auri=$(link "$aid")
        alapp=$(live_app "$auri")
        home=$(cmd role get-role-holders android.app.role.HOME 2>/dev/null)
        name=$AUTO_SWITCH_INPUT; case "$name" in */*) name="HDMI $aport" ;; esac
        awake() { dumpsys power 2>/dev/null | grep -q 'mWakefulness=Awake'; }
        (
            while true; do
                # From now on only: older lines would replay events that are long over.
                logcat -b system -v brief -s HDMI:D -T "$(date '+%m-%d %H:%M:%S.000')" 2>/dev/null | while read -r line; do
                    case "$line" in
                        *"Hotplug event:[port:$aport, connected:true]"*)
                            if ! awake; then
                                log "$name turned on; the TV is asleep, left it"
                            elif [ "$(top)" = "$home" ]; then
                                am start -a android.intent.action.VIEW -d "$auri" >/dev/null 2>&1
                                log "$name turned on: switched to it"
                            else
                                log "$name turned on; left the screen as it was ($(top) in front)"
                            fi ;;
                        *"Hotplug event:[port:$aport, connected:false]"*)
                            # At once: the screen already says "no signal". (Waiting 2 s to rule out a 5 V blip added 2 s
                            # of that, 2026-11-20; a blip comes back as "connected:true" with the home screen in front,
                            # which switches straight back.)
                            # On it = Live TV in front, showing this input (Xiaomi says which in TvSourcesStatus: "hdmi1|SDR").
                            on=; [ "$(top)" = "$alapp" ] && on=1
                            case "$(settings get global TvSourcesStatus 2>/dev/null)" in hdmi"$aport"\|*|null|'') ;; *) on= ;; esac
                            if ! awake; then
                                log "$name turned off; the TV is asleep, left it"
                            elif [ -n "$on" ]; then
                                input keyevent KEYCODE_HOME
                                log "$name turned off: back to the home screen"
                            else
                                log "$name turned off; left the screen as it was ($(top) in front)"
                            fi ;;
                    esac
                done
                sleep 5
            done
        ) &
        started=1
        log "Auto switch: listening for $name (HDMI port $aport)"
    fi
fi

[ -n "$started" ] && wait
rm -f $DIR/tv-keys.pid
