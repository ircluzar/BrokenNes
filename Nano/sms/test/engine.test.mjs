// Nano SMS: the instrument layer and the shipped files.   node Nano/sms/test/engine.test.mjs
// (The chip itself is proven against Core by parity.mjs.)
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const dist = path.join(here, '..', 'dist');
const require = createRequire(import.meta.url);
const N = require(path.join(dist, 'nano-sms.js')), lean = require(path.join(dist, 'nano-sms.lean.js'));
const kit = N._kit;
let pass = 0, fail = 0;
const check = (ok, what) => { if (ok) pass++; else { fail++; console.log('FAIL', what); } };
const same = (a, b) => a.length === b.length && Buffer.compare(Buffer.from(a.buffer, a.byteOffset, a.byteLength), Buffer.from(b.buffer, b.byteOffset, b.byteLength)) === 0;
const SR = 48000;

// pitch: zero crossings of a held square (the 10-bit period rounds: up to 14 cents off at C7, 27 at A8, as on the console)
function hz(pcm, from, to) { let z = 0, first = -1, last = -1; for (let i = from + 1; i < to; i++) if (pcm[i - 1] < 0 && pcm[i] >= 0) { if (first < 0) first = i; last = i; z++; } return (z - 1) * SR / (last - first); }
for (const note of [45, 57, 69, 81, 93, 105]) {
  const pcm = N.renderOffline([{ time: 0, type: 'on', note, preset: 'square', duration: 1 }], 1, { sampleRate: SR });
  const want = 440 * 2 ** ((note - 69) / 12), got = hz(pcm, 4800, 48000), cents = 1200 * Math.log2(got / want);
  check(Math.abs(cents) < (note > 100 ? 30 : note > 90 ? 15 : 6), `note ${note}: ${got.toFixed(2)} Hz for ${want.toFixed(2)} (${cents.toFixed(1)} cents)`);
}
// the lowest the PSG goes: period 1023 (109.3 Hz); below that the note is held at A2-ish
{
  const pcm = N.renderOffline([{ time: 0, type: 'on', note: 30, preset: 'square', duration: 1 }], 1, { sampleRate: SR });
  check(Math.abs(hz(pcm, 4800, 48000) - 3579545.4545 / 32 / 1023) < 0.5, 'below A2 the period stays at 1023');
}
// attenuation: velocity 1 = 0, the steps fall 2 dB, 0 = off
check(kit.attenuation(1) === 0 && kit.attenuation(0) === 15 && kit.attenuation(0.5) === 3, `attenuation steps (0.5 -> ${kit.attenuation(0.5)})`);
for (let a = 0; a < 15; a++) check(kit.attenuation([32767, 26028, 20675, 16422, 13045, 10362, 8231, 6568, 5193, 4125, 3277, 2603, 2067, 1642, 1304][a] / 32767) === a, `level of step ${a} maps back to ${a}`);

