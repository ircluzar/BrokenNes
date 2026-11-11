// ---- engine: notes -> AdLib / speaker writes -> samples --------------------------------------------------------------------------
//
// The instrument layer of Nano DOS (the design of the other Nanos' engines, on the PC's AdLib and speaker):
//  * ten channels: the OPL2's nine melodic voices (0-8) and the PC speaker (9); a channel is monophonic with last-note priority; a voice picks a free
//    channel of its kind, else the longest-releasing, else the longest-playing;
//  * FM: the preset is an 11-byte SBI register set (mod 20, car 20, mod 40, car 40, mod 60, car 60, mod 80, car 80, mod E0, car E0, C0: the plugin's
//    PC patch format), loaded when a voice takes the channel; key-on at the note, key-off at its release (the chip's own release rings for the preset's
//    r ms, then the channel is free); pitch = block + 10-bit F-number at 49,716 Hz; level = extra total level on the carrier (and the modulator of an
//    additive patch), 0.75 dB a step, from velocity x gain x the software a / d / s;
//  * the speaker: the 8254's channel 2 square (count = 1,193,182 / f) gated by port 61h: on or off, no volume (a level of zero is off);
//  * every control tick (64 samples) each channel's registers are rewritten from the voice that owns it, only what changed. The AdLib is mono.
// The arithmetic is plain doubles, no Math.pow / log / sin (exp2 below is a polynomial).

const CHUNK = 64;
const KIND = { fm: 0, speaker: 1 };
const KIND_NAMES = ['fm', 'speaker'];
const KIND_CHANNELS = [[0, 1, 2, 3, 4, 5, 6, 7, 8], [9]];
const CHANNELS = 10;
const MAX_STACK = 8;
const OP_MOD = [0, 1, 2, 8, 9, 10, 16, 17, 18];        // the modulator's operator offset of each voice (the carrier is +3)
const OPL_RATE = 3579545 / 72;

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
const TL_MID = new Float64Array(63);
for (let k = 0; k < 63; k++) TL_MID[k] = exp2(-(k + 0.5) * 0.0375 * 3.321928094887362);
function midiHz(cents) { return 440 * exp2((cents - 6900) / 1200); }
function tlSteps(amp) { for (let k = 0; k < 63; k++) if (amp >= TL_MID[k]) return k; return 63; }
// block (0..7) and F-number (0..1023): the lowest block whose F-number fits, F = hz x 2^(20 - block) / 49716
function blockFnum(hz) {
  let scale = 1048576;
  for (let b = 0; b < 8; b++) {
    const f = roundEven(hz * scale / OPL_RATE);
    if (f <= 1023) return (b << 10) | (f < 1 ? 1 : f);
    scale = scale * 0.5;
  }
  return (7 << 10) | 1023;
}
function speakerCount(hz) { return clamp(roundEven(1193182 / hz), 1, 65535); }

