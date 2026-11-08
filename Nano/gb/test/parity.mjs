// Nano GB's parity proof: the web library against Core, sample for sample.   node Nano/gb/test/parity.mjs [--mutants] [--keep] [--write-songs]
//
//   CHIP        every script's register writes (recorded by the web library) replayed through Core's GnubSound at the same rate, and at 44.1 kHz also
//               through APU_GNUB itself clocked in T-cycles: the stereo PCM must be the same bits.
//   ENGINE      the same note script through NanoGbEngine (C#, the BrokenNes2 plugin's Nano GB engine): the same register writes at the same samples
//               and the same PCM.
//   CHIP FUZZ   seeded random register streams (every register, wave RAM included, power off and on) at seven rates.
//   MIDI        the test songs (test/midi/*.mid, one with a sidecar) through renderMidi and through the plugin's way of playing FL's notes (one engine
//               per MIDI channel, NanoGbParity midi): this is the "sounds the same as in FL" check, compared bit for bit, with level, timing and
//               spectrum figures beside it.
// Tolerance: zero (raw float32 bits). --mutants injects deliberate errors one at a time and requires each to be caught.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { loadGb, gbRoot, distFile, testSongs, rms, spectrum } from './lib.mjs';

const args = process.argv.slice(2);
const keep = args.includes('--keep');
const midiDir = path.join(gbRoot, 'test', 'midi');
if (args.includes('--write-songs')) {
  const s = testSongs();
  fs.mkdirSync(midiDir, { recursive: true });
  fs.writeFileSync(path.join(midiDir, 'tune.mid'), s.song1);
  fs.writeFileSync(path.join(midiDir, 'chords-sfx.mid'), s.song2);
  fs.writeFileSync(path.join(midiDir, 'chords-sfx.sidecar.json'), JSON.stringify(s.sidecar2, null, 2) + '\n');
  console.log('wrote test/midi/tune.mid, chords-sfx.mid, chords-sfx.sidecar.json');
}

function parityExe() {
  if (process.env.NANO_GB_PARITY_EXE) return process.env.NANO_GB_PARITY_EXE;
  const art = process.env.NANO_GB_ARTIFACTS ?? path.join(process.env.BROKENNES_WORK || 'C:\\BrokenNes-work', 'bn_nanogb');
  const exe = path.join(art, 'bin', 'NanoGbParity', 'release', 'NanoGbParity.exe');
  execFileSync('dotnet', ['build', path.join(gbRoot, 'tools', 'NanoGbParity', 'NanoGbParity.csproj'), '-c', 'Release', '--artifacts-path', art, '-v', 'q', '-nologo'], { stdio: ['ignore', 'ignore', 'inherit'] });
  if (!fs.existsSync(exe)) throw new Error(`NanoGbParity.exe not found at ${exe}`);
  return exe;
}
function readLR(file) {
  const b = fs.readFileSync(file), all = new Float32Array(b.buffer.slice(b.byteOffset, b.byteOffset + b.length)), n = all.length / 2;
  return { left: all.subarray(0, n), right: all.subarray(n) };
}
function cmp(a, b) {
  if (a.length !== b.length) return { ok: false, why: `length ${a.length} vs ${b.length}` };
  const ia = new Uint32Array(a.buffer, a.byteOffset, a.length), ib = new Uint32Array(b.buffer, b.byteOffset, b.length);
  let diffs = 0, first = -1, pk = 0;
  for (let i = 0; i < a.length; i++) { pk = Math.max(pk, Math.abs(b[i])); if (ia[i] !== ib[i]) { diffs++; if (first < 0) first = i; } }
  return { ok: diffs === 0, diffs, first, peak: pk, why: diffs ? `${diffs} samples differ, first at ${first}` : '' };
}
const cmpLR = (a, b) => { const l = cmp(a.left, b.left), r = cmp(a.right, b.right); return { ok: l.ok && r.ok, peak: Math.max(l.peak ?? 0, r.peak ?? 0), why: l.ok ? r.why && 'right: ' + r.why : 'left: ' + l.why }; };
function sameTrace(a, b) {
  if (a.length !== b.length) return { ok: false, why: `trace length ${a.length} vs ${b.length}` };
  for (let i = 0; i < a.length; i++) if (a[i].length !== b[i].length || a[i].some((x, k) => x !== b[i][k])) return { ok: false, why: `trace op ${i}: [${a[i]}] vs [${b[i]}]` };
  return { ok: true, why: '' };
}

