# WideMess

WideMess is BrokenNes's souped-up SNES fantasy console.
- The plan: `docs/projects/widemess-plan.md`.
- The hardware contract, spec v1: `docs/projects/widemess-spec.md`.

This folder holds what is built for WideMess outside the emulator code.

| Path | What |
|---|---|
| `testroms/layers/` | Test ROM 1, eight layers of 256 colours at 400 x 224 (65816 assembly, WLA-DX), with its negative control. `build.ps1` builds both into `testroms/bin/`. |
| `testroms/picture/` | Milestone 2's test ROMs: `sprites`, `windows`, `widesnes`. One 65816 register player (`player.asm`) plays the script of register writes, DMA and HDMA that `gen_picture.py` generates for each scene; the same writes drive the generator's own model of spec section 3, which draws the picture each ROM must produce (`<rom>.expect.png`). `build.ps1` builds the ROMs, their negative controls and the pictures into `testroms/bin/`. |
| `testroms/cpu/` | Milestone 4's test ROMs (WLA-DX; `build.ps1` builds them into `testroms/bin/`): `speed.asm`, the fast CPU and fast DMA measured against the spec's cycle model, with its no-unlock control; `bigmap.asm` + `gen_bigmap.py`, a 4 MB game on the WideMess map (1 MB work RAM, the 20-bit WRAM port, 256 KB battery RAM, 64 ROM banks). |
| `testroms/bin/` | The built test ROMs: `layers.wms`, `layers.sfc` (the same game as a SNES image) and `layers_nounlock.wms` (the negative control); `sprites`, `windows`, `widesnes` (`.wms`, `_nounlock.wms`) with the model's pictures (`.expect.png`, `_nounlock.expect.png`, `.<variant>.expect.png`); `speed.wms`, `speed.sfc`, `speed_nounlock.wms`; `bigmap.wms` (its controls, cut to 2 MB or on the HiROM map, are made by `--wms-test`). |

**The emulator side** is in `Windows/NesEmulator/snes/`:
- `WideMess.cs`: the unlock and registers (`WmsExtension`), the `.wms` file (`WmsFile`) and the picture chip (`PPU_WMS`).
- `WideMess.Picture.cs` (milestone 2): `PPU_WMS`'s wide compositor, one for the WideMess mode and the SNES modes drawn 400 wide (main and sub
  screens, windows, colour math), both sprite tables and the registers `$21A0-$21BF`.
- `WideMess.Memory.cs` (milestone 4): the WideMess map's limits and the cartridge a `.wms` file becomes (`SnesCartridge.LoadWideMessMap`).
- `BOARD_SFC.WideMess.cs` (milestone 4): fast mode, the 1 MB work RAM and its 20-bit WRAM port, fast DMA.
- The hooks in `BOARD_SFC.cs` and `PPU_SFC.WideMess.cs` (read-only views of the SNES chip's registers; the SNES chip's own drawing is untouched).
- The console is registered in `Windows/NesEmulator/systems/ConsoleSystems.cs` (key `wms`).
- It runs on `SnesSession` with `wideMess: true`.

## Tools (BrokenNes Workshop)

```
BrokenNes.Workshop.exe --wms-test  [--roms WideMess	estromsin] [--snes "a.sfc;b.smc"] [--frames 900] [--reference-paths]
BrokenNes.Workshop.exe --wms-pack  --in game.sfc --out game.wms [--title T] [--features 1] [--map 0|1|2] [--sram BYTES] [--battery]
BrokenNes.Workshop.exe --wms-shot  --rom game.wms --out shot.png [--frames 60] [--scale 3] [--console wms|snes] [--wide]
BrokenNes.Workshop.exe --wms-sheet --rom game.wms --out sheet.png [--frames 120]
```

- `--wms-test` runs the milestones' checks, each with a negative control. With `--snes`, it also runs each SNES game frame for frame on the SNES
  console and on the WideMess console and requires identical pictures and sound; then draws each game 400 wide (WIDE set on the chip, the game
  itself still locked) and requires every line's columns 72-327 to be the SNES chip's own line. `--reference-paths` turns the SNES chips' fast
  paths off.
