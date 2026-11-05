// Butchering mode's effects as controllers (controller standard C3, docs/projects/controller-standard-plan.md).
// Every effect in the settings tree becomes parameters (BNControls.param) and a small default faceplate layout, by the shape of its
// settings: on / off -> toggle, numbers -> knobs (ranges, units and tapers by property name), modes -> stepped knobs, bit lists ->
// toggles named after the console's channels / attributes, pin groups -> fault count, flicker, bend writes and Clear.
//
//   BNEffects.available(path, reach)                     -> true when this console / core can do it
//   BNEffects.build(path, ctx) -> { title, params, layout }   ctx: { settings, reach, entry (catalog), onChange(), defaults() }
(() => {
  const get = (o, path) => path.split('.').reduce((a, k) => (a == null ? a : a[k]), o);

  // ranges by property name: [min, max, step, unit, taper, short label]
  const RANGE = {
    rate: [0, 100, 1, '%', 'linear', 'Rate'], duration: [1, 600, 1, 'fr', 'log', 'Frames'], range: [1, 64, 1, 'px', 'linear', 'Range'],
    width: [1, 120, 1, 'ln', 'linear', 'Width'], strength: [0, 64, 1, '', 'linear', 'Strength'], lifetime: [1, 600, 1, 'fr', 'log', 'Life'],
    bits: [1, 8, 1, 'bits', 'linear', 'Bits'], flicker: [0, 100, 1, '%', 'linear', 'Flicker'], depth: [1, 100, 1, '%', 'linear', 'Depth'],
    speed: [1, 80, 1, '', 'linear', 'Speed'], age: [1, 100, 1, '%', 'linear', 'Age'], level: [0, 100, 1, '%', 'linear', 'Level'],
    freq: [20, 8000, 1, 'Hz', 'log', 'Freq'], from: [20, 2000, 1, 'Hz', 'log', 'From'], to: [200, 16000, 10, 'Hz', 'log', 'To'],
    seconds: [1, 30, 1, 's', 'linear', 'Seconds'], periodFrames: [10, 600, 1, 'fr', 'log', 'Every'], decay: [0, 100, 1, '%', 'linear', 'RAM lost'],
    minutes: [1, 60, 1, 'min', 'linear', 'Minutes'], rows: [0, 16, 1, '', 'linear', 'Rows'], columns: [0, 16, 1, '', 'linear', 'Columns'],
    seed: [1, 999, 1, '', 'linear', 'Lines'], offset: [1, 32, 1, 'px', 'linear', 'Offset'], pixels: [-8, 8, 1, 'px', 'linear', 'Pixels'],
    step: [1, 8, 1, '', 'linear', 'Step'], maxProbes: [1, 8, 1, '', 'linear', 'At once'], value: [0, 15, 1, '', 'linear', 'Value'],
    posterize: [0, 8, 1, '', 'linear', 'Poster.'], cycleFrames: [0, 240, 1, 'fr', 'linear', 'Cycle'], field: [0, 3, 1, '', 'linear', 'Field'],
    bit: [0, 7, 1, '', 'linear', 'Bit'],
  };
  // modes: step names by "path.property" (or by property for every effect)
  const STEPS = {
    'lcd.mode': ['dark', 'light', 'flicker'], 'vramChip.chip': ['low byte', 'high byte'], 'vramChip.mode': ['stuck low', 'stuck high', 'floating'],
    'unsafe.padTh.mode': ['connected', 'stuck low', 'stuck high', 'floating'], 'palette.mode': ['hue +60', 'hue +120', 'complement', 'hue +240', 'hue + bright'],
    'sound.inject.wave': ['sine', 'square', 'saw', 'noise'], 'sound.inject.mix': ['added', 'ring mod'], 'sound.hum.hz': [{ value: 50, label: '50 Hz' }, { value: 60, label: '60 Hz' }],
    'ciramA10.value': ['connected', 'stuck low', 'stuck high', 'inverted'],
  };
  const SKIP = new Set(['on', 'address', 'data', 'bendWrites', 'bits', 'channels']);

  // which console / core can do an effect (the strip view's own rules)
  function available(path, reach) {
    if (!reach) return false;
    const c = reach.console || 'nes', nes = c === 'nes';
    const band = path.startsWith('bands.') ? path.slice(6) : null;
    if (band) return band.startsWith('extra') ? (reach.extraBands || []).length >= +band.slice(5) : (reach.bandKinds || []).includes(band) && reach.bands !== false;
    if (path.startsWith('sprites.')) return !!reach.spritesDma;
    if (path.startsWith('sound.') && reach.sound === false && !['sound.inject', 'sound.hum', 'sound.sweep', 'sound.caps', 'sound.wobble', 'sound.dacPins'].includes(path)) return false;
    switch (path) {
      case 'cartPins': return nes && !!reach.cartPins;
      case 'ciramA10': return nes;
      case 'consolePins': return !!reach.consolePins;
      case 'lcd': return !!reach.lcd;
      case 'vramChip': return c === 'snes' || c === 'wm';
      case 'paletteBus': return !nes || reach.ppuCore === 'Fix';
      case 'sound.duty': return c === 'nes' || c === 'gb';
      case 'sound.fmUnplug': return c === 'sms' && (reach.channels || []).length > 4;
      case 'sound.soundBus': case 'sound.busPins': return (reach.soundBusLines ?? 5) > 0;
      case 'sound.samplePins': return (reach.sampleLines ?? 15) > 0;
      case 'unsafe.cpuPins': return !!reach.cpuPins;
      case 'unsafe.soundCpuPins': return (reach.soundCpuLines || 0) > 0;
      case 'unsafe.dmaPins': return (reach.dmaLines || 0) > 0;
      case 'unsafe.coproPins': return !!reach.coproName;
      case 'unsafe.cicReset': return !!reach.cic;
      case 'unsafe.looseCart': return !!reach.cpuPins;
      case 'unsafe.dramRot': return c === 'snes' || c === 'md' || c === 'wm';
      case 'unsafe.linkNoise': return c === 'gb';
      case 'unsafe.padTh': case 'unsafe.z80BusHold': case 'unsafe.z80ResetHold': return c === 'md';
      case 'unsafe.soundRamRot': return !!reach.soundRam;
      case 'unsafe.echoAbuse': return c === 'snes';
      case 'unsafe.vramRot': return reach.vramRot !== false;
      case 'ram': return !!reach.ram;
      default: return true;
    }
  }

  function bandName(path, reach) {
    const k = path.slice(6);
    if (k.startsWith('extra')) return ((reach.extraBands || [])[+k.slice(5) - 1] || '').split('|')[0] || null;
    return null;
  }
  function bitNames(path, reach, n) {
    if (path === 'sprites.attr') return reach.attrNames || [];
    if (path === 'sound.drops') return reach.channels || [];
    if (path === 'sound.bitFlip') return ['b0', 'b1', 'b2', 'b3', 'b4'];
    return Array.from({ length: n }, (_, i) => 'b' + i);
  }

  function build(path, ctx) {
    const { settings, reach, entry } = ctx;
    const changed = () => ctx.onChange && ctx.onChange(path);
    const params = [], controls = [];
    const add = (p, c) => { params.push(BNControls.param(p)); controls.push(c); };
    const base = path.replace(/\./g, '_');
    const title = bandName(path, reach) || (entry && entry.name) || path;

    // the CIRAM A10 pin is a number at the top of the tree, with its flicker beside it
    if (path === 'ciramA10') {
      add({ id: base, label: 'A10', steps: STEPS['ciramA10.value'], default: 0, get: () => settings.ciramA10 || 0, set: (v) => { settings.ciramA10 = v; changed(); } }, { type: 'knob', param: base, label: 'A10' });
      add({ id: base + '_f', label: 'Flicker', min: 0, max: 100, step: 1, unit: '%', default: 0, get: () => settings.ciramFlicker || 0, set: (v) => { settings.ciramFlicker = v; changed(); } }, { type: 'knob', param: base + '_f' });
      return finish(title, params, controls);
    }
    // the controller lines: a list of codes per button
    if (path === 'unsafe.padLines') {
      const u = settings.unsafe;
      const faults = () => (u.padLines || []).filter(Boolean).length;
      add({ id: base + '_n', label: 'Faults', default: 0, get: faults, format: (v) => `${v} wire${v === 1 ? '' : 's'}` }, { type: 'display', param: base + '_n', label: 'Faulted', w: 2 });
      add({ id: base + '_f', label: 'Flicker', min: 0, max: 100, step: 1, unit: '%', default: 0, get: () => u.padFlicker || 0, set: (v) => { u.padFlicker = v; changed(); } }, { type: 'knob', param: base + '_f' });
      controls.push({ type: 'button', id: base + '_rnd', label: 'Random', glyph: '?', action: () => { const a = u.padLines; a.fill(0); a[Math.floor(Math.random() * a.length)] = 1 + Math.floor(Math.random() * 3); changed(); } });
      controls.push({ type: 'button', id: base + '_clr', label: 'Clear', glyph: '×', action: () => { (u.padLines || []).fill(0); changed(); } });
      return finishPins(title, params, controls);
    }

    const obj = get(settings, path);
    if (obj == null || typeof obj !== 'object') return finish(title, params, controls);

    // a pin group: how many lines are faulted, the flicker, bend writes, and quick actions (the strip view has the pin grid)
    if (Array.isArray(obj.address) && Array.isArray(obj.data)) {
      const faults = () => obj.address.filter(Boolean).length + obj.data.filter(Boolean).length;
      add({ id: base + '_n', label: 'Faults', default: 0, get: faults, format: (v) => `${v} line${v === 1 ? '' : 's'}` }, { type: 'display', param: base + '_n', label: 'Faulted', w: 2 });
      add({ id: base + '_f', label: 'Flicker', min: 0, max: 100, step: 1, unit: '%', default: 0, get: () => obj.flicker || 0, set: (v) => { obj.flicker = v; changed(); } }, { type: 'knob', param: base + '_f' });
      if (!path.startsWith('sound.dac') && !path.startsWith('videoDac')) add({ id: base + '_w', label: 'Bend writes', short: 'Writes', default: false, get: () => !!obj.bendWrites, set: (v) => { obj.bendWrites = v; changed(); } }, { type: 'toggle', param: base + '_w', label: 'Writes' });
      controls.push({ type: 'button', id: base + '_rnd', label: 'Random', glyph: '?', action: () => { const all = [...obj.address.map((_, i) => ['address', i]), ...obj.data.map((_, i) => ['data', i])]; if (!all.length) return; const [k, i] = all[Math.floor(Math.random() * all.length)]; obj[k][i] = 1 + Math.floor(Math.random() * 4); changed(); } });
      controls.push({ type: 'button', id: base + '_clr', label: 'Clear', glyph: '×', action: () => { obj.address.fill(0); obj.data.fill(0); changed(); } });
      return finishPins(title, params, controls);
    }

    // the switch first
    if ('on' in obj) add({ id: base + '_on', label: 'On', default: false, get: () => !!obj.on, set: (v) => { obj.on = v; changed(); } }, { type: 'toggle', param: base + '_on', label: 'On' });
    // bit / channel lists: a toggle per entry the console names
    for (const key of ['bits', 'channels']) {
      if (!Array.isArray(obj[key])) continue;
      const names = bitNames(path, reach, obj[key].length);
      names.slice(0, Math.min(8, obj[key].length)).forEach((nm, i) => {
        const id = `${base}_${key}${i}`;
        add({ id, label: nm, short: nm.slice(0, 7), default: false, get: () => !!obj[key][i], set: (v) => { obj[key][i] = v; changed(); } }, { type: 'toggle', param: id, label: nm.slice(0, 7) });
      });
    }
    // numbers and booleans
    for (const [k, v] of Object.entries(obj)) {
      if (SKIP.has(k)) continue;
      const id = `${base}_${k}`;
      const steps = STEPS[`${path}.${k}`];
      if (typeof v === 'boolean') { add({ id, label: k, default: false, get: () => !!obj[k], set: (x) => { obj[k] = x; changed(); } }, { type: 'toggle', param: id, label: k.slice(0, 7) }); continue; }
      if (typeof v !== 'number') continue;
      if (steps) { const st = steps.map((s, i) => (typeof s === 'object' ? s : { value: i, label: s })); add({ id, label: k, steps: st, default: st[0].value, get: () => obj[k], set: (x) => { obj[k] = x; changed(); } }, { type: 'knob', param: id, label: k === 'mode' ? 'Mode' : k[0].toUpperCase() + k.slice(1, 7) }); continue; }
      const r = RANGE[k] || [0, 100, 1, '', 'linear', k];
      add({ id, label: r[5], min: r[0], max: r[1], step: r[2], unit: r[3], taper: r[4], bipolar: r[0] < 0, default: v, get: () => obj[k], set: (x) => { obj[k] = x; changed(); } }, { type: 'knob', param: id, label: r[5] });
    }
    return finish(title, params, controls);
  }

  const actionsOf = (controls) => Object.fromEntries(controls.filter(c => c.action).map(c => [c.id, c.action]));

  // a pin group: two frames side by side, full height. Faults: the read-out with the one-shot buttons that change it under it;
  // Bend: the settings that stay (flicker over bend writes).
  function finishPins(title, params, controls) {
    const by = (pred) => controls.find(pred);
    const at = (c, x, y, w) => c && Object.assign({}, c, { x, y, w: w || 1, h: 1 });
    const placed = [
      at(by(c => c.type === 'display'), 0, 0, 2),
      at(by(c => c.id && c.id.endsWith('_rnd')), 0, 1),
      at(by(c => c.id && c.id.endsWith('_clr')), 1, 1),
      at(by(c => c.type === 'knob'), 2, 0),
      at(by(c => c.type === 'toggle'), 2, 1),
    ].filter(Boolean);
    const sections = [{ title: 'Faults', x: 0, y: 0, w: 2, h: 2 }, { title: 'Bend', x: 2, y: 0, w: 1, h: 2 }];
    return { title, params, layout: { title, cols: 3, rows: 2, sections, controls: placed }, actions: actionsOf(controls) };
  }

  // a default layout: controls on a grid three cells wide (displays take two). An On switch keeps column 0 to itself, so it is
  // in the same place on every module and the other controls line up in the two columns beside it.
  function finish(title, params, controls) {
    const cols = 3;   // three cells wide: a module fits a narrow board
    const on = controls[0] && controls[0].type === 'toggle' && controls[0].label === 'On' && controls.length > 1 ? controls[0] : null;
    const x0 = on ? 1 : 0; let x = x0, y = 0;
    const rest = on ? controls.slice(1) : controls;
    const placed = rest.map((c) => {
      const w = c.w || 1;
      if (x + w > cols) { x = x0; y++; }
      const p = Object.assign({}, c, { x, y, w, h: 1 });
      x += w; return p;
    });
    if (on) placed.unshift(Object.assign({}, on, { x: 0, y: 0, w: 1, h: 1 }));
    return { title, params, layout: { title, cols, rows: Math.max(1, y + 1), controls: placed }, actions: actionsOf(controls) };
  }

  const TIER_COLOUR = { Pure: '#5fd68a', Plausible: '#9fc85a', MechanismReal: '#e0b341', Steered: '#e07b3b', Forced: '#d9534f' };
  window.BNEffects = { available, build, tierColour: (t) => TIER_COLOUR[t] || '#e0523b' };
})();
