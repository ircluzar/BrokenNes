# Backbone Performance Optimization Project

Purpose: Track hypotheses ("theories") for performance improvements in the emulator backbone (NES frame loop, Bus, Cartridge, registry, scheduling, serialization, audio hand-off, config). Cores (CPU/APU/PPU internal algorithms) are assumed already optimized. Each block below is an actionable unit with: rationale, estimated impact, effort, risk, concrete steps, validation plan, and dependencies. Check off when implemented & merged.

Impact legend (rough total frame time potential, assuming current optimized cores):
- High: >5% total frame time or large remaining hotspot
- Medium: 1–5%
- Low: <1% (may stack)
- Micro: <0.3% each (only if trivial or in extreme hot paths)
- Speculative: Needs measurement / context-sensitive

Effort legend:
- XS: <30 min
- S: ~1–2 hrs
- M: half day–1 day
- L: multi‑day / refactor

Risk legend:
- Low: Impossible to change correctness (pure structural / early-exit / dead code gating)
- Med: Could subtly affect timing or state under edge cases
- High: Invasive or timing‑critical; needs regression ROM tests

Validation patterns (referenced shorthand):
- Bench.Frame: existing frame benchmark (120 * weight)
- Bench.Instr: instruction burst benchmark
- Bench.Audio: audio pacing benchmark
- ROM.Timing: timing accuracy ROM set (PPU/APU/IRQ tests)
- ROM.Mapper: mapper behavior tests
- Perf.Stat: collect reads/writes/batches counters pre/post

---

## 1. Memory Access Path Improvements

### 1.1 Page Table Structure SoA Refactor
[ ] Status
- Category: Memory / Bus
- Impact: High (2–4%)
- Effort: M
- Risk: Med (must preserve mirroring correctness)
- Rationale: Current `Page` struct introduces nullable checks + bool per access. Splitting into parallel arrays (data[], offsets[], writableMask) improves cache & branch predictability.
- Steps:
  1. Replace `Page[] pages` with: `byte[][] pageData = new byte[256][]; int[] pageOffset = new int[256]; ulong writableBitsLow/high (bitmask).`
  2. Adjust BuildPageTable accordingly.
  3. Rewrite Read/Write fast path to inline mask test: if (pageData[hi] != null) {...}
  4. Writable check via (writableMask & (1UL << hi)) != 0.
  5. Measure Bench.Frame & Perf.Stat deltas.
- Validation: Bench.Frame baseline vs post; ROM.Mapper (mirroring); targeted tests reading/writing boundaries 0x07FF/0x0800, 0x1FFF.
- Dependencies: none.

### 1.2 Broaden Direct Mapping to PRG RAM ($6000–$7FFF)
[ ] Status
- Impact: Medium (0.5–2%)
- Effort: S
- Risk: Low
- Rationale: PRG RAM is linear & stable; currently goes through ReadSlow/WriteSlow -> mapper.
- Steps: Map pages 0x60–0x7F to prgRAM with offsets; leave bank‑switchable ROM pages unmapped.
- Validation: ROM.Mapper battery save tests; Bench.Instr memory heavy scenario.

### 1.3 Fast Linear PRG ROM Windows (Mapper Cooperation)
[ ] Status
- Impact: High (up to 5% if instruction fetch dominated by banked reads)
- Effort: L
- Risk: High (bank switching correctness)
- Rationale: Many mappers expose fixed banks (e.g., upper 16KB). Provide mapper API `GetLinearBankSpan(int page)` returning `byte[]?` & offset for static mapping.
- Steps: Define optional interface `ILinearMapperWindow`; adapt common mappers (0,1,2,3,4) for stable banks; patch page table on bank changes.
- Validation: ROM.Mapper bank switch tests; Bench.Instr.

### 1.4 Inline Low-Byte Precomputation
[ ] Status
- Impact: Micro
- Effort: XS
- Risk: Low
- Rationale: Avoid `(address & 0xFF)` per access by passing separate low byte or caching in CPU core.
- Steps: Modify CPU fetch path to compute `lo = (byte)addr`; adjust bus fast path helper.
- Validation: Microbench vs baseline; ensure no regression.

