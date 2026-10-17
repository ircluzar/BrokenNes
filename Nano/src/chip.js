// ---- chip: the NES sound chip, as BrokenNes's FIX core plays it -------------------------------------------------------------
//
// A transcription of Windows/NesEmulator/apus/APU_FIX.cs (the accuracy-target core) plus Core/StreamResampler.cs, driven exactly the way
// Core/NesApuInstrument.cs drives them: the registers $4000-$4017 are written, the chip is stepped 32 CPU cycles at a time, one sample is
// made every 1789773/44100 CPU cycles (integer Bresenham pacing), and the 44.1 kHz stream is resampled to the host's rate with a 4-point
// Hermite interpolator. Every float of the original is a float32 here (Math.fround after each operation), so the output is the same bits.
//
// Differences from the C# that cannot change a sample:
//  * a channel's output level is computed when a sample is made, not on every CPU cycle (the C# recomputes it each cycle and only
//    the value at sample time is ever read);
//  * no CPU/IRQ line and no ring buffer between the chip and the resampler (the C# drains its ring after every 32-cycle step);
//  * the DMC reads its sample bytes from memory the page supplies (Chip.setDmcMemory) instead of a bus.
// tools/NanoParity checks all of this against Core, sample for sample (see Nano/README.md).

const fround = Math.fround;

const LENGTH_TABLE = new Int32Array([10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14, 12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30]);
const NOISE_PERIODS = new Int32Array([4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068]);
const DMC_RATES = new Int32Array([428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 84, 72, 54]);
// duty sequences: 12.5%, 25%, 50%, 75% (25% inverted)
const DUTY_TABLE = new Uint8Array([0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 1, 1, 1, 1, 1]);

const CPU_FREQ = 1789773;           // the sample pacing's CPU rate (NTSC), and the rate pitches are computed against
const NATIVE_RATE = 44100;          // the chip's own sample rate
const STEP_CYCLES = 32;             // NesApuInstrument steps the chip this many CPU cycles at a time

// Mixer tables (float32), the canonical nonlinear NES mix
const PULSE_MIX = new Float32Array(31);
const TND_MIX = new Float32Array(16 * 16 * 128);
(function buildMixTables() {
  const c9588 = fround(95.88), c8128 = fround(8128), c100 = fround(100), c15979 = fround(159.79), one = fround(1);
  for (let sum = 1; sum < 31; sum++) PULSE_MIX[sum] = fround(c9588 / fround(fround(c8128 / sum) + c100));
  let idx = 0;
  for (let t = 0; t < 16; t++) {
    const tf = fround(t / 8227);
    for (let n = 0; n < 16; n++) {
      const nf = fround(n / 12241);
      for (let d = 0; d < 128; d++, idx++) {
        if (t === 0 && n === 0 && d === 0) { TND_MIX[idx] = 0; continue; }
        const df = fround(d / 22638);
        const inv = fround(fround(one / fround(fround(tf + nf) + df)) + c100);
        TND_MIX[idx] = fround(c15979 / inv);
      }
    }
  }
})();

const LOW_PASS = fround(0.15), DC_R = fround(0.995), OUT_GAIN = fround(1.05);

