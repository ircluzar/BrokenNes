// convert.mjs: an FL project model (flp.mjs) -> a Standard MIDI File + a Nano GB sidecar, so the web library plays the song as the BrokenNes2
// plugin played it in FL.
//
// How the plugin hears FL (Plugin/BrokenNes2.Plugin): a voice's pitch is cents from C5 = MIDI 60 (FL key 60 = MIDI 60) plus the instance's Coarse
// and Fine, read again every control step, so FL's per-note fine pitch and its slide notes reach the chip as a pitch that moves; velocity is
// FL's note velocity / 128. Two kinds of instance are converted:
//   * the Nano GB chip (chip 30): the engine the web library runs. Polyphonic like the web player; preset = the Instrument parameter.
//   * a NES chip in Direct mode (chip 0 = FIX, the default, on the NES console): the instance is ONE channel of the NES sound unit (pulse 1,
//     pulse 2, triangle, noise), monophonic with last-note priority (Bn2Plugin.ResolveOwner), at a constant level round(15 x vel/128 x Volume)
//     (Record/NesDirect.cs). It is converted to the nearest Nano GB sound: a plain pulse at the same duty and level, a wave channel playing
//     the NES triangle's own 32-step table at full level, the noise clock nearest the NES noise period. Same notes, timing and pitch; the
//     timbre is the Game Boy's, not the NES's (the game's library is Nano GB).
// Anything else (samplers, other plugins, other chips, ROM mode) is skipped with a warning.
//
// Pitch over time: per voice, its key + fine pitch, then every slide note of its channel that starts while the voice sounds glides it, linearly
// in cents over the slide note's length, to the slide note's key + fine (FL's slide notes: they make no sound of their own). MIDI pitch bend is
// per channel, so: a NES channel is monophonic anyway (the bend follows the owning voice); on a Nano GB channel the bend follows the newest
// sounding voice and a warning counts the notes whose own bend was different at the time.

import { SLIDE_FLAG, parseBn2State } from './flp.mjs';

export const NANO_GB_CHIP = 30;
const CHIP_NAMES = { 0: 'FIX', 1: 'FIXS', 30: 'NANOGB' };
// presets.js ORDER (program numbers; append-only)
const PRESET_ORDER = ['lead', 'square', 'thin', 'pwm', 'pluck', 'chime', 'arp', 'pulsebass', 'wavebass', 'subbass', 'pad', 'wavelead', 'organ', 'kick', 'tom', 'snare', 'hat', 'openhat', 'crash', 'coin', 'jump', 'laser', 'zap', 'powerup', 'explosion', 'hit', 'blip'];
const NES_CHANNELS = ['pulse 1', 'pulse 2', 'triangle', 'noise'];
const NES_NOISE_PERIODS = [4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068];
const NES_CPU = 1789773;
// the NES triangle's sequencer: 15 down to 0, 0 up to 15
const NES_TRIANGLE = [...Array(16).keys()].map(i => 15 - i).concat([...Array(16).keys()]);

// ---- 1. which channels, and what they are ----

export function classifyChannels(proj) {
  const out = [], warnings = [];
  for (const c of proj.channels) {
    const isBn2 = /^BrokenNes2$/i.test(c.pluginName);
    if (!isBn2) {
      if (c.type === 5) warnings.push(`channel ${c.index} "${c.name}" is an automation clip: automation is NOT converted (no pitch or tempo automation reaches the .mid)`);
      else warnings.push(`channel ${c.index} "${c.name}" is not BrokenNes2 (${c.pluginName || ['sampler', '', 'plugin', '', 'layer', 'automation'][c.type] || 'type ' + c.type}): skipped`);
      continue;
    }
    const st = parseBn2State(c.state);
    if (!st) { warnings.push(`channel ${c.index} "${c.name}": BrokenNes2 with no readable state (another version?): skipped`); continue; }
    const P = st.params;
    const chipName = CHIP_NAMES[P.core] ?? `chip ${P.core}`;
    let kind = null, nesChannel = -1, why = '';
    if (P.mode === 1) why = 'ROM mode (it plays a game\'s channel, not notes)';
    else if (P.core === NANO_GB_CHIP) kind = 'nanogb';
    else if (P.console === 0 && P.core >= 0 && P.core < 20) {
      nesChannel = P.channel > 0 ? P.channel - 1 : st.placeChannel;
      if (nesChannel >= 0 && nesChannel <= 3) kind = 'nes';
      else why = `NES chip on channel ${nesChannel} (not pulse/triangle/noise)`;
    } else why = `chip ${chipName} on console ${P.console} is neither Nano GB nor a NES chip`;
    if (!kind) { warnings.push(`channel ${c.index} "${c.name}": ${why}: skipped`); continue; }
    if (kind === 'nes' && P.core !== 0) warnings.push(`channel ${c.index} "${c.name}": NES chip ${chipName} (not FIX): converted like FIX`);
    out.push({ ch: c, rack: c.index, name: c.name, kind, nesChannel, params: P, state: st, chipName });
  }
  return { parts: out, warnings };
}