### 1.5 Unsafe Pointer Fast RAM
[ ] Status
- Impact: Medium (2–4% JIT / WASM variable)
- Effort: M
- Risk: Med (unsafe code, bounds safety)
- Rationale: Remove CLR bounds checks in internal RAM reads/writes.
- Steps: In Read/Write fast path if pageData == ramPtr, perform unsafe pointer index; enable via `#if UNSAFE_RAM_FASTPATH` guard.
- Validation: Bench.Instr; memory fuzz (random writes/reads compare with safe path build).

### 1.6 Instrumentation Gating
[ ] Status
- Impact: Low (0.5–1.5%)
- Effort: XS
- Risk: Low
- Rationale: Always incrementing counters costs atomic-like operations on tight loops.
- Steps: Introduce `static bool InstrumentationEnabled`; wrap increments or compile out with `#if`.
- Validation: Bench.Frame with both on/off; ensure existing tooling still works when enabled.

### 1.7 Expand OAM DMA Fast Path Sources
[ ] Status
- Impact: Low–Medium (0.2–1%) burst dependent
- Effort: S
- Risk: Low
- Rationale: Currently only internal RAM. Extend to PRG RAM & linear PRG ROM windows.
- Steps: Extend detection in `FastOamDma` using new linear mapper API; fallback otherwise.
- Validation: Sprite heavy ROM; compare frame time & correctness (OAM content hash).

### 1.8 Read/Write Slow Handler Table
[ ] Status
- Impact: Medium (1–2%)
- Effort: M
- Risk: Med (correct register mirroring)
- Rationale: Replace range if-chains with prebuilt table of delegates or small structs per high page.
- Steps: Build `ReadHandlers[256]`, `WriteHandlers[256]` on bus init; entries for PPU mirrored pages call small inline; APU/IO entries handle special cases.
- Validation: ROM.Timing (PPUSTATUS), controller tests, APU register tests.

### 1.9 Reorder / Collapse Branches in WriteSlow
[ ] Status
- Impact: Micro
- Effort: XS
- Risk: Low
- Rationale: Frequent special writes ($4014,$4016) should appear before broader ranges for prediction.
- Steps: Reshuffle conditional order.
- Validation: Basic functional tests.

### 1.10 Remove Double Instrumentation on DMA
[ ] Status
- Impact: Micro
- Effort: XS
- Risk: Low
- Rationale: OAM DMA lumps 513 cycles; ensure only one batch flush accounted if appropriate (avoid overcount skew).
- Steps: Check `CountBatchFlush` interactions in OAM path.
- Validation: Perf.Stat consistency.

---

## 2. Frame Loop & Batching

### 2.1 Remove Per-Frame Feature Flag Branch (Event Scheduler)
[ ] Status
- Impact: Low (0.2–0.5%)
- Effort: XS
- Risk: Low
- Rationale: Branch on `EnableEventScheduler` each RunFrame; use delegate or conditional compilation.
- Steps: Assign `Action runFrameImpl` once when flag changes.
- Validation: Bench.Frame with both modes.

### 2.2 Simplify Executed Cycle Tracking
[ ] Status
- Impact: Micro
- Effort: XS
- Risk: Low
- Rationale: `executed` used only for overshoot; overshoot can be inferred; remove variable.
- Steps: Use local accumulator only if > target.
- Validation: Frame pacing equivalence.

### 2.3 Adaptive Threshold Fast Math
[ ] Status
- Impact: Micro
- Effort: XS
- Risk: Low
- Rationale: Replace conditional ladder with branchless clamp: `dynamicThreshold += signDelta & 2` style or simple bounded add.
- Steps: Implement and benchmark.
- Validation: Bench.Frame.

### 2.4 Precomputed Extra Cycle Distribution Table
[ ] Status
- Impact: Micro
- Effort: XS
- Risk: Low
- Rationale: Replace accumulator compare with table of 60 booleans for +1 cycle frames.
- Steps: Build static bool[]; index with frame counter % 60.
- Validation: Verify total cycles over 60 frames equals +33.

