# GB core family - provenance ledger

**Exception (foreign APU project, 2026-11-01):** two alternate sound chips are ports of old GPL emulators, kept on purpose for their sound and recorded as a documented exception to the policy (root `THIRD_PARTY_NOTICES.md` section 2, `docs/projects/foreign-apu-project.md`):

| What | Where it lives | Source | License |
| --- | --- | --- | --- |
| GNUB: gnuboy 1.0.3 sound engine (`sound.c`, `pcm.h`, `noise.h` tables, sound clock from `cpu.c`) as a Game Boy sound chip | `GnubSound.cs`, `APU_GNUB.cs` | [gnuboy](https://github.com/rofl0r/gnuboy) @4c2f6c4, (C) Laguna, Gilgamesh and contributors | GPL-2.0 (documented exception) |
| VBA: VisualBoyAdvance 1.7.2 Game Boy sound engine (`src/gb/gbSound.cpp`, the `Sound.cpp` tables, the `GB.cpp` sound tick) as a Game Boy sound chip | `VbaGbSound.cs`, `APU_VBA.cs` | [vba-raspi mirror](https://github.com/weimingtom/vba-raspi) @9fbd04f8, (C) Forgotten and the VBA development team | GPL-2.0 (documented exception) |
| TGBD: TGB Dual `apu.cpp` (Hii) as a Game Boy sound chip: frame-block renderer, `update()` 5x per frame, phase never reset on trigger, L/R swapped, NR52 quirks | `TgbdApu.cs`, `APU_TGBD.cs` | [tgbdual-libretro](https://github.com/libretro/tgbdual-libretro) `gb_core/apu.cpp` (+ `gb.h`, `libretro/dmy_renderer.cpp`), (C) 2001 Hii | GPL-2.0-or-later (documented exception) |
| VBAM: VBA-M 2007-12-03 Game Boy sound (the VBA 1.8.0-beta `gbSound.cpp`, the last before blargg's Gb_Apu): VBA 1.7.2 plus read masks, shadow frequency registers, length reload on retrigger | `VbamGbSound.cs`, `APU_VBAM.cs` | [VBA-M](https://github.com/visualboyadvance-m/visualboyadvance-m) @5e3902b4 `src/gb/gbSound.cpp` (+ `GB.cpp` for the write routing) | GPL-2.0-or-later (documented exception) |
| GYOB: GameYob's Game Boy-to-Nintendo-DS sound mapping (arm9 `dssoundengine.cpp` + arm7 `sound.c`), driving an emulated DS sound unit (duty squares, PCM8 wave loop, LFSR noise tables, the DC hum, 32,768 Hz mixer) | `GameYobSound.cs`, `APU_GYOB.cs` | [GameYob](https://github.com/Stewmath/GameYob) @45599fb `platform/ds/arm9/source/dssoundengine.cpp`, `arm7/source/sound.c`, `arm7/include/noise.h`, (c) 2013 Drenn & contributors | MIT (permissive; listed here with the foreign APU set) |
| MGB: minigb_apu before its April 2022 rework (float per-sample timing, no frame sequencer, per-channel high-pass) | `MinigbApu.cs`, `APU_MGB.cs` | [Peanut-GB](https://github.com/deltabeard/Peanut-GB/tree/024175d/examples/sdl2/minigb_apu) @024175d `examples/sdl2/minigb_apu/minigb_apu.c`, (c) Mahyar Koshkouei, based on [MiniGBS](https://github.com/baines/MiniGBS) by Alex Baines | MIT (permissive; listed with the foreign APU set). Licence text: https://github.com/deltabeard/Peanut-GB/blob/master/LICENSE |

The rest of this ledger covers the stock cores.

The GB cores (CPU_GB, PPU_GB, APU_GB, BOARD_GB, GbCartridge) are written from documentation (Pan Docs,
gbdev wiki) and black-box testing against test ROMs and Mesen 2. No emulator source code was copied.

Data borrowed from elsewhere:

| What | Where it lives | Source |
| --- | --- | --- |
| CGB compatibility palette tables (title checksums, 4th-letter table, triplet ids/flags, palette offsets, 30 palettes) | `GbCompatPalettes.cs` | Data from Nintendo's CGB boot ROM, transcribed from ISSOtm's disassembly `gb-bootroms/src/cgb.asm` (codeberg.org/ISSOtm/gb-bootroms). Tables only; the selection logic is rewritten from its comments. |
| Post-boot register values | `CPU_GB.ResetPostBoot`, `BOARD_GB.Reset` | Pan Docs "Power Up Sequence"; DIV phase $ABC8 measured with Mooneye `boot_div`. |
| Boot logo left in VRAM (DMG) | `PPU_GB.LoadBootLogo` | The Nintendo logo is decoded from the cartridge's own header; the 8-byte "®" tile is the glyph the DMG boot ROM leaves at tile $19 (Mealybug tests draw with it). No boot ROM file is read. |
| PPU event timeline | `PPU_GB.cs` | Timings measured by running gbmicrotest / Mooneye / Wilbertpol test ROMs (black box); no emulator source. |
