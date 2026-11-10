// flpsong.mjs: THE shared .flp reader of the music pipeline (docs/projects/flp-music-pipeline.md). An FL Studio project -> the neutral song model, the one input every game back end reads.
//
// Two layers:
//   parseFlp(buf)      the raw project (channels, patterns, arrangements, events): our own parser of the public FLhd/FLdt event format (moved here from Nano/gb/tools/flp2nano/flp.mjs, which now
//                      re-exports it unchanged so flp2nano keeps working byte for byte)
//   songFromFlp(buf)   the neutral song: every channel of the rack with its notes on the song's timeline (the playlist, or the patterns in order when it is empty), every BrokenNes2 instance's
//                      saved state decoded (parameters, where it sat, its project sections, the preset it carries in PRS1), and the caveats the reader itself knows
//   songJson(song)     the canonical text of the model: compact JSON, keys in a fixed order, integers only, every non-ASCII character as \uXXXX. The C# twin
//                      (Plugin/BrokenNes2.Plugin/Music/Flp/FlpSong.cs) writes the SAME BYTES for the same .flp: test/flpsong-identity checks it on the user's songs.
//
// The model is neutral: it says what FL and the plugin hold, never what a console does with it (the back ends decide that and report their own caveats).
import crypto from 'node:crypto';

export const SLIDE_FLAG = 0x08;
export const MODEL_FORMAT = 'brokennes-song', MODEL_VERSION = 1;

// ---------------------------------------------------------------- the raw project ----------------------------------------------------------------
//   "FLhd" u32 6, u16 format, u16 channel count, u16 PPQ;   then "FLdt" u32 length, and a flat stream of events.
//   An event is one id byte, then by id: 0-63 one byte, 64-127 a u16, 128-191 a u32, 192-255 a 7-bit varint length and that many bytes.
//   Events belong to the last "new" event before them: 64 = a channel (generator) of the channel rack, 65 = a pattern, 99 = an arrangement.
//   project  156 tempo x 1000, 199 FL version, 194 title
//   channel  21 type (0 sampler, 2 generator plugin, 4 layer, 5 automation clip ...), 201 the plugin's own name ("BrokenNes2"), 203 the name on the rack,
//            213 the plugin's saved state (a native Fruity plugin: exactly what its SaveRestoreState wrote), 219 levels (pan, volume, pitch knob)
//   pattern  224 notes, 24 bytes each: u32 position, u16 flags (bit 3 = slide note), u16 rack channel, u32 length, u16 key (60 = C5), u16 group,
//            u8 fine pitch (120 = none), u8 -, u8 release, u8 colour (MIDI channel), u8 pan (64 = centre), u8 velocity (0-128, 100 default), u8 mod X, u8 mod Y
//            223 pattern controller events (recorded automation), 193 name
//   arrangement 241 name, 233 playlist items (FL 21+: 88 bytes; older FL: 32 or 60)
// FL 26 quirk: event 172 in the project header is not always 4 bytes (3 and 5 seen in the user's FL 26.1 projects): the length after which a text event follows.

