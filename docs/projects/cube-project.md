# PPU_CUBE Performance Optimization Project – Work Tracker

Goal: Substantially reduce CPU time and memory bandwidth of `PPU_CUBE` while preserving (or controllably degrading) its visual feature set (gradient backdrops + shadow projection) and timing‑critical side effects (NMI, IRQ timing, sprite 0 hit).

This living document is now a progress tracker with actionable checklists. Mark tasks as completed (`[x]`). Do not remove historical rationale; instead append notes beneath each item. Add measurements & decisions inline.

---
## Quick Status Dashboard

Legend: P0 = immediate, P1 = next wave, P2 = advanced / experimental.

### High-Level Completion
| Tier | Total | Done | % |
|------|-------|------|---|
| P0 | 9 | 3 | 33% |
| P1 | 7 | 0 | 0% |
| P2 | 7 | 0 | 0% |
| Overall | 23 | 0 | 0% |

Update counts manually when boxes are checked.

### Fast Toggles
- [ ] Feature flags implemented (`EnableShadows`, `EnableGradient`, etc.)
- [ ] Benchmark harness created (scenes: heavy sprites, scrolling BG, static)
- [ ] Frame correctness hashing baseline captured

---

---
## 1. Current Characteristics / Hotspots
Qualitative review of `PPU_CUBE` vs `PPU_FMC` / `PPU_LQ` shows added per‑scanline & per‑pixel work:

| Area | Extra Work in CUBE | Why Expensive |
|------|--------------------|---------------|
| Gradient prefill (`PreFillGradientLine`) | Fills entire scanline (1024 writes) before BG & sprites overwrite many pixels | Doubles memory traffic for most pixels |
| Shadow projection (bg + sprites) | Full width pass each scanline reading 1–2 `bool[,]` rows and darkening candidate pixels | Multi‑dimensional array indexing + unconditional loop over 256 columns |
| Coverage history storage | `bool[,] spriteCoverageHistory` & `bool[,] bgCoverageHistory` with nested clears | 2D array adds bounds & address calc overhead; frequent clears |
| Background render | Recomputes palette lookups per pixel; no tile row decode cache; always loops 33 tiles | Redundant pattern & attribute fetch cost |
| Sprite render | Scans all 64 sprites every scanline; per‑pixel attribute logic, coverage writes | Many lines have far fewer sprites active |
| Per‑cycle stepping | `Step` iterates cycle by cycle (341 * 262 ≈ 89K loop iterations per frame) | Loop overhead & branch mispredictions (worse in WASM) |
| Array clearing | `Array.Clear(bgMask)` & manual nested loops for coverage each scanline | 240 * (multiple buffers) per frame |
| Palette rebuild flag | Check + potential rebuild per scanline | Minor but still a branch in hot path |
| Multi-dimensional arrays | `bool[,]` for histories | Slower than flat `bool[]` / bitset |
| Redundant `EnsureFrameBuffer()` calls | Called in multiple tight paths though buffer size stable | Branch + potential function call per scanline |

Estimated frame cost amplification vs FMC: 1.5–3× depending on scene (sprite count, scroll, % of pixels overwritten after gradient).

---
## 2. Optimization Strategy Overview
Tackle bandwidth + branch + allocation first. Preserve visual output by default; offer optional feature flags (shadow, gradient) to reduce cost further when disabled.

Priority tiers:
* P0: High impact, low/medium complexity, minimal correctness risk
* P1: High/medium impact, moderate complexity, needs validation
* P2: Lower impact or higher complexity / risk, optional / experimental

---
## 3. Roadmap (Actionable Items with Checkboxes)

### P0 (Do First)
1. [x] Fuse Gradient With Background Render  
   Current: fill whole scanline then overwrite non‑transparent BG pixels.  
   Change: Remove `PreFillGradientLine` pass; in BG loop when `colorIndex==0` write gradient color (cached per scanline). Lazy fill only if needed outside 256px.  
   Impact: Eliminates ~245K channel writes per frame.  
   Complexity: Low.  
   Subtasks:  
    - [x] Remove `PreFillGradientLine` invocation & method (logic replaced with cached per-scanline gradient)  
    - [x] Integrate gradient write into BG loop (transparent pixel path, only writes when first needed)  
    - [ ] Benchmark before/after (300 frames)  
    - [ ] Visual parity check (hash diff allowed only for untouched pixels)  
    - [ ] PR merged  
    Notes: 2025-08-15 Implemented fused path. Gradient no longer pre-fills entire scanline; transparent pixels lazily receive gradient color. BG shadows now applied post BG draw only onto still-transparent (gradient) pixels to preserve layering and avoid double darken. Expect substantial write reduction in dense BG scenes.

