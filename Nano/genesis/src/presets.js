// ---- presets: the built-in instruments of Nano Genesis ---------------------------------------------------------------------------
//
// The FM ones are the BrokenNes2 plugin's Genesis patch bank (Windows/NesEmulator/mix/FmPatchBank.cs, written for BrokenNes: register sets,
// none copied from a game), so a part written on the plugin's Genesis chip has the same timbres here; the PSG ones are Nano SMS's.
// THE ORDER IS THE PROGRAM NUMBER: a MIDI program change n picks PRESETS' n-th entry. Append only.
//
//   kind     'fm' (one of six FM channels), 'tone' (one of three PSG squares) or 'noise' (the PSG noise)
//   note     the pitch synth.play(name) uses
//   a d s r  software envelope (ms, ms, 0..1, ms) on the level; oneShot ignores note-off (needs s = 0); gain 0..1.
//            On FM, r is how long the chip's own release is let ring before the channel is free again (the level is held there)
//   fm       alg 0..7, fb 0..7, ams 0..3, pms 0..7 (they need setParam('lfo', 1..8)), ops: four operators in algorithm order (operator 1 first):
//            {mul 0..15, tl 0..127, dt 0..7, ar 0..31, d1r 0..31, sl 0..15, d2r 0..31, rr 0..15, rs 0..3, am 0/1, ssg 0..15}
//   vib {rate, depth cents, delay, fade}, slide {from, to, ms} (cents), arp {steps, rate, once}: software pitch effects, as on the other Nanos
//   noise    {rate 0..2, to, ms, white}: as Nano SMS
//   pan      -1 / 0 / 1: the FM channels (the PSG is mono on a Mega Drive)

const OPR = (mul, tl, x = {}) => ({ mul, tl, dt: x.dt ?? 0, ar: x.ar ?? 31, d1r: x.d1r ?? 0, sl: x.sl ?? 0, d2r: x.d2r ?? 0, rr: x.rr ?? 10, rs: 0, am: 0, ssg: 0 });
const OPOFF = OPR(1, 127);

