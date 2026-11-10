// ---- chip: the SN76489 of the Master System / Game Gear, as BrokenNes plays it --------------------------------------------------------
//
// A transcription of BrokenNes's Windows/NesEmulator/sega/Sn76489.cs (the stock Master System / Game Gear sound unit, menu id SMS / GG, the chip the
// BrokenNes2 plugin's SMS console plays) and of its BandLimitedMixer.cs, both written for BrokenNes from the SMS Power SN76489 documentation (Maxim)
// and the Game Gear Hardware Reference Manual. Sample for sample the same as the C# (Nano/sms/test/parity.mjs): every float is a float32 here as
// there (Math.fround), the band-limited kernel is built with the same formula and checked against the C# table.
//
// The one change: Tick() is not run once per PSG clock. Run() jumps straight to the next clock at which a counter expires (the output can only change
// there), which makes the same steps at the same clocks for a fraction of the work. Integer only between edges; nothing is allocated after start.

const f32 = Math.fround;
const PSG_TABLE = [32767, 26028, 20675, 16422, 13045, 10362, 8231, 6568, 5193, 4125, 3277, 2603, 2067, 1642, 1304, 0];
const PSG_LEVEL = new Float32Array(16);
for (let i = 0; i < 16; i++) PSG_LEVEL[i] = f32(PSG_TABLE[i] / 32767);
const NTSC_Z80 = 53693175 / 15;          // the Master System / Game Gear Z80 clock (NTSC master / 15); the PSG ticks at a sixteenth of it

// ---- BandLimitedMixer.cs ----
const BL_TAPS = 16, BL_PHASES = 32, BL_MASK = 4095;
const BL_KERNEL = (() => {
  const k = new Float32Array(BL_PHASES * BL_TAPS), cutoff = 0.94, tmp = new Float64Array(BL_TAPS);
  for (let p = 0; p < BL_PHASES; p++) {
    const frac = p / BL_PHASES;
    let total = 0;
    for (let t = 0; t < BL_TAPS; t++) {
      const x = t - BL_TAPS / 2 + 1 - frac;
      const sinc = Math.abs(x) < 1e-12 ? 1.0 : Math.sin(Math.PI * cutoff * x) / (Math.PI * cutoff * x);
      const w = Math.abs(x) <= BL_TAPS / 2 ? 0.42 + 0.5 * Math.cos(2 * Math.PI * x / BL_TAPS) + 0.08 * Math.cos(4 * Math.PI * x / BL_TAPS) : 0;
      tmp[t] = sinc * w; total += tmp[t];
    }
    for (let t = 0; t < BL_TAPS; t++) k[p * BL_TAPS + t] = f32(tmp[t] / total);
  }
  return k;
})();

class BandLimitedMixer {
  constructor(clockHz, sampleRate) {
    this.ring = new Float32Array(BL_MASK + 1);
    this.sampleRate = sampleRate;
    this.samplesPerClock = sampleRate / clockHz;
    this.leak = f32(2 * Math.PI * 5.0 / sampleRate);
    this.oneMinusLeak = f32(1 - this.leak);
    this.sum = 0; this.consumed = 0; this.now = 0;
  }
  step(clock, delta) {
    if (delta === 0) return;
    const pos = clock * this.samplesPerClock, whole = Math.floor(pos);
    let phase = Math.trunc((pos - whole) * BL_PHASES);
    if (phase >= BL_PHASES) phase = BL_PHASES - 1;
    const first = whole - BL_TAPS / 2 + 1;
    const over = first + BL_TAPS - this.consumed - (BL_MASK + 1);
    if (over > 0) this.consume(over);
    const ring = this.ring, kb = phase * BL_TAPS;
    for (let t = 0; t < BL_TAPS; t++) { const at = (first + t) & BL_MASK; ring[at] = f32(ring[at] + f32(delta * BL_KERNEL[kb + t])); }
  }
  advance(clock) {
    this.now = clock * this.samplesPerClock;
    const over = Math.floor(this.now) + BL_TAPS + 1 - this.consumed - (BL_MASK + 1);
    if (over > 0) this.consume(over);
  }
  get available() { return Math.max(0, Math.floor(this.now) - BL_TAPS / 2 - this.consumed); }
  // n samples (n <= available) into out[off..]: integrated and high-passed, then out = trunc(clamp(x * scale)) / 32768 (the C# host's 16-bit sample)
  readInto(out, off, n, scale) {
    const ring = this.ring, k = this.oneMinusLeak;
    let sum = this.sum, c = this.consumed;
    for (let i = 0; i < n; i++) {
      const at = (c + i) & BL_MASK;
      sum = f32(f32(sum * k) + ring[at]);
      ring[at] = 0;
      let x = f32(sum * scale);
      x = x < -32768 ? -32768 : x > 32767 ? 32767 : x;
      out[off + i] = (x | 0) / 32768;        // (short)x: toward zero, and never -0
    }
    this.sum = sum; this.consumed = c + n;
  }
  consume(n) {
    const ring = this.ring, k = this.oneMinusLeak;
    let sum = this.sum;
    for (let i = 0; i < n; i++) { const at = (this.consumed + i) & BL_MASK; sum = f32(f32(sum * k) + ring[at]); ring[at] = 0; }
    this.sum = sum; this.consumed += n;
  }
}

