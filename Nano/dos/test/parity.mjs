// Nano DOS's parity proof: the web library's AdLib (MAME 0.36 OPL2) + PC speaker against Core's Mame036Opl + NanoDosSound, sample for sample.
//   node Nano/dos/test/parity.mjs [--mutants] [--keep]
//
//   tables   the OPL2's tables built in JS (Math.pow / sin / log10) = Core's Mame036Opl tables, entry for entry
//   scripts  note scripts through the shipped dist/nano-dos.js (engine + chip, five host rates); every chip operation recorded (AdLib writes, speaker
//            changes, 44.1 kHz samples, reads) and replayed through Core by tools/NanoDosParity: the PCM must be identical
//   fuzz     random register streams straight into the chip: every operator register of every voice, waveforms, feedback, both connections, key-on edges,
//            the rhythm mode and its five drums, the AM / vibrato depths, register 08h's CSM / note-select, the speaker's counts and gating
// Tolerance: zero (raw float32 bits). --mutants injects one deliberate error at a time into a copy of the library and requires each to be caught.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import vm from 'node:vm';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..');
const distFile = path.join(root, 'dist', 'nano-dos.js');
const args = process.argv.slice(2);

function parityExe() {
  const art = process.env.NANO_ARTIFACTS ?? path.join(process.env.BROKENNES_WORK || 'C:\\BrokenNes-work', 'bn_nano_dos');
  execFileSync('dotnet', ['build', path.join(root, 'tools', 'NanoDosParity', 'NanoDosParity.csproj'), '-c', 'Release', '--artifacts-path', art, '-v', 'q', '-nologo'], { stdio: ['ignore', 'ignore', 'inherit'] });
  const exe = path.join(art, 'bin', 'NanoDosParity', 'release', 'NanoDosParity.exe');
  if (!fs.existsSync(exe)) throw new Error('NanoDosParity.exe not found at ' + exe);
  return exe;
}
function loadLib(text) {
  const m = { exports: {} };
  vm.runInNewContext(text, { module: m, exports: m.exports, Float32Array, Float64Array, Int32Array, Int16Array, Uint8Array, Uint16Array, Int8Array, Math, Map, Set, Object, Array, JSON, Number, String, Error, TypeError, RangeError, ArrayBuffer, Uint32Array, Symbol, console });
  return m.exports;
}
const readF32 = (f) => { const b = fs.readFileSync(f); return new Float32Array(b.buffer.slice(b.byteOffset, b.byteOffset + b.length)); };
const sameBits = (a, b) => { if (a.length !== b.length) return `length ${a.length} vs ${b.length}`; const x = new Uint32Array(a.buffer, a.byteOffset, a.length), y = new Uint32Array(b.buffer, b.byteOffset, b.length); for (let i = 0; i < x.length; i++) if (x[i] !== y[i]) return `first difference at ${i}: ${a[i]} vs ${b[i]}`; return null; };

