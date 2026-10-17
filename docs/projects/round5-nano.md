# Round 5: BrokenNes Nano (the NES synth for web pages)

Agent `nano`, 2026-10-16/17. Domain: the new top-level `Nano/` folder and this record. Everything is committed on the worktree branch; nothing is pushed or merged.
User-facing description: `Nano/README.md`.

## 0. Summary

A page embeds **one file** (`Nano/dist/nano.js`, 74 KB, 23 KB gzip) and gets the BrokenNes NES sound chip as a synth in an AudioWorklet: two pulse channels, a triangle, a noise channel and DPCM samples, 24 presets,
`noteOn` / `noteOff` / `play` / scheduling on the audio clock / bends / `connect()` into the page's own graph / MIDI. Its output is **bit-for-bit** the output of Core's chip (`NesApuInstrument`, FIX) and of the
real `BrokenNes2_x64.dll` plugin. What was not tested is listed in section 6.

## 1. Decision: how to build it (numbers first)

**Chosen: (a) a hand-written JavaScript port of the sound chip, run in an AudioWorklet, with the instrument layer on top.** Measured, not assumed.

What "the same sound as Core" means, found by reading the code: `Core/NesApuInstrument.cs` is a *bare chip* (the host writes `$4000-$4017`, it pulls samples through
`APU_FIX` and a Hermite resampler). It has no envelopes, duty, sweep or voices of its own. The instrument model (note to period, 4-bit level, retrigger rules,
monophonic channels with last-note priority) is the plugin's Direct mode, `Plugin/BrokenNes2.Plugin/Bn2Plugin.cs` (`Apply`, `Silence`, `ResolveOwner`). So Nano is two
layers: the **chip** (a transcription of `APU_FIX.cs` + `StreamResampler.cs`, provably bit-identical) and the **instrument** (the plugin's register mapping, plus the
envelope / vibrato / slide / arpeggio / sweep that FL supplies in the plugin and Nano supplies in presets).

| | (a) JS port, AudioWorklet | (b) C# to WebAssembly, best case (only `APU_FIX` + resampler, minimal .NET wasm app, no Blazor) |
|---|---|---|
| Download, interpreter build (no AOT) | n/a | 2.9 MB raw, 1.08 MB gzip, **886 KB brotli** |
| Download, AOT build | n/a | 6.9 MB raw, 2.2 MB gzip, **1.56 MB brotli** |
| Download, the whole `Core` (what `NesApuInstrument` really needs: `NES`, `CoreRegistry`, every core by reflection) | n/a | not built; larger than the floor above; the Lite site (all cores, AOT, trimmed) is **4.6 MB brotli** per `WebLite/BrokenNes.Lite.csproj` |
| Download, the Nano module | the chip alone 18.5 KB source, 5.4 KB gzip; **the finished module (chip, engine, 24 presets, DPCM, MIDI, API, worklet) 74 KB, 23.4 KB gzip, 20.6 KB brotli** | |
| CPU per second of audio, Node, 48 kHz, chip running | **20 to 28 ms** (2 to 3 % of a core, 36 to 49x real time), the same with 1 or 4 channels sounding | |
| CPU per second of audio, Chrome (the Browser pane of the desktop app, same V8), same register script | **20 to 22 ms** (4 runs) | non-AOT **620 to 700 ms** (1.5x real time: unusable); AOT **42 ms** |
| Start-up (localhost, no network) | one `addModule` of the script: `create()` 75 to 410 ms in headless Edge on the loaded dev machine | .NET boot 119 ms + first block 75 ms (non-AOT); boot 143 ms + 16 ms (AOT) |
| Where it can run | in the AudioWorklet itself (own thread, no jitter) | the .NET runtime needs `fetch`, a loader and worker plumbing that an AudioWorklet scope does not give; it would run on the main thread or a worker and hand samples to a worklet over `postMessage` (jitter) or `SharedArrayBuffer` (needs cross-origin isolation headers, which a plain static page cannot set) |

The first 128-sample block of the same register script has the same peak (0.3974248170852661) in the JS chip, in Node, in Edge and in the C# AOT WebAssembly build: three implementations, one number.

