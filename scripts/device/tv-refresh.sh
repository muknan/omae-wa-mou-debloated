#!/system/bin/sh
# tv-refresh.sh - "fresh boot without rebooting". Bind it to a remote button with Key Mapper
# (action: Shell command, Execution mode: ADB, script: sh /data/local/tmp/tv-refresh.sh).
#
# 1. Runs tv-apply.sh (animations, background blocks, puts background apps to sleep - never the one on screen).
# 2. am kill-all: clears cached background processes only. Running services (launcher, Key Mapper,
#    Cast, TV services, music actually playing) are untouched.
LOG=/data/local/tmp/tv-apply.log
mem() { grep MemAvailable /proc/meminfo | awk '{print $2}'; }
echo "$(date '+%F %T') refresh start (MemAvailable $(mem) kB)" >> "$LOG"
sh /data/local/tmp/tv-apply.sh
am kill-all
echo "$(date '+%F %T') refresh done (MemAvailable $(mem) kB)" >> "$LOG"