- `--wms-pack --map 2` packs a WideMess-map ROM; `--sram` is its battery RAM in bytes and `--battery` makes it the game's save.
- `--wms-shot --wide` draws a SNES game's modes 400 wide, the way the wide SNES modes show it.
- `--wms-sheet` draws the 400-wide picture and each of the 8 layers alone.
- Workshop is a GUI-subsystem program. In PowerShell, run it with `Start-Process -Wait -NoNewWindow -RedirectStandardOutput`.

The desktop app's own check is `UAT/entrypoints/widemess-desktop-smoke.ps1 -Exe <BrokenNes.Windows.exe>`. It uses a scratch settings folder.

## Milestone 1 result (2026-10-24)

- **`--wms-test`: 21 of 21 checks pass.**
  - The test ROM unlocks WideMess and draws 400 x 224 with 1,020 colours.
  - Each layer alone shows all 255 colours of its bank.
  - The layers scroll.
  - A save state round-trips.
  - The negative controls hold:
    - registers written without the unlock change nothing;
    - on a SNES the ROM sees no WideMess;
    - the loader refuses damaged, cut and too-new files, and files needing missing features, and says why.
- **8 SNES games run frame-for-frame identical (picture and sound, 900 frames each) on the SNES and WideMess consoles:** Super Mario World,
  A Link to the Past, Star Fox (Super FX), Super Mario RPG (SA-1), Pilotwings (DSP-1), F-Zero, Super Metroid and Donkey Kong Country.
- **The desktop app smoke test passes 7 of 7.**

## Milestone 2 result (2026-10-24): the picture, complete

**What it is.** Spec section 3 (3.1-3.4) and, in `PPU_WMS`, one wide compositor for the WideMess mode and the SNES modes drawn 400 wide:
- the SNES modes 0-7 at 400: layers past the SNES's 256 columns, offset-per-tile, mosaic, hi-res averaged to 400, Mode 7 continued to both edges;
- sprites: the SNES table across 400, or the WideMess table (256 sprites, 64 a line, 8 x 8 to 64 x 64, 4 or 8 bpp, 10-bit X, slots between the 8 layers);
- windows (9-bit positions, or the SNES's at the SNES picture's place) and colour math, main and sub screens, in both.

![Super Mario World drawn 400 wide](../docs/projects/widemess-images/m2-smw-wide.png)
![F-Zero's Mode 7 drawn 400 wide](../docs/projects/widemess-images/m2-fzero-wide.png)

**How it is tested.** `testroms/picture/gen_picture.py` holds a second implementation of spec section 3, in Python. Each scene's register writes
go both into the ROM's script and into that model, and the model draws the picture the ROM must produce. Workshop compares BrokenNes's picture
with it pixel for pixel.

| Scene | What the ROM draws | Its pictures (`testroms/bin`) |
|---|---|---|
| `sprites` | The WideMess table at 400: 16 sizes, 4 and 8 bpp, flips, X = -512 / -64 / -63 / -8 / 380 / 396 / 400 / 511, Y = -8 / 216, table order over priority, `WMOBJPRI` slots, 70 sprites on a line (64 drawn), 30 sprites 64 wide on a line (128 slivers drawn), `WMOBJSTAT`. | the ROM; the no-unlock control; `WMOBJ` off (the SNES table, X centred); the WideMess mode 256 wide |
| `windows` | 9-bit windows (window 1 shaped line by line by HDMA), OR / AND / XOR / XNOR / inverted for BG1-BG8, main and sub masks, add / subtract / half, sub screen, fixed colour, clip to black, math region, the per-sprite math bit. | the ROM; the no-unlock control; the SNES's `WH0-WH3` at 400 |
| `widesnes` | Bands of mode 1, mode 0, hi-res mode 5, mode 2 (offset-per-tile with mosaic) and Mode 7 drawn 400 wide (HDMA on `BGMODE`, `MOSAIC`, `BG3VOFS`): a 64-wide map's both halves, a 32-wide map repeated, offset-per-tile columns left of the SNES edge, the SNES table across 400 (X = 0 / 255 / 256 / 300 / 327 / 328 / -72 / -100), SNES windows with an edge on the SNES edge. | the ROM; the no-unlock control (it must equal the wide picture's columns 72-327); 9-bit windows; the WideMess table in a SNES mode |

