# Nano Genesis

The Genesis / Mega Drive sound (YM2612 FM + SN76489 PSG) as a web synth and MIDI player: one JavaScript file, no dependencies, an AudioWorklet.
The fourth Nano, beside Nano NES (`Nano/`), Nano GB (`Nano/gb`) and Nano SMS (`Nano/sms`), with the same API.

```html
<script src="nano-genesis.js"></script>
<script>
  let synth;
  button.onclick = async () => {
    synth ??= await BrokenNesNanoGenesis.create();
    synth.play('coin');                                   // a sound effect
    synth.noteOn('C4', 1, { preset: 'brass', duration: 0.5 });
    synth.loadMidi(await (await fetch('song.mid')).arrayBuffer(), { sidecar, loop: true }).play();
  };
</script>
```

Demos: `demo/index.html` (keyboard, an FM patch box to edit a preset's registers, sound effects, MIDI drop zone, LFO), `demo/minimal.html`. Serve them over http.

## Which chip, and why

**BrokenNes's stock Genesis unit** (menu id `MD`): `Ym2612.cs` (the slot-pipeline model of the discrete Model 1 chip with the ladder; a C# port of ymfm),
`Sn76489.cs` in the Mega Drive variant, `BandLimitedMixer.cs` and the mixing of `md/MdSoundUnit.cs`. Picked by the interleaved A/B run of 2026-11-10
(`docs/projects/nano-per-console.md`): the stock unit was the lightest by the median of minima and the most often lightest round by round, and every
foreign Genesis unit runs the stock chips underneath. Not ported: the Z80 (the engine writes the registers itself), the timers, CSM, the busy flag.

## Proof (sample for sample)

* `node Nano/genesis/test/parity.mjs [--mutants]`: the kernel = Core's bit for bit; 10 note scripts (5 host rates, every preset kind, an SSG-EG + LFO
  patch, pan, kill, all-off) and 16 random register streams (every YM2612 register of both parts, all algorithms, feedback, detune, SSG-EG, LFO AMS/PMS,
  channel 3's special mode, the DAC, per-operator key masks, PSG periods 0/1) replayed through Core's chips (`NanoGenesisSound` in Core = MdSoundUnit's
  mixing) by `tools/NanoGenesisParity`: **26/26 bit-exact, 935 k samples, 16 k writes**; the C# engine twin 10/10; plugin-way MIDI 6/6; 21 mutants.
* `node Nano/genesis/test/engine.test.mjs`: FM pitch within 3 cents (block + F-number), PSG pitch, -6 dB at velocity 0.5, voice stealing, key edges
  (a retrigger keys off, lets the chip run one sample, keys on), pan, the LFO, every preset, the MIDI player, sidecars, lean build = full build (67 checks).
* `node Nano/genesis/test/browser-test.mjs`: the worklet in headless Edge = renderOffline bit for bit, workletUrl, mono, divide, loadMidi, sidecar,
  bad arguments, a real-time context (heard 439 Hz for A4): 10/10.

## Budget (measured 2026-11-11, the shared dev machine)

| | |
|---|---|
| CPU | 4 FM + 1 PSG tone + noise, stereo, 48 kHz: **about 70 ms of CPU per second of audio** in Node (median of 7); the worklet 82 ms per audio second in headless Chrome. The YM2612 runs its 24 operators at 53,267 Hz whether a channel sounds or not (that is what keeps it bit-exact), so this is the floor; Nano SMS is about 2.5, Nano NES 20-30 |
| download | `nano-genesis.js` 96.8 KB, 29.6 KB gzip; `nano-genesis.lean.js` 79.2 KB, 24.1 KB gzip |
| memory | four 4096-float rings (64 KB) per chip; nothing allocated while playing |

`divide: 2` (the chip at half the context's rate) halves the mixer cost only: the YM2612's own 53 kHz clock is fixed.

## API

The same as Nano GB / Nano SMS: `create(options)`, `synth.noteOn / noteOff / play / bend / allOff / panic / setParam / addPreset / loadMidi / connectMidi /
onactivity / dispose`, `renderOffline`, `renderMidi`, `toAudioBuffer`, `parseMidi`, `presets`. Differences:

* Ten channels: FM 1-6 (0-5), PSG tones 1-3 (6-8), PSG noise (9). `pan` pans an FM channel left, both or right; the PSG is mono.
* `setParam('lfo', 0..8)`: the chip's LFO (0 off), for presets with `ams` / `pms`. `setParam('pan', [10 values])`.
* Presets (`src/presets.js`, ORDER = MIDI program number, append-only, 32): the plugin's 15 Genesis FM patches (brass, buzz, square, pulse, organ,
  strings, clarinet, flute, epiano, pluck, bell, marimba, bass, growl, sine), fmkick, PSG ones (psglead, psgsquare, psgarp, kick, tom, snare, hat, openhat,
  crash, laser, jump, blip, explosion, hit) and FM coin / powerup. An FM preset is `{kind: 'fm', alg, fb, ams, pms, ops: [4 x {mul, tl, dt, ar, d1r, sl, d2r, rr, rs, am, ssg}]}`
  (algorithm order: operator 1 first); its `r` is how long the chip's release rings before the channel is free.
* Sidecar: `{format: 'nano-genesis-sidecar', version: 1, channels: {'1': {preset, volume, pan, name}}, presets: {...}}`.
* Silence is not exactly zero: the Model 1 ladder adds a small offset per channel (it decays through the 5 Hz high-pass), as on the console.

## The BrokenNes2 plugin chip: Nano Genesis (`NANOMD`)

Chip index **88** on the Genesis console (appended after the x86 PC chips 77-87; nothing before it moved). Every instance in Direct mode plays a
`NanoGenesisEngine` of its own (`Windows/NesEmulator/sega/NanoGenesisEngine.cs`, the line-for-line C# twin of `src/engine.js`); the Instrument parameter
is the program number (wraps); **Export sidecar** writes the sidecar (instances in creation order = MIDI channels 1, 2, ...). The plugin path is shared
by the later Nano chips (`Plugin/BrokenNes2.Plugin/Bn2Plugin.NanoMore.cs`, `NanoKinds`). `FruityHost selftest --only nanomore`: FL's notes = the engine
sample for sample on an FM, a PSG and a noise preset, two deliberate bugs caught, Volume, a bend, 0 bytes with 4 instances, the sidecar.

## FL projects (.flp)

`node Nano/genesis/tools/flp2nano/flp2nano.mjs song.flp [--out DIR]` writes `song.nanogenesis.mid`, `.nanogenesis.json` (sidecar) and
`.nanogenesis.fence.json`. Converted: Nano Genesis instances (polyphonic, preset, Volume, Pan), the Genesis chip `MD` in Direct mode (pulses = the FM
patch the Instrument picked, the same register sets as presets 0-14; triangle = sine; noise = PSG noise), the Master System chip and NES chips in Direct
mode (one PSG channel each, as Nano SMS's converter). Test: `node Nano/genesis/test/flp2nano.test.mjs` (21 checks); the user's `overworld.flp` gives the
same 51 / 55 / 113 notes as FL's export.
