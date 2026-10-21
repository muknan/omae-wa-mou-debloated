# wallhaven-shuffle

A Wallhaven toplist for Projectivy's Booru wallpaper plugin, 24 wallpapers at a time, shuffled, walking through
the whole list. One dependency-free Vercel function.

- `api/v1/search.js` answers the plugin (`<Booru URL>/api/v1/search?...`) with the current set of 24. It never
  calls Wallhaven itself.
- `api/list.js` puts the whole toplist together from the cached pages.
- `api/page.js` reads one page from Wallhaven; each page is cached by Vercel's CDN on its own.
- `lib/core.js` has the shared code and the settings.

Setup, settings and how it behaves: [docs/06](../../docs/06-launcher-and-wallpapers.md#a-fresh-24-every-hour-through-the-whole-toplist-optional).
