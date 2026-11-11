// Nano SNES: the instrument layer and the shipped files.   node Nano/snes/test/engine.test.mjs
// (The S-DSP itself is proven against Core by parity.mjs.)
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const dist = path.join(here, '..', 'dist');
const require = createRequire(import.meta.url);
const N = require(path.join(dist, 'nano-snes.js')), lean = require(path.join(dist, 'nano-snes.lean.js'));
const kit = N._kit;
let pass = 0, fail = 0;
const check = (ok, what) => { if (ok) pass++; else { fail++; console.log('FAIL', what); } };
const same = (a, b) => a.length === b.length && Buffer.compare(Buffer.from(a.buffer, a.byteOffset, a.byteLength), Buffer.from(b.buffer, b.byteOffset, b.byteLength)) === 0;
const SR = 48000;

function hz(pcm, from, to) { let z = 0, first = -1, last = -1; for (let i = from + 1; i < to; i++) if (pcm[i - 1] < 0 && pcm[i] >= 0) { if (first < 0) first = i; last = i; z++; } return (z - 1) * SR / (last - first); }
// pitch: the 14-bit pitch register (a 32-sample loop: 1000 Hz at $1000, up to 4000 Hz)
for (const note of [33, 45, 57, 69, 81, 93]) {
  const pcm = N.renderOffline([{ time: 0, type: 'on', note, preset: 'sine', duration: 1 }], 1, { sampleRate: SR });
  const want = 440 * 2 ** ((note - 69) / 12), got = hz(pcm, 4800, 48000), cents = 1200 * Math.log2(got / want);
  check(Math.abs(cents) < (note < 40 ? 12 : 3), `sine note ${note}: ${got.toFixed(2)} Hz for ${want.toFixed(2)} (${cents.toFixed(2)} cents)`);
}
{
  const pcm = N.renderOffline([{ time: 0, type: 'on', note: 110, preset: 'sine', duration: 0.5 }], 0.5, { sampleRate: 96000 });
  check(pcm.some(x => x !== 0), 'the top note (pitch register $3FFF) still plays');
}
// BRR: the encoder's own bytes decode (through the DSP) close to the wave; filters are used
{
  const wave = Array.from({ length: 256 }, (_, i) => Math.round(20000 * Math.sin(2 * Math.PI * i / 64)));
  const brr = kit.encodeBrr(wave, 0);
  check(brr.length === 16 * 9 && (brr[brr.length - 9] & 3) === 3 && (brr[0] & 2) === 2, 'BRR: 16 blocks, loop flag on, end flag on the last');
  check([...Array(16).keys()].some(b => (brr[b * 9] & 0x0C) !== 0), 'BRR: the encoder picks a prediction filter where it helps');
  check(kit.toBase64(kit.fromBase64(kit.toBase64(brr))) === kit.toBase64(brr) && kit.fromBase64(kit.toBase64(brr)).every((x, i) => x === brr[i]), 'base64 round trip');
  let threw = false; try { N.renderOffline([{ time: 0, type: 'on', note: 60, preset: { kind: 'brr', brr: 'AAAA' } }], 0.1); } catch { threw = true; }
  check(threw, 'a BRR that is not whole 9-byte blocks is refused');
}
// level: velocity -> VOL (7 bits): half velocity is about -6 dB
{
  const peak = (v) => { const r = N.renderOffline([{ time: 0, type: 'on', note: 69, preset: 'sine', velocity: v, duration: 0.5 }], 0.5, { sampleRate: SR }); return Math.max(...r.subarray(4800).map(Math.abs)); };
  const ratio = peak(0.5) / peak(1);
  check(ratio > 0.45 && ratio < 0.56, `velocity 0.5 is about -6 dB (${ratio.toFixed(3)})`);
}
// voices: eight at once, a ninth steals the oldest; KON for notes keyed together lands together
{
  const e = new kit.Engine(SR, {});
  const p = e.resolvePreset('organ');
  for (let i = 0; i < 8; i++) e.noteOn(0, i + 1, 60 + i, 1, p);
  const buf = new Float32Array(128); e.render(buf, null, 0, 128);
  check(e.activity().every(c => c.active), 'eight voices sound');
  const envx = [...Array(8).keys()].map(v => e.chip.read((v << 4) | 8));
  check(envx.every(x => x > 0), `every voice was keyed (ENVX ${envx.join(',')})`);
  e.noteOn(e.frame, 9, 80, 1, p); e.render(buf, null, 0, 128);
  check(e.activity()[0].note === 80, 'a ninth note takes the oldest voice');
}
// pan: VOL L / R
{
  const r = N.renderOffline([{ time: 0, type: 'on', note: 69, preset: 'sine', pan: -1, duration: 0.5 }], 0.5, { sampleRate: SR, stereo: true });
  const pk = (a) => Math.max(...a.subarray(4800).map(Math.abs));
  check(pk(r.left) > 0.05 && pk(r.right) === 0, `pan left (L ${pk(r.left).toFixed(3)}, R ${pk(r.right)})`);
}
// the noise preset sets NON and the noise clock
{
  const t = N.renderOffline([{ time: 0, type: 'on', note: 38, preset: 'snare' }], 0.3, { sampleRate: SR, trace: true }).trace;
  check(t.some(o => o[0] === 0 && o[1] === 0x3D && o[2] !== 0) && t.some(o => o[0] === 0 && o[1] === 0x6C && (o[2] & 31) === 29), 'snare: NON on, noise clock 29');
}
// presets all play and stay finite
for (const p of N.presets) {
  const r = N.renderOffline([{ time: 0, type: 'on', note: p.note, preset: p.name, duration: 0.3 }], 0.6, { sampleRate: 22050 });
  check(r.some(x => x !== 0) && r.every(Number.isFinite), `preset ${p.name} plays`);
}
check(N.presets.every((p, i) => p.program === i), 'presets carry their program numbers');
{ const e = new kit.Engine(SR, {}); check(e.ramAt < 0x8000, `the built-in samples take ${e.ramAt - 0x600} bytes of sound RAM`); }

