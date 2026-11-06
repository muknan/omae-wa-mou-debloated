# 5. Key Mapper: remote buttons & the refresh button

> **Easier:** double-click `Start-Here.cmd` and the setup wizard does this for you. This page explains what it does, and how to do it by hand.

<img src="../screenshots/keymapper-keymaps.jpg" width="70%" />

[Key Mapper](https://github.com/keymapperorg/KeyMapper) is free and open source, and it works with a TV remote. The mappings in the screenshot are just **examples** of what one person liked; make your own.

## Install
Get the `…-foss.apk` from <https://github.com/keymapperorg/KeyMapper/releases>, check the SHA-256 shown on the release, then:
```powershell
adb install .\keymapper-<version>-foss.apk
```
On the TV: Settings → System → Accessibility → **Key Mapper** → On. It runs alongside Projectivy's accessibility service without problems.

## Find out what your buttons send
Key Mapper's **Record trigger** captures any button. To see the raw codes yourself:
```powershell
adb shell getevent -lt      # press buttons, Ctrl+C to stop
```
On the test TV: Live TV (TV+) = `KEY_MEMO`, PatchWall/Mi = `KEY_WWW`. Buttons for apps you've disabled (e.g. PatchWall) are free to reuse.
The TV+ button is the exception: the firmware takes it before Key Mapper sees it. To make it open an HDMI input, use
`LIVE_TV_BUTTON_INPUT` ([09](09-extras.md#the-live-tv--tv-button)).

## Example ideas
| Trigger | Action |
|---|---|
| Live TV (unused without an antenna) | Launch app → your favourite streaming app |
| Colour buttons | Rewind / Fast forward / Play-Pause / Next track |
| Double press of any spare button | **The refresh button** (below) |
| Long press of a colour button | **Next episode in every app** (below) |

## The refresh button ("fresh boot without rebooting")
Runs [`tv-refresh.sh`](../scripts/device/tv-refresh.sh): re-applies everything and clears cached apps in about 4 s, never touching the app on screen.
Apps that are busy are left running too: music playing in the background, a connected VPN (anything with a
foreground service or a playing media session). Only at start-up is everything on the lists stopped.

1. Make sure the scripts are on the TV (`Deploy-Scripts.ps1`).
2. **Expert Mode** is needed so Key Mapper can run commands with ADB rights. Start it once:
   ```powershell
   adb shell sh /storage/emulated/0/Android/data/io.github.sds100.keymapper/start.sh
   ```
   On many TVs Key Mapper's own "Auto start" can't work: it needs a pairing code typed into a notification, which Google TV can't do. Leave `START_KEYMAPPER_EXPERT_MODE=1` in `tv-apply.sh` and the boot agent restarts it at every boot. Then turn off Key Mapper's own *Auto start and keep alive*, so you don't get "Starting Expert Mode failed" notifications.
3. New key map → record your trigger (e.g. **double press** a colour button) → Add action → **Shell command**:
   - Script: `sh /data/local/tmp/tv-refresh.sh`
   - Execution mode: **ADB**
4. Add a second action, **Show volume dialog**. Key Mapper waits for the script to finish, so the volume bar flashes exactly when the refresh is done. That's a non-intrusive "done" signal that needs no notifications.

### No notification clutter
Key Mapper always keeps one or two minimum-priority notifications posted (key map status, keyboard toggle), and
launchers like Projectivy show them as a badge on the home screen. `tv-apply.sh` hides them every time it runs
(at boot, and every time you press the refresh button) by snoozing them for 30 days (`QUIET_APPS`). Key Mapper
posts them again whenever it restarts, so the background helper (`tv-daily.sh`) checks every minute and hides any
that came back. The app is not touched: no permission changes, no restarts.

Don't fix this by revoking Key Mapper's notification permission: revoking restarts the app, which itself posts
"Accessibility service needs restarting!", and some firmwares grant the permission back at boot anyway. The
`POST_NOTIFICATION` app-op is ignored on Android 13+, and Key Mapper's own **Settings > Show pause/resume
notification** opens Android's per-channel notification screen, which Google TV doesn't have (Key Mapper crashes
and restarts).

### Why the volume bar and not a toast?
On some firmwares an app's toasts are blocked together with its notifications ("Suppressing toast … by user
request"). The volume bar works everywhere. (The shell can't show the volume UI itself on some firmwares, so it has
to be Key Mapper's action.)

## Next episode in every app
Key Mapper's own *Next track* reaches most players (SmartTube, mpv), but not Nuvio: its player tells Android it can't
skip to next, and uses the Next and Channel Up keys to skip chapters. Its only way to the next episode is the player's
"Next episode" button. [`tv-next.sh`](../scripts/device/tv-next.sh) handles both: in Nuvio it presses Up, Up, Down,
Right, OK, which lands on that button whatever state the player is in (only while an episode plays, so it never opens
a menu on a movie); anywhere else it sends the media Next key.

New key map → record the trigger (e.g. **long press** Green) → Add action → **Shell command**:
- Description: `Next episode`, Script: `sh /data/local/tmp/tv-next.sh`, Execution mode: **ADB** (Expert Mode).

Tested on the Xiaomi TV: Nuvio (S15 E13 → E14, about 2.5 s), SmartTube (next video). Spotify's TV app ignores
Android's Next altogether (both the key and the media-session command); only its on-screen ⏭ skips.

## Costs
Key Mapper ≈ 50 MB plus Expert Mode ≈ 47 MB of always-resident memory. Worth it if you use it; skip Expert Mode if you don't need the refresh button.
