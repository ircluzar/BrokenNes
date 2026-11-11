// Nano Genesis: the instrument layer and the shipped files.   node Nano/genesis/test/engine.test.mjs
// (The chips themselves are proven against Core by parity.mjs.)
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const dist = path.join(here, '..', 'dist');
const require = createRequire(import.meta.url);
const N = require(path.join(dist, 'nano-genesis.js')), lean = require(path.join(dist, 'nano-genesis.lean.js'));
const kit = N._kit;
let pass = 0, fail = 0;
const check = (ok, what) => { if (ok) pass++; else { fail++; console.log('FAIL', what); } };
const same = (a, b) => a.length === b.length && Buffer.compare(Buffer.from(a.buffer, a.byteOffset, a.byteLength), Buffer.from(b.buffer, b.byteOffset, b.byteLength)) === 0;
const SR = 48000;

// pitch by zero crossings: the FM sine (block + F-number: within a cent or two) and the PSG square
function hz(pcm, from, to) { let z = 0, first = -1, last = -1; for (let i = from + 1; i < to; i++) if (pcm[i - 1] < 0 && pcm[i] >= 0) { if (first < 0) first = i; last = i; z++; } return (z - 1) * SR / (last - first); }
for (const note of [33, 45, 57, 69, 81, 93]) {
  const pcm = N.renderOffline([{ time: 0, type: 'on', note, preset: 'sine', duration: 1 }], 1, { sampleRate: SR });
  const want = 440 * 2 ** ((note - 69) / 12), got = hz(pcm, 4800, 48000), cents = 1200 * Math.log2(got / want);
  check(Math.abs(cents) < 3, `FM sine note ${note}: ${got.toFixed(2)} Hz for ${want.toFixed(2)} (${cents.toFixed(2)} cents)`);
}
for (const note of [57, 69, 81]) {
  const pcm = N.renderOffline([{ time: 0, type: 'on', note, preset: 'psgsquare', duration: 1 }], 1, { sampleRate: SR });
  const want = 440 * 2 ** ((note - 69) / 12), got = hz(pcm, 4800, 48000), cents = 1200 * Math.log2(got / want);
  check(Math.abs(cents) < 6, `PSG note ${note}: ${got.toFixed(2)} Hz (${cents.toFixed(1)} cents)`);
}
// block / F-number: the lowest block whose F-number fits 11 bits
check(kit.blockFnum(440) >> 11 === 4 && (kit.blockFnum(440) & 2047) > 1024, `A4 = block 4, F ${kit.blockFnum(440) & 2047}`);
check(kit.blockFnum(20) >> 11 === 0 && (kit.blockFnum(20000) >> 11) === 7, 'low notes block 0, high notes block 7');
// level: velocity 1 = TL + 0, half = 8 steps (6 dB), 0 = 127
check(kit.tlSteps(1) === 0 && kit.tlSteps(0.5) === 8 && kit.tlSteps(0) === 127, `level steps (0.5 -> ${kit.tlSteps(0.5)})`);
{
  const peak = (v) => { const r = N.renderOffline([{ time: 0, type: 'on', note: 69, preset: 'sine', velocity: v, duration: 0.5 }], 0.5, { sampleRate: SR }); return Math.max(...r.subarray(4800).map(Math.abs)); };
  const ratio = peak(0.5) / peak(1);
  check(ratio > 0.45 && ratio < 0.56, `velocity 0.5 is about -6 dB (${ratio.toFixed(3)})`);
}
// voices: six FM at once, a seventh steals the oldest; PSG tones on their own three
{
  const e = new kit.Engine(SR, {});
  const p = e.resolvePreset('organ');
  for (let i = 0; i < 6; i++) e.noteOn(0, i + 1, 60 + i, 1, p);
  const buf = new Float32Array(128); e.render(buf, null, 0, 128);
  check(e.activity().slice(0, 6).every(c => c.active), 'six FM channels sound');
  e.noteOn(e.frame, 7, 80, 1, p); e.render(buf, null, 0, 128);
  check(e.activity()[0].note === 80, 'a seventh note takes the oldest FM channel');
  e.noteOn(e.frame, 8, 72, 1, e.resolvePreset('psglead')); e.render(buf, null, 0, 128);
  check(e.activity()[6].active && e.activity()[6].note === 72, 'a PSG preset plays on PSG tone 1');
  e.noteOff(e.frame, 7); for (let k = 0; k < 400; k++) e.render(buf, null, 0, 128);
  check(e.activity()[0].note === 60, 'the FM channel goes back to the note under it after the release');
}
// key edges: a retriggered FM note keys off and on with a sample between (the chip sees both edges)
{
  const t = N.renderOffline([{ time: 0, type: 'on', note: 60, preset: 'pluck', channel: 0 }, { time: 0.1, type: 'on', note: 62, preset: 'pluck', channel: 0 }], 0.3, { sampleRate: SR, trace: true }).trace;
  const keys = t.map((o, i) => [o, i]).filter(([o]) => o[0] === 2 && o[1] === 0x28);
  check(keys.length >= 3 && keys[1][0][2] === 0x00 && keys[2][0][2] === 0xF0, 'retrigger: key off, then key on');
  const between = t.slice(keys[1][1], keys[2][1]);
  check(between.some(o => o[0] === 5), 'and a YM2612 sample between the two');
}
// pan: FM left / right, the PSG mono
{
  const r = N.renderOffline([{ time: 0, type: 'on', note: 69, preset: 'sine', pan: -1, duration: 0.5 }], 0.5, { sampleRate: SR, stereo: true });
  const pk = (a) => Math.max(...a.subarray(4800).map(Math.abs));
  check(pk(r.left) > 0.05 && pk(r.right) < 0.01, `FM pan left (L ${pk(r.left).toFixed(3)}, R ${pk(r.right).toFixed(4)}: the ladder's offset only)`);
  const m = N.renderOffline([{ time: 0, type: 'on', note: 69, preset: 'psgsquare', pan: -1, duration: 0.5 }], 0.5, { sampleRate: SR, stereo: true });
  check(Math.abs(pk(m.left) - pk(m.right)) < 0.02, 'the PSG ignores pan (mono on a Mega Drive)');
}
// the LFO: a preset with PMS wobbles only while it is on
{
  const vib = { kind: 'fm', alg: 7, pms: 7, r: 50, ops: [{ tl: 127 }, { tl: 127 }, { tl: 127 }, { mul: 1, tl: 0 }] };
  const a = N.renderOffline([{ time: 0, type: 'on', note: 69, preset: vib, duration: 1 }], 1, { sampleRate: SR });
  const b = N.renderOffline([{ time: 0, type: 'param', name: 'lfo', value: 4 }, { time: 0, type: 'on', note: 69, preset: vib, duration: 1 }], 1, { sampleRate: SR });
  check(!same(a, b), 'setParam lfo turns the chip LFO on');
}
// presets all play and stay finite
for (const p of N.presets) {
  const r = N.renderOffline([{ time: 0, type: 'on', note: p.note, preset: p.name, duration: 0.3 }], 0.6, { sampleRate: 22050 });
  check(r.some(x => x !== 0) && r.every(Number.isFinite), `preset ${p.name} plays`);
}
check(N.presets.every((p, i) => p.program === i), 'presets carry their program numbers');
{ let threw = false; try { N.renderOffline([{ time: 0, type: 'on', note: 60, preset: { kind: 'fm', ops: [{}, {}, {}] } }], 0.1); } catch { threw = true; } check(threw, 'an fm preset needs four operators'); }
{ let threw = false; try { N.renderOffline([{ time: 0, type: 'on', note: 60, preset: { kind: 'fm', ops: [{ mul: 16 }, {}, {}, {}] } }], 0.1); } catch { threw = true; } check(threw, 'operator fields are range checked'); }
{ let threw = false; try { N.renderOffline([{ time: 0, type: 'on', note: 60, preset: 'brass', channel: 7 }], 0.1); } catch { threw = true; } check(threw, 'an FM preset cannot be pinned to a PSG channel'); }

