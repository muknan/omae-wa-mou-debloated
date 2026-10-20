#!/data/data/com.termux/files/usr/bin/sh
# Termux:Boot script (lives in ~/.termux/boot/). At every cold boot it connects to the TV's own ADB and
# runs /data/local/tmp/tv-apply.sh twice (once early, once ~2 min later for apps that start late), then
# stops Termux so nothing stays in memory.
termux-wake-lock
LOGF="$HOME/tv-agent.log"
echo "$(date '+%F %T') boot agent start" >> "$LOGF"

T=127.0.0.1:5555
# Wait up to ~20 min for ADB: if wireless debugging was off and you switch it on in that window,
# everything still applies without another reboot.
for i in $(seq 1 80); do
    adb connect "$T" >/dev/null 2>&1
    [ "$(adb -s "$T" get-state 2>/dev/null)" = "device" ] && break
    [ "$i" -le 12 ] && sleep 5 || sleep 15
done

if [ "$(adb -s "$T" get-state 2>/dev/null)" = "device" ]; then
    adb -s "$T" shell sh /data/local/tmp/tv-apply.sh
    sleep 120
    adb -s "$T" shell sh /data/local/tmp/tv-apply.sh
    echo "$(date '+%F %T') applied twice" >> "$LOGF"
else
    echo "$(date '+%F %T') could not reach local ADB" >> "$LOGF"
fi
tail -n 50 "$LOGF" > "$LOGF.tmp" && mv "$LOGF.tmp" "$LOGF"
termux-wake-unlock

# Leave nothing resident. force-stop is fine for com.termux (Termux:Boot wakes it explicitly next boot);
# only plain-kill com.termux.boot - force-stopping it would stop it receiving BOOT_COMPLETED.
adb -s "$T" shell "setsid nohup sh -c 'sleep 8; am force-stop com.termux; am kill com.termux.boot' </dev/null >/dev/null 2>&1 &"
adb disconnect "$T" >/dev/null 2>&1
adb kill-server >/dev/null 2>&1
