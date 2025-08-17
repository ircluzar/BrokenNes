# Emit.IL Hypermicro-Optimization Work Plan (`*_EIL` Cores)

Purpose: Convert prior design notes into an actionable, risk-aware work document divided into three chapters: `APU_EIL`, `CPU_EIL`, `PPU_EIL`. Each numbered item is self‑contained with (a) task, (b) what to build/change, (c) estimated incremental performance gain (relative to current `_SPD` core baseline for that subsystem), and (d) qualitative risk of breaking correctness or destabilizing timing.

Legend:
- Perf Gain: Single-item incremental win once landed (not cumulative), rough order-of-magnitude: Trivial <1%, Small 1‑5%, Medium 5‑15%, Large 15‑30%, Very Large >30% (subsystem scope).
- Risk: Low (mechanical / easily verified), Medium (semantic subtlety or broad surface), High (timing critical or cross-component coupling).
- Where ranges overlap, conservative (lower) end used for roll‑up planning.

WASM Runtime Feasibility Legend (Blazor WebAssembly, no runtime codegen):
- [WASM: Supported] = Can be implemented directly under WebAssembly constraints (no runtime IL emission required).
- [WASM: Adapt] = Requires redesign: replace runtime IL emission / dynamic method creation with build-time source generation, pre-generated static variants, table-driven logic, or hand-written specialized code paths.
- [WASM: Not Supported] = Fundamentally depends on runtime IL emission / dynamic code generation (Reflection.Emit, DynamicMethod, runtime fusion) which is unavailable; must be skipped or replaced with a different strategy.
 - [WASM: Not Supported] = Fundamentally depends on runtime IL emission / dynamic code generation (Reflection.Emit, DynamicMethod, runtime fusion) which is unavailable.
 - [WASM: Workaround] = Original dynamic technique unsupported, but a static (build-time source generation / offline profiling / table-driven) alternative can approximate most of the projected gain.

Key Platform Constraints (Blazor WASM .NET 10):
- System.Reflection.Emit / DynamicMethod / runtime JIT: NOT available (RuntimeFeature.IsDynamicCodeSupported == false).
- Expression trees Compile() interpret only (no native IL emission).
- AOT & trimming (when enabled) remove unused metadata; reflection-heavy adaptive strategies risk breakage unless rooted. Current mobile flatpublish uses Debug + no AOT + no trim (max metadata, slower execution).
- SIMD (WasmEnableSIMD=true) is available for Vector128 / some intrinsics; good target for hot loops.

Strategic Adaptation Patterns:
1. Source Generators (compile-time) to produce opcode / shape specific static methods compiled into the assembly.
2. Hand-written templated partial classes enumerating shape combinations (small combinatoric sets) selected by tables at runtime.
3. Table-driven interpreters (pre-decoded micro-ops) instead of emitted micro-op methods.
4. Optional offline tooling (run on desktop) to generate C# files checked into repo for WASM consumption.
5. SIMD & algorithmic simplifications (do not require IL emission) pursued first for reliable gains.

Superblock / on-the-fly fusion style optimizations (dynamic binary translation) are effectively out-of-scope for WASM; focus shifts toward static specialization + vectorization.

Baseline Acceptance Targets (aggregate after all chapters): CPU ≥20% instr/sec, PPU ≥15% frame render time reduction, APU ≥10% audio step overhead reduction, zero correctness regressions.

---
## Chapter 1: `APU_EIL` (Audio & Sequencer)

APU emphasis: collapsing per‑CPU‑cycle stepping and sample mixing into batch IL with channel-shape specialization.

APU Work Items

APU-1. Batch Advance Core (`AdvanceBatch`) [WASM: Adapt]
- Do: Replace per-cycle loop with IL that advances until `cpuCycles` consumed, jumping directly between frame-sequencer event boundaries (quarter/half frame) without per-cycle branch overhead.
- Perf Gain: Medium (5‑10%).
- Risk: Medium (frame sequencer timing must stay bit-accurate; errors affect IRQ tests & music tempo).

