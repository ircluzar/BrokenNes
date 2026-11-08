// ---- chip: the Game Boy sound chip, as gnuboy plays it ----------------------------------------------------------------------------
//
// A transcription of Windows/NesEmulator/gb/GnubSound.cs, the C# port of gnuboy 1.0.3's sound.c (Laguna, Gilgamesh and the gnuboy
// contributors; GPL-2.0, see the credits in Nano/gb/README.md). gnuboy computes one output sample at a time: each channel is a phase
// accumulator advanced once per sample, and length, envelope and sweep are timed per sample too, so the chip runs at the page's own rate
// with no resampler and nothing stepped per CPU cycle. RATE = 2^21 / the rate (integer division, gnuboy's pcm_init) is the number of
// 2.097152 MHz units one sample lasts; the true output rate is 2^21 / RATE, a little above the requested one (the engine retunes for it).
// All integer: the same bits as the C#, sample for sample (Nano/gb/test/parity.mjs).
//
// Kept from gnuboy on purpose (they are its sound): unipolar pulse and noise, a bipolar wave channel, no band limiting, NR50 applied
// swapped and without its +1, writes to wave RAM dropped while the wave channel plays, the wave RAM scramble at its trigger, very high
// periods muted. The engine never reads the registers back, so read masks do not matter here.

const HZ21 = 2097152;
// SqWave of sound.c, flattened [duty * 8 + step]; gnuboy's values are 0 / -1, ANDed with the volume
const SQ = new Int32Array([0, 0, -1, 0, 0, 0, 0, 0, 0, -1, -1, 0, 0, 0, 0, 0, -1, -1, -1, -1, 0, 0, 0, 0, -1, 0, 0, -1, -1, -1, -1, -1]);
const FREQ_TAB = new Int32Array([32768, 16384, 8192, 5461, 4096, 3276, 2730, 2340]);

// noise.h's two tables, rebuilt instead of shipped: their bit streams follow b[n] = b[n-1] ^ b[n-t1] ^ b[n-t2] from an 8 / 16 bit seed
// (found with Berlekamp-Massey; test/engine.test.mjs compares every byte with the tables in GnubSound.cs)
function noiseTable(bytes, seed, seedBits, t1, t2) {
  const b = new Uint8Array(bytes * 8), out = new Uint8Array(bytes);
  for (let i = 0; i < seedBits; i++) b[i] = (seed >> (seedBits - 1 - i)) & 1;
  for (let n = seedBits; n < b.length; n++) b[n] = b[n - 1] ^ b[n - t1] ^ b[n - t2];
  for (let i = 0; i < b.length; i++) out[i >> 3] |= b[i] << (7 - (i & 7));
  return out;
}
const NOISE7 = noiseTable(16, 0xFB, 8, 6, 8);
const NOISE15 = noiseTable(4096, 0xFFFB, 16, 14, 16);
const DMG_WAVE = [0xac, 0xdd, 0xda, 0x48, 0x36, 0x02, 0xcf, 0x16, 0x2c, 0x04, 0xe5, 0x2c, 0xac, 0xdd, 0xda, 0x48];

const NR10 = 0x10, NR11 = 0x11, NR12 = 0x12, NR13 = 0x13, NR14 = 0x14, NR21 = 0x16, NR22 = 0x17, NR23 = 0x18, NR24 = 0x19,
  NR30 = 0x1A, NR31 = 0x1B, NR32 = 0x1C, NR33 = 0x1D, NR34 = 0x1E, NR41 = 0x20, NR42 = 0x21, NR43 = 0x22, NR44 = 0x23,
  NR50 = 0x24, NR51 = 0x25, NR52 = 0x26;

// struct sndchan: 4 channels x 12 fields in one Int32Array (pos is a uint32 in C#; only its bits are used, so an int32 is the same)
const ON = 0, POS = 1, CNT = 2, ENCNT = 3, SWCNT = 4, LEN = 5, ENLEN = 6, SWLEN = 7, SWFREQ = 8, FREQ = 9, ENVOL = 10, ENDIR = 11, CH = 12;

