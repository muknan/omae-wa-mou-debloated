# 2. Audit & debloat

> **Easier:** double-click `Start-Here.cmd` and the setup wizard does this for you. This page explains what it does, and how to do it by hand.

## Audit first (read-only)
```powershell
.\scripts\pc\Audit.ps1 -Device <TV-IP>:5555
```
Saves build info, memory, CPU hogs, packages, launchers and background usage to a folder. The two files worth reading first:
- `cpu.txt`: anything sitting near 100%? (On the test TV, Xiaomi's AirPlay daemon was stuck at a full core.)
- `background-24h.txt`: which apps run all day even though you didn't open them.

Quick checks by hand:
```powershell
adb shell pm list packages -3          # apps you installed
adb shell pm list packages -d          # already disabled
adb shell dumpsys cpuinfo | more       # CPU hogs
```

## The list
The wizard uses the multi-brand [catalog](../catalog/) and only asks about what your TV has. The manual script
below uses [`lists/xiaomi-google-tv.txt`](../lists/xiaomi-google-tv.txt): one package per line with a tier:

| Tier | Meaning |
|---|---|
| `LOW` | Telemetry, unused extras. Safe for almost everyone. |
| `CONDITIONAL` | Only if you don't use the feature: PatchWall, antenna TV, AirPlay, Ambient Mode. |
| `DNT` | Do not touch. Never applied; kept as documentation with the reason. |

**Other brands:** the [catalog](../catalog/packages.csv) has risk levels for 15+ brands. Copy the lines for packages your TV actually has into a list file in this format, and test in small batches.

## Apply
```powershell
# dry run
.\scripts\pc\Debloat.ps1 -Device <TV-IP>:5555 -List .\lists\xiaomi-google-tv.txt -Tier LOW -WhatIf
# for real: pauses every 3 packages so you can test
.\scripts\pc\Debloat.ps1 -Device <TV-IP>:5555 -List .\lists\xiaomi-google-tv.txt -Tier LOW
# conditional ones, explicitly
.\scripts\pc\Debloat.ps1 -Device <TV-IP>:5555 -List .\lists\xiaomi-google-tv.txt -Tier CONDITIONAL -Only com.mediatek.airplaydaemon,com.mediatek.partner.airplay_sys,com.mediatek.AirplayAPK
```
Between batches test: **Home, Back, an HDMI input, a streaming app with sound, casting, voice search.**

Every change is logged to `scripts/pc/changes.jsonl`. Undo all:
```powershell
.\scripts\pc\Debloat.ps1 -Device <TV-IP>:5555 -Restore
```
Single package by hand:
```powershell
adb shell pm disable-user --user 0 <package>
adb shell pm enable --user 0 <package>
```

## Lessons from the test TV
- **Don't disable the stock Google TV launcher** (`launcherx`), even with a third-party launcher. The Settings key opens its dashboard, Home fell through to a recovery screen, and Android soft-restarted twice. Projectivy needs it present.
- **Read the package before trusting a guide.** `partnercustomizer` is on many "safe to remove" lists, but on Xiaomi it contains the Bluetooth **remote pairing** screen.
- **Persistent system apps keep running when disabled** (AirPlay, HbbTV). Disabling still stops their services; their idle process remains.
- `pm disable-user` survives reboots. A factory reset re-enables everything.
