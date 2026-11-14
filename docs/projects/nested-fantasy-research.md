# Nested "fantasy" flavors: research (2026-11-14)

Research only. No code was changed and nothing was downloaded. The user's frame: "fantasy goals", worth trying, no major success expected.
The "realistic" Sega flavors (GG->SMS, SMS/GG->Genesis) are covered by the sibling report `docs/projects/nested-everything-research.md`; this file does not repeat them.
Target rule: plain SNES (no SA-1 / SuperFX / FXPak). Notes marked **[outside the rule]** say where a coprocessor would change the answer.

## Summary

| Rank | Flavor | Guest -> host | Score (1-5) | Best case | Hardest blocker | First proof of concept |
|---|---|---|---|---|---|---|
| 1 | Nested GB | SM83 4.19 MHz -> 65816 ~3.58 MHz | 2.5 | Simple, tile-scrolled GB games (Tetris, puzzle, slow RPG menus) at full speed; mono-palette, SGB-style border | CPU: the host is *slower* than the guest, and every SM83 memory access needs a map check | Tetris (32 KB, no MBC) or a tiny homebrew test ROM drawing a scrolling BG |
| 2 | Nested GGSNES | Z80 3.58 MHz -> 65816 | 2 | Same as SMSNES but easier picture (160x144 fits inside 256x224 with room to spare) | Z80 flags/registers + 4bpp planar tile conversion on every VRAM write | A Sega Mapper GG game with simple BG + few sprites (e.g. Columns GG) |
| 3 | Nested SMSNES | Z80 3.58 MHz -> 65816 | 1.5-2 | Slow single-screen games; most action games drop frames | Same as GG, plus 192 lines and line-IRQ split screens, 64 sprites with 8/line | Columns (SMS) or a VDP test ROM |
| 4 | Nested NesGen | 6502 1.79 MHz -> 68000 7.67 MHz | 1.5 | CPU is the *easy* part here; NROM games with no mid-frame tricks could run | Video: Genesis VDP has no NES-style attribute model, CHR banking must become VRAM uploads over a slow DMA budget, and the 2A03 sound has no good home (YM2612 is FM, PSG has no triangle/DPCM) | Donkey Kong / Balloon Fight (NROM), static recompile with BG via VRAM DMA |

**Try first: Nested GB.** It has the strongest prior art (the real Super Game Boy is literally "GB game on a SNES"), the smallest screens and tile format (2bpp converts to SNES 2bpp directly, no plane shuffling), and reuses the most of Nested's existing machinery (static recompile + runtime fallback, mapper hooks, feedback profiles). It will still be slow; the learning goal is "how far below 1:1 speed does a recompiled SM83 land".

---

## 1. Nested GB (Game Boy -> SNES, software Super Game Boy)

