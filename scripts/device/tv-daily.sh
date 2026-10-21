#!/system/bin/sh
# tv-daily.sh - runs tv-refresh.sh once a day, like pressing the refresh button yourself.
#
# Started by tv-apply.sh (DAILY_REFRESH=1) as a tiny background shell process, the same way Key Mapper's
# Expert Mode runs. It wakes every 15 minutes; on the first check at or after DAILY_REFRESH_HOUR each day it
# refreshes. While the TV is in standby it is asleep too, so a 4:00 refresh happens within 15 minutes of
# switching the TV on. It skips any check where something is playing (music, video), so it never interrupts.
# Settings are re-read from tv-apply.conf at every check: set DAILY_REFRESH=0 and it exits by itself.
DIR=/data/local/tmp
LOG=$DIR/tv-apply.log
STAMP=$DIR/tv-daily.last
INTERVAL=${DAILY_INTERVAL:-900}

# One instance only: if another live copy owns the pid file, leave.
old=$(cat $DIR/tv-daily.pid 2>/dev/null)
if [ -n "$old" ] && [ "$old" != "$$" ] && grep -q tv-daily /proc/"$old"/cmdline 2>/dev/null; then
    exit 0
fi
echo $$ > $DIR/tv-daily.pid
# Sleep in the background and wait for it, so a stop signal (kill) ends the script right away instead of after
# the current 15-minute sleep.
trap 'kill "$napper" 2>/dev/null; rm -f $DIR/tv-daily.pid; exit 0' TERM INT

while true; do
    sleep "$INTERVAL" &
    napper=$!
    wait "$napper"
    DAILY_REFRESH=1
    DAILY_REFRESH_HOUR=4
    # shellcheck source=/dev/null
    [ -f $DIR/tv-apply.conf ] && . $DIR/tv-apply.conf
    if [ "$DAILY_REFRESH" != "1" ] || [ ! -f $DIR/tv-refresh.sh ]; then
        echo "$(date '+%F %T') daily refresh stopped (switched off or removed)" >> $LOG
        rm -f $DIR/tv-daily.pid
        exit 0
    fi
    today=$(date +%F)
    hour=$(date +%H); hour=${hour#0}
    [ "$(cat $STAMP 2>/dev/null)" = "$today" ] && continue
    [ "${hour:-0}" -lt "$DAILY_REFRESH_HOUR" ] && continue
    if dumpsys media_session 2>/dev/null | grep -qE 'state=PlaybackState \{state=(PLAYING|3[,)])'; then
        continue   # something is playing: try again at the next check
    fi
    echo "$today" > $STAMP
    echo "$(date '+%F %T') daily refresh" >> $LOG
    sh $DIR/tv-refresh.sh
done