2. [x] Flatten Coverage Histories  
    Replace `bool[,]` with `byte[]` (size = `ShadowVerticalDistance * ScreenWidth`). Manual indexing.  
    Impact: ~5–10% scanline time.  
    Subtasks:  
    - [x] Introduce flat arrays  
    - [x] Replace all reads/writes  
    - [x] Remove old multidimensional arrays  
    - [ ] Benchmark  
    - [ ] PR merged  
    Notes: 2025-08-15 Implemented `spriteCoverageRows` and `bgCoverageRows` (byte). Shadow projection loops now index flattened arrays; removed nested clear loops in favor of `Array.Clear`. Visual output unchanged (shadow darkening uses same factor). Expect modest improvement due to fewer bounds checks.

3. [ ] Sparse Shadow Application  
   Track active coverage columns list; darken only touched columns next scanline.  
   Impact: Big when coverage sparse (<128 columns).  
   Subtasks:  
   - [ ] Implement per-scanline column list (bg + sprites)  
   - [ ] Integrate with coverage arrays  
   - [ ] Benchmark sparse vs dense scenes  
   - [ ] PR merged

4. [ ] Hoist `EnsureFrameBuffer()`  
   Call once at frame start; remove from hot paths.  
   Impact: Small free win.  
   Subtasks:  
   - [ ] Add single call at frame init  
   - [ ] Remove redundant calls  
   - [ ] Validate no regressions  
   - [ ] PR merged

