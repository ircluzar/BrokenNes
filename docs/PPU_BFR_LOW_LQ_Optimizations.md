# PPU_BFR / PPU_LOW / PPU_LQ Optimization Matrix

Status legend per core: [x] implemented / verified, [~] partial, [ ] planned.

Core key: (BFR / LOW / LQ)

These three are “classic” NES PPU style cores (no multi-layer shadow system that the CUBE core has). Many CUBE optimizations still apply or can be safely ported. LOW already incorporates some forward‑looking micro-optimizations (palette packing, universal BG prefill). LQ intentionally keeps certain degradations; optimizations must not erase its intended “lower spec flavor,” but we still want the hot loops lean.

---
## 1. Core Rendering Pipeline
- Background tile inner loop unrolling & reduced branches ............. (BFR:[ ] LOW:[x]* LQ:[ ])  *LOW uses collapsed bounds `(uint)pixel` and minimal per-pixel work; a light explicit unroll still pending.*
- Unsigned bounds check `(uint)pixel >= 256` pattern .................. (BFR:[ ] LOW:[x] LQ:[ ])
- Shift-based RGBA index math `(pixel << 2)` instead of multiply ...... (BFR:[ ] LOW:[x] LQ:[ ])
- Per-scanline universal BG prefill (skip writing colorIndex==0) ...... (BFR:[ ] LOW:[x] LQ:[ ])  *LQ currently writes backdrop on transparency inside loop; consider prefill to cut stores.*
- Batched PPU.Step (multi-cycle loop) ................................. (BFR:[x] LOW:[x] LQ:[x])
- Remove redundant per-pixel alpha writes (alpha preset to 255) ....... (BFR:[ ] LOW:[x] LQ:[ ])
- Fine scroll / VRAM increment correctness retained after refactors ... (BFR:[x] LOW:[x] LQ:[x])
- Optional dual‑pixel processing (nibble parallelism) ................. (BFR:[ ] LOW:[ ] LQ:[ ])

## 2. Palette & Color Handling
- Packed master 64-color RGBA table (once) ............................ (BFR:[ ] LOW:[x] LQ:[ ])
- Palette RAM -> cached packed entries (32) ........................... (BFR:[ ] LOW:[x] LQ:[ ])
- Lazy rebuilt palette cache on writes (dirty flag) ................... (BFR:[ ] LOW:[x] LQ:[ ])
- Inline sprite color fetch (remove GetSpriteColor call) .............. (BFR:[ ] LOW:[ ] LQ:[ ])
- Unified utility for universal BG color quick access ................. (BFR:[ ] LOW:[x] LQ:[ ])
- Darken / effect LUT (not shadows; maybe fade) ....................... (BFR:[x]* LOW:[ ] LQ:[ ])  *BFR has adaptive background fade (float→int cached); keep as LUT for speed.*
- Branchless palette entry select (colorIndex 1/2/3) .................. (BFR:[ ] LOW:[x] LQ:[ ])

## 3. Sprite System
- Reusable per-scanline sprite drawn mask array ....................... (BFR:[x] LOW:[x] LQ:[x])
- Sprite scanline indexing (pre-filter list) .......................... (BFR:[ ] LOW:[ ] LQ:[ ])
- OAM dirty tracking (only rebuild index when changed) ................ (BFR:[ ] LOW:[ ] LQ:[ ])
- Support optional hardware-accurate 8-sprite limit ................... (BFR:[ ] LOW:[ ] LQ:[ ])
- Configurable faster “skip hidden / off-screen” early continues ...... (BFR:[ ] LOW:[x] LQ:[x])  *LOW/LQ already early-continue for vertical & bounds.*
- Branch order: transparency → bounds → already-drawn → priority ...... (BFR:[ ] LOW:[x] LQ:[x])
- Pack sprite palette colors before per-pixel (local cache) ........... (BFR:[ ] LOW:[ ] LQ:[ ])

