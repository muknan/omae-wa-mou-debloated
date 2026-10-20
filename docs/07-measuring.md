# 7. Measuring jank: what helped, what didn't

Don't trust "feels faster". These are the commands used on the test TV.

## Frame timing of your launcher
```powershell
adb shell dumpsys gfxinfo <launcher-package> reset
# navigate with the remote for ~10 s, then:
adb shell dumpsys gfxinfo <launcher-package>
```
Look at the `50th/90th/99th percentile` lines and `Janky frames`. For a per-stage breakdown (UI thread vs GPU), use `dumpsys gfxinfo <pkg> framestats`.

Projectivy's package is `com.spocky.projengmenu`.

## "Janky first, then smooth"
Count major page faults (memory pulled back from swap or disk) before and after a burst of navigation:
```powershell
adb shell "awk '{print \$12}' /proc/$(adb shell pidof com.spocky.projengmenu)/stat"
```
On the test TV, a cold pass caused 98 major faults with an 89 ms worst frame; the warm pass right after had 1 fault and a 25 ms worst frame. The cause is memory pressure, not the CPU.

## Other checks
```powershell
adb shell cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_min_freq   # CPU ramp-up isn't the issue if this is already high
adb shell dumpsys thermalservice | findstr "Status"                    # throttling?
adb shell dumpsys activity settings | findstr CUR_MAX_CACHED           # cached-app limit
adb shell logcat -b events -d | findstr am_freeze                      # cached-apps freezer working?
adb shell dumpsys SurfaceFlinger                                       # layer count / GPU composition
```

## Results on the test TV
| Idea | Result |
|---|---|
| Debloat + keep background apps asleep | ✅ +82 MB free after boot, 7 fewer processes, 185 MB less in swap |
| 0.5× animations | ✅ feels snappier (perception, not CPU) |
| Reboot to clear a stuck AirPlay daemon (100% CPU) | ✅; disabling AirPlay keeps it from coming back |
| Lower cached-process limit | ❌ no effect (7 cached vs a limit of 32) |
| Vulkan UI renderer | ❌ slower on Mali-G52 (GPU 13 ms vs 10 ms) |
| Disable the stock launcher | ❌ broke Home and the Settings key; Android soft-restarted |
| Launcher wallpaper effects | ⚠️ the GPU is the bottleneck, so keep blur and colour effects off |
