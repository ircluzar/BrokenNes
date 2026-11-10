// Nano SMS's parity proof: the web library's chip against Core's Sn76489, sample for sample.   node Nano/sms/test/parity.mjs [--mutants] [--keep]
//
//   kernel   the band-limited step kernel built in JS = Core's BandLimitedMixer.Kernel, bit for bit (512 float32)
//   scripts  note scripts through the shipped dist/nano-sms.js (engine + chip, Master System and Game Gear, five host rates); every chip operation
//            recorded (PSG writes, stereo writes, runs, reads) and replayed through Core's Sn76489 by tools/NanoSmsParity: the PCM must be identical
//   fuzz     random register streams straight into the chip: every register, latch/data bytes, periods 0 and 1 (the Sega hold), noise rate 3 (tone 3's
//            clock), white/periodic, stereo, run lengths from 1 to 5000 clocks
// Tolerance: zero (raw float32 bits). --mutants injects one deliberate error at a time into a copy of the library and requires each to be caught.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import vm from 'node:vm';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..');
const distFile = path.join(root, 'dist', 'nano-sms.js');
const args = process.argv.slice(2);

function parityExe() {
  const art = process.env.NANO_ARTIFACTS ?? path.join(process.env.BROKENNES_WORK || 'C:\\BrokenNes-work', 'bn_nano_sms');
  execFileSync('dotnet', ['build', path.join(root, 'tools', 'NanoSmsParity', 'NanoSmsParity.csproj'), '-c', 'Release', '--artifacts-path', art, '-v', 'q', '-nologo'], { stdio: ['ignore', 'ignore', 'inherit'] });
  const exe = path.join(art, 'bin', 'NanoSmsParity', 'release', 'NanoSmsParity.exe');
  if (!fs.existsSync(exe)) throw new Error('NanoSmsParity.exe not found at ' + exe);
  return exe;
}
function loadLib(text) {
  const m = { exports: {} };
  vm.runInNewContext(text, { module: m, exports: m.exports, Float32Array, Float64Array, Int32Array, Uint8Array, Math, Map, Set, Object, Array, JSON, Number, String, Error, TypeError, RangeError, ArrayBuffer, Uint32Array, Int8Array, Symbol, console });
  return m.exports;
}
const readF32 = (f) => { const b = fs.readFileSync(f); return new Float32Array(b.buffer.slice(b.byteOffset, b.byteOffset + b.length)); };
const sameBits = (a, b) => { if (a.length !== b.length) return `length ${a.length} vs ${b.length}`; const x = new Uint32Array(a.buffer, a.byteOffset, a.length), y = new Uint32Array(b.buffer, b.byteOffset, b.length); for (let i = 0; i < x.length; i++) if (x[i] !== y[i]) return `first difference at ${i}: ${a[i]} vs ${b[i]}`; return null; };

