// ---- song: Standard MIDI Files, played on the engine -----------------------------------------------------------------------------
//
// parseMidi reads a .mid (format 0 or 1) into one time-ordered list of channel events with times in seconds (the tempo map applied).
// SongPlayer plays it on Engines, sample for sample, inside the audio worklet (or offline): by default one Engine (one Game Boy chip) per
// MIDI channel, the way FL plays one BrokenNes2 instance per channel, so a song sounds as it did in FL; {chips: 'shared'} puts every channel
// on one chip (four voices in all, the leanest). The C# side of the comparison (tools/NanoGbParity 'midi') follows this file.
//
// What a channel plays: program change n = preset n (PRESETS order; the plugin's Instrument numbers are the same), channel 10 = General MIDI
// drums (GM_DRUMS), a sidecar (the plugin's Nano GB export) overrides both. Pitch bend: +-2 semitones, or what RPN 0 sets. Velocity = level.
// CC 7 / 10 are ignored (FL writes them, the plugin does not hear them): the sidecar's volume and pan are what the channel had in FL.

function parseMidi(data) {
  const b = data instanceof Uint8Array ? data : new Uint8Array(data instanceof ArrayBuffer ? data : data.buffer ?? data);
  let p = 0;
  const u32 = () => { const v = (b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]) >>> 0; p += 4; return v; };
  const u16 = () => { const v = b[p] << 8 | b[p + 1]; p += 2; return v; };
  const str = () => String.fromCharCode(b[p], b[p + 1], b[p + 2], b[p + 3]);
  if (b.length < 14 || str() !== 'MThd') throw new TypeError('not a Standard MIDI File (no MThd)');
  p += 4; const hlen = u32(), format = u16(), ntracks = u16(), division = u16(); p = 8 + hlen;
  if (format > 1) throw new RangeError('MIDI format 2 is not supported (one song per file: format 0 or 1)');
  if (division & 0x8000) throw new RangeError('SMPTE time division is not supported');
  const raw = [];
  for (let t = 0; t < ntracks; t++) {
    if (p + 8 > b.length) throw new RangeError('MIDI file is cut short');
    if (str() !== 'MTrk') { p += 4; const l = u32(); p += l; t--; if (p >= b.length) break; continue; }
    p += 4; const len = u32(), end = p + len;
    if (end > b.length) throw new RangeError('MIDI file is cut short');
    let tick = 0, status = 0, order = 0;
    while (p < end) {
      let d = 0, c; do { c = b[p++]; d = (d << 7) | (c & 127); } while (c & 128 && p < end);
      tick += d;
      let s = b[p];
      if (s & 0x80) p++; else s = status;
      if (s === 0xFF) {
        const type = b[p++]; let l = 0; do { c = b[p++]; l = (l << 7) | (c & 127); } while (c & 128);
        if (type === 0x51 && l === 3) raw.push({ tick, tr: t, n: order++, type: 'tempo', us: b[p] << 16 | b[p + 1] << 8 | b[p + 2] });
        p += l; continue;
      }
      if (s === 0xF0 || s === 0xF7) { let l = 0; do { c = b[p++]; l = (l << 7) | (c & 127); } while (c & 128); p += l; continue; }
      if (!(s & 0x80)) throw new RangeError('MIDI running status without a status byte');
      status = s;
      const hiN = s & 0xF0, ch = s & 15, a = b[p++], two = hiN !== 0xC0 && hiN !== 0xD0, v = two ? b[p++] : 0;
      raw.push({ tick, tr: t, n: order++, type: hiN, ch, a, b: v });
    }
    p = end;
  }
  raw.sort((x, y) => x.tick - y.tick || x.tr - y.tr || x.n - y.n);
  // ticks -> seconds through the tempo map
  let us = 500000, lastTick = 0, sec = 0;
  const events = [];
  for (const e of raw) {
    sec += (e.tick - lastTick) * us / 1e6 / division; lastTick = e.tick;
    if (e.type === 'tempo') { us = e.us; continue; }
    if (e.type === 0x90 || e.type === 0x80 || e.type === 0xB0 || e.type === 0xC0 || e.type === 0xE0) events.push({ sec, type: e.type === 0x90 && e.b === 0 ? 0x80 : e.type, ch: e.ch, a: e.a, b: e.b });
  }
  const channels = [...new Set(events.filter(e => e.type === 0x90).map(e => e.ch))].sort((x, y) => x - y);
  return { format, ppq: division, duration: sec, events, channels };
}

