// ---- engine: notes -> Game Boy sound registers -> samples -------------------------------------------------------------------------
//
// The instrument layer, and the ONE definition of how Nano GB plays notes: the BrokenNes2 plugin's Nano GB chip runs a line-for-line C#
// twin of this file (Windows/NesEmulator/gb/NanoGbEngine.cs), and Nano/gb/test/parity.mjs holds the two to the same register writes at the
// same samples. Change one, change both.
//  * pitch is cents, turned into the chip's 11-bit frequency register for the true rate gnuboy makes (2^21 / RATE, slightly above the
//    requested one): pulse d = 131072 k / f, wave d = 65536 k / f, k = rate x RATE / 2^21, rounded to even;
//  * volume is the 4-bit NRx2 level, round-to-even of 15 x velocity x gain x envelope (the wave channel has only off, 25, 50, 100 %);
//    a preset with env uses the chip's own envelope instead;
//  * every control tick (64 samples) each channel's registers are rewritten from the voice that owns it, only the ones that changed;
//    a trigger (NRx4 bit 7) only for a new owner; a silenced channel is switched off through its length counter (it then costs nothing);
//  * a channel is monophonic with last-note priority: the newest voice owns it and older ones wait under it.
// The arithmetic is plain doubles, no Math.pow / log / sin (exp2 below is a polynomial), so C# computes the same bits.

const CHUNK = 64;
const KIND = { pulse: 0, wave: 1, noise: 2 };
const KIND_NAMES = ['pulse', 'wave', 'noise'];
const KIND_CHANNELS = [[0, 1], [2], [3]];
const MAX_STACK = 8;
// per channel: NRx1, NRx2 (NR32 for the wave), NRx3, NRx4
const REG1 = [0x11, 0x16, 0x1B, 0x20], REG2 = [0x12, 0x17, 0x1C, 0x21], REG3 = [0x13, 0x18, 0x1D, 0x22], REG4 = [0x14, 0x19, 0x1E, 0x23];

// C# Math.Round(double): halves to even
function roundEven(x) {
  const f = Math.floor(x), d = x - f;
  if (d < 0.5) return f;
  if (d > 0.5) return f + 1;
  return (f % 2 === 0) ? f : f + 1;
}
function clamp(x, lo, hi) { return x < lo ? lo : x > hi ? hi : x; }
// 2^x with + and x only (the same bits in any language): 2^i exactly, 2^f by a degree-12 Taylor series of e^(f ln 2), f in [0, 1)
function exp2(x) {
  let i = Math.floor(x);
  const f = (x - i) * 0.6931471805599453;
  let t = 1, s = 1;
  for (let k = 1; k <= 12; k++) { t = t * f / k; s = s + t; }
  while (i > 0) { s = s * 2; i--; }
  while (i < 0) { s = s * 0.5; i++; }
  return s;
}
function midiHz(cents) { return 440 * exp2((cents - 6900) / 1200); }