// ---- 2. the arrangement: notes on the song's timeline (FL ticks) ----

export function arrange(proj) {
  const warnings = [], notes = [];
  const arr = proj.arrangements.find(a => a.items.length) ?? null;
  let fallback = false, songEnd = 0;
  if (arr) {
    if (proj.arrangements.filter(a => a.items.length).length > 1) warnings.push(`${proj.arrangements.length} arrangements: only "${arr.name}" (the first with clips) is converted`);
    for (const it of arr.items) {
      if (it.pattern <= 0) { warnings.push(`playlist clip at tick ${it.pos} is an audio/automation clip of channel ${it.channel}: not converted`); continue; }
      if (it.flags & 0x1000) { warnings.push(`playlist clip at tick ${it.pos} (pattern ${it.pattern}) is muted: skipped`); continue; }
      const pat = proj.patterns.get(it.pattern);
      if (!pat) continue;
      const from = it.start >= 0 ? it.start : 0, to = from + it.len;
      for (const n of pat.notes) {
        if (n.pos < from || n.pos >= to) continue;                                   // FL plays the notes that start inside the clip
        const start = it.pos + n.pos - from, end = Math.min(it.pos + it.len, start + n.len);  // and cuts them at its end
        notes.push({ ...n, start, end, pattern: it.pattern });
      }
      songEnd = Math.max(songEnd, it.pos + it.len);
    }
  } else {
    fallback = true;
    const bar = proj.ppq * 4;
    let at = 0;
    for (const pat of [...proj.patterns.values()].sort((a, b) => a.index - b.index)) {
      if (!pat.notes.length) continue;
      const len = Math.max(bar, Math.ceil(Math.max(...pat.notes.map(n => n.pos + n.len)) / bar) * bar);
      for (const n of pat.notes) notes.push({ ...n, start: at + n.pos, end: at + n.pos + n.len, pattern: pat.index });
      at += len;
    }
    songEnd = at;
    warnings.push(`THE PLAYLIST IS EMPTY: the patterns are played one after another in pattern order (${[...proj.patterns.values()].filter(p => p.notes.length).map(p => p.index).join(', ')}), each rounded up to whole 4/4 bars. Put the patterns on the playlist in FL for the real arrangement.`);
  }
  for (const pat of proj.patterns.values()) if (pat.controllers.length) warnings.push(`pattern ${pat.index} has recorded controller events: not converted`);
  notes.sort((a, b) => a.start - b.start || a.channel - b.channel);
  return { notes, songEnd, fallback, warnings, arrangement: arr };
}

// ---- 3. pitch over time per voice (cents, MIDI 0 based) ----

// FL's slide as the plugin heard it, measured on the user's overworld.wav (FL 26.1, 156 BPM, nine clean 19-tick slides of the NES triangle, 66 -> 42):
// a straight line in cents that starts 0.85 tick BEFORE the slide note and lasts 1.8 ticks longer than it (83.3 ms for 76.1 ms; spread
// +-1 ms over the nine). The plain reading (a line over exactly the slide note's length) is opts.slide = 'linear'.
export const FL_SLIDE = { early: 0.85, extra: 1.8 };

export function buildVoices(notes, extraCents = 0, slideModel = 'fl') {
  const early = slideModel === 'linear' ? 0 : FL_SLIDE.early, extra = slideModel === 'linear' ? 0 : FL_SLIDE.extra;
  const voices = [], slides = [];
  let order = 0;
  for (const n of notes) {
    const cents = n.key * 100 + (n.fine - 120) + extraCents;
    if (n.flags & SLIDE_FLAG) slides.push({ at: n.start, start: n.start - early, len: Math.max(0, n.end - n.start) + extra, to: cents });
    else if (n.end > n.start) voices.push({ start: n.start, end: n.end, key: n.key, base: cents, velocity: n.velocity, glides: [], order: order++, colour: n.colour });
  }
  for (const s of slides) {
    let hit = 0;
    for (const v of voices) if (v.start <= s.at && s.at < v.end) { v.glides.push(s); hit++; }
    s.hit = hit;
  }
  for (const v of voices) v.glides.sort((a, b) => a.start - b.start);
  return { voices, slides };
}