**CPU.** SM83 is a cut-down Z80/8080 hybrid: A, F, BC, DE, HL, SP, PC; flags Z N H C. The 65816 has only A, X, Y (8 or 16 bit), D, S, DB. Plan: keep the GB registers in direct page (D=$0000 bank, ~10 bytes), A in the 65816 accumulator where possible. Z and C map to 65816 Z/C (C needs inversion on subtraction: 6502 carry = not-borrow, SM83 C = borrow). N and H have no host equivalent; they matter only for DAA, so compute them lazily (record the last op + operands, rebuild H only when DAA or PUSH AF reads it, like Nested's lazy-flag approach).
Clock: GB CPU runs 1.05 M machine cycles/s (4.19 MHz clock / 4); the SNES runs ~3.58 MHz in FastROM banks, 2.68 MHz in WRAM. That is ~2.5-3.4 host cycles per guest M-cycle; a GB instruction averages ~2 M-cycles, so the budget is **~6-7 host cycles per guest instruction**. A 65816 `LDA dp` is 3-4 cycles, so a translated `LD A,(HL)` (16-bit pointer, region check, indexed load) is ~15-25 cycles. Expect roughly **30-50% of real speed** on CPU-heavy games; idle-loop games (wait for VBlank HALT) recover a lot because HALT can skip straight to the next interrupt.
Memory map: $0000-3FFF fixed ROM, $4000-7FFF switchable (MBC1/3/5), $8000-9FFF VRAM, $A000-BFFF cart RAM, $C000-DFFF WRAM, $FE00 OAM, $FF00 I/O, $FF80 HRAM. ROM reads by fixed address can be resolved at recompile time; `(HL)` / `(BC)` / `(DE)` accesses are runtime, and any of them could hit I/O or VRAM, so each needs a fast range check (same problem Nested solves for `(zp),Y`). MBC bank switches map onto SNES ROM bank changes (put each 16 KB GB bank in its own SNES bank so the code address stays fixed; the recompiled code for bank N lives at a known SNES bank). Self-modifying code: GB games run code from HRAM (the OAM-DMA routine at $FF80 is in almost every game) and sometimes WRAM; Nested already has an interpreter/fallback path for RAM-executed code, and the OAM-DMA stub is so standard it can be pattern-matched and replaced by one SNES DMA.

**Video.** GB BG/window tiles are 2bpp in exactly the SNES 2bpp interleaved format (two bitplanes per row), so tile data copies across unchanged (the real SGB relies on this). Screen 160x144 fits in 256x224 with an SGB-style border. BG map 32x32 + SCX/SCY maps to SNES Mode 0/1 BG with scroll registers. The **window** (WX/WY) is a second layer that starts at a screen position: use a second SNES BG plus a window mask or HDMA. **STAT/LY interrupts and mid-frame SCX writes** (wavy effects, status bars) map to SNES H-IRQ / HDMA, but the GB fires them on its own timeline, which the recompiled CPU (running slower) will not match; the practical route is to record per-line register writes during a frame and replay them with HDMA next frame (one frame latency). Sprites: 40 OAM entries, 10 per line, 8x8 or 8x16; SNES has 128 sprites, 32 per line, 4bpp only, so sprite tiles must be expanded 2bpp->4bpp on upload (cheap table lookup, but every VRAM tile write that may be a sprite tile costs). DMG palettes (BGP/OBP0/OBP1) become 4-colour CGRAM sets; GBC (CGB) needs double-speed CPU and VRAM banks: **out of scope**, DMG only.

**Audio.** GB APU: 2 pulse, 1 wave (32x4-bit), 1 noise. Onto the SPC700: pulse and wave become BRR samples looped at pitch; noise uses the S-DSP noise generator (one shared noise clock, OK for one noise channel). Register writes go over the 4 APU ports to a small SPC driver (Nested already does this for the 2A03, so this is a new driver, not a new mechanism). Envelope and sweep: run in the SPC driver at 64/128 Hz.

**Prior art.**
- The Super Game Boy itself: a real SM83 core (the SGB's ICD2 chip + a GB CPU on the cartridge) feeding the SNES PPU through a line buffer. Hardware, not translation, but it proves the picture path (2bpp tiles, 160x144 in 256x224 with a border). https://gbdev.io/pandocs/SGB_Functions.html
- No known software GB emulator on a stock SNES. The SNES CPU is generally considered too slow for interpretation; static recompilation is the only plausible route.
- Static recompilation of GB code exists on modern hosts (e.g. the "gb-recompiled"/ N64 recomp style projects and the 2020s static-recomp wave), proving the decoding/control-flow side is tractable. https://github.com/N64Recomp/N64Recomp (the general technique)
- **[outside the rule]** SA-1 (10.74 MHz 65C816) triples the CPU budget and would put most DMG games in reach; this is the one flavor where a coprocessor changes the verdict a lot.

**Verdict: 2.5/5.** Best case: puzzle games, Tetris, Dr. Mario, early RPGs, slow platformers at or near full speed with HALT skipping. Hardest blocker: CPU speed (host slower than guest) plus per-access region checks. **Smallest proof of concept:** Tetris (32 KB, no MBC, no window tricks, heavy HALT use) -> boots to title, piece falls, music plays. Before that, a 1-page homebrew test ROM (fill VRAM, scroll BG, read joypad) to measure host cycles per guest instruction.

## 2. Nested SMSNES (Master System -> SNES)

**CPU.** Z80: A F BC DE HL + shadow set AF' BC' DE' HL', IX, IY, I, R, SP; flags S Z H P/V N C; block ops (LDIR, OTIR), IN/OUT port space, IM 1 interrupts + NMI (pause button). Same register-in-direct-page plan as GB, but bigger: IX/IY indexed (`LD A,(IX+d)`) is cheap on the 65816 (direct page or 16-bit X indexed); P/V parity needs a 256-byte table; EXX/EX AF swaps are cheap in DP. Clock: Z80 3.58 MHz, average ~8-10 T-states per instruction -> ~400k instr/s; SNES at ~3.58 MHz gives **~8-9 host cycles per guest instruction**. Slightly better than GB per instruction, but Z80 instructions do more (16-bit math, block copies), so the real ratio is similar: **~30-50%**. OTIR to the VDP data port is the classic VRAM upload loop and must be pattern-matched into a conversion+DMA routine or it will be hopeless.
Banking: Sega mapper ($FFFC-$FFFF, 16 KB slots 0-2, slot 0's first 1 KB fixed) maps to SNES banks like MBCs above. Codemasters mapper is similar. RAM-executed code is rare.

**Video.** SMS VDP Mode 4: tiles are **4bpp planar with 4 planes interleaved per row (bytes p0 p1 p2 p3)**; SNES 4bpp is planes 0/1 interleaved for 8 rows then planes 2/3. So every tile write must be reshuffled (a 4-byte row -> 2 split word writes; table-driven, ~30-40 cycles per row, ~250-300 per tile). Name table 32x28 with per-tile palette select, priority, H/V flip: maps well to SNES tilemap entries (very close format, just bit positions). Two 16-colour palettes (BG, sprites; sprites may use BG palette 2nd half): SNES CGRAM, 6-bit RGB (2-2-2) -> 15-bit, easy. Screen 256x192 fits in 224 lines with borders. Sprites: 64, 8 per line, 8x8/8x16, single palette; SNES 128 / 32 per line handles it (no flicker emulation needed; games that rely on the 8-limit to hide things will show more). **Line interrupts** (reg 10 counter, used for split-scroll status bars) map to SNES H-IRQ / HDMA tables; the "lock top 2 rows / right 8 columns from scrolling" bits map to HDMA scroll changes / a window. Left-column blanking (reg 0 bit 5) = SNES window mask.

**Audio.** SN76489: 3 square + 1 noise (periodic or white). SPC700: squares as looped BRR, noise via the DSP noise generator (white) or a BRR sample (periodic). Simple; 1 port write per guest register write. Optional FM unit (YM2413, Japanese SMS) is out of scope.

**Prior art.** No known SMS emulator or translator on the SNES. Reverse direction exists and is the sibling report's domain (Genesis runs SMS natively in Mode 4). No public Z80->65816 static recompiler is known; SMS static recompilation exists for modern PCs (various hobby projects).
**[outside the rule]** SA-1 again triples CPU; SuperFX does not help (it is a graphics/RISC chip with poor general-purpose access to the SNES bus).

**Verdict: 1.5-2/5.** Best case: Columns, puzzle and card games, slow RPG screens (Phantasy Star menus). Hardest blocker: CPU speed and the per-tile planar reshuffle on every VDP write. **Proof of concept:** an SMS VDP test ROM or Columns: tile conversion + Sega mapper + SN76489 driver, no line IRQ.

## 3. Nested GGSNES (Game Gear -> SNES)

Same CPU, VDP and sound chip family as the SMS (GG is an SMS with a 160x144 window, 12-bit colour, stereo PSG and a Start button). Everything in section 2 applies, with these differences:
- Screen 160x144 of the 256x192 VDP picture: the game only shows the middle, so **off-screen tile and sprite work can be ignored**, and the SNES display fits easily (SGB-like border possible).
- Palette 12-bit (4-4-4) -> 15-bit, trivial; 32 entries, two-byte writes.
- Stereo PSG register ($06) maps to SPC voice pan, free.
- GG mode games often use fewer raster tricks (small screen), so line-IRQ handling matters less.
So GGSNES is *the same project as SMSNES* with an easier picture; build them as one Z80 front end with two VDP profiles. That is why it ranks above SMSNES.
Prior art: none on SNES. The user's flash cart plays .gg as SMS mode on a Genesis (memory note), which belongs to the sibling report.
**Verdict: 2/5.** Proof of concept: Columns GG, or a homebrew test ROM, sharing the SMS proof's Z80 core.

## 4. Nested NesGen (NES -> Genesis)

**CPU: the easy part.** 6502 at 1.79 MHz (~2.9 cycles/instr average... ~600k instr/s) on a 68000 at 7.67 MHz: **~12-13 host cycles per guest instruction**, but 68000 instructions are slow (4-cycle bus, `move.b` d(An) ~12 cycles). Keep A/X/Y in data registers, NES RAM at a fixed address register base (`move.b d16(a5,d0.w)` gives zp,X in one instruction). Flags: 68000 has N Z V C, with C/X semantics; 6502 carry on SBC is inverted (not-borrow) so subtract needs an `eor` of X/C or reversed logic; decimal mode is unused on the 2A03 (good). Expect **near full speed** for most games with a static recompiler. Banking: the 68000's flat 4 MB cart space holds every PRG bank; a mapper write just changes which recompiled block the dispatch table points at. Self-modifying code in NES RAM needs Nested's existing interpreter fallback (an interpreter on a 68000 runs ~1/4-1/3 speed: OK for small routines).

**Video: the real wall.**
- Tiles: NES 2bpp planar (8 bytes plane 0, 8 bytes plane 1); Genesis tiles are **4bpp packed (nibble per pixel)**. Every CHR tile must be converted (table lookup, ~16 bytes -> 32 bytes). CHR-ROM games: pre-convert at build time, fine. **CHR banking** (MMC3 1 KB banks, swapped mid-frame for status bars) becomes VRAM re-uploads: Genesis VBlank DMA is ~7 KB/frame in H40, so swapping 4 KB+ of tiles per frame eats the budget; mid-frame swaps need HInt + raster DMA (very tight, ~18 bytes per line during active display is not available; only with display off). CHR-RAM games convert on every $2007 write.
- Attributes: NES picks a palette per 16x16 area; Genesis per 8x8 tile, so attributes expand 1->4 tilemap entries (fine). 4 BG palettes of 3+1 colours fit inside Genesis's 4 lines of 16. NES 52-colour palette -> 9-bit Genesis colour: noticeable colour shift but acceptable.
- Screen: 256x240 vs Genesis H32 256x224 (cut 16 lines, same as Nested does on SNES). Scroll: Genesis planes 64x32 cells cover NES 2-screen mirroring.
- Sprites: NES 64, 8 per line; Genesis H32 64 sprites, 16 per line: fine. Sprite 0 hit has no Genesis equivalent; emulate by computing the hit line from OAM + BG at frame time and firing HInt there.
- Mapper IRQs (MMC3 scanline) -> Genesis HInt counter: good fit, and better than the SNES in one way (HInt reloads per line).

**Audio.** 2A03: 2 pulse (duty 12.5/25/50/75), triangle, noise, DPCM. Genesis PSG (SN76489) has 3 squares at 50% duty only + noise: pulses lose their timbre, no triangle. YM2612 can fake pulses and triangle with FM patches (passable), and its DAC channel can play DPCM (via the Z80 as a sample player, as Genesis games do). This means a Z80 sound driver + FM patch design: a real project, but known territory (the user already has YM2612 cores in BrokenNes). "The Genesis has an SN76489 already" helps only for noise and a rough square.

**Prior art.**
- No known NES-on-Genesis emulator or translator. Genesis/68000 NES emulation has been discussed on SpritesMind / SegaXtreme forums and considered too slow for interpretation of the PPU; nothing playable surfaced in this search. https://gendev.spritesmind.net/forum/
- Conversely, some NES games had official Genesis/MD versions rewritten by hand (not translation).
- Nested itself (NES->SNES static recompile) is the closest proof that the CPU side can be recompiled. https://github.com/Myself086/Project-Nested
- Not applicable: SNES coprocessor note. **[outside the rule, different console]** the Sega CD or 32X would add CPU/VRAM, but that is not "Genesis" any more.

**Verdict: 1.5/5.** Best case: NROM / CHR-ROM games with one status bar (Donkey Kong, Balloon Fight, Ice Climber, Excitebike, maybe SMB1 with sprite-0 emulation). Hardest blocker: CHR banking and mid-frame PPU tricks on a VDP whose VRAM can only be fed during VBlank; second blocker: the 2A03 sound design. **Proof of concept:** Donkey Kong (NROM, 8 KB CHR, no scroll, no sprite 0): pre-converted tiles, attribute expansion, PSG-only sound. Teaches the PPU-translation layer without banking.

---

## Ranking (most to least likely to work)

1. **Nested GB**: direct tile format match, Super Game Boy proves the picture path, simplest sound mapping; CPU too slow, so aim at slow games.
2. **Nested GGSNES**: Z80 front end + small screen; shares all of its work with SMSNES.
3. **Nested SMSNES**: same as GG plus full 256x192 picture and more raster tricks.
4. **Nested NesGen**: CPU is easy, but video banking and sound are the hardest translation problems of the four.

Order of work if tried: GB test ROM -> Tetris. Then a shared Z80 front end proven on GG (Columns GG), then SMS. NesGen last, starting with Donkey Kong.

## What would help (not downloaded; ask the user)
- Public-domain / homebrew test ROMs: Blargg's `cpu_instrs.gb` (GB CPU correctness), `dmg-acid2.gb` (PPU), an SMS VDP test ROM (e.g. "SMS VDP Test" by FluBBa), and Genesis-side nothing new. These already may exist in BrokenNes's test ROM folders (see project_gb_cores memory); check there before downloading.
- Pan Docs (read online, no download needed) and SMS Power's VDP documentation (https://www.smspower.org/Development/).
