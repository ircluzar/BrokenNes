// Nano Genesis's parity proof: the web library's chips against Core's Ym2612 + Sn76489, sample for sample.   node Nano/genesis/test/parity.mjs [--mutants] [--keep]
//
//   kernel   the band-limited step kernel built in JS = Core's BandLimitedMixer.Kernel, bit for bit (512 float32)
//   scripts  note scripts through the shipped dist/nano-genesis.js (engine + chips, five host rates); every chip operation recorded (PSG and YM2612 writes,
//            runs, reads) and replayed through Core's Ym2612 + Sn76489 (NanoGenesisSound: MdSoundUnit's mixing) by tools/NanoGenesisParity: the PCM must be identical
//   fuzz     random register streams straight into the chips: every YM2612 operator / channel register on both parts, all eight algorithms, feedback,
//            detune, multiple, SSG-EG, the LFO with AMS / PMS, channel 3's special mode, the DAC, key-on masks per operator, PSG latch/data bytes, periods 0 and 1
// Tolerance: zero (raw float32 bits). --mutants injects one deliberate error at a time into a copy of the library and requires each to be caught.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import vm from 'node:vm';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..');
const distFile = path.join(root, 'dist', 'nano-genesis.js');
const args = process.argv.slice(2);

function parityExe() {
  const art = process.env.NANO_ARTIFACTS ?? path.join(process.env.BROKENNES_WORK || 'C:\\BrokenNes-work', 'bn_nano_genesis');
  execFileSync('dotnet', ['build', path.join(root, 'tools', 'NanoGenesisParity', 'NanoGenesisParity.csproj'), '-c', 'Release', '--artifacts-path', art, '-v', 'q', '-nologo'], { stdio: ['ignore', 'ignore', 'inherit'] });
  const exe = path.join(art, 'bin', 'NanoGenesisParity', 'release', 'NanoGenesisParity.exe');
  if (!fs.existsSync(exe)) throw new Error('NanoGenesisParity.exe not found at ' + exe);
  return exe;
}
function loadLib(text) {
  const m = { exports: {} };
  vm.runInNewContext(text, { module: m, exports: m.exports, Float32Array, Float64Array, Int32Array, Uint8Array, Uint16Array, Math, Map, Set, Object, Array, JSON, Number, String, Error, TypeError, RangeError, ArrayBuffer, Uint32Array, Int8Array, Symbol, console });
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
  for (const sr of RATES) for (const v of [0, 1]) {
    const name = `md${v}-${sr}`;
    const ev = v === 0 ? [...melody('brass'), ...melody('epiano', 0.05), ...melody('psglead', 0.02), { time: 0, type: 'on', note: 36, preset: 'bass', duration: 0.6, pan: -1 },
      { time: 0.1, type: 'on', note: 38, preset: 'snare' }, { time: 0.3, type: 'on', note: 42, preset: 'hat' }, { time: 0.4, type: 'on', note: 60, preset: 'strings', duration: 0.3, pan: 1 },
      { time: 0.2, type: 'on', note: 70, preset: 'laser' }, { time: 0.5, type: 'on', note: 72, preset: 'powerup' }, { time: 0.35, type: 'param', name: 'pitchBend', value: -150 },
      { time: 0.62, type: 'on', note: 49, preset: 'crash' }, { time: 0.7, type: 'on', note: 60, preset: 'bell', duration: 0.3 }, { time: 0.75, type: 'kill' }, { time: 0.8, type: 'on', note: 30, preset: 'growl', duration: 0.1 },
      { time: 0.9, type: 'on', note: 120, preset: 'sine', duration: 0.05 }, { time: 0.95, type: 'on', note: 36, preset: 'fmkick' }]
      : [{ time: 0, type: 'param', name: 'lfo', value: 6 }, { time: 0, type: 'on', note: 60, preset: { kind: 'fm', alg: 2, fb: 6, ams: 3, pms: 7, r: 100, ops: [{ mul: 3, tl: 20, am: 1, ssg: 8, d1r: 20 }, { mul: 1, tl: 10, ssg: 12, d1r: 25 }, { mul: 5, tl: 30, dt: 5 }, { mul: 1, tl: 0, am: 1, ssg: 14, d1r: 18 }] }, duration: 0.5 },
        { time: 0.05, type: 'on', note: 48, preset: 'organ', duration: 0.4 }, { time: 0.1, type: 'on', note: 52, preset: 'clarinet', duration: 0.4 }, { time: 0.12, type: 'on', note: 55, preset: 'flute', duration: 0.4 },
        { time: 0.14, type: 'on', note: 59, preset: 'marimba', duration: 0.4 }, { time: 0.16, type: 'on', note: 62, preset: 'pluck', duration: 0.4 }, { time: 0.18, type: 'on', note: 65, preset: 'buzz', duration: 0.4 },
        { time: 0.2, type: 'on', note: 67, preset: 'square', duration: 0.2 }, { time: 0.22, type: 'on', note: 69, preset: 'coin' }, { time: 0.4, type: 'param', name: 'pan', value: [-1, 1, 0, -1, 1, 0, 0, 0, 0, 0] },
        { time: 0.45, type: 'alloff' }, { time: 0.6, type: 'param', name: 'lfo', value: 0 }, { time: 0.6, type: 'on', note: 64, preset: 'pulse', duration: 0.2, bend: 50 }, { time: 0.61, type: 'on', note: 64, preset: 'pulse', duration: 0.2, channel: 0 }]
    out.push({ name, sr, ev, seconds: 1.1 + (k++ % 3) * 0.013 });
  }
  return out;
}
function fuzzStreams(N) {
  const out = [];
  let seed = 12345;
  const rnd = () => { seed = (seed * 1103515245 + 12345) >>> 0; return seed / 4294967296; };
  const R = (n) => (rnd() * n) | 0;
  for (let s = 0; s < 16; s++) {
    const sr = RATES[s % RATES.length], c = new N._kit.Chip(sr);
    c.trace = [];
    const L = [], Rr = [];
    const bl = new Float32Array(4096), br = new Float32Array(4096);
    if (s >= 12) {
      // every channel keyed with every operator audible-ish, AM on with the deepest AMS, the LFO on, SSG-EG in a mode per stream (the hold modes 1, 3, 5, 7 among them)
      c.ymWrite(0x22, 8 | (s & 7));
      for (let ch = 0; ch < 6; ch++) {
        const part = ch >= 3 ? 0x100 : 0, o = ch % 3;
        c.ymWrite(part | (0xB0 + o), 7); c.ymWrite(part | (0xB4 + o), 0xC0 | 0x30 | (ch & 7));
        for (const slot of [0, 4, 8, 12]) {
          c.ymWrite(part | (0x30 + slot + o), 1 + ch); c.ymWrite(part | (0x40 + slot + o), 8); c.ymWrite(part | (0x50 + slot + o), 31);
          c.ymWrite(part | (0x60 + slot + o), 0x80 | 12); c.ymWrite(part | (0x70 + slot + o), 6); c.ymWrite(part | (0x80 + slot + o), 0x55); c.ymWrite(part | (0x90 + slot + o), 8 | ((s * 2 + 1 + ch) & 7));
        }
        c.ymWrite(part | (0xA4 + o), 0x22); c.ymWrite(part | (0xA0 + o), 0x69 + ch * 9);
        c.ymWrite(0x28, 0xF0 | (ch >= 3 ? 4 : 0) | o);
      }
    }
    for (let step = 0; step < 500; step++) {
      const r = rnd(), part = rnd() < 0.5 ? 0x100 : 0, ch = R(3);
      if (r < 0.3) { const base = [0x30, 0x40, 0x50, 0x60, 0x70, 0x80, 0x90][R(7)], slot = [0, 4, 8, 12][R(4)]; c.ymWrite(part | (base + slot + ch), base === 0x40 ? R(64) : base === 0x90 ? (rnd() < 0.6 ? 0 : R(16)) : R(256)); }
      else if (r < 0.38) c.ymWrite(part | (0xB0 + ch), R(64));
      else if (r < 0.44) c.ymWrite(part | (0xB4 + ch), (R(4) << 6) | R(64));
      else if (r < 0.52) { const bf = R(0x4000); c.ymWrite(part | (0xA4 + ch), bf >> 8); c.ymWrite(part | (0xA0 + ch), bf & 0xFF); }
      else if (r < 0.58) c.ymWrite(0x28, (R(16) << 4) | (part ? 4 : 0) | ch);
      else if (r < 0.6) c.ymWrite(0x22, R(16));
      else if (r < 0.61) c.ymWrite(0x27, rnd() < 0.5 ? 0x40 : 0);
      else if (r < 0.62) { const bf = R(0x4000); c.ymWrite(0xAC + ch, bf >> 8); c.ymWrite(0xA8 + ch, bf & 0xFF); }
      else if (r < 0.64) { c.ymWrite(0x2B, rnd() < 0.3 ? 0x80 : 0); c.ymWrite(0x2A, R(256)); c.ymWrite(0x2C, R(16)); }
      else if (r < 0.7) { const pc = R(4), vol = rnd() < 0.5; c.psgWrite(0x80 | (pc << 5) | (vol ? 16 : 0) | R(16)); if (!vol && rnd() < 0.7) c.psgWrite(rnd() < 0.15 ? 0 : R(64)); }
      else if (r < 0.72) c.psgWrite(0x80 | R(3) << 5 | (rnd() < 0.5 ? 0 : 1));
      else if (r < 0.8) c.ymRun(1 + R(rnd() < 0.2 ? 3000 : 60));
      else if (r < 0.85) c.psgRun(1 + R(rnd() < 0.2 ? 5000 : 300));
      else { const n = 1 + R(700); c.render(bl, br, 0, n); for (let i = 0; i < n; i++) { L.push(bl[i]); Rr.push(br[i]); } }
    }
    out.push({ name: `fuzz-${String(s).padStart(2, '0')}`, sr, ops: c.trace, pcm: Float32Array.from([...L, ...Rr]) });
  }
  return out;
}

