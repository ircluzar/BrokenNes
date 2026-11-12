# Nano DOS

The DOS PC's sound (an AdLib on MAME 0.36's OPL2: nine FM voices, plus the PC speaker) as a web synth and MIDI player: one JavaScript file, no
dependencies, an AudioWorklet. Same API as the other Nanos.

```html
<script src="nano-dos.js"></script>
<script>
  let synth;
  button.onclick = async () => {
    synth ??= await BrokenNesNanoDOS.create();
    synth.play('coin');                                          // the speaker
    synth.noteOn('C4', 1, { preset: 'svpulse25', duration: 0.5 });   // the AdLib
    synth.loadMidi(await (await fetch('song.mid')).arrayBuffer(), { sidecar, loop: true }).play();
  };
</script>
```

Demos: `demo/index.html` (keyboard, a preset box to edit an FM patch's 11 SBI bytes, sound effects, MIDI drop zone), `demo/minimal.html`.

## Which chip, and why

**MAME 0.36's fmopl** (`Windows/NesEmulator/sega/Mame036Opl.cs`, the x86 console's AdLib engine `M036`), the user's pick on 2026-11-10: "the
lightest", its quirks accepted (log-table synthesis, an LFO cached on the first sample, vibrato that wraps on high notes, release rate 0 releasing at
once, the rhythm mode's odd phases). `docs/projects/nano-per-console.md` has the benchmark (M036 about half the CPU of the card's ymfm OPL2). Plus the
**PC speaker** as `x86/PcSpeaker.cs` draws it (the 8254's channel 2 in mode 3 gated by port 61h, four points averaged per 44.1 kHz sample, level x
5000 / 4), mixed as `PcSession.ReadSamples` mixes them (speaker + AdLib, saturated, at 44.1 kHz), resampled to the host rate by Core's `StreamResampler`.

## Proof (sample for sample)

* `node Nano/dos/test/parity.mjs [--mutants]`: the OPL2's tables (built with `Math.pow` / `sin` / `log10`) = Core's `Mame036Opl` tables entry for entry;
  10 note scripts (5 rates, every preset kind, a custom patch with all four waveforms, the speaker) and 16 random register streams (every operator
  register of every voice, waveforms, feedback, connections, key edges, the rhythm mode, AM / vibrato depths, the speaker's counts) replayed through
  Core by `tools/NanoDosParity` (`Core/NanoDosSound.cs`): **26/26 bit-exact, 1.25 M samples**; the C# engine twin 10/10; plugin-way MIDI 6/6.
  The speaker's lines in `NanoDosSound` are restated from `PcSpeaker.cs` / `Pit8254.cs` (no PcMachine), so its proof is the JS against that restatement.
* `node Nano/dos/test/engine.test.mjs`: pitch (FM within 4 cents, the speaker's 8254 counts), -6 dB at velocity 0.5, nine voices + the speaker, a
  retrigger's key off / on, the table fingerprint, the 3000 Hz floor (8 kHz renders: over 4:1), every preset, MIDI, sidecar with its own patch, lean = full (55 checks).
* `node Nano/dos/test/browser-test.mjs`: the worklet in headless Edge = renderOffline bit for bit, **the OPL2 tables in the browser = Node's**
  (`dist/tables.json`: a browser whose Math rounds differently would fail here), etc.: 11/11.

## Budget (measured 2026-11-11, the shared dev machine)

| | |
|---|---|
| CPU | 5 FM voices + the speaker, 48 kHz: **about 24 ms of CPU per second of audio** in Node (median of 7). The OPL2 runs all nine voices at 44.1 kHz plus the resampler |
| download | `nano-dos.js` 82 KB, 24.8 KB gzip; `nano-dos.lean.js` 68 KB, 20.3 KB gzip |
| memory | the OPL2's tables (about 110 KB, shared) + a 32 K-float resampler buffer (128 KB) per chip; nothing allocated while playing |
| host rate | 11,025 Hz or more (Core's resampler goes at most 4:1 down) |

## API and presets

The same as the other Nanos; channels 0-8 are the OPL2's voices, 9 the speaker; mono (no pan). Presets (`src/presets.js`, ORDER = program number,
append-only, 30): the BrokenNes2 plugin's PC patches first (Super VRUN DOS's seven: svpulse12, svpulse25, svpulse50, svtriangle, svmix, svnoise, svperc,
and Vaults DOS's tone `vaults`), then piano, epiano, organ, strings, brass, flute, bass, bell, lead, kick, snare, hat, crash (FM) and the speaker's beep,
beeplead, beeparp, laser, coin, jump, blip, plus FM powerup and explosion. An FM preset: `{kind: 'fm', regs: [11 SBI bytes: mod 20, car 20, mod 40,
car 40, mod 60, car 60, mod 80, car 80, mod E0, car E0, C0]}`; the velocity adds total level to the carrier (and to the modulator of an additive patch).
Sidecar: `{format: 'nano-dos-sidecar', version: 1, ...}`. The speaker is unipolar (0 to 5000 of 32768), as in the x86 console: a DC step when it starts.

## The BrokenNes2 plugin chip: Nano DOS (`NANODOS`)

Chip index **90** on the x86 PC console (after NANOSNES 89), Direct mode only like every PC chip (`Emulator.DirectOnlyChip`). Each instance plays a
`NanoDosEngine` (`Core/NanoDosEngine.cs`); Instrument = program number; **Export sidecar** as on the other Nanos. `selftest --only nanomore`.

## FL projects (.flp)

`node Nano/dos/tools/flp2nano/flp2nano.mjs song.flp [--out DIR]` -> `.nanodos.mid`, `.nanodos.json`, `.nanodos.fence.json`. Converted: Nano DOS
instances; the PC console's AdLib chips (their edited patch from the project's `OPC1` section, else bank patches 0-7 exactly, else the nearest preset,
said in the fence; another OPL2 engine than M036 is said too); PC_SPEAKER; NES chips in Direct mode with Super VRUN DOS's own mapping (pulses by duty,
triangle, noise). Test: `node Nano/dos/test/flp2nano.test.mjs` (21 checks); the user's `overworld.flp` gives 51 / 55 / 113 notes as FL's export.
