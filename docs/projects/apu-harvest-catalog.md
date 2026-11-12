# APU harvest catalog (research draft 3, 2026-11-07)

Prospects for more "character" sound cores. A core gets its character in one of three ways:
- **(a) Pre-milestone builds:** old desktop emulators taken just before a big sound-accuracy improvement.
- **(b) Console ports:** emulators that run on other consoles or handhelds and cut sound to fit weak hardware.
- **(c) Embedded ports:** ESP32, RP2040 or STM32 builds, with integer mixing, low sample rates and per-frame updates.

All research was web-only. Nothing was downloaded or cloned; files were viewed in a browser.

**How sure each entry is:**
- **[verified]**: the milestone or target and the file at that tag were seen in a browser, and the link is given.
- **[partial]**: some of it is sourced, and the gap is named.
- **[unverified]**: a lead only.

**Licence context.** The repo uses the Digital Lifeform License 1.1, and `THIRD_PARTY_NOTICES.md` allows only permissive code. GPL, LGPL, non-commercial and unlicensed code may only come in through the documented Foreign-APU art exception (FA-1, `docs/KNOWN_GAPS.md`). Flags:
- **OK**: permissive or public domain.
- **EXC**: needs FA-1.
- **??**: licence unknown, or not stated in the file.

**Already harvested, so not re-listed** (`docs/projects/foreign-apu-project.md`):
- NES: FCEU 0.96, InfoNES, NESticle, DarcNES, TUXN, VirtuaNES 0.97, PocketNES.
- SNES: ZSNES 0.15 and 1.51, Snes9x 1.16a, 1.43 and 1.51, libOpenSPC, SNDS.
- GB: gnuboy, VBA 1.7.2, VBA-M, TGB Dual, **GYOB (GameYob DS, @45599fb, MIT)**. GameYob re-surfaced in this pass and was dropped as a duplicate. Its port includes an **emulated DS sound unit** (`gb/GameYobSound.cs`), which other DS ports can reuse.
- Genesis and SMS: Gens 2.10, PicoDrive 1.85, MAME 0.36, BlastEm 0.3.1, GPGX 1.3.0, Dega 1.16, emu2413 0.55.
- x86: ymfm YM3812.

## Ranked top 10 (interest x ease)

