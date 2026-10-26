// /api/list?range=6M&atleast=3840x2160&ratios=16x9,16x10&cats=110&purity=100&fav=50&maxw=3840 - the toplist as one
// JSON array, put together from the CDN-cached /api/page answers and filtered (favourites, width). Cached pages come
// back in milliseconds and are read 8 at a time; a page Wallhaven has to be asked for costs one request, one at a
// time, paced at ~1.3 s. The toplist runs roughly from most to least favourited, so with a favourites minimum it
// stops after four pages in a row with none left that qualify (stragglers still turn up after shorter gaps). Pages
// that fail (rate limit) are skipped: the list is then marked incomplete and cached for 2 minutes only, so the next
// call fills the gaps. A complete list is cached for 6 hours.
import { CACHE_GOOD, MAX_PAGES, PER_SET, getJson, listParams, pageQuery, passes, selfUrl, sleep } from '../lib/core.js';

export const config = { maxDuration: 300 };

// What the list keeps: an entry the apps can use (an id and a picture).
const usable = (w) => w && typeof w.id === 'string' && typeof w.path === 'string' && /^https?:\/\//.test(w.path);

export default async function handler(req, res) {
  const p = listParams(req.query);
  const started = Date.now();
  // { page, cached }: page is null if it failed (or, cachedOnly, if the CDN doesn't have it); cached = the CDN
  // answered and Wallhaven wasn't asked.
  const getPage = async (n, cachedOnly = false) => {
    const r = await getJson(selfUrl(req, '/api/page', `${pageQuery(p)}&n=${n}`), 40e3, cachedOnly ? { 'x-cached-only': '1' } : {});
    return { page: Array.isArray(r?.json?.data) ? r.json : null, cached: !!r?.cached };
  };
  try {
    const head = await getPage(1);
    const first = head.page;
    if (!first) throw new Error('Wallhaven did not answer for page 1');
    if (!head.cached) await sleep(1300);
    const pages = Math.min(first.last_page, MAX_PAGES);
    const all = [...first.data];
    let missing = 0, read = 1, belowRun = 0;
    const anyQualify = (data) => data.some((w) => (w.favorites || 0) >= p.fav);
    // Pages the CDN already has are read 8 at a time, cached copies only: a warm rebuild takes about a second,
    // well inside the search endpoint's 5-second wait (one at a time it took ~6 s, and the plugin got the
    // fallback instead). A page the CDN doesn't have is then asked for on its own, with a pause after it, so
    // Wallhaven is only ever asked one page at a time. A cold start (page 1 not cached) goes one at a time
    // from the start.
    const batch = head.cached ? 8 : 1;
    const polite = async (n) => { const g = await getPage(n); if (!g.cached) await sleep(1300); return g.page; };
    for (let n = 2; n <= pages;) {
      if (p.fav && belowRun >= 4) break; // the rest of the toplist is below the favourites minimum
      if (Date.now() - started > 240e3) { missing += pages - n + 1; break; } // stay inside the 5-minute limit
      const nums = [];
      for (let k = n; k < n + batch && k <= pages; k++) nums.push(k);
      n += nums.length;
      const got = batch > 1 ? await Promise.all(nums.map((k) => getPage(k, true))) : [{ page: null }];
      for (let i = 0; i < nums.length; i++) {
        let page = got[i]?.page;
        if (!page) {
          if (p.fav && belowRun >= 4) break; // reached the end meanwhile: no need to fetch the rest
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
    const complete = missing === 0 && items.length >= PER_SET;
    console.log(`list ${pageQuery(p)} fav>=${p.fav} maxw=${p.maxw}: read ${read}/${pages} pages, ${missing} missing, ${items.length} wallpapers in ${Math.round((Date.now() - started) / 1000)} s`);
    res.setHeader('cache-control', complete ? CACHE_GOOD : 'public, max-age=0, s-maxage=120');
    res.status(200).json({ range: p.range, resolutions: p.atleast ? `>=${p.atleast}` : p.res, ratios: p.ratios, max_width: p.maxw, min_favorites: p.fav, categories: p.cats, purity: p.purity, complete, missing_pages: missing, fetched_at: new Date().toISOString(), items });
  } catch (err) {
    res.setHeader('cache-control', 'no-store');
    res.status(502).json({ error: String(err.message || err) });
  }
}
