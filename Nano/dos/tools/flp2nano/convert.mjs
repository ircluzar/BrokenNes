// convert.mjs (Nano DOS): an FL project model (the shared flp reader) -> a Standard MIDI File + a Nano DOS sidecar, so the web library Nano/dos plays
// the song as the BrokenNes2 plugin played it in FL. The pipeline of Nano GB's flp2nano (arrangement, FL's slide model, bend curve, MIDI writer imported
// unchanged); only what an instance IS differs:
//   * the Nano DOS chip (NANODOS, after NANOSNES): the engine the web library runs, polyphonic; preset = the Instrument parameter (program % count).
//   * the PC console's AdLib chips (PC_ADLIB and its engines) in Direct mode: one FM voice; its patch = the patch editor's own (OPC1) when the instance
//     has one, else the Instrument's bank patch when it is one of the first eight (Super VRUN DOS / Vaults DOS: Nano DOS presets 0-7), else the nearest
//     Nano DOS preset (said in the fence); PC_SPEAKER: the speaker.
//   * NES chips in Direct mode: Super VRUN DOS's own mapping (pulses by duty -> the SV pulses, triangle -> SV triangle, noise -> SV noise).
// Anything else is skipped with the reason, and the fence (fence.mjs) says so.

import { parseBn2State } from '../../../gb/tools/flp2nano/flp.mjs';
import { arrange, buildVoices, pitchAt, writeMidi } from '../../../gb/tools/flp2nano/convert.mjs';

