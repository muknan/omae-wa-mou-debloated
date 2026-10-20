#!/data/data/com.termux/files/usr/bin/bash
# One-time setup inside Termux. Run it from the Termux app on the TV:
#   bash /sdcard/Download/tvagent/setup.sh
# It installs adb, copies the boot script into Termux's private folder and asks the TV to authorize
# Termux's ADB key (tick "Always allow" on the prompt).
S=/sdcard/Download/tvagent
set -e
export DEBIAN_FRONTEND=noninteractive
apt-get update -y
apt-get full-upgrade -y -o Dpkg::Options::=--force-confnew   # fresh Termux needs this or adb won't link
apt-get install -y -o Dpkg::Options::=--force-confnew android-tools
adb version >/dev/null

mkdir -p ~/.termux/boot
cp "$S/10-tv-agent.sh" ~/.termux/boot/10-tv-agent.sh
chmod 700 ~/.termux/boot/10-tv-agent.sh

echo ">>> Approve the 'Allow debugging?' prompt on the TV and tick 'Always allow'"
set +e
for i in $(seq 1 90); do
    adb connect 127.0.0.1:5555 >/dev/null 2>&1
    [ "$(adb -s 127.0.0.1:5555 get-state 2>/dev/null)" = "device" ] && break
    sleep 2
done
if [ "$(adb -s 127.0.0.1:5555 get-state 2>/dev/null)" = "device" ]; then
    echo "OK: Termux can reach the TV's ADB. Reboot the TV to test the boot agent."
else
    echo "Not authorized. Is wireless debugging on, and does the TV listen on port 5555? See docs/04-boot-agent.md"
fi
adb kill-server >/dev/null 2>&1