class Chip {
  constructor(hostRate) {
    if (!(hostRate >= 3000 && hostRate <= 1048576)) throw new RangeError('sampleRate must be from 3000 to 1048576');
    this.rate = (HZ21 / hostRate) | 0;       // snd.rate
    this.hz = HZ21 / this.rate;               // the rate gnuboy really makes
    this.hi = new Uint8Array(0x41);           // ram.hi: the sound registers by their low byte
    this.wave = new Uint8Array(16);           // snd.wave: the wave channel's private copy
    this.c = new Int32Array(4 * CH);
    this.reset();
  }

  // sound_reset (a DMG; the engine loads its own waves, so the power-on pattern is never heard)
  reset() {
    this.c.fill(0); this.hi.fill(0); this.hi[0x40] = 0x91;
    this.wave.set(DMG_WAVE); this.hi.set(DMG_WAVE, 0x30);
    this.off();
    this.hi[NR52] = 0xF1;
  }

  off() {
    const h = this.hi;
    this.c.fill(0);
    h[NR10] = 0x80; h[NR11] = 0xBF; h[NR12] = 0xF3; h[NR14] = 0xBF;
    h[NR21] = 0x3F; h[NR22] = 0x00; h[NR24] = 0xBF;
    h[NR30] = 0x7F; h[NR31] = 0xFF; h[NR32] = 0x9F; h[NR34] = 0xBF;
    h[NR41] = 0xFF; h[NR42] = 0x00; h[NR43] = 0x00; h[NR44] = 0xBF;
    h[NR50] = 0x77; h[NR51] = 0xF3; h[NR52] = 0x70;
    this.dirty();
  }

  dirty() {
    const h = this.hi, c = this.c;
    c[SWLEN] = ((h[NR10] >> 4) & 7) << 14;
    c[LEN] = (64 - (h[NR11] & 63)) << 13;
    this.env(0, h[NR12]); this.f1();
    c[CH + LEN] = (64 - (h[NR21] & 63)) << 13;
    this.env(1, h[NR22]); this.f2();
    c[2 * CH + LEN] = (256 - h[NR31]) << 20;
    this.f3();
    c[3 * CH + LEN] = (64 - (h[NR41] & 63)) << 13;
    this.env(3, h[NR42]); this.f4();
  }

  env(n, b) {
    const c = this.c, o = n * CH;
    c[o + ENVOL] = b >> 4;
    const d = (b >> 3) & 1;
    c[o + ENDIR] = d | (d - 1);
    c[o + ENLEN] = (b & 7) << 15;
  }

  f1() { const d = 2048 - (((this.hi[NR14] & 7) << 8) + this.hi[NR13]); this.c[FREQ] = this.rate > (d << 4) ? 0 : ((this.rate << 17) / d) | 0; }
  f2() { const d = 2048 - (((this.hi[NR24] & 7) << 8) + this.hi[NR23]); this.c[CH + FREQ] = this.rate > (d << 4) ? 0 : ((this.rate << 17) / d) | 0; }
  f3() { const d = 2048 - (((this.hi[NR34] & 7) << 8) + this.hi[NR33]); this.c[2 * CH + FREQ] = this.rate > (d << 3) ? 0 : ((this.rate << 21) / d) | 0; }
  f4() {
    let f = (FREQ_TAB[this.hi[NR43] & 7] >> (this.hi[NR43] >> 4)) * this.rate;
    if ((f >> 18) !== 0) f = 1 << 18;
    this.c[3 * CH + FREQ] = f;
  }

