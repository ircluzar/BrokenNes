// flp2nano tests: a small FL project written by the test itself (the FLhd/FLdt events flp.mjs reads) -> .mid + sidecar -> the web library.
//   node Nano/gb/test/flp2nano.test.mjs            (self-contained)
//   FLP2NANO_SONGS="C:\Users\philt\Desktop\Reverse Coaster" node Nano/gb/test/flp2nano.test.mjs   (also the user's two projects, read only, never committed)
import fs from 'node:fs';
import path from 'node:path';
import { loadGb, pitchZc } from './lib.mjs';
import { parseFlp, parseBn2State } from '../tools/flp2nano/flp.mjs';
import { convert, nesPulseGb, FL_SLIDE } from '../tools/flp2nano/convert.mjs';

const G = loadGb();
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
check(proj.channels.length === 5 && proj.channels[1].pluginName === 'BrokenNes2', 'channels read');
check(proj.channels[4].pluginName === 'BrokenNes2' && proj.channels[4].state?.length === 84, 'the mixer plugin after the arrangement does not overwrite the last channel');
check(parseBn2State(proj.channels[3].state).params.core === 30, 'plugin state read');
check(proj.tempo === 150 && proj.arrangements[0].items.length === 2, 'tempo and playlist read');
const r = convert(proj, { title: 'test' });
const song = G.parseMidi(r.midi);
const on = (ch) => song.events.filter(e => e.type === 0x90 && e.ch === ch);
const spb = 60 / 150 / 96;
check(r.warnings.some(w => /Kick.*not BrokenNes2/.test(w)), 'the sampler is skipped with a warning');
check(r.summary.length === 4, 'four BrokenNes2 parts');
// mono NES lead: 72 (0-24), 76 (24-72), 60 +50 ct (96-144); then pattern 2 cut to 48 ticks
const lead = on(0);
check(lead.length === 4 && lead[0].a === 72 && lead[1].a === 76 && lead[2].a === 61 && lead[3].a === 62, `lead notes ${lead.map(e => e.a)}`);
check(Math.abs(lead[1].sec - 24 * spb) < 1e-6, 'the newer note takes over at its start');
const leadOff = song.events.filter(e => e.type === 0x80 && e.ch === 0);
check(leadOff.some(e => e.a === 72 && Math.abs(e.sec - 24 * spb) < 1e-6), 'the older note ends where the newer starts (monophonic, as the NES channel)');
check(leadOff.some(e => e.a === 62 && Math.abs(e.sec - (384 + 48) * spb) < 1e-6), 'the clip cuts its note');
check(r.sidecar.channels['1'].preset === 'nes-pulse-50' && r.sidecar.presets['nes-pulse-50'].duty === 2, 'NES pulse -> pulse preset at the same duty');
check(lead[0].b === Math.round(nesPulseGb(Math.round(15 * 100 / 128)) * 127 / 15), 'NES pulse level through the mixer balance');
// fine pitch +50 cents: key 61 with a bend of -50 cents
// bass: triangle, slides
check(r.sidecar.channels['2'].preset === 'nes-triangle' && on(1).length === 2 && on(1).every(e => e.b === 127), 'triangle: wave preset at full level');
check(r.summary[1].slides === 2 && r.summary[1].bentNotes === 2 && r.summary[1].bendRange === 24, `slides and the bend range ${JSON.stringify(r.summary[1])}`);
// Nano GB chords: polyphonic, preset 10 = pad, volume 0.5, pan left
check(on(2).length === 3 && r.sidecar.channels['3'].preset === 'pad' && r.sidecar.channels['3'].volume === 0.5 && r.sidecar.channels['3'].pan === -1, 'Nano GB part: preset, volume, pan, polyphony');
check(song.events.some(e => e.type === 0xC0 && e.ch === 2 && e.a === 10), 'program change = Instrument');
check(!song.channels.includes(9), 'channel 10 avoided');
// rendered: the drop glides from C4 (MIDI 60) to C2 (36), and the middle slide moves 48 -> 55
const pcm = G.renderMidi(r.midi, { sidecar: JSON.stringify(r.sidecar), sampleRate: 44100, mono: true });
// the bass is alone on MIDI channel 2: render it alone
const bassOnly = G.renderMidi(r.midi, { sidecar: JSON.stringify({ ...r.sidecar, channels: { ...r.sidecar.channels, 1: { ...r.sidecar.channels['1'], volume: 0 }, 3: { ...r.sidecar.channels['3'], volume: 0 } } }), sampleRate: 44100, mono: true });
const at = (tick) => Math.round(tick * spb * 44100);
const midiOf = (hz) => 69 + 12 * Math.log2(hz / 440);
const curve = (tick) => { const s = -FL_SLIDE.early, d = 40 + FL_SLIDE.extra; return 6000 + (3600 - 6000) * Math.max(0, Math.min(1, (tick - s) / d)); };
const p1 = midiOf(pitchZc(bassOnly, 44100, at(8), at(14))), p2 = midiOf(pitchZc(bassOnly, 44100, at(26), at(36)));
check(Math.abs(p1 - curve(11) / 100) < 0.6, `drop at tick 11: ${p1.toFixed(2)} vs ${(curve(11) / 100).toFixed(2)}`);
check(Math.abs(p2 - curve(31) / 100) < 0.6, `drop at tick 31: ${p2.toFixed(2)} vs ${(curve(31) / 100).toFixed(2)}`);
const p3 = midiOf(pitchZc(bassOnly, 44100, at(200), at(236))), p4 = midiOf(pitchZc(bassOnly, 44100, at(270), at(286)));
check(Math.abs(p3 - 48) < 0.15 && Math.abs(p4 - 55) < 0.15, `slide inside a note: ${p3.toFixed(2)} then ${p4.toFixed(2)}`);
check(pcm.length > 0, 'the whole song renders');
// the plain reading
const lin = convert(parseFlp(flp(project)), { slide: 'linear' });
check(!lin.midi.equals(r.midi), "slide 'linear' differs from FL's measured slide");
// an empty playlist: the patterns in order, loudly
const noPl = convert(parseFlp(flp({ ...project, items: [] })));
check(noPl.fallback && noPl.warnings.some(w => /PLAYLIST IS EMPTY/.test(w)), 'empty playlist -> patterns in order, with a warning');
check(Math.abs(noPl.songEnd - (384 + 384)) < 1e-9, `fallback length ${noPl.songEnd}`);

