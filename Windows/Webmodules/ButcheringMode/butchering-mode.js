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
    { code: 3, short: 'B', name: 'bridged to the next line' }
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
        wobble: { on: false, depth: 30, speed: 8 }
      },
      bands: (() => { const b = () => ({ on: false, rate: 25, width: 30, strength: 8, lifetime: 30 }); return { shift: b(), slip: b(), patternSwap: b(), nametableSwap: b(), bgOff: b(), spritesOff: b(), spriteHeight: b(), bend: b(), extra1: b(), extra2: b(), extra3: b() }; })(),
      ram: { on: false, rate: 25, step: 1, lifetime: 60, maxProbes: 3 },
      palette: { on: false, mode: 0, invert: false, channelSwap: false, posterize: 0, cycleFrames: 0 },
      ciramA10: 0, ciramFlicker: 0,
      videoDac: { address: [], data: new Array(24).fill(0), flicker: 0, bendWrites: false },
      paletteBus: { address: new Array(reach?.console === 'snes' ? 8 : 5).fill(0), data: new Array(reach?.console === 'snes' ? 15 : 6).fill(0), flicker: 0, bendWrites: false },
      unsafe: { cpuPins: { address: new Array(reach?.cpuAddressLines || 15).fill(0), data: new Array(reach?.cpuDataLines || 8).fill(0), flicker: 0, bendWrites: false },
                soundCpuPins: { address: new Array(reach?.soundCpuLines || 0).fill(0), data: new Array(8).fill(0), flicker: 0, bendWrites: false },
                ramRot: { on: false, rate: 25, bits: 1 }, vramRot: { on: false, rate: 25, bits: 1 },
                padLines: new Array(15).fill(0), padFlicker: 0,
                soundRamRot: { on: false, rate: 25, bits: 1 }, echoAbuse: { on: false, rate: 25, duration: 120 },
                ghostNmi: { on: false, rate: 25, duration: 1 }, dropNmi: { on: false, rate: 25, duration: 10 }, stuckIrq: { on: false, rate: 25, duration: 10 },
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
    $('allOff').addEventListener('click', () => { settings = defaults(reach.cartAddressLines); render(); push(); });
    $('presetSave').addEventListener('click', savePreset);
    $('presetLoad').addEventListener('click', loadPreset);
    $('presetDelete').addEventListener('click', deletePreset);
    start();
  });

  async function start() {
    try {
      const valid = await api.timejump.validateRom();
      if (!valid?.valid) { status('Load a game first.', true); await back(); return; }
    } catch { /* validation is advisory: the reach call below decides */ }

    const r = await api.butcher.getReach().catch(() => null);
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
    if (off.palette) { e.palette = d.palette; e.videoDac = d.videoDac; e.paletteBus = d.paletteBus; }
    if (off.picture) { e.ciramA10 = 0; }
    if (off.unsafe) { e.unsafe = d.unsafe; e.consolePins.bendWrites = false; e.cartPins.bendWrites = false; }
    delete e.groupsOff;
    return e;
  }

  // A section header with its group switch (clicking the switch does not fold the section).
  function groupSummary(title, key) {
    settings.groupsOff = settings.groupsOff || {};
    const box = el('input', { type: 'checkbox', checked: !settings.groupsOff[key], title: 'Group on / off (keeps its settings)',
      onclick: (e) => e.stopPropagation(),
      onchange: (e) => { settings.groupsOff[key] = !e.target.checked; push(); } });
    const sm = el('summary', {}, box, ' ', title);
    queueMicrotask(() => { if (sm.parentElement) sm.parentElement.dataset.tab = key; });
    return sm;
  }

  function push() {
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

  function slider(label, obj, key, min, max, step, onChange) {
    const out = el('output', {}, String(obj[key]));
    const input = el('input', {
      type: 'range', min, max, step, value: obj[key], 'aria-label': label,
      oninput: (e) => { obj[key] = Number(e.target.value); out.textContent = String(obj[key]); (onChange || push)(); }
    });
    return el('div', { class: 'slider' }, el('span', {}, label), input, out);
  }

  function check(label, obj, key) {
    const l = el('label', { class: 'name' },
      el('input', { type: 'checkbox', checked: !!obj[key], onchange: (e) => { obj[key] = e.target.checked; push(); } }), label);
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
    return el('button', { type: 'button', class: 'solo', title: 'Solo: only this effect runs (click again to un-solo)',
      onclick: (e) => {
        e.preventDefault(); e.stopPropagation();
        const path = pathOf(obj);
        solo = solo && solo.path === path ? null : { path, label };
        paintSolo(); push();
        status(solo ? `Solo: ${label}` : 'Solo off: every effect back.');
      } }, 'S');
  }
  function paintSolo() {
    document.querySelectorAll('button.solo').forEach(b => b.classList.remove('on'));
    if (!solo) return;
    document.querySelectorAll('button.solo').forEach(b => { if (b.closest('.namerow')?.querySelector('label')?.textContent === solo.label) b.classList.add('on'); });
  }
  function getPath(o, path) { return path.split('.').reduce((x, k) => x?.[k], o); }
  function setPath(o, path, v) { const ks = path.split('.'); const last = ks.pop(); const t = ks.reduce((x, k) => x[k], o); t[last] = v; }

  function bits(names, arr) {
    return el('div', { class: 'bits' }, ...names.map((n, i) => el('label', {},
      el('input', { type: 'checkbox', checked: !!arr[i], onchange: (e) => { arr[i] = e.target.checked; push(); } }), n)));
  }

  function pinGrid(prefix, arr) {
    const grid = el('div', { class: 'pins' });
    arr.forEach((code, i) => {
      const b = el('button', { type: 'button', class: `pin f${code}` });
      const paint = () => {
        const f = FAULTS[arr[i]];
        b.className = `pin f${f.code}`;
        b.replaceChildren(el('span', {}, f.short), `${prefix}${i}`);
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
    const body = el('fieldset', { class: available ? '' : 'disabled' },
      el('div', { class: 'sub' }, 'Address lines'), pinGrid('A', group.address),
      el('div', { class: 'sub' }, 'Data lines'), pinGrid('D', group.data),
      slider('Flicker', group, 'flicker', 0, 100, 1));
    return el('details', { open: true }, groupSummary(title, 'picture'),
      el('div', { class: 'why' }, why),
      available ? null : el('div', { class: 'off' }, unavailableText),
      el('div', { class: 'legend' }, '· connected   L stuck low   H stuck high   B bridged to the next line'),
      body);
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
  const TABS = [['picture', 'Picture'], ['sprites', 'Sprites'], ['sound', 'Sound'], ['bands', 'Bands'], ['palette', 'Palette'], ['ram', 'RAM'], ['unsafe', 'Unsafe']];
  let activeTab = (() => { try { return localStorage.getItem('butcher.tab') || 'picture'; } catch { return 'picture'; } })();
  let query = '';
  function tabify() {
    const g = $('groups');
    let key = 'picture';
    for (const c of g.children) { const sm = c.querySelector(':scope > summary [data-group]') || c.querySelector(':scope > summary'); if (c.tagName === 'DETAILS') { key = c.dataset.tab || key; c.open = true; } c.dataset.tab = key; }
    let bar = $('tabs');
    if (!bar) {
      bar = el('div', { id: 'tabs', class: 'tabs' });
      const search = el('input', { id: 'search', type: 'search', placeholder: 'Search effects…', 'aria-label': 'Search effects',
        oninput: (e) => { query = e.target.value.trim().toLowerCase(); applyTabs(); } });
      $('groups').before(bar, search);
    }
    bar.replaceChildren(...TABS.map(([k, n]) => el('button', { type: 'button', class: 'tab' + (k === activeTab ? ' on' : '') + (k === 'unsafe' ? ' red' : ''),
      onclick: () => { activeTab = k; try { localStorage.setItem('butcher.tab', k); } catch { } $('search').value = ''; query = ''; tabify(); } }, n)));
    applyTabs();
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

  function render() {
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
        slider('Depth', so.wobble, 'depth', 1, 100, 1), slider('Speed', so.wobble, 'speed', 1, 80, 1))));

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
      (reach.console === 'nes' && reach.ppuCore === 'Fix') || reach.console === 'snes' ? el('div', { class: 'fx' },
        el('div', { class: 'namerow' }, el('label', { class: 'name' }, 'Palette RAM bus'), soloBtn(settings.paletteBus, 'Palette RAM bus')),
        el('div', { class: 'why' }, 'The palette chip’s own lines: the picture reads the wrong colour entry or bent colour bits (the game’s palette stays intact).'),
        pinGrid('A', settings.paletteBus.address), pinGrid('D', settings.paletteBus.data), slider('Flicker', settings.paletteBus, 'flicker', 0, 100, 1)) : null));

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
      el('div', { class: 'legend' }, '· connected   L stuck low   H stuck high   B bridged to the next line'),
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
    unsafeBody.push(el('div', { class: 'sub' }, 'Interrupt lines'),
      eventFx('Ghost NMI', 'An extra vertical-blank interrupt: the game runs part of a frame twice.', un.ghostNmi),
      eventFx('Dropped NMI', 'Vertical-blank interrupts swallowed: frames freeze or skip.', un.dropNmi),
      eventFx('Stuck IRQ', 'The IRQ line held down: the game’s interrupt handler runs over and over.', un.stuckIrq));
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

    const rm = settings.ram;

    g.append(el('details', { open: true }, groupSummary('RAM nudges', 'ram'),
      el('div', { class: 'why' }, 'Learns which RAM bytes are live game variables (positions, timers, speeds) and nudges them a step at a time, only inside the range the game itself used. Pointers and flags are filtered out.'),
      el('div', { class: 'fx' }, check('Nudge variables', rm, 'on'),
        slider('Rate', rm, 'rate', 0, 100, 1), slider('Step', rm, 'step', 1, 8, 1),
        slider('Frames', rm, 'lifetime', 1, 600, 1), slider('At once', rm, 'maxProbes', 1, 8, 1),
        el('div', { id: 'ramStats', class: 'why' }, 'Profiler: learning…'))));
    queueMicrotask(() => { tabify(); paintSolo(); });
  }

  // ---------------------------------------------------------------- randomize / mutate
  // Seeded (mulberry32), so a roll can be saved with its seed and come back the same. One strength slider: few gentle
  // effects at low, many strong ones at high. Unsafe effects only when "include unsafe" is ticked.
  let strength = 35, includeUnsafe = false, history = [], historyAt = -1;
  function rng(seed) { let a = seed >>> 0; return () => { a = (a + 0x6D2B79F5) >>> 0; let t = a; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }
  function candidates(st) {
    const c = [];
    const sp = st.sprites, so = st.sound, bd = st.bands;
    if (reach.spritesDma) for (const k of ['xJitter', 'yJitter', 'tile', 'groupLag', 'clone', 'fieldSwap', 'forceBit']) c.push(['fx', sp[k]]);
    if (reach.spritesDma && (reach.attrNames || []).length) c.push(['bits', sp.attr, 'bits']);
    if (reach.sound !== false) { c.push(['bits', so.bitFlip, 'bits'], ['fx', so.nudge], ['bits', so.drops, 'channels'], ['fx', so.stutter], ['fx', so.drone], ['fx', so.detune]); if (isNesReach()) c.push(['fx', so.duty]); }
    if (reach.bands) { for (const k of (reach.bandKinds || [])) if (bd[k]) c.push(['fx', bd[k]]); (reach.extraBands || []).forEach((_, i) => c.push(['fx', bd['extra' + (i + 1)]])); }
    c.push(['fx', st.palette], ['fx', st.sound.wobble], ['pins', st.sound.busPins]);
    if (reach.ram) c.push(['fx', st.ram]);
    if (reach.consolePins) c.push(['pins', st.consolePins]);
    if (isNesReach() && reach.cartPins) c.push(['pins', st.cartPins]);
    if (includeUnsafe) { c.push(['fx', st.unsafe.ramRot]); if (reach.vramRot !== false) c.push(['fx', st.unsafe.vramRot]); if (reach.cpuPins) c.push(['pins', st.unsafe.cpuPins]); if (reach.soundCpuLines > 0) c.push(['pins', st.unsafe.soundCpuPins]); if (reach.dmaLines > 0) c.push(['pins', st.unsafe.dmaPins]); }
    return c;
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
    const head = document.querySelector('.head'); if (!head) return;
    const out = el('output', {}, String(strength));
    head.append(
      el('div', { class: 'row roll' },
        el('button', { type: 'button', onclick: randomize, title: 'A new random butcher' }, 'Randomize'),
        el('button', { type: 'button', onclick: mutate, title: 'Nudge the current settings' }, 'Mutate'),
        el('button', { id: 'histBack', type: 'button', onclick: () => stepHistory(-1), title: 'Previous roll', disabled: true }, '\u25C0'),
        el('span', { id: 'histLabel', class: 'why' }, '\u2014'),
        el('button', { id: 'histFwd', type: 'button', onclick: () => stepHistory(1), title: 'Next roll', disabled: true }, '\u25B6')),
      el('div', { class: 'slider' }, el('span', {}, 'Strength'),
        el('input', { type: 'range', min: 0, max: 100, step: 1, value: strength, 'aria-label': 'Randomize strength', oninput: (e) => { strength = Number(e.target.value); out.textContent = String(strength); } }), out),
      el('label', { class: 'name unsafeopt' }, el('input', { type: 'checkbox', onchange: (e) => { includeUnsafe = e.target.checked; } }), 'Include unsafe effects in rolls'));
  });
})();
