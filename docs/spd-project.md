# SPD Project Work Document: CPU / PPU / APU Performance Cores

Purpose: Track, implement, and tune speed-focused variants (`CPU_SPD`, `PPU_SPD`, `APU_SPD`). Each item below is a task or subtask with a checkbox. Already implemented items are checked. No test tasks are listed (manual / external testing assumed).

Legend (inline tags): (Impact: L/M/H/VH) (Risk: None/L/M/H) Optional = needs runtime toggle. Experimental = may be revised or dropped.

---
## 1. CPU (CPU_SPD)

### 1.1 Baseline (Completed)
- [x] Precomputed Z/N flag table (Impact:L Risk:None) — `ZNTable[256]` removes per-op flag branching.
- [x] Precomputed flag masks & branchless flag set (Impact:L Risk:None)
- [x] Inline branch opcodes decoding (Impact:M Risk:None)
- [x] Remove delegate/virtual indirection in addressing (Impact:M Risk:None)
- [x] Optional ignore invalid opcodes (Impact:L Risk:L) — treat unknown as 2-cycle NOP when enabled.
- [x] Configurable inline interrupt polling (Impact:L-M Risk:None)
- [x] Decimal flag semantics eliminated (Impact:L Risk:None)

### 1.2 Active / Planned Tasks
- [ ] Idle loop detection & fast-forward (Impact:H Risk:M Optional)  
	- [ ] Heuristic: detect tight polling on $2002/$4015 with backward branch.  
	- [ ] Fast-forward until triggering event (NMI/IRQ flag set)
	- [ ] Metrics: loops skipped, cycles saved
- [ ] Batch execute N instructions before sync (Impact:M-H Risk:M Optional)  
	- [ ] Configurable batch size (e.g. 16/32)  
	- [ ] Early abort on IO/PPU/APU register access
- [ ] Fast OAM DMA stall approximation (Impact:M Risk:L-M Optional)  
	- [ ] Replace per-cycle stepping with lumped cycle add  
	- [ ] Ensure IRQ/NMI timing fairness
- [ ] Zero-page hot cache (Impact:L-M Risk:L)  
	- [ ] Shadow 256B;  
	- [ ] Invalidate on DMA/mapper writes overlapping ZP
- [ ] Page-cross penalty precomputation (Impact:L Risk:None)
- [ ] Combine ADC/SBC logic (Impact:L Risk:None)
- [ ] Branch prediction (tiny saturating counters) (Impact:L Risk:L Experimental)
- [ ] Micro-op fusion (LDA+STA memcpy style loops) (Impact:M Risk:M Optional)  
	- [ ] Detect hot self-contained copy loops  
	- [ ] Emit accelerated copy path
- [ ] Partial cycle skipping during DMA (Impact:L-M Risk:M)
- [ ] Lightweight trace JIT (desktop only) (Impact:VH Risk:H Optional Experimental)  
	- [ ] Hot block profiling  
	- [ ] IL emit / native code  
	- [ ] State sync & invalidation strategy

### 1.3 Prioritization Snapshot
- [ ] Phase 1: Idle loop fast-forward
- [ ] Phase 1: Batch execution core scaffolding
- [ ] Phase 1: Fast OAM DMA stall approximation
- [ ] Phase 2: Zero-page hot cache
- [ ] Phase 2: Micro-op fusion (after instrumentation)
- [ ] Phase 3: Trace JIT experiment (desktop only)

---
## 2. PPU (PPU_SPD)

### 2.1 Baseline (Completed)
- [x] Scanline-level stepping (Impact:H Risk:M)
- [x] Lazy framebuffer allocation (Impact:L Risk:None)
- [x] Reuse sprite & mask arrays (Impact:L Risk:None)
- [x] Simplified sprite 0 hit (Impact:L-M Risk:L-M)
- [x] Immediate MMC3 scanline IRQ at cycle 260 (Impact:M Risk:M)
- [x] Fast OAM DMA bulk copy (Impact:M Risk:L)

### 2.2 Active / Planned Tasks
- [x] Background tile fetch batching (Impact:M Risk:L)  
	- [x] Pre-decode 32 tiles -> scanline cache (metadata arrays)  
	- [x] Reuse across fine X shifts within line (rowBits reused)  
	- Toggle: `SpeedConfig.PpuTileBatching` (default: enabled)
