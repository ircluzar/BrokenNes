// flp2nano (Nano NES) tests: a small FL project written by the test itself -> .mid + nano-nes-sidecar -> Nano NES.
//   node Nano/test/flp2nano.test.mjs
// The FL project writer is the one of Nano/gb/test/flp2nano.test.mjs (copied: that file is a script, not a module).
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { parseFlp } from '../gb/tools/flp2nano/flp.mjs';
import { convert } from '../tools/flp2nano/convert.mjs';
import { fenceOf, fenceText } from '../gb/tools/flp2nano/fence.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const N = createRequire(import.meta.url)(path.join(here, '..', 'dist', 'nano.js'));
let pass = 0, fail = 0;
const check = (ok, what) => { if (ok) pass++; else { fail++; console.log('FAIL', what); } };
// ---- an FL project of our own ----
function ev(id, v) { return id < 64 ? [id, v] : id < 128 ? [id, v & 255, v >> 8] : [id, v & 255, (v >> 8) & 255, (v >> 16) & 255, (v >>> 24) & 255]; }
function text(id, bytes) { const out = [id]; let n = bytes.length; do { out.push((n & 127) | (n > 127 ? 128 : 0)); n >>= 7; } while (n); return out.concat([...bytes]); }
const u16 = (s) => [...Buffer.from(s + '\0', 'utf16le')];
function bn2State({ core = 0, volume = 1000, pan = 0, duty = 1, channel = 0, console: con = 0, instrument = 0, place = 0 }) {
  const p = [core, volume, pan, 0, 0, duty, channel, 0, 0, 0, con, instrument, 0, 0];
  const b = Buffer.alloc(12 + 56 + 4 + 12);
  b.write('BFNP', 0, 'latin1'); b.writeInt32LE(2, 4); b.writeInt32LE(14, 8); p.forEach((v, i) => b.writeInt32LE(v, 12 + 4 * i));
  b.writeInt32LE(0, 68); b.write('EMU2', 72, 'latin1'); b.writeInt32LE(1, 76); b.writeInt32LE(place, 80);
  return [...b];
}
function notes(list) {
  const b = Buffer.alloc(24 * list.length);
  list.forEach((n, i) => { const o = 24 * i; b.writeUInt32LE(n.pos, o); b.writeUInt16LE(0x4000 | (n.slide ? 8 : 0), o + 4); b.writeUInt16LE(n.ch, o + 6); b.writeUInt32LE(n.len, o + 8); b.writeUInt16LE(n.key, o + 12); b[o + 16] = n.fine ?? 120; b[o + 18] = 64; b[o + 20] = 64; b[o + 21] = n.vel ?? 100; b[o + 22] = 128; b[o + 23] = 128; });
  return [...b];
}
function playlist(items) {
  const b = Buffer.alloc(88 * items.length);
  items.forEach((it, i) => { const o = 88 * i; b.writeUInt32LE(it.pos, o); b.writeUInt16LE(20480, o + 4); b.writeUInt16LE(20480 + it.pattern, o + 6); b.writeUInt32LE(it.len, o + 8); b.writeUInt16LE(499 - (it.track ?? 0), o + 12); b.writeUInt16LE(64, o + 18); b.writeInt32LE(it.start ?? -1, o + 24); b.writeInt32LE(it.end ?? -1, o + 28); });
  return [...b];
}
function flp({ channels, patterns, items, tempo = 150 }) {
  const e = [...text(199, Buffer.from('26.1.0.5530\0', 'latin1')), ...ev(28, 1), 0xAC, 1, 1, 0, ...text(192, u16('FL Studio')), ...ev(156, tempo * 1000)];
  for (const [n, list] of patterns) e.push(...ev(65, n), ...text(224, notes(list)));
  channels.forEach((c, i) => {
    e.push(...ev(64, i), ...ev(21, c.state ? 2 : 0), ...text(201, u16(c.plugin ?? '')), ...text(203, u16(c.name)));
    if (c.state) e.push(...text(213, bn2State(c.state)));
  });
  e.push(...ev(99, 0), ...text(241, u16('Arrangement')), ...text(233, playlist(items ?? [])));
  e.push(...text(201, u16('Fruity Limiter')), ...text(213, [1, 2, 3]));   // a mixer plugin after the arrangement: belongs to no channel
  const head = Buffer.from([...Buffer.from('FLhd'), 6, 0, 0, 0, 0, 0, channels.length, 0, 96, 0]);
  const dt = Buffer.concat([Buffer.from('FLdt'), Buffer.from([e.length & 255, (e.length >> 8) & 255, (e.length >> 16) & 255, 0]), Buffer.from(e)]);
  return Buffer.concat([head, dt]);
}