function normalizePreset(p, name) {
  if (!p || typeof p !== 'object') throw new TypeError('a preset is an object');
  const kind = KIND[p.kind];
  if (kind === undefined) throw new RangeError(`preset kind must be 'fm' or 'speaker', not ${p.kind}`);
  const num = (v, dflt, lo, hi, what) => {
    if (v === undefined || v === null) return dflt;
    if (typeof v !== 'number' || !isFinite(v) || v < lo || v > hi) throw new RangeError(`preset ${what} must be a number from ${lo} to ${hi}`);
    return v;
  };
  const out = {
    name: name ?? p.name ?? 'custom', label: p.label ?? name ?? 'custom', desc: p.desc ?? '', kind,
    note: num(p.note, 60, 0, 127, 'note'),
    gain: num(p.gain, 1, 0, 1, 'gain'),
    a: num(p.a, 0, 0, 60000, 'a'), d: num(p.d, 0, 0, 60000, 'd'), s: num(p.s, 1, 0, 1, 's'), r: num(p.r, 0, 0, 60000, 'r'),
    oneShot: !!p.oneShot,
    vibRate: 0, vibDepth: 0, vibDelay: 0, vibFade: 0,
    slideFrom: 0, slideTo: 0, slideMs: 0,
    arpSteps: null, arpRate: 0, arpOnce: false,
    regs: null,
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
  if (kind === 0) {
    if (!Array.isArray(p.regs) || p.regs.length !== 11 || p.regs.some(x => !Number.isInteger(x) || x < 0 || x > 255)) throw new RangeError('an fm preset has regs: the 11 SBI bytes (mod 20, car 20, mod 40, car 40, mod 60, car 60, mod 80, car 80, mod E0, car E0, C0)');
    out.regs = p.regs.slice();
  } else if (p.regs !== undefined) throw new RangeError('the speaker has no registers to set');
  if (p.pan !== undefined) throw new RangeError('the AdLib and the speaker are mono: no pan');
  return out;
}

function panValue(v) {
  if (v === -1 || v === 0 || v === 1 || v === 'L' || v === 'C' || v === 'R') return 0;
  throw new RangeError('pan is -1, 0 or 1 (ignored: the PC is mono)');
}

class Voice {
  constructor(id, preset, note, vel, start, channel) {
    this.id = id; this.p = preset; this.note = note; this.vel = vel; this.start = start; this.channel = channel;
    this.fresh = true; this.released = false; this.releasedAt = 0; this.relLevel = 0; this.dead = false;
    this.bend = 0; this.pan = 2;
  }
}

class Engine {
  // opts: {volume (post-chip gain), presets (extra {name: preset}), trace, meter}
  constructor(sampleRate, opts = {}) {
    if (!(sampleRate >= 11025 && sampleRate <= 768000)) throw new RangeError('sampleRate out of range (11025 to 768000: the 44.1 kHz sound is resampled, at most 4 to 1)');
    this.chip = new Chip(sampleRate);
    if (opts.trace) this.chip.trace = [];
    this.sampleRate = sampleRate;
    this.volume = opts.volume ?? 1;
    this.frame = 0; this.nextTick = 0;
    this.events = []; this.seq = 0;
    this.stacks = []; for (let c = 0; c < CHANNELS; c++) this.stacks.push([]);
    this.gate = new Array(CHANNELS).fill(false);
    this.curLevel = new Array(CHANNELS).fill(0); this.curNote = new Array(CHANNELS).fill(-1);
    this.lastVoice = new Array(CHANNELS).fill(null);
    this.regPatch = new Array(9).fill(null); this.regTl = new Int32Array(18).fill(-1); this.regFreq = new Int32Array(9).fill(-1);
    this.keyed = new Array(9).fill(false);
    this.spkOn = false; this.spkCount = -1;
    this.chanPan = new Array(CHANNELS).fill(0);
    this.pitchBend = 0; this.tune = 0;
    this.presets = {};
    for (const k of Object.keys(PRESETS)) this.presets[k] = normalizePreset(PRESETS[k], k);
    if (opts.presets) for (const k of Object.keys(opts.presets)) this.presets[k] = normalizePreset(opts.presets[k], k);
    this.voices = new Map();
    this.meter = !!opts.meter; this.peak = 0;
    this.chip.oplWrite(0x01, 0x20);       // the waveform select enable (an AdLib driver's first write)
  }
  get trace() { return this.chip.trace; }

  // ---- register access (only what changed) ----
  loadPatch(ch, p) {
    if (this.regPatch[ch] === p) return;
    this.regPatch[ch] = p;
    const m = OP_MOD[ch], c = m + 3, r = p.regs, w = (a, v) => this.chip.oplWrite(a, v);
    w(0x20 + m, r[0]); w(0x20 + c, r[1]); w(0x60 + m, r[4]); w(0x60 + c, r[5]); w(0x80 + m, r[6]); w(0x80 + c, r[7]); w(0xE0 + m, r[8]); w(0xE0 + c, r[9]); w(0xC0 + ch, r[10]);
    this.regTl[ch * 2] = this.regTl[ch * 2 + 1] = -1;
  }
  setLevel(ch, p, extra) {
    const m = OP_MOD[ch], r = p.regs, additive = (r[10] & 1) !== 0;
    const mv = (r[2] & 0xC0) | (additive ? Math.min(63, (r[2] & 63) + extra) : r[2] & 63), cv = (r[3] & 0xC0) | Math.min(63, (r[3] & 63) + extra);
    if (this.regTl[ch * 2] !== mv) { this.regTl[ch * 2] = mv; this.chip.oplWrite(0x40 + m, mv); }
    if (this.regTl[ch * 2 + 1] !== cv) { this.regTl[ch * 2 + 1] = cv; this.chip.oplWrite(0x43 + m, cv); }
  }
  setFreq(ch, bf, key) {
    const v = bf | (key ? 0x2000 : 0);
    if (this.regFreq[ch] === v) return;
    const old = this.regFreq[ch];
    this.regFreq[ch] = v;
    if (old < 0 || (old & 0xFF) !== (v & 0xFF)) this.chip.oplWrite(0xA0 + ch, v & 0xFF);
    this.chip.oplWrite(0xB0 + ch, v >> 8);
    this.keyed[ch] = key;
  }
  setSpeaker(on, count) {
    if (on === this.spkOn && (!on || count === this.spkCount)) return;
    this.spkOn = on; if (on) this.spkCount = count;
    this.chip.speaker(on, on ? count : this.chip.spkCount);
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
        break;
    }
  }

  doNoteOn(ev) {
    const p = ev.preset, o = ev.o;
    const ch = this.pickChannel(p, o.channel);
    const v = new Voice(ev.id, p, ev.note, ev.vel, this.frame, ch);
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
    if (v.p.r <= 0 || v.p.kind === 1) { v.dead = true; if (this.voices.get(v.id) === v) this.voices.delete(v.id); }
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
        if (ch < 9) { if (this.keyed[ch]) this.setFreq(ch, this.regFreq[ch] & 0x1FFF, false); }
        else this.setSpeaker(false, 0);
        this.gate[ch] = false; this.curLevel[ch] = 0; this.curNote[ch] = -1;
      }
      this.lastVoice[ch] = null;
      return;
    }
    const p = v.p, t = this.tms(v, this.frame);
    const trig = v.fresh || !this.gate[ch] || this.lastVoice[ch] !== v;
    v.fresh = false; this.lastVoice[ch] = v;
    if (ch < 9) {
      const level = v.vel * p.gain * (v.released ? v.relLevel : this.env(v, this.frame)), extra = tlSteps(level);
      if (trig) { if (this.keyed[ch]) this.setFreq(ch, this.regFreq[ch] & 0x1FFF, false); this.loadPatch(ch, p); }
      this.setLevel(ch, p, extra);
      this.setFreq(ch, blockFnum(midiHz(this.voiceCents(v, t))), !v.released);
      this.gate[ch] = true; this.curLevel[ch] = 63 - extra; this.curNote[ch] = v.note;
      return;
    }
    const level = v.vel * p.gain * this.env(v, this.frame);
    this.setSpeaker(level > 0, speakerCount(midiHz(this.voiceCents(v, t))));
    this.gate[ch] = true; this.curLevel[ch] = level > 0 ? 63 : 0; this.curNote[ch] = v.note;
  }

  tick() {
    for (let c = 0; c < CHANNELS; c++) {
      const st = this.stacks[c];
      for (let i = st.length - 1; i >= 0; i--) { const v = st[i]; if (!v.dead && this.finished(v, this.frame)) { v.dead = true; if (this.voices.get(v.id) === v) this.voices.delete(v.id); } }
      this.applyChannel(c);
    }
  }

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
      this.peak = pk;
    }
  }

  activity() {
    const a = [];
    for (let c = 0; c < CHANNELS; c++) a.push({ active: this.gate[c], level: this.curLevel[c], note: this.curNote[c], voices: this.stacks[c].length });
    return a;
  }
}
