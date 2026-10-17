// The parity proof: the web module against Core, sample for sample.   node Nano/test/parity.mjs [--mutants] [--keep] [--filter text]
//
// For every script in scenarios.mjs the web module (Nano/dist/nano.js, the shipped file) renders PCM and records every chip register write it made.
//   CHIP check      the recorded writes are replayed through Core's NesApuInstrument (FIX, the chip behind the FL plugin) by tools/NanoParity;
//                   the PCM must be bit-for-bit the web module's.
//   INSTRUMENT check the note script is run through a second implementation of the instrument layer (C#, NanoReference.cs) on Core's
//                   NesApuInstrument; it must write the same registers at the same samples and produce the same PCM.
// Tolerance: zero. Every float of the chip is a float32 in both languages, so the PCM is compared as raw bits.
//
// --mutants   proves the check can fail: it injects one deliberate error at a time into a copy of the module (chip, resampler, mixer, voice
//             handling, rounding ...) and requires each to be caught. Exit code 1 if any real check fails or any mutant survives.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { scenarios } from './scenarios.mjs';
import { loadNano } from './lib.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const nanoRoot = path.resolve(here, '..');
const distFile = path.join(nanoRoot, 'dist', 'nano.js');
const args = process.argv.slice(2);
const keep = args.includes('--keep');
const filter = args.includes('--filter') ? args[args.indexOf('--filter') + 1] : null;


// ---- the Core side ----
function parityExe() {
  if (process.env.NANO_PARITY_EXE) return process.env.NANO_PARITY_EXE;
  const art = process.env.NANO_ARTIFACTS ?? path.join(os.tmpdir(), 'brokennes-nano-artifacts');
  const exe = path.join(art, 'bin', 'NanoParity', 'release', 'NanoParity.exe');
  const csproj = path.join(nanoRoot, 'tools', 'NanoParity', 'NanoParity.csproj');
  // always (re)build: dotnet makes this a no-op when nothing changed, and a stale exe would verify yesterday's Core
  execFileSync('dotnet', ['build', csproj, '-c', 'Release', '--artifacts-path', art, '-v', 'q', '-nologo'], { stdio: ['ignore', 'ignore', 'inherit'] });
  if (!fs.existsSync(exe)) throw new Error(`NanoParity.exe not found at ${exe}`);
  return exe;
}

function readF32(file) {
  const b = fs.readFileSync(file);
  return new Float32Array(b.buffer.slice(b.byteOffset, b.byteOffset + b.length));
}

function comparePcm(a, b) {
  if (a.length !== b.length) return { ok: false, why: `length ${a.length} vs ${b.length}` };
  const ia = new Uint32Array(a.buffer, a.byteOffset, a.length), ib = new Uint32Array(b.buffer, b.byteOffset, b.length);
  let diffs = 0, first = -1, maxAbs = 0, peak = 0;
  for (let i = 0; i < a.length; i++) {
    peak = Math.max(peak, Math.abs(b[i]));
    if (ia[i] !== ib[i]) { diffs++; if (first < 0) first = i; maxAbs = Math.max(maxAbs, Math.abs(a[i] - b[i])); }
  }
  return { ok: diffs === 0, diffs, first, maxAbs, peak, why: diffs ? `${diffs} samples differ, first at ${first}, largest difference ${maxAbs}` : '' };
}

function sameTrace(a, b) {
  if (a.length !== b.length) return { ok: false, why: `trace length ${a.length} vs ${b.length}` };
  for (let i = 0; i < a.length; i++) {
    if (a[i].length !== b[i].length || a[i].some((x, k) => x !== b[i][k])) return { ok: false, why: `trace op ${i}: [${a[i]}] vs [${b[i]}]` };
  }
  return { ok: true, why: '' };
}

