// The MIDI player of Nano NES:   node Nano/test/song.test.mjs
// The chip and engine are proven against Core by parity.mjs; this checks the layer on top: that a .mid played by SongPlayer is the same PCM as
// the same notes scheduled by hand on the (parity-proven) engine, one Engine per MIDI channel, plus sidecars, seeking, looping and the errors.
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
const Nano = require(path.join(here, '..', 'dist', 'nano.js'));
const kit = Nano._kit;
let pass = 0, fail = 0;
const check = (name, ok) => { if (ok) pass++; else { fail++; console.log('FAIL ' + name); } };
const same = (a, b) => a.length === b.length && Buffer.compare(Buffer.from(a.buffer, a.byteOffset, a.byteLength), Buffer.from(b.buffer, b.byteOffset, b.byteLength)) === 0;

// ---- a tiny SMF writer (format 1) ----
const vlq = (n) => { const o = [n & 127]; while ((n >>= 7) > 0) o.unshift((n & 127) | 128); return o; };
function smf(tracks, ppq = 480) {
  const out = [0x4D, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 1, 0, tracks.length, ppq >> 8, ppq & 255];
  for (const t of tracks) {
    const evs = t.slice().sort((a, b) => a[0] - b[0]);
    const body = []; let last = 0;
    for (const [tick, ...bytes] of evs) { body.push(...vlq(tick - last), ...bytes); last = tick; }
    body.push(0, 0xFF, 0x2F, 0);
    out.push(0x4D, 0x54, 0x72, 0x6B, (body.length >>> 24) & 255, (body.length >> 16) & 255, (body.length >> 8) & 255, body.length & 255, ...body);
  }
  return new Uint8Array(out);
}

const SR = 44100;
// 120 BPM, 480 ppq: 1 tick = 1/960 s
const tempo = [[0, 0xFF, 0x51, 3, 0x07, 0xA1, 0x20]];
const lead = [[0, 0xC0, 0], [0, 0x90, 60, 100], [480, 0x80, 60, 0], [480, 0x90, 64, 127], [960, 0x80, 64, 0], [960, 0x90, 67, 80], [1440, 0x80, 67, 0]];
const bass = [[0, 0xC1, 8], [0, 0x91, 36, 127], [1400, 0x81, 36, 0]];
const drums = [[0, 0x99, 36, 127], [480, 0x99, 38, 127], [720, 0x99, 42, 100], [960, 0x99, 36, 127]];
const mid = smf([tempo, lead, bass, drums]);

const song = Nano.parseMidi(mid);
check('parse: three channels', JSON.stringify(song.channels) === '[0,1,9]');
check('parse: duration 1.5 s', Math.abs(song.duration - 1.5) < 1e-9);

// by hand: one Engine per channel, the same frames, summed in float32 the same way
function byHand(seconds, sidecarPreset) {
  const names = kit.PRESET_NAMES;
  const parts = [
    { notes: lead.filter(e => e[1] === 0x90).map(e => [e[0], e[2], e[3], lead.find(x => x[1] === 0x80 && x[2] === e[2])[0]]), preset: sidecarPreset ?? names[0] },
    { notes: [[0, 36, 127, 1400]], preset: names[8] },
    { notes: drums.map(e => [e[0], e[2], e[3], null]), drum: true },
  ];
  const total = Math.round(seconds * SR), out = new Float32Array(total), sc = new Float32Array(128);
  const engines = parts.map(p => {
    const e = new kit.Engine(SR, {}); let id = 1;
    for (const [on, note, vel, off] of p.notes) {
      const name = p.drum ? kit.GM_DRUMS[note] : p.preset, pr = e.resolvePreset(name), myId = id++;
      e.noteOn(Math.round(on / 960 * SR), myId, p.drum ? kit.PRESETS[name].note : note, Math.min(1, vel / 127), pr, {});
      if (off !== null) e.noteOff(Math.round(off / 960 * SR), myId);
    }
    return e;
  });
  for (let o = 0; o < total; o += 128) {
    const n = Math.min(128, total - o);
    for (const e of engines) { e.render(sc, 0, n); for (let i = 0; i < n; i++) out[o + i] += sc[i] * 1; }
  }
  return out;
}

const played = Nano.renderMidi(mid, { sampleRate: SR, seconds: 2 });
check('song = the same notes scheduled by hand (bit-exact)', same(played, byHand(2)));
check('song is not silent', played.some(x => x !== 0));
check('deterministic', same(played, Nano.renderMidi(mid, { sampleRate: SR, seconds: 2 })));