const RATES = [11025, 22050, 44100, 48000, 96000];
function scripts() {
  const out = [];
  const melody = (pre, t0 = 0) => [60, 64, 67, 72, 76, 79, 84, 88].map((n, i) => ({ time: t0 + i * 0.09, type: 'on', note: n, preset: pre, velocity: 0.4 + 0.075 * i, duration: 0.08 }));
  let k = 0;
  for (const sr of RATES) for (const v of [0, 1]) {
    const ev = v === 0 ? [...melody('piano'), ...melody('svpulse25', 0.05), ...melody('beep', 0.02), { time: 0, type: 'on', note: 36, preset: 'bass', duration: 0.6 },
      { time: 0.1, type: 'on', note: 60, preset: 'snare' }, { time: 0.3, type: 'on', note: 84, preset: 'hat' }, { time: 0.4, type: 'on', note: 60, preset: 'strings', duration: 0.3 },
      { time: 0.2, type: 'on', note: 70, preset: 'laser' }, { time: 0.5, type: 'on', note: 72, preset: 'powerup' }, { time: 0.35, type: 'param', name: 'pitchBend', value: -150 },
      { time: 0.62, type: 'on', note: 84, preset: 'crash' }, { time: 0.7, type: 'on', note: 60, preset: 'bell', duration: 0.3 }, { time: 0.75, type: 'kill' }, { time: 0.8, type: 'on', note: 20, preset: 'organ', duration: 0.1 },
      { time: 0.9, type: 'on', note: 120, preset: 'svtriangle', duration: 0.05 }, { time: 0.95, type: 'on', note: 36, preset: 'kick' }, { time: 0.97, type: 'on', note: 36, preset: 'explosion' }, { time: 0.3, type: 'on', note: 50, preset: 'piano', channel: 3, duration: 0.6 }, { time: 0.4, type: 'on', note: 52, preset: 'piano', channel: 3, duration: 0.3 }]
      : [{ time: 0, type: 'on', note: 60, preset: { kind: 'fm', regs: [0xE3, 0x61, 0x8A, 0x40, 0x53, 0x47, 0x24, 0x35, 3, 1, 0x0B], r: 100 }, duration: 0.5 },
        { time: 0.01, type: 'on', note: 64, preset: 'svnoise', duration: 0.4 }, { time: 0.05, type: 'on', note: 48, preset: 'svpulse12', duration: 0.4 }, { time: 0.1, type: 'on', note: 52, preset: 'svmix', duration: 0.4 },
        { time: 0.12, type: 'on', note: 55, preset: 'vaults', duration: 0.4 }, { time: 0.14, type: 'on', note: 59, preset: 'epiano', duration: 0.4 }, { time: 0.16, type: 'on', note: 62, preset: 'flute', duration: 0.4 },
        { time: 0.18, type: 'on', note: 65, preset: 'lead', duration: 0.4 }, { time: 0.2, type: 'on', note: 67, preset: 'brass', duration: 0.2 }, { time: 0.22, type: 'on', note: 69, preset: 'coin' },
        { time: 0.3, type: 'on', note: 72, preset: 'beeparp', duration: 0.3 }, { time: 0.45, type: 'alloff' }, { time: 0.6, type: 'on', note: 64, preset: 'svperc', duration: 0.2, bend: 50 },
        { time: 0.6, type: 'on', note: 67, preset: 'svpulse50', duration: 0.2, channel: 0 }, { time: 0.7, type: 'on', note: 40, preset: 'beeplead', duration: 0.3 }, { time: 0.8, type: 'on', note: 108, preset: 'blip' }];
    out.push({ name: `dos${v}-${sr}`, sr, ev, seconds: 1.1 + (k++ % 3) * 0.013 });
  }
  return out;
}
function fuzzStreams(N) {
  const out = [];
  let seed = 777;
  const rnd = () => { seed = (seed * 1103515245 + 12345) >>> 0; return seed / 4294967296; };
  const R = (n) => (rnd() * n) | 0;
  const OPS = [0, 1, 2, 3, 4, 5, 8, 9, 10, 11, 12, 13, 16, 17, 18, 19, 20, 21];
  for (let s = 0; s < 16; s++) {
    const sr = RATES[s % RATES.length], c = new N._kit.Chip(sr);
    c.trace = [];
    const L = [], Rr = [];
    const bl = new Float32Array(4096), br = new Float32Array(4096);
    if (rnd() < 0.7) c.oplWrite(0x01, 0x20);
    for (let step = 0; step < 600; step++) {
      const r = rnd();
      if (r < 0.3) c.oplWrite([0x20, 0x40, 0x60, 0x80, 0xE0][R(5)] + OPS[R(18)], R(256));
      else if (r < 0.38) c.oplWrite(0xA0 + R(9), R(256));
      else if (r < 0.48) c.oplWrite(0xB0 + R(9), R(64));
      else if (r < 0.52) c.oplWrite(0xC0 + R(9), R(16));
      else if (r < 0.56) c.oplWrite(0xBD, (s & 1 ? 0x20 : 0) | R(256));
      else if (r < 0.57) c.oplWrite([0x01, 0x02, 0x03, 0x04, 0x08][R(5)], R(256));
      else if (r < 0.6) c.speaker(rnd() < 0.7, 1 + R(rnd() < 0.3 ? 65535 : 3000));
      else if (r < 0.8 && c.out.count < 20000) c.run(1 + R(rnd() < 0.2 ? 4000 : 80));
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
      if (ev.bend !== undefined) e.bend = ev.bend;
      if (ev.duration !== undefined) e.duration = ev.duration;
      events.push(e);
    } else if (ev.type === 'off') events.push(ev.id !== undefined ? { frame, t: 'off', id: ev.id } : { frame, t: 'off', note: N.noteNumber(ev.note) });
    else if (ev.type === 'bend') events.push({ frame, t: 'bend', id: ev.id, cents: ev.cents });
    else if (ev.type === 'param') events.push({ frame, t: ev.name, value: ev.value });
    else events.push({ frame, t: ev.type });
  }
  return { sampleRate: sc.sr, frames: Math.round(sc.seconds * sc.sr), presets, events };
}