APU-2. Generated Shape Cache (Channel Mask Specialization) [WASM: Adapt]
- Do: Generate distinct IL methods keyed by enabled channel bitmask + constantVolume flags; elide code for disabled channels & envelopes not needed.
- Perf Gain: Small–Medium (3‑8%).
- Risk: Low (mechanical elimination; guarded by shadow comparison for first run of each shape).

APU-3. Envelope/Sweep Omission for Constant Volume [WASM: Supported] (can be manual branch elimination / static fast path)
- Do: In specialized shapes where constant volume & skip flag enabled, remove envelope divider/decay updates entirely; set fixed volume locals.
- Perf Gain: Small (2‑4%).
- Risk: Low (already logically equivalent to existing optimization idea; easy differential test).

APU-4. Vectorized Sample Mix (SIMD) [WASM: Supported]
- Do: Use `Vector128<float>` to process 4 samples per iteration: accumulate pulse mix & tnd approximations; fallback scalar path if intrinsics unsupported.
- Perf Gain: Medium (5‑12%).
- Risk: Medium (floating rounding differences; must stay within acceptable epsilon to pass hash diff tests; platform feature probing needed).

APU-5. Polynomial Mix Approximation (Optional Flag) [WASM: Supported]
- Do: Replace exact nonlinear formulas with 5th‑order polynomial or small LUT when speed flag set; keep reference path for validation.
- Perf Gain: Small (2‑5%) in mix-heavy scenes.
- Risk: Medium (perceptible audio deviation risk; must AB compare RMS error; keep disabled by default).

APU-6. Noise LFSR Tight IL [WASM: Supported] (hand-opt without runtime emission)
- Do: Emit noise step IL using explicit bit shifts & XOR without method calls or control branches (single block with conditional mask math).
- Perf Gain: Small (1‑3%).
- Risk: Low (algorithm compact; easy to mirror existing logic tests).

APU-7. Ring Buffer Direct Pointer & Mask Write [WASM: Supported]
- Do: Pin ring array; compute masked indices with `and` vs modulo; emit inline overflow drop logic.
- Perf Gain: Small (1‑2%).
- Risk: Low.

APU-8. Silent Fast-Forward Specialized IL [WASM: Adapt] (static pre-generated or conditional branch path)
- Do: Generate variant that updates only frame sequencer counters + sample accumulator when all channels silent; skip channel-specific timers.
- Perf Gain: Medium (4‑8%) for silence / menu periods (overall depends on workload).
- Risk: Medium (must not drift fractional sample accumulator or frame events).

APU-9. Shadow Validation Harness [WASM: Supported]
- Do: For each new generated shape, run a fixed 10K CPU cycle shadow against reference; compare ring buffer hash + key state (envelope levels, timers).
- Perf Gain: N/A (quality gate). Adds negligible overhead amortized.
- Risk: Low (enables early detection; failure -> disable shape).

APU-10. Periodic Drift Recheck (Optional Debug) [WASM: Supported]
- Do: Every N frames re-run a micro shadow window to ensure no cumulative drift (debug builds only).
- Perf Gain: N/A.
- Risk: Low (disabled in release).

APU-11. Shape Key Hash & Cache Persistence (Non-AOT platforms) [WASM: Adapt] (cache of pre-generated static delegates)
- Do: Maintain dictionary hash -> delegate; reuse after save/load; optionally record metrics.
- Perf Gain: Trivial (startup savings) but improves UX.
- Risk: Low.

APU-12. Configurable Approx Mix Level (Tiered) [WASM: Supported]
- Do: Provide levels (Exact, LUT64, Poly) baked into IL; dynamic flag triggers regeneration.
- Perf Gain: Additional Small (1‑3%) beyond APU-5.
- Risk: Medium (audio fidelity balancing).

APU-13. Build Perf Counters Post-Migration [WASM: Supported]
- Do: Instrument cycles spent in old vs new path to validate gains; auto-report first 2 seconds.
- Perf Gain: N/A (observability).
- Risk: Low.