// renderOffline's own conversion (frame = round(time x rate), ids in order) as engine events for NanoGenesisParity notes; presets normalized
function engineScript(N, sc) {
  const kit = N._kit, presets = {}, events = [];
  let nextId = 1, anon = 0;
  const eng = new kit.Engine(sc.sr, {});
  for (const ev of sc.ev) {
    const frame = Math.round((ev.time || 0) * sc.sr);
    if (ev.type === 'on') {
      const p = eng.resolvePreset(ev.preset ?? 'brass'); if (typeof ev.preset !== 'string') p.name = 'anon' + anon++; presets[p.name] = p;
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
  execFileSync(exe, ['kernel', path.join(dir, 'kernel.f32')], { stdio: 'ignore' });
  const kd = sameBits(N._kit.BL_KERNEL, readF32(path.join(dir, 'kernel.f32')));
  if (kd) fail('kernel: ' + kd);
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
    samples += c.pcm.length / 2; writes += c.ops.filter(o => o[0] === 0 || o[0] === 2).length;
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
  return { fails, ok, total: cases.length, samples, writes, kernelOk: !kd, engOk, engN, midOk, midN };
}

const exe = parityExe();
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'nano-genesis-parity-'));
const text = fs.readFileSync(distFile, 'utf8');
const t0 = Date.now();
const res = runAll(loadLib(text), exe, dir, false);
console.log(`kernel: ${res.kernelOk ? 'bit-exact' : 'DIFFERS'}; ${res.ok}/${res.total} traces bit-exact (${res.total - 16} note scripts at ${RATES.join(', ')} Hz, 16 register fuzz streams); C# engine twin ${res.engOk}/${res.engN} scripts, plugin-way MIDI ${res.midOk}/${res.midN}; ${res.samples} samples, ${res.writes} chip writes, ${((Date.now() - t0) / 1000).toFixed(1)} s`);
let bad = res.fails;