function runAll(N, exe, dir, quiet) {
  let fails = 0;
  const fail = (m) => { fails++; if (!quiet) console.log('FAIL ' + m); };
  execFileSync(exe, ['tables', path.join(dir, 'tables.i32')], { stdio: 'ignore' });
  { const kit = N._kit, mine = Int32Array.from([...kit.TL_TABLE, ...kit.SIN_TABLE, ...kit.AMS_TABLE, ...kit.VIB_TABLE, ...kit.ENV_CURVE, ...kit.KSL_TABLE, ...kit.SL_TABLE]);
    const b = fs.readFileSync(path.join(dir, 'tables.i32')), core = new Int32Array(b.buffer.slice(b.byteOffset, b.byteOffset + b.length));
    let d = -1; if (mine.length !== core.length) d = -2; else for (let i = 0; i < mine.length; i++) if (mine[i] !== core[i]) { d = i; break; }
    if (d !== -1) fail(`OPL2 tables differ from Core's (${d === -2 ? 'length' : 'entry ' + d})`); }
  const cases = [];
  for (const sc of scripts()) {
    const r = N.renderOffline(sc.ev, sc.seconds, { sampleRate: sc.sr, stereo: true, trace: true });
    cases.push({ name: sc.name, sr: sc.sr, ops: r.trace, pcm: Float32Array.from([...r.left, ...r.right]) });
  }
  cases.push(...fuzzStreams(N));
  for (const f of fs.readdirSync(dir)) if (f !== 'tables.i32') fs.unlinkSync(path.join(dir, f));
  for (const c of cases) fs.writeFileSync(path.join(dir, c.name + '.trace.json'), JSON.stringify({ rate: c.sr, ops: c.ops }));
  try { execFileSync(exe, ['replay', dir], { stdio: ['ignore', 'ignore', quiet ? 'ignore' : 'inherit'] }); } catch (e) { fail('replay threw'); return { fails }; }
  let samples = 0, writes = 0, ok = 0;
  for (const c of cases) {
    const d = sameBits(c.pcm, readF32(path.join(dir, c.name + '.core.f32')));
    samples += c.pcm.length / 2; writes += c.ops.filter(o => o[0] === 0 || o[0] === 6).length;
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
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'nano-dos-parity-'));
const text = fs.readFileSync(distFile, 'utf8');
const t0 = Date.now();
const res = runAll(loadLib(text), exe, dir, false);
console.log(`OPL2 tables = Core's; ${res.ok}/${res.total} traces bit-exact (${res.total - 16} note scripts at ${RATES.join(', ')} Hz, 16 register fuzz streams); C# engine twin ${res.engOk}/${res.engN} scripts, plugin-way MIDI ${res.midOk}/${res.midN}; ${res.samples} samples, ${res.writes} chip writes, ${((Date.now() - t0) / 1000).toFixed(1)} s`);
let bad = res.fails;