export function parseFlp(buf) {
  const b = Buffer.isBuffer(buf) ? buf : Buffer.from(buf);
  if (b.toString('latin1', 0, 4) !== 'FLhd') throw new Error('not an FL Studio project (no FLhd header)');
  const hlen = b.readUInt32LE(4);
  const ppq = b.readUInt16LE(12);
  let p = 8 + hlen;
  if (b.toString('latin1', p, p + 4) !== 'FLdt') throw new Error('FL project has no FLdt chunk');
  const dlen = b.readUInt32LE(p + 4);
  p += 8;
  const end = Math.min(b.length, p + dlen);

  const proj = { ppq, tempo: 120, tempoMilli: 120000, version: '', title: '', channels: [], patterns: new Map(), arrangements: [], events: [], warnings: [] };
  let ch = null, pat = null, arr = null;
  const patOf = (n) => { if (!proj.patterns.has(n)) proj.patterns.set(n, { index: n, name: '', notes: [], controllers: [] }); return proj.patterns.get(n); };
  const utf16 = (d) => d.toString('utf16le').replace(/\0+$/, '');

  while (p < end) {
    const at = p, id = b[p++];
    let value = null, data = null;
    if (id < 64) { value = b[p]; p += 1; }
    else if (id < 128) { value = b.readUInt16LE(p); p += 2; }
    else if (id < 192) {
      let L = 4;
      if (id === 172 && b[p + 4] < 0xC0) L = [3, 5].find(l => b[p + l] >= 0xC0) ?? 4;
      value = L === 4 ? b.readUInt32LE(p) : null; p += L;
    } else {
      let size = 0, sh = 0;
      for (;;) { const c = b[p++]; size += (c & 127) * 2 ** sh; sh += 7; if (!(c & 128)) break; }
      data = b.subarray(p, p + size); p += size;
    }
    proj.events.push({ at, id, value, data });

    switch (id) {
      case 64: ch = { index: value, type: -1, pluginName: '', name: '', state: null, levels: null, nameRaw: null, pluginRaw: null }; proj.channels.push(ch); pat = null; break;
      case 21: if (ch) ch.type = value; break;
      case 65: pat = patOf(value); ch = null; break;
      case 99: arr = { index: value, name: '', items: [] }; proj.arrangements.push(arr); ch = null; pat = null; break;
      case 156: proj.tempo = value / 1000; proj.tempoMilli = value; break;
      case 199: proj.version = data.toString('latin1').replace(/\0+$/, ''); break;
      case 194: proj.title = utf16(data); proj.titleRaw = Buffer.from(data); break;
      case 201: if (ch) { ch.pluginName = utf16(data); ch.pluginRaw = Buffer.from(data); } break;
      case 203: if (ch) { ch.name = utf16(data); ch.nameRaw = Buffer.from(data); } break;
      case 213: if (ch) ch.state = Buffer.from(data); break;
      case 219: if (ch && data.length >= 12) ch.levels = { pan: data.readInt32LE(0), volume: data.readInt32LE(4), pitch: data.readInt32LE(8) }; break;
      case 193: if (pat) pat.name = utf16(data); break;
      case 241: if (arr) arr.name = utf16(data); break;
      case 223: if (pat) pat.controllers.push(Buffer.from(data)); break;
      case 224: {
        if (!pat) break;
        for (let k = 0; k + 24 <= data.length; k += 24) pat.notes.push({
          pos: data.readUInt32LE(k), flags: data.readUInt16LE(k + 4), channel: data.readUInt16LE(k + 6), len: data.readUInt32LE(k + 8),
          key: data.readUInt16LE(k + 12), group: data.readUInt16LE(k + 14), fine: data[k + 16], release: data[k + 18], colour: data[k + 19],
          pan: data[k + 20], velocity: data[k + 21], modX: data[k + 22], modY: data[k + 23],
        });
        break;
      }
      case 233: {
        if (!arr) { arr = { index: 0, name: '', items: [] }; proj.arrangements.push(arr); }
        const size = [88, 60, 32].find(s => data.length % s === 0) ?? 32;
        for (let k = 0; k + size <= data.length; k += size) {
          const base = data.readUInt16LE(k + 4), idx = data.readUInt16LE(k + 6);
          arr.items.push({
            pos: data.readUInt32LE(k), len: data.readUInt32LE(k + 8), track: 499 - data.readUInt16LE(k + 12), flags: data.readUInt16LE(k + 18),
            pattern: idx > base ? idx - base : 0, channel: idx > base ? -1 : idx,
            start: data.readInt32LE(k + 24), end: data.readInt32LE(k + 28),
          });
        }
        break;
      }
    }
  }
  if (p !== end) proj.warnings.push(`the event stream ended ${p - end} bytes off its declared length: a part of the file was not understood`);
  return proj;
}

