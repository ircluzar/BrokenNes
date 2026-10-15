# Sega track B4a: Genesis VDP and timing, round 4 (record, written while the work goes on)

Branch `worktree-agent-aa6d456623a384ca7`, 2026-10-15, on top of `docs/projects/sega-track-b3a.md` and `-b3b.md`. Everything below was measured on BlastEm 1.0.0 run as a program (probe ROMs whose answers come
out through work RAM or the screenshot; its source was not read). Owner of `md/MdVdp*.cs`, the VDP tests and `Workshop/MdGolden`; `BOARD_MD`, the 68000 and `mix/` belong to others and were not changed
(one additive experiment knob in `BOARD_MD`, `IplSampleAdjustClocks`, default 0, is named below).

**This record says at each line what was run.** Items marked "pending" were not finished when it was last updated.

## 0. Where it stands

* **Every static picture rule that the random-scene differential could find is now identical with BlastEm**: 513 scenes (21 seeded random-scene sets and 3 probe sets, `Workshop/MdGolden/md_scene_sets.tsv`) were pixel-identical with the oracle's screenshots when they were pinned; 240 of them were the sets the rules were derived from, 224 more came from fresh seeds that were never looked at before the rules were final (224 of 225 identical, the one other scene is 4 pixels off, see 6). `scenes-check.ps1` re-verifies all 513 without BlastEm in 2 minutes.
* The vertical interrupt is right (it was 780 master clocks late), and so are the VBlank/HBlank flags, HINT, the Z80 interrupt line length and the V counter step in both widths.
* Open, with data: the 68000 takes an interrupt later on the oracle after long instructions (section 4; not mine to change), Titan Overdrive 1 frame 1007 and Overdrive 2 frame 3709 (section 5), and the list in section 6.

## 1. The rules, exactly (for whoever implements them elsewhere: `mix/MdToNes.cs`, the plugin, a future core)

Line coordinates: master clocks from the line's H counter 0 (3420 clocks a line in both widths; the H counter's top byte advances every 16 clocks in H40 and every 20 in H32 up to the sync stretch).

### 1.1 Interrupts, flags, counters (`MdVdp.cs`)

| What | Rule (all measured on the oracle) | Was |
|---|---|---|
| V counter step to the next line | clock **2640** in H40 (H counter `$14A`, top byte `$A5`), clock **2660** in H32 (`$10A`, top byte `$85`) | 2640 in both |
| VINT (status bit 7 set, IPL 6, Z80 INT asserted) | **788** clocks (H40) / **770** (H32) after the V counter's step into the first blank line (line 223 for V28), i.e. clock 8 / 10 of line 224 | 788 / 770 clocks after the START of line 224 (780 clocks late) |
| Z80 interrupt line | asserted with the VINT and held **2573** master clocks (171.5 Z80 cycles). Proved with a Z80 handler that re-enables interrupts before RETI and counts its entries (`probes_b4a.z80_int_repeats`, handler lengths 14 to 34 nops): the oracle re-enters 100 percent of frames up to 23 nops, 50 percent at 24, 20 percent at 25, 0 from 26; ours gives the same counts for any length from 2565 to 2580 clocks (2555 and below, 2590 and above differ) | a whole line (3420) |
| VBlank status flag (bit 3) | rises **33** (H40) / **49** (H32) clocks after the V step and falls the same time after the step into the last line | at the step |
| HBlank status flag (bit 2) | set from clock **2855** (H40) / **2975** (H32) until the end of the line (falls at the next H counter 0) | 2816 until 220 |
| HINT | the counter steps at clock **2642** (H40) / **2670** (H32) of the lines in its counting phase: the **pre-render line and lines 0 to ActiveLines-1**; the other lines reload it from R10. With `R10 = n` the first interrupt is at the end of line `n-1`, then every `n+1` lines; `n = 0` every line starting with the pre-render line (225 interrupts a frame); `n` above ActiveLines never fires. The handler sees the V counter already stepped (line + 1, 0 for the pre-render line) | lines 0 to ActiveLines at clock 2816 |
| H32 slot window | the next line's slot pattern takes over at clock 2800 (any value 2790-2920 gives the oracle's picture of Nemesis' CRAM flicker test); the drawn H32 line's tail slots are the counter ticks `$E9`, `$F4` (before the line start) and `$02`, `$0B` | cut 2640, tail ticks 2, 5, 8, 11 |

