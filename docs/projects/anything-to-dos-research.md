# Anything to DOS: research

User, 2026-11-14: "Anything to DOS, a pipeline to take anything and cram it on a bootable floppy. some snes and genesis games might not fit."

This is research only. Nothing was built and nothing was downloaded. It grew out of "Nested Everything" (see `nested-everything-research.md` and `nested-fantasy-research.md`, written by sibling agents; this file does not repeat them). The target is a DOS PC that boots from one floppy using our VRUN-DOS bootloader.

## Summary

| Source console | Approach | Min PC for full speed | Typical ROM | Share of library on one 1.44 MB floppy |
|---|---|---|---|---|
| NES | (a) small DOS emulator + ROM; Nested-style recompile later as an option | 486DX2-66 (NESticle-class); 386DX-40 runs at reduced frame rate | 24-512 KB | ~99% as-is (all but a few 1 MB carts, which fit compressed) |
| Game Boy (DMG) | (a) small DOS emulator | 486DX-33 to 486DX2-66 | 32 KB - 1 MB | ~97% as-is, ~100% compressed |
| Game Boy Color | (a) | 486DX4-100 / Pentium 75 | 512 KB - 4 MB | ~60% as-is, ~80% compressed |
| Master System | (a) | 486DX2-66 (Massage-class) | 32-512 KB | ~100% |
| Game Gear | (a), same core as SMS | 486DX2-50 (smaller screen) | 128-512 KB | ~100% |
| Genesis | (a) with hand-written 68000 core (KGen / Genecyst model) | Pentium 90-133 | 512 KB - 4 MB (a few 5 MB) | ~45% as-is, ~70% compressed; rest needs 2 floppies |
| SNES | (a) ZSNES-style asm core; (c) hybrid only for the CPU | Pentium 166-200 MMX (ZSNES); Pentium II for Snes9x-class accuracy | 256 KB - 4 MB (a few 6 MB) | ~25% as-is, ~45% compressed; most need 2-3 floppies or a refusal |
| WideMess | not worth it (our own fantasy console; would need a SNES-plus core) | Pentium II+ | - | - |
| N64 | out of scope (no DOS N64 emulator ever ran games at speed; carts 4-64 MB) | - | - | ~0% |

Recommendation: **NES first** (Game Boy a close second). Smallest proof of concept: one homebrew NROM/MMC3 game (our CTW VRUN NES MMC3 build) in a tiny DOS NES emulator on the VRUN-DOS floppy, booted headless in the BrokenNes x86 console.

The "share that fits" numbers are rough. They come from typical size spreads in No-Intro sets, worked out by hand, not from a scan of a real set. A size scan of the user's own roster would give exact numbers (step 1 of the plan below).

## 1. Approach per console

### The three choices
- **(a) Bundled emulator.** One DOS emulator per console, a few tens of KB, with the ROM next to it. This is proven technology: every console here had a DOS emulator in 1996-2000 that ran at full speed on period PCs. The risk is low, it works with every game, and the per-game work is zero.
- **(b) Static + dynamic recompilation to x86 (Nested-style).** It runs faster, but Nested's own HANDOVER shows the cost: a per-game climb (indirect jumps, self-modifying code, timing-sensitive raster tricks, mapper IRQs). For 6502 / Z80 / SM83 code the speed gain does not matter, because a 486 already runs those emulators at full speed. The time goes into the picture and sound chips, not the CPU.
- **(c) Hybrid.** A recompiled CPU with an emulated PPU/VDP. It only pays off where the CPU is the bottleneck on the target PC: the SNES 65816 on a 486/Pentium 60, and maybe the Genesis 68000 on a 486.

