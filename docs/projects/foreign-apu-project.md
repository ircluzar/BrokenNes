# Foreign APU project

## Concept

When we ported the APU from QuickNES (C++) to C#, the QN family ended up sounding noticeably
different from our other cores, simply because it inherited another emulator's code. This project
does the same thing with **sound chips from other consoles**. We port the audio engines of old,
open-source **SNES and Game Boy** emulators to C#, preferring early versions whose sound was still
rough, and feed NES audio through them.

## Decisions (from the interview, 2026-11-01)

| Topic | Decision |
|---|---|
| Sound style | **Straight piping.** Each NES channel drives the matching voice on the foreign chip with the same pitch, volume and timing. Nothing extra is switched on: no SNES echo, no stereo panning, master volume neutral. The character comes only from the chip's own math and bugs. |
| Port fidelity | **Bit-exact and verified.** The C# should be readable and match the original program sample for sample. Build the original here and compare outputs. Keep every quirk. Add a "fixes" toggle only where the original had one. |
| DMC on chips without it | **Stream it through the chip.** Game Boy: play DMC through the wave channel or volume register. SNES: convert DMC output to BRR samples on the fly. |
| Resampling | **Run the chip at its native rate and resample crudely** (nearest or linear) to 44.1 kHz, like 2000s emulators did. |
| Naming | Short emulator tags: `APU_ZS15`, `APU_S9X1`, `APU_OSPC`, `APU_GNUB`, `APU_VBA`. Leave room for LOW/LQ variants later. |
| Progression | **Blind-bag only.** The new cores are available only from the post-campaign random unlock pool. |
| Licensing | Use these sources for research and art. Disclose every upstream project and license in the repo's license/credits docs, and put a header on each ported file. |

## Candidates

| ID | System | Source | Era | License | Notes |
|---|---|---|---|---|---|
| `APU_ZS15` | SNES | ZSNES 0.15 S-DSP via [zsnes-asm2c](https://gitlab.com/ReverendGumby/zsnes-asm2c), the engine used in [ZSnes9x / snes9x-cursed](https://gitlab.com/ReverendGumby/snes9x-cursed) | 1997 | GPLv3 (zsnes-asm2c); Snes9x license for the fork | First ZSNES beta with sound. `dsp.asm` is ~3.5k lines of x86, reconstructed by disassembling the 0.15 binary. Its `spcplay` tool is our reference output. |
| `APU_S9X1` | SNES | Snes9x 1.39–1.43 `soundux` DSP | ~2002–04 | Snes9x non-commercial | Snes9x before its accuracy rewrites: wrong envelopes and interpolation. |
| `APU_OSPC` | SNES | [libOpenSPC](https://github.com/bmartin427/libopenspc), with parts from SNEeSe | 2002 | LGPL + SNEeSe license | Brad Martin's best attempt at accuracy at the time. |
| `APU_GNUB` | GB | [gnuboy](https://github.com/rofl0r/gnuboy) `sound.c` | ~2001 | GPL-2.0 | 464 lines of crude C. The quickest win. |
| `APU_VBA` | GB | VisualBoyAdvance 1.7.2 GB sound ([vba-raspi mirror](https://github.com/weimingtom/vba-raspi)) | ~2004 | GPL-2.0 | Different shortcuts from gnuboy. |

Alternates: TGB Dual (GPL), early blargg Gb_Snd_Emu (LGPL), old MESS.

## Architecture

```
NES game ──$4000-$4017──▶ ForeignApuHost (IAPU)
                            ├─ NES bookkeeping: $4015 status, frame counter + IRQ, length counters,
                            │   DMC fetch/IRQ. Must stay NES-correct or games break; shared by every port.
                            ├─ Translator (one per chip): NES channel state → chip register writes
                            │     GB:   pulse1/2 → CH1/CH2 (same 4 duty cycles), triangle → CH3 wave RAM
                            │           (32 × 4-bit triangle), noise → CH4 (15/7-bit LFSR), DMC → streamed
                            │     SNES: pulse → looped BRR per duty, triangle → looped BRR, noise → DSP noise
                            │           generator (FLG), DMC → BRR streamed on the fly; volume via GAIN
                            ├─ Ported chip: the foreign DSP/APU running at its native rate (bit-exact port)
                            └─ Crude resampler → the 44.1 kHz ring buffer that IAPU.GetAudioSamples returns
```

- We port only the **sound-generating chip**. The SNES SPC700 CPU is left out because the NES game
  plays the role of the driver.
- Follow `docs/core-lifecycle.md`. The core must support hot-swapping through
  `GetState`/`SetState`, plus `ClearAudioBuffers` and `Reset`. Avoid reflection-heavy serialization.

## Verification

- **Chip port:** build the original (C or asm2c) in the container. Feed the same input to both the
  original and the C# port, for example `.spc` files for SNES or register-write logs for GB, and
  require identical PCM output.
- **In BrokenNes:** `Tools/BrokenNes.Headless`
  - `./suite.sh --apu ZS15`: the NES-side APU tests (`apu_test`, length, IRQ) must score at least
    as well as `APU_FMC`, which shows the bookkeeping layer is correct.
  - `./nesh compare <rom> --a apu=FMC --b apu=ZS15 --wav`: listening A/B, plus checks that pitch
    and timing line up.
  - `./nesh bench <rom> --kind apu`: the core must stay comfortably faster than real time.
- **On Windows (user):** listening tests in the real host.

## Milestones

1. **ZS15, playable.** Shared `ForeignApuHost`, ZSNES 0.15 DSP port verified bit-exact against
   `spcplay`, and the NES→S-DSP translator. Selectable as an APU in the Windows build. Comes with
   WAV samples from the story ROMs and test ROMs. Card/progression wiring is deferred.
2. `APU_GNUB` (gnuboy), which also proves the host layer can be reused.
3. `APU_VBA`, `APU_S9X1`, `APU_OSPC`.
4. Game integration: card entries in both copies of `default-db.json`, card art in
   `SvgFactory.cs`, the blind-bag pool, the lists in `continue.js` and `gameSave.js`, and the
   license/credits doc.
