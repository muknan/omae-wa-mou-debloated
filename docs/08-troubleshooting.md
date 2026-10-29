# 8. Troubleshooting

First try: double-click **Start-Here.cmd** again. It remembers your TV and offers *Show status* and *Undo everything*.
*Show status* is a quick health check: did the boot helper run this boot, is Key Mapper's Expert Mode running, is
the background helper running (and when it last refreshed), any helper notifications showing, Private DNS, and how
often Wi-Fi reconnected since boot (many reconnects point at the router, see the row below).

## Connecting

| Symptom | Fix |
|---|---|
| The wizard finds no TV | Debugging switched itself off (it does when the TV loses Wi-Fi), or the PC is on another network or a VPN. Turn debugging back on and choose *Search again*, or type the IP (TV: Settings > Network > your Wi-Fi). |
| "Allow USB debugging?" never appears | Look at the TV while the wizard connects; the box can hide behind an app. Press Home and try again. |
| It keeps asking for a pairing code | Newer TVs need one. Developer options > Wireless debugging > *Pair device with pairing code*; the wizard asks for the code. |
| Windows says "Windows protected your PC" | It's the downloaded `Start-Here.cmd`. Click *More info* > *Run anyway*, or right-click the ZIP > Properties > *Unblock* before extracting. |
| `adb` "not recognized" (manual commands) | Use the wizard; it brings its own copy. For manual use: `winget install --id Google.PlatformTools -e`, then open a new window. |

## Boot helper

| Symptom | Fix |
|---|---|
| The test restart says the helper didn't run | Usually debugging was off at startup. Android 14+: make sure ADB Auto Enable is paired (re-run step 3, or `.\setup\Wizard.ps1 -Device <ip> -PairCode <code>`). The helper keeps trying for 20 minutes after a restart. If the log has no "apply start" at all after a restart, Termux:Boot never got to start it: `adb shell am get-standby-bucket com.termux.boot` should say `5`; if not, `adb shell cmd deviceidle whitelist +com.termux.boot` (see [04](04-boot-agent.md#install), step 2). |
| Check what it did | `adb shell tail /data/local/tmp/tv-apply.log` (each run logs "apply start (uptime ...)"). |
| Termux setup failed "apt update" | The TV had no internet. Re-run step 3. |
| Termux "is already installed from another store" | Termux and Termux:Boot must come from the same place. Uninstall Termux on the TV and re-run step 3. |
| Termux's `adb` "CANNOT LINK EXECUTABLE" | A fresh Termux needs `apt full-upgrade` first; `setup.sh` does it. Re-run step 3. |

## After setup

| Symptom | Fix |
|---|---|
| An app or feature stopped working | Undo just that package: `adb shell pm enable --user 0 <package>` (the list is in `my-tv\<your TV>\report.md`), or *Undo everything*. Then tell the wizard to skip it: add it to `skipPackages` in an answers file, or simply untick it next time. |
| A music app or VPN stops when you leave it | Re-run step 2 and move it to "stopped at startup, may run after you open it". |
| An app you use is put to sleep too often | Re-run step 2 and move it to "left alone". |
| A notification badge on the home screen | Usually Key Mapper's permanent notifications. The background helper hides them within a minute; if one stays, the helper isn't running: press the refresh button once, or re-run step 2. |
| Home screen feels like a low refresh rate; moving around stutters | Usually something on the home screen animating non-stop (Projectivy's glow animation), which keeps the GPU busy all the time; set it to None. The screen itself stays at 60 Hz. Check and fix: [Performance notes](06-launcher-and-wallpapers.md#performance-notes-measured). Stutter right after leaving an app is memory: see [Streaming apps left in the background](09-extras.md#streaming-apps-left-in-the-background). |
| Holding VOLUME changes it by one step only | An app's accessibility key filter (Projectivy's) stops Android's key repeat. The volume helper restores it (`VOLUME_HOLD_REPEAT`); see [Holding the volume buttons](09-extras.md#holding-the-volume-buttons). |
| Wi-Fi goes off and on every few minutes | Usually the router's IPv6. The boot helper works around it (`WIFI_IPV6_ROUTER_FIX`); see [Wi-Fi that keeps dropping](09-extras.md#wi-fi-that-keeps-dropping). |
| TV switches itself off after ~10 minutes on the home screen | With the screensaver off, the TV goes to standby when the screensaver would have started. `adb shell settings put system screen_off_timeout 1800000` makes that 30 minutes (milliseconds; see [03](03-settings-tweaks.md#small-background-savers)). |
| Remote button does nothing | Expert Mode isn't running. Restart the TV (the boot helper starts it) or see [05](05-key-mapper.md). |
| Home button opens the stock launcher | Projectivy needs its accessibility helper on and the stock launcher **enabled**. Re-run step 5. |
| "Accessibility service" toast from Projectivy | Another accessibility app (Key Mapper) restarted briefly. Both reconnect on their own. |
| Home is completely broken | `adb shell pm enable --user 0 com.google.android.apps.tv.launcherx` and `adb shell pm enable --user 0 com.google.android.tungsten.setupwraith`, then restart. |

## Undo everything

Double-click **Start-Here.cmd** > *Undo everything*, or:
```powershell
.\setup\Wizard.ps1 -Device <TV-IP> -Undo
```
It re-enables every package it switched off, removes the background limits and scripts, restores the animation
speed, removes the accessibility helpers it switched on, and offers to uninstall the helper apps it installed.

Used the older manual scripts instead? `.\scripts\pc\Debloat.ps1 -Device <TV-IP>:5555 -Restore` and
`.\scripts\pc\Deploy-Scripts.ps1 -Device <TV-IP>:5555 -Remove`.