Not modelled (measured, small): in H32 the VINT status flag shows about 20 clocks after the IPL. HV counter values: in a straight HV scan the oracle never reads `$E4` in H40 (ours does, 3.9 clocks of it; the oracle's `$B6` lasts 4.9 clocks against our 1.6), so the jump from `$B6` is one tick different; not pursued, nothing but a raster-timing demo would notice.

### 1.2 Planes (`MdVdpRender.cs`)

**2-cell column vertical scroll** (R11 bit 2). The columns are pairs of the plane's cells, so their boundaries follow the plane's horizontal scroll, and the stretch before the first boundary is a special case.
For plane P (A: entries `VSRAM[2c]`, B: `VSRAM[2c+1]`) with its horizontal scroll `hs` on that line (the 10-bit value of the line's hscroll entry), `vsMask` = `0x7FF` in interlace mode 2, else `0x3FF`:

```
b0 = hs & 15                                   (screen x of the first pair boundary)
if x < b0:          H40: vs = VSRAM[2*19] & VSRAM[2*19+1]   (the last pair's two entries ANDed; the same for plane A and plane B)
                    H32: vs = 0
else:               column = (x - b0) >> 4 ;  vs = VSRAM[2*column + (P == B ? 1 : 0)]
                    the column ends at x = b0 + 16*(column + 1)
vs &= vsMask
window at the left (plane A only; the window covers x < W = 16 w, w >= 1) with b0 != 0:
                    for W <= x < W + b0:  column = w (the entry of the pair AFTER the one that would be there), not (x - b0) >> 4 = w - 1
```
(the old rule, `column = x >> 4`, is the case `hs & 15 = 0` of this). Proved by the probe scenes `vsprobe` (5 scenes: H40 and H32, hscroll 0 and 13, left column blank), `vsprobe2` (12 scenes: fine scroll 1, 3, 5, 7, hscroll 29, H32, plane B, left column blank) and `vsprobe3` (12 scenes: a window of 16, 32 or 48 pixels at the left with hscroll 0, 5, 13, 21), made by `oracle-tools/md-picture/vsprobe.py` (`python vsprobe.py out.bin [2|3]`): plane A or B only, every plane row identifiable from the pixels, a different VSRAM value per column; and by the random families `vsc` (20 scenes) and `vscw` (with the window, 20 scenes). All of them are pixel-identical with BlastEm's screenshots. The H32 stretch value 0 was the last piece (probe 1's values, A15 AND B15 = 64, showed 0); the 11-bit scroll was found by a random scene with a 32 x 128 plane in mode 2 (53,000 pixels off with ten bits, 15 with eleven, 0 after the sprite rule below).

**Plane sizes that do not fit.** R16 width code `ws` and height code `hs` (0: 32, 1: 64, 3: 128 cells): `if ws == 3 and hs != 0 then hs = 0` (128 wide is only 32 high), `else if hs == 3 and ws != 0 then hs = 1` (64 wide with the height code 128 is 64 x 64). 32 x 128 is valid. Proved by the scene set `sc_sizes` (family `sizes`, seed 31, 16 scenes: 11 identical before, 16 of 16 after) and `sc_plain` scenes 16, 25, 29.

**Interlace mode 2** (R12 bits 2-1 = 11): cells are 8 x 16 (a name table entry's pattern is 64 bytes: address `tile * 64 + row * 4`, rows 0-15, vertical flip 15 - row), the field's line `y` is picture row `2y + field` (field = the odd-frame flag), vertical scroll (11 bits) and sprite Y (offset 256, not 128) count picture rows, the window's cell row is `(2y + field) >> 4`. BlastEm's screenshot in this mode is **one field** (320 x 224 or 256 x 224, 240 high in V30), not a woven picture; ours matches that field pixel for pixel (every interlace-2 scene of the pinned sets: 24 + 29 of 30 + the interlace-2 scenes of the other sets). `MdVdp.Picture` stays the field; `MdVdp.WovenPicture` (448 or 480 rows) is the extra: the app (`MdSession`) shows the woven picture at `DisplayWidth` 640 in mode 2, the field's own picture in mode 1 (no double height there: the lines of the field are the picture lines).

**Left column blank (R0 bit 5) in shadow/highlight mode** (oracle: `sc_shadow` scenes 0, 8, 16, 17, `sc_spr` 1, `sc_plain` 21, `sc_plain2` 12 and 26 were 900 to 3700 pixels off; now identical). Without shadow/highlight the first eight pixels are the backdrop and nothing else changes. With it:
1. the blank column (x 0-7) is the backdrop **in shadow** (always, a high-priority tile hidden under it does not make it normal);
2. the **planes** (A, window, B) in the second cell (x 8-15) are the planes of the first cell (x 0-7), i.e. the composite of cell 0's planes with cell 1's sprites; the sprites stay where they are. This second part looks like an oracle quirk (hardware would hardly do it) but no hardware measurement says either way; it is behind `MdVdp.LeftBlankShadowRule` (3 = both, 1 = only the first, 0 = the old picture; `--slot-set lbshadow=N`).

### 1.3 Shadow/highlight sprite rule (full)

State of a pixel (R12 bit 3 set), in this order, for every pixel:

1. `tilePrio = (plane A tile priority bit) OR (plane B tile priority bit)`, taken from the tiles even where they are transparent there. `state = normal` if `tilePrio` else `shadow`.
2. The plane ladder picks the plane pixel: a visible (colour != 0) high-priority A pixel, else a visible high-priority B pixel, else visible A, else visible B, else the backdrop. `aHi`/`bHi` = a visible high-priority pixel exists in A/B.
3. A sprite pixel (the first one drawn on that pixel; colour != 0) **wins the ladder** if its priority bit is set, or if there is no visible high-priority plane pixel (`!(aHi || bHi)`). A sprite that does not win does nothing at all (no pixel, no operator).
4. A sprite that wins:
   * palette 3 colour 14 (`0x3E`) is the highlight operator: the pixel keeps the plane colour and `state` becomes normal if it was shadow, highlight if it was normal; palette 3 colour 15 (`0x3F`) is the shadow operator: `state = shadow`; neither draws itself;
   * any other sprite pixel is drawn with its own colour; its brightness is normal if its priority bit is set **or its colour index is 14** (any palette but 3), else `state` (so a low-priority sprite is in shadow over low-priority planes, and normal where a transparent high-priority tile made the state normal). **Colour 15 outside palette 3 follows the state like any other colour** (the first version of this record said "14 or 15"; 1,700 oracle pixels of colour 15 over shadow were all shadowed).
5. Output colour = CRAM entry in normal, shadow (levels 0-7 of the DAC: 0 27 49 71 87 103 119 130) or highlight (levels 7-14) brightness.

Old rule for comparison: operators acted whatever the priorities; a low-priority sprite was hidden whenever `state` was normal, even over a low-priority plane; colours 14/15 were shadowed. Proved by the scene sets `sc_shnw` (family `shnw`, seed 32, 16 scenes: shadow/highlight only, planes full size 0-1, full scroll: 50-440 differing pixels per scene with the first rule, 13-75 with the second, **0 with the third**: colour 15 follows the state), `sc_shadow`, `sc_spr`, the fresh-seed sets, and the probe `shprobe.py`, by the `--md-scenes --sprstat` statistics (what brightness BlastEm gives a sprite pixel, by sprite priority, colour class and plane A/B state) and `--classify` (the same-entry mode changes).

## 2. What was built (all in the repo)

| Piece | Where |
|---|---|
| VINT/flags/HINT/step rules, Z80 INT length, H32 slot fit | `Windows/NesEmulator/md/MdVdp.cs` |
| Interlace 2, column scroll (+ window and H32 stretches, 11-bit scroll), plane sizes, shadow/highlight (+ left blank), debug layers | `Windows/NesEmulator/md/MdVdpRender.cs`, `MdSession.cs` (woven picture, `DisplayWidth`) |
| Experiment knobs for every number above | `Workshop --md-run/--md-scenes/--md-pic-test --slot-set "name=v;..."` (`MdRunCli.ApplySlotSet`): `vinth40 vinth32 vstep40 vstep32 vblankdelay40/32 hblank40/32 hblankend hint40 hint32 hintrule vsshift colpair sizerule iplshift z80int winstretch h32stretch lbshadow vsmask11 sprnormal cuth32 preh32 ...`; `BOARD_MD.IplSampleAdjustClocks` (additive, default 0, B4b's file). `--md-pic-test --slot-set` runs the checks on an old rule: that is how the new checks were shown able to fail |
| Static-scene differential against BlastEm | `Workshop --md-scenes <rom> --count N --shots <dir> [--meta json] [--out dir] [--classify] [--all --xmin/--xmax/--ymin/--ymax --max N] [--sprstat] [--sprline y] [--only i,j] [--hash-out tsv --set name \| --hash-check tsv --set name]` (`MdSceneCli.cs`); ROMs from `oracle-tools/md-picture/scenefuzz.py` (random complete VDP states shown until Start is pressed; families `plain interlace1 interlace2 h32 v30 shadow window vscroll small sprites sizes shnw winnw vsc vscw`), driver `scenerun.py NAME --family F --scenes N --seed S [--record tsv]` |
| **Pinned oracle scenes** (the regression asset) | `Workshop/MdGolden/md_scene_sets.tsv` (set, kind, family or probe, scene count, seed), `md_scene_oracle.tsv` (513 lines: set, scene, which of our pictures matched, its FNV-1a hash), `Workshop/Sega/oracle-tools/md-picture/scenes-check.ps1` (rebuilds every ROM from its seed with python, runs the Workshop, compares; no BlastEm; `-SlotSet` runs it on an old rule) |
| Probe ROM sources and analysis | `oracle-tools/md-picture/probes_b4a.py` (drift-sampling flag/HV/interrupt probes, single-instruction interrupt loops, Z80 INT repeats), `b4a.py`, `an_b4a.py`, `vsprobe.py`, `shprobe.py` |
| VDP feature census | `Workshop --md-census <dir> [--frames N --jobs 2 --every 3 --offset k --out tsv]` (`MdCensusCli.cs`, counters `ActiveRegWrites ... SpriteOverflowLines` in `MdVdp`; finished games are appended to `<out>.part` at once) |

The drift-sampling method: the 68000 meets the frame 5 master clocks later in phase each frame (896,040 mod 7 = 5), so a probe that repeats one thing every frame samples an event at every phase; the event's time falls out of how many samples saw it one step late (calibrated on our own emulator, whose times are known), and the HV reads of the same stream put the status samples on the line. The oracle's answers come out through the quick-save state's work RAM (`BlastEm F7 pause, backtick save`) or its screenshots.

The scene method, and how the rules were found: a random scene is a complete VDP state shown for 12 frames; a rule is wrong where the oracle's screenshot and ours differ. `--classify` says what kind of difference each pixel is (same CRAM entry in another brightness, another entry), `--all` dumps our plane A, plane B and sprite values next to both colours of every pixel in a window, and a search of our layers for the oracle's row finds the rule (the left-blank quirk showed up as "the oracle's x 8-15 equal our plane B at x 0-7"). Probe scenes (one plane, every row identifiable by its colours) then fix the exact formula. Lost Start presses in BlastEm shift the pairing of shots and scenes (probe 3: two lost presses; the tool cannot tell), so every new set is looked at for a run of large differences before it is believed.

## 3. Verification (what was run)

| What | Result |
|---|---|
| `--md-test` | 150 of 150 (the vertical-interrupt check rewritten to the measured rule, five HINT line checks, HBlank and V-step checks in H40 and H32, the Z80 line length); a poison run (old HINT rule, old HBlank, old H32 step) fails 6 |
| `--md-pic-test` | 129 of 129 (93 before B4a: 13 interlace checks, 5 column/window/H32-stretch checks, 4 + 2 shadow/highlight sprite checks, 4 left-blank checks, the 11-bit scroll); poison runs through `--slot-set`: `winstretch=0` 1 fails, `h32stretch=0` 1, `lbshadow=0` 2, `lbshadow=1` 1, `vsmask11=0` 1, `sprnormal=14,15` 1; cell height left at 8 in mode 2 fails 5, the old column rule and plane-size rule 6 |
| Pinned oracle scenes | 513 of 513 (`scenes-check.ps1`, 136 s); poison runs of the whole check: see the line below |
| `--md-golden` | 73 of 75 frames; the two that differ are the oracle-pinned frames of Titan Overdrive 1 (frame 1007) and 2 (frame 3709), see 5. The other 73 (ten roster games, test ROMs, Overdrive 1's other twelve frames and Overdrive 2's other nine) did not change with any rule in this record, so the changes are invisible in the game frames the golden set holds; the CRAM flicker entry was re-pinned as `oracle` with its original hash `85ACBA2D69E933D5` |
| `--sega-test`, `--sega-contracts-test`, `--md-cart-test` | 65, 80, 47 passed (earlier in the round; rerun on the integrated tree at wrap-up) |
| `--picture-bridge-test` | 173 of 174: the check "2-cell column vertical scroll is followed" fails until `mix/MdToNes.cs` follows the column rule above (C4b) |
| Fresh seeds, never used to derive a rule | 224 of 225 scenes identical (plain 40 of 40, all 40 of 40, interlace 2 29 of 30, shadow 25 of 25, H32 20 of 20, V30 20 of 20, window 20 of 20, sprites 30 of 30, interlace 1 20 of 20) |

## 4. Interrupt latency finding (68000/board domain, not changed)

BlastEm takes an interrupt later than we do after most instructions that are not a nop. A main loop of one instruction kind plus `bra.s`, HINT every line, mean handler entry time after the HINT time (20,000 interrupts each; `probes_b4a.LOOP_BODIES`, `hlb.py`, ours then the oracle):

| Loop body | ours | oracle | gap |
|---|---|---|---|
| `nop` | 460.8 | 460.8 | 0 |
| `mixed`: `move.l (a6),d1`, `addq.w #1,d1`, `lsl.l #8,d3`, `move.w d1,(a6)`, `nop` | 502.1 | 501.9 | 0 |
| `move`: two `move.w (a6),d1` | 462.3 | 470.3 | +8 |
| `divu d4,d3` / `move.l d3,d6; divu d4,d6` | 467.2 / 463.8 | 500.3 / 478.7 | +33 / +15 |
| `mulu d2,d3` / `moveq #1,d5; mulu d2,d5` | 712.3 / 700.1 | 778.9 / 693.1 | +67 / **-7** |
| `lsl.l #8,d5` / `moveq #30,d5; lsl.l d5,d6` | 543.6 / 692.8 | 569.7 / 684.2 | +26 / **-9** |
| `clr.l (a6)` / `add.l d3,(a6)` | 490.6 / 490.6 | 528.8 / 528.9 | +38 / +38 |
| `jsr sub1` (`sub1: rts`) | 489.9 | 554.6 | +65 |
| `movem.l d0-d7,(a6)` / `movem.l (a6),d0-d7` / `movem.l d0-d1,(a6)` | 660.9 / 674.7 / 502.0 | 752.1 / 765.3 / 564.1 | +91 / +91 / +62 |
| `longmove`: `movem.l d0-d7,-(sp)` then `movem.l (sp)+,d0-d7` | 680.7 | 963.6 | +283 |

The oracle's longest latency in the movem pair is 212 cycles against a 158-cycle loop (an interrupt that arrives during a long MOVEM waits for the end of the NEXT instruction). The 68000's interrupt overhead itself (entry, handler, RTE) is the same: mean gap across a VINT 1743.1 (ours) against 1740.3 clocks. The pattern (zero for nops, growing with an instruction's length and with its write cycles, slightly negative for two shift/multiply loops) says the oracle samples the interrupt line at a different point of the instruction than our `BOARD_MD.BeginAccess` does (ours: the start of the last bus access); `BOARD_MD.IplSampleAdjustClocks` (`--slot-set iplshift=N`) moves ours by a fixed amount and cannot fit it (a fixed shift cannot be +91 for movem and 0 for nop). For B4b: the rule needs the instruction kind (the position of the last bus cycle).

## 5. Titan Overdrive frames that changed (open)

Overdrive 1 frame 1007 and Overdrive 2 frame 3709 matched BlastEm's screenshots with the old (late) timing and do not with the measured one. For Overdrive 1 frame 1007 the old VINT time alone restores the oracle hash (`--slot-set vinth40=1568`); the frame flips between VINT before about line 224 + 545 and after. For Overdrive 2 frame 3709 the old VINT, HBlank/VBlank flag times and HINT together restore it (`9EA97E624A1A1BD3`); old HINT alone gives `D4CC5FF0BD19D9F9`, the current rule `43CE6E2A91339149`. The demos poll the VDP status in a tight loop and are phase-sensitive; the interrupt-acceptance difference of section 4 is the prime suspect (the loops contain MOVEM and the interrupt can land in them). **Not resolved**; the two golden entries stay as oracle pins that currently fail, so the golden file says what is true (73 of 75).

## 6. Open

* f_il2 scene 5 (fresh seed 63, H32 interlace 2, no shadow): 4 pixels at (215-218, 53), a line with 14 sprites and 39 cells where the oracle does not draw the sprite our model cuts after 3 of its 4 cells; the sprite-on-line row test, the cell limit and the per-line sprite limit variants tried do not move it (`--sprline 53` lists the line). Not explained.
* HV counter tick lengths in the sync stretch (above), sprite attribute cache (static scenes cannot show it; ours reads the table directly), DMA/FIFO in H32 against the oracle, Direct Color DMA, the H40 CRAM flicker phase (see below), interlace in the app (shown as the woven picture), the three Overdrive 1 scenes and Overdrive 2's frame that are not golden frames.
* Real games frame by frame: only Sonic 1 (B3b) and the golden frames; the VDP census (`--md-census`, below) says which games use HINT, column scroll, shadow/highlight, interlace and mid-frame writes, to choose the games to compare.
