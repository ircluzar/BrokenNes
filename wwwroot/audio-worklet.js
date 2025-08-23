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
    // Rubberband state: fractional read pointer and simple P-controller (varispeed)
    this._rFrac = 0; // initialized on first process using ctrl[0]
    this._rate = 1.0; // instantaneous playback rate multiplier
    this._externalRate = 1.0; // target varispeed factor sent from main thread (links pitch to emu speed)
    this._kp = 0.06; // proportional gain for fullness error -> rate delta (tuned for stability)
    // Relative clamps around externalRate to allow audible speed/pitch coupling while avoiding runaway
    this._minRel = 0.90; // -10%
    this._maxRel = 1.10; // +10%
  // De-click envelope for underruns/returns
  const sr = (typeof sampleRate === 'number' && isFinite(sampleRate)) ? sampleRate : 44100;
  const atkMs = 2.0, relMs = 4.0;
  this._attackCoef = 1 - Math.exp(-1 / (sr * (atkMs/1000)));
  this._releaseCoef = 1 - Math.exp(-1 / (sr * (relMs/1000)));
  this._env = 1.0;
    // Control messages from main thread
    try {
      this.port.onmessage = (e) => {
        const data = e?.data; if(!data) return;
        if (data.type === 'rate') {
          let v = Number(data.value);
          if (!isFinite(v) || v <= 0) v = 1.0;
          this._externalRate = Math.max(0.25, Math.min(4.0, v));
        } else if (data.type === 'bounds') {
          const minRel = Number(data.minRel); const maxRel = Number(data.maxRel);
          if (isFinite(minRel) && minRel > 0 && minRel < 1.0) this._minRel = minRel;
          if (isFinite(maxRel) && maxRel > 1.0 && maxRel <= 2.0) this._maxRel = maxRel;
        } else if (data.type === 'kp') {
          const kp = Number(data.value); if (isFinite(kp) && kp > 0 && kp <= 0.5) this._kp = kp;
        }
      };
    } catch {}
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
  const fullnessCorr = 1.0 + (this._kp * err * 10.0); // adjust read speed based on fullness
  let desiredRate = this._externalRate * fullnessCorr;
  // Smoothly approach desired rate and clamp around external rate
  const minAbs = this._externalRate * this._minRel;
  const maxAbs = this._externalRate * this._maxRel;
  this._rate = Math.max(minAbs, Math.min(maxAbs, 0.6 * this._rate + 0.4 * desiredRate));

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
