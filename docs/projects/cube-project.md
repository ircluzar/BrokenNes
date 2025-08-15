# PPU_CUBE Performance Optimization Project

Goal: Substantially reduce CPU time and memory bandwidth of `PPU_CUBE` while preserving (or controllably degrading) its visual feature set (gradient backdrops + shadow projection) and timing‑critical side effects (NMI, IRQ timing, sprite 0 hit).

This document inventories current hotspots and proposes a prioritized roadmap of optimizations with estimated impact. Focus is on changes likely to yield real performance wins (especially on WebAssembly / Blazor) rather than micro‑premature tweaks.

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
## 3. Roadmap (Actionable Items)

### P0 (Do First)
1. Fuse Gradient With Background Render
   * Current: fill whole scanline then overwrite non‑transparent BG pixels.
   * Change: Remove `PreFillGradientLine` pass. During BG tile loop, when `colorIndex==0`, compute gradient color for that (y) only once (cache `gradR/gradG/gradB` per scanline) and write it. For pixels untouched by BG (outside 256 or unrendered areas), lazy fill only if needed.
   * Impact: Eliminate ~240 * 256 * 4 ≈ 245K channel writes per frame (nearly full extra framebuffer pass) => large bandwidth & cache win.
   * Complexity: Low.

2. Flatten Coverage Histories
   * Replace `bool[,] spriteCoverageHistory` & `bgCoverageHistory` with `byte[]` sized `ShadowVerticalDistance * ScreenWidth` and manual indexing: `rowOffset + x`.
   * Removes multidimensional array overhead & simplifies clears (use `Array.Clear` over row slice or generation counters).
   * Impact: Fewer bound checks & pointer chasing; ~5–10% of scanline time.

3. Sparse Shadow Application
   * Track active coverage columns in a compact list instead of scanning 256 wide each shadow pass.
   * For each coverage row maintain `int count` and an `int[MaxShadowPixelsPerLine] columns` populated during sprite/bg rendering (only when a pixel is first made opaque). Apply shadow only to those positions next scanline.
   * Impact: When coverage < ~128 columns (typical), halves shadow loop time; big gain in sparse scenes.

4. Hoist `EnsureFrameBuffer()`
   * Call once at frame start (scanline 0 cycle 0). Remove redundant calls inside render functions.
   * Impact: Branch + call removal (small but free) & improves inlining chances.

5. Precompute Gradient Table
   * Compute a 240‑entry gradient row color array (R,G,B) at frame start (or when universal background palette index changes). Use lookup in BG transparent case.
   * Impact: Avoid recomputing luma & interpolation per scanline. Saves ~240 * (math ops) per frame.

6. Eliminate Per‑Pixel Palette Index Arithmetic
   * Prebuild a 32-entry palette RGBA table (already have `paletteResolved`) but also build 8 background attribute quadrant tables of 4 resolved colors (mapping `(paletteIndex, colorIndex)` -> 3 bytes). Then inside pixel loop just `p = bgPaletteLUT[(paletteIndex<<2)|colorIndex]`.
   * Impact: Removes shifts / adds / masks in inner loop (~5–7% BG time).

7. Batch Step to Whole Scanlines
   * New `StepScanlines(int scanlines)` path: while elapsed cycles >= 341 run one scanline iteration (handling MMC3 IRQ at cycle 260 manually). Keep existing cycle granularity for partial lines.
   * Host code can convert CPU cycles (approx) to scanlines to reduce loop iterations from ~89K to 240 + remainder.
   * Impact: Large in WASM / interpreter (~10–20%).

8. Replace `Array.Clear(bgMask)` With Generation Stamp
   * Keep `ushort[] bgMaskGen` & a frame-global incrementing `ushort currentGen`; per scanline set `bgMaskGen[px]=currentGen` when opaque BG drawn; test by comparing generation value instead of bool. Reset only when `currentGen` overflows (rare) or frame start.
   * Impact: Avoid 256 zero writes * 240 (~61K writes) per frame.

9. Remove Palette Cache Flag Branch
   * Build palette once at init & on writes (already done in `Write()` via `UpdateResolvedPaletteEntry`). Remove `if (!paletteCacheBuilt)` checks inside render loops.
   * Impact: Minor but free.

