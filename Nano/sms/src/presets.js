// ---- presets: the built-in instruments of Nano SMS --------------------------------------------------------------------------------
//
// Written for this project from the SN76489's own means (square tones, a 2 dB-step attenuator, white / periodic noise at three rates);
// none is copied from a game or its sound driver.
// THE ORDER IS THE PROGRAM NUMBER: a MIDI program change n picks PRESETS' n-th entry. Append only.
//
//   kind     'tone' (one of three square channels) or 'noise'
//   note     the pitch synth.play(name) uses
//   a d s r  volume envelope (ms, ms, 0..1, ms); oneShot ignores note-off (needs s = 0); gain 0..1
//   vib {rate, depth cents, delay, fade}, slide {from, to, ms} (cents), arp {steps, rate, once}: software pitch effects, as on the other Nanos
//   noise    {rate 0..2 (clock/512, /1024, /2048), to, ms (a glide between rates), white (default true; false = periodic, a buzzy tone)};
//            without a rate the note's pitch picks it (C5 and up 0, C4 and up 1, lower 2)
//   pan      -1 / 0 / 1: Game Gear only (the Master System is mono)

const PRESETS = {
  lead:      { label: 'Lead', kind: 'tone', note: 72, a: 4, d: 160, s: 0.75, r: 60, vib: { rate: 5.5, depth: 25, delay: 180, fade: 220 }, desc: 'square lead with a late vibrato' },
  square:    { label: 'Square', kind: 'tone', note: 72, a: 0, d: 0, s: 1, r: 20, desc: 'the plain PSG square, steady' },
  soft:      { label: 'Soft', kind: 'tone', note: 72, a: 30, d: 300, s: 0.5, r: 180, desc: 'slow attack, a gentle fade' },
  pluck:     { label: 'Pluck', kind: 'tone', note: 72, a: 0, d: 420, s: 0, r: 0, desc: 'starts loud and dies away on its own' },
  organ:     { label: 'Organ', kind: 'tone', note: 72, arp: { steps: [0, 12], rate: 60 }, a: 0, d: 0, s: 0.8, r: 40, desc: 'octave flutter at 60 Hz' },
  arp:       { label: 'Arpeggio', kind: 'tone', note: 72, arp: { steps: [0, 4, 7, 12], rate: 30 }, a: 0, d: 0, s: 0.8, r: 30, desc: 'major chord as a fast arpeggio' },
  minarp:    { label: 'Minor arpeggio', kind: 'tone', note: 72, arp: { steps: [0, 3, 7, 12], rate: 30 }, a: 0, d: 0, s: 0.8, r: 30, desc: 'minor chord as a fast arpeggio' },
  bass:      { label: 'Bass', kind: 'tone', note: 48, a: 0, d: 220, s: 0.7, r: 40, desc: 'punchy square bass (the PSG goes down to A2)' },
  pad:       { label: 'Pad', kind: 'tone', note: 60, a: 250, d: 400, s: 0.6, r: 500, vib: { rate: 4, depth: 12, delay: 300, fade: 600 }, desc: 'slow swell with a light vibrato' },
  echo:      { label: 'Echo lead', kind: 'tone', note: 72, a: 0, d: 90, s: 0.35, r: 260, desc: 'a bright hit settling to a quiet tail' },
  kick:      { label: 'Kick', kind: 'tone', note: 48, oneShot: true, a: 0, d: 140, s: 0, slide: { from: 1900, to: 0, ms: 70 }, desc: 'a square falling fast' },
  tom:       { label: 'Tom', kind: 'tone', note: 52, oneShot: true, a: 0, d: 220, s: 0, slide: { from: 1000, to: 0, ms: 140 }, desc: 'a slower falling square' },
  snare:     { label: 'Snare', kind: 'noise', note: 38, oneShot: true, a: 0, d: 170, s: 0, noise: { rate: 0, to: 1, ms: 120 }, desc: 'white noise, bright then lower' },
  hat:       { label: 'Closed hat', kind: 'noise', note: 42, oneShot: true, a: 0, d: 45, s: 0, gain: 0.7, noise: { rate: 0 }, desc: 'short bright noise' },
  openhat:   { label: 'Open hat', kind: 'noise', note: 46, oneShot: true, a: 0, d: 300, s: 0, gain: 0.6, noise: { rate: 0 }, desc: 'the same noise, left ringing' },
  crash:     { label: 'Crash', kind: 'noise', note: 49, oneShot: true, a: 0, d: 900, s: 0, gain: 0.8, noise: { rate: 0, to: 1, ms: 700 }, desc: 'long bright noise' },
  buzz:      { label: 'Buzz', kind: 'noise', note: 60, a: 0, d: 0, s: 0.9, r: 30, noise: { rate: 1, white: false }, desc: 'periodic noise: a low buzzing tone' },
  laser:     { label: 'Laser', kind: 'tone', note: 84, oneShot: true, a: 0, d: 220, s: 0, slide: { from: 2400, to: -600, ms: 190 }, desc: 'a square falling three octaves' },
  coin:      { label: 'Coin', kind: 'tone', note: 83, oneShot: true, a: 0, d: 380, s: 0, arp: { steps: [0, 5], rate: 16, once: true }, desc: 'two notes a fourth apart' },
  jump:      { label: 'Jump', kind: 'tone', note: 60, oneShot: true, a: 0, d: 280, s: 0, slide: { from: 0, to: 1200, ms: 210 }, desc: 'a rising square' },
  powerup:   { label: 'Power up', kind: 'tone', note: 72, oneShot: true, a: 0, d: 520, s: 0, arp: { steps: [0, 4, 7, 12, 16, 19, 24], rate: 22, once: true }, desc: 'a fast climbing arpeggio' },
  blip:      { label: 'Blip', kind: 'tone', note: 84, oneShot: true, a: 0, d: 90, s: 0, desc: 'a short menu beep' },
  explosion: { label: 'Explosion', kind: 'noise', note: 36, oneShot: true, a: 0, d: 1100, s: 0, noise: { rate: 1, to: 2, ms: 800 }, desc: 'noise sinking to a rumble' },
  hit:       { label: 'Hit', kind: 'noise', note: 36, oneShot: true, a: 0, d: 130, s: 0, noise: { rate: 1, to: 2, ms: 90 }, desc: 'a short thump of noise' },
};
