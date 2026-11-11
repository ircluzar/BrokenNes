// ---- engine: notes -> S-DSP writes -> samples ----------------------------------------------------------------------------------
//
// The instrument layer of Nano SNES (the design of the other Nanos' engines, on the S-DSP alone, no SPC700):
//  * eight channels = the eight DSP voices; any preset plays on any of them; a channel is monophonic with last-note priority (the newest voice
//    owns it, older ones wait under it); a voice picks a free channel, else the longest-releasing, else the longest-playing;
//  * a preset is a BRR sample (the DSP's own 9-byte blocks, with a loop) or the DSP's noise generator; the sound RAM holds every preset's sample from
//    the start (the directory at $0200, samples from $0600, in program order, then presets added later), so a note is a register write, never an upload;
//  * pitch: the 14-bit pitch register = round(4096 x f / base), base = the sample's own pitch at $1000 (a 32-sample cycle at 32 kHz: 1000 Hz);
//    level: the voice's VOL L / R (velocity x gain x the software envelope, 7 bits, pan left / both / right); the chip's own ADSR / GAIN shapes each note too;
//  * key-on / key-off: KON and KOFF are read by the DSP on every other sample, so the voices keyed between two polls are written together;
//    a release keeps the voice keyed while the software release fades its VOL, then KOFF (the DSP's own release is 8 ms);
//  * every control tick (64 samples) each channel's registers are rewritten from the voice that owns it, only what changed.
// The arithmetic is plain doubles, no Math.pow / log / sin (exp2 below is a polynomial).

const CHUNK = 64;
const KIND = { brr: 0, noise: 1 };
const KIND_NAMES = ['brr', 'noise'];
const ALL = [0, 1, 2, 3, 4, 5, 6, 7];
const KIND_CHANNELS = [ALL, ALL];
const CHANNELS = 8;
const MAX_STACK = 8;
const DIR_PAGE = 0x02, SAMPLE_BASE = 0x0600, RAM_TOP = 0xFF00;
const MAIN_VOLUME = 0x30;           // about a quarter of full scale for one voice at full level: room for four together

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
function midiHz(cents) { return 440 * exp2((cents - 6900) / 1200); }

const B64 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
function fromBase64(s) {
  const clean = s.replace(/=+$/, ''), out = new Uint8Array((clean.length * 3) >> 2);
  let bits = 0, acc = 0, o = 0;
  for (let i = 0; i < clean.length; i++) {
    const v = B64.indexOf(clean[i]);
    if (v < 0) throw new RangeError('brr is base64');
    acc = (acc << 6) | v; bits += 6;
    if (bits >= 8) { bits -= 8; out[o++] = (acc >> bits) & 0xFF; }
  }
  return out;
}
function toBase64(b) {
  let s = '';
  for (let i = 0; i < b.length; i += 3) {
    const n = b[i] << 16 | (b[i + 1] ?? 0) << 8 | (b[i + 2] ?? 0);
    s += B64[n >> 18 & 63] + B64[n >> 12 & 63] + (i + 1 < b.length ? B64[n >> 6 & 63] : '=') + (i + 2 < b.length ? B64[n & 63] : '=');
  }
  return s;
}