// voices: three tones at once, a fourth steals the oldest
{
  const e = new kit.Engine(SR, {});
  const p = e.resolvePreset('square');
  e.noteOn(0, 1, 60, 1, p); e.noteOn(0, 2, 64, 1, p); e.noteOn(0, 3, 67, 1, p);
  const buf = new Float32Array(128); e.render(buf, null, 0, 128);
  check(e.activity().slice(0, 3).every(c => c.active), 'three tone channels sound');
  e.noteOn(e.frame, 4, 72, 1, p); e.render(buf, null, 0, 128);
  check(e.activity()[0].note === 72, 'a fourth note takes the oldest channel');
  e.noteOff(e.frame, 4); e.render(buf, null, 0, 1280);
  check(e.activity()[0].note === 60, 'and gives it back when released');
}
// noise: rate by pitch, white / periodic
{
  const t = N.renderOffline([{ time: 0, type: 'on', note: 60, preset: 'buzz', duration: 0.5 }], 0.5, { sampleRate: SR, trace: true }).trace;
  check(t.some(o => o[0] === 0 && o[1] === (0xE0 | 1)), 'buzz writes periodic noise at rate 1');
  const w = N.renderOffline([{ time: 0, type: 'on', note: 38, preset: 'snare' }], 0.5, { sampleRate: SR, trace: true }).trace;
  check(w.some(o => o[0] === 0 && o[1] === 0xE4) && w.some(o => o[0] === 0 && o[1] === 0xE5), 'snare glides white noise from rate 0 to 1');
}
// Game Gear pan
{
  const r = N.renderOffline([{ time: 0, type: 'on', note: 69, preset: 'square', pan: -1, duration: 0.5 }], 0.5, { sampleRate: SR, gameGear: true, stereo: true });
  const pk = (a) => Math.max(...a.subarray(4800).map(Math.abs));
  check(pk(r.left) > 0.1 && pk(r.right) < 1e-3, `Game Gear pan left (L ${pk(r.left).toFixed(3)}, R ${pk(r.right).toFixed(4)})`);
  const m = N.renderOffline([{ time: 0, type: 'on', note: 69, preset: 'square', pan: -1, duration: 0.5 }], 0.5, { sampleRate: SR, stereo: true });
  check(same(m.left, m.right), 'a Master System ignores pan (mono on both sides)');
}
// presets all play and stay finite
for (const p of N.presets) {
  const r = N.renderOffline([{ time: 0, type: 'on', note: p.note, preset: p.name, duration: 0.3 }], 0.6, { sampleRate: 22050 });
  check(r.some(x => x !== 0) && r.every(Number.isFinite), `preset ${p.name} plays`);
}
check(N.presets.every((p, i) => p.program === i), 'presets carry their program numbers');

// MIDI player: the Nano GB test songs through the same SMF reader; sidecar format
for (const f of ['tune.mid', 'chords-sfx.mid']) {
  const file = path.join(here, '..', '..', 'gb', 'test', 'midi', f);
  const a = N.renderMidi(fs.readFileSync(file), { sampleRate: 22050 });
  check(a.left.some(x => x !== 0) && a.left.every(Number.isFinite), `${f} plays`);
}
{
  const mid = fs.readFileSync(path.join(here, '..', '..', 'gb', 'test', 'midi', 'tune.mid'));
  const base = N.renderMidi(mid, { sampleRate: 22050, mono: true });
  const ch = N.parseMidi(mid).channels[0] + 1;
  const side = { format: 'nano-sms-sidecar', version: 1, channels: { [ch]: { preset: 'pluck', volume: 0.5 } } };
  check(!same(base, N.renderMidi(mid, { sampleRate: 22050, mono: true, sidecar: side })), 'a sidecar changes the channel');
  let threw = false; try { N.renderMidi(mid, { sidecar: { format: 'nano-gb-sidecar', version: 1 } }); } catch { threw = true; }
  check(threw, 'a Nano GB sidecar is refused');
  check(same(base, lean.renderMidi(mid, { sampleRate: 22050, mono: true })), 'the lean build renders the same samples');
}

// sizes and cost
for (const f of ['nano-sms.js', 'nano-sms.lean.js']) {
  const b = fs.readFileSync(path.join(dist, f));
  console.log(`size: ${f} ${b.length} bytes, ${zlib.gzipSync(b, { level: 9 }).length} gzip`);
  check(b.length < 80 * 1024, `${f} under 80 KB`);
}
{
  const ev = [{ time: 0, type: 'on', note: 60, preset: 'lead' }, { time: 0, type: 'on', note: 64, preset: 'lead' }, { time: 0, type: 'on', note: 67, preset: 'lead' }, { time: 0, type: 'on', note: 40, preset: 'buzz' }];
  const runs = [];
  for (let k = 0; k < 7; k++) { const t0 = process.hrtime.bigint(); N.renderOffline(ev, 5, { sampleRate: 48000, stereo: true }); runs.push(Number(process.hrtime.bigint() - t0) / 1e6 / 5); }
  runs.sort((a, b) => a - b);
  console.log(`speed: 4 voices, stereo, 48 kHz: ${runs[3].toFixed(2)} ms of CPU per second of audio (median of 7; best ${runs[0].toFixed(2)}, worst ${runs[6].toFixed(2)})`);
}
console.log(`${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
