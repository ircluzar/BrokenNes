# APU_SPD Migration Project

Goal: Migrate the legacy/high-speed APU core `APU_SPD` toward the higher accuracy feature set realized in `APU_LOW` while retaining optional performance shortcuts.

## Legend
- [ ] = Not started
- [/] = In progress (mark manually)
- [x] = Complete / already present in `APU_SPD`
- [~] = Partial (follow-up subtasks listed)

## Current Baseline Assessment
Implemented in `APU_SPD`:
- [ ] Correct noise LFSR tap selection (mode bit chooses bit6 vs bit1 tap)
- [ ] Post-mix low-pass + DC high-pass filtering (plus soft clip)
- [ ] Canonical length + noise period tables (missing DMC rate table; DMC channel absent)

Missing / Divergent (targets for migration):
- Nonlinear mixing LUT (pulse + TND)
- Full 32768-entry TND domain (requires DMC)
- Float-only LUT mixing path (removal of per-sample divides)
- Absolute-cycle frame sequencer timing model (7457 / 14913 / 22371 / 29829 [/ 37281])
- Immediate Q+H tick after $4017 write in 5-step mode
- Sweep mute prediction pre-check (prevent illegal period output noise)
- DMC channel (IRQ, looping, delta counter, sample fetch)
- Canonical DMC rate table
- Explicit documented TND LUT index layout (t<<11 | n<<7 | d)
- Regression & fidelity test harness (waveform and spectrum diff vs `APU_LOW`)
- Optional soft clip configurability / alignment with `APU_LOW` output scaling

## High-Level Phases
1. Core Functional Parity (timing + channel coverage)
2. Mixing Fidelity (LUT path + removal of divides)
3. Validation & Regression (tests, ROM verification, snapshots)
4. Performance Tuning / Optional Paths (config toggles, fast modes)

---
## Task Checklist
### Phase 1: Core Functional Parity
- [ ] P1: Introduce absolute-cycle frame sequencer (track `frameCycle` + `nextFrameEventCycle` like `APU_LOW`).
- [ ] P1: Implement immediate Quarter + Half frame tick when writing $4017 with bit7=1 in 5-step mode.
- [ ] P1: Add sweep mute prediction (`SweepWouldMute`) and integrate into pulse output gating.
- [ ] P1: Implement DMC channel core (delta counter, shift reg, sample buffer, IRQ/loop flags, memory fetch).
- [ ] P1: Add canonical DMC rate table (NTSC) and hook to timer period calculation.
- [ ] P1: Wire DMC enable/disable via $4015 with proper length counter semantics & IRQ flag clearing.
- [ ] P1: Add DMC IRQ request to CPU (`bus.cpu.RequestIRQ(true)`) when enabled and sample depletion with IRQ flag set.
- [ ] P1: Persist DMC state in save state object (serialization parity with `APU_LOW`). (Pending follow-up serialization extension)

### Phase 2: Mixing Fidelity
- [ ] P2: Add precomputed Pulse mixing LUT (size 31) (95.88 / (8128 / sum + 100)).
- [ ] P2: Add precomputed full TND LUT (16 * 16 * 128 = 32768 entries) (159.79 / (1/(t/8227 + n/12241 + d/22638) + 100)).
- [ ] P2: Add constant describing LUT index layout (t<<11 | n<<7 | d) with documentation comment.
- [ ] P2: Replace per-sample division mixing path with LUT lookup + accumulation.
- [ ] P2: Remove (or bypass) runtime divides behind a feature flag `UseLutMixing` (default on).
- [ ] P2: Normalize output scaling to match `APU_LOW` (remove tanh soft clip or make configurable).
- [ ] P2: Add configuration toggle to retain legacy soft clip for backwards compatibility / subjective preference.

### Phase 3: Validation & Regression
- [ ] P3: Integrate blargg APU test ROM harness (timing, sweep, length, IRQ) and record pass/fail.
- [ ] P3: Add automated PCM snapshot capture for a fixed deterministic ROM segment (e.g., 2 seconds) for both cores.
- [ ] P3: Implement spectrum (FFT) comparison script to flag >N dB deviations in key bands.
- [ ] P3: Add unit tests for sweep mute prediction boundaries (<8 and >0x7FF periods).
- [ ] P3: Add test verifying immediate double tick after $4017 write in 5-step mode.
- [ ] P3: Add test verifying DMC loop & IRQ timing against reference sequence.
- [ ] P3: Document known acceptable minor numeric deltas (tolerances) in README or doc section.

### Phase 4: Performance & Configurability
- [ ] P4: Add configuration flag to fall back to legacy per-sample math (diagnostics / debugging).
- [ ] P4: Benchmark CPU usage (cycles / ms) pre/post LUT adoption; record in docs.
- [ ] P4: Add optional reduced-size approximate TND LUT mode (sum-based) for WASM memory constraint scenario.
- [ ] P4: Provide dynamic switch at runtime between high-accuracy and high-speed modes without desync (state translation helpers).

### Cross-Cutting / Documentation
- [ ] DOC: Update this file with progress (mark completed tasks).
- [ ] DOC: Add inline XML documentation summaries for new public members.
- [ ] DOC: Add section to `core-lifecycle.md` explaining APU variant selection logic.
- [ ] DOC: Provide migration rationale & performance table (pre vs post) in docs.

---
## Partial / Completed Items (No Action Needed unless Enhancing)
- [ ] Noise LFSR tap logic matches hardware (retain).
- [ ] Basic low-pass + DC high-pass filters present (consider parameterizing coefficients).
- [ ] Length & noise period tables correct; will be fully complete once DMC rate table added (see Phase 1 tasks).

## Stretch Goals (Optional)
- [ ] SG: Add on-the-fly resampler to support alternate host sample rates without retuning APU timing.
- [ ] SG: Integrate cycle-accurate NTSC/PAL mode switching (separate rate tables, CPU freq constants).
- [ ] SG: Implement debug tracing hooks (ring buffer of recent APU events) toggled via config.

## Verification Strategy Summary
1. Unit & ROM tests after each Phase 1 change to avoid compounding timing errors.  
2. Waveform + FFT diffs after Phase 2 to guarantee mixing parity.  
3. Performance benchmarks before and after LUT path (Phase 4) to quantify gains.  

## Status Snapshot (initialize all unchecked)
(Keep this section concise; main detail lives above.)
- Core Parity: 80% (frame sequencer, sweep mute, DMC core done; save-state pending)
- Mixing Fidelity: 100% (LUT path & toggles implemented)
- Validation: 0% (pending)
- Performance: 10% (initial LUT integration; benchmarks pending)

---
_Last updated: 2025-08-16 – reflects implementation of frame sequencer revamp, DMC, LUT mixer, config toggles._
