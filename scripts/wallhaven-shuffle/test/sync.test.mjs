// node --test test/   (no dependencies: an in-memory store stands in for Upstash)
import test from 'node:test';
import assert from 'node:assert/strict';
import { handle, memoryStore, applyOp, emptyDoc, normSettings, defaultSettings, listQueryFor, checkName, K } from '../lib/sync.js';
import { listParams, listQuery } from '../lib/core.js';

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

test('a device\'s clock offset is kept with its status (its own clock is not)', async () => {
  const { store, pc } = await setup();
  await call(store, 'poll', { token: pc.token, body: { v: -1 } });
  await call(store, 'poll', { token: pc.token, body: { v: -1, st: { next: 1, t: Date.now() - 5000 } } });
  const web = await call(store, 'redeem', { body: { code: (await call(store, 'link', { token: pc.token, body: {} })).code } });
  const st = (await call(store, 'state', { token: web.token })).status[pc.id].st;
  assert.equal(st.t, undefined);
  assert.ok(st.skew >= 4900 && st.skew < 6000, `skew ${st.skew}`);
});