// MIDI player: the test songs through the same SMF reader; sidecar format
for (const f of ['tune.mid', 'chords-sfx.mid']) {
  const a = N.renderMidi(fs.readFileSync(path.join(here, 'midi', f)), { sampleRate: 22050 });
  check(a.left.some(x => x !== 0) && a.left.every(Number.isFinite), `${f} plays`);
}
{
  const mid = fs.readFileSync(path.join(here, 'midi', 'tune.mid'));
  const base = N.renderMidi(mid, { sampleRate: 22050, mono: true });
  const ch = N.parseMidi(mid).channels[0] + 1;
  const side = { format: 'nano-genesis-sidecar', version: 1, channels: { [ch]: { preset: 'marimba', volume: 0.5 } } };
  check(!same(base, N.renderMidi(mid, { sampleRate: 22050, mono: true, sidecar: side })), 'a sidecar changes the channel');
  let threw = false; try { N.renderMidi(mid, { sidecar: { format: 'nano-sms-sidecar', version: 1 } }); } catch { threw = true; }
  check(threw, 'a Nano SMS sidecar is refused');
  check(same(base, lean.renderMidi(mid, { sampleRate: 22050, mono: true })), 'the lean build renders the same samples');
  check(same(base, N.renderMidi(mid, { sampleRate: 22050, mono: true, chips: 'channel' })), 'chips: channel is the default');
  check(N.renderMidi(mid, { sampleRate: 22050, mono: true, chips: 'shared' }).some(x => x !== 0), 'chips: shared plays');
}

// sizes and cost
for (const f of ['nano-genesis.js', 'nano-genesis.lean.js']) {
  const b = fs.readFileSync(path.join(dist, f));
  console.log(`size: ${f} ${b.length} bytes, ${zlib.gzipSync(b, { level: 9 }).length} gzip`);
  check(b.length < 110 * 1024, `${f} under 110 KB`);
}
{
  const ev = [{ time: 0, type: 'on', note: 60, preset: 'brass' }, { time: 0, type: 'on', note: 64, preset: 'epiano' }, { time: 0, type: 'on', note: 67, preset: 'strings' }, { time: 0, type: 'on', note: 36, preset: 'bass' }, { time: 0, type: 'on', note: 72, preset: 'psglead' }, { time: 0, type: 'on', note: 40, preset: 'snare' }];
  const runs = [];
  for (let k = 0; k < 7; k++) { const t0 = process.hrtime.bigint(); N.renderOffline(ev, 5, { sampleRate: 48000, stereo: true }); runs.push(Number(process.hrtime.bigint() - t0) / 1e6 / 5); }
  runs.sort((a, b) => a - b);
  console.log(`speed: 4 FM + 1 PSG tone + noise, stereo, 48 kHz: ${runs[3].toFixed(2)} ms of CPU per second of audio (median of 7; best ${runs[0].toFixed(2)}, worst ${runs[6].toFixed(2)})`);
}
console.log(`${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
