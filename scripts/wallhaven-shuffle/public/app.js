// Wallhaven Shuffle remote: what each device shows and its controls, what it shuffles and its toplist filters; the
// shared playlists; browsing a device's toplist; pairing and signing out devices. Plain JS, no build step; it talks
// to /api/sync/* (and reads /api/list for Browse).
'use strict';
(() => {
  // ---------- storage, state

  const store = {
    get(k) { try { return localStorage.getItem(k); } catch { return null; } },
    set(k, v) { try { if (v == null) localStorage.removeItem(k); else localStorage.setItem(k, v); } catch { /* private window: this visit only */ } },
  };
  let token = store.get('whs.token');
  const S = { doc: null, status: {}, presets: null, me: null, online: true, inFlight: 0 };
  const UI = { dev: store.get('whs.dev'), pending: {}, hero: {}, shown: {}, full: new Set(), browse: { dev: store.get('whs.browseDev'), sort: 'fav', withHidden: false } };
  const lists = new Map(); // list query -> { items, error, loading, at }
  const sheets = new Set(); // open sheets' refill functions, so they follow changes made elsewhere

  // ---------- DOM helpers

  function h(tag, props, ...kids) {
    const el = document.createElement(tag);
    for (const [k, v] of Object.entries(props || {})) {
      if (v == null || v === false) continue;
      if (k === 'class') el.className = v;
      else if (k === 'style' && typeof v === 'object') Object.assign(el.style, v);
      else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2).toLowerCase(), v);
      else if (k === 'value') el.value = v;
      else if (k === 'checked') el.checked = !!v;
      else if (v === true) el.setAttribute(k, '');
      else el.setAttribute(k, v);
    }
    for (const kid of kids.flat(Infinity)) if (kid != null && kid !== false) el.append(kid instanceof Node ? kid : document.createTextNode(String(kid)));
    return el;
  }
  const P = {
    now: '<rect x="3" y="4.5" width="18" height="12" rx="2"/><path d="M8.5 20h7"/>',
    playlists: '<rect x="3.5" y="3.5" width="7" height="7" rx="1.5"/><rect x="13.5" y="3.5" width="7" height="7" rx="1.5"/><rect x="3.5" y="13.5" width="7" height="7" rx="1.5"/><rect x="13.5" y="13.5" width="7" height="7" rx="1.5"/>',
    browse: '<circle cx="11" cy="11" r="6.5"/><path d="m20 20-4.2-4.2"/>',
    devices: '<rect x="6.5" y="2.5" width="11" height="19" rx="2.5"/><path d="M10.5 18.5h3"/>',
    tv: '<rect x="2.5" y="4.5" width="19" height="12.5" rx="2"/><path d="M8 20.5h8"/>',
    pc: '<rect x="4.5" y="5" width="15" height="10.5" rx="1.5"/><path d="M2.5 19h19"/>',
    web: '<rect x="6.5" y="2.5" width="11" height="19" rx="2.5"/><path d="M10.5 18.5h3"/>',
    play: '<path d="M8 5.2v13.6c0 .6.7 1 1.2.6l10.2-6.8a.7.7 0 0 0 0-1.2L9.2 4.6A.7.7 0 0 0 8 5.2z" fill="currentColor" stroke="none"/>',
    pause: '<rect x="6.5" y="5" width="3.6" height="14" rx="1" fill="currentColor" stroke="none"/><rect x="13.9" y="5" width="3.6" height="14" rx="1" fill="currentColor" stroke="none"/>',
    next: '<path d="M5.5 6.2v11.6c0 .6.6.9 1.1.6l8.6-5.8a.7.7 0 0 0 0-1.2L6.6 5.6a.7.7 0 0 0-1.1.6z" fill="currentColor" stroke="none"/><path d="M18.5 5.5v13"/>',
    prev: '<path d="M18.5 6.2v11.6c0 .6-.6.9-1.1.6l-8.6-5.8a.7.7 0 0 1 0-1.2l8.6-5.8c.5-.3 1.1 0 1.1.6z" fill="currentColor" stroke="none"/><path d="M5.5 5.5v13"/>',
    pin: '<path d="M9.5 3.5h5l-.7 5 3.7 3.6v1.4h-11v-1.4l3.7-3.6z"/><path d="M12 13.5v7"/>',
    pinOn: '<path d="M9.5 3.5h5l-.7 5 3.7 3.6v1.4h-11v-1.4l3.7-3.6z" fill="currentColor"/><path d="M12 13.5v7"/>',
    heart: '<path d="M12 19.5s-7-4.2-9-8.7a4.9 4.9 0 0 1 9-3.8 4.9 4.9 0 0 1 9 3.8c-2 4.5-9 8.7-9 8.7z"/>',
    heartOn: '<path d="M12 19.5s-7-4.2-9-8.7a4.9 4.9 0 0 1 9-3.8 4.9 4.9 0 0 1 9 3.8c-2 4.5-9 8.7-9 8.7z" fill="currentColor"/>',
    more: '<circle cx="5.5" cy="12" r="1.5" fill="currentColor" stroke="none"/><circle cx="12" cy="12" r="1.5" fill="currentColor" stroke="none"/><circle cx="18.5" cy="12" r="1.5" fill="currentColor" stroke="none"/>',
    plus: '<path d="M12 5v14M5 12h14"/>',
    x: '<path d="M6.5 6.5l11 11M17.5 6.5l-11 11"/>',
    hide: '<path d="M3.5 3.5l17 17"/><path d="M10.6 5.6A9 9 0 0 1 12 5.5c5.5 0 9 6.5 9 6.5a16 16 0 0 1-3 3.8M6.6 6.8A15.6 15.6 0 0 0 3 12s3.5 6.5 9 6.5a8.8 8.8 0 0 0 4-1"/><path d="M10 10a3 3 0 0 0 4 4"/>',
    show: '<path d="M3 12s3.5-6.5 9-6.5S21 12 21 12s-3.5 6.5-9 6.5S3 12 3 12z"/><circle cx="12" cy="12" r="2.8"/>',
    trash: '<path d="M4.5 7h15M10 11v6M14 11v6M6.5 7l.9 12.5h9.2L17.5 7M9.5 7V4.5h5V7"/>',
    edit: '<path d="M4.5 19.5h3.8L19 8.8 15.2 5 4.5 15.7z"/>',
    cast: '<path d="M3.5 16.5a4 4 0 0 1 4 4M3.5 12.5a8 8 0 0 1 8 8"/><path d="M3.5 8.5V6a1.5 1.5 0 0 1 1.5-1.5h14A1.5 1.5 0 0 1 20.5 6v12a1.5 1.5 0 0 1-1.5 1.5h-4"/>',
    external: '<path d="M13.5 4.5h6v6M19.5 4.5l-8.5 8.5M17.5 14v4.5a1 1 0 0 1-1 1h-11a1 1 0 0 1-1-1v-11a1 1 0 0 1 1-1H10"/>',
    refresh: '<path d="M19.5 11a7.5 7.5 0 1 0-2.2 5.3M19.5 4.5V11H13"/>',
    chev: '<path d="m9.5 6 6 6-6 6"/>',
    updown: '<path d="m8 9.5 4-4 4 4M8 14.5l4 4 4-4"/>',
    back: '<path d="m14.5 6-6 6 6 6"/>',
    link: '<path d="M10 14a3.8 3.8 0 0 0 5.4 0l3-3a3.8 3.8 0 0 0-5.4-5.4l-.9.9M14 10a3.8 3.8 0 0 0-5.4 0l-3 3a3.8 3.8 0 0 0 5.4 5.4l.9-.9"/>',
    image: '<rect x="3.5" y="4.5" width="17" height="15" rx="2"/><circle cx="9" cy="10" r="1.8"/><path d="m20.5 16-5-5-9 8.5"/>',
    alert: '<circle cx="12" cy="12" r="8.5"/><path d="M12 7.5v5.5M12 16.2v.1"/>',
    folder: '<path d="M3.5 7a1.5 1.5 0 0 1 1.5-1.5h4l2 2h8A1.5 1.5 0 0 1 20.5 9v9a1.5 1.5 0 0 1-1.5 1.5H5A1.5 1.5 0 0 1 3.5 18z"/>',
    signout: '<path d="M14.5 4.5H18a1.5 1.5 0 0 1 1.5 1.5v12a1.5 1.5 0 0 1-1.5 1.5h-3.5M10 16.5 5.5 12 10 7.5M5.5 12h10"/>',
    key: '<circle cx="8" cy="15" r="3.8"/><path d="m10.8 12.2 8.7-8.7M16.5 6.5l3 3"/>',
  };
  const icon = (name, cls = 'icon') => {
    const s = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    s.setAttribute('viewBox', '0 0 24 24'); s.setAttribute('fill', 'none'); s.setAttribute('stroke', 'currentColor');
    s.setAttribute('stroke-width', '1.8'); s.setAttribute('stroke-linecap', 'round'); s.setAttribute('stroke-linejoin', 'round');
    s.setAttribute('class', cls); s.setAttribute('aria-hidden', 'true');
    s.innerHTML = P[name] || '';
    return s;
  };
  const img = (src, alt = '', eager = false) => {
    const i = h('img', { src, alt, decoding: 'async', loading: eager ? 'eager' : 'lazy', draggable: 'false' });
    if (!i.complete) { i.classList.add('loading'); i.addEventListener('load', () => i.classList.remove('loading'), { once: true }); }
    i.addEventListener('error', () => i.classList.remove('loading'), { once: true });
    return i;
  };

  // rows: the building block of every list and sheet
  function item({ ic, label, sub, value, href, onclick, chev = !!(href || onclick), cls = '', external }) {
    const kids = [ic ? icon(ic) : null, h('span', { class: 'label' }, label, sub ? h('small', {}, sub) : null), value != null ? h('span', { class: 'value' }, value) : null, chev ? icon('chev', 'icon chev') : null];
    const c = `item ${ic ? 'has-icon' : ''} ${cls}`;
    if (href) return h('a', { class: c, href, ...(external ? { target: '_blank', rel: 'noopener noreferrer' } : {}) }, kids);
    return onclick ? h('button', { class: c, type: 'button', onclick }, kids) : h('div', { class: c }, kids);
  }
  function selectItem({ ic, label, value, options, onchange, id }) {
    const cur = options.find(([v]) => String(v) === String(value));
    return h('div', { class: `item ${ic ? 'has-icon' : ''}` }, ic ? icon(ic) : null, h('span', { class: 'label' }, label),
      h('span', { class: 'value' }, cur ? cur[1] : ''), icon('updown', 'icon chev'),
      h('select', { 'aria-label': label, id, onchange: (e) => onchange(e.target.value) }, options.map(([v, l]) => h('option', { value: v, selected: String(v) === String(value) }, l))));
  }
  function switchItem({ ic, label, sub, on, onchange, id }) {
    const input = h('input', { type: 'checkbox', role: 'switch', checked: on, id, onchange: (e) => onchange(e.target.checked) });
    return h('label', { class: `item ${ic ? 'has-icon' : ''}` }, ic ? icon(ic) : null, h('span', { class: 'label' }, label, sub ? h('small', {}, sub) : null), h('span', { class: 'switch' }, input, h('span')));
  }
  // append that skips nothing-children and flattens lists, like h()
  const put = (el, ...kids) => el.append(...kids.flat(Infinity).filter((k) => k != null && k !== false));
  const group = (...kids) => h('div', { class: 'group' }, kids);
  const groupLabel = (text, aside) => h('div', { class: 'group-label pad' }, h('span', {}, text), aside ? h('span', { class: 'aside' }, aside) : null);

  // ---------- wallpapers and formatting

  const thumb = (it, size = 'lg') => `https://th.wallhaven.cc/${size}/${it.id.slice(0, 2)}/${it.id}.jpg`;
  const fullOf = (it) => `https://w.wallhaven.cc/full/${it.id.slice(0, 2)}/wallhaven-${it.id}.${it.ext || 'jpg'}`;
  // On a big screen the 432 px thumbnail looks soft: the original replaces it once it has loaded (not on a phone,
  // and not when the browser asks to save data, since originals are several megabytes).
  const bigScreen = matchMedia('(min-width: 960px)');
  function sharpen(el, it) {
    if (!bigScreen.matches || navigator.connection?.saveData || UI.full.has(it.id)) return;
    const full = new Image();
    full.onload = () => { UI.full.add(it.id); if (el.isConnected) el.src = full.src; };
    full.src = fullOf(it);
  }
  const pageOf = (it) => `https://wallhaven.cc/w/${it.id}`;
  const fromList = (w) => ({ id: w.id, ext: /\.png$/i.test(w.path || '') ? 'png' : 'jpg', res: w.resolution || '', fav: w.favorites || 0 });
  const nf = new Intl.NumberFormat();
  const fmtRes = (r) => (r || '').replace('x', ' × ');
  const plural = (n, one, many = one + 's') => `${nf.format(n)} ${n === 1 ? one : many}`;
  function ago(ms) {
    const s = Math.max(0, (Date.now() - ms) / 1000);
    if (s < 90) return 'just now';
    if (s < 3600) return `${Math.round(s / 60)} min ago`;
    if (s < 86400) return `${Math.round(s / 3600)} h ago`;
    return `${Math.round(s / 86400)} days ago`;
  }
  // the same words as the TV and the PC ("Change every" … "15 minutes")
  function everyLabel(m) {
    if (m < 60) return m === 1 ? '1 minute' : `${m} minutes`;
    if (m < 1440) return m === 60 ? '1 hour' : `${m / 60} hours`;
    return '1 day';
  }
  const favLabel = (n) => (n ? `${nf.format(n)}+ favourites` : 'Any');
  const kindIcon = (k) => (k === 'tv' ? 'tv' : k === 'pc' ? 'pc' : 'web');
  const kindName = (k) => (k === 'tv' ? 'TV' : k === 'pc' ? 'PC' : 'Browser');

  // ---------- the service

  async function api(a, { body, query, method } = {}) {
    let r;
    try {
      r = await fetch(`/api/sync/${a}${query ? `?${new URLSearchParams(query)}` : ''}`, {
        method: method || (body ? 'POST' : 'GET'),
        headers: { ...(token ? { authorization: `Bearer ${token}` } : {}), ...(body ? { 'content-type': 'application/json' } : {}) },
        body: body ? JSON.stringify(body) : undefined,
        cache: 'no-store',
      });
    } catch {
      throw Object.assign(new Error('No connection. Check the network and try again.'), { status: 0 });
    }
    let j = {};
    try { j = await r.json(); } catch { /* not JSON */ }
    if (r.status === 401 && token && a !== 'redeem' && a !== 'register') signedOut(j.error || 'This browser was signed out');
    if (!r.ok) throw Object.assign(new Error(j.error || `The service answered ${r.status}`), { status: r.status });
    return j;
  }

  // ---------- local copies of the service's rules (instant feedback; the service has the last word)

  const findPl = (id) => S.doc.playlists.find((p) => p.id === id);
  const inPl = (plId, id) => !!findPl(plId)?.items.some((x) => x.id === id);
  const userPlaylists = () => S.doc.playlists.filter((p) => !p.kind);
  const devicesWithSettings = () => Object.entries(S.doc.devices).filter(([, d]) => d.settings)
    .sort(([, a], [, b]) => (a.kind === b.kind ? a.name.localeCompare(b.name) : a.kind === 'tv' ? -1 : 1));
  function checkName(name, exceptId) {
    const n = String(name ?? '').replace(/\s+/g, ' ').trim();
    if (!n) return 'Type a name';
    if (n.length > 60) return 'Keep it under 60 characters';
    if (/[<>:"/\\|?*\u0000-\u001f]/.test(n)) return 'A name can\'t contain \\ / : * ? " < > |';
    if (n.endsWith('.')) return 'A name can\'t end with a dot';
    if (/^(con|prn|aux|nul|com[1-9]|lpt[1-9])$/i.test(n.split('.')[0])) return 'Windows reserves that name';
    if (S.doc.playlists.some((p) => p.id !== exceptId && p.name.toLowerCase() === n.toLowerCase())) return `There's already a playlist called ${n}`;
    return null;
  }
  function localApply(o) {
    const d = S.doc;
    if (o.op === 'add') {
      const p = findPl(o.pl);
      if (p && !p.items.some((x) => x.id === o.item.id)) p.items.unshift({ ...o.item, at: Math.floor(Date.now() / 1000) });
      if (p && p.kind !== 'hidden') { const hd = findPl('hidden'); hd.items = hd.items.filter((x) => x.id !== o.item.id); }
    } else if (o.op === 'remove') { const p = findPl(o.pl); if (p) p.items = p.items.filter((x) => x.id !== o.id); }
    else if (o.op === 'clear') { const p = findPl(o.pl); if (p) p.items = []; }
    else if (o.op === 'create') d.playlists.push({ id: o.id, name: o.name.replace(/\s+/g, ' ').trim(), items: o.items || [] });
    else if (o.op === 'rename') { const p = findPl(o.pl); if (p) p.name = o.name.replace(/\s+/g, ' ').trim(); }
    else if (o.op === 'delete') {
      d.playlists = d.playlists.filter((p) => p.id !== o.pl);
      for (const dev of Object.values(d.devices)) if (dev.settings) dev.settings.sources = dev.settings.sources.filter((s) => s !== o.pl);
    } else if (o.op === 'settings') {
      const s = d.devices[o.device]?.settings;
      if (s) { const { filters, ...rest } = o.patch; Object.assign(s, rest); if (filters) Object.assign(s.filters, filters); }
    } else if (o.op === 'name') { const dev = d.devices[o.device]; if (dev) dev.name = o.name; }
  }

  let opSeq = 0;
  async function ops(list, { toast: msg, undo } = {}) {
    list.forEach(localApply);
    changed();
    const mine = ++opSeq;
    S.inFlight++;
    try {
      const j = await api('op', { body: { ops: list } });
      if (mine === opSeq) S.doc = j.doc; // a later change's answer brings the newest doc
      if (j.errors?.length) toast(j.errors[0].error, { err: true });
      else if (msg) toast(msg, { undo });
      return j;
    } catch (e) {
      toast(e.message, { err: true });
      refresh(true);
    } finally {
      S.inFlight--;
      changed();
    }
    return null;
  }
  const setSettings = (device, patch) => ops([{ op: 'settings', device, patch }]);
  const toggleSource = (device, key) => {
    const s = S.doc.devices[device].settings;
    setSettings(device, { sources: s.sources.includes(key) ? s.sources.filter((x) => x !== key) : [...s.sources, key] });
  };

  async function command(device, cmd, label) {
    const st = S.status[device]?.st;
    UI.pending[device] = { type: cmd.type, from: st?.cur?.key || '', at: Date.now() };
    changed();
    try {
      await api('cmd', { body: { device, cmd } });
      if (label) toast(label);
      setTimeout(() => {
        const p = UI.pending[device];
        if (p && p.at + 30e3 <= Date.now() + 50) {
          delete UI.pending[device];
          changed();
          const d = S.doc?.devices[device];
          if (d) toast(d.kind === 'tv' ? `${d.name} hasn’t changed yet. It changes only while its home screen is showing.` : `${d.name} hasn’t picked it up yet. It will at its next check-in.`);
        }
      }, 30e3);
      schedule(1200);
    } catch (e) {
      delete UI.pending[device];
      changed();
      toast(e.message, { err: true });
    }
  }

  // ---------- polling

  let timer = null, lastTouch = Date.now(), soon = null;
  const idle = () => Date.now() - lastTouch > 10 * 60e3;
  async function refresh(force) {
    if (!token) return;
    try {
      const j = await api('state', { query: idle() ? { quiet: '1' } : undefined });
      S.online = true;
      S.me = j.me; S.presets = j.presets; S.status = j.status;
      if (force || (!S.inFlight && (!S.doc || j.doc.v >= S.doc.v))) S.doc = j.doc;
      for (const [id, p] of Object.entries(UI.pending)) {
        const cur = S.status[id]?.st?.cur?.key || '';
        if (cur && cur !== p.from) delete UI.pending[id];
      }
      if (!S.doc.devices[S.me]) return signedOut('This browser was signed out');
    } catch (e) {
      if (e.status !== 401) S.online = false;
    }
    changed();
  }
  // every 4 s while in use, every 1.5 s while a device is changing, every 30 s when left alone
  function schedule(first) {
    clearTimeout(timer);
    if (!token || document.hidden) return;
    const every = Object.keys(UI.pending).length ? 1500 : idle() ? 30e3 : 4e3;
    timer = setTimeout(async () => { await refresh(); schedule(); }, first ?? every);
  }
  function refreshSoon() { clearTimeout(soon); soon = setTimeout(() => refresh(), 1500); }
  document.addEventListener('visibilitychange', () => { if (!document.hidden) { lastTouch = Date.now(); refresh().then(schedule); } else clearTimeout(timer); });
  for (const ev of ['pointerdown', 'keydown']) addEventListener(ev, () => { const was = idle(); lastTouch = Date.now(); if (was) { refresh(); schedule(); } }, { passive: true });

  // ---------- sheets, prompts, toasts

  // A sheet's content is drawn by fill(body, close), and drawn again when the data changes while it's open.
  function sheet(title, fill) {
    const body = h('div', { class: 'body' });
    const dlg = h('dialog', { class: 'sheet', 'aria-label': title || 'Details' },
      h('div', { class: 'handle', 'aria-hidden': 'true' }),
      h('div', { class: 'head' }, h('h2', { tabindex: '-1', autofocus: true }, title || ''), h('button', { class: 'icon-btn', 'aria-label': 'Close', onclick: () => close() }, icon('x'))),
      body);
    let closed = false;
    const redraw = () => {
      const keep = body.scrollTop, focused = document.activeElement?.id;
      body.replaceChildren();
      fill(body, close);
      body.scrollTop = keep;
      if (focused) document.getElementById(focused)?.focus();
    };
    const close = () => {
      if (closed) return;
      closed = true;
      sheets.delete(refill);
      dlg.classList.add('closing');
      setTimeout(() => { dlg.close(); dlg.remove(); }, 190);
    };
    const refill = () => { if (!body.contains(document.activeElement) || document.activeElement.type === 'checkbox') redraw(); };
    dlg.addEventListener('cancel', (e) => { e.preventDefault(); close(); });
    dlg.addEventListener('click', (e) => { if (e.target === dlg) close(); });
    // a downward swipe on the handle or title closes it, as on the phone's own sheets
    let y0 = null;
    for (const el of [dlg.querySelector('.handle'), dlg.querySelector('.head')]) {
      el.addEventListener('touchstart', (e) => { if (!e.target.closest('button')) { y0 = e.touches[0].clientY; dlg.style.transition = 'none'; } }, { passive: true });
      el.addEventListener('touchmove', (e) => { if (y0 != null) dlg.style.transform = `translateY(${Math.max(0, e.touches[0].clientY - y0)}px)`; }, { passive: true });
      el.addEventListener('touchend', (e) => {
        if (y0 == null) return;
        const dy = e.changedTouches[0].clientY - y0; y0 = null;
        dlg.style.transition = 'transform .2s'; dlg.style.transform = '';
        if (dy > 90) close();
      });
    }
    fill(body, close);
    sheets.add(refill);
    document.body.append(dlg);
    dlg.showModal();
    return { close, dlg };
  }

  function prompt(title, { value = '', placeholder = '', okLabel = 'Save', check, note } = {}) {
    return new Promise((resolve) => {
      let done = false;
      const s = sheet(title, (body, close) => {
        const input = h('input', { class: 'field', id: 'prompt-input', value, placeholder, maxlength: 80, autocomplete: 'off', enterkeyhint: 'done' });
        const err = h('div', { class: 'err', role: 'alert' });
        const ok = h('button', { class: 'btn primary', type: 'submit' }, okLabel);
        const validate = () => { const e = check ? check(input.value) : null; err.textContent = input.value.trim() ? e || '' : ''; ok.disabled = !!e; return !e; };
        input.addEventListener('input', validate);
        put(body, h('form', { class: 'pad', onsubmit: (e) => { e.preventDefault(); if (validate()) { done = true; resolve(input.value.replace(/\s+/g, ' ').trim()); close(); } } },
          note ? h('p', { class: 'note' }, note) : null, input, err,
          h('div', { class: 'foot' }, h('button', { class: 'btn', type: 'button', onclick: () => close() }, 'Cancel'), ok)));
        validate();
        setTimeout(() => { input.focus(); input.select(); }, 80);
      });
      s.dlg.addEventListener('close', () => { if (!done) resolve(null); });
    });
  }

  function confirmSheet(title, message, okLabel) {
    return new Promise((resolve) => {
      let done = false;
      const s = sheet(title, (body, close) => {
        put(body, h('div', { class: 'pad' }, h('p', { class: 'note' }, message),
          h('div', { class: 'foot' }, h('button', { class: 'btn', onclick: () => close() }, 'Cancel'),
            h('button', { class: 'btn danger', onclick: () => { done = true; resolve(true); close(); } }, okLabel))));
      });
      s.dlg.addEventListener('close', () => { if (!done) resolve(false); });
    });
  }

  let toastBox;
  function toast(msg, { undo, err, ms } = {}) {
    if (!toastBox) return;
    while (toastBox.children.length > 1) toastBox.firstChild.remove();
    const t = h('div', { class: `toast${err ? ' err' : ''}`, role: err ? 'alert' : 'status' }, h('span', {}, msg));
    let gone = false;
    const hide = () => { if (gone) return; gone = true; t.classList.add('out'); setTimeout(() => t.remove(), 210); };
    if (undo) t.append(h('button', { onclick: () => { hide(); undo(); } }, 'Undo'));
    toastBox.append(t);
    setTimeout(hide, ms || (undo ? 6000 : err ? 5000 : 2800));
  }

  // ---------- wallpaper actions (shared by every view)

  function toggleFav(it) {
    if (inPl('fav', it.id)) ops([{ op: 'remove', pl: 'fav', id: it.id }], { toast: 'Removed from Favourites', undo: () => ops([{ op: 'add', pl: 'fav', item: it }]) });
    else ops([{ op: 'add', pl: 'fav', item: it }], { toast: 'Saved to Favourites' });
  }
  function hide(it, device) {
    ops([{ op: 'add', pl: 'hidden', item: it }], { toast: 'Hidden on every device', undo: () => ops([{ op: 'remove', pl: 'hidden', id: it.id }]) });
    if (device && S.status[device]?.st?.cur?.id === it.id) command(device, { type: 'next' });
  }
  async function newPlaylist(withItem, forDevice) {
    const name = await prompt('New playlist', { placeholder: 'Name', okLabel: 'Create', check: (v) => checkName(v),
      note: withItem ? 'It starts with this wallpaper.' : 'It shows up on every device. Each device chooses which playlists it shuffles.' });
    if (!name) return null;
    const id = 'p_' + Array.from(crypto.getRandomValues(new Uint8Array(10)), (b) => 'abcdefghijkmnpqrstuvwxyz23456789'[b % 32]).join('');
    const list = [{ op: 'create', id, name, items: withItem ? [withItem] : [] }];
    if (forDevice) list.push({ op: 'settings', device: forDevice, patch: { sources: [...S.doc.devices[forDevice].settings.sources, id] } });
    await ops(list, { toast: `Created ${name}` });
    return id;
  }

  function addToSheet(it) {
    sheet('Add to playlist', (body, close) => {
      put(body, 
        group([findPl('fav'), ...userPlaylists()].map((p) => switchItem({
          label: p.name, sub: plural(p.items.length, 'wallpaper'), on: inPl(p.id, it.id), id: `add-${p.id}`,
          onchange: (on) => ops([on ? { op: 'add', pl: p.id, item: it } : { op: 'remove', pl: p.id, id: it.id }]),
        }))),
        group(item({ ic: 'plus', label: 'New playlist', cls: 'accent', chev: false, onclick: () => { close(); newPlaylist(it); } })));
    });
  }

  // A wallpaper up close: what it is, where it is, and what you can do with it.
  function preview(it, ctx = {}) {
    if (!it) return;
    if (it.local) {
      sheet(it.name || 'Your picture', (body) => {
        put(body, it.thumb ? h('div', { class: 'picture' }, img(it.thumb, '', true)) : null,
          h('div', { class: 'pad' }, h('p', { class: 'note' }, 'One of your own pictures on the PC. It stays there: playlists only sync Wallhaven wallpapers.')));
      });
      return;
    }
    sheet('Wallpaper', (body, close) => {
      const inLists = userPlaylists().filter((p) => p.items.some((x) => x.id === it.id)).map((p) => p.name);
      const hidden = inPl('hidden', it.id), fav = inPl('fav', it.id);
      const pl = ctx.pl && !['fav', 'hidden'].includes(ctx.pl) ? findPl(ctx.pl) : null;
      const targets = devicesWithSettings().filter(([id]) => id !== ctx.device);   // not the one it's already on
      const showOn = (id, d) => command(id, { type: 'show', item: it }, `Showing it on ${d.name}`);
      put(body, 
        h('div', { class: 'picture' }, img(thumb(it), '', true)),
        h('div', { class: 'pad' }, h('div', { class: 'facts' },
          it.res ? h('span', { class: 'num' }, fmtRes(it.res)) : null,
          it.fav ? h('span', { class: 'num' }, icon('heartOn', 'icon heart'), nf.format(it.fav)) : null,
          inLists.length ? h('span', {}, `In ${inLists.join(', ')}`) : null,
          hidden ? h('span', {}, 'Hidden') : null)),
        group(
          item({ ic: fav ? 'heartOn' : 'heart', label: fav ? 'Remove from Favourites' : 'Save to Favourites', chev: false, onclick: () => { toggleFav(it); close(); } }),
          item({ ic: 'playlists', label: 'Add to playlist', value: inLists.length ? plural(inLists.length, 'playlist') : null, onclick: () => { close(); addToSheet(it); } }),
          pl ? item({ ic: 'x', label: `Remove from ${pl.name}`, chev: false, onclick: () => {
            close();
            ops([{ op: 'remove', pl: pl.id, id: it.id }], { toast: `Removed from ${pl.name}`, undo: () => ops([{ op: 'add', pl: pl.id, item: it }]) });
          } }) : null),
        // to another device: one row each for one or two, otherwise one row that lists them
        targets.length ? group(targets.length <= 2
          ? targets.map(([id, d]) => item({ ic: 'cast', label: `Show on ${d.name}`, chev: false, onclick: () => { close(); showOn(id, d); } }))
          : item({ ic: 'cast', label: 'Show on another device', onclick: () => { close(); sheet('Show on', (b2, close2) => put(b2, group(targets.map(([id, d]) =>
              item({ ic: kindIcon(d.kind), label: d.name, chev: false, onclick: () => { close2(); showOn(id, d); } }))))); } })) : null,
        group(
          hidden ? item({ ic: 'show', label: 'Show it again', chev: false, onclick: () => { close(); ops([{ op: 'remove', pl: 'hidden', id: it.id }], { toast: 'It can show up again' }); } })
            : item({ ic: 'hide', label: 'Never show this one', chev: false, onclick: () => { close(); hide(it, ctx.device); } }),
          item({ ic: 'external', label: 'Open on Wallhaven', href: pageOf(it), external: true, chev: false })));
    });
  }

  // ---------- Now: one device at a time

  function viewNow() {
    const devs = devicesWithSettings();
    if (!devs.length) {
      return h('div', {}, h('div', { class: 'title pad' }, h('h1', {}, 'Now')),
        h('div', { class: 'empty' }, h('h2', {}, 'Pair your TV or PC'),
          h('p', {}, 'On the TV: Wallhaven Shuffle > Sync > Pair with your phone or PC. On the PC: tray icon > Remote > Pair with a code. Then type the code here.'),
          h('a', { class: 'btn primary', href: '#/devices' }, 'Pair a device')));
    }
    if (!S.doc.devices[UI.dev]?.settings) UI.dev = devs[0][0];
    const id = UI.dev;
    const pick = (did) => { UI.dev = did; store.set('whs.dev', did); changed(); };
    const names = devs.length <= 2
      ? h('div', { class: 'names', role: 'tablist' }, devs.map(([did, d]) => h('button', { class: 'name', role: 'tab', 'aria-selected': String(did === id), onclick: () => pick(did) }, d.name)))
      : h('button', { class: 'name picker', 'aria-label': 'Choose a device', onclick: () => sheet('Devices', (body, close) => put(body, group(devs.map(([did, d]) =>
          item({ ic: kindIcon(d.kind), label: d.name, sub: S.status[did]?.online ? 'Online' : 'Offline', chev: false, cls: did === id ? 'accent' : '', onclick: () => { close(); pick(did); } }))))) },
        h('span', { class: 'ellipsis' }, S.doc.devices[id].name), icon('updown', 'icon'));
    const switcher = h('div', { class: 'switcher pad' }, names,
      h('button', { class: 'icon-btn', 'aria-label': `${S.doc.devices[id].name} settings`, onclick: () => deviceSheet(id) }, icon('more')));
    return h('div', {}, switcher, devicePage(id));
  }

  function sourcesSummary(id) {
    const s = S.doc.devices[id].settings, st = S.status[id]?.st || {};
    const names = s.sources.map((k) => (k === 'toplist' ? 'Toplist' : findPl(k)?.name)).filter(Boolean)
      .concat((st.local || []).filter((l) => l.on).map((l) => l.name));
    if (!names.length) return 'Nothing yet';
    return names.length <= 2 ? names.join(', ') : `${names[0]} + ${names.length - 1} more`;
  }
  function filtersSummary(f) {
    const pr = S.presets;
    return [pr.quality[f.quality]?.label, pr.range[f.range], f.q ? `“${f.q}”` : null].filter(Boolean).join(' · ');
  }

  function devicePage(id) {
    const d = S.doc.devices[id], s = d.settings, stat = S.status[id] || {}, st = stat.st || {};
    const cur = st.cur && (st.cur.id || st.cur.local) ? st.cur : null;
    const away = !!st.away;                           // a TV in another app: wallpapers change only on its home screen
    const online = !!stat.online && !away, pend = UI.pending[id];
    const kept = !!s.keep, paused = !!s.paused;

    // the picture; the last one stays underneath while the next fades in
    const heroSrc = cur ? (cur.local ? cur.thumb : thumb(cur)) : null;
    const hero = h('div', { class: `hero ${pend ? 'pending' : ''}` });
    const prev = UI.hero[id];
    if (prev && prev !== heroSrc && !prev.startsWith('data:')) hero.style.backgroundImage = `url("${prev}")`;
    UI.hero[id] = heroSrc;
    if (heroSrc) {
      const pic = img(UI.full.has(cur.id) ? fullOf(cur) : heroSrc, cur.local ? cur.name || 'Your picture' : 'The wallpaper on screen', true);
      hero.append(pic);
      if (!cur.local) sharpen(pic, cur);
    }
    else hero.append(h('div', { class: 'blank' }, st.note || (online ? 'Nothing on screen yet' : `${d.name} will show here when it checks in`)));
    if (cur) hero.append(h('button', { class: 'tap', 'aria-label': 'About this wallpaper', onclick: () => preview(cur, { device: id }) }));
    if (kept) hero.append(h('span', { class: 'flag' }, icon('pinOn'), 'Keeping'));
    else if (paused) hero.append(h('span', { class: 'flag' }, icon('pause'), 'Paused'));

    const total = st.total || 0, shown = Math.min(st.shown || 0, total);
    const caption = h('div', { class: 'pad' },
      h('div', { class: 'caption' },
        h('span', { class: 'what' },
          cur?.local ? h('span', { class: 'ellipsis' }, cur.name || 'Your picture') : null,
          cur?.res ? h('span', { class: 'num' }, fmtRes(cur.res)) : null,
          cur?.fav ? h('span', { class: 'fav num' }, icon('heartOn'), nf.format(cur.fav)) : null),
        total ? h('span', { class: 'num' }, `${nf.format(shown)} / ${nf.format(total)}`) : null),
      total ? h('div', { class: 'track' }, h('i', { style: { width: `${(shown / total) * 100}%` } })) : null,
      h('div', { class: 'status' }, h('span', { class: `dot ${online ? 'on' : ''}` }),
        pend ? h('span', {}, 'Changing…')
        : away ? h('span', {}, 'Not on the home screen')
        : online
          ? h('span', {}, paused ? 'Paused' : kept ? 'Keeping this wallpaper' : st.next ? h('span', { 'data-next': st.next }) : 'Online')
          : h('span', {}, stat.seen ? `Offline · seen ${ago(stat.seen)}` : 'Hasn’t checked in yet')));

    const favOn = cur && !cur.local && inPl('fav', cur.id);
    const controls = h('div', { class: 'controls pad' },
      h('button', { class: `icon-btn ${kept ? 'on' : ''}`, 'aria-label': kept ? 'Stop keeping this wallpaper' : 'Keep this wallpaper', 'aria-pressed': String(kept), disabled: !cur && !kept,
        onclick: () => setSettings(id, { keep: kept ? '' : cur.key || cur.id }) }, icon(kept ? 'pinOn' : 'pin')),
      h('button', { class: 'icon-btn', 'aria-label': 'Previous', disabled: !online, onclick: () => command(id, { type: 'prev' }) }, icon('prev')),
      h('button', { class: 'icon-btn main', 'aria-label': paused ? 'Resume' : 'Pause', onclick: () => setSettings(id, { paused: !paused }) }, icon(paused ? 'play' : 'pause')),
      h('button', { class: 'icon-btn', 'aria-label': 'Next', disabled: !online, onclick: () => command(id, { type: 'next' }) }, icon('next')),
      h('button', { class: `icon-btn heart ${favOn ? 'on' : ''}`, 'aria-label': favOn ? 'Remove from Favourites' : 'Save to Favourites', 'aria-pressed': String(!!favOn),
        disabled: !cur || cur.local, onclick: () => toggleFav(cur) }, icon(favOn ? 'heartOn' : 'heart')));

    const none = !s.sources.length && !(st.local || []).some((l) => l.on);
    const settings = group(
      item({ label: 'Shuffle from', value: sourcesSummary(id), onclick: () => sourcesSheet(id), cls: none ? 'warn' : '' }),
      item({ label: 'Toplist', value: filtersSummary(s.filters), onclick: () => filtersSheet(id) }),
      selectItem({ label: 'Change every', value: s.every, id: `every-${id}`, options: S.presets.every.map((m) => [m, everyLabel(m)]), onchange: (v) => setSettings(id, { every: Number(v) }) }));

    const recent = Array.isArray(st.recent) ? st.recent.filter((r) => r && (r.id || r.local)).slice(0, 20) : [];
    const side = h('div', { class: 'side' },
      online || !stat.seen ? null : h('div', { class: 'offline pad' }, icon('alert'), h('span', {}, away ? 'Wallpapers change only on its home screen. What you change here applies when it’s back there.' : 'Changes apply when it’s back on.')),
      settings,
      // a TV changes when Projectivy asks (its Update delay): say so when that is slower than the choice
      d.kind === 'tv' && st.base > 70 && s.every * 60 < st.base
        ? h('p', { class: 'hint pad' }, `Projectivy asks about every ${everyLabel(Math.round(st.base / 60))}, so the TV changes at most that often.`) : null,
      recent.length ? [groupLabel('Recently shown'), h('div', { class: 'strip pad' }, recent.map((r) => h('button', { 'aria-label': r.local ? r.name : 'Earlier wallpaper', onclick: () => preview(r, { device: id }) },
        r.local ? (r.thumb ? img(r.thumb) : h('div', { class: 'own' }, r.name)) : img(thumb(r, 'small')))))] : null);

    return h('div', { class: 'stage' }, h('div', {}, hero, caption, controls), side);
  }

  function sourcesSheet(id) {
    sheet('Shuffle from', (body, close) => {
      const d = S.doc.devices[id];
      if (!d) return close();
      const s = d.settings, st = S.status[id]?.st || {};
      const locals = Array.isArray(st.local) ? st.local : [];
      put(body, 
        h('div', { class: 'pad' }, h('p', { class: 'note' }, `Turn on any mix. ${d.name} goes through all of them in one round, with no repeats.`)),
        group(
          switchItem({ label: 'Toplist', sub: st.counts?.toplist != null ? `${plural(st.counts.toplist, 'wallpaper')} with its filters` : 'Wallhaven’s most popular, with its filters', on: s.sources.includes('toplist'), id: `src-${id}-toplist`, onchange: () => toggleSource(id, 'toplist') }),
          switchItem({ label: 'Favourites', sub: plural(findPl('fav').items.length, 'wallpaper'), on: s.sources.includes('fav'), id: `src-${id}-fav`, onchange: () => toggleSource(id, 'fav') })),
        userPlaylists().length ? [groupLabel('Playlists'), group(userPlaylists().map((p) => switchItem({
          label: p.name, sub: plural(p.items.length, 'wallpaper'), on: s.sources.includes(p.id), id: `src-${id}-${p.id}`, onchange: () => toggleSource(id, p.id),
        })))] : null,
        locals.length ? [groupLabel('Folders on this PC'), group(locals.map((l) => switchItem({
          label: l.name, sub: l.count != null ? plural(l.count, 'picture') : null, on: l.on, id: `src-${id}-${l.key}`,
          onchange: (on) => { l.on = on; command(id, { type: 'local', key: l.key, on }); },
        })))] : null,
        group(item({ ic: 'plus', label: 'New playlist', cls: 'accent', chev: false, onclick: () => { close(); newPlaylist(null, id); } })));
    });
  }

  function filtersSheet(id) {
    sheet('Toplist', (body, close) => {
      const d = S.doc.devices[id];
      if (!d) return close();
      const f = d.settings.filters, pr = S.presets, stat = S.status[id] || {}, st = stat.st || {};
      const setF = (patch) => setSettings(id, { filters: patch });
      const cats = ['General', 'Anime', 'People'];
      const topic = h('input', { class: 'inline', id: `topic-${id}`, value: f.q, placeholder: 'Any', list: `topics-${id}`, maxlength: 40, enterkeyhint: 'done', autocomplete: 'off', 'aria-label': 'Topic',
        onchange: (e) => setF({ q: e.target.value }), onkeydown: (e) => { if (e.key === 'Enter') e.target.blur(); } });
      const matches = st.list === stat.list && st.counts?.toplist != null;
      put(body, 
        group(
          selectItem({ label: 'Quality', value: f.quality, id: `q-${id}`, options: Object.entries(pr.quality).map(([k, v]) => [k, v.label]), onchange: (v) => setF({ quality: v }) }),
          selectItem({ label: 'Shape', value: f.ratio, id: `r-${id}`, options: Object.entries(pr.ratio), onchange: (v) => setF({ ratio: v }) }),
          selectItem({ label: 'Period', value: f.range, id: `p-${id}`, options: Object.entries(pr.range), onchange: (v) => setF({ range: v }) }),
          selectItem({ label: 'Popularity', value: f.fav, id: `f-${id}`, options: pr.fav.map((n) => [n, favLabel(n)]), onchange: (v) => setF({ fav: Number(v) }) }),
          selectItem({ label: 'Largest size', value: f.maxw, id: `w-${id}`, options: Object.entries(pr.maxw), onchange: (v) => setF({ maxw: Number(v) }) })),
        groupLabel('Categories'),
        group(cats.map((label, i) => switchItem({ label, on: f.cats[i] === '1', id: `c-${id}-${i}`, onchange: (on) => {
          const next = f.cats.split(''); next[i] = on ? '1' : '0';
          if (next.join('') === '000') { toast('Keep at least one category'); sheets.forEach((r) => r()); return; }
          setF({ cats: next.join('') });
        } }))),
        group(h('div', { class: 'item' }, h('span', { class: 'label' }, 'Topic'), topic, h('datalist', { id: `topics-${id}` }, pr.topics.map((t) => h('option', { value: t }))))),
        h('div', { class: 'pad', style: { marginTop: '-8px' } }, h('p', { class: 'note', style: { fontSize: '13.5px', color: 'var(--fg-3)' } }, 'Optional. A Wallhaven search, like nature or night city.')),
        h('div', { class: 'pad' }, h('p', { class: 'note' }, matches ? `${plural(st.counts.toplist, 'wallpaper')} match on ${d.name}.` : stat.online ? `${d.name} is getting the list for these filters.` : `${d.name} gets the new list when it’s back on.`)),
        group(item({ ic: 'browse', label: 'Browse them', onclick: () => { close(); UI.browse.dev = id; store.set('whs.browseDev', id); location.hash = '#/browse'; } })));
    });
  }

  function deviceSheet(id) {
    sheet(S.doc.devices[id]?.name, (body, close) => {
      const d = S.doc.devices[id], stat = S.status[id] || {};
      if (!d) return close();
      const app = stat.st?.app ? stat.st.app.replace(/^\w+ /, 'version ') : null;
      put(body, 
        h('div', { class: 'pad' }, h('p', { class: 'note' }, [kindName(d.kind), app, id === S.me ? 'this browser' : stat.seen ? `seen ${ago(stat.seen)}` : null].filter(Boolean).join(' · '))),
        group(
          d.settings ? item({ ic: 'refresh', label: 'Get a fresh toplist', chev: false, onclick: () => { close(); command(id, { type: 'refresh' }, `${d.name} is getting a fresh list`); } }) : null,
          item({ ic: 'edit', label: 'Rename', chev: false, onclick: async () => {
            close();
            const name = await prompt('Rename', { value: d.name, check: (v) => (v.trim() ? null : 'Type a name') });
            if (name && name !== d.name) ops([{ op: 'name', device: id, name }]);
          } })),
        group(item({ ic: 'signout', label: id === S.me ? 'Sign out of this browser' : 'Sign out', cls: 'danger', chev: false, onclick: async () => {
          close();
          const msg = id === S.me ? 'You can sign in again with a pairing code.' : `${d.name} stops syncing until you pair it again. Playlists stay as they are.`;
          if (await confirmSheet(id === S.me ? 'Sign out of this browser?' : `Sign out ${d.name}?`, msg, 'Sign out')) forget(id);
        } })));
    });
  }

  async function forget(id) {
    try {
      const j = await api('forget', { body: { device: id } });
      if (j.self) return signedOut('Signed out');
      toast('Signed out');
      refresh(true);
    } catch (e) { toast(e.message, { err: true }); }
  }

  // ---------- Playlists

  const usedOn = (plId) => devicesWithSettings().filter(([, d]) => d.settings.sources.includes(plId)).map(([, d]) => d.name);

  function viewPlaylists() {
    const album = (p) => {
      const used = usedOn(p.id), first = p.items[0];
      return h('a', { class: 'album', href: `#/playlists/${encodeURIComponent(p.id)}` },
        h('div', { class: 'cover' }, first ? img(thumb(first, 'small')) : icon(p.kind === 'fav' ? 'heart' : 'image')),
        h('div', {}, h('div', { class: 'name ellipsis' }, p.name),
          h('div', { class: 'meta ellipsis' }, plural(p.items.length, 'wallpaper'), used.length ? ` · on ${used.join(', ')}` : '')));
    };
    const hiddenCount = findPl('hidden').items.length;
    return h('div', {},
      h('div', { class: 'title pad' }, h('h1', {}, 'Playlists'), h('button', { class: 'icon-btn', 'aria-label': 'New playlist', onclick: () => newPlaylist() }, icon('plus'))),
      h('p', { class: 'lede pad' }, 'On every device. Each one picks what it shuffles.'),
      h('div', { class: 'albums pad' }, [findPl('fav'), ...userPlaylists()].map(album)),
      group(item({ ic: 'hide', label: 'Hidden', sub: 'Never shown on any device', value: hiddenCount ? nf.format(hiddenCount) : null, href: '#/playlists/hidden' })));
  }

  function viewPlaylist(plId) {
    const p = findPl(plId);
    if (!p) return h('div', { class: 'empty' }, h('h2', {}, 'That playlist is gone'), h('a', { class: 'btn', href: '#/playlists' }, 'All playlists'));
    const isHidden = p.kind === 'hidden';
    const devs = devicesWithSettings();
    const options = () => {
      if (isHidden) {
        const before = p.items.slice();
        return ops([{ op: 'clear', pl: 'hidden' }], { toast: 'They can all show up again', undo: () => ops(before.map((it) => ({ op: 'add', pl: 'hidden', item: it }))) });
      }
      playlistSheet(p);
    };
    return h('div', {},
      h('div', { class: 'title pad' },
        h('a', { class: 'icon-btn', href: '#/playlists', 'aria-label': 'Playlists', style: { marginLeft: '-10px' } }, icon('back')),
        h('h1', {}, p.name),
        p.kind === 'fav' || (isHidden && !p.items.length) ? null
          : isHidden ? h('button', { class: 'text-btn', onclick: options }, 'Show all again')
            : h('button', { class: 'icon-btn', 'aria-label': 'Playlist options', onclick: options }, icon('more'))),
      h('p', { class: 'lede pad' }, isHidden ? 'These never show on any device. Tap one to let it show again.' : plural(p.items.length, 'wallpaper')),
      !isHidden && devs.length ? [h('div', { class: 'group-label pad', style: { marginTop: '0' } }, 'Shuffled on'),
        group(devs.map(([id, d]) => switchItem({ ic: kindIcon(d.kind), label: d.name, on: d.settings.sources.includes(p.id), id: `on-${p.id}-${id}`, onchange: () => toggleSource(id, p.id) })))] : null,
      isHidden ? null : group(item({ ic: 'link', label: 'Add from a Wallhaven link', cls: 'accent', chev: false, onclick: () => addLinkSheet(p) })),
      p.items.length ? h('div', { style: { marginTop: '28px' } }, photoGrid(p.items, { pl: p.id }))
        : h('div', { class: 'empty' }, h('h2', {}, isHidden ? 'Nothing hidden' : 'No wallpapers yet'),
          h('p', {}, isHidden ? 'Never show this one, on any device, puts a wallpaper here.' : 'Save one from Now, pick some in Browse, or add a Wallhaven link.'),
          isHidden ? null : h('a', { class: 'btn', href: '#/browse' }, 'Browse the toplist')));
  }

  function addLinkSheet(p) {
    sheet(`Add to ${p.name}`, (body, close) => {
      const input = h('input', { class: 'field', id: 'addlink', placeholder: 'wallhaven.cc/w/…', autocomplete: 'off', inputmode: 'url', enterkeyhint: 'go' });
      const err = h('div', { class: 'err', role: 'alert' });
      const go = h('button', { class: 'btn primary', type: 'submit' }, 'Add');
      put(body, h('form', { class: 'pad', onsubmit: async (e) => {
        e.preventDefault();
        const m = /(?:wallhaven\.cc\/w\/|whvn\.cc\/|wallhaven-)?([a-z0-9]{6})(?:\.\w+)?\/?$/i.exec(input.value.trim());
        if (!m) { err.textContent = 'Paste a link like wallhaven.cc/w/abc123'; return; }
        go.disabled = true; go.textContent = 'Adding…';
        try {
          const j = await api('wall', { query: { id: m[1].toLowerCase() } });
          close();
          await ops([{ op: 'add', pl: p.id, item: j.item }], { toast: `Added to ${p.name}` });
        } catch (x) { err.textContent = x.message; go.disabled = false; go.replaceChildren('Add'); }
      } }, h('p', { class: 'note' }, 'Paste the link of any wallpaper on Wallhaven.'), input, err,
      h('div', { class: 'foot' }, h('button', { class: 'btn', type: 'button', onclick: () => close() }, 'Cancel'), go)));
      setTimeout(() => input.focus(), 80);
    });
  }

  function playlistSheet(p) {
    sheet(p.name, (body, close) => {
      put(body, 
        group(item({ ic: 'edit', label: 'Rename', chev: false, onclick: async () => {
          close();
          const name = await prompt('Rename playlist', { value: p.name, check: (v) => checkName(v, p.id) });
          if (name && name !== p.name) ops([{ op: 'rename', pl: p.id, name }], { toast: 'Renamed' });
        } })),
        group(item({ ic: 'trash', label: 'Delete playlist', cls: 'danger', chev: false, onclick: async () => {
          close();
          if (await confirmSheet(`Delete ${p.name}?`, 'Its wallpapers stay in your other playlists. On the PC, its folder goes to the Recycle Bin.', 'Delete')) {
            location.hash = '#/playlists';
            ops([{ op: 'delete', pl: p.id }], { toast: `Deleted ${p.name}` });
          }
        } })));
    });
  }

  // A grid of wallpapers, drawn a screenful at a time as you scroll.
  function photoGrid(items, ctx = {}) {
    const grid = h('div', { class: 'photos' });
    const favs = new Set(findPl('fav').items.map((x) => x.id)), hidden = new Set(findPl('hidden').items.map((x) => x.id));
    const key = ctx.key || ctx.pl || 'grid';
    let shown = 0;
    const sentinel = h('div', { class: 'more', 'aria-hidden': 'true' });
    const more = () => {
      const end = Math.min(items.length, Math.max(shown + 60, UI.shown[key] || 0));
      for (; shown < end; shown++) {
        const it = items[shown], isHidden = hidden.has(it.id) && ctx.pl !== 'hidden';
        grid.append(h('button', { class: `photo ${isHidden ? 'dim' : ''}`, 'aria-label': `Wallpaper ${fmtRes(it.res)}`, onclick: () => preview(it, ctx) },
          img(thumb(it, 'small')),
          favs.has(it.id) && ctx.pl !== 'fav' ? h('span', { class: 'badge heart' }, icon('heartOn')) : isHidden ? h('span', { class: 'badge' }, icon('hide')) : null));
      }
      UI.shown[key] = shown;
      sentinel.hidden = shown >= items.length;
    };
    new IntersectionObserver((es) => { if (es.some((e) => e.isIntersecting)) more(); }, { rootMargin: '900px' }).observe(sentinel);
    more();
    return h('div', {}, grid, sentinel);
  }

  // ---------- Browse

  async function loadList(query) {
    const have = lists.get(query);
    if (have && (have.loading || Date.now() - (have.at || 0) < (have.items ? 30 * 60e3 : 20e3))) return;
    lists.set(query, { loading: true, items: have?.items });
    changed();
    try {
      const r = await fetch(`/api/list?${query}`);
      const j = await r.json().catch(() => ({}));
      if (!r.ok || !Array.isArray(j.items)) throw new Error(j.error || `The service answered ${r.status}`);
      lists.set(query, { items: j.items, at: Date.now() });
    } catch (e) {
      lists.set(query, { error: e.message === 'Failed to fetch' ? 'No connection' : e.message, items: have?.items, at: Date.now() });
    }
    changed();
  }

  function viewBrowse() {
    const devs = devicesWithSettings();
    const head = h('div', { class: 'title pad' }, h('h1', {}, 'Browse'));
    if (!devs.length) return h('div', {}, head, h('div', { class: 'empty' }, h('h2', {}, 'Nothing to browse yet'), h('p', {}, 'Pair a TV or PC first. Browse shows the toplist with its filters.')));
    const B = UI.browse;
    if (!S.doc.devices[B.dev]?.settings) B.dev = devs[0][0];
    const d = S.doc.devices[B.dev], f = d.settings.filters;
    const query = S.status[B.dev]?.list;
    if (query) loadList(query);
    const L = (query && lists.get(query)) || { loading: true };
    const hidden = new Set(findPl('hidden').items.map((x) => x.id));
    let items = (L.items || []).filter((w) => !f.maxw || (w.dimension_x || 0) <= f.maxw).map(fromList);
    const total = items.length;
    if (!B.withHidden) items = items.filter((w) => !hidden.has(w.id));
    if (B.sort === 'fav') items.sort((a, b) => b.fav - a.fav);
    else if (B.sort === 'new') { const at = new Map((L.items || []).map((w) => [w.id, w.created_at || ''])); items.sort((a, b) => at.get(b.id).localeCompare(at.get(a.id))); }
    else { const seed = B.seed ||= Math.random() * 1e9; const r = (id) => { let x = seed; for (const c of id) x = (x * 31 + c.charCodeAt(0)) % 2147483647; return x; }; items.sort((a, b) => r(a.id) - r(b.id)); }

    return h('div', {}, head,
      group(
        selectItem({ label: 'Toplist of', value: B.dev, id: 'browse-dev', options: devs.map(([id, x]) => [id, x.name]), onchange: (v) => { B.dev = v; store.set('whs.browseDev', v); changed(); } }),
        selectItem({ label: 'Order', value: B.sort, id: 'browse-sort', options: [['fav', 'Most favourites'], ['new', 'Newest'], ['shuffle', 'Shuffled']], onchange: (v) => { B.sort = v; B.seed = 0; changed(); } }),
        switchItem({ label: 'Show hidden ones', on: B.withHidden, id: 'browse-hidden', onchange: (on) => { B.withHidden = on; changed(); } })),
      h('p', { class: 'lede pad', style: { margin: '14px 0 16px' } }, filtersSummary(f), L.items ? ` · ${plural(total, 'wallpaper')}` : ''),
      L.error && !L.items ? h('div', { class: 'empty' }, h('h2', {}, 'Couldn’t get the list'), h('p', {}, L.error), h('button', { class: 'btn', onclick: () => { lists.delete(query); changed(); } }, 'Try again'))
        : !L.items ? h('div', {}, h('p', { class: 'lede pad' }, 'Getting the toplist. One nobody asked for lately can take a minute.'),
            h('div', { class: 'photos', 'aria-hidden': 'true' }, Array.from({ length: 12 }, () => h('div', { class: 'photo ghost' }))))
          : items.length ? photoGrid(items, { key: `browse-${B.dev}-${B.sort}` })
            : h('div', { class: 'empty' }, h('h2', {}, 'Nothing matches'), h('p', {}, `Loosen ${d.name}’s toplist filters on the Now page.`)));
  }

  // ---------- Devices

  function viewDevices() {
    const all = Object.entries(S.doc.devices).sort(([a, x], [b, y]) => (a === S.me ? -1 : b === S.me ? 1 : x.kind === y.kind ? x.name.localeCompare(y.name) : ({ tv: 0, pc: 1, web: 2 }[x.kind] - { tv: 0, pc: 1, web: 2 }[y.kind])));
    return h('div', {},
      h('div', { class: 'title pad' }, h('h1', {}, 'Devices')),
      group(item({ ic: 'plus', label: 'Pair a device', cls: 'accent', chev: false, onclick: pairSheet })),
      groupLabel('Signed in'),
      group(all.map(([id, d]) => {
        const stat = S.status[id];
        const sub = id === S.me ? 'This browser' : d.kind === 'web' ? `Added ${new Date(d.created).toLocaleDateString()}` : stat?.online ? 'Online' : stat?.seen ? `Offline · seen ${ago(stat.seen)}` : 'Hasn’t checked in yet';
        return item({ ic: kindIcon(d.kind), label: d.name, sub, onclick: () => deviceSheet(id) });
      })),
      h('p', { class: 'lede pad', style: { marginTop: '28px', fontSize: '13.5px', color: 'var(--fg-3)' } }, 'Wallpapers from wallhaven.cc.'));
  }

  function pairSheet() {
    sheet('Pair a device', (body, close) => {
      const code = h('input', { class: 'field code-field', id: 'pair-code', inputmode: 'numeric', autocomplete: 'one-time-code', maxlength: 7, placeholder: '000 000', 'aria-label': 'Code' });
      const name = h('input', { class: 'field', id: 'pair-name', placeholder: 'Name it (optional)', maxlength: 40, autocomplete: 'off' });
      const err = h('div', { class: 'err', role: 'alert' });
      const go = h('button', { class: 'btn primary', type: 'submit' }, 'Pair');
      code.addEventListener('input', () => { const v = code.value.replace(/\D/g, '').slice(0, 6); code.value = v.length > 3 ? `${v.slice(0, 3)} ${v.slice(3)}` : v; err.textContent = ''; });
      put(body, h('form', { class: 'pad', onsubmit: async (e) => {
        e.preventDefault();
        const c = code.value.replace(/\D/g, '');
        if (c.length !== 6) { err.textContent = 'The code has 6 digits'; return; }
        go.disabled = true; go.textContent = 'Pairing…';
        try {
          const j = await api('approve', { body: { code: c, name: name.value } });
          close();
          toast(`Paired ${j.name}`);
          refresh(true);
        } catch (x) { err.textContent = x.message; go.disabled = false; go.replaceChildren('Pair'); }
      } },
      h('ul', { class: 'how' },
        h('li', {}, h('b', {}, 'TV: '), 'open Wallhaven Shuffle, then Sync > Pair with your phone or PC.'),
        h('li', {}, h('b', {}, 'PC: '), 'right-click the tray icon, then Remote > Pair with a code.'),
        h('li', {}, h('b', {}, 'Phone or another browser: '), 'open this page there.')),
      code, name, err,
      h('div', { class: 'foot' }, h('button', { class: 'btn', type: 'button', onclick: () => close() }, 'Cancel'), go)));
      setTimeout(() => code.focus(), 80);
    });
  }

  // ---------- rendering: the whole view, only when something it shows changed

  let shell = null, lastSig = '', deferred = false;
  function buildShell() {
    const tab = (name, label, ic) => h('a', { href: `#/${name === 'now' ? '' : name}`, 'data-tab': name }, icon(ic), h('span', {}, label));
    const nav = h('nav', { class: 'tabbar', 'aria-label': 'Sections' }, tab('now', 'Now', 'now'), tab('playlists', 'Playlists', 'playlists'), tab('browse', 'Browse', 'browse'), tab('devices', 'Devices', 'devices'));
    const main = h('main', { id: 'main' });
    toastBox = h('div', { class: 'toasts', 'aria-live': 'polite' });
    document.getElementById('app').replaceChildren(nav, main, toastBox);
    shell = { nav, main };
  }

  function route() {
    const [, a = '', b] = (location.hash || '#/').split('/');
    return { name: ['playlists', 'browse', 'devices'].includes(a) ? a : 'now', arg: b ? decodeURIComponent(b) : null };
  }

  function changed() { render(); sheets.forEach((r) => r()); }

  function render() {
    if (!token) return;
    if (!shell) buildShell();
    const r = route();
    for (const a of shell.nav.querySelectorAll('a')) { if (a.dataset.tab === r.name) a.setAttribute('aria-current', 'page'); else a.removeAttribute('aria-current'); }
    if (!S.doc) {
      // the page's shape while the first answer comes: a title line, the picture and three rows, all quiet
      shell.main.replaceChildren(h('div', { 'aria-busy': 'true', 'aria-label': 'Loading' },
        h('div', { class: 'switcher pad' }, h('div', { class: 'ghost-line', style: { width: '45%', height: '28px' } })),
        h('div', { class: 'hero ghost' }),
        h('div', { class: 'pad', style: { paddingTop: '18px' } }, h('div', { class: 'ghost-line', style: { width: '60%' } })),
        h('div', { class: 'group ghost', style: { height: '156px', marginTop: '86px' } })));
      return;
    }
    const roundedStatus = Object.fromEntries(Object.entries(S.status).map(([k, v]) => [k, { ...v, seen: Math.round(v.seen / 60e3) }]));
    const sig = JSON.stringify([r, S.online, S.doc, roundedStatus, UI.pending, UI.dev, UI.browse, [...lists.entries()].map(([k, v]) => [k, !!v.items, v.loading, v.error])]);
    if (sig === lastSig) return;
    const active = document.activeElement;
    if (active && shell.main.contains(active) && ['INPUT', 'SELECT'].includes(active.tagName) && active.type !== 'checkbox') { deferred = true; return; }
    lastSig = sig;
    deferred = false;
    const view = r.name === 'playlists' ? (r.arg ? viewPlaylist(r.arg) : viewPlaylists()) : r.name === 'browse' ? viewBrowse() : r.name === 'devices' ? viewDevices() : viewNow();
    shell.main.replaceChildren(...[S.online ? null : h('div', { class: 'offline pad', style: { paddingTop: '16px' } }, icon('alert'), h('span', {}, 'Can’t reach the service. Retrying…')), view].filter(Boolean));
    tick();
  }
  document.addEventListener('focusout', () => setTimeout(() => { if (deferred) render(); }, 0));
  addEventListener('hashchange', () => { lastSig = ''; render(); scrollTo(0, 0); });

  // countdowns ("Next in 2:41") without drawing the page again
  function tick() {
    for (const el of document.querySelectorAll('[data-next]')) {
      const left = Math.round((Number(el.dataset.next) - Date.now()) / 1000);
      el.textContent = left <= 0 ? 'Changing now' : left >= 3600 ? `Next in ${Math.floor(left / 3600)} h ${Math.round((left % 3600) / 60)} min` : `Next in ${Math.floor(left / 60)}:${String(left % 60).padStart(2, '0')}`;
    }
  }
  setInterval(tick, 1000);

  // ---------- signing in

  function deviceName() {
    const ua = navigator.userAgent;
    const os = /iPhone/.test(ua) ? 'iPhone' : /iPad/.test(ua) ? 'iPad' : /Android/.test(ua) ? 'Android' : /Windows/.test(ua) ? 'Windows' : /Mac/.test(ua) ? 'Mac' : /Linux/.test(ua) ? 'Linux' : 'Browser';
    const br = /Edg\//.test(ua) ? 'Edge' : /Firefox\//.test(ua) ? 'Firefox' : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : '';
    return br ? `${br} on ${os}` : os;
  }

  function signedIn(t, msg) {
    token = t; store.set('whs.token', t);
    shell = null; lastSig = '';
    stopPairing();
    render();
    refresh().then(schedule);
    if (msg) setTimeout(() => toast(msg), 50);
  }
  function signedOut(msg) {
    token = null; store.set('whs.token', null);
    S.doc = null; shell = null; lastSig = '';
    clearTimeout(timer);
    sheets.clear();
    for (const d of document.querySelectorAll('dialog')) d.remove();
    showSignIn(msg);
  }

  let pairTimer = null, pairing = 0;
  const stopPairing = () => { clearTimeout(pairTimer); pairing++; };

  async function showSignIn(message) {
    const app = document.getElementById('app');
    const status = h('div', { class: 'err', role: 'alert' }, message && message !== 'Signed out' ? message : '');
    const codeBox = h('div', { class: 'code', 'aria-live': 'polite' }, '––– –––');
    const when = h('div', { class: 'when' }, ' ');
    const keyIn = h('input', { class: 'field', type: 'password', placeholder: 'Sync key', autocomplete: 'current-password', id: 'key' });
    const keyBtn = h('button', { class: 'btn wide', type: 'submit' }, 'Sign in');
    const keyForm = h('form', { hidden: true, style: { display: 'grid', gap: '12px' }, onsubmit: async (e) => {
      e.preventDefault();
      keyBtn.disabled = true;
      try { const j = await api('register', { body: { key: keyIn.value, kind: 'web', name: deviceName() } }); signedIn(j.token, 'Signed in'); }
      catch (err) { status.textContent = err.message; keyBtn.disabled = false; }
    } }, keyIn, keyBtn);
    const pairPart = h('div', { style: { display: 'grid', gap: '22px' } },
      h('div', { class: 'code-wrap' }, h('div', { class: 'when' }, 'Pairing code'), codeBox, when),
      h('ul', { class: 'how' },
        h('li', {}, 'On the PC, right-click the tray icon and choose ', h('b', {}, 'Remote > Pair a device'), '. Type this code.'),
        h('li', {}, 'Or, on a phone that’s already signed in: ', h('b', {}, 'Devices > Pair a device'), '.')));
    app.replaceChildren(h('div', { class: 'signin pad' }, h('div', { class: 'panel' },
      h('header', {}, h('img', { src: '/icon.svg', alt: '' }), h('h1', {}, 'Wallhaven Shuffle'), h('p', {}, 'The wallpapers on your TV and PC, from here.')),
      pairPart,
      h('div', {}, h('button', { class: 'text-btn', onclick: (e) => { keyForm.hidden = false; e.currentTarget.remove(); keyIn.focus(); } }, 'Use the sync key instead'), keyForm),
      status)));

    let hello = {};
    try { hello = await api('hello'); } catch (e) { status.textContent = e.message; }
    if (hello.sync === false) {
      pairPart.replaceChildren(h('p', { class: 'note', style: { margin: 0, color: 'var(--fg-2)' } }, 'Sync isn’t set up on this service yet. Add Upstash Redis to the Vercel project (Storage > Create Database), set SYNC_KEY, and deploy again.'));
      return;
    }
    const mine = ++pairing;
    const start = async () => {
      if (mine !== pairing) return;
      try {
        const p = await api('pair', { body: { kind: 'web', name: deviceName() } });
        codeBox.textContent = `${p.code.slice(0, 3)} ${p.code.slice(3)}`;
        const poll = async () => {
          if (mine !== pairing) return;
          const left = Math.round((p.expires - Date.now()) / 1000);
          if (left <= 5) return start();
          when.textContent = `A new one in ${Math.floor(left / 60)}:${String(left % 60).padStart(2, '0')}`;
          try {
            const c = await api('claim', { body: { claim: p.claim } });
            if (c.token) return signedIn(c.token, 'Paired');
          } catch (e) { if (e.status === 404) return start(); }
          pairTimer = setTimeout(poll, document.hidden ? 6000 : 2500);
        };
        poll();
      } catch (e) {
        when.textContent = e.message;
        pairTimer = setTimeout(start, 15000);
      }
    };
    start();
  }

  // ---------- start

  async function boot() {
    const m = /[#&]link=([a-z2-9]{16})/.exec(location.hash);
    if (m) {
      history.replaceState(null, '', location.pathname); // used up either way; never left in the history
      try {
        const j = await api('redeem', { body: { code: m[1], name: deviceName() } });
        return signedIn(j.token, 'Signed in');
      } catch (e) {
        if (!token) return showSignIn(e.message);
      }
    }
    if (!token) return showSignIn();
    render();
    await refresh();
    schedule();
  }
  boot();
})();
