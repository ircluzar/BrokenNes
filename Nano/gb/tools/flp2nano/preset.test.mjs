// flp2nano preset tests: FL projects written here byte by byte whose BrokenNes2 instances sit on a Presets chip and carry a Runaway preset in PRS1
// (the section, the .bn2preset file and its INS1 instrument all assembled here, apart from preset.mjs's reader), converted, played by the web library.
//   node Nano/gb/tools/flp2nano/preset.test.mjs
// Game Boy presets reach the sidecar (wave RAM exactly, duty, envelope; 7-bit noise said), other consoles' / damaged / empty presets are skipped
// with the reason (OVER in the fence, never silent). Each deliberate bug (convert.mjs MUTANT) must be caught by at least one check.
import { loadGb, pitchZc, rms } from '../../test/lib.mjs';
import { parseFlp } from './flp.mjs';
import { convert, MUTANT } from './convert.mjs';
import { fenceOf } from './fence.mjs';
import { crc32 } from './preset.mjs';

const G = loadGb();

// ---- the bytes ----
const zz = v => (v << 1) ^ (v >> 31);
function varint(v) { const o = []; while (v >= 0x80) { o.push((v & 127) | 128); v >>>= 7; } o.push(v); return o; }
const u32 = n => [n & 255, (n >>> 8) & 255, (n >>> 16) & 255, (n >>> 24) & 255];
const withCrc = b => [...b, ...u32(crc32(Buffer.from(b)))];
function ins(consoleId, kind, index, fields, blobs = []) {
  return withCrc([0x49, 0x4E, 0x53, 1, consoleId, kind, ...u32(index), ...u32(0), ...fields.flatMap(v => varint(zz(v))), ...blobs.flatMap(b => [...varint(b.length), ...b])]);
}
function presetFile(name, insBytes) {
  const h = [...Buffer.from(JSON.stringify({ format: 1, app: 'BrokenNes2', name }), 'utf8')];
  return withCrc([...Buffer.from('BN2P'), 1, 0, 0, 0, ...u32(h.length), ...h, 1, 0, ...u32(0x31534E49), ...u32(insBytes.length), ...insBytes]);
}
function prs1(file, damage = false) { const b = withCrc([1, 0, 0, 0, ...u32(file.length), ...file]); if (damage) b[b.length - 1] ^= 1; return b; }
function bn2State({ core, con, volume = 1000, section = null }) {
  const p = [core, volume, 0, 0, 0, 2, 0, 0, 0, 0, con, 0, 0, 0];
  const b = Buffer.alloc(12 + 56 + 4 + 12);
  b.write('BFNP', 0, 'latin1'); b.writeInt32LE(2, 4); b.writeInt32LE(14, 8); p.forEach((v, i) => b.writeInt32LE(v, 12 + 4 * i));
  b.writeInt32LE(0, 68); b.write('EMU2', 72, 'latin1'); b.writeInt32LE(1, 76); b.writeInt32LE(0, 80);
  return section ? [...b, ...Buffer.from('PRS1'), ...u32(section.length), ...section] : [...b];
}
function ev(id, v) { return id < 64 ? [id, v] : id < 128 ? [id, v & 255, v >> 8] : [id, v & 255, (v >> 8) & 255, (v >> 16) & 255, (v >>> 24) & 255]; }
function text(id, bytes) { const out = [id]; let n = bytes.length; do { out.push((n & 127) | (n > 127 ? 128 : 0)); n >>= 7; } while (n); return out.concat([...bytes]); }
const u16 = s => [...Buffer.from(s + '\0', 'utf16le')];
function notes(list) {
  const b = Buffer.alloc(24 * list.length);
  list.forEach((n, i) => { const o = 24 * i; b.writeUInt32LE(n.pos, o); b.writeUInt16LE(0x4000, o + 4); b.writeUInt16LE(n.ch, o + 6); b.writeUInt32LE(n.len, o + 8); b.writeUInt16LE(n.key, o + 12); b[o + 16] = 120; b[o + 18] = 64; b[o + 20] = 64; b[o + 21] = n.vel ?? 100; b[o + 22] = 128; b[o + 23] = 128; });
  return [...b];
}
function flp(channels, list, len) {
  const e = [...text(199, Buffer.from('26.1.0.5530\0', 'latin1')), ...ev(28, 1), 0xAC, 1, 1, 0, ...text(192, u16('FL Studio')), ...ev(156, 120000), ...ev(65, 1), ...text(224, notes(list))];
  channels.forEach((c, i) => { e.push(...ev(64, i), ...ev(21, 2), ...text(201, u16('BrokenNes2')), ...text(203, u16(c.name)), ...text(213, c.state)); });
  const pl = Buffer.alloc(88); pl.writeUInt32LE(0, 0); pl.writeUInt16LE(20480, 4); pl.writeUInt16LE(20481, 6); pl.writeUInt32LE(len, 8); pl.writeUInt16LE(499, 12); pl.writeUInt16LE(64, 18); pl.writeInt32LE(-1, 24); pl.writeInt32LE(-1, 28);
  e.push(...ev(99, 0), ...text(241, u16('Arrangement')), ...text(233, [...pl]));
  const head = Buffer.from([...Buffer.from('FLhd'), 6, 0, 0, 0, 0, 0, channels.length, 0, 96, 0]);
  return Buffer.concat([head, Buffer.from('FLdt'), Buffer.from(u32(e.length)), Buffer.from(e)]);
}

