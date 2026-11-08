// Shared helpers of the Nano GB tests.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const here = path.dirname(fileURLToPath(import.meta.url));
export const gbRoot = path.resolve(here, '..');
export const distFile = path.join(gbRoot, 'dist', 'nano-gb.js');

export function loadGb(text = fs.readFileSync(distFile, 'utf8')) {
  const m = { exports: {} };
  new Function('module', text)(m);
  return m.exports;
}

export function rms(a, from = 0, to = a.length) { let s = 0; for (let i = from; i < to; i++) s += a[i] * a[i]; return Math.sqrt(s / Math.max(1, to - from)); }
export function peak(a, from = 0, to = a.length) { let p = 0; for (let i = from; i < to; i++) p = Math.max(p, Math.abs(a[i])); return p; }
export const midiHz = (n) => 440 * Math.pow(2, (n - 69) / 12);

// fundamental by rising zero crossings (DC removed); NaN when fewer than three
export function pitchZc(a, rate, from, to) {
  let mean = 0; for (let i = from; i < to; i++) mean += a[i]; mean /= (to - from);
  let first = -1, last = -1, count = 0;
  for (let i = from + 1; i < to; i++) {
    const p = a[i - 1] - mean, q = a[i] - mean;
    if (p < 0 && q >= 0) { const t = i - 1 + (-p) / (q - p); if (first < 0) first = t; last = t; count++; }
  }
  return count >= 3 ? (count - 1) * rate / (last - first) : NaN;
}

// magnitude spectrum (Hann, radix-2), n a power of two
export function spectrum(a, from, n) {
  const re = new Float64Array(n), im = new Float64Array(n);
  for (let i = 0; i < n; i++) re[i] = (a[from + i] ?? 0) * (0.5 - 0.5 * Math.cos(2 * Math.PI * i / n));
  for (let i = 1, j = 0; i < n; i++) { let bit = n >> 1; for (; j & bit; bit >>= 1) j ^= bit; j ^= bit; if (i < j) { [re[i], re[j]] = [re[j], re[i]]; } }
  for (let len = 2; len <= n; len <<= 1) {
    const ang = -2 * Math.PI / len, wr = Math.cos(ang), wi = Math.sin(ang);
    for (let i = 0; i < n; i += len) {
      let cr = 1, ci = 0;
      for (let k = 0; k < len / 2; k++) {
        const ur = re[i + k], ui = im[i + k], h = i + k + len / 2;
        const vr = re[h] * cr - im[h] * ci, vi = re[h] * ci + im[h] * cr;
        re[i + k] = ur + vr; im[i + k] = ui + vi; re[h] = ur - vr; im[h] = ui - vi;
        const t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t;
      }
    }
  }
  const mag = new Float64Array(n / 2);
  for (let i = 0; i < n / 2; i++) mag[i] = Math.hypot(re[i], im[i]) / n;
  return mag;
}

// A tiny Standard MIDI File writer (format 1) for the test songs. tracks: [{events: [[tick, bytes...]], name}], tempo changes go in track 0 as [tick, 'tempo', usPerQuarter]
export function writeMidi(ppq, tracks) {
  const vlq = (n) => { const out = [n & 127]; while ((n >>= 7)) out.unshift((n & 127) | 128); return out; };
  const chunks = [];
  const hdr = Buffer.alloc(14); hdr.write('MThd', 0); hdr.writeUInt32BE(6, 4); hdr.writeUInt16BE(1, 8); hdr.writeUInt16BE(tracks.length, 10); hdr.writeUInt16BE(ppq, 12);
  chunks.push(hdr);
  for (const t of tracks) {
    const evs = t.events.slice().sort((a, b) => a[0] - b[0]);
    const bytes = []; let last = 0;
    for (const e of evs) {
      bytes.push(...vlq(e[0] - last)); last = e[0];
      if (e[1] === 'tempo') bytes.push(0xFF, 0x51, 3, (e[2] >> 16) & 255, (e[2] >> 8) & 255, e[2] & 255);
      else bytes.push(...e.slice(1));
    }
    bytes.push(0, 0xFF, 0x2F, 0);
    const h = Buffer.alloc(8); h.write('MTrk', 0); h.writeUInt32BE(bytes.length, 4);
    chunks.push(h, Buffer.from(bytes));
  }
  return Buffer.concat(chunks);
}

// The test songs (written once into test/midi by parity.mjs --write-songs; the files are committed and read from there)
export function testSongs() {
  const q = 96, n = (tick, ch, note, vel, len) => [[tick, 0x90 | ch, note, vel], [tick + len, 0x80 | ch, note, 0]];
  // song 1: a four-part tune: lead (program 0) with a bend, harmony (program 2), wave bass (program 8), GM drums, a tempo change
  const lead = [[0, 0xC0, 0]], harm = [[0, 0xC1, 2]], bass = [[0, 0xC2, 8]], drums = [];
  const mel = [72, 74, 76, 79, 77, 76, 74, 72, 71, 72, 74, 76, 74, 72, 71, 72];
  mel.forEach((m, i) => lead.push(...n(i * q, 0, m, 100, q - 8)));
  lead.push([7 * q + 20, 0xE0, 0, 80], [7 * q + 60, 0xE0, 0, 64]);
  [64, 65, 67, 64].forEach((m, i) => harm.push(...n(i * 4 * q, 1, m, 70, 4 * q - 20)));
  [36, 36, 43, 41, 36, 36, 43, 43].forEach((m, i) => bass.push(...n(i * 2 * q, 2, m, 110, 2 * q - 30)));
  for (let b = 0; b < 16; b++) { drums.push(...n(b * q, 9, b % 2 ? (b % 4 === 1 ? 40 : 38) : (b % 4 ? 35 : 36), 120, 20)); drums.push(...n(b * q + q / 2, 9, b % 3 ? 42 : 44, 80, 10)); }
  const song1 = writeMidi(q, [{ events: [[0, 'tempo', 500000], [8 * q, 'tempo', 400000]] }, { events: lead }, { events: harm }, { events: bass }, { events: drums }]);
  // song 2: chords on one channel (both pulses, then a third note steals), sound effects on a sidecar channel, running status, note-on velocity 0 as note-off
  const ch0 = [[0, 0xC0, 1]];
  [[60, 64], [62, 65, 69], [64, 67, 71, 74]].forEach((ch, i) => ch.forEach(m => ch0.push(...n(i * 2 * q, 0, m, 90, 2 * q - 10))));
  const sfx = [];
  ['coin', 'jump', 'laser', 'blip'].forEach((_, i) => sfx.push([i * q + q * 6, 0x93, 60 + i, 127], [i * q + q * 6 + 40, 0x93, 60 + i, 0]));
  const pad = [[0, 0xC4, 10], ...n(0, 4, 60, 90, 8 * q), [0, 0xB4, 101, 0], [1, 0xB4, 100, 0], [2, 0xB4, 6, 12], [4 * q, 0xE4, 0, 96], [6 * q, 0xE4, 0, 32]];
  const song2 = writeMidi(q, [{ events: [[0, 'tempo', 600000]] }, { events: ch0 }, { events: sfx }, { events: pad }]);
  const sidecar2 = { format: 'nano-gb-sidecar', version: 1, channels: { 4: { preset: 'coin', volume: 0.9 }, 1: { preset: 'square', duty: 1, pan: 'L' } } };
  return { song1, song2, sidecar2 };
}
