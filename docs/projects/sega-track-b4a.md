# Sega track B4a: Genesis VDP and timing, round 4 (record, written while the work goes on)

Branch `worktree-agent-aa6d456623a384ca7`, 2026-10-15, on top of `docs/projects/sega-track-b3a.md` and `-b3b.md`. Everything below was measured on BlastEm 1.0.0 run as a program (probe ROMs whose answers come
out through work RAM or the screenshot; its source was not read). Owner of `md/MdVdp*.cs`, the VDP tests and `Workshop/MdGolden`; `BOARD_MD`, the 68000 and `mix/` belong to others and were not changed
(one additive experiment knob in `BOARD_MD`, `IplSampleAdjustClocks`, default 0, is named below).

**This record says at each line what was run.** Items marked "pending" were not finished when it was last updated.

## 0. Where it stands

* **Every static picture rule that the random-scene differential could find is now identical with BlastEm**: 513 scenes (21 seeded random-scene sets and 3 probe sets, `Workshop/MdGolden/md_scene_sets.tsv`) were pixel-identical with the oracle's screenshots when they were pinned; 240 of them were the sets the rules were derived from, 224 more came from fresh seeds that were never looked at before the rules were final (224 of 225 identical, the one other scene is 4 pixels off, see 6). `scenes-check.ps1` re-verifies all 513 without BlastEm in 2 minutes.
* The vertical interrupt is right (it was 780 master clocks late), and so are the VBlank/HBlank flags, HINT, the Z80 interrupt line length and the V counter step in both widths.
* **DMA throughput matches the oracle within 0.2 percent** in both widths and every kind (68000 to VRAM, fill, copy, display on and off) after three fixes found with new probes (`dma_rate.py`): fills and copies stepped at the wrong slot (a 2048-byte fill took one line, the oracle takes ten), blanked H40 lines have six refresh slots not twelve, blanked H32 lines five not four (section 1.1 and 7).
* **A freeze**: a DMA copy or fill started in the last 56 clocks of a blanked line froze the VDP for good (D&D - Warriors of the Eternal Sun at its frame 541, on main too); found by the library census, fixed, with a check that fails without the fix (section 7).
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
| H32 slot window | the next line's slot pattern takes over at clock **2700** (slot 135): the oracle's last picture line of Nemesis' CRAM flicker test has dots at pixels 246, 248, 250 and 254 (slots 135, 136, 137, 139; slot 138 is a refresh slot); with 2700 and the refresh slot the picture is identical, at 2680 five pixels differ, from 2720 on the dots are wrong (the earlier 2800 matched an older capture that had no dots); the drawn H32 line's tail slots are the counter ticks `$E9`, `$F4` (before the line start) and `$02`, `$0B` | 2800 |
| Refresh slots of blanked lines | H40: **six single slots** (26, 59, 90, 122, 154, 186; the first of each former pair), H32: **five** (26, 58, 90, 122, 138). Proof: the oracle's DMA durations in blanked lines (`dma_rate.py`: fill, copy and 68000 transfers, H40 and H32, display on and off) equal ours within 0.2 percent with these, were 1.5 to 3 percent apart in H40 (12 refresh slots) and 0.6 in H32; Titan Overdrive 2 frame 3709 matches the oracle again with the pair's first slot (the second slot does not) | 12 (six pairs) / 4 |
| Fill and copy steps | the search for the next free slot keeps its starting time when it goes on in the next line (it went back to that line's start, so a fill or copy found slots before its engine had started, or the same slot over and over): a fill of 2048 bytes in a blanked H40 line takes 10.3 lines as on the oracle (25.24 laps of fill-and-wait per frame; ours was 239) | one line |

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
| DMA and write throughput probes | `oracle-tools/md-picture/probes_b4a.py` `dma_rate` (laps of one DMA of 2048 bytes and a busy poll per frame: 68000 to VRAM or CRAM, fill, copy; H40 and H32; display on and off) and `write_rate` (laps of 16 data port writes per frame to CRAM, VRAM or VSRAM), runners `dma_rate.py`, `wr_rate.py` (ours, the oracle's quick-save state, the ratio); precision about 0.1 percent, so a missing or extra slot per line shows |
| Interrupt probes | `sr_delay.py` (where a pending interrupt is taken after `move.w #$2000,sr`, `andi.w #$F8FF,sr` and a run of nops, mulu, movem, clr.l, divu, lsl), `hlb.py` / `LOOP_BODIES` (section 4) |
| Sprite-limit isolation | `oracle-tools/md-picture/sprite_isolate.py out.bin family seed index [--variants same,h40,il0]`: one random scene with its planes cleared, so only the sprites show |
| Probe ROM sources and analysis | `oracle-tools/md-picture/probes_b4a.py` (drift-sampling flag/HV/interrupt probes, single-instruction interrupt loops, Z80 INT repeats), `b4a.py`, `an_b4a.py`, `vsprobe.py`, `shprobe.py` |
| VDP feature census | `Workshop --md-census <dir> [--frames N --jobs 2 --every 3 --offset k --out tsv]` (`MdCensusCli.cs`, counters `ActiveRegWrites ... SpriteOverflowLines` in `MdVdp`; finished games are appended to `<out>.part` at once) |

The drift-sampling method: the 68000 meets the frame 5 master clocks later in phase each frame (896,040 mod 7 = 5), so a probe that repeats one thing every frame samples an event at every phase; the event's time falls out of how many samples saw it one step late (calibrated on our own emulator, whose times are known), and the HV reads of the same stream put the status samples on the line. The oracle's answers come out through the quick-save state's work RAM (`BlastEm F7 pause, backtick save`) or its screenshots.

The scene method, and how the rules were found: a random scene is a complete VDP state shown for 12 frames; a rule is wrong where the oracle's screenshot and ours differ. `--classify` says what kind of difference each pixel is (same CRAM entry in another brightness, another entry), `--all` dumps our plane A, plane B and sprite values next to both colours of every pixel in a window, and a search of our layers for the oracle's row finds the rule (the left-blank quirk showed up as "the oracle's x 8-15 equal our plane B at x 0-7"). Probe scenes (one plane, every row identifiable by its colours) then fix the exact formula. Lost Start presses in BlastEm shift the pairing of shots and scenes (probe 3: two lost presses; the tool cannot tell), so every new set is looked at for a run of large differences before it is believed.

## 3. Verification (what was run)

| What | Result |
|---|---|
| `--md-test` | 151 of 151 (the vertical-interrupt check rewritten to the measured rule, five HINT line checks, HBlank and V-step checks in H40 and H32, the Z80 line length, the copy-DMA freeze check); poison runs: old HINT rule, old HBlank, old H32 step fail 6, `--slot-set nextslotfix=0` fails the freeze check (the check runs the VDP in a thread and fails on a timeout) |
| `--md-pic-test` | 129 of 129 (93 before B4a: 13 interlace checks, 5 column/window/H32-stretch checks, 4 + 2 shadow/highlight sprite checks, 4 left-blank checks, the 11-bit scroll); poison runs through `--slot-set`: `winstretch=0` 1 fails, `h32stretch=0` 1, `lbshadow=0` 2, `lbshadow=1` 1, `vsmask11=0` 1, `sprnormal=14,15` 1; cell height left at 8 in mode 2 fails 5, the old column rule and plane-size rule 6 |
| Pinned oracle scenes | 513 of 513 (`scenes-check.ps1`, 136 s, also on the final tree); poison runs of the whole check (`-SlotSet`), scenes still matching: `vsmask11=0` 503, `lbshadow=0` 484, `sizerule=1` 465, `h32stretch=0` 460, `winstretch=0` 450, `colpair=0` 250, `sprnormal=14,15` 356 |
| `--md-golden` | **74 of 75 frames**; the one that differs is the oracle-pinned frame 1007 of Titan Overdrive 1, see 5 (Overdrive 2's frame 3709 matches again with the six H40 refresh slots). The ten roster games' frames were **re-pinned to the frames the same oracle pictures now appear at**: the correct fill speed makes the boot 1 to 5 frames longer, every pinned picture (all 21 that moved) appears again at +1 to +5 frames (`--md-golden --shift` reports it, `--move-shifted` rewrites the checkpoints; the hash, and so the oracle label, is unchanged). The CRAM flicker entry (frames 401 and 404) is pinned to a fresh oracle capture's picture (`3CB725DB8297E4B2`; the earlier pin matched an older capture). Direct colour DMA and the FIFO page (regression-only) were re-recorded; the FIFO page still shows 16 of 16 passed |
| `--sega-test`, `--sega-contracts-test`, `--md-cart-test` | 65, 80, 47 passed (on the tree after the slot refit; rerun on the integrated tree at wrap-up) |
| Speed (interleaved A/B against main, median process CPU time, shared machine) | Sonic 1 1.028, Sonic 2 0.994, Gunstar Heroes 1.033 (branch over main; per-run ranges overlap): not measurably slower; measured before the slot refit, which changes no per-pixel work |
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

Overdrive 1 frame 1007 matched BlastEm's screenshot with the old (late) timing and does not with the measured one: only the old VINT time restores the oracle hash (`--slot-set vinth40=1568`; slot, DMA, release and IPL-shift variants do not). The frame flips between VINT before about line 224 + 545 and after. The demo polls the VDP status in a tight loop and is phase-sensitive; the interrupt-acceptance difference of section 4 is the prime suspect (the loops contain MOVEM and the interrupt can land in them). **Not resolved.** Overdrive 2 frame 3709 (which also flipped with the VINT, flag and HINT changes) matches the oracle again after the six-refresh-slot fit (section 1.1), so it was a slot-timing sensitivity, not the interrupt. Only Overdrive 1 frame 1007 keeps the golden file at 74 of 75.

## 6. Open

* f_il2 scene 5 (fresh seed 63, H32 interlace 2, no shadow): 4 pixels at (215-218, 53), a line with 14 sprites and 39 cells where the oracle does not draw the sprite our model cuts after 3 of its 4 cells; the sprite-on-line row test, the cell limit and the per-line sprite limit variants tried do not move it (`--sprline 53` lists the line; `sprite_isolate.py` builds the scene with the planes cleared for an oracle run). Not explained.
* The H32 CRAM flicker test: the oracle's picture is **static** frame to frame, ours (with the slot fit of 1.1) **cycles through three pictures** in a period of three frames (the old fit was static but had no dots on the last line); the colour phase of a frame therefore cannot be matched except at the matching frame. The drift of the colour cycle per frame is the number of CRAM writes per frame modulo 3, so one slot a frame in the line-end tail window is wrong somewhere; `wr_rate.py` on the oracle (CRAM writes per frame to 0.1 percent) was queued to find it. The H40 variant (the ROM with R12 patched to `$81`) cycles too, in ours with period 3 and differently in the oracle.
* HV counter tick lengths in the sync stretch (above), sprite attribute cache (queued: scene family `satcache` shows it), DMA 68000 to CRAM in the oracle runs 3 percent longer than twice a VRAM transfer in one sample (noisy, repeat), Direct Color DMA (pictures recognisable, not compared pixel for pixel), interlace in the app (shown as the woven picture), the three Overdrive 1 scenes and Overdrive 2's frame that are not golden frames.
* Real games frame by frame: only Sonic 1 (B3b) and the golden frames; section 8 says which games use HINT, shadow/highlight, window and mid-frame writes.

## 7. Found with the new probes: the slot search, the refresh slots, a freeze

**The freeze.** The library census (section 8) stalled on D&D - Warriors of the Eternal Sun: `--md-run` hangs at its frame 541 on main as well. A debug check in the VDP's wait loops found the loop: `MdVdp.RunTo` with `dma = Copy`, `Time` exactly at a line's end. `NextFreeSlot(from)`, when its search goes on in the next line, set `from = ls` (that line's start) instead of keeping its own `from`; a copy or fill that starts within 56 clocks of the end of a line has `from = dmaStartTime` in the next line, and the search returned a slot at the start of that line, **before** `from`: `ConsumeSlot` did nothing (the engine had not started), the next search returned the same slot again, and time never advanced. Fixed for the fill and copy steps (`DmaStepWork`); `--md-test` has a check that starts a copy at every clock of the last 70 before a line end in a thread with a timeout (it fails with `--slot-set nextslotfix=0`).

**What else the same search did.** For fill and copy the old search handed out slots before `from` at every line start, so a fill of 2048 bytes in a blanked H40 line took one line (`dma_rate.py`: 239.65 laps a frame against the oracle's 25.24; fills and copies 5 to 15 times too fast, the 68000 to VRAM transfer was right to 2 percent). The oracle's DMA durations then showed the refresh slots: blanked H40 lines have 204 free slots, H32 166 (section 1.1). The ordinary FIFO writes and data port reads keep the old search (`NextFreeSlotMode = 2`; `--slot-set nextslotfix=1` applies the fix to them too): with it 21 roster golden frames changed, because the old search's extra slots at line starts are part of what the fitted timing needed; those paths were not re-fitted.

**Why the golden frames moved.** A fill of 64 KB at boot (Sonic 1 does two) now takes the 320 lines the hardware takes, so every roster game's boot is 1 to 5 frames longer, and the same pictures appear that many frames later. `--md-golden --shift` shows it: all 21 moved checkpoints were found again at +1 to +5 frames with the pinned hash unchanged, so the oracle-verified pictures are still produced; the checkpoints were moved (`--move-shifted`). One frame between them (Sonic 1 frame 89, the white flash after the SEGA logo) does not appear at all in the new sequence and is not a golden frame; it was not compared with the oracle (a capture of Sonic's boot frames was queued).

**H32 flicker.** See section 6: the last-line dots are matched (identical picture at a matching frame), the frame-to-frame drift is not.

## 8. The library census (`--md-census`)

130 of 158 games (every sixth ROM of the library, offset 1, 900 frames each under the sweep's pad script, 900 frames; a first run was lost when D&D froze it, a second over the whole sixth was started after the fix). Games using a feature in at least 2 percent of their frames: window 54 (42 percent), horizontal scroll per line 43, H32 41, horizontal scroll per cell 21, HINT 27 (19 take it 20 times or more), shadow/highlight 17 (13 percent), plane width 128 cells 25 and height 128 cells 5, V30 3, column scroll 3, **interlace 0, left column blank 0**. Registers written while a picture line is drawn: any register 128 of 130, CRAM 110, VSRAM 81, VRAM 125. Raster games (HINT taken at least 20 times, with mid-frame register writes): Moonwalker, Ayrton Senna's Super Monaco GP II, Bastard!!, OutRunners (70,000 CRAM writes in 900 frames), Capt'n Havoc, Putter Golf, Chase HQ 2, Road Rash II (R7 backdrop colour every HINT), Crystal's Pony Tale, Slaughter Sport, Sorcerer's Kingdom, Spot Goes to Hollywood, Starflight, Super Hang-On (R0 writes), Test Drive 2, Two Crude Dudes, Vectorman 2, Warlock, Gadget Twins; shadow/highlight users: Mig-29 Fulcrum, Arnold Palmer Golf, NBA Showdown 94, Bastard!!, Beast Wrestler, PGA Tour Golf 3, Powerball, Chiki Chiki Boys, Tommy Lasorda Baseball, Vectorman 2, FIFA 98 and others. What this says about the work: the shadow/highlight rule (13 percent of games) and the window and per-line scroll rules (42 and 33 percent) were worth the effort; interlace and the left column blank are not used by any game in this sample, so the app's interlace presentation and the oracle's left-blank quirk matter little; the HINT and mid-frame write timing (every game writes registers during the picture) is where the interrupt-acceptance difference of section 4 would show.
