// ---- chip: the DOS PC's sound (an AdLib on MAME 0.36's OPL2 + the PC speaker), as BrokenNes plays it --------------------------------
//
// A transcription of BrokenNes's Windows/NesEmulator/sega/Mame036Opl.cs (MAME 0.36's fmopl.c YM3812 core, 1999, Tatsuyuki Satoh and the MAME team,
// ported with its quirks kept: the x86 console's AdLib engine "M036", the user's pick for Nano DOS) and of the x86 console's PC speaker
// (x86/PcSpeaker.cs: port 61h AND the 8254's channel 2 in mode 3, four points averaged per 44.1 kHz sample), mixed as PcSession.ReadSamples mixes
// them (speaker + AdLib, saturated to 16 bits, at 44.1 kHz), then resampled to the host rate by Core/StreamResampler.cs (4-point Hermite).
// Sample for sample the same as the C# (Nano/dos/test/parity.mjs). The OPL2's tables are built here with Math.pow / sin / log10 exactly as the C#
// builds them; parity.mjs checks them against the C# tables entry for entry, and the browser test against Node's.
// The one-sample pipeline of Opl2.cs's engine path is the identity at 44.1 kHz (output m = engine sample m), so it is not modelled.

const f32 = Math.fround;
const PC_RATE = 44100, OPL_CLOCK = 3579545, PIT_FREQ = 1193182;

