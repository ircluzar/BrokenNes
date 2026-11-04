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
      consolePins: { address: new Array(consoleAddressLines).fill(0), data: new Array(8).fill(0), flicker: 0, bendWrites: false },
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
        drops: { channels: [false, false, false, false, false], rate: 25, duration: 30 },
        stutter: ev(), drone: ev(),
        duty: { on: false, rate: 25, duration: 30, value: 0 },
        detune: { on: false, rate: 25, duration: 30, value: 1 }
      },
      bands: (() => { const b = () => ({ on: false, rate: 25, width: 30, strength: 8, lifetime: 30 }); return { shift: b(), slip: b(), patternSwap: b(), nametableSwap: b(), bgOff: b(), spritesOff: b(), spriteHeight: b(), bend: b() }; })(),
      ram: { on: false, rate: 25, step: 1, lifetime: 60, maxProbes: 3 }
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
    if (settings?.ram?.on) {
      const st = await api.butcher.getStats().catch(() => null);
      const n = document.getElementById('ramStats');
      if (st?.success && n) n.textContent = `Profiler: ${st.watches} watched, ${st.confirmed} confirmed variables, ${st.nudging} being nudged`;
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

  function push() {
    clearTimeout(syncTimer);
    return new Promise(res => {
      syncTimer = setTimeout(async () => {
        const r = await api.butcher.setSettings(settings).catch(() => null);
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
    for (const p of (r?.presets || [])) {
      const o = document.createElement('option');
      o.value = p.name; o.textContent = p.name;
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
    $('presetList').value = name;
  }
  async function loadPreset() {
    const sel = $('presetList');
    const p = (sel._presets || []).find(x => x.name === sel.value);
    if (!p) return;
    settings = fit(p.settings);
    render();
    await push();
    status(`Preset "${p.name}" loaded.`);
  }
  async function deletePreset() {
    const name = $('presetList').value;
    if (!name) return;
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
    return el('label', { class: 'name' },
      el('input', { type: 'checkbox', checked: !!obj[key], onchange: (e) => { obj[key] = e.target.checked; push(); } }), label);
  }

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
      slider('Flicker', group, 'flicker', 0, 100, 1),
      el('div', { class: 'fx' }, check('Bend writes too', group, 'bendWrites'), el('div', { class: 'why' }, writesText)));
    return el('details', { open: true }, el('summary', {}, title),
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

  function render() {
    const g = $('groups');
    g.replaceChildren();
    const sp = settings.sprites, so = settings.sound;

    if (reach.consolePinsPartial) g.append(el('div', { class: 'off' }, `The ${reach.ppuCore} PPU core fetches tile graphics through its own fast path: console pins reach its nametables and $2007, not every tile fetch (cartridge pins reach them).`));
    const isNes = (reach.console || 'nes') === 'nes';
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

    const spritesWhy = reach.spritesDma ? (reach.sprites2004 || !isNes ? '' : 'Single $2004 writes are not filtered on this PPU core (DMA is).')
      : `Not reachable on the ${reach.ppuCore} PPU core.`;
    g.append(el('details', { open: true }, el('summary', {}, 'Sprites'),
      el('div', { class: 'why' }, 'Bytes on their way into sprite memory. The game resends it every frame, so a hit lasts one frame.'),
      spritesWhy ? el('div', { class: 'off' }, spritesWhy) : null,
      el('fieldset', { class: reach.spritesDma ? '' : 'disabled' },
        el('div', { class: 'sub' }, 'Classic'),
        rateFx('X jitter', 'Sprites shake sideways and split apart.', sp.xJitter, [slider('Range px', sp.xJitter, 'range', 1, 64, 1)]),
        rateFx('Y jitter', 'Hidden sprites stay hidden.', sp.yJitter, [slider('Range px', sp.yJitter, 'range', 1, 64, 1)]),
        rateFx('Tile ±1', 'The neighbouring tile: wrong body part or animation frame.', sp.tile),
        el('div', { class: 'fx' }, el('label', { class: 'name' }, 'Attribute flips'),
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

    g.append(el('details', { open: true }, el('summary', {}, 'Sound'),
      el('div', { class: 'why' }, isNes ? 'Writes on their way into the APU. DMC samples are left alone.' : 'Writes on their way into the sound chip. Power and wave RAM are left alone.'),
      el('div', { class: 'sub' }, 'Classic'),
      el('div', { class: 'fx' }, el('label', { class: 'name' }, 'Bit flips (pitch / volume / envelope bits)'),
        bits(['Bit 0', 'Bit 1', 'Bit 2', 'Bit 3', 'Bit 4'], so.bitFlip.bits), slider('Rate', so.bitFlip, 'rate', 0, 100, 1)),
      rateFx('Detune ±1', 'Small pitch nudges on period writes.', so.nudge),
      el('div', { class: 'fx' }, el('label', { class: 'name' }, 'Channel drop-outs'),
        el('div', { class: 'why' }, 'A channel is switched off, then back on; it stays silent until its next note.'),
        bits(reach.channels || ['Pulse 1', 'Pulse 2', 'Triangle', 'Noise', 'DMC'], so.drops.channels),
        slider('Rate', so.drops, 'rate', 0, 100, 1), slider('Frames', so.drops, 'duration', 1, 600, 1)),
      el('div', { class: 'sub' }, 'Organic (events)'),
      eventFx('Stutter', 'Older register writes are replayed: notes and arpeggios repeat.', so.stutter),
      eventFx('Drone', 'Channels forced to constant, full volume.', so.drone),
      eventFx('Duty force', 'Both pulse channels forced to one duty.', so.duty, [slider('Duty', so.duty, 'value', 0, 3, 1)]),
      eventFx('Detune', 'Period low byte XORed: wrong notes.', so.detune, [slider('Amount', so.detune, 'value', 1, 255, 1)])));

    const bd = settings.bands;
    const bandFx = (label, why, fx, strengthLabel) => el('div', { class: 'fx' }, check(label, fx, 'on'), el('div', { class: 'why' }, why),
      slider('Rate', fx, 'rate', 0, 100, 1), slider('Width', fx, 'width', 1, 240, 1),
      strengthLabel ? slider(strengthLabel, fx, 'strength', 1, 64, 1) : null, slider('Frames', fx, 'lifetime', 1, 600, 1));
    g.append(el('details', { open: true }, el('summary', {}, 'Bands (scanlines)'),
      el('div', { class: 'why' }, 'Timed bands of lines with one effect each; up to six at once, drifting a line now and then.'),
      reach.bands ? null : el('div', { class: 'off' }, isNes ? `Bands need the PPU FIX core (now ${reach.ppuCore}).` : 'Bands are NES-only for now (PPU FIX).'),
      el('fieldset', { class: reach.bands ? '' : 'disabled' },
        bandFx('Shift', 'The band slides sideways.', bd.shift, 'Pixels'),
        bandFx('Slip', 'The picture below the band drops by some rows.', bd.slip, 'Rows'),
        bandFx('Pattern swap', 'The band draws from the other pattern table.', bd.patternSwap),
        bandFx('Nametable swap', 'The band shows the neighbouring screen.', bd.nametableSwap),
        bandFx('Background off', 'A black band.', bd.bgOff),
        bandFx('Sprites off', 'Sprites vanish inside the band.', bd.spritesOff),
        bandFx('Sprite height', 'Sprites switch 8 / 16 pixels tall for the whole frame.', bd.spriteHeight),
        bandFx('Bend', 'Address lines grounded inside the band only.', bd.bend, 'Lines'))));

    const rm = settings.ram;
    g.append(el('details', { open: true }, el('summary', {}, 'RAM nudges'),
      el('div', { class: 'why' }, 'Learns which RAM bytes are live game variables (positions, timers, speeds) and nudges them a step at a time, only inside the range the game itself used. Pointers and flags are filtered out.'),
      el('div', { class: 'fx' }, check('Nudge variables', rm, 'on'),
        slider('Rate', rm, 'rate', 0, 100, 1), slider('Step', rm, 'step', 1, 8, 1),
        slider('Frames', rm, 'lifetime', 1, 600, 1), slider('At once', rm, 'maxProbes', 1, 8, 1),
        el('div', { id: 'ramStats', class: 'why' }, 'Profiler: learning…'))));
  }
})();
