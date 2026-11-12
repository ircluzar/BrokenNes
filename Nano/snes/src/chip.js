// ---- chip: the SNES S-DSP, as BrokenNes plays it ----------------------------------------------------------------------------------
//
// A transcription of BrokenNes's Windows/NesEmulator/snes/DSP_SFC.cs (the stock SNES sound generator, the S-DSP of the SFC unit; a C# port of the logic
// of blargg's snes_spc 0.9.0 SPC_DSP.cpp, LGPL 2.1 or later: the Gauss, RatePeriod and RateOffset tables are its) and of Core/StreamResampler.cs (the
// 4-point Hermite resampler the BrokenNes2 plugin plays the S-DSP's 32 kHz through). No SPC700: the engine writes the DSP registers and the sound RAM
// itself, as a sound driver would. Sample for sample the same as the C# (Nano/snes/test/parity.mjs): the DSP is integer; the resampler's floats are
// float32 here as there (Math.fround after every operation, in C#'s order).
// Left out (never used by the engine, never on a SNES): the WideMess voices, cubic interpolation, the push-into-the-game overrides, Butchering.

const f32 = Math.fround;
const DSP_RATE = 32000;
const MVOLL = 0x0C, MVOLR = 0x1C, EVOLL = 0x2C, EVOLR = 0x3C, KON = 0x4C, KOFF = 0x5C, FLG = 0x6C, ENDX = 0x7C, EFB = 0x0D, PMON = 0x2D, NON = 0x3D, EON = 0x4D, DIR = 0x5D, ESA = 0x6D, EDL = 0x7D;
const ENV_RELEASE = 0, ENV_ATTACK = 1, ENV_DECAY = 2, ENV_SUSTAIN = 3;

const GAUSS = new Int16Array([
     0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   2,   2,   2,   2,   2,
     2,   2,   3,   3,   3,   3,   3,   4,   4,   4,   4,   4,   5,   5,   5,   5,   6,   6,   6,   6,   7,   7,   7,   8,   8,   8,   9,   9,   9,  10,  10,  10,
    11,  11,  11,  12,  12,  13,  13,  14,  14,  15,  15,  15,  16,  16,  17,  17,  18,  19,  19,  20,  20,  21,  21,  22,  23,  23,  24,  24,  25,  26,  27,  27,
    28,  29,  29,  30,  31,  32,  32,  33,  34,  35,  36,  36,  37,  38,  39,  40,  41,  42,  43,  44,  45,  46,  47,  48,  49,  50,  51,  52,  53,  54,  55,  56,
    58,  59,  60,  61,  62,  64,  65,  66,  67,  69,  70,  71,  73,  74,  76,  77,  78,  80,  81,  83,  84,  86,  87,  89,  90,  92,  94,  95,  97,  99, 100, 102,
   104, 106, 107, 109, 111, 113, 115, 117, 118, 120, 122, 124, 126, 128, 130, 132, 134, 137, 139, 141, 143, 145, 147, 150, 152, 154, 156, 159, 161, 163, 166, 168,
   171, 173, 175, 178, 180, 183, 186, 188, 191, 193, 196, 199, 201, 204, 207, 210, 212, 215, 218, 221, 224, 227, 230, 233, 236, 239, 242, 245, 248, 251, 254, 257,
   260, 263, 267, 270, 273, 276, 280, 283, 286, 290, 293, 297, 300, 304, 307, 311, 314, 318, 321, 325, 328, 332, 336, 339, 343, 347, 351, 354, 358, 362, 366, 370,
   374, 378, 381, 385, 389, 393, 397, 401, 405, 410, 414, 418, 422, 426, 430, 434, 439, 443, 447, 451, 456, 460, 464, 469, 473, 477, 482, 486, 491, 495, 499, 504,
   508, 513, 517, 522, 527, 531, 536, 540, 545, 550, 554, 559, 563, 568, 573, 577, 582, 587, 592, 596, 601, 606, 611, 615, 620, 625, 630, 635, 640, 644, 649, 654,
   659, 664, 669, 674, 678, 683, 688, 693, 698, 703, 708, 713, 718, 723, 728, 732, 737, 742, 747, 752, 757, 762, 767, 772, 777, 782, 787, 792, 797, 802, 806, 811,
   816, 821, 826, 831, 836, 841, 846, 851, 855, 860, 865, 870, 875, 880, 884, 889, 894, 899, 904, 908, 913, 918, 923, 927, 932, 937, 941, 946, 951, 955, 960, 965,
   969, 974, 978, 983, 988, 992, 997,1001,1005,1010,1014,1019,1023,1027,1032,1036,1040,1045,1049,1053,1057,1061,1066,1070,1074,1078,1082,1086,1090,1094,1098,1102,
  1106,1109,1113,1117,1121,1125,1128,1132,1136,1139,1143,1146,1150,1153,1157,1160,1164,1167,1170,1174,1177,1180,1183,1186,1190,1193,1196,1199,1202,1205,1207,1210,
  1213,1216,1219,1221,1224,1227,1229,1232,1234,1237,1239,1241,1244,1246,1248,1251,1253,1255,1257,1259,1261,1263,1265,1267,1269,1270,1272,1274,1275,1277,1279,1280,
  1282,1283,1284,1286,1287,1288,1290,1291,1292,1293,1294,1295,1296,1297,1297,1298,1299,1300,1300,1301,1302,1302,1303,1303,1303,1304,1304,1304,1304,1304,1305,1305,
]);
const COUNTER_RANGE = 2048 * 5 * 3;
const RATE_PERIOD = [COUNTER_RANGE + 1, 2048, 1536, 1280, 1024, 768, 640, 512, 384, 320, 256, 192, 160, 128, 96, 80, 64, 48, 40, 32, 24, 20, 16, 12, 10, 8, 6, 5, 4, 3, 2, 1];
const RATE_OFFSET = [1, 0, 1040, 536, 0, 1040, 536, 0, 1040, 536, 0, 1040, 536, 0, 1040, 536, 0, 1040, 536, 0, 1040, 536, 0, 1040, 536, 0, 1040, 536, 0, 1040, 0, 0];