// Fills in every field of a preset. The plugin's table is these normalized objects as JSON (build.mjs), so a field added here needs the C# too.
function normalizePreset(p, name) {
  if (!p || typeof p !== 'object') throw new TypeError('a preset is an object');
  const kind = KIND[p.kind];
  if (kind === undefined) throw new RangeError(`preset kind must be 'pulse', 'wave' or 'noise', not ${p.kind}`);
  const num = (v, dflt, lo, hi, what) => {
    if (v === undefined || v === null) return dflt;
    if (typeof v !== 'number' || !isFinite(v) || v < lo || v > hi) throw new RangeError(`preset ${what} must be a number from ${lo} to ${hi}`);
    return v;
  };
  const int = (v, dflt, lo, hi, what) => { const x = num(v, dflt, lo, hi, what); if (!Number.isInteger(x)) throw new RangeError(`preset ${what} must be a whole number`); return x; };
  const out = {
    name: name ?? p.name ?? 'custom', label: p.label ?? name ?? 'custom', desc: p.desc ?? '', kind,
    note: num(p.note, 60, 0, 127, 'note'),
    duty: int(p.duty, 2, 0, 3, 'duty'), dutySteps: null, dutyRate: 0,
    gain: num(p.gain, 1, 0, 1, 'gain'),
    a: num(p.a, 0, 0, 60000, 'a'), d: num(p.d, 0, 0, 60000, 'd'), s: num(p.s, 1, 0, 1, 's'), r: num(p.r, 0, 0, 60000, 'r'),
    oneShot: !!p.oneShot, hwEnv: -1,
    vibRate: 0, vibDepth: 0, vibDelay: 0, vibFade: 0,
    slideFrom: 0, slideTo: 0, slideMs: 0,
    arpSteps: null, arpRate: 0, arpOnce: false,
    noiseShift: -1, noiseTo: 0, noiseMs: 0, noiseRatio: 0, noiseShort: false,
    sweep: -1, wave: null, waveKey: '', pan: 0,
  };
  if (out.oneShot && out.s !== 0) throw new RangeError('a oneShot preset needs s = 0 (it ends by itself after a + d)');
  if (p.dutySeq) {
    const st = p.dutySeq.steps;
    if (kind !== 0) throw new RangeError('only a pulse preset has a duty');
    if (!Array.isArray(st) || st.length === 0 || st.some(x => !Number.isInteger(x) || x < 0 || x > 3)) throw new RangeError('dutySeq.steps must be a list of 0..3');
    out.dutySteps = st.slice(); out.dutyRate = num(p.dutySeq.rate, 8, 0.1, 1000, 'dutySeq.rate');
  }
  if (p.env) {
    if (kind === 1) throw new RangeError('the wave channel has no hardware envelope');
    out.hwEnv = (p.env.up ? 8 : 0) | int(p.env.step, 1, 1, 7, 'env.step');
  }
  if (p.vib) {
    out.vibRate = num(p.vib.rate, 5, 0, 50, 'vib.rate'); out.vibDepth = num(p.vib.depth, 20, 0, 2400, 'vib.depth');
    out.vibDelay = num(p.vib.delay, 0, 0, 60000, 'vib.delay'); out.vibFade = num(p.vib.fade, 0, 0, 60000, 'vib.fade');
  }
  if (p.slide) {
    out.slideFrom = num(p.slide.from, 0, -9600, 9600, 'slide.from'); out.slideTo = num(p.slide.to, 0, -9600, 9600, 'slide.to');
    out.slideMs = num(p.slide.ms, 100, 0, 60000, 'slide.ms');
  }
  if (p.arp) {
    const st = p.arp.steps;
    if (!Array.isArray(st) || st.length === 0 || st.some(x => typeof x !== 'number' || !isFinite(x) || x < -96 || x > 96)) throw new RangeError('arp.steps must be a list of semitone offsets');
    out.arpSteps = st.slice(); out.arpRate = num(p.arp.rate, 30, 0.1, 1000, 'arp.rate'); out.arpOnce = !!p.arp.once;
  }
  if (p.noise) {
    if (kind !== 2) throw new RangeError('only a noise preset has noise settings');
    out.noiseShift = num(p.noise.shift, 0, 0, 13, 'noise.shift'); out.noiseTo = num(p.noise.to, out.noiseShift, 0, 13, 'noise.to');
    out.noiseMs = num(p.noise.ms, 100, 0, 60000, 'noise.ms'); out.noiseRatio = int(p.noise.ratio, 0, 0, 7, 'noise.ratio'); out.noiseShort = !!p.noise.short;
  }
  if (p.sweep) {
    if (kind !== 0) throw new RangeError('only a pulse preset can use the sweep unit');
    out.sweep = (int(p.sweep.period, 1, 1, 7, 'sweep.period') << 4) | (p.sweep.down ? 8 : 0) | int(p.sweep.shift, 1, 0, 7, 'sweep.shift');
  }
  if (kind === 1) {
    const w = p.wave ?? 'triangle';
    const arr = typeof w === 'string' ? WAVES[w] : w;
    if (!arr) throw new RangeError(`unknown wave '${w}'`);
    if (!(Array.isArray(arr) || ArrayBuffer.isView(arr)) || arr.length !== 32 || Array.prototype.some.call(arr, x => !Number.isInteger(x) || x < 0 || x > 15)) throw new RangeError('a wave is 32 whole numbers from 0 to 15');
    out.wave = Array.from(arr); out.waveKey = out.wave.join(',');
  } else if (p.wave !== undefined) throw new RangeError('only a wave preset has a wave');
  if (p.pan !== undefined) out.pan = panValue(p.pan);
  return out;
}