// ---- the scripts ----
function scenarios(G) {
  const out = [], P = G.presets.map(p => p.name);
  const add = (name, sampleRate, seconds, events, presets) => out.push({ name, sampleRate, seconds, events, presets });
  for (const p of G.presets) for (const rate of [44100, 48000]) {
    const ev = [];
    [-12, 0, 7].forEach((d, i) => ev.push({ time: 0.01 + i * 0.4, type: 'on', note: p.note + d, preset: p.name, duration: 0.3, velocity: 1 - i * 0.2 }));
    add(`preset_${p.name}_${rate}`, rate, 1.3, ev);
  }
  add('velocity_ladder', 44100, 1.2, Array.from({ length: 16 }, (_, i) => ({ time: i * 0.07, type: 'on', note: 69, preset: 'square', velocity: i / 15, duration: 0.06 })));
  add('stealing', 48000, 1.5, [
    { time: 0, type: 'on', note: 60, preset: 'square', id: 1 }, { time: 0.1, type: 'on', note: 64, preset: 'lead', id: 2 }, { time: 0.2, type: 'on', note: 67, preset: 'thin', id: 3 },
    { time: 0.5, type: 'off', id: 3 }, { time: 0.7, type: 'off', id: 1 }, { time: 0.8, type: 'on', note: 72, preset: 'jump', id: 4 }, { time: 1.1, type: 'off', note: 64 }, { time: 1.2, type: 'alloff' }]);
  add('bends_pan_params', 44100, 1.5, [
    { time: 0, type: 'on', note: 57, preset: 'wavelead', id: 1, pan: 'L' }, { time: 0.1, type: 'bend', id: 1, cents: 150 }, { time: 0.3, type: 'bend', id: 1, cents: -230.5 },
    { time: 0.2, type: 'on', note: 45, preset: 'pulsebass', id: 2, pan: 1, duty: 3 }, { time: 0.4, type: 'param', name: 'pitchBend', value: 77 }, { time: 0.5, type: 'param', name: 'tune', value: -13 },
    { time: 0.6, type: 'param', name: 'pan', value: [1, -1, 0, 1] }, { time: 0.6, type: 'on', note: 40, preset: 'snare', id: 3 }, { time: 0.8, type: 'param', name: 'volume', value: 0.7 },
    { time: 1.0, type: 'kill' }, { time: 1.1, type: 'on', note: 62, preset: 'organ', id: 4, duration: 0.2 }]);
  add('hw_envelope_32768', 32768, 1.6, [{ time: 0, type: 'on', note: 60, preset: 'pluck' }, { time: 0.3, type: 'on', note: 72, preset: 'chime' }, { time: 0.4, type: 'on', note: 50, preset: 'hwnoise' }, { time: 0.9, type: 'on', note: 64, preset: 'swell' }],
    { hwnoise: { kind: 'noise', env: { step: 2 }, d: 600, s: 0, oneShot: true }, swell: { kind: 'pulse', env: { step: 3, up: true }, gain: 0.2, d: 600, s: 0, oneShot: true } });
  add('noise_follows_note', 44100, 1.2, Array.from({ length: 10 }, (_, i) => ({ time: i * 0.1, type: 'on', note: 30 + i * 9, preset: 'pitched', duration: 0.08 })), { pitched: { kind: 'noise', s: 1, r: 10 } });
  const myWave = Array.from({ length: 32 }, (_, i) => (i * 7) % 16);
  add('user_wave', 44100, 1.0, [{ time: 0, type: 'on', note: 48, preset: 'mine', duration: 0.3 }, { time: 0.35, type: 'on', note: 48, preset: 'wavebass', duration: 0.3 }, { time: 0.7, type: 'on', note: 55, preset: 'mine', duration: 0.2 }],
    { mine: { kind: 'wave', wave: myWave, a: 10, d: 100, s: 0.5, r: 40 } });
  add('pitch_extremes', 44100, 1.4, [0, 12, 24, 108, 120, 127].flatMap((m, i) => [{ time: i * 0.2, type: 'on', note: m, preset: i % 2 ? 'square' : 'subbass', duration: 0.15 }, { time: i * 0.2, type: 'on', note: m, preset: 'snare' }]));
  for (const rate of [22050, 32000, 96000, 11025, 44100, 48000]) {
    const ev = [];
    for (let b = 0; b < 8; b++) { ev.push({ time: b * 0.25, type: 'on', note: 48 + b, preset: 'wavebass', duration: 0.2 }, { time: b * 0.25, type: 'on', note: 72 + (b % 3), preset: 'lead', duration: 0.2 }, { time: b * 0.25 + 0.1, type: 'on', note: 76, preset: 'arp', duration: 0.12 }, { time: b * 0.25, type: 'on', note: 36, preset: b % 2 ? 'snare' : 'hat' }); }
    add(`passage_${rate}`, rate, 2.2, ev);
  }
  const rng = (seed) => { let s = seed >>> 0; return () => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return s / 4294967296; }; };
  for (let k = 1; k <= 12; k++) {
    const r = rng(k * 101), ev = [], rate = [44100, 48000, 22050, 32000][k % 4];
    for (let i = 0; i < 40; i++) {
      const t = r() * 2.5, pr = P[Math.floor(r() * P.length)];
      ev.push({ time: t, type: 'on', note: 30 + Math.floor(r() * 70), preset: pr, velocity: r(), id: i + 1, ...(r() < 0.3 ? { duration: r() * 0.5 } : {}) });
      if (r() < 0.5) ev.push({ time: t + r() * 0.6, type: 'off', id: i + 1 });
      if (r() < 0.15) ev.push({ time: t + r() * 0.3, type: 'bend', id: i + 1, cents: (r() - 0.5) * 400 });
    }
    ev.sort((a, b) => a.time - b.time);
    add(`random_${k}`, rate, 3.2, ev);
  }
  return out;
}

