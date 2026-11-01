// ---- presets: the built-in instruments -----------------------------------------------------------------------------------------
//
// A preset is plain data (see normalizePreset for every field and its default). Times are milliseconds, pitches are cents, levels are 0..1.
// They are written for this project from the sound chip's own parameters (duty cycles, a stepped 4-bit volume, the sweep unit, the noise
// channel's 16 fixed periods); none is copied from a game or its sound driver.
//
//   kind        'pulse' (one of two square channels), 'triangle' (no volume control: on or off) or 'noise' (16 fixed pitches)
//   note        the pitch synth.play(name) uses (a MIDI note number)
//   duty        pulse width 0..3 = 12.5%, 25%, 50%, 75%; dutySeq {steps, rate} cycles it (a timbre that moves)
//   a d s r     volume envelope: attack ms, decay ms, sustain level 0..1, release ms (the NES has no release tail: 0 closes the channel)
//   oneShot     a note-off is ignored; the sound plays out (drums, effects). It needs s = 0: the voice ends by itself after a + d
//   gain        preset loudness 0..1 (multiplies velocity)
//   vib         {rate Hz, depth cents, delay ms, fade ms}: triangle-wave vibrato
//   slide       {from, to, ms}: a pitch offset in cents, moving linearly from `from` to `to` over `ms`, then staying at `to`
//   arp         {steps [semitones], rate (steps per second), once}: arpeggio; once = stop on the last step
//   noise       {idx, to, ms, mode}: noise period index (0 = highest .. 15 = lowest) gliding idx -> to, and the long/short (metallic) mode;
//               without it the noise follows the note's pitch
//   sweep       {period 0..7, negate, shift 0..7}: the chip's own pitch-sweep unit (a pulse channel). Pitch then belongs to the hardware:
//               vibrato, slide, arp and bends are ignored by the voice

const PRESETS = {
  lead:      { label: 'Lead', kind: 'pulse', note: 72, duty: 1, a: 4, d: 140, s: 0.8, r: 50, vib: { rate: 5.5, depth: 28, delay: 160, fade: 240 }, desc: '25% pulse with a late vibrato' },
  square:    { label: 'Square', kind: 'pulse', note: 72, duty: 2, a: 0, d: 0, s: 1, r: 25, desc: 'plain 50% square, steady' },
  thin:      { label: 'Thin pulse', kind: 'pulse', note: 72, duty: 0, a: 0, d: 220, s: 0.55, r: 70, desc: '12.5% pulse, nasal, fading to a sustain' },
  pwm:       { label: 'Moving pulse', kind: 'pulse', note: 72, dutySeq: { steps: [0, 1, 2, 3, 2, 1], rate: 9 }, a: 2, d: 0, s: 0.9, r: 60, desc: 'the pulse width cycles: a swirling tone' },
  pluck:     { label: 'Pluck', kind: 'pulse', note: 72, duty: 1, a: 0, d: 380, s: 0, r: 0, desc: 'plucked string: starts loud, dies away on its own' },
  organ:     { label: 'Organ', kind: 'pulse', note: 72, duty: 2, arp: { steps: [0, 12], rate: 60 }, a: 0, d: 0, s: 0.85, r: 40, desc: 'octave flutter at 60 Hz, like a driver faking two voices' },
  arp:       { label: 'Arpeggio', kind: 'pulse', note: 72, duty: 1, arp: { steps: [0, 4, 7, 12], rate: 30 }, a: 0, d: 0, s: 0.8, r: 30, desc: 'major chord played as a fast arpeggio on one channel' },
  pulsebass: { label: 'Pulse bass', kind: 'pulse', note: 36, duty: 2, a: 0, d: 260, s: 0.55, r: 35, desc: 'punchy 50% bass that settles' },
  bass:      { label: 'Bass', kind: 'triangle', note: 36, a: 0, d: 0, s: 1, r: 0, desc: 'the triangle channel: round, no volume steps' },
  trilead:   { label: 'Triangle lead', kind: 'triangle', note: 72, a: 0, d: 0, s: 1, r: 0, vib: { rate: 5, depth: 22, delay: 140, fade: 200 }, desc: 'soft lead with vibrato' },
  kick:      { label: 'Kick', kind: 'triangle', note: 36, oneShot: true, a: 0, d: 150, s: 0, slide: { from: 2400, to: 0, ms: 80 }, desc: 'a triangle falling two octaves in 80 ms' },
  tom:       { label: 'Tom', kind: 'triangle', note: 48, oneShot: true, a: 0, d: 230, s: 0, slide: { from: 1200, to: 0, ms: 140 }, desc: 'a slower triangle fall' },
  snare:     { label: 'Snare', kind: 'noise', note: 38, oneShot: true, a: 0, d: 170, s: 0, noise: { idx: 5, to: 7, ms: 130, mode: 0 }, desc: 'noise, mid period, short decay' },
  hat:       { label: 'Closed hat', kind: 'noise', note: 42, oneShot: true, a: 0, d: 45, s: 0, gain: 0.7, noise: { idx: 1, mode: 1 }, desc: 'short metallic noise' },
  openhat:   { label: 'Open hat', kind: 'noise', note: 46, oneShot: true, a: 0, d: 280, s: 0, gain: 0.6, noise: { idx: 1, mode: 1 }, desc: 'the same noise, left ringing' },
  crash:     { label: 'Crash', kind: 'noise', note: 49, oneShot: true, a: 0, d: 900, s: 0, gain: 0.8, noise: { idx: 2, to: 4, ms: 700, mode: 0 }, desc: 'long bright noise' },
  laser:     { label: 'Laser', kind: 'pulse', note: 84, duty: 0, oneShot: true, a: 0, d: 220, s: 0, slide: { from: 2400, to: -600, ms: 190 }, desc: 'a thin pulse falling three octaves' },
  zap:       { label: 'Zap (sweep unit)', kind: 'pulse', note: 84, duty: 1, oneShot: true, a: 0, d: 450, s: 0, sweep: { period: 2, negate: false, shift: 2 }, desc: 'the chip\'s own sweep unit drags the pitch down until it mutes' },
  coin:      { label: 'Coin', kind: 'pulse', note: 83, duty: 2, oneShot: true, a: 0, d: 380, s: 0, arp: { steps: [0, 5], rate: 16, once: true }, desc: 'two notes a fourth apart, the second ringing out' },
  jump:      { label: 'Jump', kind: 'pulse', note: 60, duty: 1, oneShot: true, a: 0, d: 280, s: 0, slide: { from: 0, to: 1200, ms: 210 }, desc: 'a rising pulse, an octave in 210 ms' },
  powerup:   { label: 'Power up', kind: 'pulse', note: 72, duty: 2, oneShot: true, a: 0, d: 520, s: 0, arp: { steps: [0, 4, 7, 12, 16, 19, 24], rate: 22, once: true }, desc: 'a fast climbing arpeggio' },
  blip:      { label: 'Blip', kind: 'pulse', note: 84, duty: 2, oneShot: true, a: 0, d: 90, s: 0, desc: 'a short menu beep' },
  explosion: { label: 'Explosion', kind: 'noise', note: 36, oneShot: true, a: 0, d: 1100, s: 0, noise: { idx: 9, to: 14, ms: 900, mode: 0 }, desc: 'noise sinking to a rumble' },
  hit:       { label: 'Hit', kind: 'noise', note: 36, oneShot: true, a: 0, d: 130, s: 0, noise: { idx: 3, to: 8, ms: 100, mode: 0 }, desc: 'a short thump of noise' },
};