// Runs the scripts through a module and Core; returns [{name, chip, instrument, samples, writes, peak}]
export function runParity(Nano, list, exe) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'nano-parity-'));
  const js = new Map();
  for (const sc of list) {
    const pcm = Nano.renderOffline(sc.events, sc.seconds, { sampleRate: sc.sampleRate, presets: sc.presets, samples: sc.samples, trace: true });
    js.set(sc.name, pcm);
    fs.writeFileSync(path.join(dir, sc.name + '.trace.json'), JSON.stringify({ core: 'FIX', rate: sc.sampleRate, mask: 15, ops: pcm.trace, dmcRom: pcm.dmcRom ? Buffer.from(pcm.dmcRom).toString('base64') : undefined }));
    fs.writeFileSync(path.join(dir, sc.name + '.scn.json'), JSON.stringify({ sampleRate: sc.sampleRate, frames: pcm.length, presets: pcm.logPresets, events: pcm.log, dmcRom: pcm.dmcRom ? Buffer.from(pcm.dmcRom).toString('base64') : undefined }));
  }
  execFileSync(exe, ['replay', dir], { stdio: ['ignore', 'ignore', 'inherit'] });
  execFileSync(exe, ['notes', dir], { stdio: ['ignore', 'ignore', 'inherit'] });
  const results = [];
  for (const sc of list) {
    const pcm = js.get(sc.name);
    const core = readF32(path.join(dir, sc.name + '.core.f32'));
    const ref = readF32(path.join(dir, sc.name + '.ref.f32'));
    const refTrace = JSON.parse(fs.readFileSync(path.join(dir, sc.name + '.ref.trace.json'), 'utf8'));
    const chip = comparePcm(pcm, core);
    const tr = sameTrace(pcm.trace, refTrace);
    const inst = comparePcm(pcm, ref);
    results.push({
      name: sc.name, samples: pcm.length, writes: pcm.trace.filter(o => o[0] === 0).length, peak: chip.peak,
      chip, instrument: { ok: tr.ok && inst.ok, why: tr.ok ? inst.why : tr.why },
    });
  }
  if (!keep) fs.rmSync(dir, { recursive: true, force: true });
  else console.log('kept ' + dir);
  return results;
}

// The chip alone, driven by seeded random register writes ($4000-$4017 except the DMC registers and the DMC enable: every channel, the sweep units, the
// frame counter in both modes, the length counters, at six host rates), replayed through Core's NesApuInstrument. This is what notices a change in
// Windows/NesEmulator/apus/APU_FIX.cs that the note scripts do not reach (they use only the 4-step frame counter and a few registers).
export function runChipFuzz(Nano, exe, { rates = [44100, 48000, 22050, 96000, 32000, 16000], seeds = [1, 2, 3], steps = 300 } = {}) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'nano-fuzz-'));
  const mine = new Map(), names = [];
  const rng = (seed) => { let s = seed >>> 0; return () => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return s / 4294967296; }; };
  for (const rate of rates) for (const seed of seeds) {
    const r = rng(seed * 7919 + rate), chip = new Nano._kit.Chip(rate, 0x0F);
    const ops = [], chunks = [], name = `fuzz_${rate}_${seed}`;
    for (let step = 0; step < steps; step++) {
      for (let k = Math.floor(r() * 6); k > 0; k--) {
        const reg = Math.floor(r() * 0x18);
        if ((reg >= 0x10 && reg <= 0x13) || reg === 0x14 || reg === 0x16) continue;
        let v = Math.floor(r() * 256);
        if (reg === 0x15) v &= 0x0F;
        if (reg === 0x17 && r() < 0.5) v |= 0x40;
        ops.push([0, reg, v]); chip.write(reg, v);
      }
      const n = 1 + Math.floor(r() * (r() < 0.2 ? 3000 : 300));
      const out = new Float32Array(n); chip.render(out, 0, n);
      const last = ops[ops.length - 1]; if (last && last[0] === 1) last[1] += n; else ops.push([1, n]);
      chunks.push(out);
    }
    const pcm = new Float32Array(chunks.reduce((a, c) => a + c.length, 0)); let o = 0; for (const c of chunks) { pcm.set(c, o); o += c.length; }
    fs.writeFileSync(path.join(dir, name + '.trace.json'), JSON.stringify({ core: 'FIX', rate, mask: 15, ops }));
    mine.set(name, pcm); names.push(name);
  }
  execFileSync(exe, ['replay', dir], { stdio: ['ignore', 'ignore', 'inherit'] });
  const results = names.map(name => ({ name, cmp: comparePcm(mine.get(name), readF32(path.join(dir, name + '.core.f32'))) }));
  if (!keep) fs.rmSync(dir, { recursive: true, force: true });
  return results;
}

