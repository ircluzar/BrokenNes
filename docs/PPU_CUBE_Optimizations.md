# PPU_CUBE Optimization Matrix

Status legend: [x] implemented / verified, [~] partially implemented, [ ] planned / not yet done.

## Core Rendering Pipeline
- [x] Gradient precompute & full-frame cache with per-scanline BlockCopy
- [x] Integer luma + integer interpolation (removed floating point in gradient build)
- [x] Background tile inner loop unrolled (bitmask extraction, fewer branches)
- [x] Removed per-pixel universal BG write on transparent background pixels (kept cached gradient)
- [x] Unsafe pointer loops for background & sprite pixel writes (reduced bounds checks)
- [x] Unsigned range checks `(uint)idx >= width` to collapse two comparisons
- [x] Inlined palette lookup (removed GetSpriteColor call)
- [x] Shift-based index math `(pixel << 2)` instead of multiply for RGBA access

## Palette & Color Handling
- [x] Palette RAM resolved RGB cache (32 entries * 3 bytes)
- [x] Automatic palette cache entry update on palette writes
- [x] Darken LUT replacing per-pixel float multiply in shadow blending
- [x] Corrected darken factor (kept original brightness intent)
- [ ] Unified 64 NES master color direct table for all palette lookups (minor gain)
- [ ] Palette change batching (defer resolved rebuild until end-of-frame)

## Sprite System
- [x] Sprite scanline index (per-scanline list of relevant sprites)
- [x] OAM dirty tracking to rebuild index only when needed
- [x] Sprite size mode tracking (8x8 vs 8x16) invalidates index
- [x] Early skip when no sprites on a scanline
- [x] Reuse sprite coverage history arrays (no per-frame allocs)
- [~] Emulation of secondary OAM behavior (current: all sprites per scanline; potential: cap to first 8 like hardware)
- [ ] Optional limit to first 8 sprites per line for even more speed (accuracy trade-off)

## Pattern / CHR Access
- [x] Pattern row cache (2 tables * 256 tiles * 8 rows) with packed planes
- [x] Generation-based invalidation for CHR writes
- [x] Public manual invalidation hook (InvalidatePatternCache)
- [ ] Mapper hook integration for bank switching to auto-invalidate pattern cache
- [ ] Tile usage statistics for prefetch / selective eviction
- [ ] Cross-frame persistent decode when CHR static (no bank switching)

## Shadows & Effects
- [x] Shadow effect guarded by runtime toggle (SetShadowsEnabled)
- [x] Darken LUT (byte->byte) eliminates float per pixel
- [ ] Shadow span batching (aggregate contiguous pixels before applying darken)
- [ ] SIMD accelerated darken (when Wasm SIMD + Vector<byte> supported) 
- [ ] Configurable shadow strength (table rebuild on change)

## Memory & Allocation
- [x] Eliminated per-scanline temporary allocations (reuse arrays)
- [x] Avoided boxing / delegate overhead in hot paths
- [ ] Convert bgMask to stackalloc Span<byte> (careful with lifetime) when rendering scanline
- [ ] Pool / reuse larger temporary buffers (if future features added)

## Branch & Instruction Reduction
- [x] Removed redundant alpha presence checks after gradient prefill
- [x] Consolidated priority & transparency checks order in sprite loop
- [x] Early continues for out-of-range / transparent before expensive work
- [ ] Branchless colorIndex extraction via lookup table (plane0, plane1, bit -> color) 
- [ ] Compute two pixels at a time using bit tricks (nibble parallelism)

## Frame-Level Optimizations
- [ ] Dirty frame detection (skip render if no VRAM/OAM/palette changes & no scroll change)
- [ ] Partial scanline redraw (track dirty tile spans)
- [ ] Adaptive cycle skipping when PPU output not queried (headless mode)

## SIMD / Advanced
- [ ] Vectorized gradient cache build (Vector<byte>/Vector<uint>)
- [ ] Vectorized shadow application
- [ ] WebAssembly multi-thread (future: off-main-thread rendering with SharedArrayBuffer)

## Diagnostics & Tuning
- [ ] Lightweight in-frame counters (cache hits/misses, sprites processed) behind DEBUG or FAST_EMU_STATS define
- [ ] Toggle for dumping hotspot metrics to console
- [ ] Visual overlay mode to display sprite overlap / shadow coverage

## API / Config
- [x] SetShadowsEnabled(bool) runtime toggle
- [ ] SetShadowStrength(float) with LUT regeneration
- [ ] Configuration object (startup) to enable/disable caches individually

## Safety / Correctness Notes
- Generation counter wrap handled (resets gens if overflow)
- Pattern cache invalidation mandatory on CHR bank switch (pending mapper integration)
- Sprite 0 hit logic preserved during refactors

## Potential Future Trade-Off Flags
- [ ] FAST_BG_ONLY (skip sprites entirely)
- [ ] FAST_NO_SHADOWS (already runtime toggle; could compile-time strip code) 
- [ ] FAST_LIMIT8SPRITES (cap to 8 like hardware or even fewer for speed)

## Summary
Current implementation integrates multiple zero-allocation caches (gradient, palette, pattern rows, sprite scanlines) and removes most high-frequency floating-point & branching overhead in hot loops. Remaining gains lie in conditional rendering skips, SIMD vectorization, and deeper tile/sprite decode batching.

---
Last updated: (auto-generated)
