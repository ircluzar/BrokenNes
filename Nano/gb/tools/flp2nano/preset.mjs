// preset.mjs: a Runaway preset carried by a BrokenNes2 instance (its PRS1 state section, docs/projects/flp-music-pipeline.md section 9) -> what flp2nano needs of it.
// The JS twin of the parts of the plugin's C# it reads (Presets/PresetSection.cs, Presets/PresetFile.cs, Instruments/InstrumentCodec.cs, Instruments/GbInstruments.cs):
//   PRS1     u8 version 1, u8 flags 0, u16 -, u32 N, the .bn2preset file (N bytes), u32 CRC-32 of everything before
//   file     "BN2P" u16 version u16 flags u32 H, H bytes UTF-8 JSON header, u16 blob count, blobs (u32 tag, u32 length, bytes), u32 CRC-32
//   INS1     "INS" u8 version 1 | u8 console | u8 kind | i32 index | i32 origin | fields: zig-zag varints in the kind's schema order | blobs: varint length + bytes | u32 CRC-32
// Only the Game Boy schemas are decoded (Nano GB is a Game Boy): another console's preset is named (its console) and refused by the caller.

export const PRESET_CHIPS = [23, 28];                       // Bn2Consoles.cs Cx.PresetChips: PRESETS, PRE-NES, PRE-GB, PRE-SNES, PRE-SMS, PRE-MD
export const CONSOLES = ['NES', 'Game Boy', 'SNES', 'Master System', 'Genesis'];    // ConsoleId
const KINDS = { 40: 'pulse1', 41: 'pulse2', 42: 'wave', 43: 'noise' };            // InstrumentKind GbPulse1..GbNoise
// GbInstruments.cs field order (the schema): the values are stored in this order, nothing else
const FIELDS = {
  pulse1: ['sweep.period', 'sweep.dir', 'sweep.shift', 'duty', 'length', 'env.vol', 'env.dir', 'env.pace'],
  pulse2: ['duty', 'length', 'env.vol', 'env.dir', 'env.pace'],
  wave: ['dac', 'length', 'level'],
  noise: ['length', 'env.vol', 'env.dir', 'env.pace', 'clock.shift', 'width', 'divisor'],
};
const RANGES = { 'sweep.period': 7, 'sweep.dir': 1, 'sweep.shift': 7, duty: 3, length: 63, 'env.vol': 15, 'env.dir': 1, 'env.pace': 7, dac: 1, level: 3, 'clock.shift': 15, width: 1, divisor: 7 };
const TAG_INS1 = 0x31534E49;

const CRC = (() => { const t = new Uint32Array(256); for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1; t[n] = c >>> 0; } return t; })();
export function crc32(b) { let c = 0xFFFFFFFF; for (const x of b) c = CRC[(c ^ x) & 255] ^ (c >>> 8); return (c ^ 0xFFFFFFFF) >>> 0; }

/** The preset of a parsed BrokenNes2 state (parseBn2State), or null when it has no PRS1 section. {ok, problem, console, consoleName, kind, index, name, fields, wave} */
export function presetOfState(st) {
  const s = st?.sectionData?.find(x => x.tag === 'PRS1');
  return s ? decodePrs1(Buffer.from(s.data)) : null;
}

export function decodePrs1(d) {
  const bad = (problem, extra = {}) => ({ ok: false, problem, ...extra });
  if (d.length < 12 + 18) return bad('cut short');
  if (d[0] !== 1) return bad(`section version ${d[0]}`);
  if (d[1] !== 0) return bad('an unknown feature flag');
  const n = d.readUInt32LE(4);
  if (n !== d.length - 12) return bad('damaged (its length)');
  if (d.readUInt32LE(d.length - 4) !== crc32(d.subarray(0, d.length - 4))) return bad('damaged (the checksum does not match)');
  const f = d.subarray(8, 8 + n);
  if (f.toString('latin1', 0, 4) !== 'BN2P') return bad('not a preset file');
  if (f.readUInt32LE(f.length - 4) !== crc32(f.subarray(0, f.length - 4))) return bad('damaged (the preset file checksum does not match)');
  const hn = f.readUInt32LE(8);
  if (12 + hn + 2 > f.length - 4) return bad('its header is cut short');
  let header = {};
  try { header = JSON.parse(f.toString('utf8', 12, 12 + hn)); } catch { return bad('its header is not JSON'); }
  let o = 12 + hn; const count = f.readUInt16LE(o); o += 2;
  let ins = null;
  for (let i = 0; i < count; i++) {
    if (o + 8 > f.length - 4) return bad('a blob is cut short');
    const tag = f.readUInt32LE(o), len = f.readUInt32LE(o + 4);
    if (o + 8 + len > f.length - 4) return bad('a blob is cut short');
    if (tag === TAG_INS1 && !ins) ins = f.subarray(o + 8, o + 8 + len);
    o += 8 + len;
  }
  if (!ins) return bad('it has no instrument (INS1)');
  const r = decodeIns(ins);
  r.name = header.name || header.instrument || '';
  return r;
}

export function decodeIns(b) {
  const bad = (problem, extra = {}) => ({ ok: false, problem, ...extra });
  if (b.length < 18 || b.toString('latin1', 0, 3) !== 'INS') return bad('its instrument is not an instrument');
  if (b[3] !== 1) return bad('its instrument is from another version');
  if (b.readUInt32LE(b.length - 4) !== crc32(b.subarray(0, b.length - 4))) return bad('its instrument is damaged (checksum)');
  const consoleId = b[4], kindId = b[5], consoleName = CONSOLES[consoleId] ?? `console ${consoleId}`;
  const out = { ok: false, problem: '', console: consoleId, consoleName, kindId, index: b.readInt32LE(6), fields: {}, wave: null };
  if (consoleId !== 1) { out.problem = `a ${consoleName} preset`; out.foreign = true; return out; }
  const kind = KINDS[kindId];
  if (!kind) return bad(`its instrument kind ${kindId} is not a Game Boy one`, { console: 1, consoleName });
  out.kind = kind;
  let pos = 14;
  const end = b.length - 4;
  const varint = () => { let v = 0, sh = 0; for (;;) { if (pos >= end || sh > 28) return null; const x = b[pos++]; v += (x & 127) * 2 ** sh; if (!(x & 128)) return v; sh += 7; } };
  for (const id of FIELDS[kind]) {
    const z = varint();
    if (z === null) return bad('its instrument is cut short', { console: 1, consoleName });
    const v = z % 2 ? -(z + 1) / 2 : z / 2;
    if (v < 0 || v > RANGES[id]) return bad(`its instrument is damaged (${id} = ${v})`, { console: 1, consoleName });
    out.fields[id] = v;
  }
  if (kind === 'wave') {
    const len = varint();
    if (len !== 16 || pos + 16 > end) return bad('its wave RAM is not 16 bytes', { console: 1, consoleName });
    const w = b.subarray(pos, pos + 16); pos += 16;
    out.wave = [...w].flatMap(x => [x >> 4, x & 15]);           // high nibble first: 32 samples, as the wave channel plays them
  }
  if (pos !== end) return bad('its instrument is damaged (trailing bytes)', { console: 1, consoleName });
  out.ok = true;
  return out;
}