// ---- mutants ----
const MUTANTS = [
  // the chip
  ['chip: low-pass coefficient 0.15 -> 0.16', 'const LOW_PASS = fround(0.15)', 'const LOW_PASS = fround(0.16)'],
  ['chip: DC filter 0.995 -> 0.994', 'DC_R = fround(0.995)', 'DC_R = fround(0.994)'],
  ['chip: low-pass kept in double precision (no float32 rounding)', 'this.lpLast = fround(this.lpLast + fround(fround(mixed - this.lpLast) * LOW_PASS));', 'this.lpLast = this.lpLast + fround(fround(mixed - this.lpLast) * LOW_PASS);'],
  ['chip: mixer constant 95.88 -> 95.89', 'c9588 = fround(95.88)', 'c9588 = fround(95.89)'],
  ['chip: one entry of the 50% duty sequence', '0, 1, 1, 1, 1, 0, 0, 0,', '0, 1, 1, 1, 0, 0, 0, 0,'],
  ['chip: noise period table 508 -> 507', '380, 508, 762', '380, 507, 762'],
  ['chip: short-mode noise tap 6 -> 5', '((s >> 6) & 1)', '((s >> 5) & 1)'],
  ['chip: pulse divider (timer+1)*2 off by one', 'pTC[0] = (pTimer[0] + 1) * 2;', 'pTC[0] = (pTimer[0] + 1) * 2 + 1;'],
  ['chip: triangle timer reload off by one', 'this.triTimerCounter = this.triTimer + 1;\n        if (this.triEnabled', 'this.triTimerCounter = this.triTimer + 2;\n        if (this.triEnabled'],
  ['chip: pulse 1 sweep ones-complement lost', "(i === 0 ? 1 : 0)) : (timer + change);\n      if (timer >= 8", "(i === 0 ? 0 : 0)) : (timer + change);\n      if (timer >= 8"],
  ['chip: the sweep ignores its enable bit', 'this.pSwDiv[i] <= 0 && this.pSwEnable[i] && shift !== 0', 'this.pSwDiv[i] <= 0 && shift !== 0'],
  ['chip: a write to $4008 sets the linear counter reload flag', 'this.triHalt = (value & 0x80) ? 1 : 0; break;', 'this.triHalt = (value & 0x80) ? 1 : 0; this.triReload = 1; break;'],
  ['chip: 5-step frame counter with the half frame one step early', 'case 1: this.quarterFrame(); this.halfFrame(); this.nextFrameEvent = 22371; break;\n          case 2: this.quarterFrame(); this.nextFrameEvent = 29829; break;\n          case 3: this.nextFrameEvent = 37281; break;', 'case 1: this.quarterFrame(); this.nextFrameEvent = 22371; break;\n          case 2: this.quarterFrame(); this.halfFrame(); this.nextFrameEvent = 29829; break;\n          case 3: this.nextFrameEvent = 37281; break;'],
  ['chip: frame sequencer step 22371 -> 22372', 'this.nextFrameEvent = 22371; break;\n          case 2: this.quarterFrame(); this.nextFrameEvent = 29829; break;\n          case 3: this.quarterFrame(); this.halfFrame(); this.frameStep = -1', 'this.nextFrameEvent = 22372; break;\n          case 2: this.quarterFrame(); this.nextFrameEvent = 29829; break;\n          case 3: this.quarterFrame(); this.halfFrame(); this.frameStep = -1'],
  ['chip: Hermite coefficient 1.5 -> 1.4', 'fround(1.5 * fround(y1 - y2))', 'fround(1.4 * fround(y1 - y2))'],
  ['chip: resampler step computed with a rounded input rate', 'this.rsStep = NATIVE_RATE / hostRate;\n\n    // NesApuInstrument.Attach', 'this.rsStep = 44100.5 / hostRate;\n\n    // NesApuInstrument.Attach'],
  // DPCM
  ['dmc: delta step +2 -> +1', 'if (this.dmcDelta <= 125) this.dmcDelta += 2;', 'if (this.dmcDelta <= 125) this.dmcDelta += 1;'],
  ['dmc: sample bytes read one address off', 'mem[this.dmcAddress & 0x3FFF]', 'mem[(this.dmcAddress + 1) & 0x3FFF]'],
  ['dmc: rate table entry 54 -> 55', '106, 84, 72, 54]', '106, 84, 72, 55]'],
  ['dmc: loop flag ignored', 'this.dmcLoop = (value & 0x40) ? 1 : 0;', 'this.dmcLoop = 0;'],
  ['dmc: the engine forgets the start level', 'this.wf(0x11, p.dmcStart);', ''],
  // the instrument layer
  ['engine: round-half-up instead of round-to-even', 'return (f % 2 === 0) ? f : f + 1;', 'return f + 1;'],
  ['engine: pulse period formula - 1.0 -> - 1.2', 'CPU_HZ / (16.0 * freq) - 1.0', 'CPU_HZ / (16.0 * freq) - 1.2'],
  ['engine: stealing takes the newest voice, not the oldest', 'if (top.start < bestAt) {', 'if (top.start > -1) {'],
  ['engine: a new note does not retrigger the channel', 'v.fresh || !this.gate[ch] ||', '!this.gate[ch] ||'],
  ['engine: release tail 1% too long', 'v.relLevel * Math.max(0, 1 - rt / p.r)', 'v.relLevel * Math.max(0, 1 - rt / (p.r * 1.01))'],
  ['engine: vibrato rising slope 4 -> 3.9', 'ph < 0.25 ? 4 * ph :', 'ph < 0.25 ? 3.9 * ph :'],
  ['engine: noise index divisor 3 -> 3.1', 'idx = clamp(15 - Math.floor((semis - 24.0) / 3.0), 0, 15);', 'idx = clamp(15 - Math.floor((semis - 24.0) / 3.1), 0, 15);'],
  ['engine: one-shots obey note-off', 'if (v.p.oneShot) return;', 'if (false) return;'],
  ['engine: control tick every 63 samples', 'const CHUNK = 64;', 'const CHUNK = 63;'],
  ['engine: every register written even when unchanged', 'w(reg, value) { if (this.chip.shadow[reg] !== value) this.wf(reg, value); }', 'w(reg, value) { this.wf(reg, value); }'],
  ['engine: triangle off writes 0x81', 'else if (ch === 2) this.w(8, 0x80);', 'else if (ch === 2) this.w(8, 0x81);'],
  ['engine: velocity ignores the preset gain', 'const vol = v.vel * p.gain * this.env(v, this.frame);', 'const vol = v.vel * this.env(v, this.frame);'],
];

