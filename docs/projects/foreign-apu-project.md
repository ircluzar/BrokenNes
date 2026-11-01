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
| `PICO` | Genesis APU + SMS/GG PSG | PicoDrive v1.85 `ym2612.c`, `sn76496.c` | PicoDrive non-commercial + MAME | **done (MVP)**: `sega/PicoYm2612.cs`, `sega/PicoSn76496.cs`, `md/MdSoundUnitPico.cs`, `sega/SmsSoundPico.cs` (050cbcca; 44.1 kHz frame blocks, no SSG-EG/CSM, FM ch 6 sounds under the DAC, unipolar PSG; DAC path untested) |

## Batch 3: the big batch (interview 2026-11-01, all nine done (MVP))

Picked from three research passes:
- old Sega cores
- **notoriously bad** open-source sound
- **the last version before each emulator's sound breakthrough** ("the best of the bad")

The user picked all nine:

| ID | Console slot | Source | License | Why | Status |
|---|---|---|---|---|---|
| `FCEU` | NES APU (+ MMC5) | FCE Ultra 0.96 `sound.c` + `filter.c` + `mbshare/mmc5.c` (sourceforge fceultra 0.96 tarball) | GPL-2+ | The last version before 0.98.1 fixed expansion audio | **done (MVP)**: `apus/FceuApu.cs`, `apus/APU_FCEU.cs`, `apus/IApuExpansionAudio.cs` + a `Bus` hook (a3847f3f; the shipped low-quality default at 44.1 kHz, MMC5 sound through the new hook; VRC7/5B/VRC6/N163/FDS not ported; r=0.83 vs FIX) |
| `INES` | NES APU | InfoNES `InfoNES_pAPU.cpp` (github.com/jay-kumogata/InfoNES, master, fetched 2026-11-01) | Apache-2.0 | 8-bit unsigned, the 5 channels averaged, square 2 sweeps the wrong way, tiny DMC | **done (MVP)**: `apus/InfoNesApu.cs`, `apus/APU_INES.cs` (a0b7ca52; envelopes and sweeps are dead code in the original, so flat volumes and no sweep; DMC ~10x slow; r=0.82 vs FIX) |
| `DARC` | NES APU + SMS/GG PSG | DarcNES `nes_psg.c` 1.50 + `sms_psg.c` 1.14 (iDarcNES @6f8eb3d) | credit required, non-commercial | NES: no DMC, no noise (its renderer is `#if 0`), no triangle linear counter. SMS: 5-bit noise, linear volume, per-frame updates | **done (MVP)**: `apus/DarcNesPsg.cs`, `apus/APU_DARC.cs`, `sega/DarcSmsPsg.cs`, `sega/SmsSoundDarc.cs` (e2adddae; NES r=0.88 vs FIX, unipolar 8-bit; SMS r=0.91, ~2.3x louder, mono) |
| `DEGA` | SMS/GG PSG | Dega 1.16 `mast/dpsg.cpp` + `samp.cpp` + `samples.h`, Enhanced PSG mode (github.com/Plombo/dega) | non-commercial | Square waves replaced by sampled instruments, plus an added echo | **done (MVP)**: `sega/DegaPsg.cs`, `sega/DegaSamples.cs`, `sega/SmsSoundDega.cs` (b86d07ac; 44.1 kHz, ~5x stock level near clipping, retrigger on volume jumps, GG stereo at 1/4 instead of mute; r=0.80 vs SMS) |
| `S951` | SNES APU | Snes9x 1.51 `soundux.cpp`, `apu.cpp` (snes9x-rr mirror) | Snes9x non-commercial | The mature old DSP, right before blargg's SNES_SPC took over in 1.52 | **done (MVP)**: `snes/S951Dsp.cs`, `snes/APU_S951.cs` (b5da264b; stereo 32 kHz 1:1 in 533-frame blocks, gaussian, private echo; r=0.97 vs SFC) |
| `S916` | SNES APU | Snes9x 1.16a `soundux.cpp`, `apu.cpp`, `globals.cpp` (Debian snes9x 1.16a-1 sources) | Snes9x non-commercial | The 1998 soundux: 22 kHz, no interpolation, an additive PITCH_MOD guess, half-speed envelopes, a BRR sample cache | **done (MVP)**: `snes/S916Dsp.cs`, `snes/APU_S916.cs` (e7b8f2db; 22,050 Hz held to 32 kHz, ~2.4% sharp, ~6 dB quieter; r=0.80 vs SFC; the PROBLEMS-file ILLUSION bug was already fixed in 1.16a itself) |
| `TGBD` | GB APU | TGB Dual `gb_core/apu.cpp` (github.com/libretro/tgbdual-libretro, master, fetched 2026-11-01) | GPL-2+ | Renders whole frames at once with ~299 Hz envelope updates and a crude filter; L/R swapped | **done (MVP)**: `gb/TgbdApu.cs`, `gb/APU_TGBD.cs` (e1bc5ab9; 44.1 kHz frame blocks, trigger never resets phase, loud wave channel; r=0.98 vs GB) |
| `ZS151` | SNES APU | ZSNES 1.51 `src/cpu/dspproc.asm` + `dsp.asm` (github.com/emillon/zsnes, tag upstream/1.510) | GPL-2 | ZSNES's final DSP (its own echo/ADSR); about 8,000 lines of x86 asm | **done (MVP)**: `snes/Zs151Dsp.cs`, `snes/APU_ZS151.cs` (33f7502b; 32 kHz stereo Gaussian at ZSNES defaults, ~1.7x louder; r=0.986 vs SFC) |
| `NSTC` | NES APU | NESticle 1997 prototype `Source/NESSOUND.CPP` (github.com/athros/NESticle, master, fetched 2026-11-01) | **none: leaked source** (user decision: art exception, flagged) | Silent noise and DMC, squares ~2.75 semitones sharp, triangle ~an octave sharp, 58.5% duty, flat envelopes | **done (MVP)**: `apus/NesticleSound.cs`, `apus/APU_NSTC.cs` (4f1b056c; 22,050 Hz held to 44.1 kHz, notes restart on every reg 2/3 write, 16-bit wrap mix; r=0.83 vs FIX) |