// ---- Mame036Opl.cs ----
const OPL_ARRATE = 141280, OPL_DRRATE = 1956000, FREQ_BITS = 24, FREQ_RATE = 1 << (FREQ_BITS - 20), TL_BITS = FREQ_BITS + 2;
const OPL_OUTSB = TL_BITS + 3 - 16, OPL_MAXOUT = 0x7fff << OPL_OUTSB, OPL_MINOUT = -0x8000 << OPL_OUTSB;
const SIN_ENT = 2048, ENV_BITS = 16, EG_ENT = 4096, EG_OFF = (2 * EG_ENT) << ENV_BITS, EG_DED = EG_OFF, EG_DST = EG_ENT << ENV_BITS, EG_AED = EG_DST, EG_AST = 0;
const EG_STEP = 96.0 / EG_ENT, VIB_ENT = 512, VIB_SHIFT = 32 - 9, AMS_ENT = 512, AMS_SHIFT = 32 - 9, VIB_RATE = 256;
const ENV_MOD_RR = 0, ENV_MOD_DR = 1, ENV_MOD_AR = 2, TL_MAX = EG_ENT * 2;
const SLOT_ARRAY = [0, 2, 4, 1, 3, 5, -1, -1, 6, 8, 10, 7, 9, 11, -1, -1, 12, 14, 16, 13, 15, 17, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1];
const KSL_TABLE = (() => {
  const ML = 0.1875 * 2 / EG_STEP;
  const t = [
    0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000,
    0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000, 0.750, 1.125, 1.500, 1.875, 2.250, 2.625, 3.000,
    0.000, 0.000, 0.000, 0.000, 0.000, 1.125, 1.875, 2.625, 3.000, 3.750, 4.125, 4.500, 4.875, 5.250, 5.625, 6.000,
    0.000, 0.000, 0.000, 1.875, 3.000, 4.125, 4.875, 5.625, 6.000, 6.750, 7.125, 7.500, 7.875, 8.250, 8.625, 9.000,
    0.000, 0.000, 3.000, 4.875, 6.000, 7.125, 7.875, 8.625, 9.000, 9.750, 10.125, 10.500, 10.875, 11.250, 11.625, 12.000,
    0.000, 3.000, 6.000, 7.875, 9.000, 10.125, 10.875, 11.625, 12.000, 12.750, 13.125, 13.500, 13.875, 14.250, 14.625, 15.000,
    0.000, 6.000, 9.000, 10.875, 12.000, 13.125, 13.875, 14.625, 15.000, 15.750, 16.125, 16.500, 16.875, 17.250, 17.625, 18.000,
    0.000, 9.000, 12.000, 13.875, 15.000, 16.125, 16.875, 17.625, 18.000, 18.750, 19.125, 19.500, 19.875, 20.250, 20.625, 21.000,
  ];
  return Uint32Array.from(t, x => Math.trunc(x * ML));
})();
const SL_TABLE = Int32Array.from([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 31], d => Math.trunc(d * ((3 / EG_STEP) * (1 << ENV_BITS)) + EG_DST));
const MUL_TABLE = [1, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 20, 24, 24, 30, 30];
const RATE_0 = new Int32Array(16);
const TL_TABLE = new Int32Array(TL_MAX * 2), SIN_TABLE = new Int32Array(SIN_ENT * 4), AMS_TABLE = new Int32Array(AMS_ENT * 2), VIB_TABLE = new Int32Array(VIB_ENT * 2), ENV_CURVE = new Int32Array(2 * EG_ENT + 1);
(() => {
  for (let t = 0; t < EG_ENT - 1; t++) { const rate = ((1 << TL_BITS) - 1) / Math.pow(10, EG_STEP * t / 20); TL_TABLE[t] = Math.trunc(rate); TL_TABLE[TL_MAX + t] = -TL_TABLE[t]; }
  for (let t = EG_ENT - 1; t < TL_MAX; t++) TL_TABLE[t] = TL_TABLE[TL_MAX + t] = 0;
  SIN_TABLE[0] = SIN_TABLE[SIN_ENT / 2] = EG_ENT - 1;
  for (let s = 1; s <= SIN_ENT / 4; s++) {
    let pom = Math.sin(2 * Math.PI * s / SIN_ENT);
    pom = 20 * Math.log10(1 / pom);
    const j = Math.trunc(pom / EG_STEP);
    SIN_TABLE[s] = SIN_TABLE[SIN_ENT / 2 - s] = j;
    SIN_TABLE[SIN_ENT / 2 + s] = SIN_TABLE[SIN_ENT - s] = TL_MAX + j;
  }
  for (let s = 0; s < SIN_ENT; s++) {
    SIN_TABLE[SIN_ENT * 1 + s] = s < (SIN_ENT / 2) ? SIN_TABLE[s] : EG_ENT;
    SIN_TABLE[SIN_ENT * 2 + s] = SIN_TABLE[s % (SIN_ENT / 2)];
    SIN_TABLE[SIN_ENT * 3 + s] = (((s / (SIN_ENT / 4)) | 0) & 1) !== 0 ? EG_ENT : SIN_TABLE[SIN_ENT * 2 + s];
  }
  for (let i = 0; i < EG_ENT; i++) { const pom = Math.pow((EG_ENT - 1 - i) / EG_ENT, 8) * EG_ENT; ENV_CURVE[i] = Math.trunc(pom); ENV_CURVE[(EG_DST >> ENV_BITS) + i] = i; }
  ENV_CURVE[EG_OFF >> ENV_BITS] = EG_ENT - 1;
  for (let i = 0; i < AMS_ENT; i++) { const pom = (1.0 + Math.sin(2 * Math.PI * i / AMS_ENT)) / 2; AMS_TABLE[i] = Math.trunc((1.0 / EG_STEP) * pom); AMS_TABLE[AMS_ENT + i] = Math.trunc((4.8 / EG_STEP) * pom); }
  for (let i = 0; i < VIB_ENT; i++) { const pom = VIB_RATE * 0.06 * Math.sin(2 * Math.PI * i / VIB_ENT); VIB_TABLE[i] = Math.trunc(VIB_RATE + (pom * 0.07)); VIB_TABLE[VIB_ENT + i] = Math.trunc(VIB_RATE + (pom * 0.14)); }
})();
// a fingerprint of every table (the browser test compares a browser's with Node's: a Math.pow or sin that rounds differently would show here)
function oplTableSum() {
  let h = 0;
  for (const t of [TL_TABLE, SIN_TABLE, AMS_TABLE, VIB_TABLE, ENV_CURVE, KSL_TABLE, SL_TABLE]) for (let i = 0; i < t.length; i++) h = (Math.imul(h, 31) + (t[i] | 0)) | 0;
  return h >>> 0;
}

class OplSlot {
  constructor() {
    this.TL = 0; this.TLL = 0; this.KSR = 0; this.AR = RATE_0; this.DR = RATE_0; this.RR = RATE_0; this.ARo = 0; this.DRo = 0; this.RRo = 0; this.SL = 0;
    this.ksl = 0; this.ksr = 0; this.mul = 0; this.Cnt = 0; this.Incr = 0; this.eg_typ = 0; this.evm = 0; this.evc = 0; this.eve = 0; this.evs = 0;
    this.evsa = 0; this.evsd = 0; this.evsr = 0; this.ams = 0; this.vib = 0; this.wavetable = 0;
  }
}
class OplChannel {
  constructor() { this.SLOT = [new OplSlot(), new OplSlot()]; this.CON = 0; this.FB = 0; this.connect1Carrier = false; this.op1_out = new Int32Array(2); this.block_fnum = 0; this.kcode = 0; this.fc = 0; this.ksl_base = 0; this.keyon = 0; }
}

