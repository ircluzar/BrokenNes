// ---- api: the page-facing side ---------------------------------------------------------------------------------------------------
//
// BrokenNesNanoDOS.create(options) -> Promise<Synth>. The sound is made in an AudioWorklet (the engine in chip.js / engine.js / song.js,
// shipped inside this file and loaded from a Blob URL; {workletUrl} loads nano-dos-worklet.js instead where a Content-Security-Policy forbids
// blob: scripts). No fallback: a browser without AudioWorklet gets an Error that says so.

const VERSION = '0.1.0';
const PROCESSOR_NAME = 'brokennes-nano-dos';

//#worklet-begin
function workletMain(kit, PROCESSOR_NAME) {
  class NanoDosProcessor extends AudioWorkletProcessor {
    constructor(options) {
      super();
      const po = (options && options.processorOptions) || {};
      this.divide = po.divide || 1;
      this.mono = !!po.mono;
      this.rate = sampleRate / this.divide;
      this.engine = new kit.Engine(this.rate, { volume: po.volume === undefined ? 1 : po.volume, presets: po.presets });
      this.song = null;
      this.base = null; this.expect = -1; this.pending = [];
      this.report = false; this.quanta = 0;
      this.bufL = new Float32Array(128); this.bufR = new Float32Array(128);
      this.port.onmessage = (e) => this.onMessage(e.data);
    }
    toEngineFrame(at) { return at === null || at === undefined ? this.engine.frame : Math.max(this.engine.frame, Math.round(at / this.divide) - this.base); }
    onMessage(m) {
      try {
        if (this.base === null && m.at !== null && m.at !== undefined) { this.pending.push(m); return; }
        this.handle(m);
      } catch (err) { this.port.postMessage({ t: 'error', message: String(err && err.message || err) }); }
    }
    handle(m) {
      const e = this.engine, f = this.toEngineFrame(m.at);
      switch (m.t) {
        case 'on': e.noteOn(f, m.id, m.note, m.vel, e.resolvePreset(m.preset), m.o || {}); break;
        case 'off': e.noteOff(f, m.ref); break;
        case 'bend': e.bendVoice(f, m.ref, m.cents); break;
        case 'alloff': e.allOff(f); break;
        case 'kill': e.kill(f); if (this.song) this.song.seek(this.song.pos / this.rate); break;
        case 'param': e.setParam(f, m.name, m.value); break;
        case 'addPreset': e.addPreset(m.name, m.def); break;
        case 'report': this.report = !!m.on; e.meter = this.report; break;
        case 'song': this.song = m.song ? new kit.SongPlayer(this.rate, m.song, m.o) : null; break;
        case 'songctl': {
          const s = this.song; if (!s) break;
          if (m.op === 'play') { if (s.ended) s.seek(0); s.playing = true; }
          else if (m.op === 'pause') { s.playing = false; for (const p of s.parts) p.engine.allOff(p.engine.frame); }
          else if (m.op === 'stop') { s.playing = false; s.seek(0); }
          else if (m.op === 'seek') s.seek(m.v);
          else if (m.op === 'tempo') s.tempo = m.v;
          else if (m.op === 'volume') s.volume = m.v;
          else if (m.op === 'loop') { s.loop = m.v.on; if (m.v.start !== undefined) s.loopStart = m.v.start; if (m.v.end !== undefined) s.loopEnd = m.v.end; }
          break;
        }
        case 'ping': this.port.postMessage({ t: 'pong', n: m.n }); break;
      }
    }
    process(inputs, outputs) {
      const out = outputs[0], L = out[0], R = this.mono ? null : out[1], n = L.length / this.divide;
      if (this.expect !== currentFrame) this.base = Math.round(currentFrame / this.divide) - this.engine.frame;
      if (this.pending.length) { const p = this.pending; this.pending = []; for (const m of p) this.onMessage(m); }
      const d = this.divide, bl = d === 1 ? L : this.bufL, br = d === 1 ? R : (R ? this.bufR : null);
      this.engine.render(bl, br, 0, n);
      const s = this.song;
      if (s) {
        const was = s.playing; s.render(bl, br, 0, n);
        if (was && s.ended) this.port.postMessage({ t: 'ended' });
      }
      if (d !== 1) for (let i = 0; i < L.length; i++) { L[i] = bl[(i / d) | 0]; if (R) R[i] = br[(i / d) | 0]; }
      this.expect = currentFrame + L.length;
      if (this.report && ++this.quanta % 8 === 0) {
        this.port.postMessage({ t: 'activity', channels: this.engine.activity(), peak: this.engine.peak, song: s ? { position: s.pos / this.rate, playing: s.playing } : null });
        this.engine.peak = 0;
      }
      return true;
    }
  }
  registerProcessor(PROCESSOR_NAME, NanoDosProcessor);
}
//#worklet-end

