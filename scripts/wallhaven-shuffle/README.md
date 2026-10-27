# wallhaven-shuffle

A Wallhaven toplist for Projectivy's Booru wallpaper plugin, a set at a time (`SET_SIZE`, default 200), shuffled, walking through
the whole list. One dependency-free Vercel function.

- `api/v1/search.js` answers the plugin (`<Booru URL>/api/v1/search?...`) with the current set. It never
  calls Wallhaven itself.
- `api/list.js` puts the whole toplist together from the cached pages.
- `api/page.js` reads one page from Wallhaven; each page is cached by Vercel's CDN on its own.
- `lib/core.js` has the shared code and the settings.

Setup, settings and how it behaves: [docs/06](../../docs/06-launcher-and-wallpapers.md#a-fresh-set-every-hour-through-the-whole-toplist-optional).

## On a Windows PC

[`windows/`](windows) is a small tray app that uses the same list as the desktop wallpaper: see its
[README](windows/README.md).

## On the TV, in order (recommended)

[`projectivy-plugin/`](projectivy-plugin) is a small Projectivy wallpaper plugin of our own. It keeps the list on the
TV and hands Projectivy one wallpaper at a time, in the Windows app's order: every one once per round, no repeats.
With the Booru plugin, Projectivy picks at random from each hour's set. See its [README](projectivy-plugin/README.md).
