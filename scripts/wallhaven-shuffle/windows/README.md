# Wallhaven Shuffle for Windows

A small tray app that changes the desktop wallpaper every few minutes from a wallhaven-shuffle deployment's
`/api/list`, the same list the TV uses. It is one exe with no installer and no .NET SDK.

![icon](icon-preview.png)

- It shows every wallpaper once, in random order, before repeating. The next one is downloaded ahead of time.
- It changes with Windows' own cross-fade, the same one the built-in slideshow uses. This needs **Animation effects**
  on, under Settings > Accessibility > Visual effects. If the fade can't be used, it changes instantly.
- It waits while a game, a full-screen video or a presentation is running, and while the PC is locked. It can also
  wait while on battery.
- It follows Windows' light or dark theme and your accent colour.

**Use it**
- **Double-click** the tray icon for the next wallpaper.
- **Right-click** it for the everyday actions and **Open Wallhaven Shuffle**: a Windows 11 style flyout with the
  wallpaper, the controls ♡ ⏮ ⏯ ⏭ ⋯, and five settings that apply right away. Click anywhere else or press Esc to
  close it.
- Hotkeys:

  | Keys | Action |
  |---|---|
  | `Ctrl+Alt+Shift+→` | next |
  | `Ctrl+Alt+Shift+←` | previous |
  | `Ctrl+Alt+Shift+P` | pause |
  | `Ctrl+Alt+Shift+F` | favourite |

- Favourites are copied to `Pictures\Wallhaven Favourites`.
- In the **⋯** menu:
  - "Never show this one"
  - "Open on Wallhaven"
  - "Restore my original wallpaper"
  - **Advanced settings**, which opens `settings.ini` in Notepad. Changes apply a few seconds after you save.

**Build and install**
```powershell
powershell -ExecutionPolicy Bypass -File build.ps1      # -> bin\WallhavenShuffle.exe
```
1. Copy `bin\WallhavenShuffle.exe` to a folder you own, for example `%USERPROFILE%\WallhavenShuffle`, and run it.
2. Set `ListUrl` in Advanced settings, for example:
   `https://<your-project>.vercel.app/api/list?range=6M&atleast=3840x2160&ratios=16x9,16x10&cats=110&purity=100&fav=50&maxw=0`
   (`maxw=0`: a PC handles 5K/8K fine).
3. "Start with Windows" is switched on at the first run.

Settings, cache and log stay next to the exe. Avoid AppData: an app started from a packaged (MSIX) app has its
AppData writes redirected into that package, where Explorer can't read the wallpaper and shows black.
