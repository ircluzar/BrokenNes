// ---- api: the page-facing side ---------------------------------------------------------------------------------------------------
//
// BrokenNesNano.create(options) -> Promise<Synth>. The sound is made in an AudioWorklet (the engine in engine.js / chip.js, shipped inside this
// file and loaded from a Blob URL, so the page needs no second file and no server; pass {workletUrl} to load nano-worklet.js instead when a
// Content-Security-Policy forbids blob: scripts). There is no fallback: a browser without AudioWorklet gets an Error that says so.

const VERSION = '0.1.0';
const PROCESSOR_NAME = 'brokennes-nano';

// ---- the AudioWorkletProcessor (runs in the audio thread; kit is the engine) ----
//#worklet-begin
function workletMain(kit, PROCESSOR_NAME) {
  class NanoProcessor extends AudioWorkletProcessor {
    constructor(options) {
      super();
      const po = (options && options.processorOptions) || {};
      this.engine = new kit.Engine(sampleRate, { volume: po.volume === undefined ? 1 : po.volume, presets: po.presets });
      this.base = null;                  // ctx frame - engine frame
      this.expect = -1;
      this.pending = [];                 // timed events that arrived before the first block
      this.report = false;
      this.quanta = 0;
      this.port.onmessage = (e) => this.onMessage(e.data);
    }

    toEngineFrame(at) {
      return at === null || at === undefined ? this.engine.frame : Math.max(this.engine.frame, Math.round(at) - this.base);
    }

    onMessage(m) {
      try {
        if (this.base === null && m.at !== null && m.at !== undefined) { this.pending.push(m); return; }
        this.handle(m);
      } catch (err) {
        this.port.postMessage({ t: 'error', message: String(err && err.message || err) });
      }
    }

    handle(m) {
      const e = this.engine, f = this.toEngineFrame(m.at);
      switch (m.t) {
        case 'on': e.noteOn(f, m.id, m.note, m.vel, e.resolvePreset(m.preset), m.o || {}); break;
        case 'off': e.noteOff(f, m.ref); break;
        case 'bend': e.bendVoice(f, m.ref, m.cents); break;
        case 'alloff': e.allOff(f); break;
        case 'kill': e.kill(f); break;
        case 'param': e.setParam(f, m.name, m.value); break;
        case 'addPreset': e.addPreset(m.name, m.def); break;
        case 'report': this.report = !!m.on; break;
        case 'ping': this.port.postMessage({ t: 'pong', n: m.n }); break;
      }
    }

    process(inputs, outputs) {
      const out = outputs[0][0];
      if (this.expect !== currentFrame) this.base = currentFrame - this.engine.frame;
      if (this.pending.length) { const p = this.pending; this.pending = []; for (const m of p) this.onMessage(m); }
      this.engine.render(out, 0, out.length);
      for (let c = 1; c < outputs[0].length; c++) outputs[0][c].set(out);
      this.expect = currentFrame + out.length;
      if (this.report && ++this.quanta % 8 === 0) {
        this.port.postMessage({ t: 'activity', channels: this.engine.activity(), peak: this.engine.peak });
        this.engine.peak = 0;
      }
      return true;
    }
  }
  registerProcessor(PROCESSOR_NAME, NanoProcessor);
}
//#worklet-end

const kit = engineKit();

function workletSource() {
  return '(' + workletMain.toString() + ')((' + engineKit.toString() + ')(), ' + JSON.stringify(PROCESSOR_NAME) + ');\n';
}

function fail(code, message) { const e = new Error(message); e.code = code; return e; }

// Is Nano usable in this browser? {ok, reason}
function support() {
  if (typeof window === 'undefined' || typeof AudioContext === 'undefined' && typeof webkitAudioContext === 'undefined') return { ok: false, reason: 'no Web Audio API in this environment' };
  if (typeof AudioWorkletNode === 'undefined') return { ok: false, reason: 'this browser has no AudioWorklet (Nano needs it; there is no fallback)' };
  if (window.isSecureContext === false) return { ok: false, reason: 'AudioWorklet only works on a secure page: https://, http://localhost or file://' };
  // Chromium-based browsers (tested: Edge 155) refuse to load any AudioWorklet module on a file:// page, so create() will reject there
  if (window.location && window.location.protocol === 'file:') return { ok: true, reason: '', warning: 'file:// pages: Chromium refuses AudioWorklet modules; serve the page over http(s) or localhost. Nano.toAudioBuffer() works anyway.' };
  return { ok: true, reason: '' };
}

