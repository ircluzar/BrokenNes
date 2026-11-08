// ---- presets: the built-in instruments, and their program numbers ----------------------------------------------------------------
//
// Plain data (normalizePreset fills in every field). Written for this project from the Game Boy chip's own parameters (four duty widths,
// the 4-bit volume and its hardware envelope, the pulse 1 sweep unit, the 32-step 4-bit wave channel and its four volume steps, the two
// noise widths); none is copied from a game or its sound driver.
//
// THE ORDER IS THE PROGRAM NUMBER: a MIDI program change n picks PRESETS' n-th entry, in this library and in the BrokenNes2 plugin's Nano GB
// chip (whose table is generated from this file by Nano/gb/build.mjs). Append only; never reorder.
//
//   kind     'pulse' (pulse 1 or 2), 'wave' (the wave channel) or 'noise'
//   note     the pitch synth.play(name) uses (a MIDI note number)
//   duty     pulse width 0..3 = 12.5, 25, 50, 75 %; dutySeq {steps, rate} cycles it
//   a d s r  software volume envelope: attack ms, decay ms, sustain 0..1, release ms (0 closes the channel at note-off)
//   env      {step 1..7, up}: the chip's own envelope instead (one volume step every step / 64 s); the note starts at its velocity's
//            level. a d s r then only say when the voice ends
//   oneShot  a note-off is ignored; the sound plays out (needs s = 0: the voice ends after a + d)
//   gain     loudness 0..1 (multiplies velocity)
//   vib      {rate Hz, depth cents, delay ms, fade ms}; slide {from, to, ms} cents; arp {steps [semitones], rate per second, once}
//   sweep    {period 1..7, down, shift 0..7}: pulse 1's sweep unit (the pitch then belongs to the chip: vib, slide, arp and bends are ignored)
//   wave     a built-in wave name (WAVES) or 32 numbers 0..15
//   noise    {shift 0..13 (higher is lower), to, ms, ratio 0..7, short}: the noise clock gliding shift -> to; short = the 7-bit LFSR
//            (metallic). Without it the noise follows the note
//   pan      -1 left, 0 both, 1 right (the chip has no other positions)

const WAVES = (function () {
  const w = {}, mk = (f) => { const a = []; for (let i = 0; i < 32; i++) a.push(Math.max(0, Math.min(15, Math.round(f(i))))); return a; };
  const S = (i, h) => Math.sin(2 * Math.PI * h * i / 32);
  w.sine = mk(i => 7.5 + 7.5 * S(i, 1));
  w.triangle = mk(i => i < 16 ? i : 31 - i);
  w.saw = mk(i => i >> 1);
  w.square = mk(i => i < 16 ? 15 : 0);
  w.pulse = mk(i => i < 8 ? 15 : 0);
  w.organ = mk(i => 7.5 + 4.2 * S(i, 1) + 2.2 * S(i, 2) + 1.1 * S(i, 4));
  w.soft = mk(i => 7.5 + 6.5 * S(i, 1) + 1 * S(i, 3));
  w.buzz = mk(i => ((i * 5) % 32) >> 1);
  return w;
})();

