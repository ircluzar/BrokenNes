# BrokenNes Nano

**The BrokenNes NES sound chip as a synth for web pages.** One script file, no server, no downloads at run time: a page that needs bleeps and bloops embeds it and
plays two pulse channels, a triangle, a noise channel and (optionally) DPCM samples from notes. It is the chip behind the BrokenNes2 FL Studio plugin, ported to
JavaScript and proven **sample for sample** against it (section "How it is proven").

```html
<button id="go">Play a coin</button>
<script src="nano.js"></script>
<script>
  let synth;
  go.onclick = async () => {
    synth ??= await BrokenNesNano.create();   // inside a click or key handler: browsers keep a page silent until the user has interacted with it
    synth.play('coin');                       // a one-shot sound effect; or synth.noteOn('C4', 1, { preset: 'lead', duration: 0.5 })
  };
</script>
```

That is `demo/minimal.html`. `demo/index.html` is the full demo: a keyboard (mouse and computer keys, MIDI), 24 presets plus two DPCM samples the page makes, sound effects, a
16-step tune player scheduled on the audio clock, the four channels and the DPCM channel as meters, and an oscilloscope. Serve the `Nano` folder with any static server
(`node Nano/test/serve.mjs Nano` prints a URL) and open `/demo/index.html`.

## Files

| File | What |
|---|---|
| `dist/nano.js` | **the module** (classic script, global `BrokenNesNano`; also `require()`s in Node). 74 KB readable source with its comments, **23 KB gzip, 21 KB brotli** |
| `dist/nano.mjs` | the same as an ES module (`import Nano, { create } from './nano.mjs'`) |
| `dist/nano-worklet.js` | only the audio worklet (16 KB gzip), for pages whose Content-Security-Policy forbids `blob:` scripts: `create({ workletUrl: 'nano-worklet.js' })` |
| `dist/LICENSE.txt` | the license, which must travel with copies |
| `src/` | the sources the dist is built from (`node Nano/build.mjs`; `--check` rebuilds in memory and compares) |
| `demo/`, `test/`, `tools/` | the demo pages, the tests, the Core-side reference tool (`tools/NanoParity`) and the measurement project for the WebAssembly decision (`tools/WasmFloor`) |

The dist files are generated, committed because they are small, and a test re-runs the build and compares them byte for byte. Nothing in them is minified: they are the sources
concatenated. Pass them through your own minifier if you want (about 19 KB gzip with the comments gone).

## The API

