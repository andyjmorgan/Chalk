/* Chalk UI — read + delete. Hash routes: #/ · #/b/<board>?prefix= · #/b/<board>/changes · #/b/<board>/expiring */
(() => {
  const $ = (sel, el = document) => el.querySelector(sel);
  const h = (tag, attrs = {}, ...kids) => {
    const el = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs)) {
      if (v == null || v === false) continue;
      if (k === 'class') el.className = v;
      else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
      else if (k === 'html') el.innerHTML = v;
      else el.setAttribute(k, v === true ? '' : v);
    }
    for (const kid of kids.flat()) if (kid != null && kid !== false) el.append(kid.nodeType ? kid : document.createTextNode(String(kid)));
    return el;
  };
  const esc = (s) => String(s ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const store = { get(k, d) { try { const v = localStorage.getItem(k); return v == null ? d : JSON.parse(v); } catch { return d; } }, set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch {} }, del(k) { try { localStorage.removeItem(k); } catch {} } };

  // ---- time ----
  const rel = (iso) => {
    if (!iso) return '—';
    const t = new Date(iso).getTime(), d = Date.now() - t, a = Math.abs(d), s = d > 0 ? ' ago' : '';
    const f = (n, u) => (d > 0 ? `${n}${u} ago` : `in ${n}${u}`);
    if (a < 60e3) return d > 0 ? 'just now' : 'in <1m';
    if (a < 3600e3) return f(Math.round(a / 60e3), 'm');
    if (a < 86400e3) return f(Math.round(a / 3600e3), 'h');
    if (a < 30 * 86400e3) return f(Math.round(a / 86400e3), 'd');
    return new Date(iso).toISOString().slice(0, 10) + (s && '');
  };
  const abs = (iso) => (iso ? new Date(iso).toISOString().replace('T', ' ').replace(/\.\d+Z$/, 'Z') : '');
  const timeEl = (iso, cls = '') => h('span', { class: `mono ${cls}`, title: abs(iso) }, rel(iso));
  const soon = (iso) => iso && new Date(iso).getTime() - Date.now() < 86400e3;

  // ---- auth / api ----
  let apiKey = store.get('chalk.apiKey', null);
  const showLogin = (msg = '') => { $('#loginErr').textContent = msg; $('#login').classList.remove('hidden'); $('#apiKey').focus(); };
  const hideLogin = () => $('#login').classList.add('hidden');
  async function api(path, opts = {}) {
    if (!apiKey) { showLogin(); throw new Error('no key'); }
    const r = await fetch(path, { ...opts, headers: { 'X-Api-Key': apiKey, ...(opts.body ? { 'Content-Type': 'application/json' } : {}), ...(opts.headers || {}) } });
    if (r.status === 401) { showLogin('That key was rejected.'); throw new Error('unauthorized'); }
    const j = await r.json().catch(() => ({}));
    if (!r.ok) throw Object.assign(new Error(j.error?.message || r.statusText), { code: j.error?.code, status: r.status });
    return j;
  }
  $('#loginForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    apiKey = $('#apiKey').value.trim();
    try { const me = await api('/api/v1/me'); store.set('chalk.apiKey', apiKey); hideLogin(); $('#who').textContent = me.data.principal; route(); }
    catch (err) { apiKey = null; showLogin(err.message === 'unauthorized' ? 'That key was rejected.' : err.message); }
  });
  $('#logout').addEventListener('click', () => { store.del('chalk.apiKey'); apiKey = null; $('#who').textContent = ''; showLogin(); });
  $('#theme').addEventListener('click', () => {
    const cur = document.documentElement.dataset.theme || (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
    const next = cur === 'dark' ? 'light' : 'dark';
    document.documentElement.dataset.theme = next; store.set('chalk.theme', next);
  });
  { const t = store.get('chalk.theme', null); if (t) document.documentElement.dataset.theme = t; }

  // ---- routing ----
  const app = $('#app'), crumbs = $('#crumbs');
  const parse = () => {
    const [path, qs] = location.hash.replace(/^#/, '').split('?');
    const parts = path.split('/').filter(Boolean);
    return { parts, q: new URLSearchParams(qs || '') };
  };
  const link = (parts, q = {}) => '#/' + parts.map(encodeURIComponent).join('/') + (Object.keys(q).length ? '?' + new URLSearchParams(Object.fromEntries(Object.entries(q).filter(([, v]) => v != null && v !== ''))) : '');
  const setCrumbs = (...items) => { crumbs.replaceChildren(...items.flatMap((it, i) => [i ? h('span', {}, '›') : null, typeof it === 'string' ? h('span', {}, it) : it]).filter(Boolean)); };
  async function route() {
    if (!apiKey) return showLogin();
    const { parts, q } = parse();
    closeDrawer();
    try {
      if (parts.length === 0) await viewHome();
      else if (parts[0] === 'b' && parts[1]) {
        const board = decodeURIComponent(parts[1]);
        if (parts[2] === 'changes') await viewChanges(board, q);
        else if (parts[2] === 'expiring') await viewExpiring(board, q);
        else await viewBoard(board, q);
      } else app.replaceChildren(h('div', { class: 'empty' }, 'Nothing here.'));
    } catch (err) { if (err.message !== 'no key' && err.message !== 'unauthorized') app.replaceChildren(h('div', { class: 'empty' }, `Error: ${err.message}`)); }
  }
  addEventListener('hashchange', route);

  // ---- home ----
  async function viewHome() {
    setCrumbs('Boards');
    app.replaceChildren(h('div', { class: 'empty' }, 'Loading…'));
    const { data } = await api('/api/v1/boards');
    if (!data.length) return app.replaceChildren(h('div', { class: 'empty' }, 'No boards visible to this key.'));
    app.replaceChildren(h('div', { class: 'cards' }, data.map((b) => h('a', { class: 'bcard', href: link(['b', b.name]) },
      h('h2', {}, b.name, b.stale ? h('span', { class: 'badge stale', title: 'No write for longer than the TTL' }, 'stale') : null, b.embed ? h('span', { class: 'badge', title: `Vector search on ${b.embed.fields.join(', ')}` }, 'embed') : null),
      h('p', { class: 'desc' }, b.description || ' '),
      h('div', { class: 'stats' },
        h('div', { class: 'stat' }, h('b', {}, b.counts.live), h('span', {}, 'live')),
        h('div', { class: 'stat' }, h('b', {}, b.counts.writesToday), h('span', {}, 'written today')),
        h('div', { class: 'stat' }, h('b', {}, b.counts.changedToday), h('span', {}, 'changed today')),
        h('div', { class: 'stat' }, h('b', { class: b.counts.expiring24h ? 'soon' : '' }, b.counts.expiring24h), h('span', {}, 'expiring 24h'))),
      h('div', { class: 'foot' },
        h('span', { class: 'mono' }, b.key.join(' / '), b.ttl ? ` · ttl ${b.ttl}` : ' · keep'),
        b.lastWrite ? h('span', {}, 'last write ', timeEl(b.lastWrite.at), ' by ', h('span', { class: 'mono' }, b.lastWrite.writer)) : h('span', {}, 'never written'))))));
  }

  // ---- board page ----
  let boardCache = {};
  async function describe(board) { return boardCache[board] ||= (await api(`/api/v1/boards/${encodeURIComponent(board)}`)).data; }
  const tabs = (board, on, prefix) => h('div', { class: 'tabs' },
    h('a', { href: link(['b', board], { prefix }), class: on === 'records' ? 'on' : '' }, 'Records'),
    h('a', { href: link(['b', board, 'changes'], { prefix }), class: on === 'changes' ? 'on' : '' }, 'Changes'),
    h('a', { href: link(['b', board, 'expiring']), class: on === 'expiring' ? 'on' : '' }, 'Expiring'));

  async function viewBoard(board, q) {
    const def = await describe(board);
    const prefix = q.get('prefix') || '', query = q.get('q') || '';
    const filters = q.getAll('filter');
    setCrumbs(h('a', { href: '#/' }, 'Boards'), h('a', { href: link(['b', board]) }, board), prefix ? h('span', { class: 'mono' }, prefix) : null);
    const lastSeenKey = `chalk.seen.${board}`;
    const since = store.get(lastSeenKey, null);

    const bar = h('div', { class: 'bar' }, h('h1', {}, board), h('span', { class: 'meta' }, def.description), h('span', { class: 'meta mono' }, `ttl ${def.ttl || 'keep'} · ${def.counts.live} live`), h('div', { class: 'spacer' }), tabs(board, 'records', prefix));
    const treeEl = h('div', { class: 'panel tree' });
    const tableEl = h('div', { class: 'panel' });
    app.replaceChildren(bar, h('div', { class: 'split' }, treeEl, tableEl));
    await Promise.all([renderTree(treeEl, board, def, prefix, since), renderTable(tableEl, board, def, { prefix, query, filters })]);
    store.set(lastSeenKey, new Date().toISOString());
  }

  async function renderTree(el, board, def, selected, since) {
    el.replaceChildren(h('div', { class: 'troot' }, def.segments[0]));
    const root = h('div', { class: `tnode ${selected === '' ? 'sel' : ''}`, onclick: () => (location.hash = link(['b', board])) }, h('span', { class: 'tw' }, '▾'), h('span', { class: 'seg' }, 'all'), h('span', { class: 'cnt' }, def.counts.live));
    el.append(root);
    const kids = h('div', { class: 'tkids' });
    el.append(kids);
    await loadChildren(kids, board, def, '', selected, since);
  }
  async function loadChildren(container, board, def, prefix, selected, since) {
    container.replaceChildren(h('div', { class: 'muted', style: 'padding:3px 8px' }, '…'));
    const qs = new URLSearchParams({ prefix }); if (since) qs.set('since', since);
    const { data } = await api(`/api/v1/boards/${encodeURIComponent(board)}/tree?${qs}`);
    container.replaceChildren();
    if (!data.children.length) return container.append(h('div', { class: 'muted', style: 'padding:3px 8px' }, 'empty'));
    for (const c of data.children) {
      const isSel = selected === c.prefix || (c.leaf && selected === c.prefix);
      const open = !c.leaf && selected.startsWith(c.prefix) && selected !== c.prefix;
      const sub = h('div', { class: 'tkids' });
      const tw = h('span', { class: 'tw' }, c.leaf ? '·' : open ? '▾' : '▸');
      const node = h('div', { class: `tnode ${isSel ? 'sel' : ''}`, title: c.prefix },
        tw, h('span', { class: 'seg' }, c.segment), c.changedSince ? h('span', { class: 'dot', title: `${c.changedSince} changes since your last visit` }) : null,
        h('span', { class: 'cnt' }, c.live, c.expiring24h ? h('span', { class: 'soon' }, ` ·${c.expiring24h}`) : null));
      let loaded = open;
      node.addEventListener('click', async (e) => {
        if (c.leaf) { location.hash = link(['b', board], { prefix: c.prefix }); return; }
        if (e.target === tw) { // toggle only
          const isOpen = !sub.classList.contains('hidden');
          sub.classList.toggle('hidden', isOpen); tw.textContent = isOpen ? '▸' : '▾';
          if (!isOpen && !loaded) { loaded = true; await loadChildren(sub, board, def, c.prefix, selected, since); }
          return;
        }
        location.hash = link(['b', board], { prefix: c.prefix });
      });
      container.append(node, sub);
      if (open) await loadChildren(sub, board, def, c.prefix, selected, since); else sub.classList.add('hidden');
    }
  }

  async function renderTable(el, board, def, { prefix, query, filters }, cursor = null) {
    const bq = (patch) => link(['b', board], { prefix, q: query, ...patch });
    const search = h('input', { type: 'search', placeholder: prefix ? `Search within ${prefix}` : 'Search this board', value: query });
    search.addEventListener('keydown', (e) => { if (e.key === 'Enter') location.hash = link(['b', board], { prefix, q: search.value.trim(), filter: filters }); });
    const tools = h('div', { class: 'tools' }, search,
      query ? h('button', { onclick: () => (location.hash = link(['b', board], { prefix, filter: filters })) }, 'Clear search') : null,
      h('div', { class: 'spacer' }),
      prefix ? deleteControl(`Delete everything under ${prefix}`, async () => {
        const { data } = await api(`/api/v1/boards/${encodeURIComponent(board)}/records?prefix=${encodeURIComponent(prefix)}&expect=__N__`.replace('&expect=__N__', ''), { method: 'DELETE' });
        boardCache = {}; location.hash = link(['b', board], { prefix: prefix.split('/').slice(0, -2).join('/') + (prefix.split('/').length > 2 ? '/' : '') });
        return data.deleted;
      }, async () => (await api(`/api/v1/boards/${encodeURIComponent(board)}/records?prefix=${encodeURIComponent(prefix)}&limit=1`)).meta.total) : null);
    el.replaceChildren(tools, h('div', { class: 'empty' }, 'Loading…'));

    let rows, meta, next = null;
    if (query) {
      const qs = new URLSearchParams({ q: query, limit: 50 }); if (prefix) qs.set('prefix', prefix); for (const f of filters) qs.append('filter', f);
      ({ data: rows, meta } = await api(`/api/v1/boards/${encodeURIComponent(board)}/search?${qs}`));
    } else {
      const qs = new URLSearchParams({ limit: 100 }); if (prefix) qs.set('prefix', prefix); if (cursor) qs.set('cursor', cursor); for (const f of filters) qs.append('filter', f);
      const r = await api(`/api/v1/boards/${encodeURIComponent(board)}/records?${qs}`); rows = r.data; meta = r.meta; next = r.cursor;
    }

    // facets
    const chips = h('div', { class: 'chips' });
    if (meta.facets) for (const [field, counts] of Object.entries(meta.facets)) {
      chips.append(h('span', { class: 'chipk mono' }, field.replace(/^\//, '')));
      for (const [val, n] of Object.entries(counts)) {
        const f = `${field}=${val}`, on = filters.includes(f);
        if (field === 'writer' && Object.keys(counts).length === 1) { chips.append(h('span', { class: 'chip', style: 'cursor:default' }, val)); continue; }
        if (field === 'writer') continue; // writer isn't a pointer filter; show only when single
        chips.append(h('span', { class: `chip ${on ? 'on' : ''}`, onclick: () => (location.hash = link(['b', board], { prefix, q: query, filter: on ? filters.filter((x) => x !== f) : [...filters, f] })) }, val, h('span', { class: 'n' }, n)));
      }
    }

    // columns: remaining key segments, search fields, then metadata
    const segNames = def.segments;
    const depth = segNames.length === 1 ? 0 : (prefix ? prefix.split('/').filter(Boolean).length : 0);
    const keyCols = segNames.slice(depth);
    const fieldCols = (def.search?.fields || []).map((p) => p.replace(/^\//, '')).filter((f) => !segNames.includes(f));
    const enumCols = Object.entries(def.schema?.properties || {}).filter(([, d]) => d.enum).map(([k]) => k).filter((k) => !segNames.includes(k) && !fieldCols.includes(k));
    const head = h('tr', {}, ...keyCols.map((c) => h('th', {}, c)), ...enumCols.map((c) => h('th', {}, c)), ...fieldCols.map((c) => h('th', {}, c)), query ? h('th', {}, 'score') : null, h('th', {}, 'observed'), h('th', {}, 'expires'), h('th', { class: 'num' }, 'rev'), h('th', {}, 'writer'), h('th', {}));
    const get = (v, path) => path.split('.').reduce((o, k) => (o == null ? o : o[k]), v);
    const cell = (v) => (v == null ? h('span', { class: 'muted' }, '—') : typeof v === 'object' ? h('code', {}, JSON.stringify(v)) : String(v));
    const body = h('tbody', {}, rows.map((r) => {
      const tr = h('tr', { title: r.key },
        ...keyCols.map((c) => h('td', { class: 'k mono' }, r.segments?.[c] ?? r.key)),
        ...enumCols.map((c) => { const v = r.value[c]; return h('td', {}, v != null ? [h('span', { class: `st ${esc(v)}` }), String(v)] : h('span', { class: 'muted' }, '—')); }),
        ...fieldCols.map((c) => h('td', { class: 'ell', title: typeof get(r.value, c) === 'string' ? get(r.value, c) : '' }, r.snippet && c === fieldCols[0] ? h('span', { html: r.snippet }) : cell(get(r.value, c)))),
        query ? h('td', { class: 'num mono' }, r.score?.toFixed(3)) : null,
        h('td', {}, timeEl(r.observedAt)), h('td', {}, r.expiresAt ? timeEl(r.expiresAt, soon(r.expiresAt) ? 'soon' : '') : h('span', { class: 'muted' }, 'keep')),
        h('td', { class: 'num mono' }, r.revision), h('td', { class: 'mono muted' }, r.writer),
        h('td', { onclick: (e) => e.stopPropagation() }, deleteControl(`Delete ${r.key}`, async () => { await api(`/api/v1/boards/${encodeURIComponent(board)}/records/${encodeURIComponent(r.key)}`, { method: 'DELETE' }); tr.remove(); boardCache = {}; return 1; })));
      tr.addEventListener('click', () => { [...body.children].forEach((x) => x.classList.remove('sel')); tr.classList.add('sel'); openDrawer(board, def, r); });
      return tr;
    }));
    const table = h('div', { class: 'tblwrap' }, h('table', {}, h('thead', {}, head), body));
    const pager = h('div', { class: 'pager' }, h('span', {}, query ? `${rows.length} results · ${meta.mode}` : `${rows.length} of ${meta.total} shown`),
      next ? h('button', { onclick: () => renderTable(el, board, def, { prefix, query, filters }, next) }, 'Next page →') : null);
    el.replaceChildren(tools, chips.children.length ? chips : null, rows.length ? table : h('div', { class: 'empty' }, query ? 'No matches.' : 'No live records here.'), pager);
  }

  // Inline two-step delete: button → "Delete N? [Yes] [No]". No browser dialogs.
  function deleteControl(label, doDelete, countFn) {
    const wrap = h('span', {});
    const btn = h('button', { class: 'danger', title: label }, '🗑');
    btn.addEventListener('click', async (e) => {
      e.stopPropagation();
      let n = 1; if (countFn) { try { n = await countFn(); } catch {} }
      const yes = h('button', { class: 'danger' }, 'Delete'), no = h('button', {}, 'Keep');
      const box = h('span', { class: 'confirm' }, h('span', {}, countFn ? `Delete ${n} record${n === 1 ? '' : 's'}?` : 'Delete this record?'), yes, no);
      wrap.replaceChildren(box);
      no.addEventListener('click', (ev) => { ev.stopPropagation(); wrap.replaceChildren(btn); });
      yes.addEventListener('click', async (ev) => {
        ev.stopPropagation(); yes.disabled = true; yes.textContent = '…';
        try { const d = await doDelete(n); box.replaceChildren(h('span', {}, `Deleted ${d}.`)); setTimeout(() => wrap.replaceChildren(), 1500); }
        catch (err) { box.replaceChildren(h('span', {}, err.message), no); }
      });
    });
    wrap.append(btn);
    return wrap;
  }

  // ---- drawer ----
  let drawerEl = null;
  function closeDrawer() { drawerEl?.remove(); drawerEl = null; }
  async function openDrawer(board, def, rec) {
    closeDrawer();
    const body = h('div', { class: 'body' }, h('div', { class: 'empty' }, 'Loading history…'));
    drawerEl = h('aside', { class: 'drawer' }, h('header', {}, h('span', { class: 'k mono', title: rec.key }, rec.key), h('button', { class: 'icon', onclick: closeDrawer, 'aria-label': 'Close' }, '✕')), body);
    document.body.append(drawerEl);
    const onKey = (e) => { if (e.key === 'Escape') { closeDrawer(); removeEventListener('keydown', onKey); } };
    addEventListener('keydown', onKey);
    const { data } = await api(`/api/v1/boards/${encodeURIComponent(board)}/records/${encodeURIComponent(rec.key)}/history`);
    body.replaceChildren(h('p', { class: 'muted' }, `${data.length} revision${data.length === 1 ? '' : 's'} · newest first`), ...data.map((rv) => h('div', { class: 'rev' },
      h('div', { class: 'rh' }, h('b', { class: 'mono' }, `r${rv.revision}`), h('span', {}, 'written ', timeEl(rv.writtenAt)), h('span', {}, 'observed ', timeEl(rv.observedAt)), rv.expiresAt ? h('span', {}, 'expires ', timeEl(rv.expiresAt)) : h('span', {}, 'keep'), h('span', { class: 'mono' }, rv.writer)),
      rv.diff ? (rv.diff.length ? h('div', { class: 'diff' }, rv.diff.map((d) => h('div', {}, h('code', {}, d.path || '/'), h('span', {}, h('span', { class: 'from' }, JSON.stringify(d.from)), ' → ', h('span', { class: 'to' }, JSON.stringify(d.to)))))) : h('div', { class: 'diff muted' }, 'no change in value')) : h('div', { class: 'diff muted' }, 'first revision'),
      h('details', {}, h('summary', {}, 'value'), h('pre', { class: 'val' }, JSON.stringify(rv.value, null, 2))))));
  }

  // ---- changes ----
  async function viewChanges(board, q) {
    const def = await describe(board);
    const prefix = q.get('prefix') || '', kinds = q.get('kind') || '', days = Number(q.get('days') || 1);
    setCrumbs(h('a', { href: '#/' }, 'Boards'), h('a', { href: link(['b', board]) }, board), 'Changes');
    const since = new Date(Date.now() - days * 86400e3).toISOString();
    const sel = (name, val, opts) => { const s = h('select', { onchange: () => (location.hash = link(['b', board, 'changes'], { prefix, kind: kinds, days, [name]: s.value })) }); for (const [v, l] of opts) s.append(h('option', { value: v, selected: String(v) === String(val) }, l)); return s; };
    const prefixIn = h('input', { class: 'mono', placeholder: 'prefix, e.g. office/', value: prefix });
    prefixIn.addEventListener('keydown', (e) => { if (e.key === 'Enter') location.hash = link(['b', board, 'changes'], { prefix: prefixIn.value.trim(), kind: kinds, days }); });
    const bar = h('div', { class: 'bar' }, h('h1', {}, board), h('span', { class: 'meta' }, 'what the agents decided'), h('div', { class: 'spacer' }), tabs(board, 'changes', prefix));
    const tools = h('div', { class: 'tools' }, sel('days', days, [[1, 'last 24h'], [3, 'last 3 days'], [7, 'last 7 days'], [30, 'last 30 days']]),
      sel('kind', kinds, [['', 'all kinds'], ['created', 'created'], ['changed', 'changed'], ['expired', 'expired'], ['deleted', 'deleted']]), prefixIn);
    const feed = h('div', { class: 'feed' }, h('div', { class: 'empty' }, 'Loading…'));
    app.replaceChildren(bar, h('div', { class: 'panel' }, tools, feed));
    const qs = new URLSearchParams({ since, limit: 500 }); if (prefix) qs.set('prefix', prefix); if (kinds) qs.set('kind', kinds);
    const { data, meta } = await api(`/api/v1/boards/${encodeURIComponent(board)}/changes?${qs}`);
    if (!data.length) return feed.replaceChildren(h('div', { class: 'empty' }, 'Nothing changed in this window.'));
    const show = (def.search?.fields || []).map((p) => p.replace(/^\//, '')).concat(Object.entries(def.schema?.properties || {}).filter(([, d]) => d.enum).map(([k]) => k)).filter((f, i, a) => a.indexOf(f) === i && !def.segments.includes(f));
    const fromTo = (ev) => {
      const parts = [];
      for (const f of show) {
        const a = ev.from?.[f], b = ev.to?.[f];
        if (ev.kind === 'changed' && JSON.stringify(a) === JSON.stringify(b)) continue;
        parts.push(h('span', {}, h('span', { class: 'p' }, `${f}: `), a != null && ev.kind !== 'created' ? h('span', { class: ev.to ? 'a' : '' }, String(a)) : null, a != null && b != null ? ' → ' : '', b != null ? h('span', { class: 'b' }, String(b)) : null, ' '));
      }
      return parts.length ? parts : [h('span', { class: 'p' }, JSON.stringify(ev.to ?? ev.from).slice(0, 160))];
    };
    const summary = h('div', { class: 'chips' }, ...Object.entries(meta.counts || {}).map(([k, n]) => h('span', { class: `badge kind-${k}` }, `${n} ${k}`)));
    feed.replaceChildren(summary);
    let day = '';
    for (const ev of data) {
      const d = ev.at.slice(0, 10);
      if (d !== day) { day = d; feed.append(h('div', { class: 'day' }, d === new Date().toISOString().slice(0, 10) ? 'today' : d)); }
      feed.append(h('div', { class: 'ev' }, timeEl(ev.at, 'muted'), h('span', {}, h('span', { class: `badge kind-${ev.kind}` }, ev.kind)),
        h('a', { class: 'mono', href: link(['b', board], def.segments.length > 1 ? { prefix: ev.key.split('/').slice(0, -1).join('/') + '/' } : {}), title: ev.key }, ev.key),
        h('span', { class: 'ft' }, ...fromTo(ev)), h('span', { class: 'mono muted' }, ev.writer)));
    }
  }

  // ---- expiring ----
  async function viewExpiring(board) {
    const def = await describe(board);
    setCrumbs(h('a', { href: '#/' }, 'Boards'), h('a', { href: link(['b', board]) }, board), 'Expiring');
    const bar = h('div', { class: 'bar' }, h('h1', {}, board), h('span', { class: 'meta' }, 'live records the sweep will remove within 24 hours'), h('div', { class: 'spacer' }), tabs(board, 'expiring', ''));
    const panel = h('div', { class: 'panel' }, h('div', { class: 'empty' }, 'Loading…'));
    app.replaceChildren(bar, panel);
    const { data, meta } = await api(`/api/v1/boards/${encodeURIComponent(board)}/records?expiringWithin=24h&sort=expiresAt&limit=500`);
    if (!data.length) return panel.replaceChildren(h('div', { class: 'empty' }, 'Nothing expires in the next 24 hours.'));
    const fieldCols = (def.search?.fields || []).map((p) => p.replace(/^\//, ''));
    const body = h('tbody', {}, data.map((r) => { const tr = h('tr', {}, h('td', { class: 'k mono' }, r.key), ...fieldCols.map((c) => h('td', { class: 'ell' }, r.value[c] ?? '')), h('td', {}, timeEl(r.expiresAt, 'soon')), h('td', {}, timeEl(r.observedAt)), h('td', { class: 'num mono' }, r.revision), h('td', { class: 'mono muted' }, r.writer)); tr.addEventListener('click', () => openDrawer(board, def, r)); return tr; }));
    panel.replaceChildren(h('div', { class: 'tools' }, h('span', { class: 'muted' }, `${meta.total} expiring · if one of these should live, fix the agent's TTL, not the record`)),
      h('div', { class: 'tblwrap' }, h('table', {}, h('thead', {}, h('tr', {}, h('th', {}, 'key'), ...fieldCols.map((c) => h('th', {}, c)), h('th', {}, 'expires'), h('th', {}, 'observed'), h('th', { class: 'num' }, 'rev'), h('th', {}, 'writer'))), body)));
  }

  // ---- boot ----
  (async () => {
    if (!apiKey) return showLogin();
    try { const me = await api('/api/v1/me'); $('#who').textContent = me.data.principal; hideLogin(); route(); }
    catch (e) { /* login shown by api() */ }
  })();
})();
