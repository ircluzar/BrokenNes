// convert.mjs (Nano SNES): an FL project model (the shared flp reader) -> a Standard MIDI File + a Nano SNES sidecar, so the web library Nano/snes
// plays the song as the BrokenNes2 plugin played it in FL. The pipeline of Nano GB's flp2nano (arrangement, FL's slide model, bend curve, MIDI writer
// imported unchanged); only what an instance IS differs:
//   * the Nano SNES chip (NANOSNES, after NANOMD): the engine the web library runs, polyphonic; preset = the Instrument parameter (program % count).
//   * a Presets chip (PRESETS / PRE-SNES) holding a SNES sample preset (PRS1: a Runaway rip saved in the project): the preset's own BRR bytes, loop,
//     envelope (ADSR / GAIN) and tuning become a sidecar preset, polyphonic as the plugin plays it. Read from the project, never from the library folder.
//   * the SNES chip and the NES chips in Direct mode: one voice each (pulses by duty: 12.5% / 25% / 50% samples, the triangle, the noise).
// Anything else is skipped with the reason, and the fence (fence.mjs) says so.

import { parseBn2State } from '../../../gb/tools/flp2nano/flp.mjs';
import { arrange, buildVoices, pitchAt, writeMidi } from '../../../gb/tools/flp2nano/convert.mjs';

export const NANO_SNES_CHIP = 89;     // Plugin/BrokenNes2.Plugin/Bn2Consoles.cs: NANOSNES, after NANOMD 88; the plugin's selftest nanomore pins it
export const SNES_CHIP = 19, PRESETS_CHIP = 23, PRE_SNES_CHIP = 26;
const CHIP_NAMES = { 0: 'FIX', 1: 'FIXS', 19: 'SNES', 23: 'PRESETS', 26: 'PRE-SNES', 89: 'NANOSNES' };
// Nano/snes/src/presets.js ORDER (program numbers; append-only)
export const PRESET_ORDER = ['piano', 'strings', 'brass', 'organ', 'choir', 'sine', 'square', 'pulse25', 'pulse12', 'saw', 'triangle', 'lead', 'bass', 'pluck', 'bell', 'marimba', 'arp', 'kick', 'tom', 'snare', 'hat', 'openhat', 'crash', 'laser', 'coin', 'jump', 'powerup', 'blip', 'explosion', 'hit'];
const NES_CHANNELS = ['pulse 1', 'pulse 2', 'triangle', 'noise'];
const PSG_CHANNELS = ['tone 1', 'tone 2', 'tone 3', 'noise'];

