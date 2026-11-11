// Nano SNES's parity proof: the web library's S-DSP against Core's DSP_SFC + StreamResampler, sample for sample.   node Nano/snes/test/parity.mjs [--mutants] [--keep]
//
//   scripts  note scripts through the shipped dist/nano-snes.js (engine + chip, five host rates); every chip operation recorded (DSP writes, sound RAM
//            writes, DSP samples, reads) and replayed through Core's DSP_SFC by tools/NanoSnesParity (NanoSnesSound): the PCM must be identical
//   fuzz     random register streams straight into the chip: every voice register, KON / KOFF in every pattern, ADSR and every GAIN mode, the noise
//            clock, pitch modulation, echo with feedback and the FIR, the soft reset / mute flags, ENDX, BRR blocks with every filter and range (the
//            invalid ranges 13-15 too) written into sound RAM, loops
// Tolerance: zero (raw float32 bits). --mutants injects one deliberate error at a time into a copy of the library and requires each to be caught.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import vm from 'node:vm';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..');
const distFile = path.join(root, 'dist', 'nano-snes.js');
const args = process.argv.slice(2);

function parityExe() {
  const art = process.env.NANO_ARTIFACTS ?? path.join(process.env.BROKENNES_WORK || 'C:\\BrokenNes-work', 'bn_nano_snes');
  execFileSync('dotnet', ['build', path.join(root, 'tools', 'NanoSnesParity', 'NanoSnesParity.csproj'), '-c', 'Release', '--artifacts-path', art, '-v', 'q', '-nologo'], { stdio: ['ignore', 'ignore', 'inherit'] });
  const exe = path.join(art, 'bin', 'NanoSnesParity', 'release', 'NanoSnesParity.exe');
  if (!fs.existsSync(exe)) throw new Error('NanoSnesParity.exe not found at ' + exe);
  return exe;
}
function loadLib(text) {
  const m = { exports: {} };
  vm.runInNewContext(text, { module: m, exports: m.exports, Float32Array, Float64Array, Int32Array, Int16Array, Uint8Array, Uint16Array, Int8Array, Math, Map, Set, Object, Array, JSON, Number, String, Error, TypeError, RangeError, ArrayBuffer, Uint32Array, Symbol, console });
  return m.exports;
}
const readF32 = (f) => { const b = fs.readFileSync(f); return new Float32Array(b.buffer.slice(b.byteOffset, b.byteOffset + b.length)); };
const sameBits = (a, b) => { if (a.length !== b.length) return `length ${a.length} vs ${b.length}`; const x = new Uint32Array(a.buffer, a.byteOffset, a.length), y = new Uint32Array(b.buffer, b.byteOffset, b.length); for (let i = 0; i < x.length; i++) if (x[i] !== y[i]) return `first difference at ${i}: ${a[i]} vs ${b[i]}`; return null; };