const PRESET_NAMES = Object.keys(PRESETS);

class SongPlayer {
  // song: parseMidi's result. opts: {chips: 'channel' | 'shared', sidecar, presets (extra), volume, loop, loopStart, loopEnd (seconds)}
  constructor(sampleRate, song, opts = {}) {
    this.sampleRate = sampleRate;
    this.song = song;
    this.shared = opts.chips === 'shared';
    if (opts.chips !== undefined && opts.chips !== 'shared' && opts.chips !== 'channel') throw new RangeError("chips is 'channel' (one chip per MIDI channel, as in FL) or 'shared'");
    this.volume = opts.volume ?? 1;
    this.loop = !!opts.loop;
    this.loopStart = opts.loopStart ?? 0;
    this.loopEnd = opts.loopEnd ?? song.duration;
    this.tempo = 1;
    this.frames = song.events.map(e => Math.round(e.sec * sampleRate));
    this.endFrame = Math.round(song.duration * sampleRate);
    const side = opts.sidecar ? normalizeSidecar(opts.sidecar) : null;
    this.side = side;
    const extra = Object.assign({}, side ? side.presets : null, opts.presets || null);
    this.parts = [];          // [{ch (or -1 shared), engine}]
    this.partOf = new Int8Array(16).fill(-1);
    const mk = (ch) => {
      const e = new Engine(sampleRate, { presets: extra });
      const cs = side && side.channels[ch];
      if (cs && cs.pan !== undefined) e.chanPan = [cs.pan, cs.pan, cs.pan, cs.pan];
      return e;
    };
    if (this.shared) { this.parts.push({ ch: -1, engine: mk(-1) }); this.partOf.fill(0); }
    else for (const ch of song.channels) { this.partOf[ch] = this.parts.length; this.parts.push({ ch, engine: mk(ch) }); }
    this.scratchL = new Float32Array(128); this.scratchR = new Float32Array(128);
    this.reset();
    this.playing = false;
    this.ended = false;
  }

  // channel state at the song's start
  reset() {
    this.pos = 0; this.next = 0; this.id = 1;
    this.program = new Int32Array(16); this.bendRange = new Float64Array(16).fill(2); this.rpn = new Int32Array(16).fill(0x3FFF);
    this.held = new Map();
  }

  presetFor(ch, note) {
    const cs = this.side && this.side.channels[ch];
    if (cs && cs.preset) return { name: cs.preset, note, gain: cs.volume ?? 1, duty: cs.duty };
    if (ch === 9) { const n = GM_DRUMS[note]; return n ? { name: n, note: PRESETS[n].note, gain: 1 } : null; }
    return { name: PRESET_NAMES[this.program[ch] % PRESET_NAMES.length], note, gain: 1 };
  }

  // one MIDI event at engine-relative frame `at` (0 = the start of the block being made)
  dispatch(e, at) {
    const pi = this.partOf[e.ch];
    if (pi < 0 && e.type === 0x90) return;
    const eng = pi >= 0 ? this.parts[pi].engine : null, f = eng ? eng.frame + at : 0;
    const key = e.ch * 128 + e.a;
    switch (e.type) {
      case 0x90: {
        const old = this.held.get(key);
        if (old) { eng.noteOff(f, old); this.held.delete(key); }
        const ps = this.presetFor(e.ch, e.a);
        if (!ps) return;
        const preset = eng.resolvePreset(ps.name), id = this.id++;
        const o = {}; if (ps.duty !== undefined && ps.duty !== null) o.duty = ps.duty;
        eng.noteOn(f, id, ps.note, Math.min(1, e.b / 127 * ps.gain), preset, o);
        this.held.set(key, id);
        break;
      }
      case 0x80: { const id = this.held.get(key); if (id !== undefined) { eng.noteOff(f, id); this.held.delete(key); } break; }
      case 0xC0: this.program[e.ch] = e.a; break;
      case 0xE0: if (eng && !this.shared) eng.setParam(f, 'pitchBend', ((e.b << 7 | e.a) - 8192) / 8192 * this.bendRange[e.ch] * 100); break;
      case 0xB0:
        if (e.a === 101) this.rpn[e.ch] = (this.rpn[e.ch] & 0x7F) | (e.b << 7);
        else if (e.a === 100) this.rpn[e.ch] = (this.rpn[e.ch] & 0x3F80) | e.b;
        else if (e.a === 6 && this.rpn[e.ch] === 0) this.bendRange[e.ch] = e.b;
        else if ((e.a === 123 || e.a === 120) && eng) { if (e.a === 120) eng.kill(f); else eng.allOff(f); }
        break;
    }
  }