- [x] Precomputed pattern line expansion cache (Impact:M Risk:L)  
	- [x] Expand plane bytes -> color indices once per tile line  
	- [x] Invalidate on pattern table write  
	- Toggle: `SpeedConfig.PpuPatternCache` (default: enabled)
- [ ] Dirty column / partial frame rendering (Impact:M Risk:M Optional)  
	- [ ] Track name table writes (bitmask per 8x8 column)  
	- [ ] Redraw only changed columns
- [ ] Sprite evaluation skip when sprites disabled (Impact:L Risk:None)
- [ ] Palette & attribute quadrant cache (Impact:L-M Risk:L)
- [x] Skip rendering blank scanlines (Impact:L Risk:L) — implemented via batchAllZero fast fill
	- Toggle: `SpeedConfig.PpuSkipBlankScanlines` (default: enabled)
- [ ] Coarse sprite 0 hit shortcut (Impact:L Risk:M Optional)
- [ ] Optional sprite limit removal + fast iteration (Impact:L Risk:M Optional)
- [ ] Fast palette read path (unsafe optional) (Impact:L Risk:None Optional)

### 2.3 Prioritization Snapshot
- [ ] Phase 1: Background tile batching
- [ ] Phase 1: Pattern line expansion
- [ ] Phase 2: Dirty column rendering
- [ ] Phase 2: Sprite evaluation skip / palette cache

---
## 3. APU (APU_SPD)

### 3.1 Baseline (Completed)
- [ ] LUT nonlinear mixer (Impact:M Risk:None) (Not yet implemented in current APU_SPD)
- [ ] Multi-cycle fast-forward event loop (Impact:H Risk:M) (Current code is per-cycle)
- [x] Ring buffer w/ capped copy-out (Impact:L Risk:None)
- [ ] DMC channel + batched bit events (Impact:M Risk:M) (DMC absent presently)
- [x] Simplified filter chain (Impact:L Risk:L)
- [x] Deferred seq-based env/length/sweep updates (Impact:L Risk:None)
- [x] Per-cycle stepping core (baseline reference implementation)

### 3.2 Active / Planned Tasks
- [ ] Implement DMC channel (sample fetch, delta counter, IRQ) (Impact:M Risk:M Optional)
- [ ] Add DMC batched bit/event processing loop (Impact:M Risk:M)  
	- [ ] Integrate with new fast-forward stepping when added
- [ ] Introduce LUT nonlinear mixer (Pulse + TND tables) (Impact:M Risk:None Optional)  
	- [ ] Allocate tables lazily to manage WASM memory
- [ ] Replace per-cycle loop with multi-event fast-forward stepping (Impact:H Risk:M Optional)  
	- [ ] Advance to next earliest timer/sequence/sample boundary
- [x] Silent channel skip (Impact:M Risk:L-M Optional) — Implemented fast-forward when all channels inactive (bulk frame sequencer advance + silence sample batching)
	- NOTE: Added gating (min batch cycles) + instrumentation after initial regression; monitor counters
- [ ] Lower internal sample rate mode (Impact:M Risk:M Optional)  
	- [ ] 22050 Hz internal -> upsample
- [ ] Approximate reduced LUT mixer (Impact:L Risk:L-M Optional)  
	- [ ] Single (t+n+d) index variant for WASM memory footprint
- [ ] DMC fetch pre-buffer queue (Impact:L Risk:L)
- [x] Skip envelope decay when constantVolume (Impact:L Risk:L) — Implemented (toggle: SpeedConfig.ApuSkipEnvelopeOnConstantVolume)
	- Instrumented skip event counter for telemetry
- [ ] Batch multiple frame seq steps on idle fast-forward (Impact:L Risk:M)
- [ ] Audio frameskip in turbo mode (Impact:H Risk:H Optional)

### 3.3 Prioritization Snapshot
- [ ] Phase 1: Silent channel skip
- [ ] Phase 1: Lower sample rate toggle
- [ ] Phase 2: Reduced LUT mixer
- [ ] Phase 2: DMC pre-buffer
- [ ] Phase 3: SIMD mixing + batching improvements

