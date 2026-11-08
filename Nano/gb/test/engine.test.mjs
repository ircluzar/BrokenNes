// Nano GB without Core: presets, API rules, the lean build, mono, the song player, allocation, cost.   node Nano/gb/test/engine.test.mjs
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { loadGb, gbRoot, rms, peak, pitchZc, midiHz, spectrum } from './lib.mjs';

const G = loadGb(), kit = G._kit;
let pass = 0, fail = 0;
const t = (name, ok, detail = '') => { if (ok) pass++; else { fail++; console.log(`FAIL ${name} ${detail}`); } };

// the build is current, and the lean build is the same code
t('build --check', (() => { try { execFileSync(process.execPath, [path.join(gbRoot, 'build.mjs'), '--check'], { stdio: 'ignore' }); return true; } catch { return false; } })());
const lean = loadGb(fs.readFileSync(path.join(gbRoot, 'dist', 'nano-gb.lean.js'), 'utf8'));
const tune = [];
for (let b = 0; b < 8; b++) tune.push({ time: b * 0.2, type: 'on', note: 60 + b, preset: G.presets[b * 3 % 27].name, duration: 0.15 }, { time: b * 0.2, type: 'on', note: 38, preset: 'snare' });
const same = (a, b) => a.length === b.length && Buffer.from(a.buffer, a.byteOffset, a.byteLength).equals(Buffer.from(b.buffer, b.byteOffset, b.byteLength));
{ const a = G.renderOffline(tune, 2, { stereo: true }), b = lean.renderOffline(tune, 2, { stereo: true }); t('lean build renders the same bits', same(a.left, b.left) && same(a.right, b.right)); }

// mono is (l + r) / 2 of the stereo render
{ const s = G.renderOffline(tune, 2, { stereo: true }), m = G.renderOffline(tune, 2); let ok = true; for (let i = 0; i < m.length; i++) if (m[i] !== Math.fround((s.left[i] + s.right[i]) / 2)) { ok = false; break; } t('mono = (left + right) / 2', ok); }

// every preset audible, pitched ones on pitch, all distinct
const R = 48000, feats = [];
for (const p of G.presets) {
  const pcm = G.renderOffline([{ time: 0, type: 'on', note: p.note, preset: p.name, duration: 0.5 }], 0.8, { sampleRate: R });
  t(`preset ${p.name} audible`, peak(pcm) > 0.05, `peak ${peak(pcm)}`);
  const n = kit.normalizePreset(kit.PRESETS[p.name], p.name);
  const steady = p.name !== 'organ' && !n.slideMs && !n.arpSteps && n.sweep < 0 && n.kind !== 2 && !n.vibDepth;
  if (steady) { const f = pitchZc(pcm, R, Math.round(0.02 * R), Math.round(0.12 * R)), want = midiHz(p.note), c = 1200 * Math.log2(f / want); t(`preset ${p.name} on pitch`, Math.abs(c) < 15, `${f.toFixed(1)} Hz vs ${want.toFixed(1)} (${c.toFixed(1)} cents)`); }
  const sp = spectrum(pcm, 0, 16384), env = []; for (let i = 0; i < 16; i++) env.push(rms(pcm, i * 2400, (i + 1) * 2400));
  feats.push({ name: p.name, sp, env });
}
for (let i = 0; i < feats.length; i++) for (let j = i + 1; j < feats.length; j++) {
  const a = feats[i], b = feats[j]; let d = 0, e = 0, n = 0;
  for (let k = 0; k < a.sp.length; k++) { d += Math.abs(a.sp[k] - b.sp[k]); n += a.sp[k] + b.sp[k]; }
  for (let k = 0; k < 16; k++) e += Math.abs(a.env[k] - b.env[k]);
  t(`${a.name} vs ${b.name} distinct`, d / n > 0.05 || e > 0.05, `spectral ${(d / n).toFixed(3)} envelope ${e.toFixed(3)}`);
}

// sweep effects actually sweep; wave channel pitch with a user wave
{ const pcm = G.renderOffline([{ time: 0, type: 'on', note: 55, preset: 'jump' }], 0.3, { sampleRate: R }); const f0 = pitchZc(pcm, R, 100, 1500), f1 = pitchZc(pcm, R, 4000, 5500); t('jump sweeps up', f1 > f0 * 1.2, `${f0} -> ${f1}`); }
{ const pcm = G.renderOffline([{ time: 0, type: 'on', note: 96, preset: 'laser' }], 0.3, { sampleRate: R }); const f0 = pitchZc(pcm, R, 0, 600), f1 = pitchZc(pcm, R, 3000, 4800); t('laser sweeps down', f1 < f0 * 0.8, `${f0} -> ${f1}`); }
{ const w = Array.from({ length: 32 }, (_, i) => i < 16 ? 15 : 0); const pcm = G.renderOffline([{ time: 0, type: 'on', note: 57, preset: { kind: 'wave', wave: w } }], 0.3, { sampleRate: R }); const f = pitchZc(pcm, R, 960, 9600); t('user wave on pitch', Math.abs(1200 * Math.log2(f / 220)) < 15, f); }

// pan: hard left reaches only the left
{ const s = G.renderOffline([{ time: 0, type: 'on', note: 60, preset: 'square', pan: 'L', duration: 0.2 }], 0.3, { stereo: true }); t('pan left', peak(s.left) > 0.1 && peak(s.right) === 0); }