// ---- what is checked ----
const RATES = [8000, 22050, 44100, 48000, 96000];
function scripts() {
  const out = [];
  const melody = (pre, t0 = 0) => [60, 64, 67, 72, 76, 79, 84, 88].map((n, i) => ({ time: t0 + i * 0.09, type: 'on', note: n, preset: pre, velocity: 0.4 + 0.075 * i, duration: 0.08 }));
  let k = 0;
  for (const gg of [false, true]) for (const sr of RATES) {
    const name = `${gg ? 'gg' : 'sms'}-${sr}`;
    const ev = [...melody('lead'), ...melody('pluck', 0.05), { time: 0, type: 'on', note: 45, preset: 'bass', duration: 0.6, pan: gg ? -1 : undefined },
      { time: 0.1, type: 'on', note: 38, preset: 'snare' }, { time: 0.3, type: 'on', note: 42, preset: 'hat' }, { time: 0.4, type: 'on', note: 60, preset: 'buzz', duration: 0.2, pan: gg ? 1 : undefined },
      { time: 0.2, type: 'on', note: 70, preset: 'laser' }, { time: 0.5, type: 'on', note: 72, preset: 'powerup' }, { time: 0.35, type: 'param', name: 'pitchBend', value: -150 },
      { time: 0.62, type: 'on', note: 49, preset: 'crash' }, { time: 0.7, type: 'on', note: 60, preset: 'pad', duration: 0.3 }, { time: 0.75, type: 'kill' }, { time: 0.8, type: 'on', note: 30, preset: 'square', duration: 0.1 },
      { time: 0.9, type: 'on', note: 120, preset: 'square', duration: 0.05 }];
    out.push({ name, sr, gg, ev: ev.map(e => { const o = { ...e }; if (o.pan === undefined) delete o.pan; return o; }), seconds: 1.1 + (k++ % 3) * 0.013 });
  }
  return out;
}
function fuzzStreams(N) {
  const out = [];
  let seed = 12345;
  const rnd = () => { seed = (seed * 1103515245 + 12345) >>> 0; return seed / 4294967296; };
  for (let s = 0; s < 18; s++) {
    const sr = RATES[s % RATES.length], c = new N._kit.Chip(sr);
    c.trace = [];
    const L = [], Rr = [];
    const bl = new Float32Array(4096), br = new Float32Array(4096);
    for (let step = 0; step < 400; step++) {
      const r = rnd();
      if (r < 0.45) { const ch = (rnd() * 4) | 0, vol = rnd() < 0.5; c.write(0x80 | (ch << 5) | (vol ? 16 : 0) | ((rnd() * 16) | 0)); if (!vol && rnd() < 0.7) c.write((rnd() < 0.15 ? 0 : (rnd() * 64)) | 0); }
      else if (r < 0.5) c.write(0x80 | ((rnd() * 3) | 0) << 5 | (rnd() < 0.5 ? 0 : 1));   // period 0 / 1 (the hold)
      else if (r < 0.55) c.write((rnd() * 128) | 0);                                      // a bare data byte to whatever is latched
      else if (r < 0.6) c.writeStereo((rnd() * 256) | 0);
      else if (r < 0.8) c.run(1 + ((rnd() * (rnd() < 0.2 ? 5000 : 300)) | 0));
      else { const n = 1 + ((rnd() * 700) | 0); c.render(bl, br, 0, n); for (let i = 0; i < n; i++) { L.push(bl[i]); Rr.push(br[i]); } }
    }
    out.push({ name: `fuzz-${String(s).padStart(2, '0')}`, sr, clock: c.cpuClockHz, ops: c.trace, pcm: Float32Array.from([...L, ...Rr]) });
  }
  return out;
}

function runAll(N, exe, dir, quiet) {
  let fails = 0;
  const fail = (m) => { fails++; if (!quiet) console.log('FAIL ' + m); };
  // kernel
  execFileSync(exe, ['kernel', path.join(dir, 'kernel.f32')], { stdio: 'ignore' });
  const kd = sameBits(N._kit.BL_KERNEL, readF32(path.join(dir, 'kernel.f32')));
  if (kd) fail('kernel: ' + kd);
  // scripts + fuzz -> traces
  const cases = [];
  for (const sc of scripts()) {
    const r = N.renderOffline(sc.ev, sc.seconds, { sampleRate: sc.sr, gameGear: sc.gg, stereo: true, trace: true });
    cases.push({ name: sc.name, sr: sc.sr, ops: r.trace, pcm: Float32Array.from([...r.left, ...r.right]), clock: N._kit.NTSC_Z80 });
  }
  cases.push(...fuzzStreams(N));
  for (const f of fs.readdirSync(dir)) if (f.endsWith('.trace.json') || f.endsWith('.core.f32')) fs.unlinkSync(path.join(dir, f));
  for (const c of cases) fs.writeFileSync(path.join(dir, c.name + '.trace.json'), JSON.stringify({ rate: c.sr, clock: c.clock, ops: c.ops }));
  execFileSync(exe, ['replay', dir], { stdio: ['ignore', 'ignore', 'inherit'] });
  let samples = 0, writes = 0, ok = 0;
  for (const c of cases) {
    const d = sameBits(c.pcm, readF32(path.join(dir, c.name + '.core.f32')));
    samples += c.pcm.length / 2; writes += c.ops.filter(o => o[0] === 0 || o[0] === 2).length;
    if (d) fail(`${c.name}: ${d}`); else ok++;
    if (!d && !c.pcm.some(x => x !== 0)) fail(`${c.name}: silent (proves nothing)`);
  }
  return { fails, ok, total: cases.length, samples, writes, kernelOk: !kd };
}

