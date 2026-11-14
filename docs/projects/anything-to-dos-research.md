# Anything to DOS: research

User, 2026-11-14: "Anything to DOS, a pipeline to take anything and cram it on a bootable floppy. some snes and genesis games might not fit."

This is research only. Nothing was built and nothing was downloaded. It grew out of "Nested Everything" (see `nested-everything-research.md` and `nested-fantasy-research.md`, written by sibling agents; this file does not repeat them). The target is a DOS PC that boots from one floppy using our VRUN-DOS bootloader.

## How to read this file
Every number is tagged: **[measured]** = measured on this machine on 2026-11-14 (script and data below); **[sourced]** = quoted from a period readme or page, link given; **[code]** = read from BrokenNes source; **[estimate]** = my own reasoning, not checked. The first pass of this file (06f2a00e) used remembered figures and guessed library shares; this second pass replaces them.

## Summary

| Source console | Approach | Min PC for full speed | Scanned set (games) | Median ROM [measured] | Fits 1 floppy raw [measured] | Fits 1 floppy compressed [measured] | Needs 2 / 3 / 4+ floppies [measured] |
|---|---|---|---|---|---|---|---|
| NES | (a) small DOS emulator + ROM | 486 class; NESticle reported 486SX-40 [sourced, forum]; 386 only with frame skip [estimate] | 758 | 256 KB | 757 (99.9%) | 758 (100%) | 0 / 0 / 0 |
| Game Boy (DMG) | (a) | 386DX-33 (no$gmb) [sourced]; 486DX-33, DX2-66 recommended (VGB-DOS) [sourced] | 622 | 128 KB | 620 (99.7%) | 622 (100%) | 0 / 0 / 0 |
| Game Boy Color | (a) | 486 ~66 MHz (no$gmb) [sourced] | 529 | 1024 KB | 327 (61.8%) | 499 (94.3%) | 30 / 0 / 0 |
| Master System | (a) | Pentium strongly recommended, 486 with severe frame skip (Massage) [sourced] | 457 | 256 KB | 457 (100%) | 457 (100%) | 0 / 0 / 0 |
| Game Gear | (a), same core as SMS | as SMS [sourced, same emulator]; smaller screen helps [estimate] | 640 | 256 KB | 640 (100%) | 640 (100%) | 0 / 0 / 0 |
| Genesis | (a), hand-written 68000 core (Genecyst/KGen model) | Pentium class (Genecyst) [sourced, secondary]; 486 DX4-100 = slow motion with frame skip [sourced, forum] | 947 | 1024 KB | 624 (65.9%) | 867 (91.6%) | 76 / 4 / 0 |
| SNES | (a) ZSNES-style asm core; (c) hybrid CPU later | ZSNES 0.800: 386 minimum, P100 recommended, P200 for SuperFX [sourced]; ZSNES 1.51 DOS: Pentium II 233 [sourced]; Snes9x 0.1 DOS: Pentium class [sourced] | 2122 | 1536 KB | 920 (43.4%) | 1627 (76.7%) | 454 / 32 / 9 |
| WideMess | not worth it (our fantasy console; needs a SNES-plus core) | above SNES [estimate] | - | - | - | - | - |
| N64 | out of scope for DOS | - | - | - | - | - | - |

Recommendation: **NES first** (Game Boy a close second). Smallest proof of concept: our VRUN CTW NES MMC3 build in a tiny DOS NES emulator on the VRUN-DOS floppy, booted headless in the BrokenNes x86 console.

