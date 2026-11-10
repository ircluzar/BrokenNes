// convert.mjs (Nano NES): an FL project model -> a Standard MIDI File + a Nano NES sidecar ('nano-nes-sidecar'), so Nano NES plays the song as the
// BrokenNes2 plugin's NES chip played it in FL.
//
// It shares the FL side with Nano GB's flp2nano (Nano/gb/tools/flp2nano/convert.mjs: which channels are BrokenNes2, the playlist arrangement, voices,
// FL slide notes, the SMF writer); only the target differs. Converted: a BrokenNes2 instance on a NES chip in Direct mode (chip 0..19 on the NES
// console; FIX is the chip Nano NES mirrors sample for sample): ONE channel of the NES sound unit (pulse 1, pulse 2, triangle, noise), monophonic,
// last-note priority, at a constant level round(15 x vel/128 x Volume) (Plugin/BrokenNes2.Plugin/Record/NesDirect.cs). Here that becomes:
//   pulse    preset nes-pulse-<duty>: the same duty, a flat envelope; MIDI velocity = level x 127 / 15, which Nano NES turns back into the level
//   triangle preset nes-triangle (on or off: the triangle has no volume)
//   noise    preset nes-noise; the sidecar's noiseMode carries the plugin's Noise mode (long / short); the period follows the pitch, by the same
//            semitone -> period rule as Direct mode
// Unlike Nano GB, the sound is the NES's own: same chip, same mixer. A Nano GB instance (chip 30) or anything else is skipped with a warning.
// Each FL instance gets its own MIDI channel, so the web player's per-channel chips keep them apart (FL ran one NES chip per instance too).

import { classifyChannels, arrange, buildVoices, pitchAt, writeMidi } from '../../gb/tools/flp2nano/convert.mjs';

const NES_CHANNELS = ['pulse 1', 'pulse 2', 'triangle', 'noise'];
const DUTY = ['12', '25', '50', '75'];
// Nano NES presets.js ORDER (program numbers): the nearest built-in, written as the program change so a player without the sidecar is close
const PROGRAM = { pulse: [2, 0, 1, 1], triangle: 8, noise: 12 };

function vlq(n) { const out = [n & 127]; while ((n >>= 7) > 0) out.unshift((n & 127) | 128); return out; }

