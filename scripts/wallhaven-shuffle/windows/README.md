# Wallhaven Shuffle for Windows

A small tray app that changes the desktop wallpaper every few minutes from a wallhaven-shuffle deployment's
toplist, the same one the TV uses. It is one exe with no installer and no .NET SDK.

![icon](icon-preview.png)

- It shows every wallpaper once per round, in random order, and nothing comes back within 20 changes of itself
  (also across rounds). The flyout shows where you are ("33 of 1,178"); Previous counts down, Next counts up. The
  next one is downloaded ahead of time.
- Each change uses Windows' own smooth cross-fade, the one the built-in slideshow uses, when **Animation effects** is on
  in Settings > Accessibility > Visual effects.
- Wallpapers stay in their original quality. The app downloads the original file, and sets `JPEGImportQuality` to
  100, because otherwise Windows re-encodes every wallpaper as a JPEG at quality 85 (visible banding in dark scenes).
- Downloaded originals are kept, so each wallpaper downloads once. When the list changes, new wallpapers download as
  their turn comes. Files of wallpapers that left the list are deleted, but only after a *complete* list confirms it.
  If the cache reaches its limit (`CacheLimitGB`, 10 GB by default), wallpapers already shown this round go first.
  Set `CacheDir` in Advanced settings to keep the cache on another drive; it applies after a restart and the files
  move over. The tidy-up only ever touches its own files (named after the wallpaper id).
- It keeps going when the network doesn't. After sleep it waits a few seconds for Wi-Fi, and a wallpaper that
  can't be downloaded is swapped for one already in the cache.
- It waits while a game, a full-screen video or a presentation is running, and while the PC is locked. It can also
  wait while on battery, and put each wallpaper on the lock screen too.
- It follows Windows' light or dark theme and your accent colour.

**Use it**
- **Double-click** the tray icon for the next wallpaper. **Right-click** it for the everyday actions and **Open
  Wallhaven Shuffle**: a Windows 11 style flyout with the wallpaper, the controls 📌 ♡ ⏮ ⏯ ⏭ ⋯ and three settings
  (**Shuffle from**, **Toplist**, **Change wallpaper every**). ← and → also go back and forward. Click anywhere else
  or press Esc to close it.
- **Keep this wallpaper** (📌, the tray menu, or `Ctrl+Alt+Shift+K`): no more changes until you take it off again.
  It's remembered across restarts. Next, Previous or another source take it off as well; Pause is separate.
- **Shuffle from** is a list of switches: the **Toplist**, **Favourites**, your **playlists** and **folders of your
  own pictures**, in any mix. One round goes through all of them together, with no repeats, and each mix keeps its
  own round (the toplist alone carries on where it was).
- **Toplist** picks which toplist: quality (Full HD and up … 4K and up, exactly 4K, 5K, 8K), shape (widescreen,
  16:9, 16:10, ultrawide, any), period (past day … past year), popularity (minimum favourites), categories (General,
  Anime, People), an optional topic (a Wallhaven search, like *nature* or *night city*) and the largest size. These
  are the same choices as on the TV and in the remote; the list comes from `/api/list` with them.
- Hotkeys:

  | Keys | Action |
  |---|---|
  | `Ctrl+Alt+Shift+→` | next |
  | `Ctrl+Alt+Shift+←` | previous |
  | `Ctrl+Alt+Shift+P` | pause |
  | `Ctrl+Alt+Shift+F` | favourite |
  | `Ctrl+Alt+Shift+K` | keep this wallpaper / stop keeping it |

**Playlists**
- A playlist is a normal folder in `Playlists`, next to the cache folder (`PlaylistsDir` in Advanced settings to put
  them elsewhere); Favourites are `Pictures\Wallhaven Favourites` (`FavouritesDir` to change it). Make one with
  **New playlist…** (Shuffle from, Add to playlist, or ⋯ > Playlists).
- **Add to playlist** (tray menu, or ⋯) puts the wallpaper on screen into a playlist; a ✓ marks the ones it's in.
  Pictures are added as **hard links** to the downloaded original: the picture shows up in the folder like any
  file, but takes no extra space, and stays there if the cache later drops it. On another drive it's copied instead.
- The folders and the playlists stay in step both ways: a picture copied into a playlist's folder in Explorer joins
  the playlist (within a minute), one you delete leaves it, and a new folder becomes a new playlist. Taken out of a
  playlist means to the Recycle Bin. Only files the app synced before are ever removed.
- **Never show this one** (⋯) puts the wallpaper in **Hidden**, which every source leaves out. ⋯ > **Show hidden
  wallpapers again** clears it; the remote shows Hidden and can let them back one by one.
- **Add a folder of your pictures…** shuffles any folder (top level; JPG, PNG, WebP, BMP, GIF) where it is: nothing
  is copied. Pictures of your own stay on this PC; playlists sync Wallhaven wallpapers only.
- ⋯ > **Playlists** lists each playlist and folder with **Open its folder**, **Rename…** and **Delete…** (deleted
  playlists go to the Recycle Bin; a wallpaper still in the cache or another playlist stays there).

**The remote and sync** (with a deployment that has sync set up: see the
[service README](../README.md#the-remote-and-sync))
- Paired, the playlists, Favourites and Hidden are the same on the PC, the TV and the
  [web remote](../README.md#the-remote-and-sync), and each device still picks what it shuffles on its own. The remote
  shows what the PC shows and can change everything above, send Next / Previous, and send a wallpaper to the PC.
- **Remote** (tray menu, or ⋯): **Open the remote** (signs the browser in for you), **Pair a device…** (type the
  code a TV or phone shows), **Connect with the sync key…** or **Pair with a code…** to connect this PC, **Service
  address…**, and **Sign out of sync**. Signed out, the PC keeps its playlists and carries on on its own.
- ⋯ > **Show on ▸** sends the wallpaper on screen to the TV (or another PC) right away.
- Connecting merges: playlists with the same name become one, and Favourites and Hidden are combined.
- It checks in every 20 seconds, every 3 while a remote is open, with one small request; playlists download only
  when they changed.

**⋯ menu**: Add to playlist, Show on, Show in folder, Open on Wallhaven, Never show this one, Playlists, Show hidden
wallpapers again, Remote, **Settings** (picture fit, the pause and lock screen switches, Start with Windows, and
**Advanced settings**, which opens `settings.ini` in Notepad; saved changes apply within a few seconds), and Restore
my original wallpaper (a copy of it is kept next to the app at the first run).

**Build and install**
```powershell
powershell -ExecutionPolicy Bypass -File build.ps1      # -> bin\WallhavenShuffle.exe
```
1. Copy `bin\WallhavenShuffle.exe` to a folder you own, for example `%USERPROFILE%\WallhavenShuffle`, and run it.
2. In Advanced settings, set `Server` to your deployment (`https://<your-project>.vercel.app`), or ⋯ > Remote >
   Service address. To connect sync right away, also put the deployment's `SYNC_KEY` in `SyncKey` (it's cleared once
   it has worked).
3. "Start with Windows" is switched on at the first run.

Coming from 2.1, the settings move over by themselves: `ListUrl` becomes `Server` plus the toplist filters (the same
list), the one source becomes the mix, existing playlist folders become playlists, and "Never show this one"
moves to Hidden.

Settings, cache and log stay next to the exe. Avoid AppData: an app started from a packaged (MSIX) app has its
AppData writes redirected into that package, where Explorer can't read the wallpaper and shows black.
