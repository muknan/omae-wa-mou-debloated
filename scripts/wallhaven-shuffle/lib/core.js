// Shared code for the endpoints: settings, reading one Wallhaven toplist page, and picking the current set of 24.
// Env vars (all optional): TOP_RANGE, RESOLUTIONS (exact) or ATLEAST (minimum, e.g. 3840x2160) with RATIOS
// (e.g. 16x9,16x10), MAX_WIDTH (e.g. 3840: skip 5K/8K, which small TVs can't decode comfortably), MIN_FAVORITES,
// CATEGORIES (e.g. 110 = no People), PURITY, SLOT_MINUTES, WALLHAVEN_API_KEY.
// Each can also come from the request (?range=&res=&atleast=&ratios=&maxw=&fav=&cats=&purity=), which wins.

export const PER_SET = 24;
export const MAX_PAGES = 100; // 2400 wallpapers (6 months of 4K is ~1600); keeps a long range (1y) bounded
// CDN caching: a good answer stays fresh for 6 hours, then is served stale (instantly) for up to a week while a
// new one is fetched in the background.
export const CACHE_GOOD = 'public, max-age=0, s-maxage=21600, stale-while-revalidate=604800';
// A bit shorter than Projectivy's ~1-hour refresh, so two refreshes never land in the same window (which would
// repeat a set). The price: now and then a set is skipped; it comes back in a later pass.
const SLOT_MINUTES = Number(process.env.SLOT_MINUTES || 55);
const API_KEY = process.env.WALLHAVEN_API_KEY || '';
const RANGES = new Set(['1d', '3d', '1w', '1M', '3M', '6M', '1y']);

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// The list settings from the request (the plugin URL's /r/<range>/<res>/<min favourites> part), else env, else defaults.
export function listParams(query = {}) {
  const range = RANGES.has(query.range) ? query.range : (process.env.TOP_RANGE || '3M');
  const res = /^\d{3,5}x\d{3,5}(,\d{3,5}x\d{3,5})*$/.test(query.res || '') ? query.res : (process.env.RESOLUTIONS || '3840x2160');
  // atleast = minimum size (any larger one too); then res is not used. ratios narrows it to these aspect ratios.
  const atleast = /^\d{3,5}x\d{3,5}$/.test(query.atleast || '') ? query.atleast : (query.res ? '' : (process.env.ATLEAST || ''));
  const ratios = /^\d{1,2}x\d{1,2}(,\d{1,2}x\d{1,2})*$/.test(query.ratios || '') ? query.ratios : (process.env.RATIOS || '');
  const maxw = /^\d{1,5}$/.test(query.maxw || '') ? Number(query.maxw) : Number(process.env.MAX_WIDTH || 0); // 0 = no limit
  const fav = /^\d{1,5}$/.test(query.fav || '') ? Number(query.fav) : Number(process.env.MIN_FAVORITES || 0);
  // Wallhaven's own filter switches, as 0/1 flags: categories = general, anime, people; purity = SFW, sketchy, NSFW
  // (sketchy/NSFW need an API key). Defaults match Wallhaven's site: all categories, SFW only.
  const cats = /^[01]{3}$/.test(query.cats || '') ? query.cats : (process.env.CATEGORIES || '111');
  const purity = /^[01]{3}$/.test(query.purity || '') ? query.purity : (process.env.PURITY || '100');
  return { range, res, atleast, ratios, maxw, fav, cats, purity };
}

// What Wallhaven is asked for: the CDN cache key of /api/page.
export const pageQuery = (p) => `range=${p.range}` +
  (p.atleast ? `&atleast=${p.atleast}` : `&res=${encodeURIComponent(p.res)}`) +
  (p.ratios ? `&ratios=${encodeURIComponent(p.ratios)}` : '') + `&cats=${p.cats}&purity=${p.purity}`;
// ... plus the filters applied while putting the list together: the CDN cache key of /api/list.
export const listQuery = (p) => `${pageQuery(p)}&fav=${p.fav}&maxw=${p.maxw}`;

// Keeps a wallpaper that passes the favourites minimum and the width limit.
export const passes = (p) => (w) => (w.favorites || 0) >= p.fav && (!p.maxw || (w.dimension_x || 0) <= p.maxw);

// Our own endpoints, through the CDN (so cached pages and lists come back in milliseconds).
export function selfUrl(req, path, query) {
  const host = req.headers['x-forwarded-host'] || req.headers.host;
  const scheme = /^(localhost|127\.0\.0\.1)(:|$)/.test(host) ? 'http' : 'https'; // `vercel dev` / local tests
  return `${scheme}://${host}${path}?${query}`;
}

// One page of the toplist straight from Wallhaven: { data, last_page }. No waiting on a rate limit (429): the
// caller skips the page and a later call fills it in. Vercel's outgoing addresses are shared with other projects,
// so Wallhaven's 45 requests/minute limit is shared too.
export async function fetchToplistPage(p, page) {
  const url = 'https://wallhaven.cc/api/v1/search?sorting=toplist' +
    `&topRange=${p.range}` + (p.atleast ? `&atleast=${p.atleast}` : `&resolutions=${encodeURIComponent(p.res)}`) +
    (p.ratios ? `&ratios=${encodeURIComponent(p.ratios)}` : '') + `&categories=${p.cats}&purity=${p.purity}&page=${page}` +
    (API_KEY ? `&apikey=${API_KEY}` : '');
  for (let attempt = 0; ; attempt++) {
    const r = await fetch(url, { headers: { 'user-agent': 'wallhaven-shuffle (Projectivy Booru plugin helper)' } });
    if (r.ok) {
      const j = await r.json();
      return { data: j.data || [], last_page: j.meta?.last_page || 1 };
    }
    if (r.status < 500 || attempt >= 1) throw new Error(`Wallhaven answered ${r.status} for page ${page}`);
    await sleep(2000); // one retry for a server error
  }
}

function seededRandom(seedText) {
  let h = 2166136261;
  for (const ch of seedText) h = Math.imul(h ^ ch.charCodeAt(0), 16777619);
  return () => { // mulberry32
    h = (h + 0x6d2b79f5) | 0;
    let t = Math.imul(h ^ (h >>> 15), 1 | h);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function shuffled(items, seedText) {
  const out = items.slice();
  const rand = seededRandom(seedText);
  for (let i = out.length - 1; i > 0; i--) {
    const j = Math.floor(rand() * (i + 1));
    [out[i], out[j]] = [out[j], out[i]];
  }
  return out;
}

// The set for the time window (SLOT_MINUTES) `now` falls in. Each pass shuffles the list with a new seed and hands
// out sets 1..N, one per window, so every wallpaper is shown once per pass; then the next pass reshuffles.
export function pickSet(items, now, seedText) {
  const slotMs = SLOT_MINUTES * 60 * 1000;
  const slot = Math.floor(now / slotMs);
  const sets = Math.ceil(items.length / PER_SET);
  const pass = Math.floor(slot / sets);
  const index = slot % sets;
  const order = shuffled(items, `${pass}|${seedText}`);
  let set = order.slice(index * PER_SET, (index + 1) * PER_SET);
  if (set.length < PER_SET && order.length > PER_SET) set = set.concat(order.slice(0, PER_SET - set.length)); // last set: top up
  return { set, pass, index, sets, nextChange: (slot + 1) * slotMs };
}
