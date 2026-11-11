// ---- presets: the built-in instruments of Nano SNES ---------------------------------------------------------------------------------
//
// Written for this project from the S-DSP's own means (BRR samples from src/samples.js, the chip's ADSR / GAIN, its noise generator); none is copied
// from a game. THE ORDER IS THE PROGRAM NUMBER: a MIDI program change n picks PRESETS' n-th entry. Append only.
//
//   kind     'brr' (a sample: brr = base64 BRR blocks + loop = the block index it loops to (-1 plays once) + base = its pitch in Hz at pitch $1000,
//            or wave = 16-bit samples the engine encodes) or 'noise' (the DSP's noise generator: noise.rate 0..31, else from the note's pitch)
//   note     the pitch synth.play(name) uses
//   a d s r  software envelope (ms, ms, 0..1, ms) on the voice's volume; the voice stays keyed through r, then KOFF; oneShot ignores note-off (s = 0)
//   adsr     the chip's own envelope [attack 0..15, decay 0..7, sustain level 0..7, sustain rate 0..31] (default [15, 0, 7, 0]: instant, held);
//            gainReg instead: the GAIN byte (a direct level or a ramp)
//   vib {rate, depth cents, delay, fade}, slide {from, to, ms} (cents), arp {steps, rate, once}: software pitch effects, as on the other Nanos
//   pan      -1 / 0 / 1

const S = SAMPLES;
const PRESETS = {
  piano:     { label: 'E. piano', kind: 'brr', ...S.bell, note: 60, adsr: [15, 3, 3, 10], r: 250, desc: 'a bright strike that decays (chip ADSR)' },
  strings:   { label: 'Strings', kind: 'brr', ...S.strings, note: 60, a: 180, d: 300, s: 0.85, r: 400, vib: { rate: 5, depth: 10, delay: 300, fade: 400 }, desc: 'a soft-edged saw, slow swell' },
  brass:     { label: 'Brass', kind: 'brr', ...S.brass, note: 60, a: 40, d: 200, s: 0.8, r: 120, desc: 'bright harmonics, a quick swell' },
  organ:     { label: 'Organ', kind: 'brr', ...S.organ, note: 60, r: 40, desc: 'drawbars 1, 2, 3, 4, 6, 8' },
  choir:     { label: 'Choir', kind: 'brr', ...S.choir, note: 60, a: 250, d: 300, s: 0.8, r: 500, vib: { rate: 4.5, depth: 15, delay: 200, fade: 500 }, desc: 'a soft vowel-like pad' },
  sine:      { label: 'Sine', kind: 'brr', ...S.sine, note: 72, r: 30, desc: 'the plain sine' },
  square:    { label: 'Square', kind: 'brr', ...S.square, note: 72, r: 30, desc: 'a band-limited square' },
  pulse25:   { label: 'Pulse 25%', kind: 'brr', ...S.pulse25, note: 72, r: 30, desc: 'a band-limited 25% pulse' },
  pulse12:   { label: 'Pulse 12%', kind: 'brr', ...S.pulse12, note: 72, r: 30, desc: 'a band-limited 12.5% pulse' },
  saw:       { label: 'Saw', kind: 'brr', ...S.saw, note: 72, r: 30, desc: 'a band-limited sawtooth' },
  triangle:  { label: 'Triangle', kind: 'brr', ...S.triangle, note: 60, r: 30, desc: 'a band-limited triangle' },
  lead:      { label: 'Lead', kind: 'brr', ...S.square, note: 72, a: 4, d: 160, s: 0.75, r: 60, vib: { rate: 5.5, depth: 25, delay: 180, fade: 220 }, desc: 'square lead with a late vibrato' },
  bass:      { label: 'Bass', kind: 'brr', ...S.bass, note: 36, adsr: [15, 2, 5, 6], r: 60, desc: 'a round bass that settles' },
  pluck:     { label: 'Pluck', kind: 'brr', ...S.saw, note: 60, adsr: [15, 5, 1, 14], r: 80, desc: 'a saw that dies away (chip ADSR)' },
  bell:      { label: 'Bell', kind: 'brr', ...S.bell, note: 72, adsr: [15, 2, 2, 7], r: 600, desc: 'inharmonic-ish partials, a long ring' },
  marimba:   { label: 'Marimba', kind: 'brr', ...S.sine, note: 60, adsr: [15, 6, 0, 18], r: 60, desc: 'a sine knocked and damped' },
  arp:       { label: 'Arpeggio', kind: 'brr', ...S.pulse25, note: 72, arp: { steps: [0, 4, 7, 12], rate: 30 }, s: 0.8, r: 30, desc: 'major chord as a fast arpeggio' },
  kick:      { label: 'Kick', kind: 'brr', ...S.kick, note: 60, oneShot: true, a: 0, d: 120, s: 0, desc: 'a falling sine (a one-shot sample)' },
  tom:       { label: 'Tom', kind: 'brr', ...S.tom, note: 60, oneShot: true, a: 0, d: 160, s: 0, desc: 'a lower falling sine' },
  snare:     { label: 'Snare', kind: 'noise', note: 38, oneShot: true, a: 0, d: 170, s: 0, noise: { rate: 29 }, desc: 'the DSP noise, bright' },
  hat:       { label: 'Closed hat', kind: 'noise', note: 42, oneShot: true, a: 0, d: 45, s: 0, gain: 0.6, noise: { rate: 31 }, desc: 'short bright noise' },
  openhat:   { label: 'Open hat', kind: 'noise', note: 46, oneShot: true, a: 0, d: 300, s: 0, gain: 0.5, noise: { rate: 31 }, desc: 'the same noise, left ringing' },
  crash:     { label: 'Crash', kind: 'noise', note: 49, oneShot: true, a: 0, d: 900, s: 0, gain: 0.7, noise: { rate: 30 }, desc: 'long bright noise' },
  laser:     { label: 'Laser', kind: 'brr', ...S.square, note: 84, oneShot: true, a: 0, d: 220, s: 0, slide: { from: 2400, to: -600, ms: 190 }, desc: 'a square falling three octaves' },
  coin:      { label: 'Coin', kind: 'brr', ...S.square, note: 83, oneShot: true, a: 0, d: 380, s: 0, arp: { steps: [0, 5], rate: 16, once: true }, desc: 'two notes a fourth apart' },
  jump:      { label: 'Jump', kind: 'brr', ...S.pulse25, note: 60, oneShot: true, a: 0, d: 280, s: 0, slide: { from: 0, to: 1200, ms: 210 }, desc: 'a rising pulse' },
  powerup:   { label: 'Power up', kind: 'brr', ...S.pulse25, note: 72, oneShot: true, a: 0, d: 520, s: 0, arp: { steps: [0, 4, 7, 12, 16, 19, 24], rate: 22, once: true }, desc: 'a fast climbing arpeggio' },
  blip:      { label: 'Blip', kind: 'brr', ...S.sine, note: 84, oneShot: true, a: 0, d: 90, s: 0, desc: 'a short menu beep' },
  explosion: { label: 'Explosion', kind: 'noise', note: 36, oneShot: true, a: 0, d: 1100, s: 0, desc: 'noise; its rate falls with the note (play it low)' },
  hit:       { label: 'Hit', kind: 'noise', note: 36, oneShot: true, a: 0, d: 130, s: 0, noise: { rate: 22 }, desc: 'a short thump of noise' },
};