### 2.5 Merge FlushBatch Multiply
[ ] Status
- Impact: Micro
- Effort: XS
- Risk: Low
- Rationale: Ensure (cycles*3) optimized; explicitly use `(c<<1)+c` if JIT underperforms on WASM.
- Steps: Add conditional #if WASM directive.
- Validation: Inspect emitted IL / WASM; microbench.

---

## 3. Serialization & State

### 3.1 Cache Reflection Metadata in PlainSerialize
[ ] Status
- Impact: Low (save latency) / negligible runtime
- Effort: S
- Risk: Low
- Rationale: Avoid repeated `GetFields`/`GetProperties` per save; cache arrays per Type.
- Steps: Static ConcurrentDictionary<Type,Member[]>; prewarm on first save.
- Validation: Measure SaveState time for large states before/after.

### 3.2 Array Pooling for Large JSON Buffers
[ ] Status
- Impact: Low
- Effort: S
- Risk: Med (pool misuse leaks)
- Rationale: Reduce GC allocations for large byte arrays printed as number lists.
- Steps: Use `ArrayPool<char>` for StringBuilder-like writer.
- Validation: GC allocation profile.

### 3.3 Base64 Encoding Option for Large Byte Arrays
[ ] Status
- Impact: Low (I/O size) / faster parse
- Effort: M
- Risk: Med (compat with legacy states)
- Rationale: Shrink JSON & parse faster; toggle via version field.
- Steps: Add version; detect & decode.
- Validation: Load old states; new saves load; measure size.

### 3.4 ROM Hash Skip When Unchanged
[ ] Status
- Impact: Micro
- Effort: XS
- Risk: Low
- Rationale: Recomputes hash each save; skip if same reference & length.
- Steps: Track `lastRomRef` & `lastHash`.
- Validation: Save twice, ensure second faster.

---

## 4. Reflection / Core Registry

### 4.1 Replace LINQ in Type Scans with Manual Loops
[ ] Status
- Impact: Low (startup) / reduces WASM code size
- Effort: S
- Risk: Low
- Rationale: Cut allocations & AOT bloat.
- Steps: Manual foreach building lists.
- Validation: Startup profiling.

### 4.2 Cache Constructors as Delegates
[ ] Status
- Impact: Low (core swap latency)
- Effort: S
- Risk: Low
- Rationale: Avoid reflection invoke overhead on swaps.
- Steps: Use `Func<Bus,TIface>` compiled or Expression.
- Validation: Measure core swap time.

### 4.3 Devirtualize Interface Calls (Abstract Base or Local Copy)
[ ] Status
- Impact: Medium (1–3%)
- Effort: M
- Risk: Med (API refactor)
- Rationale: Interface dispatch in hot loops; either convert to abstract classes or cache concrete typed reference local before loops.
- Steps: Evaluate CPU core call sites; ensure JIT inlining.
- Validation: Bench.Instr delta.

---

## 5. Idle Loop Skip & Instrumentation

### 5.1 Compile-Time Remove Idle Loop Tracking When Off
[ ] Status
- Impact: Low (branch removal) / reduces noise
- Effort: S
- Risk: Low
- Rationale: Even disabled, tracking fields may be updated.
- Steps: Wrap instrumentation code in `#if IDLE_SKIP`.
- Validation: Build variants; Bench.Frame.

### 5.2 Adaptive Idle Skip Exponential Backoff
[ ] Status
- Impact: Low
- Effort: S
- Risk: Med (timing variance)
- Rationale: Reduce per-loop checks for long stable loops.
- Steps: Iteration budget grows: 1,2,4,...capped.
- Validation: ROM.Timing (ensure no missed interrupts); perf vs baseline.

### 5.3 APU Status Loop Skip Refinement
[ ] Status
- Impact: Low–Medium (depends on ROM)
- Effort: S
- Risk: Med (IRQ edge timing)
- Rationale: Tight polling on $4015 may need stricter guards.
- Steps: Add cycle-bound window; revert to precise mode when pending IRQ flag transitions.
- Validation: IRQ edge test ROMs.

