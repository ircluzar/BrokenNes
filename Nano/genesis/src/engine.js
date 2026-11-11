// ---- engine: notes -> YM2612 / PSG writes -> samples ------------------------------------------------------------------------------
//
// The instrument layer of Nano Genesis (the design of Nano GB's and Nano SMS's engines, on the Genesis sound unit):
//  * ten channels: FM 1-6 (channels 0-5), PSG tones 1-3 (6-8), PSG noise (9); a channel is monophonic with last-note priority (the newest voice
//    owns it, older ones wait under it); a voice picks a free channel of its kind, else the longest-releasing, else the longest-playing;
//  * FM: the preset is a register set (algorithm, feedback, four operators, AMS / PMS), loaded when a voice takes the channel; key-on at the
//    note, key-off at its release (the chip's own envelope plays the release; the voice is kept for the preset's r ms, then the channel is
//    free). Pitch is cents -> block + 11-bit F-number at the chip's 53,267 Hz; level (velocity x gain x the software a/d/s) is extra total
//    level on the carriers (0.75 dB a step, the nearest step by geometric midpoints). A retrigger keys off, lets the chip run one sample (a
//    key edge is seen once per sample), keys on;
//  * PSG: Nano SMS's tones and noise (10-bit period, 4-bit attenuator, white / periodic noise at three rates); the Mega Drive PSG is mono;
//  * every control tick (64 samples) each channel's registers are rewritten from the voice that owns it, only what changed;
//  * pan: FM channels left / both / right (register $B4); the PSG is mono on a Mega Drive.
// The arithmetic is plain doubles, no Math.pow / log / sin (exp2 below is a polynomial; sqrt is exact in every language).

const CHUNK = 64;
const KIND = { fm: 0, tone: 1, noise: 2 };
const KIND_NAMES = ['fm', 'tone', 'noise'];
const KIND_CHANNELS = [[0, 1, 2, 3, 4, 5], [6, 7, 8], [9]];
const CHANNELS = 10;
const MAX_STACK = 8;
const SLOT = [0, 8, 4, 12];                 // operator n (algorithm order) -> its register offset in the channel's block (the chip stores 1, 3, 2, 4)
const CARRIERS = [8, 8, 8, 8, 10, 14, 14, 15];

// the PSG attenuator's 16 levels and the geometric midpoints between neighbours: a level >= ATT_MID[a] gets attenuation a
const ATT_MID = new Float64Array(15);
for (let a = 0; a < 14; a++) ATT_MID[a] = Math.sqrt(PSG_TABLE[a] * PSG_TABLE[a + 1]) / 32767;
ATT_MID[14] = PSG_TABLE[14] / 2 / 32767;

function roundEven(x) {
  const f = Math.floor(x), d = x - f;
  if (d < 0.5) return f;
  if (d > 0.5) return f + 1;
  return (f % 2 === 0) ? f : f + 1;
}
function clamp(x, lo, hi) { return x < lo ? lo : x > hi ? hi : x; }
function exp2(x) {
  let i = Math.floor(x);
  const f = (x - i) * 0.6931471805599453;
  let t = 1, s = 1;
  for (let k = 1; k <= 12; k++) { t = t * f / k; s = s + t; }
  while (i > 0) { s = s * 2; i--; }
  while (i < 0) { s = s * 0.5; i++; }
  return s;
}
// total-level steps (0.75 dB): a level >= TL_MID[k] gets k extra steps; 10^(-(k + 0.5) x 0.75 / 20) through exp2
const TL_MID = new Float64Array(127);
for (let k = 0; k < 127; k++) TL_MID[k] = exp2(-(k + 0.5) * 0.0375 * 3.321928094887362);
function midiHz(cents) { return 440 * exp2((cents - 6900) / 1200); }
function attenuation(amp) { for (let a = 0; a < 15; a++) if (amp >= ATT_MID[a]) return a; return 15; }
function tlSteps(amp) { for (let k = 0; k < 127; k++) if (amp >= TL_MID[k]) return k; return 127; }
// block (0..7) and F-number (0..2047) for a pitch: the lowest block whose F-number fits, F = hz x 2^(21 - block) / 53267
function blockFnum(hz) {
  let scale = 2097152;
  for (let b = 0; b < 8; b++) {
    const f = roundEven(hz * scale / YM_RATE);
    if (f <= 2047) return (b << 11) | (f < 1 ? 1 : f);
    scale = scale * 0.5;
  }
  return (7 << 11) | 2047;
}