// ---- the BrokenNes2 plugin's saved state (Plugin/BrokenNes.Fruity/FruityPluginBase.cs + Plugin/BrokenNes2.Plugin/Bn2Plugin.cs SaveRestoreState) ----
//   "BFNP", i32 version (2), i32 count, count x i32 parameter values (Bn2Params.cs, append-only order), i32 ROM path length + UTF-8 path, then "EMU2" i32 emulator id, i32 channel,
//   then tagged sections (4-byte tag, i32 length, bytes).
export const PARAMS = ['core', 'volume', 'pan', 'coarse', 'fine', 'duty', 'channel', 'noiseMode', 'mode', 'emulator', 'console', 'instrument', 'voice', 'recipe'];
export const CONSOLES = ['NES', 'GB', 'SNES', 'SMS', 'MD'];

export function parseBn2State(state) {
  if (!state || state.length < 12 || state.toString('latin1', 0, 4) !== 'BFNP') return null;
  const version = state.readInt32LE(4), count = state.readInt32LE(8);
  if (count < 0 || count > 64 || 12 + 4 * count > state.length) return null;
  const params = {}, values = [];
  for (let i = 0; i < count; i++) { const v = state.readInt32LE(12 + 4 * i); params[PARAMS[i] ?? `p${i}`] = v; values.push(v); }
  let o = 12 + 4 * count, romPath = '', placeEmu = 0, placeChannel = -1, romPathBytes = null;
  if (o + 4 <= state.length) {
    const n = state.readInt32LE(o); o += 4;
    if (n > 0 && o + n <= state.length) { romPath = state.toString('utf8', o, o + n); romPathBytes = state.subarray(o, o + n); }
    o += Math.max(0, n);
    if (o + 12 <= state.length && state.toString('latin1', o, o + 4) === 'EMU2') { placeEmu = state.readInt32LE(o + 4); placeChannel = state.readInt32LE(o + 8); o += 12; }
  }
  const sections = [], sectionData = [];
  while (o + 8 <= state.length) {
    const tag = state.toString('latin1', o, o + 4), len = state.readInt32LE(o + 4);
    if (len < 0 || o + 8 + len > state.length) break;
    sections.push(tag); sectionData.push({ tag, at: o, data: state.subarray(o + 8, o + 8 + len) }); o += 8 + len;
  }
  return { version, params, values, romPath, romPathBytes, placeEmu, placeChannel, sections, sectionData, rest: state.length - o };
}

// ---------------------------------------------------------------- the neutral song ----------------------------------------------------------------

const sha = (b) => crypto.createHash('sha256').update(b).digest('hex');
const CRC_TABLE = (() => { const t = new Uint32Array(256); for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1; t[n] = c >>> 0; } return t; })();
function crc32(b) { let c = 0xFFFFFFFF; for (const x of b) c = CRC_TABLE[(c ^ x) & 255] ^ (c >>> 8); return (c ^ 0xFFFFFFFF) >>> 0; }

// text as FL stored it: UTF-16 code units (lone surrogates kept), trailing NULs dropped; latin1 the same way
function u16text(d) { if (!d) return ''; let s = ''; for (let k = 0; k + 1 < d.length; k += 2) s += String.fromCharCode(d[k] | d[k + 1] << 8); return s.replace(/\0+$/, ''); }