| # | Prospect | Chip | Exact target | Licence | Status |
|---|---|---|---|---|---|
| 1 | **nesDS ARM7 sound** (Mamiya's NSF-player APU; only the triangle is software-mixed, so the other channels presumably go to DS hardware channels) | NES APU | [DS-Homebrew/NesDS](https://github.com/DS-Homebrew/NesDS) tag **v2.0.1** `652c8aa` (2025-10-17, before v2.1 added APU frame IRQ), file `arm7/source/s_apu.c` (about 850 lines, C) + `audiosys.c` | **Public domain** (README) + Mamiya library "PDS", **OK** | [verified] |
| 2 | **minigb_apu, before the April 2022 rework** | GB APU | Peanut-GB `024175d` (2022-03-31), `examples/sdl2/minigb_apu/minigb_apu.c` (about 500 lines) | MIT (repo LICENSE), **OK** | [verified] |
| 3 | **DOSBox 0.72 old OPL** (MAME fmopl v0.70 + ymf262) | OPL2/OPL3 | SVN `RELEASE_0_72/src/hardware/fmopl.c` r2883 (2007-06-12, 2518 lines) + `ymf262.c` | none in the file; MAME-derived, **??/EXC** | [verified] |
| 4 | **Nuked-OPL3 1.7.4** (from AdPlug) | OPL3 | adplug `src/nukedopl.c` at tag **adplug-2.3.1** (header "version: 1.7.4"); the last commit before 1.8 is `52d037e` (2016-10-02) | LGPL-2.1+ at that tag (the first import, `14e57b8`, came before the LGPL relicense commit), **EXC** | [verified] |
| 5 | **DOSBox 0.74 compat OPL** (based on Ken Silverman's ADLIBEMU) | OPL2/OPL3 | `RELEASE_0_74/src/hardware/opl.cpp` r3548, 1462 lines | LGPL-2.1+, **EXC** | [verified] |
| 6 | **DOSBox 0.74 fast OPL** (dbopl) | OPL2/OPL3 | `RELEASE_0_74/src/hardware/dbopl.cpp` r3582 (2010-04-15), 1513 lines | GPL-2+, **EXC** | [verified] |
| 7 | **retro-go PCE-GO `psg.c`**, with its downsampling option | HuC6280 PSG | retro-go `05e97e8` (2022-11-18; `404025b`, 2023-02-26, removed the "audio downsampling option"), `retro-core/components/pce-go/psg.c` | retro-go GPLv2, **EXC** | [verified] |
| 8 | **retro-go nofrendo `apu.c`** | NES APU | retro-go `05e97e8` (2022-11-18, before the Feb 2024 timing changes), `retro-core/components/nofrendo/nes/apu.c` | Library GPL v2, **EXC** | [verified] |
| 9 | **atari800 POKEY (Ron Fries v2.4)** | POKEY | Debian `atari800 0.9.8a-2`, `pokeysnd.c` (796 lines) | LGPL-2 (Library GPL v2), **EXC** | [verified] |
| 10 | **MAME 0.124 `ay8910.c`**, before couriersud's rewrite in 0.124u4 | AY-3-8910/YM2149 | mamedev/mame tag **mame0124**, `src/emu/sound/ay8910.c` (900 lines) | MAME licence of that era (non-commercial), **EXC** | [verified] |

Runners-up: DOSBox 0.74 GUS (mono bug), DOSBox 0.72 SB/Tandy, retro-go Gwenesis (mono YM2612, AGPL), reSID 0.16, VICE FastSID, PocketSPC 0.3.

### Changes from draft 2
- **nesDS moved up to #1.** It is now on GitHub with tags, its licence is public domain, and the APU source is C, not asm. We already have an emulated DS sound unit from the GYOB port, so it should be cheap.
- **minigb_apu is pinned before its rework.** In April and May 2022 it moved to S16 output and fixed-point timing, and fixed wave clipping, noise starting late, register R/W while powered off, and noise clipping ([history](https://github.com/deltabeard/Peanut-GB/commits/master/examples/sdl2/minigb_apu/minigb_apu.c)). The `024175d` version is float-based, with a hipass of `0.996f` per channel and the old bugs.
- **Nuked 1.7.4 was found** in AdPlug ([issue #104](https://github.com/adplug/adplug/issues/104); [file history](https://github.com/adplug/adplug/commits/master/src/nukedopl.c)).
- **POKEY and MAME AY now have exact targets**, and both joined the top 10.
- **PCE-GO and nofrendo are pinned** to `05e97e8`.
- **GameYob dropped** (already GYOB).
- **Lameboy dropped**: closed source.
- **Snes9x GX dropped**: redundant.

## 1. NES APU

**nesDS** ([repo](https://github.com/DS-Homebrew/NesDS); [GameBrew changelog](https://gamebrew.org/wiki/NesDS)):
- **Authors:** loopy and FluBBa; "Sound by Mamiya".
- **Sound history:**
  - v0.43b: experimental DMC.
  - v1.1b2: mapped each APU channel to a DS sound channel.
  - v1.3a (2012): changed the volume curve to log-style.
  - v2.1 (2025-12-25): added APU frame IRQ and fixed DMC.
- **Git tags:** v1.3c+ `b6b5b30` (2023-09-05), v1.3d+ `d1f8a2d` (2023-10-26), v2.0 `0fff918`, v2.0.1 `652c8aa` and v2.1 `928afd9` ([tags](https://github.com/DS-Homebrew/NesDS/tags)).
- **Builds before v1.3a are not in git**, so the original linear-volume target is lost.
- **What `s_apu.c` @v2.0.1 does:**
  - Only the **triangle is summed in software**. The square, noise and DPCM render calls are commented out (presumably played by the DS hardware channels).
  - **The log volume conversion is commented out**, so volume is linear with fixed shifts.
  - DPCM is a 6-bit counter stepping ±1.
  - **The triangle's ultrasonic cutoff is disabled.**
  - The minimum rate is 11025 Hz.
- **Licence:** README "released into the PUBLIC DOMAIN"; `arm7/source/license.txt` lists Mamiya's player library as "PDS".
- **Port plan:** C, so a straight port plus the DS sound unit from GYOB. [verified]

**retro-go nofrendo** (target `05e97e8`, [raw @master](https://raw.githubusercontent.com/ducalex/retro-go/master/retro-core/components/nofrendo/nes/apu.c); [file history](https://github.com/ducalex/retro-go/commits/master/retro-core/components/nofrendo/nes/apu.c)):
- Header: Matthew Conte 1998–2000, Library GPL v2.
- **Cuts to reproduce:**
  - Runs once per frame, with no cycle stepping.
  - **The frame-counter advance is commented out.**
  - Envelope, length and triangle tables are precomputed per frame.
  - Channel output is `<<8`, and decay is `x -= x>>7`.
  - The DMC counter steps by 2 and is clamped.
  - Default filter: (3·cur + prev)/4.
  - **Per-channel enable checks are commented out.**
  - Writes to `$4009` and `$400D` are ignored.
- The behaviour was read @master. Between `05e97e8` and master only the timing constants changed (`ac6c23f`) and N163 was added, so the cuts apply to both. [verified]

**NesterJ (PSP)** ([GameBrew](https://gamebrew.org/wiki/NesterJ_PSP)):
- Licence: GPLv2.
- **Only v1.11 was released with source** (on the author's site, rukapsp.genin.jp; archive not opened).
- The sound processing was changed in v1.05 and v1.06 (2005-06) and FDS in v1.10. Since 1.11 is after those changes, it is a "PSP cut" core, not a pre-milestone one. [partial]

**NesterDC 7.1** (Dreamcast, Dec 2002) ([repo](https://github.com/pqrs-org/NesterDC/); [release notes](https://nesterdc.pqrs.org/docs/releasenotes/)):
- Licence: the nester core (`nester/nes/apu/`) is GPL-2; the Dreamcast code (`nester/dreamcast/`, including `sound/`, `dc_sound.c` and `dc_sound_mgr.cpp`) is BSD.
- Since 4.0 its APU is "ported from unofficial nester", likely the same lineage as NesterJ.
- A Racketboy review rates its sound as accurate, so there is **little jank; low priority**. [partial]

Dropped:
- **esp32-nesemu** has no sound ([repo](https://github.com/espressif/esp32-nesemu)).
- **pico-infonesPlus / pico-infones** (GPL-3.0, [repo](https://github.com/shuichitakano/pico-infones)) use InfoNES, which we already have. Their cuts were not documented. [unverified]
- **teensy-nes**: sound support unknown. [unverified]

## 2. SNES DSP
- **Snes9x GX** is **redundant**. It used the 1.51 core until 4.2.6 (March 2011, "Updated to Snes9x 1.52"), and its 1.43 branch is also covered ([snes9xgx](https://github.com/dborth/snes9x-gx), [snes9xgx143](https://github.com/askotx/snes9xgx143)).
- **PocketSPC 0.3** ([GameBrew](https://gamebrew.org/wiki/PocketSPC)):
  - Changes: 0.3 used an echo hack at 24 kHz; 0.4 brought real echo and FIR; 0.7 added Gaussian interpolation.
  - Source is a single 7z; licence "Mixed" (??). [partial]
- **SNES Station** (PS2) and **DreamSNES**: no source or licence found. [unverified]
- **Snes9xTYL**: based on Snes9x 1.39, which is close to S9X1. [partial]

## 3. Game Boy APU
- **minigb_apu @`024175d`** (see top 10):
  - Float timing, per-channel hipass, no frame sequencer.
  - Wave is offset by a per-volume diff of 7.5, 3.75 or 1.5.
  - Noise disables itself at `freq>=14`.
  - The bugs fixed later (wave clipping, noise late start, register R/W while off) stay in. [verified]
- **Pokitto Game Boy port**: its forum thread says the two squares carry most games, and sound costs about 20% CPU. That suggests a **"squares only" cut**. The forum's certificate failed when fetching, so this was not confirmed. [unverified]
- **Lameboy**: closed source ([GameBrew](https://gamebrew.org/wiki/GameYob_DS)). Dropped.

## 4. Sega
- **retro-go Gwenesis**
  - Files: `gwenesis/components/gwenesis/src/sound/ym2612.c` + `gwenesis_sn76489.c` ([raw](https://raw.githubusercontent.com/ducalex/retro-go/master/gwenesis/components/gwenesis/src/sound/ym2612.c)).
  - The YM2612 is MAME `fm.c` 1.4 with Eke-Eke's GPGX changes.
  - **Embedded cuts:** panning in `#if 0` with the right channel commented out, so **mono**; LFO PM table cut to 16 entries of `UINT8`.
  - The PSG is Maxim's SN76489 with "few simplification for gwenesis to fit on MCU" and a 16-step integer volume table.
  - Upstream gwenesis is **AGPL-3.0** ([repo](https://github.com/bzhxx/gwenesis)), EXC; flag this.
  - Close to GPGX 1.3.0, which we already have, so the value is mainly the mono summing. [verified]
- **jEnesisDS** (Lordus): "custom YM2612 and PSG on the ARM7".
  - v0.7.4 rewrote FM in ASM, moved FM from 16 to 28 kHz, and gave every channel its own DS channel ([GameBrew](https://gamebrew.org/wiki/JEnesisDS)).
  - **No source link**, licence "Mixed": unavailable.
- **PGEN** (PS2, Sjeep): source was posted to SourceForge ([project](https://sourceforge.net/projects/pgen/)), but no licence or sound files were found. [unverified]
- **SMSAdvance** (FluBBa, GBA) ([GameBrew](https://gamebrew.org/wiki/SMSAdvance_GBA)):
  - **No YM2413.**
  - V1.5 (2006-05-13) fixed "volume attenuation", and V0.9 optimised the mixer.
  - Only the final V2.5 source archive exists. Licence "Mixed" (??). ARM asm, so reimplement it rather than port. [partial]
  - Its successor S8GBA ([releases](https://github.com/FluBBaOfWard/S8GBA/releases)) was not checked for a licence.

## 5. Other famous chips
- **POKEY**: atari800 0.9.8a `pokeysnd.c` ([Debian](https://sources.debian.org/src/atari800/0.9.8a-2/pokeysnd.c)).
  - Ron Fries v2.4, 1996–98.
  - Event-driven divide-by-N, with **channels above the playback rate skipped**.
  - The 1998 high-pass filters are on channels 1 and 2.
  - **Volume-only mode is faked** by setting the counter to max.
  - The later "new Pokey engine" lacks VOL_ONLY on the second POKEY ([BUGS](https://sources.debian.org/src/atari800/5.2.0-2/DOC/BUGS)).
  - 796 lines, LGPL-2. [verified]
- **AY-3-8910**: MAME `mame0124` `ay8910.c` ([file](https://github.com/mamedev/mame/blob/mame0124/src/emu/sound/ay8910.c)).
  - A 32-step table at 1.5 dB per step (÷1.188502227 from 0x7fff), with no resistor-load mixing.
  - The rewrite that added "a model to calculate mixing of channels with different resistance loads" landed in 0.124u4 ([MAMEdev wiki](https://wiki.mamedev.org/index.php/MAME_0.124u4)). [verified]
- **SID, VICE FastSID** ([SVN trunk](https://sourceforge.net/p/vice-emu/code/HEAD/tree/trunk/vice/src/sid/fastsid.c)):
  - At r46375: 1409 lines, GPL-2+, by Teemu Rantanen, Michael Schwendt and Ettore Perazzoli.
  - Its own comments flag "kludge for ring modulation", "XXX: fix this" in decay, and "XXX: check these" on the ADSR tables.
  - FastSID was never replaced, so **any VICE tag works**. Pin a release tag when porting. [partial]
- **SID, reSID 0.16** (2004-06-11):
  - 0.16 rewrote the filter convolutions and fixed a filter init bug. reSID 1.0 models the filter "at the transistor level" ([NEWS](https://github.com/libsidplayfp/resid/blob/master/NEWS)).
  - Source: [Olzaq/reSID](https://github.com/Olzaq/reSID), a copy of the zimmers.net release (version not checked in-browser).
  - GPL-2. [partial]
- **PC Engine PSG**: retro-go PCE-GO @`05e97e8` ([raw @master](https://raw.githubusercontent.com/ducalex/retro-go/master/retro-core/components/pce-go/psg.c)).
  - 22050 Hz, 16.16 fixed-point pitch.
  - **No LFO.**
  - **DDA is a fixed repeat of 3 samples.**
  - Noise on channels 4–5 only; mono averages L and R.
  - The target commit also keeps the audio downsampling option. [verified]
- **Teensy SID**: SIDKick ([repo](https://github.com/frntc/SIDKick)) runs reSID 0.16/1.0 and fmOPL. It is not a cut core, so skip it.
- **Thumby** (GPL-3 hardware) and **Pokitto**: no emulators with sound source found ([Hackster](https://hackster.io/news/tinycircuits-open-sources-its-rp2040-powered-tiny-games-console-the-thumby-under-the-gpl3-license-8317fc0dfa29)). [unverified]

## 6. DOS sound cards (x86 console)
DOS works as "card + engine": the x86 console offers cards, and each card has engine variants. AdLib now has **five verified engine choices**: ymfm (stock), DOSBox 0.72 MAME, DOSBox compat, DOSBox fast, and Nuked 1.7.4. A sixth is our existing MAME 0.36 OPL (`sega/Mame036Opl.cs`).

| Engine | Target ([SVN tags](https://sourceforge.net/p/dosbox/code-0/HEAD/tree/dosbox/tags/)) | Licence | Evidence |
|---|---|---|---|
| 0.72 MAME OPL | `RELEASE_0_72/src/hardware/fmopl.c` r2883 + `ymf262.c` | ?? | 0.73 "Add two new opl2+opl3 emulators"; 0.74 "Remove old OPL cores" ([ChangeLog](https://github.com/blackberry/DosBox/blob/master/ChangeLog)) [verified] |
| compat | `RELEASE_0_74/.../opl.cpp` r3548 | LGPL-2.1+ | [verified] |
| fast | `RELEASE_0_74/.../dbopl.cpp` r3582 | GPL-2+ | [verified] |
| Nuked 1.7.4 | adplug `adplug-2.3.1` `src/nukedopl.c` | LGPL-2.1+ | The 1.8 update was AdPlug `51c8d6a` (2020-10-20); the author says 1.8 fixed differences against the die shot ([VOGONS](https://www.vogons.org/viewtopic.php?p=505488)) [verified] |
| GUS | `RELEASE_0_74/.../gus.cpp` r3548 | GPL-2+ | 0.74 mono, fixed in SVN ([VOGONS](https://www.vogons.org/viewtopic.php?p=719442)); avoid 0.74-2 ([VOGONS](https://www.vogons.org/viewtopic.php?p=719237)) [partial] |
| SB DSP | `RELEASE_0_72/.../sblaster.cpp` r2952 | GPL-2+ | 0.74 changed the DSP reset and fixed the SB 1.5 DSP version [verified] |
| Tandy | `RELEASE_0_72/.../tandy_sound.cpp` r2905 | GPL-2+ | 0.74 "Add low level Tandy DAC emulation", so 0.72 has no DAC [verified] |
| PC speaker, Disney, Game Blaster | 0.72 `pcspeaker.cpp` r2920, `disney.cpp`, `gameblaster.cpp` r2728 | GPL-2+ | [partial] |
| Munt (MT-32) | 0.1.3 (2005) was the accuracy step: "Volume, pitch, filter and envelopes are now more accurately calculated" ([VOGONS](http://www.vogons.org/viewtopic.php?t=9300)). So 0.1.2 or earlier is the target, but **no 0.1.x tag was found in git** ([tags](https://github.com/munt/munt/tags)) | LGPL-2.1+ | [partial]. **Needs MT-32 ROMs**, which conflicts with the no-firmware rule |
| PCem / 86Box | the build before the PCem NukedOPL default patch ([forum](https://pcem-emulator.co.uk/phpBB3/viewtopic.php?p=5799)), likely dbopl, so redundant | GPL-2 | [partial] |

## Still open
- **Game & Watch retro-go fork:** sylverb / olderzeus, GPLv2 + MIT parts ([fork](https://github.com/Icebox2/game-and-watch-retro-go)). Its cut-down sound paths were not inspected. [unverified]
- **No source found** for SNES Station, DreamSNES, PGEN sound, jEnesisDS or Lameboy.
- **Version tags still to pin:** reSID 0.16 (in the mirror), a VICE tag for FastSID, and Munt 0.1.x.
- **Licences unconfirmed:** S8GBA and SMSAdvance.
- **Not seen yet:** the Pokitto Game Boy port's code, and the NesterJ 1.11 archive contents.
- **Recordings:** none were compared for any entry.

## Climb plan (2026-11-07)
User decisions are in memory `project_apu_harvest_climb.md`. Art instruments: licences are handled with references (THIRD_PARTY_NOTICES, FA-1), not exclusions. Faithful ports, flaws kept and listed in each core's header. Sources go to `C:\BrokenNes-tools\apu-harvest\<name>\` (untrusted, sha256 manifest, never run). Excluded: Munt/MT-32, anything without source.

Order: nesDS; minigb_apu; AdLib engines + x86 card/engine menu; nofrendo + PCE-GO; POKEY; MAME 0.124 AY; DOSBox SB/GUS/Tandy; PocketSPC, SMSAdvance, Gwenesis; the rest.
Per core: fetch, port, register, notices, `--apu-channels` + `--state`, suite, commit, push (ancestor check vs refs/heads/main). Full battery (UAT/battery.ps1 + fuzz) at the midpoint and at the end.

### Progress
| Core | Status | Commit |
|---|---|---|
| nesDS ARM7 (v2.0.1), NES APU id NSDS | ported | (this commit) |
| minigb_apu (024175d), GB APU id MGB + NES DMG_MGB | ported | (this commit) |
| AdLib engines (M036, D072, D074C, D074F, NUK174) + x86 APU menu ADLIB_<engine> | ported | (this commit) |
| nofrendo (05e97e8), NES APU id NOFR | ported | (this commit) |
| VICE 3.7 FastSID, NES APU id FSID (re-keyed) | ported | (this commit) |
| NesterJ 1.11 (PSP, nofrendo nes_apu.c without APU_YANO), NES APU id NSTRJ (2026-11-12) | ported | (this commit) |
| PocketSPC 0.3 (GameBrew pocketspc-src-v3), SNES APU id PSPC3 + NES SNES_PSPC3 (v3 has no echo: ECHO_ENABLED off, the "echo hack" adds EVOL to EON voices' volume) | ported | (this commit) |
| reSID 0.16, NES APU id RSID16 (re-keyed) | ported | 164e32a1 (cherry-picked) |
| PCE-GO psg (05e97e8), NES APU id PCEG (re-keyed onto the HuC6280 PSG) | ported | 24398fcb (cherry-picked) |
| atari800 POKEY, NES APU id POKY (re-keyed) | ported | (this commit) |
| MAME 0.124 AY, NES APU id AY124 (re-keyed) | ported | (this commit) |
| DOSBox 0.72 SB / 0.74 GUS / 0.72 Tandy + SPEAKER, COVOX cards, 8237 DMA | ported (cherry-picked from 5b55df52, c13a53a9, 991dd851; DOS env BLASTER / ULTRASND on the FreeDOS disk) | merged |
| PocketSPC 0.3 | planned | |
| SMSAdvance 2.5 PSG (sn76496.s), SMS/GG APU id SMSA + NES SMS_SMSA (no Genesis: the GBA emulator has no MD PSG path offered) | ported | (this commit) |
| Gwenesis YM2612 | done (GWEN) | `sega/GwenYm2612.cs`, `GwenSn76489.cs`, `md/MdSoundUnitGwen.cs`, `SmsSoundGwen.cs`; Genesis APU + SMS/GG PSG menus |
