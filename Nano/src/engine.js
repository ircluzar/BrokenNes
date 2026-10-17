// ---- engine: notes -> sound chip registers -> samples ---------------------------------------------------------------------------
//
// The instrument layer. It follows Direct mode of the BrokenNes2 plugin (Plugin/BrokenNes2.Plugin/Bn2Plugin.cs, Apply / Silence / ResolveOwner):
//  * pitch is a number of cents, turned into the chip's period register exactly as the plugin does (CPU Hz / (16 f) - 1 for a pulse, / (32 f) for the
//    triangle, rounded to even), and the 4-bit volume is round-to-even of 15 x level;
//  * every control tick (64 samples, ~1.3 ms, like the plugin) the registers of each channel are rewritten from the voice that owns it, and only the
//    ones that changed are written; $4003 / $400B (which restart the waveform) only on a new note or when the period's high bits change;
//  * a channel is monophonic with last-note priority: the newest voice owns it and the older ones wait under it (a sound effect that steals a
//    channel gives it back, as on the console);
//  * where the plugin takes pitch and volume from FL's piano roll, Nano takes them from the preset (envelope, vibrato, slide, arpeggio ...) and the
//    note-on / note-off / bend events.
// Everything the chip does with those registers is the chip's (chip.js).

const CPU_HZ = 1789773;
const CHUNK = 64;                 // the control tick, in samples
const KIND = { pulse: 0, triangle: 1, noise: 2 };
const KIND_CHANNELS = [[0, 1], [2], [3]];
const KIND_NAMES = ['pulse', 'triangle', 'noise'];
const MAX_STACK = 8;

// C# Math.Round(double): halves go to the even neighbour (JS Math.round sends them up)
function roundEven(x) {
  const f = Math.floor(x), d = x - f;
  if (d < 0.5) return f;
  if (d > 0.5) return f + 1;
  return (f % 2 === 0) ? f : f + 1;
}
function clamp(x, lo, hi) { return x < lo ? lo : x > hi ? hi : x; }

// Fills in every field of a preset (plain data in, plain data out; the same shape is what tools/NanoParity reads).
function normalizePreset(p, name) {
  if (!p || typeof p !== 'object') throw new TypeError('a preset is an object');
  const kind = KIND[p.kind];
  if (kind === undefined) throw new RangeError(`preset kind must be 'pulse', 'triangle' or 'noise', not ${p.kind}`);
  const num = (v, dflt, lo, hi, what) => {
    if (v === undefined || v === null) return dflt;
    if (typeof v !== 'number' || !isFinite(v) || v < lo || v > hi) throw new RangeError(`preset ${what} must be a number from ${lo} to ${hi}`);
    return v;
  };
  const out = {
    name: name ?? p.name ?? 'custom', label: p.label ?? name ?? 'custom', desc: p.desc ?? '', kind,
    note: num(p.note, 60, 0, 127, 'note'),
    duty: num(p.duty, 1, 0, 3, 'duty') | 0,
    dutySteps: null, dutyRate: 0,
    gain: num(p.gain, 1, 0, 1, 'gain'),
    a: num(p.a, 0, 0, 60000, 'a'), d: num(p.d, 0, 0, 60000, 'd'), s: num(p.s, 1, 0, 1, 's'), r: num(p.r, 0, 0, 60000, 'r'),
    oneShot: !!p.oneShot,
    vibRate: 0, vibDepth: 0, vibDelay: 0, vibFade: 0,
    slideFrom: 0, slideTo: 0, slideMs: 0,
    arpSteps: null, arpRate: 0, arpOnce: false,
    noiseIdx: -1, noiseTo: 0, noiseMs: 0, noiseMode: 0,
    sweep: -1,
  };
  if (out.oneShot && out.s !== 0) throw new RangeError('a oneShot preset needs s = 0 (it ends by itself after a + d)');
  if (p.dutySeq) {
    const st = p.dutySeq.steps;
    if (!Array.isArray(st) || st.length === 0 || st.some(x => !Number.isInteger(x) || x < 0 || x > 3)) throw new RangeError('dutySeq.steps must be a list of 0..3');
    out.dutySteps = st.slice(); out.dutyRate = num(p.dutySeq.rate, 8, 0.1, 1000, 'dutySeq.rate');
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
    out.noiseIdx = num(p.noise.idx, 0, 0, 15, 'noise.idx');
    out.noiseTo = num(p.noise.to, out.noiseIdx, 0, 15, 'noise.to'); out.noiseMs = num(p.noise.ms, 100, 0, 60000, 'noise.ms');
    out.noiseMode = num(p.noise.mode, 0, 0, 1, 'noise.mode') | 0;
  }
  if (p.sweep) {
    if (kind !== 0) throw new RangeError('only a pulse preset can use the sweep unit');
    const per = num(p.sweep.period, 0, 0, 7, 'sweep.period') | 0, sh = num(p.sweep.shift, 1, 0, 7, 'sweep.shift') | 0;
    out.sweep = 0x80 | (per << 4) | (p.sweep.negate ? 8 : 0) | sh;
  }
  return out;
}