// bad arguments throw
const bad = [() => G.renderOffline([{ type: 'on', note: 'H2' }], 1), () => G.renderOffline([{ type: 'on', note: 60, preset: 'nope' }], 1), () => G.renderOffline([{ type: 'on', note: 60, preset: 'square', channel: 2 }], 1),
  () => G.renderOffline([{ type: 'on', note: 60, preset: 'square', duty: 4 }], 1), () => G.renderOffline([{ type: 'on', note: 60, preset: 'square', pan: 0.5 }], 1), () => kit.normalizePreset({ kind: 'wave', wave: [1, 2] }),
  () => kit.normalizePreset({ kind: 'noise', sweep: {} }), () => kit.normalizePreset({ kind: 'pulse', oneShot: true, s: 1 }), () => G.renderOffline([{ type: 'wat' }], 1), () => G.renderMidi(new Uint8Array(10)),
  () => kit.normalizeSidecar({ format: 'nano-gb-sidecar', version: 1, channels: { 17: {} } }), () => kit.normalizeSidecar({ format: 'nano-gb-sidecar', version: 1, channels: { 1: { preset: 'nope' } } }),
  () => kit.normalizeSidecar({ format: 'nano-gb-sidecar', version: 1, channels: { 1: { colour: 3 } } }), () => kit.normalizeSidecar({ format: 'x' }), () => new kit.Chip(100)];
bad.forEach((f, i) => { let ok = false; try { f(); } catch (e) { ok = e instanceof RangeError || e instanceof TypeError || e instanceof SyntaxError; } t(`bad argument ${i} throws`, ok); });

// block size does not change a sample
{ const e1 = new kit.Engine(44100), e2 = new kit.Engine(44100); for (const e of [e1, e2]) { e.noteOn(0, 1, 60, 1, e.resolvePreset('pwm'), { duration: 0.4 }); e.noteOn(1000, 2, 40, 1, e.resolvePreset('snare'), {}); }
  const a = new Float32Array(30000), b = new Float32Array(30000), r1 = new Float32Array(30000), r2 = new Float32Array(30000);
  e1.render(a, r1, 0, 30000); for (let o = 0; o < 30000;) { const n = Math.min(1 + (o * 7919 % 333), 30000 - o); e2.render(b, r2, o, n); o += n; }
  t('block-size independent', same(a, b) && same(r1, r2)); }

// the song player: plays, loops, tempo, sidecar; shared mode is cheaper
const tuneMid = fs.readFileSync(path.join(gbRoot, 'test', 'midi', 'tune.mid'));
{ const s = G.parseMidi(tuneMid); t('parseMidi: 4 channels, duration', s.channels.length === 4 && s.duration > 6.5 && s.duration < 7.5, JSON.stringify([s.channels, s.duration])); }
{ const a = G.renderMidi(tuneMid, { sampleRate: 22050 }), b = G.renderMidi(tuneMid, { sampleRate: 22050, chips: 'shared' }); t('renderMidi channel and shared both play', peak(a.left) > 0.1 && peak(b.left) > 0.1); }
{ const pl = new kit.SongPlayer(22050, G.parseMidi(tuneMid), { loop: true }); pl.playing = true; const L = new Float32Array(128); let n = 0; for (let i = 0; i < 22050 * 8 / 128; i++) { L.fill(0); pl.render(L, null, 0, 128); if (pl.pos < 22050) n++; } t('loop goes round', n > 200, n); }

// allocation: the chip's render loop makes nothing; the engine's control tick (750 a second) leaves only V8's short-lived double boxes
{ const c = new kit.Chip(48000); for (const [r, v] of [[0x12, 0xF0], [0x14, 0x87], [0x17, 0xF0], [0x19, 0x86], [0x1A, 0x80], [0x1C, 0x20], [0x1E, 0x85], [0x21, 0xF0], [0x22, 0x31], [0x23, 0x80]]) c.write(r, v);
  const L = new Float32Array(128), Rr = new Float32Array(128);
  for (let i = 0; i < 2000; i++) c.render(L, Rr, 0, 128);
  if (global.gc) global.gc();
  const h0 = process.memoryUsage().heapUsed; for (let i = 0; i < 22500; i++) c.render(L, Rr, 0, 128); const h1 = process.memoryUsage().heapUsed;
  t('chip: no allocation over 60 s of audio, 4 channels on', h1 - h0 < 16 * 1024, `${h1 - h0} bytes`); }
{ const e = new kit.Engine(48000); for (const [id, pr, nt] of [[1, 'pwm', 60], [2, 'lead', 67], [3, 'pad', 48]]) e.noteOn(0, id, nt, 1, e.resolvePreset(pr), {});
  e.addPreset('drone', { kind: 'noise', s: 1 }); e.noteOn(0, 5, 50, 1, e.resolvePreset('drone'), {});
  const L = new Float32Array(128), Rr = new Float32Array(128);
  for (let i = 0; i < 2000; i++) e.render(L, Rr, 0, 128);
  if (global.gc) global.gc();
  const h0 = process.memoryUsage().heapUsed; for (let i = 0; i < 22500; i++) e.render(L, Rr, 0, 128); const h1 = process.memoryUsage().heapUsed;
  const perSec = (h1 - h0) / 60; console.log(`engine, 4 moving voices: ${(perSec / 1024).toFixed(1)} KB/s of young-generation garbage (V8 heap numbers of the control tick; no objects)`);
  t('engine: under 64 KB/s of garbage', perSec < 64 * 1024, `${perSec} B/s`); }

console.log(`${pass} checks passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
