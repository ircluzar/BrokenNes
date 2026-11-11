// Nano DOS: the instrument layer and the shipped files.   node Nano/dos/test/engine.test.mjs
// (The OPL2 and the speaker are proven against Core by parity.mjs.)
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const dist = path.join(here, '..', 'dist');
const require = createRequire(import.meta.url);
const N = require(path.join(dist, 'nano-dos.js')), lean = require(path.join(dist, 'nano-dos.lean.js'));
const kit = N._kit;
let pass = 0, fail = 0;
const check = (ok, what) => { if (ok) pass++; else { fail++; console.log('FAIL', what); } };
const same = (a, b) => a.length === b.length && Buffer.compare(Buffer.from(a.buffer, a.byteOffset, a.byteLength), Buffer.from(b.buffer, b.byteOffset, b.byteLength)) === 0;
const SR = 48000;

// pitch by crossings of the mean (the speaker is unipolar)
function hz(pcm, from, to) { let m = 0; for (let i = from; i < to; i++) m += pcm[i]; m /= to - from; let z = 0, first = -1, last = -1; for (let i = from + 1; i < to; i++) if (pcm[i - 1] < m && pcm[i] >= m) { if (first < 0) first = i; last = i; z++; } return (z - 1) * SR / (last - first); }
for (const note of [33, 45, 57, 69, 81, 93]) {
  const pcm = N.renderOffline([{ time: 0, type: 'on', note, preset: 'svtriangle', duration: 1 }], 1, { sampleRate: SR });
  const want = 440 * 2 ** ((note - 69) / 12), got = hz(pcm, 4800, 48000), cents = 1200 * Math.log2(got / want);
  check(Math.abs(cents) < 4, `FM sine note ${note}: ${got.toFixed(2)} Hz for ${want.toFixed(2)} (${cents.toFixed(2)} cents)`);
}
for (const note of [45, 69, 93]) {
  const pcm = N.renderOffline([{ time: 0, type: 'on', note, preset: 'beep', duration: 1 }], 1, { sampleRate: SR });
  const want = 440 * 2 ** ((note - 69) / 12), got = hz(pcm, 4800, 48000), cents = 1200 * Math.log2(got / want);
  check(Math.abs(cents) < 6, `speaker note ${note}: ${got.toFixed(2)} Hz (${cents.toFixed(2)} cents)`);
}
check(kit.blockFnum(440) >> 10 === 4 && kit.speakerCount(440) === 2712, `A4: block ${kit.blockFnum(440) >> 10}, speaker count ${kit.speakerCount(440)}`);
// level: velocity 0.5 is about -6 dB on FM; the speaker has none
{
  const peak = (v, p) => { const r = N.renderOffline([{ time: 0, type: 'on', note: 69, preset: p, velocity: v, duration: 0.5 }], 0.5, { sampleRate: SR }); let a = 0; for (let i = 4800; i < r.length; i++) a = Math.max(a, Math.abs(r[i])); return a; };
  const ratio = peak(0.5, 'svtriangle') / peak(1, 'svtriangle');
  check(ratio > 0.42 && ratio < 0.58, `FM velocity 0.5 is about -6 dB (${ratio.toFixed(3)})`);
  check(peak(0.3, 'beep') === peak(1, 'beep'), 'the speaker has no volume');
}
// voices: nine FM at once, a tenth steals; the speaker is its own channel
{
  const e = new kit.Engine(SR, {});
  const p = e.resolvePreset('organ');
  for (let i = 0; i < 9; i++) e.noteOn(0, i + 1, 60 + i, 1, p);
  e.noteOn(0, 20, 72, 1, e.resolvePreset('beep'));
  const buf = new Float32Array(128); e.render(buf, null, 0, 128);
  check(e.activity().every(c => c.active), 'nine FM voices and the speaker sound');
  e.noteOn(e.frame, 10, 80, 1, p); e.render(buf, null, 0, 128);
  check(e.activity()[0].note === 80, 'a tenth FM note takes the oldest voice');
}
// a retrigger keys off then on in the same moment (MAME 0.36 restarts the envelope at the write)
{
  const t = N.renderOffline([{ time: 0, type: 'on', note: 60, preset: 'piano', channel: 0 }, { time: 0.1, type: 'on', note: 62, preset: 'piano', channel: 0 }], 0.3, { sampleRate: SR, trace: true }).trace;
  const b0 = t.filter(o => o[0] === 0 && o[1] === 0xB0).map(o => o[2] & 0x20);
  check(b0.length >= 3 && b0[0] === 0x20 && b0.includes(0) && b0[b0.length - 1] === 0x20, `B0 writes: key on, off, on (${b0.join(',')})`);
}
check(kit.oplTableSum() === JSON.parse(fs.readFileSync(path.join(dist, 'tables.json'), 'utf8')).oplTableSum, 'the OPL2 tables match the fingerprint the build wrote');
{ let threw = false; try { N.renderOffline([], 0.1, { sampleRate: 8000 }); } catch { threw = true; } check(threw, 'a host rate under 11025 Hz is refused (the resampler goes 4:1 at most)'); }
{ let threw = false; try { N.renderOffline([{ time: 0, type: 'on', note: 60, preset: { kind: 'fm', regs: [1, 2, 3] } }], 0.1); } catch { threw = true; } check(threw, 'an fm preset needs its 11 SBI bytes'); }
for (const p of N.presets) {
  const r = N.renderOffline([{ time: 0, type: 'on', note: p.note, preset: p.name, duration: 0.3 }], 0.6, { sampleRate: 22050 });
  check(r.some(x => x !== 0) && r.every(Number.isFinite), `preset ${p.name} plays`);
}
check(N.presets.every((p, i) => p.program === i), 'presets carry their program numbers');
for (const f of ['tune.mid', 'chords-sfx.mid']) {
  const a = N.renderMidi(fs.readFileSync(path.join(here, 'midi', f)), { sampleRate: 22050 });
  check(a.left.some(x => x !== 0) && a.left.every(Number.isFinite), `${f} plays`);
}
{
  const mid = fs.readFileSync(path.join(here, 'midi', 'tune.mid'));
  const base = N.renderMidi(mid, { sampleRate: 22050, mono: true });
  const ch = N.parseMidi(mid).channels[0] + 1;
  const side = { format: 'nano-dos-sidecar', version: 1, channels: { [ch]: { preset: 'mine', volume: 0.5 } }, presets: { mine: { kind: 'fm', regs: [0x21, 0x21, 0x10, 0, 0xF0, 0xF0, 0x0F, 0x0F, 1, 0, 0x0C] } } };
  check(!same(base, N.renderMidi(mid, { sampleRate: 22050, mono: true, sidecar: side })), 'a sidecar with its own patch changes the channel');
  check(same(base, lean.renderMidi(mid, { sampleRate: 22050, mono: true })), 'the lean build renders the same samples');
}
for (const f of ['nano-dos.js', 'nano-dos.lean.js']) {
  const b = fs.readFileSync(path.join(dist, f));
  console.log(`size: ${f} ${b.length} bytes, ${zlib.gzipSync(b, { level: 9 }).length} gzip`);
  check(b.length < 100 * 1024, `${f} under 100 KB`);
}
{
  const ev = [{ time: 0, type: 'on', note: 60, preset: 'strings' }, { time: 0, type: 'on', note: 64, preset: 'piano' }, { time: 0, type: 'on', note: 67, preset: 'organ' }, { time: 0, type: 'on', note: 36, preset: 'bass' }, { time: 0, type: 'on', note: 72, preset: 'beeplead' }, { time: 0, type: 'on', note: 60, preset: 'snare' }];
  const runs = [];
  for (let k = 0; k < 7; k++) { const t0 = process.hrtime.bigint(); N.renderOffline(ev, 5, { sampleRate: 48000, stereo: true }); runs.push(Number(process.hrtime.bigint() - t0) / 1e6 / 5); }
  runs.sort((a, b) => a - b);
  console.log(`speed: 5 FM + the speaker, 48 kHz: ${runs[3].toFixed(2)} ms of CPU per second of audio (median of 7; best ${runs[0].toFixed(2)}, worst ${runs[6].toFixed(2)})`);
}
console.log(`${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
