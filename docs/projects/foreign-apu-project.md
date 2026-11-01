# Foreign APU project

Alternate **SNES and Game Boy sound chips** ported from old, janky, open-source emulators. When we
ported the QuickNES APU from C++, the QN family ended up with a sound of its own; this project does
the same for the other consoles. BrokenNes2 is intercompatible, so a new SNES or GB chip also works
on the NES and on other consoles through the existing bridges, for free:
- `APU_SNES` with `MixConfig.SnesApu` / `SnesCores`
- `APU_DMG` with `GbCores`
- `NesApuOnSnes`, `SnesApuOnChip`

## Decisions (user interviews, 2026-11-01)

| Topic | Decision |
|---|---|
| Speed | **Quick and dirty first, polish later.** Port fast, check that it works, push. No long suites. |
| Verification | **Smoke test only.** It builds; one game or test ROM renders a WAV that is clearly music (non-silent, follows the song); the stock chip still works. |
| Fidelity | Port the original's code and **keep its bugs**; they are the sound. No sound "improvements". |
| Sound path | The flavor comes from the chip itself. Games drive it exactly as they drive the stock chip. Add no effects of our own. |
| Resampling | Run each engine at its own rate and convert crudely (nearest/linear) to the unit's rate. |
| Pacing | ZS15 first (it sets the pattern), then the other four in parallel. |
| Access | Selectable in the console core menus now. Cards and the blind-bag pool come in the polish pass. |
| Licensing | **Allowed for the sake of art, documented, revisited later.** The repo policy forbids GPL, LGPL and non-commercial code; these cores are a recorded exception in `THIRD_PARTY_NOTICES.md` §2 and in the family ledgers. Every ported file gets a header naming its source, authors and license. |

## Cores