Everything is on the global `BrokenNesNano` (or the module's exports).

### `BrokenNesNano.create(options)` -> `Promise<Synth>`

Makes a synth: loads the audio worklet (from a Blob made out of the script itself) and connects it. Options, all optional:

| option | meaning |
|---|---|
| `context` | an `AudioContext` (or `OfflineAudioContext`) of yours; otherwise Nano makes one and `dispose()` closes it |
| `destination` | an `AudioNode` to connect to; `false` leaves the synth unconnected (then `synth.connect(...)` it). Default: `context.destination` |
| `volume` | output gain, default **1.5**. The raw chip is quiet by design (one full-volume pulse peaks at -16 dBFS); 1 is exactly the chip's own level, 3 is the practical top for a full chord |
| `preset` | the preset `noteOn` uses when you do not name one (default `'lead'`) |
| `presets` | extra presets, `{ name: definition }` (see "Presets") |
| `sampleRate`, `latencyHint` | passed to the `AudioContext` Nano makes |
| `workletUrl` | load the worklet from this URL instead of a Blob (strict CSP) |

**Autoplay.** `create()` itself is allowed anywhere, but the browser keeps the context `suspended` until a user gesture. Call it from a click or key handler
(`noteOn` and `play` also try `context.resume()`, which only works inside a gesture), or `await synth.resume()` in one. `synth.context.state` says what happened.

**No fallback.** In a browser without AudioWorklet, or without a secure page, `create()` rejects with an `Error` whose `code` is `'unsupported'` and whose message says why. It never plays
something else. `BrokenNesNano.support()` answers the same question up front: `{ ok, reason, warning? }`.

### The synth

| member | meaning |
|---|---|
| `noteOn(note, velocity = 1, opts)` | starts a note; returns a **handle** `{ id, off(opts), bend(cents, opts) }`. `note`: a MIDI number (60 = middle C, fractions allowed) or a name (`'C4'`, `'F#3'`, `'Bb2'`). `velocity`: 0 to 1 |
| `noteOff(handleOrNote, opts)` | ends a note: the handle, or a note number/name (the newest held note with that pitch) |
| `play(preset, opts)` | a one-off sound effect at the preset's own pitch: `synth.play('coin')`; `opts.note`, `opts.velocity` change them |
| `bend(handle, cents, opts)` | bends one sounding note |
| `allOff(opts)` | releases every held note (release tails and one-shots play out) |
| `panic(opts)` | silences everything at once, DPCM included |
| `setParam(name, value, opts)` | `'volume'` (0 to 8), `'pitchBend'` (cents, all notes), `'tune'` (cents, all notes), `'preset'` (the default preset) |
| `addPreset(name, definition)` | adds or replaces a preset |
| `addSample(name, data, opts)` | adds a DPCM sample (below) |
| `presets` | the presets this synth knows: `[{ name, label, kind, desc, note }]` |
| `connect(node)` / `disconnect()` | route the output into your own Web Audio graph (a `GainNode`, an analyser, a convolver); `synth.node` is the `AudioWorkletNode` itself |
| `volume` | the output gain (get and set) |
| `onactivity` | a callback, about 40 times a second: `{ channels: [{ active, level, note } x 5], peak }` for pulse 1, pulse 2, triangle, noise, DPCM (meters, visualisers) |
| `onerror` | a callback for errors raised inside the audio thread |
| `connectMidi(opts)` | plays a MIDI keyboard (below) |
| `resume()`, `sync()`, `dispose()` | resume the context; resolve when the audio thread has received everything sent so far (before an `OfflineAudioContext` render); stop and release (closes a context Nano made) |

`opts` of `noteOn` / `play`:

| option | meaning |
|---|---|
| `preset` | a preset name, or a preset definition object |
| `time` | when to start, in `AudioContext` seconds (`ctx.currentTime + 1`): scheduled on the audio clock, to the sample. `delay`: seconds from now. Without either: as soon as the audio thread gets the message (about 3 ms) |
| `duration` | seconds until the note ends by itself |
| `channel` | pin the note to a chip channel: 0 pulse 1, 1 pulse 2, 2 triangle, 3 noise, 4 DPCM (must suit the preset) |
| `duty` | 0 to 3: 12.5, 25, 50, 75 percent, over the preset's own |
| `noiseMode` | 0 long, 1 short (metallic), over the preset's own |
| `bend` | an initial bend in cents |

The same `time` / `delay` apply to `noteOff`, `bend`, `allOff`, `panic`, `setParam`. Bad arguments throw (`RangeError`/`TypeError`) at the call; they are not ignored.

### Without an audio context

```js
const pcm = BrokenNesNano.renderOffline(
  [{ time: 0, type: 'on', note: 'A4', preset: 'square' }, { time: 0.5, type: 'off', note: 'A4' }],
  1.0,                                     // seconds
  { sampleRate: 44100, volume: 1 });       // a Float32Array of mono samples; volume 1 = the chip's own level
const buffer = BrokenNesNano.toAudioBuffer(ctx, events, seconds);   // the same, as an AudioBuffer of `ctx`
```

This is the same engine, bit for bit, and it needs no AudioWorklet: it **works from a `file://` page**, in Node, and is the cheapest way to play a sound effect (render it once, play the buffer; 1 s of
audio costs about 25 ms of CPU to make). Events: `{ time, type: 'on', note, velocity, preset, id?, duration?, channel?, duty?, noiseMode?, bend? }`, `{ time, type: 'off', id | note }`,
`{ type: 'bend', id, cents }`, `{ type: 'alloff' }`, `{ type: 'kill' }`, `{ type: 'param', name, value }`; `opts.presets` and `opts.samples` add presets and DPCM samples.

## Presets

A preset is plain data. The 24 built-ins were written for this project from the chip's own parameters (none is copied from a game or its sound driver). Each is audible, on pitch where it has a pitch,
and different from every other, checked by `test/engine.test.mjs`.

| name | label | kind | what it is |
|---|---|---|---|
| `lead` | Lead | pulse | 25% pulse with a late vibrato |
| `square` | Square | pulse | plain 50% square, steady |
| `thin` | Thin pulse | pulse | 12.5% pulse, nasal, fading to a sustain |
| `pwm` | Moving pulse | pulse | the pulse width cycles: a swirling tone |
| `pluck` | Pluck | pulse | plucked string: starts loud, dies away on its own |
| `organ` | Organ | pulse | octave flutter at 60 Hz, like a driver faking two voices |
| `arp` | Arpeggio | pulse | major chord played as a fast arpeggio on one channel |
| `pulsebass` | Pulse bass | pulse | punchy 50% bass that settles |
| `bass` | Bass | triangle | the triangle channel: round, no volume steps |
| `trilead` | Triangle lead | triangle | soft lead with vibrato |
| `kick` | Kick | triangle | a triangle falling two octaves in 80 ms |
| `tom` | Tom | triangle | a slower triangle fall |
| `snare` | Snare | noise | noise, mid period, short decay |
| `hat` | Closed hat | noise | short metallic noise |
| `openhat` | Open hat | noise | the same noise, left ringing |
| `crash` | Crash | noise | long bright noise |
| `laser` | Laser | pulse | a thin pulse falling three octaves |
| `zap` | Zap (sweep unit) | pulse | the chip's own sweep unit drags the pitch down until it mutes |
| `coin` | Coin | pulse | two notes a fourth apart, the second ringing out |
| `jump` | Jump | pulse | a rising pulse, an octave in 210 ms |
| `powerup` | Power up | pulse | a fast climbing arpeggio |
| `blip` | Blip | pulse | a short menu beep |
| `explosion` | Explosion | noise | noise sinking to a rumble |
| `hit` | Hit | noise | a short thump of noise |

Your own: `synth.addPreset('mine', { kind: 'pulse', duty: 2, a: 5, d: 100, s: 0.7, r: 40 })`, or pass the object as `opts.preset`. Fields (times in ms, pitch in cents, levels 0 to 1):

| field | meaning |
|---|---|
| `kind` | `'pulse'`, `'triangle'` (no volume control: on or off) or `'noise'` (16 fixed pitches). Required |
| `note` | the pitch `play()` uses (MIDI number, default 60) |
| `duty`, `dutySeq: { steps: [0..3], rate }` | pulse width 0 to 3 (12.5, 25, 50, 75 percent); `dutySeq` cycles it `rate` times a second |
| `a`, `d`, `s`, `r` | volume envelope: attack, decay (ms), sustain level, release (ms). The 4-bit volume steps are the chip's. `r: 0` closes the channel at note-off, as the console does |
| `gain` | preset loudness, multiplies velocity |
| `oneShot` | a note-off is ignored and the sound plays out (drums, effects); needs `s: 0`, and ends by itself after `a + d` |
| `vib: { rate, depth, delay, fade }` | triangle-wave vibrato: Hz, cents, ms before it starts, ms to reach full depth |
| `slide: { from, to, ms }` | a pitch offset gliding from `from` to `to` cents over `ms`, then staying at `to` |
| `arp: { steps: [semitones], rate, once }` | an arpeggio, `rate` steps a second; `once` stops on the last step |
| `noise: { idx, to, ms, mode }` | the noise period index (0 highest, 15 lowest) gliding `idx` to `to`, and the long/short mode; without it the noise follows the note's pitch |
| `sweep: { period, negate, shift }` | the chip's own sweep unit (pulse only). Pitch then belongs to the hardware: vibrato, slide, arp and bends are ignored |

### DPCM samples

`synth.addSample('thump', data, opts)` adds a sample as a preset of the same name; the chip plays it on its fifth channel. `data` is raw DPCM bytes (a `Uint8Array`: one bit per step) or PCM audio (a
`Float32Array`, with `opts.sampleRate`) that `BrokenNesNano.encodeDpcm(pcm, { sampleRate, rate, gain, start })` turns into DPCM for you, following the chip's own counter. `opts`: `rate` (0 to 15,
the chip's 16 sample rates from 4.2 to 33.1 kHz; default 15), `loop`, `note`, `pitched` (the note picks the nearest of the 16 rates, relative to `note`), `start` (the output level the sample starts
from, default 0), `label`, `desc`. A one-shot plays once; a looped sample plays until note-off. **The DMC has no volume control**: velocity 0 does not play, any other velocity plays at the sample's level.
All samples together take at most 16 KB (the chip addresses `$C000-$FFFF`); `addSample` throws when it is full.