  // sound_write (a register by its low byte, 0x10..0x3F)
  write(r, b) {
    const h = this.hi, c = this.c;
    if ((h[NR52] & 128) === 0 && r !== NR52) return;
    if ((r & 0xF0) === 0x30) {
      if (c[2 * CH + ON] === 0) this.wave[r - 0x30] = h[r] = b;
      return;
    }
    switch (r) {
      case NR10: h[r] = b; c[SWLEN] = ((b >> 4) & 7) << 14; c[SWFREQ] = ((h[NR14] & 7) << 8) + h[NR13]; break;
      case NR11: h[r] = b; c[LEN] = (64 - (b & 63)) << 13; break;
      case NR12: h[r] = b; this.env(0, b); break;
      case NR13: h[r] = b; this.f1(); break;
      case NR14:
        h[r] = b; this.f1();
        if (b & 128) {
          c[SWCNT] = 0; c[SWFREQ] = ((h[NR14] & 7) << 8) + h[NR13]; this.env(0, h[NR12]);
          if (c[ON] === 0) c[POS] = 0;
          c[ON] = 1; c[CNT] = 0; c[ENCNT] = 0;
        }
        break;
      case NR21: h[r] = b; c[CH + LEN] = (64 - (b & 63)) << 13; break;
      case NR22: h[r] = b; this.env(1, b); break;
      case NR23: h[r] = b; this.f2(); break;
      case NR24:
        h[r] = b; this.f2();
        if (b & 128) { this.env(1, h[NR22]); if (c[CH + ON] === 0) c[CH + POS] = 0; c[CH + ON] = 1; c[CH + CNT] = 0; c[CH + ENCNT] = 0; }
        break;
      case NR30: h[r] = b; if ((b & 128) === 0) c[2 * CH + ON] = 0; break;
      case NR31: h[r] = b; c[2 * CH + LEN] = (256 - b) << 13; break;
      case NR32: h[r] = b; break;
      case NR33: h[r] = b; this.f3(); break;
      case NR34:
        h[r] = b; this.f3();
        if (b & 128) {
          const o = 2 * CH;
          if (c[o + ON] === 0) c[o + POS] = 0;
          c[o + CNT] = 0; c[o + ON] = h[NR30] >> 7;
          if (c[o + ON]) for (let i = 0; i < 16; i++) h[i + 0x30] = 0x13 ^ h[i + 0x31];
        }
        break;
      case NR41: h[r] = b; c[3 * CH + LEN] = (64 - (b & 63)) << 13; break;
      case NR42: h[r] = b; this.env(3, b); break;
      case NR43: h[r] = b; this.f4(); break;
      case NR44:
        h[r] = b;
        if (b & 128) { const o = 3 * CH; this.env(3, h[NR42]); c[o + ON] = 1; c[o + POS] = 0; c[o + CNT] = 0; c[o + ENCNT] = 0; }
        break;
      case NR50: case NR51: h[r] = b; break;
      case NR52: h[r] = b; if ((b & 128) === 0) this.off(); break;
    }
  }

