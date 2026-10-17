// The note scripts the parity test plays. Each scenario is {name, sampleRate, seconds, events, presets?} in the form Nano.renderOffline takes.
// Together they cover: every built-in preset at low / middle / high pitch, the velocity ladder (including the x.5 ties that round to even),
// pitch extremes and fractions, every duty, noise modes, the sweep unit, stealing and note return, release tails, one-shots, scheduled and
// auto-ending notes, bends, and seeded random scripts.

export function rng(seed) {
  let s = seed >>> 0;
  return () => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return s / 4294967296; };
}

const MINE = {
  // custom presets, to cover fields the built-ins do not combine
  slowpad: { kind: 'triangle', a: 300, d: 400, s: 0.6, r: 500, vib: { rate: 4, depth: 40, delay: 0, fade: 0 } },
  swell: { kind: 'pulse', duty: 3, a: 700, d: 0, s: 1, r: 300, gain: 0.7 },
  chirp: { kind: 'pulse', duty: 0, a: 0, d: 90, s: 0.2, r: 0, slide: { from: -1200, to: 1200, ms: 60 }, arp: { steps: [0, 3, -5], rate: 40 } },
  rumble: { kind: 'noise', a: 20, d: 600, s: 0.3, r: 200 },
  sweepup: { kind: 'pulse', duty: 2, oneShot: true, a: 0, d: 400, s: 0, sweep: { period: 1, negate: true, shift: 3 } },
  sweepdn: { kind: 'pulse', duty: 3, a: 0, d: 600, s: 1, r: 80, sweep: { period: 4, negate: false, shift: 1 } },
};

// samples for the DPCM scripts: PCM that the module encodes, and raw DPCM bytes
function pcmSweep(n, rate) { const a = new Float32Array(n); let ph = 0; for (let i = 0; i < n; i++) { const f = 900 - 700 * i / n; ph += 2 * Math.PI * f / rate; a[i] = Math.sin(ph) * (1 - i / n); } return a; }
function rawBytes(seed, n) { const r = rng(seed); const b = new Uint8Array(n); for (let i = 0; i < n; i++) b[i] = Math.floor(r() * 256); return b; }
export function dpcmSamples(Nano) {
  return {
    sweep: { data: pcmSweep(22050, 44100), sampleRate: 44100, rate: 15, note: 60 },
    sweepslow: { data: pcmSweep(8000, 44100), sampleRate: 44100, rate: 6, note: 60, pitched: true },
    crunch: { data: rawBytes(5, 257), rate: 12, note: 48 },
    buzz: { data: Uint8Array.from({ length: 33 }, (_, i) => (i % 2 ? 0xF0 : 0x0F)), rate: 10, loop: true, note: 60, pitched: true },
    tone: { data: Nano.encodeDpcm(Float32Array.from({ length: 2048 }, (_, i) => Math.sin(2 * Math.PI * 441 * i / 44100)), { sampleRate: 44100, rate: 15 }), rate: 15, loop: true, note: 60, start: 20 },
  };
}