// PRS1 (Plugin/BrokenNes2.Plugin/Presets/PresetSection.cs): u8 version, u8 flags, u16 -, u32 file length, the .bn2preset file, u32 CRC-32. The file (Presets/PresetFile.cs):
// "BN2P" u16 version u16 flags u32 N, N bytes UTF-8 JSON header, u16 blob count, blobs (tag u32 length bytes), u32 CRC.
function presetOf(data) {
  const out = { ok: false, problem: '', sha256: '', header: '', blobs: [], file: '' };
  if (data.length < 8 + 18 + 4) { out.problem = 'cut short'; return out; }
  if (data[0] !== 1) { out.problem = `section version ${data[0]}`; return out; }
  if (data[1] !== 0) { out.problem = 'an unknown feature flag'; return out; }
  const len = data.readUInt32LE(4);
  if (len !== data.length - 12) { out.problem = 'damaged (its length)'; return out; }
  if (data.readUInt32LE(data.length - 4) !== crc32(data.subarray(0, data.length - 4))) { out.problem = 'damaged (the checksum does not match)'; return out; }
  const f = data.subarray(8, 8 + len);
  out.sha256 = sha(f); out.file = f.toString('base64');
  if (f.toString('latin1', 0, 4) !== 'BN2P') { out.problem = 'not a preset file'; return out; }
  const hn = f.readUInt32LE(8);
  if (12 + hn + 2 > f.length) { out.problem = 'its header is cut short'; return out; }
  out.header = f.toString('utf8', 12, 12 + hn);
  let o = 12 + hn; const count = f.readUInt16LE(o); o += 2;
  for (let i = 0; i < count && o + 8 <= f.length - 4; i++) {
    const tag = f.toString('latin1', o, o + 4), bl = f.readUInt32LE(o + 4);
    if (o + 8 + bl > f.length - 4) { out.problem = 'a blob is cut short'; return out; }
    out.blobs.push({ tag, length: bl, sha256: sha(f.subarray(o + 8, o + 8 + bl)) }); o += 8 + bl;
  }
  out.ok = true;
  return out;
}

// the timeline: the playlist's first arrangement with clips, else the patterns one after another (each rounded up to whole 4/4 bars) -- what FL plays and what flp2nano always did
function timeline(proj, caveats) {
  const notes = [];
  const arr = proj.arrangements.find(a => a.items.length) ?? null;
  let mode = 'playlist', songEnd = 0;
  if (arr) {
    if (proj.arrangements.filter(a => a.items.length).length > 1) caveats.push(`${proj.arrangements.length} arrangements: only "${arr.name}" (the first with clips) is read`);
    for (const it of arr.items) {
      if (it.pattern <= 0) { caveats.push(`playlist clip at tick ${it.pos} is an audio/automation clip of channel ${it.channel}: not read`); continue; }
      if (it.flags & 0x1000) { caveats.push(`playlist clip at tick ${it.pos} (pattern ${it.pattern}) is muted: skipped`); continue; }
      const pat = proj.patterns.get(it.pattern);
      if (!pat) continue;
      const from = it.start >= 0 ? it.start : 0, to = from + it.len;
      for (const n of pat.notes) {
        if (n.pos < from || n.pos >= to) continue;
        const start = it.pos + n.pos - from, end = Math.min(it.pos + it.len, start + n.len);
        notes.push({ ...n, start, end, pattern: it.pattern });
      }
      songEnd = Math.max(songEnd, it.pos + it.len);
    }
  } else {
    mode = 'patterns-in-order';
    const bar = proj.ppq * 4;
    let at = 0;
    for (const pat of [...proj.patterns.values()].sort((a, b) => a.index - b.index)) {
      if (!pat.notes.length) continue;
      const len = Math.max(bar, Math.ceil(Math.max(...pat.notes.map(n => n.pos + n.len)) / bar) * bar);
      for (const n of pat.notes) notes.push({ ...n, start: at + n.pos, end: at + n.pos + n.len, pattern: pat.index });
      at += len;
    }
    songEnd = at;
    caveats.push(`THE PLAYLIST IS EMPTY: the patterns are played one after another in pattern order (${[...proj.patterns.values()].filter(p => p.notes.length).map(p => p.index).join(', ')}), each rounded up to whole 4/4 bars. Put the patterns on the playlist in FL for the real arrangement.`);
  }
  for (const pat of [...proj.patterns.values()].sort((a, b) => a.index - b.index)) if (pat.controllers.length) caveats.push(`pattern ${pat.index} has recorded controller events: not read`);
  // stable: by start, then rack channel, then the order FL stored them
  notes.forEach((n, i) => { n.seq = i; });
  notes.sort((a, b) => a.start - b.start || a.channel - b.channel || a.seq - b.seq);
  return { notes, songEnd, mode, arrangement: arr ? arr.name : '' };
}