  // sound_mix for n samples: l and r (gnuboy's -128..127) go to L[off + i] and R[off + i] as l / 128 (what APU_GNUB's 16-bit samples are
  // over 32768). R null: mono, L gets (l + r) / 256. The channel state lives in locals for the loop.
  render(L, R, off, n) {
    const h = this.hi, c = this.c, RATE = this.rate, wave = this.wave, end = off + n;
    let on1 = c[ON], on2 = c[CH + ON], on3 = c[2 * CH + ON], on4 = c[3 * CH + ON];
    if ((on1 | on2 | on3 | on4) === 0) {
      // every channel off: gnuboy mixes l = r = 0; nothing else moves
      L.fill(0, off, end); if (R) R.fill(0, off, end);
      h[NR52] = h[NR52] & 0xF0;
      return;
    }
    let pos1 = c[POS], cnt1 = c[CNT], encnt1 = c[ENCNT], swcnt = c[SWCNT], swfreq = c[SWFREQ], freq1 = c[FREQ], envol1 = c[ENVOL];
    const len1 = c[LEN], enlen1 = c[ENLEN], endir1 = c[ENDIR], swlen = c[SWLEN];
    let pos2 = c[CH + POS], cnt2 = c[CH + CNT], encnt2 = c[CH + ENCNT], envol2 = c[CH + ENVOL];
    const len2 = c[CH + LEN], enlen2 = c[CH + ENLEN], endir2 = c[CH + ENDIR], freq2 = c[CH + FREQ];
    let pos3 = c[2 * CH + POS], cnt3 = c[2 * CH + CNT];
    const len3 = c[2 * CH + LEN], freq3 = c[2 * CH + FREQ];
    let pos4 = c[3 * CH + POS], cnt4 = c[3 * CH + CNT], encnt4 = c[3 * CH + ENCNT], envol4 = c[3 * CH + ENVOL];
    const len4 = c[3 * CH + LEN], enlen4 = c[3 * CH + ENLEN], endir4 = c[3 * CH + ENDIR], freq4 = c[3 * CH + FREQ];
    const duty1 = (h[NR11] >> 6) << 3, duty2 = (h[NR21] >> 6) << 3;
    const lc1 = h[NR14] & 64, lc2 = h[NR24] & 64, lc3 = h[NR34] & 64, lc4 = h[NR44] & 64;
    const sh3 = (h[NR32] & 96) ? 3 - ((h[NR32] >> 5) & 3) : -1;
    const nz = (h[NR43] & 8) ? NOISE7 : NOISE15, nzMask = (h[NR43] & 8) ? 15 : 4095;
    const p = h[NR51], ml = h[NR50] & 7, mr = (h[NR50] & 0x70) >> 4;
    for (let i = off; i < end; i++) {
      let l = 0, r = 0, s;
      if (on1) {
        s = SQ[duty1 + ((pos1 >> 18) & 7)] & envol1;
        pos1 = (pos1 + freq1) | 0;
        if (lc1 && (cnt1 += RATE) >= len1) on1 = 0;
        if (enlen1 && (encnt1 += RATE) >= enlen1) { encnt1 -= enlen1; envol1 += endir1; if (envol1 < 0) envol1 = 0; if (envol1 > 15) envol1 = 15; }
        if (swlen && (swcnt += RATE) >= swlen) {
          swcnt -= swlen;
          let f = swfreq;
          const sn = h[NR10] & 7;
          if (h[NR10] & 8) f -= f >> sn; else f += f >> sn;
          if (f > 2047) on1 = 0;
          else {
            swfreq = f; h[NR13] = f; h[NR14] = (h[NR14] & 0xF8) | (f >> 8);
            const d = 2048 - f;
            freq1 = RATE > (d << 4) ? 0 : ((RATE << 17) / d) | 0;
          }
        }
        s <<= 2;
        if (p & 1) r += s;
        if (p & 16) l += s;
      }
      if (on2) {
        s = SQ[duty2 + ((pos2 >> 18) & 7)] & envol2;
        pos2 = (pos2 + freq2) | 0;
        if (lc2 && (cnt2 += RATE) >= len2) on2 = 0;
        if (enlen2 && (encnt2 += RATE) >= enlen2) { encnt2 -= enlen2; envol2 += endir2; if (envol2 < 0) envol2 = 0; if (envol2 > 15) envol2 = 15; }
        s <<= 2;
        if (p & 2) r += s;
        if (p & 32) l += s;
      }
      if (on3) {
        s = wave[(pos3 >> 22) & 15];
        s = ((pos3 & 0x200000) ? s & 15 : s >> 4) - 8;
        pos3 = (pos3 + freq3) | 0;
        if (lc3 && (cnt3 += RATE) >= len3) on3 = 0;
        s = sh3 >= 0 ? s << sh3 : 0;
        if (p & 4) r += s;
        if (p & 64) l += s;
      }
      if (on4) {
        s = 1 & (nz[(pos4 >> 20) & nzMask] >> (7 - ((pos4 >> 17) & 7)));
        s = (-s) & envol4;
        pos4 = (pos4 + freq4) | 0;
        if (lc4 && (cnt4 += RATE) >= len4) on4 = 0;
        if (enlen4 && (encnt4 += RATE) >= enlen4) { encnt4 -= enlen4; envol4 += endir4; if (envol4 < 0) envol4 = 0; if (envol4 > 15) envol4 = 15; }
        s += s << 1;
        if (p & 8) r += s;
        if (p & 128) l += s;
      }
      l = (l * ml) >> 4; r = (r * mr) >> 4;
      if (l > 127) l = 127; else if (l < -128) l = -128;
      if (r > 127) r = 127; else if (r < -128) r = -128;
      if (R) { L[i] = l / 128; R[i] = r / 128; } else L[i] = (l + r) / 256;
    }
    c[ON] = on1; c[POS] = pos1; c[CNT] = cnt1; c[ENCNT] = encnt1; c[SWCNT] = swcnt; c[SWFREQ] = swfreq; c[FREQ] = freq1; c[ENVOL] = envol1;
    c[CH + ON] = on2; c[CH + POS] = pos2; c[CH + CNT] = cnt2; c[CH + ENCNT] = encnt2; c[CH + ENVOL] = envol2;
    c[2 * CH + ON] = on3; c[2 * CH + POS] = pos3; c[2 * CH + CNT] = cnt3;
    c[3 * CH + ON] = on4; c[3 * CH + POS] = pos4; c[3 * CH + CNT] = cnt4; c[3 * CH + ENCNT] = encnt4; c[3 * CH + ENVOL] = envol4;
    h[NR52] = (h[NR52] & 0xF0) | on1 | (on2 << 1) | (on3 << 2) | (on4 << 3);
  }
}