## 4. Pattern / CHR Access
- Pattern row decode cache (per tile row) ............................. (BFR:[ ] LOW:[ ] LQ:[ ])
- Generation-based invalidation on CHR writes ......................... (BFR:[ ] LOW:[ ] LQ:[ ])
- Mapper bank switch hook to auto-invalidate .......................... (BFR:[ ] LOW:[ ] LQ:[ ])
- Optional static CHR persistence (no bank events) .................... (BFR:[ ] LOW:[ ] LQ:[ ])
- Dual-plane packing & pre-expanded 2-bit -> color index table ........ (BFR:[ ] LOW:[ ] LQ:[ ])

## 5. Effects / Degradations (LQ specific constraints)
- Maintain LQ bandwidth budget logic (not removed by opt) ............. (BFR:[—] LOW:[—] LQ:[x])
- Keep DRAM stall scanline duplication feature ........................ (BFR:[—] LOW:[—] LQ:[x])
- Post-scanline quantization (2-2-2 w/ attenuation) ................... (BFR:[—] LOW:[—] LQ:[x])
- Vectorize quantization (SIMD when available) ........................ (BFR:[ ] LOW:[ ] LQ:[ ])

## 6. Memory & Allocation
- Zero allocations per frame (steady arrays only) ..................... (BFR:[x] LOW:[x] LQ:[x])
- Convert short-lived bool[] bgMask to stackalloc Span<byte> per line . (BFR:[ ] LOW:[ ] LQ:[ ])  *Need careful lifetime; Wasm stack constraints measured first.*
- Pool large temporary buffers (future features) ...................... (BFR:[ ] LOW:[ ] LQ:[ ])
- Replace Array.Clear with Span.Fill(false) / Unsafe.InitBlock ........ (BFR:[ ] LOW:[ ] LQ:[ ])

## 7. Branch & Instruction Reduction
- Reorder sprite pixel tests (transparency first) ..................... (BFR:[x] LOW:[x] LQ:[x])
- Collapse bounds checks via unsigned casts ........................... (BFR:[ ] LOW:[x] LQ:[ ])
- Hoist constant per-tile computations outside inner pixel loop ....... (BFR:[ ] LOW:[x] LQ:[ ])
- Branchless colorIndex decode (lookup[plane0Bit | plane1Bit<<1]) ..... (BFR:[ ] LOW:[ ] LQ:[ ])
- Two-pixel parallel extraction using bit slicing ..................... (BFR:[ ] LOW:[ ] LQ:[ ])

## 8. Frame-Level Optimizations
- Dirty frame skip (no VRAM/OAM/palette changes) ...................... (BFR:[ ] LOW:[ ] LQ:[ ])
- Partial scanline redraw (tile span dirties) ......................... (BFR:[ ] LOW:[ ] LQ:[ ])
- Headless mode short-circuit (offscreen / no consumer) ............... (BFR:[ ] LOW:[ ] LQ:[ ])
- Adaptive LQ degradation intensity (dynamic budgets) ................. (BFR:[—] LOW:[—] LQ:[ ])

## 9. SIMD / Advanced
- Vectorized background prefill (write 16 pixels per iteration) ....... (BFR:[ ] LOW:[ ] LQ:[ ])
- SIMD palette expansion / quantization .............................. (BFR:[ ] LOW:[ ] LQ:[ ])
- WASM threads: off-main-thread scanline build (future) ............... (BFR:[ ] LOW:[ ] LQ:[ ])

## 10. Diagnostics & Tuning
- Lightweight counters: spritesProcessed / tilesDecoded ............... (BFR:[ ] LOW:[ ] LQ:[ ])
- Cache hit/miss stats (pattern/palette) .............................. (BFR:[ ] LOW:[ ] LQ:[ ])
- Toggle build: FAST_EMU_STATS define gating counters ................. (BFR:[ ] LOW:[ ] LQ:[ ])
- Visual overlay (sprite coverage / fetch budget used) ................ (BFR:[ ] LOW:[ ] LQ:[ ])

## 11. API / Config Surface
- Expose SetBackgroundFade / EnableAutoFade (already BFR) ............. (BFR:[x] LOW:[ ] LQ:[ ])
- Unified configuration object (init-time) ............................ (BFR:[ ] LOW:[ ] LQ:[ ])
- Toggle: EnablePatternCache / EnableSpriteIndexing ................... (BFR:[ ] LOW:[ ] LQ:[ ])
- LQ: knobs for budget min/max & quantization strength ................ (BFR:[—] LOW:[—] LQ:[ ])