// The engine-level script NanoGbParity notes runs: renderOffline's own conversion (frame = round(time x rate), ids in order), presets normalized
function engineScript(G, sc) {
  const kit = G._kit, presets = {}, events = [];
  let nextId = 1;
  const eng = new kit.Engine(sc.sampleRate, { presets: sc.presets });
  for (const ev of sc.events) {
    const frame = Math.round((ev.time || 0) * sc.sampleRate);
    if (ev.type === 'on') {
      const p = eng.resolvePreset(ev.preset ?? 'lead'); presets[p.name] = p;
      const e = { frame, t: 'on', id: ev.id ?? nextId++, note: G.noteNumber(ev.note), vel: ev.velocity ?? 1, preset: p.name };
      if (ev.channel !== undefined) e.channel = ev.channel;
      if (ev.duty !== undefined) e.duty = ev.duty;
      if (ev.pan !== undefined) e.pan = kit.panValue(ev.pan);
      if (ev.bend !== undefined) e.bend = ev.bend;
      if (ev.duration !== undefined) e.duration = ev.duration;
      events.push(e);
    } else if (ev.type === 'off') events.push(ev.id !== undefined ? { frame, t: 'off', id: ev.id } : { frame, t: 'off', note: G.noteNumber(ev.note) });
    else if (ev.type === 'bend') events.push({ frame, t: 'bend', id: ev.id, cents: ev.cents });
    else if (ev.type === 'param' && ev.name === 'pan') events.push({ frame, t: 'pan', value: ev.value.map(kit.panValue) });
    else if (ev.type === 'param') events.push({ frame, t: ev.name, value: ev.value });
    else events.push({ frame, t: ev.type });
  }
  return { sampleRate: sc.sampleRate, frames: Math.round(sc.seconds * sc.sampleRate), presets, events };
}