class Mame036Opl {
  constructor(clock, rate) {
    this.P_CH = []; for (let c = 0; c < 9; c++) this.P_CH.push(new OplChannel());
    this.clock = clock; this.rate = rate;
    this.freqbase = rate !== 0 ? (clock / rate) / 72 : 0;
    this.AR_TABLE = new Int32Array(76); this.DR_TABLE = new Int32Array(76); this.FN_TABLE = new Uint32Array(1024);
    this.address = 0; this.status = 0; this.statusmask = 0; this.rythm = 0; this.wavesel = 0; this.mode = 0;
    this.ams_table = 0; this.vib_table = 0; this.amsCnt = 0; this.vibCnt = 0;
    this.cached = false; this.c_ams_table = 0; this.c_vib_table = 0; this.c_amsIncr = 0; this.c_vibIncr = 0;
    this.outd = 0; this.ams = 0; this.vib = 0; this.feedback2 = 0; this.randSeed = 1;
    this.initTimeTables();
    for (let fn = 0; fn < 1024; fn++) this.FN_TABLE[fn] = Math.trunc(this.freqbase * fn * FREQ_RATE * (1 << 7) / 2) >>> 0;
    this.amsIncrInit = rate !== 0 ? Math.trunc(AMS_ENT * (1 << AMS_SHIFT) / rate * 3.7 * (clock / 3600000)) : 0;
    this.vibIncrInit = rate !== 0 ? Math.trunc(VIB_ENT * (1 << VIB_SHIFT) / rate * 6.4 * (clock / 3600000)) : 0;
    this.resetChip();
  }
  initTimeTables() {
    const A = this.AR_TABLE, D = this.DR_TABLE;
    for (let i = 0; i < 4; i++) A[i] = D[i] = 0;
    for (let i = 4; i <= 60; i++) {
      let r = this.freqbase;
      if (i < 60) r *= 1.0 + (i & 3) * 0.25;
      r *= 1 << ((i >> 2) - 1);
      r *= (EG_ENT << ENV_BITS);
      A[i] = Math.trunc(r / OPL_ARRATE); D[i] = Math.trunc(r / OPL_DRRATE);
    }
    for (let i = 60; i < 76; i++) { A[i] = EG_AED - 1; D[i] = D[60]; }
    D[0] = EG_AED - 1; D[75] = 0;
  }
  statusSet(flag) { this.status |= flag; if ((this.status & 0x80) === 0 && (this.status & this.statusmask) !== 0) this.status |= 0x80; }
  statusReset(flag) { this.status &= ~flag & 0xFF; if ((this.status & 0x80) !== 0 && (this.status & this.statusmask) === 0) this.status &= 0x7f; }
  keyOn(s) { s.Cnt = 0; s.evm = ENV_MOD_AR; s.evs = s.evsa; s.evc = EG_AST; s.eve = EG_AED; }
  keyOff(s) { if (s.evm > ENV_MOD_RR) { s.evm = ENV_MOD_RR; if ((s.evc & EG_DST) === 0) s.evc = EG_DST; s.eve = EG_DED; s.evs = s.evsr; } }
  calcSlot(s) {
    if ((s.evc = (s.evc + s.evs) | 0) >= s.eve) {
      switch (s.evm) {
        case ENV_MOD_AR: s.evm = ENV_MOD_DR; s.evc = EG_DST; s.eve = s.SL; s.evs = s.evsd; break;
        case ENV_MOD_DR: s.evc = s.SL; s.eve = EG_DED; if (s.eg_typ !== 0) s.evs = 0; else { s.evm = ENV_MOD_RR; s.evs = s.evsr; } break;
        case ENV_MOD_RR: s.evc = EG_OFF; s.eve = EG_OFF + 1; s.evs = 0; break;
      }
    }
    return (s.TLL + ENV_CURVE[s.evc >> ENV_BITS] + (s.ams !== 0 ? this.ams : 0)) >>> 0;
  }
  calcFcSlot(ch, s) {
    s.Incr = Math.imul(ch.fc, s.mul) >>> 0;
    const ksr = ch.kcode >> s.KSR;
    if (s.ksr !== ksr) { s.ksr = ksr; s.evsa = s.AR[s.ARo + ksr]; s.evsd = s.DR[s.DRo + ksr]; s.evsr = s.RR[s.RRo + ksr]; }
    s.TLL = (s.TL + (ch.ksl_base >>> s.ksl)) | 0;
  }
  setMul(slot, v) { const ch = this.P_CH[(slot / 2) | 0], s = ch.SLOT[slot & 1]; s.mul = MUL_TABLE[v & 0x0f]; s.KSR = (v & 0x10) ? 0 : 2; s.eg_typ = (v & 0x20) >> 5; s.vib = v & 0x40; s.ams = v & 0x80; this.calcFcSlot(ch, s); }
  setKslTl(slot, v) {
    const ch = this.P_CH[(slot / 2) | 0], s = ch.SLOT[slot & 1], ksl = v >> 6;
    s.ksl = ksl !== 0 ? 3 - ksl : 31;
    s.TL = Math.trunc((v & 0x3f) * (0.75 / EG_STEP));
    if ((this.mode & 0x80) === 0) s.TLL = (s.TL + (ch.ksl_base >>> s.ksl)) | 0;
  }
  setArDr(slot, v) {
    const s = this.P_CH[(slot / 2) | 0].SLOT[slot & 1], ar = v >> 4, dr = v & 0x0f;
    if (ar !== 0) { s.AR = this.AR_TABLE; s.ARo = ar << 2; } else { s.AR = RATE_0; s.ARo = 0; }
    s.evsa = s.AR[s.ARo + s.ksr];
    if (s.evm === ENV_MOD_AR) s.evs = s.evsa;
    if (dr !== 0) { s.DR = this.DR_TABLE; s.DRo = dr << 2; } else { s.DR = RATE_0; s.DRo = 0; }
    s.evsd = s.DR[s.DRo + s.ksr];
    if (s.evm === ENV_MOD_DR) s.evs = s.evsd;
  }
  setSlRr(slot, v) {
    const s = this.P_CH[(slot / 2) | 0].SLOT[slot & 1], sl = v >> 4, rr = v & 0x0f;
    s.SL = SL_TABLE[sl];
    if (s.evm === ENV_MOD_DR) s.eve = s.SL;
    s.RR = this.DR_TABLE; s.RRo = rr << 2;
    s.evsr = s.RR[s.RRo + s.ksr];
    if (s.evm === ENV_MOD_RR) s.evs = s.evsr;
  }
  opOut(s, env, con) { return TL_TABLE[SIN_TABLE[s.wavetable + (((s.Cnt + con) >>> 0) >>> 13 & (SIN_ENT - 1))] + env]; }
  step(s) { if (s.vib !== 0) s.Cnt = (s.Cnt + Math.floor((Math.imul(s.Incr, this.vib) >>> 0) / VIB_RATE)) >>> 0; else s.Cnt = (s.Cnt + s.Incr) >>> 0; }
  calcCh(ch) {
    this.feedback2 = 0;
    let s = ch.SLOT[0], env = this.calcSlot(s);
    if (env < EG_ENT - 1) {
      this.step(s);
      let o;
      if (ch.FB !== 0) { const fb1 = (ch.op1_out[0] + ch.op1_out[1]) >> ch.FB; ch.op1_out[1] = ch.op1_out[0]; o = ch.op1_out[0] = this.opOut(s, env, fb1); }
      else o = this.opOut(s, env, 0);
      if (ch.connect1Carrier) this.outd = (this.outd + o) | 0; else this.feedback2 = (this.feedback2 + o) | 0;
    } else { ch.op1_out[1] = ch.op1_out[0]; ch.op1_out[0] = 0; }
    s = ch.SLOT[1]; env = this.calcSlot(s);
    if (env < EG_ENT - 1) { this.step(s); this.outd = (this.outd + this.opOut(s, env, this.feedback2)) | 0; }
  }
  rand() { this.randSeed = (Math.imul(this.randSeed, 214013) + 2531011) >>> 0; return (this.randSeed >>> 16) & 0x7fff; }
  calcRh() {
    const whitenoise = Math.trunc((this.rand() & 1) * (6.0 / EG_STEP));
    const P = this.P_CH, ch6 = P[6], s7_1 = P[7].SLOT[0], s7_2 = P[7].SLOT[1], s8_1 = P[8].SLOT[0], s8_2 = P[8].SLOT[1];
    this.feedback2 = 0;
    let s = ch6.SLOT[0], env = this.calcSlot(s);
    if (env < EG_ENT - 1) {
      this.step(s);
      if (ch6.FB !== 0) { const fb1 = (ch6.op1_out[0] + ch6.op1_out[1]) >> ch6.FB; ch6.op1_out[1] = ch6.op1_out[0]; this.feedback2 = ch6.op1_out[0] = this.opOut(s, env, fb1); }
      else this.feedback2 = this.opOut(s, env, 0);
    } else { this.feedback2 = 0; ch6.op1_out[1] = ch6.op1_out[0]; ch6.op1_out[0] = 0; }
    s = ch6.SLOT[1]; env = this.calcSlot(s);
    if (env < EG_ENT - 1) { this.step(s); this.outd = (this.outd + this.opOut(s, env, this.feedback2) * 2) | 0; }
    const env_sd = (this.calcSlot(s7_2) + whitenoise) >>> 0, env_tam = this.calcSlot(s8_1), env_top = this.calcSlot(s8_2), env_hh = (this.calcSlot(s7_1) + whitenoise) >>> 0;
    const vib = this.vib;
    if (s7_1.vib !== 0) s7_1.Cnt = (s7_1.Cnt + Math.floor((Math.imul(Math.imul(2, s7_1.Incr) >>> 0, vib) >>> 0) / VIB_RATE)) >>> 0; else s7_1.Cnt = (s7_1.Cnt + Math.imul(2, s7_1.Incr)) >>> 0;
    if (s7_2.vib !== 0) s7_2.Cnt = (s7_2.Cnt + Math.floor((Math.imul(Math.imul(P[7].fc, 8) >>> 0, vib) >>> 0) / VIB_RATE)) >>> 0; else s7_2.Cnt = (s7_2.Cnt + Math.imul(P[7].fc, 8)) >>> 0;
    if (s8_1.vib !== 0) s8_1.Cnt = (s8_1.Cnt + Math.floor((Math.imul(s8_1.Incr, vib) >>> 0) / VIB_RATE)) >>> 0; else s8_1.Cnt = (s8_1.Cnt + s8_1.Incr) >>> 0;
    if (s8_2.vib !== 0) s8_2.Cnt = (s8_2.Cnt + Math.floor((Math.imul(Math.imul(P[8].fc, 48) >>> 0, vib) >>> 0) / VIB_RATE)) >>> 0; else s8_2.Cnt = (s8_2.Cnt + Math.imul(P[8].fc, 48)) >>> 0;
    const tone8 = this.opOut(s8_2, whitenoise, 0);
    if (env_sd < EG_ENT - 1) this.outd = (this.outd + this.opOut(s7_1, env_sd, 0) * 8) | 0;
    if (env_tam < EG_ENT - 1) this.outd = (this.outd + this.opOut(s8_1, env_tam, 0) * 2) | 0;
    if (env_top < EG_ENT - 1) this.outd = (this.outd + this.opOut(s7_2, env_top, tone8) * 2) | 0;
    if (env_hh < EG_ENT - 1) this.outd = (this.outd + this.opOut(s7_2, env_hh, tone8) * 2) | 0;
  }
  writeReg(r, v) {
    r &= 0xff; v &= 0xff;
    let slot;
    switch (r & 0xe0) {
      case 0x00:
        switch (r & 0x1f) {
          case 0x01: this.wavesel = v & 0x20; if (this.wavesel === 0) for (let c = 0; c < 9; c++) { this.P_CH[c].SLOT[0].wavetable = 0; this.P_CH[c].SLOT[1].wavetable = 0; } return;
          case 0x02: return;
          case 0x03: return;
          case 0x04:
            if (v & 0x80) this.statusReset(0x7f);
            else { this.statusReset(v & 0x78); this.statusmask = ((~v) & 0x78) | 0x01; this.statusSet(0); this.statusReset(0); }
            return;
        }
        break;
      case 0x20: slot = SLOT_ARRAY[r & 0x1f]; if (slot === -1) return; this.setMul(slot, v); return;
      case 0x40: slot = SLOT_ARRAY[r & 0x1f]; if (slot === -1) return; this.setKslTl(slot, v); return;
      case 0x60: slot = SLOT_ARRAY[r & 0x1f]; if (slot === -1) return; this.setArDr(slot, v); return;
      case 0x80: slot = SLOT_ARRAY[r & 0x1f]; if (slot === -1) return; this.setSlRr(slot, v); return;
      case 0xa0: {
        const P = this.P_CH;
        if (r === 0xbd) {
          const rkey = this.rythm ^ v;
          this.ams_table = (v & 0x80) ? AMS_ENT : 0;
          this.vib_table = (v & 0x40) ? VIB_ENT : 0;
          this.rythm = v & 0x3f;
          if (this.rythm & 0x20) {
            if (rkey & 0x10) { if (v & 0x10) { P[6].op1_out[0] = P[6].op1_out[1] = 0; this.keyOn(P[6].SLOT[0]); this.keyOn(P[6].SLOT[1]); } else { this.keyOff(P[6].SLOT[0]); this.keyOff(P[6].SLOT[1]); } }
            if (rkey & 0x08) { if (v & 0x08) this.keyOn(P[7].SLOT[1]); else this.keyOff(P[7].SLOT[1]); }
            if (rkey & 0x04) { if (v & 0x04) this.keyOn(P[8].SLOT[0]); else this.keyOff(P[8].SLOT[0]); }
            if (rkey & 0x02) { if (v & 0x02) this.keyOn(P[8].SLOT[1]); else this.keyOff(P[8].SLOT[1]); }
            if (rkey & 0x01) { if (v & 0x01) this.keyOn(P[7].SLOT[0]); else this.keyOff(P[7].SLOT[0]); }
          }
          return;
        }
        if ((r & 0x0f) > 8) return;
        const ch = P[r & 0x0f];
        let block_fnum;
        if ((r & 0x10) === 0) block_fnum = (ch.block_fnum & 0x1f00) | v;
        else {
          const keyon = (v >> 5) & 1;
          block_fnum = ((v & 0x1f) << 8) | (ch.block_fnum & 0xff);
          if (ch.keyon !== keyon) {
            if ((ch.keyon = keyon) !== 0) { ch.op1_out[0] = ch.op1_out[1] = 0; this.keyOn(ch.SLOT[0]); this.keyOn(ch.SLOT[1]); }
            else { this.keyOff(ch.SLOT[0]); this.keyOff(ch.SLOT[1]); }
          }
        }
        if (ch.block_fnum !== block_fnum) {
          const blockRv = 7 - (block_fnum >> 10), fnum = block_fnum & 0x3ff;
          ch.block_fnum = block_fnum;
          ch.ksl_base = KSL_TABLE[block_fnum >> 6];
          ch.fc = this.FN_TABLE[fnum] >>> blockRv;
          ch.kcode = (ch.block_fnum >> 9) & 0xFF;
          if ((this.mode & 0x40) !== 0 && (ch.block_fnum & 0x100) !== 0) ch.kcode |= 1;
          this.calcFcSlot(ch, ch.SLOT[0]); this.calcFcSlot(ch, ch.SLOT[1]);
        }
        return;
      }
      case 0xc0: {
        if ((r & 0x0f) > 8) return;
        const ch = this.P_CH[r & 0x0f], feedback = (v >> 1) & 7;
        ch.FB = feedback !== 0 ? (8 + 1) - feedback : 0;
        ch.CON = v & 1; ch.connect1Carrier = ch.CON !== 0;
        return;
      }
      case 0xe0:
        slot = SLOT_ARRAY[r & 0x1f];
        if (slot === -1) return;
        if (this.wavesel !== 0) this.P_CH[(slot / 2) | 0].SLOT[slot & 1].wavetable = (v & 0x03) * SIN_ENT;
        return;
    }
  }
  resetChip() {
    this.mode = 0;
    this.statusReset(0x7f);
    this.writeReg(0x01, 0); this.writeReg(0x02, 0); this.writeReg(0x03, 0); this.writeReg(0x04, 0);
    for (let i = 0xff; i >= 0x20; i--) this.writeReg(i, 0);
    for (let c = 0; c < 9; c++) for (let s = 0; s < 2; s++) { const sl = this.P_CH[c].SLOT[s]; sl.wavetable = 0; sl.evc = EG_OFF; sl.eve = EG_OFF + 1; sl.evs = 0; }
    this.cached = false;
  }
  write(a, v) { if ((a & 1) === 0) this.address = v & 0xFF; else this.writeReg(this.address, v); }
  update() {
    if (!this.cached) { this.cached = true; this.c_amsIncr = this.amsIncrInit; this.c_vibIncr = this.vibIncrInit; this.c_ams_table = this.ams_table; this.c_vib_table = this.vib_table; }
    const rh = (this.rythm & 0x20) !== 0, rEnd = rh ? 6 : 9;
    this.amsCnt = (this.amsCnt + this.c_amsIncr) >>> 0; this.ams = AMS_TABLE[this.c_ams_table + (this.amsCnt >>> AMS_SHIFT)];
    this.vibCnt = (this.vibCnt + this.c_vibIncr) >>> 0; this.vib = VIB_TABLE[this.c_vib_table + (this.vibCnt >>> VIB_SHIFT)];
    this.outd = 0;
    for (let c = 0; c < rEnd; c++) this.calcCh(this.P_CH[c]);
    if (rh) this.calcRh();
    const data = this.outd > OPL_MAXOUT ? OPL_MAXOUT : this.outd < OPL_MINOUT ? OPL_MINOUT : this.outd;
    return (data >> OPL_OUTSB) << 16 >> 16;
  }
}