function clamp16(v) { return v < -32768 ? -32768 : v > 32767 ? 32767 : v; }
const s16 = (v) => (v << 16) >> 16;
const s8 = (v) => (v << 24) >> 24;

class DspVoice {
  constructor() { this.buf = new Int32Array(24); this.bufPos = 0; this.interpPos = 0; this.brrAddr = 0; this.brrOffset = 1; this.konDelay = 0; this.env = 0; this.hiddenEnv = 0; this.mode = ENV_RELEASE; }
}

// ---- DSP_SFC.cs ----
class Dsp {
  constructor(aram) {
    this.ram = aram;
    this.regs = new Uint8Array(128);
    this.voices = []; for (let i = 0; i < 8; i++) this.voices.push(new DspVoice());
    this.ratePhase = new Int32Array(32);
    this.echoHist = new Int32Array(32);     // [16][2]
    this.output = null;                      // (l, r) per sample
    this.reset();
  }
  reset() {
    this.regs.fill(0); this.regs[FLG] = 0xE0;
    for (const v of this.voices) { v.buf.fill(0); v.bufPos = v.interpPos = v.brrAddr = v.konDelay = v.env = v.hiddenEnv = 0; v.brrOffset = 1; v.mode = ENV_RELEASE; }
    this.newKon = this.kon = this.koff = 0;
    this.everyOther = false; this.counter = 0; this.noise = 0x4000;
    for (let r = 0; r < 32; r++) this.ratePhase[r] = (this.counter + RATE_OFFSET[r]) % RATE_PERIOD[r];
    this.echoOffset = this.echoLength = this.echoHistPos = 0;
    this.echoHist.fill(0);
  }
  write(addr, value) {
    addr &= 0x7F;
    this.regs[addr] = value;
    if (addr === KON) this.newKon = value;
    else if (addr === ENDX) this.regs[ENDX] = 0;
  }
  rateFires(rate) { return rate !== 0 && this.ratePhase[rate] === 0; }