Reading of the numbers: the C# route costs 70 to 270 times the bytes of the JS route (and that is the floor, not the real Core), is no faster once AOT'd (42 ms against 20 to 28 ms),
and cannot sit in the audio thread of an arbitrary static page. (c) "something smaller", for example native `OscillatorNode`s with a `PeriodicWave` and a noise
buffer, would be a few KB but cannot be the same sound: no 4-bit stepped mixer curve, no aliasing of the real pulse, no DC filter, no LFSR noise, no 44.1 kHz Hermite path.
It cannot be held to the "provably the same as Core" bar, so it was rejected without building it (stated, not measured).

How the numbers were taken (so they can be repeated): `Nano/tools/WasmFloor` is the floor project (`dotnet publish Nano/tools/WasmFloor/WasmFloor.csproj -c Release [-p:EnableWasmAot=true]
--artifacts-path <dir>`, then serve `publish/WasmFloor/release/wwwroot` with `node Nano/test/serve.mjs` and open it: it prints boot, first block and ms per audio second into the page).
Sizes are the files in `wwwroot` compressed with Node's `zlib` (gzip level 9, brotli default quality 11). The JS CPU numbers are medians of runs of 5 to 10 s rendered through
`Chip.render` in blocks of 128 on this shared machine (noisy: other agents were running; the Edge figure was taken in the desktop app's own browser pane).

## 2. What exists

All under `Nano/`; `Nano/README.md` has the API and the preset list.

| Path | What |
|---|---|
| `src/chip.js` | the chip: `APU_FIX` + `StreamResampler`, float32-exact, the DMC reading user sample memory |
| `src/engine.js`, `src/presets.js` | the instrument layer after the plugin's `Apply`; 24 presets; voices, stacks, scheduling, DPCM sample bank |
| `src/api.js` | the page API, the worklet processor, `renderOffline`, `toAudioBuffer`, `encodeDpcm`, MIDI |
| `build.mjs`, `dist/` | builds `nano.js`, `nano.mjs`, `nano-worklet.js`, `LICENSE.txt` (generated, committed, `-text` in `.gitattributes`, rebuilt and compared by a test) |
| `demo/index.html`, `demo/minimal.html` | the demo and the 10-line embed |
| `test/` | `parity.mjs`, `plugin-parity.mjs`, `engine.test.mjs`, `browser-test.html` + `browser-test.mjs`, `scenarios.mjs`, `lib.mjs`, `serve.mjs`, `shot.mjs` |
| `tools/NanoParity/` | the Core side: replays traces through `NesApuInstrument` / a NES with a 16 KB cartridge; `NanoReference.cs` = the instrument layer again in C# |
| `tools/WasmFloor/` | the measurement project for the decision (section 1) |
| `UAT/entrypoints/nano-smoke.ps1`, `UAT/certify.ps1` (+17 lines: the `nano` entry) | runs the tests together; NOT RUN with the reason when node, a browser or the plugin DLL is missing |

Edits outside `Nano/`, named: this record; a row for Nano in `THIRD_PARTY_NOTICES.md` (section 2, the NET-NES provenance question Nano inherits through `APU_FIX`); `UAT/entrypoints/nano-smoke.ps1` (new); `UAT/certify.ps1` (the header table row and one `Run 'nano'` block).
Nothing in `Workshop/`, `Core/`, `Plugin/`, `Windows/` was touched. `notices-check.ps1` is green.

### Design decisions

- **One chip, all channels** (the plugin runs a chip per FL instance and FL sums them): a chord goes through the real nonlinear mixer. A single channel playing alone is identical to the plugin's output (proven).
- **The instrument layer is the plugin's, including its quirks**: pitch to period with `Math.Round` half-to-even (JS rounds half up: `roundEven`), and the level as `round(15 x float32(volume))` with the product taken in **float32** (found by the plugin
  comparison: 0.3 x 15 is 4.5 in float32, so the plugin plays level 4, where double arithmetic gives 5). Every control tick is 64 samples, like the plugin's `Chunk`.
