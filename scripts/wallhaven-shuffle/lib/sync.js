// Sync between the apps (TV plugin, Windows app, the web remote): shared playlists, each device's own settings and
// what it's showing now, and remote commands. Kept in Upstash Redis (KV_REST_API_URL + KV_REST_API_TOKEN, which
// Vercel's Upstash integration adds). SYNC_KEY is the owner's key: it signs in a first device, and every other one
// is paired with a code that a signed-in device approves.
//
// What's stored (all under whs:):
//   doc        one JSON document: playlists (Favourites, Hidden and your own) and devices with their settings. Every
//              change goes through withDoc (a short lock, read, change, write) and bumps doc.v.
//   dev:<id>   a hash per device: h (its token's hash), v (the doc version, so a device's poll is one read),
//              st (what it shows now), seen, live (a remote is open: poll faster until then), cmds (recent commands).
//   pair:<code> / claim:<hash> / link:<code>   pairing and sign-in codes, gone after 10 / 10 / 2 minutes.
// A device polls with one HGETALL; it reads the doc only when doc.v changed. So two devices checking in every 20-30 s
// stay well inside Upstash's free 500k commands a month.
import crypto from 'node:crypto';
import { listQuery, normTopic } from './core.js';

// ---- the store: Upstash's REST API (one command, or a pipeline of several, per request), or memory for tests

export function redisStore(url, token) {
  const call = async (path, body) => {
    const r = await fetch(url.replace(/\/$/, '') + path, {
      method: 'POST', headers: { authorization: `Bearer ${token}` }, body: JSON.stringify(body), signal: AbortSignal.timeout(8000),
    });
    if (!r.ok) throw new Error(`the database answered ${r.status}`);
    return r.json();
  };
  return {
    async cmd(...args) { const j = await call('', args); if (j.error) throw new Error(j.error); return j.result; },
    async pipe(cmds) {
      if (!cmds.length) return [];
      const j = await call('/pipeline', cmds);
      return j.map((x) => { if (x.error) throw new Error(x.error); return x.result; });
    },
  };
}

// The few commands this file uses, with Upstash's answers (strings; HGETALL as a flat list).
export function memoryStore() {
  const kv = new Map();
  const get = (k) => { const e = kv.get(k); if (e && e.exp && e.exp <= Date.now()) { kv.delete(k); return null; } return e || null; };
  const run = ([op, k, ...a]) => {
    const e = get(k);
    switch (op.toUpperCase()) {
      case 'GET': return e && typeof e.v === 'string' ? e.v : null;
      case 'GETDEL': kv.delete(k); return e && typeof e.v === 'string' ? e.v : null;
      case 'SET': {
        let nx = false, exp = 0;
        for (let i = 1; i < a.length; i++) {
          const f = String(a[i]).toUpperCase();
          if (f === 'NX') nx = true;
          else if (f === 'EX') exp = Date.now() + Number(a[++i]) * 1000;
          else if (f === 'PX') exp = Date.now() + Number(a[++i]);
        }
        if (nx && e) return null;
        kv.set(k, { v: String(a[0]), exp });
        return 'OK';
      }
      case 'DEL': { const had = !!e; kv.delete(k); return had ? 1 : 0; }
      case 'INCR': { const n = (e ? Number(e.v) : 0) + 1; kv.set(k, { v: String(n), exp: e ? e.exp : 0 }); return n; }
      case 'EXPIRE': if (!e) return 0; e.exp = Date.now() + Number(a[0]) * 1000; return 1;
      case 'HGETALL': return e && e.h ? [...e.h].flat() : [];
      case 'HSET': {
        const h = e && e.h ? e.h : new Map();
        let added = 0;
        for (let i = 0; i < a.length; i += 2) { if (!h.has(a[i])) added++; h.set(String(a[i]), String(a[i + 1])); }
        kv.set(k, { h, exp: e ? e.exp : 0 });
        return added;
      }
      default: throw new Error(`memoryStore: ${op} isn't supported`);
    }
  };
  return { kv, async cmd(...c) { return run(c); }, async pipe(cmds) { return cmds.map(run); } };
}

export function storeFromEnv(env = process.env) {
  const url = env.KV_REST_API_URL || env.UPSTASH_REDIS_REST_URL, token = env.KV_REST_API_TOKEN || env.UPSTASH_REDIS_REST_TOKEN;
  return url && token ? redisStore(url, token) : null;
}

