# BrokenNes Nano GB

**The Game Boy sound chip as a tiny web synth and MIDI player.** One script, no server, no downloads at run time. It plays gnuboy's Game Boy sound
(two pulses, the 32-step wave channel and noise) from notes, sound effects and Standard MIDI Files. The BrokenNes2 FL Studio plugin's **Nano GB** chip
runs the same engine, so a song composed in FL plays the same in your game: **sample for sample**, as shown under "How it is proven" below.
Its sibling, the NES synth, is [Nano NES](../README.md).

```html
<button id="go">Play a coin</button>
<script src="nano-gb.js"></script>
<script>
  let synth;
  go.onclick = async () => {
    synth ??= await BrokenNesNanoGB.create();   // inside a click: browsers keep a page silent until the user interacts with it
    synth.play('coin');                         // or synth.noteOn('C4', 1, { preset: 'lead', duration: 0.5 })
  };
</script>
```

That snippet is `demo/minimal.html`. `demo/index.html` is the full demo, with:
- a keyboard (mouse, computer keys);
- the 27 presets and the sound-effect buttons;
- a **MIDI drop zone** for a `.mid` plus its sidecar `.json`, with play, pause, stop, loop, tempo, seek and one chip per channel or one shared chip;
- the **wavetable editor**: draw 32 steps of 4 bits and play them;
- channel meters, and the mono and internal-rate options.

To open it, serve the folder (`node Nano/test/serve.mjs Nano/gb` prints a URL) and go to `/demo/index.html`.

## Files

| File | What |
|---|---|
| `dist/nano-gb.js` | **the library**: a classic script with the global `BrokenNesNanoGB`, or `require()` in Node; the worklet is inside it. 77.6 KB readable, **24.4 KB gzip, 21.6 KB brotli** |
| `dist/nano-gb.lean.js` | the same code with no comments and no indentation: 60.7 KB, **18.8 KB gzip, 16.8 KB brotli**. A test shows it renders the same bits |
| `dist/nano-gb.mjs` | the ES module (`import NanoGB, { create, renderMidi } from './nano-gb.mjs'`) |
| `dist/nano-gb-worklet.js` | only the audio worklet (18.2 KB gzip), for a Content-Security-Policy that forbids `blob:`: `create({ workletUrl: 'nano-gb-worklet.js' })` |
| `dist/NOTICE.txt` | credits and licences, which travel with the files |
| `src/` | `chip.js`, `engine.js`, `presets.js`, `song.js`, `api.js`; `node Nano/gb/build.mjs` builds dist, and `--check` compares |
| `test/`, `tools/NanoGbParity` | the tests, the test songs (`test/midi`) and the C# side of the proofs |

The build also writes `Windows/NesEmulator/gb/NanoGbPresets.g.cs`: the plugin's preset table, generated from `src/presets.js`.

## The API

`BrokenNesNanoGB.create(options)` returns a `Promise<Synth>`. All options are optional:

