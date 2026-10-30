// A local stand-in for the deployment, for working on the remote: serves public/, runs /api/sync on an in-memory
// store, and passes /api/list on to a real deployment (LIST_ORIGIN). With --demo it also pairs a pretend TV and PC
// that show wallpapers from that list and follow the remote's commands.
//   LIST_ORIGIN=https://<your-project>.vercel.app node test/dev-server.mjs --demo     # then open the printed link
//   DEMO_AWAY=1 … --demo     # the pretend TV is in another app (not on its home screen)
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { handle, memoryStore, listQueryFor } from '../lib/sync.js';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'public');
const port = Number(process.env.PORT) || 8787;
const origin = (process.env.LIST_ORIGIN || '').replace(/\/$/, '');
process.env.SYNC_KEY ||= 'dev-key';
const store = memoryStore();
const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png', '.webmanifest': 'application/manifest+json' };

// Calls the sync handler the way Vercel would.
function sync(a, { method = 'POST', token, body, query = {} } = {}) {
  return new Promise((resolve) => {
    const req = { method, query: { a, ...query }, headers: token ? { authorization: `Bearer ${token}` } : {}, body };
    const res = { setHeader() {}, status(c) { this.c = c; return this; }, json(j) { resolve(j); } };
    handle(store, req, res);
  });
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://${req.headers.host}`);
  if (url.pathname.startsWith('/api/sync/')) {
    let raw = '';
    for await (const chunk of req) raw += chunk;
    let body;
    try { body = raw ? JSON.parse(raw) : undefined; } catch { body = raw; }
    const query = { ...Object.fromEntries(url.searchParams), a: url.pathname.slice(10) };
    const vres = { setHeader: (k, v) => res.setHeader(k, v), status(c) { res.statusCode = c; return this; }, json(j) { res.setHeader('content-type', 'application/json'); res.end(JSON.stringify(j)); } };
    return handle(store, { method: req.method, query, headers: req.headers, body }, vres);
  }
  if (url.pathname === '/api/list') {
    if (!origin) { res.statusCode = 502; return res.end('{"error":"set LIST_ORIGIN to use the list"}'); }
    const r = await fetch(`${origin}/api/list${url.search}`).catch(() => null);
    res.statusCode = r ? r.status : 502;
    res.setHeader('content-type', 'application/json');
    return res.end(r ? Buffer.from(await r.arrayBuffer()) : '{"error":"no answer"}');
  }
  const file = path.join(root, url.pathname === '/' ? 'index.html' : path.normalize(url.pathname).replace(/^([/\\])+/, ''));
  if (!file.startsWith(root) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) { res.statusCode = 404; return res.end('not found'); }
  res.setHeader('content-type', types[path.extname(file)] || 'application/octet-stream');
  fs.createReadStream(file).pipe(res);
});

