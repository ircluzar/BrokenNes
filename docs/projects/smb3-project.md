# SMB3 APU Compatibility Project

Goal: Make Super Mario Bros. 3 boot & run correctly on modern/feature cores (focus: `APU_FMC`) without falling back to legacy `APU_LQ`.

## Current State
Only `APU_LQ` boots SMB3. All more accurate variants (`APU_FMC`, `APU`, `APU_LQ2`, etc.) fail during early initialization. `APU_LQ` is permissive: it has no DMC implementation, no frame IRQ generation, simplified frame sequencer timing, `$4015` read returns last written value, auto-enables channels, simplified sweep negate logic. These omissions likely mask a power‑on timing defect in the accurate cores rather than representing the correct hardware behavior.

## New Root Cause Synthesis (Recent Research)
Research into the classic “red clouds” SMB3 bug (caused by incorrect APU frame counter startup) strongly suggests our boot failure shares the same root: incorrect emulation of the hardware’s implicit power‑on write to `$4017` roughly 9–12 CPU cycles before the first instruction. On real hardware the frame counter effectively starts as if `$00` were written (4‑step mode, IRQs enabled) and its first frame IRQ appears at a deterministic phase relative to CPU & PPU. If we start in the wrong mode, wrong phase, or raise an unexpected early frame IRQ (or suppress an expected one), SMB3’s tightly sequenced early init can diverge (e.g., palette / stack / IRQ handling), leading to a hang or crash before observable video progression. `APU_LQ` masking frame IRQs altogether avoids the *symptom* (unexpected early IRQ) but not the underlying accuracy gap.

## High-Level Strategy
1. Reproduce & log first-frame timing (CPU cycles 0–30000) for `APU_FMC` vs a synthetic "ideal" model implementing the implicit `$4017=$00` power‑on write + correct 4-step schedule.
2. Implement a precise power‑on path: simulate `$4017` write (with its 3–4 cycle application delay) 9–12 cycles pre-CPU-start; ensure frame counter internal tick offset matches hardware references.
3. Verify IRQ sequencing: no spurious frame IRQ before expected tick; flag remains until acknowledged; suppression (bit 6) semantics correct.
4. Only after frame counter alignment succeeds, reintroduce DMC, status flag, and sweep complexities one subsystem at a time.

## Updated Working Theories (Ordered by Probability of Fixing Boot)
Each has: rationale, experiment, instrumentation needed, status checkbox.

### 1. Missing / Mis-phased Implicit `$4017=$00` Power-On Write
Rationale: Most authoritative documentation ties SMB3 anomalies to failure to simulate the hardware’s implicit frame counter reset/write a few cycles pre-boot. Without correct phase, first frame IRQ timing is wrong.
Experiment: Add a dedicated `Apu.InitializePowerOn(cpuCycleOffset= -10)` that queues a `$4017=$00` write effect (with 3–4 cycle delay) before cycle 0. Run SMB3; if it progresses further / boots, confirm by toggling feature off.
Instrumentation: Log at boot: (a) CPU cycle when power-on write effect latched; (b) first quarter/half-frame events; (c) first frame IRQ assert & clear.
[ ] Implement power-on write simulation & test.

### 2. Frame Counter Phase Offset (First Sequencer Step Misaligned)
Rationale: Even with implicit write, using the wrong base cycle (e.g., starting counting at cycle 0 instead of the hardware’s post-write alignment) shifts quarter/half-frame events & IRQ.
Experiment: Parameterize initial frame counter tick offset; binary search offsets (0..20 cycles) to find a phase that lets SMB3 continue. Expect a very narrow valid window.
Instrumentation: Timeline diff of expected vs actual event cycles (quarter at 3729?, half at 7457?, etc.).
[ ] Add adjustable `FrameCounterInitialOffset` + trace.

### 3. Incorrect 4-Step Sequence Event Timing (3728/3729 Pattern / NTSC specifics)
Rationale: If event schedule (envelope & length counter clocks) is off by even one CPU cycle early, a SMB3 spin-wait observing status ($4015) may not exit.
Experiment: Cross-check against known test ROM timing; optionally drop in a reference table from NESDev; compare logs.
Instrumentation: Emit event indices with absolute CPU cycle numbers; diff to reference.
[ ] Add reference schedule & validator.

### 4. Early / Spurious Frame IRQ Assertion
Rationale: A premature frame IRQ could vector to SMB3’s IRQ handler during palette / zero page init, corrupting state.
Experiment: Force-disable frame IRQ generation while keeping frame sequencer functional; if SMB3 then boots (already partly tested), reinforces relation. After implementing proper power-on, re-enable and confirm no early IRQ.
Instrumentation: IRQ assert/clear log + CPU PC at interrupt entry.
[x] Toggle exists (frame IRQ suppression). Need post-fix retest.

### 5. Frame IRQ Flag Clear Semantics ($4015) Incorrect
Rationale: Clearing on read too early (or failing to clear) may wedge a poll loop or create re-entrant IRQ.
Experiment: Delay clear until after CPU acknowledges (one cycle after read) vs current behavior; observe difference.
Instrumentation: Log `$4015` reads including returned bits & post-read flag state.
[x] Delayed clear experiment implemented; pending correlation with new power-on logic.

### 6. `$4015` Read Content Divergence (Status Bits vs Latch)
Rationale: SMB3 might expect certain bits (frame IRQ, DMC) in a stable state during early loops; over-accurate fluctuation vs latch behavior can stall boot.
Experiment: Force latch-only read (LQ behavior) WITH accurate frame counter; see if boot difference disappears once frame counter is correct (likely becomes irrelevant then).
Instrumentation: Compare sequences of values returned in first 200 reads.
[x] Latch-only mode exists; need retest after implementing theory #1.