| Option | What it does |
|---|---|
| `context` | your own `AudioContext` |
| `destination` | where to connect the output; `false` leaves it unconnected |
| `volume` | default 1.5 (1 is the chip's own level; one full pulse peaks at about -14 dBFS) |
| `preset` | the default preset |
| `presets` | your own presets, `{ name: def }` |
| `mono` | one output channel, `(left + right) / 2`; stereo otherwise |
| `divide` | 1, 2 or 4: the chip runs at the context's rate divided by this, each sample held, so at /2 the chip is 22 kHz on a 44.1 kHz context and costs half |
| `sampleRate`, `latencyHint`, `workletUrl` | as in Nano NES |

**No fallback.** If AudioWorklet is missing, the page is not a secure context or the page is `file://`, `create()` rejects with an `Error` that has a `code` and says why. `support()` asks the same question up front.

### The synth

| Member | What it does |
|---|---|
| `noteOn(note, velocity, opts)` | starts a note and returns a handle `{ off(), bend(cents) }`. `note` is a MIDI number or a name like `'C4'` or `'F#3'`. The options are below |
| `noteOff(handle or note)` | ends a note |
| `play(preset, opts)` | a sound effect at the preset's own pitch |
| `bend(handle, cents)` | bends one note |
| `allOff()` | releases every note |
| `panic()` | stops everything |
| `setParam(name, value)` | `'volume'` (0 to 8), `'pitchBend'` and `'tune'` (cents), `'preset'`, `'pan'` (`[p1, p2, wave, noise]`, each -1, 0 or 1) |
| `addPreset(name, def)`, `addWave(name, samples32, opts)` | a preset of your own, or a wave preset made from 32 numbers 0 to 15 |
| `loadMidi(data, opts)` | loads a song (below) |
| `presets` | the preset list |
| `connect(node)`, `disconnect()`, `node` | routing |
| `onactivity` | about 40 times a second: `{ channels: [{ active, level, note, voices }] x 4, peak, song }`. **While it is null nothing is sent and no peak is measured** |
| `onerror` | errors from the audio thread |
| `connectMidi()` | a live MIDI keyboard: program change picks a preset by number, channel 10 plays GM drums |
| `sync()`, `resume()`, `dispose()` | as in Nano NES |

`noteOn` and `play` options:

| Option | What it does |
|---|---|
| `preset` | the preset to play |
| `time` / `delay` | when to start: `time` is on the audio clock, to the sample; `delay` is from now |
| `duration` | how long the note lasts |
| `channel` | 0 pulse 1, 1 pulse 2, 2 wave, 3 noise; it must suit the preset |
| `duty` | 0 to 3: 12.5, 25, 50 or 75 % |
| `pan` | -1, 0 or 1, or `'L'`, `'C'`, `'R'` |
| `bend` | cents |

Bad arguments throw a `RangeError` or `TypeError`; they are never ignored.

**Voices.** The chip has four channels. A preset's kind picks its channel: pulse (pulse 1 or 2), wave or noise. A sweep preset only plays on pulse 1.
Each channel plays one note at a time, the newest one. A new note takes:
1. a free channel of its kind;
2. if none is free, the one whose note has been releasing longest;
3. otherwise the one that has played longest. The older note waits under the new one and comes back when it ends.

### Songs (Standard MIDI Files)

```js
const song = synth.loadMidi(await (await fetch('theme.mid')).arrayBuffer(), { sidecar: await (await fetch('theme.json')).text(), loop: true });
song.play();  // also: pause(), stop(), seek(seconds), loop(on, start, end), tempo(0.05..8), volume(0..8), onend, duration, position, channels
```

The song plays inside the audio worklet, sample-accurate. Formats 0 and 1 are read, with tempo changes.

**The chips.** By default every MIDI channel gets its own chip: `chips: 'channel'`. That is how FL plays one BrokenNes2 Nano GB instance per channel,
so it is what sounds the same. `chips: 'shared'` puts the whole song on one chip, four voices in all, which is the leanest and the most "Game Boy".

**What a channel plays:**
- **Program change** `n` picks preset `n`. The numbers are the `program` column below; the plugin's Instrument numbers are the same.
- **Channel 10** plays General MIDI drums: kick, snare, hats, toms, crash.
- **A sidecar** overrides both (below).
- **Velocity** is the level, MIDI velocity / 127.
- **Pitch bend** is ±2 semitones; RPN 0 changes the range.
- **CC 7 and CC 10 are ignored**, because the plugin never sees them; the sidecar carries FL's volume and pan instead.

**The sidecar** is what the plugin's **Export sidecar** button writes (Nano GB chip, Emulator tab):

```json
{ "format": "nano-gb-sidecar", "version": 1, "channels": { "1": { "preset": "square", "volume": 0.9, "duty": 1, "pan": "L" } }, "presets": {} }
```

- Channels are numbered 1 to 16.
- `volume` (0 to 1) scales the velocity.
- `duty` overrides the pulse width.
- `pan` is -1, 0 or 1, or `"L"`, `"C"`, `"R"`.
- `presets` holds custom preset definitions.
- Unknown keys, presets or channels are refused, not ignored.

**The plugin numbers its channels by its Nano GB instances in creation order: the first instance is channel 1.** This assumes FL made them in
channel-rack order, which has not been checked in real FL. Look at the exported file, and edit it if a channel is off.

Without a device: `BrokenNesNanoGB.renderMidi(data, opts)` returns `{ left, right }`. `renderOffline(events, seconds, opts)` and `toAudioBuffer(ctx, events, seconds)`
work as in Nano NES; they return stereo with `opts.stereo`, and also work on `file://` pages and in Node.

### FL projects (.flp) straight to a song: `tools/flp2nano`

FL's own MIDI export loses what the plugin heard: the presets, slide notes and fine pitch, and it is empty when the playlist is. `flp2nano` reads the `.flp` itself and writes the song:

```
node Nano/gb/tools/flp2nano/flp2nano.mjs "song.flp" [more.flp ...] [--out DIR] [--slide fl|linear] [--dump]
```

It writes `song.nano.mid` and `song.nano.json` (the sidecar) next to the project, and nothing else.

- **The parser** (`flp.mjs`) is our own, from the public description of the FLhd / FLdt event format: the channel rack, each BrokenNes2 instance's saved state (`BFNP`: every parameter, and where the instance sat), the patterns' notes, the playlist (FL 21+ 88-byte items, clip offsets) and the tempo. FL 26 writes header event 172 with 3 or 5 bytes; the parser handles it.
- **Channels:** each BrokenNes2 channel gets a MIDI channel in channel-rack order (1, 2, 3 ...; 10 is skipped). Samplers, other plugins, automation clips, ROM mode and other chips are skipped with a warning.
- **Arrangement:** the first arrangement's pattern clips, at their positions, with their start offsets, cut at their ends. **An empty playlist** plays the patterns once each in pattern order, rounded up to whole bars, with a loud warning.
- **The Nano GB chip** (chip 30): program change and sidecar preset = the Instrument parameter; the sidecar's `volume` = Volume, `pan` = Pan (hard left or right from 50 %).
- **A NES chip in Direct mode** (chip 0 = FIX, the default): the instance is one NES channel, monophonic with last-note priority, at a constant level. It becomes the nearest Nano GB sound, defined in the sidecar's `presets`: `nes-pulse-25` (and the other duties), `nes-triangle` (the NES triangle's own 32 steps at full level) and `nes-noise`. The notes are made monophonic as the NES channel plays them. The level goes through the NES mixer's formula (with a -2 dB trim measured on `overworld.wav`), because the NES pulses are much quieter than its triangle and the Game Boy's channels are not: level 12 becomes 6.
- **Pitch:** FL key = MIDI key (the plugin hears cents from C5 = 60). Fine pitch (120 = none, taken as 1 cent a step: not checked in FL yet), Coarse and Fine, and **slide notes** become pitch bend, with an RPN range as wide as the widest bend (±23 for overworld). MIDI bends a whole channel: a NES channel has one note at a time anyway; on a Nano GB channel the newest note's bend is used and a warning counts the conflicts.
- **FL's slide, measured:** in the user's `overworld.wav` (FL 26.1, 156 BPM, nine clean 19-tick slides), the glide is a straight line in cents that starts 0.85 tick before the slide note and lasts 1.8 ticks longer (83.3 ms for 76.1 ms). That is the default; `--slide linear` is the plain reading (exactly the slide note's length). Bends are written every 2 ticks of 960 PPQ.
- **Not converted** (warned when present): automation clips (pitch, tempo), recorded pattern controllers, the channel's porta switch (its place in the channel parameters is not known), mixer effects and FL's master chain.

`test/flp2nano.test.mjs` builds an FL project of its own and checks the whole path, the rendered slides included; with `FLP2NANO_SONGS=<folder>` it also checks the user's two songs against FL's own export (the same keys at the same times).

## Presets

The order is the program number, and it is append-only. All 27 were written for this project from the chip's own parameters; none is copied from a game.
`test/engine.test.mjs` checks that each one is audible, that the steady pitched ones are on pitch (within 15 cents) and that every pair is distinct.

| # | Name | Kind | What it is |
|---|---|---|---|
| 0 | `lead` | pulse | 25% pulse with a late vibrato |
| 1 | `square` | pulse | plain 50% square |
| 2 | `thin` | pulse | 12.5% pulse fading to a sustain |
| 3 | `pwm` | pulse | the duty cycles: a swirling tone |
| 4 | `pluck` | pulse | the chip's own envelope, a step every 16 ms |
| 5 | `chime` | pulse | 12.5% with a long hardware decay |
| 6 | `arp` | pulse | a major chord as a fast arpeggio |
| 7 | `pulsebass` | pulse | punchy 50% bass |
| 8 | `wavebass` | wave | sawtooth bass |
| 9 | `subbass` | wave | 4-bit sine |
| 10 | `pad` | wave | soft wave fading in, slow vibrato |
| 11 | `wavelead` | wave | buzzy saw lead |
| 12 | `organ` | wave | drawbar harmonics |
| 13 | `kick` | wave | a sine falling two octaves in 70 ms |
| 14 | `tom` | wave | a slower triangle fall |
| 15 | `snare` | noise | long noise, short decay |
| 16 | `hat` | noise | 7-bit metallic, short |
| 17 | `openhat` | noise | the same, ringing |
| 18 | `crash` | noise | long bright noise |
| 19 | `coin` | pulse | two notes a fourth apart |
| 20 | `jump` | pulse | the **sweep unit** pushing the pitch up |
| 21 | `laser` | pulse | the sweep unit dropping fast |
| 22 | `zap` | pulse | slow upward sweep steps |
| 23 | `powerup` | pulse | a climbing arpeggio |
| 24 | `explosion` | noise | noise sinking to a rumble |
| 25 | `hit` | noise | a short thump |
| 26 | `blip` | pulse | a menu beep |

**Preset fields.** Times are in ms, pitch is in cents and levels are 0 to 1:

| Field | What it does |
|---|---|
| `kind` | `'pulse'`, `'wave'` or `'noise'` |
| `note` | the pitch `play()` uses |
| `duty`, `dutySeq` | the pulse width; `dutySeq` cycles it |
| `a`, `d`, `s`, `r` | the software envelope, 4-bit steps |
| `env: { step, up }` | the chip's hardware envelope instead |
| `oneShot` | the sound plays out; needs `s: 0` |
| `gain` | the preset's level |
| `vib`, `slide`, `arp` | vibrato, a pitch slide, an arpeggio |
| `sweep: { period 1..7, down, shift }` | pulse 1's sweep unit |
| `wave` | a name from `BrokenNesNanoGB.waves` (sine, triangle, saw, square, pulse, organ, soft, buzz) or 32 numbers 0 to 15 |
| `noise: { shift 0..13, to, ms, ratio, short }` | the noise clock gliding from `shift` to `to`; `short` is the 7-bit LFSR |
| `pan` | -1, 0 or 1 |

The wave channel's volume has four steps: off, 25, 50 and 100 %.

## How it sounds like the chip

- **`chip.js`** transcribes `Windows/NesEmulator/gb/GnubSound.cs`, the C# port of gnuboy 1.0.3's `sound.c`.
  - gnuboy makes **one sample at a time**, at the page's own rate, so there is no resampler and nothing runs per CPU cycle.
  - It is all integer arithmetic on typed arrays.
  - The noise tables of `noise.h` are rebuilt from their recurrence, and a test checks them byte for byte against the C#.
  - RATE = 2^21 / rate, rounded down, so gnuboy runs slightly fast: 1.2 % at 44.1 kHz. The engine sets the frequency registers for the true rate, so notes are on pitch.
  - gnuboy's quirks stay because they are its sound: unipolar pulse and noise, NR50 swapped, no band limiting.
- **`engine.js`** turns notes into registers: the 4-bit level, the 11-bit frequency, triggers only for a new note, and a silenced channel switched off through its length counter so it costs nothing.
  Its arithmetic is plain doubles in a fixed order, with `exp2` as a polynomial and no `Math.pow`.
  **`Windows/NesEmulator/gb/NanoGbEngine.cs` is its line-for-line C# twin**, playing through the real `GnubSound`, and the plugin's Nano GB chip uses that twin.

## How it is proven

Run each check with `node Nano/gb/test/<name>.mjs`. You need Node 22 or newer, the .NET 10 SDK for `parity.mjs` and Edge or Chrome for `browser-test.mjs`.

| Check | What it compares | Result |
|---|---|---|
| `parity.mjs` chip | 79 note scripts:<br>• every preset at 3 pitches and 2 rates<br>• velocity, stealing, bends, pans<br>• the hardware envelope at an exact-step rate<br>• user waves, pitch extremes<br>• passages at 11 to 96 kHz<br>• 12 random scripts<br><br>Their register writes are replayed through `GnubSound` at the same rate, and at 44.1 kHz also through **`APU_GNUB`** itself, clocked in T-cycles | **bit-exact**: 5.6 M stereo samples, 17.2 k writes (one script, with the post-chip volume change, is engine-only) |
| `parity.mjs` engine | the same scripts through `NanoGbEngine` (C#) | **identical** register writes at the same samples, and identical PCM |
| `parity.mjs` fuzz | 21 random register streams at 7 rates: wave RAM, power cycles, every register | **bit-exact** (APU_GNUB too at 44.1 kHz) |
| `parity.mjs` MIDI | `test/midi/tune.mid` (4 parts, GM drums, a bend, a tempo change) and `chords-sfx.mid` + sidecar (chords on one channel with stealing, sound effects, RPN bend range), each through `renderMidi` and through the plugin's way (one `NanoGbEngine` per channel, `NanoGbParity midi`), at 44.1 and 48 kHz | **bit-exact**: level -21.98 / -21.98 dB, first sound at the same sample, spectral distance 0 |
| `--mutants` | 26 deliberate errors (chip, engine, MIDI player) | **all caught** |
| plugin `selftest --only nanogb` | FL notes through the real DLL against `NanoGbEngine` offline | **0 samples differ**; 0 bytes allocated over 3.6 s of 4 instances (see `Plugin/CLAUDE.md`) |
| `browser-test.mjs` | the worklet in headless Edge: the Blob and the `workletUrl` file against `renderOffline`; mono; `divide`; `loadMidi` against `renderMidi`; sidecar; `addWave`; bad arguments; real time (the analyser hears 439 Hz for A4, and no reports while `onactivity` is null) | **11 checks pass**, and all 5 of its own mutants are caught |
| `engine.test.mjs` | build `--check`, the lean build = the full build, mono = (L+R)/2, presets audible / on pitch / pairwise distinct, sweeps sweep, pan, 15 bad arguments, block-size independence, the song player, allocation | 415 checks |

**Together, these proofs make the FL-to-game chain exact.** The plugin chip is `NanoGbEngine` (selftest: DLL = engine), the engine is the JS engine (parity), and a `.mid` played by the
JS player is the same notes as FL playing them one instance per channel (MIDI check).

**What is not covered:**
- Real FL itself: how FL numbers channels in its MIDI export, and the instance order the sidecar assumes. Check both on the first song.
- The plugin's Volume parameter scales the output, while the sidecar's `volume` scales velocity. They are the same at the default (1.0), but they differ when the 4-bit level rounds.
- The plugin does not use the sidecar's `duty`, and channel 10 is not drums there: the Instrument parameter picks the preset.
- Firefox and Safari, and a real MIDI device.

## Cost and size (measured 2026-11-08, Node 25, this dev machine, other agents running)

| | Nano GB | Nano NES |
|---|---|---|
| CPU, `renderOffline` 4 busy voices at 48 kHz | **2.0 ms per audio second** (stereo 2.1, mono 2.0) | 30.6 ms in the same run (README: about 25) |
| at 22.05 kHz | **1.0 ms** | - |
| silent (all channels off) | 0.34 ms | the chip runs regardless |
| MIDI song, one chip per channel (4 chips) / one shared chip | 5.2 ms / 2.2 ms | - |
| in Edge (worklet in an OfflineAudioContext, 4 voices, 10 s) | 6.1 ms (an empty offline context: 0.5 ms; the browser's own block overhead dominates) | 20 to 25 ms |
| download, gzip / brotli | 24.4 / 21.6 KB; lean 18.8 / 16.8 KB | 23.4 / 20.6 KB |
| memory per synth | about 6 KB of state (4 KB noise table, 0.25 KB registers and channels) plus 2 x 512 B scratch | 16 KB + 128 KB + 128 KB |

So Nano GB costs about **one fifteenth** of Nano NES's CPU (the target was a tenth).

**Allocation:** the chip's render loop allocates nothing (tested over 60 s). The JS engine's control tick, 750 a second, leaves about 29 KB/s of
V8's short-lived number boxes and no objects. The C# twin allocates nothing (plugin selftest: 0 bytes).

## Credits and licences

The chip is gnuboy's sound. gnuboy 1.0.3 is by **Laguna and Gilgamesh and the gnuboy contributors** (`docs/CREDITS` of the source), revived at
https://github.com/rofl0r/gnuboy (commit `4c2f6c4`), and is under the **GPL-2.0**. BrokenNes records the port as a documented exception in `THIRD_PARTY_NOTICES.md` section 2,
like the other harvested cores. `dist/NOTICE.txt` and the header of every dist file carry this credit.

The engine, the presets, the MIDI player and the demos were written for BrokenNes under the Digital Lifeform License 1.1. Nothing here is copied from a game or a sound driver.
