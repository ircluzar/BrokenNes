# The WideMess test cartridge

One WideMess cartridge (`WideMess/testroms/bin/widemess-testcart.wms`) whose menu runs the console's tests live, in WideMess's own 400 x 224
picture. It is a **conformance cartridge**: it depends on the spec alone (`docs/projects/widemess-spec.md`, spec 1.0), judges itself wherever
the hardware can (it reads back what it wrote, and measures timing with the console's own NMI and vblank), shows PASS / FAIL on screen, and
keeps every result in a fixed table in work RAM, so a harness on any implementation (BrokenNes, a C++ port, an FPGA core) can read them
without screenshots. What cannot judge itself (a picture) has a reference picture drawn by a second implementation of the spec (milestone 2's
Python model), to compare pixel for pixel.

![The test cartridge's screens](../../docs/projects/widemess-images/testcart/sheet.png)

## Running it

- **The desktop app**: Console > **WideMess test cartridge** (the cartridge is built into the program). HTTP: `POST /api/widemess/testcart`.
- **The file**: open `WideMess/testroms/bin/widemess-testcart.wms` on the WideMess console, in any app or with Workshop
  (`--wms-run --rom ...`, `--wms-shot --rom ...`).
- **BrokenDev**: project `widemess-testcart` (`brokendev build widemess-testcart`, `verify`, `run`).
- **Every self-check, unattended**: hold **SELECT** at power-on (from the first frame), or choose **RUN ALL SELF-CHECKS** in the menu. The
  run-all ends on a summary screen after about 1,700 frames (28 seconds).
- **Workshop's check**: `BrokenNes.Workshop.exe --wms-testcart --roms WideMess\testroms\bin` (also section TC of `--wms-test`).
- **The screens as pictures**: `BrokenNes.Workshop.exe --wms-testcart-shots --rom WideMess\testroms\bin\widemess-testcart.wms --out <dir>`.

Keys: the d-pad and **A** choose a test; **B** or **START** go back to the menu from any test. In tests 2-4, **LEFT / RIGHT** change page and
**SELECT** makes a live page still (its reference picture) or, on a reference page, shows its next variant.

## The tests

Each test's status is one of **PASS**, **FAIL**, **N/A** (the console's `WMFEAT` lacks what it needs: nothing of it ran), **EYE** (nothing to
judge by itself: compare its pictures with their references) and **-** (not run yet).

| # | Test | Needs (`WMFEAT`) | Self-checks (all must pass) | By eye / by picture |
|---|---|---|---|---|
| 1 | Eight layers of 256 colours: milestone 1's test ROM (its 1,024 colours, 33 tiles of 8 bits and eight 64 x 32 maps), each layer scrolling at (n + 1) / 2 pixels a frame. LEFT / RIGHT choose a layer, A switches it on / off, X all on, Y the chosen one alone, SELECT stops the scrolling. The bands at the top and the bottom are the SNES picture (WMCTRL = 0, written by HDMA line by line, spec 3.1) with the legend in SNES mode 0. | bit 0 | 3: the colours, the tiles and the maps read back through `WMCGDATA` / `WMVDATA` exactly as written (colours 15 bits). | Each layer alone draws all 255 colours of its bank (the legend borrows 15 of bank 0); the lines below the band are 400 wide. |
| 2 | Sprites: page 1, the swarm (live): the 256-sprite table at 400, balls 8 x 8 to 32 x 32 in 4 bits, a 64 x 64 planet in 8 bits (255 colours), a line of 72 sprites of which the first 64 are drawn, `WMOBJSTAT` live. Page 2: milestone 2's sprites scene. | bits 0, 1 | 5: `WMOBJSTAT` AND $C0 after three frames of each layout on rows 100-107: 72 sprites 8 wide: $40; 30 sprites 64 wide (240 slivers): $80; exactly 64 sprites: $00; exactly 16 sprites 64 wide (128 slivers): $00; none: $00 (cleared each frame). | Both pages against their pictures. |
| 3 | Windows and colour math at 400: page 1, a spotlight (live): window 1 a circle whose edges HDMA writes into `WMWH0` / `WMWH1` line by line, colour math subtracting a grey outside it, window 2 a 24-pixel bar sweeping the 400 columns (9-bit positions), the logo (BG3) masked by window 1 XNOR window 2. Page 2: milestone 2's windows scene. | bits 0, 1 | none: **EYE**. | Both pages, and the variant, against their pictures. |
| 4 | The SNES modes drawn 400 wide, under a caption band in the WideMess mode (WMCTRL 3 then 1 by HDMA): mode 1 (a 64-tile map whose two halves show, the SNES sprite table at X -72 to 320), mode 0 (four layers), mode 5 (hi-res: stripes 1 half-pixel wide blend, 2 wide show), mode 2 (offset-per-tile from BG3's map, mosaic on BG2), Mode 7 (the plane turning to both edges), and milestone 2's SNES-modes scene. All live except the last. | bits 0, 1 | none: **EYE**. | All six pages, and the two variants, against their pictures. |
| 5 | Sound: the 32 voices in a grid, each lit by its envelope (ENVX) as it plays: voices 0-7 the S-DSP's (BRR in 64 KB, blue), 8-31 the new ones (even 16-bit PCM, green; odd BRR, amber: the same tone side by side). D-pad: a voice; A plays it; X all 32; Y its echo on / off; SELECT the same note as 16-bit PCM (voice 8), then BRR (voice 9). | bit 2 | 38: `WID` ($9D) is $57; each of the 32 voices alone sounds (its ENVX and OUTX not 0) while the other 31 stay silent (32 checks); all 32 at once; voice 8's playing address (`VxADDR`) stays inside its PCM sample and moves; its `WENDX` bit is set after it loops; the echo buffer stays 0 without `WEON` and fills with it. | - |
| 6 | Fast CPU and fast DMA: speed.asm's loops (`WideMess/testroms/cpu`), counted from NMI to NMI, at SNES speed and fast, beside spec 2's predictions, with meters; measured again and again. | bit 3 (CPU), bit 5 (DMA); what is missing shows N/A | 12: the 8 counts below within their windows; fast CPU alone leaves DMA within 512 bytes of normal; fast DMA moves at least 1.8 times normal DMA; `WMSPEED` and `WMDMA` read back 1. | - |
| 7 | Memories, pass after pass (each cell lights blue while it is tested, then green or red): work RAM $70-$7F (the 20-bit WRAM port writes each bank, the A bus reads it back), video RAM (8 units of 64 KB, through `WMVDATA`), sound RAM (banks 1-15 of 64 KB, through `$21F3`), the colours, the WideMess sprite table, the A bus to the port, the port's wrap, the $00 mirror, $7E:2000-FFFF in place. Fast CPU is on for the checks when the console has it. Video RAM is tested in forced blank (the screen goes black for about a second). | any of bits 0, 1, 2, 4 (each part what it needs) | 45: each item (sound RAM bank 0 is the running SPC700's: skipped). | - |
| 8 | Identity: `WMID`, `WMVER`, `WMREV`, `WMFEAT` and its bits, and what the cartridge saw at power-on before the unlock. | none | 6: `WMID` $57 and `WMVER` 1 once unlocked; before the unlock `WMID` and `WMSPEED` read open bus ($42, the address's high byte for `LDA $42xx`); after the broken sequence $57, $00, $4D `WMID` still reads $42; `$4227-$422F` read 0 once unlocked. `WMREV` is shown, any value. | - |

A full run on BrokenNes: tests 1, 2, 5, 6, 7, 8 **PASS** (3 + 5 + 38 + 12 + 45 + 6 = 109 checks, none failed), tests 3 and 4 **EYE**.

### Test 6's expected counts

Spec section 2's cycle model: a frame is 262 lines of 1,364 master clocks, the DRAM refresh takes 40 a line; at SNES speed ROM (SlowROM) and
work RAM take 8 clocks, internal cycles 6, I/O 6; in fast mode every cycle takes 2 except the I/O registers ($2100-$21FF, $4200-$43FF: 6). The
count in a frame is (357,368 - 10,480 - the NMI) / the loop's clocks. The cartridge's NMI is its `NmiEntry` (`jmp (nmijump)`: 3 ROM, 2 work RAM
cycles) and speed.asm's handler (16 ROM, 16 work RAM, 11 internal, 1 I/O): 368 clocks at SNES speed, 102 fast. Every count must be within 0.5%.

| Row | The loop | Clocks (SNES / fast) | Predicted | Window | BrokenNes |
|---|---|---|---|---|---|
| LOOP A | `inc dp`, `lda dp`, `beq` (16-bit A): 6 ROM, 6 work RAM, 2 internal | 108 | 3,209 | 3,193-3,224 | 3,208 |
| LOOP A FAST | the same, `WMSPEED` = 1 | 28 | 12,385 | 12,324-12,447 | 12,385 |
| LOOP B | loop A + `lda $4214` (16-bit): + 3 ROM, 2 I/O | 144 | 2,406 | 2,395-2,418 | 2,406 |
| LOOP B FAST | the same, fast | 46 | 7,539 | 7,502-7,576 | 7,539 |
| FASTROM A | loop A at $80:xxxx with `MEMSEL` = 1: its ROM cycles at 6 | 96 | 3,610 | 3,592-3,627 | 3,609 |
| FASTROM FAST | the same, fast (`MEMSEL` changes nothing) | 28 | 12,385 | 12,324-12,447 | 12,385 |
| DMA | general DMA, ROM to `$2180`, 512-byte transfers started in one vblank | 8 a byte | 6,123 fit | 4,587-6,635 | 5,632 |
| DMA FAST | the same, `WMDMA` = 1 | 4 a byte | 12,247 fit | 9,687-12,759 | 10,752 |

(Fast CPU alone, `WMSPEED` = 1 and `WMDMA` = 0: BrokenNes moves 6,144, within 512 of 5,632.) While it counts, the cartridge's NMI is the short
handler above, HDMA is off and the pad is not read (`NMITIMEN` = $80), as in speed.asm.

### The picture pages and their reference pictures

Every picture page is a script of register writes and DMA that `gen_testcart.py` makes and feeds to milestone 2's model of spec section 3
(`WideMess/testroms/picture/gen_picture.py`), which draws the picture the page must show (`WideMess/testroms/bin/widemess-testcart.<page>.expect.png`,
and `.<page>.<variant>.expect.png`). Before a script the cartridge restores the power-on picture state: every picture register at its reset
value (spec 3: `$21A0-$21BF` 0 except `WMOBJPRI0-3` = 12, 8, 4, 0), the 512 KB of video RAM, the 1,024 colours and both sprite tables 0. The
reference pages are milestone 2's three scenes exactly; their pictures are milestone 2's (`sprites.expect.png` and so on), pixel for pixel.

| Test | Page (RIGHT from the first) | Live | Picture | SELECT on it |
|---|---|---|---|---|
| 2 | 1 swarm | yes | `swarm` | still |
| 2 | 2 ref-sprites | | `ref-sprites` | variants `snes_table` (`WMOBJ` = 0), `narrow` (`WMCTRL` = 2: 256 wide) |
| 3 | 1 spot | yes | `spot` | still |
| 3 | 2 ref-windows | | `ref-windows` | variant `snes_positions` (`WMWCTL` = 0) |
| 4 | 1 mode1, 2 mode0, 3 mode5, 4 mode2, 5 mode7 | yes | `mode1` ... `mode7` | still |
| 4 | 6 ref-widesnes | | `ref-widesnes` | variants `wide_windows` (9-bit windows), `widemess_table` (`WMOBJ` = 1) |

A live page's still picture is its script's state, before any animation: what its reference shows. `widemess-testcart.json` (beside the
cartridge) lists the pages, their numbers, their pictures and their variants.

## The results table

Work RAM `$7E:1000` (also `$00:1000`). It survives every screen; the run-all clears the statuses first.

| Address | Size | What |
|---|---|---|
| `$7E:1000` | 4 | `WMTC` |
| `$1004` | 1 | the table's version: 1 |
| `$1005` | 2 | the cartridge's version: 1, 0 |
| `$1007` | 1 | state: 0 starting, 1 interactive, 2 a run-all running, **3 a run-all done**, 4 not a WideMess (the SNES screen), 5 a WideMess without the picture (the SNES screen) |
| `$1008` | 2 | frames (+ 1 every NMI) |
| `$100A` | 1 | the screen: 0 title, 1 menu, 2-9 tests 1-8, 10 run-all, 11 summary, 12 not available |
| `$100B` | 1 | run-alls finished |
| `$100C` | 1 | 1: a WideMess answered |
| `$100D` | 2 | the pad at power-on (`$4218`) |
| `$1010-$1013` | 4 | `WMID`, `WMVER`, `WMREV`, `WMFEAT` once unlocked |
| `$1014` | 1 | the features the tests use: `WMFEAT` AND the cartridge's mask |
| `$1015` | 1 | the mask (ROM `$00:FF87`, below) |
| `$1016-$1018` | 3 | before the unlock: `WMID`, `WMSPEED`; `WMID` after $57, $00, $4D |
| `$1019` | 1 | 1: `$4227-$422F` read 0 once unlocked |
| `$1020-$1027` | 8 | each test's status: `P` pass, `F` fail, `N` not available, `V` by eye, `R` running, 0 not run |
| `$1028-$102F` | 8 | each test's checks made |
| `$1030-$1037` | 8 | each test's checks failed |
| `$1100 + (test - 1) x $40` | $40 | each test's own values (below) |

| Block | Offsets |
|---|---|
| test 1, `$1100` | +0 colour mismatches (words), +2 tile mismatches (words), +4 map mismatches (words), +6 the layers on (`WMTM`), +7 the chosen layer, +8 1 = scrolling stopped |
| test 2, `$1140` | +0-4 `WMOBJSTAT` for the five layouts, +8-12 each one `P` / `F`; +$10 the page shown, +$11 still, +$12 the variant |
| tests 3, 4, `$1180`, `$11C0` | +$10 the page shown, +$11 still, +$12 the variant |
| test 5, `$1200` | +0 `WID`; +1-4 the voices that sounded alone (bit v, voice 0 first); +5-8 the voices sounding with all 32 on; +9 1 = the echo buffer stayed 0 without `WEON`; +$A 1 = it filled with it; +$B 1 = voice 8's address in its PCM sample and moving; +$C `WENDX` of bank 0; +$D the step reached (6 = done); +$E 1 = the SPC700 program runs |
| test 6, `$1240` | 16-bit counts: +0 loop A, +2 A fast, +4 B, +6 B fast, +8 FastROM A, +$A FastROM A fast, +$C DMA bytes, +$E fast DMA bytes, +$10 DMA bytes with fast CPU alone; +$12 / +$13 `WMSPEED` / `WMDMA` read back; +$14 rounds measured (16 bits) |
| test 7, `$1280` | one status a byte: +0-$0F work RAM $70-$7F, +$10-$17 video RAM units 0-7, +$18-$27 sound RAM banks 0-15 (bank 0 `S`), +$28 colours, +$29 the sprite table, +$2A the A bus to the port, +$2B the port's wrap, +$2C the $00 mirror, +$2D $7E:2000-FFFF; +$30 passes (16 bits), +$32 words that did not read back (16 bits, saturating) |
| test 8, `$12C0` | a copy of `$1010-$101F` |

## From a harness

1. Power on with SELECT held (from the first frame for a few frames).
2. Run until `$7E:1007` = 3 (about 1,700 frames; give it 4,000).
3. Check `$7E:1000` = `WMTC`, the statuses `$1020-$1027` = `P P V V P P P P` and `$1030-$1037` all 0. On a console that lacks features
   the statuses show `N` for what it lacks, and the rest must still pass.
4. For the pictures: from power-on, START (the menu), DOWN test - 1 times, A; RIGHT to the page; SELECT on a live page (wait until its block's
   +$11 is 1) or SELECT k times on a reference page (+$12 = k); then compare the frame with the page's picture. Wait for the block's +$10 (the
   page number in `widemess-testcart.json`) before comparing, and let a few frames pass after each screen change.

**The feature mask.** ROM `$00:FF87` (byte $FF87 of the image, inside the cartridge's info block `WMTC` at `$00:FF80`) is ANDed with
`WMFEAT` before any test reads it. It is $FF. Patch it (and the `.wms` header's CRC: `Workshop --wms-pack` makes a new file) to see the
cartridge's "not available" paths on a full console: $FB (no sound+) gives `P P V V N P P P`; $01 (the picture alone) `P N N N N N P P`; $00 a
SNES-picture screen saying the picture is missing (state 5).

**On a SNES.** `widemess-testcart.sfc` is the same image as a SNES HiROM file: a SNES reads open bus at `$4221` and the cartridge shows, in
SNES mode 0, "THIS IS A WIDEMESS CARTRIDGE" with the value it read (state 4, no test run).

The `.wms` file asks for no feature (`required` 0): the cartridge reads `WMFEAT` itself and says what is missing, rather than being refused.

## How it is made

| File | What |
|---|---|
| `testcart.asm` | the 65816 program's header, vectors, boot (the identity probes), NMI, the text and panel layers; it includes the rest |
| `tc_defs.inc` | the registers, the work RAM and the results table's addresses |
| `tc_video.asm` | every picture register to its reset value, DMA into video RAM and the colours, the title / menu backdrop |
| `tc_screens.asm` | the menu, the title, the run-all and its summary, "not available", the SNES screen, test 8 |
| `tc_layers.asm` | test 1 |
| `tc_picture.asm` | tests 2-4's pages: the power-on picture state, milestone 2's register player, the variants |
| `tc_pages.asm`, `tc_modes.asm` | the live pages: test 2's swarm and its self-checks, test 3's spotlight; test 4's SNES modes |
| `tc_sound.asm`, `tc_spc.asm` | test 5: the 65816 side and the SPC700 program |
| `tc_speed.asm`, `tc_mem.asm` | tests 6 and 7 |
| `gen_testcart.py` | the data: the font, the backdrop, the pages' scripts and their model pictures, the memory pattern, the samples, test 6's predictions, `widemess-testcart.json` |
| `build.ps1` | builds it all into `WideMess/testroms/bin` (`-Out` for another folder): `gen_testcart.py`, WLA-DX (`wla-spc700`, `wla-65816`, `wlalink`), `Workshop --wms-pack --map 2` |

The cartridge is 512 KB on the WideMess map (game-file map 2): the code in ROM bank 0's upper half (it runs at `$00:8000-FFFF`), the SPC700
program at `$C0:0000`, the data in banks `$C1-$C7`. The build is deterministic: the same sources give the same `.wms` byte for byte.

## What it does not test

- The picture is never judged by the cartridge itself: only against the reference pictures, outside it.
- Modes 3, 4 and 6 drawn wide, Mode 7's EXTBG, the SNES table's priority rotation and the overscan picture (239 lines): the model draws none
  of them, so no page uses them.
- Sound: pitches and levels are not measured (Workshop's milestone-3 checks do that, by FFT, on `WideMess/testroms/sound`); noise and pitch
  modulation are not played.
- Battery RAM and the WideMess map's ROM mirrors (`WideMess/testroms/cpu/bigmap.asm` tests them), HDMA from the new work RAM.
- The debug port (`WMFEAT` bit 7) is shown, not tested.
