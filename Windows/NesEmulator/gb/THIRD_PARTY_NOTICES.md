# GB core family - provenance ledger

**Exception (foreign APU project, 2026-11-01):** two alternate sound chips are ports of old GPL emulators, kept on purpose for their sound and recorded as a documented exception to the policy (root `THIRD_PARTY_NOTICES.md` section 2, `docs/projects/foreign-apu-project.md`):

| What | Where it lives | Source | License |
| --- | --- | --- | --- |
| GNUB: gnuboy 1.0.3 sound engine (`sound.c`, `pcm.h`, `noise.h` tables, sound clock from `cpu.c`) as a Game Boy sound chip | `GnubSound.cs`, `APU_GNUB.cs` | [gnuboy](https://github.com/rofl0r/gnuboy) @4c2f6c4, (C) Laguna, Gilgamesh and contributors | GPL-2.0 (documented exception) |
| VBA: VisualBoyAdvance 1.7.2 Game Boy sound engine (`src/gb/gbSound.cpp`, the `Sound.cpp` tables, the `GB.cpp` sound tick) as a Game Boy sound chip | `VbaGbSound.cs`, `APU_VBA.cs` | [vba-raspi mirror](https://github.com/weimingtom/vba-raspi) @9fbd04f8, (C) Forgotten and the VBA development team | GPL-2.0 (documented exception) |

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