class Chip {
  // hostRate: the rate of the samples render() produces. enableMask: the $4015 value (bit 0 pulse 1, 1 pulse 2, 2 triangle, 3 noise, 4 DMC).
  constructor(hostRate, enableMask = 0x0F) {
    this.hostRate = hostRate;
    this.enableMask = enableMask;
    this.shadow = new Uint8Array(0x18);

    // pulses: index 0 and 1
    this.pTimer = new Int32Array(2);          // the 11-bit period register (the sweep unit changes it)
    this.pTimerCounter = new Int32Array(2);
    this.pSeq = new Int32Array(2);
    this.pDuty = new Int32Array(2);
    this.pLength = new Int32Array(2);
    this.pEnabled = new Uint8Array(2);
    this.pHalt = new Uint8Array(2);           // length halt / envelope loop
    this.pConst = new Uint8Array(2);          // constant volume
    this.pVol = new Int32Array(2);            // volume / envelope period
    this.pEnvStart = new Uint8Array(2);
    this.pEnvDiv = new Int32Array(2);
    this.pEnvDecay = new Int32Array(2);
    this.pSwNeg = new Uint8Array(2);
    this.pSwShift = new Int32Array(2);
    this.pSwPeriod = new Int32Array(2);
    this.pSwReload = new Uint8Array(2);
    this.pSwDiv = new Int32Array(2);

    // triangle
    this.triTimer = 0; this.triTimerCounter = 0; this.triSeq = 0; this.triLinearReg = 0; this.triLinear = 0;
    this.triReload = 0; this.triHalt = 0; this.triLength = 0; this.triEnabled = 0;

    // noise
    this.noiseShift = 1; this.noiseTimerCounter = 0; this.noisePeriodReg = 0; this.noiseLength = 0; this.noiseEnabled = 0;
    this.noiseHalt = 0; this.noiseConst = 0; this.noiseVol = 0; this.noiseEnvStart = 0; this.noiseEnvDiv = 0; this.noiseEnvDecay = 0;

    // DMC
    this.dmcMem = null;                        // Uint8Array(0x4000): the bytes at $C000-$FFFF, mirrored at $8000 (null: reads 0)
    this.dmcEnabled = 0; this.dmcCtrl = 0; this.dmcAddrReg = 0; this.dmcLenReg = 0;
    this.dmcTimer = 428; this.dmcTimerPeriod = 428; this.dmcAddress = 0; this.dmcLenRemaining = 0; this.dmcIrqEnable = 0; this.dmcLoop = 0;
    this.dmcShiftReg = 0; this.dmcBitsRemaining = 8; this.dmcDelta = 0; this.dmcSilence = 0; this.dmcBuffer = 0; this.dmcBufferFilled = 0;
    this.dmcStartDelay = 0; this.dmcCycle = 0;

    // frame sequencer
    this.frameCycle = 0; this.frameMode5 = 0; this.frameIrqInhibit = 0; this.frameStep = 0; this.nextFrameEvent = 7457;

    // sample pacing and filters
    this.samplePhase = 0; this.lpLast = 0; this.dcLastIn = 0; this.dcLastOut = 0;
    this.channelMask = 0x1F;

    // the resampler (Core/StreamResampler.cs)
    this.rsCap = 1 << 15;
    this.rsBuf = new Float32Array(this.rsCap);
    this.rsCount = 3; this.rsPos = 1;
    this.rsStep = NATIVE_RATE / hostRate;

    // NesApuInstrument.Attach
    this.writeApu(0x4017, 0x40);
    this.writeApu(0x4015, enableMask);
  }

  setHostRate(rate) { this.hostRate = rate; this.rsStep = NATIVE_RATE / rate; }

  // Memory for the DMC: bytes[i] is the byte at $C000 + i (sample data, at most 16 KB). The 16 KB is also seen at $8000, like a 16 KB cartridge, so a
  // sample that runs past $FFFF wraps round to the start of it.
  setDmcMemory(bytes) {
    const mem = new Uint8Array(0x4000);
    mem.set(bytes.subarray(0, Math.min(bytes.length, 0x4000)));
    this.dmcMem = mem;
  }

  // NesApuInstrument.Write: reg 0 = $4000 ... 0x17 = $4017
  write(reg, value) {
    this.shadow[reg] = value;
    this.writeApu(0x4000 + reg, value);
  }