// ---- the presets ----
const WAVE = [...Array(32).keys()].map(i => ((i >> 1) + 3) % 16);            // a ramp (one rising crossing a period), not a built-in table
const waveRam = [...Array(16).keys()].map(i => WAVE[2 * i] << 4 | WAVE[2 * i + 1]);
const P = {
  wave: presetFile('ramp wave', ins(1, 42, 2, [1, 0, 1], [waveRam])),                                   // dac, length, level + wave RAM
  pulse: presetFile('thin fade', ins(1, 41, 1, [0, 0, 15, 0, 3])),                                      // pulse 2: duty 12.5%, length, env vol 15, fades, pace 3
  noise: presetFile('buzz', ins(1, 43, 3, [0, 15, 0, 0, 2, 1, 3])),                                     // noise: 7-bit width
  sms: presetFile('psg', ins(3, 20, 0, [100, 200, 300, 0, 0, 0, 255])),                                 // a Master System PSG tones preset
};
const chans = [
  { name: 'Wave', state: bn2State({ core: 25, con: 1, section: prs1(P.wave) }) },
  { name: 'Pulse', state: bn2State({ core: 25, con: 1, volume: 800, section: prs1(P.pulse) }) },
  { name: 'Noise', state: bn2State({ core: 25, con: 1, section: prs1(P.noise) }) },
  { name: 'SMS', state: bn2State({ core: 27, con: 3, section: prs1(P.sms) }) },
  { name: 'Bad', state: bn2State({ core: 25, con: 1, section: prs1(P.wave, true) }) },
  { name: 'Empty', state: bn2State({ core: 25, con: 1 }) },
];
const list = [];
for (let c = 0; c < chans.length; c++) list.push({ pos: 0, len: 192, key: 60, ch: c }, { pos: 96, len: 96, key: 67, ch: c });   // overlapping: a preset channel is monophonic
const proj = () => parseFlp(flp(chans, list, 384));