const OP_FIELDS = [['mul', 1, 0, 15], ['tl', 0, 0, 127], ['dt', 0, 0, 7], ['ar', 31, 0, 31], ['d1r', 0, 0, 31], ['sl', 0, 0, 15], ['d2r', 0, 0, 31], ['rr', 10, 0, 15], ['rs', 0, 0, 3], ['am', 0, 0, 1], ['ssg', 0, 0, 15]];

function normalizePreset(p, name) {
  if (!p || typeof p !== 'object') throw new TypeError('a preset is an object');
  const kind = KIND[p.kind];
  if (kind === undefined) throw new RangeError(`preset kind must be 'fm', 'tone' or 'noise', not ${p.kind}`);
  const num = (v, dflt, lo, hi, what) => {
    if (v === undefined || v === null) return dflt;
    if (typeof v !== 'number' || !isFinite(v) || v < lo || v > hi) throw new RangeError(`preset ${what} must be a number from ${lo} to ${hi}`);
    return v;
  };
  const int = (v, dflt, lo, hi, what) => { const x = num(v, dflt, lo, hi, what); if (!Number.isInteger(x)) throw new RangeError(`preset ${what} must be a whole number`); return x; };
  const out = {
    name: name ?? p.name ?? 'custom', label: p.label ?? name ?? 'custom', desc: p.desc ?? '', kind,
    note: num(p.note, 60, 0, 127, 'note'),
    gain: num(p.gain, 1, 0, 1, 'gain'),
    a: num(p.a, 0, 0, 60000, 'a'), d: num(p.d, 0, 0, 60000, 'd'), s: num(p.s, 1, 0, 1, 's'), r: num(p.r, 0, 0, 60000, 'r'),
    oneShot: !!p.oneShot,
    vibRate: 0, vibDepth: 0, vibDelay: 0, vibFade: 0,
    slideFrom: 0, slideTo: 0, slideMs: 0,
    arpSteps: null, arpRate: 0, arpOnce: false,
    noiseRate: -1, noiseTo: 0, noiseMs: 0, noiseWhite: true,
    pan: 0,
    alg: 0, fb: 0, ams: 0, pms: 0, ops: null,
  };
  if (out.oneShot && out.s !== 0) throw new RangeError('a oneShot preset needs s = 0 (it ends by itself after a + d)');
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
    if (p.noise.rate !== undefined) { out.noiseRate = int(p.noise.rate, 0, 0, 2, 'noise.rate'); out.noiseTo = int(p.noise.to, out.noiseRate, 0, 2, 'noise.to'); out.noiseMs = num(p.noise.ms, 100, 0, 60000, 'noise.ms'); }
    out.noiseWhite = p.noise.white !== false;
  }
  if (kind === 0) {
    out.alg = int(p.alg, 0, 0, 7, 'alg'); out.fb = int(p.fb, 0, 0, 7, 'fb'); out.ams = int(p.ams, 0, 0, 3, 'ams'); out.pms = int(p.pms, 0, 0, 7, 'pms');
    if (!Array.isArray(p.ops) || p.ops.length !== 4) throw new RangeError('an fm preset has ops: four operators (algorithm order: operator 1 first)');
    out.ops = p.ops.map((o, i) => {
      if (!o || typeof o !== 'object') throw new RangeError(`fm operator ${i + 1} is an object`);
      const r = {};
      for (const [k, d, lo, hi] of OP_FIELDS) r[k] = int(o[k], d, lo, hi, `ops[${i}].${k}`);
      for (const k of Object.keys(o)) if (!OP_FIELDS.some(f => f[0] === k)) throw new RangeError(`fm operator ${i + 1}: unknown field '${k}'`);
      return r;
    });
  } else if (p.ops || p.alg !== undefined) throw new RangeError('only an fm preset has operators');
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
    this.bend = 0; this.pan = 2;
  }
}