---

## 6. Audio Buffer Path

### 6.1 Single-Pass Drain with Backlog Cap
[ ] Status
- Impact: Low
- Effort: XS
- Risk: Low
- Rationale: Currently drains (drop) then separate fetch; unify.
- Steps: Add `GetCappedAudioSamples(request, backlogCap)` API.
- Validation: Audio latency stable, no GC increase.

### 6.2 Span / ArraySegment Return Option
[ ] Status
- Impact: Medium (if copying large buffers)
- Effort: M
- Risk: Med (consumer changes)
- Rationale: Avoid copying into new arrays.
- Steps: Add alt API; gradually switch UI path.
- Validation: Compare throughput & memory allocations.

### 6.3 Ring Buffer Pooling
[ ] Status
- Impact: Low–Medium (long sessions / GC)
- Effort: M
- Risk: Med
- Rationale: Reuse fixed buffers to reduce LOH / fragmentation.
- Steps: Implement circular buffer; adapt writer/reader.
- Validation: Memory profiling over 10+ minutes.

---

## 7. Event Scheduler Enhancements

### 7.1 Adaptive Instruction Burst Size in Event Mode
[ ] Status
- Impact: Low–Medium (1–2%)
- Effort: S
- Risk: Low
- Rationale: Grow burst when next event far to amortize loop overhead.
- Steps: Inspect min distance; if > threshold, raise max burst temporarily.
- Validation: Bench.Frame with scheduler on.

### 7.2 Min-Heap Event Queue (When >3 Event Types)
[ ] Status
- Impact: Speculative
- Effort: M
- Risk: Med (complexity)
- Rationale: Current manual min selection trivial with 3 events; scales poorly if more added.
- Steps: Implement binary heap; compare overhead.
- Validation: Microbench with synthetic events.

---

## 8. Data Layout & Config

### 8.1 Bitfield Pack SpeedConfig Bools
[ ] Status
- Impact: Low (1–2% if many reads) / code size reduction
- Effort: M
- Risk: Med (readability)
- Rationale: Multiple bool loads can inflate memory traffic; pack into uint flags.
- Steps: Provide accessor methods `IsEnabled(flag)`; adapt call sites gradually.
- Validation: Bench.Frame; ensure semantic parity.

### 8.2 Hot Counter Struct Reorganization
[ ] Status
- Impact: Micro
- Effort: XS
- Risk: Low
- Rationale: Group hottest counters to reduce false sharing.
- Steps: Separate struct for frequently sampled metrics.
- Validation: Perf.Stat; minor.

---

## 9. Miscellaneous Micro Optimizations

### 9.1 Branch Ordering in Read/Write Slow
[ ] Status
- Impact: Micro
- Effort: XS
- Risk: Low
- Rationale: Prioritize highest frequency addresses.
- Steps: Empirically reorder.
- Validation: Perf.Stat branch counters (if available).

### 9.2 SaveState Log Elimination in Release
[ ] Status
- Impact: Micro
- Effort: XS
- Risk: Low
- Rationale: Remove stub function call per log call site.
- Steps: Surround with `#if DIAG_LOG`.
- Validation: SaveState timing.

### 9.3 State Digest Loop Fusion & Vectorization
[ ] Status
- Impact: Micro
- Effort: S
- Risk: Low
- Rationale: Combine two passes into one + Vector<T> for 64+64 sums.
- Steps: Use `System.Numerics` when length >= 64.
- Validation: Microbench.

### 9.4 Cached Crash Font Glyph Bitmaps
[ ] Status
- Impact: Negligible (run rarely)
- Effort: XS
- Risk: Low
- Rationale: Precompute patterns; only for completeness.
- Steps: Build dictionary<char,ulong> 3x5 bits.
- Validation: Visual crash output unchanged.

---

## 10. Architectural / Advanced Refactors

### 10.1 Linear Mapper Windows (See 1.3) – Consolidated Tracking
[ ] Status
- Impact: High
- Effort: L
- Risk: High
- Rationale: Major reduction in per-fetch overhead by eliminating mapper virtual call for stable banks.
- Steps: Merge with task 1.3; treat as umbrella epic.
- Validation: All mapper test ROMs + performance.

