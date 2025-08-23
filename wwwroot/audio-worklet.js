// audio-worklet.js - AudioWorkletProcessor pulling from SharedArrayBuffer ring filled by main thread (NES emulator)
// Processor options: { sab: SharedArrayBuffer(Float32 samples), ctrl: SharedArrayBuffer(Int32 indices) }
// ctrl layout: [readIndex, writeIndex, capacity, droppedSamples]
class NesRingProcessor extends AudioWorkletProcessor {
  constructor(options){
    super();
    const { sab, ctrl } = options.processorOptions || {};
    this._buf = sab ? new Float32Array(sab) : new Float32Array(1);
    this._ctrl = ctrl ? new Int32Array(ctrl) : new Int32Array(4);
    if(this._ctrl.length < 4){
      const tmp = new Int32Array(4);
      tmp.set(this._ctrl.subarray(0, this._ctrl.length));
      this._ctrl = tmp;
    }
    // Rubberband state: fractional read pointer and simple P-controller
    this._rFrac = 0; // initialized on first process using ctrl[0]
    this._rate = 1.0; // playback rate multiplier (1.0 = normal)
  this._kp = 0.04; // proportional gain for fullness error -> rate delta (lower for stability)
  this._minRate = 0.985; // tighter bounds to avoid timbre shift
  this._maxRate = 1.015;
  // De-click envelope for underruns/returns
  const sr = (typeof sampleRate === 'number' && isFinite(sampleRate)) ? sampleRate : 44100;
  const atkMs = 2.0, relMs = 4.0;
  this._attackCoef = 1 - Math.exp(-1 / (sr * (atkMs/1000)));
  this._releaseCoef = 1 - Math.exp(-1 / (sr * (relMs/1000)));
  this._env = 1.0;
  }
  process(inputs, outputs){
    const outChannels = outputs[0];
    if(!outChannels || outChannels.length===0) return true;
    const out = outChannels[0]; // mono
    const buf = this._buf; const ctrl = this._ctrl;
    // Initialize fractional pointer on first run
    let r = Atomics.load(ctrl,0);
    if(this._rFrac === 0) this._rFrac = r;
    let w = Atomics.load(ctrl,1);
    const cap = buf.length;
    // Compute available based on integer read index as floor of rFrac
  const rInt = Math.floor(this._rFrac) % cap;
    let available = w >= rInt ? (w - rInt) : (cap - rInt + w);
    // Target fullness ~ 40% of capacity for headroom both ways
    const target = Math.max(64, Math.floor(cap * 0.40));
    const err = (available - target) / cap; // normalize
    const desiredRate = 1.0 + (this._kp * err * 10.0); // scale error effect
    // Smoothly approach desired rate to avoid zippering
    this._rate = Math.max(this._minRate, Math.min(this._maxRate, 0.6 * this._rate + 0.4 * desiredRate));

    // Read with linear interpolation at variable rate
    let rFrac = this._rFrac;
    for(let i=0;i<out.length;i++){
      // Recompute available occasionally (cheap integer bound)
      const rBase = Math.floor(rFrac);
      const availNow = w >= rBase ? (w - rBase) : (cap - rBase + w);
      let sample;
      if(availNow <= 0){
        sample = 0;
        // Nudge pointer slowly to avoid lock if producer stalls
        rFrac = rBase;
      } else if (availNow === 1) {
        // Not enough for interpolation; output nearest sample
        const idx = rBase >= cap ? 0 : rBase;
        sample = buf[idx];
        rFrac += this._rate;
      } else {
        // Linear interpolate between base and next sample (wrap-safe)
        const idx0 = rBase >= cap ? 0 : rBase;
        const idx1 = (idx0 + 1) === cap ? 0 : (idx0 + 1);
        const t = rFrac - rBase;
        const s0 = buf[idx0];
        const s1 = buf[idx1];
        sample = s0 + (s1 - s0) * t;
        rFrac += this._rate;
      }
      // De-click envelope apply
      const target = (availNow > 0) ? 1.0 : 0.0;
      const coef = target > this._env ? this._attackCoef : this._releaseCoef;
      this._env += (target - this._env) * coef;
      out[i] = sample * this._env;
      // Wrap fractional pointer
      if(rFrac >= cap) rFrac -= cap;
      if(rFrac < 0) rFrac += cap;
    }
    this._rFrac = rFrac;
    // Publish integer read index for producer fullness checks
    Atomics.store(ctrl,0, Math.floor(this._rFrac) % cap);
    return true;
  }
}
registerProcessor('nes-ring-proc', NesRingProcessor);
