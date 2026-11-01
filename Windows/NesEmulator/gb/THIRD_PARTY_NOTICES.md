# GB core family - provenance ledger

The GB cores (CPU_GB, PPU_GB, APU_GB, BOARD_GB, GbCartridge) are written from documentation (Pan Docs,
gbdev wiki) and black-box testing against test ROMs and Mesen 2. No emulator source code was copied.

Data borrowed from elsewhere:

| What | Where it lives | Source |
| --- | --- | --- |
| CGB compatibility palette tables (title checksums, 4th-letter table, triplet ids/flags, palette offsets, 30 palettes) | `GbCompatPalettes.cs` | Data from Nintendo's CGB boot ROM, transcribed from ISSOtm's disassembly `gb-bootroms/src/cgb.asm` (codeberg.org/ISSOtm/gb-bootroms). Tables only; the selection logic is rewritten from its comments. |
| Post-boot register values | `CPU_GB.ResetPostBoot`, `BOARD_GB.Reset` | Pan Docs "Power Up Sequence"; DIV phase $ABC8 measured with Mooneye `boot_div`. |
| Boot logo left in VRAM (DMG) | `PPU_GB.LoadBootLogo` | The Nintendo logo is decoded from the cartridge's own header; the 8-byte "®" tile is the glyph the DMG boot ROM leaves at tile $19 (Mealybug tests draw with it). No boot ROM file is read. |
| PPU event timeline | `PPU_GB.cs` | Timings measured by running gbmicrotest / Mooneye / Wilbertpol test ROMs (black box); no emulator source. |
