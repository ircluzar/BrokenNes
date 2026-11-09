// flp.mjs: reads an FL Studio project (.flp) into a plain model. Our own parser, written from the public description of the format:
//
//   "FLhd" u32 6, u16 format, u16 channel count, u16 PPQ;   then "FLdt" u32 length, and a flat stream of events.
//   An event is one id byte, then by id: 0-63 one byte, 64-127 a u16, 128-191 a u32, 192-255 a 7-bit varint length and that many bytes.
//   Events belong to the last "new" event before them: 64 = a channel (generator) of the channel rack, 65 = a pattern, 99 = an arrangement.
//
// What is read (everything else is kept as raw events for the dump):
//   project  156 tempo x 1000, 199 FL version, 194 title
//   channel  21 type (0 sampler, 2 generator plugin, 4 layer, 5 automation clip ...), 201 the plugin's own name ("BrokenNes2"), 203 the name on the rack,
//            213 the plugin's saved state (for a native Fruity plugin: exactly what its SaveRestoreState wrote), 219 levels (pan, volume, pitch knob)
//   pattern  224 notes, 24 bytes each: u32 position, u16 flags (bit 3 = slide note), u16 rack channel, u32 length, u16 key (60 = C5), u16 group,
//            u8 fine pitch (120 = none), u8 -, u8 release, u8 colour (MIDI channel), u8 pan (64 = centre), u8 velocity (0-128, 100 default), u8 mod X, u8 mod Y
//            223 pattern controller events (recorded automation), 193 name
//   arrangement 241 name, 233 playlist items (FL 21+: 88 bytes; older FL: 32 or 60): u32 position, u16 pattern base (20480), u16 item index
//            (> base: pattern index - base; else a channel = an audio / automation clip), u32 length, u16 track (499 - n), u16 group, 2 -, u16 flags, 4 -,
//            i32 start offset, i32 end offset (-1 = the clip starts at the pattern's start)
//
// FL 26 quirk: event 172 in the project header is not always 4 bytes (3 and 5 seen in the user's FL 26.1 projects). The parser takes the
// length (4, else 3, else 5) after which a text event (id >= 192) follows, which is what every FL 26 header shows next.

export const SLIDE_FLAG = 0x08;

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

  const proj = { ppq, tempo: 120, version: '', title: '', channels: [], patterns: new Map(), arrangements: [], events: [], warnings: [] };
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
      case 64: ch = { index: value, type: -1, pluginName: '', name: '', state: null, levels: null }; proj.channels.push(ch); pat = null; break;
      case 21: if (ch) ch.type = value; break;
      case 65: pat = patOf(value); ch = null; break;
      case 99: arr = { index: value, name: '', items: [] }; proj.arrangements.push(arr); ch = null; pat = null; break;   // (the mixer's plugins come after: they belong to no channel)
      case 156: proj.tempo = value / 1000; break;
      case 199: proj.version = data.toString('latin1').replace(/\0+$/, ''); break;
      case 194: proj.title = utf16(data); break;
      case 201: if (ch) ch.pluginName = utf16(data); break;
      case 203: if (ch) ch.name = utf16(data); break;
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
//   "BFNP" (0x504E4642), i32 version (2), i32 count, count x i32 parameter values (Plugin/BrokenNes2.Plugin/Bn2Params.cs, append-only order),
//   i32 ROM path length + UTF-8 path, then "EMU2" i32 emulator id, i32 channel (where the instance sat), then optional tagged sections.

export const PARAMS = ['core', 'volume', 'pan', 'coarse', 'fine', 'duty', 'channel', 'noiseMode', 'mode', 'emulator', 'console', 'instrument', 'voice', 'recipe'];

export function parseBn2State(state) {
  if (!state || state.length < 12 || state.toString('latin1', 0, 4) !== 'BFNP') return null;
  const version = state.readInt32LE(4), count = state.readInt32LE(8);
  if (count < 0 || count > 64 || 12 + 4 * count > state.length) return null;
  const params = {};
  for (let i = 0; i < count; i++) params[PARAMS[i] ?? `p${i}`] = state.readInt32LE(12 + 4 * i);
  let o = 12 + 4 * count, romPath = '', placeEmu = 0, placeChannel = -1;
  if (o + 4 <= state.length) {
    const n = state.readInt32LE(o); o += 4;
    if (n > 0 && o + n <= state.length) romPath = state.toString('utf8', o, o + n);
    o += Math.max(0, n);
    if (o + 12 <= state.length && state.toString('latin1', o, o + 4) === 'EMU2') { placeEmu = state.readInt32LE(o + 4); placeChannel = state.readInt32LE(o + 8); o += 12; }
  }
  const sections = [];
  while (o + 8 <= state.length) { const tag = state.toString('latin1', o, o + 4), len = state.readInt32LE(o + 4); if (len < 0 || o + 8 + len > state.length) break; sections.push(tag); o += 8 + len; }
  return { version, params, romPath, placeEmu, placeChannel, sections };
}