**Results.**
- **`--wms-test`: 43 of 43 without games; 70 of 70 with the 9 SNES games** (Super Mario World, A Link to the Past, Star Fox, Super Mario RPG,
  Pilotwings, F-Zero, Super Metroid, Donkey Kong Country, Chrono Trigger):
  - every test-ROM picture, control and variant is pixel-exact against the model;
  - three deliberate bugs planted to test the tests (sprite v-flip, the XNOR window, the SNES X split at 328) failed 8 checks between them, in all three scenes;
  - a save state carries the sprite table and the new registers;
  - milestone 1's 8-layer picture is unchanged (frame 61 hash `1977C01DE6BFDDE7`);
  - each game is frame-identical (picture and sound) on the SNES and the WideMess consoles;
  - drawn 400 wide, every line's columns 72-327 are the SNES chip's own line (fast and reference paths).
- **Coverage of the games drawn wide** (3,000 frames each, about 6 million lines, all identical in the centre): modes 1, 2 (offset-per-tile),
  3, 5 (hi-res), 7, mosaic, colour math, windows and sprites. The games check the centre only; the side columns are checked by the test
  ROMs' model (modes 0, 1, 2, 5, 7). Modes 4 and 6 drawn wide are exercised by no game and no test ROM yet; mode 3's sides only by the
  shared code path.
- **Speed in Workshop** (the whole console; the picture alone in brackets; best of two timings on a shared CPU): layers 489 fps (553),
  sprites 687 (957), windows 351 (341), widesnes 302 (730). Drawing allocates nothing. `--wms-test` gates at 90 and 120.
- **The SNES path is unchanged:** the SNES chip's own drawing is untouched; the ALttP speed-pass golden is IDENTICAL; an interleaved A/B
  against milestone 1's build (ALttP, 2,400 frames, 4 pairs) shows no slowdown (mean of medians 705 fps for milestone 1, 853 for this build:
  noise on a shared CPU, the code is the same).
- **UAT `run-suite.ps1`: 125 pass, 0 fail, 16 skip, 3 info.** Lite and the plugin's Core build (WideMess stays hidden there).
- **The desktop app smoke test passes 9 of 9**: it now also checks that the app shows `sprites.wms` and `widesnes.wms` exactly as the model draws them.

## Milestone 4 result (2026-10-24): CPU and memory

- **`--wms-test` checks M4.1-M4.6 pass** (64 checks in all with milestone 1's), the same on the reference paths (`--reference-paths`).
  - Fast mode: the speed ROM's loops run the iterations the spec's cycle model predicts, to one iteration (loop A 3,208 a frame at SNES speed,
    12,386 fast: 3.86x; loop B, with one I/O read, 2,407 and 7,539: 3.13x). From FastROM, loop A runs 3,610 at SNES speed (the 96-clock
    prediction) and the same 12,385 in fast mode: `MEMSEL` does nothing there.
  - Fast DMA: 5,632 bytes a vblank in 512-byte transfers, 10,752 with fast DMA; fast CPU alone leaves DMA as it was.
  - The WideMess map: a 4 MB game with 256 KB of battery RAM finds all 64 ROM banks and their mirrors, the 1 MB work RAM over `$70-$7F`
    (A bus, 20-bit WRAM port, DMA both ways), and its battery save comes back in a new session.
  - Save states carry the new work RAM, the 20-bit port and fast mode. The new paths allocate nothing.
  - The negative controls hold: locked, the speed ROM measures exactly what a SNES does; a LoROM cartridge has no work RAM at `$70-$7D` and a
    17-bit port; the 4 MB game cut to 2 MB loses banks 32-63; on the HiROM map it finds ROM, not RAM; the loader refuses map-2 files the
    map cannot hold, and maps 0 / 1 that disagree with their image.
- **SNES games stay SNES:** the 9 games of the milestone (Super Mario World, A Link to the Past, Star Fox, Super Mario RPG, Pilotwings,
  F-Zero, Super Metroid, Donkey Kong Country, Chrono Trigger) are frame-for-frame identical on both consoles; the speed-pass goldens are
  unchanged with and without the reference paths; SNES speed is unchanged (interleaved A/B).
- **UAT 125 pass, 0 fail** (16 skip, 3 info); the desktop smoke test passes 7 of 7; Lite and Core build.
- Not covered by a test ROM: HDMA tables in the new work RAM (HDMA reads it through the same path as DMA, which is tested), and the old joypad
  ports' 12 clocks in fast mode (the I/O rule is tested through `$4214`).