### MIDI

`await synth.connectMidi()` (the browser asks for permission) plays every MIDI input: note on/off, pitch bend (`bendRange` semitones, default 2), program change (picks a preset by number), CC 120 / 123, and
on channel 10 the General MIDI drum notes (kick, snare, hats, toms, crash). Options: `input` (id or name), `channel`, `bendRange`, `drums`. It returns `{ close() }`. **Not tested with a device**: the message handling is
tested with synthetic messages through the real worklet; the permission prompt and real hardware were not (headless Edge refuses the permission, and the error reaches the caller).

## How it sounds like the chip, and what it adds

Two layers, both in `src/`:

1. **The chip** (`chip.js`): a transcription of `Windows/NesEmulator/apus/APU_FIX.cs` (the accuracy-target core) and `Core/StreamResampler.cs`, driven exactly as `Core/NesApuInstrument.cs` drives them: the registers
   `$4000-$4017` are written, the chip steps 32 CPU cycles at a time, one sample is made every 1789773/44100 cycles, and the 44.1 kHz stream is resampled to the host rate with a 4-point Hermite interpolator.
   Every float of the original is a float32 here (`Math.fround` after each operation), which is why the output is the same bits. All four tone channels and the DMC are in it.
2. **The instrument** (`engine.js`): what turns notes into register writes. `NesApuInstrument` is a *bare* chip: it has no envelopes, duty, sweep or voices. Those live in the plugin's Direct mode
   (`Plugin/BrokenNes2.Plugin/Bn2Plugin.cs`: `Apply`, `Silence`, `ResolveOwner`), and Nano follows it: pitch in cents becomes the period register with the plugin's formulas (rounded to even, as C# does),
   the 4-bit volume is `round(15 x volume)` computed in float32 like the plugin's, every control tick of **64 samples** each channel's registers are rewritten from the voice that owns it and only the ones that changed are written,
   `$4003`/`$400B` (which restart the waveform) are written only on a new note or when the period's high bits change, and a channel is **monophonic with last-note priority**: a newer note takes the channel and the
   older one waits under it and comes back when the newer one ends (a sound effect that steals a channel gives it back, as on the console).
   Where the plugin takes pitch and volume from FL's piano roll, Nano takes them from the preset's envelope, vibrato, slide, arpeggio and the note-on / note-off / bend events.