// PRS1 (Plugin/BrokenNes2.Plugin/Presets/PresetSection.cs) -> the .bn2preset (Presets/PresetFile.cs: "BN2P" u16 version u16 flags u32 N, N bytes JSON header,
// u16 blob count, blobs (tag u32 length bytes), u32 CRC) -> INS1 (Instruments/InstrumentCodec.cs: "INS" version console kind i32 index i32 origin, zig-zag varints
// in the schema's order, blobs: varint length + bytes, CRC). A SNES sample's schema (Instruments/SnesInstruments.cs): env.adsr, env.ar, env.dr, env.sl, env.sr,
// gain.mode, gain.curve, gain.rate, vol.l, vol.r, tune.cents, mix.echo, mix.noise, mix.pmod, loop.addr; one blob, the BRR bytes from audio RAM address origin.
const SNES_FIELDS = ['adsr', 'ar', 'dr', 'sl', 'sr', 'gmode', 'gcurve', 'grate', 'voll', 'volr', 'tune', 'echo', 'noise', 'pmod', 'loop'];
export function snesPreset(data) {
  if (!data || data.length < 30 || data[0] !== 1) return { why: 'its preset section is not one this converter reads' };
  const len = data.readUInt32LE(4);
  if (len + 12 !== data.length) return { why: 'its preset section is damaged' };
  const f = data.subarray(8, 8 + len);
  if (f.toString('latin1', 0, 4) !== 'BN2P') return { why: 'not a preset file' };
  const hn = f.readUInt32LE(8);
  let header = {}; try { header = JSON.parse(f.toString('utf8', 12, 12 + hn)); } catch { }
  let o = 12 + hn; const count = f.readUInt16LE(o); o += 2;
  let ins = null;
  for (let i = 0; i < count && o + 8 <= f.length - 4; i++) { const tag = f.toString('latin1', o, o + 4), bl = f.readUInt32LE(o + 4); if (tag === 'INS1') ins = f.subarray(o + 8, o + 8 + bl); o += 8 + bl; }
  if (!ins) return { why: 'the preset has no instrument (INS1)' };
  if (ins.toString('latin1', 0, 3) !== 'INS' || ins[3] !== 1) return { why: 'the instrument is of an unknown version' };
  if (ins[4] !== 2 || ins[5] !== 1) return { why: `the preset is not a SNES sample (console ${ins[4]}, kind ${ins[5]}): only SNES samples convert to Nano SNES` };
  const origin = ins.readInt32LE(10);
  let p = 14;
  const varint = () => { let v = 0, sh = 0, x; do { x = ins[p++]; v |= (x & 127) << sh; sh += 7; } while (x & 128); return v >>> 0; };
  const fv = {};
  for (const k of SNES_FIELDS) { const z = varint(); fv[k] = (z >>> 1) ^ -(z & 1); }
  const blen = varint(), brr = ins.subarray(p, p + blen);
  const blocks = Math.floor(brr.length / 9), warnings = [];
  if (blocks < 1) return { why: 'the sample is empty' };
  const name = header.name || header.instrumentName || header.Name || 'preset';
  // the walk the S-DSP makes: until a block with the end flag; the loop pointer must point at a block of the sample (a corrupted one that points elsewhere is played once)
  let last = blocks - 1;
  for (let b = 0; b < blocks; b++) if (brr[b * 9] & 1) { last = b; break; }
  const loops = (brr[last * 9] & 2) !== 0;
  let loop = -1;
  if (loops) {
    const off = fv.loop - origin;
    if (off >= 0 && off % 9 === 0 && off / 9 <= last) loop = off / 9;
    else warnings.push(`the sample loops to $${fv.loop.toString(16)}, outside its own blocks (a corrupted loop pointer, or one into the context's audio RAM): it plays once here`);
  }
  const bytes = Buffer.from(brr.subarray(0, (last + 1) * 9));
  if (loop < 0) bytes[last * 9] = (bytes[last * 9] & ~2) | 1;
  const out = { kind: 'brr', brr: bytes.toString('base64'), loop, base: 261.6255653005986 * Math.pow(2, -fv.tune / 1200), note: 60, r: 0, desc: `SNES sample preset "${name}" from the project` };
  if (fv.adsr) out.adsr = [fv.ar, fv.dr, fv.sl, fv.sr]; else out.gainReg = fv.gmode << 7 | fv.gcurve << 5 | fv.grate;
  const wl = Math.abs(fv.voll), wr = Math.abs(fv.volr);
  if (wl !== 127 || wr !== 127) { out.gain = Math.min(1, Math.max(wl, wr) / 127); if (wl === 0 && wr > 0) out.pan = 1; else if (wr === 0 && wl > 0) out.pan = -1; if (wl !== wr && wl && wr) warnings.push(`the preset's left / right weights ${fv.voll} / ${fv.volr} play as one level (Nano SNES pans left, both or right)`); if (fv.voll < 0 || fv.volr < 0) warnings.push('a negative (phase-flipped) weight plays positive'); }
  if (fv.noise) warnings.push('the preset plays the S-DSP noise in place of its sample: Nano SNES plays the sample');
  if (fv.pmod) warnings.push('pitch modulation by the previous voice is not converted');
  if (fv.echo) warnings.push('echo is not converted (Nano SNES has the echo unit off)');
  return { preset: out, name, blocks: last + 1, warnings };
}