APU Aggregate Expected (capped to avoid naive sum): ~10‑15% APU subsystem CPU time reduction.

---
## Chapter 2: `CPU_EIL` (6502 Execution)

CPU emphasis: micro-op emission + optional superblock fusion for instruction streams; minimizing dispatch & memory bounds overhead.

CPU Work Items

CPU-1. `CpuState` Struct & Abstraction Layer [WASM: Supported]
- Do: Define unmanaged struct (A,X,Y,PC,SP,P, irq/nmi flags, cycle accumulator) used by emitted methods; ensures ref passing & predictable layout.
- Perf Gain: Trivial (<1%) enabling later steps.
- Risk: Low.

CPU-2. 256 Micro-Op IL Emission [WASM: Adapt] (source-generate or table-map to hand-written methods)
- Do: Emit one method per opcode performing fetch, execute, flag update, PC advance, cycle return; remove large switch dispatch.
- Perf Gain: Large (15‑25%).
- Risk: Medium (flag semantics correctness critical; broad surface).

CPU-3. Dispatch Loop with Pre-Decoded Table [WASM: Supported]
- Do: Build `Func<ref CpuState,int>[]` table at reset; loop uses `calli` / delegate invoke on opcode byte.
- Perf Gain: Small–Medium (3‑8%) on top of CPU-2 (due to reduced branch mispredict).
- Risk: Low (mechanical once micro-ops stable).

CPU-4. Page-Cross Penalty Branchless Calc [WASM: Supported]
- Do: Inline penalty: `(base & 0xFF00) ^ (eff & 0xFF00)` -> condition -> add cycles using arithmetic mask to avoid branch.
- Perf Gain: Small (1‑3%).
- Risk: Low.

CPU-5. Zero-Page / Stack Direct Pointer Fast Path [WASM: Supported]
- Do: Pin `ram` and use pointer arithmetic for addresses <0x2000; fallback to bus for others.
- Perf Gain: Small–Medium (4‑6%) memory-heavy code.
- Risk: Medium (bounds & correctness if pointer misuse; require tests on mirrored RAM behavior).

CPU-6. ZN Flag Table Inline Load [WASM: Supported]
- Do: Replace per-instruction bit manipulations with table OR mask (already conceptually present) via single `ldelem.u1`.
- Perf Gain: Small (1‑2%).
- Risk: Low.

CPU-7. ADC/SBC Flag Combined Arithmetic IL [WASM: Supported]
- Do: Emit condensed carry/overflow calc using minimal stack ops; ensure identical results for all operand combinations.
- Perf Gain: Small (2‑3%).
- Risk: Medium (edge cases with carry & signed overflow).

CPU-8. Branch Folding (Relative Conditional) [WASM: Adapt] (pre-generated variants or conditional fast paths)
- Do: Emit specialized micro-ops for each branch that inline taken/not-taken path PC update & cycle addition; record branch hotness separately.
- Perf Gain: Small (2‑4%).
- Risk: Low.

CPU-9. Idle Poll Loop Specialized Skipper [WASM: Adapt] (heuristic loop unrolling without dynamic emission)
- Do: Generate variant micro-op for detection sites that accumulates multiple loop iterations in one call when safe (reusing existing heuristics but without general instrumentation overhead each iteration).
- Perf Gain: Medium (5‑10%) in vblank-heavy workloads.
- Risk: Medium (timing—must not skip across NMI/IRQ boundaries).

CPU-10. Superblock Fusion (Straight-Line Runs) [WASM: Workaround]
- Original Idea: Runtime detection & emission of fused straight-line instruction blocks into dynamically generated methods to remove per-op dispatch and enable cross-instruction optimization.
- Why Dynamic Form Unsupported: Requires runtime IL/code generation (not available in WASM).
- Workaround Strategy (Static / Build-Time Fusion):
	1. Offline Profiling (desktop tool) collects top N hot linear instruction sequences (no branches/self-mod side effects) across a representative ROM corpus.
	2. Source Generator (or pre-build tool) emits a static C# class with specialized methods (e.g., `CpuSuperblock_XYZ(ref CpuState)`), each containing the straight-line code for that sequence.
	3. At runtime on WASM, decode instruction stream; when a span matches a known fused signature (hash of opcode bytes + addressing modes), invoke the pre-generated method instead of per-instruction dispatch.
	4. Fallback to normal micro-op loop when sequence not in table or when self-modifying write invalidates the span.
	5. Optional light dirty-page bitmap to invalidate only affected superblock signatures (smaller than full dynamic fusion infra).
