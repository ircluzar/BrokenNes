# Sega track B3a: Genesis VDP accuracy, round 3 (record, stopped early by the usage cap)

Branch `worktree-agent-a6f86a8f278165ee4`, 2026-10-09. Only mission item 1 (probing the slot model) and a part of item 2 (the known residue) were worked on. Items 3 (interlace, VSRAM quirk, sprite cache,
window edge cases, sprite shadow/highlight, 128-cell/V30 oracle frames, display disable, HV jump), 4 (real-game checks) and 5 (speed) were **not started**. Everything below was measured on BlastEm 1.0.0 run as a program.

## Tools built (all in `Workshop/Sega/oracle-tools/md-picture/`, BlastEm and results stay outside git)

| File | What |
|---|---|
| `asm68k.py` | a text 68000 assembler (labels, rept, tables, vectors, ROM image maker), self-checked against hand-encoded opcodes |
| `probes.py` | the probe ROM sources: HV counter scans, register-write pixel probes, FIFO release and stall probes, DMA resume probes (`dma_hv`, `dma_grid`, `replica_hv`), the direct colour demo replica, Nemesis FIFO wait-state replicas, iteration clock |
| `oracle.py`, `runprobe.py` | run a probe on both emulators; the **RAM channel**: BlastEm paused, quick-save read, 64 KB work RAM is the plain big-endian byte stream at state offset `$10941` (odd offset; the first attempt read it as little-endian words at `$10942` and was off by one byte, found because a probe's first result appeared as the last), the **picture channel**: its screenshot (320x240, picture rows start at row 9) |
| `slotmap.py`, `slotcmp.py` | colour-coded probes (every DMA word is a colour: R=n&7, G=n>>3&7, B=n>>6&7) make the picture a map of where each word landed; `slotcmp` diffs rows as run lists |
| `bl.ps1` | now takes a private working root (`BN_ORACLE_ROOT`) with its own `blastem\` copy whose `default.cfg` points screenshots and saves into the folder |
| `Workshop --md-run --slot-set "name=v;..."` | experiment hook that overrides slot-table and DMA/FIFO parameters for one run (names in `MdRunCli.ApplySlotSet`), the fitting handle |

## Hardware facts found (each by a probe, numbers are BlastEm's)

1. **The picture starts 12 H-counter ticks after the counter's 0** (H40: pixel 0 at tick 12 = 192 master clocks; H32: 240 clocks). Found by setting R7 right after an HV read (BlastEm's dot lands 24 pixels left of ours). Before, the VDP assumed pixel 0 = counter 0. Now `MdVdp.PixelOrigin`; the renderer, dots and slot tables use it. In H32 a blanked line is then one uniform 20-clock grid of 171 slots with pixel 0 = slot 12.
2. **Slot windows.** Slot tables are per H-counter tick (the 211-value sequence in H40, 171 in H32) from the counter map, windowed from the V counter step (offset 2640) to the next step; negative offsets are the previous line's end. A drawn H40 line has 18 free slots: 15 visible (pixels 12 44 60 76 108 124 140 172 188 204 236 252 268 298 300) and three before pixel 0 at ticks **`$E8` (end of the previous line), `$02`, `$0A`** (measured: the old table put them before the V step, BlastEm after it). A blanked H40 line has 198 free slots of 210: **six refresh pairs** at slots 26/29, 59/62, 90/93, 122/125, 154/157, 186/189 (a free slot pair between the two refresh slots, so the gaps in the dots are single slots 6 pixels apart, not adjacent pairs as the old table had).
3. **DMA (68000 to VDP) is not slot-by-slot.** It fetches a word every 16 clocks while the FIFO has room (from about 56 clocks after the command, `DmaStartDelay`), the writes happen in slots; the 68000 is released when the **last word is fetched**, not written (resume times of 1..700-word DMAs in blanked lines equal BlastEm's: 62 of 81 exact, 79 within one tick; 61 of 64 FIFO-synchronised picture-line cases within one tick, markers within 1 pixel). While the display is off the engine keeps at most two of its words in the FIFO; with it on, up to four (fits both scoreboards, cause unknown).
4. A 68000 held by a full FIFO or a finished DMA runs again about one slot (16 clocks) after the slot.
5. 68000 refresh: BlastEm stalls 2 cycles per 128 (same rate as ours, HV-scan 85.334 vs 85.340 clocks per 12-cycle sample); **whether the period follows the master clock or counts executed cycles matters** (below).
6. The oracle's power-on: its first HV read in an unsynchronised probe is 428 master clocks earlier in the line than ours (`--slot-set boot=-428` makes 2297 of 3000 samples' H equal; no other single parameter improved it).
7. At the end of the last picture line BlastEm already has blank-type slots from about pixel 246 (H32): the slot window of the next line starts later than the V counter step there (ours starts it at 2640; 2720 to 2800 leaves 4 pixels, not zero).

## Results (default build, tip of this branch)

| Check | Result |
|---|---|
| `--md-pic-test` | 93 of 93 (two pixel-timed checks now add `PixelOrigin`) |
| `--md-test` | 144 of 144 (143 + a check of the pixel origin; one B2b sound check's upper peak bound 7500 raised to 7900 with the reason: the band-limited square wave's overshoot moves with the 68000's start time) |
| Nemesis VDP FIFO test (page with test 16) | all ten wait-state groups pass; it is knife-edge: changing `DmaStartDelay`, the release latency or the refresh model flips groups 7 and 9 (they need a full FIFO state after a stall, a window a few clocks wide); the fitted values pass, 60 or the time-based refresh fail |
| `--md-golden` | 70 of 75 frames match unchanged: all ten roster games, Titan Overdrive 1 (13 frames) and 2 (10 frames), vctest, window tests, the FIFO page. **Changed and re-recorded:** the direct colour DMA demo (3 frames) and Nemesis' CRAM flicker H32 (2 frames; was oracle-identical, now 7 pixels differ on the last picture line at x 240-254; entry relabelled `regression`, see fact 7) |
| Direct colour DMA, before and after | the demo with an index-coded image (patched ROM) vs BlastEm: **differing rows 224 of 224 before, 0 after only with the time-based refresh** (`--slot-set timerefresh=1`); with the default (cycle-counting refresh) the demo is one word (2 pixels) off; the replica of its lead-in (13 FIFO writes, 4 nops, DMA with the display switched off) is pixel-identical with both |
| Not run on the final tree | `--m68ktest`, `--sega-contracts-test`, `--z80test --selftest`, `--sega-test`, `--ym-oracle-test`, `--smstest`, desktop smoke scripts (stopped early; none of these touch code changed here except `--sega-test`/smoke which build a Genesis session) |

Proof the verifiers can fail: the golden suite and `--md-test`/`--md-pic-test` flagged this work's own timeline changes (every regression above was found by them); the replica probes failed 224 of 224 rows until the DMA model was fixed. The picture and RAM channels were validated by a probe whose answer was known (the counting pattern, and a probe whose first result showed up last).

## Open and half-done (exact next steps, in order)

1. **VINT timing (from B3b's finding):** `StartNextLine` sets `vintTime = lineStart + VintDelay` at line 224, but the 788 clocks are counted from the VBlank flag at line 223 offset 2640; handler-entry HV is BlastEm `$E023`, ours `$E050`. A local `vintTime = lineStart + 40` gave `$E027` / `$E022` and left the Sonic 1 comparison unchanged. Do this first, then rerun `--md-golden`.
2. **Direct colour DMA and the refresh model.** `--slot-set timerefresh=1` (refresh requests every 896 master clocks, independent of what the 68000 does) makes the real demo pixel-identical, but it breaks the Nemesis groups unless the release latencies are split: `release=20;dmarelease=12` passes Nemesis with it, and then Overdrive 2's frames 1535-3709 change (they match the oracle in the default build). Find the refresh rule (when the stall falls relative to the line) with the iteration-clock probe (`probes.iteration_clock`, CPU-only loop differs by 0.6 tick per iteration) and the from-reset HV probe (`boot=-428`), then flip the defaults. The remaining fitted values live in `MdVdp`/`BOARD_MD` statics (`DmaStartDelay`, `DmaFetchClocks`, `DmaFifoLimit`, `DmaBlankLimit`, `ReleaseLatency`, `DmaReleaseLatency`, `TimeBasedRefresh`, `RefreshPeriodClocks`, `BootOffsetClocks`, `SlotTable.*`).
3. **H32.** The tail slots of a drawn H32 line measured in the oracle are ticks `$E9`, `$F4`, `$02`, `$0B` (`--slot-set preh32=148,159,2,11`; the table in the code still has the old guesses `2,5,8,11`: with the measured ones the CRAM flicker frame moves from 400 to 399 and 4 pixels still differ); the blank-line pattern in H32 is unmeasured (`dcd32.py` printed nothing; rerun), the H32 DMA fetch period and the window cut for H32 are guesses. CRAM flicker H40 phase, Overdrive 1 scenes 4, 6, 12 and Overdrive 2's differing frame were **not** compared in this round.
4. Mission items 3, 4 and 5 untouched.

## Shared files touched

`Windows/NesEmulator/md/BOARD_MD.cs` (small: time-based refresh option, power-on offset, DMA release latency, read trace shows the clock), `Workshop/Sega/MdSoundTest.cs` (one bound), `Workshop/Sega/MdBoardTest.cs`, `MdPictureTest.cs` (timing checks), `Workshop/Sega/MdRunCli.cs` (`--slot-set`), `Workshop/MdGolden/md_golden.json`. Dashboard not updated (stopped early).