const RATES = [8000, 22050, 44100, 48000, 96000];
function scripts() {
  const out = [];
  const melody = (pre, t0 = 0) => [60, 64, 67, 72, 76, 79, 84, 88].map((n, i) => ({ time: t0 + i * 0.09, type: 'on', note: n, preset: pre, velocity: 0.4 + 0.075 * i, duration: 0.08 }));
  let k = 0;
  for (const sr of RATES) for (const v of [0, 1]) {
    const ev = v === 0 ? [...melody('piano'), ...melody('lead', 0.05), { time: 0, type: 'on', note: 36, preset: 'bass', duration: 0.6, pan: -1 },
      { time: 0.1, type: 'on', note: 38, preset: 'snare' }, { time: 0.3, type: 'on', note: 42, preset: 'hat' }, { time: 0.4, type: 'on', note: 60, preset: 'strings', duration: 0.3, pan: 1 },
      { time: 0.2, type: 'on', note: 70, preset: 'laser' }, { time: 0.5, type: 'on', note: 72, preset: 'powerup' }, { time: 0.35, type: 'param', name: 'pitchBend', value: -150 },
      { time: 0.62, type: 'on', note: 49, preset: 'crash' }, { time: 0.7, type: 'on', note: 60, preset: 'bell', duration: 0.3 }, { time: 0.75, type: 'kill' }, { time: 0.8, type: 'on', note: 30, preset: 'organ', duration: 0.1 },
      { time: 0.9, type: 'on', note: 120, preset: 'sine', duration: 0.05 }, { time: 0.95, type: 'on', note: 60, preset: 'kick' }, { time: 0.97, type: 'on', note: 36, preset: 'explosion' }]
      : [{ time: 0, type: 'on', note: 60, preset: { kind: 'brr', wave: Array.from({ length: 64 }, (_, i) => ((i * 977) % 2001 - 1000) * 20), loop: 16, base: 500, adsr: [9, 4, 3, 12], r: 80 }, duration: 0.5 },
        { time: 0.01, type: 'on', note: 64, preset: { kind: 'brr', wave: Array.from({ length: 48 }, (_, i) => (i % 12 < 6 ? 15000 : -15000)), loop: -1, gainReg: 0xDF, r: 0 }, duration: 0.4 },
        { time: 0.05, type: 'on', note: 48, preset: 'organ', duration: 0.4 }, { time: 0.1, type: 'on', note: 52, preset: 'choir', duration: 0.4 }, { time: 0.12, type: 'on', note: 55, preset: 'marimba', duration: 0.4 },
        { time: 0.14, type: 'on', note: 59, preset: 'pluck', duration: 0.4 }, { time: 0.16, type: 'on', note: 62, preset: 'triangle', duration: 0.4 }, { time: 0.18, type: 'on', note: 65, preset: 'saw', duration: 0.4 },
        { time: 0.2, type: 'on', note: 67, preset: 'square', duration: 0.2 }, { time: 0.22, type: 'on', note: 69, preset: 'coin' }, { time: 0.4, type: 'param', name: 'pan', value: [-1, 1, 0, -1, 1, 0, 0, 1] },
        { time: 0.45, type: 'alloff' }, { time: 0.6, type: 'on', note: 64, preset: 'pulse12', duration: 0.2, bend: 50 }, { time: 0.6, type: 'on', note: 67, preset: 'pulse25', duration: 0.2, channel: 0 }];
    out.push({ name: `snes${v}-${sr}`, sr, ev, seconds: 1.1 + (k++ % 3) * 0.013 });
  }
  return out;
}
function fuzzStreams(N) {
  const out = [];
  let seed = 4242;
  const rnd = () => { seed = (seed * 1103515245 + 12345) >>> 0; return seed / 4294967296; };
  const R = (n) => (rnd() * n) | 0;
  for (let s = 0; s < 16; s++) {
    const sr = RATES[s % RATES.length], c = new N._kit.Chip(sr);
    c.trace = [];
    const L = [], Rr = [];
    const bl = new Float32Array(4096), br = new Float32Array(4096);
    // a directory of 8 samples at $0200, random BRR from $1000 (every filter and range, loops), echo buffer at $8000
    const dir = [];
    let at = 0x1000;
    for (let k = 0; k < 8; k++) {
      const blocks = 1 + R(12), b = [];
      for (let j = 0; j < blocks; j++) { b.push((R(16) << 4) | (R(4) << 2) | (j === blocks - 1 ? 1 : 0) | (rnd() < 0.7 ? 2 : 0)); for (let q = 0; q < 8; q++) b.push(R(256)); }
      c.writeRam(at, b); const loop = at + 9 * R(blocks); dir.push(at & 255, at >> 8, loop & 255, loop >> 8); at += b.length;
    }
    c.writeRam(0x200, dir);
    c.write(0x5D, 2); c.write(0x6C, 0x00); c.write(0x0C, 0x40); c.write(0x1C, 0x40);
    if (s >= 12) {
      // loud samples (range 12, extreme nybbles: the interpolation's 16-bit wrap) on every voice, GAIN bent-line increase, full volume
      const b = []; for (let j = 0; j < 4; j++) { b.push(0xC0 | (j === 3 ? 3 : 2)); for (let q = 0; q < 8; q++) b.push(j & 1 ? 0x77 : 0x88); }
      c.writeRam(0x7000, b); c.writeRam(0x200 + 8 * 4, [0, 0x70, 0, 0x70]);
      for (let v = 0; v < 8; v++) { c.write(v << 4 | 4, 8); c.write(v << 4 | 5, 0); c.write(v << 4 | 7, 0xE0 | (20 + v)); c.write(v << 4, 127); c.write(v << 4 | 1, 127); c.write(v << 4 | 2, 0x00); c.write(v << 4 | 3, 0x08 + v); }
      c.write(0x4C, 0xFF);
    }
    for (let step = 0; step < 600; step++) {
      const r = rnd(), v = R(8) << 4;
      if (r < 0.3) c.write(v | R(8), R(256));
      else if (r < 0.36) c.write(0x4C, R(256));
      else if (r < 0.41) c.write(0x5C, rnd() < 0.5 ? 0 : R(256));
      else if (r < 0.45) c.write(0x6C, (rnd() < 0.1 ? R(8) << 5 : rnd() < 0.5 ? 0x20 : 0) | R(32));
      else if (r < 0.48) c.write([0x2D, 0x3D, 0x4D][R(3)], R(256));
      else if (r < 0.52) c.write([0x0C, 0x1C, 0x2C, 0x3C, 0x0D, 0x6D, 0x7D, 0x7C][R(8)], [0x6D].includes(0) ? 0 : R(256) & (rnd() < 0.5 ? 0x8F : 0xFF));
      else if (r < 0.55) c.write(R(8) << 4 | 0x0F, R(256));
      else if (r < 0.57) { const a = 0x1000 + R(0x400); c.writeRam(a, [R(256), R(256), R(256)]); }
      else if (r < 0.78 && c.left.count < 20000) c.run(1 + R(rnd() < 0.2 ? 4000 : 80));
      else { const n = 1 + R(700); c.render(bl, br, 0, n); for (let i = 0; i < n; i++) { L.push(bl[i]); Rr.push(br[i]); } }
    }
    out.push({ name: `fuzz-${String(s).padStart(2, '0')}`, sr, ops: c.trace, pcm: Float32Array.from([...L, ...Rr]) });
  }
  return out;
}