const exe = parityExe();
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'nano-sms-parity-'));
const text = fs.readFileSync(distFile, 'utf8');
const t0 = Date.now();
const res = runAll(loadLib(text), exe, dir, false);
console.log(`kernel: ${res.kernelOk ? 'bit-exact' : 'DIFFERS'}; ${res.ok}/${res.total} traces bit-exact (${res.total - 18} note scripts on Master System and Game Gear at ${RATES.join(', ')} Hz, 18 register fuzz streams); ${res.samples} samples, ${res.writes} chip writes, ${((Date.now() - t0) / 1000).toFixed(1)} s`);
let bad = res.fails;

if (args.includes('--mutants')) {
  const MUTANTS = [
    ['noise taps 0,3 -> 0,2', 'fb = (this.noise & 4) ? ((s ^ (s >> 3)) & 1)', 'fb = (this.noise & 4) ? ((s ^ (s >> 2)) & 1)'],
    ['no Sega hold on period 1', 'const nt = p <= 1 ? 1 : tone[i] ^ 1;', 'const nt = p < 1 ? 1 : tone[i] ^ 1;'],
    ['noise divider removed', 'if (this.noiseDivider) {', 'if (true) {'],
    ['noise rate 2 = 0x80', "r === 2 ? 0x40", "r === 2 ? 0x80"],
    ['attenuation table step', '32767, 26028, 20675', '32767, 26028, 20676'],
    ['kernel cutoff', 'cutoff = 0.94', 'cutoff = 0.95'],
    ['leak not float32', 'this.oneMinusLeak = f32(1 - this.leak);', 'this.oneMinusLeak = 1 - this.leak;'],
    ['integrator in double', 'sum = f32(f32(sum * k) + ring[at]);\n      ring[at] = 0;\n      let x', 'sum = sum * k + ring[at];\n      ring[at] = 0;\n      let x'],
    ['round instead of truncate', 'out[off + i] = (x | 0) / 32768;', 'out[off + i] = Math.round(x) / 32768;'],
    ['stereo bit of noise', "if (st & (0x10 << i)) l = f32(l + a);", "if (st & (0x10 << (i & 3 ^ (i === 3 ? 1 : 0)))) l = f32(l + a);"],
    ['latch data keeps low bits wrong', "(this.period[ch] & 0x00F) | ((value & 0x3F) << 4)", "(this.period[ch] & 0x00F) | ((value & 0x1F) << 4)"],
    ['noise write does not reload', "else { this.noise = value & 7; this.shift = 0x8000; }\n    } else {", "else { this.noise = value & 7; }\n    } else {"],
    ['kernel phase rounding', 'let phase = Math.trunc((pos - whole) * BL_PHASES);', 'let phase = Math.round((pos - whole) * BL_PHASES);'],
    ['jump skips a counter', 'if (cnt[3] < m) m = cnt[3];', ''],
  ];
  let caught = 0;
  for (const [name, from, to] of MUTANTS) {
    if (!text.includes(from)) { console.log(`MUTANT NOT APPLIED (source moved): ${name}`); bad++; continue; }
    let r;
    try { r = runAll(loadLib(text.replace(from, to)), exe, dir, true); } catch (e) { r = { fails: 1 }; }
    if (r.fails > 0) caught++; else { console.log(`MUTANT SURVIVED: ${name}`); bad++; }
  }
  console.log(`mutants: ${caught}/${MUTANTS.length} caught`);
}
if (!args.includes('--keep')) fs.rmSync(dir, { recursive: true, force: true }); else console.log('kept ' + dir);
console.log(bad ? 'PARITY FAILED' : 'PARITY OK');
process.exit(bad ? 1 : 0);
