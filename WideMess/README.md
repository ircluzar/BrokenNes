# WideMess

WideMess is BrokenNes's souped-up SNES fantasy console.
- The plan: `docs/projects/widemess-plan.md`.
- The hardware contract, spec v1: `docs/projects/widemess-spec.md`.

This folder holds what is built for WideMess outside the emulator code.

| Path | What |
|---|---|
| `testroms/layers/` | Test ROM 1, eight layers of 256 colours at 400 x 224 (65816 assembly, WLA-DX), with its negative control. `build.ps1` builds both into `testroms/bin/`. |
| `testroms/cpu/` | Milestone 4's test ROMs (WLA-DX; `build.ps1` builds them into `testroms/bin/`): `speed.asm`, the fast CPU and fast DMA measured against the spec's cycle model, with its no-unlock control; `bigmap.asm` + `gen_bigmap.py`, a 4 MB game on the WideMess map (1 MB work RAM, the 20-bit WRAM port, 256 KB battery RAM, 64 ROM banks). |
| `testroms/bin/` | The built test ROMs: `layers.wms`, `layers.sfc` (the same game as a SNES image) and `layers_nounlock.wms` (the negative control); `speed.wms`, `speed.sfc`, `speed_nounlock.wms`; `bigmap.wms` (its controls, cut to 2 MB or on the HiROM map, are made by `--wms-test`). |

**The emulator side** is in `Windows/NesEmulator/snes/`:
- `WideMess.cs`: the unlock and registers (`WmsExtension`), the `.wms` file (`WmsFile`) and the picture chip (`PPU_WMS`).
- `WideMess.Memory.cs`: the WideMess map's limits and the cartridge a `.wms` file becomes (`SnesCartridge.LoadWideMessMap`).
- `BOARD_SFC.WideMess.cs`: fast mode, the 1 MB work RAM and its 20-bit WRAM port, fast DMA.
- The hooks in `BOARD_SFC.cs` and `PPU_SFC.WideMess.cs`.
- The console is registered in `Windows/NesEmulator/systems/ConsoleSystems.cs` (key `wms`).
- It runs on `SnesSession` with `wideMess: true`.

## Tools (BrokenNes Workshop)

```
BrokenNes.Workshop.exe --wms-test  [--roms WideMess\testroms\bin] [--snes "a.sfc;b.smc"] [--frames 900] [--reference-paths]
BrokenNes.Workshop.exe --wms-pack  --in game.sfc --out game.wms [--title T] [--features 1] [--map 0|1|2] [--sram BYTES] [--battery]
BrokenNes.Workshop.exe --wms-shot  --rom game.wms --out shot.png [--frames 60] [--scale 3] [--console wms|snes]
BrokenNes.Workshop.exe --wms-sheet --rom game.wms --out sheet.png [--frames 120]
```

- `--wms-test` runs the milestones' checks, each with a negative control. With `--snes`, it also runs each SNES game frame for frame on the SNES
  console and on the WideMess console and requires identical pictures and sound. `--reference-paths` turns the SNES chips' fast paths off.
- `--wms-pack --map 2` packs a WideMess-map ROM; `--sram` is its battery RAM in bytes and `--battery` makes it the game's save.
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

## Milestone 4 result (2026-10-24): CPU and memory

- **`--wms-test` checks M4.1-M4.6 pass** (62 checks in all with milestone 1's), the same on the reference paths (`--reference-paths`).
  - Fast mode: the speed ROM's loops run the iterations the spec's cycle model predicts, to one iteration (loop A 3,208 a frame at SNES speed,
    12,386 fast: 3.86x; loop B, with one I/O read, 2,407 and 7,539: 3.13x).
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
