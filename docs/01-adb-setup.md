# 1. ADB setup & pairing

> **Easier:** double-click `Start-Here.cmd` and the setup wizard does this for you. This page explains what it does, and how to do it by hand.

## Install ADB on Windows
```powershell
winget install --id Google.PlatformTools -e
```
Open a **new** PowerShell window afterwards so `adb` is on your PATH. Check:
```powershell
adb version
```
(macOS: `brew install android-platform-tools` · Linux: your distro's `android-tools` / `adb` package.)

## Turn on debugging on the TV
1. Settings → System → About → click **Android TV OS build** 7 times ("You are now a developer").
   Older Android TVs: Settings → Device Preferences → About → **Build**.
2. Settings → System → **Developer options** → turn on **USB debugging**, and **Wireless debugging** or
   **Network debugging** if your TV has it.
3. Note the TV's IP (Settings → Network & Internet → your Wi-Fi, or on the Wireless debugging screen).

Fire TV: Settings → My Fire TV → About → click the device name 7 times, then Developer options → **ADB debugging**.
NVIDIA Shield: Settings → Device Preferences → About → **Build** 7 times, then Developer options → **Network debugging**.

## Connect
Many Google TVs also listen on the classic port **5555** while wireless debugging is on:
```powershell
adb connect <TV-IP>:5555
```
Approve **"Allow debugging?"** on the TV and tick **Always allow from this computer**. Check:
```powershell
adb devices          # should list <TV-IP>:5555  device
```

### If port 5555 is refused: pair with a code
On the TV: Developer options → Wireless debugging → **Pair device with pairing code**. Leave that screen open, then on the PC:
```powershell
adb pair <TV-IP>:<pairing-port>          # enter the 6-digit code when asked
adb connect <TV-IP>:<connect-port>       # the IP:port shown on the main Wireless debugging screen
```
The connect port changes after reboots on Android 11+; port 5555 (if your TV offers it) doesn't.

## Good to know
- **"Allow USB debugging?"** is Android's wording for *any* ADB connection, wireless included. USB debugging itself can stay off.
- Android **turns Wireless debugging off if the TV loses Wi-Fi or changes networks**. [ADB Auto Enable](04-boot-agent.md#adb-auto-enable) turns it back on at boot.
- **Revoke USB debugging authorisations** removes *every* approved key, including the boot agent's.
- Keys unused for 7 days are forgotten by default. To keep them (your choice, it's a security setting):
  ```powershell
  adb shell settings put global adb_allowed_connection_time 0
  ```
  Undo: `adb shell settings delete global adb_allowed_connection_time`