class Engine {
  // opts: {volume (post-chip gain; 1 = the unit's own level), presets (extra {name: preset}), trace, meter}
  constructor(sampleRate, opts = {}) {
    if (!(sampleRate >= 3000 && sampleRate <= 768000)) throw new RangeError('sampleRate out of range');
    this.chip = new Chip(sampleRate);
    this.sampleRate = sampleRate;
    this.volume = opts.volume ?? 1;
    this.frame = 0; this.nextTick = 0;
    this.events = []; this.seq = 0;
    this.stacks = []; for (let c = 0; c < CHANNELS; c++) this.stacks.push([]);
    this.gate = new Array(CHANNELS).fill(false);
    this.curLevel = new Array(CHANNELS).fill(0); this.curNote = new Array(CHANNELS).fill(-1);
    this.lastVoice = new Array(CHANNELS).fill(null);
    this.regTone = new Int32Array([-1, -1, -1]); this.regAtt = new Int32Array([15, 15, 15, 15]); this.regNoise = -1;
    this.regFreq = new Int32Array(6).fill(-1); this.regPatch = new Array(6).fill(null); this.regTl = new Int32Array(24).fill(-1); this.regPan = new Int32Array(6).fill(-1);
    this.keyed = new Array(6).fill(false); this.keyAt = new Float64Array(6).fill(-1);
    this.chanPan = new Array(CHANNELS).fill(0);
    this.pitchBend = 0; this.tune = 0; this.lfo = 0;
    this.presets = {};
    for (const k of Object.keys(PRESETS)) this.presets[k] = normalizePreset(PRESETS[k], k);
    if (opts.presets) for (const k of Object.keys(opts.presets)) this.presets[k] = normalizePreset(opts.presets[k], k);
    this.voices = new Map();
    if (opts.trace) this.chip.trace = [];
    this.meter = !!opts.meter; this.peak = 0;
  }
  get trace() { return this.chip.trace; }

