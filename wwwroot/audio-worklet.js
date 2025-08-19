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
  }
  process(inputs, outputs){
    const outChannels = outputs[0];
    if(!outChannels || outChannels.length===0) return true;
    const out = outChannels[0]; // mono
    const buf = this._buf; const ctrl = this._ctrl;
    let r = Atomics.load(ctrl,0);
    let w = Atomics.load(ctrl,1);
    const cap = buf.length;
    let available = w >= r ? (w - r) : (cap - r + w);
    for(let i=0;i<out.length;i++){
      if(available === 0){
        out[i] = 0; // underrun -> silence
      } else {
        out[i] = buf[r];
        r++; if(r===cap) r=0;
        available--;
      }
    }
    Atomics.store(ctrl,0,r);
    return true;
  }
}
registerProcessor('nes-ring-proc', NesRingProcessor);