const PRESETS = {
  brass:     { label: 'Brass', kind: 'fm', note: 60, r: 250, alg: 4, fb: 3, ops: [OPR(1, 20, { ar: 18, d1r: 8, sl: 2 }), OPR(1, 4), OPR(1, 24, { dt: 3, ar: 17, d1r: 8, sl: 2 }), OPR(1, 22, { dt: 3 })], desc: 'two 1:1 pairs a few cents apart that swell and settle' },
  buzz:      { label: 'Buzz lead', kind: 'fm', note: 72, r: 200, alg: 4, fb: 3, ops: [OPR(1, 14), OPR(1, 0), OPOFF, OPOFF], vib: { rate: 5.5, depth: 20, delay: 200, fade: 250 }, desc: 'a fed-back operator bending a 1:1 carrier: bright, buzzy' },
  square:    { label: 'FM square', kind: 'fm', note: 72, r: 200, alg: 7, fb: 0, ops: [OPR(1, 3), OPR(3, 16), OPR(5, 22), OPR(7, 25)], desc: 'odd harmonics 1, 3, 5, 7 at 1/n: a band-limited square' },
  pulse:     { label: 'Pulse 25%', kind: 'fm', note: 72, r: 200, alg: 7, fb: 0, ops: [OPR(1, 7), OPR(2, 11), OPR(3, 20), OPR(5, 26)], desc: 'harmonics at the spectrum of a 25% pulse: a thin NES-like pulse' },
  organ:     { label: 'Organ', kind: 'fm', note: 60, r: 200, alg: 7, fb: 0, ops: [OPR(1, 4), OPR(2, 7), OPR(4, 11), OPR(6, 15, { ar: 28 })], desc: 'four drawbars at 1x, 2x, 4x and 6x' },
  strings:   { label: 'Strings', kind: 'fm', note: 60, r: 600, alg: 5, fb: 0, ops: [OPR(1, 28, { ar: 15, rr: 7 }), OPR(1, 3, { ar: 15, rr: 7 }), OPR(1, 30, { dt: 3, ar: 14, rr: 7 }), OPR(1, 30, { dt: 7, ar: 14, rr: 7 })], desc: 'a 1:1 modulator over three detuned carriers, slow attack' },
  clarinet:  { label: 'Clarinet', kind: 'fm', note: 60, r: 200, alg: 4, fb: 0, ops: [OPR(2, 30, { d1r: 6, sl: 3 }), OPR(1, 0), OPOFF, OPOFF], desc: 'a modulator at twice the frequency: odd harmonics' },
  flute:     { label: 'Flute', kind: 'fm', note: 72, r: 200, alg: 4, fb: 0, ops: [OPR(1, 54), OPR(1, 0, { ar: 22 }), OPOFF, OPOFF], vib: { rate: 5, depth: 12, delay: 250, fade: 300 }, desc: 'a whisper of modulation on a sine, soft attack' },
  epiano:    { label: 'E. piano', kind: 'fm', note: 60, r: 300, alg: 4, fb: 0, ops: [OPR(1, 36, { d1r: 10, sl: 8 }), OPR(1, 4), OPR(14, 38, { d1r: 26, sl: 15 }), OPR(1, 18)], desc: 'a bright tine over a body that mellows' },
  pluck:     { label: 'Pluck', kind: 'fm', note: 60, r: 200, alg: 4, fb: 0, ops: [OPR(1, 16, { d1r: 14, sl: 10 }), OPR(1, 4), OPOFF, OPOFF], desc: 'bright, then mellow' },
  bell:      { label: 'Bell', kind: 'fm', note: 72, r: 800, alg: 4, fb: 0, ops: [OPR(7, 24, { d1r: 12, sl: 12, rr: 5 }), OPR(1, 4, { rr: 5 }), OPR(3, 30, { d1r: 14, sl: 15, rr: 5 }), OPR(2, 14, { rr: 5 })], desc: 'a 7x modulator fading over a fundamental and an octave: glassy' },
  marimba:   { label: 'Marimba', kind: 'fm', note: 60, r: 200, alg: 4, fb: 0, ops: [OPR(3, 22, { d1r: 16, sl: 12 }), OPR(1, 3, { d1r: 9, sl: 15 }), OPOFF, OPOFF], desc: 'a knock, then a round tone that dies away' },
  bass:      { label: 'Bass punch', kind: 'fm', note: 36, r: 150, alg: 4, fb: 3, ops: [OPR(1, 20, { d1r: 14, sl: 10 }), OPR(1, 0), OPOFF, OPOFF], desc: 'a fed-back modulator that fades quickly: a punchy synth bass' },
  growl:     { label: 'Bass growl', kind: 'fm', note: 36, r: 150, alg: 0, fb: 4, ops: [OPR(1, 30, { d1r: 6, sl: 4 }), OPR(1, 24, { d1r: 6, sl: 4 }), OPR(1, 20), OPR(1, 0)], desc: 'a four-operator chain at 1:1: a harsh bass' },
  sine:      { label: 'Sine', kind: 'fm', note: 72, r: 200, alg: 7, fb: 0, ops: [OPOFF, OPOFF, OPOFF, OPR(1, 0)], desc: 'one carrier: a pure sine' },
  fmkick:    { label: 'FM kick', kind: 'fm', note: 36, oneShot: true, a: 0, d: 260, s: 0, r: 0, alg: 4, fb: 5, ops: [OPR(1, 12, { d1r: 24, sl: 15 }), OPR(1, 0, { d1r: 12, sl: 15 }), OPOFF, OPOFF], slide: { from: 2400, to: 0, ms: 60 }, desc: 'a falling, fed-back thump' },
  psglead:   { label: 'PSG lead', kind: 'tone', note: 72, a: 4, d: 160, s: 0.75, r: 60, vib: { rate: 5.5, depth: 25, delay: 180, fade: 220 }, desc: 'square lead with a late vibrato (PSG)' },
  psgsquare: { label: 'PSG square', kind: 'tone', note: 72, a: 0, d: 0, s: 1, r: 20, desc: 'the plain PSG square, steady' },
  psgarp:    { label: 'PSG arpeggio', kind: 'tone', note: 72, arp: { steps: [0, 4, 7, 12], rate: 30 }, a: 0, d: 0, s: 0.8, r: 30, desc: 'major chord as a fast arpeggio (PSG)' },
  kick:      { label: 'Kick', kind: 'tone', note: 48, oneShot: true, a: 0, d: 140, s: 0, slide: { from: 1900, to: 0, ms: 70 }, desc: 'a PSG square falling fast' },
  tom:       { label: 'Tom', kind: 'tone', note: 52, oneShot: true, a: 0, d: 220, s: 0, slide: { from: 1000, to: 0, ms: 140 }, desc: 'a slower falling PSG square' },
  snare:     { label: 'Snare', kind: 'noise', note: 38, oneShot: true, a: 0, d: 170, s: 0, noise: { rate: 0, to: 1, ms: 120 }, desc: 'white noise, bright then lower' },
  hat:       { label: 'Closed hat', kind: 'noise', note: 42, oneShot: true, a: 0, d: 45, s: 0, gain: 0.7, noise: { rate: 0 }, desc: 'short bright noise' },
  openhat:   { label: 'Open hat', kind: 'noise', note: 46, oneShot: true, a: 0, d: 300, s: 0, gain: 0.6, noise: { rate: 0 }, desc: 'the same noise, left ringing' },
  crash:     { label: 'Crash', kind: 'noise', note: 49, oneShot: true, a: 0, d: 900, s: 0, gain: 0.8, noise: { rate: 0, to: 1, ms: 700 }, desc: 'long bright noise' },
  laser:     { label: 'Laser', kind: 'tone', note: 84, oneShot: true, a: 0, d: 220, s: 0, slide: { from: 2400, to: -600, ms: 190 }, desc: 'a PSG square falling three octaves' },
  coin:      { label: 'Coin', kind: 'fm', note: 83, oneShot: true, a: 0, d: 380, s: 0, r: 0, alg: 7, fb: 0, ops: [OPOFF, OPOFF, OPR(2, 20), OPR(1, 0)], arp: { steps: [0, 5], rate: 16, once: true }, desc: 'two notes a fourth apart, on FM' },
  jump:      { label: 'Jump', kind: 'tone', note: 60, oneShot: true, a: 0, d: 280, s: 0, slide: { from: 0, to: 1200, ms: 210 }, desc: 'a rising PSG square' },
  powerup:   { label: 'Power up', kind: 'fm', note: 72, oneShot: true, a: 0, d: 520, s: 0, r: 0, alg: 4, fb: 2, ops: [OPR(1, 26), OPR(1, 0), OPOFF, OPOFF], arp: { steps: [0, 4, 7, 12, 16, 19, 24], rate: 22, once: true }, desc: 'a fast climbing FM arpeggio' },
  blip:      { label: 'Blip', kind: 'tone', note: 84, oneShot: true, a: 0, d: 90, s: 0, desc: 'a short menu beep (PSG)' },
  explosion: { label: 'Explosion', kind: 'noise', note: 36, oneShot: true, a: 0, d: 1100, s: 0, noise: { rate: 1, to: 2, ms: 800 }, desc: 'noise sinking to a rumble' },
  hit:       { label: 'Hit', kind: 'noise', note: 36, oneShot: true, a: 0, d: 130, s: 0, noise: { rate: 1, to: 2, ms: 90 }, desc: 'a short thump of noise' },
};
