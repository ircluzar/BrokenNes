# Foreign APU project

Alternate **SNES and Game Boy sound chips** ported from old, janky, open-source emulators. When we
ported the QuickNES APU from C++, the QN family ended up with a sound of its own; this project does
the same for the other consoles. BrokenNes2 is intercompatible, so a new SNES or GB chip also works
on the NES and on other consoles through the existing bridges, for free:
- `APU_SNES` with `MixConfig.SnesApu` / `SnesCores`
- `APU_DMG` with `GbCores`
- `NesApuOnSnes`, `SnesApuOnChip`

## Current status (end of the cloud session, 2026-11-01)

**27 ported sound engines (ids) in 33 console slots, all done (MVP)**: smoke-tested, robustness-checked (`--apu-fuzz`), not yet heard by a person.

| Console | Cores (menu ids) |
|---|---|
| SNES (sound unit) | ZS15, ZS151, S916, S9X1, S951, OSPC, SNDS |
| Game Boy / GBC (sound chip) | GNUB, VBA, VBAM, TGBD, GYOB |
| Genesis (sound unit) | GENS, PICO, M036, BLST, GPGX |
| Master System / Game Gear (PSG) | GENS, PICO, M036, BLST, GPGX, DARC, DEGA |
| Japanese Master System (FM unit; not on Game Gear) | M036FM, E055 |
| NES (`APU_*` cores, family "Foreign") | FCEU, INES, NSTC, DARC, TUXN, VNES, PNES |

**All-APU matrix (2026-11-05):** every port of the other families is also a NES APU id with its engine fixed (`mix/PortedEngineBridges.cs`): `SNES_<port>`, `DMG_<port>`, `SMS_<port>`, `MD_<port>` and, for the two YM2413 engines, `SMSFM_M036FM` / `SMSFM_E055` (the NES's `APU_SMSFM` bridge with the ported FM unit: the bridge's FM and PSG writes go through the unit's ports, its stock chips mirror them, the unit's output plays). The NES menu lists them directly; NES, SNES, GB/GBC, Genesis and WideMess reach them as `NES:<id>` under the family's "Ported from other emulators" heading. The Game Gear has no FM chip: no FM entry in any form, and a swap to one is refused with that reason. `Workshop --apu-sweep [--console NES|SNES|GB|SMS|GG|MD|WMS]` covers every cell; `--plant menu|silent|stuck|crash|stockpick` are the negative controls (`stockpick` swaps to the stock bridge, which the samples-identical check must catch).

Each core's source, version and quirks are in the batch tables below. Every shortcut is in `docs/KNOWN_GAPS.md`. The licenses are in `THIRD_PARTY_NOTICES.md` §2 and the family ledgers.

**Review pass at the end of the session:**
- `Workshop --apu-fuzz` checks every core (and the stock ones) against random register writes and corrupted sound RAM, round-trips save states between ports and stock in every direction, and reports allocations per emulated second. All 39 pass.
- It found and fixed:
  - a BLST crash (BlastEm reads past a 3-element array)
  - Game Boy core swaps that went silent: all 30 chip-to-chip swaps now keep a held note (`--swap`)
  - GENS and BLST losing the game's instruments on a state load (`--md-reload`)
  - M036FM and E055 losing instruments and held notes (`--fm-reload`)
- Also fixed: the experimental Web build. It had been broken on `main` since 2026-10-09, and PNES broke it again. The whole solution, Web, WebLite and the MCP server all build.
- `UAT/entrypoints/notices-check.ps1` passes. The license list in `THIRD_PARTY_NOTICES.md` §2a is grouped by license class.

**Next:**
1. Listen on Windows.
2. Decide the licensing question (FA-1, P1).
3. The remaining P2 gaps:
   - FA-3: SNES engine state is not in save states; the ports replay the DSP registers and re-key the voices.
   - FA-5: cards and the blind bag.
   - FA-WEB-1 is now fixed.

## Follow-up test batch (2026-11-05, primer `foreign-apu-next-primer.md`)