export function runParity(G, list, exe) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'nanogb-parity-'));
  const js = new Map(), skip = new Set();
  for (const sc of list) {
    const r = G.renderOffline(sc.events, sc.seconds, { sampleRate: sc.sampleRate, presets: sc.presets, stereo: true, trace: true });
    js.set(sc.name, r);
    fs.writeFileSync(path.join(dir, sc.name + '.trace.json'), JSON.stringify({ rate: sc.sampleRate, ops: r.trace }));
    const scr = engineScript(G, sc);
    if (scr.events.some(e => e.t === 'volume')) skip.add(sc.name);   // (the gain after the chip is not in the register trace: the chip check skips this one, the engine check covers it)
    fs.writeFileSync(path.join(dir, sc.name + '.scn.json'), JSON.stringify(scr));
  }
  execFileSync(exe, ['replay', dir], { stdio: ['ignore', 'ignore', 'inherit'] });
  execFileSync(exe, ['notes', dir], { stdio: ['ignore', 'ignore', 'inherit'] });
  const results = [];
  for (const sc of list) {
    const r = js.get(sc.name);
    const post = skip.has(sc.name);
    const chip = post ? { ok: true, na: true, peak: 1 } : cmpLR(r, readLR(path.join(dir, sc.name + '.core.f32')));
    const apuFile = path.join(dir, sc.name + '.apu.f32');
    const apu = !post && fs.existsSync(apuFile) ? cmpLR(r, readLR(apuFile)) : null;
    let engine;
    {
      const tr = sameTrace(r.trace, JSON.parse(fs.readFileSync(path.join(dir, sc.name + '.ref.trace.json'), 'utf8')));
      const pcm = cmpLR(r, readLR(path.join(dir, sc.name + '.ref.f32')));
      engine = { ok: tr.ok && pcm.ok, why: tr.ok ? pcm.why : tr.why };
    }
    results.push({ name: sc.name, samples: r.left.length, writes: r.trace.filter(o => o[0] === 0).length, peak: chip.peak, chip, apu, engine });
  }
  if (!keep) fs.rmSync(dir, { recursive: true, force: true }); else console.log('kept ' + dir);
  return results;
}

export function runChipFuzz(G, exe, { rates = [44100, 48000, 22050, 96000, 32000, 16000, 11025], seeds = [1, 2, 3], steps = 300 } = {}) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'nanogb-fuzz-'));
  const mine = new Map();
  const rng = (seed) => { let s = seed >>> 0; return () => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return s / 4294967296; }; };
  for (const rate of rates) for (const seed of seeds) {
    const r = rng(seed * 7919 + rate), chip = new G._kit.Chip(rate), ops = [], L = [], R = [], name = `fuzz_${rate}_${seed}`;
    for (let step = 0; step < steps; step++) {
      for (let k = Math.floor(r() * 7); k > 0; k--) {
        let reg = 0x10 + Math.floor(r() * 0x30);
        let v = Math.floor(r() * 256);
        if (reg === 0x26) v = r() < 0.97 ? 0x80 : 0;
        if ((reg === 0x14 || reg === 0x19 || reg === 0x1E || reg === 0x23) && r() < 0.6) v |= 0x80;
        if (reg === 0x1A && r() < 0.7) v |= 0x80;
        if (reg === 0x24 && r() < 0.8) v |= 0x44;
        ops.push([0, reg, v]); chip.write(reg, v);
      }
      const n = 1 + Math.floor(r() * (r() < 0.2 ? 3000 : 300));
      const l = new Float32Array(n), rr = new Float32Array(n); chip.render(l, rr, 0, n);
      ops.push([1, n]); L.push(l); R.push(rr);
    }
    const join = (parts) => { const t = new Float32Array(parts.reduce((a, c) => a + c.length, 0)); let o = 0; for (const c of parts) { t.set(c, o); o += c.length; } return t; };
    fs.writeFileSync(path.join(dir, name + '.trace.json'), JSON.stringify({ rate, ops }));
    mine.set(name, { left: join(L), right: join(R) });
  }
  execFileSync(exe, ['replay', dir], { stdio: ['ignore', 'ignore', 'inherit'] });
  const res = [...mine].map(([name, pcm]) => {
    const c = cmpLR(pcm, readLR(path.join(dir, name + '.core.f32')));
    const af = path.join(dir, name + '.apu.f32');
    if (c.ok && fs.existsSync(af)) { const a = cmpLR(pcm, readLR(af)); if (!a.ok) return { name, cmp: { ok: false, why: 'APU_GNUB: ' + a.why } }; }
    return { name, cmp: c };
  });
  if (!keep) fs.rmSync(dir, { recursive: true, force: true });
  return res;
}