  writeApu(address, value) {
    switch (address) {
      case 0x4000: this.pulseCtl(0, value); break;
      case 0x4001: this.pulseSweep(0, value); break;
      case 0x4002: this.pTimer[0] = (this.pTimer[0] & 0x0700) | value; break;
      case 0x4003: this.pulseHigh(0, value); break;
      case 0x4004: this.pulseCtl(1, value); break;
      case 0x4005: this.pulseSweep(1, value); break;
      case 0x4006: this.pTimer[1] = (this.pTimer[1] & 0x0700) | value; break;
      case 0x4007: this.pulseHigh(1, value); break;
      case 0x4008: this.triLinearReg = value; this.triHalt = (value & 0x80) ? 1 : 0; this.triReload = 1; break;
      case 0x400A: this.triTimer = (this.triTimer & 0x0700) | value; break;
      case 0x400B:
        this.triTimer = (this.triTimer & 0x00FF) | ((value & 7) << 8);
        if (this.triEnabled) this.triLength = LENGTH_TABLE[(value >> 3) & 0x1F];
        this.triSeq = 0; this.triReload = 1; this.triTimerCounter = this.triTimer + 1;
        break;
      case 0x400C:
        this.noiseHalt = (value & 0x20) ? 1 : 0; this.noiseConst = (value & 0x10) ? 1 : 0; this.noiseVol = value & 0x0F; this.noiseEnvStart = 1;
        break;
      case 0x400E: this.noisePeriodReg = value; break;
      case 0x400F:
        if (this.noiseEnabled) this.noiseLength = LENGTH_TABLE[(value >> 3) & 0x1F];
        this.noiseEnvStart = 1;
        break;
      case 0x4010:
        this.dmcCtrl = value; this.dmcIrqEnable = (value & 0x80) ? 1 : 0; this.dmcLoop = (value & 0x40) ? 1 : 0;
        this.dmcTimerPeriod = DMC_RATES[value & 0x0F];
        break;
      case 0x4011: this.dmcDelta = value & 0x7F; break;
      case 0x4012: this.dmcAddrReg = value; break;
      case 0x4013: this.dmcLenReg = value; break;
      case 0x4015: {
        this.pEnabled[0] = (value & 1) ? 1 : 0; if (!this.pEnabled[0]) this.pLength[0] = 0;
        this.pEnabled[1] = (value & 2) ? 1 : 0; if (!this.pEnabled[1]) this.pLength[1] = 0;
        this.triEnabled = (value & 4) ? 1 : 0; if (!this.triEnabled) this.triLength = 0;
        this.noiseEnabled = (value & 8) ? 1 : 0; if (!this.noiseEnabled) this.noiseLength = 0;
        const enableDmc = (value & 0x10) !== 0;
        if (enableDmc) { if (this.dmcLenRemaining === 0) { this.restartDmc(); this.dmcStartDelay = (this.dmcCycle & 1) === 1 ? 2 : 3; } }
        else this.dmcLenRemaining = 0;
        this.dmcEnabled = enableDmc ? 1 : 0;
        break;
      }
      case 0x4017:
        this.frameMode5 = (value & 0x80) ? 1 : 0; this.frameIrqInhibit = (value & 0x40) ? 1 : 0;
        this.frameCycle = 0; this.frameStep = 0; this.nextFrameEvent = 7457;
        if (this.frameMode5) { this.quarterFrame(); this.halfFrame(); }
        break;
    }
  }

  pulseCtl(i, v) {
    this.pDuty[i] = (v >> 6) & 3; this.pHalt[i] = (v & 0x20) ? 1 : 0; this.pConst[i] = (v & 0x10) ? 1 : 0; this.pVol[i] = v & 0x0F; this.pEnvStart[i] = 1;
  }
  pulseSweep(i, v) {
    this.pSwPeriod[i] = ((v >> 4) & 7) + 1; this.pSwNeg[i] = (v & 8) ? 1 : 0; this.pSwShift[i] = v & 7; this.pSwReload[i] = 1;
  }
  pulseHigh(i, v) {
    this.pTimer[i] = (this.pTimer[i] & 0x00FF) | ((v & 7) << 8);
    if (this.pEnabled[i]) this.pLength[i] = LENGTH_TABLE[(v >> 3) & 0x1F];
    this.pSeq[i] = 0; this.pEnvStart[i] = 1; this.pTimerCounter[i] = (this.pTimer[i] + 1) * 2;
  }