export function scenarios(Nano) {
  const out = [];
  const all = Nano.presets.map(p => p.name);
  const add = (name, sampleRate, seconds, events, presets, samples) => out.push({ name, sampleRate, seconds, events, presets, samples });

  // 1. every preset at its natural pitch and an octave below / two above, held then released
  for (const rate of [44100, 48000]) {
    for (const p of Nano.presets) {
      const ev = [];
      let t = 0;
      for (const shift of [0, -12, 24]) {
        ev.push({ time: t, type: 'on', note: Math.max(0, Math.min(127, p.note + shift)), velocity: 1, preset: p.name, id: ev.length + 1 });
        ev.push({ time: t + 0.35, type: 'off', id: ev.length });
        t += 1.2;
      }
      add(`preset_${p.name}_${rate}`, rate, t + 0.2, ev);
    }
  }

  // 2. velocity ladder on the square (levels 0..15 including exact halves) and on the triangle (on/off)
  {
    const ev = []; let t = 0;
    const vels = [0, 1 / 30, 0.05, 0.1, 0.3, 0.7, 0.5, 1 / 6, 0.25, 0.75, 7.5 / 15, 6.5 / 15, 0.9, 1];
    for (const v of vels) { ev.push({ time: t, type: 'on', note: 69, velocity: v, preset: 'square', id: ev.length + 1 }); ev.push({ time: t + 0.1, type: 'off', id: ev.length }); t += 0.15; }
    for (const v of [0, 0.02, 0.5]) { ev.push({ time: t, type: 'on', note: 45, velocity: v, preset: 'bass', id: ev.length + 1 }); ev.push({ time: t + 0.1, type: 'off', id: ev.length }); t += 0.15; }
    add('velocity_ladder', 44100, t + 0.1, ev);
  }

  // 3. pitch extremes, fractions and every duty / noise mode override
  {
    const ev = []; let t = 0;
    for (const n of [0, 1, 12, 24, 33, 45, 59.5, 60.37, 71.99, 96, 110, 120, 127]) {
      for (const pr of ['square', 'bass']) { ev.push({ time: t, type: 'on', note: n, velocity: 1, preset: pr, id: ev.length + 1 }); ev.push({ time: t + 0.06, type: 'off', id: ev.length }); t += 0.08; }
    }
    for (const d of [0, 1, 2, 3]) { ev.push({ time: t, type: 'on', note: 60, velocity: 1, preset: 'square', duty: d, id: ev.length + 1 }); ev.push({ time: t + 0.1, type: 'off', id: ev.length }); t += 0.12; }
    for (const m of [0, 1]) for (const n of [24, 40, 60, 80]) { ev.push({ time: t, type: 'on', note: n, velocity: 1, preset: 'rumble', noiseMode: m, id: ev.length + 1 }); ev.push({ time: t + 0.1, type: 'off', id: ev.length }); t += 0.12; }
    add('pitch_extremes', 48000, t + 0.3, ev, MINE);
  }

  // 4. voice handling: a third pulse steals, return to the older note, the triangle and the noise channel (single), pinned channels, release tails
  {
    const ev = [];
    const on = (time, note, preset, extra = {}) => { const id = ev.filter(e => e.type === 'on').length + 1; ev.push({ time, type: 'on', note, velocity: 1, preset, id, ...extra }); return id; };
    const off = (time, id) => ev.push({ time, type: 'off', id });
    const a = on(0, 60, 'square'), b = on(0.2, 64, 'square'), c = on(0.4, 67, 'lead');
    off(0.7, c); off(0.9, b); off(1.1, a);                  // c steals; when c ends the older note returns
    const d = on(1.4, 36, 'bass'), e = on(1.6, 43, 'bass'); off(1.9, e); off(2.1, d);   // triangle: last note wins, the older returns
    const f = on(2.4, 60, 'snare'), g = on(2.45, 60, 'hat'); off(2.6, f); off(2.7, g);  // oneShots ignore the note-off
    const h = on(3.0, 72, 'swell'), i = on(3.1, 76, 'swell'); off(3.3, h); on(3.35, 80, 'swell'); off(3.6, i);  // a release tail occupies a channel
    on(4.2, 60, 'square', { channel: 1 }); on(4.3, 64, 'square', { channel: 1 }); on(4.4, 67, 'square', { channel: 0 });  // pinned
    ev.push({ time: 4.8, type: 'alloff' });
    on(5.2, 72, 'laser'); on(5.25, 76, 'laser'); on(5.3, 79, 'laser'); ev.push({ time: 5.5, type: 'kill' });
    on(5.8, 60, 'pluck'); ev.push({ time: 6.0, type: 'off', note: 60 });   // by note
    add('voice_handling', 44100, 6.6, ev, MINE);
  }

  // 5. the sweep unit, and a note that gets its channel back after a sweep took it
  {
    const ev = [];
    ev.push({ time: 0, type: 'on', note: 60, velocity: 1, preset: 'sweepdn', id: 1 });
    ev.push({ time: 0.5, type: 'on', note: 72, velocity: 1, preset: 'zap', id: 2 });
    ev.push({ time: 0.6, type: 'on', note: 76, velocity: 1, preset: 'sweepup', id: 3 });
    ev.push({ time: 1.4, type: 'on', note: 65, velocity: 0.8, preset: 'zap', id: 4, channel: 1 });
    ev.push({ time: 2.0, type: 'off', id: 1 });
    ev.push({ time: 2.4, type: 'on', note: 55, velocity: 1, preset: 'sweepdn', id: 5 });
    ev.push({ time: 2.5, type: 'on', note: 67, velocity: 1, preset: 'square', id: 6 });
    ev.push({ time: 3.3, type: 'off', id: 6 });
    ev.push({ time: 3.8, type: 'off', id: 5 });
    add('sweep_unit', 44100, 4.4, ev, MINE);
  }

  // 6. bends, global bend and tune, scheduled and auto-ending notes
  {
    const ev = [];
    ev.push({ time: 0, type: 'on', note: 60, velocity: 1, preset: 'square', id: 1, bend: 50 });
    ev.push({ time: 0.2, type: 'bend', id: 1, cents: 200 });
    ev.push({ time: 0.4, type: 'bend', id: 1, cents: -300 });
    ev.push({ time: 0.6, type: 'param', name: 'pitchBend', value: 100 });
    ev.push({ time: 0.8, type: 'param', name: 'tune', value: -50 });
    ev.push({ time: 1.0, type: 'off', id: 1 });
    ev.push({ time: 1.2, type: 'on', note: 48, velocity: 1, preset: 'bass', id: 2, duration: 0.3 });
    ev.push({ time: 1.7, type: 'on', note: 84, velocity: 0.6, preset: 'lead', id: 3, duration: 0.25, duty: 3 });
    ev.push({ time: 1.7001, type: 'bend', id: 3, cents: 700 });
    ev.push({ time: 2.2, type: 'param', name: 'pitchBend', value: 0 });
    ev.push({ time: 2.2, type: 'param', name: 'tune', value: 0 });
    ev.push({ time: 2.3, type: 'on', note: 62, velocity: 1, preset: 'chirp', id: 4 });
    ev.push({ time: 2.311, type: 'on', note: 62, velocity: 1, preset: 'chirp', id: 5 });
    ev.push({ time: 2.7, type: 'off', note: 62 });
    ev.push({ time: 2.9, type: 'off', note: 62 });
    add('bends_and_timing', 48000, 3.4, ev, MINE);
  }

  // 7. a busy four-channel passage (a mix through the real nonlinear mixer), at three host rates
  for (const rate of [44100, 48000, 32000, 96000]) {
    const ev = []; let id = 1;
    const note = (time, dur, n, preset, v = 1) => { ev.push({ time, type: 'on', note: n, velocity: v, preset, id }); ev.push({ time: time + dur, type: 'off', id }); id++; };
    for (let bar = 0; bar < 4; bar++) {
      const t0 = bar * 0.8;
      note(t0, 0.18, 36 + [0, 5, 7, 3][bar], 'bass');
      note(t0 + 0.4, 0.18, 43 + [0, 5, 7, 3][bar], 'bass');
      note(t0, 0.38, 72 + [0, 5, 7, 3][bar], 'arp', 0.8);
      note(t0 + 0.4, 0.39, 76 + [0, 5, 7, 3][bar], 'lead', 0.9);
      ev.push({ time: t0, type: 'on', note: 36, velocity: 1, preset: 'kick', id: id++ });
      ev.push({ time: t0 + 0.2, type: 'on', note: 38, velocity: 1, preset: 'hat', id: id++ });
      ev.push({ time: t0 + 0.4, type: 'on', note: 38, velocity: 1, preset: 'snare', id: id++ });
      ev.push({ time: t0 + 0.6, type: 'on', note: 38, velocity: 1, preset: 'openhat', id: id++ });
    }
    add(`passage_${rate}`, rate, 3.6, ev);
  }

  // 7b. DPCM: one-shots, loops and notes that stop them, the mixer with the other channels, taking the channel and giving it back
  {
    const samples = dpcmSamples(Nano);
    const ev = [];
    ev.push({ time: 0, type: 'on', note: 60, preset: 'sweep', id: 1 });
    ev.push({ time: 0.8, type: 'on', note: 60, preset: 'crunch', id: 2 });
    ev.push({ time: 1.2, type: 'on', note: 60, preset: 'tone', id: 3 });
    ev.push({ time: 1.7, type: 'off', id: 3 });
    ev.push({ time: 1.9, type: 'on', note: 65, preset: 'buzz', id: 4 });
    ev.push({ time: 2.4, type: 'off', id: 4 });
    ev.push({ time: 2.6, type: 'on', note: 48, preset: 'sweepslow', id: 5 });
    ev.push({ time: 3.2, type: 'on', note: 72, preset: 'sweepslow', id: 6 });
    ev.push({ time: 3.8, type: 'on', note: 60, preset: 'sweep', id: 7 });
    ev.push({ time: 3.9, type: 'kill' });
    add('dpcm_basic', 44100, 4.4, ev, undefined, samples);
  }
  {
    const samples = dpcmSamples(Nano);
    const ev = [];
    ev.push({ time: 0.0, type: 'on', note: 72, preset: 'lead', id: 1 });
    ev.push({ time: 0.0, type: 'on', note: 36, preset: 'bass', id: 2 });
    ev.push({ time: 0.1, type: 'on', note: 60, preset: 'tone', id: 3 });
    ev.push({ time: 0.2, type: 'on', note: 38, preset: 'snare', id: 4 });
    ev.push({ time: 0.5, type: 'on', note: 60, preset: 'crunch', id: 5, velocity: 0 });
    ev.push({ time: 0.9, type: 'on', note: 55, preset: 'buzz', id: 6 });       // takes the channel from the held tone
    ev.push({ time: 1.3, type: 'off', id: 6 });                                  // and gives it back
    ev.push({ time: 1.8, type: 'off', id: 3 });
    ev.push({ time: 1.9, type: 'off', id: 1 });
    ev.push({ time: 2.0, type: 'on', note: 60, preset: 'sweep', id: 7, channel: 4 });
    ev.push({ time: 2.2, type: 'alloff' });
    add('dpcm_voices', 48000, 3.0, ev, undefined, samples);
  }
  for (const rate of [32000, 96000]) {
    const samples = dpcmSamples(Nano);
    add(`dpcm_rate_${rate}`, rate, 1.6, [{ time: 0, type: 'on', note: 60, preset: 'sweep', id: 1 }, { time: 0.9, type: 'on', note: 60, preset: 'tone', id: 2 }, { time: 1.3, type: 'off', id: 2 }], undefined, samples);
  }

  // 8. seeded random scripts
  const rates = [44100, 48000, 22050, 44100, 48000, 96000];
  for (let s = 1; s <= 12; s++) {
    const r = rng(s * 7717);
    const names = [...all, ...Object.keys(MINE)];
    const ev = []; let ids = 0;
    for (let k = 0; k < 70; k++) {
      const time = Math.round(r() * 7000) / 1000;
      const x = r();
      if (x < 0.55) {
        const preset = names[Math.floor(r() * names.length)];
        const kind = (MINE[preset] ?? Nano._kit.PRESETS[preset]).kind;
        const e = { time, type: 'on', note: r() < 0.2 ? Math.round(r() * 1270) / 10 : Math.floor(r() * 128), velocity: r() < 0.3 ? Math.floor(r() * 31) / 30 : Math.round(r() * 100) / 100, preset, id: ++ids };
        if (r() < 0.15) e.duration = Math.round(r() * 800) / 1000;
        if (r() < 0.1) e.channel = kind === 'pulse' ? Math.floor(r() * 2) : kind === 'triangle' ? 2 : 3;
        if (kind === 'pulse' && r() < 0.15) e.duty = Math.floor(r() * 4);
        if (kind === 'noise' && r() < 0.2) e.noiseMode = Math.floor(r() * 2);
        if (r() < 0.1) e.bend = Math.round((r() - 0.5) * 1200);
        ev.push(e);
      } else if (x < 0.8 && ids > 0) ev.push({ time, type: 'off', id: 1 + Math.floor(r() * ids) });
      else if (x < 0.88) ev.push({ time, type: 'off', note: Math.floor(r() * 128) });
      else if (x < 0.93 && ids > 0) ev.push({ time, type: 'bend', id: 1 + Math.floor(r() * ids), cents: Math.round((r() - 0.5) * 2400) });
      else if (x < 0.96) ev.push({ time, type: 'alloff' });
      else if (x < 0.975) ev.push({ time, type: 'kill' });
      else ev.push({ time, type: 'param', name: r() < 0.5 ? 'pitchBend' : 'tune', value: Math.round((r() - 0.5) * 400) });
    }
    ev.sort((a, b) => a.time - b.time);
    add(`random_${s}`, rates[s % rates.length], 8.5, ev, MINE);
  }
  for (let s = 1; s <= 4; s++) {
    const r = rng(s * 4231), samples = dpcmSamples(Nano), sn = Object.keys(samples);
    const names = [...all, ...Object.keys(MINE), ...sn, ...sn];
    const ev = []; let ids = 0;
    for (let k = 0; k < 60; k++) {
      const time = Math.round(r() * 5000) / 1000, x = r();
      if (x < 0.6) {
        const preset = names[Math.floor(r() * names.length)];
        const kind = sn.includes(preset) ? 'dpcm' : (MINE[preset] ?? Nano._kit.PRESETS[preset]).kind;
        const e = { time, type: 'on', note: Math.floor(r() * 100) + 20, velocity: r() < 0.1 ? 0 : Math.round(r() * 100) / 100, preset, id: ++ids };
        if (r() < 0.15) e.duration = Math.round(r() * 600) / 1000;
        if (kind === 'dpcm' && r() < 0.1) e.channel = 4;
        ev.push(e);
      } else if (x < 0.85 && ids > 0) ev.push({ time, type: 'off', id: 1 + Math.floor(r() * ids) });
      else if (x < 0.92) ev.push({ time, type: 'alloff' });
      else if (x < 0.96) ev.push({ time, type: 'kill' });
      else ev.push({ time, type: 'off', note: Math.floor(r() * 100) + 20 });
    }
    ev.sort((a, b) => a.time - b.time);
    add(`random_dpcm_${s}`, [44100, 48000, 44100, 32000][s - 1], 6, ev, MINE, samples);
  }
  return out;
}