function engineScript(N, sc) {
  const kit = N._kit, presets = {}, events = [];
  let nextId = 1, anon = 0;
  const eng = new kit.Engine(sc.sr, {});
  for (const ev of sc.ev) {
    const frame = Math.round((ev.time || 0) * sc.sr);
    if (ev.type === 'on') {
      const p = { ...eng.resolvePreset(ev.preset ?? 'piano') }; if (typeof ev.preset !== 'string') p.name = 'anon' + anon++; presets[p.name] = p;
      const e = { frame, t: 'on', id: ev.id ?? nextId++, note: N.noteNumber(ev.note), vel: ev.velocity ?? 1, preset: p.name };
      if (ev.channel !== undefined) e.channel = ev.channel;
      if (ev.pan !== undefined) e.pan = kit.panValue(ev.pan);
      if (ev.bend !== undefined) e.bend = ev.bend;
      if (ev.duration !== undefined) e.duration = ev.duration;
      events.push(e);
    } else if (ev.type === 'off') events.push(ev.id !== undefined ? { frame, t: 'off', id: ev.id } : { frame, t: 'off', note: N.noteNumber(ev.note) });
    else if (ev.type === 'bend') events.push({ frame, t: 'bend', id: ev.id, cents: ev.cents });
    else if (ev.type === 'param' && ev.name === 'pan') events.push({ frame, t: 'pan', value: ev.value.map(kit.panValue) });
    else if (ev.type === 'param') events.push({ frame, t: ev.name, value: ev.value });
    else events.push({ frame, t: ev.type });
  }
  return { sampleRate: sc.sr, frames: Math.round(sc.seconds * sc.sr), presets, events };
}