| Phase | Tool | Result | Commits |
|---|---|---|---|
| 1. Every channel sounds | `Workshop --apu-channels [--console X]` | 274 cells (core x channel vs the stock core, RMS + pitch), 0 port bugs, 35 intended deviations (the originals' sound, KNOWN_GAPS FA-CH-1..3); WAVs in `%LOCALAPPDATA%\VRUN_Nes_Dev_work\fapu_build\wav` | `4ff599ca` |
| 2. Save states | `--apu-channels --state` | every port keeps sounding at its level after a load (NES ports were silent: FA-NES-ST; Genesis / SMS lost notes: FA-MD-ST). Re-keyed, not sample-exact, except the Game Boy ports (FA-ST-1, open) | `b0b8195e`, `70e51e57` |
| 3. Cross-console sweep | `--apu-sweep [--console X] [--only ID] [--plant silent\|stuck\|crash] [--tsv f]` | 162 cells (21 of them stock controls): each port native on its menu, swapped live mid-song, and behind every bridge that reaches it (NES game on APU_SNES / APU_DMG, SNES / GB / SMS / GG / Genesis game on `NES:<port>` swapped in live and started on it, SMS game on `NES:DMG` / `NES:SNES`, GB game on `NES:SNES`), each judged against the stock control of its route: live swap, save + load, Butchering mode (4 sound effects, then off + reload), key-off (no stuck notes), swap back to stock (nothing carried back). All pass after the fixes; the plants are caught | this batch |

Phase 3 found and fixed (KNOWN_GAPS): **FA-SW-5** a NES core swap after a state load replayed the pre-load registers (a later swap could silence the game for good), and the APU_SNES / APU_DMG bridges did not refeed their chip on a load; **FA-SW-2** stuck notes on the `NES:<chip>` bridges with DARC / INES (the bridges now key off in `$4015`); **FA-SW-3** the SNES-to-NES bridges ignored the S-DSP mute. Intended (the originals): FA-SW-1 (NSTC caps a held note at 0.5 s), FA-SW-4 (ZS15 / ZS151 / S9X1 / SNDS ignore FLG mute). Open: FA-BR-1 (the NES menu's SNES / DMG bridges cannot pick a port: a decision), FA-SW-6 (five NES ports' sample counts / NSTC pitch behind the Sega hub, pre-existing in `--swap-matrix-test`), FA-ST-1 (exact per-engine state).

Every session is headless; run one console per process (`--console`), six at a time take about a minute.

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

## Batches 1-2: the first SNES / GB ports and the Sega set

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

## Batch 4: the remaining candidates (2026-11-01, all seven done (MVP))

The user asked for the rest of the research shortlist, using the same process. The budget is limited, so ports run in two waves and every merged core is pushed straight away.

| ID | Console slot | Source | License | Why | Status |
|---|---|---|---|---|---|
| `TUXN` | NES APU | TuxNES 0.75 `src/sound.c` (github.com/tuxnes/tuxnes @f537b4b, "tuxnes 0.75") | GPL-2+ | Its header admits "DMC pops periodically" and "Noise channel fades in signed 16 bit"; 8-bit default; a triangle hack "inspired by nosefart" | **done (MVP)**: `apus/TuxnesSound.cs`, `apus/APU_TUXN.cs` (bc59db7f; u8 44.1 kHz per-frame render, writes squeezed into the frame's first quarter, 60 Hz length counters (notes twice as long), DMC pops; RMS 0.101 vs 0.089, envelope r=0.51 on a flat-loudness song) |
| `VNES` | NES APU (+ MMC5 through `IApuExpansionAudio`) | VirtuaNES 0.97 `APU_INTERNAL.cpp`, `APU.cpp` mixer, `APU_MMC5.cpp` (github.com/ChillMagic/VirtuaNES, a modified mirror, master) | not stated in the files | Norix's own 2002 APU, not band-limited | **done (MVP)**: `apus/VirtuaNesApu.cs`, `apus/VirtuaNesMmc5.cs`, `apus/APU_VNES.cs` (14f1f9e2; 22,050 Hz 8-bit, box-averaged squares, bright bipolar noise, DMC attenuates triangle/noise; envelope r=0.52 on a flat song, 0.74 with noise muted; MMC5 untested) |
| `VBAM` | GB APU | VBA-M commit 5e3902b4 (2007-12-03) `src/gb/gbSound.cpp`, the VBA 1.8.0-beta core right before blargg's Gb_Apu | GPL-2+ | About 150 lines different from VBA 1.7.2: read masks, length reload on retrigger, shadowed frequency registers | **done (MVP)**: `gb/VbamGbSound.cs`, `gb/APU_VBAM.cs` (7605d654; r=0.977 vs GB, 0.994 vs VBA; 99% of samples identical to VBA on musictest; the idle DC is gone, fade-ins from volume 0 play ~4x loud) |
| `GPGX` | Genesis APU + SMS/GG PSG | Genesis Plus GX 1.3.0 (tree 6f65ac3) `source/sound/fm.c` + `sn76489.c` (1.3.0 has no sn76496.c; fm.c is the default `fm_core=0`) | MAME license + Genesis Plus terms | The last GX before 1.3.1 dropped the old core: half-done SSG-EG, older envelopes and LFO | **done (MVP)**: `sega/GpgxYm2612.cs`, `sega/GpgxSn76489.cs`, `md/MdSoundUnitGpgx.cs`, `sega/SmsSoundGpgx.cs` (9f69d5ca; GX defaults: hq_fm (chip rate 1:1), output filter on, PSG x1.5; MD r=0.69 vs MD / 0.955 vs BLST (chords decay like real D1R); SMS r=0.994, ~4 dB quieter, mono) |
| `SNDS` | SNES APU | SNEmulDS `arm7/source/dsp.c` + `dspMixer.S` (DSP from PocketSPC by gladius) (github.com/cotodevel/snemulds @79762ad) | GPL-2 | A Nintendo DS emulator's cheap DSP: no interpolation, broken echo, a cheap ARM mixer | **done (MVP)**: `snes/SnemulDsDsp.cs`, `snes/APU_SNDS.cs` (7ef6933e; ARM mixer translated line by line incl. its self-patching echo, 63 frames mixed / 62 played, 2.4% sharp, noise and pitch-mod silent, ~2.5x louder; r=0.64 vs SFC) |
| `GYOB` | GB APU | GameYob (DS) `dssoundengine.cpp` + `arm7 sound.c` + `noise.h` (github.com/Stewmath/GameYob @45599fb) | MIT | Plays Game Boy channels on the DS's own sound hardware (emulated here); only its optional "hyperSound" sync handles effects like Pikachu accurately | **done (MVP)**: `gb/GameYobSound.cs`, `gb/APU_GYOB.cs` (0875ba89; default mode: commands once per GB frame, emulated DS mixer at 32,768 Hz, DS duty table, the DC hum; r=0.947 vs GB) |
| `PNES` | NES APU | PocketNES (GBA) `src/sound.s` (github.com/Dwedit/PocketNES @9c07f3d) | none stated | Plays NES channels on the GBA's Game Boy sound hardware (driven here through BrokenNes2's own `APU_GB`); users report dropped notes | **done (MVP)**: `apus/PocketNesSound.cs`, `apus/APU_PNES.cs` (4e1950c5; software envelopes/length/sweep written as fixed GB volumes, a retrigger on every volume or period change, pitches re-quantised to GB periods, on/off triangle, DirectSound-style DMC; ~2.5x louder; r=0.53 vs FIX) |

## Batch 5: one last port (2026-11-01) (done (MVP))

| ID | Console slot | Source | License | Why | Status |
|---|---|---|---|---|---|
| `E055` | Japanese SMS FM (YM2413) | emu2413 v0.55 (Mitsutaka Okazaki, 2002) `emu2413.c` + `emu2413.h` + `2413tone.h` (github.com/digital-sound-antiques/emu2413-legacy, tag `0.55` @2d92835) | none stated in the legacy repo | The author's own README: "Its quality is extremely low". Early drum and hi-hat models, a crude low-pass filter. The second alternate for the FM unit after M036FM | **done (MVP)**: `sega/Emu2413v055.cs`, `sega/SmsFmSoundE055.cs` (a41e3ce6; sample2413.c default OPLL_new(3579545, 44100), quality 0, held up to the chip rate; log-curve attack, 2-tap averaging "LPF", early drums doubled, its own tone ROM; FM-only RMS 638 vs stock 650, envelope r=0.85 FM-only / 0.976 full mix) |

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
- **Swap / state checks:** `Workshop/cli.sh --apu-fuzz --swap` (Game Boy chip-to-chip swaps keep a held note) `--apu-fuzz --md-reload` (Genesis units keep the instruments across a state load) and `--apu-fuzz --fm-reload` (the Japanese FM units keep instruments and held notes).
- **Robustness check:** `Workshop/cli.sh --apu-fuzz [--seconds 3] [--seed N] [--only ID,...]` drives every foreign core (and the stock ones as controls) with random register writes and random sound RAM, as the corruption tools would. Exit code 0 = no core threw. Add new core ids to the lists in `Workshop/Sega/ForeignApuFuzzCli.cs`.
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

## How a Sega sound engine plugs in (the GENS pattern)

- **Genesis:** subclass `md/MdSoundUnit.cs` and override its hooks:
  - `OnYmWrite(port, value)` and `OnPsgWrite(value)` receive the writes.
  - `OnFmSample(ref left, ref right)` is called per stock YM2612 sample (about 53 kHz); replace the values to replace the FM sound.
  - `OnPsgSamples(Span<short>)` holds the host-rate PSG audio; overwrite it in place.
  - `OnReset()` runs after Reset and after LoadState.

  Register the unit in `sega/SegaSoundFactories.cs` (`GenesisSoundFactories`) and the Genesis menu in `systems/ConsoleSystems.cs`. Examples: `md/MdSoundUnitGens.cs`, `MdSoundUnitGpgx.cs`.
- **Master System / Game Gear:** subclass `SmsSound` (`sega/ISmsSound.cs`) with `OnPsgWrite`, `OnStereoWrite`, `OnPsgSamples` and `OnReset`. Register it in `SmsSoundFactories` and the Master System and Game Gear menus. Example: `sega/SmsSoundGens.cs`.
- **Japanese FM:** subclass `SmsFmSound` with `OnFmAddress`, `OnFmData` and `int OnFmSample(int)` (mono, at cpuClock/72). Offer it on the Master System only. Examples: `sega/SmsFmSoundM036.cs`, `SmsFmSoundE055.cs`.
- **Smoke tests:** the VGMs from `Workshop/Sega/foreign-apu/make_test_vgm.py`, played with `--ym-render --unit ID` and `--sms-render --unit ID`.

## How a NES sound engine plugs in (the APU_DARC pattern)

- Subclass `apus/APU_FIX.cs` as `APU_<ID>` (re-implement `IAPU` like `APU_FIXS`). The base keeps $4015 reads, IRQs and DMC DMA correct.
  - Forward every register write to the engine.
  - Clock the engine from `Step`.
  - Return the engine's audio from the sample methods, and drop the base's ring.
- `CoreRegistry` finds the class by its name.
- **Required:** add the id to `NesApuFamily` in `systems/ConsoleSystems.cs` as `"Foreign"` (outside the five plugin families, so the plugin's chip indices stay put), and add a root in `WebLite/LinkerConfig.xml`.
- **MMC5 audio:** comes through `apus/IApuExpansionAudio.cs` (see `APU_FCEU`, `APU_VNES`).
- **Smoke test:** `--mixlab nes --rom Plugin/BrokenNes2.Plugin/Resources/musictest.nes --apu ID`.

## MVP status of the first five cores (batch 1, kept for history)

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
- Swapping a running game to GNUB started the engine fresh, and notes stayed silent after a swap until re-triggered: fixed later (KNOWN_GAPS FA-4).

## Later (the polish pass)

- Save the engines' own state, so a state load does not re-key the voices.
- Cards, card art and the blind-bag pool. BrokenNes2's cards are NES-only today; the SNES and GB cores appear in the console menus through `CoreCatalog`.
- Resolve the licensing question. See `THIRD_PARTY_NOTICES.md` §2.
- Optional: bit-exact checks against the original programs.
