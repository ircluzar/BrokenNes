// CPU per second of audio, Nano GB against Nano NES, in this Node.   node Nano/gb/test/bench.mjs
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { loadGb, gbRoot } from './lib.mjs';

const G = loadGb();
const m = { exports: {} }; new Function('module', fs.readFileSync(path.join(gbRoot, '..', 'dist', 'nano.js'), 'utf8'))(m); const NES = m.exports;
const SECS = 30;
function busy(names) {
  const ev = [];
  for (let b = 0; b < SECS * 4; b++) names.forEach(([p, n], i) => ev.push({ time: b * 0.25, type: 'on', note: n + (b % 5), preset: p, duration: 0.2, id: b * 8 + i + 1 }));
  return ev;
}
function time(fn, reps = 5) { fn(); const r = []; for (let i = 0; i < reps; i++) { const t0 = performance.now(); fn(); r.push(performance.now() - t0); } r.sort((a, b) => a - b); return r[reps >> 1] / SECS; }
const gbEv = busy([['lead', 72], ['square', 64], ['wavebass', 36], ['snare', 38]]), nesEv = busy([['lead', 72], ['square', 64], ['bass', 36], ['snare', 38]]);
const rows = [
  ['Nano NES renderOffline, 4 voices, 48 kHz', time(() => NES.renderOffline(nesEv, SECS, { sampleRate: 48000 }))],
  ['Nano GB  renderOffline, 4 voices, 48 kHz stereo', time(() => G.renderOffline(gbEv, SECS, { sampleRate: 48000, stereo: true }))],
  ['Nano GB  renderOffline, 4 voices, 48 kHz mono', time(() => G.renderOffline(gbEv, SECS, { sampleRate: 48000 }))],
  ['Nano GB  renderOffline, 4 voices, 22.05 kHz mono', time(() => G.renderOffline(gbEv, SECS, { sampleRate: 22050 }))],
  ['Nano GB  renderOffline, silent', time(() => G.renderOffline([], SECS, { sampleRate: 48000, stereo: true }))],
];
const mid = fs.readFileSync(path.join(gbRoot, 'test', 'midi', 'tune.mid')), dur = G.parseMidi(mid).duration + 1;
const tm = (o) => { G.renderMidi(mid, o); const t0 = performance.now(); for (let i = 0; i < 5; i++) G.renderMidi(mid, o); return (performance.now() - t0) / 5 / dur; };
rows.push(['Nano GB  renderMidi tune.mid, a chip per channel (4), 48 kHz', tm({ sampleRate: 48000 })]);
rows.push(['Nano GB  renderMidi tune.mid, one shared chip, 48 kHz', tm({ sampleRate: 48000, chips: 'shared' })]);
for (const [n, ms] of rows) console.log(`${n.padEnd(64)} ${ms.toFixed(2)} ms per audio second`);
for (const f of ['nano-gb.js', 'nano-gb.lean.js', 'nano-gb-worklet.js']) {
  const b = fs.readFileSync(path.join(gbRoot, 'dist', f));
  console.log(`${f.padEnd(22)} ${b.length} B, gzip ${zlib.gzipSync(b, { level: 9 }).length} B, brotli ${zlib.brotliCompressSync(b).length} B`);
}