function runAll(N, exe, dir, quiet) {
  let fails = 0;
  const fail = (m) => { fails++; if (!quiet) console.log('FAIL ' + m); };
  const cases = [];
  for (const sc of scripts()) {
    const r = N.renderOffline(sc.ev, sc.seconds, { sampleRate: sc.sr, stereo: true, trace: true });
    cases.push({ name: sc.name, sr: sc.sr, ops: r.trace, pcm: Float32Array.from([...r.left, ...r.right]) });
  }
  cases.push(...fuzzStreams(N));
  for (const f of fs.readdirSync(dir)) fs.unlinkSync(path.join(dir, f));
  for (const c of cases) fs.writeFileSync(path.join(dir, c.name + '.trace.json'), JSON.stringify({ rate: c.sr, ops: c.ops }));
  try { execFileSync(exe, ['replay', dir], { stdio: ['ignore', 'ignore', quiet ? 'ignore' : 'inherit'] }); } catch (e) { fail('replay threw'); return { fails }; }
  let samples = 0, writes = 0, ok = 0;
  for (const c of cases) {
    const d = sameBits(c.pcm, readF32(path.join(dir, c.name + '.core.f32')));
    samples += c.pcm.length / 2; writes += c.ops.filter(o => o[0] === 0).length;
    if (d) fail(`${c.name}: ${d}`); else ok++;
    if (!d && !c.pcm.some(x => x !== 0)) fail(`${c.name}: silent (proves nothing)`);
  }
  for (const sc of scripts()) fs.writeFileSync(path.join(dir, 'e-' + sc.name + '.scn.json'), JSON.stringify(engineScript(N, sc)));
  execFileSync(exe, ['notes', dir], { stdio: ['ignore', 'ignore', 'inherit'] });
  let engOk = 0, engN = 0;
  for (const sc of scripts()) {
    engN++;
    const r = N.renderOffline(sc.ev, sc.seconds, { sampleRate: sc.sr, stereo: true });
    const d = sameBits(Float32Array.from([...r.left, ...r.right]), readF32(path.join(dir, 'e-' + sc.name + '.ref.f32')));
    if (d) fail(`engine ${sc.name}: ${d}`); else engOk++;
  }
  const midiDir = path.join(here, 'midi');
  let midOk = 0, midN = 0;
  const songs = [];
  for (const f of fs.readdirSync(midiDir).filter(f => f.endsWith('.mid'))) for (const sr of [44100, 48000, 22050]) {
    const base = `m-${f.replace('.mid', '')}-${sr}`;
    fs.copyFileSync(path.join(midiDir, f), path.join(dir, base + '.mid'));
    fs.writeFileSync(path.join(dir, base + '.rate.txt'), String(sr));
    const side = path.join(midiDir, f.replace('.mid', '.sidecar.json'));
    if (fs.existsSync(side)) fs.copyFileSync(side, path.join(dir, base + '.sidecar.json'));
    songs.push({ base, sr, mid: fs.readFileSync(path.join(midiDir, f)), side: fs.existsSync(side) ? fs.readFileSync(side, 'utf8') : undefined });
  }
  execFileSync(exe, ['midi', dir], { stdio: ['ignore', 'ignore', 'inherit'] });
  for (const so of songs) {
    midN++;
    const r = N.renderMidi(so.mid, { sampleRate: so.sr, sidecar: so.side });
    const d = sameBits(Float32Array.from([...r.left, ...r.right]), readF32(path.join(dir, so.base + '.midi.f32')));
    if (d) fail(`midi ${so.base}: ${d}`); else midOk++;
    if (!d && !r.left.some(x => x !== 0)) fail(`midi ${so.base}: silent`);
  }
  return { fails, ok, total: cases.length, samples, writes, engOk, engN, midOk, midN };
}

const exe = parityExe();
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'nano-snes-parity-'));
const text = fs.readFileSync(distFile, 'utf8');
const t0 = Date.now();
const res = runAll(loadLib(text), exe, dir, false);
console.log(`${res.ok}/${res.total} traces bit-exact (${res.total - 16} note scripts at ${RATES.join(', ')} Hz, 16 register fuzz streams); C# engine twin ${res.engOk}/${res.engN} scripts, plugin-way MIDI ${res.midOk}/${res.midN}; ${res.samples} samples, ${res.writes} DSP writes, ${((Date.now() - t0) / 1000).toFixed(1)} s`);
let bad = res.fails;