  runSample() {
    const regs = this.regs, ram = this.ram;
    this.everyOther = !this.everyOther;
    if (this.everyOther) { this.newKon &= ~this.kon & 0xFF; this.kon = this.newKon; this.koff = regs[KOFF]; }
    if (--this.counter < 0) this.counter = COUNTER_RANGE - 1;
    const phase = this.ratePhase;
    for (let r = 1; r < 32; r++) if (--phase[r] < 0) phase[r] = RATE_PERIOD[r] - 1;
    if (this.rateFires(regs[FLG] & 0x1F)) {
      const feedback = (this.noise << 13) ^ (this.noise << 14);
      this.noise = (feedback & 0x4000) ^ (this.noise >> 1);
    }
    const pmon = regs[PMON] & 0xFE, non = regs[NON], eon = regs[EON];
    let mainL = 0, mainR = 0, echoL = 0, echoR = 0, prevOutput = 0, endx = regs[ENDX];
    for (let i = 0; i < 8; i++) {
      const v = this.voices[i], bit = 1 << i, r = i << 4;
      let dirEntry = (regs[DIR] * 0x100 + regs[r + 4] * 4) & 0xFFFF;
      if (v.konDelay === 0) dirEntry += 2;
      const nextAddr = ram[dirEntry & 0xFFFF] | ram[(dirEntry + 1) & 0xFFFF] << 8;
      let pitch = regs[r + 2] | (regs[r + 3] & 0x3F) << 8;
      const brrByte = ram[(v.brrAddr + v.brrOffset) & 0xFFFF];
      let header = ram[v.brrAddr & 0xFFFF];
      const usePmod = (pmon & bit) !== 0, useNoise = (non & bit) !== 0, useEcho = (eon & bit) !== 0;
      const volL = s8(regs[r]), volR = s8(regs[r + 1]), adsr1 = regs[r + 5], adsr2 = regs[r + 6], gain = regs[r + 7];
      if (usePmod) pitch += ((prevOutput >> 5) * pitch) >> 10;
      if (v.konDelay !== 0) {
        if (v.konDelay === 5) { v.brrAddr = nextAddr; v.brrOffset = 1; v.bufPos = 0; header = 0; }
        v.env = 0; v.hiddenEnv = 0;
        v.interpPos = (--v.konDelay & 3) !== 0 ? 0x4000 : 0;
        pitch = 0;
      }
      let output;
      if (v.env === 0) output = 0;
      else {
        output = useNoise ? s16(this.noise * 2) : this.interpolate(v);
        output = ((output * v.env) >> 11) & ~1;
      }
      regs[r + 8] = (v.env >> 4) & 0xFF;
      if ((regs[FLG] & 0x80) !== 0 || (header & 3) === 1) { v.mode = ENV_RELEASE; v.env = 0; }
      if (this.everyOther) {
        if (this.koff & bit) v.mode = ENV_RELEASE;
        if (this.kon & bit) { v.konDelay = 5; v.mode = ENV_ATTACK; }
      }
      if (v.konDelay === 0) this.runEnvelope(v, adsr1, adsr2, gain);
      let looped = false;
      if (v.interpPos >= 0x4000) {
        this.decodeBrr(v, header, brrByte);
        if ((v.brrOffset += 2) >= 9) {
          v.brrAddr = (v.brrAddr + 9) & 0xFFFF;
          if (header & 1) { v.brrAddr = nextAddr; looped = true; }
          v.brrOffset = 1;
        }
      }
      v.interpPos = Math.min((v.interpPos & 0x3FFF) + pitch, 0x7FFF);
      const ampL = (output * volL) >> 7, ampR = (output * volR) >> 7;
      mainL = clamp16(mainL + ampL); mainR = clamp16(mainR + ampR);
      if (useEcho) { echoL = clamp16(echoL + ampL); echoR = clamp16(echoR + ampR); }
      if (looped) endx |= bit;
      if (v.konDelay === 5) endx &= ~bit & 0xFF;
      regs[r + 9] = (output >> 8) & 0xFF;
      prevOutput = output;
    }
    regs[ENDX] = endx;
    this.runEcho(mainL, mainR, echoL, echoR);
  }

  interpolate(v) {
    const p = (v.interpPos >> 12) + v.bufPos, offset = (v.interpPos >> 4) & 0xFF, fwd = 255 - offset, rev = offset, b = v.buf;
    let out = (GAUSS[fwd] * b[p]) >> 11;
    out += (GAUSS[fwd + 256] * b[p + 1]) >> 11;
    out += (GAUSS[rev + 256] * b[p + 2]) >> 11;
    out = s16(out);
    out += (GAUSS[rev] * b[p + 3]) >> 11;
    return clamp16(out) & ~1;
  }