// 16-bit PCM -> BRR, integer only (the same bytes everywhere). Each block of 16 tries the four filters and the ranges 0-12 through the S-DSP's own
// decoding arithmetic (DSP_SFC.DecodeBrr) and keeps the one with the least squared error; a nybble is the residual rounded and clamped. The decoder's
// state carries from block to block (the first block of a loop uses filter 0, so the loop starts clean). loopStart (a multiple of 16, or -1) sets the
// loop flag on every block and the end flag on the last.
function brrStep(nib, shift, filter, p1, p2h) {
  let s = (nib << shift) >> 1;
  if (filter >= 8) { s += p1 - p2h; if (filter === 8) { s += p2h >> 4; s += (p1 * -3) >> 6; } else { s += (p1 * -13) >> 7; s += (p2h * 3) >> 4; } }
  else if (filter !== 0) { s += p1 >> 1; s += (-p1) >> 5; }
  s = s < -32768 ? -32768 : s > 32767 ? 32767 : s;
  return ((s * 2) << 16) >> 16;
}
function encodeBrr(pcm, loopStart) {
  const blocks = Math.max(1, Math.ceil(pcm.length / 16)), out = new Uint8Array(blocks * 9), nibs = new Int8Array(16), best = new Int8Array(16);
  let p1 = 0, p2 = 0;
  for (let b = 0; b < blocks; b++) {
    let bestErr = Infinity, bestF = 0, bestS = 0, bp1 = 0, bp2 = 0;
    const loopBlock = loopStart >= 0 && b * 16 === loopStart;
    for (let fi = 0; fi < 4; fi++) {
      if ((b === 0 || loopBlock) && fi > 0) break;
      const filter = fi << 2;
      for (let shift = 0; shift <= 12; shift++) {
        let q1 = p1, q2 = p2, err = 0;
        for (let i = 0; i < 16; i++) {
          const x = (pcm[b * 16 + i] ?? 0) & ~1;
          const pred = brrStep(0, shift, filter, q1, q2 >> 1);
          const d = x - pred, unit = 1 << shift;           // the stored value moves by nib << shift
          let n = d < 0 ? -Math.floor((-d + (unit >> 1)) / unit) : Math.floor((d + (unit >> 1)) / unit);
          n = n < -8 ? -8 : n > 7 ? 7 : n;
          const y = brrStep(n, shift, filter, q1, q2 >> 1);
          nibs[i] = n; err += (x - y) * (x - y);
          q2 = q1; q1 = y;
          if (err >= bestErr) break;
        }
        if (err < bestErr) { bestErr = err; bestF = filter; bestS = shift; best.set(nibs); bp1 = q1; bp2 = q2; }
      }
    }
    p1 = bp1; p2 = bp2;
    out[b * 9] = (bestS << 4) | bestF | (b === blocks - 1 ? 1 : 0) | (loopStart >= 0 ? 2 : 0);
    for (let i = 0; i < 16; i++) { const n = best[i]; if (i & 1) out[b * 9 + 1 + (i >> 1)] |= n & 15; else out[b * 9 + 1 + (i >> 1)] = (n & 15) << 4; }
  }
  return out;
}

