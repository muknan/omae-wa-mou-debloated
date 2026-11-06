#!/system/bin/sh
# tv-keys.sh - the remote's Live TV button (Xiaomi: "TV+") opens the input you chose, e.g. HDMI 1.
#
# On Xiaomi TVs the firmware's key policy catches this button (keycode 352, KEY_MEMO on the remote) before any app,
# accessibility services included, so Key Mapper can't record or remap it. The firmware then opens Mi TV+ (PatchWall
# on) or Live TV on the last input used (PatchWall off). This helper only *listens* (getevent, read-only: nothing is
# intercepted). When the button is released it waits until the firmware's Live TV screen is in front, then hands it
# LIVE_TV_BUTTON_INPUT the standard Android way: a passthrough-input link. Live TV takes that as an update to the
# screen already open: it switches input in place if it opened another one, and does nothing (no re-tune, no
# flicker) if it's already on it. Starting a second, fresh Live TV screen instead made Live TV close both: a black
# flash (tested on the Xiaomi TV). Once the input has been used, it's also the firmware's own "last input", so most
# presses need no switch at all.
#
# LIVE_TV_BUTTON_INPUT in tv-apply.conf: the input's name in the TV's input list ("HDMI 1") or its id
# ("com.mediatek.tis/.HdmiInputService/HW2"). LIVE_TV_KEY: the button's name in `getevent -l` (default KEY_MEMO).
# Started by tv-apply.sh, kept running by tv-daily.sh; LIVE_TV_BUTTON_INPUT="" stops it.
DIR=/data/local/tmp
LOG=$DIR/tv-apply.log
LIVE_TV_BUTTON_INPUT=
LIVE_TV_KEY=KEY_MEMO
# shellcheck source=/dev/null
[ -f $DIR/tv-apply.conf ] && . $DIR/tv-apply.conf
[ -n "$LIVE_TV_BUTTON_INPUT" ] || exit 0

# One instance only.
old=$(cat $DIR/tv-keys.pid 2>/dev/null)
if [ -n "$old" ] && [ "$old" != "$$" ] && grep -q tv-keys /proc/"$old"/cmdline 2>/dev/null; then
    exit 0
fi
echo $$ > $DIR/tv-keys.pid

log() { echo "$(date '+%F %T') $*" >> $LOG; }
# The input's id: as given, or looked up by its name in the TV's input list.
input_id() {
    case "$LIVE_TV_BUTTON_INPUT" in
        */*) echo "$LIVE_TV_BUTTON_INPUT" ;;
        *)   dumpsys tv_input 2>/dev/null | sed -n "s/.*TvInputInfo{id=\([^,]*\),.* mLabel=$LIVE_TV_BUTTON_INPUT,.*/\1/p" | head -1 ;;
    esac
}
id=$(input_id)
if [ -z "$id" ]; then
    log "Live TV button: no input called '$LIVE_TV_BUTTON_INPUT' on this TV; not running"
    rm -f $DIR/tv-keys.pid
    exit 0
fi
uri="content://android.media.tv/passthrough/$(echo "$id" | sed 's#/#%2F#g')"
# The app that opens such links: the TV's Live TV app. (The shell can't read a passthrough link's type, so it asks
# by the type Live TV apps register for.)
live=$(cmd package resolve-activity --brief -a android.intent.action.VIEW -d "$uri" -t vnd.android.cursor.item/channel 2>/dev/null | tail -n 1 | grep / | sed 's#/.*##')
[ -n "$live" ] || log "Live TV button: couldn't find the Live TV app; switching without waiting for it"
on_top() { dumpsys activity activities 2>/dev/null | grep -m1 -E 'topResumedActivity|mResumedActivity' | grep -q " $live/"; }

# getevent ends when an input device goes away (the Bluetooth remote disconnects when the TV sleeps), so listen
# again whenever it does.
while true; do
    getevent -lq 2>/dev/null | while read -r _ type key state; do
        [ "$type" = EV_KEY ] && [ "$key" = "$LIVE_TV_KEY" ] && [ "$state" = UP ] || continue
        # Wait (up to 3 s) for the firmware's own Live TV screen, then give it the input as an update.
        n=0
        while [ $n -lt 15 ] && [ -n "$live" ] && ! on_top; do sleep 0.2; n=$((n + 1)); done
        am start -a android.intent.action.VIEW -d "$uri" >/dev/null 2>&1
    done
    sleep 1
done
