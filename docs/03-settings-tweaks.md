# 3. Settings tweaks

> **Easier:** double-click `Start-Here.cmd` and the setup wizard does this for you. This page explains what it does, and how to do it by hand.

All reversible. Run them yourself, or let `tv-apply.sh` handle the ones that reset at boot.

## Animation speed (the one you'll actually feel)
```powershell
adb shell settings put global window_animation_scale 0.5
adb shell settings put global transition_animation_scale 0.5
adb shell settings put global animator_duration_scale 0.5
```
`0.5` = twice as fast, `0` = off, `1.0` = stock. Makes navigation *feel* faster; it doesn't speed up the CPU.
Some firmwares reset these at boot (Xiaomi resets `animator_duration_scale` every time). `tv-apply.sh` re-applies them, plus Xiaomi's hidden `mitv_fast_cur_anim_scale` key.

## Small background savers
```powershell
adb shell settings put global wifi_scan_always_enabled 0                 # no Wi-Fi scanning for location
adb shell settings put global wifi_networks_available_notification_on 0  # no "open network" scans/notices
adb shell settings put global activity_starts_logging_enabled 0          # no per-app-launch metrics logging
adb shell settings put global user_experience_enabled 0                  # Xiaomi "user experience" program
adb shell settings put secure screensaver_enabled 0                      # only if you don't use the screensaver
```
Undo by putting back `1`. (`send_action_app_error 0` also works, but Google Services may re-enable it. Not worth fighting.)

## Background limits (what tv-apply.sh does per app)
```powershell
adb shell cmd appops set <package> RUN_ANY_IN_BACKGROUND ignore   # no background work at all
adb shell am set-standby-bucket <package> restricted              # fewest jobs/alarms
adb shell am force-stop <package>                                 # asleep until you open it
```
Streaming apps are fine with this, because playback happens while they're on screen. Music players and VPNs aren't: put those in `STOP_ONLY` in `tv-apply.sh` (stopped at boot, but allowed to run once you start them).
On many firmwares, the test TV included, these app-ops reset at every cold boot, and a force-stopped app wakes up again at the next restart. That's why the [boot agent](04-boot-agent.md) exists.

## Things we measured and **didn't** do
| Tweak | Why not |
|---|---|
| Background process limit / "Don't keep activities" | Breaks apps, causes cold starts. |
| Lower `max_cached_processes` | Only 7 apps were cached vs a limit of 32 → no effect. |
| Vulkan UI renderer (`debug.hwui.renderer skiavk`) | Measured *slower* on Mali-G52: GPU 13 ms vs 10 ms per frame. |
| Task killers / RAM cleaners | Android's cached-app freezer already freezes background apps 10 s after they leave the screen. |
| Disable the stock launcher | See [02](02-audit-and-debloat.md#lessons-from-the-test-tv). |
