// node --test test/   (no dependencies: an in-memory store stands in for Upstash)
import test from 'node:test';
import assert from 'node:assert/strict';
import { handle, memoryStore, applyOp, emptyDoc, normSettings, defaultSettings, listQueryFor, mostfavQueryFor, activeQueryFor, checkName, K } from '../lib/sync.js';
import { listParams, listQuery, passes } from '../lib/core.js';

process.env.SYNC_KEY = 'test-key-0123456789';

function call(store, a, { method, token, body, query } = {}) {
  const req = { method: method || (['hello', 'state', 'wall'].includes(a) ? 'GET' : 'POST'), query: { a, ...query }, headers: {}, body };
  if (token) req.headers.authorization = `Bearer ${token}`;
  return new Promise((resolve) => {
    const res = { headers: {}, setHeader(k, v) { this.headers[k] = v; }, status(code) { this.code = code; return this; }, json(j) { resolve({ code: this.code, ...j }); } };
    handle(store, req, res);
  });
}

async function setup() {
  const store = memoryStore();
  const pc = await call(store, 'register', { body: { key: 'test-key-0123456789', kind: 'pc', name: 'Laptop' } });
  return { store, pc };
}

test('hello works without a store and says whether sync is set up', async () => {
  assert.deepEqual((await call(null, 'hello')).sync, false);
  assert.equal((await call(null, 'state')).code, 503);
  assert.equal((await call(memoryStore(), 'hello')).sync, true);
});

test('register: right key signs in, wrong key is refused and rate limited', async () => {
  const { store, pc } = await setup();
  assert.equal(pc.code, 200);
  assert.match(pc.token, /^[a-z2-9]{10}\.[A-Za-z0-9_-]{43}$/);
  assert.equal(pc.doc.devices[pc.id].name, 'Laptop');
  assert.deepEqual(pc.doc.devices[pc.id].settings.sources, ['toplist']);
  for (let i = 0; i < 10; i++) assert.equal((await call(store, 'register', { body: { key: 'nope', kind: 'pc' } })).code, 403);
  assert.equal((await call(store, 'register', { body: { key: 'nope', kind: 'pc' } })).code, 429);
  assert.equal((await call(store, 'register', { body: { key: 'test-key-0123456789', kind: 'toaster' } })).code, 400);
});

test('auth: bad, missing and signed-out tokens are refused', async () => {
  const { store, pc } = await setup();
  assert.equal((await call(store, 'state')).code, 401);
  assert.equal((await call(store, 'state', { token: pc.token.slice(0, -1) + (pc.token.endsWith('A') ? 'B' : 'A') })).code, 401);
  assert.equal((await call(store, 'state', { token: pc.token })).code, 200);
  assert.equal((await call(store, 'forget', { token: pc.token, body: {} })).self, true);
  assert.equal((await call(store, 'state', { token: pc.token })).code, 401);
});

test('pairing: code, approve once, claim once, expired codes', async () => {
  const { store, pc } = await setup();
  const p = await call(store, 'pair', { body: { kind: 'tv', name: 'Living room TV', settings: { every: 5 } } });
  assert.match(p.code, /^\d{6}$/);
  assert.equal((await call(store, 'claim', { body: { claim: p.claim } })).waiting, true);
  assert.equal((await call(store, 'approve', { body: { code: p.code } })).code, 401); // must be signed in
  const ok = await call(store, 'approve', { token: pc.token, body: { code: p.code.slice(0, 3) + ' ' + p.code.slice(3) } });
  assert.equal(ok.code, 200);
  assert.equal(ok.name, 'Living room TV');
  assert.equal((await call(store, 'approve', { token: pc.token, body: { code: p.code } })).code, 404); // used
  const got = await call(store, 'claim', { body: { claim: p.claim } });
  assert.equal(got.id, ok.id);
  assert.equal((await call(store, 'claim', { body: { claim: p.claim } })).code, 404); // handed out once
  const st = await call(store, 'state', { token: got.token });
  assert.equal(st.doc.devices[got.id].kind, 'tv');
  assert.equal(st.doc.devices[got.id].settings.every, 5);
  assert.equal(st.doc.devices[got.id].settings.filters.maxw, 3840); // TV default
  assert.equal((await call(store, 'claim', { body: { claim: 'x'.repeat(32) } })).code, 404);
  assert.equal((await call(store, 'approve', { token: pc.token, body: { code: '12345' } })).code, 400);
});