export function songFromFlp(buf) {
  const b = Buffer.isBuffer(buf) ? buf : Buffer.from(buf);
  const proj = parseFlp(b);
  const caveats = [...proj.warnings];
  const tl = timeline(proj, caveats);
  const instruments = [];
  for (const c of proj.channels) {
    const ins = { rack: c.index, name: u16text(c.nameRaw), plugin: u16text(c.pluginRaw), type: c.type, levels: c.levels ? [c.levels.pan, c.levels.volume, c.levels.pitch] : null, bn2: null, notes: [] };
    if (/^BrokenNes2$/i.test(ins.plugin)) {
      const st = parseBn2State(c.state);
      if (!st) { caveats.push(`channel ${c.index} "${ins.name}": BrokenNes2 with no readable state (another version?)`); }
      else {
        const P = st.params;
        const resolved = P.channel > 0 ? P.channel - 1 : st.placeChannel;     // Channel 0 = Auto: where the instance sat (EMU2)
        const sections = [];
        let preset = null;
        for (const s of st.sectionData) {
          sections.push({ tag: s.tag, length: s.data.length, sha256: sha(s.data) });
          if (s.tag === 'PRS1' && !preset) { preset = presetOf(s.data); if (!preset.ok) caveats.push(`channel ${c.index} "${ins.name}": its preset (PRS1) cannot be used: ${preset.problem}`); }
        }
        if (st.rest) caveats.push(`channel ${c.index} "${ins.name}": ${st.rest} bytes at the end of its state were not understood`);
        ins.bn2 = {
          version: st.version, params: st.values, console: P.console ?? 0, consoleName: CONSOLES[P.console] ?? `console ${P.console}`, core: P.core ?? 0,
          mode: P.mode === 1 ? 'rom' : 'direct', channel: resolved, emulator: st.placeEmu, romPath: st.romPath, sections, preset,
        };
      }
    } else if (c.type === 5) caveats.push(`channel ${c.index} "${ins.name}" is an automation clip: automation is not read`);
    instruments.push(ins);
  }
  const byRack = new Map(instruments.map(i => [i.rack, i]));
  for (const n of tl.notes) {
    const ins = byRack.get(n.channel);
    if (!ins) { caveats.push(`a note at tick ${n.start} is on rack channel ${n.channel}, which the project does not have: not read`); continue; }
    ins.notes.push([n.start, n.end, n.key, n.fine, n.velocity, n.pan, n.colour, n.flags & SLIDE_FLAG ? 1 : 0, n.pattern]);
  }
  return {
    format: MODEL_FORMAT, version: MODEL_VERSION,
    source: { fl: proj.version, title: u16text(proj.titleRaw), ppq: proj.ppq, tempoMilli: proj.tempoMilli, bytes: b.length, sha256: sha(b) },
    timeline: { mode: tl.mode, arrangement: tl.arrangement, end: tl.songEnd },
    noteFields: ['start', 'end', 'key', 'fine', 'velocity', 'pan', 'colour', 'slide', 'pattern'],
    instruments, caveats,
  };
}

// ---------------------------------------------------------------- canonical JSON ----------------------------------------------------------------
// Objects keep the key order they were built with (the C# twin writes the same order); numbers are integers; strings escape ", \, control characters and every non-ASCII code unit.
function str(s) {
  let o = '"';
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    if (c === 0x22) o += '\\"'; else if (c === 0x5C) o += '\\\\';
    else if (c < 0x20 || c > 0x7E) o += '\\u' + c.toString(16).padStart(4, '0');
    else o += s[i];
  }
  return o + '"';
}
function val(v) {
  if (v === null || v === undefined) return 'null';
  if (typeof v === 'boolean') return v ? 'true' : 'false';
  if (typeof v === 'number') { if (!Number.isInteger(v)) throw new Error('the song model holds integers only: ' + v); return String(v); }
  if (typeof v === 'string') return str(v);
  if (Array.isArray(v)) return '[' + v.map(val).join(',') + ']';
  return '{' + Object.entries(v).map(([k, x]) => str(k) + ':' + val(x)).join(',') + '}';
}
export function songJson(song) { return val(song) + '\n'; }