**Decisions in this interview**
- NES ports become ordinary NES `APU_*` cores, like the QN family, plus the source's expansion chips where it has them.
- A new NES APU id needs a family in `CoreCatalog.NesApuFamily`, and one outside the five plugin families keeps the plugin's chip indices stable (`Plugin/CLAUDE.md`). It also needs a root in `WebLite/LinkerConfig.xml`.
- NESticle is included even though it has no license.

**Excluded:** iNES 0.5 and MasterGear 1.0. Both are leaked closed source that their author promised not to release, and the archive they came from also bundles an intrusion tool.

**Still candidates:** TuxNES 0.75, VirtuaNES 0.97 (all the NES expansion chips), VBA-M 2007 (an almost-free variant of VBA), SNEmulDS, GameYob, PocketNES (ARM asm), Genesis Plus GX 1.3.0 `fm.c`.

## Batch 4: the remaining candidates (2026-11-01, in progress)

The user asked for the rest of the research shortlist, using the same process. The budget is limited, so ports run in two waves and every merged core is pushed straight away.

| ID | Console slot | Source | License | Why | Status |
|---|---|---|---|---|---|
| `TUXN` | NES APU | TuxNES 0.75 `src/sound.c` (github.com/tuxnes/tuxnes) | GPL-2+ | Its header admits "DMC pops periodically" and "Noise channel fades in signed 16 bit"; 8-bit default; a triangle hack "inspired by nosefart" | planned |
| `VNES` | NES APU (+ MMC5 through `IApuExpansionAudio`) | VirtuaNES 0.97 `NES/ApuEX/APU_INTERNAL.cpp` (+ `APU_MMC5.cpp`) (github.com/ChillMagic/VirtuaNES mirror) | not stated in the files | Norix's own 2002 APU, not band-limited; the full expansion set exists (VRC6/VRC7/N106/FDS/FME7 left as gaps, no mappers) | planned |
| `VBAM` | GB APU | VBA-M commit 5e3902b4 (2007-12-03) `src/gb/gbSound.cpp`, the VBA 1.8.0-beta core right before blargg's Gb_Apu | GPL-2+ | About 150 lines different from our VBA 1.7.2 port: read masks, length reload on retrigger, shadowed frequency registers | planned |
| `GPGX` | Genesis APU + SMS/GG PSG | Genesis Plus GX 1.3.0 (tree 6f65ac3) `source/sound/fm.c` (MAME fm.c 1.4 + Eke's fixes) + its `sn76496.c` | MAME license | The last GX before 1.3.1 dropped the old core: half-done SSG-EG, older envelopes and LFO | planned |
| `SNDS` | SNES APU | SNEmulDS `arm7/source/dsp.c` + `dspMixer.S` (DSP from PocketSPC by gladius) (github.com/cotodevel/snemulds) | GPL-2 | A Nintendo DS emulator's cheap DSP: echo forced off at init, a cheap ARM mixer | planned |
| `GYOB` | GB APU | GameYob (DS) `dssoundengine.cpp` + `arm7 sound.c` (github.com/Stewmath/GameYob) | MIT | Plays Game Boy channels on the DS's own sound hardware (emulated here); only its optional "hyperSound" sync handles effects like Pikachu accurately | planned |
| `PNES` | NES APU | PocketNES (GBA) `src/sound.s` (github.com/Dwedit/PocketNES) | none stated | Plays NES channels on the GBA's Game Boy sound hardware (driven here through BrokenNes2's own `APU_GB`); users report dropped notes | planned |

