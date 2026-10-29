# Wallhaven Shuffle for Projectivy

A wallpaper plugin for [Projectivy Launcher](https://github.com/spocky/projectivy-plugin-wallpaper-provider) that shows
a wallhaven-shuffle list on the TV **in shuffled order with no repeats**: every wallpaper once per round, and none
comes back within 20 changes of itself, also across rounds. These are the same rules as the [Windows app](../windows).
A 45 KB app in plain Java, with no libraries.

![banner](res/drawable-xhdpi/banner.png)

**Why not the Booru plugin?** Projectivy keeps whatever a plugin returns for the plugin's cache time (the Booru
plugin: an hour) and at each change shows a **random** one of those. A set of 200 shown once a minute still repeats
a few times an hour, and the set depends on the service answering within seconds, right when the TV asks. This
plugin does it differently:

- **It returns one wallpaper at a time, kept for 30 s.** At every change Projectivy asks again and gets the next
  one in the rotation, so Projectivy's random pick has only one to choose from.
- **The list lives on the TV.** It's downloaded from the service's `/api/list` twice a day, with no time limit, and
  saved as a small file of just what the TV shows (~100 KB). A request reads that file in milliseconds. If the
  service or the network is down, the saved list keeps working.
- **It remembers where it is.** The round in progress survives restarts, updates and power cuts.
- **It's light.** Projectivy calls it once per change, only while the home screen is on. Measured on a 2 GB TV:
  ~1.5 s of CPU in the first 6 minutes, most of it the first list download.

## Install

1. Build it (below), or use a build you trust, and install it: `adb install -r WallhavenShuffle-Projectivy.apk`.
2. Set the list address, either on the TV (**Wallhaven Shuffle** in the app list, or Projectivy's **Configure** for
   the plugin) or over ADB:
   ```powershell
   adb shell am start -n io.github.muknan.wallhavenshuffle/.SettingsActivity --es list_url "https://<your-project>.vercel.app/api/list?range=1y&atleast=3840x2160&ratios=16x9,16x10&cats=110&purity=100&fav=50&maxw=0" --ei max_width 3840
   ```
   Use the same address as the Windows app, with `maxw=0`, and let the plugin apply the width limit (`max_width`).
   Then both ask for the same list, which the PC keeps warm in the service's cache.
3. In Projectivy: **Settings > Appearance > Wallpaper > Launcher wallpaper > Wallhaven Shuffle**. Its **Update delay**
   is how often the wallpaper changes.

To go back, choose **Booru Wallpaper Provider** (or anything else) there. Uninstall: `adb uninstall io.github.muknan.wallhavenshuffle`.

## Panel reset (MediaTek TVs)

The plugin also carries the fix for a MediaTek firmware fault: after a 25 fps video the panel stays at 50 Hz and the
home screen judders until a restart. `PanelResetActivity` plays a silent black 24 fps clip in a 2-pixel corner for
under a second, which puts the panel back to 60 Hz; nothing shows on screen. The TV's background helper starts it by
itself when needed ([docs/09](../../../docs/09-extras.md#home-screen-judders-after-a-video-until-you-restart)). By hand:

```powershell
adb shell am start -n io.github.muknan.wallhavenshuffle/.PanelResetActivity
```

## Next wallpaper from the remote

```powershell
adb shell am broadcast -a io.github.muknan.wallhavenshuffle.NEXT -n io.github.muknan.wallhavenshuffle/.NextReceiver
```

Bind that to a button with Key Mapper (action: Shell command, ADB mode). Projectivy shows the next one right away,
but it accepts a refresh only about every half minute, so a second press straight after does nothing. The settings
screen has the same button.

## Build

Needs a JDK 17 and the Android SDK command-line tools with `platforms;android-34` and `build-tools;34.0.0` (no
Android Studio, no Gradle). By default, `build.ps1` looks for them in `%USERPROFILE%\.whs-build\` (`jdk-*`, `sdk`).
`JAVA_HOME` and `ANDROID_SDK` override that.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -Test   # rotation tests on the PC, then bin\WallhavenShuffle-Projectivy.apk
```

The first build creates a signing key in `%USERPROFILE%\.whs-build\` (`whs-plugin.jks` and `.pass`). Keep both:
Android only installs an update that's signed with the same key. Neither ever goes into the repo.

## How it fits together

- `src/.../Shuffle.java`: the rotation (plain Java, tested by `test/ShuffleTest.java` on the PC).
- `src/.../ListStore.java`: downloads, compacts and reads the list.
- `src/.../ShuffleService.java`: what Projectivy calls (`getWallpapers` for TIME_ELAPSED; settings for
  Projectivy's backup).
- `src/.../SettingsActivity.java`, `NextReceiver.java`: the settings screen and "next wallpaper".
- `src/.../PanelResetActivity.java`: the 60 Hz panel reset (makes its clip on the TV with MediaCodec on first use).
- `aidl/` and `src/tv/projectivy/...`: Projectivy's plugin interface, from
  [projectivy-plugin-wallpaper-provider](https://github.com/spocky/projectivy-plugin-wallpaper-provider) (Apache 2.0),
  with the two data classes written in Java to the same layout.