export function pitchAt(v, t) {
  let cur = v.base;
  for (const g of v.glides) {
    if (t <= g.start) break;
    const from = cur;
    if (g.len <= 0 || t >= g.start + g.len) cur = g.to;
    else { cur = from + (g.to - from) * (t - g.start) / g.len; break; }
  }
  return cur;
}

// ---- 4. MIDI ----

function vlq(n) { const out = [n & 127]; while ((n >>= 7) > 0) out.unshift((n & 127) | 128); return out; }

function trackBytes(events) {
  events.sort((a, b) => a.tick - b.tick || a.rank - b.rank || a.seq - b.seq);
  const out = [];
  let last = 0;
  for (const e of events) { out.push(...vlq(e.tick - last), ...e.bytes); last = e.tick; }
  out.push(0, 0xFF, 0x2F, 0);
  return out;
}

function u32(n) { return [(n >>> 24) & 255, (n >>> 16) & 255, (n >>> 8) & 255, n & 255]; }

export function writeMidi(tracks, ppq) {
  const bytes = [...Buffer.from('MThd'), ...u32(6), 0, 1, (tracks.length >> 8) & 255, tracks.length & 255, (ppq >> 8) & 255, ppq & 255];
  for (const t of tracks) { const body = trackBytes(t); bytes.push(...Buffer.from('MTrk'), ...u32(body.length), ...body); }
  return Buffer.from(bytes);
}

// ---- 5. the whole conversion ----