- Expected Gain vs Original: ~70–85% of the dynamic fusion estimate (so if original projected 10–20% CPU gain, expect ~7–15%) because:
	* Coverage limited to sequences observed in profiling set.
	* Cannot adapt to rare, game-specific hot paths unseen during profiling.
	* No runtime super-optimization (e.g., constant folding for values only known at runtime).
- Constraints / Limits:
	* Code size growth proportional to number and length of fused blocks; cap N to control publish size.
	* Must ensure determinism: signature invalidation on any RAM write to included addresses or on writes to banked code regions.
	* Self-modifying code heavy titles may gain less (superblocks will fall back frequently).
- Validation:
	* Shadow-execute first invocation of each fused block (single pass) comparing end-state (registers, flags, cycles, memory touched).
	* Hash-based quick check to skip re-verification after first success.
- Tooling Needed:
	* `tools/ProfilerRunner` (desktop) to emit JSON of candidate blocks (opcode byte sequences + frequencies).
	* Source generator or pre-build script to convert JSON -> `CpuSuperblocks.g.cs`.
- Rollout Recommendation: Treat as optional tier after core table-driven micro-ops (CPU-2/3) are stable; can be deferred without blocking MVP.
- Do: Profile first few frames; fuse top N hot linear blocks (no branches/self-mod writes) into single emitted method executing multi-instruction sequence.
- Perf Gain: Medium–Large (10‑20%) on instruction-dense games.
- Risk: High (self-modifying code invalidation & correctness on indirect jumps; must add dirty page bitmap).

CPU-11. Dirty Page Bitmap & Invalidation [WASM: Adapt] (only needed if partial fusion attempted; otherwise omit)
- Do: Track writes; if write touches fused block region, invalidate affected superblocks.
- Perf Gain: Enabler (protects CPU-10).
- Risk: Medium.

CPU-12. Shadow Execution Harness (Per Opcode First Use) [WASM: Supported]
- Do: Execute reference path alongside emitted version first time each opcode executes; compare state & cycles.
- Perf Gain: N/A (safety).
- Risk: Low.

CPU-13. Optional Deterministic Mode Freeze [WASM: Supported]
- Do: After first successful verification pass, disable further adaptive fusion so savestates replay identically.
- Perf Gain: N/A.
- Risk: Low.

CPU-14. Metrics Overlay & Diff Reporting [WASM: Supported]
- Do: Track instructions/sec before/after enable; display overlay line.
- Perf Gain: N/A.
- Risk: Low.

CPU-15. Fallback Path on Mismatch (Per Opcode Bitset) [WASM: Supported]
- Do: Maintain bitset of failed opcodes; route to generic reference implementation.
- Perf Gain: Protects overall improvement; avoids global disable.
- Risk: Low.

CPU Aggregate Expected (bounded): ~22‑30% CPU subsystem speedup (assuming superblocks succeed; ~15‑20% without fusion).

---
## Chapter 3: `PPU_EIL` (Scanline & Tile Pipeline)

PPU emphasis: hot pixel loops (background tiles + sprites) and reducing per-scanline branching & memory indirection.

PPU Work Items

PPU-1. Scanline Renderer IL (Background Only) [WASM: Adapt] (source-generate unrolled loops at build time)
- Do: Emit IL for 33-tile loop with pattern cache fast path & deferred attribute fetch; unroll tile iteration; store pixels as packed `uint`.
- Perf Gain: Large (12‑18%).
- Risk: Medium (must preserve scroll fineX handling, attribute quadrant logic).