class Voice {
  constructor(id, preset, note, vel, start, channel) {
    this.id = id; this.p = preset; this.note = note; this.vel = vel; this.start = start; this.channel = channel;
    this.fresh = true; this.released = false; this.releasedAt = 0; this.relLevel = 0; this.dead = false;
    this.bend = 0; this.duty = -1; this.noiseMode = -1;
  }
}

class Engine {
  // opts: {volume (post-chip gain, 1 = the chip's own level), presets (extra {name: preset}), trace}
  constructor(sampleRate, opts = {}) {
    if (!(sampleRate >= 3000 && sampleRate <= 768000)) throw new RangeError('sampleRate out of range');
    this.sampleRate = sampleRate;
    this.chip = new Chip(sampleRate, 0x0F);
    this.volume = opts.volume ?? 1;
    this.frame = 0;                    // engine time in samples
    this.nextTick = 0;                 // the frame of the next control tick
    this.events = [];                  // sorted by frame, then by arrival
    this.seq = 0;
    this.stacks = [[], [], [], []];    // per channel: the voices on it, newest last
    this.gate = [false, false, false, false];
    this.curPeriod = [0, 0, 0, 0];
    this.curLevel = [0, 0, 0, 0];
    this.curNote = [-1, -1, -1, -1];
    this.lastVoice = [null, null, null, null];   // the voice whose registers each channel last got
    this.pitchBend = 0;                // cents, all voices
    this.tune = 0;                     // cents, all voices
    this.presets = {};
    for (const k of Object.keys(PRESETS)) this.presets[k] = normalizePreset(PRESETS[k], k);
    if (opts.presets) for (const k of Object.keys(opts.presets)) this.presets[k] = normalizePreset(opts.presets[k], k);
    this.voices = new Map();           // id -> Voice (alive ones)
    this.trace = opts.trace ? [] : null;   // [[0, reg, value], [1, samples]]: what the chip was asked to do, for tools/NanoParity
    this.peak = 0;
    this.stats = { stolen: 0, noteOns: 0 };
  }

  // ---- register access (with the plugin's shadow compare) ----
  w(reg, value) { if (this.chip.shadow[reg] !== value) this.wf(reg, value); }
  wf(reg, value) {
    this.chip.write(reg, value);
    if (this.trace) this.trace.push([0, reg, value]);
  }

  // ---- events ----
  // frame: the engine time to apply it at (an earlier time means now)
  schedule(frame, ev) {
    ev.frame = frame; ev.seq = this.seq++;
    const evs = this.events;
    let i = evs.length;
    while (i > 0 && evs[i - 1].frame > frame) i--;
    evs.splice(i, 0, ev);
  }

