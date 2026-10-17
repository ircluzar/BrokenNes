// Tests of the Nano module that need no browser and no Core: node Nano/test/engine.test.mjs
// (the bit-exactness proofs are parity.mjs and plugin-parity.mjs; the AudioWorklet and the pages are browser-test.html)
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { loadNano, nanoRoot, distFile, rms, peak, pitchZc, midiHz, spectrum } from './lib.mjs';
import { rng } from './scenarios.mjs';

// --mutants: runs this file once per deliberate error injected into a copy of the module and requires each run to fail
const MUTANTS = [
  ['pulse period formula - 1.0 -> - 1.3', 'CPU_HZ / (16.0 * freq) - 1.0', 'CPU_HZ / (16.0 * freq) - 1.3'],
  ['triangle period divisor 32 -> 16', 'CPU_HZ / (32.0 * freq) - 1.0', 'CPU_HZ / (16.0 * freq) - 1.0'],
  ['stealing takes the newest voice', 'if (top.start < bestAt) {', 'if (top.start > -1) {'],
  ['one-shots obey note-off', 'if (v.p.oneShot) return;', 'if (false) return;'],
  ['a finished voice never leaves its channel', 'return p.s === 0 && this.tms(v, frame) >= p.a + p.d;', 'return false;'],
  ['notes ignore their duration', 'if (o.duration > 0) this.schedule', 'if (false) this.schedule'],
  ['events are not applied in order', 'while (i > 0 && evs[i - 1].frame > frame) i--;', 'i = 0;'],
  ['vibrato is ignored', 'if (p.vibDepth > 0 && p.vibRate > 0) {', 'if (false) {'],
  ['slides are ignored', 'if (p.slideMs > 0 || p.slideFrom !== 0 || p.slideTo !== 0) {', 'if (false) {'],
  ['arpeggios are ignored', 'if (p.arpSteps) {\n      let i', 'if (false) {\n      let i'],
  ['voices per channel unbounded', 'const MAX_STACK = 8;', 'const MAX_STACK = 80;'],
  ['every preset gets duty 1', "duty: num(p.duty, 1, 0, 3, 'duty') | 0,", 'duty: 1,'],
];
if (process.argv.includes('--mutants')) {
  const text = fs.readFileSync(distFile, 'utf8');
  let survived = 0;
  MUTANTS.forEach(([name, find, replace], i) => {
    const at = text.indexOf(find);
    if (at < 0 || text.indexOf(find, at + 1) >= 0) { console.log('ERROR   ' + name + ': text to change ' + (at < 0 ? 'not found' : 'found twice')); survived++; return; }
    const file = path.join(nanoRoot, '.mutant-' + process.pid + '-' + i + '.js');
    fs.writeFileSync(file, text.slice(0, at) + replace + text.slice(at + find.length));
    let code = 0;
    try { execFileSync(process.execPath, [fileURLToPath(import.meta.url)], { stdio: 'pipe', env: { ...process.env, NANO_DIST: file } }); } catch (e) { code = e.status || 1; }
    fs.rmSync(file);
    console.log((code ? 'caught   ' : 'SURVIVED ') + name);
    if (!code) survived++;
  });
  console.log(survived ? survived + ' mutant(s) survived' : 'all ' + MUTANTS.length + ' mutants caught');
  process.exit(survived ? 1 : 0);
}

const Nano = process.env.NANO_DIST ? loadNano(fs.readFileSync(process.env.NANO_DIST, 'utf8')) : loadNano();
const kit = Nano._kit;
let pass = 0, fail = 0;
const failures = [];
function check(name, ok, detail = '') {
  if (ok) pass++; else { fail++; failures.push(name); }
  if (!ok || process.argv.includes('-v')) console.log(`${ok ? 'ok  ' : 'FAIL'} ${name}${detail ? '  (' + detail + ')' : ''}`);
}
function throws(fn, what) { try { fn(); } catch (e) { return e; } return null; }

const RATE = 44100;
const render = (events, seconds, opts = {}) => Nano.renderOffline(events, seconds, { sampleRate: RATE, ...opts });
const one = (preset, note, seconds, off = null, extra = {}) => render([{ time: 0, type: 'on', note, velocity: 1, preset, id: 1, ...extra }, ...(off === null ? [] : [{ time: off, type: 'off', id: 1 }])], seconds);