const NOTE_OFFSETS = { C: 0, D: 2, E: 4, F: 5, G: 7, A: 9, B: 11 };
// 60 / 'C4' / 'c#4' / 'Db3' -> a MIDI note number (middle C = C4 = 60)
function noteNumber(n) {
  if (typeof n === 'number') {
    if (!isFinite(n)) throw new RangeError('note must be a finite number');
    return n;
  }
  const m = /^([A-Ga-g])([#b]?)(-?\d+)$/.exec(String(n).trim());
  if (!m) throw new RangeError(`not a note: ${n}`);
  return NOTE_OFFSETS[m[1].toUpperCase()] + (m[2] === '#' ? 1 : m[2] === 'b' ? -1 : 0) + (Number(m[3]) + 1) * 12;
}

const loaded = new WeakMap();    // AudioContext -> Promise (addModule once per context)
function loadWorklet(ctx, workletUrl) {
  let p = loaded.get(ctx);
  if (p) return p;
  let url = workletUrl, blobUrl = null;
  if (!url) {
    blobUrl = URL.createObjectURL(new Blob([workletSource()], { type: 'text/javascript' }));
    url = blobUrl;
  }
  p = ctx.audioWorklet.addModule(url).then(() => { if (blobUrl) URL.revokeObjectURL(blobUrl); }, (err) => {
    loaded.delete(ctx);
    const onFile = typeof location !== 'undefined' && location.protocol === 'file:';
    throw fail('worklet', 'could not load the Nano worklet: ' + (err && err.message || err) +
      (onFile ? ' (this is a file:// page, and Chromium-based browsers refuse AudioWorklet modules there, from a Blob or a file alike: open the page through a web server, even http://localhost; Nano.toAudioBuffer() and Nano.renderOffline() do work from file://)' :
        workletUrl ? '' : ' (if the page\'s Content-Security-Policy forbids blob: scripts, pass {workletUrl: \'nano-worklet.js\'})'));
  });
  loaded.set(ctx, p);
  return p;
}

function describePresets(table) {
  return Object.keys(table).map(k => { const p = table[k]; return { name: k, label: p.label, kind: kit.KIND_NAMES[p.kind], desc: p.desc, note: p.note }; });
}

class NoteHandle {
  constructor(synth, id, note) { this.synth = synth; this.id = id; this.note = note; }
  /** Releases the note. opts: {time | delay} as for noteOn. */
  off(opts = {}) { this.synth.noteOff(this, opts); }
  /** Bends the note by `cents` from now on. */
  bend(cents, opts = {}) { this.synth.bend(this, cents, opts); }
}

class Synth {
  constructor(ctx, node, ownsContext, options) {
    this.context = ctx;
    this.node = node;
    this._own = ownsContext;
    this._table = {};
    for (const k of Object.keys(kit.PRESETS)) this._table[k] = kit.normalizePreset(kit.PRESETS[k], k);
    if (options.presets) for (const k of Object.keys(options.presets)) this._table[k] = kit.normalizePreset(options.presets[k], k);
    this._default = options.preset ?? 'lead';
    if (!this._table[this._default]) throw new RangeError(`unknown preset '${this._default}'`);
    this._volume = options.volume ?? 1.5;
    this._id = 1;
    this._cb = null;
    this._disposed = false;
    this._pings = new Map();
    this._ping = 0;
    this.onerror = null;
    node.port.onmessage = (e) => {
      const m = e.data;
      if (m.t === 'activity' && this._cb) this._cb({ channels: m.channels, peak: m.peak });
      else if (m.t === 'pong') { const w = this._pings.get(m.n); if (w) { this._pings.delete(m.n); w(); } }
      else if (m.t === 'error') { if (this.onerror) this.onerror(new Error(m.message)); else console.error('BrokenNes Nano: ' + m.message); }
    };
  }

  get sampleRate() { return this.context.sampleRate; }
  get volume() { return this._volume; }
  set volume(v) { this.setParam('volume', v); }
  /** The presets this synth knows: [{name, label, kind, desc, note}] */
  get presets() { return describePresets(this._table); }
  /** Called ~30 times a second with {channels: [{active, level, note}] x4 (pulse 1, pulse 2, triangle, noise), peak}. Set to null to stop. */
  get onactivity() { return this._cb; }
  set onactivity(fn) { this._cb = fn || null; this._post({ t: 'report', on: !!fn }); }

  _post(m) { if (!this._disposed) this.node.port.postMessage(m); }
  _at(o) {
    if (o && o.time !== undefined) return Math.round(o.time * this.context.sampleRate);
    if (o && o.delay) return Math.round((this.context.currentTime + o.delay) * this.context.sampleRate);
    return null;
  }

  /**
   * Starts a note and returns a handle ({id, off(), bend(cents)}). note: MIDI number (60 = middle C) or a name like 'C4', 'F#3'; velocity 0..1.
   * opts: {preset: name | preset object (default: the synth's current preset), time: AudioContext seconds, delay: seconds from now,
   *        duration: seconds until the note ends by itself, channel: 0..3 to pin the voice to a chip channel, duty: 0..3, noiseMode: 0|1, bend: cents}
   * Browsers keep an AudioContext silent until a user gesture: call this from a click or key handler, or await synth.resume() there first.
   */
  noteOn(note, velocity = 1, opts = {}) {
    if (this._disposed) throw fail('disposed', 'this synth was disposed');
    const n = noteNumber(note);
    if (!(velocity >= 0 && velocity <= 1)) throw new RangeError('velocity must be from 0 to 1');
    let preset = opts.preset ?? this._default;
    const norm = typeof preset === 'string' ? this._table[preset] : kit.normalizePreset(preset);
    if (!norm) throw new RangeError(`unknown preset '${preset}'`);
    const o = {};
    for (const k of ['channel', 'duty', 'noiseMode', 'bend', 'duration']) if (opts[k] !== undefined) o[k] = opts[k];
    if (o.channel !== undefined && !kit.KIND_CHANNELS[norm.kind].includes(o.channel)) throw new RangeError(`a ${kit.KIND_NAMES[norm.kind]} preset cannot play on channel ${o.channel}`);
    const id = this._id++;
    this._autoResume();
    this._post({ t: 'on', id, note: n, vel: velocity, preset, o, at: this._at(opts) });
    return new NoteHandle(this, id, n);
  }

  /** Ends a note: pass the handle noteOn returned, or a note number/name (the newest held note with that pitch). */
  noteOff(ref, opts = {}) {
    this._post({ t: 'off', ref: ref && typeof ref === 'object' ? ref.id : { note: noteNumber(ref) }, at: this._at(opts) });
  }

  /** Plays a preset as a one-off sound effect at its own natural pitch: synth.play('coin'). */
  play(preset, opts = {}) {
    const norm = this._table[preset];
    if (!norm) throw new RangeError(`unknown preset '${preset}'`);
    return this.noteOn(opts.note ?? norm.note, opts.velocity ?? 1, { ...opts, preset });
  }

  /** Bends one sounding note by `cents` (handle from noteOn). */
  bend(handle, cents, opts = {}) { this._post({ t: 'bend', ref: handle.id, cents, at: this._at(opts) }); }
  /** Releases every held note (their release tails and one-shot sounds play out). */
  allOff(opts = {}) { this._post({ t: 'alloff', at: this._at(opts) }); }
  /** Stops every sound at once. */
  panic(opts = {}) { this._post({ t: 'kill', at: this._at(opts) }); }

  /** 'volume' (output gain; 1 = the chip's own level, default 1.5), 'pitchBend' (cents, all notes), 'tune' (cents, all notes), 'preset' (the default preset). */
  setParam(name, value) {
    switch (name) {
      case 'volume':
        if (!(value >= 0 && value <= 8)) throw new RangeError('volume must be from 0 to 8');
        this._volume = value; this._post({ t: 'param', name, value }); break;
      case 'pitchBend': case 'tune':
        if (!isFinite(value)) throw new RangeError(name + ' must be a number of cents');
        this._post({ t: 'param', name, value }); break;
      case 'preset':
        if (!this._table[value]) throw new RangeError(`unknown preset '${value}'`);
        this._default = value; break;
      default: throw new RangeError(`unknown parameter '${name}'`);
    }
  }

  /** Adds (or replaces) a preset: synth.addPreset('mine', {kind: 'pulse', duty: 2, a: 5, d: 100, s: 0.7, r: 40}). See the README for every field. */
  addPreset(name, def) {
    this._table[name] = kit.normalizePreset(def, name);
    this._post({ t: 'addPreset', name, def });
  }

  /** Routes the sound somewhere else: synth.connect(myGainNode). By default it is connected to the context's destination. */
  connect(destination, output, input) { return this.node.connect(destination, output, input); }
  disconnect(...args) { this.node.disconnect(...args); }

  /**
   * Resolves once the audio thread has received everything sent so far (notes, presets, parameters). Useful before starting an OfflineAudioContext
   * and in tests; a page playing live never needs it.
   */
  sync() {
    if (this._disposed) return Promise.resolve();
    return new Promise((resolve) => { const n = ++this._ping; this._pings.set(n, resolve); this.node.port.postMessage({ t: 'ping', n }); });
  }

  /** Resumes the AudioContext (call it from a click/keypress handler if the context was created suspended). */
  resume() { return this.context.state === 'running' ? Promise.resolve() : this.context.resume(); }
  _autoResume() { if (this.context.state === 'suspended') this.context.resume().catch(() => { }); }

  /** Stops the synth and releases everything; closes the AudioContext if Nano made it. */
  async dispose() {
    if (this._disposed) return;
    this._disposed = true;
    try { this.node.port.postMessage({ t: 'kill', at: null }); this.node.disconnect(); this.node.port.close(); } catch (e) { }
    if (this._own) await this.context.close();
  }
}

/**
 * Makes a synth. options: {context (an existing AudioContext; otherwise one is made), destination (an AudioNode to connect to, false to leave it unconnected;
 * default: the context's destination), volume (default 1.5), preset (default 'lead'), presets ({name: def}), sampleRate, latencyHint, workletUrl}
 */
async function create(options = {}) {
  const s = support();
  if (!s.ok) throw fail('unsupported', 'BrokenNes Nano cannot run here: ' + s.reason);
  const own = !options.context;
  const AC = typeof AudioContext !== 'undefined' ? AudioContext : webkitAudioContext;
  const ctx = options.context || new AC({ latencyHint: options.latencyHint || 'interactive', ...(options.sampleRate ? { sampleRate: options.sampleRate } : {}) });
  try {
    await loadWorklet(ctx, options.workletUrl);
    const node = new AudioWorkletNode(ctx, PROCESSOR_NAME, {
      numberOfInputs: 0, numberOfOutputs: 1, outputChannelCount: [1],
      processorOptions: { volume: options.volume ?? 1.5, presets: options.presets },
    });
    const synth = new Synth(ctx, node, own, options);
    if (options.destination !== false) node.connect(options.destination || ctx.destination);
    return synth;
  } catch (e) {
    if (own) ctx.close().catch(() => { });
    throw e;
  }
}

// ---- rendering without an AudioContext ----

/**
 * Renders a script of events to samples (a Float32Array) without any audio device: the same engine, bit for bit.
 * events: [{time (seconds), type: 'on', note, velocity, preset, id?, ...opts} | {time, type: 'off', id | note} | {time, type: 'alloff'} | {time, type: 'param', name, value}]
 * opts: {sampleRate (default 44100), volume (default 1 = the chip's own level), presets, trace}
 * Returns the Float32Array; with opts.trace the register trace tools/NanoParity replays is on `result.trace`.
 */
function renderOffline(events, seconds, opts = {}) {
  const sr = opts.sampleRate || 44100;
  const eng = new kit.Engine(sr, { volume: opts.volume ?? 1, presets: opts.presets, trace: !!opts.trace });
  let nextId = 1;
  for (const ev of events) {
    const f = Math.round((ev.time || 0) * sr);
    switch (ev.type) {
      case 'on': {
        const id = ev.id ?? nextId++;
        const o = {};
        for (const k of ['channel', 'duty', 'noiseMode', 'bend', 'duration']) if (ev[k] !== undefined) o[k] = ev[k];
        eng.noteOn(f, id, noteNumber(ev.note), ev.velocity ?? 1, eng.resolvePreset(ev.preset ?? 'lead'), o);
        break;
      }
      case 'off': eng.noteOff(f, ev.id !== undefined ? ev.id : { note: noteNumber(ev.note) }); break;
      case 'bend': eng.bendVoice(f, ev.id, ev.cents); break;
      case 'alloff': eng.allOff(f); break;
      case 'kill': eng.kill(f); break;
      case 'param': eng.setParam(f, ev.name, ev.value); break;
      default: throw new RangeError(`unknown event type '${ev.type}'`);
    }
  }
  const total = Math.round(seconds * sr);
  const out = new Float32Array(total);
  for (let o = 0; o < total; o += 128) eng.render(out, o, Math.min(128, total - o));
  if (opts.trace) { out.trace = eng.trace; out.log = eng.log; out.logPresets = eng.logPresets; }
  return out;
}

/** Renders a script to an AudioBuffer of `ctx` (mono, at the context's rate): bake a sound effect once and play it with an AudioBufferSourceNode. */
function toAudioBuffer(ctx, events, seconds, opts = {}) {
  const data = renderOffline(events, seconds, { ...opts, sampleRate: ctx.sampleRate });
  const buf = ctx.createBuffer(1, data.length, ctx.sampleRate);
  buf.getChannelData(0).set(data);
  return buf;
}

const api = {
  version: VERSION,
  create, support, noteNumber, renderOffline, toAudioBuffer,
  /** The built-in presets: [{name, label, kind, desc, note}] */
  presets: describePresets(Object.fromEntries(Object.keys(kit.PRESETS).map(k => [k, kit.normalizePreset(kit.PRESETS[k], k)]))),
  /** The engine itself, for tools and tests (not a stable interface). */
  _kit: kit,
};