Differences from the plugin, all deliberate: Nano is **one chip with all channels** (the plugin runs one chip per FL instance and sums them in FL), so chords go through the real nonlinear NES mixer;
two pulse notes at once use both pulse channels (a third steals the oldest); the output is mono, with a gain stage after the chip (`volume`) that is not part of the proof (the proofs run at volume 1).

## How it is proven

Everything below was run; the numbers are in `docs/projects/round5-nano.md`. `node Nano/test/<name>.mjs` runs each; `UAT/entrypoints/nano-smoke.ps1` runs them together.

| check | what it compares | result |
|---|---|---|
| `parity.mjs`, chip | the PCM of 77 note scripts (every preset at three pitches and two host rates, the velocity ladder with the ties that round to even, pitch extremes, stealing and note return, release tails, one-shots, the sweep unit, bends, DPCM, a four-channel passage at four host rates, 16 seeded random scripts) against Core's `NesApuInstrument` (and, for DPCM, the FIX core on a 16 KB cartridge) replaying the register writes the web module made | **bit-exact: 16.8 M samples, 25.4 k register writes** |
| `parity.mjs`, instrument | the same scripts through a second implementation of the instrument layer (C#, `tools/NanoParity/NanoReference.cs`) on Core: same registers at the same samples, same PCM | **identical** |
| `parity.mjs`, chip fuzz | 18 seeded random register streams (every register but the DMC's, both frame-counter modes, six host rates from 16 to 96 kHz) | **bit-exact** |
| `plugin-parity.mjs` | the `BrokenNes2_x64.dll` plugin, rendered by the FL test host playing piano-roll notes with slides, bends, overlapping notes, extreme pitches and tiny velocities, on pulse 1, pulse 2 (three duties), the triangle and the noise channel (both modes) | **bit-exact, 6 of 6 runs** |
| `browser-test.mjs` | the AudioWorklet itself in headless Edge: its output (from the embedded Blob and from `nano-worklet.js`) against `renderOffline`; custom presets, DPCM, MIDI messages, `connect()`; the browser's own analyser hears 439 Hz for A4 | **bit-exact, 14 checks** |
| `engine.test.mjs` | every preset audible and on pitch (zero crossings), presets pairwise distinct (spectra and envelope), vibrato / slide / arpeggio move the pitch, voice handling, DPCM encoder round trip, block-size independence, random scripts finite | 122 checks |

**Tolerance: zero.** The PCM is compared as raw float32 bits. Each check was shown able to fail: `parity.mjs --mutants` injects 34 deliberate errors one at a time (a filter coefficient, one entry of a duty table, a rounding mode, the stealing rule ...) and every one is
caught; `engine.test.mjs --mutants` 14; `browser-test.mjs --mutant N` 4; `plugin-parity.mjs --mutant`. And it failed for real once: when `APU_FIX` was corrected on `main` (five fixes found by the BrokenDev differential replay against Mesen), the chip
as first written failed 18 of 77 scripts and all 18 fuzz traces against the new Core, until it was updated to follow.

**This is a promise about the BrokenNes FIX chip, not about every NES.** When `APU_FIX.cs` changes, `parity.mjs` fails and `chip.js` must follow (`UAT/certify.ps1` runs it as the `nano` entry).

Not covered by the proof: the browser's own mixing after the worklet, sound through a loudspeaker (the dev machine has no sound card; what was measured is the browser's analyser and the sample values), host rates under 11 kHz
(Core's `StreamResampler` itself throws there; Nano's port clamps and plays, untested against Core), Firefox and Safari.

## Cost and size

| | |
|---|---|
| download | `nano.js` 74 KB, 23.4 KB gzip, 20.6 KB brotli; `nano-worklet.js` alone 52 KB, 16.2 KB gzip. Nothing else is fetched |
| CPU | one chip simulates 1.79 M cycles a second whatever is playing: **about 20 to 25 ms of CPU per second of audio** (2 to 3 percent of one core; 40 to 48x real time) in Node and in Edge, the same with 1 or 4 voices sounding, ~21 ms measured for the chip alone in the desktop app's browser pane. It runs while the synth is connected, silent or not; `dispose()` or `disconnect()` a synth you are not using |
| start-up | `create()` took 75 to 410 ms (module load and worklet start; the spread is the shared dev machine) and the first activity report came 60 to 330 ms after `noteOn` in headless Edge (it includes the report's own 23 ms cadence). Latency after that is the `AudioContext`'s: 10 ms base latency here |
| memory | a 16 KB sample memory, a 128 KB mixer table, a 128 KB resampler buffer per synth |

Why JavaScript and not the C# compiled to WebAssembly: measured, in `docs/projects/round5-nano.md` section 1 (886 KB brotli at 650 ms of CPU per audio second without AOT, 1.56 MB brotli at 42 ms with it, for the
APU alone, and a runtime that cannot run inside an AudioWorklet).

## Browser support

| | |
|---|---|
| tested | Edge 155 (Chromium) headless and in the desktop app's browser pane, on Windows: everything above |
| needs | `AudioWorklet`, `AudioContext`, `Blob` + `URL.createObjectURL` (or a `workletUrl`), a secure page (`https:`, `http://localhost`) |
| expected, not tested | current Chrome, Firefox (76+) and Safari (14.1+) have AudioWorklet; Web MIDI is Chromium and Firefox only |
| **`file://`** | **`create()` does not work**: Chromium refuses to load any AudioWorklet module on a `file://` page, from a Blob or a file alike (tested). `create()` rejects with a message that says so; `renderOffline` and `toAudioBuffer` do work there |
| CSP | a policy that forbids `blob:` scripts needs `create({ workletUrl })` with `nano-worklet.js` |

## What is not supported

No stereo or panning (the chip is mono: route `synth.node` through a `StereoPannerNode`). No other sound chips (the plugin's Game Boy, SNES and Sega chips are not in Nano; the FIX chip only). DPCM has no volume control and
pitch only through the 16 rates, chosen when the note starts. DPCM samples are limited to 16 KB. Presets cannot change a note's pitch live beyond bends. Pitch is the chip's: whole period steps, so about 8 cents apart at 500 Hz and coarser above. No
No AudioWorklet on `file://` pages. Not tested: Safari, Firefox, a real MIDI device.

## Licensing

Nano is part of BrokenNes and under the **Digital Lifeform License 1.1** (`dist/LICENSE.txt`, the repository's `LICENSE.txt`): the license text has to accompany copies, and the license's provenance and
agent-assistance conditions apply. `THIRD_PARTY_NOTICES.md` (root ledger) has a row for Nano. Nano contains no third-party source, **but its chip is a transcription of `APU_FIX`, which was forked from `APU_FMC`, which the
ledger records as loosely based on NET-NES by BotRandomness, whose license is not recorded in this repository yet**. That open question is inherited, not new; settle it before publishing Nano outside the repository.
The NES hardware tables in the chip (length counter table, noise periods, DMC rates, duty sequences, the mixer formulas) are public facts of the hardware. The presets, the engine and the demo were written for this project;
`demo/index.html` uses system fonts only. Nothing here is copied from a game: no ROM, no game audio, no sound driver.