export function convert(proj, opts = {}) {
  const warnings = [...proj.warnings];
  const { parts: all, warnings: w1 } = classifyChannels(proj);
  const arr = arrange(proj);
  for (const w of w1) warnings.push(w.replace('is neither Nano GB nor a NES chip', 'is not a NES chip'));
  warnings.push(...arr.warnings);
  const parts = [];
  for (const p of all) {
    if (p.kind === 'nes') parts.push(p);
    else warnings.push(`channel ${p.rack} "${p.name}": a Nano GB chip (Game Boy), not the NES: skipped (convert it with Nano/gb/tools/flp2nano)`);
  }
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
  const sidecar = { format: 'nano-nes-sidecar', version: 1, channels: {}, presets: {} };
  const summary = [];
  let midiCh = 0;
  for (const part of parts) {
    if (midiCh === 9) midiCh++;
    if (midiCh > 15) { warnings.push(`channel ${part.rack} "${part.name}": more than 15 BrokenNes2 parts, no MIDI channel left: skipped`); continue; }
    const P = part.params, mc = midiCh++, nc = part.nesChannel;
    const { voices, slides } = buildVoices(arr.notes.filter(n => n.channel === part.rack), P.coarse * 100 + P.fine, opts.slide);
    for (const s of slides) if (!s.hit) warnings.push(`channel ${part.rack} "${part.name}": a slide note at tick ${s.at} has no note under it: it does nothing in FL either`);
    const trk = []; tracks.push(trk);
    const tname = part.name || `BrokenNes2 ${mc + 1}`;
    ev(trk, 0, 0, [0xFF, 0x03, ...vlq(Buffer.byteLength(tname)), ...Buffer.from(tname)]);
    if (P.pan !== 0) warnings.push(`channel ${part.rack} "${part.name}": pan ${P.pan} is ignored (the NES is mono)`);
    const volume = Math.max(0, Math.min(1, P.volume / 1000));
    let preset, program;
    const ch = { name: tname };
    if (nc <= 1) {
      preset = `nes-pulse-${DUTY[P.duty & 3]}`; program = PROGRAM.pulse[P.duty & 3];
      sidecar.presets[preset] = { kind: 'pulse', note: 72, duty: P.duty & 3, a: 0, d: 0, s: 1, r: 0, desc: `BrokenNes2 Direct mode: ${['12.5', '25', '50', '75'][P.duty & 3]}% pulse, constant level` };
    } else if (nc === 2) {
      preset = 'nes-triangle'; program = PROGRAM.triangle;
      sidecar.presets[preset] = { kind: 'triangle', note: 48, a: 0, d: 0, s: 1, r: 0, desc: 'BrokenNes2 Direct mode: the triangle, on or off' };
    } else {
      preset = 'nes-noise'; program = PROGRAM.noise;
      sidecar.presets[preset] = { kind: 'noise', note: 60, a: 0, d: 0, s: 1, r: 0, desc: 'BrokenNes2 Direct mode: noise, constant level, the period from the pitch' };
      if (P.noiseMode) ch.noiseMode = 1;
    }
    ch.preset = preset;
    sidecar.channels[String(mc + 1)] = ch;
    ev(trk, 0, 1, [0xC0 | mc, program]);
    // the level Direct mode plays at, as a MIDI velocity Nano NES rounds back to the same 4-bit level
    const levelOf = (vel) => {
      const nes = Math.max(0, Math.min(15, Math.round(15 * (vel / 128) * volume)));
      if (nc === 2) return nes > 0 ? 127 : 0;
      return nes === 0 ? 0 : Math.round(nes * 127 / 15);
    };
    // monophonic: the newest held voice owns the channel
    const segs = [];
    const times = [...new Set(voices.flatMap(v => [v.start, v.end]))].sort((a, b) => a - b);
    for (let i = 0; i + 1 < times.length; i++) {
      const t = times[i];
      let own = null;
      for (const v of voices) if (v.start <= t && t < v.end && (!own || v.start > own.start || v.start === own.start && v.order > own.order)) own = v;
      if (!own) continue;
      const last = segs[segs.length - 1];
      if (last && last.v === own && last.to === t) last.to = times[i + 1]; else segs.push({ v: own, from: t, to: times[i + 1] });
    }
    let maxBend = 0, notesOut = 0, bentNotes = 0;
    for (const s of segs) {
      s.key = Math.max(0, Math.min(127, Math.round(s.v.base / 100)));
      if (s.v.glides.length) for (let t = s.from; t <= s.to; t++) maxBend = Math.max(maxBend, Math.abs(pitchAt(s.v, t) - s.key * 100));
      else maxBend = Math.max(maxBend, Math.abs(pitchAt(s.v, s.from) - s.key * 100));
    }
    const range = Math.max(2, Math.min(127, Math.ceil(maxBend / 100 - 1e-9)));
    for (const bytes of [[101, 0], [100, 0], [6, range], [38, 0], [101, 127], [100, 127]]) ev(trk, 0, 1, [0xB0 | mc, ...bytes]);
    const bendVal = (cents) => Math.max(0, Math.min(16383, Math.round(8192 + cents / (range * 100) * 8192)));
    let lastBend = 8192;
    const bend = (tick, cents) => { const b = bendVal(cents); if (b !== lastBend) { ev(trk, tick, 2, [0xE0 | mc, b & 127, b >> 7]); lastBend = b; } };
    ev(trk, 0, 1, [0xE0 | mc, 0, 64]);
    for (const s of segs) {
      const vel = levelOf(s.v.velocity);
      if (vel <= 0) continue;
      notesOut++; if (s.v.glides.length) bentNotes++;
      ev(trk, s.from * scale, 3, [0x90 | mc, s.key, vel]);
      ev(trk, s.to * scale, 0, [0x80 | mc, s.key, 0]);
    }
    const marks = new Set();
    for (const s of segs) { marks.add(s.from); for (const g of s.v.glides) for (let t = g.start * scale; t <= (g.start + g.len) * scale; t += bendStep) marks.add(t / scale); }
    for (const t of [...marks].sort((a, b) => a - b)) {
      let o = null; for (const s of segs) if (s.from <= t && t < s.to) o = s;
      if (!o) continue;
      const inGlide = o.v.glides.some(g => t >= g.start && t < g.start + g.len);
      bend(Math.round(t * scale), pitchAt(o.v, inGlide ? Math.min(o.to, t + lead) : t) - o.key * 100);
    }
    summary.push({ midiChannel: mc + 1, rack: part.rack, name: tname, kind: `NES ${NES_CHANNELS[nc]} (${part.chipName}, Direct mode) -> Nano NES "${preset}"`, preset, program, volume, notes: notesOut, slides: slides.length, bentNotes, bendRange: range, flNotes: voices.length });
  }
  tempoTrack.push({ tick: arr.songEnd * scale, rank: 9, seq: seq++, bytes: [0xFF, 0x01, 3, 0x65, 0x6E, 0x64] });
  return { midi: writeMidi(tracks, ppq), sidecar, summary, warnings, info: [], ppq, tempo: proj.tempo, songEnd: arr.songEnd, flPpq: proj.ppq, fallback: arr.fallback, seconds: arr.songEnd / proj.ppq * 60 / proj.tempo };
}
