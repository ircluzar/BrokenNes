// Butchering Mode WebModule - the Glitch Boy's bus-corruption mechanisms, one control per effect
// (docs/projects/butchering-mode-plan.md; engine Windows/NesEmulator/corruption/Butcher.cs).
// Entering takes a base state (like Corruption Slop); Reload State returns to it; leaving turns every effect off and reloads it.
(() => {
  const api = window.webapi;
  const STATE_NAME = 'Butchering Mode';
  const FAULTS = [
    { code: 0, short: '·', name: 'connected' },
    { code: 1, short: 'L', name: 'stuck low' },
    { code: 2, short: 'H', name: 'stuck high' },
    { code: 3, short: 'B', name: 'bridged to the next line' },
    { code: 4, short: 'F', name: 'floating (keeps the last value on it)' }
  ];

  let baseStateId = null;
  let reach = null;
  let consoleAddressLines = 14;
  let settings = null;
  let syncTimer = null;
  let leaving = false;

  const $ = (id) => document.getElementById(id);
  const isNesReach = () => (reach?.console || 'nes') === 'nes';

  function defaults(cartLines) {
    const ev = () => ({ on: false, rate: 25, duration: 30 });
    return {
      enabled: true,
      consolePins: { address: new Array(consoleAddressLines).fill(0), data: new Array(reach?.consoleDataLines || 8).fill(0), flicker: 0, bendWrites: false },
      cartPins: { address: new Array(cartLines).fill(0), data: new Array(8).fill(0), flicker: 0, bendWrites: false },
      sprites: {
        xJitter: { on: false, rate: 25, range: 8 },
        yJitter: { on: false, rate: 25, range: 8 },
        tile: { on: false, rate: 25 },
        attr: { bits: [false, false, false, false, false], rate: 25 },
        groupLag: ev(), clone: ev(), fieldSwap: ev(),
        forceBit: { on: false, rate: 25, duration: 30, field: 2, bit: 7, high: true }
      },
      sound: {
        bitFlip: { bits: [false, false, false, false, false], rate: 25 },
        nudge: { on: false, rate: 25 },
        drops: { channels: new Array(16).fill(false), rate: 25, duration: 30 },
        stutter: ev(), drone: ev(),
        duty: { on: false, rate: 25, duration: 30, value: 0 },
        detune: { on: false, rate: 25, duration: 30, value: 1 },
        busPins: { address: new Array(reach?.soundBusLines ?? 5).fill(0), data: new Array(8).fill(0), flicker: 0, bendWrites: false },
        samplePins: { address: new Array(reach?.sampleLines ?? 15).fill(0), data: new Array(8).fill(0), flicker: 0, bendWrites: false },
        dacPins: { address: [], data: new Array(16).fill(0), flicker: 0, bendWrites: false },
        wobble: { on: false, depth: 30, speed: 8 },
        caps: { on: false, age: 40 },
        fmUnplug: { on: false, rate: 10, duration: 60 },
        inject: { on: false, wave: 0, freq: 440, level: 20, mix: 0 },
        hum: { on: false, hz: 60, level: 15 },
        sweep: { on: false, from: 100, to: 8000, seconds: 4, level: 15 }
      },
      bands: (() => { const b = () => ({ on: false, rate: 25, width: 30, strength: 8, lifetime: 30 }); return { shift: b(), slip: b(), patternSwap: b(), nametableSwap: b(), bgOff: b(), spritesOff: b(), spriteHeight: b(), bend: b(), extra1: b(), extra2: b(), extra3: b() }; })(),
      ram: { on: false, rate: 25, step: 1, lifetime: 60, maxProbes: 3 },
      palette: { on: false, mode: 0, invert: false, channelSwap: false, posterize: 0, cycleFrames: 0 },
      ciramA10: 0, ciramFlicker: 0,
      lcd: { on: false, columns: 2, rows: 0, mode: 0, seed: 1 },
      vramChip: { on: false, chip: 0, mode: 0, flicker: 0 },
      video: { snow: { on: false, level: 20 }, roll: { on: false, speed: 2 }, ghost: { on: false, level: 30, offset: 6 }, misconverge: { on: false, pixels: 2 } },
      videoDac: { address: [], data: new Array(24).fill(0), flicker: 0, bendWrites: false },
      paletteBus: (() => { const z = { nes: [5, 6], snes: [8, 15], md: [6, 12], sms: [5, 0], gg: [5, 0], gb: [6, 8] }[reach?.console || 'nes'] || [5, 6]; return { address: new Array(z[0]).fill(0), data: new Array(z[1]).fill(0), flicker: 0, bendWrites: false }; })(),
      unsafe: { cpuPins: { address: new Array(reach?.cpuAddressLines || 15).fill(0), data: new Array(reach?.cpuDataLines || 8).fill(0), flicker: 0, bendWrites: false },
                soundCpuPins: { address: new Array(reach?.soundCpuLines || 0).fill(0), data: new Array(8).fill(0), flicker: 0, bendWrites: false },
                ramRot: { on: false, rate: 25, bits: 1 }, vramRot: { on: false, rate: 25, bits: 1 },
                cicReset: { on: false, periodFrames: 60 }, resetBounce: { on: false, rate: 3 }, powerSag: { on: false, rate: 3, decay: 30 }, looseCart: { on: false, rate: 10, duration: 20 },
                dramRot: { on: false, rate: 10, rows: 1 }, heatSoak: { on: false, minutes: 5 }, esd: { on: false, rate: 2, bits: 16 }, linkNoise: { on: false, rate: 5 }, padTh: { mode: 0, flicker: 0 },
                padLines: new Array(15).fill(0), padFlicker: 0,
                soundRamRot: { on: false, rate: 25, bits: 1 }, echoAbuse: { on: false, rate: 25, duration: 120 },
                ghostNmi: { on: false, rate: 25, duration: 1 }, clockStall: { on: false, rate: 25, duration: 10 }, clockRush: { on: false, rate: 25, duration: 30 }, z80BusHold: { on: false, rate: 25, duration: 60 }, z80ResetHold: { on: false, rate: 25, duration: 60 }, dropNmi: { on: false, rate: 25, duration: 10 }, stuckIrq: { on: false, rate: 25, duration: 10 },
                ramBus: { address: new Array(reach?.ramBusLines || 11).fill(0), data: new Array(8).fill(0), flicker: 0, bendWrites: false },
                videoBus: { address: new Array(reach?.videoBusLines || 3).fill(0), data: new Array(reach?.cpuDataLines || 8).fill(0), flicker: 0, bendWrites: false },
                dmaPins: { address: new Array(reach?.dmaLines || 0).fill(0), data: new Array(reach?.dmaDataLines || 8).fill(0), flicker: 0, bendWrites: false },
                coproPins: { address: new Array(reach?.coproName && reach.coproName !== 'DSP-1' ? 24 : 0).fill(0), data: new Array(reach?.coproName ? 8 : 0).fill(0), flicker: 0, bendWrites: false } },
      groupsOff: { picture: false, sprites: false, sound: false, bands: false, ram: false, palette: false, unsafe: false }
    };
  }

  // A preset saved on a game with a different CHR chip size: keep what fits, pad the rest.
  function fit(s) {
    const d = defaults(reach.cartAddressLines);
    const merge = (dst, src) => {
      if (!src || typeof src !== 'object') return dst;
      for (const k of Object.keys(dst)) {
        if (!(k in src)) continue;
        if (Array.isArray(dst[k])) dst[k] = dst[k].map((v, i) => (src[k] && i < src[k].length ? src[k][i] : v));
        else if (dst[k] && typeof dst[k] === 'object') merge(dst[k], src[k]);
        else dst[k] = src[k];
      }
      return dst;
    };
    const out = merge(d, s);
    out.enabled = true;
    if (s && s.seed !== undefined) out.seed = s.seed;
    return out;
  }

  function status(text, isError) {
    const n = $('status');
    n.textContent = text;
    n.classList.toggle('err', !!isError);
  }

  document.addEventListener('DOMContentLoaded', () => {
    if (!api) { console.error('[ButcheringMode] webapi helper not loaded'); return; }
    $('toggle').addEventListener('click', () => $('panel').classList.toggle('hidden'));
    $('exit').addEventListener('click', exitMode);
    $('reload').addEventListener('click', reloadState);
    $('viewStrip').addEventListener('click', () => setView('strip'));
    $('viewBuild').addEventListener('click', () => setView('build'));
    $('allOff').addEventListener('click', () => { settings = defaults(reach.cartAddressLines); render(); push(); });
    $('presetList').addEventListener('change', () => { if ($('presetList').value) loadPreset(); });
    $('presetSave').addEventListener('click', () => {
      const row = $('presetNameRow'), sel = $('presetList');
      row.hidden = !row.hidden;
      if (!row.hidden) { if (sel.value.startsWith('u:')) $('presetName').value = sel.value.slice(2); $('presetName').focus(); $('presetName').select(); }
    });
    $('presetSaveOk').addEventListener('click', async () => { await savePreset(); if ($('presetName').value.trim()) $('presetNameRow').hidden = true; });
    $('presetSaveCancel').addEventListener('click', () => { $('presetNameRow').hidden = true; });
    $('presetName').addEventListener('keydown', (e) => { if (e.key === 'Enter') $('presetSaveOk').click(); if (e.key === 'Escape') $('presetNameRow').hidden = true; });
    popMenu($('presetMore'), $('presetMenu'));
    $('presetDelete').addEventListener('click', () => { $('presetMenu').hidden = true; deletePreset(); });
    start();
  });

  async function start() {
    // The reach call decides first: it knows every console. The time-jump ROM check only knows the NES (while a SNES / Game Boy / Sega
    // game runs, the NES holds its placeholder ROM, so the check said "no game" and the panel closed itself on every other console).
    const r = await api.butcher.getReach().catch(() => null);
    if ((r?.console || r?.reach?.console || 'nes') === 'nes') {
      try {
        const valid = await api.timejump.validateRom();
        if (!valid?.valid) { status('Load a game first.', true); await back(); return; }
      } catch { /* validation is advisory */ }
    }
    if (!r?.success) { status(r?.error || 'Butchering mode is not available.', true); return; }
    if (!r.supported) { status(r.reason || 'Not supported on this console yet.', true); return; }
    reach = r.reach;
    consoleAddressLines = r.consoleAddressLines || 14;

    // The menu bar stays with only PPU, APU and SHADER (config.json visibleMenus), so cores and shaders can be changed while butchering.

    const base = await api.gh.addBaseState(STATE_NAME).catch(() => null);
    const b = base?.baseState || base;
    baseStateId = b?.id || b?.Id || null;
    if (!baseStateId) { status('Could not take the entry state - nothing was changed.', true); return; }
    $('reload').disabled = false;

    settings = defaults(reach.cartAddressLines);
    await loadCatalog();
    render();
    await push();
    await refreshPresets();
    status(isNesReach() ? `Entry state saved. PPU core: ${reach.ppuCore || '?'} · mapper ${reach.mapper}` : `Entry state saved. ${reach.ppuCore}`);
  }

  // The PPU, APU and SHADER menus stay usable in this mode: when a core is swapped, what the mode can reach changes with it.
  setInterval(async () => {
    if (leaving || !reach) return;
    const st = await api.butcher.getStats().catch(() => null);
    if (st?.success) {
      const n = document.getElementById('ramStats');
      if (n && settings?.ram?.on) n.textContent = st.confirmed > 0
        ? `Ready: ${st.confirmed} confirmed variable${st.confirmed === 1 ? '' : 's'}, ${st.nudging} being nudged (${st.watches} watched). What it learned survives Reload State.`
        : `Learning… ${st.watches} byte${st.watches === 1 ? '' : 's'} watched, nothing confirmed yet (title screens give it little: play a bit).`;
      watchdog(st.crashed ? 999 : (st.stillFrames || 0), st.crashed);
    }
    const r = await api.butcher.getReach().catch(() => null);
    if (!r?.success || !r.supported) return;
    const before = JSON.stringify(reach), now = JSON.stringify(r.reach);
    if (before === now) return;
    reach = r.reach;
    settings = fit(settings);
    render();
    await push();
    status(`Cores changed. PPU core: ${reach.ppuCore || '?'} · mapper ${reach.mapper}`);
  }, 1500);

  // Freeze watchdog: the game's RAM has not changed for about 3 seconds - offer the entry state.
  let freezeDismissed = false;
  function watchdog(still, crashed) {
    let bar = document.getElementById('freezeBar');
    if (still < 180) { if (bar) bar.remove(); freezeDismissed = false; return; }
    if (bar || freezeDismissed) return;
    bar = el('div', { id: 'freezeBar', class: 'freeze' },
      el('span', {}, crashed ? 'The game crashed.' : 'The game looks frozen.'),
      el('button', { type: 'button', onclick: async () => { document.getElementById('freezeBar')?.remove(); await reloadState(); } }, 'Reload State'),
      el('button', { type: 'button', onclick: () => { freezeDismissed = true; document.getElementById('freezeBar')?.remove(); } }, 'Keep going'));
    document.querySelector('.head')?.after(bar);
  }

  async function reloadState() {
    if (!baseStateId) return;
    const r = await api.gh.loadBase(baseStateId).catch(() => null);
    status(r?.success ? 'Entry state reloaded (effects still on).' : 'Reload failed.', !r?.success);
  }

  async function exitMode() {
    if (leaving) return;
    leaving = true;
    clearTimeout(syncTimer);
    try { await api.butcher.off(); } catch (e) { console.warn('[ButcheringMode] off failed', e); }
    await new Promise(res => setTimeout(res, 50));
    if (baseStateId) {
      try { await api.gh.loadBase(baseStateId); } catch (e) { console.warn('[ButcheringMode] reload on exit failed', e); }
    }
    try { await api.ui.showMenu(); } catch { /* ignore */ }
    await back();
  }

  async function back() {
    try { await api.navigation.goToEmulator(); } catch (e) { console.error('[ButcheringMode] back to emulator failed', e); }
  }

  // What the engine gets: a group switched off in the panel goes as its all-off defaults (its own values stay in the panel).
  function effective() {
    const d = defaults(reach.cartAddressLines);
    if (solo?.path) {
      const only = JSON.parse(JSON.stringify(d));
      setPath(only, solo.path, JSON.parse(JSON.stringify(getPath(settings, solo.path))));
      delete only.groupsOff;
      return only;
    }
    const e = JSON.parse(JSON.stringify(settings));
    const off = settings.groupsOff || {};
    if (off.picture) { e.consolePins = d.consolePins; e.cartPins = d.cartPins; }
    if (off.sprites) e.sprites = d.sprites;
    if (off.sound) e.sound = d.sound;
    if (off.bands) e.bands = d.bands;
    if (off.ram) e.ram = d.ram;
    if (off.palette) { e.video = d.video; e.palette = d.palette; e.videoDac = d.videoDac; e.paletteBus = d.paletteBus; }
    if (off.picture) { e.ciramA10 = 0; e.lcd = d.lcd; e.vramChip = d.vramChip; }
    if (off.unsafe) { e.ram = d.ram; e.unsafe = d.unsafe; e.consolePins.bendWrites = false; e.cartPins.bendWrites = false; }
    delete e.groupsOff;
    return e;
  }

  // A section header with its group switch (clicking the switch does not fold the section).
  function groupSummary(title, key, tab) {
    settings.groupsOff = settings.groupsOff || {};
    const box = el('input', { type: 'checkbox', checked: !settings.groupsOff[key], title: 'Group on / off (keeps its settings)',
      onclick: (e) => e.stopPropagation(),
      onchange: (e) => { settings.groupsOff[key] = !e.target.checked; push(); } });
    const sm = el('summary', {}, box, ' ', title);
    queueMicrotask(() => { if (sm.parentElement) sm.parentElement.dataset.tab = tab || key; });
    return sm;
  }

  function push() {
    if (view === 'strip') refreshOnNow();
    clearTimeout(syncTimer);
    return new Promise(res => {
      syncTimer = setTimeout(async () => {
        const r = await api.butcher.setSettings(effective()).catch(() => null);
        if (!r?.success) status(r?.error || 'Could not apply the controls.', true);
        res();
      }, 40);
    });
  }

  // ------------------------------------------------------------------ presets
  async function refreshPresets() {
    const r = await api.butcher.getPresets().catch(() => null);
    const sel = $('presetList');
    sel.innerHTML = '<option value="">Presets…</option>';
    // built-ins (read-only, shipped with the module) first, then the user's own
    for (const p of (r?.presets || [])) {
      const o = document.createElement('option');
      o.value = (p.builtIn ? 'b:' : 'u:') + p.name; o.textContent = p.builtIn ? `★ ${p.name}` : p.name;
      if (p.builtIn) o.title = 'Built-in preset (read-only): Save with its name to make your own copy';
      sel.appendChild(o);
    }
    sel._presets = r?.presets || [];
  }
  async function savePreset() {
    const name = $('presetName').value.trim();
    if (!name) { status('Give the preset a name first.', true); return; }
    const r = await api.butcher.savePreset(name, settings).catch(() => null);
    status(r?.success ? `Preset "${name}" saved.` : (r?.error || 'Save failed.'), !r?.success);
    await refreshPresets();
    $('presetList').value = 'u:' + name;
  }
  async function loadPreset() {
    const sel = $('presetList');
    const p = (sel._presets || []).find(x => (x.builtIn ? 'b:' : 'u:') + x.name === sel.value);
    if (!p) return;
    settings = fit(p.settings);
    render();
    await push();
    status(`Preset "${p.name}" loaded.`);
  }
  async function deletePreset() {
    const v = $('presetList').value;
    if (!v) return;
    if (v.startsWith('b:')) { status('Built-in presets cannot be deleted (they are always there to come back to).', true); return; }
    const name = v.slice(2);
    await api.butcher.deletePreset(name).catch(() => null);
    await refreshPresets();
    status(`Preset "${name}" deleted.`);
  }

  // ------------------------------------------------------------------ panel
  function el(tag, attrs, ...kids) {
    const n = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs || {})) {
      if (k === 'class') n.className = v;
      else if (k.startsWith('on')) n.addEventListener(k.slice(2), v);
      else if (v !== undefined && v !== null && v !== false) n.setAttribute(k, v === true ? '' : v);
    }
    for (const k of kids) if (k != null) n.append(k);
    return n;
  }

  // A value as a controller-standard knob (the Build board's): a placeholder carrying the parameter; knobRows() puts consecutive
  // ones into one panel once the strip is drawn. Wide ranges (frames, hertz) turn on a log scale.
  let knobSeq = 0;
  function slider(label, obj, key, min, max, step, onChange) {
    const s = el('div', { class: 'kslot' });
    const log = min > 0 && max / min >= 50;
    s._param = BNControls.param({ id: 'k' + (++knobSeq), label, short: label.replace(/\s*\(.*\)\s*/, '').replace(/ px$/, ''), min, max, step,
      unit: / px$/.test(label) ? 'px' : /\(Hz\)/.test(label) ? 'Hz' : label === 'Rate' || label === 'Flicker' || label === 'Level' ? '%' : label === 'Frames' ? 'f' : '', default: obj[key], taper: log ? 'log' : 'linear',
      get: () => obj[key],
      set: (v) => { const q = Math.min(max, Math.max(min, Math.round(v / step) * step)); obj[key] = +q.toFixed(4); (onChange || push)(); } });
    return s;
  }
  function knobRows(root) {
    const seen = new Set();
    for (const s of root.querySelectorAll('.kslot')) {
      if (seen.has(s)) continue;
      const run = [s]; seen.add(s);
      for (let n = s.nextElementSibling; n && n.classList.contains('kslot'); n = n.nextElementSibling) { run.push(n); seen.add(n); }
      const host = el('div', { class: 'knobrow' });
      s.before(host);
      const params = run.map(r => r._param), per = 4;
      const layout = { title: '', cols: Math.min(per, run.length), rows: Math.ceil(run.length / per), accent: '#ff6b6b',
        controls: params.map((p, i) => ({ type: 'knob', param: p.id, x: i % per, y: (i / per) | 0 })) };
      try { host._panel = BNControls.createPanel(host, layout, params); host._params = params; } catch (e) { console.warn('[ButcheringMode] knobs', e); }
      run.forEach(r => r.remove());
    }
  }

  function check(label, obj, key) {
    const l = el('label', { class: 'name' },
      el('input', { type: 'checkbox', class: key === 'on' ? 'sw' : '', 'aria-label': label, checked: !!obj[key], onchange: (e) => { obj[key] = e.target.checked; push(); } }), label);
    return key === 'on' ? el('div', { class: 'namerow' }, l, soloBtn(obj, label)) : l;
  }

  // ---------------------------------------------------------------- solo
  // Only the soloed effect is sent; everything else stays in the panel and comes back on un-solo.
  let solo = null;   // { path, label }
  function pathOf(target, node = settings, path = '') {
    if (node === target) return path;
    if (!node || typeof node !== 'object' || Array.isArray(node)) return null;
    for (const k of Object.keys(node)) { const r = pathOf(target, node[k], path ? path + '.' + k : k); if (r !== null) return r; }
    return null;
  }
  function soloBtn(obj, label) {
    const b = soloBtnRaw(obj, label); b._label = label; return b;
  }
  function soloBtnRaw(obj, label) {
    return el('button', { type: 'button', class: 'solo', title: 'Solo: only this effect runs (click again to un-solo)',
      onclick: (e) => {
        e.preventDefault(); e.stopPropagation();
        const path = pathOf(obj);
        solo = solo && solo.path === path ? null : { path, label };
        paintSolo(); push();
        status(solo ? `Solo: ${label}` : 'Solo off: every effect back.');
      } }, 'Solo');
  }
  function paintSolo() {
    document.querySelectorAll('button.solo').forEach(b => b.classList.remove('on'));
    if (!solo) return;
    document.querySelectorAll('button.solo').forEach(b => { if (b._label === solo.label) b.classList.add('on'); });
  }
  function getPath(o, path) { return path.split('.').reduce((x, k) => x?.[k], o); }
  function setPath(o, path, v) { const ks = path.split('.'); const last = ks.pop(); const t = ks.reduce((x, k) => x[k], o); t[last] = v; }

  function bits(names, arr) {
    return el('div', { class: 'bits tg' }, ...names.map((n, i) => el('label', {},
      el('input', { type: 'checkbox', checked: !!arr[i], onchange: (e) => { arr[i] = e.target.checked; push(); } }), n)));
  }

  function pinGrid(prefix, arr) {
    const grid = el('div', { class: 'pins' });
    arr.forEach((code, i) => {
      if (i % 8 === 0) grid.appendChild(el('span', { class: 'pinlab' }, i === 0 ? prefix : ''));
      const b = el('button', { type: 'button', class: `pin f${code}` });
      const paint = () => {
        const f = FAULTS[arr[i]];
        b.className = `pin f${f.code}`;
        b.textContent = String(i);
        b.title = `${prefix}${i}: ${f.name} (click: next fault, right-click: reconnect)`;
      };
      b.addEventListener('click', () => { arr[i] = (arr[i] + 1) % FAULTS.length; paint(); push(); });
      b.addEventListener('contextmenu', (e) => { e.preventDefault(); arr[i] = 0; paint(); push(); });
      paint();
      grid.appendChild(b);
    });
    return grid;
  }

  function pinGroup(title, why, group, available, unavailableText, writesText) {
    const redraw = () => { render(); push(); };
    const body = el('fieldset', { class: available ? 'pinbody' : 'pinbody disabled' },
      pinGrid('A', group.address), pinGrid('D', group.data),
      el('div', { class: 'row pinacts' }, slider('Flicker', group, 'flicker', 0, 100, 1), el('span', { class: 'grow' }),
        el('button', { type: 'button', title: 'One to three random lines faulted', onclick: () => { const all = [...group.address.keys()].map(i => ['address', i]).concat([...group.data.keys()].map(i => ['data', i])); const n = 1 + Math.floor(Math.random() * 3); for (let k = 0; k < n; k++) { const [w, i] = all[Math.floor(Math.random() * all.length)]; group[w][i] = 1 + Math.floor(Math.random() * (FAULTS.length - 1)); } redraw(); } }, 'Random'),
        el('button', { type: 'button', title: 'Every line connected', onclick: () => { group.address.fill(0); group.data.fill(0); redraw(); } }, 'Clear'),
        el('label', { class: 'wtoggle', title: writesText || 'Writes land bent too' }, el('input', { type: 'checkbox', checked: !!group.bendWrites, onchange: (e) => { group.bendWrites = e.target.checked; push(); } }), 'Writes')));
    return el('details', { open: true }, groupSummary(title, 'picture'),
      el('div', { class: 'why' }, why),
      available ? null : el('div', { class: 'off' }, unavailableText),
      pinLegend(),
      body);
  }

  function pinLegend() {
    return el('div', { class: 'legend' }, ...FAULTS.map(f => el('span', {}, el('i', { class: `sq f${f.code}` }), f.code === 0 ? 'ok' : f.name.replace(/^stuck /, '').replace(/ to the next line$/, '').replace(/ \(.*$/, ''))),
      el('span', { class: 'grow' }), el('span', {}, 'click cycles, right-click clears'));
  }

  function rateFx(label, why, fx, extra) {
    return el('div', { class: 'fx' }, check(label, fx, 'on'), why ? el('div', { class: 'why' }, why) : null,
      slider('Rate', fx, 'rate', 0, 100, 1), ...(extra || []));
  }
  function eventFx(label, why, fx, extra) {
    return el('div', { class: 'fx' }, check(label, fx, 'on'), why ? el('div', { class: 'why' }, why) : null,
      slider('Rate', fx, 'rate', 0, 100, 1), slider('Frames', fx, 'duration', 1, 600, 1), ...(extra || []));
  }

  // The band types this console's chip does (reach.bandKinds), then its own extras (reach.extraBands: "Name|what it does").
  function bandList(bd, bandFx) {
    const shared = {
      shift: () => bandFx('Shift', 'The band slides sideways.', bd.shift, 'Pixels'),
      slip: () => bandFx('Slip', 'The picture below the band drops by some rows.', bd.slip, 'Rows'),
      patternSwap: () => bandFx('Pattern swap', 'The band draws from the other tile set.', bd.patternSwap),
      nametableSwap: () => bandFx('Map swap', 'The band shows another tile map.', bd.nametableSwap),
      bgOff: () => bandFx('Background off', 'The background vanishes inside the band.', bd.bgOff),
      spritesOff: () => bandFx('Sprites off', 'Sprites vanish inside the band.', bd.spritesOff),
      spriteHeight: () => bandFx('Sprite height', 'Sprites switch size for the whole frame.', bd.spriteHeight),
      bend: () => bandFx('Bend', 'Address lines grounded inside the band only.', bd.bend, 'Lines'),
    };
    const kinds = reach.bandKinds || Object.keys(shared);
    const out = kinds.filter(k => shared[k]).map(k => shared[k]());
    (reach.extraBands || []).forEach((e, i) => {
      const [name, why] = String(e).split('|');
      const fx = bd['extra' + (i + 1)];
      if (fx) out.push(bandFx(name, why || '', fx, 'Strength'));
    });
    return out;
  }

  // ---------------------------------------------------------------- tabs + search
  const TABS = [['picture', 'Picture'], ['sprites', 'Sprites'], ['sound', 'Sound'], ['bands', 'Bands'], ['palette', 'Palette'], ['unsafe', 'Unsafe']];
  let activeTab = (() => { try { const t = localStorage.getItem('butcher.tab') || 'picture'; return t === 'ram' ? 'unsafe' : t; } catch { return 'picture'; } })();   // the RAM tab is gone: RAM nudges are unsafe
  let query = '';
  function tabify() {
    const g = $('groups');
    let key = 'picture';
    for (const c of g.children) { const sm = c.querySelector(':scope > summary [data-group]') || c.querySelector(':scope > summary'); if (c.tagName === 'DETAILS') { key = c.dataset.tab || key; c.open = true; } c.dataset.tab = key; }
    let bar = $('tabs');
    if (!bar) {
      bar = el('div', { id: 'tabs', class: 'tabs' });
      const search = el('input', { id: 'search', type: 'search', placeholder: 'Search effects…', 'aria-label': 'Search effects', hidden: true,
        oninput: (e) => { query = e.target.value.trim().toLowerCase(); applyTabs(); } });
      $('groups').before(bar, search, el('div', { id: 'onnow', class: 'onnow', hidden: true }));
    }
    bar.replaceChildren(...TABS.map(([k, n]) => el('button', { type: 'button', 'data-tab': k, class: 'tab' + (k === activeTab ? ' on' : '') + (k === 'unsafe' ? ' red' : ''),
      onclick: () => { activeTab = k; try { localStorage.setItem('butcher.tab', k); } catch { } $('search').value = ''; query = ''; tabify(); } }, n, el('span', { class: 'n', hidden: true }))),
      el('button', { type: 'button', class: 'tab icon', title: 'Search effects', 'aria-label': 'Search effects', onclick: () => {
        const s = $('search'); s.hidden = !s.hidden; if (s.hidden) { s.value = ''; query = ''; applyTabs(); } else s.focus(); } }, '\u2315'));
    if (query) $('search').hidden = false;
    applyTabs();
    refreshOnNow();
  }
  function applyTabs() {
    const g = $('groups');
    for (const c of g.children) {
      if (!query) { c.style.display = c.dataset.tab === activeTab ? '' : 'none'; c.querySelectorAll('.fx').forEach(f => f.style.display = ''); continue; }
      let hit = false;
      c.querySelectorAll('.fx').forEach(f => { const m = f.textContent.toLowerCase().includes(query); f.style.display = m ? '' : 'none'; hit ||= m; });
      const own = (c.querySelector(':scope > summary')?.textContent || '').toLowerCase().includes(query);
      if (own) c.querySelectorAll('.fx').forEach(f => f.style.display = '');
      c.style.display = hit || own ? '' : 'none';
    }
    document.querySelectorAll('#tabs .tab').forEach(b => b.style.opacity = query ? '.5' : '');
  }

  function render() { if (view === 'build') renderBuild(); else renderStrip(); }

  // ---------------------------------------------------------------- the compact strip (approved 2026-11-07)
  // An effect is one line: its switch, its name, a short summary of its values while it is on, Solo, "i" (the explanation) and ▸.
  // Clicking the line opens its controls under it (kept open across redraws). A group's explanation sits behind its own "i".
  const openFx = new Set();
  function infoBtn(text, owner) {
    return el('span', { class: 'info', role: 'button', tabindex: 0, title: text, 'aria-label': 'What it does',
      onclick: (e) => { e.preventDefault(); e.stopPropagation(); owner.classList.toggle('tipopen'); },
      onkeydown: (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); owner.classList.toggle('tipopen'); } } }, 'i');
  }
  function compactStrip(g) {
    for (const d of g.querySelectorAll('details')) {
      const sm = d.querySelector(':scope > summary'); if (!sm || sm._compact) continue; sm._compact = true;
      const whys = [...d.querySelectorAll(':scope > .why')];
      const text = whys.map(w => w.textContent).join(' ').trim();
      if (text) { whys.forEach(w => w.remove()); sm.append(infoBtn(text, d)); sm.after(el('div', { class: 'tip gtip' }, text)); }
      sm.append(el('span', { class: 'gcount' }));
    }
    for (const fx of g.querySelectorAll('.fx')) {
      if (fx._compact) continue; fx._compact = true;
      const head = fx.querySelector(':scope > .namerow') || fx.querySelector(':scope > label.name');
      if (!head) continue;
      const lab = head.matches('label') ? head : head.querySelector('label.name');
      const cb = lab?.querySelector('input[type=checkbox]') || null;
      const name = (lab?.textContent || '').trim();
      const solo = head.querySelector('button.solo');
      const whys = [...fx.querySelectorAll(':scope > .why')];
      const text = whys.map(w => w.textContent).join(' ').trim();
      whys.forEach(w => w.remove());
      const body = el('div', { class: 'fxbody' });
      [...fx.children].filter(c => c !== head).forEach(c => body.append(c));
      const val = el('span', { class: 'val' }), chev = el('span', { class: 'chev', 'aria-hidden': 'true' });
      if (cb) cb.classList.add('sw');
      const setOpen = (o) => { fx.classList.toggle('open', o); if (o) openFx.add(name); else openFx.delete(name); chev.textContent = o ? '\u25BE' : '\u25B8'; row.setAttribute('aria-expanded', String(o)); };
      const row = el('div', { class: 'fxrow', role: 'button', tabindex: 0,
        onclick: (e) => { if (e.target.closest('input, button, .info, label')) return; setOpen(!fx.classList.contains('open')); },
        onkeydown: (e) => { if ((e.key === 'Enter' || e.key === ' ') && e.target === row) { e.preventDefault(); setOpen(!fx.classList.contains('open')); } } },
        cb || el('span', { class: 'swgap' }), el('span', { class: 'name' }, name), val, solo, text ? infoBtn(text, fx) : null, chev);
      head.remove();
      fx.append(row);
      if (text) fx.append(el('div', { class: 'tip' }, text));
      fx.append(body);
      fx._name = name; fx._cb = cb; fx._val = val;
      setOpen(openFx.has(name));
    }
  }
  // on: its switch, or (no switch) any bit / channel ticked or line faulted
  function fxOn(fx) {
    if (fx._cb) return fx._cb.checked;
    return !!fx.querySelector('.fxbody input[type=checkbox]:checked, .fxbody .pin:not(.f0)');
  }
  function fxSummary(fx) {
    const ps = [...fx.querySelectorAll('.fxbody .knobrow')].flatMap(h => h._params || []).slice(0, 2);
    return ps.map(p => p.format(p.get())).join(' · ');
  }
  function fxOff(fx) {
    if (fx._cb) { fx._cb.checked = false; fx._cb.dispatchEvent(new Event('change')); return; }
    fx.querySelectorAll('.fxbody input[type=checkbox]:checked').forEach(c => { c.checked = false; c.dispatchEvent(new Event('change')); });
    fx.querySelectorAll('.fxbody .pin:not(.f0)').forEach(b => b.dispatchEvent(new MouseEvent('contextmenu', { cancelable: true })));
  }
  // the pin groups are groups of their own (a details with lines, no effects inside)
  const pinGroups = (g) => [...g.querySelectorAll(':scope > details')].filter(d => d.querySelector('.pins') && !d.querySelector('.fx'));
  let onNowQueued = false;
  function refreshOnNow() {
    if (onNowQueued) return; onNowQueued = true;
    requestAnimationFrame(() => {
      onNowQueued = false;
      const g = $('groups'), box = $('onnow');
      if (!g || !box || view !== 'strip') return;
      const counts = {}, on = [];
      for (const fx of g.querySelectorAll('.fx')) {
        if (!fx._compact || !fx._name) continue;
        const lit = fxOn(fx);
        fx.classList.toggle('lit', lit);
        if (fx._val) fx._val.textContent = lit ? fxSummary(fx) : '';
        if (lit) { const tab = fx.closest('[data-tab]')?.dataset.tab; counts[tab] = (counts[tab] || 0) + 1; on.push({ name: fx._name, tab, node: fx }); }
      }
      for (const d of pinGroups(g)) {
        const n = d.querySelectorAll('.pin:not(.f0)').length;
        if (n) { counts[d.dataset.tab] = (counts[d.dataset.tab] || 0) + 1; on.push({ name: [...(d.querySelector(':scope > summary')?.childNodes || [])].filter(x => x.nodeType === 3).map(x => x.textContent).join('').trim().replace(/^Picture bus · /, '').replace(/^./, c => c.toUpperCase()) + ` (${n})`, tab: d.dataset.tab, node: d, pins: true }); }
      }
      for (const d of g.querySelectorAll(':scope > details')) {
        const c = d.querySelector(':scope > summary .gcount');
        if (!c) continue;
        const n = [...d.querySelectorAll('.fx')].filter(f => f.classList.contains('lit')).length + (pinGroups(g).includes(d) && d.querySelector('.pin:not(.f0)') ? 1 : 0);
        c.textContent = n ? `${n} on` : '';
      }
      document.querySelectorAll('#tabs .tab[data-tab]').forEach(b => { const n = counts[b.dataset.tab] || 0, s = b.querySelector('.n'); s.textContent = String(n); s.hidden = !n; });
      box.hidden = !on.length;
      box.replaceChildren(el('span', { class: 'onlab' }, 'On now'), ...on.map(o => el('span', { class: 'chip' },
        el('button', { type: 'button', class: 'chipgo', title: 'Show it', onclick: () => {
          if (activeTab !== o.tab) { activeTab = o.tab; tabify(); }
          if (!o.pins && !o.node.classList.contains('open')) o.node.querySelector('.fxrow')?.click();
          o.node.scrollIntoView({ block: 'nearest' });
          o.node.classList.add('flashfx'); setTimeout(() => o.node.classList.remove('flashfx'), 700);
        } }, o.name),
        el('button', { type: 'button', class: 'chipx', title: `Switch ${o.name} off`, 'aria-label': `Switch ${o.name} off`, onclick: () => {
          if (o.pins) o.node.querySelectorAll('.pin:not(.f0)').forEach(b => b.dispatchEvent(new MouseEvent('contextmenu', { cancelable: true })));
          else fxOff(o.node);
          refreshOnNow();
        } }, '\u00D7'))));
    });
  }

  function renderStrip() {
    document.getElementById('panel')?.classList.remove('wide');
    trying = null; tryPanel = null; document.getElementById('trybench')?.remove();
    for (const id of ['tabs', 'search']) { const n = $(id); if (n) n.style.display = ''; }
    const g = $('groups');
    g.replaceChildren();
    const sp = settings.sprites, so = settings.sound;

    const isNes = (reach.console || 'nes') === 'nes';
    if (reach.consolePinsPartial) g.append(el('div', { class: 'off' }, isNes ? `The ${reach.ppuCore} PPU core fetches tile graphics through its own fast path: console pins reach its nametables and $2007, not every tile fetch (cartridge pins reach them).` : 'The picture is drawn on another chip (PPU menu), which reads video RAM itself: the pins reach only the fetches of the console’s own chip. Pick that chip in the PPU menu for the full effect.'));
    g.append(pinGroup(reach.pinsTitle || 'Picture bus · console pins',
      reach.pinsWhy || '',
      settings.consolePins, reach.consolePins,
      `Not reachable on the ${reach.ppuCore} PPU core (it has no PPU bus to bend).`,
      isNes ? 'PPU writes ($2007 and CHR-RAM) land bent too, so the damage stays until Reload State.'
            : 'Writes to video RAM land bent too, so the damage stays until Reload State.'));

    if (isNes) g.append(pinGroup(`Picture bus · cartridge pins (${reach.chrIsRam ? 'CHR-RAM' : 'CHR-ROM'}, ${reach.cartAddressLines} address lines)`,
      'The CHR chip\'s own pins, after the mapper\'s banking: only tile graphics.',
      settings.cartPins, reach.cartPins,
      `This cartridge (mapper ${reach.mapper}) does not expose its CHR chip yet.`,
      reach.chrIsRam ? 'Writes to CHR-RAM land bent too, so the damage stays until Reload State.' : 'CHR-ROM cannot be written: this has no effect here.'));

    if (isNes) g.append(el('details', { open: true }, groupSummary('Picture bus · CIRAM A10 (mirroring pin)', 'picture'),
      el('div', { class: 'why' }, 'The cartridge line that picks which nametable the console’s VRAM answers: shorted, screens repeat, swap or tear (PPU FIX).'),
      el('div', { class: 'fx' }, el('div', { class: 'bits' }, el('label', {}, 'A10 ', el('select', { onchange: (e) => { settings.ciramA10 = Number(e.target.value); push(); } },
        ...['connected', 'stuck low', 'stuck high', 'inverted'].map((n, i) => el('option', { value: i, selected: settings.ciramA10 === i }, n))))),
        slider('Flicker', settings, 'ciramFlicker', 0, 100, 1))));
    if (reach.console === 'snes' || reach.console === 'wm') {
      const vc = settings.vramChip;
      g.append(el('details', { open: true }, groupSummary('One VRAM chip failing', 'picture'),
        el('div', { class: 'why' }, 'The SNES builds its video RAM from two chips, one for the low byte of every word and one for the high byte: one of them dies.'),
        el('div', { class: 'fx' }, check('Chip failing', vc, 'on'),
          el('div', { class: 'bits' }, el('label', {}, 'Chip ', el('select', { onchange: (e) => { vc.chip = Number(e.target.value); push(); } },
            ...['low byte (D0-D7)', 'high byte (D8-D15)'].map((n, i) => el('option', { value: i, selected: vc.chip === i }, n)))),
            el('label', {}, ' Lines ', el('select', { onchange: (e) => { vc.mode = Number(e.target.value); push(); } },
            ...['stuck low', 'stuck high', 'floating'].map((n, i) => el('option', { value: i, selected: vc.mode === i }, n))))),
          slider('Flicker', vc, 'flicker', 0, 100, 1))));
    }
    if (reach.lcd) {
      const lc = settings.lcd;
      g.append(el('details', { open: true }, groupSummary('LCD drivers (column / row lines)', 'picture'),
        el('div', { class: 'why' }, 'The screen’s column and row driver chips failing: whole lines stay dark, light or flicker - the classic dead lines of an old handheld.'),
        el('div', { class: 'fx' }, check('Dead lines', lc, 'on'),
          slider('Columns', lc, 'columns', 0, 16, 1), slider('Rows', lc, 'rows', 0, 16, 1),
          el('div', { class: 'bits' }, el('label', {}, 'Lines ', el('select', { onchange: (e) => { lc.mode = Number(e.target.value); push(); } },
            ...['dark', 'light', 'flickering'].map((n, i) => el('option', { value: i, selected: lc.mode === i }, n))))),
          slider('Which lines', lc, 'seed', 1, 999, 1))));
    }

    const spritesWhy = reach.spritesDma ? (reach.sprites2004 || !isNes ? '' : 'Single $2004 writes are not filtered on this PPU core (DMA is).')
      : isNes ? `Not reachable on the ${reach.ppuCore} PPU core.` : 'Not reachable on this console yet.';
    g.append(el('details', { open: true }, groupSummary('Sprites', 'sprites'),
      el('div', { class: 'why' }, 'Bytes on their way into sprite memory. The game resends it every frame, so a hit lasts one frame.'),
      spritesWhy ? el('div', { class: 'off' }, spritesWhy) : null,
      el('fieldset', { class: reach.spritesDma ? '' : 'disabled' },
        el('div', { class: 'sub' }, 'Classic'),
        rateFx('X jitter', 'Sprites shake sideways and split apart.', sp.xJitter, [slider('Range px', sp.xJitter, 'range', 1, 64, 1)]),
        rateFx('Y jitter', 'Hidden sprites stay hidden.', sp.yJitter, [slider('Range px', sp.yJitter, 'range', 1, 64, 1)]),
        rateFx('Tile ±1', 'The neighbouring tile: wrong body part or animation frame.', sp.tile),
        el('div', { class: 'fx' }, el('div', { class: 'namerow' }, el('label', { class: 'name' }, 'Attribute flips'), soloBtn(sp.attr, 'Attribute flips')),
          bits(reach.attrNames || ['V-flip', 'H-flip', 'Behind BG', 'Palette 1', 'Palette 0'], sp.attr.bits), slider('Rate', sp.attr, 'rate', 0, 100, 1)),
        el('div', { class: 'sub' }, 'Organic (events)'),
        eventFx('Group lag', 'A group of 8 sprites keeps last frame\'s bytes.', sp.groupLag),
        eventFx('Clones', 'Sprites copy the one two places back.', sp.clone),
        eventFx('Field swap', 'Each sprite\'s bytes rotate: X lands in Y, and so on.', sp.fieldSwap),
        eventFx('Force bit', 'One bit forced in one field of every sprite.', sp.forceBit, [
          el('div', { class: 'bits' },
            el('label', {}, 'Field ', el('select', { onchange: (e) => { sp.forceBit.field = Number(e.target.value); push(); } },
              ...['Y', 'Tile', 'Attributes', 'X'].map((n, i) => el('option', { value: i, selected: sp.forceBit.field === i }, n)))),
            el('label', {}, 'Bit ', el('select', { onchange: (e) => { sp.forceBit.bit = Number(e.target.value); push(); } },
              ...[0, 1, 2, 3, 4, 5, 6, 7].map(i => el('option', { value: i, selected: sp.forceBit.bit === i }, String(i))))),
            el('label', {}, el('input', { type: 'checkbox', checked: sp.forceBit.high, onchange: (e) => { sp.forceBit.high = e.target.checked; push(); } }), 'Set (off = clear)'))
        ]))));

    g.append(el('details', { open: true }, groupSummary('Sound', 'sound'),
      el('div', { class: 'why' }, isNes ? 'Writes on their way into the APU. DMC samples are left alone.' : 'Writes on their way into the sound chip.'),
      reach.soundNote ? el('div', { class: 'off' }, reach.soundNote) : null,
      el('div', { class: 'sub' }, 'Classic'),
      el('div', { class: 'fx' }, el('div', { class: 'namerow' }, el('label', { class: 'name' }, 'Bit flips (pitch / volume / envelope bits)'), soloBtn(so.bitFlip, 'Bit flips (pitch / volume / envelope bits)')),
        bits(['Bit 0', 'Bit 1', 'Bit 2', 'Bit 3', 'Bit 4'], so.bitFlip.bits), slider('Rate', so.bitFlip, 'rate', 0, 100, 1)),
      rateFx('Detune ±1', 'Small pitch nudges on period writes.', so.nudge),
      el('div', { class: 'fx' }, el('div', { class: 'namerow' }, el('label', { class: 'name' }, 'Channel drop-outs'), soloBtn(so.drops, 'Channel drop-outs')),
        el('div', { class: 'why' }, 'A channel is switched off, then back on; it stays silent until its next note.'),
        bits(reach.channels || ['Pulse 1', 'Pulse 2', 'Triangle', 'Noise', 'DMC'], so.drops.channels),
        slider('Rate', so.drops, 'rate', 0, 100, 1), slider('Frames', so.drops, 'duration', 1, 600, 1)),
      el('div', { class: 'sub' }, 'Organic (events)'),
      eventFx('Stutter', 'Older register writes are replayed: notes and arpeggios repeat.', so.stutter),
      eventFx('Drone', 'Channels forced to constant, full volume.', so.drone),
      eventFx('Duty force', 'Both pulse channels forced to one duty.', so.duty, [slider('Duty', so.duty, 'value', 0, 3, 1)]),
      eventFx('Detune', 'Period low byte XORed: wrong notes.', so.detune, [slider('Amount', so.detune, 'value', 1, 255, 1)]),
      el('div', { class: 'sub' }, 'Pins'),
      el('div', { class: 'fx' }, el('div', { class: 'namerow' }, el('label', { class: 'name' }, 'Register bus'), soloBtn(so.busPins, 'Register bus')),
        el('div', { class: 'why' }, 'The wires from the CPU to the sound chip: writes land on the wrong register (the bass plays the lead) or with bent bits.'),
        (reach.soundBusLines || 0) > 0 ? pinGrid('A', so.busPins.address) : null, pinGrid('D', so.busPins.data), slider('Flicker', so.busPins, 'flicker', 0, 100, 1)),
      (reach.sampleLines || 0) > 0 || reach.console === 'md' ? el('div', { class: 'fx' }, el('div', { class: 'namerow' }, el('label', { class: 'name' }, reach.samplePinsTitle || 'Sample fetch'), soloBtn(so.samplePins, 'Sample fetch')),
        el('div', { class: 'why' }, 'Where the chip reads its own samples: drums crunch, instruments morph into noise.'),
        (reach.sampleLines || 0) > 0 ? pinGrid('A', so.samplePins.address) : null, pinGrid('D', so.samplePins.data), slider('Flicker', so.samplePins, 'flicker', 0, 100, 1)) : null,
      el('div', { class: 'fx' }, el('div', { class: 'namerow' }, el('label', { class: 'name' }, 'DAC pins (output)'), soloBtn(so.dacPins, 'DAC pins (output)')),
        el('div', { class: 'why' }, 'The 16 lines into the DAC: bitcrush and distortion on the final sound. Display-only: the game never hears it.'),
        pinGrid('D', so.dacPins.data), slider('Flicker', so.dacPins, 'flicker', 0, 100, 1)),
      el('div', { class: 'fx' }, check('Clock wobble', so.wobble, 'on'), el('div', { class: 'why' }, 'A drifting crystal: the pitch sways like a worn tape.'),
        slider('Depth', so.wobble, 'depth', 1, 100, 1), slider('Speed', so.wobble, 'speed', 1, 80, 1)),
      el('div', { class: 'fx' }, check('Dried-out capacitors', so.caps, 'on'), el('div', { class: 'why' }, 'The audio path’s old capacitors: the treble goes first, then crackle, then the sound cuts out for moments.'),
        slider('Age', so.caps, 'age', 1, 100, 1)),
      reach.console === 'sms' && (reach.channels || []).length > 4 ? eventFx('FM unit loses contact', 'The FM unit’s connector or socket: only the PSG is heard while it is out.', so.fmUnplug) : null,
      el('div', { class: 'sub' }, 'Generated sound (forced: made outside the console)'),
      el('div', { class: 'fx' }, check('Mains hum', so.hum, 'on'), el('div', { class: 'why' }, 'A ground loop between the console, the amp and the wall: a low buzz.'),
        el('div', { class: 'bits' }, el('label', {}, 'Mains ', el('select', { onchange: (e) => { so.hum.hz = Number(e.target.value); push(); } },
          ...[50, 60].map(v => el('option', { value: v, selected: so.hum.hz === v }, v + ' Hz'))))),
        slider('Level', so.hum, 'level', 0, 100, 1)),
      el('div', { class: 'fx' }, check('Tone injection', so.inject, 'on'), el('div', { class: 'why' }, 'A generated waveform pushed into the output, added or ring-modulating the game.'),
        el('div', { class: 'bits' }, el('label', {}, 'Wave ', el('select', { onchange: (e) => { so.inject.wave = Number(e.target.value); push(); } },
          ...['sine', 'square', 'saw', 'noise'].map((n, i) => el('option', { value: i, selected: so.inject.wave === i }, n)))),
          el('label', {}, ' Mix ', el('select', { onchange: (e) => { so.inject.mix = Number(e.target.value); push(); } },
          ...['added', 'ring modulation'].map((n, i) => el('option', { value: i, selected: so.inject.mix === i }, n))))),
        slider('Frequency (Hz)', so.inject, 'freq', 20, 8000, 1), slider('Level', so.inject, 'level', 0, 100, 1)),
      el('div', { class: 'fx' }, check('Test sweep', so.sweep, 'on'), el('div', { class: 'why' }, 'A test tone sweeping from low to high, over and over.'),
        slider('From (Hz)', so.sweep, 'from', 20, 2000, 1), slider('To (Hz)', so.sweep, 'to', 200, 16000, 10), slider('Seconds', so.sweep, 'seconds', 1, 30, 1), slider('Level', so.sweep, 'level', 0, 100, 1))));

    const bd = settings.bands;
    const bandFx = (label, why, fx, strengthLabel) => el('div', { class: 'fx' }, check(label, fx, 'on'), el('div', { class: 'why' }, why),
      slider('Rate', fx, 'rate', 0, 100, 1), slider('Width', fx, 'width', 1, 240, 1),
      strengthLabel ? slider(strengthLabel, fx, 'strength', 1, 64, 1) : null, slider('Frames', fx, 'lifetime', 1, 600, 1));
    g.append(el('details', { open: true }, groupSummary('Bands (scanlines)', 'bands'),
      el('div', { class: 'why' }, 'Timed bands of lines with one effect each; up to six at once, drifting a line now and then.'),
      reach.bands ? null : el('div', { class: 'off' }, `Not reachable on the ${reach.ppuCore} picture chip.`),
      el('fieldset', { class: reach.bands ? '' : 'disabled' },
        ...bandList(bd, bandFx))));

    const pl = settings.palette;
    g.append(el('details', { open: true }, groupSummary('Palette (display only)', 'palette'),
      el('div', { class: 'why' }, 'The picture’s colours through a transform, as the Glitch Boy does it: the game’s palette is never touched, so fades and flashes keep working.'),
      el('div', { class: 'fx' }, check('Palette glitch', pl, 'on'),
        el('div', { class: 'bits' }, el('label', {}, 'Mode ', el('select', { onchange: (e) => { pl.mode = Number(e.target.value); push(); } },
          ...['Hue +60°', 'Hue +120°', 'Complement', 'Hue +240°', 'Hue + brightness flip'].map((n, i) => el('option', { value: i, selected: pl.mode === i }, n))))),
        el('div', { class: 'bits' },
          el('label', {}, el('input', { type: 'checkbox', checked: pl.invert, onchange: (e) => { pl.invert = e.target.checked; push(); } }), 'Invert'),
          el('label', {}, el('input', { type: 'checkbox', checked: pl.channelSwap, onchange: (e) => { pl.channelSwap = e.target.checked; push(); } }), 'Channel swap')),
        slider('Posterize', pl, 'posterize', 0, 8, 1),
        slider('Cycle frames', pl, 'cycleFrames', 0, 240, 1)),
      el('div', { class: 'fx' }, el('div', { class: 'namerow' }, el('label', { class: 'name' }, 'Video DAC pins (R0-R7, G0-G7, B0-B7)'), soloBtn(settings.videoDac, 'Video DAC pins (R0-R7, G0-G7, B0-B7)')),
        el('div', { class: 'why' }, 'The 24 lines into the video DAC: stuck or bridged colour bits on the final picture.'),
        pinGrid('D', settings.videoDac.data)),
      (reach.console !== 'nes' || reach.ppuCore === 'Fix') ? el('div', { class: 'fx' },
        el('div', { class: 'namerow' }, el('label', { class: 'name' }, 'Palette RAM bus'), soloBtn(settings.paletteBus, 'Palette RAM bus')),
        el('div', { class: 'why' }, 'The palette chip’s own lines: the picture reads the wrong colour entry or bent colour bits (the game’s palette stays intact).'),
        pinGrid('A', settings.paletteBus.address), settings.paletteBus.data.length ? pinGrid('D', settings.paletteBus.data) : null, slider('Flicker', settings.paletteBus, 'flicker', 0, 100, 1)) : null));

    const un = settings.unsafe;
    const rotFx = (label, why, fx) => el('div', { class: 'fx' }, check(label, fx, 'on'), el('div', { class: 'why' }, why),
      slider('Rate', fx, 'rate', 0, 100, 1), slider('Bits / hit', fx, 'bits', 1, 64, 1));
    const unsafeBody = [
      el('div', { class: 'off' }, 'These damage the game itself: it can crash, freeze or stay broken until Reload State. The freeze watchdog offers the entry state when it stops.'),
      el('div', { class: 'sub' }, 'Bent writes'),
      el('div', { class: 'fx' }, check('Console pins bend writes too', settings.consolePins, 'bendWrites'),
        el('div', { class: 'why' }, 'Video RAM writes land at the bent address with the bent value: the damage stays.')),
    ];
    if (isNes) unsafeBody.push(el('div', { class: 'fx' }, check('Cartridge pins bend writes too', settings.cartPins, 'bendWrites'),
        el('div', { class: 'why' }, reach.chrIsRam ? 'CHR-RAM writes land bent: tile graphics stay damaged.' : 'CHR-ROM cannot be written: no effect on this cartridge.')));
    if (reach.cpuPins) unsafeBody.push(
      el('div', { class: 'sub' }, reach.cpuPinsTitle || 'CPU bus · cartridge PRG pins'),
      el('div', { class: 'why' }, 'The CPU reads the game\u2019s code and data through these: wrong instructions, wrong numbers, often a crash.'),
      el('div', { class: 'legend' }, '· connected   L stuck low   H stuck high   B bridged to the next line   F floating'),
      el('div', { class: 'sub' }, 'Address lines'), pinGrid('A', un.cpuPins.address),
      el('div', { class: 'sub' }, 'Data lines'), pinGrid('D', un.cpuPins.data),
      slider('Flicker', un.cpuPins, 'flicker', 0, 100, 1),
      el('div', { class: 'fx' }, check('Bend writes too (mapper registers)', un.cpuPins, 'bendWrites')));
    if (reach.soundCpuLines > 0) unsafeBody.push(
      el('div', { class: 'sub' }, reach.soundCpuTitle || 'Sound CPU bus'),
      el('div', { class: 'why' }, 'The sound CPU runs the music program through these: wrong notes, broken tempos, a sound driver that locks up.'),
      el('div', { class: 'sub' }, 'Address lines'), pinGrid('A', un.soundCpuPins.address),
      el('div', { class: 'sub' }, 'Data lines'), pinGrid('D', un.soundCpuPins.data),
      slider('Flicker', un.soundCpuPins, 'flicker', 0, 100, 1));
    if (reach.dmaLines > 0) unsafeBody.push(
      el('div', { class: 'sub' }, `DMA controller · source bus (A0-A${reach.dmaLines - 1}, D0-D${(reach.dmaDataLines || 8) - 1})`),
      el('div', { class: 'why' }, 'Block copies (tiles, maps, palettes, sprite tables) read from the wrong place or arrive bent.'),
      el('div', { class: 'sub' }, 'Address lines'), pinGrid('A', un.dmaPins.address),
      el('div', { class: 'sub' }, 'Data lines'), pinGrid('D', un.dmaPins.data),
      slider('Flicker', un.dmaPins, 'flicker', 0, 100, 1));
    if (reach.coproName) unsafeBody.push(
      el('div', { class: 'sub' }, `Coprocessor · ${reach.coproName}` + (reach.coproName === 'DSP-1' ? ' (data port: data lines only)' : ' bus (A0-A23, D0-D7)')),
      el('div', { class: 'why' }, 'The cartridge chip that helps the console (3D, maths, a second CPU) reads its program and data through these.'),
      reach.coproName === 'DSP-1' ? null : el('div', { class: 'sub' }, 'Address lines'), reach.coproName === 'DSP-1' ? null : pinGrid('A', un.coproPins.address),
      el('div', { class: 'sub' }, 'Data lines'), pinGrid('D', un.coproPins.data),
      slider('Flicker', un.coproPins, 'flicker', 0, 100, 1));
    unsafeBody.push(el('div', { class: 'sub' }, `Work-RAM bus (A0-A${(reach.ramBusLines || 11) - 1}, D0-D7)`),
      el('div', { class: 'why' }, 'The CPU’s own RAM chip: every variable, the stack and pointers read through bent lines.'),
      el('div', { class: 'sub' }, 'Address lines'), pinGrid('A', un.ramBus.address),
      el('div', { class: 'sub' }, 'Data lines'), pinGrid('D', un.ramBus.data),
      slider('Flicker', un.ramBus, 'flicker', 0, 100, 1),
      el('div', { class: 'fx' }, check('Bend writes too', un.ramBus, 'bendWrites')));
    unsafeBody.push(el('div', { class: 'sub' }, 'Interrupt lines'),
      eventFx('Ghost NMI', 'An extra vertical-blank interrupt: the game runs part of a frame twice.', un.ghostNmi),
      eventFx('Dropped NMI', 'Vertical-blank interrupts swallowed: frames freeze or skip.', un.dropNmi),
      eventFx('Stuck IRQ', 'The IRQ line held down: the game’s interrupt handler runs over and over.', un.stuckIrq));
    if (reach.console === 'md') unsafeBody.push(el('div', { class: 'sub' }, '68000 / Z80 bus arbitration'),
      eventFx('BUSREQ stuck', 'The 68000 keeps the Z80 off the bus: the sound CPU halts, the music freezes.', un.z80BusHold),
      eventFx('Z80 reset held', 'The sound CPU is held in reset: silence, then a restart from scratch.', un.z80ResetHold));
    unsafeBody.push(el('div', { class: 'sub' }, 'Reset line and power'),
      reach.cic ? el('div', { class: 'fx' }, check('Lockout chip (CIC) fails', un.cicReset, 'on'), el('div', { class: 'why' }, 'The console resets about once a second, the famous blinking power light.'),
        slider('Every (frames)', un.cicReset, 'periodFrames', 10, 600, 1)) : null,
      el('div', { class: 'fx' }, check('Reset bounce', un.resetBounce, 'on'), el('div', { class: 'why' }, 'A worn reset button: the console resets by itself now and then.'),
        slider('Rate', un.resetBounce, 'rate', 0, 100, 1)),
      el('div', { class: 'fx' }, check('Power sag (brown-out)', un.powerSag, 'on'), el('div', { class: 'why' }, 'The supply dips: the console resets, and RAM keeps only part of what it held.'),
        slider('Rate', un.powerSag, 'rate', 0, 100, 1), slider('RAM lost (%)', un.powerSag, 'decay', 0, 100, 1)),
      reach.cpuPins ? eventFx('Loose cartridge', 'A badly seated or tilted cartridge: bursts of faults on its pins, then it sits right again.', un.looseCart) : null);
    unsafeBody.push(el('div', { class: 'sub' }, 'Wear and environment'),
      el('div', { class: 'fx' }, check('Heat soak', un.heatSoak, 'on'), el('div', { class: 'why' }, 'Cracked joints open up as the board warms: every flickering pin fault is there more and more often, until it never lets go. A reload cools it.'),
        slider('Minutes to worst', un.heatSoak, 'minutes', 1, 60, 1)),
      el('div', { class: 'fx' }, check('Static discharge', un.esd, 'on'), el('div', { class: 'why' }, 'A static zap: bit flips in every memory at the same moment.'),
        slider('Rate', un.esd, 'rate', 0, 100, 1), slider('Bits', un.esd, 'bits', 1, 512, 1)),
      reach.console === 'snes' || reach.console === 'md' || reach.console === 'wm' ? el('div', { class: 'fx' }, check('DRAM refresh rot', un.dramRot, 'on'),
        el('div', { class: 'why' }, reach.console === 'md' ? 'The VRAM and work RAM are DRAM: when refresh fails, whole rows fade to 0.' : 'The work RAM is DRAM: when refresh fails, whole rows fade to 0.'),
        slider('Rate', un.dramRot, 'rate', 0, 100, 1), slider('Rows', un.dramRot, 'rows', 1, 16, 1)) : null,
      reach.console === 'gb' ? el('div', { class: 'fx' }, check('Link port noise', un.linkNoise, 'on'), el('div', { class: 'why' }, 'A dirty link port picks up noise: garbage bytes arrive as if a cable were plugged in.'),
        slider('Rate', un.linkNoise, 'rate', 0, 100, 1)) : null,
      reach.console === 'md' ? el('div', { class: 'fx' }, el('div', { class: 'bits' }, el('label', {}, 'Pad TH wire ', el('select', { onchange: (e) => { un.padTh.mode = Number(e.target.value); push(); } },
          ...['connected', 'stuck low', 'stuck high', 'floating'].map((n, i) => el('option', { value: i, selected: un.padTh.mode === i }, n))))),
        el('div', { class: 'why' }, 'Player 1’s select wire: the pad answers the wrong half of its buttons (6-button pads lose X, Y, Z and Mode).'),
        slider('Flicker', un.padTh, 'flicker', 0, 100, 1)) : null);
    unsafeBody.push(el('div', { class: 'sub' }, 'Clock pins'),
      eventFx('Clock stall', 'The CPU’s clock stops for a while: the picture and sound freeze.', un.clockStall),
      eventFx('Clock rush', 'The clock runs double: the game races, the music doubles its tempo.', un.clockRush));
    unsafeBody.push(el('div', { class: 'sub' }, 'Video-chip register bus'),
      el('div', { class: 'why' }, 'The CPU’s wires to the picture chip’s registers: scroll, control and VRAM-address writes land bent or on the wrong register.'),
      el('div', { class: 'sub' }, 'Register lines'), pinGrid('A', un.videoBus.address),
      el('div', { class: 'sub' }, 'Data lines'), pinGrid('D', un.videoBus.data),
      slider('Flicker', un.videoBus, 'flicker', 0, 100, 1));
    // controller lines: one wire per button
    const PAD = [['·', 'connected'], ['R', 'stuck released'], ['P', 'stuck pressed'], ['B', 'bridged to the next button']];
    const padGrid = el('div', { class: 'pins' });
    (reach.padOrder || []).forEach((bit, i) => {
      const b = el('button', { type: 'button' });
      const paint = () => { const f = un.padLines[bit] || 0; b.className = `pin f${f}`; b.replaceChildren(el('span', {}, PAD[f][0]), (reach.padNames || [])[i] || String(bit)); b.title = `${(reach.padNames || [])[i]}: ${PAD[f][1]}`; };
      b.addEventListener('click', () => { un.padLines[bit] = ((un.padLines[bit] || 0) + 1) % 4; paint(); push(); });
      b.addEventListener('contextmenu', (e) => { e.preventDefault(); un.padLines[bit] = 0; paint(); push(); });
      paint(); padGrid.appendChild(b);
    });
    unsafeBody.push(el('div', { class: 'sub' }, 'Controller lines (player 1)'),
      el('div', { class: 'why' }, 'A wire per button: stuck released, stuck pressed (ghost presses), or bridged to the next one (pressing either presses both).'),
      el('div', { class: 'legend' }, '· connected   R stuck released   P stuck pressed   B bridged'),
      padGrid, slider('Flicker', un, 'padFlicker', 0, 100, 1));
    if (reach.soundRam) unsafeBody.push(el('div', { class: 'sub' }, 'Sound memory'),
      rotFx('Sound RAM rot', 'Bits flip in the sound chip’s own RAM (samples, the music driver): decay, then maybe silence.', un.soundRamRot));
    if (reach.console === 'snes') unsafeBody.push(eventFx('Echo abuse', 'Echo at its longest and loudest, writing over sample memory: the famous SNES melt.', un.echoAbuse));
    unsafeBody.push(el('div', { class: 'sub' }, 'Bit rot'),
      rotFx('RAM rot', 'Random bits flip anywhere in work RAM: the stack and pointers too.', un.ramRot));
    if (reach.vramRot !== false) unsafeBody.push(rotFx('VRAM rot', 'Random bits flip in video RAM: tiles and maps decay.', un.vramRot));
    g.append(el('details', { open: false, class: 'unsafe' }, groupSummary('Unsafe (can break the game)', 'unsafe'), ...unsafeBody));

    const vd = settings.video;
    g.append(el('details', { open: true }, groupSummary('Video signal (display only, forced)', 'palette'),
      el('div', { class: 'why' }, 'What a bad cable, modulator or TV does to the picture on its way to the screen: laid over the finished picture, the game never sees it.'),
      el('div', { class: 'fx' }, check('Picture roll (sync loss)', vd.roll, 'on'), el('div', { class: 'why' }, 'Vertical sync lost: the picture rolls with its dark blanking bar.'),
        slider('Speed', vd.roll, 'speed', -20, 20, 1)),
      el('div', { class: 'fx' }, check('RF ghost', vd.ghost, 'on'), el('div', { class: 'why' }, 'An echo on the antenna signal: a faint copy beside the picture.'),
        slider('Level', vd.ghost, 'level', 0, 100, 1), slider('Offset', vd.ghost, 'offset', 1, 32, 1)),
      el('div', { class: 'fx' }, check('RGB misconvergence', vd.misconverge, 'on'), el('div', { class: 'why' }, 'The red and blue beams miss their dots: coloured fringes.'),
        slider('Pixels', vd.misconverge, 'pixels', -8, 8, 1)),
      el('div', { class: 'fx' }, check('RF snow', vd.snow, 'on'), el('div', { class: 'why' }, 'A weak signal: snow over the picture.'),
        slider('Level', vd.snow, 'level', 0, 100, 1))));

    const rm = settings.ram;

    g.append(el('details', { open: true, class: 'unsafe' }, groupSummary('RAM nudges (unsafe: can leave the game broken)', 'ram', 'unsafe'),
      el('div', { class: 'why' }, 'Writes into the work RAM of the game itself, so it can break progress until Reload State. Learns which RAM bytes are live game variables (positions, timers, speeds) and nudges them a step at a time, only inside the range the game itself used. Pointers and flags are filtered out.'),
      el('div', { class: 'fx' }, check('Nudge variables', rm, 'on'),
        slider('Rate', rm, 'rate', 0, 100, 1), slider('Step', rm, 'step', 1, 8, 1),
        slider('Frames', rm, 'lifetime', 1, 600, 1), slider('At once', rm, 'maxProbes', 1, 8, 1),
        el('div', { id: 'ramStats', class: 'why' }, 'Profiler: learning…'))));
    queueMicrotask(() => { const g = $('groups'); knobRows(g); compactStrip(g); tabify(); paintSolo(); });
  }

  // ---------------------------------------------------------------- Build view (controller standard C4)
  // Every effect this console can do, from the purity catalog, in a list; drag one (or press +) onto the board and it becomes its
  // controller (effect-controllers.js). The board places modules on a 24 px step, kept per console; modules move by their title bar.
  // The board edits the same settings as the strip: both views always agree.
  let view = 'strip', catalog = null;
  let BOARD_COLS = 24;   // set from the board's width when it is drawn (at least one module wide)
  const PITCH = 8;       // the board's placement step: fine, so a module's footprint is its real size rounded up by at most 7 px
  const MODULE_GAP = 8;  // the space a module keeps to its right and below, so neighbours can sit 8 px apart and no closer
  const boardKey = () => `butcher.board.v1.${reach?.console || 'nes'}`;
  function loadBoard() { try { const b = JSON.parse(localStorage.getItem(boardKey()) || '[]'); return Array.isArray(b) ? b : []; } catch { return []; } }
  function saveBoard(b) { try { localStorage.setItem(boardKey(), JSON.stringify(b)); } catch { /* not kept */ } }
  async function loadCatalog() { const r = await api.butcher.getCatalog().catch(() => null); catalog = r?.success ? r : { effects: [] }; }
  // ---- classification menu: right-click a module's title bar. Unsafe (in the catalog's unsafe group, or tagged here) and Disgraced
  // (feels unnatural: kept out of rolls unless "Include disgraced effects in rolls" is ticked). The app keeps them in Butchering/tags.json.
  function tagMenu(ev, path) {
    ev.preventDefault();
    document.getElementById('tagmenu')?.remove();
    const e = (catalog?.effects || []).find(x => x.path === path); if (!e) return;
    const item = (label, key, hint) => el('label', { class: 'tagitem', title: hint },
      el('input', { type: 'checkbox', checked: e[key] ? '' : null, onchange: async (c) => {
        const v = c.target.checked;
        const r = await api.butcher.setTags(path, { [key]: v }).catch(() => null);
        if (r?.success) {
          e.unsafe = r.unsafe; e.disgraced = r.disgraced;
          status(`${e.name}: ${key === 'unsafe' ? (r.unsafe ? 'tagged unsafe' : 'not unsafe') : (r.disgraced ? 'disgraced' : 'no longer disgraced')}.`);
          menu.remove(); render();
        } else { c.target.checked = !v; status(r?.error || 'Could not save the tag.', true); }
      } }), label);
    const menu = el('div', { id: 'tagmenu', class: 'tagmenu', role: 'menu', style: `left:${ev.clientX}px;top:${ev.clientY}px` },
      el('div', { class: 'tagtitle' }, e.name),
      item('Unsafe', 'unsafe', e.unsafeBuiltIn ? 'In the catalog\'s unsafe group' : 'Tag it when it can crash or wreck the game'),
      item('Disgraced', 'disgraced', 'Feels unnatural: kept out of rolls'));
    document.body.append(menu);
    const b = menu.getBoundingClientRect();
    if (b.right > innerWidth) menu.style.left = `${Math.max(0, innerWidth - b.width - 4)}px`;
    if (b.bottom > innerHeight) menu.style.top = `${Math.max(0, innerHeight - b.height - 4)}px`;
    const close = (x) => {
      if (!document.body.contains(menu) || (x.type === 'keydown' ? x.key === 'Escape' : !menu.contains(x.target))) {
        menu.remove(); removeEventListener('pointerdown', close, true); removeEventListener('keydown', close, true);
      }
    };
    setTimeout(() => { addEventListener('pointerdown', close, true); addEventListener('keydown', close, true); });
  }
  async function setView(v) {
    view = v;
    $('viewStrip').classList.toggle('on', v === 'strip'); $('viewStrip').setAttribute('aria-selected', String(v === 'strip'));
    $('viewBuild').classList.toggle('on', v === 'build'); $('viewBuild').setAttribute('aria-selected', String(v === 'build'));
    try { localStorage.setItem('butcher.view', v); } catch { }
    if (v === 'build' && !catalog?.effects?.length) await loadCatalog();
    render();
  }
  const GROUP_NAMES = { picture: 'Picture', sprites: 'Sprites', sound: 'Sound', bands: 'Bands', palette: 'Palette & video', unsafe: 'Unsafe' };
  // a module's size on the board: its faceplate's cells plus the plate's frame and title
  // a module's footprint in board cells: its faceplate (controller standard sizes: cells, gutters, the plate's padding) plus the title bar
  // a module's footprint, from its real rendered size (title bar + faceplate), measured once per layout
  const sizeCache = new Map();
  function moduleSize(built) {
    const key = JSON.stringify(built.layout);
    let px = sizeCache.get(key);
    if (!px) {
      const probe = el('div', { class: 'bmodule', style: 'position:absolute;left:-10000px;top:0;visibility:hidden' },
        el('div', { class: 'bhandle' }, el('span', { class: 'bname' }, 'x')), el('div', { class: 'bplate' }));
      document.body.append(probe);
      BNControls.createPanel(probe.lastChild, Object.assign({}, built.layout, { title: '' }), built.params);
      px = { w: probe.offsetWidth, h: probe.offsetHeight };
      probe.remove();
      if (px.w > 0) sizeCache.set(key, px);
    }
    return { w: Math.ceil((px.w + MODULE_GAP) / PITCH), h: Math.ceil((px.h + MODULE_GAP) / PITCH) };
  }
  // is this effect doing something: its On switch, or any bit / channel / pin it has set
  function isLive(o) {
    if (!o || typeof o !== 'object') return false;
    if ('on' in o) return !!o.on;
    return Object.values(o).some(v => Array.isArray(v) && v.some(x => x === true || (typeof x === 'number' && x !== 0)));
  }
  function freeSpot(board, w, h) {
    const taken = (x, y) => board.some(m => x < m.x + m.w && x + w > m.x && y < m.y + m.h && y + h > m.y);
    for (let y = 0; y < 600; y++) for (let x = 0; x + w <= BOARD_COLS; x++) if (!taken(x, y)) return { x, y };
    return { x: 0, y: 0 };
  }
  function buildCtx(path) {
    const entry = (catalog?.effects || []).find(e => e.path === path);
    return { settings, reach, entry, onChange: () => { push(); refreshBoardDisplays(); } };
  }
  let boardPanels = [], trying = null, tryPanel = null;
  function refreshBoardDisplays() { for (const bp of boardPanels) bp.refresh(); tryPanel?.refresh(); }
  function renderBuild() {
    document.getElementById('panel')?.classList.add('wide');
    for (const id of ['tabs', 'search']) { const n = $(id); if (n) n.style.display = 'none'; }
    const g = $('groups');
    g.replaceChildren();
    boardPanels = [];
    const effects = (catalog?.effects || []).filter(e => BNEffects.available(e.path, reach));
    let board = loadBoard().filter(m => effects.some(e => e.path === m.path));

    // ---- the list
    const list = el('div', { class: 'efflist', role: 'list', 'aria-label': 'Effects' });
    const filter = el('input', { type: 'search', class: 'efffilter', placeholder: 'Filter effects…', 'aria-label': 'Filter effects', oninput: () => paintList() });
    function paintList() {
      const q = filter.value.trim().toLowerCase();
      const rows = [];
      for (const grp of Object.keys(GROUP_NAMES)) {
        const items = effects.filter(e => e.group === grp && (!q || e.name.toLowerCase().includes(q))).sort((a, b) => b.purityHere - a.purityHere);
        if (!items.length) continue;
        rows.push(el('div', { class: 'effgrp' }, GROUP_NAMES[grp]));
        for (const e of items) {
          const placed = board.some(m => m.path === e.path);
          const item = el('div', { class: 'effitem' + (placed ? ' placed' : '') + (trying === e.path ? ' selected' : ''), role: 'listitem', draggable: 'true', tabindex: 0, title: `${e.name}\n${e.tier} ${e.purityHere}: ${e.why}`,
            onclick: (ev) => { if (!ev.target.closest('button')) tryEffect(e.path); },
            onkeydown: (ev) => { if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); tryEffect(e.path); } },
            ondragstart: (ev) => { ev.dataTransfer.setData('text/x-butcher-effect', e.path); ev.dataTransfer.effectAllowed = 'copy'; } },
            el('span', { class: 'tierdot', style: `background:${BNEffects.tierColour(e.tier)}`, 'aria-label': `${e.tier}, purity ${e.purityHere}` }),
            el('span', { class: 'effname' }, e.name), el('span', { class: 'effscore' }, String(e.purityHere)),
            el('button', { type: 'button', class: 'effadd', 'aria-label': `Add ${e.name} to the board`, disabled: placed ? '' : null, onclick: () => addModule(e.path) }, placed ? '✓' : '+'));
          rows.push(item);
        }
      }
      list.replaceChildren(...rows);
    }

    // ---- the test bench: the selected effect's controller, alone in the free space left of the panel, to play with before placing
    function tryEffect(path) {
      const e = effects.find(x => x.path === path); if (!e) return;
      trying = path;
      const built = BNEffects.build(path, buildCtx(path));
      let bench = document.getElementById('trybench');
      if (!bench) { bench = el('div', { id: 'trybench', class: 'trybench', role: 'dialog', 'aria-label': 'Try an effect' }); document.body.append(bench); }
      const placed = board.some(m => m.path === path);
      const plateHost = el('div', { class: 'bplate' });
      bench.replaceChildren(
        el('div', { class: 'bhandle trytitle' },
          el('span', { class: 'tierdot', style: `background:${BNEffects.tierColour(e.tier)}` }),
          el('span', { class: 'bname' }, built.title), el('span', { class: 'effscore' }, String(e.purityHere)),
          el('button', { type: 'button', class: 'bremove', 'aria-label': 'Close the test bench', onclick: closeTry }, '×')),
        plateHost,
        el('div', { class: 'trywhy' }, `${e.tier}: ${e.why}`),
        el('div', { class: 'row tryrow' },
          el('button', { type: 'button', disabled: placed ? '' : null, onclick: () => addModule(path) }, placed ? 'On the board' : 'Add to board')),
        el('div', { class: 'trywhy' }, 'Live: it changes the running game, like the board does.'));
      built.layout.title = ''; built.layout.accent = BNEffects.tierColour(e.tier);
      const bp = BNControls.createPanel(plateHost, built.layout, built.params, { onPress: (id) => { built.actions[id]?.(); bp.refresh(); } });
      tryPanel = bp;
      paintList();
    }
    function closeTry() { trying = null; tryPanel = null; document.getElementById('trybench')?.remove(); paintList(); }

    // ---- the board
    const grid = el('div', { class: 'board', style: `--bcols:${BOARD_COLS}` });
    grid.addEventListener('dragover', (ev) => { if (ev.dataTransfer.types.includes('text/x-butcher-effect')) { ev.preventDefault(); ev.dataTransfer.dropEffect = 'copy'; } });
    grid.addEventListener('drop', (ev) => {
      const path = ev.dataTransfer.getData('text/x-butcher-effect'); if (!path) return;
      ev.preventDefault();
      const r = grid.getBoundingClientRect();
      addModule(path, Math.floor((ev.clientX - r.left) / PITCH), Math.floor((ev.clientY - r.top + grid.scrollTop) / PITCH));
    });
    function addModule(path, x, y) {
      if (board.some(m => m.path === path)) return;
      if (trying === path) closeTry();
      const built = BNEffects.build(path, buildCtx(path));
      const sz = moduleSize(built);
      let spot = x === undefined ? freeSpot(board, sz.w, sz.h) : { x: Math.max(0, Math.min(BOARD_COLS - sz.w, x)), y: Math.max(0, y) };
      board.push({ path, x: spot.x, y: spot.y, w: sz.w, h: sz.h });
      saveBoard(board); paintBoard(); paintList();
    }
    // Import Live: every effect the current settings have switched on gets a module, biggest first, each in the
    // first free spot scanning row by row, so they pack into the board's width.
    function importLive() {
      const fresh = effects.filter(e => !board.some(m => m.path === e.path) && isLive(getPath(settings, e.path)))
        .map(e => ({ path: e.path, ...moduleSize(BNEffects.build(e.path, buildCtx(e.path))) }))
        .sort((a, b) => b.h - a.h || b.w - a.w);
      if (!fresh.length) { status(board.length ? 'Every live effect is already on the board.' : 'No effect is on right now: load a preset or press Randomize first.'); return; }
      for (const f of fresh) { const s = freeSpot(board, f.w, f.h); board.push({ path: f.path, x: s.x, y: s.y, w: f.w, h: f.h }); }
      saveBoard(board); paintBoard(); paintList();
      status(`Imported ${fresh.length} live effect${fresh.length === 1 ? '' : 's'}.`);
    }
    function paintBoard() {
      grid.replaceChildren(); boardPanels = [];
      BOARD_COLS = Math.max(36, Math.floor((grid.clientWidth || 576) / PITCH));
      grid.style.setProperty('--bcols', BOARD_COLS);
      // a module the narrower board no longer fits moves to the first free spot
      // sizes first (a faceplate's size follows the controller standard), then settle: a module that no longer fits the board, or now
      // overlaps one placed before it (sizes changed since it was saved), moves to the first free spot
      for (const m of board) { const sz = moduleSize(BNEffects.build(m.path, buildCtx(m.path))); m.w = sz.w; m.h = sz.h; }
      const settled = [];
      for (const m of [...board].sort((a, b) => a.y - b.y || a.x - b.x)) {
        const clash = m.x + m.w > BOARD_COLS || settled.some(o => m.x < o.x + o.w && m.x + m.w > o.x && m.y < o.y + o.h && m.y + m.h > o.y);
        if (clash) { const sp = freeSpot(settled, m.w, m.h); m.x = sp.x; m.y = sp.y; }
        settled.push(m);
      }
      saveBoard(board);
      const rows = Math.max(48, ...board.map(m => m.y + m.h + 1));
      grid.style.gridTemplateRows = `repeat(${rows}, ${PITCH}px)`;
      for (const m of board) {
        const built = BNEffects.build(m.path, buildCtx(m.path));
        const sz = moduleSize(built); m.w = sz.w; m.h = sz.h;
        const entry = (catalog?.effects || []).find(e => e.path === m.path);
        const mod = el('div', { class: 'bmodule', style: `grid-column:${m.x + 1} / span ${m.w};grid-row:${m.y + 1} / span ${m.h}` });
        const handle = el('div', { class: 'bhandle', title: 'Drag to move · right-click to classify', oncontextmenu: (ev) => tagMenu(ev, m.path) },
          el('span', { class: 'tierdot', style: `background:${BNEffects.tierColour(entry?.tier)}` }),
          el('span', { class: 'bname' }, built.title),
          entry?.unsafe ? el('span', { class: 'tagbadge unsafe', title: 'Unsafe' }, 'U') : null, entry?.disgraced ? el('span', { class: 'tagbadge', title: 'Disgraced' }, 'D') : null,
          el('span', { class: 'effscore' }, entry ? String(entry.purityHere) : ''),
          el('button', { type: 'button', class: 'bremove', 'aria-label': `Remove ${built.title}`, onclick: () => { board = board.filter(b => b !== m); saveBoard(board); paintBoard(); paintList(); } }, '×'));
        mod.append(handle);
        const host = el('div', { class: 'bplate' });
        mod.append(host);
        built.layout.title = '';
        built.layout.accent = BNEffects.tierColour(entry?.tier);
        const bp = BNControls.createPanel(host, built.layout, built.params, { onPress: (id) => { built.actions[id]?.(); bp.refresh(); } });
        boardPanels.push(bp);
        // move by the title bar, snapping to cells, never onto another module
        // The module follows the pointer; where the pointer's spot would overlap a neighbour, it takes the nearest free spot to it
        // instead of stopping, so it slides along edges and into corners. No text gets selected while dragging.
        handle.addEventListener('pointerdown', (ev) => {
          if (ev.target.closest('button') || ev.button !== 0) return;
          ev.preventDefault();
          handle.setPointerCapture(ev.pointerId);
          document.body.classList.add('bdragging'); mod.classList.add('moving');
          const sx = ev.clientX, sy = ev.clientY, ox = m.x, oy = m.y;
          const others = board.filter(o => o !== m);
          const free = (x, y) => x >= 0 && y >= 0 && x + m.w <= BOARD_COLS && !others.some(o => x < o.x + o.w && x + m.w > o.x && y < o.y + o.h && y + m.h > o.y);
          const move = (e2) => {
            const tx = Math.max(0, Math.min(BOARD_COLS - m.w, ox + Math.round((e2.clientX - sx) / PITCH))), ty = Math.max(0, oy + Math.round((e2.clientY - sy) / PITCH));
            let best = null, bd = Infinity;
            for (let r = 0; r <= 40 && !best; r++) for (let dy = -r; dy <= r; dy++) for (let dx = -r; dx <= r; dx++) {
              if (Math.max(Math.abs(dx), Math.abs(dy)) !== r) continue;
              const d = dx * dx + dy * dy;
              if (d < bd && free(tx + dx, ty + dy)) { bd = d; best = { x: tx + dx, y: ty + dy }; }
            }
            if (best && (best.x !== m.x || best.y !== m.y)) { m.x = best.x; m.y = best.y; mod.style.gridColumn = `${m.x + 1} / span ${m.w}`; mod.style.gridRow = `${m.y + 1} / span ${m.h}`; }
          };
          const up = () => { handle.removeEventListener('pointermove', move); handle.removeEventListener('pointerup', up); handle.removeEventListener('pointercancel', up); document.body.classList.remove('bdragging'); saveBoard(board); paintBoard(); };
          handle.addEventListener('pointermove', move); handle.addEventListener('pointerup', up); handle.addEventListener('pointercancel', up);
        });
        grid.append(mod);
      }
      if (!board.length) grid.append(el('div', { class: 'boardhint' }, 'Drag effects here from the list (or press + beside one). Each becomes its own controller; move it by its title bar.'));
    }

    const tools = el('div', { class: 'row boardtools' },
      el('button', { type: 'button', onclick: () => { board = []; saveBoard(board); paintBoard(); paintList(); } }, 'Clear board'),
      el('button', { type: 'button', title: 'Put every effect that is on right now (a loaded preset, a roll, the strip) on the board, packed tight', onclick: importLive }, 'Import Live'),
      el('span', { class: 'note' }, 'The board edits the same settings as the strip. Dot colour: purity tier (green pure … red forced).'));
    g.append(tools, el('div', { class: 'buildview' }, el('div', { class: 'listpane' }, filter, list), grid));
    paintList(); paintBoard();
    if (trying) tryEffect(trying);   // a redraw keeps the bench, bound to the new board
  }

  // ---------------------------------------------------------------- randomize / mutate
  // Seeded (mulberry32), so a roll can be saved with its seed and come back the same. One strength slider: few gentle
  // effects at low, many strong ones at high. Unsafe effects only when "include unsafe" is ticked.
  let strength = 35, includeUnsafe = false, includeDisgraced = false, history = [], historyAt = -1;
  function rng(seed) { let a = seed >>> 0; return () => { a = (a + 0x6D2B79F5) >>> 0; let t = a; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }
  function candidates(st) {
    const c = [];
    const sp = st.sprites, so = st.sound, bd = st.bands;
    if (reach.spritesDma) for (const k of ['xJitter', 'yJitter', 'tile', 'groupLag', 'clone', 'fieldSwap', 'forceBit']) c.push(['fx', sp[k]]);
    if (reach.spritesDma && (reach.attrNames || []).length) c.push(['bits', sp.attr, 'bits']);
    if (reach.sound !== false) { c.push(['bits', so.bitFlip, 'bits'], ['fx', so.nudge], ['bits', so.drops, 'channels'], ['fx', so.stutter], ['fx', so.drone], ['fx', so.detune]); if (isNesReach()) c.push(['fx', so.duty]); }
    if (reach.bands) { for (const k of (reach.bandKinds || [])) if (bd[k]) c.push(['fx', bd[k]]); (reach.extraBands || []).forEach((_, i) => c.push(['fx', bd['extra' + (i + 1)]])); }
    c.push(['fx', st.palette], ['fx', st.sound.wobble], ['pins', st.sound.busPins], ['fx', st.sound.caps]);
    if (reach.lcd) c.push(['fx', st.lcd]);
    c.push(['fx', st.video.roll], ['fx', st.video.ghost], ['fx', st.video.misconverge], ['fx', st.video.snow], ['fx', st.sound.hum], ['fx', st.sound.inject], ['fx', st.sound.sweep]);
    if (reach.console === 'snes' || reach.console === 'wm') c.push(['fx', st.vramChip]);
    if (reach.console === 'sms' && (reach.channels || []).length > 4) c.push(['fx', st.sound.fmUnplug]);
    if (reach.consolePins) c.push(['pins', st.consolePins]);
    if (isNesReach() && reach.cartPins) c.push(['pins', st.cartPins]);
    if (includeUnsafe) { if (reach.ram) c.push(['fx', st.ram]); c.push(['fx', st.unsafe.resetBounce], ['fx', st.unsafe.powerSag], ['fx', st.unsafe.esd], ['fx', st.unsafe.heatSoak]); if (reach.console === 'snes' || reach.console === 'md') c.push(['fx', st.unsafe.dramRot]); if (reach.console === 'gb') c.push(['fx', st.unsafe.linkNoise]); if (reach.cic) c.push(['fx', st.unsafe.cicReset]); if (reach.cpuPins) c.push(['fx', st.unsafe.looseCart]); c.push(['fx', st.unsafe.ramRot]); if (reach.vramRot !== false) c.push(['fx', st.unsafe.vramRot]); if (reach.cpuPins) c.push(['pins', st.unsafe.cpuPins]); if (reach.soundCpuLines > 0) c.push(['pins', st.unsafe.soundCpuPins]); if (reach.dmaLines > 0) c.push(['pins', st.unsafe.dmaPins]); }
    // the classification tags: an effect tagged unsafe by hand needs "include unsafe"; a disgraced one needs "include disgraced"
    const tagOf = (fx) => { const p = pathOf(fx, st); return p ? (catalog?.effects || []).find(e => e.path === p) : null; };
    return c.filter(([, fx]) => { const e = tagOf(fx); return !e || ((includeUnsafe || !e.unsafe || e.unsafeBuiltIn) && (includeDisgraced || !e.disgraced)); });
  }
  function turnOn(kind, fx, r, k, listKey) {
    const amt = 0.15 + 0.85 * k;
    if (kind === 'pins') { const n = 1 + Math.floor(r() * (1 + 3 * k)); for (let i = 0; i < n; i++) fx.address[Math.floor(r() * fx.address.length)] = 1 + Math.floor(r() * 3); fx.flicker = Math.round(r() * 60 * (1 - k)); return; }
    if (kind === 'bits') { const arr = fx[listKey]; const len = listKey === 'channels' ? (reach.channels || []).length : listKey === 'bits' && fx === settings.sprites?.attr ? (reach.attrNames || []).length : 5; for (let i = 0; i < Math.max(1, len); i++) arr[i] = r() < 0.5; if (!arr.some(Boolean)) arr[0] = true; }
    else fx.on = true;
    if ('rate' in fx) fx.rate = Math.round(10 + 85 * amt * r());
    if ('range' in fx) fx.range = Math.max(1, Math.round(2 + 30 * amt * r()));
    if ('duration' in fx) fx.duration = Math.round(5 + 120 * r());
    if ('width' in fx) fx.width = Math.max(1, Math.round(4 + 100 * amt * r()));
    if ('age' in fx) fx.age = Math.max(1, Math.round(15 + 85 * amt * r()));
    if ('columns' in fx) { fx.columns = 1 + Math.floor(r() * (1 + 4 * k)); fx.rows = r() < 0.3 ? 1 + Math.floor(r() * 2) : 0; fx.mode = Math.floor(r() * 3); fx.seed = 1 + Math.floor(r() * 998); }
    if ('decay' in fx) fx.decay = Math.round(10 + 60 * amt * r());
    if ('level' in fx) fx.level = Math.max(1, Math.round(5 + 60 * amt * r()));
    if ('freq' in fx) { fx.freq = Math.round(60 + 2000 * r() * r()); fx.wave = Math.floor(r() * 4); fx.mix = r() < 0.3 ? 1 : 0; }
    if ('pixels' in fx) fx.pixels = (r() < 0.5 ? -1 : 1) * (1 + Math.floor(r() * 4));
    if ('speed' in fx && !('depth' in fx)) fx.speed = (r() < 0.5 ? -1 : 1) * (1 + Math.floor(r() * 6));
    if ('strength' in fx) fx.strength = Math.max(1, Math.round(2 + 40 * amt * r()));
    if ('lifetime' in fx) fx.lifetime = Math.round(10 + 150 * r());
    if ('mode' in fx && 'posterize' in fx) { fx.mode = Math.floor(r() * 5); fx.invert = r() < 0.2; fx.channelSwap = r() < 0.2; fx.posterize = r() < 0.2 ? 2 + Math.floor(r() * 5) : 0; }
    if ('bits' in fx && typeof fx.bits === 'number') fx.bits = 1 + Math.floor(r() * 8 * amt);
    if ('value' in fx && 'duration' in fx) fx.value = fx === settings.sound?.duty ? Math.floor(r() * 4) : 1 + Math.floor(r() * 20);
  }
  function randomize() {
    const seed = (Math.random() * 4294967296) >>> 0, r = rng(seed), k = strength / 100;
    const keepOff = settings.groupsOff;
    settings = defaults(reach.cartAddressLines); settings.groupsOff = keepOff; settings.seed = seed;
    const c = candidates(settings);
    const n = Math.max(1, Math.round(1 + k * Math.min(10, c.length - 1)));
    for (let i = 0; i < n && c.length; i++) { const [kind, fx, lk] = c.splice(Math.floor(r() * c.length), 1)[0]; turnOn(kind, fx, r, k, lk); }
    record(`Roll #${seed.toString(16)} (strength ${strength})`);
  }
  function mutate() {
    const seed = (Math.random() * 4294967296) >>> 0, r = rng(seed), k = strength / 100;
    const nudge = (v, lo, hi) => Math.max(lo, Math.min(hi, Math.round(v + (r() * 2 - 1) * k * (hi - lo) * 0.5)));
    for (const [kind, fx, lk] of candidates(settings)) {
      const on = kind === 'pins' ? fx.address.some(Boolean) : kind === 'bits' ? fx[lk].some(Boolean) : fx.on;
      if (!on) { if (r() < k * 0.15) turnOn(kind, fx, r, k, lk); continue; }
      if (r() < k * 0.1) { if (kind === 'pins') fx.address.fill(0); else if (kind === 'bits') fx[lk].fill(false); else fx.on = false; continue; }
      if (kind === 'pins') { if (r() < k) fx.address[Math.floor(r() * fx.address.length)] = Math.floor(r() * 4); continue; }
      if ('rate' in fx) fx.rate = nudge(fx.rate, 0, 100);
      if ('range' in fx) fx.range = nudge(fx.range, 1, 64);
      if ('duration' in fx) fx.duration = nudge(fx.duration, 1, 600);
      if ('width' in fx) fx.width = nudge(fx.width, 1, 240);
      if ('strength' in fx) fx.strength = nudge(fx.strength, 1, 64);
    }
    settings.seed = seed;
    record(`Mutation #${seed.toString(16)} (strength ${strength})`);
  }
  function record(label) {
    history = history.slice(0, historyAt + 1);
    history.push({ label, settings: JSON.parse(JSON.stringify(settings)) });
    if (history.length > 20) history.shift();
    historyAt = history.length - 1;
    solo = null; render(); push(); paintHistory();
    status(label);
  }
  function stepHistory(d) {
    const i = historyAt + d;
    if (i < 0 || i >= history.length) return;
    historyAt = i; settings = fit(JSON.parse(JSON.stringify(history[i].settings)));
    solo = null; render(); push(); paintHistory(); status(history[i].label);
  }
  function paintHistory() {
    const n = $('histLabel'); if (n) n.textContent = history.length ? `${historyAt + 1} / ${history.length}` : '—';
    const b = $('histBack'), f = $('histFwd'); if (b) b.disabled = historyAt <= 0; if (f) f.disabled = historyAt >= history.length - 1;
  }
  document.addEventListener('DOMContentLoaded', () => {
    const anchor = $('stateRow'); if (!anchor) return;
    const out = el('output', {}, String(strength));
    const menu = el('div', { class: 'popmenu', role: 'menu', hidden: true },
      el('label', { class: 'unsafeopt' }, el('input', { type: 'checkbox', checked: includeUnsafe ? '' : null, onchange: (e) => { includeUnsafe = e.target.checked; } }), 'Include unsafe effects in rolls'),
      el('label', { class: 'unsafeopt' }, el('input', { type: 'checkbox', checked: includeDisgraced ? '' : null, onchange: (e) => { includeDisgraced = e.target.checked; } }), 'Include disgraced effects in rolls'));
    const more = el('button', { type: 'button', title: 'Roll options', 'aria-haspopup': 'menu', 'aria-expanded': 'false' }, '\u25BE');
    anchor.before(
      el('div', { class: 'row roll' },
        el('button', { type: 'button', onclick: randomize, title: 'A new random butcher' }, 'Randomize'),
        el('button', { type: 'button', onclick: mutate, title: 'Nudge the current settings' }, 'Mutate'),
        el('button', { id: 'histBack', type: 'button', onclick: () => stepHistory(-1), title: 'Previous roll', disabled: true }, '\u25C0'),
        el('span', { id: 'histLabel', class: 'hist' }, '\u2014'),
        el('button', { id: 'histFwd', type: 'button', onclick: () => stepHistory(1), title: 'Next roll', disabled: true }, '\u25B6'),
        el('label', { class: 'mini', title: 'Randomize strength: few gentle effects at low, many strong ones at high' }, out,
          el('input', { type: 'range', min: 0, max: 100, step: 1, value: strength, 'aria-label': 'Randomize strength', oninput: (e) => { strength = Number(e.target.value); out.textContent = String(strength); } })),
        more),
      menu);
    popMenu(more, menu);
  });

  // a small menu under its button: opens on click, closes on a click elsewhere or Escape
  function popMenu(btn, menu) {
    const close = () => { menu.hidden = true; btn.setAttribute('aria-expanded', 'false'); };
    btn.addEventListener('click', (e) => { e.stopPropagation(); menu.hidden = !menu.hidden; btn.setAttribute('aria-expanded', String(!menu.hidden)); });
    document.addEventListener('pointerdown', (e) => { if (!menu.hidden && !menu.contains(e.target) && e.target !== btn) close(); }, true);
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && !menu.hidden) close(); });
  }
})();