test('sign-in links work once', async () => {
  const { store, pc } = await setup();
  const l = await call(store, 'link', { token: pc.token, body: {} });
  const web = await call(store, 'redeem', { body: { code: l.code, name: 'Phone' } });
  assert.equal(web.code, 200);
  assert.equal(web.doc.devices[web.id].kind, 'web');
  assert.equal(web.doc.devices[web.id].settings, undefined);
  assert.equal((await call(store, 'redeem', { body: { code: l.code } })).code, 404);
});

test('poll: doc only when changed, status saved, commands delivered once and expire', async () => {
  const { store, pc } = await setup();
  let r = await call(store, 'poll', { token: pc.token, body: { v: -1 } });
  assert.ok(r.doc);
  const v = r.v;
  r = await call(store, 'poll', { token: pc.token, body: { v, st: { cur: { id: 'abc123' }, shown: 3, total: 10 } } });
  assert.equal(r.doc, undefined);
  assert.equal(r.live, false);
  const web = await call(store, 'redeem', { body: { code: (await call(store, 'link', { token: pc.token, body: {} })).code } });
  const s = await call(store, 'state', { token: web.token });
  assert.equal(s.status[pc.id].st.cur.id, 'abc123');
  assert.equal(s.status[pc.id].online, true);
  assert.ok(s.status[pc.id].list.startsWith('range=1y&atleast=3840x2160&ratios=16x9,16x10&cats=110&purity=100&fav=50&maxw=0'));
  r = await call(store, 'poll', { token: pc.token, body: { v } });
  assert.equal(r.live, true); // the remote is open
  assert.equal((await call(store, 'cmd', { token: web.token, body: { device: pc.id, cmd: { type: 'next' } } })).seq, 1);
  assert.equal((await call(store, 'cmd', { token: web.token, body: { device: pc.id, cmd: { type: 'show', item: { id: 'zz99yy', path: 'x.png' } } } })).seq, 2);
  assert.equal((await call(store, 'cmd', { token: web.token, body: { device: pc.id, cmd: { type: 'explode' } } })).code, 400);
  assert.equal((await call(store, 'cmd', { token: web.token, body: { device: web.id, cmd: { type: 'next' } } })).code, 404);
  r = await call(store, 'poll', { token: pc.token, body: { v, cs: 0 } });
  assert.deepEqual(r.cmds.map((c) => c.type), ['next', 'show']);
  assert.equal(r.cmds[1].item.ext, 'png');
  assert.equal(r.cs, 2);
  r = await call(store, 'poll', { token: pc.token, body: { v, cs: 2 } });
  assert.equal(r.cmds, undefined);
  // an old command (the device was off) isn't run
  const h = store.kv.get(K.dev(pc.id)).h;
  h.set('cmds', JSON.stringify([{ seq: 3, type: 'next', at: Date.now() - 600e3 }]));
  r = await call(store, 'poll', { token: pc.token, body: { v, cs: 2 } });
  assert.equal(r.cmds, undefined);
  assert.equal(r.cs, 3);
});

test('ops: playlists, hidden, settings, errors, idempotence, version bump', async () => {
  const { store, pc } = await setup();
  const item = { id: 'AbC123', path: 'https://w.wallhaven.cc/full/ab/wallhaven-abc123.jpg', resolution: '3840x2160', favorites: 321 };
  let r = await call(store, 'op', { token: pc.token, body: { ops: [
    { op: 'add', pl: 'hidden', item },
    { op: 'create', id: 'p_cozy01', name: '  Cozy   evenings ', items: [item, item] },
    { op: 'create', id: 'p_cozy01', name: 'Cozy evenings' },           // again: fine
    { op: 'create', id: 'p_other1', name: 'cozy EVENINGS' },           // name taken
    { op: 'create', id: 'p_bad', name: 'x' },                          // bad id
    { op: 'create', id: 'p_con001', name: 'CON' },
    { op: 'rename', pl: 'fav', name: 'Faves' },
    { op: 'settings', device: pc.id, patch: { sources: ['toplist', 'p_cozy01', 'bogus'], every: 30, filters: { quality: '8k', q: '  Night   CITY ', fav: 7, cats: '000' } } },
  ] } });
  assert.equal(r.code, 200);
  assert.deepEqual(r.errors.map((e) => e.i), [3, 4, 5, 6]);
  const cozy = r.doc.playlists.find((p) => p.id === 'p_cozy01');
  assert.equal(cozy.name, 'Cozy evenings');
  assert.deepEqual(cozy.items.map((x) => x.id), ['abc123']);
  assert.equal(r.doc.playlists.find((p) => p.id === 'hidden').items.length, 0); // put in a playlist: not hidden any more
  const s = r.doc.devices[pc.id].settings;
  assert.deepEqual(s.sources, ['toplist', 'p_cozy01']);
  assert.equal(s.every, 30);
  assert.equal(s.filters.quality, '8k');
  assert.equal(s.filters.q, 'night city');
  assert.equal(s.filters.fav, 7);
  assert.equal(s.filters.cats, '110');
  assert.equal(listQueryFor(s.filters), 'range=1y&atleast=7680x4320&ratios=16x9,16x10&cats=110&purity=100&q=night%20city&fav=7&maxw=0');
  const v = r.v;
  r = await call(store, 'op', { token: pc.token, body: { ops: [{ op: 'remove', pl: 'p_cozy01', id: 'nothere' }] } });
  assert.equal(r.v, v); // nothing changed: same version
  r = await call(store, 'op', { token: pc.token, body: { ops: [{ op: 'delete', pl: 'p_cozy01' }, { op: 'delete', pl: 'hidden' }] } });
  assert.equal(r.errors.length, 1);
  assert.deepEqual(r.doc.devices[pc.id].settings.sources, ['toplist']); // deleted playlist left the sources
  assert.equal(r.v, v + 1);
  assert.equal((await call(store, 'op', { token: pc.token, body: { ops: [] } })).code, 400);
});

