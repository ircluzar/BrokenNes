# Nested "fantasy" flavors: research (2026-11-14, second pass)

Research only. No code was changed and nothing was downloaded. The user called these "fantasy goals": worth trying, but no major success expected.
The "realistic" Sega flavors (GG->SMS, SMS/GG->Genesis) are covered by the sibling report `docs/projects/nested-everything-research.md`, so this file does not repeat them.
Target rule: plain SNES (no SA-1 / SuperFX / FXPak). Notes marked **[outside the rule]** say where a coprocessor would change the answer.

How to read the labels:
- **[measured]**: from our own code or our own test runs (file named).
- **[sourced]**: from a web page (linked).
- **[estimate]**: my arithmetic or judgement, not checked by a run.

## Summary

| Rank | Flavor | Guest CPU -> host CPU | Score (1-5) | Best case | Hardest blocker | First proof of concept |
|---|---|---|---|---|---|---|
| 1 | Nested GB | SM83 ~1.05 M machine cycles/s -> 65816 2.68-3.58 MHz | 2 | Slow, HALT-heavy DMG games (Tetris, puzzle games, RPG menus) | CPU: the SNES has *less* speed headroom than Nested already has for the NES, and Nested still lags there | `cpu_instrs.gb` sub-tests, then Tetris |
| 2 | Nested GGSNES | Z80 3.58 MHz -> 65816 | 2 | Same as SMS but a 160x144 picture (easier) | Z80 speed + reshuffling 4bpp tiles on every VRAM write | `VDPTEST.sms`-style checks in GG mode, then Columns GG |
| 3 | Nested SMSNES | Z80 3.58 MHz -> 65816 | 1.5 | Slow single-screen games | Same as GG, plus a 256x192 picture and line-IRQ splits | `VDPTEST.sms`, then Columns |
| 4 | Nested NesGen | 6502 1.79 MHz -> 68000 7.67 MHz | 2 (CPU) / 1 (video+audio) = **1.5** | NROM games: proven for SMB1 by a 2010 hand-assisted port | Video: CHR banking and $2007 byte writes onto VRAM that is only open in VBlank; 2A03 sound has no good home | Donkey Kong or Nuts & Milk (NROM), static translation |

**Try first: Nested GB.** It reuses the most of Nested's machinery, its tile format copies across unchanged, and all the test ROMs it needs are already on disk. It is also the one where a speed measurement answers "is this possible at all" fastest.

**Changes from the first pass.** GB went down from 2.5 to 2, because we now have measured evidence that Nested lags at a 2:1 clock ratio. NesGen gained real prior art (a 2010 SMB1 port and a 2012 mapper-0 emulator). Nothing turned up for GB, SMS or GG running on the SNES.

---

## 0. What our own Nested fork tells us (measured)