const exe = parityExe();
const text = fs.readFileSync(process.env.NANO_DIST ?? distFile, 'utf8');
let list = scenarios(loadNano(text));
if (filter) list = list.filter(s => s.name.includes(filter));
let failed = 0;

console.log(`PARITY: ${list.length} scripts against Core (${exe})`);
const t0 = Date.now();
const res = runParity(loadNano(text), list, exe);
let totalSamples = 0, totalWrites = 0;
for (const r of res) {
  totalSamples += r.samples; totalWrites += r.writes;
  const bad = !r.chip.ok || !r.instrument.ok;
  if (bad) failed++;
  if (bad || args.includes('-v')) console.log(`${bad ? 'FAIL' : 'ok  '} ${r.name.padEnd(28)} ${String(r.samples).padStart(7)} samples ${String(r.writes).padStart(5)} writes  chip: ${r.chip.ok ? 'bit-exact' : r.chip.why}  instrument: ${r.instrument.ok ? 'bit-exact' : r.instrument.why}`);
}
const silent = res.filter(r => r.peak < 1e-4).map(r => r.name);
console.log(`${res.length - failed}/${res.length} scripts bit-exact on both checks; ${totalSamples} samples, ${totalWrites} register writes, ${((Date.now() - t0) / 1000).toFixed(1)} s`);
if (silent.length) { console.log('SILENT scripts (a test that proves nothing): ' + silent.join(', ')); failed++; }
{
  const fz = runChipFuzz(loadNano(text), exe);
  const bad = fz.filter(f => !f.cmp.ok);
  console.log(`chip fuzz: ${fz.length - bad.length}/${fz.length} random register streams (all registers, both frame-counter modes, six host rates) bit-exact against Core` + (bad.length ? ': ' + bad.map(b => b.name + ' ' + b.cmp.why).join('; ') : ''));
  if (bad.length) failed++;
}

