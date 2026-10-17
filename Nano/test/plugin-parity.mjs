// Nano against the BrokenNes2 plugin itself (not only against Core): the plugin DLL is rendered by the FL test host (BrokenNes.FruityHost render)
// playing a piano-roll scenario into one instance, and the web module plays the same notes; the two must agree sample for sample.
//
//   node Nano/test/plugin-parity.mjs --host <BrokenNes.FruityHost.exe> --dll <BrokenNes2_x64.dll> [--mutant]
//   (or the environment variables NANO_FRUITYHOST and NANO_PLUGIN_DLL)
//
// The scenarios are one instance on one channel (pulse 1 / pulse 2-free, triangle, noise), 48 kHz, 64-sample blocks, notes on multiples of 64 samples so that
// the host's block start and Nano's event frame are the same sample. Pitch slides and bends are written the way the host mixer feeds them to the plugin
// (every block, as a float32 number of cents), so they become Nano 'bend' events at the same frames. Output scale: the plugin's centre pan and 100 % volume
// are unity gain, so Nano runs at volume 1.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { loadNano } from './lib.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const argv = process.argv.slice(2);
const opt = (n, env) => (argv.includes(n) ? argv[argv.indexOf(n) + 1] : process.env[env]);
const host = opt('--host', 'NANO_FRUITYHOST'), dll = opt('--dll', 'NANO_PLUGIN_DLL');
if (!host || !dll || !fs.existsSync(host) || !fs.existsSync(dll)) {
  console.log('NOT RUN: needs --host <BrokenNes.FruityHost.exe> and --dll <BrokenNes2_x64.dll> (built from Plugin/; see Plugin/CLAUDE.md)');
  process.exit(2);
}

let text = fs.readFileSync(process.env.NANO_DIST ?? path.join(here, '..', 'dist', 'nano.js'), 'utf8');
if (argv.includes('--mutant')) {   // proof that this check can fail: one wrong digit in the pulse period formula
  const find = 'CPU_HZ / (16.0 * freq) - 1.0';
  if (!text.includes(find)) throw new Error('mutation target missing');
  text = text.replace(find, 'CPU_HZ / (16.0 * freq) - 1.3');
}
const Nano = loadNano(text);

const RATE = 48000, TEMPO = 150, SPB = RATE * 60 / TEMPO;     // 19200 samples per beat = 300 blocks of 64
const f32 = Math.fround;

function pitchCents(n, beat) {                                   // Scenario.PitchCents of the host
  let cents = (n.key - 60) * 100;
  if (n.slides) {
    let from = n.key;
    for (const seg of n.slides) {
      if (beat < seg.startBeat) break;
      const f = seg.lengthBeats <= 0 ? 1 : Math.min(1, Math.max(0, (beat - seg.startBeat) / seg.lengthBeats));
      cents = (from + (seg.toKey - from) * f - 60) * 100;
      from = seg.toKey;
    }
  }
  if (n.bend && n.bend.length) {
    const pts = n.bend;
    if (beat <= pts[0][0]) cents += pts[0][1];
    else if (beat >= pts[pts.length - 1][0]) cents += pts[pts.length - 1][1];
    else for (let i = 1; i < pts.length; i++) if (beat <= pts[i][0]) { const f = (beat - pts[i - 1][0]) / (pts[i][0] - pts[i - 1][0]); cents += pts[i - 1][1] + (pts[i][1] - pts[i - 1][1]) * f; break; }
  }
  return cents;
}

const N = (startBeat, lengthBeats, key, velocity, extra = {}) => ({ startBeat, lengthBeats, key, velocity, ...extra });
const melody = [
  N(0, 1, 60, 0.78), N(1.5, 1, 69, 0.5), N(2.5, 0.5, 72, 1.0), N(2.75, 0.5, 76, 0.3),          // the second pair overlaps: the newer note owns the channel, the older returns
  N(4, 1.5, 60, 0.9, { slides: [{ startBeat: 0.5, lengthBeats: 1, toKey: 72 }] }),
  N(6, 2, 64, 0.6, { bend: [[0, 0], [1, 300], [2, -200]] }),
  N(8.25, 0.5, 24, 1.0), N(8.75, 0.5, 110, 1.0), N(9.25, 0.5, 48, 0.02), N(9.75, 0.5, 52, 0.07), N(10.25, 0.5, 55, 0.4),
  N(11, 1, 67, 0.78, { slides: [{ startBeat: 0, lengthBeats: 0.5, toKey: 55 }, { startBeat: 0.5, lengthBeats: 0.5, toKey: 79 }] }),
];
const drums = [N(0, 0.5, 30, 1), N(1, 0.5, 45, 0.8), N(2, 0.5, 60, 0.6), N(3, 0.5, 75, 1), N(4, 0.5, 90, 0.5), N(5, 1, 36, 1, { bend: [[0, 0], [1, -2400]] }), N(6.25, 0.5, 48, 0.9), N(6.5, 0.5, 50, 0.9)];

