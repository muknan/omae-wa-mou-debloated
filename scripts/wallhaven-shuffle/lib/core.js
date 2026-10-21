// Shared code for the two endpoints: reading the Wallhaven toplist and picking the current set of 24.
// Env vars (all optional): TOP_RANGE, RESOLUTIONS, MIN_FAVORITES, CATEGORIES (e.g. 110 = no People),
// PURITY, SLOT_MINUTES, WALLHAVEN_API_KEY.

export const PER_SET = 24;
// A bit shorter than Projectivy's ~1-hour refresh, so two refreshes never land in the same window (which would
// repeat a set). The price: now and then a set is skipped; it comes back in a later pass.
const SLOT_MINUTES = Number(process.env.SLOT_MINUTES || 55);
const API_KEY = process.env.WALLHAVEN_API_KEY || '';
const MAX_PAGES = 100; // 2400 wallpapers (6 months of 4K is ~1600); keeps a long range (1y) bounded
const RANGES = new Set(['1d', '3d', '1w', '1M', '3M', '6M', '1y']);

// The list settings from the request (the plugin URL's /r/<range>/<res>/<min favourites> part), else env, else defaults.
export function listParams(query = {}) {
  const range = RANGES.has(query.range) ? query.range : (process.env.TOP_RANGE || '3M');
  const res = /^\d{3,5}x\d{3,5}(,\d{3,5}x\d{3,5})*$/.test(query.res || '') ? query.res : (process.env.RESOLUTIONS || '3840x2160');
  const fav = /^\d{1,5}$/.test(query.fav || '') ? Number(query.fav) : Number(process.env.MIN_FAVORITES || 0);
  // Wallhaven's own filter switches, as 0/1 flags: categories = general, anime, people; purity = SFW, sketchy, NSFW
  // (sketchy/NSFW need an API key). Defaults match Wallhaven's site: all categories, SFW only.
  const cats = /^[01]{3}$/.test(query.cats || '') ? query.cats : (process.env.CATEGORIES || '111');
  const purity = /^[01]{3}$/.test(query.purity || '') ? query.purity : (process.env.PURITY || '100');
  return { range, res, fav, cats, purity };
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function getJson(url) {
  for (let attempt = 0; ; attempt++) {
    const r = await fetch(url, { headers: { 'user-agent': 'wallhaven-shuffle (Projectivy Booru plugin helper)' } });
    if (r.ok) return r.json();
    if (attempt >= 3 || (r.status !== 429 && r.status < 500)) throw new Error(`Wallhaven answered ${r.status} for ${url.replace(/apikey=[^&]+/, 'apikey=***')}`);
    await sleep(r.status === 429 ? 15000 * (attempt + 1) : 2000); // 429 = rate limited (45 requests/minute)
  }
}

// The whole toplist, read one page every ~1.4 s so even a long list stays under Wallhaven's 45 requests/minute:
// ~45 s for 3 months of 4K (32 pages), ~2 minutes for 6 months (67 pages).
export async function fetchToplist({ range, res, cats, purity }) {
  const base = 'https://wallhaven.cc/api/v1/search?sorting=toplist' +
    `&topRange=${range}&resolutions=${encodeURIComponent(res)}&categories=${cats}&purity=${purity}` +
    (API_KEY ? `&apikey=${API_KEY}` : '');
  const first = await getJson(`${base}&page=1`);
  const pages = Math.min(first.meta?.last_page || 1, MAX_PAGES);
  const all = [...first.data];
  for (let p = 2; p <= pages; p++) {
    await sleep(1400);
    all.push(...(await getJson(`${base}&page=${p}`)).data);
  }
  const seen = new Set();
  const items = all.filter((w) => w && w.id && !seen.has(w.id) && seen.add(w.id)).sort((a, b) => (a.id < b.id ? -1 : 1));
  if (!items.length) throw new Error('Wallhaven returned an empty list');
  return items;
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

// The set for the hour `now` falls in. Each pass shuffles the list with a new seed and hands out sets 1..N, one
// per hour, so every wallpaper is shown once per pass; then the next pass reshuffles.
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