if (args.includes('--mutants')) {
  // a smaller set of scripts is enough to see each fault, and keeps a shared machine polite
  const subset = scenarios(loadNano(text)).filter(s => /^(voice_handling|sweep_unit|bends_and_timing|velocity_ladder|pitch_extremes|passage_44100|random_[1-5]|dpcm_basic|dpcm_voices)$|^preset_(lead|snare|zap|coin|kick|pwm|crash|hat)_44100$/.test(s.name));
  console.log(`\nMUTANTS: ${MUTANTS.length} deliberate errors, each against ${subset.length} scripts`);
  let survived = 0;
  for (const [name, find, replace] of MUTANTS) {
    const at = text.indexOf(find);
    if (at < 0 || text.indexOf(find, at + 1) >= 0) { console.log(`ERROR  ${name}: the text to change was ${at < 0 ? 'not found' : 'found twice'}`); survived++; continue; }
    const mutated = text.slice(0, at) + replace + text.slice(at + find.length);
    let caught = [], note = '';
    try {
      const r = runParity(loadNano(mutated), subset, exe);
      const chipBad = r.filter(x => !x.chip.ok), instBad = r.filter(x => !x.instrument.ok);
      const fz = runChipFuzz(loadNano(mutated), exe, { rates: [44100, 48000], seeds: [1, 2], steps: 200 }).filter(f => !f.cmp.ok);
      if (fz.length) caught.push(`chip fuzz in ${fz.length}`);
      if (chipBad.length) caught.push(`chip check in ${chipBad.length}`);
      if (instBad.length) caught.push(`instrument check in ${instBad.length}`);
      note = (chipBad[0] ?? instBad[0]) ? ` e.g. ${(chipBad[0] ?? instBad[0]).name}: ${(chipBad[0]?.chip ?? instBad[0]?.instrument).why}` : '';
    } catch (e) { caught.push('threw ' + String(e.message).split('\n')[0]); }
    if (caught.length) console.log(`caught   ${name}  [${caught.join(', ')}]${note}`);
    else { console.log(`SURVIVED ${name}`); survived++; }
  }
  console.log(survived ? `${survived} mutant(s) SURVIVED: the verifier has a hole` : `all ${MUTANTS.length} mutants caught`);
  failed += survived;
}

console.log(failed ? 'PARITY FAILED' : 'PARITY OK');
process.exit(failed ? 1 : 0);