export function classifyChannels(proj) {
  const out = [], warnings = [];
  for (const c of proj.channels) {
    if (!/^BrokenNes2$/i.test(c.pluginName)) {
      if (c.type === 5) warnings.push(`channel ${c.index} "${c.name}" is an automation clip: automation is NOT converted`);
      else warnings.push(`channel ${c.index} "${c.name}" is not BrokenNes2 (${c.pluginName || 'type ' + c.type}): skipped`);
      continue;
    }
    const st = parseBn2State(c.state);
    if (!st) { warnings.push(`channel ${c.index} "${c.name}": BrokenNes2 with no readable state: skipped`); continue; }
    const P = st.params;
    const chipName = CHIP_NAMES[P.core] ?? `chip ${P.core}`;
    const where = P.channel > 0 ? P.channel - 1 : st.placeChannel;
    let kind = null, why = '', extra = null;
    if (P.mode === 1) why = 'ROM mode (it plays a game\'s channel, not notes)';
    else if (P.core === NANO_SNES_CHIP) kind = 'nano';
    else if (P.core === PRESETS_CHIP || P.core === PRE_SNES_CHIP) { const sec = st.sectionData.find(x => x.tag === 'PRS1'); const r = sec ? snesPreset(sec.data) : { why: 'it holds no preset (PRS1)' }; if (r.preset) { kind = 'preset'; extra = r; } else why = `Presets chip: ${r.why}`; }
    else if (P.core === SNES_CHIP) { if (where >= 0 && where <= 3) kind = 'snes'; else why = `SNES chip on channel ${where}`; }
        else if (P.console === 0 && P.core >= 0 && P.core < 20) { if (where >= 0 && where <= 3) kind = 'nes'; else why = `NES chip on channel ${where}`; }
    else why = `chip ${chipName} on console ${P.console} is neither Nano SNES, a SNES sample preset, the SNES chip nor a NES chip`;
    if (!kind) { warnings.push(`channel ${c.index} "${c.name}": ${why}: skipped`); continue; }
    if (kind === 'nes' && P.core !== 0) warnings.push(`channel ${c.index} "${c.name}": NES chip ${chipName} (not FIX): converted like FIX`);
    out.push({ rack: c.index, name: c.name, kind, channel: where, params: P, chipName, extra });
  }
  return { parts: out, warnings };
}

// the MIDI key whose pitch makes Nano SMS pick the noise rate the pitch picks in FL (engine.js: C5 and up rate 0, C4 and up 1, lower 2): the note itself
function vlq(n) { const out = [n & 127]; while ((n >>= 7) > 0) out.unshift((n & 127) | 128); return out; }