PPU-2. Palette Entry Fast Locals & Cache [WASM: Supported]
- Do: Preload frequently used palette entries (universal background + first sprite/background palettes) into locals at scanline start; direct writes without repeated indexing.
- Perf Gain: Small (1‑3%).
- Risk: Low.

PPU-3. Sprite Compositing Specialized Path (≤8 Sprites) [WASM: Adapt]
- Do: After OAM evaluation, generate IL that inlines each sprite’s row bits decode (cached) & compositing rules (priority, transparency, sprite-zero hit) without inner per-sprite loops.
- Perf Gain: Medium (6‑10%).
- Risk: Medium (sprite-zero hit & overflow flag correctness).

PPU-4. Blank Scanline Early Exit IL [WASM: Supported]
- Do: If pre-analysis shows all 33 tile rows zero (rowBits==0) and sprites disabled, emit path filling line with universal color via vectorized fill.
- Perf Gain: Small–Medium (3‑7%) scenes with blank margins / status bars.
- Risk: Low.

PPU-5. CHR Bank Signature Invalidation Hook [WASM: Supported]
- Do: Observe mapper CHR signature; if changed, invalidate pattern row caches & regenerate IL next scanline.
- Perf Gain: Enabler (avoids stale code). Trivial direct gain.
- Risk: Low.

PPU-6. Mirroring / Scroll Config Shape Key [WASM: Adapt]
- Do: Include mirroring mode + coarse/fine scroll quadrant in shape key; regeneration when crossing tile boundary requiring new horizontal attr pattern.
- Perf Gain: Trivial (ensures other gains remain valid).
- Risk: Low.

PPU-7. Attribute Decode Precomputed Table [WASM: Supported]
- Do: Table of 32 entries mapping (coarseX, coarseY) -> shift mask; IL just indexes.
- Perf Gain: Small (1‑2%).
- Risk: Low.

PPU-8. Optional Vectorization (SIMD Store Burst) [WASM: Supported]
- Do: Use `Vector128<uint>` wide stores for 8 pixels when rowBits not sparse; fallback for partial edges.
- Perf Gain: Small–Medium (4‑6%).
- Risk: Medium (alignment subtleties; more benefit on platforms with good SIMD throughput).

PPU-9. Combined Background+Sprite Fused Path (Heuristic) [WASM: Adapt]
- Do: When sprite count small & no priority conflicts predicted (all priority=front), emit single loop merging decisions to avoid second pass.
- Perf Gain: Small–Medium (5‑8%) sprite-light frames.
- Risk: High (priority + transparency interplay; risk of subtle pixel differences).

PPU-10. Diagnostic Hash Dual-Rendering (First 2 Frames) [WASM: Supported]
- Do: Render both reference and IL versions; hash lines; disable on mismatch & log tile/sprite index diff sample.
- Perf Gain: N/A (safety).
- Risk: Low.

PPU-11. Hot Reload Guard (Regeneration Throttle) [WASM: Adapt] (mainly relevant if adaptation still regenerates shape variants)
- Do: Debounce IL regeneration to once per frame even if multiple invalidation triggers occur.
- Perf Gain: N/A (prevents perf regressions from thrash).
- Risk: Low.

PPU-12. Sprite Pattern Row Cache Tight Invalidation [WASM: Supported]
- Do: Invalidate only affected tile rows on CHR writes, not entire cache.
- Perf Gain: Small (2‑4%) CHR RAM heavy games.
- Risk: Low.

PPU Aggregate Expected: ~15‑22% PPU subsystem time reduction (upper bound assumes fused path & SIMD).

---
## Cross-Cutting / Shared Infrastructure

X-1. Dynamic Capability Gate [WASM: Supported] (will report dynamic code unsupported)
- Do: Detect `RuntimeFeature.IsDynamicCodeSupported`; register *_EIL cores only if true; else log capability message.
- Perf Gain: N/A.
- Risk: Low.