const project = {
  tempo: 150,
  channels: [
    { name: 'Kick', plugin: '' },
    { name: 'Lead', plugin: 'BrokenNes2', state: { place: 0, duty: 2 } },
    { name: 'Bass', plugin: 'BrokenNes2', state: { place: 2 } },
    { name: 'Chords', plugin: 'BrokenNes2', state: { core: 30, console: 1, instrument: 10, volume: 500, pan: -100 } },
    { name: 'ROM', plugin: 'BrokenNes2', state: { core: 0, place: 1, channel: 0 } },
  ],
  patterns: [
    [1, [{ pos: 0, len: 48, key: 72, ch: 1 }, { pos: 24, len: 48, key: 76, ch: 1 }, { pos: 96, len: 48, key: 60, ch: 1, fine: 170 },        // overlap (mono), +50 cents
      { pos: 0, len: 40, key: 60, ch: 2 }, { pos: 0, len: 40, key: 36, ch: 2, slide: true },                                            // a two-octave drop
      { pos: 192, len: 96, key: 48, ch: 2 }, { pos: 240, len: 24, key: 55, ch: 2, slide: true },                                         // a slide in the middle of a note
      { pos: 0, len: 96, key: 60, ch: 3 }, { pos: 0, len: 96, key: 64, ch: 3 }, { pos: 0, len: 96, key: 67, ch: 3 },
      { pos: 0, len: 10, key: 60, ch: 0 }]],
    [2, [{ pos: 0, len: 96, key: 62, ch: 1 }]],
  ],
  items: [{ pos: 0, pattern: 1, len: 384 }, { pos: 384, pattern: 2, len: 48, start: 0, end: 48 }],
};
const proj = parseFlp(flp(project));
const r = convert(proj, { title: 'test' });
check(r.sidecar.format === 'nano-nes-sidecar', 'sidecar format');
check(r.summary.length === 3, `three NES parts (lead, bass, the pulse-2 instance): ${r.summary.length}`);
check(r.warnings.some(w => /Chords.*Nano GB.*skipped/.test(w)), 'the Nano GB part is skipped with a warning');
check(r.warnings.some(w => /Kick.*not BrokenNes2/.test(w)), 'the sampler is skipped');
check(r.sidecar.channels['1'].preset === 'nes-pulse-50' && r.sidecar.channels['2'].preset === 'nes-triangle', 'presets per channel');
const song = N.parseMidi(r.midi);
const on = (ch) => song.events.filter(e => e.type === 0x90 && e.ch === ch);
check(on(0).map(e => e.a).join() === '72,76,61,62', `mono lead keys (60 +50 ct is key 61 bent -50): ${on(0).map(e => e.a).join()}`);
check(on(0).every(e => e.b === Math.round(Math.round(15 * 100 / 128) * 127 / 15)), 'lead velocity = Direct-mode level');
check(on(1).length === 2 && on(1).every(e => e.b === 127), 'triangle on at full');
check(song.events.some(e => e.type === 0xE0 && e.ch === 1 && (e.b << 7 | e.a) < 8192 - 1000), 'the triangle drop is a bend');
const pcm = N.renderMidi(r.midi, { sidecar: r.sidecar, sampleRate: 22050 });
check(pcm.some(x => x !== 0) && pcm.every(Number.isFinite), 'Nano NES plays the conversion');
// the engine turns velocity back into the same 4-bit level: roundEven(15 x vel/127)
for (let lv = 1; lv <= 15; lv++) check(N._kit.roundEven(Math.fround(15 * Math.fround(Math.round(lv * 127 / 15) / 127))) === lv, `level ${lv} survives`);
// noise, with the short mode, travels in the sidecar
{
  const p2 = JSON.parse(JSON.stringify(project));
  p2.channels.push({ name: 'Hiss', plugin: 'BrokenNes2', state: { place: 3 } });
  p2.patterns[0][1].push({ pos: 0, len: 48, key: 60, ch: 5 });
  const pr = parseFlp(flp(p2));
  const r2 = convert(pr, {});
  check(r2.sidecar.presets['nes-noise'] !== undefined && Object.values(r2.sidecar.channels).some(c => c.preset === 'nes-noise'), 'noise part converted');
  check(N.renderMidi(r2.midi, { sidecar: r2.sidecar, sampleRate: 22050 }).some(x => x !== 0), 'noise part plays');
}
const f = fenceOf(r, 'Nano NES', proj);
check(f.engine === 'Nano NES' && f.lines.some(l => l.level === 'ok'), 'fence report');
console.log(fenceText(f).split('\n')[0]);
console.log(`${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