const PRESETS = {
  lead:      { label: 'Lead', kind: 'pulse', note: 72, duty: 1, a: 4, d: 140, s: 0.8, r: 50, vib: { rate: 5.5, depth: 25, delay: 160, fade: 240 }, desc: '25% pulse with a late vibrato' },
  square:    { label: 'Square', kind: 'pulse', note: 72, duty: 2, a: 0, d: 0, s: 1, r: 25, desc: 'plain 50% square, steady' },
  thin:      { label: 'Thin pulse', kind: 'pulse', note: 72, duty: 0, a: 0, d: 220, s: 0.55, r: 70, desc: '12.5% pulse, nasal, fading to a sustain' },
  pwm:       { label: 'Moving pulse', kind: 'pulse', note: 72, dutySeq: { steps: [0, 1, 2, 3, 2, 1], rate: 9 }, a: 2, d: 0, s: 0.9, r: 60, desc: 'the pulse width cycles: a swirling tone' },
  pluck:     { label: 'Pluck', kind: 'pulse', note: 72, duty: 1, env: { step: 1 }, a: 0, d: 240, s: 0, r: 0, desc: 'the chip\'s own envelope: loud, falling a step every 16 ms' },
  chime:     { label: 'Chime', kind: 'pulse', note: 84, duty: 0, env: { step: 5 }, a: 0, d: 1200, s: 0, r: 0, desc: '12.5% pulse with a long hardware decay' },
  arp:       { label: 'Arpeggio', kind: 'pulse', note: 72, duty: 1, arp: { steps: [0, 4, 7, 12], rate: 30 }, a: 0, d: 0, s: 0.8, r: 30, desc: 'major chord as a fast arpeggio on one channel' },
  pulsebass: { label: 'Pulse bass', kind: 'pulse', note: 36, duty: 2, a: 0, d: 260, s: 0.55, r: 35, desc: 'punchy 50% bass that settles' },
  wavebass:  { label: 'Wave bass', kind: 'wave', note: 36, wave: 'saw', a: 0, d: 300, s: 0.6, r: 30, desc: 'the wave channel as a sawtooth bass' },
  subbass:   { label: 'Sub bass', kind: 'wave', note: 36, wave: 'sine', a: 0, d: 0, s: 1, r: 20, desc: 'a round 4-bit sine, deep' },
  pad:       { label: 'Pad', kind: 'wave', note: 60, wave: 'soft', a: 260, d: 400, s: 0.6, r: 300, vib: { rate: 4, depth: 12, delay: 300, fade: 400 }, desc: 'a soft wave fading in, with slow vibrato' },
  wavelead:  { label: 'Wave lead', kind: 'wave', note: 72, wave: 'saw', a: 0, d: 0, s: 1, r: 40, vib: { rate: 6, depth: 20, delay: 120, fade: 150 }, desc: 'a buzzy sawtooth lead' },
  organ:     { label: 'Organ', kind: 'wave', note: 60, wave: 'organ', a: 0, d: 0, s: 1, r: 30, desc: 'drawbar-ish harmonics in one wavetable' },
  kick:      { label: 'Kick', kind: 'wave', note: 36, wave: 'sine', oneShot: true, a: 0, d: 140, s: 0, slide: { from: 2400, to: 0, ms: 70 }, desc: 'a sine falling two octaves in 70 ms' },
  tom:       { label: 'Tom', kind: 'wave', note: 48, wave: 'triangle', oneShot: true, a: 0, d: 220, s: 0, slide: { from: 1200, to: 0, ms: 140 }, desc: 'a slower triangle fall' },
  snare:     { label: 'Snare', kind: 'noise', note: 38, oneShot: true, a: 0, d: 170, s: 0, noise: { shift: 2, to: 4, ms: 130 }, desc: 'long noise, mid clock, short decay' },
  hat:       { label: 'Closed hat', kind: 'noise', note: 42, oneShot: true, a: 0, d: 45, s: 0, gain: 0.7, noise: { shift: 0, ratio: 1, short: true }, desc: 'short metallic 7-bit noise' },
  openhat:   { label: 'Open hat', kind: 'noise', note: 46, oneShot: true, a: 0, d: 280, s: 0, gain: 0.6, noise: { shift: 0, ratio: 1, short: true }, desc: 'the same noise, left ringing' },
  crash:     { label: 'Crash', kind: 'noise', note: 49, oneShot: true, a: 0, d: 900, s: 0, gain: 0.8, noise: { shift: 1, to: 3, ms: 700 }, desc: 'long bright noise' },
  coin:      { label: 'Coin', kind: 'pulse', note: 83, duty: 2, oneShot: true, a: 0, d: 380, s: 0, arp: { steps: [0, 5], rate: 16, once: true }, desc: 'two notes a fourth apart, the second ringing out' },
  jump:      { label: 'Jump (sweep)', kind: 'pulse', note: 55, duty: 1, oneShot: true, a: 0, d: 200, s: 0, sweep: { period: 2, shift: 5 }, desc: 'the sweep unit pushes the pitch up until it runs out' },
  laser:     { label: 'Laser (sweep)', kind: 'pulse', note: 96, duty: 0, oneShot: true, a: 0, d: 200, s: 0, sweep: { period: 1, down: true, shift: 3 }, desc: 'the sweep unit drops a thin pulse fast' },
  zap:       { label: 'Zap (sweep)', kind: 'pulse', note: 70, duty: 2, oneShot: true, a: 0, d: 300, s: 0, sweep: { period: 3, shift: 2 }, desc: 'slow upward sweep steps that end in silence' },
  powerup:   { label: 'Power up', kind: 'pulse', note: 72, duty: 2, oneShot: true, a: 0, d: 520, s: 0, arp: { steps: [0, 4, 7, 12, 16, 19, 24], rate: 22, once: true }, desc: 'a fast climbing arpeggio' },
  explosion: { label: 'Explosion', kind: 'noise', note: 36, oneShot: true, a: 0, d: 1100, s: 0, noise: { shift: 5, to: 9, ms: 900 }, desc: 'noise sinking to a rumble' },
  hit:       { label: 'Hit', kind: 'noise', note: 36, oneShot: true, a: 0, d: 130, s: 0, noise: { shift: 3, to: 6, ms: 100 }, desc: 'a short thump of noise' },
  blip:      { label: 'Blip', kind: 'pulse', note: 84, duty: 2, oneShot: true, a: 0, d: 90, s: 0, desc: 'a short menu beep' },
};

// General MIDI drum notes (channel 10) -> presets
const GM_DRUMS = { 35: 'kick', 36: 'kick', 37: 'hit', 38: 'snare', 39: 'hit', 40: 'snare', 41: 'tom', 42: 'hat', 43: 'tom', 44: 'hat', 45: 'tom', 46: 'openhat', 47: 'tom', 48: 'tom', 49: 'crash', 50: 'tom', 51: 'crash', 57: 'crash' };