  restartDmc() { this.dmcAddress = 0xC000 + (this.dmcAddrReg << 6); this.dmcLenRemaining = (this.dmcLenReg << 4) + 1; }

  // ---- frame sequencer ----
  quarterFrame() {
    for (let i = 0; i < 2; i++) {
      if (this.pEnvStart[i]) { this.pEnvStart[i] = 0; this.pEnvDecay[i] = 15; this.pEnvDiv[i] = this.pVol[i] + 1; }
      else if (--this.pEnvDiv[i] <= 0) { this.pEnvDiv[i] = this.pVol[i] + 1; if (this.pEnvDecay[i] > 0) this.pEnvDecay[i]--; else if (this.pHalt[i]) this.pEnvDecay[i] = 15; }
    }
    if (this.noiseEnvStart) { this.noiseEnvStart = 0; this.noiseEnvDecay = 15; this.noiseEnvDiv = this.noiseVol + 1; }
    else if (--this.noiseEnvDiv <= 0) { this.noiseEnvDiv = this.noiseVol + 1; if (this.noiseEnvDecay > 0) this.noiseEnvDecay--; else if (this.noiseHalt) this.noiseEnvDecay = 15; }
    if (this.triReload) this.triLinear = this.triLinearReg & 0x7F;
    else if (this.triLinear > 0) this.triLinear--;
    if ((this.triLinearReg & 0x80) === 0) this.triReload = 0;
  }

  halfFrame() {
    if (!this.pHalt[0] && this.pLength[0] > 0) this.pLength[0]--;
    if (!this.pHalt[1] && this.pLength[1] > 0) this.pLength[1]--;
    if (!this.triHalt && this.triLength > 0) this.triLength--;
    if (!this.noiseHalt && this.noiseLength > 0) this.noiseLength--;
    this.sweep(0); this.sweep(1);
  }

  sweep(i) {
    const shift = this.pSwShift[i], period = this.pSwPeriod[i];
    if (shift === 0) { if (this.pSwReload[i]) { this.pSwDiv[i] = period; this.pSwReload[i] = 0; } return; }
    if (this.pSwReload[i]) { this.pSwDiv[i] = period; this.pSwReload[i] = 0; }
    else if (--this.pSwDiv[i] <= 0) {
      this.pSwDiv[i] = period;
      const timer = this.pTimer[i], change = timer >> shift;
      const target = this.pSwNeg[i] ? (timer - change - (i === 0 ? 1 : 0)) : (timer + change);
      if (target >= 8 && target <= 0x7FF) this.pTimer[i] = target;
    }
  }

  frameSequencer() {
    while (this.frameCycle >= this.nextFrameEvent) {
      if (!this.frameMode5) {
        switch (this.frameStep) {
          case 0: this.quarterFrame(); this.nextFrameEvent = 14913; break;
          case 1: this.quarterFrame(); this.halfFrame(); this.nextFrameEvent = 22371; break;
          case 2: this.quarterFrame(); this.nextFrameEvent = 29829; break;
          case 3: this.quarterFrame(); this.halfFrame(); this.frameStep = -1; this.frameCycle -= 29830; this.nextFrameEvent = 7457; break;
        }
      } else {
        switch (this.frameStep) {
          case 0: this.quarterFrame(); this.halfFrame(); this.nextFrameEvent = 14913; break;
          case 1: this.quarterFrame(); this.nextFrameEvent = 22371; break;
          case 2: this.quarterFrame(); this.halfFrame(); this.nextFrameEvent = 29829; break;
          case 3: this.quarterFrame(); this.nextFrameEvent = 37281; break;
          case 4: this.quarterFrame(); this.frameStep = -1; this.frameCycle -= 37282; this.nextFrameEvent = 7457; break;
        }
      }
      this.frameStep++;
    }
  }

