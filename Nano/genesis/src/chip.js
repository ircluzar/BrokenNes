// ---- chip: the Genesis / Mega Drive sound (YM2612 + PSG), as BrokenNes plays it ------------------------------------------------------
//
// A transcription of BrokenNes's stock Genesis sound unit (menu id MD; Windows/NesEmulator/md/MdSoundUnit.cs): its YM2612 (sega/Ym2612.cs, the
// slot-pipeline model of the discrete Model 1 chip with the ladder, itself a C# port of ymfm by Aaron Giles, BSD-3-Clause), its SN76489 PSG in the
// Mega Drive variant (sega/Sn76489.cs), and the mixing of MdSoundUnit: each FM sample a step of a held level into a band-limited mixer
// (sega/BandLimitedMixer.cs) at the chip's 53,267 Hz, the PSG its own, the two summed as 16-bit samples and clipped. Sample for sample the same as
// the C# (Nano/genesis/test/parity.mjs): every float is a float32 here as there (Math.fround), every unsigned register is kept unsigned (>>> 0).
//
// Left out (the engine never uses them): the timers, CSM and the busy flag, the status read, the DAC's write path is kept (a fuzz test plays it).
// The one change: the PSG's Tick() is not run once per PSG clock (Run() jumps to the next counter expiry, as Nano SMS's chip does). The YM2612 runs
// one sample at a time, as many as the mixer needs. Nothing is allocated after start.

const f32 = Math.fround;
const MD_MASTER = 53693175;                 // NTSC master clock
const NTSC_Z80 = MD_MASTER / 15;            // the Z80 / PSG input clock (the PSG ticks at a sixteenth of it)
const YM_RATE = MD_MASTER / 1008;           // one YM2612 sample per 1008 master clocks (144 chip clocks of master / 7)

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
    this.samplesPerClock = sampleRate / clockHz;
    this.oneMinusLeak = f32(1 - f32(2 * Math.PI * 5.0 / sampleRate));
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
  // n samples as BandLimitedMixer.Read gives them (float32, integrated and high-passed) into out[off..]
  read(out, off, n) {
    const ring = this.ring, k = this.oneMinusLeak;
    let sum = this.sum;
    const c = this.consumed;
    for (let i = 0; i < n; i++) { const at = (c + i) & BL_MASK; sum = f32(f32(sum * k) + ring[at]); ring[at] = 0; out[off + i] = sum; }
    this.sum = sum; this.consumed = c + n;
  }
  consume(n) {
    const ring = this.ring, k = this.oneMinusLeak;
    let sum = this.sum;
    for (let i = 0; i < n; i++) { const at = (this.consumed + i) & BL_MASK; sum = f32(f32(sum * k) + ring[at]); ring[at] = 0; }
    this.sum = sum; this.consumed += n;
  }
}

// ---- Sn76489.cs, variant MegaDrive (no hold on periods 0 / 1; mono: the stereo port is the Game Gear's) ----
const PSG_TABLE = [32767, 26028, 20675, 16422, 13045, 10362, 8231, 6568, 5193, 4125, 3277, 2603, 2067, 1642, 1304, 0];
const PSG_LEVEL = new Float32Array(16);
for (let i = 0; i < 16; i++) PSG_LEVEL[i] = f32(PSG_TABLE[i] / 32767);

