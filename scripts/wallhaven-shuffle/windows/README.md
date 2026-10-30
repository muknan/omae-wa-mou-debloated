# Wallhaven Shuffle for Windows

A small tray app that changes the desktop wallpaper every few minutes from a wallhaven-shuffle deployment's
`/api/list`, the same list the TV uses. It is one exe with no installer and no .NET SDK.

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
  move over. If that drive isn't there at start, the app uses the folder next to it until it is. The tidy-up only
  ever touches its own files (named after the wallpaper id), never anything else in that folder.
- "Show on the lock screen too" puts each wallpaper on the Windows lock screen as well, through Windows' own lock
  screen API. It replaces Windows Spotlight on the lock screen while it's on.
- It keeps going when the network doesn't. After sleep it waits a few seconds for Wi-Fi, and a wallpaper that
  can't be downloaded is swapped for one already in the cache. The list refreshes in the background and never holds
  up a change.
- It waits while a game, a full-screen video or a presentation is running, and while the PC is locked. It can also
  wait while on battery.
- It follows Windows' light or dark theme and your accent colour.

**Use it**
- **Double-click** the tray icon for the next wallpaper.
- **Right-click** it for the everyday actions and **Open Wallhaven Shuffle**: an acrylic Windows 11 style flyout with the
  wallpaper, the controls 📌 ♡ ⏮ ⏯ ⏭ ⋯, and seven settings that apply right away. In the flyout, ← and → also go back
  and forward, and ↑/↓ change a focused dropdown. Click anywhere else or press Esc to close it.
- **Keep this wallpaper** (📌 in the flyout, the tray menu, or `Ctrl+Alt+Shift+K`): no more changes until you take it
  off again. It's remembered across restarts, and the cache never deletes the kept one. Next, Previous or picking
  another source takes it off as well (you're moving on); Pause is separate.

**Playlists**
- **Shuffle from** (the first setting, also in the tray menu) picks what it shuffles: the Wallhaven list,
  **Favourites**, one of your **playlists**, or **a folder of your own pictures**. Each keeps its own round, so going
  back to the list carries on where it was. Switching shows one from the new source right away.
- A playlist is a normal folder in `Playlists`, next to the cache folder (`PlaylistsDir` in Advanced settings to put
  them elsewhere). Make one with **New playlist…** (in Shuffle from, Add to playlist, or ⋯ > Playlists).
- **Add to playlist** (tray menu, or ⋯) puts the wallpaper on screen into a playlist; a ✓ marks the ones it's in, and
  clicking a ✓ takes it out (to the Recycle Bin). Pictures are added as **hard links** to the downloaded original: the
  picture shows up in the folder like any file, but takes no extra space, and stays there if the cache later drops it.
  That needs the playlist on the same drive as the cache, which is why playlists live next to it; on another drive
  it's copied instead (the log says which).
- **⋯ > Playlists** also renames or deletes the playlist being shuffled (deleted ones go to the Recycle Bin; a
  wallpaper still in the cache or another playlist stays there) and opens the playlists or favourites folder.
- **Add a folder of your pictures…** shuffles any folder (top level; JPG, PNG, WebP, BMP, GIF) where it is: nothing
  is copied, and it works without a Wallhaven list. "Stop using" takes it off the list and never touches the folder.
  Pictures copied into a playlist or folder by hand show up within 20 seconds.
- Favourites are hard-linked too when `Pictures` is on the same drive as the cache, otherwise copied as before.
  Pictures saved by this app are named after their Wallhaven id, so "Open on Wallhaven" and the resolution / ♥ pill
  still work for them; for your own pictures the pill shows the file name and clicking the picture shows it in
  Explorer (⋯ > **Show in folder** does that for any wallpaper).
- Pressing Next or Previous again while a change is still fading is remembered: it happens right after (up to 5).
- Hotkeys:

  | Keys | Action |
  |---|---|
  | `Ctrl+Alt+Shift+→` | next |
  | `Ctrl+Alt+Shift+←` | previous |
  | `Ctrl+Alt+Shift+P` | pause |
  | `Ctrl+Alt+Shift+F` | favourite |
  | `Ctrl+Alt+Shift+K` | keep this wallpaper / stop keeping it |

- Favourites go to `Pictures\Wallhaven Favourites`.
- In the **⋯** menu:
  - "Add to playlist", "Show in folder", "Open on Wallhaven", "Never show this one"
  - "Playlists": new, add a folder, rename / delete the one being shuffled, open the folders
  - "Show hidden wallpapers again", which undoes every "Never show this one"
  - "Restore my original wallpaper" (a copy of it is kept next to the app at the first run, since Windows' own copy
    is replaced with each new wallpaper)
  - **Advanced settings**, which opens `settings.ini` in Notepad. Changes apply a few seconds after you save.

**Build and install**
```powershell
powershell -ExecutionPolicy Bypass -File build.ps1      # -> bin\WallhavenShuffle.exe
```
1. Copy `bin\WallhavenShuffle.exe` to a folder you own, for example `%USERPROFILE%\WallhavenShuffle`, and run it.
2. Set `ListUrl` in Advanced settings, for example:
   `https://<your-project>.vercel.app/api/list?range=1y&atleast=3840x2160&ratios=16x9,16x10&cats=110&purity=100&fav=50&maxw=0`
   (`maxw=0`: a PC handles 5K/8K fine).
3. "Start with Windows" is switched on at the first run.

Settings, cache and log stay next to the exe. Avoid AppData: an app started from a packaged (MSIX) app has its
AppData writes redirected into that package, where Explorer can't read the wallpaper and shows black.