export const K = {
  doc: 'whs:doc', lock: 'whs:lock',
  dev: (id) => `whs:dev:${id}`, pair: (code) => `whs:pair:${code}`, claim: (hash) => `whs:claim:${hash}`, link: (code) => `whs:link:${code}`,
  limit: (what) => `whs:limit:${what}`, cmdLock: (id) => `whs:cmdlock:${id}`,
};

// ---- small helpers

export class HttpError extends Error { constructor(status, message) { super(message); this.status = status; } }
const fail = (status, message) => { throw new HttpError(status, message); };
export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const ALPHABET = 'abcdefghijkmnpqrstuvwxyz23456789'; // 32 symbols (no l, o, 0, 1): uniform from a byte, easy to read
const randomId = (n) => [...crypto.randomBytes(n)].map((b) => ALPHABET[b % 32]).join('');
export const sha = (s) => crypto.createHash('sha256').update(String(s)).digest('hex');
const sameHex = (a, b) => typeof a === 'string' && a.length === b.length && crypto.timingSafeEqual(Buffer.from(a), Buffer.from(b));
export const hashObj = (flat) => { const o = {}; for (let i = 0; i + 1 < (flat || []).length; i += 2) o[flat[i]] = flat[i + 1]; return o; };
const clip = (s, n) => String(s ?? '').replace(/[\u0000-\u001f]/g, '').replace(/\s+/g, ' ').trim().slice(0, n);

// ---- what can be chosen (the web remote gets this list from /api/sync/state; the apps carry the same values)

export const QUALITY = {
  '1080+': { label: 'Full HD and up', atleast: '1920x1080' },
  '1440+': { label: '1440p and up', atleast: '2560x1440' },
  '4k+': { label: '4K and up', atleast: '3840x2160' },
  '4k': { label: 'Exactly 4K', res: '3840x2160,3840x2400' },
  '5k+': { label: '5K and up', atleast: '5120x2880' },
  '8k': { label: '8K', atleast: '7680x4320' },
};
export const RATIO = { '16x9,16x10': 'Widescreen', '16x9': '16:9 (TV)', '16x10': '16:10 (laptop)', '21x9,32x9': 'Ultrawide', '': 'Any' };
export const RANGE = { '1d': 'Past day', '3d': 'Past 3 days', '1w': 'Past week', '1M': 'Past month', '3M': 'Past 3 months', '6M': 'Past 6 months', '1y': 'Past year' };
export const FAVS = [0, 10, 25, 50, 100, 250, 500, 1000];
export const MAXW = { 0: 'Any', 3840: 'Up to 4K', 5120: 'Up to 5K' };
export const EVERY = [1, 2, 5, 10, 15, 30, 60, 120, 180, 360, 720, 1440];
export const TOPICS = ['nature', 'landscape', 'mountains', 'forest', 'ocean', 'space', 'city', 'night', 'sunset', 'winter', 'architecture', 'minimalism', 'abstract', 'digital art', 'cars'];
export const PRESETS = { quality: QUALITY, ratio: RATIO, range: RANGE, fav: FAVS, maxw: MAXW, every: EVERY, topics: TOPICS };

export const MAX_ITEMS = 5000, MAX_PLAYLISTS = 100, MAX_DEVICES = 30;
const KINDS = new Set(['tv', 'pc', 'web']);
const KIND_NAME = { tv: 'TV', pc: 'PC', web: 'Browser' };

export function defaultSettings(kind) {
  return {
    sources: ['toplist'],
    // The same list the apps used before sync (1 year of 4K+, widescreen, no People, 50+ favourites); a TV skips
    // anything wider than 4K, which its decoder handles slowly.
    filters: { quality: '4k+', ratio: '16x9,16x10', range: '1y', fav: 50, cats: '110', maxw: kind === 'tv' ? 3840 : 0, q: '' },
    every: kind === 'tv' ? 1 : 15,
    paused: false,
    keep: '',
  };
}