test('concurrent ops all land (the lock)', async () => {
  const { store, pc } = await setup();
  await Promise.all(Array.from({ length: 12 }, (_, i) => call(store, 'op', { token: pc.token, body: { ops: [{ op: 'add', pl: 'fav', item: { id: `id${String(i).padStart(4, '0')}` } }] } })));
  const st = await call(store, 'state', { token: pc.token });
  assert.equal(st.doc.playlists.find((p) => p.id === 'fav').items.length, 12);
});

test('names follow the Windows rules', () => {
  const doc = emptyDoc();
  assert.equal(checkName('a/b', doc)[1], 'A name can\'t contain \\ / : * ? " < > |');
  assert.equal(checkName('lpt1.txt', doc)[1], 'Windows reserves that name');
  assert.equal(checkName('end.', doc)[1], 'A name can\'t end with a dot');
  assert.equal(checkName('favourites', doc)[1], 'There\'s already a playlist called favourites');
  assert.equal(checkName('x'.repeat(61), doc)[1], 'Keep it under 60 characters');
  assert.equal(checkName('Space', doc)[0], 'Space');
});

test('settings: patch merges, defaults per kind, the same list query the apps used before', () => {
  const s = normSettings({ filters: { ratio: '' } }, defaultSettings('pc'));
  assert.equal(s.filters.ratio, '');
  assert.equal(s.filters.quality, '4k+');
  assert.equal(listQueryFor(defaultSettings('tv').filters), 'range=1y&atleast=3840x2160&ratios=16x9,16x10&cats=110&purity=100&fav=50&maxw=0');
  assert.equal(listQueryFor({ ...defaultSettings('tv').filters, quality: '4k' }), 'range=1y&res=3840x2160,3840x2400&ratios=16x9,16x10&cats=110&purity=100&fav=50&maxw=0');
  // the service reads a list query back to the same settings
  const p = listParams(Object.fromEntries(new URLSearchParams('range=1y&atleast=3840x2160&ratios=16x9,16x10&cats=110&purity=100&q=night%20city&fav=50&maxw=0')));
  assert.equal(listQuery(p), 'range=1y&atleast=3840x2160&ratios=16x9,16x10&cats=110&purity=100&q=night%20city&fav=50&maxw=0');
  assert.equal(applyOp(emptyDoc(), { op: 'weird' }), 'Unknown change');
});

test('state leaves the doc out when the remote already has that version', async () => {
  const { store, pc } = await setup();
  const full = await call(store, 'state', { token: pc.token });
  assert.ok(full.doc && full.v === full.doc.v);
  const same = await call(store, 'state', { token: pc.token, query: { v: String(full.v) } });
  assert.equal(same.code, 200);
  assert.equal(same.doc, undefined);
  assert.equal(same.v, full.v);
  assert.ok(same.status);
  await call(store, 'op', { token: pc.token, body: { ops: [{ op: 'create', id: 'p_abc12345', name: 'Night' }] } });
  const after = await call(store, 'state', { token: pc.token, query: { v: String(full.v) } });
  assert.ok(after.doc && after.v === full.v + 1);
});