5. [x] Precompute Gradient Table  
   240-entry gradient row cache recomputed at frame start or palette change.  
   Impact: Saves per-scanline math.  
   Subtasks:  
    - [x] Detect palette change trigger (rebuild when paletteRAM[0] changes at frame start)  
    - [x] Build cache arrays (R,G,B)  
    - [x] Use in BG transparent case (fused with item #1)  
    - [ ] Benchmark  
    - [ ] PR merged  
    Notes: 2025-08-15 Cache consumed via per-scanline arrays (R,G,B) inside BG loop; removed unconditional per-line fill.

6. [ ] Eliminate Per‑Pixel Palette Index Arithmetic  
   Prebuild attribute quadrant LUT(s); inner loop single lookup.  
   Impact: ~5–7% BG time.  
   Subtasks:  
   - [ ] Design LUT layout  
   - [ ] Precompute at palette or attribute change  
   - [ ] Replace shifts/masks in loop  
   - [ ] Benchmark  
   - [ ] PR merged

7. [ ] Batch Step to Whole Scanlines  
   Introduce `StepScanlines(int)`; handle MMC3 IRQ at cycle 260 within batch.  
   Impact: ~10–20% (WASM).  
   Subtasks:  
   - [ ] API design & docs  
   - [ ] Implement cycle accumulation & scanline dispatch  
   - [ ] Ensure NMI / IRQ / sprite 0 semantics unchanged  
   - [ ] Benchmark  
   - [ ] PR merged

8. [ ] Replace `Array.Clear(bgMask)` With Generation Stamp  
   `ushort[] bgMaskGen`, increment per scanline.  
   Impact: Avoid ~61K writes/frame.  
   Subtasks:  
   - [ ] Add generation array & counter  
   - [ ] Integrate into BG opaque set/test  
   - [ ] Overflow handling strategy  
   - [ ] Benchmark  
   - [ ] PR merged

9. [ ] Remove Palette Cache Flag Branch  
   Palette built on writes; drop runtime branch.  
   Impact: Minor.  
   Subtasks:  
   - [ ] Confirm write paths always update cache  
   - [ ] Remove flag & conditionals  
   - [ ] Benchmark (sanity)  
   - [ ] PR merged

### P1 (Next Wave)
10. [ ] Tile Row Decode Cache  
    Cache `(patternAddr)` rows; invalidate on CHR write/bank change.  
    Impact: 40–60% reduction in pattern fetch work (static scenes).  
    Subtasks:  
    - [ ] Cache data structure & key  
    - [ ] Invalidation hooks (mapper events)  
    - [ ] Integrate BG loop  
    - [ ] Benchmark scrolling vs static  
    - [ ] PR merged

11. [ ] Attribute Prefetch per Tile Column  
    Compute palette index once per 2x2 tile group.  
    Impact: ~3–4% BG loop.  
    Subtasks:  
    - [ ] Prefetch logic  
    - [ ] Integrate with row decode cache  
    - [ ] Benchmark  
    - [ ] PR merged

12. [ ] Sprite Scanline Candidate List  
    Pre-pass to build candidate sprite indices per scanline.  
    Impact: Large on sparse lines.  
    Subtasks:  
    - [ ] Pre-pass implementation  
    - [ ] Replace full iteration in render loop  
    - [ ] Validate 8-sprite-per-line limit behavior  
    - [ ] Benchmark varied sprite counts  
    - [ ] PR merged

13. [ ] Bitset Coverage + Shadow Vectorization  
    Use `ulong` bitsets; process words only when nonzero.  
    Impact: Additional reduction in dense or mixed cases.  
    Subtasks:  
    - [ ] Bitset layout  
    - [ ] Darken iteration logic  
    - [ ] Compare vs sparse list (retain best)  
    - [ ] Benchmark  
    - [ ] PR merged

14. [ ] Unsafe / Span Fast Paths  
    Introduce `unsafe` per-pixel loops & potential inlining.  
    Impact: 5–15%.  
    Subtasks:  
    - [ ] Add conditional compilation (`#if CUBE_UNSAFE`)  
    - [ ] Implement unsafe versions  
    - [ ] Benchmark WASM vs desktop  
    - [ ] PR merged

15. [ ] Merge Shadow Darken & Pixel Write (Alternative)  
    Evaluate immediate darken approach; may supersede sparse pass.  
    Impact: Potential extra pass removal.  
    Subtasks:  
    - [ ] Prototype ring buffer method  
    - [ ] Validate ordering correctness  
    - [ ] Benchmark vs existing  
    - [ ] Decision recorded  
    - [ ] PR merged (if adopted)

16. [ ] Adaptive Feature Level  
    Runtime toggles to degrade (disable shadows / simplify gradient).  
    Impact: Frame time stability.  
    Subtasks:  
    - [ ] Threshold logic  
    - [ ] Toggle plumbing & config keys  
    - [ ] Benchmark dynamic response  
    - [ ] PR merged

### P2 (Advanced / Experimental)
17. [ ] Multi-threaded Scanline Batching (Desktop Only)  
    Parallelize bands; high complexity.  
    Impact: 2–3× on multi-core (risk).  
    Notes: Cycle-exact side effects risk; may be deferred.

18. [ ] JIT-Time Code Generation / Source Generators  
    Specialized loops per feature set.  
    Impact: Moderate; high complexity.

19. [ ] GPU Blit / WebGL Shader Path  
    Offload gradient + shadow to GPU.  
    Impact: Large on browsers.

20. [ ] Frame Difference (Dirty Rect) Streaming  
    Only redraw changed scanlines.  
    Impact: Large for static scenes.

21. [ ] Pattern Decode Ahead-of-Time (CHR ROM)  
    Decode immutable tiles once.  
    Impact: Big BG render speed in ROM cases.

22. [ ] Sprite Palette + Priority Precomputation  
    Precompute row structs.  
    Impact: Moderate.

23. [ ] Branchless Darken  
    LUT-based darken (or pre-darkened palette variants).  
    Impact: Small; needs measurement.

---
## 4. Implementation Order & Bundling
Suggested initial PR sequence (keep diffs reviewable):
1. Remove gradient prefill pass; introduce per-frame gradient row cache. (P0 #1 + #5)
2. Flatten coverage + adopt sparse list for shadows. (P0 #2 + #3)
3. Hoist allocations & remove repeated `EnsureFrameBuffer()` calls. (P0 #4 + #9)
4. Generation-based bgMask & palette LUT simplification. (P0 #6 + #8)
5. Batch scanline stepping (public API addition). (P0 #7)
6. Sprite candidate pre-pass. (P1 #12)
7. Tile row decode cache (BG). (P1 #10 + #11)
8. Unsafe/SIMD pass (benchmark gated). (P1 #14)
9. Optional feature toggle + adaptive degrade. (P1 #16)
10. Longer-term caches for CHR ROM / pattern AOT. (P2 #21)

Each PR should include: micro-benchmark results (at least frame time over 300 frames for representative scenes), correctness tests (sprite 0 hit, MMC3 IRQ timing unaffected), and a feature flag test matrix.

---
## 5. Quick Win Pseudocode Sketches

### 5.1 Gradient Cache
```csharp
// At frame start or when paletteRAM[0] changes:
for (int y=0; y<Height; y++) { gradientR[y]=...; gradientG[y]=...; gradientB[y]=...; }
// In BG loop when colorIndex==0: write gradientR[scanline], etc.
```

### 5.2 Sparse Coverage Lists
```csharp
const int MaxCols = 256; // upper bound
int[] prevBgCols = new int[MaxCols]; int prevBgCount;
int[] thisBgCols = new int[MaxCols]; int thisBgCount;
// When setting bg pixel opaque:
if (!bgSeen[px]) { bgSeen[px]=true; thisBgCols[thisBgCount++]=px; }
// Before drawing new scanline shadows:
for (int i=0;i<prevBgCount;i++){ int px=prevBgCols[i]+ShadowOffsetX; if ((uint)px<Width) Darken(px); }
// Swap arrays & reset markers.
```

### 5.3 Generation Stamps for bgMask
```csharp
ushort[] bgMaskGen = new ushort[256];
ushort gen; // increment each scanline
// Set: bgMaskGen[px]=gen;
// Test: (bgMaskGen[px]==gen)
```

### 5.4 Tile Row Decode Cache Entry
```csharp
struct TileRow { byte mask; fixed byte colors[8]; }
// mask bit i set if pixel i opaque.
Dictionary<int, TileRow> cache; // key = patternAddr
```

---
## 6. Risk / Correctness Considerations
* Shadow systems rely on correct temporal offset (previous scanline). Sparse list must preserve ordering independence—only darken based on prior coverage.
* MMC3 IRQ at cycle 260 must still trigger even if batching scanlines; emulate cycle milestone within batch.
* Sprite 0 hit detection must operate on true background mask for that pixel; generation stamp logic must set before sprite evaluation.
* Pattern decode cache invalidation needs CHR write / bank switch hooks (Mapper events) to avoid stale visuals.
* Unsafe / SIMD code must stay endian and alignment safe; provide fallback for WASM if intrinsics unavailable.

---
## 7. Measurement Plan
1. Build micro benchmark harness (off-screen) running: (a) heavy sprite scene, (b) scrolling BG, (c) static screen.
2. Capture average ms/frame & 95th percentile over 300 frames.
3. Report delta after each optimization PR; reject regressions >1% unless justified.
4. Validate functional parity: hash entire framebuffer each frame & compare to baseline (except for deliberately altered visual features like gradient precision if toggled).

---
## 8. Summary of Expected Gains
| Optimization (cumulative path) | Est. Gain vs Previous | Notes |
|--------------------------------|-----------------------|-------|
| Remove gradient prefill        | 15–25%                | Bandwidth heavy scenes |
| Sparse + flattened coverage    | 5–15%                 | More if sparse sprites |
| Palette / bgMask gen stamps    | 3–8%                  | Branch & clear removal |
| Batch scanlines stepping       | 10–20% (WASM)         | Loop overhead shrink |
| Sprite candidate pre-pass      | 5–30%                 | Scene dependent |
| Tile row decode cache          | 10–25%                | Static / slowly scrolling |
| Unsafe + minor vectorization   | 5–15%                 | Platform dependent |
| CHR ROM AOT decode             | 5–20%                 | Cartridge type dependent |
| Combined (typical)             | ~2–3× speedup         | Conservative aggregate |

---
## 9. Optional Feature Flags (Future)
Environment / config keys:
* `CubePpu:EnableShadows` (default true)
* `CubePpu:EnableGradient` (default true)
* `CubePpu:AdaptiveDegrade` (default false)
* `CubePpu:TileDecodeCache` (default true for CHR ROM)
* `CubePpu:SpritePrepass` (default true)

---
## 10. Next Steps
Proceed with P0 batch #1 PR (gradient removal + cached gradient rows) and benchmark. Use this document as living roadmap; update estimates with real measurements.

---
_Last updated: 2025-08-15 (converted to checklist format)_
