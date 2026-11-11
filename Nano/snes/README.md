# Nano SNES

The SNES sound generator (the S-DSP: eight voices of BRR samples, ADSR / GAIN envelopes, the noise generator) as a web synth and MIDI player: one
JavaScript file, no dependencies, an AudioWorklet. Same API as Nano GB / SMS / Genesis.

```html
<script src="nano-snes.js"></script>
<script>
  let synth;
  button.onclick = async () => {
    synth ??= await BrokenNesNanoSNES.create();
    synth.play('coin');
    synth.noteOn('C4', 1, { preset: 'strings', duration: 0.8 });
    synth.addPreset('mine', { kind: 'brr', wave: myCycleOf32Samples, base: 1000 });   // your own sample (16-bit), encoded to BRR
    synth.loadMidi(await (await fetch('song.mid')).arrayBuffer(), { sidecar, loop: true }).play();
  };
</script>
```

Demos: `demo/index.html` (keyboard, a preset box to edit or paste a preset, sound effects, MIDI drop zone), `demo/minimal.html`.

## Which chip, and why

**BrokenNes's stock S-DSP** (`snes/DSP_SFC.cs`, the SFC unit's DSP; a C# port of blargg's snes_spc 0.9.0), **alone: no SPC700**. The engine writes
the DSP registers and the 64 KB sound RAM itself, as a sound driver would. Picked 2026-11-10 (`docs/projects/nano-per-console.md`): the stock SNES unit
was the lightest by the median of minima and the most often lightest round by round; the SPC700 is most of every SNES unit's cost, and an instrument
engine does not need it. Its 32 kHz output is resampled to the host rate by Core's `StreamResampler` (4-point Hermite), the path the BrokenNes2 plugin
plays its S-DSP samplers through. Not used: echo (off), pitch modulation.

## Proof (sample for sample)

* `node Nano/snes/test/parity.mjs [--mutants]`: 10 note scripts (5 rates, every preset kind, user `wave` presets, ADSR and GAIN, pan, kill) and 16
  random register streams (every voice register, KON / KOFF patterns, ADSR / GAIN modes, noise clock, pitch modulation, echo with feedback and the
  FIR, random BRR with every filter and range incl. the invalid ones, loops) replayed through Core's `DSP_SFC` + `StreamResampler`
  (`Core/NanoSnesSound.cs`) by `tools/NanoSnesParity`: **26/26 bit-exact, 1.43 M samples, 25 k DSP writes**; the C# engine twin 10/10; plugin-way MIDI 6/6.
* `node Nano/snes/test/engine.test.mjs`: pitch, the BRR encoder (filters chosen, flags, base64), -6 dB at velocity 0.5, 8 voices keyed together,
  stealing, pan, noise, every preset, MIDI, sidecar with its own sample, lean = full (56 checks).
* `node Nano/snes/test/browser-test.mjs`: the worklet in headless Edge = renderOffline bit for bit, etc.: 10/10.

## Budget (measured 2026-11-11, the shared dev machine)

| | |
|---|---|
| CPU | 6 voices, stereo, 48 kHz: **about 26 ms of CPU per second of audio** in Node (median of 7). The S-DSP runs all 8 voices at 32 kHz plus the resampler |
| download | `nano-snes.js` 88 KB, 31 KB gzip; `nano-snes.lean.js` 73 KB, 26 KB gzip (7 KB of it the built-in samples) |
| memory | 64 KB sound RAM + two 32 K-float resampler buffers (256 KB) per chip; nothing allocated while playing |

## Presets and samples

`src/presets.js` (ORDER = MIDI program number, append-only, 30): piano, strings, brass, organ, choir, sine, square, pulse25, pulse12, saw, triangle, lead,
bass, pluck, bell, marimba, arp, kick, tom, snare, hat, openhat, crash, laser, coin, jump, powerup, blip, explosion, hit. The samples (`src/samples.js`) are
made by `tools/make-samples.mjs` (band-limited single-cycle loops of 32 / 64 samples and two one-shot drums) and stored as literal BRR.

A preset: `{kind: 'brr', brr: base64 BRR blocks, loop: block index (-1 once), base: Hz at pitch $1000}` or `{kind: 'brr', wave: [16-bit samples], loop:
sample index (a multiple of 16)}` (encoded by the engine's integer BRR encoder: the four filters and ranges 0-12 tried per block through the DSP's own
arithmetic), or `{kind: 'noise', noise: {rate 0..31}}`; plus `adsr: [attack, decay, sustain level, sustain rate]` (the chip's) or `gainReg`, and the usual
software `a d s r`, `vib`, `slide`, `arp`, `pan`, `gain`. Every sample is put in the sound RAM when the engine starts (directory at $0200, samples from
$0600), so a note never uploads. Sidecar: `{format: 'nano-snes-sidecar', version: 1, channels: {...}, presets: {...}}` (presets may carry their samples).

## The BrokenNes2 plugin chip: Nano SNES (`NANOSNES`)

Chip index **89** on the SNES console (after NANOMD 88). Every instance in Direct mode plays a `NanoSnesEngine` (`Core/NanoSnesEngine.cs`, the twin of
`src/engine.js`; in Core because Core's resampler is part of the sound); the Instrument parameter is the program number; **Export sidecar** as on the
other Nanos (`Plugin/BrokenNes2.Plugin/Bn2Plugin.NanoMore.cs`). `FruityHost selftest --only nanomore` proves FL's notes = the engine sample for sample.

## FL projects (.flp)

`node Nano/snes/tools/flp2nano/flp2nano.mjs song.flp [--out DIR]` -> `.nanosnes.mid`, `.nanosnes.json`, `.nanosnes.fence.json`. Converted: Nano SNES
instances; **SNES-sample Runaway presets saved in the project** (a Presets chip's `PRS1` section: the `.bn2preset` -> its `INS1` instrument -> the BRR
bytes up to the end block, the loop when it points inside the sample, ADSR / GAIN, tuning (`base`), the weights) as sidecar presets, polyphonic; the SNES
and NES chips in Direct mode (pulses by duty, triangle, noise). A loop pointer outside the sample (a corruption, or one into the preset's context audio
RAM), noise, pitch modulation and echo flags are said in the fence. Test: `node Nano/snes/test/flp2nano.test.mjs` (21 checks, a PRS1 preset built by the test).