| ID | Console slot | Source | License | Status |
|---|---|---|---|---|
| `ZS15` | SNES APU | ZSNES 0.15 (1997) sound engine, via [zsnes-asm2c](https://gitlab.com/ReverendGumby/zsnes-asm2c) `dsp.asm` + `mach.c`. This is the engine in ZSnes9x ([snes9x-cursed](https://gitlab.com/ReverendGumby/snes9x-cursed)) | GPL-2+/GPL-3+ | **done (MVP)**: `snes/Zs15Dsp.cs`, `snes/APU_ZS15.cs` |
| `S9X1` | SNES APU | Snes9x 1.39–1.43 sound code (`soundux.cpp`, `apu.cpp` DSP side) | Snes9x non-commercial | **done (MVP)**: `snes/S9x1Dsp.cs`, `snes/APU_S9X1.cs` (libretro/snes9x2005 @a79dfe9; stereo 32,040 Hz in 534-frame blocks; noise is snes9x2005's replacement LFSR, not 1.43's) |
| `OSPC` | SNES APU | [libOpenSPC](https://github.com/bmartin427/libopenspc), with parts from SNEeSe (2002) | LGPL + SNEeSe license | **done (MVP)**: `snes/OspcDsp.cs`, `snes/APU_OSPC.cs` (32 kHz stereo, 1:1; echo uses a private buffer) |
| `GNUB` | GB APU | [gnuboy](https://github.com/rofl0r/gnuboy) `sound.c` | GPL-2.0 | **done (MVP)**: `gb/GnubSound.cs`, `gb/APU_GNUB.cs` (@4c2f6c4; 8-bit, 44,620 Hz) |
| `VBA` | GB APU | VisualBoyAdvance 1.7.2 `gb/gbSound.cpp` ([vba-raspi mirror](https://github.com/weimingtom/vba-raspi)) | GPL-2.0 | **done (MVP)**: `gb/VbaGbSound.cs`, `gb/APU_VBA.cs` (@9fbd04f8; 22,050 Hz) |
| `BLST` | Genesis APU + SMS/GG PSG | BlastEm 0.3.1 `ym2612.c`, `psg.c` | GPL-3.0+ | **done (MVP)**: `sega/BlastemYm2612.cs`, `sega/BlastemPsg.cs`, `md/MdSoundUnitBlst.cs`, `sega/SmsSoundBlst.cs` (e3b41ed3; Genesis r=0.67 vs MD, chords fade; SMS PSG positive-only, ~9 dB quieter) |
| `M036FM` | Japanese SMS FM (YM2413) | MAME 0.36 `ym2413.c` (to-OPL2 translator) + `fmopl.c` | MAME non-commercial | **done (MVP)**: `sega/Mame036Opl.cs`, `sega/Mame036Ym2413.cs`, `sega/SmsFmSoundM036.cs` (aa7bad36; an octave low, volumes ignored, guessed rhythm; ~3x louder than stock FM) |
| `M036` | Genesis APU + SMS/GG PSG | MAME 0.36 `fm.c` v0.36e (YM2612) + `sn76496.c` | MAME non-commercial | **done (MVP)**: `sega/Mame036Opn2.cs`, `sega/Mame036Sn76496.cs`, `md/MdSoundUnitM036.cs`, `sega/SmsSoundM036.cs` (2c32d62d; 22,050 Hz; x^8 attack, LFO mostly off, old noise taps, unipolar PSG; ~1.6-1.9x louder than stock) |
| `GENS` | Genesis APU + SMS/GG PSG | Gens 2.10 `ym2612.c`, `psg.c` | GPL-2.0 | **done (MVP)**: `sega/GensYm2612.cs`, `sega/GensPsg.cs`, `md/MdSoundUnitGens.cs`, `sega/SmsSoundGens.cs` (3ef3944a; 22,050 Hz, Gens defaults (Improv off), scanline-driven DAC/timers, unipolar PSG) |

## How a SNES sound engine plugs in (the ZS15 pattern)

- `APU_SFC` has three hooks:
  - `OnDspSample(l, r)`: each 32 kHz S-DSP sample.
  - `OnDspRegWrite(reg, value)`: every SPC700 write to DSP registers $00–$7F.
  - `OnReset()`: called after `Reset` and after a state load.
- A new unit `APU_XXX : APU_SFC` keeps the stock SPC700, timers and S-DSP running. The game's sound driver therefore reads correct DSP registers (ENDX, ENVX), and the WideMess, the bridges, the tools and the save states keep working.
- The new unit mirrors every register write into its ported engine. In `OnDspSample` it pushes the engine's output instead of the S-DSP's, through `PushSample`, resampled crudely to 32 kHz.
- `OnReset` re-feeds the engine the S-DSP's registers, the way `spcplay` loads a song: all registers, then key-on last. The engine's own internal state is not saved; that is MVP scope.
- **Registration** takes one line in each of these places:
  - `systems/ConsoleSessions.cs`: the `SnesSession` constructor `else if`, `IsUnitKind` and `NewUnit`.
  - `systems/ConsoleSystems.cs`: the SNES APU menu list.
  - `mix/SnesCoreContracts.cs`: `RegisterApu`.
- **Smoke test** on Linux:

  ```bash
  Workshop/cli.sh --mixlab nes --rom Plugin/BrokenNes2.Plugin/Resources/musictest.nes --apu SNES --snes-apu XXX \
    --frames 900 --png-at 999999 --wav out.wav --out-dir /tmp/x
  ```

  This plays VRUN's music through the SNES unit. Then compare it against `--snes-apu SFC`: the loudness envelopes should correlate and the output should not be noise.
  - ZS15 result: correlation 0.92, louder than stock (no master volume) and mono.

## How a GB sound engine plugs in

- Implement `IGbApu` (`gb/IGbApu.cs`):
  - `Tick(tCycles)` at 4.194304 MHz and `FrameSequencerStep()` at 512 Hz.
  - `ReadRegister` / `WriteRegister` for $10–$3F (NR10–NR52 and wave RAM).
  - `ReadSamples` (interleaved stereo int16) at `SampleRate`.
  - `SaveState` / `LoadState`.
- **Register** in `mix/GbSoundBridges.cs` `GbCores`, in `GbSession.SoundFactory` (`systems/ConsoleSessions.cs`) and in the GB APU menu (`systems/ConsoleSystems.cs`).
- **Swap caveat:** `TrySwapCore(Apu)` copies the old unit's state into the new one. A variant that can't read `APU_GB`'s format must start fresh or have that swap path handled.
- **Smoke test:** `--mixlab nes ... --apu DMG` with `--gb-apu XXX` plays NES music through the GB chip. `--gbrun --rom <gb rom> --wav` also works if a GB ROM with music is at hand.

## MVP status (2026-11-01)

All five cores are merged. In the smoke test, VRUN's `musictest.nes` plays through `APU_SNES` (SNES
cores) or `APU_DMG` (GB cores). Results are compared with the stock chip over 600 frames.

| Core | Rate | RMS | Loudness envelope r vs stock |
|---|---|---|---|
| ZS15 | 32 kHz (44.1 kHz engine, mono) | 12,896 | 0.92 vs SFC |
| S9X1 | 32 kHz (32,040 Hz engine, stereo) | 4,269 | 0.85 vs SFC |
| OSPC | 32 kHz (native, stereo) | 4,525 | 1.00 vs SFC |
| GNUB | 44,620 Hz | 4,834 | 0.98 vs GB |
| VBA | 22,050 Hz | 6,204 | 0.96 vs GB |

Nobody has listened to them yet. The user checks them by ear on Windows.

**Known MVP gaps**
- SNES cores keep the engine's own state out of save states. A load or a swap re-feeds the registers and re-keys the voices.
- GNUB and VBA are not `APU_GB`, so these skip them (with `is` checks, no crash):
  - BrokenDev channel mute
  - the WAVE memory domain
  - the plugin's GbSnapshot, sampler and instrument push
  - FruityHost's inject checks
- Swapping a running game to GNUB starts the engine fresh. VBA writes an `APU_GB`-format block first, so swaps load, but notes stay silent until they are re-triggered.

## Later (the polish pass)

- Save the engines' own state, so a state load does not re-key the voices.
- Cards, card art and the blind-bag pool. BrokenNes2's cards are NES-only today; the SNES and GB cores appear in the console menus through `CoreCatalog`.
- Resolve the licensing question. See `THIRD_PARTY_NOTICES.md` §2.
- Optional: bit-exact checks against the original programs.