function panValue(v) {
  if (v === 'L' || v === 'l' || v === 'left') return -1;
  if (v === 'R' || v === 'r' || v === 'right') return 1;
  if (v === 'C' || v === 'c' || v === 'center' || v === 'centre') return 0;
  if (v === -1 || v === 0 || v === 1) return v;
  throw new RangeError('pan is -1 (left), 0 (both) or 1 (right), or \'L\', \'C\', \'R\'');
}

class Voice {
  constructor(id, preset, note, vel, start, channel) {
    this.id = id; this.p = preset; this.note = note; this.vel = vel; this.start = start; this.channel = channel;
    this.fresh = true; this.released = false; this.releasedAt = 0; this.relLevel = 0; this.dead = false;
    this.bend = 0; this.duty = -1; this.pan = 2; this.trigLevel = -1;
  }
}

class Engine {
  // opts: {volume (post-chip gain; 1 = the chip's own level), presets (extra {name: preset}), trace, meter}
  constructor(sampleRate, opts = {}) {
    this.chip = new Chip(sampleRate);
    this.sampleRate = sampleRate;
    this.k = sampleRate * this.chip.rate / 2097152;   // how much faster than asked gnuboy runs at this rate (1.0118 at 44.1 kHz)
    this.volume = opts.volume ?? 1;
    this.frame = 0; this.nextTick = 0;
    this.events = []; this.seq = 0;
    this.stacks = [[], [], [], []];
    this.gate = [false, false, false, false];
    this.curLevel = [0, 0, 0, 0]; this.curNote = [-1, -1, -1, -1];
    this.lastVoice = [null, null, null, null];
    this.loadedWave = '';
    this.chanPan = [0, 0, 0, 0];
    this.pitchBend = 0; this.tune = 0;
    this.presets = {};
    for (const k of Object.keys(PRESETS)) this.presets[k] = normalizePreset(PRESETS[k], k);
    if (opts.presets) for (const k of Object.keys(opts.presets)) this.presets[k] = normalizePreset(opts.presets[k], k);
    this.voices = new Map();
    this.trace = opts.trace ? [] : null;
    this.meter = !!opts.meter; this.peak = 0;
    // the 112 noise clocks (NR43 >> 4 = shift 0..13, & 7 = ratio) by the rate the LFSR steps at, for notes on a noise preset
    this.noiseCode_ = new Int32Array(112); this.noiseFreq = new Int32Array(112);
    for (let sh = 0, n = 0; sh < 14; sh++) for (let ra = 0; ra < 8; ra++, n++) {
      let f = (FREQ_TAB[ra] >> sh) * this.chip.rate; if ((f >> 18) !== 0) f = 1 << 18;
      this.noiseCode_[n] = (sh << 4) | ra; this.noiseFreq[n] = f;
    }
    this.wf(0x25, 0xFF);   // every channel to both sides (gnuboy powers on with 0xF3)
  }

  // ---- register access ----
  w(reg, value) { if (this.chip.hi[reg] !== value) this.wf(reg, value); }
  wf(reg, value) { this.chip.write(reg, value); if (this.trace) this.trace.push([0, reg, value]); }

  // ---- events ----
  schedule(frame, ev) {
    ev.frame = frame; ev.seq = this.seq++;
    const evs = this.events;
    let i = evs.length;
    while (i > 0 && evs[i - 1].frame > frame) i--;
    evs.splice(i, 0, ev);
  }
  noteOn(frame, id, note, vel, preset, o = {}) { this.schedule(frame, { t: 'on', id, note, vel, preset, o }); }
  noteOff(frame, ref) { this.schedule(frame, { t: 'off', ref }); }
  bendVoice(frame, ref, cents) { this.schedule(frame, { t: 'bend', ref, cents }); }
  allOff(frame) { this.schedule(frame, { t: 'alloff' }); }
  kill(frame) { this.schedule(frame, { t: 'kill' }); }
  setParam(frame, name, value) { this.schedule(frame, { t: 'param', name, value }); }
  addPreset(name, def) { this.presets[name] = normalizePreset(def, name); }
  resolvePreset(p) {
    if (typeof p === 'string') { const r = this.presets[p]; if (!r) throw new RangeError(`unknown preset '${p}'`); return r; }
    return normalizePreset(p);
  }