- **Voices**: per channel a stack, newest on top, monophonic with last-note priority, so a sound effect that steals a pulse channel gives it back (note return, as the plugin's `ResolveOwner`). A new note takes a free channel of its kind, else one in its release tail, else the one playing longest. At most 8 voices per stack.
- **Timing**: events carry a sample-exact frame (`time` in `AudioContext` seconds); the engine splits a block at every event and ticks at multiples of 64 samples of engine time, so the block size of the host does not change a sample (tested: 1, 128, 441, 5000).
- **Two threads, one source**: the engine is one function (`engineKit`), used as live code on the main thread (`renderOffline`, validation) and turned into the Blob worklet by `Function.prototype.toString`, so the page needs no second file.
- **No silent fallbacks**: no AudioWorklet, an insecure page, a `file://` page (Chromium refuses worklets there), a CSP that forbids blobs, a full sample memory, a bad argument: each is an error with a message, never a different sound.

## 3. Findings about Core on the way

1. `Core/NesApuInstrument.cs` has no instrument model (see section 1); the model is the plugin's.
2. **`StreamResampler` throws `ArgumentOutOfRangeException` for host rates under about 11 kHz** (a quarter of 44.1 kHz): after the last read the position can land past the buffered input and the shift `AsSpan(drop, count - drop)` goes negative. Found by an 8 kHz parity run; nobody hosts at 8 kHz, so it is harmless today. Nano's port clamps (`drop <= count`), which changes nothing at any rate where Core works; rates from 11.025 kHz up are tested against Core.
3. **The chip drifted under Nano once, and the verifier caught it.** After merging `main`, `APU_FIX.cs` had five corrections from the BrokenDev differential replay against Mesen (the envelope start flag only from `$4003/$4007/$400F`, the triangle reload flag only from `$400B`, the sweep unit's enable bit and order, the pulse sweep period at power-on, the 5-step frame counter table). The chip as first written, run against the new Core: **18 of 77 note scripts** (every one that uses the sweep unit: `zap`, `sweep_unit`, the random scripts) and **18 of 18 fuzz traces** failed, with first differences at samples 449 to 15458 and peak differences 0.20 to 0.40. `chip.js` was updated to follow; the same 77 scripts and 18 traces then pass bit-exact. This is the maintenance rule for Nano: **when `APU_FIX.cs` changes, `parity.mjs` fails and `chip.js` must follow**; `UAT/certify.ps1`'s `nano` entry runs it.

## 4. Verifiers (every one shown able to fail)

Tolerance: **zero**. The chip's arithmetic is float32 in both languages, so the PCM is compared as raw bits; there is no RMS bound to state.

| Verifier | What it proves | Result | Proof it can fail |
|---|---|---|---|
| `test/parity.mjs`, chip check | 77 note scripts through the shipped `dist/nano.js` give PCM; the register writes the module made are replayed through Core's `NesApuInstrument` (FIX); for DPCM scripts through a NES with a 16 KB cartridge holding the samples, driven as `NesApuInstrument` drives it. Scripts: every preset at 3 pitches x 2 host rates (48), velocity ladder (14 velocities incl. 0.3, 0.7 and exact halves), pitch extremes (0 to 127, fractions, every duty, noise modes), voice handling, the sweep unit, bends, a 4-channel passage at 44.1, 48, 32 and 96 kHz, DPCM (one-shots, loops, note-off, kill, taking a channel and giving it back, `velocity 0`, pinned channel, 4 host rates), 16 seeded random scripts | **77/77 bit-exact; 16,830,695 samples, 25,417 register writes** | `--mutants`: 33 deliberate errors injected one at a time into a copy of the module (filter coefficients, mixer constant, a duty-table entry, the noise period table, the noise tap, the divider off by one, the triangle reload, the sweep (ones' complement, enable bit), the frame counter step and the 5-step table, the Hermite coefficient, a resampler rate, the DMC step, rate table, loop flag, byte offset, start level, rounding mode, period formula, stealing rule, retrigger, release tail, vibrato slope, noise index, one-shots, tick length, shadow compare, triangle off value, velocity gain): see section 5 for the final run. The largest differences that were caught: 5.8e-8 (the low-pass kept in double precision) and 1.4e-5 (a mixer constant) |
| `test/parity.mjs`, instrument check | the same scripts through `tools/NanoParity/NanoReference.cs`, a second implementation of the instrument layer in C# on Core: the same register writes at the same samples (as lists) and the same PCM | **77/77 identical** | the same mutants (the engine ones are caught here by the trace lists: e.g. "trace length 76 vs 70") |
| `test/parity.mjs`, chip fuzz | 18 seeded random register streams (every register but the DMC's, both frame-counter modes, 6 host rates 16 to 96 kHz) through `NesApuInstrument` | **18/18 bit-exact** | the pre-merge chip against the new Core: 0/18 (section 3) |
| `test/plugin-parity.mjs` | the **real plugin DLL** (`BrokenNes2_x64.dll`, built 2026-10-16 23:25, after the `APU_FIX` fixes) rendered by `BrokenNes.FruityHost render` at 48 kHz, 64-sample blocks: piano-roll notes with slides, per-note bends, overlapping notes (note return), periods clamped at 0x7FF and muted below 8, velocities 0.02 to 1; on pulse 1 (duty 25 % and 75 %), pulse 2 (12.5 %), the triangle, the noise channel (long and short). Nano plays the same notes with the plugin's constant-volume preset at volume 1 | **6/6 bit-exact** (254,400 and 158,400 samples each, left and right channels identical) | `--mutant` (one wrong digit in the pulse period formula): the three pulse runs fail from sample 389, the triangle and noise runs stay exact; while it was being written it found one real difference in the instrument layer (the float32 level product, fixed) and made the one deliberate difference visible (Nano gives two overlapping pulse notes two channels, where one plugin instance has one: the comparison pins the notes to the same channel) |
| `test/engine.test.mjs` | 122 checks without Core or a browser: `dist/` equals a fresh build, license copy, size and no-network-call guards, API errors outside a browser (`create()` rejects with `code: 'unsupported'`), every one of the 24 presets audible (peak 0.03 to 0.7) and, where flat, **on pitch within 6 cents** of the chip's quantised note (zero-crossing measurement), presets pairwise distinct (spectra + envelope, closest pair `lead` / `pwm` at distance 0.29, threshold 0.25), vibrato / slide / arpeggio move the pitch by the right amounts, voice stealing / note return / release tails / durations / late events, DPCM (encoder round trip: a looped 441 Hz sine plays at 437 Hz, one-shots end, memory full, no volume, `pitched` follows the note), **block-size independence**, random scripts finite and below full scale | **122 passed** | `--mutants`: 14 injected errors, final run in section 5 |
| `test/browser-test.mjs` (headless Edge 155) | the AudioWorklet itself: its output, from the embedded Blob and from `nano-worklet.js`, against `renderOffline` for the same script (timed events through the API), bit for bit; two synths on one context; `toAudioBuffer` through an `AudioBufferSourceNode`; bad arguments throw; the preset list; a custom preset; a DPCM sample; MIDI messages (note on/off, bend, GM drums, program change, all notes off); `connect()` into a `GainNode`; a 10 s four-voice render; a real-time context whose output the browser's own `AnalyserNode` reads: **A4 is heard at 439 Hz** | **14 passed**; `--file` (a `file://` page): `create()` rejects with an explanation, `toAudioBuffer` works | `--mutant N`: 4 wrapper errors (timed events one sample late, applied at once, the last sample of every block dropped, volume ignored) are each caught by 3 to 4 checks |

How the browser test is run: `node Nano/test/browser-test.mjs` starts a static server and Edge with `--headless=new --remote-debugging-port=0 --autoplay-policy=no-user-gesture-required`, drives it over the DevTools protocol, and kills only the process tree it started.
An `OfflineAudioContext` is used for the deterministic comparisons (with `synth.sync()` first: without it the worklet's messages race the first render quantum, which the early runs showed as false failures).

## 5. The final full run

After the last code change (`dist/` rebuilt from the sources, the merge of `refs/heads/main` at 89eeec7 in), `pwsh UAT/entrypoints/nano-smoke.ps1 -Mutants -FruityHost <host> -PluginDll <dll>` was run once, start to end, in **395 s** on the shared machine:

| Step | Result |
|---|---|
| build: `dist/` is exactly what `build.mjs` makes | PASS |
| engine tests (122 checks) | PASS |
| parity with Core: 77 scripts + 18 fuzz traces, **33 of 33 injected errors caught** | PASS |
| parity with the plugin DLL (`bn_build` publish of 2026-10-16 23:25): 6 of 6 runs bit-exact | PASS |
| browser (headless Edge 155): 14 of 14; the analyser hears 439 Hz for A4; `create()` 69 ms, first activity report 52 ms after `noteOn` in this run | PASS |

`engine.test.mjs --mutants` (14 of 14 caught) and `browser-test.mjs --mutant 0..3` (4 of 4 caught) were run separately, not inside `nano-smoke.ps1`.
**The runner itself can fail**: with one byte appended to `dist/nano.js` (and nothing else changed), `nano-smoke.ps1` reports FAIL for the build check and the engine tests (which re-run it), skips the plugin step and says so, and exits 1; the parity and browser steps still pass, as they should (a trailing space changes no sound). The file was rebuilt afterwards.

The plugin DLL used is built from the shared tree after the five `APU_FIX` corrections (commit df02acf, 21:47) and is not rebuilt by this agent; the plugin runs do not reach the sweep unit or the 5-step frame counter, so they would also pass for the pre-merge chip. The Core parity is what covers those.
Numbers that vary run to run on this shared machine: `create()` 52 to 410 ms, first sound 52 to 330 ms after `noteOn`, CPU 20 to 43 ms per audio second.

## 6. What was and was not exercised

**Clicked and measured in the Browser pane** (the desktop app's own pane, 302 px wide, Edge/Chromium): the demo page over `http://127.0.0.1`: a real click on "Click to start the chip" (the AudioContext became `running` at 48 kHz, base latency 10 ms);
a real click on the Square preset (it plays a test note: 19 activity reports, peak 0.211, pulse 1 at note 72); the `z` key (a tap shorter than the lead's 4 ms attack is silent, as it should be for that preset). In headless Edge at 1100 and 390 px: the whole page, the tune player
(bar and step cursors move, the meters show notes on all channels), the MIDI button (the browser refuses the permission headlessly and the error is shown), screenshots read. **No sound card on this machine**: "heard" means measured: sample values in `OfflineAudioContext`s, and the browser's own `AnalyserNode` on the live context (A4 at 439 Hz).

**Not tested**: any audible output through a loudspeaker; Firefox and Safari; a real MIDI device or the permission prompt; a phone; an `https` host (localhost is a secure context); a CSP that forbids blobs (the `workletUrl` path is tested, the CSP is not); host rates under 11 kHz against Core (it throws there); rates above 96 kHz; Nano running next to a page's own heavy audio load.
**Known limits**: `file://` pages cannot load an AudioWorklet in Chromium (tested), so `create()` is refused there; the chip simulates 1.79 M cycles a second while connected, silent or not (about 2 to 3 percent of a core: `dispose()` an idle synth); no stereo; one chip only (FIX); DPCM has no volume and a note changes its rate only when it starts; the chip's pitch is quantised to whole period steps (about 8 cents at 500 Hz).

## 7. Open problems and next steps

- **Provenance**: the chip inherits the open NET-NES license question through `APU_FIX` / `APU_FMC` (ledger section 2). Resolve it before publishing Nano outside the repository.
- **Idle CPU**: an exact "sleep" (advance the chip analytically when nothing sounds) would let an idle synth cost nothing; not done, because the exactness proof would have to cover the fast-forward.
- **Minified build**: not shipped (the readable file is 23 KB gzip); a safe minifier step would need the whole test set to run against its output.
- **Firefox / Safari / a real MIDI keyboard / a loudspeaker**: should be tried once by a person.
- The Lite site could embed Nano for its own sound effects; out of scope.
- A `Nano` mode for other chips (the plugin's Game Boy, SNES, Sega chips) would need their own ports and their own proofs; the instrument layer and the parity tooling are written so that a chip can be swapped (the proof tooling already takes any `IChipDriver`).