// ---- the shipped files ----
{
  execFileSync(process.execPath, [path.join(nanoRoot, 'build.mjs'), '--check'], { stdio: 'pipe' });
  check('dist/ is exactly what build.mjs makes from src/', true);
  const license = fs.readFileSync(path.join(nanoRoot, '..', 'LICENSE.txt'), 'utf8').replace(/\r\n/g, '\n');
  check('dist/LICENSE.txt is the repository license', fs.readFileSync(path.join(nanoRoot, 'dist', 'LICENSE.txt'), 'utf8') === license);
  for (const f of ['nano.js', 'nano.mjs', 'nano-worklet.js']) {
    const b = fs.readFileSync(path.join(nanoRoot, 'dist', f));
    check(`${f} is under 80 KB (${b.length} bytes, ${zlib.gzipSync(b, { level: 9 }).length} gzip)`, b.length < 80 * 1024);
    check(`${f} makes no network calls (no fetch, XMLHttpRequest, WebSocket, importScripts, eval)`, !/\b(fetch|XMLHttpRequest|WebSocket|importScripts|eval)\s*\(/.test(b.toString('utf8')));
  }
  const sizes = fs.readdirSync(path.join(nanoRoot, 'dist')).map(f => fs.statSync(path.join(nanoRoot, 'dist', f)).size);
  check('no shipped file is over 1 MB', sizes.every(s => s < 1 << 20));
}

// ---- the API where there is no browser: it says so, it does not pretend ----
{
  const s = Nano.support();
  check('support() is false outside a browser and says why', s.ok === false && /Web Audio/.test(s.reason), s.reason);
  let err = null;
  await Nano.create().catch(e => { err = e; });
  check('create() rejects with code "unsupported" outside a browser', err && err.code === 'unsupported', err && err.message);
  check('noteNumber: names and numbers', Nano.noteNumber('C4') === 60 && Nano.noteNumber('a4') === 69 && Nano.noteNumber('F#3') === 54 && Nano.noteNumber('Bb2') === 46 && Nano.noteNumber('C-1') === 0 && Nano.noteNumber(61.5) === 61.5);
  check('noteNumber rejects junk', throws(() => Nano.noteNumber('H4')) instanceof RangeError && throws(() => Nano.noteNumber(NaN)) instanceof RangeError);
  check('unknown preset is an error', throws(() => render([{ time: 0, type: 'on', note: 60, preset: 'nope' }], 0.1)) instanceof RangeError);
  check('bad presets are rejected', throws(() => kit.normalizePreset({ kind: 'sine' })) instanceof RangeError &&
    throws(() => kit.normalizePreset({ kind: 'pulse', duty: 7 })) instanceof RangeError &&
    throws(() => kit.normalizePreset({ kind: 'triangle', sweep: { period: 1 } })) instanceof RangeError &&
    throws(() => kit.normalizePreset({ kind: 'pulse', oneShot: true, s: 0.5 })) instanceof RangeError &&
    throws(() => kit.normalizePreset({ kind: 'pulse', arp: { steps: [] } })) instanceof RangeError);
  check('24 presets, all named, all with a description', Nano.presets.length === 24 && Nano.presets.every(p => p.name && p.label && p.desc && ['pulse', 'triangle', 'noise'].includes(p.kind)));
}

// ---- every preset is audible; pitched ones play the note they are asked for ----
const CPU = 1789773;
const quant = (kindDiv, note) => { const f = midiHz(note); const p = Math.round(CPU / (kindDiv * f) - 1); return CPU / (kindDiv * (p + 1)); };
for (const p of Nano.presets) {
  const x = one(p.name, p.note, 1.0, 0.6);
  check(`preset ${p.name}: audible (peak ${peak(x).toFixed(3)})`, peak(x) > 0.03 && peak(x) < 0.7);
  check(`preset ${p.name}: finite samples`, x.every(Number.isFinite));
  const spec = kit.PRESETS[p.name];
  const flat = !spec.vib && !spec.slide && !spec.arp && !spec.sweep && !spec.noise && p.kind !== 'noise';
  if (flat) {
    const endsAt = spec.s === 0 ? Math.floor((spec.a + spec.d) * RATE / 1000 * 0.8) : 3000 + 16384;
    const f = pitchZc(x, RATE, 300, Math.min(300 + 16384, endsAt));
    const want = quant(p.kind === 'triangle' ? 32 : 16, p.note);
    const cents = 1200 * Math.log2(f / want);
    check(`preset ${p.name}: plays its note (${f.toFixed(1)} Hz, wanted ${want.toFixed(1)} Hz, ${cents.toFixed(1)} cents off)`, Math.abs(cents) < 6);
  }
  // after the note ends the voice goes quiet (a release tail included), and one-shots end by themselves
  const tail = rms(x, Math.floor(0.93 * RATE), RATE);
  check(`preset ${p.name}: silent again 0.9 s in (rms ${tail.toExponential(1)})`, spec.oneShot && (spec.a + spec.d) > 850 ? true : tail < 0.003);
}

// ---- the presets are distinct ----
{
  const feats = [];
  for (const p of Nano.presets) {
    const x = one(p.name, p.note, 0.7, 0.4);
    const f = [];
    const bands = 24;
    for (const start of [800, 6000]) {
      const mag = spectrum(x, start, 4096);
      for (let b = 0; b < bands; b++) {
        const lo = Math.floor(2 * Math.pow(mag.length / 2, b / bands)), hi = Math.floor(2 * Math.pow(mag.length / 2, (b + 1) / bands));
        let s = 0; for (let i = lo; i < Math.max(hi, lo + 1); i++) s += mag[i] * mag[i];
        f.push(Math.log10(Math.sqrt(s) + 1e-5));
      }
    }
    for (let w = 0; w < 7; w++) f.push(3 * Math.log10(rms(x, Math.floor(w * 0.1 * RATE), Math.floor((w + 1) * 0.1 * RATE)) + 1e-5));
    feats.push({ name: p.name, f });
  }
  let min = Infinity, pair = '';
  for (let i = 0; i < feats.length; i++) for (let j = i + 1; j < feats.length; j++) {
    let d = 0; for (let k = 0; k < feats[i].f.length; k++) d += (feats[i].f[k] - feats[j].f[k]) ** 2;
    d = Math.sqrt(d / feats[i].f.length);
    if (d < min) { min = d; pair = `${feats[i].name} / ${feats[j].name}`; }
  }
  check(`every pair of presets differs (closest: ${pair}, distance ${min.toFixed(2)})`, min > 0.25);
}

// ---- pitch moves where a preset says it should ----
{
  const track = (x, from, to, win = 1100, hop = 441) => { const r = []; for (let a = from; a + win <= to; a += hop) r.push(pitchZc(x, RATE, a, a + win)); return r; };
  const cents = (a, b) => 1200 * Math.log2(a / b);
  const lead = one('lead', 72, 1.6, 1.5), square = one('square', 72, 1.6, 1.5);
  const tl = track(lead, Math.floor(0.5 * RATE), Math.floor(1.4 * RATE), 4410, 441).filter(Number.isFinite), ts = track(square, Math.floor(0.5 * RATE), Math.floor(1.4 * RATE), 4410, 441).filter(Number.isFinite);
  const spread = (t) => cents(Math.max(...t), Math.min(...t));
  check(`vibrato: the lead's pitch wavers by ${spread(tl).toFixed(0)} cents, the square's by ${spread(ts).toFixed(0)}`, spread(tl) > 20 && spread(ts) < 8);
  const laser = one('laser', 84, 0.4);
  const tz = track(laser, 200, Math.floor(0.19 * RATE), 800, 200).filter(Number.isFinite);
  check(`slide: the laser falls ${(cents(tz[0], tz[tz.length - 1]) / 100).toFixed(1)} semitones in its first 190 ms`, tz.length > 4 && cents(tz[0], tz[tz.length - 1]) > 2400);
  const jump = one('jump', 60, 0.4);
  const tj = track(jump, 200, Math.floor(0.2 * RATE), 800, 200).filter(Number.isFinite);
  check(`slide: the jump rises ${(cents(tj[tj.length - 1], tj[0]) / 100).toFixed(1)} semitones`, tj.length > 3 && cents(tj[tj.length - 1], tj[0]) > 700);
  const arp = one('arp', 72, 0.5, 0.45);
  const ta = track(arp, 300, Math.floor(0.3 * RATE), 600, 150).filter(Number.isFinite).map(f => Math.round(cents(f, midiHz(72)) / 100));
  check(`arpeggio: the arp preset visits 0, 4, 7 and 12 semitones (saw ${[...new Set(ta)].sort((a, b) => a - b)})`, [0, 4, 7, 12].every(k => ta.includes(k)));
}

// ---- voice handling, as the plugin does it ----
{
  const mk = (events, secs = 1.2, presets) => render(events, secs, { presets });
  const eng = (events) => { const e = new kit.Engine(RATE, { presets: { hold: { kind: 'pulse', duty: 2 } } }); for (const ev of events) e.noteOn(Math.round(ev.time * RATE), ev.id, ev.note, 1, e.resolvePreset(ev.preset), ev.o ?? {}); return e; };
  // three pulse notes: the third takes the channel of the oldest, which comes back when the third ends
  const e = eng([{ time: 0, id: 1, note: 60, preset: 'hold' }, { time: 0.1, id: 2, note: 64, preset: 'hold' }, { time: 0.2, id: 3, note: 67, preset: 'hold' }]);
  const out = new Float32Array(RATE * 0.5); e.render(out, 0, out.length);
  check('a third pulse note steals the oldest voice\'s channel', e.stacks[0].length === 2 && e.stacks[1].length === 1 && e.stacks[0][1].id === 3 && e.stats.stolen === 1);
  e.noteOff(e.frame, 3); e.render(new Float32Array(1000), 0, 1000);
  check('the displaced note comes back when the thief ends', e.stacks[0].length === 1 && e.stacks[0][0].id === 1 && e.curNote[0] === 60);
  // one-shots ignore note-off; held notes obey it
  const e2 = eng([{ time: 0, id: 1, note: 38, preset: 'snare' }, { time: 0, id: 2, note: 60, preset: 'square' }]);
  e2.noteOff(100, 1); e2.noteOff(100, 2); e2.render(new Float32Array(2000), 0, 2000);
  check('a note-off ends a square but not a snare', e2.stacks[3].length === 1 && e2.stacks[0].length === 0);
  // duration ends a note by itself
  const x = render([{ time: 0, type: 'on', note: 69, preset: 'square', duration: 0.2, id: 1 }], 0.6);
  check('duration: sounds for 0.2 s then stops', rms(x, 0, 8000) > 0.05 && rms(x, 12000, 26000) < 1e-4);
  // an event in the past applies at once; events at the same frame keep their order
  const e3 = new kit.Engine(RATE); e3.render(new Float32Array(500), 0, 500);
  e3.noteOn(10, 1, 60, 1, e3.resolvePreset('bass'), {}); e3.noteOff(10, 1);
  e3.render(new Float32Array(200), 0, 200);
  check('late events apply immediately, in order', e3.stacks[0].length === 0 && e3.voices.size === 0);
  // a note that sustains on and on is bounded: the stack never grows past 8
  const e4 = new kit.Engine(RATE);
  for (let i = 0; i < 30; i++) e4.noteOn(0, i + 1, 60 + i, 1, e4.resolvePreset('bass'), {});
  e4.render(new Float32Array(300), 0, 300);
  check('thirty notes on one channel keep eight', e4.stacks[2].length === 8 && e4.voices.size === 8);
  // a voice that ended by itself leaves its channel (a kick is 150 ms)
  const e7 = eng([{ time: 0, id: 1, note: 36, preset: 'kick' }]); e7.render(new Float32Array(RATE / 4), 0, RATE / 4);
  check('a kick that decayed to nothing has left the triangle channel', e7.stacks[2].length === 0 && e7.voices.size === 0 && e7.gate[2] === false);
  // pinned channels must suit the kind
  check('a pulse preset cannot be pinned to the triangle', throws(() => { const e5 = new kit.Engine(RATE); e5.noteOn(0, 1, 60, 1, e5.resolvePreset('square'), { channel: 2 }); e5.render(new Float32Array(10), 0, 10); }) instanceof RangeError);
  // a release tail: the channel stays busy until it is over
  const e6 = eng([{ time: 0, id: 1, note: 60, preset: 'pwm' }]);
  e6.noteOff(1000, 1); e6.render(new Float32Array(1500), 0, 1500);
  check('release tail keeps the voice (pwm r = 60 ms)', e6.stacks[0].length === 1 && e6.stacks[0][0].released);
  e6.render(new Float32Array(4000), 0, 4000);
  check('and frees it afterwards', e6.stacks[0].length === 0);
}

// ---- determinism: the block size of the host does not change a sample ----
{
  const events = [];
  const r = rng(99);
  for (let i = 0; i < 40; i++) events.push({ time: Math.round(r() * 3000) / 1000, type: 'on', note: 30 + Math.floor(r() * 70), velocity: Math.round(r() * 100) / 100, preset: ['lead', 'kick', 'snare', 'arp', 'laser', 'pwm', 'bass', 'zap'][i % 8], id: i + 1 });
  for (let i = 0; i < 20; i++) events.push({ time: Math.round(r() * 3500) / 1000, type: 'off', id: 1 + Math.floor(r() * 40) });
  const renderBlocks = (block) => {
    const e = new kit.Engine(48000);
    for (const ev of events) { const f = Math.round(ev.time * 48000); if (ev.type === 'on') e.noteOn(f, ev.id, ev.note, ev.velocity, e.resolvePreset(ev.preset), {}); else e.noteOff(f, ev.id); }
    const out = new Float32Array(48000 * 4);
    for (let o = 0; o < out.length; o += block) e.render(out, o, Math.min(block, out.length - o));
    return out;
  };
  const a = renderBlocks(128), b = renderBlocks(1), c = renderBlocks(5000), d = renderBlocks(441);
  const same = (x, y) => x.every((v, i) => v === y[i]);
  check('rendering in blocks of 128, 1, 441 or 5000 samples gives the same samples', same(a, b) && same(a, c) && same(a, d));
}

// ---- robustness: random scripts stay finite and within range ----
{
  let bad = 0;
  for (let s = 1; s <= 10; s++) {
    const r = rng(s * 31337), ev = [];
    const names = Nano.presets.map(p => p.name);
    for (let k = 0; k < 120; k++) {
      const t = r() * 4, x = r();
      if (x < 0.6) ev.push({ time: t, type: 'on', note: r() * 130 - 1, velocity: r(), preset: names[Math.floor(r() * names.length)], id: k + 1 });
      else if (x < 0.85) ev.push({ time: t, type: 'off', id: 1 + Math.floor(r() * 100) });
      else if (x < 0.9) ev.push({ time: t, type: 'alloff' });
      else if (x < 0.93) ev.push({ time: t, type: 'kill' });
      else ev.push({ time: t, type: 'param', name: 'pitchBend', value: (r() - 0.5) * 6000 });
    }
    const out = render(ev, 4.5, { volume: 1.5 });
    if (!out.every(Number.isFinite) || peak(out) > 1) bad++;
  }
  check('120-event random scripts (notes down to -1 and up to 128, bends of +-30 st) stay finite and below full scale', bad === 0, `${bad} bad`);
}

// ---- speed (informational, with a generous guard) ----
{
  const e = new kit.Engine(48000);
  e.noteOn(0, 1, 60, 1, e.resolvePreset('lead'), {}); e.noteOn(0, 2, 64, 1, e.resolvePreset('lead'), {}); e.noteOn(0, 3, 36, 1, e.resolvePreset('bass'), {}); e.noteOn(0, 4, 38, 1, e.resolvePreset('rumble' in e.presets ? 'rumble' : 'hat'), {});
  const out = new Float32Array(128);
  const times = [];
  for (let rep = 0; rep < 5; rep++) { const t0 = performance.now(); for (let i = 0; i < 48000 * 5 / 128; i++) e.render(out, 0, 128); times.push((performance.now() - t0) / 5); }
  times.sort((a, b) => a - b);
  console.log(`speed: engine + chip with 4 voices, ${times[2].toFixed(1)} ms of CPU per second of audio at 48 kHz (best ${times[0].toFixed(1)}), ${(1000 / times[2]).toFixed(0)}x real time`);
  check('engine + chip run at least 8x faster than real time here', times[2] < 125);
}

console.log(`${pass} passed, ${fail} failed${fail ? ': ' + failures.join('; ') : ''}`);
process.exit(fail ? 1 : 0);