## Resuming this work locally (or in any new session)

- **Everything is on the branch `claude/blissful-archimedes-i7zlbe` of `ircluzar/BrokenNes`.** It is pushed after every merged core. To continue on your machine:

  ```bash
  git fetch origin claude/blissful-archimedes-i7zlbe
  git checkout -b foreign-apu origin/claude/blissful-archimedes-i7zlbe
  ```

  (Or `git switch claude/blissful-archimedes-i7zlbe` once fetched.) Note the repo also has a TAG named `main` (an old 2026-03-31 commit, see `docs/KNOWN_GAPS.md` LX-4): use `origin/main` or `refs/heads/main` explicitly when you mean the branch.
- **State of the work:**
  - This file: what is done and what is planned. The "Status" column says **done (MVP)** or planned.
  - `docs/KNOWN_GAPS.md`: every shortcut taken.
  - `THIRD_PARTY_NOTICES.md` §2 and the family ledgers (`snes/`, `gb/`, `sega/THIRD_PARTY_NOTICES.md`): what was copied from where.
- **How to port one core:** the coordinator's recipe, used by every agent.
  1. Read this file's "plugs in" sections and the newest similar core: `APU_ZS15` for SNES, `APU_GNUB` for GB, `MdSoundUnitGens` / `SmsSoundGens` for Sega, and for NES the QN family plus `CoreRegistry`.
  2. Fetch the source into a scratch dir and only read it; don't run it.
  3. Port it with its quirks kept. Header: source, version, license and the exception line.
  4. Register it: the factory or registry, the console menu in `systems/ConsoleSystems.cs`, and for NES ids also `NesApuFamily` and `WebLite/LinkerConfig.xml`.
  5. Smoke-test it (below).
  6. Add the ledger row, a row in this file's table, and `KNOWN_GAPS` rows.
  7. Commit.
- **Smoke tests:**
  - NES: `Workshop/cli.sh --mixlab nes --rom Plugin/BrokenNes2.Plugin/Resources/musictest.nes --apu <ID> --frames 900 --png-at 999999 --wav out.wav --out-dir dir`, compared with `--apu FIX`.
  - SNES: the same, with `--apu SNES --snes-apu <ID>`.
  - GB: `--apu DMG --gb-apu <ID>`.
  - Genesis: `Workshop/cli.sh --ym-render Workshop/Sega/foreign-apu/md-test.vgm out.wav --unit <ID>`.
  - SMS/GG: `--sms-render Workshop/Sega/foreign-apu/sms-test.vgm out.wav --unit <ID> [--console gg]`.
  - On Windows use `Workshop/bin/Release/net10.0-windows/BrokenNes.Workshop.exe` with the same arguments.
- **Linux/cloud only:** `Workshop/setup-linux.sh` sets everything up.

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