// A settings patch merged into base, field by field; anything invalid or unknown is left out.
export function normSettings(patch, base) {
  const out = structuredClone(base);
  const s = patch && typeof patch === 'object' ? patch : {};
  if (Array.isArray(s.sources)) out.sources = [...new Set(s.sources.filter((x) => typeof x === 'string' && /^(toplist|fav|p_[a-z0-9]{4,24})$/.test(x)))].slice(0, MAX_PLAYLISTS + 2);
  if (s.filters && typeof s.filters === 'object') {
    const f = s.filters, o = out.filters;
    if (Object.hasOwn(QUALITY, f.quality)) o.quality = f.quality;
    if (typeof f.ratio === 'string' && Object.hasOwn(RATIO, f.ratio)) o.ratio = f.ratio;
    if (Object.hasOwn(RANGE, f.range)) o.range = f.range;
    if (Number.isInteger(f.fav) && f.fav >= 0 && f.fav <= 100000) o.fav = f.fav;
    if (/^[01]{3}$/.test(f.cats) && f.cats !== '000') o.cats = f.cats;
    if (Number.isInteger(f.maxw) && Object.hasOwn(MAXW, f.maxw)) o.maxw = f.maxw;
    if (f.q !== undefined) o.q = normTopic(f.q);
  }
  if (Number.isInteger(s.every) && s.every >= 1 && s.every <= 1440) out.every = s.every;
  if (typeof s.paused === 'boolean') out.paused = s.paused;
  if (typeof s.keep === 'string' && s.keep.length <= 400) out.keep = s.keep;
  return out;
}

// The /api/list query for a device's filters: always without the width limit, which each app applies itself, so
// devices with the same filters share one cached list.
export function listQueryFor(f) {
  const q = QUALITY[f.quality] || QUALITY['4k+'];
  return listQuery({ range: f.range, res: q.res || '', atleast: q.atleast || '', ratios: f.ratio, cats: f.cats, purity: '100', q: f.q, fav: f.fav, maxw: 0 });
}

// ---- the document

export const emptyDoc = () => ({
  v: 0,
  playlists: [
    { id: 'fav', name: 'Favourites', kind: 'fav', items: [] },
    { id: 'hidden', name: 'Hidden', kind: 'hidden', items: [] },
  ],
  devices: {},
});

function upgrade(doc) {
  const d = doc && typeof doc === 'object' ? doc : emptyDoc();
  if (!Array.isArray(d.playlists)) d.playlists = [];
  for (const b of emptyDoc().playlists.reverse()) if (!d.playlists.some((p) => p.id === b.id)) d.playlists.unshift(b);
  if (!d.devices || typeof d.devices !== 'object') d.devices = {};
  d.v = Number(d.v) || 0;
  return d;
}

// A wallpaper as playlists keep it: its Wallhaven id, file type, size and favourites when added. The picture and
// page addresses follow from the id (w.wallhaven.cc/full/<first two>/wallhaven-<id>.<ext>, wallhaven.cc/w/<id>).
export function normItem(x) {
  if (!x || typeof x !== 'object') return null;
  const id = String(x.id || '').toLowerCase();
  if (!/^[a-z0-9]{4,12}$/.test(id)) return null;
  const ext = x.ext === 'png' || /\.png$/i.test(x.path || '') ? 'png' : 'jpg';
  const res = /^\d{3,5}x\d{3,5}$/.test(x.res || x.resolution || '') ? (x.res || x.resolution) : '';
  const fav = Math.max(0, Math.min(1e8, Math.round(Number(x.fav ?? x.favorites) || 0)));
  const at = Number(x.at) > 1.5e9 && Number(x.at) < 1e10 ? Math.round(Number(x.at)) : Math.floor(Date.now() / 1000);
  return { id, ext, res, fav, at };
}