// sidecar: channel 1 plays 'square' instead of program 0
const side = { format: 'nano-nes-sidecar', version: 1, channels: { '1': { preset: 'square' } } };
check('sidecar overrides the program', same(Nano.renderMidi(mid, { sampleRate: SR, seconds: 2, sidecar: side }), byHand(2, 'square')));
check('sidecar as a string', same(Nano.renderMidi(mid, { sampleRate: SR, seconds: 2, sidecar: JSON.stringify(side) }), byHand(2, 'square')));
const custom = { format: 'nano-nes-sidecar', version: 1, presets: { mine: { kind: 'pulse', duty: 3, a: 0, d: 0, s: 1, r: 0 } }, channels: { '1': { preset: 'mine', volume: 0.5 } } };
check('sidecar preset + volume: differs from default', !same(Nano.renderMidi(mid, { sampleRate: SR, seconds: 2, sidecar: custom }), played));
const throws = (f) => { try { f(); return false; } catch { return true; } };
check('sidecar: wrong format refused', throws(() => Nano.renderMidi(mid, { sidecar: { format: 'nano-gb-sidecar', version: 1 } })));
check('sidecar: unknown key refused', throws(() => Nano.renderMidi(mid, { sidecar: { format: 'nano-nes-sidecar', version: 1, channels: { '1': { pan: 1 } } } })));
check('sidecar: unknown preset refused', throws(() => Nano.renderMidi(mid, { sidecar: { format: 'nano-nes-sidecar', version: 1, channels: { '1': { preset: 'nope' } } } })));
check('sidecar: noiseMode accepted', !throws(() => Nano.renderMidi(mid, { seconds: 0.1, sidecar: { format: 'nano-nes-sidecar', version: 1, channels: { '10': { noiseMode: 1 } } } })));
check('not a MIDI file refused', throws(() => Nano.parseMidi(new Uint8Array(20))));

// shared: one chip
const shared = Nano.renderMidi(mid, { sampleRate: SR, seconds: 2, chips: 'shared' });
check('shared chips: plays, and differs from per-channel', shared.some(x => x !== 0) && !same(shared, played));

// seek / loop on the SongPlayer itself
{
  const pl = new kit.SongPlayer(SR, song, { loop: true, loopStart: 0, loopEnd: 0.5 });
  pl.playing = true;
  const a = new Float32Array(Math.round(1.6 * SR));
  for (let o = 0; o < a.length; o += 128) pl.render(a, o, Math.min(128, a.length - o));
  check('loop keeps playing past the song end', pl.playing && a.subarray(Math.round(1.5 * SR)).some(x => x !== 0));
}
{
  const pl = new kit.SongPlayer(SR, song, {});
  pl.playing = true;
  const a = new Float32Array(Math.round(3 * SR));
  for (let o = 0; o < a.length; o += 128) pl.render(a, o, Math.min(128, a.length - o));
  check('the song ends', pl.ended && !pl.playing);
}

// the Nano GB test songs play here too (the same SMF reader)
for (const f of ['tune.mid', 'chords-sfx.mid']) {
  const file = path.join(here, '..', 'gb', 'test', 'midi', f);
  if (!fs.existsSync(file)) continue;
  const r = Nano.renderMidi(fs.readFileSync(file), { sampleRate: 22050 });
  check(`${f} plays`, r.some(x => x !== 0) && r.every(x => Number.isFinite(x)));
}

// cost of the song layer: a 4-channel song at 48 kHz
{
  const big = smf([tempo, ...[0, 1, 2, 3].map(c => { const t = []; for (let i = 0; i < 64; i++) t.push([i * 120, 0x90 | c, 48 + c * 7 + (i % 12), 100], [i * 120 + 100, 0x80 | c, 48 + c * 7 + (i % 12), 0]); return t; })]);
  const s = Nano.parseMidi(big), runs = [];
  for (let k = 0; k < 5; k++) { const t0 = process.hrtime.bigint(); Nano.renderMidi(s, { sampleRate: 48000 }); runs.push(Number(process.hrtime.bigint() - t0) / 1e6 / (s.duration + 1)); }
  runs.sort((a, b) => a - b);
  console.log(`speed: a 4-channel song (4 chips), ${runs[2].toFixed(1)} ms of CPU per second of audio at 48 kHz (best ${runs[0].toFixed(1)}, worst ${runs[4].toFixed(1)})`);
}

console.log(`${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
