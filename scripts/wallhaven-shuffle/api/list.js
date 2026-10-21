// /api/list?range=6M&res=3840x2160&cats=110&purity=100 - the whole toplist as one JSON array, put together from the
// CDN-cached /api/page answers. Cached pages come back in milliseconds; a page Wallhaven has to be asked for costs
// one request, paced at ~1.3 s. Pages that fail (rate limit) are skipped: the list is then marked incomplete and
// cached for 2 minutes only, so the next call fills the gaps. A complete list is cached for 6 hours.
import { CACHE_GOOD, MAX_PAGES, PER_SET, listParams, selfUrl, sleep } from '../lib/core.js';

export const config = { maxDuration: 300 };

export default async function handler(req, res) {
  const p = listParams(req.query);
  const started = Date.now();
  const getPage = async (n) => {
    const r = await fetch(selfUrl(req, '/api/page', p, `&n=${n}`));
    if (r.headers.get('x-vercel-cache') !== 'HIT') await sleep(1300); // it went to Wallhaven: keep the pace polite
    return r.ok ? r.json() : null;
  };
  try {
    const first = await getPage(1);
    if (!first) throw new Error('Wallhaven did not answer for page 1');
    const pages = Math.min(first.last_page, MAX_PAGES);
    const all = [...first.data];
    let missing = 0;
    for (let n = 2; n <= pages; n++) {
      if (Date.now() - started > 240e3) { missing += pages - n + 1; break; } // stay inside the 5-minute limit
      const page = await getPage(n);
      if (page) all.push(...page.data); else missing++;
    }
    const seen = new Set();
    const items = all.filter((w) => w && w.id && !seen.has(w.id) && seen.add(w.id)).sort((a, b) => (a.id < b.id ? -1 : 1));
    const complete = missing === 0 && items.length >= PER_SET;
    console.log(`list ${p.range} ${p.res} ${p.cats}: ${pages} pages, ${missing} missing, ${items.length} wallpapers in ${Math.round((Date.now() - started) / 1000)} s`);
    res.setHeader('cache-control', complete ? CACHE_GOOD : 'public, max-age=0, s-maxage=120');
    res.status(200).json({ range: p.range, resolutions: p.res, categories: p.cats, purity: p.purity, complete, missing_pages: missing, fetched_at: new Date().toISOString(), items });
  } catch (err) {
    res.setHeader('cache-control', 'no-store');
    res.status(502).json({ error: String(err.message || err) });
  }
}