  clockDmc() {
    this.dmcCycle++;
    if (--this.dmcTimer <= 0) {
      this.dmcTimer = this.dmcTimerPeriod;
      if (!this.dmcSilence) {
        if (this.dmcShiftReg & 1) { if (this.dmcDelta <= 125) this.dmcDelta += 2; }
        else if (this.dmcDelta >= 2) this.dmcDelta -= 2;
      }
      this.dmcShiftReg >>= 1;
      if (--this.dmcBitsRemaining === 0) {
        if (this.dmcBufferFilled) { this.dmcSilence = 0; this.dmcShiftReg = this.dmcBuffer; this.dmcBitsRemaining = 8; this.dmcBufferFilled = 0; }
        else { this.dmcSilence = 1; this.dmcBitsRemaining = 8; }
        this.dmcFetch();
      }
    }
    if (this.dmcStartDelay > 0) { if (--this.dmcStartDelay === 0) this.dmcFetch(); }
    else this.dmcFetch();
  }

  dmcFetch() {
    if (this.dmcBufferFilled) return;
    if (this.dmcLenRemaining === 0) return;
    const mem = this.dmcMem;
    this.dmcBuffer = mem ? mem[this.dmcAddress & 0x3FFF] : 0;
    this.dmcAddress++; if (this.dmcAddress > 0xFFFF) this.dmcAddress = 0x8000;
    this.dmcLenRemaining--; this.dmcBufferFilled = 1;
    if (this.dmcLenRemaining === 0 && this.dmcLoop) this.restartDmc();
  }

  // Would the sweep unit mute this pulse (the target period is out of range)?
  sweepMutes(i) {
    const shift = this.pSwShift[i];
    if (shift === 0) return false;
    const timer = this.pTimer[i], change = timer >> shift;
    const target = this.pSwNeg[i] ? (timer - change - (i === 0 ? 1 : 0)) : (timer + change);
    return target > 0x7FF || target < 8;
  }

  pulseOut(i) {
    if (!this.pEnabled[i] || this.pLength[i] === 0 || this.pTimer[i] < 8 || this.sweepMutes(i)) return 0;
    if (DUTY_TABLE[(this.pDuty[i] << 3) + this.pSeq[i]] !== 1) return 0;
    return this.pConst[i] ? this.pVol[i] : this.pEnvDecay[i];
  }

  // One output sample (MixAndStore): the nonlinear mix, a one-pole low-pass and a DC-blocking high-pass.
  mix() {
    const mask = this.channelMask;
    const p1 = (mask & 1) ? this.pulseOut(0) : 0;
    const p2 = (mask & 2) ? this.pulseOut(1) : 0;
    const pulseMix = PULSE_MIX[p1 + p2];
    let t = 0;
    if ((mask & 4) && this.triEnabled && this.triLength !== 0 && this.triLinear !== 0 && this.triTimer >= 2) {
      const s = this.triSeq; t = s < 16 ? 15 - s : s - 16;
    }
    let n = 0;
    if ((mask & 8) && this.noiseEnabled && this.noiseLength !== 0 && (this.noiseShift & 1) === 0) n = this.noiseConst ? this.noiseVol : this.noiseEnvDecay;
    const d = ((mask & 0x10) && this.dmcEnabled) ? this.dmcDelta : 0;
    const mixed = fround(pulseMix + TND_MIX[(t << 11) | (n << 7) | d]);
    this.lpLast = fround(this.lpLast + fround(fround(mixed - this.lpLast) * LOW_PASS));
    const lp = this.lpLast;
    const hp = fround(fround(lp - this.dcLastIn) + fround(DC_R * this.dcLastOut));
    this.dcLastIn = lp; this.dcLastOut = hp;
    // straight into the resampler's input (NesApuInstrument drains the chip's ring into it after every step)
    if (this.rsCount >= this.rsCap) throw new Error('resampler input overflow');
    this.rsBuf[this.rsCount++] = fround(hp * OUT_GAIN);
  }