Conclusion: (a) everywhere for version 1. Keep (c) as a later "slow PC" mode for SNES/Genesis only. Pure (b) is not worth it for DOS. (Nested makes sense on the SNES because the SNES can't emulate a NES. A PC can.)

### Period data (published requirements; check the pages before quoting them anywhere)
- **NESticle** (Bloodlust Software, 1997): its readme recommends a 486DX2-66 or better and says a Pentium is best. It is assembly-heavy and uses Mode X / VESA. Zophar's Domain NES DOS section: https://www.zophar.net/nes.html
- **NESten** (Windows era, Pentium). **LoopyNES** (DOS): Pentium-class for full speed with accurate timing.
- **ZSNES** (DOS, x86 asm): original docs list a 486DX4-100 as the minimum (slow) and recommend a Pentium 200 MMX for full speed. https://www.zsnes.com (docs/readme), mirror on Zophar SNES page https://www.zophar.net/snes.html
- **Snes9x DOS** (C++ with asm cores): the readme recommends a Pentium 166+ and says the sound DSP needs a Pentium II for good quality. https://www.snes9x.com
- **Genecyst** (DOS, 1997, asm Starscream 68000 core): Pentium 90-133 for full speed with sound. https://www.zophar.net/genesis.html
- **KGen** (DOS): Pentium 100-ish. **DGen** (later, Linux/DOS ports).
- **Massage** (DOS SMS/GG, Marat Fayzullin-era Z80 cores): runs on a 486. **MEKA** (DOS, Allegro): its docs recommend a Pentium. https://www.smspower.org/emulators/
- **Virtual GameBoy (VGB-DOS)** (Marat Fayzullin): 486DX-33 and up. **no$gmb** (DOS): 386/486-class for DMG, more for GBC. https://www.zophar.net/gb.html , https://problemkaputt.de/gmb.htm

These are the authors' own figures. They show that a 486DX2-66 is enough for the 8-bit consoles and a Pentium 100-200 for the 16-bit ones. The user's slowest target PC for VRUN-DOS is a **386** (decision 2026-11-07). Only NES/GB/SMS at reduced frame rate (frame skip 1-2, no sound mixing) are plausible on a 386DX-40. Tell the user plainly: NES/GB/SMS/GG want a 486, Genesis/SNES want a Pentium.

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

Reuse: Vaults DOS already has an FM probe, its own ISR stack and AdLib playback, and our x86 console has a PC speaker plus OPL2 (ymfm). What we lack: **there is no Sound Blaster DSP/DMA in the x86 console yet**. That blocks headless PCM testing. Adding SB16 or SBPro emulation (DSP ports 22xh, 8237 DMA, IRQ 5/7) to the x86 console is a prerequisite for every console except a speaker/OPL proof of concept.

## 3. The floppy budget

1.44 MB = 1,474,560 bytes. FAT12 overhead ~17 KB (from our mkfloppy.py: 16 root entries, contiguous files). The VRUN-DOS boot sector, stage 2 and screen take a few KB. The UPX'd FreeDOS kernel is 46 KB. COMMAND.COM fallback ~66 KB (it could be dropped for these images: SHELL=the emulator). Emulator runtime: ~40-120 KB compressed. Save file: 8-64 KB.
**Room for the ROM: ~1.25-1.30 MB** (more if we drop COMMAND.COM).

Typical ROM sizes and what fits (rough, by eye from No-Intro sets):
- **NES** 24 KB (NROM) to 512 KB (most), a few 1 MB (e.g. Kirby's Adventure is 768 KB, some MMC5 carts 1 MB). Nearly all fit as-is.
- **GB** 32 KB - 1 MB, a few 2 MB. **GBC** 1-4 MB (Pokemon Gold/Silver 2 MB, Crystal 2 MB, many late carts 4 MB). With compression, most 2 MB GBC carts fit.
- **SMS/GG** 32-512 KB (a handful 1 MB). Everything fits.
- **Genesis** 512 KB - 2 MB common; 2-4 MB late games (Sonic 3 2 MB, Sonic & Knuckles locked-on 4 MB, Street Fighter II SCE 5 MB with its own mapper). About half fit raw; 2 MB ones usually fit compressed.
- **SNES** 512 KB - 4 MB, with many at 1.5-3 MB (SMW 512 KB fits; Link to the Past 1 MB fits; Chrono Trigger 4 MB, Tales of Phantasia 6 MB do not). Chip carts (SA-1, SuperFX) add CPU cost too.

**Compression:** a ROM is decompressed into RAM once at boot (DOS/32A gives flat memory; 4 MB of RAM is needed for a 4 MB ROM, and these PCs had 4-16 MB). Choices:
- **SC/32A**, which we already use, compresses the EXE only.
- **LZ4 / LZSA** decompress at ~20-40 MB/s on a Pentium and ~2-5 MB/s on a 486 (byte copies). That is under a second for 2 MB. Ratio ~1.3-1.6x on ROMs.
- **aPLib / ZX0 / LZMA** give better ratios (1.6-2x on ROMs; graphics-heavy ROMs compress well, already-compressed data poorly) but run slower: LZMA ~1 MB/s on a 486, a few seconds for 2 MB. That is acceptable at boot because floppy reads dominate (our model: ~26 s for 1.2 MB at 386 speed).
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

**Headless test:** the BrokenNes x86 console boots FreeDOS images (`--x86boot --until/--shotat/--ips`). At 130M ips with the JIT, it runs Vaults at ~3x real time. A 486DX2-66 is roughly 30-50M instr/s, so we can set `--ips` to model the target PC and check "full speed at 486 level" by counting the emulator's own frame counter. Golden tests: screenshot hashes at fixed frames compared with the same ROM in BrokenNes's native core (differential, like the Mesen checks). This is the big win: the pipeline checks itself. Gaps: SB DSP (see above); the gameport isn't emulated; disk timing isn't modelled in the x86 console (use DOSBox-X + tools/floppymodel.py from the bootloader round for boot-time estimates).

**Real hardware:** the user's period PC (VRUN-DOS already "works beautifully" there). Check the 36-sector cross-head read, sound card detection and save writes.

**Input:** keyboard by default (an INT 9 handler of our own, as in Vaults; arrows + Z/X/A/S + Enter/RShift), remappable in the config. Gameport joystick (port 201h): 2-4 buttons, analog axes polled by timing. Fine for NES/SMS/GB (2 buttons). Genesis 3-button and SNES 6-8 buttons need keyboard plus joystick together, or a Gravis GamePad Pro (GrIP protocol), which is more work later.

**Save RAM:** at the user's pause, or on exit, write battery RAM to `A:\GAME.SAV` (the floppy is writable; the VRUN-DOS cache drops on writes, which is already handled). Use a fixed-size, preallocated file so the write never changes the FAT (safer on bad media); Vaults does this already per the playbook. writefloppy.ps1 already keeps a save when rewriting. For multi-disk sets, the save lives on disk 1, so the user is prompted to reinsert it.

## 5. Recommendation and risks

**First: NES.** It has the best period proof (NESticle at 486DX2-66), nearly the whole library fits on one disk, the palette is exact, and we have the deepest accuracy tooling (FIX cores, Mesen diffs, the Nested certification work). Our own VRUN CTW NES MMC3 ROM is the obvious first game (no licensing question). **Game Boy** is a strong second. gnuboy is already ported for Nano GB and it is the lightest emulator; the 4-colour picture is trivial.

**Smallest proof of concept:**
1. Port a small C NES emulator (InfoNES-class, or a lean C rewrite of the FIX core: CPU + PPU, mappers 0/1/2/4) to Open Watcom + DOS/32A. Use Mode X 320x240, keyboard only, PC speaker or OPL2 square approximation for sound (both already emulated in our x86 console, so headless tests work today).
2. Build a VRUN-DOS image with VRUN CTW NES MMC3 as the game.
3. Boot it headless in the x86 console at a 486-like `--ips`, take a screenshot hash at frame N, and compare it with BrokenNes NES at frame N.
4. Then do the BrokenDev command wrapper, compression, saves, SB PCM.

**Biggest risks:**
1. **Speed on the stated 386 floor.** Period data says 486 for 8-bit and Pentium for 16-bit. The 386 target set for VRUN-DOS does not carry over. We need the user's decision on the minimum PC per console.
2. **Sound Blaster is missing from the x86 console.** Without it, PCM audio (needed for 2A03 quality, SPC700, YM2612 software FM) cannot be tested headless.
3. **SNES / Genesis size and speed together:** many don't fit and those that do need a Pentium. SNES colour math doesn't fit a 256-colour palette. Expect "SNES to DOS" to cover only a slice of the library.
4. **Licences:** reusing period or open emulators brings GPL and non-commercial terms (Snes9x, MAME-derived code). Our own cores avoid that but cost far more work.
5. **Accuracy drift:** a lean DOS core is less accurate than FIX. Without the differential frame-hash tests some games will silently glitch, so make the golden comparison part of the pipeline from day one.

## Files I'd want (not downloaded; each needs the user's approval)
- InfoNES or LiteNES source (NES proof-of-concept core), from its official repository, pinned commit.
- SMS Plus source (Charles MacDonald), for SMS/GG.
- Optionally ZX0 (Einar Saukas) reference compressor + 8086/386 decompressor source, pinned release.
- NESticle / ZSNES DOS readmes, only to quote exact requirements (reading the web pages is probably enough).
gnuboy, Open Watcom, DOS/32A, NASM, UPX and FreeDOS are already in C:\BrokenNes-tools.