export function convert(proj, opts = {}) {
  const warnings = [...proj.warnings], info = [];
  const { parts, warnings: w1 } = classifyChannels(proj);
  const arr = arrange(proj);
  warnings.push(...w1, ...arr.warnings);
  const scale = Math.max(1, Math.round(960 / proj.ppq)), ppq = proj.ppq * scale;   // finer MIDI ticks for smooth bends
  const bendStep = opts.bendStep ?? 2;                                                 // a bend every 2 ticks of 960 (about 0.8 ms at 156 BPM)
  // A bend holds until the next one and the engine reads it at its next 64-sample control step: each bend carries the pitch of the middle of its
  // step plus half a control step (0.73 ms at 44.1 kHz), so the stairs sit on FL's line instead of behind it.
  const lead = (bendStep / 2) / scale + (32 / 44100) * proj.ppq * proj.tempo / 60;    // in FL ticks
  const usPerBeat = Math.round(60e6 / proj.tempo);
  let seq = 0;
  const ev = (list, tick, rank, bytes) => list.push({ tick, rank, seq: seq++, bytes });

  const tempoTrack = [];
  ev(tempoTrack, 0, 0, [0xFF, 0x58, 4, 4, 2, 24, 8]);
  ev(tempoTrack, 0, 0, [0xFF, 0x51, 3, (usPerBeat >> 16) & 255, (usPerBeat >> 8) & 255, usPerBeat & 255]);
  const name = opts.title || proj.title || 'FL song';
  ev(tempoTrack, 0, 0, [0xFF, 0x03, ...vlq(Buffer.byteLength(name)), ...Buffer.from(name)]);
  const tracks = [tempoTrack];

  const sidecar = { format: 'nano-gb-sidecar', version: 1, channels: {}, presets: {} };
  const summary = [];
  let midiCh = 0;
  for (const part of parts) {
    if (midiCh === 9) midiCh++;                                  // channel 10 is GM drums in the web player
    if (midiCh > 15) { warnings.push(`channel ${part.rack} "${part.name}": more than 15 BrokenNes2 parts, no MIDI channel left: skipped`); continue; }
    const P = part.params, mc = midiCh++;
    const myNotes = arr.notes.filter(n => n.channel === part.rack);
    const { voices, slides } = buildVoices(myNotes, P.coarse * 100 + P.fine, opts.slide);
    for (const s of slides) if (!s.hit) warnings.push(`channel ${part.rack} "${part.name}": a slide note at tick ${s.at} has no note under it: it does nothing in FL either`);
    const trk = [];
    tracks.push(trk);
    const tname = part.name || `BrokenNes2 ${mc + 1}`;
    ev(trk, 0, 0, [0xFF, 0x03, ...vlq(Buffer.byteLength(tname)), ...Buffer.from(tname)]);

    // what the channel plays, and the level FL's velocity becomes
    const volume = Math.max(0, Math.min(1, P.volume / 1000));
    const pan = P.pan <= -50 ? -1 : P.pan >= 50 ? 1 : 0;
    if (P.pan !== 0 && Math.abs(P.pan) !== 100) warnings.push(`channel ${part.rack} "${part.name}": pan ${P.pan} becomes ${['left', 'centre', 'right'][pan + 1]} (the Game Boy pans each channel hard left, centre or hard right only)`);
    let preset, program, kindLabel, levelOf;
    if (part.kind === 'nanogb') {
      program = ((P.instrument % 27) + 27) % 27;
      preset = PRESET_ORDER[program];
      kindLabel = `Nano GB, preset #${program} ${preset}`;
      levelOf = (vel) => vel;
    } else {
      const nc = part.nesChannel;
      if (nc <= 1) {
        preset = `nes-pulse-${['12', '25', '50', '75'][P.duty & 3]}`;
        sidecar.presets[preset] = { kind: 'pulse', duty: P.duty & 3, a: 0, d: 0, s: 1, r: 0, desc: `the NES pulse of BrokenNes2 Direct mode: ${['12.5', '25', '50', '75'][P.duty & 3]}% duty, constant level` };
        program = [2, 0, 1, 0][P.duty & 3];
      } else if (nc === 2) {
        preset = 'nes-triangle';
        sidecar.presets[preset] = { kind: 'wave', wave: NES_TRIANGLE, a: 0, d: 0, s: 1, r: 0, desc: 'the NES triangle: its own 32-step table, full level, no volume' };
        program = 9;
      } else {
        preset = P.noiseMode ? 'nes-noise-short' : 'nes-noise';
        sidecar.presets[preset] = { kind: 'noise', a: 0, d: 0, s: 1, r: 0, desc: 'the NES noise of Direct mode: constant level, the clock nearest the NES period' };
        if (P.noiseMode) { warnings.push(`channel ${part.rack}: NES short (metallic) noise has no exact Game Boy twin: played as long noise`); }
        program = 15;
      }
      kindLabel = `NES ${NES_CHANNELS[nc]} (${part.chipName}, Direct mode, Duty ${['12.5', '25', '50', '75'][P.duty & 3]}%) -> Nano GB "${preset}"`;
      levelOf = (vel) => {
        const nes = Math.max(0, Math.min(15, Math.round(15 * (vel / 128) * volume)));
        if (nc === 2) return nes > 0 ? 127 : 0;                                         // the triangle is on or off
        if (nes === 0) return 0;
        // The NES mixes its pulses through a much weaker DAC than its triangle; the Game Boy gives every channel the same 4-bit scale. So the
        // Game Boy level is the NES pulse's loudness against the NES triangle's (which is played at full level here), not the NES number itself.
        const target = nc === 3 ? nesNoiseGb(nes) : nesPulseGb(nes);
        const v = Math.max(1, Math.min(127, Math.round(target * 127 / 15)));      // the sidecar's volume is 1 for a NES part: Volume is in here
        return v;
      };
    }
    const sideVolume = part.kind === 'nanogb' ? volume : 1;
    sidecar.channels[String(mc + 1)] = { name: tname, preset, ...(sideVolume !== 1 ? { volume: sideVolume } : {}), ...(pan ? { pan } : {}) };
    ev(trk, 0, 1, [0xC0 | mc, program]);

    // segments: who sounds when (NES: monophonic, the newest held voice; Nano GB: every voice)
    const mono = part.kind === 'nes';
    const segs = [];   // {v, from, to}
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

    // the bend curve: per tick of the output, the pitch of the newest sounding segment minus its MIDI key
    let maxBend = 0, conflicts = 0, notesOut = 0, bentNotes = 0;
    const keyOf = (s) => Math.max(0, Math.min(127, Math.round(s.v.base / 100)));   // the note FL wrote (its fine pitch and slides are the bend)
    for (const s of segs) {
      s.key = part.kind === 'nes' && part.nesChannel === 3 ? noiseKey(pitchAt(s.v, s.from)) : keyOf(s);
      if (s.v.glides.length) for (let t = s.from; t <= s.to; t++) maxBend = Math.max(maxBend, Math.abs(pitchAt(s.v, Math.min(t, s.to)) - s.key * 100));
      else maxBend = Math.max(maxBend, Math.abs(pitchAt(s.v, s.from) - s.key * 100));
    }
    if (part.kind === 'nes' && part.nesChannel === 3) maxBend = 0;
    const range = Math.max(2, Math.min(127, Math.ceil(maxBend / 100 - 1e-9)));
    for (const bytes of [[101, 0], [100, 0], [6, range], [38, 0], [101, 127], [100, 127]]) ev(trk, 0, 1, [0xB0 | mc, ...bytes]);   // RPN 0: the bend range
    const bendVal = (cents) => Math.max(0, Math.min(16383, Math.round(8192 + cents / (range * 100) * 8192)));
    let lastBend = 8192;
    const bend = (tick, cents) => { const b = bendVal(cents); if (b !== lastBend) { ev(trk, tick, 2, [0xE0 | mc, b & 127, b >> 7]); lastBend = b; } };
    ev(trk, 0, 1, [0xE0 | mc, 0, 64]);

    // the owner of the bend at a tick: the newest segment sounding then
    const owners = (t) => { let o = null; for (const s of segs) if (s.from <= t && t < s.to && (!o || s.from > o.from || s.from === o.from && s.v.order > o.v.order)) o = s; return o; };
    for (const s of segs) {
      const vel = levelOf(s.v.velocity);
      if (vel <= 0) continue;
      notesOut++;
      if (s.v.glides.length) bentNotes++;
      ev(trk, s.from * scale, 3, [0x90 | mc, s.key, vel]);
      ev(trk, s.to * scale, 0, [0x80 | mc, s.key, 0]);
    }
    // bends: at every change of owner, and through every glide
    const marks = new Set();
    for (const s of segs) { marks.add(s.from); for (const g of s.v.glides) for (let t = g.start * scale; t <= (g.start + g.len) * scale; t += bendStep) marks.add(t / scale); }
    for (const t of [...marks].sort((a, b) => a - b)) {
      const o = owners(t);
      if (!o) continue;
      const inGlide = o.v.glides.some(g => t >= g.start && t < g.start + g.len);
      const cents = part.kind === 'nes' && part.nesChannel === 3 ? 0 : pitchAt(o.v, inGlide ? Math.min(o.to, t + lead) : t) - o.key * 100;
      if (!mono) for (const s of segs) if (s !== o && s.from <= t && t < s.to && Math.abs((pitchAt(s.v, t) - s.key * 100) - cents) > 1) { conflicts++; break; }
      bend(Math.round(t * scale), cents);
    }
    if (conflicts) warnings.push(`channel ${part.rack} "${part.name}": ${conflicts} moments where two notes sounding together had different bends (MIDI bends a whole channel): the newest note's bend is used`);
    summary.push({ midiChannel: mc + 1, rack: part.rack, name: tname, kind: kindLabel, preset, program, volume, pan, notes: notesOut, slides: slides.length, bentNotes, bendRange: range, flNotes: voices.length });
  }
  const lastTick = arr.songEnd * scale;
  tempoTrack.push({ tick: lastTick, rank: 9, seq: seq++, bytes: [0xFF, 0x01, 3, 0x65, 0x6E, 0x64] });   // a text "end" so the song lasts as long as the playlist
  return { midi: writeMidi(tracks, ppq), sidecar, summary, warnings, info, ppq, tempo: proj.tempo, songEnd: arr.songEnd, flPpq: proj.ppq, fallback: arr.fallback, seconds: arr.songEnd / proj.ppq * 60 / proj.tempo };
}

