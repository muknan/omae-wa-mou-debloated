// /api/page?range=6M&atleast=3840x2160&ratios=16x9,16x10&cats=110&purity=100&n=3 - one page (24) of the toplist.
// Each page is cached by the CDN on its own, so the whole list is rebuilt one cheap page at a time and progress
// survives Wallhaven's rate limit: a page that fails isn't cached and is simply asked for again next time.
import { CACHE_GOOD, MAX_PAGES, fetchToplistPage, listParams } from '../lib/core.js';

export default async function handler(req, res) {
  const p = listParams(req.query);
  const n = Math.min(Math.max(parseInt(req.query.n, 10) || 1, 1), MAX_PAGES);
  try {
    const page = await fetchToplistPage(p, n);
    res.setHeader('cache-control', CACHE_GOOD);
    res.status(200).json(page);
  } catch (err) {
    res.setHeader('cache-control', 'no-store');
    res.status(503).json({ error: String(err.message || err) });
  }
}