### 7. DMC Engine Side-Effects (Fetch / IRQ / Memory Reads)
Rationale: Previously top theory; still plausible secondary disruptor if frame counter becomes correct but boot still fails. LQ lacks DMC so problem masked.
Experiment: After fixing frame counter, reintroduce DMC incrementally: (a) enable registers no fetch; (b) fetch no IRQ; (c) full behavior.
Instrumentation: Log DMC fetch cycles, addresses, IRQ asserts.
[x] Bypass & phased re-enable scaffolding present.

### 8. Channel Enable / Length Counter Initial State Differences
Rationale: Hardware power-on leaves length counters halted until first enable; if we differ, status polling could mis-sync.
Experiment: Snapshot counters after first 1000 cycles vs reference; optionally mimic LQ auto-enable to test invariance once frame timing fixed.
Instrumentation: Log length counter loads & decrements.
[x] Auto-enable toggle exists; verify necessity after theory #1 fix.

### 9. Sweep / Envelope Unit Power-On Randomization Missing
Rationale: Hardware may start envelopes at specific reset states; deterministic zero may affect loops expecting a non-zero start.
Experiment: Introduce randomized (but bounded & reproducible via seed) initial envelope/sweep registers; test if any change (likely minor once timing fixed).
Instrumentation: Dump initial sweep/envelope params.
[ ] Add randomized power-on option (seeded).

### 10. DMC Delta Counter Initial Value / `$4011` Handling
Rationale: If delta counter init not 0x40 or `$4011` write sequence mis-modeled, status bits may differ early.
Experiment: Log initial delta + first `$4011` write timing.
[ ] Verify + log.

### 11. Audio Sample Output Loop Influencing Cycle Budget
Rationale: If audio mixing loop advances CPU/APU clocks slightly differently than expected inside first frame, event phase drifts.
Experiment: Temporarily stub mixer to no-op; ensure identical frame sequencing.
[ ] Add mixer bypass toggle for boot window only.

### 12. Soft Reset Path Divergence (Warm Reset Not Reinitializing Frame Counter Correctly)
Rationale: Emulated reset may reuse prior frame counter phase rather than hardware-equivalent path.
Experiment: Compare cold vs soft reset logs; ensure both simulate implicit `$4017` write when appropriate.
[ ] Add explicit soft vs cold power-on verification.

## Retired / Reordered Prior Theories
Original list emphasized DMC first; evidence now suggests DMC issues are secondary to frame counter power-on phase. DMC-related hypotheses remain (#7, #10) but de-prioritized until frame sequencing correctness is established.

## Cross-Cutting Diagnostic Tasks
[ ] Unified boot trace (first 30k CPU cycles): events -> CSV (cycle,event,detail)
[ ] Configurable trace filters (frame_irq, dmc_fetch, 4015_read, 4017_write)
[ ] Reference timing injector: produce expected event cycles for comparison
[ ] Minimal diff reporter highlighting earliest divergence cycle
[ ] Palette safety check: detect writes to $3F00–$3F1F during unexpected IRQ nesting (optional visual debug overlay)

## Implementation Checklist (Condensed)
Power-On & Frame Counter:
- [ ] Implement implicit `$4017=$00` simulated write pre-cycle0
- [ ] Add adjustable initial offset & delay semantics
- [ ] Validate event schedule vs reference
IRQ Semantics:
- [x] Suppression toggle
- [x] Delayed clear option
- [ ] Confirm correct flag persistence window
Status & Reads:
- [x] Latch-only mode
- [ ] Comprehensive `$4015` read logger (first 200 reads)
DMC Phased Bring-Up:
- [x] Master bypass
- [ ] Stage 1: registers only
- [ ] Stage 2: fetch w/o IRQ
- [ ] Stage 3: full
Misc:
- [x] Simplified frame sequencer experiment
- [x] Sweep bypass
- [x] Auto-enable channels toggle
- [ ] Randomized envelope/sweep init (seeded)
- [ ] Mixer bypass toggle for boot

## Success Criteria
Primary: SMB3 boots past title/demo into gameplay using `APU_FMC` with accurate frame counter + (eventually) full DMC & IRQ functionality enabled.
Secondary: No reliance on latch-only `$4015` or disabled subsystems once final; pass relevant NES APU timing test ROMs (apu_test, frame_counter, dmc_irq, etc.).
Tertiary: Consistent boot across cold & soft reset; no divergence after 60 frames.

## Follow-Up Once Booting
- Re-enable all accuracy paths sequentially ensuring regression-free behavior.
- Remove or compile-guard invasive debug toggles.
- Add automated regression test: run SMB3 boot for N frames, assert deterministic log hash for frame events.
- Document final timing model in `core-lifecycle.md`.

## Notes
Focus first on eliminating *timing uncertainty* (power-on + frame counter phase). Avoid layering multiple speculative fixes; isolate cause/effect with instrumentation. `APU_LQ` is a masking reference, not a correctness oracle.

## Appendix: Expected 4-Step (NTSC) Event Reference (For Logging Alignment)
Cycle counts (CPU cycles after the implicit power-on write takes effect; confirm exact offsets with authoritative docs):
- Step 0: 3729 (Quarter: envelope + linear) (Half at 7457)
- Step 1: 7457 (Quarter + Half)
- Step 2: 11186 (Quarter)
- Step 3: 14915 (Quarter + Half + (optional) Frame IRQ just before/after sequence end in 4-step unless inhibited)
Adjust exact +1 cycle variations (3728/3729 pattern) per hardware tests; log which variant chosen.

Maintain a single source-of-truth structure (e.g., table or array) so experimental offsets are controlled & logged.
