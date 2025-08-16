# BrokenNes Performance Optimization Theories

> Purpose: Consolidated, prioritized catalogue of structural + micro performance improvements for BrokenNes based on prior scan (`docs/workpad/results.txt`) and a fresh code skim (`NES.RunFrame`, `Bus`, APU cores, memory access). Ordered top → bottom by (Impact × Likelihood) / Risk, with low‑risk high‑ROI items first. Use this as a living checklist; instrument before & after each change.

---
## Legend
- Impact: L (1–3%), M (3–10%), H (10–25%), VH (25%+ potential or multiplicative) overall frame time reduction potential (desktop+WASM aggregate) if baseline is unoptimized in that area.
- Effort: S (≤1h), M (half day), L (multi‑day), XL (multi‑week / staged).
- Risk: Low (unlikely to break timing / accuracy), Med (cycle timing sensitive; needs tests), High (significant refactor / correctness risk). 
- Priority Score (rough): (ImpactWeight / EffortWeight) adjusted down for Risk. Used only to sort initial ordering.

---
## Top Level Prioritized List (TL;DR)
1. Event / batch scheduler (collapse per‑instruction PPU & APU stepping)  (VH, M, Med)
2. Event‑driven APU timers (eliminate per‑CPU‑cycle loop)  (H, M, Med)
3. LUT + float‑only audio mixing (remove per‑sample divides)  (M–H, S, Low)
4. Fixed‑point frame timing (remove double math in `RunFrame`)  (M, S, Low)
5. Page table based Bus memory dispatch (replace cascaded ifs)  (H, M, Med)
6. Inline CPU RAM mirroring via page table (remove `& 0x07FF`)  (M, S, Low)
7. OAM DMA fast block copy (Buffer/Unsafe vs 256 bus reads)  (M, S, Low)
8. Audio buffer pooling / zero‑alloc sample fetch (`Span`/rental)  (M (GC), S, Low)
9. APU mixing + filter fusion (single biquad; adopt integer pacing)  (M, M, Med)
10. Central unified event scheduler (CPU run until nextEvent)  (H (synergy), M–L (if #1 foundation), Med)
11. CPU opcode dispatch table (function pointers / metadata arrays)  (M, M, Low)
12. Bus fast path inside CPU for <0x2000 + common PRG fetch  (M, S, Low)
13. Nametable mirroring precomputed tables  (M, S, Low)
14. PPU scanline (or sub‑scanline) batching  (H, M, Med)
15. APU integer Bresenham sample pacing  (S, Low, pairs w/ #2/#9)  (S, Low)
16. Audio nonlinear mix precomputed (with existing LUT)  (S, Low)
17. IRQ/NMI scheduled boundary (avoid per‑instr tests)  (S, Low)
18. SaveState source‑generated / cached serializer  (M (state-heavy use only), M, Low)
19. Reflection & logging stripping / `#if DEBUG`  (S, Low)
20. Remove per‑frame Console.WriteLine & guard debug prints  (S, Low)
21. Bitfield pack APU/PPU booleans  (S, Low)
22. ADC/SBC flag computation LUT  (S, Low)
23. Opcode predecode / tiny trace cache (if still CPU bound)  (M, L, Med)
24. DMC / channel event coalescing (advanced APU)  (M, M, Med)
25. Filters vectorization / SIMD palette writes  (S–M, Med)  
26. Unified open bus & mapper IRQ scheduling in event wheel  (M, M, Med)
27. ArrayPool for large temp buffers (savestates, audio)  (S, Low)
28. NativeAOT / WASM AOT with trimming + invariant globalization  (M, M, Med)
29. PGO / ReadyToRun desktop publish  (S, Low)
30. [SkipLocalsInit] + selective AggressiveInlining cleanup  (S, Low)
31. Replace small Dictionary lookups with arrays after init  (S, Low)
32. Fast OAM DMA stall emulation (simulate cycles, copy bulk once)  (S, Low)
33. Mapper‑specific direct bank pointer fields (common mappers)  (M, M, Med)
34. Source generator for mapper boilerplate  (M, L, Med)
35. Optional frameskip when tab hidden (skip PPU render)  (Contextual Impact, S, Low)
36. Turbo / fast‑forward skip audio mixing above threshold  (Contextual, S, Med)
37. Palette prefetch + write packed uint RGBA  (S, Low)
38. Event counters & Benchmark harness (instrumentation)  (Foundational, S, Low)
39. Hot method tiered JIT warmup (dummy frames early)  (S, Low)
40. Advanced scanline event granularity (sprite 0 / MMC3 IRQ micro events)  (Accuracy first, L, High)

---
## Detailed Theories & Rationale

[ ] ### 1. Batch / Event Scheduler (Collapse per‑instruction stepping)
Current: `NES.RunFrame` executes each instruction then calls `ppu.Step(ppuCycles)` and `bus.StepAPU(cpuCycles)`. APU core loops per cycle internally. This produces extremely high cross‑subsystem call frequency.
Theory: Accumulate CPU cycles; only advance PPU/APU in coarse batches or by event boundaries. Better: Maintain global `currentCpuCycle`, schedule `nextPpuEvent`, `nextApuEvent`, `frameEnd`, `nextIrq`. CPU runs a tight loop executing instructions until reaching the next boundary.
Impact: Large reduction in call overhead / branch churn. 15–30% plausible. Especially beneficial on WASM due to function call overhead & instruction cache locality.
Risk: Moderate—must preserve cycle accuracy for test ROMs (NMI/IRQ alignment). Add regression tests computing state hash after N frames.
Effort: Medium—central loop refactor plus public interface adjustments.

[ ] ### 2. Event‑Driven APU Timers (Remove per‑CPU cycle loop)
Current: `APU_LOW.Step(int cpuCycles)` (and similar) iterates for each CPU cycle, decrementing multiple counters and possibly generating samples.
Theory: Track per‑channel countdowns (`cyclesUntilPulse1`, `cyclesUntilTriangle`, `cyclesUntilFrameSeq`, etc.). Find smallest delta, advance time by that delta once, perform all events that hit zero, reschedule. Sample pacing uses integer accumulation (#15) to emit correct number of samples over advanced span.
Impact: 10–15% overall if APU currently >20% cost.
Risk: Medium—edge cases in frame sequencer 4‑ vs 5‑step modes, DMC timing, sweep negation boundaries.
Effort: Medium.

[ ] ### 3. LUT + Float‑Only Audio Mixing (Replace divides)
Current: `MixAndStore` performs two divides per sample and double→float casts.
Theory: Precompute `pulseMixLut[0..30]` and `tndLut[0..(15+15+127)]`. Keep channel outputs as ints or small floats; final `mixed = pulseMixLut[p1+p2] + tndLut[t+n+d]`. Convert pipelines to float only; optional pre-scaling to preserve amplitude.
Impact: Medium–High (44.1k×samples per second * ~2 divides).
Risk: Low (tables replicate formula exactly; or generate at startup to avoid code size bloat).
Effort: Small.

[ ] ### 4. Fixed‑Point Frame Timing (Remove double in `RunFrame`)
Current: Frame loop uses `double CyclesPerFrame + cycleRemainder` each call.
Theory: Maintain 64‑bit integer accumulator or 32.32 fixed for fractional cycles: accumulate CPU cycles per frame (1789773 for NTSC); subtract target (29780.5 × 60) or use precomputed per‑frame integer remainder elimination algorithm. Avoid double conversions.
Impact: 2–5% (minor alone but synergistic with scheduler cleanliness; reduces GC of boxing doubles if any and FP pipeline pressure in WASM).
Risk: Low.
Effort: Small.

[ ] ### 5. Page Table Memory Dispatch
Current: `Bus.Read`/`Write` contain branches customizing RAM, PPU regs, APU regs, cartridge dispatch each access.
Theory: Build 256-entry `Page` descriptors at mapper init / bank switch: struct `{ byte[] data; int base; bool writable; Func<ushort,byte>? readCb; Action<ushort,byte>? writeCb; }`. Resolve via `var page = pages[addr>>8];` If `readCb==null`, data access; else invoke callback for PPU/APU/mapper side-effects. Set all RAM mirror pages (0x00–0x1F) to same underlying array with distinct base offsets eliminating `&0x07FF`.
Impact: 15–30% if bus hits dominate (post-scheduler). Realistic combined with other changes.
Risk: Moderate (ensure side-effects for PPU open bus, mapper IRQ counters still fire in callback pages).
Effort: Medium.

[ ] ### 6. Inline RAM Mirroring Removal via Page Table
By mapping mirrored addresses to same physical RAM pages you remove `(address & 0x07FF)` bitmask per access.
Impact: 3–6% CPU time inside memory ops.
Risk: Low.
Effort: Small (done concurrently with #5).

[ ] ### 7. OAM DMA Fast Path
Current: Likely loops 256 `bus.Read` + `ppu.WriteOAMByte` (presently `ppu.WriteOAMDMA(page)` just invoked with page value then loops inside PPU/BUS).
Theory: If source page is linear (RAM / PRG) perform a single `Buffer.BlockCopy` or `Unsafe.CopyBlockUnaligned`. Simulate CPU stall cycles by adding 513/514 to cycle counter instead of literal per byte timing.
Impact: 2–4% (once per frame typical). More in sprite heavy scenarios.
Risk: Low (validate DMA timing and base address). 
Effort: Small.

[ ] ### 8. Audio Buffer Pooling / Span API
Current: Each `GetAudioSamples(int)` allocates new float[]. Frequent UI polling creates GC churn.
Theory: Maintain ring buffer; expose `TryRead(int max, Span<float> dst)` or rent from `ArrayPool<float>` and copy. Provide zero‑copy enumeration via two slices.
Impact: Medium in GC reduction / frame time jitter; small raw CPU.
Risk: Low.
Effort: Small.

[ ] ### 9. Filter Fusion & Integer/Float Simplification
Current: Low-pass then DC high-pass executed sequentially per sample.
Theory: Combine into a single biquad (Direct Form I/II). Optionally move to single multiply-add chain or approximate with simple leaky integrators. Keep float only.
Impact: 3–6% audio step cost; synergy with #3.
Risk: Medium (audio tone difference; verify with test ROM waveforms).
Effort: Medium.

[ ] ### 10. Unified Central Event Scheduler
Build on #1. Single loop: determine `next = Min(nextPpuEvent, nextApuEvent, nextIrq, frameEnd)`; run CPU instructions until `next`; process events; repeat.
Impact: Additional 5–10% combined due to larger uninterrupted CPU batches.
Risk: Medium (timing edge cases). Requires robust tests for NMI, APU frame IRQ, mapper IRQ alignment.
Effort: Medium–Large depending on integration.

[ ] ### 11. CPU Opcode Dispatch Table
Current: (Inferred) large switch each instruction. JIT usually jump-tables, but combining addressing mode + operation into metadata reduces code size & branches.
Theory: Precompute arrays: `delegate*<CPU,int> exec[256]; byte baseCycles[256]; AddrMode modes[256];` Execution: fetch opcode, call function pointer returning extra cycles. Optionally inline addressing resolution per mode via small function pointer tables.
Impact: 5–8% CPU instruction dispatch (2–5% overall after larger structural wins).
Risk: Low (unchanged semantics; ensure unofficial opcodes maintained).
Effort: Medium.

[ ] ### 12. Bus Fast Path Inside CPU
Expose direct references to `ram` / common PRG ROM bank arrays; implement `ReadFast`/`WriteFast` inside CPU that bypass Bus for most addresses, fallback for specials—if page table (#5) not yet implemented.
Impact: 3–6%.
Risk: Low.
Effort: Small.

[ ] ### 13. Precomputed Nametable Mirroring
Build four static `ushort ntResolve[4096]` lookup tables (H, V, SingleA, SingleB). PPU VRAM access becomes `vram[ ntResolve[address & 0x0FFF] ]` with current table pointer.
Impact: 2–5% PPU read/write overhead if currently branchy.
Risk: Low.
Effort: Small.

[ ] ### 14. Scanline (or Sub‑Scanline) PPU Batching
Aggregate 341 PPU cycles at a time. Inside processing, generate pixels or stage fetches in contiguous loops.
Impact: 5–15% depending on per-pixel cost & overhead removed.
Risk: Medium (mid‑scanline effects; may require sub‑segments for precise `sprite 0` hit / MMC3 IRQ cycle numbers if supporting later).
Effort: Medium.

[ ] ### 15. Integer Bresenham Sample Pacing
Replace fractional double accumulator: accumulate `sampleAcc += elapsedCycles * sampleRate; while(sampleAcc >= cpuClock){ Emit(); sampleAcc -= cpuClock; }`.
Impact: 2–4% plus lower FP pressure.
Risk: Low.
Effort: Small.

[ ] ### 16. Nonlinear Mix Table Precompute (if not done in #3)
If nonlinear portions not fully LUT’d, build extended LUTs keyed by channel sums.
Impact: Included in #3; item stands only if partial implementation chosen.
Risk: Low.
Effort: Small.

[ ] ### 17. Interrupt Boundary Scheduling
Maintain `nextIrqCycle`, `nextNmiCycle`; CPU batch loop only checks when surpassing those cycles. Removes per‑instruction flag checks.
Impact: 1–3%.
Risk: Low–Med (must not overshoot hardware latency expectations).
Effort: Small.

[ ] ### 18. SaveState Serializer Specialization
Current: Reflection heavy (PlainSerialize). Replace with source-generated partial or cached FieldInfo arrays once, or custom struct writers.
Impact: Strong when frequent savestates / rewind; negligible otherwise.
Risk: Low.
Effort: Medium.

[ ] ### 19. Reflection & Verbose Logging Stripping
Use `#if DEBUG` around diagnostic `Console.WriteLine` (observed in `LoadState` and likely elsewhere).
Impact: Small steady; prevents I/O stalls & log noise; improves startup JIT locality.
Risk: Low.
Effort: Small.

### 20. Remove Per‑Frame Console Writes
Ensure no `Console.WriteLine` inside hot loops (scan showed prints in state load; verify none in `RunFrame`).
Impact: Small but crucial to avoid perf cliffs.
Risk: Low.
Effort: Small.

[ ] ### 21. Bitfield Packing for APU/PPU Flags
Combine multiple bool fields into a single `uint flags` reducing memory loads / footprint, improving cache density.
Impact: 1–2% (micro) but helps with APU event-driven rewrite.
Risk: Low.
Effort: Small.

[ ] ### 22. ADC/SBC Flags LUT
Precompute `(A,M,carryIn)` → `(result, flags)` compressed table (e.g., 256×256×2 possibilities impractical; optimize to 64K indexing via `(A<<8)|(M<<0)|(carry<<16)` or narrower). Store combined flags mask.
Impact: 1–3% instruction dispatch cost.
Risk: Low.
Effort: Small.

[ ] ### 23. Opcode Predecode / Micro Trace Cache
Cache decoded function pointer + cycle info by PC (direct-mapped). Invalidate on writes to code page.
Impact: Additional 2–5% if CPU still a bottleneck after earlier improvements.
Risk: Medium (self-modifying code edge cases; complexity).
Effort: Large relative to gain—defer until profiling proves need.

[ ] ### 24. DMC & Channel Event Coalescing
Further compress events by coalescing consecutive identical period ticks instead of per-bit evaluation; schedule DMC sample fetch events at countdown boundaries.
Impact: 2–4% incremental post event-driven baseline.
Risk: Medium (DMC exact behavior is timing sensitive).
Effort: Medium.

[ ] ### 25. Filter / Palette SIMD & Vectorization
SIMD mixing for multiple samples or pixel palette writes in groups of 8/16 using `System.Numerics.Vector<T>`.
Impact: 1–4% each; more on modern CPUs with good SIMD lanes.
Risk: Low.
Effort: Small–Medium.

[ ] ### 26. Unified Open Bus & Mapper IRQ Scheduling
Integrate mapper IRQ counters (e.g., MMC3 scanline IRQ) and open bus emulation directly into scheduler event set reducing conditional logic.
Impact: 1–3% + correctness for future mappers.
Risk: Medium.
Effort: Medium.

[ ] ### 27. ArrayPool for Large Temporary Buffers
Pool buffers for savestates (serialize into rented `IMemoryOwner<byte>` / `ArrayPool<byte>`), large audio drains.
Impact: GC pause reduction.
Risk: Low.
Effort: Small.

[ ] ### 28. NativeAOT / WASM AOT / Trimming
Publish with AOT (Blazor WASM: `dotnet publish -p:WasmEnableSIMD=true -p:InvariantGlobalization=true` etc.). Desktop: NativeAOT for improved cold start & code layout.
Impact: 5–15% runtime throughput + size reduction in WASM; improved startup.
Risk: Medium (reflection restrictions; ensure serializer updated first).
Effort: Medium.

[ ] ### 29. PGO / ReadyToRun
Enable Profile Guided Optimization for representative workload; improves instruction layout & inlining.
Impact: 3–8% depending on hotspots.
Risk: Low.
Effort: Small (build pipeline config).

[ ] ### 30. [SkipLocalsInit] & AggressiveInlining Hygiene
Add assembly-level `[module:SkipLocalsInit]` where safe to bypass zeroing for stackalloc heavy methods. Remove redundant `[AggressiveInlining]` on large methods; keep on tiny hot helpers (current `Bus.Read/Write` already annotated).
Impact: 0.5–2% micro wins.
Risk: Low (ensure no reliance on uninitialized stack locals).
Effort: Small.

[ ] ### 31. Dictionary → Arrays Post-Initialization
Replace runtime lookups (core id dictionaries) with arrays or direct fields after selection to reduce overhead in any hot path referencing them (should be minor now).
Impact: <1–2%.
Risk: Low.
Effort: Small.

[ ] ### 32. DMA Stall Simulation Simplification
After fast block copy (#7), simulate stall cycles by incrementing cycle counter once; removes per-byte cycle tick logic.
Impact: Already counted partly in #7; small incremental.
Risk: Low.
Effort: Small.

[ ] ### 33. Mapper-Specific Direct Bank Pointers
For common mappers (NROM, MMC1, UxROM, MMC3) store active PRG bank spans directly in `Bus` / CPU for immediate indexing.
Impact: 2–5% memory access where mapper invoked currently.
Risk: Medium (bank switch correctness).
Effort: Medium.

[ ] ### 34. Mapper Source Generator
Generate partial classes with hard-coded bank update logic & page table patching.
Impact: Maintainability + incremental speed (~1–3%).
Risk: Medium.
Effort: Large (tooling).

[ ] ### 35. Frameskip When Tab Hidden / Unfocused
Skip PPU rendering & optionally audio mixing when not visible; maintain deterministic cycle counters.
Impact: Huge user-perceived CPU savings (outside pure benchmark). Not a raw optimization of core speed.
Risk: Low.
Effort: Small.

[ ] ### 36. Turbo Fast-Forward Audio Skip
When user holds turbo, mix fewer or no audio samples (or downsample) to free CPU for faster catch-up.
Impact: Large situational.
Risk: Medium (audio artifacts).
Effort: Small.

[ ] ### 37. Packed RGBA Writes (Palette Precompute)
Maintain `uint[] paletteRGBA` and write directly to framebuffer; remove per-pixel 4 byte assignments.
Impact: 1–3% PPU rendering overhead.
Risk: Low.
Effort: Small.

[ ] ### 38. Instrumentation & Benchmark Harness
Add counters: `cpuInstrCount`, `busReadsRam`, `busWritesRam`, `ppuScanlines`, `apuEvents`, `dmaFastCopies`, `dmaFallbackCopies`. Provide `BenchmarkDotNet` harness: run 10k instructions, run 600 frames, audio mixing of 1 second.
Impact: Foundational (enables safe optimization & regression detection).
Risk: Low.
Effort: Small.

[ ] ### 39. Tiered JIT Warmup
Execute a small scripted sequence at startup to force Tier1 JIT for hot methods before interactive use.
Impact: Startup smoothness; small throughput improvement early.
Risk: Low.
Effort: Small.

[ ] ### 40. Fine-Grained PPU Event Granularity (Advanced Accuracy)
After scanline batching, optionally subdivide into mid-scanline events for sprite 0 hit / MMC3 IRQ cycle accuracy.
Impact: Mainly correctness for timing-sensitive ROMs; small perf cost (not a boost). Consider only if accuracy goals expand.
Risk: High (complexity).
Effort: Large.

---
## Suggested Implementation Sequence & Milestones
Milestone A (Foundations & Quick Wins)
- Implement instrumentation (#38)
- LUT audio mixing (#3)
- Fixed-point frame timing (#4)
- Remove logging / allocate pooling (#19, #20, #8)

Milestone B (Memory & Scheduling Core)
- Page table + RAM mirroring (#5, #6)
- OAM DMA fast path (#7)
- Batch scheduler prototype (PPU/APU still stepped in chunks) (#1 partial)

Milestone C (APU Overhaul)
- Event-driven APU (#2)
- Integer sample pacing + filter fusion (#9, #15)
- Nonlinear mix finalization (#16)

Milestone D (Full Event Loop & CPU Dispatch)
- Unified event scheduler (#10, with interrupts #17)
- Opcode dispatch table (#11) & RAM fast path (#12) if still CPU bound

Milestone E (PPU & Rendering)
- Scanline batching (#14)
- Nametable mirroring LUT (#13)
- Packed RGBA writes (#37)

Milestone F (Advanced & Build)
- SaveState serializer (#18)
- Bitfield packing (#21), ADC/SBC LUT (#22)
- PGO / ReadyToRun / AOT (#28, #29)
- Optional trace cache (#23) if profiling warrants

---
## Measurement Plan
1. Baseline: Record 600 NTSC frames (10s) of representative ROMs (action, scrolling, audio heavy) capturing ms/frame, bus ops, instructions.
2. After each milestone: Re-run same ROM set; diff metrics; store in `/docs/perf-history/`.
3. Fail fast threshold: If regression >2% in average frame time or state hash mismatch over 100 frames, rollback & investigate.
4. Audio correctness: Capture short WAV (1s) before/after APU refactors; compute RMS difference & ensure within tolerance (<1 LSB scaled level typical).

---
## Risk Mitigation Checklist
- Add cycle & state hash tests (CPU/PPU/APU) pre-refactor.
- Encapsulate new scheduler behind feature flag `USE_EVENT_SCHEDULER` for staged rollout.
- Keep old APU implementation selectable until parity validated.
- For WASM size growth (LUTs): generate at runtime when size > few KB.

---
## Appendix: Data Structures Sketches
Page Struct Example:
```csharp
struct Page {
    public byte[]? data; // null if callback page
    public int baseOffset; // added to (addr & 0xFF)
    public bool writable;
    public delegate*<ushort, byte> readPtr; // optional function pointer (null if data!=null)
    public delegate*<ushort, byte, void> writePtr;
}
```
Event Scheduler Loop Pseudocode:
```csharp
while (current < frameEnd) {
    var next = Min(nextPpu, nextApu, nextIrq, frameEnd);
    cpu.RunUntil(next, ref current);
    if (current >= nextPpu) { ppu.ProcessScanline(); nextPpu += 341; }
    if (current >= nextApu) { apu.AdvanceTo(current); nextApu = apu.NextEventCycle; }
    if (current >= nextIrq) { cpu.TriggerIrq(); nextIrq = ComputeNextIrq(); }
}
```
APU Event Advancement Skeleton:
```csharp
while (current < target) {
    int delta = Min(cyclesUntilPulse1, cyclesUntilPulse2, cyclesUntilTri, cyclesUntilNoise, cyclesUntilDmcBit, cyclesUntilFrameEvent, target - current);
    current += delta;
    DecrementAll(delta);
    if (cyclesUntilPulse1==0) { ClockPulse1(); cyclesUntilPulse1 = pulse1Period; }
    // ... repeat per channel
    if (cyclesUntilFrameEvent==0) { FrameSequencerTick(); cyclesUntilFrameEvent = NextFrameSeqDelta(); }
    apuSampleAcc += delta * sampleRate;
    while (apuSampleAcc >= cpuClock) { MixAndStoreLut(); apuSampleAcc -= cpuClock; }
}
```

---
## Maintenance Notes
Keep `optimize.md` under version control and update Impact/Risk estimates after real measurements. Remove completed items (or move to a "Done" appendix) to keep active list focused.

---
## Done Items (Move here as changes land)
*(Empty initially)*

---
Generated: (initial version) – Update date/time as list evolves.