class Psg {
  constructor(sampleRate, cpuClockHz) {
    this.cpuClockHz = cpuClockHz;
    this.mix = new BandLimitedMixer(cpuClockHz / 16, sampleRate);
    this.scale = f32(f32(0.2) * 32767);
    this.period = new Int32Array(3); this.volume = new Int32Array(4); this.counter = new Int32Array(4); this.tone = new Uint8Array(3);
    this.buf = new Float32Array(BL_MASK + 1);
    this.period.fill(0); this.volume.fill(15);
    this.noise = 0; this.latched = 0; this.noiseDivider = false; this.shift = 0x8000;
    for (let i = 0; i < 3; i++) { this.counter[i] = 1; this.tone[i] = 1; }
    this.counter[3] = this.noiseRate();
    this.cpuRemainder = 0; this.clock = 0; this.last = 0;
    this.setOutput();
  }
  write(value) {
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
  noiseRate() { const r = this.noise & 3; return r === 0 ? 0x10 : r === 1 ? 0x20 : r === 2 ? 0x40 : (this.period[2] === 0 ? 1 : this.period[2]); }
  run(cpuClocks) {
    this.cpuRemainder += cpuClocks;
    let ticks = this.cpuRemainder >> 4;
    this.cpuRemainder &= 15;
    const cnt = this.counter, per = this.period, tone = this.tone;
    while (ticks > 0) {
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
        tone[i] ^= 1; changed = true;               // the Mega Drive part toggles on every expiry, periods 0 and 1 too
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
    this.mix.advance(this.clock);
  }
  setOutput() {
    let l = 0;
    for (let i = 0; i < 4; i++) {
      const high = i < 3 ? this.tone[i] !== 0 : (this.shift & 1) !== 0;
      if (high) l = f32(l + PSG_LEVEL[this.volume[i]]);
    }
    if (l !== this.last) { this.mix.step(this.clock, f32(l - this.last)); this.last = l; }
  }
  // CPU clocks that Sn76489.RunForSamples(frames) runs (0 when enough are finished)
  clocksFor(frames) {
    if (this.mix.available >= frames) return 0;
    const need = this.mix.consumed + frames + BL_TAPS / 2 + 1;
    const clocks = Math.ceil(need / this.mix.samplesPerClock) - this.clock + 1;
    return Math.max(0, clocks * 16 - this.cpuRemainder);
  }
  // n 16-bit samples (ReadSamples: (short)clamp(x * Gain * 32767)) into out[0..n); the PSG is mono, so left = right
  readShorts(out, n) {
    const b = this.buf, s = this.scale;
    this.mix.read(b, 0, n);
    for (let i = 0; i < n; i++) { let x = f32(b[i] * s); x = x < -32768 ? -32768 : x > 32767 ? 32767 : x; out[i] = x | 0; }
  }
}

// ---- Ym2612.cs (SlotPipeline on, Ladder on: the Model 1 chip) ----
// ymfm's tables (ymfm_fm.ipp, BSD-3-Clause, Copyright (c) 2021 Aaron Giles), as sega/Ym2612.Tables.cs has them
const YM_SIN = [
  0x859, 0x6c3, 0x607, 0x58b, 0x52e, 0x4e4, 0x4a6, 0x471, 0x443, 0x41a, 0x3f5, 0x3d3, 0x3b5, 0x398, 0x37e, 0x365,
  0x34e, 0x339, 0x324, 0x311, 0x2ff, 0x2ed, 0x2dc, 0x2cd, 0x2bd, 0x2af, 0x2a0, 0x293, 0x286, 0x279, 0x26d, 0x261,
  0x256, 0x24b, 0x240, 0x236, 0x22c, 0x222, 0x218, 0x20f, 0x206, 0x1fd, 0x1f5, 0x1ec, 0x1e4, 0x1dc, 0x1d4, 0x1cd,
  0x1c5, 0x1be, 0x1b7, 0x1b0, 0x1a9, 0x1a2, 0x19b, 0x195, 0x18f, 0x188, 0x182, 0x17c, 0x177, 0x171, 0x16b, 0x166,
  0x160, 0x15b, 0x155, 0x150, 0x14b, 0x146, 0x141, 0x13c, 0x137, 0x133, 0x12e, 0x129, 0x125, 0x121, 0x11c, 0x118,
  0x114, 0x10f, 0x10b, 0x107, 0x103, 0x0ff, 0x0fb, 0x0f8, 0x0f4, 0x0f0, 0x0ec, 0x0e9, 0x0e5, 0x0e2, 0x0de, 0x0db,
  0x0d7, 0x0d4, 0x0d1, 0x0cd, 0x0ca, 0x0c7, 0x0c4, 0x0c1, 0x0be, 0x0bb, 0x0b8, 0x0b5, 0x0b2, 0x0af, 0x0ac, 0x0a9,
  0x0a7, 0x0a4, 0x0a1, 0x09f, 0x09c, 0x099, 0x097, 0x094, 0x092, 0x08f, 0x08d, 0x08a, 0x088, 0x086, 0x083, 0x081,
  0x07f, 0x07d, 0x07a, 0x078, 0x076, 0x074, 0x072, 0x070, 0x06e, 0x06c, 0x06a, 0x068, 0x066, 0x064, 0x062, 0x060,
  0x05e, 0x05c, 0x05b, 0x059, 0x057, 0x055, 0x053, 0x052, 0x050, 0x04e, 0x04d, 0x04b, 0x04a, 0x048, 0x046, 0x045,
  0x043, 0x042, 0x040, 0x03f, 0x03e, 0x03c, 0x03b, 0x039, 0x038, 0x037, 0x035, 0x034, 0x033, 0x031, 0x030, 0x02f,
  0x02e, 0x02d, 0x02b, 0x02a, 0x029, 0x028, 0x027, 0x026, 0x025, 0x024, 0x023, 0x022, 0x021, 0x020, 0x01f, 0x01e,
  0x01d, 0x01c, 0x01b, 0x01a, 0x019, 0x018, 0x017, 0x017, 0x016, 0x015, 0x014, 0x014, 0x013, 0x012, 0x011, 0x011,
  0x010, 0x00f, 0x00f, 0x00e, 0x00d, 0x00d, 0x00c, 0x00c, 0x00b, 0x00a, 0x00a, 0x009, 0x009, 0x008, 0x008, 0x007,
  0x007, 0x007, 0x006, 0x006, 0x005, 0x005, 0x005, 0x004, 0x004, 0x004, 0x003, 0x003, 0x003, 0x002, 0x002, 0x002,
  0x002, 0x001, 0x001, 0x001, 0x001, 0x001, 0x001, 0x001, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000,
];
const YM_POW = [
  0x3fa, 0x3f5, 0x3ef, 0x3ea, 0x3e4, 0x3df, 0x3da, 0x3d4, 0x3cf, 0x3c9, 0x3c4, 0x3bf, 0x3b9, 0x3b4, 0x3ae, 0x3a9,
  0x3a4, 0x39f, 0x399, 0x394, 0x38f, 0x38a, 0x384, 0x37f, 0x37a, 0x375, 0x370, 0x36a, 0x365, 0x360, 0x35b, 0x356,
  0x351, 0x34c, 0x347, 0x342, 0x33d, 0x338, 0x333, 0x32e, 0x329, 0x324, 0x31f, 0x31a, 0x315, 0x310, 0x30b, 0x306,
  0x302, 0x2fd, 0x2f8, 0x2f3, 0x2ee, 0x2e9, 0x2e5, 0x2e0, 0x2db, 0x2d6, 0x2d2, 0x2cd, 0x2c8, 0x2c4, 0x2bf, 0x2ba,
  0x2b5, 0x2b1, 0x2ac, 0x2a8, 0x2a3, 0x29e, 0x29a, 0x295, 0x291, 0x28c, 0x288, 0x283, 0x27f, 0x27a, 0x276, 0x271,
  0x26d, 0x268, 0x264, 0x25f, 0x25b, 0x257, 0x252, 0x24e, 0x249, 0x245, 0x241, 0x23c, 0x238, 0x234, 0x230, 0x22b,
  0x227, 0x223, 0x21e, 0x21a, 0x216, 0x212, 0x20e, 0x209, 0x205, 0x201, 0x1fd, 0x1f9, 0x1f5, 0x1f0, 0x1ec, 0x1e8,
  0x1e4, 0x1e0, 0x1dc, 0x1d8, 0x1d4, 0x1d0, 0x1cc, 0x1c8, 0x1c4, 0x1c0, 0x1bc, 0x1b8, 0x1b4, 0x1b0, 0x1ac, 0x1a8,
  0x1a4, 0x1a0, 0x19c, 0x199, 0x195, 0x191, 0x18d, 0x189, 0x185, 0x181, 0x17e, 0x17a, 0x176, 0x172, 0x16f, 0x16b,
  0x167, 0x163, 0x160, 0x15c, 0x158, 0x154, 0x151, 0x14d, 0x149, 0x146, 0x142, 0x13e, 0x13b, 0x137, 0x134, 0x130,
  0x12c, 0x129, 0x125, 0x122, 0x11e, 0x11b, 0x117, 0x114, 0x110, 0x10c, 0x109, 0x106, 0x102, 0x0ff, 0x0fb, 0x0f8,
  0x0f4, 0x0f1, 0x0ed, 0x0ea, 0x0e7, 0x0e3, 0x0e0, 0x0dc, 0x0d9, 0x0d6, 0x0d2, 0x0cf, 0x0cc, 0x0c8, 0x0c5, 0x0c2,
  0x0be, 0x0bb, 0x0b8, 0x0b5, 0x0b1, 0x0ae, 0x0ab, 0x0a8, 0x0a4, 0x0a1, 0x09e, 0x09b, 0x098, 0x094, 0x091, 0x08e,
  0x08b, 0x088, 0x085, 0x082, 0x07e, 0x07b, 0x078, 0x075, 0x072, 0x06f, 0x06c, 0x069, 0x066, 0x063, 0x060, 0x05d,
  0x05a, 0x057, 0x054, 0x051, 0x04e, 0x04b, 0x048, 0x045, 0x042, 0x03f, 0x03c, 0x039, 0x036, 0x033, 0x030, 0x02d,
  0x02a, 0x028, 0x025, 0x022, 0x01f, 0x01c, 0x019, 0x016, 0x014, 0x011, 0x00e, 0x00b, 0x008, 0x006, 0x003, 0x000,
];
const YM_INC = [
  0x00000000, 0x00000000, 0x10101010, 0x10101010, 0x10101010, 0x10101010, 0x11101110, 0x11101110,
  0x10101010, 0x10111010, 0x11101110, 0x11111110, 0x10101010, 0x10111010, 0x11101110, 0x11111110,
  0x10101010, 0x10111010, 0x11101110, 0x11111110, 0x10101010, 0x10111010, 0x11101110, 0x11111110,
  0x10101010, 0x10111010, 0x11101110, 0x11111110, 0x10101010, 0x10111010, 0x11101110, 0x11111110,
  0x10101010, 0x10111010, 0x11101110, 0x11111110, 0x10101010, 0x10111010, 0x11101110, 0x11111110,
  0x10101010, 0x10111010, 0x11101110, 0x11111110, 0x10101010, 0x10111010, 0x11101110, 0x11111110,
  0x11111111, 0x21112111, 0x21212121, 0x22212221, 0x22222222, 0x42224222, 0x42424242, 0x44424442,
  0x44444444, 0x84448444, 0x84848484, 0x88848884, 0x88888888, 0x88888888, 0x88888888, 0x88888888,
];
const YM_DETUNE = [
  0, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0, 1, 2, 2, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3,
  0, 1, 2, 4, 0, 1, 3, 4, 0, 1, 3, 4, 0, 1, 3, 5, 0, 2, 4, 5, 0, 2, 4, 6, 0, 2, 4, 6, 0, 2, 5, 7,
  0, 2, 5, 8, 0, 3, 6, 8, 0, 3, 6, 9, 0, 3, 7, 10, 0, 4, 8, 11, 0, 4, 8, 12, 0, 4, 9, 13, 0, 5, 10, 14,
  0, 5, 11, 16, 0, 6, 12, 17, 0, 6, 13, 19, 0, 7, 14, 20, 0, 8, 16, 22, 0, 8, 16, 22, 0, 8, 16, 22, 0, 8, 16, 22,
];
const YM_PM_SHIFTS = [
  0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x72, 0x72, 0x72, 0x72,
  0x77, 0x77, 0x77, 0x72, 0x72, 0x72, 0x17, 0x17, 0x77, 0x77, 0x72, 0x72, 0x17, 0x17, 0x12, 0x12,
  0x77, 0x77, 0x72, 0x17, 0x17, 0x17, 0x12, 0x07, 0x77, 0x77, 0x17, 0x12, 0x07, 0x07, 0x02, 0x01,
  0x77, 0x77, 0x17, 0x12, 0x07, 0x07, 0x02, 0x01, 0x77, 0x77, 0x17, 0x12, 0x07, 0x07, 0x02, 0x01,
];
const YM_WAVE = new Uint16Array(1024), YM_POWER = new Uint16Array(256);
for (let i = 0; i < 1024; i++) { let input = i; if (input & 0x100) input = ~input; YM_WAVE[i] = YM_SIN[input & 0xFF] | (((i >> 9) & 1) << 15); }
for (let i = 0; i < 256; i++) YM_POWER[i] = (YM_POW[i] | 0x400) << 2;
const YM_LFO_MAX = [109, 78, 72, 68, 63, 45, 9, 6];
const YM_LFO_DELAY = [0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 2, 1, 1, 1, 1, 1, 2, 1, 1, 1, 1, 1, 2];
const YM_SLOT_OF_OP = [0, 2, 1, 3];
const YM_FAST_PHASE = [0, 3, 1, 1];
const EG_ATTACK = 1, EG_DECAY = 2, EG_SUSTAIN = 3, EG_RELEASE = 4, EG_QUIET = 0x380, PHASE_DYNAMIC = 1, LFO_LEAD = 4;

class Op {
  constructor(choffs, opoffs) {
    this.choffs = choffs; this.opoffs = opoffs;
    this.phase = 0; this.envAtt = 0x3FF; this.envState = EG_RELEASE; this.ssgInverted = 0; this.keyState = 0; this.keyonLive = 0;
    this.phaseStep = 0; this.totalLevel = 0; this.blockFreq = 0; this.multiple = 0; this.egSustainLevel = 0; this.detune = 0;
    this.rate = new Int32Array(5); this.ssgEnable = false; this.amEnable = false; this.ssgMode = 0; this.pmSens = 0;
  }
}

class Ym2612 {
  constructor() {
    this.regs = new Uint8Array(0x200);
    this.ops = [];
    for (let ch = 0; ch < 6; ch++) {
      const choffs = ch % 3 + 0x100 * ((ch / 3) | 0);
      for (let n = 0; n < 4; n++) this.ops.push(new Op(choffs, choffs + (n === 0 ? 0 : n === 1 ? 8 : n === 2 ? 4 : 12)));
    }
    this.lfoHistAm = new Int32Array(4); this.lfoHistPm = new Int32Array(4);
    this.hist1 = new Int32Array(18); this.hist2 = new Int32Array(6);
    this.envCounter = 0; this.prepareCount = 0; this.lfoCounter = LFO_LEAD; this.lfoAm = 0;
    this.activeChannels = 0x3F; this.modifiedChannels = 0x3F;
    this.address = 0; this.dacData = 0; this.dacEnable = false;
    this.samples = 0;
    this.regs[0xB4] = this.regs[0xB5] = this.regs[0xB6] = 0xC0;
    this.regs[0x1B4] = this.regs[0x1B5] = this.regs[0x1B6] = 0xC0;
  }

  // a write to port 0 / 2 (address, part 1 / 2) or 1 / 3 (data)
  write(port, value) {
    switch (port & 3) {
      case 0: this.address = value; break;
      case 2: this.address = 0x100 | value; break;
      case 1:
        if (this.address & 0x100) return;
        if (this.address === 0x2A) this.dacData = (this.dacData & ~0x1FE) | ((value ^ 0x80) << 1);
        else if (this.address === 0x2B) this.dacEnable = (value & 0x80) !== 0;
        else if (this.address === 0x2C) this.dacData = (this.dacData & ~1) | ((value >> 3) & 1);
        else this.writeRegister(this.address, value);
        break;
      case 3:
        if (!(this.address & 0x100)) return;
        this.writeRegister(this.address, value);
        break;
    }
  }

  writeRegister(index, data) {
    const regs = this.regs;
    if (index === 0x27) { this.modifiedChannels = 0x3F; regs[0x27] = data; return; }     // (timers and CSM are not modelled; channel 3's mode is)
    this.modifiedChannels = 0x3F;
    if ((index & 0xF0) === 0xA0) {
      if ((index & 3) === 3) return;
      const latch = 0xB8 | ((index >> 3) & 1);
      if (index & 4) regs[latch] = data & 0x3F;
      else { regs[index] = data; regs[index | 4] = regs[latch]; }
      return;
    }
    if ((index & 0xF8) === 0xB8) return;
    regs[index] = data;
    if (index === 0x28) {
      let channel = data & 3;
      if (channel === 3) return;
      channel += ((data >> 2) & 1) * 3;
      const mask = (data >> 4) & 0xF;
      for (let n = 0; n < 4; n++) { const o = this.ops[channel * 4 + n]; o.keyonLive = (o.keyonLive & ~1) | ((mask >> n) & 1); }
    }
  }

  cacheOperator(o) {
    const regs = this.regs, choffs = o.choffs, opoffs = o.opoffs;
    let blockFreq = o.blockFreq = (regs[0xA4 + choffs] & 0x3F) << 8 | regs[0xA0 + choffs];
    if ((regs[0x27] >> 6 & 3) !== 0 && choffs === 2) {
      if (opoffs === 2) blockFreq = o.blockFreq = (regs[0xAD] & 0x3F) << 8 | regs[0xA9];
      else if (opoffs === 10) blockFreq = o.blockFreq = (regs[0xAE] & 0x3F) << 8 | regs[0xAA];
      else if (opoffs === 6) blockFreq = o.blockFreq = (regs[0xAC] & 0x3F) << 8 | regs[0xA8];
    }
    let keycode = ((blockFreq >> 10) & 0xF) << 1;
    keycode |= (0xFE80 >>> ((blockFreq >> 7) & 0xF)) & 1;
    const detuneReg = regs[0x30 + opoffs] >> 4 & 7;
    const adjust = YM_DETUNE[keycode * 4 + (detuneReg & 3)];
    o.detune = (detuneReg & 4) ? -adjust : adjust;
    const multi = regs[0x30 + opoffs] & 0xF;
    o.multiple = multi === 0 ? 1 : multi * 2;
    const lfoOn = (regs[0x22] & 8) !== 0;
    o.pmSens = regs[0xB4 + choffs] & 7;
    o.phaseStep = !lfoOn || o.pmSens === 0 ? this.computePhaseStep(o, 0) : PHASE_DYNAMIC;
    o.totalLevel = (regs[0x40 + opoffs] & 0x7F) << 3;
    let sl = regs[0x80 + opoffs] >> 4 & 0xF;
    sl |= (sl + 1) & 0x10;
    o.egSustainLevel = sl << 5;
    const ksr = regs[0x50 + opoffs] >> 6 & 3;
    const ksrval = keycode >> (ksr ^ 3);
    const eff = (raw) => raw === 0 ? 0 : Math.min(raw + ksrval, 63);
    o.rate[EG_ATTACK] = eff((regs[0x50 + opoffs] & 0x1F) * 2);
    o.rate[EG_DECAY] = eff((regs[0x60 + opoffs] & 0x1F) * 2);
    o.rate[EG_SUSTAIN] = eff((regs[0x70 + opoffs] & 0x1F) * 2);
    o.rate[EG_RELEASE] = eff((regs[0x80 + opoffs] & 0xF) * 4 + 2);
    o.ssgEnable = (regs[0x90 + opoffs] & 8) !== 0;
    o.ssgMode = regs[0x90 + opoffs] & 7;
    o.amEnable = (regs[0x60 + opoffs] & 0x80) !== 0;
  }

  computePhaseStep(o, lfoRawPm) {
    let fnum = (o.blockFreq & 0x7FF) << 1;
    const pmSens = this.regs[0xB4 + o.choffs] & 7;
    if (pmSens !== 0) {
      const fnumBits = (o.blockFreq >> 4) & 0x7F, absPm = lfoRawPm < 0 ? -lfoRawPm : lfoRawPm;
      const shifts = YM_PM_SHIFTS[pmSens * 8 + (absPm & 7)];
      let adj = (fnumBits >> (shifts & 0xF)) + (fnumBits >> ((shifts >> 4) & 0xF));
      if (pmSens > 5) adj <<= pmSens - 5;
      adj >>= 2;
      fnum = (fnum + (lfoRawPm < 0 ? -adj : adj)) & 0xFFF;
    }
    const block = (o.blockFreq >> 11) & 7;
    let phaseStep = (fnum << block) >> 2;
    phaseStep = (phaseStep + o.detune) & 0x1FFFF;
    return (phaseStep * o.multiple) >>> 1;
  }

  clockEngine() {
    if (this.modifiedChannels !== 0 || this.prepareCount++ >= 4096) {
      let active = 0;
      for (let ch = 0; ch < 6; ch++) {
        let a = 0;
        for (let n = 0; n < 4; n++) if (this.prepareOperator(this.ops[ch * 4 + n])) a = 1;
        active |= a << ch;
      }
      this.activeChannels = active;
      this.modifiedChannels = this.prepareCount = 0;
    }
    const envPrevious = this.envCounter;
    this.envCounter = (this.envCounter + 1) >>> 0;
    if ((this.envCounter & 3) === 3) this.envCounter = (this.envCounter + 1) >>> 0;
    const lfoRawPm = this.clockLfo();
    const ha = this.lfoHistAm, hp = this.lfoHistPm;
    ha[3] = ha[2]; ha[2] = ha[1]; ha[1] = ha[0]; hp[3] = hp[2]; hp[2] = hp[1]; hp[1] = hp[0];
    ha[0] = this.lfoAm; hp[0] = lfoRawPm;
    for (let ch = 0; ch < 6; ch++) {
      for (let n = 0; n < 4; n++) {
        const env = n === 3 && ch >= 4 ? envPrevious : this.envCounter;
        this.clockOperator(this.ops[ch * 4 + n], env, hp[YM_LFO_DELAY[ch + 6 * YM_SLOT_OF_OP[n]] + 1]);
      }
    }
  }

  prepareOperator(o) {
    this.cacheOperator(o);
    const ks = o.keyonLive !== 0 ? 1 : 0;
    if (ks !== o.keyState) {
      o.keyState = ks;
      if (ks) this.startAttack(o, false); else this.startRelease(o);
    }
    o.keyonLive &= ~4;
    return o.envState !== EG_RELEASE || o.envAtt < EG_QUIET;
  }

  clockLfo() {
    const regs = this.regs;
    if ((regs[0x22] & 8) === 0) { this.lfoCounter = LFO_LEAD; this.lfoAm = 0x3F; return 0; }
    const subcount = this.lfoCounter & 0xFF;
    this.lfoCounter = (this.lfoCounter + 1) >>> 0;
    if (subcount >= YM_LFO_MAX[regs[0x22] & 7]) this.lfoCounter = (this.lfoCounter + 0x101 - subcount) >>> 0;
    const c = this.lfoCounter;
    let am = (c >>> 8) & 0x3F;
    if (((c >>> 14) & 1) === 0) am ^= 0x3F;
    this.lfoAm = am;
    let pm = (c >>> 10) & 7;
    if ((c >>> 13) & 1) pm ^= 7;
    return ((c >>> 14) & 1) ? -pm : pm;
  }

  clockOperator(o, env, lfoRawPm) {
    if (o.ssgEnable) this.clockSsg(o); else o.ssgInverted = 0;
    if ((env & 3) === 0) this.clockEnvelope(o, env >>> 2);
    let step = o.phaseStep;
    if (step === PHASE_DYNAMIC) step = this.computePhaseStep(o, lfoRawPm);
    o.phase = (o.phase + step) >>> 0;
  }

  startAttack(o, isRestart) {
    if (o.envState === EG_ATTACK) return;
    o.envState = EG_ATTACK;
    if (!isRestart) o.ssgInverted = (o.ssgEnable ? 1 : 0) & (o.ssgMode >> 2 & 1);
    if (!isRestart) o.phase = 0;
    if (o.rate[EG_ATTACK] >= 62) o.envAtt = 0;
  }

  startRelease(o) {
    if (o.envState >= EG_RELEASE) return;
    o.envState = EG_RELEASE;
    if (o.ssgInverted !== 0) { o.envAtt = (0x200 - o.envAtt) & 0x3FF; o.ssgInverted = 0; }
  }

  clockSsg(o) {
    if ((o.envAtt & 0x200) === 0) return;
    const mode = o.ssgMode;
    if (mode & 1) {
      o.ssgInverted = (mode >> 2 ^ mode >> 1) & 1;
      if (o.envState !== EG_ATTACK) o.envAtt = o.ssgInverted !== 0 ? 0x200 : 0x3FF;
    } else {
      o.ssgInverted ^= mode >> 1 & 1;
      if (o.envState === EG_DECAY || o.envState === EG_SUSTAIN) this.startAttack(o, true);
      if ((mode >> 1 & 1) === 0) o.phase = 0;
    }
    if (o.envState === EG_RELEASE) o.envAtt = 0x3FF;
  }

  clockEnvelope(o, env) {
    let state = o.envState, nextState = state;
    if (state === EG_ATTACK && o.envAtt === 0) { if (o.ssgEnable) state = nextState = EG_DECAY; else nextState = EG_DECAY; }
    else if (state === EG_DECAY && o.envAtt >= o.egSustainLevel) state = nextState = EG_SUSTAIN;
    const rate = o.rate[state], rateShift = rate >> 2, orig = env;
    env = (env << rateShift) >>> 0;
    if ((env & 0x7FF) !== 0) { o.envState = nextState; return; }
    let relevant = (env >>> (rateShift <= 11 ? 11 : rateShift)) & 7;
    if (rateShift >= 12) relevant = (orig + YM_FAST_PHASE[rate & 3]) & 7;
    const increment = (YM_INC[rate] >>> (4 * relevant)) & 0xF;
    if (state === EG_ATTACK) {
      if (rate < 62 && o.envAtt !== 0) o.envAtt = (o.envAtt + ((~o.envAtt * increment) >> 4)) & 0xFFFF;
    } else {
      if (!o.ssgEnable) o.envAtt += increment;
      else if (o.envAtt < 0x200) o.envAtt += 4 * increment;
      if (o.envAtt >= 0x400) o.envAtt = 0x3FF;
    }
    o.envState = nextState;
  }

  computeVolume(o, phase, amOffset) {
    if (o.envAtt > EG_QUIET) return 0;
    const sinAtt = YM_WAVE[phase & 0x3FF];
    let result = o.envAtt;
    if (o.ssgInverted !== 0) result = (0x200 - result) & 0x3FF;
    if (o.amEnable) result += amOffset;
    result += o.totalLevel;
    if (result > 0x3FF) result = 0x3FF;
    const input = (sinAtt & 0x7FFF) + (result << 2);
    const volume = YM_POWER[input & 0xFF] >>> (input >> 8);
    return (sinAtt & 0x8000) ? -volume : volume;
  }

  lfoAmOffset(choffs, am) { const amShift = (1 << ((this.regs[0xB4 + choffs] >> 4 & 3) ^ 3)) - 1; return (am << 1) >> amShift; }

  channelValue(ch) {
    const ops = this.ops, o1 = ops[ch * 4], o2 = ops[ch * 4 + 1], o3 = ops[ch * 4 + 2], o4 = ops[ch * 4 + 3];
    const choffs = o1.choffs, ha = this.lfoHistAm, regs = this.regs;
    const am1 = this.lfoAmOffset(choffs, ha[YM_LFO_DELAY[ch]]), am3 = this.lfoAmOffset(choffs, ha[YM_LFO_DELAY[ch + 6]]);
    const am2 = this.lfoAmOffset(choffs, ha[YM_LFO_DELAY[ch + 12]]), am4 = this.lfoAmOffset(choffs, ha[YM_LFO_DELAY[ch + 18]]);
    const h = ch * 3, h1 = this.hist1;
    const a1 = h1[h], a2 = h1[h + 1], b1 = this.hist2[ch];
    const fb = regs[0xB0 + choffs] >> 3 & 7;
    const mod1 = fb !== 0 ? (a1 + a2) >> (10 - fb) : 0;
    const v1 = this.computeVolume(o1, (o1.phase >>> 10) + mod1, am1) << 16 >> 16;
    const alg = regs[0xB0 + choffs] & 7;
    let m2, m3, m4;
    switch (alg) {
      case 0: m2 = a1; m3 = b1; m4 = 0; break;
      case 1: m2 = 0; m3 = a2 + b1; m4 = 0; break;
      case 2: m2 = 0; m3 = b1; m4 = a1; break;
      case 3: m2 = a1; m3 = 0; m4 = b1; break;
      case 4: m2 = a1; m3 = 0; m4 = 0; break;
      case 5: m2 = a1; m3 = a2; m4 = a1; break;
      case 6: m2 = a1; m3 = 0; m4 = 0; break;
      default: m2 = 0; m3 = 0; m4 = 0; break;
    }
    const v2 = this.computeVolume(o2, (o2.phase >>> 10) + (m2 >> 1), am2) << 16 >> 16;
    const v3 = this.computeVolume(o3, (o3.phase >>> 10) + (m3 >> 1), am3) << 16 >> 16;
    const m4sum = alg <= 4 ? m4 + v3 : m4;
    const v4 = this.computeVolume(o4, (o4.phase >>> 10) + (m4sum >> 1), am4);
    let acc = 0;
    const cl = (v) => v < -256 ? -256 : v > 255 ? 255 : v;
    if (alg === 7) acc = cl(acc + (a1 >> 5));
    if (alg >= 5) acc = cl(acc + (v3 >> 5));
    if (alg >= 4) acc = cl(acc + (v2 >> 5));
    acc = cl(acc + (v4 >> 5));
    h1[h + 2] = h1[h + 1]; h1[h + 1] = a1; h1[h] = v1;
    this.hist2[ch] = v2;
    return acc;
  }

  // one sample: the ladder chip's left and right (a 16-bit range pair), as Ym2612.GenerateSample with SlotPipeline and Ladder
  generate(out) {
    this.clockEngine();
    let accL = 0, accR = 0;
    const lastFm = this.dacEnable ? 5 : 6, regs = this.regs;
    for (let ch = 0; ch < lastFm; ch++) {
      let v = 0;
      if ((this.activeChannels >> ch) & 1) v = this.channelValue(ch);
      else { const h = ch * 3, h1 = this.hist1; h1[h + 2] = h1[h + 1]; h1[h + 1] = h1[h]; h1[h] = 0; this.hist2[ch] = 0; }
      const cpan = regs[0xB4 + this.ops[ch * 4].choffs] >> 6 & 3;
      accL += (cpan & 2) ? v + (v < 0 ? -3 : 4) : (v < 0 ? -4 : 4);
      accR += (cpan & 1) ? v + (v < 0 ? -3 : 4) : (v < 0 ? -4 : 4);
    }
    if (this.dacEnable) {
      const dac = (this.dacData << 23) >> 23, pan = regs[0x1B6] >> 6 & 3;
      const dv = dac < 0 ? dac - 3 : dac + 4, off = dac < 0 ? -4 : 4;
      accL += (pan & 2) ? dv : off;
      accR += (pan & 1) ? dv : off;
    }
    out[0] = Math.trunc(accL * 8192 / 390);
    out[1] = Math.trunc(accR * 8192 / 390);
    this.samples++;
  }
}

// ---- the sound unit: YM2612 + PSG mixed as MdSoundUnit mixes them ----
class Chip {
  // opts: {trace}
  constructor(sampleRate) {
    this.sampleRate = sampleRate;
    this.cpuClockHz = NTSC_Z80;
    this.psg = new Psg(sampleRate, NTSC_Z80);
    this.ym = new Ym2612();
    this.fmLeft = new BandLimitedMixer(YM_RATE, sampleRate);
    this.fmRight = new BandLimitedMixer(YM_RATE, sampleRate);
    this.fmClock = 0; this.lastL = 0; this.lastR = 0;
    this.pair = new Int32Array(2);
    this.psgOut = new Int32Array(BL_MASK + 1);
    this.fl = new Float32Array(BL_MASK + 1); this.fr = new Float32Array(BL_MASK + 1);
    this.trace = null;      // [[0, v] PSG write, [1, cpuClocks] PSG run, [2, register (0..0x1FF), v] YM2612 write, [5, n] YM2612 samples, [3, frames] read]: tools/NanoGenesisParity replays it
  }

  psgWrite(v) { if (this.trace) this.trace.push([0, v]); this.psg.write(v); }
  // a YM2612 register (0x000-0x0FF part 1, 0x100-0x1FF part 2): the address port, then the data port
  ymWrite(reg, v) {
    if (this.trace) this.trace.push([2, reg, v]);
    this.ym.write(reg & 0x100 ? 2 : 0, reg & 0xFF); this.ym.write(reg & 0x100 ? 3 : 1, v);
  }
  get ymSamples() { return this.ym.samples; }
  // n YM2612 samples now, each a step into the FM mixers (MdSoundUnit's sink), then the mixers told how far the stream has got
  ymRun(n) {
    if (n <= 0) return;
    if (this.trace) { const t = this.trace, last = t[t.length - 1]; if (last && last[0] === 5) last[1] += n; else t.push([5, n]); }
    const p = this.pair, ym = this.ym;
    for (let i = 0; i < n; i++) {
      ym.generate(p);
      const l = f32(p[0] * (1 / 32768)), r = f32(p[1] * (1 / 32768));
      this.fmClock++;
      if (l !== this.lastL) { this.fmLeft.step(this.fmClock, f32(l - this.lastL)); this.lastL = l; }
      if (r !== this.lastR) { this.fmRight.step(this.fmClock, f32(r - this.lastR)); this.lastR = r; }
    }
    this.fmLeft.advance(this.fmClock); this.fmRight.advance(this.fmClock);
  }
  psgRun(cpuClocks) { if (this.trace) this.trace.push([1, cpuClocks]); this.psg.run(cpuClocks); }

  // Fills L[off..off+n) and R (null: mono = the left side) with the next n samples (MdSoundUnit.ReadSamples: PSG short + (int)(FM x 32768), clipped)
  render(L, R, off, n) {
    const c = this.psg.clocksFor(n);
    if (c > 0) this.psgRun(c);
    while (this.fmLeft.available < n) this.ymRun(1);      // one FM sample at a time until the mixer has n finished
    if (this.trace) this.trace.push([3, n]);
    const ps = this.psgOut, fl = this.fl, fr = this.fr;
    this.psg.readShorts(ps, n);
    this.fmLeft.read(fl, 0, n); this.fmRight.read(fr, 0, n);
    for (let i = 0; i < n; i++) {
      let l = ps[i] + (f32(fl[i] * 32768) | 0);
      l = l < -32768 ? -32768 : l > 32767 ? 32767 : l;
      L[off + i] = l / 32768;
      if (R) {
        let r = ps[i] + (f32(fr[i] * 32768) | 0);
        r = r < -32768 ? -32768 : r > 32767 ? 32767 : r;
        R[off + i] = r / 32768;
      }
    }
  }
}
