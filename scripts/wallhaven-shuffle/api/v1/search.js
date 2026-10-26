// Wallhaven shuffle - feeds Projectivy's Booru wallpaper plugin a Wallhaven toplist (default: top of the last
// 3 months, 4K) a set at a time (SET_SIZE, default 24), walking through the whole list in a shuffled order.
//
// The plugin asks "<its Booru URL>/api/v1/search?q=...&sorting=random" about once an hour and shows the wallpapers
// it gets back. Wallhaven itself can't return a random set from a toplist, so this does:
//   - read the whole toplist from /api/list (CDN-cached, so this answers in well under a second),
//   - shuffle it with a seed that changes after every full pass,
//   - return the set that belongs to the current 55-minute window: set 1, 2, 3 ... until every wallpaper has been
//     shown, then a new shuffle starts. Windows when the TV is off are skipped; those come back in a later pass.
//   - if the list isn't ready within 5 s (first use, or the CDN dropped it), answer with six toplist pages that
//     change every window (never the same first page every time, which made the same wallpapers come back); the
//     list keeps being built in the background and is used from the next request on.
// This endpoint never talks to Wallhaven itself. The plugin's own query (tags, sorting) is ignored. Pick the list
// with the URL you give the plugin:
//   https://<your-app>.vercel.app                       -> defaults (3M, 3840x2160, all categories, SFW) or env vars
//   .../api/v1/search?atleast=3840x2160&ratios=16x9,16x10&maxw=3840&fav=50  -> any setting as a query parameter
//   https://<your-app>.vercel.app/r/6M/2560x1440        -> top of the last 6 months, 1440p
//   https://<your-app>.vercel.app/r/3M/3840x2160/20     -> ... only wallpapers with at least 20 favourites
import { PER_SET, getJson, listParams, listQuery, pageQuery, passes, pickSet, selfUrl, slotOf } from '../../lib/core.js';

const LIST_WAIT_MS = 5000; // the plugin gives up after ~10 s

const memory = new Map(); // list query -> { until, list }: a warm instance skips even the CDN read

const withTimeout = (promise, ms) => Promise.race([promise, new Promise((done) => setTimeout(done, ms, null))]);

// The list as { items, complete } (complete or partial), or null if nothing usable arrived in time.
async function getList(req, p) {
  const key = listQuery(p);
  const hit = memory.get(key);
  if (hit && Date.now() < hit.until) return hit.list;
  try {
    const r = await withTimeout(fetch(selfUrl(req, '/api/list', listQuery(p))), LIST_WAIT_MS);
    if (r && r.ok) {
      const j = await r.json();
      if (j.items?.length >= PER_SET) {
        if (memory.size >= 16 && !memory.has(key)) memory.delete(memory.keys().next().value); // a few lists at most
        const list = { items: j.items, complete: j.complete === true };
        memory.set(key, { until: Date.now() + (list.complete ? 3600e3 : 120e3), list });
        return list;
      }
    }
  } catch { /* fall through */ }
  return hit ? hit.list : null;
}

const agent = (req) => String(req.headers['user-agent'] || '-').slice(0, 60);

// The list isn't ready: six pages of the toplist instead (about an hour's worth after the filters), different ones
// in each window (pages 1-30, six at a time, so the same ones come back only every 5 windows). Pages are CDN-cached
// one by one, so this is quick. Page 1 tops it up if those come back nearly empty (a short range).
const FALLBACK_PAGES = 30; // the first ~700 of the toplist: inside any sensible favourites minimum
async function fallbackSet(req, p, now) {
  const slot = slotOf(now);
  const nums = [0, 1, 2, 3, 4, 5].map((i) => ((slot * 6 + i) % FALLBACK_PAGES) + 1);
  const getPage = (n) => getJson(selfUrl(req, '/api/page', `${pageQuery(p)}&n=${n}`), 4000).then((r) => (Array.isArray(r?.json?.data) ? r.json.data : []));
  let all = (await Promise.all(nums.map(getPage))).flat();
  if (all.filter(passes(p)).length < 8) all = all.concat(await getPage(1));
  const seen = new Set();
  all = all.filter((w) => w && w.id && !seen.has(w.id) && seen.add(w.id));
  const good = all.filter(passes(p));
  return { set: (good.length >= 8 ? good : all).slice(0, PER_SET), pages: nums };
}

export default async function handler(req, res) {
  const p = listParams(req.query);
  const { range, res: resolutions, fav } = p;
  try {
    const list = await getList(req, p);
    if (!list) { // list not ready yet: a few toplist pages that change every window
      const { set: top, pages } = await fallbackSet(req, p, Date.now());
      if (!top.length) throw new Error('the list is being built and Wallhaven is not answering right now');
      console.log(`list not ready, served pages ${pages.join(',')} (${top.length}) to ${agent(req)}`);
      res.setHeader('cache-control', 'no-store');
      return res.status(200).json({ data: top, meta: { current_page: 1, last_page: 1, per_page: PER_SET, total: top.length, query: null, seed: null, shuffle: { fallback: 'list is being built', pages } } });
    }
    const all = list.items, filtered = all.filter(passes(p));
    const items = filtered.length >= PER_SET ? filtered : all; // a too-strict minimum falls back to the whole list
    const now = Date.now();
    const { set, pass, index, sets, nextChange } = pickSet(items, now, listQuery(p));
    const secondsLeft = Math.max(30, Math.floor((nextChange - now) / 1000));
    console.log(`set ${index + 1}/${sets} (${set.length}) pass ${pass} pool ${items.length} for ${agent(req)} asking ${String(req.url).slice(0, 160)}`);
    // Cached until the next set, but only briefly while the list is still being filled in (e.g. right after a
    // deploy): otherwise a set from a partial list would stick for up to an hour.
    res.setHeader('cache-control', `public, max-age=0, s-maxage=${list.complete ? secondsLeft : Math.min(120, secondsLeft)}`);
    res.status(200).json({
      data: set,
      meta: {
        current_page: 1, last_page: 1, per_page: PER_SET, total: set.length, query: null, seed: null,
        shuffle: { toplist: range, resolutions: p.atleast ? `>=${p.atleast}` : resolutions, ratios: p.ratios, max_width: p.maxw, categories: p.cats, purity: p.purity, min_favorites: fav, pool: items.length, set: index + 1, sets, pass, next_set_at: new Date(nextChange).toISOString() },
      },
    });
  } catch (err) {
    res.setHeader('cache-control', 'no-store');
    res.status(502).json({ error: String(err.message || err) });
  }
}