server.listen(port, async () => {
  console.log(`remote on http://localhost:${port}/`);
  if (!process.argv.includes('--demo')) return;
  const pc = await sync('register', { body: { key: process.env.SYNC_KEY, kind: 'pc', name: 'Laptop' } });
  const p = await sync('pair', { body: { kind: 'tv', name: 'Living room TV' } });
  await sync('approve', { token: pc.token, body: { code: p.code } });
  const tv = await sync('claim', { body: { claim: p.claim } });
  const link = await sync('link', { token: pc.token, body: {} });
  console.log(`sign in: http://localhost:${port}/#link=${link.code}   (more links: POST /api/sync/link with the PC token)`);
  fs.writeFileSync(path.join(process.env.TEMP || '/tmp', 'whs-dev-tokens.json'), JSON.stringify({ pc: pc.token, tv: tv.token }));

  let pool = [];
  if (origin) {
    const j = await fetch(`${origin}/api/list?${listQueryFor({ quality: '4k+', ratio: '16x9,16x10', range: '1y', fav: 50, cats: '110', q: '' })}`).then((r) => r.json()).catch(() => ({}));
    pool = (j.items || []).map((w) => ({ id: w.id, ext: /png$/.test(w.path) ? 'png' : 'jpg', res: w.resolution, fav: w.favorites, w: w.dimension_x }));
  }
  if (!pool.length) pool = [{ id: 'demo01', ext: 'jpg', res: '3840x2160', fav: 100 }];
  await sync('op', { token: pc.token, body: { ops: [
    { op: 'create', id: 'p_cozy0001', name: 'Cozy evenings', items: pool.slice(0, 7) },
    { op: 'create', id: 'p_space001', name: 'Space', items: pool.slice(20, 25) },
    { op: 'add', pl: 'fav', item: pool[3] }, { op: 'add', pl: 'fav', item: pool[9] }, { op: 'add', pl: 'hidden', item: pool[40] || pool[0] },
  ] } });

  // Two pretend devices: each shows a wallpaper and follows next / prev / show, as the real apps do: they say a
  // command arrived (ack), the PC changes a second later, the TV when "Projectivy" listens again (30 s after its
  // last change), and both say what Next would show (up).
  for (const [token, kind] of [[tv.token, 'tv'], [pc.token, 'pc']]) {
    const me = { v: -1, cs: 0, recent: [], cur: null, shown: 37, next: 0, doc: null, up: null, ack: 0, eta: 0, todo: null, changedAt: 0 };
    const randomOne = () => ({ ...pool[Math.floor(Math.random() * pool.length)] });
    const showIt = (w) => { if (me.cur) me.recent.unshift(me.cur); me.recent = me.recent.slice(0, 12); me.cur = { ...w, key: w.id }; me.shown++; me.changedAt = Date.now(); me.up = randomOne(); };
    showIt(randomOne());
    const loop = async () => {
      const set = me.doc?.devices && Object.values(me.doc.devices).find((d) => d.kind === kind)?.settings;
      const everyMs = (set?.every || (kind === 'tv' ? 1 : 15)) * 60e3;
      if (me.todo && Date.now() >= me.eta) { me.todo(); me.todo = null; me.eta = 0; me.next = Date.now() + everyMs; }
      else if (!me.todo && set && !set.paused && !set.keep && Date.now() > me.next) { showIt(me.up); me.next = Date.now() + everyMs; }
      const r = await sync('poll', { token, body: { v: me.v, cs: me.cs, st: {
        up: me.up, ack: { seq: me.ack, eta: me.eta }, app: `${kind} dev`, away: kind === 'tv' && !!process.env.DEMO_AWAY || undefined, cur: me.cur, shown: me.shown, total: pool.length, next: set && !set.paused && !set.keep ? me.next : 0,
        recent: me.recent, counts: { toplist: pool.length }, list: set ? listQueryFor(set.filters) : '', base: kind === 'tv' ? 60 : undefined,
        local: kind === 'pc' ? [{ key: 'folder:D:\\Photos\\Holidays', name: 'Holidays', count: 48, on: false }] : undefined,
      } } });
      if (r.doc) { me.doc = r.doc; me.v = r.v; }
      me.cs = r.cs ?? me.cs;
      for (const c of r.cmds || []) {
        me.ack = Math.max(me.ack, c.seq);
        const at = kind === 'tv' ? Math.max(Date.now() + 1500, me.changedAt + 30e3) : Date.now() + 1000;
        if (c.type === 'next') { me.todo = () => showIt(me.up); me.eta = at; }
        if (c.type === 'prev' && me.recent.length) { me.todo = () => { me.cur = me.recent.shift(); me.shown = Math.max(1, me.shown - 1); me.changedAt = Date.now(); }; me.eta = at; }
        if (c.type === 'show') { me.todo = () => showIt(c.item); me.eta = at; }
      }
      setTimeout(loop, r.cmds?.length ? 100 : me.todo ? Math.max(100, Math.min(1500, me.eta - Date.now())) : r.live ? 1500 : 8000);
    };
    loop();
  }
});