const kit = engineKit();

function workletSource() {
  return '(' + workletMain.toString() + ')((' + engineKit.toString() + ')(), ' + JSON.stringify(PROCESSOR_NAME) + ');\n';
}

function fail(code, message) { const e = new Error(message); e.code = code; return e; }

function support() {
  if (typeof window === 'undefined' || typeof AudioContext === 'undefined' && typeof webkitAudioContext === 'undefined') return { ok: false, reason: 'no Web Audio API in this environment' };
  if (typeof AudioWorkletNode === 'undefined') return { ok: false, reason: 'this browser has no AudioWorklet (Nano DOS needs it; there is no fallback)' };
  if (window.isSecureContext === false) return { ok: false, reason: 'AudioWorklet only works on a secure page: https:// or http://localhost' };
  if (window.location && window.location.protocol === 'file:') return { ok: true, reason: '', warning: 'file:// pages: Chromium refuses AudioWorklet modules; serve the page over http(s) or localhost. renderOffline() and toAudioBuffer() work anyway.' };
  return { ok: true, reason: '' };
}

const NOTE_OFFSETS = { C: 0, D: 2, E: 4, F: 5, G: 7, A: 9, B: 11 };
function noteNumber(n) {
  if (typeof n === 'number') { if (!isFinite(n)) throw new RangeError('note must be a finite number'); return n; }
  const m = /^([A-Ga-g])([#b]?)(-?\d+)$/.exec(String(n).trim());
  if (!m) throw new RangeError(`not a note: ${n}`);
  return NOTE_OFFSETS[m[1].toUpperCase()] + (m[2] === '#' ? 1 : m[2] === 'b' ? -1 : 0) + (Number(m[3]) + 1) * 12;
}

const loaded = new WeakMap();
function loadWorklet(ctx, workletUrl) {
  let p = loaded.get(ctx);
  if (p) return p;
  let url = workletUrl, blobUrl = null;
  if (!url) { blobUrl = URL.createObjectURL(new Blob([workletSource()], { type: 'text/javascript' })); url = blobUrl; }
  p = ctx.audioWorklet.addModule(url).then(() => { if (blobUrl) URL.revokeObjectURL(blobUrl); }, (err) => {
    loaded.delete(ctx);
    const onFile = typeof location !== 'undefined' && location.protocol === 'file:';
    throw fail('worklet', 'could not load the Nano DOS worklet: ' + (err && err.message || err) +
      (onFile ? ' (this is a file:// page: Chromium refuses AudioWorklet modules there; serve it, even from http://localhost; renderOffline() and toAudioBuffer() do work from file://)' :
        workletUrl ? '' : ' (if the page\'s Content-Security-Policy forbids blob: scripts, pass {workletUrl: \'nano-dos-worklet.js\'})'));
  });
  loaded.set(ctx, p);
  return p;
}

function describePresets(table) {
  return Object.keys(table).map((k, i) => { const p = table[k]; return { name: k, label: p.label, kind: kit.KIND_NAMES[p.kind], desc: p.desc, note: p.note, program: i < kit.PRESET_NAMES.length && kit.PRESET_NAMES[i] === k ? i : -1 }; });
}

const OPT_KEYS = ['channel', 'pan', 'bend', 'duration'];
function noteOpts(src, kind) {
  const o = {};
  for (const k of OPT_KEYS) if (src[k] !== undefined) o[k] = src[k];
  if (o.pan !== undefined) o.pan = kit.panValue(o.pan);
  if (o.duration !== undefined && !(o.duration >= 0 && isFinite(o.duration))) throw new RangeError('duration must be a number of seconds');
  if (o.bend !== undefined && !isFinite(o.bend)) throw new RangeError('bend must be a number of cents');
  if (o.channel !== undefined && !kit.KIND_CHANNELS[kind].includes(o.channel)) throw new RangeError(`a ${kit.KIND_NAMES[kind]} preset cannot play on channel ${o.channel}`);
  return o;
}

class NoteHandle {
  constructor(synth, id, note) { this.synth = synth; this.id = id; this.note = note; }
  off(opts = {}) { this.synth.noteOff(this, opts); }
  bend(cents, opts = {}) { this.synth.bend(this, cents, opts); }
}

// The song player of a synth (synth.loadMidi): play / pause / stop / seek / loop / tempo / volume, onend
class SongControl {
  constructor(synth, song) { this.synth = synth; this.song = song; this.duration = song.duration; this.channels = song.channels.map(c => c + 1); this.onend = null; this.position = 0; this.playing = false; }
  _ctl(op, v) { this.synth._post({ t: 'songctl', op, v }); }
  play() { this.synth._autoResume(); this.playing = true; this._ctl('play'); return this; }
  pause() { this.playing = false; this._ctl('pause'); return this; }
  stop() { this.playing = false; this._ctl('stop'); return this; }
  seek(seconds) { if (!(seconds >= 0 && isFinite(seconds))) throw new RangeError('seek takes seconds from 0'); this._ctl('seek', seconds); return this; }
  loop(on = true, start, end) {
    if (start !== undefined && !(start >= 0)) throw new RangeError('loop start must be seconds from 0');
    if (end !== undefined && !(end > (start ?? 0))) throw new RangeError('loop end must be after its start');
    this._ctl('loop', { on: !!on, start, end }); return this;
  }
  tempo(x) { if (!(x >= 0.05 && x <= 8)) throw new RangeError('tempo is a factor from 0.05 to 8'); this._ctl('tempo', x); return this; }
  volume(v) { if (!(v >= 0 && v <= 8)) throw new RangeError('volume must be from 0 to 8'); this._ctl('volume', v); return this; }
  unload() { this.synth._post({ t: 'song', song: null }); if (this.synth.song === this) this.synth.song = null; }
}

function toSong(data) {
  if (data && data.events && data.channels) return data;
  if (data instanceof ArrayBuffer || ArrayBuffer.isView(data)) return kit.parseMidi(data);
  throw new TypeError('a song is an ArrayBuffer / Uint8Array of a .mid file (or BrokenNesNanoDOS.parseMidi\'s result)');
}
function songOpts(opts) {
  const o = { chips: opts.chips ?? 'channel', volume: opts.volume ?? 1, loop: !!opts.loop, loopStart: opts.loopStart, loopEnd: opts.loopEnd, presets: opts.presets };
  if (opts.sidecar) o.sidecar = typeof opts.sidecar === 'string' ? JSON.parse(opts.sidecar) : opts.sidecar;
  return o;
}

class Synth {
  constructor(ctx, node, ownsContext, options) {
    this.context = ctx; this.node = node; this._own = ownsContext;
    this._table = {};
    for (const k of Object.keys(kit.PRESETS)) this._table[k] = kit.normalizePreset(kit.PRESETS[k], k);
    if (options.presets) for (const k of Object.keys(options.presets)) this._table[k] = kit.normalizePreset(options.presets[k], k);
    this._default = options.preset ?? 'piano';
    if (!this._table[this._default]) throw new RangeError(`unknown preset '${this._default}'`);
    this._volume = options.volume ?? 1.5;
    this._id = 1; this._cb = null; this._disposed = false; this._pings = new Map(); this._ping = 0;
    this.onerror = null; this.song = null;
    node.port.onmessage = (e) => {
      const m = e.data;
      if (m.t === 'activity') { if (m.song && this.song) { this.song.position = m.song.position; this.song.playing = m.song.playing; } if (this._cb) this._cb({ channels: m.channels, peak: m.peak, song: m.song }); }
      else if (m.t === 'ended') { if (this.song) { this.song.playing = false; if (this.song.onend) this.song.onend(); } }
      else if (m.t === 'pong') { const w = this._pings.get(m.n); if (w) { this._pings.delete(m.n); w(); } }
      else if (m.t === 'error') { if (this.onerror) this.onerror(new Error(m.message)); else console.error('BrokenNes Nano DOS: ' + m.message); }
    };
  }

  get sampleRate() { return this.context.sampleRate; }
  get volume() { return this._volume; }
  set volume(v) { this.setParam('volume', v); }
  get presets() { return describePresets(this._table); }
  /** About 40 times a second {channels: [{active, level, note, voices}] x10 (OPL2 voices 1-9, the speaker), peak, song}. Nothing is sent while it is null. */
  get onactivity() { return this._cb; }
  set onactivity(fn) { this._cb = fn || null; this._post({ t: 'report', on: !!fn }); }

  _post(m) { if (!this._disposed) this.node.port.postMessage(m); }
  _at(o) {
    if (o && o.time !== undefined) { if (!isFinite(o.time)) throw new RangeError('time must be AudioContext seconds'); return Math.round(o.time * this.context.sampleRate); }
    if (o && o.delay) { if (!(o.delay >= 0)) throw new RangeError('delay must be seconds from now'); return Math.round((this.context.currentTime + o.delay) * this.context.sampleRate); }
    return null;
  }

  /** Starts a note; returns {id, off(), bend(cents)}. opts: {preset, time, delay, duration, channel 0..9 (9 = the speaker), bend} */
  noteOn(note, velocity = 1, opts = {}) {
    if (this._disposed) throw fail('disposed', 'this synth was disposed');
    const n = noteNumber(note);
    if (!(velocity >= 0 && velocity <= 1)) throw new RangeError('velocity must be from 0 to 1');
    const preset = opts.preset ?? this._default;
    const norm = typeof preset === 'string' ? this._table[preset] : kit.normalizePreset(preset);
    if (!norm) throw new RangeError(`unknown preset '${preset}'`);
    const o = noteOpts(opts, norm.kind);
    const at = this._at(opts), id = this._id++;
    this._autoResume();
    this._post({ t: 'on', id, note: n, vel: velocity, preset, o, at });
    return new NoteHandle(this, id, n);
  }
  noteOff(ref, opts = {}) { this._post({ t: 'off', ref: ref && typeof ref === 'object' ? ref.id : { note: noteNumber(ref) }, at: this._at(opts) }); }
  play(preset, opts = {}) {
    const norm = this._table[preset];
    if (!norm) throw new RangeError(`unknown preset '${preset}'`);
    return this.noteOn(opts.note ?? norm.note, opts.velocity ?? 1, { ...opts, preset });
  }
  bend(handle, cents, opts = {}) { if (!isFinite(cents)) throw new RangeError('cents must be a number'); this._post({ t: 'bend', ref: handle.id, cents, at: this._at(opts) }); }
  allOff(opts = {}) { this._post({ t: 'alloff', at: this._at(opts) }); }
  panic(opts = {}) { this._post({ t: 'kill', at: this._at(opts) }); }

  /** 'volume' 0..8, 'pitchBend' / 'tune' cents, 'preset' (the default) (the PC is mono: no pan) */
  setParam(name, value, opts = {}) {
    switch (name) {
      case 'volume': if (!(value >= 0 && value <= 8)) throw new RangeError('volume must be from 0 to 8'); this._volume = value; this._post({ t: 'param', name, value, at: this._at(opts) }); break;
      case 'pitchBend': case 'tune': if (!isFinite(value)) throw new RangeError(name + ' must be a number of cents'); this._post({ t: 'param', name, value, at: this._at(opts) }); break;
      case 'preset': if (!this._table[value]) throw new RangeError(`unknown preset '${value}'`); this._default = value; break;
      default: throw new RangeError(`unknown parameter '${name}'`);
    }
  }

  addPreset(name, def) { this._table[name] = kit.normalizePreset(def, name); this._post({ t: 'addPreset', name, def }); }

  /** Loads a Standard MIDI File (ArrayBuffer) for playing: returns its player ({play, pause, stop, seek, loop, tempo, volume, onend, duration, position}).
   *  opts: {chips: 'channel' (default: one chip per MIDI channel, as FL plays one plugin per channel) | 'shared' (one chip, 10 voices in all), sidecar (the plugin's export), loop, loopStart, loopEnd, volume, presets} */
  loadMidi(data, opts = {}) {
    const song = toSong(data), o = songOpts(opts);
    new kit.SongPlayer(44100, song, o);          // the same checks the audio thread makes, here, so a bad sidecar throws at the call
    this._post({ t: 'song', song, o });
    this.song = new SongControl(this, song);
    if (opts.play) this.song.play();
    return this.song;
  }

  async connectMidi(opts = {}) {
    if (typeof navigator === 'undefined' || !navigator.requestMIDIAccess && !opts.access) throw fail('unsupported', 'this browser has no Web MIDI');
    const access = opts.access || await navigator.requestMIDIAccess();
    const handle = this._midiHandler(opts), wired = new Map();
    const attach = (input) => {
      if (wired.has(input.id) || (opts.input && opts.input !== input.id && opts.input !== input.name)) return;
      const fn = (e) => handle(e.data); input.addEventListener('midimessage', fn); wired.set(input.id, [input, fn]);
    };
    for (const input of access.inputs.values()) attach(input);
    const onState = () => { for (const input of access.inputs.values()) attach(input); };
    access.addEventListener('statechange', onState);
    return { close: () => { access.removeEventListener('statechange', onState); for (const [input, fn] of wired.values()) input.removeEventListener('midimessage', fn); wired.clear(); handle.release(); } };
  }

  // live MIDI messages -> notes (program change picks presets by the shared program numbers, channel 10 plays GM drums)
  _midiHandler(opts = {}) {
    const held = new Map(), bendRange = opts.bendRange ?? 2, drums = opts.drums !== false;
    const f = (data, time) => {
      if (!data || data.length < 1) return;
      const status = data[0], type = status & 0xF0, ch = status & 15;
      if (status >= 0xF0) return;
      if (opts.channel !== undefined && opts.channel !== 'omni' && ch !== opts.channel) return;
      const at = time === undefined ? {} : { time };
      if (type === 0x90 || type === 0x80) {
        const key = ch + ':' + data[1], old = held.get(key);
        if (type === 0x90 && data[2] > 0) {
          if (old) old.off(at);
          if (ch === 9 && drums) { const name = kit.GM_DRUMS[data[1]]; if (name && this._table[name]) this.play(name, { ...at, velocity: data[2] / 127 }); return; }
          held.set(key, this.noteOn(data[1], data[2] / 127, at));
        } else if (old) { old.off(at); held.delete(key); }
      } else if (type === 0xE0) this.setParam('pitchBend', ((data[2] << 7 | data[1]) - 8192) / 8192 * bendRange * 100, at);
      else if (type === 0xC0 && opts.program !== false) this.setParam('preset', kit.PRESET_NAMES[data[1] % kit.PRESET_NAMES.length]);
      else if (type === 0xB0 && (data[1] === 123 || data[1] === 120)) { if (data[1] === 120) this.panic(at); else this.allOff(at); held.clear(); }
    };
    f.release = () => { for (const h of held.values()) h.off(); held.clear(); };
    return f;
  }

  connect(destination, output, input) { return this.node.connect(destination, output, input); }
  disconnect(...args) { this.node.disconnect(...args); }
  sync() {
    if (this._disposed) return Promise.resolve();
    return new Promise((resolve) => { const n = ++this._ping; this._pings.set(n, resolve); this.node.port.postMessage({ t: 'ping', n }); });
  }
  resume() { return this.context.state === 'running' ? Promise.resolve() : this.context.resume(); }
  _autoResume() { if (this.context.state === 'suspended') this.context.resume().catch(() => { }); }
  async dispose() {
    if (this._disposed) return;
    this._disposed = true;
    try { this.node.port.postMessage({ t: 'kill', at: null }); this.node.disconnect(); this.node.port.close(); } catch (e) { }
    if (this._own) await this.context.close();
  }
}

/** options: {context, destination (false: unconnected), volume (default 1.5), preset, presets, mono (one output channel), divide (1, 2 or 4: the chip runs at
 *  the context's rate / divide, each sample held: 22 kHz on a 44.1 kHz context costs half), sampleRate, latencyHint, workletUrl} */
async function create(options = {}) {
  const s = support();
  if (!s.ok) throw fail('unsupported', 'BrokenNes Nano DOS cannot run here: ' + s.reason);
  const divide = options.divide ?? 1;
  if (divide !== 1 && divide !== 2 && divide !== 4) throw new RangeError('divide is 1, 2 or 4');
  if (options.volume !== undefined && !(options.volume >= 0 && options.volume <= 8)) throw new RangeError('volume must be from 0 to 8');
  const own = !options.context;
  const AC = typeof AudioContext !== 'undefined' ? AudioContext : webkitAudioContext;
  const ctx = options.context || new AC({ latencyHint: options.latencyHint || 'interactive', ...(options.sampleRate ? { sampleRate: options.sampleRate } : {}) });
  try {
    await loadWorklet(ctx, options.workletUrl);
    const mono = !!options.mono;
    const node = new AudioWorkletNode(ctx, PROCESSOR_NAME, {
      numberOfInputs: 0, numberOfOutputs: 1, outputChannelCount: [mono ? 1 : 2],
      processorOptions: { volume: options.volume ?? 1.5, presets: options.presets, mono, divide },
    });
    const synth = new Synth(ctx, node, own, options);
    if (options.destination !== false) node.connect(options.destination || ctx.destination);
    return synth;
  } catch (e) {
    if (own) ctx.close().catch(() => { });
    throw e;
  }
}

/** Renders note events to samples without any audio device: the same engine, bit for bit. Returns a Float32Array (mono), or {left, right} with opts.stereo.
 *  events: [{time, type: 'on', note, velocity, preset, id?, duration?, channel?, pan?, bend?} | {time, type: 'off', id | note} | 'bend' | 'alloff' | 'kill' | {type: 'param', name, value}]
 *  opts: {sampleRate (default 44100), volume (default 1), presets, stereo, trace} */
function renderOffline(events, seconds, opts = {}) {
  const sr = opts.sampleRate || 44100;
  if (!(seconds >= 0 && isFinite(seconds))) throw new RangeError('seconds must be a number from 0');
  const eng = new kit.Engine(sr, { volume: opts.volume ?? 1, presets: opts.presets, trace: !!opts.trace });
  let nextId = 1;
  for (const ev of events) {
    const f = Math.round((ev.time || 0) * sr);
    switch (ev.type) {
      case 'on': { const p = eng.resolvePreset(ev.preset ?? 'piano'); eng.noteOn(f, ev.id ?? nextId++, noteNumber(ev.note), ev.velocity ?? 1, p, noteOpts(ev, p.kind)); break; }
      case 'off': eng.noteOff(f, ev.id !== undefined ? ev.id : { note: noteNumber(ev.note) }); break;
      case 'bend': eng.bendVoice(f, ev.id, ev.cents); break;
      case 'alloff': eng.allOff(f); break;
      case 'kill': eng.kill(f); break;
      case 'param': eng.setParam(f, ev.name, ev.name === 'pan' ? ev.value.map(kit.panValue) : ev.value); break;
      default: throw new RangeError(`unknown event type '${ev.type}'`);
    }
  }
  const total = Math.round(seconds * sr);
  const L = new Float32Array(total), R = opts.stereo ? new Float32Array(total) : null;
  for (let o = 0; o < total; o += 128) eng.render(L, R, o, Math.min(128, total - o));
  const out = R ? { left: L, right: R } : L;
  if (opts.trace) out.trace = eng.trace;
  return out;
}

/** Renders a .mid (ArrayBuffer, or parseMidi's result) offline: {left, right} (or mono with opts.mono). opts: as loadMidi, plus sampleRate and seconds (default: the song + 1 s). */
function renderMidi(data, opts = {}) {
  const song = toSong(data), sr = opts.sampleRate || 44100;
  const pl = new kit.SongPlayer(sr, song, songOpts(opts));
  pl.playing = true;
  const total = Math.round((opts.seconds ?? song.duration + 1) * sr);
  const L = new Float32Array(total), R = opts.mono ? null : new Float32Array(total);
  for (let o = 0; o < total; o += 128) pl.render(L, R, o, Math.min(128, total - o));
  return R ? { left: L, right: R } : L;
}

/** An AudioBuffer of `ctx` (stereo; mono with opts.mono) from note events: bake a sound effect once, play it with an AudioBufferSourceNode. */
function toAudioBuffer(ctx, events, seconds, opts = {}) {
  const r = renderOffline(events, seconds, { ...opts, sampleRate: ctx.sampleRate, stereo: !opts.mono });
  const buf = ctx.createBuffer(opts.mono ? 1 : 2, (r.left ?? r).length, ctx.sampleRate);
  if (opts.mono) buf.getChannelData(0).set(r); else { buf.getChannelData(0).set(r.left); buf.getChannelData(1).set(r.right); }
  return buf;
}

const api = {
  version: VERSION,
  create, support, noteNumber, renderOffline, renderMidi, toAudioBuffer,
  parseMidi: (data) => kit.parseMidi(data),
  /** The built-in presets in program order: [{name, label, kind, desc, note, program}] */
  presets: describePresets(Object.fromEntries(kit.PRESET_NAMES.map(k => [k, kit.normalizePreset(kit.PRESETS[k], k)]))),
  _kit: kit,
};