const runs = [
  { name: 'pulse 1, duty 25%', channel: 0, params: { 6: 1 }, preset: { kind: 'pulse', duty: 1 }, notes: melody },
  { name: 'pulse 1, duty 75%', channel: 0, params: { 6: 1, 5: 3 }, preset: { kind: 'pulse', duty: 3 }, notes: melody },
  { name: 'pulse 2, duty 12.5%', channel: 1, params: { 6: 2, 5: 0 }, preset: { kind: 'pulse', duty: 0 }, notes: melody },
  { name: 'triangle', channel: 2, params: { 6: 3 }, preset: { kind: 'triangle' }, notes: melody },
  { name: 'noise, long mode', channel: 3, params: { 6: 4 }, preset: { kind: 'noise', noiseMode: 0 }, notes: drums },
  { name: 'noise, short mode', channel: 3, params: { 6: 4, 7: 1 }, preset: { kind: 'noise', noiseMode: 1 }, notes: drums },
];

function readWavFloat(file) {
  const b = fs.readFileSync(file);
  if (b.toString('ascii', 0, 4) !== 'RIFF' || b.readUInt16LE(20) !== 3 || b.readUInt16LE(22) !== 2) throw new Error('unexpected WAV format');
  const n = (b.length - 44) / 4;
  const all = new Float32Array(b.buffer.slice(b.byteOffset + 44, b.byteOffset + 44 + n * 4));
  const L = new Float32Array(n / 2), R = new Float32Array(n / 2);
  for (let i = 0; i < L.length; i++) { L[i] = all[2 * i]; R[i] = all[2 * i + 1]; }
  return { L, R };
}

const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'nano-plugin-'));
let failed = 0;
for (const run of runs) {
  const lengthBeats = Math.max(...run.notes.map(n => n.startBeat + n.lengthBeats)) + 0.5;
  const scenario = {
    name: run.name, tempo: TEMPO, lengthBeats,
    params: run.params,
    notes: run.notes.map(n => ({ startBeat: n.startBeat, lengthBeats: n.lengthBeats, key: n.key, channel: 0, velocity: n.velocity, slides: n.slides, bend: n.bend })),
  };
  const sfile = path.join(dir, 'scenario.json'), wav = path.join(dir, 'plugin.wav');
  fs.writeFileSync(sfile, JSON.stringify(scenario));
  execFileSync(host, ['render', dll, sfile, wav, '--rate', String(RATE), '--block', '64'], { stdio: ['ignore', 'ignore', 'inherit'] });
  const plugin = readWavFloat(wav);

  // the same notes in Nano; the preset is the plugin's: constant volume at the velocity, no envelope, closes at note-off
  const events = [];
  let id = 1;
  for (const n of run.notes) {
    const start = Math.round(n.startBeat * SPB), end = Math.round((n.startBeat + n.lengthBeats) * SPB);
    const base = (n.key - 60) * 100;
    const bendAt = (s) => f32(pitchCents(n, (s - start) / SPB)) - base;          // the host hands the plugin float32 cents
    const myId = id++;
    events.push({ time: start / RATE, type: 'on', note: n.key, velocity: f32(n.velocity), preset: 'plugin', id: myId, bend: bendAt(start), channel: run.channel, ...(run.preset.noiseMode !== undefined ? { noiseMode: run.preset.noiseMode } : {}) });
    if (n.slides || n.bend) for (let s = start + 64; s < end; s += 64) events.push({ time: s / RATE, type: 'bend', id: myId, cents: bendAt(s) });
    events.push({ time: end / RATE, type: 'off', id: myId });
  }
  const preset = { ...run.preset, a: 0, d: 0, s: 1, r: 0 };
  const mine = Nano.renderOffline(events, plugin.L.length / RATE, { sampleRate: RATE, volume: 1, presets: { plugin: preset } });

  let diffs = 0, first = -1, maxAbs = 0, peak = 0, lrDiff = 0;
  const n = Math.min(mine.length, plugin.L.length);
  for (let i = 0; i < n; i++) {
    peak = Math.max(peak, Math.abs(plugin.L[i]));
    if (plugin.L[i] !== plugin.R[i]) lrDiff++;
    if (mine[i] !== plugin.L[i]) { diffs++; if (first < 0) first = i; maxAbs = Math.max(maxAbs, Math.abs(mine[i] - plugin.L[i])); }
  }
  const ok = diffs === 0 && lrDiff === 0 && mine.length === plugin.L.length && peak > 0.01;
  if (!ok) failed++;
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${run.name.padEnd(22)} ${String(n).padStart(7)} samples, peak ${peak.toFixed(3)}: ` +
    (ok ? 'bit-exact with the plugin' : `${diffs} samples differ${first >= 0 ? ` (first at ${first}, largest ${maxAbs})` : ''}, left/right differ in ${lrDiff}, lengths ${mine.length}/${plugin.L.length}`));
}
fs.rmSync(dir, { recursive: true, force: true });
console.log(failed ? 'PLUGIN PARITY FAILED' : 'PLUGIN PARITY OK');
process.exit(failed ? 1 : 0);
