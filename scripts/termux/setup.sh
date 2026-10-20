#!/data/data/com.termux/files/usr/bin/bash
# One-time setup inside Termux. The setup wizard types this into Termux for you; by hand it's:
#   bash /sdcard/Download/tvagent/setup.sh
# It installs adb, copies the boot script into Termux's private folder and asks the TV to authorize
# Termux's ADB key (tick "Always allow" on the prompt).
# Progress goes to $S/setup.log and the result to $S/status (running / ok / fail: reason), so the wizard
# on the PC can follow along.
S=/sdcard/Download/tvagent
STATUS="$S/status"
exec > >(tee -a "$S/setup.log") 2>&1
echo running > "$STATUS"
fail() { echo "fail: $*" > "$STATUS"; echo "FAILED: $*"; exit 1; }

export DEBIAN_FRONTEND=noninteractive
echo ">>> step 1/3: updating Termux packages (needs internet, 1-3 minutes)"
apt-get update -y || fail "apt update (no internet?)"
# A fresh Termux needs the full upgrade, otherwise adb fails with "CANNOT LINK EXECUTABLE".
apt-get full-upgrade -y -o Dpkg::Options::=--force-confnew || fail "apt full-upgrade"
echo ">>> step 2/3: installing adb"
apt-get install -y -o Dpkg::Options::=--force-confnew android-tools || fail "install android-tools"
adb version >/dev/null || fail "adb does not run"

mkdir -p ~/.termux/boot
cp "$S/10-tv-agent.sh" ~/.termux/boot/10-tv-agent.sh || fail "copy boot script"
chmod 700 ~/.termux/boot/10-tv-agent.sh

echo ">>> step 3/3: Approve the 'Allow debugging?' prompt on the TV and tick 'Always allow'"
for i in $(seq 1 90); do
    adb connect 127.0.0.1:5555 >/dev/null 2>&1
    [ "$(adb -s 127.0.0.1:5555 get-state 2>/dev/null)" = "device" ] && break
    sleep 2
done
if [ "$(adb -s 127.0.0.1:5555 get-state 2>/dev/null)" = "device" ]; then
    echo "OK: Termux can reach the TV's ADB."
    echo ok > "$STATUS"
else
    fail "not authorized, or the TV is not listening on port 5555"
fi
adb kill-server >/dev/null 2>&1