---
## 4. Cross-Subsystem / Scheduler
- [ ] Dynamic throttle (adjust CPU/APU batch size) (Impact:M Risk:M Optional)
- [ ] Global frame skip / fast-forward orchestrator (Impact:H Risk:H Optional)
- [ ] Idle frame detection (Impact:M Risk:M Experimental)  
	- [ ] Criteria: no input, no VRAM writes, minimal audio delta
- [x] Mapper IRQ coarse scheduling (MMC3 scanline) (Impact:M Risk:M)
- [ ] Generalize coarse IRQ scheduling for other mappers (Impact:M Risk:M)

---
## 5. Configuration & Telemetry Infrastructure
- [ ] Introduce `SpeedConfig` struct (Impact:L Risk:None)
	- [x] Basic `SpeedConfig` class added (`SpeedConfig.cs`) with `ApuSilentChannelSkip` toggle default enabled
- [ ] Central registry to propagate config to cores (Impact:L Risk:None)
- [ ] Presets (Accurate / Balanced / Fast / Turbo) (Impact:M Risk:M)
- [ ] Per-hack enable flags serialization (Impact:L Risk:None)
- [ ] Telemetry counters (idle loops skipped, tiles decoded, samples mixed) (Impact:M Risk:L)
- [ ] On-screen debug overlay (Impact:L Risk:L Optional)
- [ ] Auto-disable anomaly detector hooks (Impact:M Risk:M Optional)

---
## 6. Implementation Guidelines (Reference)
- [x] Guard hacks with booleans / strategy enums
- [ ] Centralize toggles (pending `SpeedConfig`)
- [x] Avoid premature unsafe code (policy note)
- [x] Preserve save-state determinism; recompute derived state
- [x] Small focused commits per hack (process guideline)
- [ ] Micro-benchmark doc (separate) — create outline

---
## 7. Accuracy Risk Mitigation
- [ ] Curate regression ROM list (SMB1 scroll, Kirby IRQ, MMC3 IRQ tests, DMC IRQ, sprite 0, Blargg APU) (Impact:M Risk:None)
- [ ] Tag hacks with affected test categories (Impact:L Risk:None)
- [ ] Per-game override config (hash-based) (Impact:M Risk:L)
- [ ] Quick toggle UI/CLI for bisecting issues (Impact:L Risk:None)

---
## 8. Phased Roadmap (Execution Order)
Phase 1 (Foundational & High ROI):
- [ ] `SpeedConfig` scaffolding
- [ ] CPU idle loop detection
- [ ] CPU batch execution base
- [ ] PPU background tile batching
- [ ] APU silent channel skip

Phase 2 (Broad Caching & Rendering Optimizations):
- [ ] PPU pattern line expansion
- [ ] PPU dirty column rendering
- [ ] APU reduced LUT mixer (memory saver)
- [ ] CPU zero-page hot cache

Phase 3 (Optional / Higher Risk / Platform Specific):
- [ ] Frame skip system
- [ ] Static frame reuse
- [ ] CPU micro-op fusion
- [ ] APU lower sample rate toggle
- [ ] DMC pre-buffer queue
- [ ] SIMD audio mixing (desktop)

Phase 4 (Experimental / Advanced):
- [ ] Dynamic throttle scheduler
- [ ] Trace JIT prototype (desktop)
- [ ] Idle frame detection
- [ ] Expanded mapper IRQ coarse scheduling

---
## 9. Measuring Success
- [ ] Instrument average ms/frame per preset
- [ ] Log CPU instructions executed vs skipped
- [ ] Track PPU tiles/columns redrawn vs reused
- [ ] Track APU samples mixed & channel active ratios
- [ ] Report memory footprint delta per hack
- [ ] Aggregate “speed score” (normalized vs Accurate baseline)

Success Target (draft): Balanced preset => ≥25% frame time reduction vs fully accurate baseline with >99% commercial game compatibility.

---
## 10. Out of Scope / Rejected
- [x] Generic GPU acceleration of core PPU logic (complexity outweighs benefit here)
- [x] JIT on WebAssembly target (sandbox limitation)
- [x] Per-game hardcoded timing hacks (maintain general heuristics only)

---
Document maintained as a living task list; update checkboxes and notes as progress is made.