export function convert(proj, opts = {}) {
  const warnings = [...proj.warnings], info = [];
  const { parts, warnings: w1 } = classifyChannels(proj);
  const arr = arrange(proj);
  warnings.push(...w1, ...arr.warnings);
  const scale = Math.max(1, Math.round(960 / proj.ppq)), ppq = proj.ppq * scale;
  const bendStep = opts.bendStep ?? 2;
  const lead = (bendStep / 2) / scale + (32 / 44100) * proj.ppq * proj.tempo / 60;
  const usPerBeat = Math.round(60e6 / proj.tempo);
  let seq = 0;
  const ev = (list, tick, rank, bytes) => list.push({ tick, rank, seq: seq++, bytes });
  const tempoTrack = [];
  ev(tempoTrack, 0, 0, [0xFF, 0x58, 4, 4, 2, 24, 8]);
  ev(tempoTrack, 0, 0, [0xFF, 0x51, 3, (usPerBeat >> 16) & 255, (usPerBeat >> 8) & 255, usPerBeat & 255]);
  const name = opts.title || proj.title || 'FL song';
  ev(tempoTrack, 0, 0, [0xFF, 0x03, ...vlq(Buffer.byteLength(name)), ...Buffer.from(name)]);
  const tracks = [tempoTrack];
  const sidecar = { format: 'nano-snes-sidecar', version: 1, channels: {}, presets: {} };
  const summary = [];
  let midiCh = 0, lowNotes = 0;
  for (const part of parts) {
    if (midiCh === 9) midiCh++;
    if (midiCh > 15) { warnings.push(`channel ${part.rack} "${part.name}": more than 15 BrokenNes2 parts, no MIDI channel left: skipped`); continue; }
    const P = part.params, mc = midiCh++;
    const { voices, slides } = buildVoices(arr.notes.filter(n => n.channel === part.rack), P.coarse * 100 + P.fine, opts.slide);
    for (const s of slides) if (!s.hit) warnings.push(`channel ${part.rack} "${part.name}": a slide note at tick ${s.at} has no note under it: it does nothing in FL either`);
    const trk = [];
    tracks.push(trk);
    const tname = part.name || `BrokenNes2 ${mc + 1}`;
    ev(trk, 0, 0, [0xFF, 0x03, ...vlq(Buffer.byteLength(tname)), ...Buffer.from(tname)]);
    const volume = Math.max(0, Math.min(1, P.volume / 1000));
    const pan = P.pan <= -50 ? -1 : P.pan >= 50 ? 1 : 0;
    if (P.pan !== 0 && Math.abs(P.pan) !== 100) warnings.push(`channel ${part.rack} "${part.name}": pan ${P.pan} becomes ${['left', 'both', 'right'][pan + 1]} (Nano SNES pans a voice left, both or right only)`);
    let preset, program, kindLabel, levelOf;
    const N = PRESET_ORDER.length;
    if (part.kind === 'nano') {
      program = ((P.instrument % N) + N) % N;
      preset = PRESET_ORDER[program];
      kindLabel = `Nano SNES, preset #${program} ${preset}`;
      levelOf = (vel) => vel;
    } else if (part.kind === 'preset') {
      const x = part.extra;
      preset = 'flp-' + (part.name || 'preset').replace(/[^A-Za-z0-9]+/g, '-').toLowerCase() + '-' + part.rack;
      sidecar.presets[preset] = x.preset;
      program = 0;
      kindLabel = `SNES sample preset "${x.name}" (${x.blocks} BRR blocks${x.preset.loop >= 0 ? ', loops' : ''}) -> Nano SNES sidecar preset "${preset}"`;
      for (const w of x.warnings) warnings.push(`channel ${part.rack} "${part.name}": ${w}`);
      levelOf = (vel) => vel;
    } else {
      const noise = part.channel === 3;
      if (noise) {
        preset = 'snes-noise';
        sidecar.presets[preset] = { kind: 'noise', a: 0, d: 0, s: 1, r: 0, desc: 'one S-DSP voice of noise at a constant level, the clock from the pitch' };
        program = PRESET_ORDER.indexOf('snare');
      } else {
        preset = part.channel === 2 ? 'triangle' : ['pulse12', 'pulse25', 'square', 'pulse25'][P.duty & 3];
        program = PRESET_ORDER.indexOf(preset);
      }
      kindLabel = `${part.kind === 'snes' ? 'SNES chip' : 'NES'} ${NES_CHANNELS[part.channel]} (${part.chipName}, Direct mode) -> Nano SNES "${preset}"`;
      if (part.channel === 3 && P.noiseMode) warnings.push(`channel ${part.rack} "${part.name}": NES short (metallic) noise plays as the S-DSP's noise`);
      levelOf = (vel) => { const v = Math.round(127 * (vel / 128) * volume); return part.channel === 2 ? (v > 0 ? 127 : 0) : v; };
    }
    const sideVolume = part.kind === 'nano' || part.kind === 'preset' ? volume : 1;
    sidecar.channels[String(mc + 1)] = { name: tname, preset, ...(sideVolume !== 1 ? { volume: sideVolume } : {}), ...(pan ? { pan } : {}) };
    ev(trk, 0, 1, [0xC0 | mc, program]);

    const mono = part.kind !== 'nano' && part.kind !== 'preset';
    const segs = [];
    if (mono) {
      const times = [...new Set(voices.flatMap(v => [v.start, v.end]))].sort((a, b) => a - b);
      for (let i = 0; i + 1 < times.length; i++) {
        const t = times[i];
        let own = null;
        for (const v of voices) if (v.start <= t && t < v.end && (!own || v.start > own.start || v.start === own.start && v.order > own.order)) own = v;
        if (!own) continue;
        const last = segs[segs.length - 1];
        if (last && last.v === own && last.to === t) last.to = times[i + 1]; else segs.push({ v: own, from: t, to: times[i + 1] });
      }
    } else for (const v of voices) segs.push({ v, from: v.start, to: v.end });

    let maxBend = 0, conflicts = 0, notesOut = 0, bentNotes = 0;
    const keyOf = (s) => Math.max(0, Math.min(127, Math.round(s.v.base / 100)));
    for (const s of segs) {
      s.key = keyOf(s);
      
      if (s.v.glides.length) for (let t = s.from; t <= s.to; t++) maxBend = Math.max(maxBend, Math.abs(pitchAt(s.v, Math.min(t, s.to)) - s.key * 100));
      else maxBend = Math.max(maxBend, Math.abs(pitchAt(s.v, s.from) - s.key * 100));
    }
    const range = Math.max(2, Math.min(127, Math.ceil(maxBend / 100 - 1e-9)));
    for (const bytes of [[101, 0], [100, 0], [6, range], [38, 0], [101, 127], [100, 127]]) ev(trk, 0, 1, [0xB0 | mc, ...bytes]);
    const bendVal = (cents) => Math.max(0, Math.min(16383, Math.round(8192 + cents / (range * 100) * 8192)));
    let lastBend = 8192;
    const bend = (tick, cents) => { const b = bendVal(cents); if (b !== lastBend) { ev(trk, tick, 2, [0xE0 | mc, b & 127, b >> 7]); lastBend = b; } };
    ev(trk, 0, 1, [0xE0 | mc, 0, 64]);
    const owners = (t) => { let o = null; for (const s of segs) if (s.from <= t && t < s.to && (!o || s.from > o.from || s.from === o.from && s.v.order > o.v.order)) o = s; return o; };
    for (const s of segs) {
      const vel = levelOf(s.v.velocity);
      if (vel <= 0) continue;
      notesOut++;
      if (s.v.glides.length) bentNotes++;
      ev(trk, s.from * scale, 3, [0x90 | mc, s.key, vel]);
      ev(trk, s.to * scale, 0, [0x80 | mc, s.key, 0]);
    }
    const marks = new Set();
    for (const s of segs) { marks.add(s.from); for (const g of s.v.glides) for (let t = g.start * scale; t <= (g.start + g.len) * scale; t += bendStep) marks.add(t / scale); }
    for (const t of [...marks].sort((a, b) => a - b)) {
      const o = owners(t);
      if (!o) continue;
      const inGlide = o.v.glides.some(g => t >= g.start && t < g.start + g.len);
      const cents = pitchAt(o.v, inGlide ? Math.min(o.to, t + lead) : t) - o.key * 100;
      if (!mono) for (const s of segs) if (s !== o && s.from <= t && t < s.to && Math.abs((pitchAt(s.v, t) - s.key * 100) - cents) > 1) { conflicts++; break; }
      bend(Math.round(t * scale), cents);
    }
    if (conflicts) warnings.push(`channel ${part.rack} "${part.name}": ${conflicts} moments where two notes sounding together had different bends (MIDI bends a whole channel): the newest note's bend is used`);
    summary.push({ midiChannel: mc + 1, rack: part.rack, name: tname, kind: kindLabel, preset, program, volume, pan, notes: notesOut, slides: slides.length, bentNotes, bendRange: range, flNotes: voices.length });
  }
  tempoTrack.push({ tick: arr.songEnd * scale, rank: 9, seq: seq++, bytes: [0xFF, 0x01, 3, 0x65, 0x6E, 0x64] });
  return { midi: writeMidi(tracks, ppq), sidecar, summary, warnings, info, ppq, tempo: proj.tempo, songEnd: arr.songEnd, flPpq: proj.ppq, fallback: arr.fallback, seconds: arr.songEnd / proj.ppq * 60 / proj.tempo };
}