  // to a time in seconds: every note stops, the channels' programs and bends are replayed up to there
  seek(sec) {
    for (const p of this.parts) p.engine.kill(p.engine.frame);
    this.reset();
    const target = Math.round(sec * this.sampleRate);
    while (this.next < this.frames.length && this.frames[this.next] < target) {
      const e = this.song.events[this.next++];
      if (e.type === 0xC0 || e.type === 0xE0 || e.type === 0xB0 && e.a !== 120 && e.a !== 123) this.dispatch(e, 0);
    }
    this.pos = target; this.ended = false;
  }

  // adds the song's next n samples into L / R (R null: mono)
  render(L, R, off, n) {
    const tempo = this.tempo;
    if (this.playing) {
      const until = this.pos + n * tempo;
      while (this.next < this.frames.length && this.frames[this.next] < until) {
        const at = Math.max(0, Math.floor((this.frames[this.next] - this.pos) / tempo));
        this.dispatch(this.song.events[this.next++], at);
      }
      this.pos = until;
      const end = this.loop ? Math.round(this.loopEnd * this.sampleRate) : this.endFrame;
      if (this.pos >= end) {
        if (this.loop && this.loopEnd > this.loopStart) this.seek(this.loopStart);
        else if (this.next >= this.frames.length) { this.playing = false; this.ended = true; for (const p of this.parts) p.engine.allOff(p.engine.frame + n); }
      }
    }
    const sl = this.scratchL, sr = R ? this.scratchR : null, g = this.volume;
    for (const p of this.parts) {
      p.engine.render(sl, sr, 0, n);
      for (let i = 0; i < n; i++) L[off + i] += sl[i] * g;
      if (R) for (let i = 0; i < n; i++) R[off + i] += sr[i] * g;
    }
  }
}

// The plugin's export (and anything written by hand): {format: 'nano-gb-sidecar', version: 1, channels: {'1': {preset, volume, duty, pan}, ...}, presets: {name: def}}
// Channels are numbered 1..16 as in FL and MIDI. Unknown keys are refused, not ignored.
function normalizeSidecar(s) {
  if (typeof s === 'string') s = JSON.parse(s);
  if (!s || typeof s !== 'object' || s.format !== 'nano-gb-sidecar') throw new TypeError("a sidecar is the JSON the plugin exports ({format: 'nano-gb-sidecar', ...})");
  if (s.version !== 1) throw new RangeError(`sidecar version ${s.version} is newer than this library (1)`);
  const out = { channels: {}, presets: {} };
  if (s.presets) for (const k of Object.keys(s.presets)) { normalizePreset(s.presets[k], k); out.presets[k] = s.presets[k]; }
  for (const k of Object.keys(s.channels || {})) {
    const n = Number(k);
    if (!Number.isInteger(n) || n < 1 || n > 16) throw new RangeError(`sidecar channel '${k}' is not 1..16`);
    const c = s.channels[k], o = {};
    for (const f of Object.keys(c)) if (!['preset', 'volume', 'duty', 'pan', 'name'].includes(f)) throw new RangeError(`sidecar channel ${k}: unknown setting '${f}'`);
    if (c.preset !== undefined) { if (!(c.preset in PRESETS) && !(c.preset in out.presets)) throw new RangeError(`sidecar channel ${k}: unknown preset '${c.preset}'`); o.preset = c.preset; }
    if (c.volume !== undefined) { if (!(c.volume >= 0 && c.volume <= 1)) throw new RangeError(`sidecar channel ${k}: volume is 0..1`); o.volume = c.volume; }
    if (c.duty !== undefined) { if (!Number.isInteger(c.duty) || c.duty < 0 || c.duty > 3) throw new RangeError(`sidecar channel ${k}: duty is 0..3`); o.duty = c.duty; }
    if (c.pan !== undefined) o.pan = panValue(c.pan);
    out.channels[n - 1] = o;
  }
  return out;
}