// the NES mixer (nesdev "APU Mixer"): pulse = 95.88 / (8128 / n + 100), triangle/noise/DMC = 159.79 / (1 / (t / 8227 + n / 12241) + 100)
const NES_TRI_FULL = 159.79 / (1 / (15 / 8227) + 100);
export const NES_BALANCE = { pulse: 0.8, noise: 1 };   // trims on the formula: pulse 0.8 (-2 dB; level 12 -> 6) measured on overworld.wav (FL's pulses against its triangle); noise not measured
export function nesPulseGb(level) { return Math.max(1, Math.min(15, roundEven(15 * NES_BALANCE.pulse * (95.88 / (8128 / level + 100)) / NES_TRI_FULL))); }
export function nesNoiseGb(level) { return Math.max(1, Math.min(15, roundEven(15 * NES_BALANCE.noise * (159.79 / (1 / (level / 12241) + 100)) / NES_TRI_FULL))); }

function roundEven(x) { const f = Math.floor(x), d = x - f; return d < 0.5 ? f : d > 0.5 ? f + 1 : (f % 2 === 0 ? f : f + 1); }

// the MIDI key whose Game Boy noise clock (Nano GB: nearest to 16 x the note's frequency) is the NES noise period FL's pitch picks
function noiseKey(cents) {
  const semis = cents / 100;
  const idx = Math.max(0, Math.min(15, 15 - Math.floor((semis - 24) / 3)));
  const rate = NES_CPU / NES_NOISE_PERIODS[idx];
  return Math.max(0, Math.min(127, Math.round(69 + 12 * Math.log2(rate / 16 / 440))));
}
