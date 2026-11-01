// Shared helpers of the Nano tests.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const here = path.dirname(fileURLToPath(import.meta.url));
export const nanoRoot = path.resolve(here, '..');
export const distFile = path.join(nanoRoot, 'dist', 'nano.js');

// Loads a copy of the shipped module from its text (the text can be a mutated one).
export function loadNano(text = fs.readFileSync(distFile, 'utf8')) {
  const m = { exports: {} };
  new Function('module', text)(m);
  return m.exports;
}

// FFT-free pitch / loudness helpers for checking that a sound is what it should be.
export function rms(a, from = 0, to = a.length) { let s = 0; for (let i = from; i < to; i++) s += a[i] * a[i]; return Math.sqrt(s / Math.max(1, to - from)); }
export function peak(a, from = 0, to = a.length) { let p = 0; for (let i = from; i < to; i++) p = Math.max(p, Math.abs(a[i])); return p; }

// Fundamental frequency by autocorrelation (works for the square-ish waves of the chip), in Hz; NaN when there is none.
export function pitchHz(a, rate, from, to, lo = 40, hi = 4000) {
  const n = to - from;
  let best = -1, bestLag = 0;
  const minLag = Math.floor(rate / hi), maxLag = Math.min(Math.floor(rate / lo), (n >> 1) - 1);
  let e0 = 0; for (let i = from; i < to; i++) e0 += a[i] * a[i];
  if (e0 < 1e-9) return NaN;
  for (let lag = minLag; lag <= maxLag; lag++) {
    let s = 0;
    for (let i = from; i + lag < to; i++) s += a[i] * a[i + lag];
    const c = s / e0;
    if (c > best) { best = c; bestLag = lag; }
  }
  // accept the first lag that is nearly as good as the best (avoids picking a multiple of the period)
  for (let lag = minLag; lag <= maxLag; lag++) {
    let s = 0; for (let i = from; i + lag < to; i++) s += a[i] * a[i + lag];
    if (s / e0 > best * 0.93) {
      // refine by parabolic interpolation around lag
      const c = (l) => { let t = 0; for (let i = from; i + l < to; i++) t += a[i] * a[i + l]; return t; };
      const y0 = c(lag - 1), y1 = c(lag), y2 = c(lag + 1);
      const d = y0 - 2 * y1 + y2;
      const off = d !== 0 ? 0.5 * (y0 - y2) / d : 0;
      return rate / (lag + off);
    }
  }
  return rate / bestLag;
}

export const midiHz = (n) => 440 * Math.pow(2, (n - 69) / 12);

// Magnitude spectrum (Hann window, radix-2 FFT) of a[from .. from+n), n a power of two; returns n/2 magnitudes.
export function spectrum(a, from, n) {
  const re = new Float64Array(n), im = new Float64Array(n);
  for (let i = 0; i < n; i++) re[i] = (a[from + i] ?? 0) * (0.5 - 0.5 * Math.cos(2 * Math.PI * i / n));
  for (let i = 1, j = 0; i < n; i++) {
    let bit = n >> 1;
    for (; j & bit; bit >>= 1) j ^= bit;
    j ^= bit;
    if (i < j) { [re[i], re[j]] = [re[j], re[i]]; }
  }
  for (let len = 2; len <= n; len <<= 1) {
    const ang = -2 * Math.PI / len, wr = Math.cos(ang), wi = Math.sin(ang);
    for (let i = 0; i < n; i += len) {
      let cr = 1, ci = 0;
      for (let k = 0; k < len / 2; k++) {
        const ur = re[i + k], ui = im[i + k];
        const vr = re[i + k + len / 2] * cr - im[i + k + len / 2] * ci, vi = re[i + k + len / 2] * ci + im[i + k + len / 2] * cr;
        re[i + k] = ur + vr; im[i + k] = ui + vi; re[i + k + len / 2] = ur - vr; im[i + k + len / 2] = ui - vi;
        const t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t;
      }
    }
  }
  const mag = new Float64Array(n / 2);
  for (let i = 0; i < n / 2; i++) mag[i] = Math.hypot(re[i], im[i]) / n;
  return mag;
}

// Fundamental by counting rising zero crossings over a window (exact for the chip's square, pulse and triangle waves, which cross zero once per
// period once the DC offset is removed); NaN when there are fewer than three.
export function pitchZc(a, rate, from, to) {
  let mean = 0; for (let i = from; i < to; i++) mean += a[i]; mean /= (to - from);
  let first = -1, last = -1, count = 0;
  for (let i = from + 1; i < to; i++) {
    const p = a[i - 1] - mean, q = a[i] - mean;
    if (p < 0 && q >= 0) {
      const t = i - 1 + (-p) / (q - p);
      if (first < 0) first = t;
      last = t; count++;
    }
  }
  return count >= 3 ? (count - 1) * rate / (last - first) : NaN;
}