X-2. `IlEmitter` Utility Module [WASM: Adapt] (becomes compile-time generator helpers; avoid runtime IL APIs)
- Do: Wrapper for common IL patterns (load constant, branchless add cycles, table loads, calli jump tables) + optional debug hex dump.
- Perf Gain: Indirect (reduces bug risk & code size).
- Risk: Low.

X-3. Shape Hash Builder (64-bit) [WASM: Adapt] (select pre-generated variant or enable/disable approximations)
- Do: Deterministic hash of key structural inputs; used for caching & mismatch logs.
- Perf Gain: Trivial.
- Risk: Low.

X-4. Shadow Execution Engine [WASM: Supported]
- Do: Generic harness that executes reference & emitted delegate; supports CPU (per opcode), APU (window cycles), PPU (scanline) with pluggable state comparison.
- Perf Gain: N/A (quality).
- Risk: Low.

X-5. Metrics & Telemetry Export [WASM: Supported]
- Do: Unified struct capturing per-core: generation count, failures, cumulative time saved estimate, active shapes.
- Perf Gain: N/A.
- Risk: Low.

X-6. Deterministic Mode Switch [WASM: Supported]
- Do: Freeze adaptive regeneration post-verification for savestate determinism; flag in SpeedConfig.
- Perf Gain: N/A.
- Risk: Low.

X-7. Developer Commands [WASM: Adapt] (dump variant metadata vs IL bytes)
- Do: `eil.dump <core> id=<shape>` and `eil.stats`; produce IL byte dump & summary.
- Perf Gain: N/A.
- Risk: Low.

X-8. Continuous Integration Differential Tests [WASM: Supported]
- Do: Add test suite that runs small instruction/audio/frame traces; asserts hashes unchanged; fails PR if divergence.
- Perf Gain: N/A.
- Risk: Low.

---
## Risk Mitigation Summary
High-risk items now limited to adapted fusion-like features: CPU-10 (static fused superblocks via workaround) and PPU-9 (requires adaptation). Both guarded by signature/variant shadow verification. Medium-risk items retain shadow verification on first execution. Low-risk items can batch merge after cursory review.

---
## Rollout Order (Suggested)
1. Shared infra (WASM first pass): X-1 (capability gate), X-4 (shadow), X-5 (metrics).
2. SIMD & algorithmic wins (no emission): APU-4, APU-5, CPU-4/5/6/7, PPU-2, PPU-4, PPU-7, PPU-8, PPU-12.
3. Table / source-generated micro-ops: CPU-2 (adapt), CPU-3, CPU-8 (adapt), CPU-9 (adapt if worthwhile), omit CPU-10 on WASM.
4. Static specialization (APU & PPU): APU-1/2/8 (adapt), PPU-1/3/6/9 (adapt) via source generation.
5. Approximation & optional modes: APU-12, APU-13, PPU-10 diagnostics early.
6. Evaluate remaining adapted items; decide if complexity : gain ratio justifies implementation on WASM.

---
## Aggregate Expected Gains (Non-Cumulative Illustrative)
- CPU_EIL (WASM realistic, no superblocks): 15‑20% via table/variant + branchless + pointer + SIMD.
- PPU_EIL (WASM adapted static specialization): 12‑18% (upper bound depends on practicality of pre-generated variants replacing dynamic emission).
- APU_EIL: 10‑15% (vector mix + batching via static specialization + silence fast path).
Overall WASM frame time improvement target (aggregate) remains plausible at ~20‑30% vs current baseline if adapted set is fully realized, despite loss of runtime fusion.

---
## Completion Definition
WASM-adapted EIL MVP definition: Table-driven CPU-2/3 (no runtime emission), branchless & pointer optimizations (CPU-4/5/6/7), SIMD audio mix (APU-4), batch advance APU-1 (static), adapted PPU-1 static scanline unroll, shadow harness (X-4), capability gate (X-1), metrics (X-5); achieving ≥ 12% CPU + 10% PPU + 8% APU improvement on three benchmark ROMs.

---
## Notes
Performance estimates updated for WASM adaptation reflect removal of runtime fusion / dynamic IL. Validate with instrumentation (X-5) before committing to deeper adaptation complexity.