function run(mutant = 0) {
  const fails = [];
  const check = (ok, what) => { if (!ok) fails.push(what); };
  const pj = proj();
  const r = convert(pj, { title: 'presets', mutant });
  const fence = fenceOf(r, 'Nano GB', pj);
  const S = r.sidecar, by = name => Object.values(S.channels).find(c => c.name === name);
  const pre = name => S.presets[by(name)?.preset] ?? null;
  check(r.summary.length === 3, `3 Game Boy preset parts expected, got ${r.summary.map(s => s.name)}`);
  // wave: the 32 samples exactly
  check(JSON.stringify(pre('Wave')?.wave) === JSON.stringify(WAVE) && pre('Wave')?.kind === 'wave', `the wave preset's RAM is not the sidecar's wave (${JSON.stringify(pre('Wave'))})`);
  // pulse: duty 12.5%, the envelope fades with pace 3, the Volume on the channel
  check(pre('Pulse')?.kind === 'pulse' && pre('Pulse')?.duty === 0, `the pulse preset's duty is not 12.5% (${JSON.stringify(pre('Pulse'))})`);
  check(pre('Pulse')?.env?.step === 3 && !pre('Pulse')?.env?.up, `the pulse preset's envelope is not carried (${JSON.stringify(pre('Pulse'))})`);
  check(by('Pulse')?.volume === 0.8, 'the instance Volume is not on the channel');
  // noise: 7-bit said
  check(pre('Noise')?.kind === 'noise', 'the noise preset is not a noise preset');
  check(fence.lines.some(l => l.level === 'note' && l.rule === 'preset' && /7-bit/.test(l.text)), 'the 7-bit noise is not a fence note');
  // the others: OVER with the reason, never silent
  check(fence.lines.some(l => l.level === 'over' && /"SMS".*Master System preset/.test(l.text)), 'the Master System preset is not OVER');
  check(fence.lines.some(l => l.level === 'over' && /"Bad".*cannot be used.*checksum/.test(l.text)), 'the damaged preset is not OVER');
  check(fence.lines.some(l => l.level === 'over' && /"Empty".*no PRS1/.test(l.text)), 'the Presets chip without a preset is not OVER');
  check(fence.lines.filter(l => l.level === 'ok' && l.rule === 'preset').length === 3, 'the carried presets have no ok lines');
  // played by the web library: monophonic, the wave's pitch, the envelope's fade
  const song = G.parseMidi(r.midi);
  const mc = name => Number(Object.keys(S.channels).find(k => S.channels[k].name === name)) - 1;
  const ons = song.events.filter(e => e.type === 0x90 && e.ch === mc('Wave'));
  const offs = song.events.filter(e => e.type === 0x80 && e.ch === mc('Wave'));
  check(ons.length === 2 && offs.some(e => e.a === 60 && Math.abs(e.sec - 0.5) < 1e-6), 'the preset channel is not monophonic (the newest note takes over)');
  const solo = name => G.renderMidi(r.midi, { sidecar: JSON.stringify({ ...S, channels: Object.fromEntries(Object.entries(S.channels).map(([k, c]) => [k, { ...c, volume: c.name === name ? (c.volume ?? 1) : 0 }])) }), sampleRate: 44100, mono: true });
  const w = solo('Wave'), hz = pitchZc(w, 44100, 4410, 17640);
  check(Math.abs(1200 * Math.log2(hz / 261.6256)) < 15, `the wave preset plays middle C at ${hz.toFixed(1)} Hz`);
  const pu = solo('Pulse'), early = rms(pu, 441, 4410), late = rms(pu, 17640, 21000);
  check(early > 0.01 && late < early * 0.6, `the pulse preset does not fade (rms ${early.toFixed(4)} then ${late.toFixed(4)})`);
  return fails;
}

let failed = 0;
const base = run();
for (const f of base) console.log('FAIL', f);
failed += base.length;
console.log(`flp2nano presets: ${base.length ? 'FAILED' : 'all checks passed'}`);
for (const [name, bit] of Object.entries(MUTANT)) {
  const f = run(bit);
  console.log(`  deliberate bug ${name}: ${f.length ? `caught (${f[0]})` : 'NOT CAUGHT'}`);
  if (!f.length) failed++;
}
process.exit(failed ? 1 : 0);