const RESERVED = /^(con|prn|aux|nul|com[1-9]|lpt[1-9])$/i;
// The Windows app's rules (a playlist is a folder there), and its words.
export function checkName(name, doc, exceptId) {
  const n = String(name ?? '').replace(/\s+/g, ' ').trim();
  if (!n) return [null, 'Type a name'];
  if (n.length > 60) return [null, 'Keep it under 60 characters'];
  if (/[<>:"/\\|?*\u0000-\u001f]/.test(n)) return [null, 'A name can\'t contain \\ / : * ? " < > |'];
  if (n.endsWith('.')) return [null, 'A name can\'t end with a dot'];
  if (RESERVED.test(n.split('.')[0])) return [null, 'Windows reserves that name'];
  if (doc.playlists.some((p) => p.id !== exceptId && p.name.toLowerCase() === n.toLowerCase())) return [null, `There's already a playlist called ${n}`];
  return [n, null];
}

const findPl = (doc, id) => doc.playlists.find((p) => p.id === id);

function uniqueDeviceName(doc, name, exceptId) {
  const taken = new Set(Object.entries(doc.devices).filter(([id]) => id !== exceptId).map(([, d]) => d.name.toLowerCase()));
  if (!taken.has(name.toLowerCase())) return name;
  for (let i = 2; ; i++) if (!taken.has(`${name} ${i}`.toLowerCase())) return `${name} ${i}`;
}

// One change to the document. Returns null when done (or already so: every op can safely be sent twice), or a
// sentence for the person explaining why not.
export function applyOp(doc, op) {
  const o = op && typeof op === 'object' ? op : {};
  switch (o.op) {
    case 'add': {
      const p = findPl(doc, o.pl), it = normItem(o.item);
      if (!p) return 'That playlist is gone';
      if (!it) return 'Only Wallhaven wallpapers can be synced';
      if (!p.items.some((x) => x.id === it.id)) {
        if (p.items.length >= MAX_ITEMS) return `"${p.name}" is full (${MAX_ITEMS} wallpapers)`;
        p.items.unshift(it);
      }
      // Putting a wallpaper in a playlist (Favourites too) means you want to see it: it's no longer hidden.
      if (p.kind !== 'hidden') { const h = findPl(doc, 'hidden'); h.items = h.items.filter((x) => x.id !== it.id); }
      return null;
    }
    case 'remove': {
      const p = findPl(doc, o.pl);
      if (p) p.items = p.items.filter((x) => x.id !== String(o.id || '').toLowerCase());
      return null;
    }
    case 'create': {
      if (typeof o.id !== 'string' || !/^p_[a-z0-9]{4,24}$/.test(o.id)) return 'That playlist couldn\'t be made (bad id)';
      if (findPl(doc, o.id)) return null;
      if (doc.playlists.length >= MAX_PLAYLISTS + 2) return `That's the most playlists there can be (${MAX_PLAYLISTS})`;
      const [name, err] = checkName(o.name, doc);
      if (err) return err;
      const seen = new Set();
      const items = (Array.isArray(o.items) ? o.items : []).map(normItem).filter((x) => x && !seen.has(x.id) && seen.add(x.id)).slice(0, MAX_ITEMS);
      doc.playlists.push({ id: o.id, name, items, created: Date.now() });
      const h = findPl(doc, 'hidden');
      h.items = h.items.filter((x) => !seen.has(x.id));
      return null;
    }
    case 'rename': {
      const p = findPl(doc, o.pl);
      if (!p) return 'That playlist is gone';
      if (p.kind) return `"${p.name}" can't be renamed`;
      const [name, err] = checkName(o.name, doc, p.id);
      if (err) return err;
      p.name = name;
      return null;
    }
    case 'delete': {
      const p = findPl(doc, o.pl);
      if (!p) return null;
      if (p.kind) return `"${p.name}" can't be deleted`;
      doc.playlists = doc.playlists.filter((x) => x !== p);
      for (const d of Object.values(doc.devices)) if (d.settings) d.settings.sources = d.settings.sources.filter((s) => s !== p.id);
      return null;
    }
    case 'clear': {
      const p = findPl(doc, o.pl);
      if (p) p.items = [];
      return null;
    }
    case 'settings': {
      const d = doc.devices[o.device];
      if (!d || !d.settings) return 'That device is gone';
      d.settings = normSettings(o.patch, d.settings);
      return null;
    }
    case 'name': {
      const d = doc.devices[o.device];
      if (!d) return 'That device is gone';
      const n = clip(o.name, 40);
      if (!n) return 'Type a name';
      d.name = uniqueDeviceName(doc, n, o.device);
      return null;
    }
    default: return 'Unknown change';
  }
}

// ---- the handlers (one function, api/sync.js, routes here by ?a=)

async function withLock(store, key, fn) {
  const mine = randomId(12);
  for (let i = 0; ; i++) {
    if (await store.cmd('SET', key, mine, 'NX', 'PX', 5000) === 'OK') break;
    if (i >= 80) fail(503, 'Busy, try again in a moment');
    await sleep(40 + Math.random() * 60);
  }
  try { return await fn(); } finally {
    if (await store.cmd('GET', key) === mine) await store.cmd('DEL', key); // the lock also expires by itself
  }
}

async function readDoc(store) {
  const raw = await store.cmd('GET', K.doc);
  try { return upgrade(raw ? JSON.parse(raw) : null); } catch { return emptyDoc(); }
}

// Runs fn on the document under the lock; if it changed, saves it with the next version and tells every device's
// hash (so their next poll sees it). Returns { doc, result }.
export async function withDoc(store, fn) {
  return withLock(store, K.lock, async () => {
    const doc = await readDoc(store);
    const before = JSON.stringify(doc);
    const result = await fn(doc);
    if (JSON.stringify(doc) !== before) {
      doc.v += 1;
      const cmds = [['SET', K.doc, JSON.stringify(doc)]];
      for (const [id, d] of Object.entries(doc.devices)) if (d.kind !== 'web') cmds.push(['HSET', K.dev(id), 'v', doc.v]);
      await store.pipe(cmds);
    }
    return { doc, result };
  });
}

async function authed(store, req) {
  const m = /^Bearer ([a-z2-9]{10})\.([A-Za-z0-9_-]{43})$/.exec(req.headers.authorization || '');
  if (!m) fail(401, 'Not signed in');
  const h = hashObj(await store.cmd('HGETALL', K.dev(m[1])));
  if (!sameHex(h.h, sha(m[2]))) fail(401, 'This device was signed out');
  return { id: m[1], h };
}

// Allows `max` of `what` per `seconds` (all callers together): pairing codes and wrong keys, which anyone can try.
async function limit(store, what, max, seconds) {
  const [n] = await store.pipe([['INCR', K.limit(what)]]);
  if (n === 1) await store.cmd('EXPIRE', K.limit(what), seconds);
  if (n > max) fail(429, 'Too many tries; wait a few minutes');
}

async function newDevice(store, kind, name, settings) {
  if (!KINDS.has(kind)) fail(400, 'Unknown kind of device');
  const id = randomId(10), secret = crypto.randomBytes(32).toString('base64url');
  await store.cmd('HSET', K.dev(id), 'h', sha(secret), 'seen', Date.now());
  try {
    const { doc } = await withDoc(store, (d) => {
      if (Object.keys(d.devices).length >= MAX_DEVICES) fail(409, `That's the most devices there can be (${MAX_DEVICES}); sign one out first`);
      d.devices[id] = { name: uniqueDeviceName(d, clip(name, 40) || KIND_NAME[kind]), kind, created: Date.now() };
      if (kind !== 'web') d.devices[id].settings = normSettings(settings, defaultSettings(kind));
    });
    return { token: `${id}.${secret}`, id, name: doc.devices[id].name, v: doc.v, doc };
  } catch (err) {
    await store.cmd('DEL', K.dev(id));
    throw err;
  }
}

const body = (req) => {
  if (req.body && typeof req.body === 'object' && !Buffer.isBuffer(req.body)) return req.body;
  try { return JSON.parse(Buffer.isBuffer(req.body) ? req.body.toString('utf8') : req.body || '{}') || {}; } catch { return {}; }
};

function statusOf(h, now) {
  let st = null;
  try { st = h.st ? JSON.parse(h.st) : null; } catch { /* left out */ }
  const seen = Number(h.seen) || 0;
  return { st, seen, online: now - seen < 150e3 };
}

const ACTIONS = {
  // Is sync set up here? (No sign-in needed: the apps ask before offering to connect.)
  async hello(store) { return { sync: !!store, key: !!process.env.SYNC_KEY }; },

  // Signs in with the owner's key (SYNC_KEY): the first device, or any later one.
  async register(store, req) {
    const b = body(req), key = process.env.SYNC_KEY || '';
    if (!key) fail(403, 'SYNC_KEY isn\'t set on the service, so only pairing works');
    const given = String(b.key || '').trim();
    if (!(given.length === key.length && crypto.timingSafeEqual(Buffer.from(given), Buffer.from(key)))) {
      await limit(store, 'key', 10, 900);
      fail(403, 'That isn\'t the sync key');
    }
    return newDevice(store, b.kind, b.name, b.settings);
  },

  // A new device asks for a code to show; it then asks `claim` until a signed-in device approves the code.
  async pair(store, req) {
    const b = body(req);
    if (!KINDS.has(b.kind)) fail(400, 'Unknown kind of device');
    await limit(store, 'pair', 60, 600);
    const claim = crypto.randomBytes(24).toString('base64url');
    const settings = b.settings && JSON.stringify(b.settings).length < 4000 ? b.settings : undefined;
    for (let i = 0; i < 20; i++) {
      const code = String(crypto.randomInt(0, 1e6)).padStart(6, '0');
      const rec = JSON.stringify({ claim: sha(claim), kind: b.kind, name: clip(b.name, 40), settings, at: Date.now() });
      if (await store.cmd('SET', K.pair(code), rec, 'NX', 'EX', 600) === 'OK') {
        await store.cmd('SET', K.claim(sha(claim)), 'wait', 'EX', 600);
        return { code, claim, expires: Date.now() + 600e3 };
      }
    }
    fail(503, 'Couldn\'t make a code; try again');
  },

  async claim(store, req) {
    const c = String(body(req).claim || '');
    if (!/^[A-Za-z0-9_-]{32}$/.test(c)) fail(400, 'Bad claim');
    const v = await store.cmd('GET', K.claim(sha(c)));
    if (!v) fail(404, 'The code expired; get a new one');
    if (v === 'wait') return { waiting: true };
    await store.cmd('DEL', K.claim(sha(c)));
    return JSON.parse(v);
  },

  async approve(store, req) {
    const me = await authed(store, req);
    const b = body(req), code = String(b.code || '').replace(/\D/g, '');
    if (code.length !== 6) fail(400, 'The code has 6 digits');
    const raw = await store.cmd('GET', K.pair(code));
    if (!raw) fail(404, 'No device is showing that code (codes last 10 minutes)');
    const rec = JSON.parse(raw);
    await store.cmd('DEL', K.pair(code)); // first: a code approves one device, even if two approvals race
    const dev = await newDevice(store, rec.kind, clip(b.name, 40) || rec.name, rec.settings);
    await store.cmd('SET', K.claim(rec.claim), JSON.stringify({ token: dev.token, id: dev.id, name: dev.name }), 'EX', 600);
    return { id: dev.id, name: dev.name, kind: rec.kind, by: me.id };
  },

  // A one-time sign-in link for a browser, made by a signed-in app ("Open the remote").
  async link(store, req) {
    await authed(store, req);
    const code = randomId(16);
    await store.cmd('SET', K.link(code), '1', 'EX', 120);
    return { code };
  },

  async redeem(store, req) {
    const b = body(req), code = String(b.code || '');
    if (!/^[a-z2-9]{16}$/.test(code) || !(await store.cmd('GETDEL', K.link(code)))) fail(404, 'That sign-in link was used or has expired; open the remote from the app again');
    return newDevice(store, 'web', b.name || 'Browser');
  },

  // A device checks in: sends what it shows (when that changed, or at least every minute) and gets the doc (when
  // it changed), any new commands, and whether a remote is open (poll faster).
  async poll(store, req) {
    const { id, h } = await authed(store, req);
    const b = body(req), now = Date.now();
    if (b.st && typeof b.st === 'object') {
      const st = JSON.stringify(b.st);
      if (st.length > 60000) fail(413, 'Status too big');
      await store.cmd('HSET', K.dev(id), 'st', st, 'seen', now);
    } else if (now - (Number(h.seen) || 0) > 60e3) {
      await store.cmd('HSET', K.dev(id), 'seen', now);
    }
    const out = { v: Number(h.v) || 0, now, live: (Number(h.live) || 0) > now };
    if (String(b.v ?? '') !== String(out.v)) { out.doc = await readDoc(store); out.v = out.doc.v; }
    let cmds = [];
    try { cmds = JSON.parse(h.cmds || '[]'); } catch { /* none */ }
    out.cs = cmds.reduce((m, c) => Math.max(m, c.seq), Number(b.cs) || 0);
    const fresh = cmds.filter((c) => c.seq > (Number(b.cs) || 0) && now - c.at < 120e3);
    if (fresh.length) out.cmds = fresh;
    return out;
  },

  // Everything the web remote shows: the doc (left out when the remote already has this version: ?v=), each
  // device's status, and the choices.
  async state(store, req) {
    const { id: me } = await authed(store, req);
    const doc = await readDoc(store), now = Date.now();
    const ids = Object.keys(doc.devices).filter((i) => doc.devices[i].kind !== 'web');
    const hashes = (await store.pipe(ids.map((i) => ['HGETALL', K.dev(i)]))).map(hashObj);
    const status = {}, wake = [];
    ids.forEach((i, n) => {
      status[i] = { ...statusOf(hashes[n], now), list: listQueryFor(doc.devices[i].settings.filters) };
      if ((Number(hashes[n].live) || 0) < now + 60e3) wake.push(['HSET', K.dev(i), 'live', now + 120e3]);
    });
    // Devices poll every few seconds while a remote is open and in use (an idle one asks with quiet=1).
    if (wake.length && req.query.quiet !== '1') await store.pipe(wake);
    const out = { me, v: doc.v, status, now, presets: PRESETS };
    if (String(req.query.v ?? '') !== String(doc.v)) out.doc = doc;
    return out;
  },

  async op(store, req) {
    await authed(store, req);
    const ops = body(req).ops;
    if (!Array.isArray(ops) || !ops.length || ops.length > 500) fail(400, 'No changes sent');
    const { doc, result } = await withDoc(store, (d) => ops.map((o, i) => ({ i, error: applyOp(d, o) })).filter((r) => r.error));
    return { v: doc.v, doc, errors: result };
  },

  // A command for a device: next, prev, refresh (its list), show (this wallpaper), local (a PC folder on or off).
  async cmd(store, req) {
    await authed(store, req);
    const b = body(req), c = b.cmd && typeof b.cmd === 'object' ? b.cmd : {};
    const doc = await readDoc(store), d = doc.devices[b.device];
    if (!d || d.kind === 'web') fail(404, 'That device is gone');
    const entry = { type: c.type, at: Date.now() };
    if (c.type === 'show') { entry.item = normItem(c.item); if (!entry.item) fail(400, 'Only Wallhaven wallpapers can be sent'); }
    else if (c.type === 'local') { entry.key = clip(c.key, 400); entry.on = !!c.on; }
    else if (!['next', 'prev', 'refresh'].includes(c.type)) fail(400, 'Unknown command');
    return withLock(store, K.cmdLock(b.device), async () => {
      const h = hashObj(await store.cmd('HGETALL', K.dev(b.device)));
      let cmds = [];
      try { cmds = JSON.parse(h.cmds || '[]'); } catch { /* start over */ }
      entry.seq = cmds.reduce((m, x) => Math.max(m, x.seq), 0) + 1;
      cmds = cmds.concat(entry).slice(-10);
      await store.cmd('HSET', K.dev(b.device), 'cmds', JSON.stringify(cmds), 'live', Date.now() + 120e3);
      return { seq: entry.seq };
    });
  },

  // Signs a device out (itself, or another one from the remote).
  async forget(store, req) {
    const { id: me } = await authed(store, req);
    const target = String(body(req).device || me);
    await withDoc(store, (d) => { delete d.devices[target]; });
    await store.cmd('DEL', K.dev(target));
    return { ok: true, self: target === me };
  },

  // A wallpaper's details from Wallhaven, for adding one by its link in the remote.
  async wall(store, req) {
    await authed(store, req);
    const id = String(req.query.id || '').toLowerCase();
    if (!/^[a-z0-9]{4,12}$/.test(id)) fail(400, 'That isn\'t a Wallhaven link');
    const key = process.env.WALLHAVEN_API_KEY;
    const r = await fetch(`https://wallhaven.cc/api/v1/w/${id}${key ? `?apikey=${key}` : ''}`, { signal: AbortSignal.timeout(10000) }).catch(() => null);
    if (!r || r.status === 404) fail(404, 'Wallhaven has no wallpaper with that id');
    if (!r.ok) fail(502, `Wallhaven answered ${r.status}; try again`);
    const w = (await r.json()).data || {};
    return { item: normItem({ id: w.id, path: w.path, resolution: w.resolution, favorites: w.favorites }), purity: w.purity };
  },
};

const METHODS = { hello: 'GET', state: 'GET', wall: 'GET' };

export async function handle(store, req, res) {
  res.setHeader('cache-control', 'no-store');
  const a = String(req.query.a || '');
  const fn = Object.hasOwn(ACTIONS, a) ? ACTIONS[a] : null;
  try {
    if (!fn) fail(404, 'Unknown action');
    if (req.method !== (METHODS[a] || 'POST')) fail(405, `Use ${METHODS[a] || 'POST'}`);
    if (!store && a !== 'hello') fail(503, 'Sync isn\'t set up on this service yet (add Upstash Redis to the Vercel project)');
    res.status(200).json(await fn(store, req));
  } catch (err) {
    const status = err instanceof HttpError ? err.status : 500;
    if (status === 500) console.error(`sync ${a}: ${err.stack || err}`);
    res.status(status).json({ error: status === 500 ? 'Something went wrong on the service; try again' : err.message });
  }
}