  decodeBrr(v, header, firstByte) {
    let nybbles = firstByte << 8 | this.ram[(v.brrAddr + v.brrOffset + 1) & 0xFFFF];
    const shift = header >> 4, filter = header & 0x0C;
    let pos = v.bufPos;
    v.bufPos = v.bufPos + 4 >= 12 ? 0 : v.bufPos + 4;
    const b = v.buf;
    for (let i = 0; i < 4; i++, pos++, nybbles <<= 4) {
      let s = s16(nybbles) >> 12;
      s = (s << shift) >> 1;
      if (shift >= 0x0D) s = (s >> 25) << 11;
      const p1 = b[pos + 11], p2 = b[pos + 10] >> 1;
      if (filter >= 8) {
        s += p1 - p2;
        if (filter === 8) { s += p2 >> 4; s += (p1 * -3) >> 6; }
        else { s += (p1 * -13) >> 7; s += (p2 * 3) >> 4; }
      } else if (filter !== 0) { s += p1 >> 1; s += (-p1) >> 5; }
      s = s16(clamp16(s) * 2);
      b[pos] = b[pos + 12] = s;
    }
  }

  runEnvelope(v, adsr1, adsr2, gain) {
    let env = v.env;
    if (v.mode === ENV_RELEASE) { v.env = Math.max(env - 8, 0); return; }
    let rate, envData = adsr2;
    if (adsr1 & 0x80) {
      if (v.mode >= ENV_DECAY) {
        env--; env -= env >> 8;
        rate = envData & 0x1F;
        if (v.mode === ENV_DECAY) rate = ((adsr1 >> 3) & 0x0E) + 0x10;
      } else {
        rate = (adsr1 & 0x0F) * 2 + 1;
        env += rate < 31 ? 0x20 : 0x400;
      }
    } else {
      envData = gain;
      const mode = envData >> 5;
      if (mode < 4) { env = envData * 0x10; rate = 31; }
      else {
        rate = envData & 0x1F;
        if (mode === 4) env -= 0x20;
        else if (mode < 6) { env--; env -= env >> 8; }
        else { env += 0x20; if (mode > 6 && (v.hiddenEnv >>> 0) >= 0x600) env += 0x8 - 0x20; }
      }
    }
    if ((env >> 8) === (envData >> 5) && v.mode === ENV_DECAY) v.mode = ENV_SUSTAIN;
    v.hiddenEnv = env;
    if ((env >>> 0) > 0x7FF) { env = env < 0 ? 0 : 0x7FF; if (v.mode === ENV_ATTACK) v.mode = ENV_DECAY; }
    if (this.rateFires(rate)) v.env = env;
  }

  runEcho(mainL, mainR, echoL, echoR) {
    const regs = this.regs, ram = this.ram, h = this.echoHist;
    const ptr = (regs[ESA] * 0x100 + this.echoOffset) & 0xFFFF;
    this.echoHistPos = (this.echoHistPos + 1) & 7;
    const inL = s16(ram[ptr] | ram[(ptr + 1) & 0xFFFF] << 8) >> 1;
    const inR = s16(ram[(ptr + 2) & 0xFFFF] | ram[(ptr + 3) & 0xFFFF] << 8) >> 1;
    const hp = this.echoHistPos;
    h[hp * 2] = h[(hp + 8) * 2] = inL;
    h[hp * 2 + 1] = h[(hp + 8) * 2 + 1] = inR;
    const firL = this.fir(0), firR = this.fir(1);
    let outL = clamp16(s16((mainL * s8(regs[MVOLL])) >> 7) + s16((firL * s8(regs[EVOLL])) >> 7));
    let outR = clamp16(s16((mainR * s8(regs[MVOLR])) >> 7) + s16((firR * s8(regs[EVOLR])) >> 7));
    const eoL = clamp16(echoL + s16((firL * s8(regs[EFB])) >> 7)) & ~1;
    const eoR = clamp16(echoR + s16((firR * s8(regs[EFB])) >> 7)) & ~1;
    if ((regs[FLG] & 0x20) === 0) {
      ram[ptr] = eoL & 0xFF; ram[(ptr + 1) & 0xFFFF] = (eoL >> 8) & 0xFF;
      ram[(ptr + 2) & 0xFFFF] = eoR & 0xFF; ram[(ptr + 3) & 0xFFFF] = (eoR >> 8) & 0xFF;
    }
    if (this.echoOffset === 0) this.echoLength = (regs[EDL] & 0x0F) * 0x800;
    this.echoOffset += 4;
    if (this.echoOffset >= this.echoLength) this.echoOffset = 0;
    if (regs[FLG] & 0x40) { outL = 0; outR = 0; }
    this.output(s16(outL), s16(outR));
  }