if (args.includes('--mutants')) {
  const MUTANTS = [
    ['TL table: 10^(x/20) as 10^(x/21)', 'Math.pow(10, EG_STEP * t / 20)', 'Math.pow(10, EG_STEP * t / 21)'],
    ['attack curve power 8 -> 7', 'Math.pow((EG_ENT - 1 - i) / EG_ENT, 8)', 'Math.pow((EG_ENT - 1 - i) / EG_ENT, 7)'],
    ['release-rate overrun kept', 'D[0] = EG_AED - 1; D[75] = 0;', 'D[75] = 0;'],
    ['LFO cache refreshed', "if (!this.cached) { this.cached = true;", "if (true) { this.cached = true;"],
    ['vibrato before divide', 'Math.floor((Math.imul(s.Incr, this.vib) >>> 0) / VIB_RATE)', 'Math.imul(Math.floor(s.Incr / VIB_RATE), this.vib)'],
    ['feedback shift', 'ch.FB = feedback !== 0 ? (8 + 1) - feedback : 0;', 'ch.FB = feedback !== 0 ? (8 + 2) - feedback : 0;'],
    ['key-off envelope start', 'if ((s.evc & EG_DST) === 0) s.evc = EG_DST;', 'if ((s.evc & EG_DST) === 0) s.evc = EG_DST + 1;'],
    ['snare x8 -> x4', 'this.opOut(s7_1, env_sd, 0) * 8', 'this.opOut(s7_1, env_sd, 0) * 4'],
    ['white noise seed', 'this.randSeed = (Math.imul(this.randSeed, 214013) + 2531011) >>> 0;', 'this.randSeed = (Math.imul(this.randSeed, 214013) + 2531013) >>> 0;'],
    ['waveform select ignored', 'if (this.wavesel !== 0) this.P_CH[(slot / 2) | 0].SLOT[slot & 1].wavetable = (v & 0x03) * SIN_ENT;', ''],
    ['KSL shift', 's.ksl = ksl !== 0 ? 3 - ksl : 31;', 's.ksl = ksl !== 0 ? 2 - ksl : 31;'],
    ['output shift', 'return (data >> OPL_OUTSB) << 16 >> 16;', 'return (data >> (OPL_OUTSB + 1)) << 16 >> 16;'],
    ['speaker half period', 'half = (c + 1) >> 1;', 'half = c >> 1;'],
    ['speaker 3 points', 'for (let k = 0; k < 4; k++) { let t = t0 + Math.floor((t1 - t0) * k / 4);', 'for (let k = 0; k < 4; k++) { let t = t0 + Math.floor((t1 - t0) * (k & 2) / 4);'],
    ['speaker phase kept on reload', 'if (count !== this.spkCount || !this.spkOn && on) { this.spkCount = count; this.spkLoad = pitOfSample(this.samples); }', 'if (count !== this.spkCount || !this.spkOn && on) { this.spkCount = count; }'],
    ['mix not saturated', 's = s < -32768 ? -32768 : s > 32767 ? 32767 : s;\n      this.out.writeOne', '\n      this.out.writeOne'],
    ['resampler not float32', 'out[off + i] = f32(f32(f32(f32(f32(f32(c3 * t) + c2) * t) + c1) * t) + y1);', 'out[off + i] = ((c3 * t + c2) * t + c1) * t + y1;'],
    ['retrigger without key-off', 'if (trig) { if (this.keyed[ch]) this.setFreq(ch, this.regFreq[ch] & 0x1FFF, false); this.loadPatch(ch, p); }', 'if (trig) { this.loadPatch(ch, p); }'],
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
