// flp2nano (Nano SMS) tests: a small FL project written by the test itself -> .mid + sidecar -> the web library Nano/sms.
//   node Nano/sms/test/flp2nano.test.mjs   (the FL project builder is copied from Nano/gb/test/flp2nano.test.mjs)
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { pitchZc } from '../../gb/test/lib.mjs';
import { parseFlp } from '../../gb/tools/flp2nano/flp.mjs';
import { fenceOf } from '../../gb/tools/flp2nano/fence.mjs';
import { convert, NANO_SMS_CHIP, PRESET_ORDER } from '../tools/flp2nano/convert.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const N = createRequire(import.meta.url)(path.join(here, '..', 'dist', 'nano-sms.js'));
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
    { name: 'Lead', plugin: 'BrokenNes2', state: { place: 0, duty: 1 } },                                 // NES pulse 1, 25% duty
    { name: 'Bass', plugin: 'BrokenNes2', state: { place: 2 } },                                          // NES triangle
    { name: 'Chords', plugin: 'BrokenNes2', state: { core: NANO_SMS_CHIP, console: 3, instrument: 8, volume: 500, pan: -100 } },
    { name: 'Psg', plugin: 'BrokenNes2', state: { core: 20, console: 3, channel: 4 } },                   // the Master System chip, noise
    { name: 'GB', plugin: 'BrokenNes2', state: { core: 30, console: 1 } },                                 // Nano GB: not this library's
  ],
  patterns: [
    [1, [{ pos: 0, len: 48, key: 72, ch: 1 }, { pos: 24, len: 48, key: 76, ch: 1 }, { pos: 96, len: 48, key: 60, ch: 1, fine: 170 },
      { pos: 0, len: 40, key: 72, ch: 2 }, { pos: 0, len: 10, key: 60, ch: 2, slide: true },
      { pos: 0, len: 96, key: 60, ch: 3 }, { pos: 0, len: 96, key: 64, ch: 3 }, { pos: 0, len: 96, key: 67, ch: 3 },
      { pos: 0, len: 30, key: 74, ch: 4 }, { pos: 0, len: 30, key: 60, ch: 5 }, { pos: 0, len: 10, key: 60, ch: 0 }]],
  ],
  items: [{ pos: 0, pattern: 1, len: 384 }],
};
const proj = parseFlp(flp(project));
const r = convert(proj, { title: 'test' });
const song = N.parseMidi(r.midi);
const on = (ch) => song.events.filter(e => e.type === 0x90 && e.ch === ch);
const spb = 60 / 150 / 96;
check(r.summary.length === 4, `four parts (got ${r.summary.length})`);
check(r.warnings.some(w => /Kick.*not BrokenNes2/.test(w)) && r.warnings.some(w => /"GB".*skipped/.test(w)), 'the sampler and the Nano GB part are skipped with a warning');
check(r.warnings.some(w => /25% duty plays as the PSG's 50% square/.test(w)) && r.warnings.some(w => /triangle plays as a PSG square/.test(w)), 'the NES duty and triangle are said');
const lead = on(0);
check(lead.length === 3 && lead[0].a === 72 && lead[1].a === 76 && lead[2].a === 61 && Math.abs(lead[1].sec - 24 * spb) < 1e-6, `mono NES lead ${lead.map(e => e.a)}`);
check(lead[0].b === Math.round(127 * 100 / 128), 'NES level = velocity x Volume');
check(r.sidecar.format === 'nano-sms-sidecar' && r.sidecar.channels['1'].preset === 'sms-tone' && r.sidecar.presets['sms-tone'].kind === 'tone', 'NES pulse -> sms-tone');
check(on(1).length === 1 && on(1)[0].b === 127 && r.summary[1].slides === 1 && r.summary[1].bendRange === 12, `triangle: full level, the slide bends a full octave ${JSON.stringify(r.summary[1])}`);
check(on(2).length === 3 && r.sidecar.channels['3'].preset === PRESET_ORDER[8] && r.sidecar.channels['3'].volume === 0.5 && r.sidecar.channels['3'].pan === -1, 'Nano SMS part: preset, volume, pan, polyphony');
check(song.events.some(e => e.type === 0xC0 && e.ch === 2 && e.a === 8), 'program change = Instrument');
check(r.sidecar.channels['4'].preset === 'sms-noise' && on(3).length === 1, 'the Master System noise channel -> sms-noise');
for (const s of r.summary) check(s.notes > 0, `part ${s.name} has notes`);
check(PRESET_ORDER.join() === N.presets.map(p => p.name).join(), 'PRESET_ORDER = the library\'s program order');
const pcm = N.renderMidi(r.midi, { sidecar: JSON.stringify(r.sidecar), sampleRate: 44100, mono: true });
check(pcm.some(x => x !== 0), 'the song renders, not silent');
const only = (keep) => JSON.stringify({ ...r.sidecar, channels: Object.fromEntries(Object.entries(r.sidecar.channels).map(([k, c]) => [k, k === keep ? c : { ...c, volume: 0 }])) });
const at = (tick) => Math.round(tick * spb * 44100);
const midiOf = (hz) => 69 + 12 * Math.log2(hz / 440);
const bass = N.renderMidi(r.midi, { sidecar: only('2'), sampleRate: 44100, mono: true });
const p0 = midiOf(pitchZc(bass, 44100, at(18), at(38)));
check(Math.abs(p0 - 60) < 0.2, `after the slide the triangle plays C4 (${p0.toFixed(2)})`);
const ld = N.renderMidi(r.midi, { sidecar: only('1'), sampleRate: 44100, mono: true });
const p1 = midiOf(pitchZc(ld, 44100, at(100), at(140)));
check(Math.abs(p1 - 60.5) < 0.15, `fine pitch +50 cents: ${p1.toFixed(2)}`);
const fence = fenceOf(r, 'Nano SMS', proj);
check(fence.lines.length === r.warnings.length + 1 && fence.lines.some(l => l.level === 'over' && /"GB"/.test(l.text)), 'fence: every warning is a line, the skipped Nano GB part is over');
console.log(`flp2nano (Nano SMS): ${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