test('a warm server reads the version, not the whole doc, and still sees every change', async () => {
  const { store, pc } = await setup();
  const gets = [];
  const cmd = store.cmd.bind(store);
  store.cmd = (...c) => { if (c[0] === 'GET') gets.push(c[1]); return cmd(...c); };
  await call(store, 'state', { token: pc.token });
  gets.length = 0;
  const again = await call(store, 'state', { token: pc.token });
  assert.ok(again.doc);
  assert.ok(!gets.includes(K.doc), `read ${gets.join(', ')}`);
  await call(store, 'op', { token: pc.token, body: { ops: [{ op: 'create', id: 'p_ver00001', name: 'Fresh' }] } });
  const after = await call(store, 'state', { token: pc.token });
  assert.ok(after.doc.playlists.some((p) => p.name === 'Fresh'));
});

test('a document saved before Wallhaven had a sort reads as Wallhaven sorted by favourites, with nothing written', async () => {
  const { store, pc } = await setup();
  const raw = JSON.parse(await store.cmd('GET', K.doc));
  const s = raw.devices[pc.id].settings;
  s.sources = ['mostfav', 'fav']; delete s.sort; s.mostfav = { fav: 500, favmax: 1000, order: 'top' };
  raw.v += 1; await store.cmd('SET', K.doc, JSON.stringify(raw)); await store.cmd('SET', K.ver, String(raw.v));
  const st = await call(store, 'state', { token: pc.token });
  const got = st.doc.devices[pc.id].settings;
  assert.deepEqual(got.sources, ['toplist', 'fav']);
  assert.equal(got.sort, 'fav');
  assert.deepEqual(got.mostfav, { fav: 500, favmax: 1000, order: 'top' });
  assert.equal(st.v, raw.v, 'reading does not bump the version');
  // a device without settings (the remote) and a current one are left as they are
  const again = await call(store, 'state', { token: pc.token });
  assert.deepEqual(again.doc.devices[pc.id].settings, got);
});

test('a device\'s clock offset is kept with its status (its own clock is not)', async () => {
  const { store, pc } = await setup();
  await call(store, 'poll', { token: pc.token, body: { v: -1 } });
  await call(store, 'poll', { token: pc.token, body: { v: -1, st: { next: 1, t: Date.now() - 5000 } } });
  const web = await call(store, 'redeem', { body: { code: (await call(store, 'link', { token: pc.token, body: {} })).code } });
  const st = (await call(store, 'state', { token: web.token })).status[pc.id].st;
  assert.equal(st.t, undefined);
  assert.ok(st.skew >= 4900 && st.skew < 6000, `skew ${st.skew}`);
});

test('Wallhaven, sorted by Toplist or Most favourited: one source, its range and order, the list for its sort', () => {
  const base = defaultSettings('tv');
  assert.equal(base.sort, 'toplist');
  assert.deepEqual(base.mostfav, { fav: 250, favmax: 0, order: 'top' });
  assert.equal(base.order, undefined);
  assert.equal(base.filters.favmax, undefined);
  assert.equal(activeQueryFor(base), listQueryFor(base.filters), 'sorted as the toplist: the toplist');
  let s = normSettings({ sort: 'fav', mostfav: { fav: 200, favmax: 1000, order: 'shuffle' } }, base);
  assert.deepEqual(s.sources, ['toplist']);
  assert.equal(s.sort, 'fav');
  assert.equal(activeQueryFor(s), mostfavQueryFor(s), 'sorted by favourites: Most favourited');
  assert.equal(normSettings({ sort: 'newest' }, s).sort, 'fav', 'an unknown sort is left out');
  assert.deepEqual(s.mostfav, { fav: 200, favmax: 1000, order: 'shuffle' });
  // Most favourited as a source of its own (PC 2.11, TV 1.21) becomes Wallhaven sorted by favourites
  const mid = normSettings({ sources: ['mostfav', 'fav'] }, base);
  assert.deepEqual(mid.sources, ['toplist', 'fav']);
  assert.equal(mid.sort, 'fav');
  const stored = { ...structuredClone(base), sources: ['toplist', 'mostfav'] };
  const both = normSettings({}, stored);
  assert.deepEqual([both.sources, both.sort], [['toplist'], 'fav'], 'stored that way: the same');
  // the toplist's filters (quality, shape, categories, topic) with its own range in place of the period and minimum
  assert.equal(mostfavQueryFor(s), 'sort=fav&atleast=3840x2160&ratios=16x9,16x10&cats=110&purity=100&fav=200&favmax=1000&maxw=0');
  assert.equal(listQueryFor(s.filters), 'range=1y&atleast=3840x2160&ratios=16x9,16x10&cats=110&purity=100&fav=50&maxw=0');
  assert.equal(normSettings({ mostfav: { order: 'newest', favmax: -1 } }, s).mostfav.order, 'shuffle');
  assert.equal(normSettings({ mostfav: { favmax: 100001 } }, s).mostfav.favmax, 1000);
  assert.equal(normSettings({ mostfav: { fav: 2000 } }, s).mostfav.favmax, 0);
  assert.equal(normSettings({ mostfav: { fav: 1000 } }, s).mostfav.favmax, 1000);   // equal is fine: exactly 1,000
  // what the first range build (PC 2.10) saved on the toplist goes; settings from before get Most favourited's defaults
  const old = structuredClone(base); delete old.mostfav; old.order = 'fav'; old.filters.favmax = 900;
  const up = normSettings({ order: 'fav', filters: { favmax: 500, fav: 25 } }, old);
  assert.deepEqual(up.mostfav, { fav: 250, favmax: 0, order: 'top' });
  assert.equal(up.order, undefined);
  assert.equal(up.filters.favmax, undefined);
  assert.equal(up.filters.fav, 25);
});

