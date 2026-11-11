// convert.mjs (Nano Genesis): an FL project model (the shared flp reader) -> a Standard MIDI File + a Nano Genesis sidecar, so the web library
// Nano/genesis plays the song as the BrokenNes2 plugin played it in FL. The pipeline of Nano GB's flp2nano (arrangement, FL's slide model, bend curve,
// MIDI writer imported unchanged) and Nano SMS's converter; only what an instance IS differs:
//   * the Nano Genesis chip (NANOMD, after the PC chips): the engine the web library runs, polyphonic; preset = the Instrument parameter (program % count).
//   * the Genesis chip MD in Direct mode: its pulses play the FM patch the Instrument parameter picks (FmPatchBank: the same register sets as Nano
//     Genesis's first 15 presets; the bank's last entry, the PSG square, = psgsquare), its triangle the sine, its noise the PSG noise.
//   * the Master System chip SMS and the NES chips in Direct mode: one PSG channel each (as Nano SMS's converter: a steady square, white noise).
// Anything else is skipped with the reason, and the fence (fence.mjs) says so.

import { parseBn2State } from '../../../gb/tools/flp2nano/flp.mjs';
import { arrange, buildVoices, pitchAt, writeMidi } from '../../../gb/tools/flp2nano/convert.mjs';

export const NANO_MD_CHIP = 88;       // Plugin/BrokenNes2.Plugin/Bn2Consoles.cs: NANOMD, after the PC chips (77-87); the plugin's selftest nanomore pins it
export const SMS_CHIP = 20, MD_CHIP = 21;
const CHIP_NAMES = { 0: 'FIX', 1: 'FIXS', 20: 'SMS', 21: 'MD', 88: 'NANOMD' };
// Nano/genesis/src/presets.js ORDER (program numbers; append-only)
export const PRESET_ORDER = ['brass', 'buzz', 'square', 'pulse', 'organ', 'strings', 'clarinet', 'flute', 'epiano', 'pluck', 'bell', 'marimba', 'bass', 'growl', 'sine', 'fmkick', 'psglead', 'psgsquare', 'psgarp', 'kick', 'tom', 'snare', 'hat', 'openhat', 'crash', 'laser', 'coin', 'jump', 'powerup', 'blip', 'explosion', 'hit'];
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
    let kind = null, why = '';
    if (P.mode === 1) why = 'ROM mode (it plays a game\'s channel, not notes)';
    else if (P.core === NANO_MD_CHIP) kind = 'nano';
    else if (P.core === MD_CHIP) { if (where >= 0 && where <= 3) kind = 'md'; else why = `Genesis chip on channel ${where}`; }
    else if (P.core === SMS_CHIP) { if (where >= 0 && where <= 3) kind = 'sms'; else why = `Master System chip on channel ${where}`; }
    else if (P.console === 0 && P.core >= 0 && P.core < 20) { if (where >= 0 && where <= 3) kind = 'nes'; else why = `NES chip on channel ${where}`; }
    else why = `chip ${chipName} on console ${P.console} is neither Nano Genesis, the Genesis chip, the Master System chip nor a NES chip`;
    if (!kind) { warnings.push(`channel ${c.index} "${c.name}": ${why}: skipped`); continue; }
    if (kind === 'nes' && P.core !== 0) warnings.push(`channel ${c.index} "${c.name}": NES chip ${chipName} (not FIX): converted like FIX`);
    out.push({ rack: c.index, name: c.name, kind, channel: where, params: P, chipName });
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
  const sidecar = { format: 'nano-genesis-sidecar', version: 1, channels: {}, presets: {} };
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
    if (P.pan !== 0 && Math.abs(P.pan) !== 100) warnings.push(`channel ${part.rack} "${part.name}": pan ${P.pan} becomes ${['left', 'both', 'right'][pan + 1]} (Nano Genesis pans an FM channel left, both or right only; the PSG is mono)`);
    let preset, program, kindLabel, levelOf;
    const noise = part.kind !== 'nano' && part.channel === 3;
    const N = PRESET_ORDER.length;
    if (part.kind === 'nano') {
      program = ((P.instrument % N) + N) % N;
      preset = PRESET_ORDER[program];
      kindLabel = `Nano Genesis, preset #${program} ${preset}`;
      levelOf = (vel) => vel;
    } else if (part.kind === 'md' && part.channel <= 2) {
      // the Genesis chip's FM voice: pulses = the Instrument's patch (FmPatchBank order = Nano Genesis presets 0-14; 15 and past = the PSG square), triangle = the sine
      const patch = part.channel === 2 ? 14 : Math.max(0, P.instrument);
      preset = patch >= 15 ? 'psgsquare' : PRESET_ORDER[patch];
      program = PRESET_ORDER.indexOf(preset);
      kindLabel = `Genesis ${NES_CHANNELS[part.channel]} (MD, Direct mode) -> Nano Genesis "${preset}"`;
      warnings.push(`channel ${part.rack} "${part.name}": the Genesis chip's per-note level matching (its NES-loudness calibration) is not converted: velocity x Volume is the level`);
      levelOf = (vel) => Math.round(127 * (vel / 128) * volume);
    } else {
      preset = noise ? 'md-noise' : 'md-tone';
      sidecar.presets[preset] = noise
        ? { kind: 'noise', a: 0, d: 0, s: 1, r: 0, desc: 'one PSG noise channel at a constant level: white noise, the rate from the pitch' }
        : { kind: 'tone', a: 0, d: 0, s: 1, r: 0, desc: 'one PSG square channel at a constant level' };
      program = PRESET_ORDER.indexOf(noise ? 'snare' : 'psgsquare');
      if (part.kind === 'sms' || part.kind === 'md') kindLabel = `${part.kind === 'md' ? 'Genesis' : 'Master System'} ${PSG_CHANNELS[part.channel]} (${part.chipName}, Direct mode) -> Nano Genesis "${preset}"`;
      else {
        kindLabel = `NES ${NES_CHANNELS[part.channel]} (${part.chipName}, Direct mode) -> Nano Genesis "${preset}"`;
        if (part.channel <= 1 && (P.duty & 3) !== 2) warnings.push(`channel ${part.rack} "${part.name}": the NES pulse's ${['12.5', '25', '50', '75'][P.duty & 3]}% duty plays as the PSG's 50% square`);
        if (part.channel === 2) warnings.push(`channel ${part.rack} "${part.name}": the NES triangle plays as a PSG square`);
        if (part.channel === 3 && P.noiseMode) warnings.push(`channel ${part.rack} "${part.name}": NES short (metallic) noise plays as white noise`);
      }
      levelOf = (vel) => { const v = Math.round(127 * (vel / 128) * volume); return part.kind === 'nes' && part.channel === 2 ? (v > 0 ? 127 : 0) : v; };
    }
    const sideVolume = part.kind === 'nano' ? volume : 1;
    sidecar.channels[String(mc + 1)] = { name: tname, preset, ...(sideVolume !== 1 ? { volume: sideVolume } : {}), ...(pan ? { pan } : {}) };
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
      if (!noise && s.key < 45 && !(part.kind === 'md' && part.channel <= 2)) lowNotes++;
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
  if (lowNotes) warnings.push(`${lowNotes} PSG tone notes are below A2 (110 Hz): the SN76489's 10-bit period cannot go lower, they play at A2 (FM goes lower)`);
  tempoTrack.push({ tick: arr.songEnd * scale, rank: 9, seq: seq++, bytes: [0xFF, 0x01, 3, 0x65, 0x6E, 0x64] });
  return { midi: writeMidi(tracks, ppq), sidecar, summary, warnings, info, ppq, tempo: proj.tempo, songEnd: arr.songEnd, flPpq: proj.ppq, fallback: arr.fallback, seconds: arr.songEnd / proj.ppq * 60 / proj.tempo };
}