Sources:
- `C:\BrokenNes-work\nested_compat\HANDOVER.md`
- fork `...\Nested_VRUN\Project-Nested-core\Assembly\Project\` (`Recompiler.asm`, `StaticRec.asm`, `Interpreter.asm`, `SelfMod.asm`, `IO.asm`, `Hdma.asm`)

**How Nested works.** It combines:
- JIT recompilation on the SNES itself, with an ahead-of-time static pass;
- an interpreter fallback for code it cannot recompile;
- a self-modifying-code module (`SelfMod.asm`);
- feedback profiles: a list of known entry points fed back from a run (our `nested-build -Feedback brokennes`).

**How it handles the PPU.** It does not draw pixels.
- NES register writes are recorded per scanline and replayed with SNES HDMA buffers (`Hdma.asm`: `Hdma__UpdateScrolling`, up to 3 CHR sets per frame per HANDOVER).
- Sprite-0 hit is faked. A `$2002` read returns "hit" once the guessed scanline is reached, and the scanline is estimated from SNES time (`IO.asm` lines ~120-225, `IO__r2002_SaveScanline`, `_Sprite0Line`).
- Per-tile CHR latches (mapper 9) have no hook (HANDOVER).

The same design would carry over to GB STAT/LY effects and SMS line interrupts.

**Clock headroom it has.**
- NES 6502 = 1,789,773 Hz [measured: `Windows/NesEmulator/apus/APU.cs`].
- SNES 65816 = 21.477 MHz master / 6 (FastROM banks and I/O at $2100-$3FFF, $4200-$5FFF) or / 8 (WRAM, SlowROM) [measured: `snes/BOARD_SFC.cs` `TimedAccessClocks`]. That is 3.58 or 2.68 MHz, so Nested has **1.5-2x** the guest clock.

**What it gets with that headroom** [measured, HANDOVER, fast sweep m8 of 53 games]:
- Perfect 5, Playable 22, Glitchy 5, Boots 2, Black 2, Unsupported 17.
- Heavy lag even on supported games: Metroid 1194 lag frames vs 23 on the NES; MM4 578 vs 50; MM5 1167 vs 681.

**Lesson for every flavor below:** a guest that needs more host cycles per guest cycle than the NES does will lag *more* than this.

---

## 1. Nested GB (Game Boy -> SNES, a software Super Game Boy)

**CPU.**

*Instruction-set fit.* SM83 has A, F, BC, DE, HL, SP and PC, with flags Z N H C. The plan:
- Keep the GB registers in direct page.
- Map Z and C straight onto the 65816. C needs inverting on subtract: 6502/65816 carry means "no borrow", SM83 C means "borrow".
- Keep N and H lazily (only DAA and PUSH AF read them).

*Budget.*
- Per cycle: the GB CPU runs at 4,194,304 Hz, which is about 1.05 M machine cycles/s [sourced: Pan Docs]. The SNES has 2.68-3.58 MHz [measured], so about **2.5-3.4 host cycles per guest machine cycle**. Nested on the NES has 1.5-2 host cycles per NES cycle [measured], but one 6502 cycle does far less work than one SM83 machine cycle (4 clocks).
- Per instruction: SM83 instructions average roughly 2 machine cycles [estimate], which gives **~5-7 host cycles per guest instruction**. A 6502 instruction averages ~3 cycles, which gives Nested ~5-6 host cycles per NES instruction [estimate].
- So the budget per instruction is about the same as Nested's, but SM83 instructions do more: 16-bit register pairs, `(HL)` pointer accesses that need a region check, and `LDI A,(HL+)`. **Expect worse lag than Nested on the NES** [estimate]. Metroid-class lag would be typical, and full speed only for games that idle in HALT.
- Note: the real SGB runs its GB CPU about 2.4% fast on NTSC [sourced: Pan Docs SGB].

*Memory.* The GB map (fixed ROM, banked ROM, VRAM, cart RAM, WRAM, OAM, I/O, HRAM) means every `(HL)` / `(BC)` / `(DE)` access is a runtime range check. This is the same class of problem as Nested's `(zp),Y` I/O stores, which needed core steps 21/23 [measured, HANDOVER].

*Banking and self-modifying code.*
- MBC1/3/5 banks map to fixed SNES banks: one 16 KB GB bank per SNES bank, with the recompiled code at a known address.
- Almost every game runs the OAM-DMA routine from HRAM ($FF80). Pattern-match it into a single SNES DMA.
- Other RAM-executed code goes to the interpreter / `SelfMod.asm` path.

**Video.**
- **Tiles:** GB 2bpp tiles are in the same layout as SNES 2bpp tiles, and the SGB relies on this [sourced: Pan Docs SGB]. BG tiles copy unchanged.
- **Screen:** 160x144 centred in 256x224 with a border [sourced: Pan Docs SGB].
- **Window (WX/WY):** a second BG plus a window mask or HDMA.
- **STAT/LY effects and mid-frame SCX writes:** record and replay with HDMA, exactly as Nested does for the NES (`Hdma.asm`) [measured design].
- **Sprites:** 40 per frame, 10 per line, on SNES OBJ (128, 32 per line) [estimate: comfortable]. SNES sprites are 4bpp only, so sprite tiles need expanding 2bpp->4bpp on upload.
- **Palettes:** DMG only; CGB double speed is out of scope.

**Audio.** GB APU = 2 pulse, 1 wave, 1 noise. On the SPC700, pulses and wave become looped BRR samples and noise uses the S-DSP noise generator. This needs a new SPC driver, but Nested already ships an SPC driver for the 2A03 (`Assembly/Project/Spc700/`), so the mechanism exists.

**Prior art.**
- The **real Super Game Boy** contains a full GB system-on-chip and an ICD2 bridge, not a translator. The SNES only shows the picture and border [sourced: https://gbdev.io/pandocs/SGB_Functions.html].
- **No GB emulator or recompiler running on SNES hardware was found** in searches of the web, gametechwiki and GitHub [sourced: no hits]. SGB emulation exists only *inside PC SNES emulators* (bsnes with SameBoy, Mesen-S) [sourced: https://github.com/byuu/bsnes/wiki, https://docs.libretro.com/library/bsnes/].
- **Static SM83 recompilers exist for PC targets:**
  - **gb-recompiled** lifts SM83 to C with a small fallback interpreter for indirect jumps. The author reports ~98% of the catalogue *compiles*, but compiling is not the same as running: many games glitch [sourced: https://retrohandhelds.gg/game-boy-roms-go-native-with-new-static-recompiler/, https://raw.githubusercontent.com/sp00nznet/pokemon-gold/HEAD/README.md].
  - JIT: **jitboy** [sourced: https://github.com/sysprog21/jitboy].
  - Both prove the control-flow side is tractable. Neither targets a slow host.
- **[outside the rule]** An SA-1 (10.74 MHz 65C816) would triple the CPU budget. That would plausibly put most DMG games in reach and lift the score to ~3.5.

**Verdict: 2/5** (was 2.5).
- Best case: Tetris, Dr. Mario, puzzle games and turn-based RPGs, at or near full speed thanks to HALT skipping. Action games lag.
- Hardest blocker: CPU speed (proven tight by our own Nested lag numbers).
- **Proof of concept, ordered by what it teaches:**
  1. Recompile and run the individual `cpu_instrs` sub-tests (already on disk). This gives correctness plus a measured host-cycles-per-guest-instruction number from the BrokenNes SNES core's cycle counter.
  2. Tetris (32 KB, no MBC, HALT-driven): title screen, a falling piece, music.
  3. `dmg-acid2.gb` (on disk) for the window, sprites and palettes.

## 2. Nested SMSNES (Master System -> SNES)

**CPU.**

*Instruction-set fit.*
- Z80 has two register sets (EXX / EX AF), IX/IY with displacement, I, R, IM 1 + NMI, and a separate IN/OUT port space.
- It has block ops: LDIR and OTIR, the latter the standard VDP upload loop.
- Flags S Z H P/V N C. Parity needs a 256-byte table.
- IX/IY+d maps well onto 65816 `d,X` addressing [estimate].

*Budget.*
- Z80 at 3.58 MHz NTSC [sourced: https://copetti.org/writings/consoles/master-system], about the same as the SNES FastROM clock [measured]. So the ratio is **~1:1 host cycle per guest T-state**.
- Z80 instructions average ~8-10 T-states, which gives ~8-10 host cycles per guest instruction [estimate]. Roughly Nested's budget again, with heavier instructions (16-bit ALU, block ops).
- OTIR/LDIR must be pattern-matched into "convert + DMA" routines.

*Banking.* The Sega mapper ($FFFC-$FFFF, three 16 KB slots, first 1 KB fixed) and the Codemasters mapper map to SNES banks like the GB MBCs.

**Video.**
- **Tiles:** SMS Mode 4 tiles store 4 bitplanes interleaved per row (4 bytes per row) [sourced: SMS Power VDP docs, https://www.smspower.org/Development/]. SNES 4bpp stores planes 0/1 for 8 rows, then planes 2/3. Every tile write therefore needs a reshuffle (table-driven, about 250-300 host cycles per tile [estimate]). VRAM is 16 KB [measured: `sms/SmsVdp.cs`].
- **Name table:** entries carry palette, priority and flips, close to the SNES tilemap format.
- **Palettes:** 2x16 colours, 6-bit -> 15-bit.
- **Screen:** 192 lines of picture [measured: `SmsVdp.cs`, 224/240 extended modes exist], which fits in 224.
- **Sprites:** 64 per frame, 8 per line, fine on SNES OBJ.
- **Line interrupts and the top-row / right-column scroll locks:** SNES H-IRQ / HDMA, Nested-style.
- **Left-column blank:** SNES window.

**Audio.** SN76489 (3 squares + noise) onto the SPC700 with looped BRR squares and DSP noise. Simple. Our own SN76489 port (shared Sega PSG, sega-cores plan phase 0) is the reference. The YM2413 FM unit is out of scope.

**Prior art.**
- **No SMS/GG emulator or Z80 translator running on the SNES was found** [sourced: no hits; https://emulation.gametechwiki.com/index.php/Master_System/Game_Gear_emulators lists only PC/handheld hosts].
- Searches also found no Z80->65816 static recompiler.
- The Genesis runs SMS natively; that belongs to the sibling report.
- **[outside the rule]** SA-1 would help a lot. SuperFX does not (it is poor at general bus work) [estimate].

**Verdict: 1.5/5.**
- Best case: Columns, card and puzzle games, slow RPG screens.
- Hardest blocker: CPU at about 1:1 plus the per-tile reshuffle on every VDP write.
- **Proof of concept:** `VDPTEST.sms` (on disk) to prove the VDP translation, then Columns.

## 3. Nested GGSNES (Game Gear -> SNES)

Same CPU, VDP and PSG as the SMS, so section 2 applies, with these differences:
- Only a 160x144 window of the 256x192 picture is shown [measured: our SMS/GG VDP handles both]. Off-screen tile and sprite work can be skipped, and an SGB-like border fits.
- Colour is 12-bit, which converts trivially.
- The stereo PSG register maps to SPC voice pan for free.
- Small-screen games use fewer raster tricks [estimate].

Build GG and SMS as one Z80 front end with two VDP profiles; that is why GG ranks above SMS.

Prior art: none found on the SNES (same searches).

**Verdict: 2/5.** Proof of concept: `VDPTEST.sms` run in GG mode, then Columns GG, sharing the SMS proof's Z80 core.

## 4. Nested NesGen (NES -> Genesis)

**CPU: the easy part.**
- Clocks: 68000 at ~7.67 MHz [sourced: https://www.copetti.org/writings/consoles/mega-drive-genesis] vs the 6502 at 1.79 MHz [measured], so **~4.3 host cycles per guest cycle**. That is the best ratio of the four, but 68000 memory instructions cost 8-16 cycles [estimate].
- Registers: A/X/Y go in data registers, NES RAM sits behind a base register, and `move.b d(a5,d0.w)` covers zp,X.
- Banking: the 68000's flat 4 MB cart space holds every PRG bank; a mapper write retargets a dispatch table.
- Carry: 6502 SBC carry is inverted. A 2012 porter said exactly this blocked most of his hand ports: only Nuts & Milk worked well, because it "doesn't abuse of the carry flag" [sourced: SpritesMind, below].

**Video: the wall.**
- **Tiles:** NES 2bpp planar becomes Genesis 4bpp packed. CHR-ROM tiles can be converted at build time. CHR-RAM tiles have to be converted on every `$2007` write, and a 2010 thread named single-byte PPU VRAM updates as the hard part [sourced: SpritesMind t=755].
- **CHR banking (MMC3 1 KB banks) means re-uploading VRAM.** Our VDP model has 16 free slots per drawn line in H32 and 167 per blanked line [measured: `md/MdVdp.cs` slot table]. With ~38 blanked lines per NTSC frame that is roughly **6 KB of VRAM writes per frame** [estimate from the measured slot counts]. During drawing there is almost nothing (16 slots per line), so mid-frame CHR swaps for status bars cannot be uploaded live. They need pre-converted duplicate tile sets.
- **Attributes:** the NES picks a palette per 16x16 area, the Genesis per 8x8 tile, so each attribute expands into tilemap entries (fine).
- **Screen:** 256x240 becomes H32 at 256x224 (the same cut as Nested on the SNES). The 2010 SMB1 port used H32 [sourced].
- **Sprite 0:** emulate it like Nested's `$2002` trick.
- **MMC3 IRQ:** Genesis HInt, which is a good fit [sourced: mic_'s advice in the 2012 thread].

**Audio.**
- 2A03 = 2 pulse with variable duty, triangle, noise, DPCM. The Genesis PSG has 50% squares only and no triangle; one forum post notes the adjustable pulse width as a problem [sourced: SpritesMind "How hard..." thread].
- The realistic route is YM2612 FM patches for the pulses and triangle, plus the DAC through a Z80 sample driver for DPCM. The 2010 SMB1 port used the YM2612 [sourced]. The user already has YM2612 cores to prototype against [measured: BrokenNes Genesis cores].

**Prior art (real, this time).**
- **Super Mario Bros. on Mega Drive (2010).** An "automatic code translation" of the commented SMB disassembly, with the PPU and I/O emulated in H32 and FM music. Described as "pretty damn accurate" with sprite-limit, Cheep-cheep and coin-colour glitches. Reported not to boot on real hardware at the time. [sourced: https://gendev.spritesmind.net/forum/viewtopic.php?t=755]
- **Mairtrus's NeMul (2012).** A general NES interpreter on the Genesis running most mapper-0 games at **8-10 fps** (Pac-Man and Galaga failed). Ported to Sega CD at the same speed. Reports of failures on real hardware. He also hand-ported several disassembled games; only Nuts & Milk worked well. [sourced: https://gendev.spritesmind.net/forum/viewtopic.php?p=15977]
- **General discussion.** Shiru and others say interpretation is too slow and that translation plus manual hardware rewrites is the way [sourced: https://gendev.spritesmind.net/forum/viewtopic.php?p=920].
- Unverified forum claim: an old Yuji Naka NES emulator for the Genesis. Treat it as folklore.

So the interpreter route is measured dead (8-10 fps), and the translation route has worked for one NROM game.

**Verdict: 1.5/5.** CPU 2/5 (prior art proves it), video 1/5 beyond NROM, audio a real project.
- Best case: NROM and CHR-ROM games with at most one status-bar split (Donkey Kong, Balloon Fight, Nuts & Milk, SMB1 per the 2010 port).
- **Proof of concept:** Donkey Kong or Nuts & Milk (NROM, CHR-ROM, no scroll, no sprite 0) with pre-converted tiles and PSG-only sound. This teaches the PPU layer without banking. Comparing against the 2010 SMB1 result is a free benchmark.

**Proof of concept built (2026-11-14, branch `nested-nesgen-poc`): Donkey Kong. All numbers below are [measured]** on BrokenNes's Genesis core headless (`--md-run`), against the NES on the FIX cores (`--romtest`), with `BrokenDev/tools/nested-everything/nes2md_test.py`.
- **Method.** A static 6502 -> 68000 translator (`nes2md.py`, `brokendev.ps1 nested-everything nes2md`). Recursive descent from the vectors finds all 6,012 instructions of Donkey Kong; there are no indirect jumps, and the dynamic fallback (a dispatch miss stops the ROM and the feedback pass adds the address) never fired in 1,100 frames. JSR/RTS are native 68000 calls with a stack check, so stack tricks fall back to a 32 K dispatch table. The NMI runs from the VInt. The 8 KB of CHR-ROM is converted once at build time. Nametable writes go to plane A (H32, the top 8 lines cropped), attributes to palette bits, `$4014` to the sprite table, and the 2A03 to the PSG.
- **How far it gets.** It boots, shows the title, runs the attract demo, and starts and plays a game from the pad (Start, walk, jump). Its pictures match the NES's black/non-black layout at 99.3-100% at the check frames, allowing for its ~30-frame later boot. Sound plays (PSG; pulse duty, sweep and DMC are missing): around -19 to -30 dB RMS, against the NES's -27 dB.
- **CPU cost.** **30.5-32 68000 cycles per 6502 instruction** (a count build gives the instructions, a normal build the NMI length from the V counter). At an assumed ~3 6502 cycles per instruction [estimate], that is ~10 Genesis cycles per NES cycle, against the 4.3 clock ratio.
- **Frame time.** Donkey Kong's NMI runs 2,200 (play) to 3,300 (demo) 6502 instructions. That takes **143 lines (55%) of the 262-line frame in play and 207 lines (79%) in the demo**. 37 of the first 1,000 VInts are lost, almost all while booting, when the screen-drawing NMIs run up to 492 lines.
- **What it took.** Three fixes mattered. Stack pushes must move S before writing, so a VInt between the two 68000 instructions cannot overwrite the byte (that was a real crash). The plane behind must point at a blank tile. Native JSR/RTS took the cost from 36.5 to 30.5 cycles per instruction.
- **Verdict on the PoC.** Donkey Kong plays at full speed. The CPU cost (~2.3x the clock ratio) leaves headroom only for light NROM games. A heavier game such as SMB1 would need the 68000 code to be optimised further, or it would lag in busy scenes.

---

## Ranking (most to least likely to work)

1. **Nested GB.** The picture path is already proven (SGB, tile formats match), it has the most Nested reuse, the test ROMs are on disk, and the CPU speed question is quickly measurable.
2. **Nested GGSNES.** The shared Z80 front end proven on the smaller screen.
3. **Nested SMSNES.** GG plus a bigger picture and more raster tricks.
4. **Nested NesGen.** It has the only real prior art and the easiest CPU, but beyond NROM the video banking and sound are the hardest translation problems of the four.

NesGen and GB are close. If the user's interest is "something that actually plays", NesGen limited to NROM games has the best-documented odds. If the interest is "the most universal adapter", GB is it.

**First step:** recompile `cpu_instrs.gb` sub-test 06 (`ld r,r`) or 01 (`special`) to SNES, run it on BrokenNes's SNES core, and read the host cycles per guest instruction. Over ~20 host cycles per guest instruction would mean GB is limited to HALT-heavy games.

## Test ROMs: already on disk (nothing to approve)

- GB CPU: `C:\Users\philt\OneDrive\Documents\PROJECTS\BrokenNes\Windows\Resources\gb-test-roms\blargg\cpu_instrs\cpu_instrs.gb` (the folder also holds the individual sub-tests, from c-sp/game-boy-test-roms v7.0)
- GB PPU: `C:\Users\philt\OneDrive\Documents\PROJECTS\BrokenNes\Windows\Resources\gb-test-roms\dmg-acid2\dmg-acid2.gb` (+ reference PNGs)
- SMS VDP: `C:\BrokenNes-tools\sega\test-sets\sms-roms\smsvdptest-1.31\VDPTEST.sms` (+ ReadMe)
- Genesis VDP FIFO tests (for NesGen upload timing): `C:\BrokenNes-tools\sega\test-sets\md-roms\VDPFIFOTesting.zip`

Optional, not on disk, would need approval:
- The NeMul.bin emulator from the 2012 thread (http://www.fileden.com/... ; the host is long dead).
- The 2010 SMB1 Mega Drive port.
Both are only for comparison.

## Sources

- Pan Docs, SGB: https://gbdev.io/pandocs/SGB_Functions.html
- SMS Power development docs: https://www.smspower.org/Development/
- Copetti, Master System: https://copetti.org/writings/consoles/master-system ; Mega Drive: https://www.copetti.org/writings/consoles/mega-drive-genesis
- Project Nested: https://github.com/Myself086/Project-Nested ; https://emulation.gametechwiki.com/index.php/Project_Nested
- SpritesMind: SMB1 port https://gendev.spritesmind.net/forum/viewtopic.php?t=755 ; NES on Sega https://gendev.spritesmind.net/forum/viewtopic.php?p=15977 ; NES/SMS emulator feasibility https://gendev.spritesmind.net/forum/viewtopic.php?p=920
- gb-recompiled: https://retrohandhelds.gg/game-boy-roms-go-native-with-new-static-recompiler/ ; jitboy https://github.com/sysprog21/jitboy
- bsnes SGB: https://github.com/byuu/bsnes/wiki ; https://docs.libretro.com/library/bsnes/
- SMS/GG emulator list: https://emulation.gametechwiki.com/index.php/Master_System/Game_Gear_emulators
