// /api/list?range=6M&atleast=3840x2160&ratios=16x9,16x10&cats=110&purity=100&fav=50&maxw=3840 - the toplist as one
// JSON array, put together from the CDN-cached /api/page answers and filtered (favourites, width). Cached pages come
// back in milliseconds and are read 8 at a time; a page Wallhaven has to be asked for costs one request, one at a
// time, paced at ~1.3 s. The toplist runs roughly from most to least favourited, so with a favourites minimum it
// stops after four pages in a row with none left that qualify (stragglers still turn up after shorter gaps). Pages
// that fail (rate limit) are skipped: the list is then marked incomplete and cached for 2 minutes only, so the next
// call fills the gaps. A complete list is cached for 6 hours.
//
// /api/list?sort=fav&atleast=...&fav=200&favmax=1000 - Most favourited: all of Wallhaven sorted by favourites (half a
// million with no filters, ~48,000 at 4K widescreen), so it is never read whole. Favourites only go down from page to
// page, so a range is one run of pages: its first and last page are found by halving (about 11 pages each, cached
// like any other), and only that run is read, at most MAX_PAGES (the 4,800 most favourited in the range).
import { CACHE_GOOD, MAX_PAGES, getJson, listParams, pageQuery, passes, selfUrl, sleep } from '../lib/core.js';

export const config = { maxDuration: 300 };

// What the list keeps: an entry the apps can use (an id and a picture).
const usable = (w) => w && typeof w.id === 'string' && typeof w.path === 'string' && /^https?:\/\//.test(w.path);
const favOf = (w) => w?.favorites || 0;

// The smallest page in lo..hi where ok(page) holds, given that it holds from some page on (hi + 1 if none does).
// A page that can't be read ends the search with what is known so far (null).
export async function firstPage(lo, hi, ok, read) {
  let a = lo, b = hi + 1;
  while (a < b) {
    const mid = Math.floor((a + b) / 2);
    const page = await read(mid);
    if (!page) return null;
    if (!page.data.length || ok(page.data)) b = mid; else a = mid + 1;
  }
  return a;
}

export default async function handler(req, res) {
  const p = listParams(req.query);
  const started = Date.now();
  // { page, cached }: page is null if it failed (or, cachedOnly, if the CDN doesn't have it); cached = the CDN
  // answered and Wallhaven wasn't asked.
  const getPage = async (n, cachedOnly = false) => {
    const r = await getJson(selfUrl(req, '/api/page', `${pageQuery(p)}&n=${n}`), 40e3, cachedOnly ? { 'x-cached-only': '1' } : {});
    return { page: Array.isArray(r?.json?.data) ? r.json : null, cached: !!r?.cached };
  };
  const polite = async (n) => { const g = await getPage(n); if (!g.cached) await sleep(1300); return g.page; };
  try {
    const head = await getPage(1);
    const first = head.page;
    if (!first) throw new Error('Wallhaven did not answer for page 1');
    if (!head.cached) await sleep(1300);
    const lastPage = first.last_page || 1;
    // The pages to read: the toplist from the top; Most favourited, the run of pages its range covers.
    let from = 1, to = Math.min(lastPage, MAX_PAGES), inRange = null, capped = false;
    if (p.sort === 'fav') {
      // the first page that reaches down to the maximum, and the first page that is wholly below the minimum
      from = p.favmax ? await firstPage(1, lastPage, (d) => favOf(d[d.length - 1]) <= p.favmax, polite) : 1;
      const after = from == null ? null : p.fav ? await firstPage(from, lastPage, (d) => favOf(d[0]) < p.fav, polite) : lastPage + 1;
      if (from == null || after == null) throw new Error('Wallhaven did not answer while finding the range');
      const last = after - 1;   // the page before it may still cross the minimum: it is read, and filtered
      inRange = Math.max(last - from + 1, 0);
      to = Math.min(last, from + MAX_PAGES - 1);
      capped = last > to;
    }
    const all = from === 1 ? [...first.data] : [];
    let missing = 0, read = 1, belowRun = 0;
    const anyQualify = (data) => data.some((w) => favOf(w) >= p.fav);
    // Pages the CDN already has are read 8 at a time, cached copies only: a warm rebuild takes about a second,
    // well inside the search endpoint's 5-second wait (one at a time it took ~6 s, and the plugin got the
    // fallback instead). A page the CDN doesn't have is then asked for on its own, with a pause after it, so
    // Wallhaven is only ever asked one page at a time. A cold start (page 1 not cached) goes one at a time
    // from the start.
    const batch = head.cached ? 8 : 1;
    const toplistEnded = () => p.sort !== 'fav' && p.fav && belowRun >= 4; // the rest of the toplist is below the minimum
    for (let n = Math.max(from, 2); n <= to;) {
      if (toplistEnded()) break;
      if (Date.now() - started > 240e3) { missing += to - n + 1; break; } // stay inside the 5-minute limit
      const nums = [];
      for (let k = n; k < n + batch && k <= to; k++) nums.push(k);
      n += nums.length;
      const got = batch > 1 ? await Promise.all(nums.map((k) => getPage(k, true))) : [{ page: null }];
      for (let i = 0; i < nums.length; i++) {
        let page = got[i]?.page;
        if (!page) {
          if (toplistEnded()) break; // reached the end meanwhile: no need to fetch the rest
          page = await polite(nums[i]);
        }
        read++;
        if (!page) { missing++; continue; }
        all.push(...page.data);
        belowRun = anyQualify(page.data) ? 0 : belowRun + 1;
      }
    }
    const seen = new Set();
    const items = all.filter((w) => usable(w) && !seen.has(w.id) && seen.add(w.id)).filter(passes(p)).sort((a, b) => (a.id < b.id ? -1 : 1));
    const complete = missing === 0 && items.length > 0; // nothing missing: complete, however short the list
    console.log(`list ${pageQuery(p)} fav>=${p.fav}${p.favmax ? `<=${p.favmax}` : ''} maxw=${p.maxw}: pages ${from}-${to} of ${lastPage}, read ${read}, ${missing} missing, ${items.length} wallpapers in ${Math.round((Date.now() - started) / 1000)} s`);
    res.setHeader('cache-control', complete ? CACHE_GOOD : 'public, max-age=0, s-maxage=120');
    res.status(200).json({
      sort: p.sort, range: p.sort === 'fav' ? null : p.range, resolutions: p.atleast ? `>=${p.atleast}` : p.res, ratios: p.ratios, max_width: p.maxw,
      min_favorites: p.fav, max_favorites: p.favmax, categories: p.cats, purity: p.purity, topic: p.q,
      // Most favourited: about how many the range holds (24 a page), and whether only its top MAX_PAGES were read
      ...(p.sort === 'fav' ? { in_range: inRange * 24, capped } : {}),
      complete, missing_pages: missing, fetched_at: new Date().toISOString(), items,
    });
  } catch (err) {
    res.setHeader('cache-control', 'no-store');
    res.status(502).json({ error: String(err.message || err) });
  }
}