  fir(ch) {
    const h = this.echoHist, hp = this.echoHistPos, regs = this.regs;
    let sum = 0;
    for (let i = 0; i < 7; i++) sum += (h[(hp + i + 1) * 2 + ch] * s8(regs[i * 0x10 + 0x0F])) >> 6;
    sum = s16(sum);
    sum += s16((h[(hp + 8) * 2 + ch] * s8(regs[0x7F])) >> 6);
    return clamp16(sum) & ~1;
  }
}

// ---- Core/StreamResampler.cs ----
class StreamResampler {
  constructor(inputRate, outputRate, capacity = 1 << 15) {
    this.buffer = new Float32Array(capacity);
    this.count = 3; this.position = 1;
    this.step = inputRate / outputRate;
  }
  get available() { return Math.max(0, Math.floor((this.count - 3 - this.position) / this.step) + 1); }
  writeOne(x) { if (this.count >= this.buffer.length) throw new Error('resampler input overflow'); this.buffer[this.count++] = x; }
  read(out, off, n) {
    const b = this.buffer;
    let pos = this.position;
    for (let i = 0; i < n; i++) {
      const k = Math.trunc(pos), t = f32(pos - k);
      const y0 = b[k - 1], y1 = b[k], y2 = b[k + 1], y3 = b[k + 2];
      const c1 = f32(0.5 * f32(y2 - y0));
      const c2 = f32(f32(f32(y0 - f32(2.5 * y1)) + f32(2 * y2)) - f32(0.5 * y3));
      const c3 = f32(f32(0.5 * f32(y3 - y0)) + f32(1.5 * f32(y1 - y2)));
      out[off + i] = f32(f32(f32(f32(f32(f32(c3 * t) + c2) * t) + c1) * t) + y1);
      pos += this.step;
    }
    this.position = pos;
    const drop = Math.min(Math.trunc(pos) - 1, this.count);   // over 4:1 the position can run past the buffered input
    if (drop > 0) { b.copyWithin(0, drop, this.count); this.count -= drop; this.position -= drop; }
  }
}

// ---- the sound unit: the S-DSP with its 64 KB of sound RAM, resampled to the host rate ----
class Chip {
  constructor(sampleRate) {
    this.sampleRate = sampleRate;
    this.aram = new Uint8Array(65536);
    this.dsp = new Dsp(this.aram);
    this.left = new StreamResampler(DSP_RATE, sampleRate); this.right = new StreamResampler(DSP_RATE, sampleRate);
    this.dsp.output = (l, r) => { this.left.writeOne(f32(l * (1 / 32768))); this.right.writeOne(f32(r * (1 / 32768))); };
    this.samples = 0;
    this.scratch = new Float32Array(4096);
    this.trace = null;     // [[0, reg, v] DSP write, [4, addr, [bytes]] sound RAM write, [1, n] DSP samples, [3, frames] read]: tools/NanoSnesParity replays it
  }
  write(reg, v) { if (this.trace) this.trace.push([0, reg, v]); this.dsp.write(reg, v); }
  read(reg) { return this.dsp.regs[reg & 0x7F]; }
  writeRam(addr, bytes) { if (this.trace) this.trace.push([4, addr, Array.from(bytes)]); this.aram.set(bytes, addr); }
  // the DSP's KON / KOFF polls so far (it reads them on every other sample, the first included)
  get polls() { return (this.samples + 1) >> 1; }
  run(n) {
    if (n <= 0) return;
    if (this.trace) { const t = this.trace, last = t[t.length - 1]; if (last && last[0] === 1) last[1] += n; else t.push([1, n]); }
    for (let i = 0; i < n; i++) this.dsp.runSample();
    this.samples += n;
  }
  render(L, R, off, n) {
    while (this.left.available < n) this.run(1);
    if (this.trace) this.trace.push([3, n]);
    this.left.read(L, off, n);
    if (R) this.right.read(R, off, n); else this.right.read(this.scratch, 0, n);
  }
}
