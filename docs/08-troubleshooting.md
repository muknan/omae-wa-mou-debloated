# 8. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `adb connect` refused | Wireless debugging is off (it turns off after Wi-Fi drops). Turn it on, or install [ADB Auto Enable](04-boot-agent.md#adb-auto-enable). |
| `adb connect` times out | Wrong IP, or the PC is on a different network/VPN. Give the TV a fixed IP in your router. |
| Boot agent didn't run | Check `adb shell run-as com.termux cat files/home/tv-agent.log`. "could not reach local ADB" = wireless debugging was off at boot; the agent waits 20 min for you to turn it on. |
| Tweaks gone after reboot | Is Termux:Boot force-stopped? Open it once. Check `adb shell tail /data/local/tmp/tv-apply.log`. |
| Termux's `adb` "CANNOT LINK EXECUTABLE" | Fresh Termux needs `apt full-upgrade` before `android-tools`; `setup.sh` does this. |
| Key Mapper refresh does nothing, toast says Expert Mode needed | Expert Mode isn't running. Run its `start.sh` once (see [05](05-key-mapper.md)) or reboot (the boot agent starts it). |
| "Accessibility service" toast from Projectivy | Another accessibility app (Key Mapper) restarted briefly, e.g. after a permission change. Both reconnect on their own. |
| Home button opens the stock launcher | Projectivy's override needs the stock launcher **enabled** and Projectivy's accessibility service on. Re-enable `com.google.android.apps.tv.launcherx` if you disabled it. |
| An app stopped working | Re-enable it: `adb shell pm enable --user 0 <package>`, or undo everything: `Debloat.ps1 -Restore`. |
| A music app / VPN stops when you leave it | Move it to `STOP_ONLY` in `tv-apply.sh` and redeploy. |
| An app you use is put to sleep too often | Add it to `EXTRA_PROTECT` in `tv-apply.sh` and redeploy. |

## Undo everything
```powershell
.\scripts\pc\Debloat.ps1 -Device <TV-IP>:5555 -Restore          # re-enable disabled packages
.\scripts\pc\Deploy-Scripts.ps1 -Device <TV-IP>:5555 -Remove     # remove the TV scripts
adb uninstall com.termux.boot ; adb uninstall com.termux         # remove the boot agent
adb shell settings put global window_animation_scale 1.0
adb shell settings put global transition_animation_scale 1.0
adb shell settings put global animator_duration_scale 1.0
```
Background app limits reset by themselves at the next reboot once the agent is gone.