  noteOn(frame, id, note, vel, preset, o = {}) { this.schedule(frame, { t: 'on', id, note, vel, preset, o }); }
  noteOff(frame, ref) { this.schedule(frame, { t: 'off', ref }); }          // ref: a voice id, or {note}
  bendVoice(frame, ref, cents) { this.schedule(frame, { t: 'bend', ref, cents }); }
  allOff(frame) { this.schedule(frame, { t: 'alloff' }); }
  kill(frame) { this.schedule(frame, { t: 'kill' }); }
  setParam(frame, name, value) { this.schedule(frame, { t: 'param', name, value }); }

  addPreset(name, def) { this.presets[name] = normalizePreset(def, name); }

  resolvePreset(p) {
    if (typeof p === 'string') {
      const r = this.presets[p];
      if (!r) throw new RangeError(`unknown preset '${p}'`);
      return r;
    }
    return normalizePreset(p);
  }

  findVoice(ref) {
    if (typeof ref === 'number') return this.voices.get(ref) ?? null;
    // by note: the newest voice with that note that is still held
    let best = null;
    for (const v of this.voices.values()) if (v.note === ref.note && !v.released && (!best || v.start >= best.start)) best = v;
    return best;
  }

  apply(ev) {
    switch (ev.t) {
      case 'on': this.doNoteOn(ev); break;
      case 'off': { const v = this.findVoice(ev.ref); if (v) this.release(v); break; }
      case 'bend': { const v = this.findVoice(ev.ref); if (v) v.bend = ev.cents; break; }
      case 'alloff': for (const v of Array.from(this.voices.values())) this.release(v); break;
      case 'kill': for (const v of Array.from(this.voices.values())) this.remove(v); for (let c = 0; c < 4; c++) this.applyChannel(c); break;
      case 'param':
        if (ev.name === 'volume') this.volume = ev.value;
        else if (ev.name === 'pitchBend') this.pitchBend = ev.value;
        else if (ev.name === 'tune') this.tune = ev.value;
        break;
    }
  }

  doNoteOn(ev) {
    const p = ev.preset;           // already a normalized preset
    const o = ev.o;
    this.stats.noteOns++;
    const ch = this.pickChannel(p.kind, o.channel);
    const v = new Voice(ev.id, p, ev.note, ev.vel, this.frame, ch);
    if (o.duty !== undefined && o.duty !== null) v.duty = o.duty | 0;
    if (o.noiseMode !== undefined && o.noiseMode !== null) v.noiseMode = o.noiseMode | 0;
    if (o.bend) v.bend = o.bend;
    const st = this.stacks[ch];
    st.push(v);
    if (st.length > MAX_STACK) { const old = st.shift(); this.voices.delete(old.id); }
    this.voices.set(v.id, v);
    this.applyChannel(ch);
    if (o.duration > 0) this.noteOff(this.frame + Math.round(o.duration * this.sampleRate), v.id);
  }

  // Which channel a new voice of this kind takes: a free one; else one whose voice is in its release tail (the longest gone); else the one whose
  // voice has been playing longest. The voice it displaces stays under the new one and returns when the new one ends.
  pickChannel(kind, pin) {
    let cand = KIND_CHANNELS[kind];
    if (pin !== undefined && pin !== null) {
      if (!cand.includes(pin)) throw new RangeError(`a ${KIND_NAMES[kind]} preset cannot play on channel ${pin}`);
      cand = [pin];
    }
    for (const c of cand) this.prune(c);
    for (const c of cand) if (this.stacks[c].length === 0) return c;
    let best = -1, bestAt = Infinity;
    for (const c of cand) {
      const top = this.stacks[c][this.stacks[c].length - 1];
      if (top.released && top.releasedAt < bestAt) { best = c; bestAt = top.releasedAt; }
    }
    if (best >= 0) return best;
    best = cand[0]; bestAt = Infinity;
    for (const c of cand) {
      const top = this.stacks[c][this.stacks[c].length - 1];
      if (top.start < bestAt) { best = c; bestAt = top.start; }
    }
    this.stats.stolen++;
    return best;
  }