// MIDI player
for (const f of ['tune.mid', 'chords-sfx.mid']) {
  const a = N.renderMidi(fs.readFileSync(path.join(here, 'midi', f)), { sampleRate: 22050 });
  check(a.left.some(x => x !== 0) && a.left.every(Number.isFinite), `${f} plays`);
}
{
  const mid = fs.readFileSync(path.join(here, 'midi', 'tune.mid'));
  const base = N.renderMidi(mid, { sampleRate: 22050, mono: true });
  const ch = N.parseMidi(mid).channels[0] + 1;
  const side = { format: 'nano-snes-sidecar', version: 1, channels: { [ch]: { preset: 'mine', volume: 0.5 } }, presets: { mine: { kind: 'brr', wave: Array.from({ length: 32 }, (_, i) => (i < 16 ? 12000 : -12000)), base: 1000 } } };
  check(!same(base, N.renderMidi(mid, { sampleRate: 22050, mono: true, sidecar: side })), 'a sidecar with its own sample changes the channel');
  let threw = false; try { N.renderMidi(mid, { sidecar: { format: 'nano-sms-sidecar', version: 1 } }); } catch { threw = true; }
  check(threw, 'a Nano SMS sidecar is refused');
  check(same(base, lean.renderMidi(mid, { sampleRate: 22050, mono: true })), 'the lean build renders the same samples');
}

for (const f of ['nano-snes.js', 'nano-snes.lean.js']) {
  const b = fs.readFileSync(path.join(dist, f));
  console.log(`size: ${f} ${b.length} bytes, ${zlib.gzipSync(b, { level: 9 }).length} gzip`);
  check(b.length < 100 * 1024, `${f} under 100 KB`);
}
{
  const ev = [{ time: 0, type: 'on', note: 60, preset: 'strings' }, { time: 0, type: 'on', note: 64, preset: 'piano' }, { time: 0, type: 'on', note: 67, preset: 'choir' }, { time: 0, type: 'on', note: 36, preset: 'bass' }, { time: 0, type: 'on', note: 72, preset: 'lead' }, { time: 0, type: 'on', note: 40, preset: 'snare' }];
  const runs = [];
  for (let k = 0; k < 7; k++) { const t0 = process.hrtime.bigint(); N.renderOffline(ev, 5, { sampleRate: 48000, stereo: true }); runs.push(Number(process.hrtime.bigint() - t0) / 1e6 / 5); }
  runs.sort((a, b) => a - b);
  console.log(`speed: 6 voices, stereo, 48 kHz: ${runs[3].toFixed(2)} ms of CPU per second of audio (median of 7; best ${runs[0].toFixed(2)}, worst ${runs[6].toFixed(2)})`);
}
console.log(`${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
