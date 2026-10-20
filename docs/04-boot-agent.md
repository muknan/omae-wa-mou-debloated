# 4. Boot agent: make it stick

> **Easier:** double-click `Start-Here.cmd` and the setup wizard does this for you. This page explains what it does, and how to do it by hand.

**The problem:** on many firmwares a cold boot resets background app-ops (and some reset animation speed), and every restart wakes apps back up. Android 14 also blocks the shell from disabling another app's boot receivers. So nothing set over ADB is permanent; it has to be re-applied at boot.

**The fix:** at boot, a tiny Termux script connects to the TV's *own* ADB (`127.0.0.1:5555`), runs `tv-apply.sh` twice (early, and again 2 min later for apps that start late), then force-stops Termux so nothing stays in memory.

```
boot ─► ADB Auto Enable turns wireless debugging on
     ─► Termux:Boot runs 10-tv-agent.sh
          └─► adb 127.0.0.1:5555 shell sh /data/local/tmp/tv-apply.sh   (≈80 s and ≈200 s after boot)
          └─► stops Termux
```

## Requirements
- Your TV listens on port **5555** while wireless debugging is on (most Google TVs do; check with `adb connect <IP>:5555`).
- Wireless debugging stays on (see [ADB Auto Enable](#adb-auto-enable)).

## Install
1. Download the official APKs and **check their SHA-256** against the checksum file in each release:
   - Termux: <https://github.com/termux/termux-app/releases> → `termux-app_…_armeabi-v7a.apk` for 32-bit TVs, `arm64-v8a` for 64-bit. Check yours: `adb shell getprop ro.product.cpu.abi`.
   - Termux:Boot: <https://github.com/termux/termux-boot/releases>
   ```powershell
   (Get-FileHash .\termux-app.apk).Hash
   adb install .\termux-app.apk
   adb install .\termux-boot.apk
   ```
2. Give Termux storage access and keep it from being killed mid-script:
   ```powershell
   adb shell pm grant com.termux android.permission.READ_EXTERNAL_STORAGE
   adb shell pm grant com.termux android.permission.WRITE_EXTERNAL_STORAGE
   adb shell cmd deviceidle whitelist +com.termux
   ```
3. Push the TV scripts and stage the Termux files:
   ```powershell
   .\scripts\pc\Deploy-Scripts.ps1 -Device <TV-IP>:5555 -Termux
   ```
4. On the TV, open **Termux:Boot** once (this registers it for boot), then open **Termux** and type:
   ```
   bash /sdcard/Download/tvagent/setup.sh
   ```
   Approve the **Allow debugging?** prompt for Termux's key (tick *Always allow*).
5. Remove the staged copies (any app with storage access could edit them):
   ```powershell
   .\scripts\pc\Deploy-Scripts.ps1 -Device <TV-IP>:5555 -CleanStaging
   ```
6. Reboot and check that it ran:
   ```powershell
   adb shell tail /data/local/tmp/tv-apply.log
   ```

**Never force-stop Termux:Boot** (`com.termux.boot`): a force-stopped app stops receiving the boot signal. `tv-apply.sh` protects it by default.

## ADB Auto Enable
Android switches wireless debugging off when the TV drops off Wi-Fi, and then the agent can't connect at the next boot. [ADB Auto Enable](https://github.com/mouldybread/adb-auto-enable) switches it back on at boot.
```powershell
adb install .\ADB-auto-enable-<version>.apk
adb shell pm grant com.tpn.adbautoenable android.permission.WRITE_SECURE_SETTINGS
```
On TVs that keep listening on port 5555 after a restart (like the Xiaomi test TV), that one permission is enough.
On most Android 14+ TVs the port is random after a restart, so ADB Auto Enable must also be **paired once** so it can
switch it back to 5555: open `http://<TV-IP>:9093` in a browser, then on the TV choose Developer options → Wireless
debugging → *Pair device with pairing code* and type the code and port into that page. The wizard does this for you.
(On some Xiaomi TVs a system service already uses port 9093; those don't need the pairing.)
**Security trade-off:** the TV keeps ADB open on your LAN. Only approved keys can connect, but use this only on a network you trust.

## Change what it does
Edit the CONFIG block in [`scripts/device/tv-apply.sh`](../scripts/device/tv-apply.sh), then:
```powershell
.\scripts\pc\Deploy-Scripts.ps1 -Device <TV-IP>:5555
```
No Termux changes are needed. The agent always runs whatever is in `/data/local/tmp/tv-apply.sh`.