  // a voice by id, or {note}: the newest held voice with that note (channels in order, then each stack bottom to top)
  findVoice(ref) {
    if (typeof ref === 'number') return this.voices.get(ref) ?? null;
    let best = null;
    for (let c = 0; c < 4; c++) for (const v of this.stacks[c]) if (!v.dead && v.note === ref.note && !v.released && (!best || v.start >= best.start)) best = v;
    return best;
  }

  apply(ev) {
    switch (ev.t) {
      case 'on': this.doNoteOn(ev); break;
      case 'off': { const v = this.findVoice(ev.ref); if (v) this.release(v); break; }
      case 'bend': { const v = this.findVoice(ev.ref); if (v) v.bend = ev.cents; break; }
      case 'alloff': for (let c = 0; c < 4; c++) for (const v of this.stacks[c].slice()) this.release(v); break;
      case 'kill':
        for (let c = 0; c < 4; c++) { for (const v of this.stacks[c]) { v.dead = true; this.voices.delete(v.id); } this.stacks[c].length = 0; }
        for (let c = 0; c < 4; c++) this.applyChannel(c);
        break;
      case 'param':
        if (ev.name === 'volume') this.volume = ev.value;
        else if (ev.name === 'pitchBend') this.pitchBend = ev.value;
        else if (ev.name === 'tune') this.tune = ev.value;
        else if (ev.name === 'pan') { for (let c = 0; c < 4; c++) this.chanPan[c] = ev.value[c]; }
        break;
    }
  }

  doNoteOn(ev) {
    const p = ev.preset, o = ev.o;
    const ch = this.pickChannel(p, o.channel);
    const v = new Voice(ev.id, p, ev.note, ev.vel, this.frame, ch);
    if (o.duty !== undefined && o.duty !== null) v.duty = o.duty | 0;
    if (o.pan !== undefined && o.pan !== null) v.pan = o.pan;
    if (o.bend) v.bend = o.bend;
    const st = this.stacks[ch];
    st.push(v);
    if (st.length > MAX_STACK) { const old = st.shift(); old.dead = true; this.voices.delete(old.id); }
    const prev = this.voices.get(v.id); if (prev) { prev.dead = true; }
    this.voices.set(v.id, v);
    this.applyChannel(ch);
    if (o.duration > 0) this.schedule(this.frame + Math.round(o.duration * this.sampleRate), { t: 'off', ref: v.id });
  }

  // a free channel of the kind; else the one whose voice is releasing longest; else the one playing longest (that voice waits under the new one)
  pickChannel(p, pin) {
    let cand = p.sweep >= 0 ? [0] : KIND_CHANNELS[p.kind];
    if (pin !== undefined && pin !== null) {
      if (!cand.includes(pin)) throw new RangeError(`a ${KIND_NAMES[p.kind]}${p.sweep >= 0 ? ' sweep' : ''} preset cannot play on channel ${pin}`);
      cand = [pin];
    }
    for (const c of cand) this.prune(c);
    for (const c of cand) if (this.stacks[c].length === 0) return c;
    let best = -1, bestAt = Infinity;
    for (const c of cand) { const top = this.stacks[c][this.stacks[c].length - 1]; if (top.released && top.releasedAt < bestAt) { best = c; bestAt = top.releasedAt; } }
    if (best >= 0) return best;
    best = cand[0]; bestAt = Infinity;
    for (const c of cand) { const top = this.stacks[c][this.stacks[c].length - 1]; if (top.start < bestAt) { best = c; bestAt = top.start; } }
    return best;
  }

  prune(c) {
    const st = this.stacks[c];
    for (let i = st.length - 1; i >= 0; i--) if (st[i].dead) { if (this.voices.get(st[i].id) === st[i]) this.voices.delete(st[i].id); st.splice(i, 1); }
  }

  tms(v, frame) { return (frame - v.start) * 1000 / this.sampleRate; }

  env(v, frame) {
    const p = v.p;
    if (v.released) {
      if (p.r <= 0) return 0;
      const rt = (frame - v.releasedAt) * 1000 / this.sampleRate;
      return v.relLevel * Math.max(0, 1 - rt / p.r);
    }
    const t = this.tms(v, frame);
    if (t < p.a) return t / p.a;
    const td = t - p.a;
    if (p.d > 0 && td < p.d) return 1 - (1 - p.s) * td / p.d;
    return p.s;
  }

