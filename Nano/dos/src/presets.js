// ---- presets: the built-in instruments of Nano DOS --------------------------------------------------------------------------------
//
// FM presets are 11-byte SBI register sets (mod 20, car 20, mod 40, car 40, mod 60, car 60, mod 80, car 80, mod E0, car E0, C0), the BrokenNes2
// plugin's PC patch format (Plugin/BrokenNes2.Plugin/Pc/OplPatch.cs). The first eight are the plugin's bank: Super VRUN DOS's seven patches and Vaults
// DOS's tone (the user's own games); the rest were designed for this library. The speaker presets are the PC speaker's square.
// THE ORDER IS THE PROGRAM NUMBER: a MIDI program change n picks PRESETS' n-th entry. Append only.
//
//   kind     'fm' (one of nine OPL2 voices: regs = the 11 SBI bytes) or 'speaker' (the PC speaker: on / off, no volume)
//   note     the pitch synth.play(name) uses
//   a d s r  software envelope (ms, ms, 0..1, ms) on the carrier's level (the speaker: on while it is above 0); on FM, r is how long the chip's own
//            release rings before the channel is free (the level is held there)
//   vib {rate, depth cents, delay, fade}, slide {from, to, ms} (cents), arp {steps, rate, once}: software pitch effects, as on the other Nanos

