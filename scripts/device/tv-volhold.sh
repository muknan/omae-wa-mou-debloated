#!/system/bin/sh
# tv-volhold.sh - hold-to-repeat for the remote's volume buttons.
#
# On this TV, holding VOLUME changes the volume by one step only. Android generates the "still held" repeats
# itself, but not while an accessibility service filters key events, and Projectivy's (needed for its Home button
# handling) does. This helper restores the repeat without touching that. It only *listens* to the remote
# (getevent, read-only: nothing is intercepted, every button keeps working as before). When VOLUME UP or DOWN stays
# held longer than HOLD_DELAY, it steps the volume every REPEAT_EVERY until the button is released.
#
# The steps go straight to Android's audio service (adjustStreamVolume, with the volume bar shown), not as key
# presses: Xiaomi's own volume-down handling tracks the held key, and extra key presses confuse it (it can end up
# muting). The audio service's method numbers depend on the firmware, so at start the helper checks them with two
# read-only calls (current and maximum volume must match what Android reports) and doesn't run if they don't.
#
# Safety: at most MAX_REPEATS steps per hold (also the limit if a release is ever missed); the steps don't come
# back through getevent, so it can't trigger itself. Started by tv-apply.sh, kept running by tv-daily.sh;
# VOLUME_HOLD_REPEAT="0" in tv-apply.conf stops it. Stop it now: pkill -f tv-volhold.sh
DIR=/data/local/tmp
LOG=$DIR/tv-apply.log
HELD=$DIR/tv-volhold.held   # names the hold in progress; the repeat loop runs only while it names its own hold
HOLD_DELAY=0.4      # seconds before repeating starts (Android's own key repeat waits 0.4 s too)
REPEAT_GAP=0.01     # pause between steps; each step itself takes ~0.07 s, so about 12 steps a second
MAX_REPEATS=100     # a whole 0-100 sweep in one hold (~8 s)
# IAudioService method numbers on this firmware (Android 14): adjustStreamVolume, getStreamVolume, getStreamMaxVolume
ADJUST=10; GET=24; GETMAX=26

# One instance only.
old=$(cat $DIR/tv-volhold.pid 2>/dev/null)
if [ -n "$old" ] && [ "$old" != "$$" ] && grep -q tv-volhold /proc/"$old"/cmdline 2>/dev/null; then
    exit 0
fi

# Check the method numbers before using them (read-only calls).
parcel_int() { service call audio "$1" i32 3 2>/dev/null | sed -n 's/.*00000000 \([0-9a-f]\{8\}\).*/\1/p' | head -1; }
now=$(cmd media_session volume --stream 3 --get 2>/dev/null | sed -n 's/.*volume is \([0-9]*\) in range \[[0-9]*\.\.\([0-9]*\)\].*/\1 \2/p')
got=$(parcel_int $GET); max=$(parcel_int $GETMAX)
if [ -z "$now" ] || [ -z "$got" ] || [ -z "$max" ] || [ "$(printf '%d %d' 0x$got 0x$max)" != "$now" ]; then
    echo "$(date '+%F %T') volume hold: this firmware's audio service doesn't match (got '$got/$max', expected '$now'); not running" >> $LOG
    getprop ro.build.fingerprint > $DIR/tv-volhold.unsupported   # tv-apply/tv-daily don't retry on this firmware
    exit 0
fi
rm -f $DIR/tv-volhold.unsupported

echo $$ > $DIR/tv-volhold.pid
rm -f $HELD

# getevent ends when an input device goes away (the Bluetooth remote disconnects when the TV sleeps, or a battery
# change), so listen again whenever it does; a hold in progress stops then too.
while true; do
    getevent -lq 2>/dev/null | while read -r dev type key state; do
        [ "$type" = EV_KEY ] || continue
        case "$key:$state" in
            KEY_VOLUMEUP:DOWN | KEY_VOLUMEDOWN:DOWN)
                dir=1; [ "$key" = KEY_VOLUMEDOWN ] && dir=-1
                hold=$dir.$(date +%s%N)
                echo "$hold" > $HELD
                (
                    sleep "$HOLD_DELAY"
                    n=0
                    while [ $n -lt $MAX_REPEATS ]; do
                        now=; read -r now < $HELD 2>/dev/null
                        [ "$now" = "$hold" ] || break          # released (or a newer hold took over)
                        service call audio $ADJUST i32 3 i32 $dir i32 1 s16 com.android.shell >/dev/null 2>&1   # 1 = show the volume bar
                        sleep "$REPEAT_GAP"
                        n=$((n + 1))
                    done
                ) &
                ;;
            KEY_VOLUMEUP:UP | KEY_VOLUMEDOWN:UP) rm -f $HELD ;;
        esac
    done
    rm -f $HELD
    sleep 1
done