## 12. Safety / Correctness Notes
- Preserve sprite 0 hit timing semantics .............................. (BFR:[x] LOW:[x] LQ:[x])
- VRAM increment / coarse/fine scroll logic unchanged ................. (BFR:[x] LOW:[x] LQ:[x])
- Palette mirroring ($3F10/$3F14/...) honored ........................ (BFR:[x] LOW:[x] LQ:[x])
- Mapper IRQ scanline trigger preserved .............................. (BFR:[x] LOW:[x] LQ:[x])
- LQ degradations never corrupt game logic state ...................... (BFR:[—] LOW:[—] LQ:[x])

## 13. Immediate Next Steps (Recommended Order)
1. LOW: Minor explicit unroll of background pixel loop (process 2 or 4 pixels per iteration) after profiling to ensure win in WASM.
2. Shared: Introduce optional pattern row cache (tileIndex+fineY -> 2 bytes packed) with generation counter; wire VRAM writes & mapper bank switch to invalidate.
3. BFR & LQ: Port LOW's packed master palette + 32-entry cache; convert per-pixel palette lookups to cached packed RGBA writes.
4. Shared: Implement sprite scanline indexing (array[240] of small structs listing sprite indices); rebuild only when OAM or size mode dirty.
5. Shared: Add cheap dirty-frame hash (running CRC/hash of VRAM+OAM+palette pointers/versions) to skip full frame when static.
6. LQ: Replace inner per-pixel backdrop writes with universal prefill like LOW, keeping bandwidth budget logic (budget compares then early fill remainder).
7. Shared: Provide compile-time FAST_NO_SPRITES / FAST_BG_ONLY flags for benchmarking.
8. Evaluate branchless colorIndex decode LUT (four-entry) and two-pixel extraction to reduce shifts/masks.

## 14. Profiling Guidance
- Capture baseline frame time (WASM) for each core with: no ROM (static), light ROM (few sprites), heavy sprite ROM (MMC3 IRQ). Identify top 3 hotspots per core (likely background loops & sprite loops).
- After each optimization, gather delta % and ensure no regression to correctness (NES test ROMs: scrolling, sprite 0 hit, palette emphasis if implemented later).
- For LQ ensure degradation variance (DRAM stall probability) remains stable frame-to-frame; changes must not accidentally make stalls deterministic.

## 15. Constraints / Cautions
- Avoid large stackalloc on WASM—measure typical stack headroom before moving bgMask.
- Pattern cache must respect CHR bank switching mid-frame (may opt for per-scanline safe re-check if mapper triggers mid-frame changes; first version can invalidate whole cache on any CHR write/bank change).
- Keep BFR background fade computation out of per-pixel path (already cached int 0..256). Future LUT approach should build table once when alpha changes.
- LQ optimizations must not eliminate *intended* fetch shortfall / DRAM shimmer—guard with #if LQ_DEGRADE or runtime flag.

## 16. Potential Future Trade-Off Flags
- FAST_BG_ONLY (skip sprites) ......................................... (Planned)
- FAST_LIMIT8 (cap to 8 sprites like hardware even if more drawn) ..... (Planned)
- DISABLE_PATTERN_CACHE (debug correctness) ........................... (Planned)
- LQ_DISABLE_DEGRADATION (pure speed / dev mode) ...................... (Planned)

## Summary
BFR is currently a mostly straightforward correctness core augmented with a dynamic background fade effect; it lacks palette packing, scanline prefill, and pattern caching. LOW is the most optimized baseline (palette caches, packed writes, universal BG prefill, several branch reductions) and serves as the template for shared improvements. LQ keeps stylistic degradations; we can still graft safe micro-optimizations (palette packing, prefill, scanline indexing) without losing its personality. Highest ROI items next: pattern row cache, sprite scanline index, and bringing palette packing to BFR & LQ.

---
Last updated: 2025-08-12