export const NANO_DOS_CHIP = 90;      // Plugin/BrokenNes2.Plugin/Bn2Consoles.cs: NANODOS, after NANOSNES 89; the plugin's selftest nanomore pins it
export const ADLIB_CHIPS = [77, 78, 79, 80, 81, 82], SPEAKER_CHIP = 83;
const CHIP_NAMES = { 0: 'FIX', 1: 'FIXS', 77: 'PC_ADLIB', 78: 'PC_ADLIB_M036', 79: 'PC_ADLIB_D072', 80: 'PC_ADLIB_D074C', 81: 'PC_ADLIB_D074F', 82: 'PC_ADLIB_NUK174', 83: 'PC_SPEAKER', 90: 'NANODOS' };
// Nano/dos/src/presets.js ORDER (program numbers; append-only)
export const PRESET_ORDER = ['svpulse12', 'svpulse25', 'svpulse50', 'svtriangle', 'svmix', 'svnoise', 'svperc', 'vaults', 'piano', 'epiano', 'organ', 'strings', 'brass', 'flute', 'bass', 'bell', 'lead', 'kick', 'snare', 'hat', 'crash', 'beep', 'beeplead', 'beeparp', 'laser', 'coin', 'jump', 'powerup', 'blip', 'explosion'];
const NES_CHANNELS = ['pulse 1', 'pulse 2', 'triangle', 'noise'];
const PSG_CHANNELS = ['tone 1', 'tone 2', 'tone 3', 'noise'];

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
    else if (P.core === NANO_DOS_CHIP) kind = 'nano';
    else if (ADLIB_CHIPS.includes(P.core)) { kind = 'adlib'; const sec = st.sectionData.find(x => x.tag === 'OPC1'); if (sec && sec.data.length >= 12 && sec.data[0] === 1) extra = [...sec.data.subarray(1, 12)]; }
    else if (P.core === SPEAKER_CHIP) kind = 'speaker';
        else if (P.console === 0 && P.core >= 0 && P.core < 20) { if (where >= 0 && where <= 3) kind = 'nes'; else why = `NES chip on channel ${where}`; }
    else why = `chip ${chipName} on console ${P.console} is neither Nano DOS, an AdLib or speaker PC chip nor a NES chip`;
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
  const sidecar = { format: 'nano-dos-sidecar', version: 1, channels: {}, presets: {} };
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
    if (P.pan !== 0) warnings.push(`channel ${part.rack} "${part.name}": pan ${P.pan} is not converted (the AdLib and the speaker are mono)`);
    let preset, program, kindLabel, levelOf;
    const N = PRESET_ORDER.length;
    if (part.kind === 'nano') {
      program = ((P.instrument % N) + N) % N;
      preset = PRESET_ORDER[program];
      kindLabel = `Nano DOS, preset #${program} ${preset}`;
      levelOf = (vel) => vel;
    } else if (part.kind === 'adlib') {
      if (part.extra) {
        preset = 'flp-adlib-' + part.rack;
        sidecar.presets[preset] = { kind: 'fm', regs: part.extra, r: 100, desc: `the patch editor's own patch of "${part.name}"` };
        program = 0;
        kindLabel = `AdLib (${part.chipName}, Direct mode), its edited patch (OPC1) -> Nano DOS sidecar preset "${preset}"`;
      } else if (P.instrument >= 0 && P.instrument <= 7) {
        program = P.instrument; preset = PRESET_ORDER[program];
        kindLabel = `AdLib (${part.chipName}, Direct mode), bank patch ${P.instrument} -> Nano DOS "${preset}"`;
      } else {
        const near = P.instrument <= 23 ? 'organ' : ['piano', 'bell', 'organ', 'lead', 'bass', 'strings', 'strings', 'brass', 'brass', 'flute', 'lead', 'strings', 'svnoise', 'svnoise', 'svperc', 'svnoise'][Math.min(15, ((P.instrument - 24) >> 3))];
        program = PRESET_ORDER.indexOf(near); preset = near;
        warnings.push(`channel ${part.rack} "${part.name}": the plugin's patch ${P.instrument} (${P.instrument <= 23 ? 'a YM2413 instrument' : 'GM program ' + (P.instrument - 23)}) is not in Nano DOS: it plays the nearest preset, "${near}"`);
        kindLabel = `AdLib (${part.chipName}, Direct mode), bank patch ${P.instrument} -> Nano DOS "${preset}" (nearest)`;
      }
      if (part.chipName !== 'PC_ADLIB_M036') warnings.push(`channel ${part.rack} "${part.name}": played on ${part.chipName}'s OPL2 in FL; Nano DOS's OPL2 is MAME 0.36's (PC_ADLIB_M036): the same patch, its own quirks`);
      levelOf = (vel) => Math.round(127 * (vel / 128) * volume);
    } else if (part.kind === 'speaker') {
      preset = 'beep'; program = PRESET_ORDER.indexOf(preset);
      kindLabel = `PC speaker (PC_SPEAKER, Direct mode) -> Nano DOS "beep"`;
      levelOf = (vel) => vel > 0 && volume > 0 ? 127 : 0;
    } else {
      preset = part.channel === 3 ? 'svnoise' : part.channel === 2 ? 'svtriangle' : ['svpulse12', 'svpulse25', 'svpulse50', 'svpulse25'][P.duty & 3];
      program = PRESET_ORDER.indexOf(preset);
      kindLabel = `NES ${NES_CHANNELS[part.channel]} (${part.chipName}, Direct mode) -> Nano DOS "${preset}" (Super VRUN DOS's mapping)`;
      if (part.channel === 3 && P.noiseMode) warnings.push(`channel ${part.rack} "${part.name}": NES short (metallic) noise plays as the SV noise patch`);
      levelOf = (vel) => { const v = Math.round(127 * (vel / 128) * volume); return part.channel === 2 ? (v > 0 ? 127 : 0) : v; };
    }
    const sideVolume = part.kind === 'nano' ? volume : 1;
    sidecar.channels[String(mc + 1)] = { name: tname, preset, ...(sideVolume !== 1 ? { volume: sideVolume } : {}),  };
    ev(trk, 0, 1, [0xC0 | mc, program]);

    const mono = part.kind !== 'nano';
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