// A fake Wallhaven behind /api/page: n wallpapers, favourites going down from the top, 24 a page. Counts the pages read.
async function fakeList(query, n) {
  const all = Array.from({ length: n }, (_, i) => ({ id: 'w' + String(i).padStart(5, '0'), path: 'https://w/' + i + '.jpg', favorites: n - i, dimension_x: 3840 }));
  const asked = [];
  const realFetch = globalThis.fetch;
  globalThis.fetch = async (url) => {
    const u = new URL(url), page = Number(u.searchParams.get('n'));
    asked.push(page);
    return new Response(JSON.stringify({ data: all.slice((page - 1) * 24, page * 24), last_page: Math.ceil(n / 24) }), { headers: { 'x-vercel-cache': 'HIT' } });
  };
  try {
    const { default: list } = await import('../api/list.js');
    const req = { query: Object.fromEntries(new URLSearchParams(query)), headers: { host: 'localhost:1' } };
    const out = await new Promise((resolve) => {
      const res = { setHeader() {}, status(code) { this.code = code; return this; }, json(j) { resolve({ code: this.code, ...j }); } };
      list(req, res);
    });
    return { ...out, asked: new Set(asked).size };
  } finally { globalThis.fetch = realFetch; }
}

test('/api/list sort=fav reads only the pages its range covers, and all of the range', async () => {
  const r = await fakeList('sort=fav&atleast=3840x2160&fav=1000&favmax=3000&maxw=0', 24 * 1000);   // 24,000 wallpapers, 1,000 pages
  assert.equal(r.code, 200);
  const favs = r.items.map((w) => w.favorites).sort((a, b) => a - b);
  assert.equal(favs.length, 2001);
  assert.deepEqual([favs[0], favs.at(-1)], [1000, 3000]);
  assert.ok(r.asked < 120, `read ${r.asked} pages`);   // the 84-page slice and ~20 to find it, not 1,000
  assert.equal(r.capped, false);
  assert.equal(r.complete, true);
  // no minimum: the most favourited MAX_PAGES (4,800), marked capped
  const top = await fakeList('sort=fav&atleast=3840x2160&fav=0&maxw=0', 24 * 1000);
  assert.equal(top.items.length, 4800);
  assert.equal(Math.min(...top.items.map((w) => w.favorites)), 24000 - 4799);
  assert.equal(top.capped, true);
  // a range nothing is in: empty, not an error
  const none = await fakeList('sort=fav&atleast=3840x2160&fav=90000&maxw=0', 24 * 10);
  assert.equal(none.code, 200);
  assert.equal(none.items.length, 0);
  // the toplist still reads from the top
  const tl = await fakeList('range=1y&atleast=3840x2160&fav=0&maxw=0', 24 * 5);
  assert.equal(tl.items.length, 120);
});

test('/api/list and search read the range: favmax only in the address when set; below the minimum it means none', () => {
  const q = (s) => listParams(Object.fromEntries(new URLSearchParams(s)));
  const p = q('range=1y&atleast=3840x2160&fav=200&favmax=1000&maxw=0');
  assert.equal(p.favmax, 1000);
  assert.equal(listQuery(p), 'range=1y&atleast=3840x2160&cats=111&purity=100&fav=200&favmax=1000&maxw=0');
  assert.equal(listQuery(q('range=1y&atleast=3840x2160&fav=200&maxw=0')), 'range=1y&atleast=3840x2160&cats=111&purity=100&fav=200&maxw=0');
  assert.equal(q('fav=500&favmax=100').favmax, 0);
  const keep = passes(p);
  assert.deepEqual([150, 200, 640, 1000, 1001].map((n) => keep({ favorites: n, dimension_x: 3840 })), [false, true, true, true, false]);
  assert.equal(passes(q('fav=0'))({ favorites: 90000 }), true);
});
