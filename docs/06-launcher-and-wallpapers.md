# 6. Launcher & wallpapers

> **Easier:** double-click `Start-Here.cmd` and the setup wizard does this for you. This page explains what it does, and how to do it by hand.

<p>
  <img src="../screenshots/home-02.jpg" width="49%" />
  <img src="../screenshots/wallhaven-plugin.jpg" width="49%" />
</p>

## Projectivy Launcher
[Projectivy Launcher](https://play.google.com/store/apps/details?id=com.spocky.projengmenu) replaces the ad-heavy Google TV home. Install it from the Play Store, then follow its setup to make it the default home and enable its accessibility service (that's what lets it take over the Home button).

- Keep the **stock Google TV launcher enabled**. On the test TV, Projectivy's Home-button takeover and the Settings key depend on it (see [02](02-audit-and-debloat.md#lessons-from-the-test-tv)).
- Use Projectivy's own **backup** before experimenting.

## Rotating wallpapers from Wallhaven
The **Booru Wallpaper Provider** plugin for Projectivy (package `nl.kurocon.plugin.wallpaperprovider.booru`) fetches
wallpapers from Booru-style sites, Wallhaven included.

In the plugin's settings:
| Field | Value |
|---|---|
| Booru Type | `Wallhaven` |
| Booru URL | 4K TV: `https://wallhaven.cc/api/v1/search?sorting=toplist&topRange=6M&resolutions=3840x2160#`<br>1080p TV: `https://wallhaven.cc/api/v1/search?sorting=toplist&topRange=6M&resolutions=1920x1080#` |
| Search query | leave empty (with the URL above it's ignored; put `&q=...` in the URL instead) |
| User settings | optional Wallhaven login/API key |

That's **the most popular wallpapers of the last 6 months, in exactly your TV's resolution**, safe-for-work only.
The wizard prints the right one for your TV (it reads the panel resolution with `adb shell wm size`).

Then in Projectivy: **Settings > Appearance > Wallpaper > Launcher wallpaper**, scroll to the bottom of the list and
pick **Booru Wallpaper Provider**.

### Why the URL looks like that
The plugin builds its request as `<Booru URL>/api/v1/search?q=<search>&sorting=random`, and the search box is
URL-encoded, so Wallhaven's filters can't go in there. The Booru URL field is used as typed, so the filters go
there instead, and the trailing `#` turns everything the plugin appends into a URL fragment, which is never sent
to the server. Wallhaven only sees your filters.

Change it to taste (all [Wallhaven API](https://wallhaven.cc/help/api) parameters work):
| Want | Change |
|---|---|
| Top of the last month / 3 months / year | `topRange=1M` / `3M` / `1y` |
| Anime only / no anime | add `&categories=010` / `&categories=100` |
| A theme | add `&q=nature` (or any Wallhaven tag) before the `#` |
| Random instead of top | `sorting=random` (and drop `topRange`) |

Exact resolution matters on small TVs: a 6K image decodes to about 85 MB of memory, a 4K one to about 33 MB.

### After changing the URL
Projectivy caches the list and only re-asks the plugin every so often. To switch right away: **Launcher wallpaper >
pick another source, then pick Booru Wallpaper Provider again**. Check **Show wallpapers**: the names are Wallhaven
image IDs, so you can look any of them up at `https://wallhaven.cc/w/<id>`.

### Ask for the next wallpaper over ADB
Projectivy listens for the same "wallpaper updated" broadcast the plugin sends:
```powershell
adb shell am broadcast -a tv.projectivy.plugin.action.WALLPAPER_PROVIDER_UPDATED -n com.spocky.projengmenu/.receivers.WallpaperProviderUpdateReceiver
```
Projectivy decides when it actually changes, so expect a new wallpaper about every 2nd or 3rd request (wait ~25 s between tries). That's how the screenshots here were made.

## Performance notes (measured)
On a 2 GB TV with a small Mali GPU, Projectivy is **GPU-bound**: the UI thread needs about 3 ms per frame, but the GPU needs about 21 ms. That's ~40 fps instead of 60.
- Wallpaper **blur / brightness / contrast / saturation** effects cost GPU time on every frame; keep them neutral.
- A **still image** is cheaper than a GIF or video background.
- "First navigation after idle is janky, then smooth" is memory being paged back in from zram. Debloating and keeping background apps asleep reduce it.