  // ---- register access (only what changed) ----
  setTone(i, n) { if (this.regTone[i] === n) return; this.regTone[i] = n; this.chip.psgWrite(0x80 | (i << 5) | (n & 15)); this.chip.psgWrite((n >> 4) & 0x3F); }
  setAtt(i, a) { if (this.regAtt[i] === a) return; this.regAtt[i] = a; this.chip.psgWrite(0x90 | (i << 5) | a); }
  setNoise(v, force) { if (!force && this.regNoise === v) return; this.regNoise = v; this.chip.psgWrite(0xE0 | v); }
  fmReg(ch, reg, v) { this.chip.ymWrite((ch >= 3 ? 0x100 : 0) | (reg + ch % 3), v); }
  setKey(ch, on) {
    this.keyed[ch] = on; this.keyAt[ch] = this.chip.ymSamples;
    this.chip.ymWrite(0x28, (on ? 0xF0 : 0) | (ch >= 3 ? 4 : 0) | (ch % 3));
  }
  loadPatch(ch, p) {
    if (this.regPatch[ch] === p) return;
    this.regPatch[ch] = p;
    for (let n = 0; n < 4; n++) {
      const o = p.ops[n], s = SLOT[n];
      this.fmReg(ch, 0x30 + s, (o.dt << 4) | o.mul);
      this.fmReg(ch, 0x50 + s, (o.rs << 6) | o.ar);
      this.fmReg(ch, 0x60 + s, (o.am << 7) | o.d1r);
      this.fmReg(ch, 0x70 + s, o.d2r);
      this.fmReg(ch, 0x80 + s, (o.sl << 4) | o.rr);
      this.fmReg(ch, 0x90 + s, o.ssg);
    }
    this.fmReg(ch, 0xB0, (p.fb << 3) | p.alg);
    this.regPan[ch] = -1;
    for (let n = 0; n < 4; n++) this.regTl[ch * 4 + n] = -1;
  }
  setFmLevel(ch, p, extra) {
    const car = CARRIERS[p.alg];
    for (let n = 0; n < 4; n++) {
      const tl = (car >> n) & 1 ? Math.min(127, p.ops[n].tl + extra) : p.ops[n].tl;
      if (this.regTl[ch * 4 + n] === tl) continue;
      this.regTl[ch * 4 + n] = tl; this.fmReg(ch, 0x40 + SLOT[n], tl);
    }
  }
  setFmPan(ch, p, pan) {
    const v = (pan <= 0 ? 0x80 : 0) | (pan >= 0 ? 0x40 : 0) | (p.ams << 4) | p.pms;
    if (this.regPan[ch] === v) return;
    this.regPan[ch] = v; this.fmReg(ch, 0xB4, v);
  }
  setFreq(ch, bf) { if (this.regFreq[ch] === bf) return; this.regFreq[ch] = bf; this.fmReg(ch, 0xA4, bf >> 8); this.fmReg(ch, 0xA0, bf & 0xFF); }
  panOf(c) {
    const st = this.stacks[c], v = st.length ? st[st.length - 1] : null;
    if (!v || v.dead) return this.chanPan[c];
    return v.pan !== 2 ? v.pan : v.p.pan !== 0 ? v.p.pan : this.chanPan[c];
  }

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

  findVoice(ref) {
    if (typeof ref === 'number') return this.voices.get(ref) ?? null;
    let best = null;
    for (let c = 0; c < CHANNELS; c++) for (const v of this.stacks[c]) if (!v.dead && v.note === ref.note && !v.released && (!best || v.start >= best.start)) best = v;
    return best;
  }

  apply(ev) {
    switch (ev.t) {
      case 'on': this.doNoteOn(ev); break;
      case 'off': { const v = this.findVoice(ev.ref); if (v) this.release(v); break; }
      case 'bend': { const v = this.findVoice(ev.ref); if (v) v.bend = ev.cents; break; }
      case 'alloff': for (let c = 0; c < CHANNELS; c++) for (const v of this.stacks[c].slice()) this.release(v); break;
      case 'kill':
        for (let c = 0; c < CHANNELS; c++) { for (const v of this.stacks[c]) { v.dead = true; this.voices.delete(v.id); } this.stacks[c].length = 0; }
        for (let c = 0; c < CHANNELS; c++) this.applyChannel(c);
        break;
      case 'param':
        if (ev.name === 'volume') this.volume = ev.value;
        else if (ev.name === 'pitchBend') this.pitchBend = ev.value;
        else if (ev.name === 'tune') this.tune = ev.value;
        else if (ev.name === 'lfo') { this.lfo = ev.value; this.chip.ymWrite(0x22, ev.value > 0 ? 8 | (ev.value - 1) : 0); }
        else if (ev.name === 'pan') { for (let c = 0; c < CHANNELS; c++) this.chanPan[c] = ev.value[c]; for (let c = 0; c < 6; c++) if (this.regPatch[c]) this.setFmPan(c, this.regPatch[c], this.panOf(c)); }
        break;
    }
  }