### How the library numbers were measured
- Sets: the user's own folders under `X:\EMULATION` (NES `Nintendo Entertainment System` 758 .nes; GB `GameBoy` 622 zipped; GBC `GameBoy Color`, only files with the CGB flag at 0x143 = 529; SMS `Sega Master System` 338 .7z + 3 .sms, extracted to scratch, 457 ROM files because some archives hold several; GG `Sega Game Gear` 640; Genesis `Sega Genesis` 947; SNES `Super Nintendo` 2122). These are GoodTools-style sets, so revisions, hacks and bad dumps are counted as separate games; I did not de-duplicate. The Nested roster (`C:\BrokenNes-work
ested_compat
oms`, 55 NES ROMs) is a subset of the NES set. iNES (16 bytes) and copier (512 bytes) headers were stripped.
- Room for the ROM [estimate built on measured parts]: 1,474,560 bytes minus FAT12 + root (~10 KB), VRUN-DOS boot + screen (~10 KB), UPX FreeDOS kernel (46 KB, measured in the bootloader round), the emulator EXE (~70 KB for an 8-bit core, ~140 KB for Genesis, ~170 KB for SNES, all [estimate]) and a 32 KB save slot. That gives 1.33 MB for 8-bit consoles, 1.26 MB for Genesis and 1.23 MB for SNES. Every extra floppy is counted at 1.44 MB.
- Compressed = Python `lzma` raw LZMA1, preset 9 extreme, as a stand-in for a ratio-first packer. Median ratio [measured]: NES 2.26, GB 2.01, GBC 3.45, SMS 2.23, GG 2.22, Genesis 2.25, SNES 2.11. A real DOS-side packer such as ZX0 would compress somewhat less (my guess is 10-20% bigger output) [estimate], so read the compressed columns as an upper bound.
- Scripts are in the session scratchpad (`a2d/scan.py`, `a2d/an.py`, data `a2d/sizes.json`); they are not committed.

## 1. Approach per console

### The three choices
- **(a) Bundled emulator.** One DOS emulator per console, a few tens of KB, with the ROM next to it. This is proven technology: every console here had a DOS emulator in 1996-2000 that ran at full speed on period PCs. The risk is low, it works with every game, and the per-game work is zero.
- **(b) Static + dynamic recompilation to x86 (Nested-style).** It runs faster, but Nested's own HANDOVER shows the cost: a per-game climb (indirect jumps, self-modifying code, timing-sensitive raster tricks, mapper IRQs). For 6502 / Z80 / SM83 code the speed gain does not matter, because a 486 already runs those emulators at full speed. The time goes into the picture and sound chips, not the CPU.
- **(c) Hybrid.** A recompiled CPU with an emulated PPU/VDP. It only pays off where the CPU is the bottleneck on the target PC: the SNES 65816 on a 486/Pentium 60, and maybe the Genesis 68000 on a 486.

Conclusion: (a) everywhere for version 1. Keep (c) as a later "slow PC" mode for SNES/Genesis only. Pure (b) is not worth it for DOS. (Nested makes sense on the SNES because the SNES can't emulate a NES. A PC can.)

### Period data (checked 2026-11-14; quotes are short, links are the pages read)
| Emulator | What the source says | Tag | Link |
|---|---|---|---|
| no$gmb (DOS) | "for monochrome games a 386 with at least 33Mhz", "for color games a 486 with something like 66Mhz" | sourced (author's page) | https://problemkaputt.de/gmb.htm |
| VGB-DOS 0.88 | "A 486DX-33 (486DX2-66 recommended)", VGA, AdLib/SB Pro/SB16 and joystick; default video 320x200; needs a DPMI host (CWSDPMI) | sourced (readme) | https://discmaster.textfiles.com/view/23261/CHIP%20Kas%C4%B1m%201998.iso/emulators/gameboy/vgbdos88dos.exe/VGB.TXT |
| ZSNES 0.800 (DOS, 1998) | minimum "386 processor (486 processor required for new graphics engine)", 8 MB RAM; recommended "Pentium processor (P100 or higher)"; P166 for 65536-colour mode, P200 for SuperFX; 10 MB RAM for 48 Mbit ROMs | sourced (archive page copying the readme) | https://archaic-ruins.lngn.net/emulator/snes/zsnes.htm |
| ZSNES 1.51 (DOS port) | "Pentium II (or equivalent) 233MHz", 32 MB RAM, SB Pro or compatible | sourced (official docs) | https://zsnes-docs.sourceforge.net/html/readme.htm |
| Snes9x 0.1 (DOS, 1997) | "a Pentium-class PC or higher", 8 MB RAM under DOS, a Sound Blaster | sourced (readme) | https://discmaster.textfiles.com/view/38188/CD2.iso/GAME/SNES9X.DOS/README.TXT |
| Massage (SMS/GG, DOS) | "A Pentium is strongly recommended, but is playable on a 486 with some severe frame skipping." | sourced (archive page) | https://archaic-ruins.lngn.net/emulator/sms/massage.htm |
| Genecyst (Genesis, DOS) | no requirement on the archive page; a retro write-up says a Pentium-class machine with VESA and a DOS sound card was needed; Vogons users report a 486 is slow motion and enjoyable only from a DX4-100 with frame skip | sourced, secondary only | https://archaic-ruins.lngn.net/emulator/genesis/genecyst.htm , https://www.retrounlim.com/?p=729240 |
| KGen 98 (Genesis, DOS) | no CPU figure found; the author says the focus "has now changed from speed to accuracy"; palette raster effects need VESA 2.0 | sourced (archive page) | https://archaic-ruins.lngn.net/emulator/genesis/kgen98.htm |
| NESticle (DOS) | readme copy not reachable (404); Wikipedia calls it a good choice for 486 and earlier PCs; a Vogons list gives "req 486SX40" | sourced, secondary only | https://en.wikipedia.org/wiki/NESticle |

Not reached: the Vogons "fastest emulators" thread (403 / refused), the Game Tech Wiki Genecyst page (403), Sega Retro (bot wall). Still missing: an official NESticle, Genecyst or KGen CPU figure; those would need the original archives, which are downloads (not done).

What this tells us: Game Boy is the only console with a sourced 386 figure (DMG only). NES, GB Color, SMS and GG sit at 486 class. Genesis and SNES need a Pentium for full speed with sound. The 386 floor set for VRUN-DOS on 2026-11-07 does not carry over to most consoles, and the user should decide the minimum PC per console.

### Write our own or reuse a period emulator?
The period emulators are closed-source freeware (NESticle, Genecyst) or GPL/nonfree licences (ZSNES GPL, Snes9x non-commercial), and most of them want Windows-era drivers or a DOS4GW extender. Options:
1. **Port our own cores to DOS** with Open Watcom + DOS/32A (the same toolchain as Vaults/Super VRUN DOS). Our C# FIX cores do not port directly. We would rewrite a lean C/asm core per console, using the FIX cores as the reference and Mesen/BrokenNes for differential checks. The size target is about 40-80 KB compressed per core.
2. **Port small open C emulators** that are already DOS-friendly: gnuboy (already used for Nano GB; it had a DOS port), InfoNES/LiteNES (NES), SMS Plus (SMS/GG, had a DOS build), Gens/Genesis Plus (Genesis; heavier), Snes9x (SNES; a DOS build existed). The licences vary (GPL; Snes9x non-commercial). We already took the "licence ignored for art, credits kept" line for Nano GB. Ask the user again for a shipped pipeline.

Option 2 is the fastest route to a proof of concept. Option 1 matches the "our own cores" spirit and the FIX accuracy work.

## 2. Video and sound on DOS

### Video
| Console resolution | Mode | Notes |
|---|---|---|
| NES 256x240 (show 256x224) | Mode 13h 320x200 cannot hold 224 lines; use **Mode X 320x240** (tweaked, planar) or 256x240 tweaked "Mode Q"-style | NESticle used tweaked modes. Our Vaults DOS uses tweaked 360x480 with centring (DOS_GAME_PLAYBOOK.md), so the tweak code already exists. |
| SNES / SMS / Genesis 256x224 | Mode X 320x240, centred | One shared mode for all 256-wide consoles. |
| Genesis H40 320x224 | Mode X 320x240 | Exact width fit. |
| Game Boy 160x144, Game Gear 160x144 | Mode 13h 320x200 (pixel-doubled 2x vertically does not fit; use 1x or 2x horizontal only) or Mode X 320x240 at 2x = 320x288 does not fit, so use 1x centred, or 360x480 tweaked with 2x | The simplest choice is 13h, 1x centred, or a stretched 2x horizontal and 1x vertical. |
| VESA 640x480x8 | Optional, needs VBE (Pentium-era cards) | Not needed for any console here. |

Mode 13h is linear and fastest to draw. Mode X is planar and costs plane switches. The usual trick is to render each line into a linear buffer and copy it out per plane. Our x86 console already has a "VGA planar line fast path", and Vaults uses a tweaked planar mode, so both are tested.

**Palette:** VGA has 256 colours from 18-bit RGB (6 bits per gun).
- NES (~54 colours) and GB/GBC-DMG (4) fit exactly. SMS (64, 6-bit) fits exactly in 6-bit DAC values.
- Game Gear (4096, 12-bit) shows at most 32 at once. Load the 32 CRAM entries into DAC slots on each CRAM write. Exact.
- Genesis (9-bit, 512 colours) shows 64 at once (+ shadow/highlight = 192). Map CRAM to DAC slots directly. 3 bits per gun fit in 6. Exact, including shadow/highlight, if we reserve 3x64 slots.
- GBC (15-bit) shows up to 56 at once per frame. Fits in DAC slots, but games that change palettes mid-frame (HDMA tricks) need a per-line remap. Accept small errors.
- SNES (15-bit, 256 CGRAM entries) is the only console that overflows: colour math (add/sub, half) makes new colours not in CGRAM. Option: quantise to a 6-6-6 (216) or 8-8-4 cube plus CGRAM. That costs a little quality, and the SNES "fit" section of the playbook would note it. Mid-frame CGRAM writes (gradients) also break the 256-slot model. Accept banding.

### Sound
| Source chip | DOS output | How |
|---|---|---|
| NES 2A03 | Sound Blaster DSP PCM (8-bit 11-22 kHz, auto-init DMA) | Mix the 5 channels in software. A 486 manages ~22 kHz. |
| NES on a PC without SB | OPL2 (squares as FM approximations) or PC speaker (1 channel) | Low-fidelity fallback. |
| GB/GBC APU, SMS/GG SN76489 PSG | SB PCM; OPL2 can fake square waves; speaker as a last resort | PSG is cheap to mix. |
| Genesis YM2612 + PSG | **FM mapping YM2612 -> OPL2/OPL3** (4-op to 2-op loses timbre; OPL3 4-op mode is closer) or full software FM via SB PCM (Pentium needed) | Genecyst-era emulators did software FM on the SB. Mapping to OPL3 is the cheap route on a 486. |
| SNES SPC700 + DSP | SB PCM only (8 voice BRR sample mixing) | Expensive. Snes9x needed a Pentium II for good DSP sound. ZSNES used lower-quality mixing on slower PCs. |

Reuse: Vaults DOS already has an FM probe, its own ISR stack and AdLib playback. **Checked in code [code]: the x86 console already has a Sound Blaster.** Commit 5a32f732 (2026-11-07) added an 8237 DMA controller (`Windows/NesEmulator/x86/Dma8237.cs`) and `Dosbox072SoundBlaster.cs`, a line-by-line port of DOSBox 0.72's card. It works as an SB Pro 2 (DSP 3.02) at 220h, IRQ 7, DMA 1, with `SET BLASTER=A220 I7 D1 T4`, and keeps 0.72's quirks on purpose. SB16-only commands answer as an SB Pro, and its FM is the board's OPL2, not an OPL3. There are also a GUS (`Dosbox074Gus.cs`), Tandy, Covox, PC speaker and several OPL engines (ymfm, Nuked, DOSBox DBOPL), chosen through the x86 APU id (`PcSound.Apply`: ADLIB, SB_D072, SB_D072_<engine>, GUS_D074, ...). So 8-bit PCM sound can be tested headless today. Two caveats: it is a 'faithful quirks' card (direct-DAC timing is smeared to 1 ms; IRQs come up to ~1 ms late), so a real SB can differ; and there is no OPL3, so a YM2612-to-OPL3 4-op mapping cannot be heard headless. Target SB Pro (8-bit, 22 kHz) rather than SB16 for that reason.

## 3. The floppy budget

1.44 MB = 1,474,560 bytes. The room left for the ROM is 1.23-1.33 MB depending on the console (breakdown in "How the library numbers were measured" at the top; the kernel size is measured, the emulator sizes are [estimate]). Dropping the COMMAND.COM fallback (~66 KB) for these images would give more room.

Measured over the user's sets (table at the top): with compression, **every** NES, DMG, SMS and GG game fits on one floppy. 94% of GBC, 92% of Genesis and 77% of SNES games fit too. Without compression, GBC drops to 62%, Genesis to 66% and SNES to 43%. The user's guess ("some snes and genesis games might not fit") holds: 80 Genesis and 495 SNES files (counting revisions) need a second or third floppy, and only 9 SNES files would need four or more.

**Compression:** a ROM is decompressed into RAM once at boot (DOS/32A gives flat memory; 4 MB of RAM is needed for a 4 MB ROM, and these PCs had 4-16 MB). Choices:
- **SC/32A**, which we already use, compresses the EXE only.
- **LZ4 / LZSA** unpack fast, perhaps 2-5 MB/s on a 486, but compress less (maybe 1.3-1.6x) [estimate].
- **aPLib / ZX0 / LZMA** give better ratios. LZMA at its highest setting measured a median 2.0-2.3x on these sets (3.45x on GBC) [measured]. They unpack more slowly: my guess for LZMA is ~0.3-1 MB/s on a 486, a few seconds for 2 MB [estimate, not timed on a 486; it could be timed in the x86 console at a 486-like --ips]. That is acceptable at boot because floppy reads dominate (our model: ~26 s for 1.2 MB at 386 speed).
Recommendation: one decompressor, **ZX0 or LZMA-lite** (we write or port a small asm unpacker; both are tiny). Use a ratio-first choice, because reading floppy sectors costs far more than unpacking. Proof: in the bootloader round, SC's unpack cost was "far less than the reads saved".

**Games that don't fit:**
1. **Multi-floppy sets.** Disk 1 holds the boot + runtime + part 1 of the ROM; then "insert disk 2" (full-screen ASCII prompt in our loader style); all parts load into RAM before play. Clean, period-authentic, and needs no swapping during play. Saves go to disk 1.
2. **DMF 1.68 MB / 2.88 MB.** The user decided on **standard 1.44 MB only (no DMF)** on 2026-11-07. 2.88 MB ED drives are rare. Don't propose them again except as a footnote.
3. **Refusal.** BrokenDev tells the user "needs N floppies" and builds the set, or refuses beyond a cap (say 4 disks / needs > 16 MB RAM).
Suggested rule: fits raw -> 1 disk; fits compressed -> 1 disk; else a multi-disk set up to 3; else refuse.

## 4. The pipeline as a BrokenDev feature

`brokendev dos-floppy <rom> [-Out IMG] [-Disks auto]`:
1. Detect the console (BrokenNes already does this), pick the matching DOS runtime EXE (prebuilt, cached, sha pinned), and check the size budget (table above).
2. Compress the ROM, write a `game.json` (VRUN-DOS NEW_GAME.md format: logo/kicker/name/colours), and add a generated config (key map, video mode, sound card).
3. Call `VRUN_DOS_Boot_dev/build.ps1 -Game ... -Exe <runtime> -Files rom.zx0,cfg` -> IMG (one or more for multi-disk sets). Ship the source zip next to it (GPL: FreeDOS + any GPL emulator code).
4. Release to `T:\BrokenDev\Releases\<Game>_DOS\` + .sha256 (releases-folder rule).
5. Real floppy: `tools\writefloppy.ps1 -Image IMG -Save NAME.SAV` (track by track, boot sector last, verify, keeps the save).

**Headless test:** the BrokenNes x86 console boots FreeDOS images (`--x86boot --until/--shotat/--ips`). At 130M ips with the JIT, it runs Vaults at ~3x real time. A 486DX2-66 is roughly 30-50M instr/s, so we can set `--ips` to model the target PC and check "full speed at 486 level" by counting the emulator's own frame counter. Golden tests: screenshot hashes at fixed frames compared with the same ROM in BrokenNes's native core (differential, like the Mesen checks). This is the big win: the pipeline checks itself. Gaps: the gameport is not emulated (`PcMachine.cs` port 201h returns FFh, 'no joystick') [code]; there is no OPL3; disk timing isn't modelled in the x86 console (use DOSBox-X + tools/floppymodel.py from the bootloader round for boot-time estimates).

**Real hardware:** the user's period PC (VRUN-DOS already "works beautifully" there). Check the 36-sector cross-head read, sound card detection and save writes.

**Input:** keyboard by default (an INT 9 handler of our own, as in Vaults; arrows + Z/X/A/S + Enter/RShift), remappable in the config. Gameport joystick (port 201h): 2-4 buttons, analog axes polled by timing. Fine for NES/SMS/GB (2 buttons). Genesis 3-button and SNES 6-8 buttons need keyboard plus joystick together, or a Gravis GamePad Pro (GrIP protocol), which is more work later.

**Save RAM:** at the user's pause, or on exit, write battery RAM to `A:\GAME.SAV` (the floppy is writable; the VRUN-DOS cache drops on writes, which is already handled). Use a fixed-size, preallocated file so the write never changes the FAT (safer on bad media); Vaults does this already per the playbook. writefloppy.ps1 already keeps a save when rewriting. For multi-disk sets, the save lives on disk 1, so the user is prompted to reinsert it.

## 4b. Where this sits: the "Nested Everything" section of BrokenDev

The user's new plan (relayed 2026-11-14): every cross-platform pipeline lives in one "Nested Everything" section of BrokenDev. Each pipeline is easy to configure and turns a game into a ROM or image that runs on a platform it wasn't made for: NES->SNES (our Nested fork), GG->SMS, ->Genesis, ->DOS floppy, later ->N64. Anything to DOS fits as one *target* in that section. What it shares with the others, and what is its own:

- **Shared pipeline shape** [estimate, design]: `source ROM -> detect console -> pick a recipe (target + approach + options) -> build -> pack for the target medium -> verify headless -> release`. NES->SNES is a recompile plus runtime, GG->SMS is mostly a patch plus palette/screen adapter, and ->DOS is an emulator plus ROM plus boot medium. One recipe file (say `nested/<target>/<source>.json`: approach, core, video mode, sound card, key map, compression, disk limit) lets the UI show each pipeline as a form instead of code.
- **Shared verification:** every target can be checked the same way: run the output in BrokenNes's emulator for the *target* (SNES core for Nested, SMS core for GG->SMS, x86 console for DOS) and compare frame hashes with the *source* game in its native BrokenNes core. For DOS that is the `--x86boot --shotat` route; the x86 console's frames are deterministic (instruction clock) [code, per project notes].
- **Shared "does it fit" step:** each target has a size budget (SNES cart sizes for Nested, 1.44 MB floppies here, N64 cart sizes later). The measured table above shows the form the UI could use: fits / fits compressed / needs N media / refused.
- **DOS-specific parts:** the VRUN-DOS bootloader + FreeDOS (GPL: ship the source zip with every image), the per-console DOS runtime EXEs, multi-floppy sets, `writefloppy.ps1` for real disks, saves on the floppy.
- **Reuse across targets:** the DOS runtimes are emulators, not recompiles, so they serve *every* source game of a console. A Nested-style recompile pays off only where the target is too slow to emulate (NES->SNES, and maybe ->N64, which is out of scope here).
- **Order inside the section** [estimate]: NES->SNES exists; GG->SMS is the cheapest next one; ->DOS NES and DMG are cheap once one DOS runtime exists; ->Genesis and ->N64 are separate research topics (see the sibling `nested-everything-research.md`).

## 5. Recommendation and risks

**First: NES.** It has the best period proof (NESticle at 486DX2-66), nearly the whole library fits on one disk, the palette is exact, and we have the deepest accuracy tooling (FIX cores, Mesen diffs, the Nested certification work). Our own VRUN CTW NES MMC3 ROM is the obvious first game (no licensing question). **Game Boy** is a strong second. gnuboy is already ported for Nano GB and it is the lightest emulator; the 4-colour picture is trivial.

**Smallest proof of concept:**
1. Port a small C NES emulator (InfoNES-class, or a lean C rewrite of the FIX core: CPU + PPU, mappers 0/1/2/4) to Open Watcom + DOS/32A. Use Mode X 320x240, keyboard only, PC speaker or OPL2 square approximation for sound (both already emulated in our x86 console, so headless tests work today).
2. Build a VRUN-DOS image with VRUN CTW NES MMC3 as the game.
3. Boot it headless in the x86 console at a 486-like `--ips`, take a screenshot hash at frame N, and compare it with BrokenNes NES at frame N.
4. Then do the BrokenDev command wrapper, compression, saves, SB PCM.

**Biggest risks:**
1. **Speed on the stated 386 floor.** Period data says 486 for 8-bit and Pentium for 16-bit. The 386 target set for VRUN-DOS does not carry over. We need the user's decision on the minimum PC per console.
2. **Headless sound and input testing has gaps.** The x86 console's SB Pro 2 is a quirk-faithful DOSBox 0.72 port, so timing on a real card can differ. There is no OPL3 (no 4-op YM2612 mapping test) and no gameport [code].
3. **SNES / Genesis size and speed together:** many don't fit and those that do need a Pentium. SNES colour math doesn't fit a 256-colour palette. Expect "SNES to DOS" to cover only a slice of the library.
4. **Licences:** reusing period or open emulators brings GPL and non-commercial terms (Snes9x, MAME-derived code). Our own cores avoid that but cost far more work.
5. **Accuracy drift:** a lean DOS core is less accurate than FIX. Without the differential frame-hash tests some games will silently glitch, so make the golden comparison part of the pipeline from day one.

## Files I'd want (not downloaded; each needs the user's approval)
- InfoNES or LiteNES source (NES proof-of-concept core), from its official repository, pinned commit.
- SMS Plus source (Charles MacDonald), for SMS/GG.
- Optionally ZX0 (Einar Saukas) reference compressor + 8086/386 decompressor source, pinned release.
- The original NESticle, Genecyst and KGen 98 archives, only to read their own readme requirements (the web pages didn't give official CPU figures). These are downloads, so they need approval.
gnuboy, Open Watcom, DOS/32A, NASM, UPX and FreeDOS are already in C:\BrokenNes-tools.