// ---- Sn76489.cs (variant Sega: the Master System / Game Gear part) ----
class Chip {
  // opts: {clockHz (the Z80 clock, default NTSC), gameGear (the stereo port), trace}
  constructor(sampleRate, opts = {}) {
    this.cpuClockHz = opts.clockHz ?? NTSC_Z80;
    this.sampleRate = sampleRate;
    this.left = new BandLimitedMixer(this.cpuClockHz / 16, sampleRate);
    this.right = new BandLimitedMixer(this.cpuClockHz / 16, sampleRate);
    this.gain = f32(0.2);
    this.scale = f32(this.gain * 32767);
    this.period = new Int32Array(3);
    this.volume = new Int32Array(4);
    this.counter = new Int32Array(4);
    this.tone = new Uint8Array(3);
    this.trace = null;            // [[0, value] PSG write, [2, value] stereo write, [1, cpuClocks] run, [3, frames] read]: tools/NanoSmsParity replays it
    this.reset();
  }

  reset() {
    this.period.fill(0); this.volume.fill(15);
    this.noise = 0; this.latched = 0; this.stereo = 0xFF; this.noiseDivider = false;
    this.shift = 0x8000;
    for (let i = 0; i < 3; i++) { this.counter[i] = 1; this.tone[i] = 1; }
    this.counter[3] = this.noiseRate();
    this.cpuRemainder = 0; this.clock = 0;
    this.lastLeft = 0; this.lastRight = 0;
    this.setOutput();
  }

  write(value) {
    if (this.trace) this.trace.push([0, value]);
    if (value & 0x80) {
      this.latched = (value >> 4) & 7;
      const ch = this.latched >> 1;
      if (this.latched & 1) this.volume[ch] = value & 0x0F;
      else if (ch < 3) this.period[ch] = (this.period[ch] & 0x3F0) | (value & 0x0F);
      else { this.noise = value & 7; this.shift = 0x8000; }
    } else {
      const ch = this.latched >> 1;
      if (this.latched & 1) this.volume[ch] = value & 0x0F;
      else if (ch < 3) this.period[ch] = (this.period[ch] & 0x00F) | ((value & 0x3F) << 4);
      else { this.noise = value & 7; this.shift = 0x8000; }
    }
    this.setOutput();
  }

  writeStereo(value) { if (this.trace) this.trace.push([2, value]); this.stereo = value & 0xFF; this.setOutput(); }

  noiseRate() { const r = this.noise & 3; return r === 0 ? 0x10 : r === 1 ? 0x20 : r === 2 ? 0x40 : (this.period[2] === 0 ? 1 : this.period[2]); }

  // cpuClocks of the Z80 clock: a sixteenth as many PSG clocks, the remainder carried
  run(cpuClocks) {
    if (this.trace) this.trace.push([1, cpuClocks]);
    this.cpuRemainder += cpuClocks;
    let ticks = this.cpuRemainder >> 4;
    this.cpuRemainder &= 15;
    const cnt = this.counter, per = this.period, tone = this.tone;
    while (ticks > 0) {
      // the next clock at which a counter expires (every counter is >= 1)
      let m = cnt[0];
      if (cnt[1] < m) m = cnt[1];
      if (cnt[2] < m) m = cnt[2];
      if (cnt[3] < m) m = cnt[3];
      if (m > ticks) { cnt[0] -= ticks; cnt[1] -= ticks; cnt[2] -= ticks; cnt[3] -= ticks; this.clock += ticks; break; }
      cnt[0] -= m; cnt[1] -= m; cnt[2] -= m; cnt[3] -= m; this.clock += m; ticks -= m;
      let changed = false;
      for (let i = 0; i < 3; i++) {
        if (cnt[i] > 0) continue;
        const p = per[i];
        cnt[i] = p === 0 ? 1 : p;
        const nt = p <= 1 ? 1 : tone[i] ^ 1;       // a period of 0 or 1 holds the Sega part's output high
        if (nt !== tone[i]) { tone[i] = nt; changed = true; }
      }
      if (cnt[3] <= 0) {
        cnt[3] = this.noiseRate();
        this.noiseDivider = !this.noiseDivider;
        if (this.noiseDivider) {
          const s = this.shift, fb = (this.noise & 4) ? ((s ^ (s >> 3)) & 1) : (s & 1);
          this.shift = (s >> 1) | (fb << 15);
          if ((s ^ this.shift) & 1) changed = true;
        }
      }
      if (changed) this.setOutput();
    }
    this.left.advance(this.clock); this.right.advance(this.clock);
  }

  setOutput() {
    let l = 0, r = 0;
    const st = this.stereo;
    for (let i = 0; i < 4; i++) {
      const high = i < 3 ? this.tone[i] !== 0 : (this.shift & 1) !== 0;
      if (!high) continue;
      const a = PSG_LEVEL[this.volume[i]];
      if (st & (0x10 << i)) l = f32(l + a);
      if (st & (0x01 << i)) r = f32(r + a);
    }
    if (l !== this.lastLeft) { this.left.step(this.clock, f32(l - this.lastLeft)); this.lastLeft = l; }
    if (r !== this.lastRight) { this.right.step(this.clock, f32(r - this.lastRight)); this.lastRight = r; }
  }

  // Fills L[off..off+n) and R (null: mono = the left side, which on a Master System is the same as the right) with the next n samples.
  render(L, R, off, n) {
    const need = this.left.consumed + n + BL_TAPS / 2 + 1;   // floor(now) - 8 - consumed >= n
    if (this.left.available < n) {
      // PSG clocks to reach `need` output samples, as CPU clocks; one PSG clock more than the exact edge so rounding never leaves it short
      const clocks = Math.ceil(need / this.left.samplesPerClock) - this.clock + 1;
      this.run(Math.max(0, clocks * 16 - this.cpuRemainder));
    }
    if (this.trace) this.trace.push([3, n]);
    this.left.readInto(L, off, n, this.scale);
    if (R) this.right.readInto(R, off, n, this.scale);
    else this.right.consume(n);
  }
}