  doNoteOn(ev) {
    const p = ev.preset, o = ev.o;
    const ch = this.pickChannel(p, o.channel);
    const v = new Voice(ev.id, p, ev.note, ev.vel, this.frame, ch);
    if (o.pan !== undefined && o.pan !== null) v.pan = o.pan;
    if (o.bend) v.bend = o.bend;
    const st = this.stacks[ch];
    st.push(v);
    if (st.length > MAX_STACK) { const old = st.shift(); old.dead = true; this.voices.delete(old.id); }
    const prev = this.voices.get(v.id); if (prev) prev.dead = true;
    this.voices.set(v.id, v);
    this.applyChannel(ch);
    if (o.duration > 0) this.schedule(this.frame + Math.round(o.duration * this.sampleRate), { t: 'off', ref: v.id });
  }

  pickChannel(p, pin) {
    let cand = KIND_CHANNELS[p.kind];
    if (pin !== undefined && pin !== null) {
      if (!cand.includes(pin)) throw new RangeError(`a ${KIND_NAMES[p.kind]} preset cannot play on channel ${pin}`);
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
    if (v.p.r <= 0) { v.dead = true; if (this.voices.get(v.id) === v) this.voices.delete(v.id); }
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

  applyChannel(ch) {
    this.prune(ch);
    const st = this.stacks[ch];
    const v = st.length ? st[st.length - 1] : null;
    if (!v) {
      if (this.gate[ch]) {
        if (ch < 6) { if (this.keyed[ch]) this.setKey(ch, false); }
        else this.setAtt(ch - 6, 15);
        this.gate[ch] = false; this.curLevel[ch] = 0; this.curNote[ch] = -1;
      }
      this.lastVoice[ch] = null;
      return;
    }
    const p = v.p, t = this.tms(v, this.frame);
    const trig = v.fresh || !this.gate[ch] || this.lastVoice[ch] !== v;
    v.fresh = false; this.lastVoice[ch] = v;
    if (ch < 6) {
      // FM: the level is the software envelope held at its release value while the chip's own release plays
      const level = v.vel * p.gain * (v.released ? v.relLevel : this.env(v, this.frame));
      const extra = tlSteps(level);
      if (trig) {
        if (this.keyed[ch]) this.setKey(ch, false);
        if (this.keyAt[ch] === this.chip.ymSamples) this.chip.ymRun(1);     // the chip sees a key edge once a sample: let one pass
        this.loadPatch(ch, p);
      }
      this.setFmPan(ch, p, this.panOf(ch));
      this.setFmLevel(ch, p, extra);
      this.setFreq(ch, blockFnum(midiHz(this.voiceCents(v, t))));
      if (trig && !v.released) this.setKey(ch, true);
      else if (v.released && this.keyed[ch]) this.setKey(ch, false);
      this.gate[ch] = true; this.curLevel[ch] = 127 - extra; this.curNote[ch] = v.note;
      return;
    }
    const att = attenuation(v.vel * p.gain * this.env(v, this.frame));
    const i = ch - 6;
    if (ch === 9) {
      let rate;
      if (p.noiseRate >= 0) {
        const f = p.noiseMs > 0 ? Math.min(1, t / p.noiseMs) : 1;
        rate = clamp(Math.floor(p.noiseRate + (p.noiseTo - p.noiseRate) * f), 0, 2);
      } else {
        const semis = this.voiceCents(v, t) / 100;
        rate = semis >= 72 ? 0 : semis >= 60 ? 1 : 2;
      }
      this.setNoise((p.noiseWhite ? 4 : 0) | rate, trig);
    } else {
      const n = clamp(roundEven(this.chip.cpuClockHz / (32 * midiHz(this.voiceCents(v, t)))), 2, 1023);
      this.setTone(i, n);
    }
    this.setAtt(i, att);
    this.gate[ch] = true; this.curLevel[ch] = 15 - att; this.curNote[ch] = v.note;
  }

  tick() {
    for (let c = 0; c < CHANNELS; c++) {
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
    for (let c = 0; c < CHANNELS; c++) a.push({ active: this.gate[c], level: this.curLevel[c], note: this.curNote[c], voices: this.stacks[c].length });
    return a;
  }
}