### 10.2 Instruction Macro-Batching with Event Boundaries
[ ] Status
- Impact: High (5–10%)
- Effort: L
- Risk: High (timing / interrupt ordering)
- Rationale: Let CPU core run until predicted event boundary reducing bus overhead & loop bookkeeping.
- Steps: CPU provides cyclesUntilBoundary(); run; flush once; update events.
- Validation: Timing ROMs & diff of cycle logs.

### 10.3 Fast Mode Build Variant
[ ] Status
- Impact: High (aggregate / code size)
- Effort: M
- Risk: Med (feature availability)
- Rationale: Strip unused cores, instrumentation, serializer reflection.
- Steps: Add compilation symbol FAST_MODE gating code.
- Validation: Build size comparison & Bench.Frame.

### 10.4 Mapper-Aware Prefetch / ICache Friendly Decode
[ ] Status
- Impact: Speculative
- Effort: L
- Risk: High
- Rationale: Batch fetch next N opcode bytes from linear bank into local buffer to reduce random memory touches.
- Steps: CPU core extension; fallback on bank boundary.
- Validation: Bench.Instr heavy loops.

---

## 11. WASM/AOT Specific

### 11.1 AggressiveInlining Audit
[ ] Status
- Impact: Low
- Effort: S
- Risk: Low
- Rationale: Ensure tiny helpers inlined; avoid over-inlining hot large methods.
- Steps: Inspect size; annotate selectively.
- Validation: Wasm size & perf.

### 11.2 [SkipLocalsInit] on Hot Methods
[ ] Status
- Impact: Micro
- Effort: XS
- Risk: Low
- Rationale: Avoid zeroing overhead when safe.
- Steps: Attribute on RunFrame/FlushBatch.
- Validation: Perf diff microbench.

### 11.3 Remove LINQ From Runtime Paths
[ ] Status
- Impact: Low
- Effort: XS
- Risk: Low
- Rationale: LINQ allocs; ensure only in startup code.
- Steps: Static analyzers / grep.
- Validation: Memory profile.

---

## 12. Validation & Benchmark Template
Use this template per completed task:
```
### Task X.Y Completion
- Baseline Frame(ms): <value> (iter=N)
- New Frame(ms): <value>
- Delta: -Z%
- Reads/Writes Delta: R% / W%
- Side Effects: (e.g., code size, memory, allocations)
- Regression Tests: PASS/FAIL summary
```

---

## 13. Cumulative Tracking
Maintain a separate summary table (update as tasks close):

| Task | Done | Est Gain | Measured Gain | Notes |
|------|------|----------|---------------|-------|
| 1.1  |      | 2–4%     |               |       |
| 1.2  |      | 0.5–2%   |               |       |
| 1.3  |      | up to 5% |               |       |
| 2.1  |      | 0.5%     |               |       |
| 3.1  |      | (save)   |               |       |
| 4.3  |      | 1–3%     |               |       |
| 10.2 |      | 5–10%    |               |       |
| ...  |      | ...      |               |       |

(Extend as tasks complete.)

---

## 14. Suggested Initial Execution Order (High ROI First)
1. 1.6 Instrumentation Gating
2. 1.2 PRG RAM direct mapping
3. 4.3 Devirtualize interface calls (local caching now, abstract refactor later)
4. 1.1 Page table SoA
5. 1.8 Handler table slow path
6. 1.7 OAM DMA fast path extension
7. 2.1 Delegate-based frame path split
8. 3.1 Serialize metadata cache
9. 10.3 Fast Mode build
10. 1.3 / 10.1 Linear mapper windows (larger epic)

---

## 15. Notes
- Always capture before/after instrumentation snapshot (Reads, Writes, BatchFlushes) to correlate macro time deltas with micro operation reductions.
- When stacking multiple micro tasks, re-run baseline after each to avoid attributing compounding gains incorrectly.
- Keep feature flags until confidence high; then collapse dead code paths to reclaim icache.

---

(End of document)