  // APU_FIX.Step: n CPU cycles
  step(cycles) {
    const pTimer = this.pTimer, pTC = this.pTimerCounter, pSeq = this.pSeq;
    for (let i = 0; i < cycles; i++) {
      if (this.frameCycle >= this.nextFrameEvent) this.frameSequencer();
      this.clockDmc();
      if (--pTC[0] <= 0) { pTC[0] = (pTimer[0] + 1) * 2; pSeq[0] = (pSeq[0] + 1) & 7; }
      if (--pTC[1] <= 0) { pTC[1] = (pTimer[1] + 1) * 2; pSeq[1] = (pSeq[1] + 1) & 7; }
      // triangle: the timer free-runs, the sequence only advances while both counters are non-zero
      if (--this.triTimerCounter <= 0) {
        this.triTimerCounter = this.triTimer + 1;
        if (this.triEnabled && this.triLength !== 0 && this.triLinear !== 0) this.triSeq = (this.triSeq + 1) & 31;
      }
      // noise: the LFSR is clocked by the timer whatever the channel is doing
      if (--this.noiseTimerCounter <= 0) {
        this.noiseTimerCounter = NOISE_PERIODS[this.noisePeriodReg & 0x0F];
        const s = this.noiseShift, tap = (this.noisePeriodReg & 0x80) ? ((s >> 6) & 1) : ((s >> 1) & 1);
        this.noiseShift = (s >> 1) | (((s & 1) ^ tap) << 14);
      }
      this.frameCycle++;
      this.samplePhase += NATIVE_RATE;
      while (this.samplePhase >= CPU_FREQ) { this.samplePhase -= CPU_FREQ; this.mix(); }
    }
  }

  // ---- resampler ----
  rsAvailable() { return Math.max(0, Math.floor((this.rsCount - 3 - this.rsPos) / this.rsStep) + 1); }

  rsRead(out, off, len) {
    const n = Math.min(len, this.rsAvailable());
    const buf = this.rsBuf, step = this.rsStep;
    let pos = this.rsPos;
    for (let i = 0; i < n; i++) {
      const k = Math.floor(pos);
      const t = fround(pos - k);
      const y0 = buf[k - 1], y1 = buf[k], y2 = buf[k + 1], y3 = buf[k + 2];
      const c1 = fround(0.5 * fround(y2 - y0));
      const c2 = fround(fround(fround(y0 - fround(2.5 * y1)) + fround(2 * y2)) - fround(0.5 * y3));
      const c3 = fround(fround(0.5 * fround(y3 - y0)) + fround(1.5 * fround(y1 - y2)));
      out[off + i] = fround(fround(fround(fround(fround(fround(c3 * t) + c2) * t) + c1) * t) + y1);
      pos += step;
    }
    this.rsPos = pos;
    // (Core's resampler throws here when a host rate is under about a quarter of 44100 Hz: the read position can land past the buffered
    // input. Clamping changes nothing at any rate where Core works.)
    let drop = Math.floor(pos) - 1;
    if (drop > this.rsCount) drop = this.rsCount;
    if (drop > 0) {
      buf.copyWithin(0, drop, this.rsCount);
      this.rsCount -= drop;
      this.rsPos -= drop;
    }
    return n;
  }

  // NesApuInstrument.Render: fills out[off .. off+len) with mono samples at the host rate
  render(out, off, len) {
    let done = 0;
    while (done < len) {
      done += this.rsRead(out, off + done, len - done);
      if (done >= len) break;
      this.step(STEP_CYCLES);
    }
  }
}