// The test songs through the web library and the plugin's way; figures: level (RMS dB), first-sound timing, the spectral distance
export function runMidi(G, exe) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'nanogb-midi-'));
  const songs = fs.readdirSync(midiDir).filter(f => f.endsWith('.mid'));
  const res = [];
  for (const rate of [44100, 48000]) {
    for (const f of songs) {
      const base = `${path.basename(f, '.mid')}_${rate}`;
      fs.copyFileSync(path.join(midiDir, f), path.join(dir, base + '.mid'));
      fs.writeFileSync(path.join(dir, base + '.rate.txt'), String(rate));
      const sc = path.join(midiDir, f.replace(/\.mid$/, '.sidecar.json'));
      if (fs.existsSync(sc)) fs.copyFileSync(sc, path.join(dir, base + '.sidecar.json'));
    }
    execFileSync(exe, ['midi', dir], { stdio: ['ignore', 'ignore', 'inherit'] });
    for (const f of songs) {
      const base = `${path.basename(f, '.mid')}_${rate}`;
      const sc = path.join(midiDir, f.replace(/\.mid$/, '.sidecar.json'));
      const js = G.renderMidi(fs.readFileSync(path.join(midiDir, f)), { sampleRate: rate, sidecar: fs.existsSync(sc) ? fs.readFileSync(sc, 'utf8') : undefined });
      const cs = readLR(path.join(dir, base + '.midi.f32'));
      const c = cmpLR(js, cs);
      const db = (a) => 20 * Math.log10(Math.max(1e-9, rms(a)));
      const onset = (a) => { for (let i = 0; i < a.length; i++) if (Math.abs(a[i]) > 0.01) return i; return -1; };
      const N = 8192, sa = spectrum(js.left, Math.floor(js.left.length / 3), N), sb = spectrum(cs.left, Math.floor(cs.left.length / 3), N);
      let num = 0, den = 0; for (let i = 0; i < sa.length; i++) { num += (sa[i] - sb[i]) ** 2; den += sa[i] ** 2; }
      res.push({ name: base, cmp: c, level: [db(js.left), db(cs.left)], onset: [onset(js.left), onset(cs.left)], spec: Math.sqrt(num / Math.max(den, 1e-30)) });
    }
  }
  if (!keep) fs.rmSync(dir, { recursive: true, force: true });
  return res;
}