### P1 (Next Wave)
10. Tile Row Decode Cache
    * Cache decoded 8‑pixel colorIndex rows for pattern+fineY: key `(patternAddr)` or `(tileIndex, fineY, tableBank)`. Invalidate on CHR bank switch or pattern memory write.
    * Store as `byte[8]` entries; BG loop then just copies or tests zeros. Could also pre-evaluate transparency bitmask as a byte.
    * Impact: Reduces `Read` calls & bit arithmetic by ~40–60% in scroll‑static scenes.

11. Attribute Prefetch per Tile Column
    * For each 2x2 tile group compute palette index once per scanline row; reuse for the two tiles horizontally encountered.
    * Impact: ~3–4% BG loop.

12. Sprite Scanline Candidate List
    * Pre-pass each scanline: evaluate which sprites overlap (like actual NES). Build small array (<= 64). Inner render loop iterates only candidates.
    * Impact: Large on lines with few sprites (common). CPU time drops proportional to average active sprites per line.

13. Bitset Coverage + Shadow Vectorization
    * Store coverage in `ulong[ShadowVerticalDistance * (ScreenWidth/64)]`. Iterate 64 bits at a time; if word nonzero apply darken only to set bits (bit scan loop). Potential for SIMD mask expansion.
    * Impact: Additional reduction vs sparse lists for dense rows or when using bit intrinsics.

14. Unsafe / Span Fast Paths
    * Introduce `unsafe` block for per-pixel loops to eliminate bounds checks & repeated index + null suppression.
    * Impact: 5–15% depending on JIT. (WASM may benefit less but still some gain.)

15. Merge Shadow Darken & Pixel Write
    * Instead of darkening previous coverage next scanline, darken immediately when coverage written (store pre-darkened color to shadow landing position with an offset ring buffer). Requires storing original color for potential subsequent overwrites (riskier) or performing a single delayed pass only on touched columns (P0 sparse approach already). This is an alternative path—evaluate trade-offs.
    * Impact: Potentially removes an extra pass; complexity moderate/high.

16. Adaptive Feature Level
    * Runtime toggle: if frame time > threshold disable shadows or reduce gradient to simple fill until time recovers.
    * Impact: Stability on low-power devices.

### P2 (Advanced / Experimental)
17. Multi-threaded Scanline Batching (Desktop Only)
    * Partition frame into N bands; background pass parallelized (since stateful scroll increments complicate correctness). Requires replicating/deriving per-band VRAM addressing from starting `v`. Complex and may break cycle-exact side effects; likely out of scope unless targeting high-refresh.
    * Impact: Potential 2–3× on multi-core, but high risk.

18. JIT-Time Code Generation / Source Generators
    * Emit specialized render loops based on feature flags (shadows on/off, sprite size, pattern table base) to remove branches.
    * Impact: Moderate; complexity high.

19. GPU Blit / WebGL Shader Path
    * Offload gradient + shadow compositing to WebGL/WebGPU fragment shader after uploading a simpler BG + sprite layer buffer.
    * Impact: Big on browsers; complexity & portability trade-offs.

20. Frame Difference (Dirty Rect) Streaming
    * Only redraw changed scanlines (if scroll static and no sprite changes). Maintain hash/CRC per scanline to detect unchanged lines.
    * Impact: Large for pause/static scenes.

21. Pattern Decode Ahead-of-Time (CHR ROM)
    * For CHR ROM (immutable), decode all tiles into 8x8 2bpp -> 8 bytes of packed colorIndex arrays once; BG render becomes simple array indexing.
    * Impact: BG render cost drops drastically for ROM-backed content. Additional memory (~8 bytes * tiles) acceptable.

22. Sprite Palette + Priority Precomputation
    * For each sprite row, construct an 8-element struct array with resolved RGBA & transparency mask before per-pixel loop.
    * Impact: Reduces per-pixel branching, moderate gain.

23. Branchless Darken
    * For shadow darken apply integer multiply via LUT: prebuild 256-entry table for 69% darken (value*69/100). Replaces 3 multiplies per darkened pixel with 3 table lookups (could be faster or slower; benchmark). Might integrate with palette pre-darkened variants.
    * Impact: Small; measure.

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
_Last updated: 2025-08-15_
