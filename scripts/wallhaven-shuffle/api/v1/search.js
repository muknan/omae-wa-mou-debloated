// Wallhaven shuffle - feeds Projectivy's Booru wallpaper plugin a Wallhaven toplist (default: top of the last
// 3 months, 4K) 24 wallpapers at a time, walking through the whole list in a shuffled order.
//
// The plugin asks "<its Booru URL>/api/v1/search?q=...&sorting=random" about once an hour and shows the 24
// wallpapers it gets back. Wallhaven itself can't return a random 24 from a toplist, so this does:
//   - read the whole toplist from /api/list (kept by Vercel's CDN, so this answers in well under a second),
//   - shuffle it with a seed that changes after every full pass,
//   - return the set that belongs to the current hour: set 1, 2, 3 ... until every wallpaper has been shown,
//     then a new shuffle starts. Hours when the TV is off are skipped; those wallpapers come back in a later pass.
// The plugin's own query (tags, sorting) is ignored. Pick the list with the URL you give the plugin:
//   https://<your-app>.vercel.app                       -> defaults (3M, 3840x2160, all categories, SFW) or env vars
//   https://<your-app>.vercel.app/r/6M/2560x1440        -> top of the last 6 months, 1440p
//   https://<your-app>.vercel.app/r/3M/3840x2160/20     -> ... only wallpapers with at least 20 favourites
import { fetchToplist, listParams, pickSet, PER_SET } from '../../lib/core.js';

export const config = { maxDuration: 300 };

const memory = new Map(); // "range|res" -> { at, items }: a warm instance skips even the CDN read

async function getList(req, p) {
  const key = `${p.range}|${p.res}|${p.cats}|${p.purity}`;
  const hit = memory.get(key);
  if (hit && Date.now() - hit.at < 3600e3) return hit.items;
  let items;
  try {
    const host = req.headers['x-forwarded-host'] || req.headers.host;
    const r = await fetch(`https://${host}/api/list?range=${p.range}&res=${encodeURIComponent(p.res)}&cats=${p.cats}&purity=${p.purity}`);
    if (!r.ok) throw new Error(`list ${r.status}`);
    items = (await r.json()).items;
  } catch {
    items = hit ? hit.items : await fetchToplist(p); // CDN miss and list endpoint down: read it directly
  }
  memory.set(key, { at: Date.now(), items });
  return items;
}

export default async function handler(req, res) {
  const p = listParams(req.query);
  const { range, res: resolutions, fav } = p;
  try {
    const all = await getList(req, p);
    const items = fav ? all.filter((w) => (w.favorites || 0) >= fav) : all;
    const now = Date.now();
    const { set, pass, index, sets, nextChange } = pickSet(items.length >= PER_SET ? items : all, now, `${range}|${resolutions}|${fav}|${p.cats}|${p.purity}`);
    const secondsLeft = Math.max(30, Math.floor((nextChange - now) / 1000));
    console.log(`set ${index + 1}/${sets} pass ${pass} pool ${items.length} for ${String(req.headers['user-agent'] || '-').slice(0, 60)}`);
    res.setHeader('cache-control', `public, max-age=0, s-maxage=${secondsLeft}`);
    res.status(200).json({
      data: set,
      meta: {
        current_page: 1, last_page: 1, per_page: PER_SET, total: set.length, query: null, seed: null,
        shuffle: { toplist: range, resolutions, categories: p.cats, purity: p.purity, min_favorites: fav, pool: items.length, set: index + 1, sets, pass, next_set_at: new Date(nextChange).toISOString() },
      },
    });
  } catch (err) {
    res.setHeader('cache-control', 'no-store');
    res.status(502).json({ error: String(err.message || err) });
  }
}