const MUTANTS = [
  ['chip: 25% duty table entry', '0, -1, -1, 0, 0, 0, 0, 0, -1, -1', '0, -1, -1, 0, 0, 0, 0, -1, -1, -1'],
  ['chip: wave volume shift 3 -> 2', "3 - ((h[NR32] >> 5) & 3) : -1", "2 - ((h[NR32] >> 5) & 3) : -1"],
  ['chip: NR50 not swapped', 'l = (l * ml) >> 4; r = (r * mr) >> 4;', 'l = (l * mr) >> 4; r = (r * ml) >> 4;'],
  ['chip: noise 7-bit tap', 'noiseTable(16, 0xFB, 8, 6, 8)', 'noiseTable(16, 0xFB, 8, 5, 8)'],
  ['chip: noise clamp 1 << 18 -> 1 << 17', 'if ((f >> 18) !== 0) f = 1 << 18;\n    this.c[3 * CH + FREQ] = f;', 'if ((f >> 18) !== 0) f = 1 << 17;\n    this.c[3 * CH + FREQ] = f;'],
  ['chip: sweep adds one', 'else f += f >> sn;', 'else f += (f >> sn) + 1;'],
  ['chip: envelope steps a unit late', 'if (enlen1 && (encnt1 += RATE) >= enlen1)', 'if (enlen1 && (encnt1 += RATE) > enlen1)'],
  ['chip: wave RAM writes accepted while playing', 'if (c[2 * CH + ON] === 0) this.wave[r - 0x30] = h[r] = b;', 'this.wave[r - 0x30] = h[r] = b;'],
  ['chip: length counter units 13 -> 12', 'case NR41: h[r] = b; c[3 * CH + LEN] = (64 - (b & 63)) << 13;', 'case NR41: h[r] = b; c[3 * CH + LEN] = (64 - (b & 63)) << 12;'],
  ['chip: output scale', 'L[i] = l / 128; R[i] = r / 128;', 'L[i] = l / 127; R[i] = r / 128;'],
  ['chip: RATE rounded instead of truncated', 'this.rate = (HZ21 / hostRate) | 0;', 'this.rate = Math.round(HZ21 / hostRate);'],
  ['engine: pulse pitch constant', '(ch === 2 ? 65536 : 131072) * this.k / f', '(ch === 2 ? 65536 : 131073) * this.k / f'],
  ['engine: no retune for the true rate', 'this.k = sampleRate * this.chip.rate / 2097152;', 'this.k = 1;'],
  ['engine: exp2 series one term short', 'for (let k = 1; k <= 12; k++) { t = t * f / k; s = s + t; }', 'for (let k = 1; k <= 4; k++) { t = t * f / k; s = s + t; }'],
  ['engine: round half up', 'return (f % 2 === 0) ? f : f + 1;', 'return f + 1;'],
  ['engine: wave volume steps', 'level < 6 ? 3 : level < 12 ? 2 : 1', 'level < 5 ? 3 : level < 12 ? 2 : 1'],
  ['engine: stealing takes the newest', 'if (top.start < bestAt) { best = c; bestAt = top.start; }', 'if (top.start > -1) { best = c; bestAt = top.start; }'],
  ['engine: control tick 63', 'const CHUNK = 64;', 'const CHUNK = 63;'],
  ['engine: release tail 1% long', 'v.relLevel * Math.max(0, 1 - rt / p.r)', 'v.relLevel * Math.max(0, 1 - rt / (p.r * 1.01))'],
  ['engine: noise clock target x 15', 'const target = 16 * midiHz(cents)', 'const target = 15 * midiHz(cents)'],
  ['engine: silence leaves the length counter off', "this.wf(REG4[ch], 0x40 | (this.chip.hi[REG4[ch]] & 7));", "this.wf(REG4[ch], 0x00 | (this.chip.hi[REG4[ch]] & 7));"],
  ['engine: vibrato slope', 'ph < 0.25 ? 4 * ph :', 'ph < 0.25 ? 3.9 * ph :'],
  ['song: velocity / 128', 'Math.min(1, e.b / 127 * ps.gain)', 'Math.min(1, e.b / 128 * ps.gain)'],
  ['song: bend range ignored', '/ 8192 * this.bendRange[e.ch] * 100', '/ 8192 * 2 * 100'],
  ['song: drums at the note, not the preset pitch', 'return n ? { name: n, note: PRESETS[n].note, gain: 1 } : null;', 'return n ? { name: n, note, gain: 1 } : null;'],
  ['song: sidecar duty ignored', 'if (ps.duty !== undefined && ps.duty !== null) o.duty = ps.duty;', ''],
];