if (args.includes('--mutants')) {
  const MUTANTS = [
    ['gauss table entry', '1305,1305,\n]);', '1305,1304,\n]);'],
    ['interpolation 16-bit wrap dropped', '    out = s16(out);\n    out += (GAUSS[rev]', '    out += (GAUSS[rev]'],
    ['BRR filter 2 coefficient', "if (filter === 8) { s += p2 >> 4; s += (p1 * -3) >> 6; }\n        else", "if (filter === 8) { s += p2 >> 4; s += (p1 * -3) >> 5; }\n        else"],
    ['BRR invalid range', 'if (shift >= 0x0D) s = (s >> 25) << 11;\n      const p1', 'if (shift >= 0x0E) s = (s >> 25) << 11;\n      const p1'],
    ['envelope release step', 'if (v.mode === ENV_RELEASE) { v.env = Math.max(env - 8, 0); return; }', 'if (v.mode === ENV_RELEASE) { v.env = Math.max(env - 7, 0); return; }'],
    ['rate offset', 'const RATE_OFFSET = [1, 0, 1040, 536,', 'const RATE_OFFSET = [1, 0, 1040, 535,'],
    ['KON every sample', "this.everyOther = !this.everyOther;\n    if (this.everyOther)", "this.everyOther = true;\n    if (this.everyOther)"],
    ['noise feedback', 'const feedback = (this.noise << 13) ^ (this.noise << 14);', 'const feedback = (this.noise << 13) ^ (this.noise << 15);'],
    ['pitch modulation', 'if (usePmod) pitch += ((prevOutput >> 5) * pitch) >> 10;', 'if (usePmod) pitch += ((prevOutput >> 4) * pitch) >> 10;'],
    ['echo FIR wrap', '    sum = s16(sum);\n    sum += s16(', '    sum += s16('],
    ['echo length', 'this.echoLength = (regs[EDL] & 0x0F) * 0x800;', 'this.echoLength = (regs[EDL] & 0x0F) * 0x400;'],
    ['bent line', 'if (mode > 6 && (v.hiddenEnv >>> 0) >= 0x600) env += 0x8 - 0x20;', 'if (mode > 6 && (v.hiddenEnv >>> 0) >= 0x700) env += 0x8 - 0x20;'],
    ['resampler not float32', 'out[off + i] = f32(f32(f32(f32(f32(f32(c3 * t) + c2) * t) + c1) * t) + y1);', 'out[off + i] = ((c3 * t + c2) * t + c1) * t + y1;'],
    ['resampler drop', 'const drop = Math.trunc(pos) - 1;', 'const drop = Math.trunc(pos) - 2;'],
    ['KON not batched', 'this.konBits |= bit;', 'this.konBits = bit;'],
    ['volume rounding', 'const level = v.vel * p.gain * this.env(v, this.frame), vol = clamp(roundEven(level * 127), 0, 127)', 'const level = v.vel * p.gain * this.env(v, this.frame), vol = clamp(Math.floor(level * 127), 0, 127)'],
  ];
  let caught = 0;
  for (const [name, from, to] of MUTANTS) {
    if (!text.includes(from)) { console.log(`MUTANT NOT APPLIED (source moved): ${name}`); bad++; continue; }
    let r;
    try { r = runAll(loadLib(text.split(from).join(to)), exe, dir, true); } catch (e) { r = { fails: 1 }; }
    if (r.fails > 0) caught++; else { console.log(`MUTANT SURVIVED: ${name}`); bad++; }
  }
  console.log(`mutants: ${caught}/${MUTANTS.length} caught`);
}
if (!args.includes('--keep')) fs.rmSync(dir, { recursive: true, force: true }); else console.log('kept ' + dir);
console.log(bad ? 'PARITY FAILED' : 'PARITY OK');
process.exit(bad ? 1 : 0);