function normalizePreset(p, name) {
  if (!p || typeof p !== 'object') throw new TypeError('a preset is an object');
  const kind = KIND[p.kind];
  if (kind === undefined) throw new RangeError(`preset kind must be 'brr' or 'noise', not ${p.kind}`);
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
    pan: 0,
    brr: '', loop: -1, base: 1000, adsr1: 0x8F, adsr2: 0xE0, gainReg: 0x7F, noiseRate: -1,
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
  if (p.adsr !== undefined) {
    if (!Array.isArray(p.adsr) || p.adsr.length !== 4) throw new RangeError('adsr is [attack 0..15, decay 0..7, sustain level 0..7, sustain rate 0..31] (the chip\'s own)');
    const [ar, dr, sl, sr] = p.adsr;
    out.adsr1 = 0x80 | int(dr, 0, 0, 7, 'adsr decay') << 4 | int(ar, 15, 0, 15, 'adsr attack');
    out.adsr2 = int(sl, 7, 0, 7, 'adsr sustain level') << 5 | int(sr, 0, 0, 31, 'adsr sustain rate');
  }
  if (p.gainReg !== undefined) { out.gainReg = int(p.gainReg, 0x7F, 0, 255, 'gainReg'); if (p.adsr === undefined) out.adsr1 = 0; }
  if (kind === 0) {
    if (typeof p.brr === 'string' && p.brr.length) {
      const b = fromBase64(p.brr);
      if (b.length === 0 || b.length % 9 !== 0) throw new RangeError('brr is whole 9-byte blocks (base64)');
      out.brr = p.brr; out.loop = int(p.loop, -1, -1, b.length / 9 - 1, 'loop (a block index)');
    } else if (Array.isArray(p.wave) && p.wave.length) {
      if (p.wave.some(x => !Number.isInteger(x) || x < -32768 || x > 32767)) throw new RangeError('wave is 16-bit whole numbers');
      const loop = int(p.loop, 0, -1, p.wave.length - 1, 'loop');
      if (loop >= 0 && loop % 16 !== 0) throw new RangeError('a wave loop starts on a multiple of 16 samples');
      out.brr = toBase64(encodeBrr(p.wave, loop)); out.loop = loop >= 0 ? loop / 16 : -1;
    } else throw new RangeError('a brr preset has brr (base64 BRR blocks) or wave (16-bit samples)');
    out.base = num(p.base, 1000, 1, 128000, 'base');
  } else {
    if (p.brr !== undefined || p.wave !== undefined) throw new RangeError('a noise preset has no sample');
    if (p.noise && p.noise.rate !== undefined) out.noiseRate = int(p.noise.rate, 0, 0, 31, 'noise.rate');
  }
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
  // opts: {volume (post-chip gain), presets (extra {name: preset}), trace, meter}
  constructor(sampleRate, opts = {}) {
    if (!(sampleRate >= 3000 && sampleRate <= 768000)) throw new RangeError('sampleRate out of range');
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
    this.regSrc = new Int32Array(CHANNELS).fill(-1); this.regPitch = new Int32Array(CHANNELS).fill(-1); this.regVolL = new Int32Array(CHANNELS).fill(-1); this.regVolR = new Int32Array(CHANNELS).fill(-1);
    this.regEnv = new Int32Array(CHANNELS).fill(-1);
    this.keyed = new Array(CHANNELS).fill(false);
    this.konBits = 0; this.konPoll = -1; this.koff = 0; this.non = 0; this.noiseRate = -1;
    this.chanPan = new Array(CHANNELS).fill(0);
    this.pitchBend = 0; this.tune = 0;
    this.presets = {}; this.srcOf = new Map(); this.nextSrc = 0; this.ramAt = SAMPLE_BASE;
    // the DSP at rest: main volume up, echo off and its writes off, nothing keyed; the directory page
    const w = (r, v) => this.chip.write(r, v);
    w(0x6C, 0x20); w(0x0C, MAIN_VOLUME); w(0x1C, MAIN_VOLUME); w(0x2C, 0); w(0x3C, 0); w(0x0D, 0); w(0x2D, 0); w(0x3D, 0); w(0x4D, 0); w(0x5D, DIR_PAGE); w(0x5C, 0);
    for (const k of Object.keys(PRESETS)) this.addPreset(k, PRESETS[k]);
    if (opts.presets) for (const k of Object.keys(opts.presets)) this.addPreset(k, opts.presets[k]);
    this.voices = new Map();
    this.meter = !!opts.meter; this.peak = 0;
  }
  get trace() { return this.chip.trace; }

  // a preset's sample into the sound RAM (once per sample: the same base64 shares its place) and its directory entry
  addPreset(name, def) {
    const p = normalizePreset(def, name);
    if (p.kind === 0 && !this.srcOf.has(p.brr)) {
      if (this.nextSrc >= 256) throw new RangeError('the sound RAM directory is full (256 samples)');
      const bytes = fromBase64(p.brr);
      if (this.ramAt + bytes.length > RAM_TOP) throw new RangeError('the samples do not fit the 64 KB sound RAM');
      const at = this.ramAt, loop = p.loop >= 0 ? at + p.loop * 9 : at, src = this.nextSrc++;
      this.chip.writeRam(at, bytes);
      this.chip.writeRam(DIR_PAGE * 0x100 + src * 4, [at & 0xFF, at >> 8, loop & 0xFF, loop >> 8]);
      this.ramAt += bytes.length;
      this.srcOf.set(p.brr, src);
    }
    this.presets[name] = p;
  }

  setKey(ch, on) {
    const bit = 1 << ch;
    if (on) {
      const poll = this.chip.polls;
      if (poll !== this.konPoll) { this.konPoll = poll; this.konBits = 0; }
      this.konBits |= bit;
      if (this.koff & bit) { this.koff &= ~bit; this.chip.write(0x5C, this.koff); }
      this.chip.write(0x4C, this.konBits);
    } else if (!(this.koff & bit)) { this.koff |= bit; this.chip.write(0x5C, this.koff); }
    this.keyed[ch] = on;
  }
  vreg(ch, r, v) { this.chip.write((ch << 4) | r, v); }

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
  resolvePreset(p) {
    if (typeof p === 'string') { const r = this.presets[p]; if (!r) throw new RangeError(`unknown preset '${p}'`); return r; }
    const n = normalizePreset(p);
    if (n.kind === 0 && !this.srcOf.has(n.brr)) this.addPreset('\u0000' + n.brr, p);
    return n;
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
        else if (ev.name === 'pan') { for (let c = 0; c < CHANNELS; c++) this.chanPan[c] = ev.value[c]; }
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

  panOf(c) {
    const st = this.stacks[c], v = st.length ? st[st.length - 1] : null;
    if (!v || v.dead) return this.chanPan[c];
    return v.pan !== 2 ? v.pan : v.p.pan !== 0 ? v.p.pan : this.chanPan[c];
  }

  applyChannel(ch) {
    this.prune(ch);
    const st = this.stacks[ch];
    const v = st.length ? st[st.length - 1] : null;
    if (!v) {
      if (this.gate[ch]) { if (this.keyed[ch]) this.setKey(ch, false); this.gate[ch] = false; this.curLevel[ch] = 0; this.curNote[ch] = -1; }
      this.lastVoice[ch] = null;
      return;
    }
    const p = v.p, t = this.tms(v, this.frame);
    const trig = v.fresh || !this.gate[ch] || this.lastVoice[ch] !== v;
    v.fresh = false; this.lastVoice[ch] = v;
    const bit = 1 << ch;
    if (trig) {
      const src = p.kind === 0 ? this.srcOf.get(p.brr) : 0;
      if (this.regSrc[ch] !== src) { this.regSrc[ch] = src; this.vreg(ch, 4, src); }
      const envKey = p.adsr1 << 16 | p.adsr2 << 8 | p.gainReg;
      if (this.regEnv[ch] !== envKey) { this.regEnv[ch] = envKey; this.vreg(ch, 5, p.adsr1); this.vreg(ch, 6, p.adsr2); this.vreg(ch, 7, p.gainReg); }
      const non = p.kind === 1 ? this.non | bit : this.non & ~bit;
      if (non !== this.non) { this.non = non; this.chip.write(0x3D, non); }
    }
    if (p.kind === 1) {
      let rate = p.noiseRate;
      if (rate < 0) rate = clamp(Math.floor(this.voiceCents(v, t) / 400) + 1, 0, 31);   // a third of an octave a step: 31 at C10, as high as a note goes
      if (rate !== this.noiseRate) { this.noiseRate = rate; this.chip.write(0x6C, 0x20 | rate); }
    } else {
      const pitch = clamp(roundEven(4096 * midiHz(this.voiceCents(v, t)) / p.base), 0, 0x3FFF);
      if (this.regPitch[ch] !== pitch) { this.regPitch[ch] = pitch; this.vreg(ch, 2, pitch & 0xFF); this.vreg(ch, 3, pitch >> 8); }
    }
    const level = v.vel * p.gain * this.env(v, this.frame), vol = clamp(roundEven(level * 127), 0, 127), pan = this.panOf(ch);
    const vl = pan > 0 ? 0 : vol, vr = pan < 0 ? 0 : vol;
    if (this.regVolL[ch] !== vl) { this.regVolL[ch] = vl; this.vreg(ch, 0, vl); }
    if (this.regVolR[ch] !== vr) { this.regVolR[ch] = vr; this.vreg(ch, 1, vr); }
    if (trig) this.setKey(ch, true);
    this.gate[ch] = true; this.curLevel[ch] = vol; this.curNote[ch] = v.note;
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