  release(v) {
    if (v.released || v.dead || v.p.oneShot) return;
    v.relLevel = this.env(v, this.frame);
    v.released = true; v.releasedAt = this.frame;
    if (v.p.r <= 0 || v.p.hwEnv >= 0) { v.dead = true; if (this.voices.get(v.id) === v) this.voices.delete(v.id); }
    this.applyChannel(v.channel);
  }

  finished(v, frame) {
    const p = v.p;
    if (v.dead) return true;
    if (v.released) return p.r <= 0 || (frame - v.releasedAt) * 1000 / this.sampleRate >= p.r;
    return p.s === 0 && this.tms(v, frame) >= p.a + p.d;
  }

  voiceCents(v, t) {
    const p = v.p;
    let c = v.note * 100 + v.bend + this.pitchBend + this.tune;
    if (p.slideMs > 0 || p.slideFrom !== 0 || p.slideTo !== 0) {
      const f = p.slideMs > 0 ? Math.min(1, t / p.slideMs) : 1;
      c += p.slideFrom + (p.slideTo - p.slideFrom) * f;
    }
    if (p.arpSteps) {
      let i = Math.floor(t / 1000 * p.arpRate);
      i = p.arpOnce ? Math.min(i, p.arpSteps.length - 1) : i % p.arpSteps.length;
      c += p.arpSteps[i] * 100;
    }
    if (p.vibDepth > 0 && p.vibRate > 0) {
      const vt = t - p.vibDelay;
      if (vt > 0) {
        const amp = p.vibDepth * (p.vibFade > 0 ? Math.min(1, vt / p.vibFade) : 1);
        let ph = vt / 1000 * p.vibRate;
        ph -= Math.floor(ph);
        c += amp * (ph < 0.25 ? 4 * ph : ph < 0.75 ? 2 - 4 * ph : 4 * ph - 4);
      }
    }
    return c;
  }

  // the channel's registers from the voice that owns it
  applyChannel(ch) {
    this.prune(ch);
    const st = this.stacks[ch];
    const v = st.length ? st[st.length - 1] : null;
    if (!v) {
      if (this.gate[ch]) { this.silence(ch); this.gate[ch] = false; this.curLevel[ch] = 0; this.curNote[ch] = -1; }
      this.lastVoice[ch] = null;
      return;
    }
    const p = v.p, t = this.tms(v, this.frame);
    let trig = v.fresh || !this.gate[ch] || this.lastVoice[ch] !== v;
    v.fresh = false; this.lastVoice[ch] = v;
    let level;
    if (p.hwEnv >= 0) { if (trig) v.trigLevel = clamp(roundEven(15 * v.vel * p.gain), 0, 15); level = v.trigLevel; }
    else level = clamp(roundEven(15 * (v.vel * p.gain * this.env(v, this.frame))), 0, 15);
    const pan = v.pan !== 2 ? v.pan : p.pan !== 0 ? p.pan : this.chanPan[ch];
    const r1 = REG1[ch], r2 = REG2[ch], r3 = REG3[ch], r4 = REG4[ch];
    if (ch === 3) {
      let code;
      if (p.noiseShift >= 0) {
        const f = p.noiseMs > 0 ? Math.min(1, t / p.noiseMs) : 1;
        code = (clamp(Math.floor(p.noiseShift + (p.noiseTo - p.noiseShift) * f), 0, 13) << 4) | p.noiseRatio;
      } else code = this.noiseCode(this.voiceCents(v, t));
      this.w(r1, 0);
      this.w(r2, p.hwEnv >= 0 ? (level << 4) | p.hwEnv : level << 4);
      this.w(r3, code | (p.noiseShort ? 8 : 0));
      if (trig) this.wf(r4, 0x80);
    } else {
      const cents = p.sweep >= 0 ? v.note * 100 : this.voiceCents(v, t);
      const f = midiHz(cents);
      let d = roundEven((ch === 2 ? 65536 : 131072) * this.k / f);
      d = clamp(d, 1, 2048);
      const x = 2048 - d, lo = x & 0xFF, hi = x >> 8;
      if (ch === 2) {
        if (trig || this.loadedWave !== p.waveKey) {
          if (this.loadedWave !== p.waveKey) {
            this.wf(0x1A, 0);
            for (let i = 0; i < 16; i++) this.wf(0x30 + i, (p.wave[2 * i] << 4) | p.wave[2 * i + 1]);
            this.loadedWave = p.waveKey;
          }
          trig = true;
          this.w(0x1A, 0x80);
        }
        this.w(r1, 0);
        this.w(r2, (level === 0 ? 0 : level < 6 ? 3 : level < 12 ? 2 : 1) << 5);
      } else {
        const duty = p.dutySteps ? p.dutySteps[Math.floor(t / 1000 * p.dutyRate) % p.dutySteps.length] : (v.duty >= 0 ? v.duty : p.duty);
        this.w(r1, duty << 6);
        this.w(r2, p.hwEnv >= 0 ? (level << 4) | p.hwEnv : level << 4);
        if (ch === 0) this.w(0x10, p.sweep >= 0 ? p.sweep : 0);
      }
      if (trig) { this.w(r3, lo); this.wf(r4, 0x80 | hi); }
      else if (p.sweep < 0) { this.w(r3, lo); if ((this.chip.hi[r4] & 0x47) !== hi) this.wf(r4, hi); }
    }
    const bits = pan < 0 ? 0x10 << ch : pan > 0 ? 1 << ch : 0x11 << ch;
    this.w(0x25, (this.chip.hi[0x25] & ~(0x11 << ch)) | bits);
    this.gate[ch] = true; this.curLevel[ch] = level; this.curNote[ch] = v.note;
  }