const exe = parityExe();
const text = fs.readFileSync(process.env.NANO_GB_DIST ?? distFile, 'utf8');
const G = loadGb(text);
let failed = 0;
const list = scenarios(G);
console.log(`PARITY: ${list.length} scripts against Core (${exe})`);
const t0 = Date.now();
let samples = 0, writes = 0, apuN = 0, engN = 0, chipN = 0;
for (const r of runParity(G, list, exe)) {
  samples += r.samples; writes += r.writes; if (r.apu) apuN++; engN++; if (!r.chip.na) chipN++;
  const bad = !r.chip.ok || (r.apu && !r.apu.ok) || !r.engine.ok || r.peak < 1e-3;
  if (bad) failed++;
  if (bad || args.includes('-v')) console.log(`${bad ? 'FAIL' : 'ok  '} ${r.name.padEnd(26)} ${String(r.samples).padStart(7)} samples ${String(r.writes).padStart(5)} writes  chip: ${r.chip.ok ? 'bit-exact' : r.chip.why}  APU_GNUB: ${r.apu ? (r.apu.ok ? 'bit-exact' : r.apu.why) : '-'}  engine: ${r.engine.ok ? 'bit-exact' : r.engine.why}${r.peak < 1e-3 ? '  SILENT' : ''}`);
}
console.log(`${list.length - failed}/${list.length} scripts: chip bit-exact against GnubSound in ${chipN} (${apuN} also against APU_GNUB), engine bit-exact against NanoGbEngine in ${engN}; ${samples} stereo samples, ${writes} register writes, ${((Date.now() - t0) / 1000).toFixed(1)} s`);
{
  const fz = runChipFuzz(G, exe), bad = fz.filter(f => !f.cmp.ok);
  console.log(`chip fuzz: ${fz.length - bad.length}/${fz.length} random register streams (seven rates, wave RAM, power cycles) bit-exact against GnubSound (and APU_GNUB at 44.1 kHz)` + (bad.length ? ': ' + bad.map(b => b.name + ' ' + b.cmp.why).join('; ') : ''));
  if (bad.length) failed++;
}
{
  const mr = runMidi(G, exe);
  for (const m of mr) {
    if (!m.cmp.ok || m.cmp.peak < 0.01) failed++;
    console.log(`midi ${m.cmp.ok ? 'bit-exact' : 'DIFFERS (' + m.cmp.why + ')'} ${m.name.padEnd(20)} level web ${m.level[0].toFixed(2)} dB / plugin ${m.level[1].toFixed(2)} dB, first sound at sample ${m.onset[0]} / ${m.onset[1]}, spectral distance ${m.spec.toExponential(1)}`);
  }
}

if (args.includes('--mutants')) {
  const subset = list.filter(s => /^(stealing|bends_pan_params|hw_envelope_32768|noise_follows_note|user_wave|velocity_ladder|pitch_extremes|passage_44100|passage_22050|random_[1-4])$|^preset_(lead|pluck|jump|laser|snare|hat|kick|pad|pwm)_48000$/.test(s.name));
  console.log(`\nMUTANTS: ${MUTANTS.length} deliberate errors`);
  let survived = 0;
  for (const [name, find, replace] of MUTANTS) {
    const at = text.indexOf(find);
    if (at < 0 || text.indexOf(find, at + 1) >= 0) { console.log(`ERROR  ${name}: the text to change was ${at < 0 ? 'not found' : 'found twice'}`); survived++; continue; }
    const M = loadGb(text.slice(0, at) + replace + text.slice(at + find.length));
    const caught = [];
    try {
      const r = runParity(M, subset, exe);
      const c = r.filter(x => !x.chip.ok || (x.apu && !x.apu.ok)).length, e = r.filter(x => !x.engine.ok).length;
      if (c) caught.push(`chip in ${c}`); if (e) caught.push(`engine in ${e}`);
      if (name.startsWith('chip')) { const fz = runChipFuzz(M, exe, { rates: [44100, 22050], seeds: [1], steps: 200 }).filter(f => !f.cmp.ok); if (fz.length) caught.push(`fuzz in ${fz.length}`); }
      if (name.startsWith('song')) { const m = runMidi(M, exe).filter(x => !x.cmp.ok); if (m.length) caught.push(`midi in ${m.length}`); }
    } catch (err) { caught.push('threw ' + String(err.message).split('\n')[0]); }
    if (caught.length) console.log(`caught   ${name}  [${caught.join(', ')}]`); else { console.log(`SURVIVED ${name}`); survived++; }
  }
  console.log(survived ? `${survived} mutant(s) SURVIVED` : `all ${MUTANTS.length} mutants caught`);
  failed += survived;
}
console.log(failed ? 'PARITY FAILED' : 'PARITY OK');
process.exit(failed ? 1 : 0);
