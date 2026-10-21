// /api/list?range=3M&res=3840x2160&cats=110&purity=100 - the whole Wallhaven toplist as one JSON array.
// Reading it from Wallhaven takes 20-40 s, so Vercel's CDN keeps the answer: fresh for 6 hours, then the stale
// copy is still served instantly while a new one is read in the background. /api/v1/search reads this.
import { fetchToplist, listParams } from '../lib/core.js';

export const config = { maxDuration: 120 };

export default async function handler(req, res) {
  const p = listParams(req.query);
  try {
    const items = await fetchToplist(p);
    res.setHeader('cache-control', 'public, max-age=0, s-maxage=21600, stale-while-revalidate=604800');
    res.status(200).json({ range: p.range, resolutions: p.res, categories: p.cats, purity: p.purity, fetched_at: new Date().toISOString(), items });
  } catch (err) {
    res.setHeader('cache-control', 'no-store');
    res.status(502).json({ error: String(err.message || err) });
  }
}