const SV = (q) => [q[0], q[1], q[2], 0, q[3], q[4], q[5], q[6], q[7], q[8], q[9]];      // a Super VRUN DOS 10-byte row (OplBank.FromSvrun)
const PRESETS = {
  svpulse12: { label: 'SV pulse 12.5%', kind: 'fm', note: 72, r: 60, regs: SV([0x21, 0x21, 0x0E, 0xF0, 0xF0, 0x0F, 0x08, 0, 0, 0x0C]), desc: 'Super VRUN DOS: x1:x1, feedback 6, bright (the game\'s 12.5% pulse)' },
  svpulse25: { label: 'SV pulse 25%', kind: 'fm', note: 72, r: 60, regs: SV([0x21, 0x21, 0x14, 0xF0, 0xF0, 0x0F, 0x08, 0, 0, 0x08]), desc: 'Super VRUN DOS: x1:x1, feedback 4 (the game\'s 25% pulse)' },
  svpulse50: { label: 'SV pulse 50%', kind: 'fm', note: 72, r: 60, regs: SV([0x22, 0x21, 0x1C, 0xF0, 0xF0, 0x0F, 0x08, 0, 0, 0x00]), desc: 'Super VRUN DOS: x2:x1, square-like (the game\'s 50% pulse)' },
  svtriangle: { label: 'SV triangle', kind: 'fm', note: 60, r: 60, regs: SV([0x21, 0x21, 0x3F, 0xF0, 0xF0, 0x0F, 0x08, 0, 0, 0x00]), desc: 'Super VRUN DOS: the carrier alone, a sine' },
  svmix: { label: 'SV mix', kind: 'fm', note: 72, r: 60, regs: SV([0x21, 0x21, 0x1A, 0xF0, 0xF0, 0x0F, 0x08, 0, 0, 0x06]), desc: 'Super VRUN DOS: softer' },
  svnoise: { label: 'SV noise', kind: 'fm', note: 60, r: 40, regs: SV([0x2F, 0x2E, 0x00, 0xF0, 0xF0, 0x0F, 0x08, 0, 0, 0x0E]), desc: 'Super VRUN DOS: x15:x14, feedback 7, noise-like' },
  svperc: { label: 'SV percussion', kind: 'fm', note: 60, oneShot: true, a: 0, d: 300, s: 0, regs: SV([0x0F, 0x0E, 0x00, 0xF8, 0xF6, 0x0F, 0xF8, 0, 0, 0x0E]), desc: 'Super VRUN DOS: the noise patch, decaying' },
  vaults: { label: 'Vaults tone', kind: 'fm', note: 72, r: 60, regs: [0x22, 0x21, 0x16, 0x00, 0xF0, 0xF0, 0x0F, 0x0F, 0, 0, 0x00], desc: 'Vaults DOS: the Game Gear tone on the AdLib' },
  piano: { label: 'Piano', kind: 'fm', note: 60, r: 300, regs: [0x01, 0x01, 0x4F, 0x00, 0xF1, 0xF2, 0x53, 0x74, 0, 0, 0x06], desc: 'a struck, decaying tone' },
  epiano: { label: 'E. piano', kind: 'fm', note: 60, r: 300, regs: [0x0E, 0x01, 0x2A, 0x00, 0xF5, 0xF3, 0x76, 0x74, 0, 0, 0x00], desc: 'a bright tine over a sine body' },
  organ: { label: 'Organ', kind: 'fm', note: 60, r: 40, regs: [0x22, 0x21, 0x1E, 0x00, 0xF0, 0xF0, 0x00, 0x0F, 0, 0, 0x01], desc: 'two sines an octave apart, additive' },
  strings: { label: 'Strings', kind: 'fm', note: 60, r: 400, regs: [0x61, 0x21, 0x1B, 0x00, 0x75, 0x72, 0x13, 0x17, 0, 0, 0x0E], desc: 'a slow, bowed swell (vibrato bits on)' },
  brass: { label: 'Brass', kind: 'fm', note: 60, r: 150, regs: [0x21, 0x21, 0x18, 0x00, 0x83, 0x83, 0x15, 0x16, 0, 0, 0x0A], desc: 'a modulator that swells with the attack' },
  flute: { label: 'Flute', kind: 'fm', note: 72, r: 120, regs: [0xE1, 0xE1, 0x2E, 0x00, 0x86, 0x75, 0x07, 0x07, 0, 0, 0x08], vib: { rate: 5, depth: 10, delay: 250, fade: 300 }, desc: 'breathy, soft attack' },
  bass: { label: 'Bass', kind: 'fm', note: 36, r: 80, regs: [0x01, 0x01, 0x16, 0x00, 0xF3, 0xF4, 0x56, 0x77, 0, 0, 0x0C], desc: 'a plucked synth bass' },
  bell: { label: 'Bell', kind: 'fm', note: 72, r: 800, regs: [0x07, 0x01, 0x23, 0x00, 0xF4, 0xF2, 0x24, 0x35, 0, 0, 0x04], desc: 'a 7:1 bell, a long ring' },
  lead: { label: 'Lead', kind: 'fm', note: 72, r: 80, regs: [0x21, 0x21, 0x10, 0x00, 0xF0, 0xF0, 0x0F, 0x0F, 2, 0, 0x0C], vib: { rate: 5.5, depth: 25, delay: 180, fade: 220 }, desc: 'abs-sine modulator, feedback 6: a buzzy lead with a late vibrato' },
  kick: { label: 'Kick', kind: 'fm', note: 36, oneShot: true, a: 0, d: 200, s: 0, regs: [0x00, 0x00, 0x0B, 0x00, 0xF8, 0xF6, 0xFF, 0xF8, 0, 0, 0x08], slide: { from: 1800, to: 0, ms: 60 }, desc: 'a falling thump' },
  snare: { label: 'Snare', kind: 'fm', note: 60, oneShot: true, a: 0, d: 170, s: 0, regs: [0x0F, 0x0E, 0x00, 0x00, 0xF8, 0xF7, 0xF8, 0xF7, 0, 0, 0x0E], desc: 'feedback-7 noise, short' },
  hat: { label: 'Closed hat', kind: 'fm', note: 84, oneShot: true, a: 0, d: 45, s: 0, gain: 0.6, regs: [0x0F, 0x0E, 0x00, 0x04, 0xF8, 0xF9, 0xF8, 0xF9, 0, 0, 0x0E], desc: 'high, short noise' },
  crash: { label: 'Crash', kind: 'fm', note: 84, oneShot: true, a: 0, d: 900, s: 0, gain: 0.7, regs: [0x0F, 0x0E, 0x00, 0x04, 0xF2, 0xF2, 0xF2, 0xF2, 0, 0, 0x0E], desc: 'high noise, a long ring' },
  beep: { label: 'Beeper', kind: 'speaker', note: 72, r: 0, desc: 'the PC speaker\'s square, steady' },
  beeplead: { label: 'Beeper lead', kind: 'speaker', note: 72, vib: { rate: 6, depth: 30, delay: 150, fade: 200 }, desc: 'the speaker with a vibrato' },
  beeparp: { label: 'Beeper arpeggio', kind: 'speaker', note: 72, arp: { steps: [0, 4, 7, 12], rate: 40 }, desc: 'the classic one-voice chord' },
  laser: { label: 'Laser', kind: 'speaker', note: 84, oneShot: true, a: 0, d: 220, s: 0, slide: { from: 2400, to: -600, ms: 190 }, desc: 'the speaker falling three octaves' },
  coin: { label: 'Coin', kind: 'speaker', note: 83, oneShot: true, a: 0, d: 300, s: 0, arp: { steps: [0, 5], rate: 16, once: true }, desc: 'two notes a fourth apart' },
  jump: { label: 'Jump', kind: 'speaker', note: 60, oneShot: true, a: 0, d: 250, s: 0, slide: { from: 0, to: 1200, ms: 210 }, desc: 'a rising beep' },
  powerup: { label: 'Power up', kind: 'fm', note: 72, oneShot: true, a: 0, d: 520, s: 0, regs: [0x21, 0x21, 0x14, 0x00, 0xF0, 0xF0, 0x0F, 0x0F, 0, 0, 0x08], arp: { steps: [0, 4, 7, 12, 16, 19, 24], rate: 22, once: true }, desc: 'a fast climbing arpeggio (FM)' },
  blip: { label: 'Blip', kind: 'speaker', note: 84, oneShot: true, a: 0, d: 70, s: 0, desc: 'a short menu beep' },
  explosion: { label: 'Explosion', kind: 'fm', note: 36, oneShot: true, a: 0, d: 1100, s: 0, regs: [0x0F, 0x0E, 0x00, 0x00, 0xF4, 0xF3, 0xF3, 0xF3, 0, 0, 0x0E], slide: { from: 0, to: -1200, ms: 800 }, desc: 'low noise sinking' },
};