  prune(c) {
    const st = this.stacks[c];
    for (let i = st.length - 1; i >= 0; i--) if (st[i].dead) { this.voices.delete(st[i].id); st.splice(i, 1); }
  }

  remove(v) {
    v.dead = true;
    this.voices.delete(v.id);
    const st = this.stacks[v.channel];
    const i = st.indexOf(v);
    if (i >= 0) st.splice(i, 1);
  }

  // ---- the voice's sound at a time ----
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
    if (v.released || v.dead) return;
    if (v.p.oneShot) return;
    v.relLevel = this.env(v, this.frame);
    v.released = true; v.releasedAt = this.frame;
    if (v.p.r <= 0) { v.dead = true; this.voices.delete(v.id); }   // the stack drops it at the next prune
    this.applyChannel(v.channel);
  }

  // has the voice reached its natural end (a release tail over, or an envelope that decayed to nothing)?
  finished(v, frame) {
    const p = v.p;
    if (v.dead) return true;
    if (v.released) return p.r <= 0 || (frame - v.releasedAt) * 1000 / this.sampleRate >= p.r;
    return p.s === 0 && this.tms(v, frame) >= p.a + p.d;
  }

  voiceCents(v, t) {
    const p = v.p;
    let c = (v.note - 60) * 100 + v.bend + this.pitchBend + this.tune;
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
        const tri = ph < 0.25 ? 4 * ph : ph < 0.75 ? 2 - 4 * ph : 4 * ph - 4;
        c += amp * tri;
      }
    }
    return c;
  }

  // ---- the plugin's Apply: write this channel's registers from the voice that owns it ----
  applyChannel(ch) {
    this.prune(ch);
    const st = this.stacks[ch];
    const v = st.length ? st[st.length - 1] : null;
    if (!v) {
      if (this.gate[ch]) { this.silence(ch); this.gate[ch] = false; this.curLevel[ch] = 0; this.curNote[ch] = -1; }
      this.lastVoice[ch] = null;
      return;
    }
    const p = v.p;
    const t = this.tms(v, this.frame);
    const cents = this.voiceCents(v, t);
    const vol = v.vel * p.gain * this.env(v, this.frame);
    let level = clamp(roundEven(15 * vol), 0, 15);
    // a voice that follows (or is) a sweep-unit voice restarts the channel: the chip's sweep unit moved the period behind the shadow registers' back
    const lv = this.lastVoice[ch];
    const retrigger = v.fresh || !this.gate[ch] || (lv !== v && ((lv !== null && lv.p.sweep >= 0) || p.sweep >= 0));
    v.fresh = false;
    this.lastVoice[ch] = v;
    const duty = p.dutySteps ? p.dutySteps[Math.floor(t / 1000 * p.dutyRate) % p.dutySteps.length] : (v.duty >= 0 ? v.duty : p.duty);

    if (ch < 2) {
      const b = ch * 4;
      const freq = 440.0 * Math.pow(2.0, ((60.0 + cents / 100.0) - 69.0) / 12.0);
      let period = roundEven(CPU_HZ / (16.0 * freq) - 1.0);
      if (period > 0x7FF) period = 0x7FF;
      if (period < 8) level = 0;
      period = Math.max(period, 8);
      this.w(b, (duty << 6) | 0x30 | level);
      this.w(b + 1, p.sweep >= 0 ? p.sweep : 0x08);
      const hi = (period >> 8) & 7, lo = period & 0xFF;
      if (retrigger || (p.sweep < 0 && hi !== ((this.curPeriod[ch] >> 8) & 7))) {
        this.w(b + 2, lo);
        this.wf(b + 3, hi | 0xF8);
      } else if (p.sweep < 0) this.w(b + 2, lo);
      this.curPeriod[ch] = period;
    } else if (ch === 2) {
      const freq = 440.0 * Math.pow(2.0, ((60.0 + cents / 100.0) - 69.0) / 12.0);
      let period = roundEven(CPU_HZ / (32.0 * freq) - 1.0);
      period = clamp(period, 2, 0x7FF);
      this.w(8, level > 0 ? 0xFF : 0x80);
      const hi = (period >> 8) & 7, lo = period & 0xFF;
      if (retrigger || hi !== ((this.curPeriod[2] >> 8) & 7)) {
        this.w(0x0A, lo);
        this.wf(0x0B, hi | 0xF8);
      } else this.w(0x0A, lo);
      this.curPeriod[2] = period;
    } else {
      let idx;
      if (p.noiseIdx >= 0) {
        const f = p.noiseMs > 0 ? Math.min(1, t / p.noiseMs) : 1;
        idx = clamp(Math.floor(p.noiseIdx + (p.noiseTo - p.noiseIdx) * f), 0, 15);
      } else {
        const semis = 60.0 + cents / 100.0;
        idx = clamp(15 - Math.floor((semis - 24.0) / 3.0), 0, 15);
      }
      const mode = v.noiseMode >= 0 ? v.noiseMode : p.noiseMode;
      this.w(0x0C, 0x30 | level);
      this.w(0x0E, (mode << 7) | idx);
      if (retrigger) this.wf(0x0F, 0xF8);
      this.curPeriod[3] = idx;
    }
    this.gate[ch] = true;
    this.curLevel[ch] = level;
    this.curNote[ch] = v.note;
  }

  silence(ch) {
    // the plugin's Silence: the channel's level to 0, the gate closed. A pulse keeps the duty of the voice that just left.
    if (ch < 2) this.w(ch * 4, (this.lastDuty(ch) << 6) | 0x30);
    else if (ch === 2) this.w(8, 0x80);
    else this.w(0x0C, 0x30);
  }
  lastDuty(ch) { return (this.chip.shadow[ch * 4] >> 6) & 3; }

  // One control tick: finished voices go, every channel is rewritten from its owner.
  tick() {
    for (let c = 0; c < 4; c++) {
      const st = this.stacks[c];
      for (let i = st.length - 1; i >= 0; i--) {
        const v = st[i];
        if (!v.dead && this.finished(v, this.frame)) { v.dead = true; this.voices.delete(v.id); }
      }
      this.applyChannel(c);
    }
  }

  // ---- rendering ----
  renderChip(out, off, n) {
    if (this.trace) {
      const last = this.trace[this.trace.length - 1];
      if (last && last[0] === 1) last[1] += n; else this.trace.push([1, n]);
    }
    this.chip.render(out, off, n);
  }

  // Fills out[off .. off+n) with mono samples.
  render(out, off, n) {
    const end = off + n;
    let o = off;
    while (o < end) {
      // events due now, then the control tick if it is due
      while (this.events.length && this.events[0].frame <= this.frame) this.apply(this.events.shift());
      if (this.frame >= this.nextTick) { this.tick(); this.nextTick = this.frame - (this.frame % CHUNK) + CHUNK; }
      let seg = Math.min(end - o, this.nextTick - this.frame);
      if (this.events.length) seg = Math.min(seg, Math.max(1, this.events[0].frame - this.frame));
      this.renderChip(out, o, seg);
      if (this.volume !== 1) { const g = Math.fround(this.volume); for (let i = o; i < o + seg; i++) out[i] = Math.fround(out[i] * g); }
      o += seg; this.frame += seg;
    }
    let pk = this.peak;
    for (let i = off; i < end; i++) { const a = out[i] < 0 ? -out[i] : out[i]; if (a > pk) pk = a; }
    this.peak = pk;
  }

  // What the four channels are doing (for meters).
  activity() {
    const a = [];
    for (let c = 0; c < 4; c++) a.push({ active: this.gate[c], level: this.curLevel[c], note: this.curNote[c], voices: this.stacks[c].length });
    return a;
  }
}
