# wallhaven-shuffle

A Wallhaven toplist for the TV and the PC, shuffled, walking through the whole list, and a web remote that keeps
both in step. One Vercel project with no dependencies.

- `api/list.js` puts the whole toplist together from the cached pages; `api/page.js` reads one page from Wallhaven
  (each page is cached by Vercel's CDN on its own). The apps ask for `/api/list?<filters>`.
- `api/v1/search.js` answers Projectivy's Booru plugin (`<Booru URL>/api/v1/search?...`) with the current set.
- `api/sync.js` (`/api/sync/<action>`) is the sync for the apps and the remote; `public/` is the remote itself.
- `lib/core.js` has the shared list code and settings, `lib/sync.js` the sync.

Setup, settings and how it behaves: [docs/06](../../docs/06-launcher-and-wallpapers.md#a-fresh-set-every-hour-through-the-whole-toplist-optional).

## On the TV, in order (recommended)

[`projectivy-plugin/`](projectivy-plugin) is a small Projectivy wallpaper plugin of our own. It keeps the list on the
TV and hands Projectivy one wallpaper at a time: every one once per round, no repeats. See its
[README](projectivy-plugin/README.md). With the Booru plugin instead, Projectivy picks at random from each hour's set.

## On a Windows PC

[`windows/`](windows) is a small tray app with the same list as the desktop wallpaper, "Keep this wallpaper",
favourites, playlists (folders of hard links, no duplicate files) and folders of your own: see its
[README](windows/README.md).

## The remote and sync

Open the deployment's address on a phone (or any browser) for the remote:

- **Now**: what each device shows, with Keep, Previous, Pause, Next and ♡; what it shuffles (any mix of the toplist,
  Favourites and playlists, and a PC's own folders), its toplist filters, and how often it changes. Tap the picture
  for the rest: show it on another device, add it to a playlist, never show it again, open it on Wallhaven.
- **Playlists**: the same on every device. Each device picks which ones it shuffles. Add a wallpaper by its
  Wallhaven link; Hidden lists what's never shown, to let ones back.
- **Browse**: a device's toplist as a grid, by most favourites, newest or shuffled, to add to playlists.
- **Devices**: pair a TV, PC or browser with the 6-digit code it shows; rename or sign out any of them.

It works as an app on the phone's home screen (Add to Home screen). Changes reach a device within its next check-in:
1.5 s while the remote is open, otherwise about every 10 s. Next shows the coming wallpaper at once
(dimmed) and then where it's at: Sending…, Waiting for the device to check in…, Changing in 0:12 (a TV waits until
Projectivy listens again, at most about 30 s). The TV changes wallpapers only on Projectivy's home screen: in another
app the remote says "Not on the home screen".

**Set it up**
1. In the Vercel project: **Storage > Create Database > Upstash for Redis** (free plan, in the functions' region,
   connected to the project). That adds `KV_REST_API_URL` and `KV_REST_API_TOKEN`.
2. Add an environment variable `SYNC_KEY`: a long random password of your own (it signs in the first device; every
   other one pairs with a code). Then deploy again.
3. Connect the PC (⋯ > Remote > Connect with the sync key, or `SyncKey` in its settings), then **Remote > Open the
   remote** on the PC signs the browser in. Pair the TV (Wallhaven Shuffle on the TV > Pair) and the phone (open the
   address) with the codes they show: **Devices > Pair a device** in the remote, or **Remote > Pair a device** on the PC.

Sign-in uses one token per device, kept as a hash on the service; a signed-out device stops at once. Pairing codes
last 10 minutes and work once; sign-in links from the PC, 2 minutes. The free Upstash plan (500k commands a month)
is plenty: a device's check-in is one read, and the playlists download only when they changed.

**Working on it**: `node --test test/sync.test.mjs` runs the sync's tests on an in-memory store, and
`LIST_ORIGIN=https://<your-project>.vercel.app node test/dev-server.mjs --demo` serves the remote locally with a
pretend TV and PC (it prints a sign-in link).