// ---- the user's projects, when they are here (read only) ----
const dir = process.env.FLP2NANO_SONGS;
if (dir && fs.existsSync(path.join(dir, 'overworld.flp'))) {
  const ow = convert(parseFlp(fs.readFileSync(path.join(dir, 'overworld.flp'))));
  check(ow.summary.map(s => s.notes).join() === '51,55,113,0', `overworld notes per part ${ow.summary.map(s => s.notes)} (FL's own export: 51, 55, 113)`);
  check(ow.summary[2].slides === 29 && ow.summary[2].bendRange >= 23 && ow.tempo === 156, 'overworld: 29 slides on the triangle, 156 BPM');
  const fm = G.parseMidi(fs.readFileSync(path.join(dir, 'overworld.mid'))), om = G.parseMidi(ow.midi);
  const keys = (s, ch) => s.events.filter(e => e.type === 0x90 && e.ch === ch).map(e => `${e.a}@${Math.round(e.sec * 2000)}`).join();   // (to the half millisecond: the two files count time in different ticks)
  for (const ch of [0, 1, 2]) check(keys(fm, ch) === keys(om, ch), `overworld channel ${ch + 1}: the same keys at the same times as FL's export`);
  const tt = convert(parseFlp(fs.readFileSync(path.join(dir, 'Title Theme.flp'))));
  check(tt.fallback && tt.tempo === 134 && tt.summary.map(s => s.notes).join() === '92,53,36,0', `Title Theme ${tt.summary.map(s => s.notes)}`);
}

console.log(`flp2nano: ${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
