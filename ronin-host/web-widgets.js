// Ronin host: the widgets (agenda, system ink, focus timer, album, world clocks) for the web renderer.
// Injected next to the bridge script; it never edits the page, only adds its own elements, styled with the
// page's CSS variables and placed with the page's own place(). The host sends ready-made data ("wd" messages).
(() => {
  const WIDGETS = [
    // id, show key, x key, y key, scale key
    ['calendar', 'showCalendar', 'calX', 'calY', 'calScale'],
    ['system', 'showSystem', 'sysX', 'sysY', 'sysScale'],
    ['timer', 'showTimer', 'timerX', 'timerY', 'timerScale'],
    ['album', 'showAlbum', 'albumX', 'albumY', 'albumScale'],
    ['world', 'showWorld', 'worldX', 'worldY', 'worldScale'],
  ];
  const P = {}, D = {};
  let BOARD = null; // the task board for this monitor (TaskStore.ForWallpaper)
  let ready = false;
  const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const post = m => { try { window.chrome.webview.postMessage(m); } catch (e) {} };

  window.__roninWidgets = {
    props(v) { Object.assign(P, v); if (ready) render(); },
    data(v) { Object.assign(D, v); if (ready) render(); },
    board(v) { BOARD = v; if (ready) render(); },
  };

  const CSS = `
    .rw { position: fixed; z-index: 10; color: var(--text); font-family: var(--font); width: max-content; text-shadow: 0 1px 12px rgba(0,0,0,.95); }
    .guides .rw { outline: 1px dashed var(--accent); outline-offset: 12px; }
    .rw .title { font-size: 14px; letter-spacing: .5em; text-transform: uppercase; }
    .rw .rule { margin: 12px 0 14px; } .rw .rule span { width: 60px; }
    .rw table { border-collapse: collapse; }
    .rw[data-align=right] table { margin-left: auto; } .rw[data-align=center] table { margin: 0 auto; }
    .rw td { padding: 3px 0; vertical-align: middle; white-space: nowrap; }
    .rw .lab { font-size: 11px; letter-spacing: .3em; text-transform: uppercase; opacity: .8; padding-right: 18px; }
    .rw .day { font-size: 11px; letter-spacing: .35em; text-transform: uppercase; color: var(--accent); opacity: .85; padding-top: 10px; }
    .rw .ev { font-family: var(--quote-font); font-size: 20px; }
    .rw .tm { font-size: 12px; letter-spacing: .12em; opacity: .7; padding-right: 16px; }
    .rw .tm.now { color: var(--accent); opacity: 1; }
    .rw .val { font-size: 13px; color: var(--accent); padding-left: 14px; }
    .rw .note { font-family: var(--quote-font); font-size: 18px; opacity: .6; }
    .rw .big { font-size: 26px; letter-spacing: .03em; }
    .rw .small { font-size: 9px; letter-spacing: .25em; color: var(--accent); margin-left: 6px; }
    .rw .off1 { font-size: 10px; color: var(--accent); margin-left: 8px; vertical-align: super; }
    #rw-timer { cursor: pointer; text-align: center; }
    #rw-timer .clock { position: absolute; left: 0; right: 0; top: 50px; font-size: 30px; letter-spacing: .04em; }
    #rw-timer .phase { position: absolute; left: 0; right: 0; top: 94px; font-size: 10px; letter-spacing: .45em; color: var(--accent); }
    #rw-timer .ring { position: relative; width: 150px; height: 150px; margin: 0 auto; }
    #rw-timer .hint { font-size: 10px; letter-spacing: .35em; opacity: .72; margin-top: 10px; }
    #rw-timer .reset { font-size: 11px; letter-spacing: .35em; color: var(--accent); margin-top: 8px; cursor: pointer; }
    #rw-album .wrap { display: flex; align-items: center; gap: 18px; }
    #rw-album img { width: 96px; height: 96px; object-fit: cover; border: 1px solid color-mix(in srgb, var(--accent) 55%, transparent); }
    #rw-album .t { font-family: var(--quote-font); font-size: 23px; line-height: 1.15; max-width: 300px; }
    #rw-album .a { font-size: 11px; letter-spacing: .32em; text-transform: uppercase; color: var(--accent); margin-top: 8px; }
    #rw-album .none { font-size: 11px; letter-spacing: .35em; opacity: .5; }
    /* the task board replaces the page's own simple list */
    #todo { display: none !important; }
    #rw-tasks .rw-row { display: flex; align-items: center; gap: 14px; padding: 5px 0; }
    #rw-tasks[data-align=right] .rw-row { flex-direction: row-reverse; }
    #rw-tasks[data-align=center] .rw-row { justify-content: center; }
    #rw-tasks .dia { width: 8px; height: 8px; flex: none; transform: rotate(45deg); border: 1px solid var(--accent); position: relative; cursor: pointer; }
    #rw-tasks .dia.s1::after { content: ''; position: absolute; inset: 2px; background: var(--accent); box-shadow: 0 0 6px var(--accent); }
    #rw-tasks .dia.s2 { background: var(--accent); box-shadow: 0 0 10px var(--accent); }
    #rw-tasks .tt { font-family: var(--quote-font); font-size: 21px; line-height: 1.25; cursor: pointer; }
    #rw-tasks .task.s2 { opacity: .4; } #rw-tasks .task.s2 .tt { text-decoration: line-through; text-decoration-color: var(--accent); }
    #rw-tasks .tag { font-size: 9px; letter-spacing: .35em; color: var(--accent); }
    #rw-tasks .mk { font-size: 11px; opacity: .6; } #rw-tasks .mk.full { color: var(--accent); opacity: .9; }
    #rw-tasks .head { margin-top: 14px; padding: 4px 0 6px; border-bottom: 1px solid color-mix(in srgb, var(--accent) 22%, transparent); cursor: pointer; }
    #rw-tasks .head .ht { font-size: 12px; letter-spacing: .4em; text-transform: uppercase; color: var(--accent); }
    #rw-tasks .head .fold { color: var(--accent); opacity: .8; font-size: 12px; width: 8px; }
    #rw-tasks .head .cnt { font-size: 10px; letter-spacing: .2em; opacity: .5; } #rw-tasks .head .cnt.full { color: var(--accent); opacity: .8; }
    #rw-tasks .ctl { display: none; gap: 12px; color: var(--accent); font-family: 'Segoe UI Symbol'; font-size: 15px; }
    #rw-tasks .rw-row.hov .ctl { display: inline-flex; }
    #rw-tasks .ctl span { cursor: pointer; padding: 0 2px; }
    #rw-tasks .foot { margin-top: 12px; font-size: 10px; letter-spacing: .35em; color: var(--accent); opacity: .6; }
    #rw-tasks .foot span { cursor: pointer; } #rw-tasks .foot .sep { opacity: .6; margin: 0 14px; cursor: default; }
    #rw-tasks .none { font-family: var(--quote-font); font-size: 18px; opacity: .55; margin-top: 14px; }
    /* the background behind the tasks (todoBg: 1 one panel, 2 a box per line; styles match TaskStyle.cs) */
    #rw-tasks[data-bg="1"]::before, #rw-tasks[data-bg="2"] > :not(.rule)::before {
      content: ''; position: absolute; z-index: -1; pointer-events: none; border-radius: var(--rw-round);
      background: linear-gradient(var(--rw-fill-top), var(--rw-fill)); }
    #rw-tasks[data-bg="1"]::before { inset: -16px -20px; }
    #rw-tasks[data-bg="2"] > :not(.rule) { position: relative; }
    #rw-tasks[data-bg="2"] > :not(.rule)::before { inset: 1.5px -12px; }
    #rw-tasks[data-bgs="0"]::before, #rw-tasks[data-bgs="0"] > :not(.rule)::before { box-shadow: inset 0 0 0 1px var(--rw-edge); }
    #rw-tasks[data-bgs="0"][data-bg="1"]::before {
      background: linear-gradient(90deg, transparent, var(--rw-gold), transparent) top / 100% 1px no-repeat, linear-gradient(var(--rw-fill-top), var(--rw-fill)); }
    #rw-tasks[data-bgs="1"][data-bg="1"]::before, #rw-tasks[data-bgs="1"][data-bg="2"] > :not(.rule)::before {
      box-shadow: inset 0 0 0 1px var(--rw-edge);
      background:
        linear-gradient(var(--rw-gold), var(--rw-gold)) top left / 12px 1px no-repeat, linear-gradient(var(--rw-gold), var(--rw-gold)) top left / 1px 12px no-repeat,
        linear-gradient(var(--rw-gold), var(--rw-gold)) top right / 12px 1px no-repeat, linear-gradient(var(--rw-gold), var(--rw-gold)) top right / 1px 12px no-repeat,
        linear-gradient(var(--rw-gold), var(--rw-gold)) bottom left / 12px 1px no-repeat, linear-gradient(var(--rw-gold), var(--rw-gold)) bottom left / 1px 12px no-repeat,
        linear-gradient(var(--rw-gold), var(--rw-gold)) bottom right / 12px 1px no-repeat, linear-gradient(var(--rw-gold), var(--rw-gold)) bottom right / 1px 12px no-repeat,
        linear-gradient(var(--rw-fill-top), var(--rw-fill)); }`;

  // TaskStyle.cs, in the same order: [edge alpha, shade]
  const BG_STYLES = [[.32, .25], [.10, .15], [0, 0]];
  function rgba(hex, a) {
    let c = String(hex || '#0c0d10').replace('#', '');
    if (c.length === 3) c = c.split('').map(x => x + x).join('');
    const n = parseInt(c.slice(-6), 16) || 0;
    return `rgba(${n >> 16 & 255},${n >> 8 & 255},${n & 255},${Math.max(0, Math.min(1, a)).toFixed(3)})`;
  }
  function styleBackdrop(e) {
    const mode = Math.max(0, Math.min(2, +(P.todoBg ?? 0))), si = Math.max(0, Math.min(BG_STYLES.length - 1, +(P.todoBgStyle ?? 0)));
    const op = Math.max(0, Math.min(1, (+(P.todoBgOpacity ?? 55)) / 100)), gold = Math.min(1, op * 2), [edge, shade] = BG_STYLES[si];
    e.dataset.bg = mode; e.dataset.bgs = si;
    if (!mode) return;
    const fill = P.todoBgColor || '#0c0d10';
    e.style.setProperty('--rw-fill', rgba(fill, op));
    e.style.setProperty('--rw-fill-top', rgba(fill, op * (1 - shade)));
    e.style.setProperty('--rw-edge', `color-mix(in srgb, var(--accent) ${Math.round(edge * gold * 100)}%, transparent)`);
    e.style.setProperty('--rw-gold', `color-mix(in srgb, var(--accent) ${Math.round(.85 * gold * 100)}%, transparent)`);
    e.style.setProperty('--rw-round', Math.max(0, Math.min(24, +(P.todoBgRound ?? 3))) + 'px');
  }

  function el(id) {
    let e = document.getElementById('rw-' + id);
    if (!e) {
      e = document.createElement('div');
      e.id = 'rw-' + id; e.className = 'rw off';
      document.body.appendChild(e);
    }
    return e;
  }

  const header = t => `<div class="title">${esc(t)}</div><div class="rule"><span></span><i></i><span></span></div>`;

  function spark(hist) {
    const w = 150, h = 22;
    if (!hist || hist.length < 2) return `<svg width="${w}" height="${h}"><line x1="0" y1="${h}" x2="${w}" y2="${h}" stroke="currentColor" stroke-opacity=".16"/></svg>`;
    const step = w / 59, x0 = w - (hist.length - 1) * step;
    const pts = hist.map((v, i) => `${(x0 + i * step).toFixed(1)},${(h - Math.max(0, Math.min(100, v)) / 100 * h).toFixed(1)}`).join(' ');
    return `<svg width="${w}" height="${h}" style="overflow:visible"><line x1="0" y1="${h}" x2="${w}" y2="${h}" stroke="currentColor" stroke-opacity=".16"/>` +
      `<polyline points="${pts}" fill="none" stroke="var(--accent)" stroke-opacity=".18" stroke-width="4.5" stroke-linecap="round" stroke-linejoin="round"/>` +
      `<polyline points="${pts}" fill="none" stroke="var(--accent)" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"/></svg>`;
  }

  function ring(frac, dim) {
    const r = 64, c = 75, len = 2 * Math.PI * r, f = Math.max(0, Math.min(1, frac));
    const a = -Math.PI / 2 + f * 2 * Math.PI, ex = c + r * Math.cos(a), ey = c + r * Math.sin(a);
    return `<svg width="150" height="150"><circle cx="${c}" cy="${c}" r="${r}" fill="none" stroke="currentColor" stroke-opacity=".16" stroke-width="1.2"/>` +
      (f > .001 ? `<circle cx="${c}" cy="${c}" r="${r}" fill="none" stroke="var(--accent)" stroke-opacity="${dim ? .55 : 1}" stroke-width="2.6" stroke-linecap="round"
        stroke-dasharray="${(len * f).toFixed(1)} ${len.toFixed(1)}" transform="rotate(-90 ${c} ${c})"/>
        <rect x="${ex - 3.2}" y="${ey - 3.2}" width="6.4" height="6.4" transform="rotate(45 ${ex} ${ey})" fill="var(--accent)" fill-opacity="${dim ? .55 : 1}"/>` : '') + `</svg>`;
  }

  const builders = {
    calendar(d) {
      if (!d) return header('Agenda');
      let rows = '';
      for (const r of d.rows || []) {
        rows += r.day ? `<tr><td colspan="2" class="day">${esc(r.day)}</td></tr>`
          : `<tr><td class="tm ${r.now ? 'now' : ''}">${esc(r.time)}</td><td class="ev">${esc(r.title)}</td></tr>`;
      }
      return header('Agenda') + (d.note ? `<div class="note">${esc(d.note)}</div>` : `<table>${rows}</table>`);
    },
    system(d) {
      const rows = (d && d.rows || []).map(r => `<tr><td class="lab">${esc(r.label)}</td><td>${spark(r.hist)}</td><td class="val">${esc(r.value)}</td></tr>`).join('');
      return header('System') + `<table>${rows}</table>`;
    },
    timer(d) {
      d = d || { clock: '25:00', phase: 'FOCUS', frac: 1, dim: true, hint: 'CLICK TO BEGIN' };
      return `<div class="ring" data-act="timer:toggle">${ring(d.frac, d.dim)}<div class="clock">${esc(d.clock)}</div><div class="phase">${esc(d.phase)}</div></div>` +
        `<div class="hint">${esc(d.hint)}</div>` + (d.reset ? `<div class="reset" data-act="timer:reset">◇&nbsp;&nbsp;RESET</div>` : '');
    },
    album(d) {
      if (!d || !d.title) return `<div class="none">◆&nbsp;&nbsp;NOTHING PLAYING</div>`;
      return `<div class="wrap">${d.art ? `<img src="${d.art}">` : ''}<div><div class="t">${esc(d.title)}</div>${d.artist ? `<div class="a">${esc(d.artist)}</div>` : ''}</div></div>`;
    },
    world(d) {
      if (!d || !(d.rows || []).length) return header('World time') + `<div class="note">Add cities in Settings (Name=Time zone)</div>`;
      return header('World time') + '<table>' + d.rows.map(r =>
        `<tr><td class="lab" style="width:110px">${esc(r.name)}</td><td><span class="big">${esc(r.time)}</span>` +
        (r.ampm ? `<span class="small">${esc(r.ampm)}</span>` : '') + (r.offset ? `<span class="off1">${esc(r.offset)}</span>` : '') + `</td></tr>`).join('') + '</table>';
    },
  };

  function tasksHtml(b) {
    const id = b.id, mode = +(P.todoDone ?? 0), edit = P.todoEdit !== false;
    const act = (v, item) => `data-act="task:${v}:${id}:${item || ''}"`;
    const ctl = (item, header) => edit ? `<span class="ctl">` + (header ? `<span ${act('add', item)}>+</span>` : '') +
      `<span ${act('up', item)}>↑</span><span ${act('down', item)}>↓</span><span ${act('delete', item)}>✕</span></span>` : '';
    const items = b.items || [];
    const sections = [{ h: null, t: [] }];
    for (const it of items) { if (it.header) sections.push({ h: it, t: [] }); else sections[sections.length - 1].t.push(it); }
    let html = header(b.name || 'Tasks');
    for (const { h, t } of sections) {
      let tasks = t.filter(x => !(mode === 2 && x.status === 2 && x.finished && Date.now() - Date.parse(x.finished) > 864e5));
      if (mode === 1) tasks = tasks.filter(x => x.status !== 2).concat(tasks.filter(x => x.status === 2));
      if (h) {
        const done = t.filter(x => x.status === 2).length;
        html += `<div class="rw-row head" data-row="${h.id}" ${act('toggle', h.id)}><span class="fold">${h.collapsed ? '▸' : '▾'}</span>` +
          `<span class="ht">${esc(h.title)}</span>` + (t.length ? `<span class="cnt ${done === t.length ? 'full' : ''}">${done} / ${t.length}</span>` : '') + ctl(h.id, true) + `</div>`;
        if (h.collapsed) continue;
      }
      for (const x of tasks) {
        const marks = (x.status === 1 ? `<span class="tag">WIP</span>` : '') + (x.notes ? `<span class="mk">≡</span>` : '') +
          (x.subTotal ? `<span class="mk ${x.subDone === x.subTotal ? 'full' : ''}">${x.subDone}/${x.subTotal}</span>` : '');
        html += `<div class="rw-row task s${x.status}" data-row="${x.id}"><span class="dia s${x.status}" ${act('cycle', x.id)}></span>` +
          `<span class="tt" ${act('open', x.id)}>${esc(x.title)}</span>${marks}${ctl(x.id, false)}</div>`;
      }
    }
    if (!items.length) html += `<div class="none">No tasks yet</div>`;
    if (edit) html += `<div class="foot"><span ${act('add', '')}>+&nbsp;&nbsp;ADD TASK</span>` +
      (items.some(x => !x.header && x.status === 2) ? `<span class="sep">·</span><span ${act('clear', '')}>CLEAR DONE</span>` : '') + `</div>`;
    return html;
  }

  let hoverRow = null;
  function renderTasks() {
    const e = el('tasks');
    const on = !!BOARD && P.showTodo !== false;
    e.classList.toggle('off', !on);
    if (!on) return;
    styleBackdrop(e);
    const html = tasksHtml(BOARD);
    if (last.tasks !== html) {
      e.innerHTML = html; last.tasks = html;
      if (hoverRow) { const r = e.querySelector(`[data-row="${hoverRow}"]`); if (r) r.classList.add('hov'); }
    }
    if (typeof place === 'function') place(e, +(P.todoX ?? 0), +(P.todoY ?? 55), (+(P.todoScale ?? 100)) / 100);
  }

  const last = {};
  function render() {
    renderTasks();
    for (const [id, show, kx, ky, ks] of WIDGETS) {
      const e = el(id);
      const on = !!P[show];
      e.classList.toggle('off', !on);
      if (!on) continue;
      const html = builders[id](D[id]);
      if (last[id] !== html) { e.innerHTML = html; last[id] = html; }
      if (typeof place === 'function') place(e, +(P[kx] ?? 50), +(P[ky] ?? 50), (+(P[ks] ?? 100)) / 100);
    }
  }

  function start() {
    const st = document.createElement('style'); st.textContent = CSS; document.head.appendChild(st);
    // clicks (the host replays desktop clicks into the page): the timer's ring and its reset link
    document.addEventListener('click', e => {
      const a = e.target.closest && e.target.closest('[data-act]');
      if (a && a.closest('.rw')) post({ t: 'req', v: a.dataset.act });
    });
    // hover controls on the task board: the host replays the mouse position into the page (no real :hover under the icons)
    window.addEventListener('mousemove', e => {
      const t = document.elementFromPoint(e.clientX, e.clientY);
      const r = t && t.closest && t.closest('#rw-tasks .rw-row');
      const id = r ? r.dataset.row : null;
      if (id === hoverRow) return;
      hoverRow = id;
      document.querySelectorAll('#rw-tasks .rw-row.hov').forEach(x => x.classList.remove('hov'));
      if (r) r.classList.add('hov');
    });
    ready = true;
    render();
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
})();