  // the noise clock nearest (by ratio) to 16 x the note's frequency in LFSR steps a second
  noiseCode(cents) {
    const target = 16 * midiHz(cents) * 131072 / this.sampleRate;
    let best = 0, bq = Infinity;
    for (let n = 0; n < 112; n++) { const f = this.noiseFreq[n]; if (f <= 0) continue; const q = f > target ? f / target : target / f; if (q < bq) { bq = q; best = this.noiseCode_[n]; } }
    return best;
  }

  // the channel's level to 0 and the channel off through its length counter (about 4 ms later: it costs nothing after that)
  silence(ch) {
    if (ch === 2) { this.w(0x1A, 0); return; }
    this.w(REG2[ch], 0);
    this.w(REG1[ch], (this.chip.hi[REG1[ch]] & 0xC0) | 63);
    this.wf(REG4[ch], 0x40 | (this.chip.hi[REG4[ch]] & 7));
  }

  tick() {
    for (let c = 0; c < 4; c++) {
      const st = this.stacks[c];
      for (let i = st.length - 1; i >= 0; i--) { const v = st[i]; if (!v.dead && this.finished(v, this.frame)) { v.dead = true; if (this.voices.get(v.id) === v) this.voices.delete(v.id); } }
      this.applyChannel(c);
    }
  }

  // Fills L[off .. off+n) and R (null: mono into L)
  render(L, R, off, n) {
    const end = off + n;
    let o = off;
    while (o < end) {
      while (this.events.length && this.events[0].frame <= this.frame) this.apply(this.events.shift());
      if (this.frame >= this.nextTick) { this.tick(); this.nextTick = this.frame - (this.frame % CHUNK) + CHUNK; }
      let seg = Math.min(end - o, this.nextTick - this.frame);
      if (this.events.length) seg = Math.min(seg, Math.max(1, this.events[0].frame - this.frame));
      if (this.trace) { const last = this.trace[this.trace.length - 1]; if (last && last[0] === 1) last[1] += seg; else this.trace.push([1, seg]); }
      this.chip.render(L, R, o, seg);
      o += seg; this.frame += seg;
    }
    if (this.volume !== 1) {
      const g = this.volume;
      for (let i = off; i < end; i++) L[i] = L[i] * g;
      if (R) for (let i = off; i < end; i++) R[i] = R[i] * g;
    }
    if (this.meter) {
      let pk = this.peak;
      for (let i = off; i < end; i++) { const a = L[i] < 0 ? -L[i] : L[i]; if (a > pk) pk = a; }
      if (R) for (let i = off; i < end; i++) { const a = R[i] < 0 ? -R[i] : R[i]; if (a > pk) pk = a; }
      this.peak = pk;
    }
  }

  activity() {
    const a = [];
    for (let c = 0; c < 4; c++) a.push({ active: this.gate[c], level: this.curLevel[c], note: this.curNote[c], voices: this.stacks[c].length });
    return a;
  }
}