// ---- Core/StreamResampler.cs (as Nano SNES) ----
class StreamResampler {
  constructor(inputRate, outputRate, capacity = 1 << 15) { this.buffer = new Float32Array(capacity); this.count = 3; this.position = 1; this.step = inputRate / outputRate; }
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

// ---- the sound: the AdLib (port 388h / 389h) and the speaker (8254 channel 2 + port 61h) on one 44.1 kHz timeline ----
class Chip {
  constructor(sampleRate) {
    this.sampleRate = sampleRate;
    this.opl = new Mame036Opl(OPL_CLOCK, PC_RATE);
    this.out = new StreamResampler(PC_RATE, sampleRate);
    this.samples = 0;                 // 44.1 kHz samples made
    this.spkOn = false; this.spkCount = 0x10000; this.spkLoad = 0;
    this.scratch = new Float32Array(4096);
    this.trace = null;      // [[0, reg, v] AdLib write, [6, on, count] speaker, [1, n] 44.1 kHz samples, [3, frames] read]: tools/NanoDosParity replays it
  }
  oplWrite(reg, v) { if (this.trace) this.trace.push([0, reg, v]); this.opl.write(0, reg); this.opl.write(1, v); }
  // the speaker: on (port 61h = 3) or off (0), and channel 2's count (mode 3, written low then high byte: the 8254 reloads, its phase starts now)
  speaker(on, count) {
    if (this.trace) this.trace.push([6, on ? 1 : 0, count]);
    if (count !== this.spkCount || !this.spkOn && on) { this.spkCount = count; this.spkLoad = pitOfSample(this.samples); }
    this.spkOn = on;
  }
  run(n) {
    if (n <= 0) return;
    if (this.trace) { const t = this.trace, last = t[t.length - 1]; if (last && last[0] === 1) last[1] += n; else t.push([1, n]); }
    for (let i = 0; i < n; i++) {
      let spk = 0;
      if (this.spkOn) {
        const t0 = pitOfSample(this.samples), t1 = pitOfSample(this.samples + 1), c = this.spkCount, half = (c + 1) >> 1;
        let level = 0;
        for (let k = 0; k < 4; k++) { let t = t0 + Math.floor((t1 - t0) * k / 4); if (t < this.spkLoad) t = this.spkLoad; if ((t - this.spkLoad) % c < half) level++; }
        spk = (level * 5000 / 4) | 0;
      }
      let s = spk + this.opl.update();
      s = s < -32768 ? -32768 : s > 32767 ? 32767 : s;
      this.out.writeOne(f32(s * (1 / 32768)));
      this.samples++;
    }
  }
  render(L, R, off, n) {
    while (this.out.available < n) this.run(1);
    if (this.trace) this.trace.push([3, n]);
    this.out.read(L, off, n);
    if (R) R.set(L.subarray(off, off + n), off);
  }
}
function pitOfSample(n) { return Math.floor(n * PIT_FREQ / PC_RATE); }