if (args.includes('--mutants')) {
  const MUTANTS = [
    ['ladder offset +4 -> +3', 'accL += (cpan & 2) ? v + (v < 0 ? -3 : 4)', 'accL += (cpan & 2) ? v + (v < 0 ? -3 : 3)'],
    ['output scale 64/65 dropped', 'out[0] = Math.trunc(accL * 8192 / 390);', 'out[0] = Math.trunc(accL * 128 / 6);'],
    ['channel clamp 255 -> 256', "function ymClamp9(v) { return v < -256 ? -256 : v > 255 ? 255 : v; }", "function ymClamp9(v) { return v < -256 ? -256 : v > 256 ? 256 : v; }"],
    ['feedback shift', 'const mod1 = fb !== 0 ? (a1 + a2) >> (10 - fb) : 0;', 'const mod1 = fb !== 0 ? (a1 + a2) >> (9 - fb) : 0;'],
    ['algorithm 5 modulator', 'case 5: m2 = a1; m3 = a2; m4 = a1; break;', 'case 5: m2 = a1; m3 = a1; m4 = a1; break;'],
    ['envelope divide by three', 'if ((this.envCounter & 3) === 3) this.envCounter = (this.envCounter + 1)', 'if ((this.envCounter & 3) === 3) this.envCounter = (this.envCounter + 2)'],
    ['late slot env tick removed', 'const env = n === 3 && ch >= 4 ? envPrevious : this.envCounter;', 'const env = this.envCounter;'],
    ['fast-rate phase', 'const YM_FAST_PHASE = [0, 3, 1, 1];', 'const YM_FAST_PHASE = [0, 1, 1, 1];'],
    ['detune sign', 'o.detune = (detuneReg & 4) ? -adjust : adjust;', 'o.detune = (detuneReg & 4) ? adjust : adjust;'],
    ['multiple 0 = x1', 'o.multiple = multi === 0 ? 1 : multi * 2;', 'o.multiple = multi === 0 ? 2 : multi * 2;'],
    ['sustain level 15 not 31', 'sl |= (sl + 1) & 0x10;', 'sl |= 0;'],
    ['lfo AM delay', 'const YM_LFO_DELAY = [0, 0, 0, 0, 0, 1,', 'const YM_LFO_DELAY = [0, 0, 0, 0, 1, 1,'],
    ['ssg hold value', "if (o.envState !== EG_ATTACK) o.envAtt = o.ssgInverted !== 0 ? 0x200 : 0x3FF;", "if (o.envState !== EG_ATTACK) o.envAtt = o.ssgInverted !== 0 ? 0x1FF : 0x3FF;"],
    ['FM step not float32', "const l = f32(p[0] * (1 / 32768)), r = f32(p[1] * (1 / 32768));", "const l = p[0] * (1 / 32767), r = f32(p[1] * (1 / 32768));"],
    ['FM read rounds', "let l = ps[i] + (f32(fl[i] * 32768) | 0);", "let l = ps[i] + Math.round(fl[i] * 32768);"],
    ['PSG MD variant holds', "tone[i] ^= 1; changed = true;", "if (p > 1) tone[i] ^= 1; changed = true;"],
    ['PSG noise taps', 'fb = (this.noise & 4) ? ((s ^ (s >> 3)) & 1)', 'fb = (this.noise & 4) ? ((s ^ (s >> 2)) & 1)'],
    ['kernel cutoff', 'cutoff = 0.94', 'cutoff = 0.95'],
    ['dac sign', 'const dac = (this.dacData << 23) >> 23', 'const dac = (this.dacData << 23) >>> 23'],
    ['key edge not given a sample', 'if (this.keyAt[ch] === this.chip.ymSamples) this.chip.ymRun(1);', ''],
    ['lfo pm sign', "return ((c >>> 14) & 1) ? -pm : pm;", "return pm;"],
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
