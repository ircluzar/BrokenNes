# Hot Path JS ↔ WASM / CLR Boundary Optimization Project ("HotPot")

Date: 2025-08-19
Scope: BrokenNes (Blazor WASM NES emulator + SoundFont bridges)

---

## 1. Executive Summary
Profiling + code inspection show our worst perf multipliers stem from (a) high-frequency, small-payload interop (`IJSRuntime.Invoke*` / `[JSInvokable]`) inside the frame + audio + note event loops and (b) redundant marshalling (arrays copied each boundary). Largest wins will come from eliminating per-event calls and moving bulk data via shared memory or batched codecs. This document lists concrete improvements ranked by estimated payoff vs effort.

### 1.1 Progress Snapshot (Updated 2025-08-19)
Implemented / prototyped since initial draft:
* ✅ HOTPOT-02: Coalesced `presentFrame` unified JS call (see `Nes.razor` & `nesInterop.presentFrame`).
* 🚧 HOTPOT-04: AudioWorklet + SharedArrayBuffer ring prototype auto-inits on first audio buffer (fallback preserved).
* 🔄 HOTPOT-05: Low-copy / pull framebuffer path (WebGL1-compatible; redesign underway – minimize interop & copy without WebGL2-only features).
* 🚧 HOTPOT-07: Partial async/state machine reduction (coalesced present reduces awaits; full ValueTask refactor pending).
* 🔍 Instrumentation: Basic audio diagnostics (`audioDiag`) & zero-copy enable logs present.

Not yet started / pending major work:
* ⭕ HOTPOT-01 Packed input bitmask (`UpdateInputPacked`).
* ⭕ HOTPOT-03 Note event batching (still per-event invokes).
* ⭕ HOTPOT-X1 Comprehensive interop call/byte counter decorator service.
* Finalization tasks for HOTPOT-04/05 (make new paths default + retire legacy paths where safe).

### 1.2 Updated Top 5 Remaining High-Leverage Improvements
1. HOTPOT-03 Note event batching (or shared memory polling) – remove bursty micro invokes.
2. HOTPOT-01 Packed input bitmask (and optionally JS polling) – shrink input marshaling & spikes.
3. HOTPOT-04 Complete SAB ring integration on .NET side; eliminate legacy per-frame audio buffer on supported browsers.
4. HOTPOT-05 Reintroduce (WebGL1) low-copy framebuffer pull path with progressive bandwidth optimizations.
5. HOTPOT-X1 Metrics + automated bench harness (X1 + X2) – enables safe iteration & regression gating.

Runners-up: Finish HOTPOT-07 ValueTask refactor; consider HOTPOT-06 loop inversion only if inbound invoke remains material after above; HOTPOT-08 unmarshalled primitives if still justified post-batching.

---

## 2. Current Hot Boundaries (Simplified Map)

| Path | Direction | Frequency | Payload | Mechanism | Notes |
|------|-----------|-----------|---------|-----------|-------|
| `requestAnimationFrame` → `[JSInvokable] FrameTick()` | JS→.NET | ~60 Hz | none | DotNetObjectRef invoke | Schedules whole emu frame.
| `RunFrame()` → `JS.InvokeVoidAsync("nesInterop.playAudio", float[], rate)` | .NET→JS | 60 Hz (variable) | Float32 samples (768–2048) | Marshalled array | Copies full audio buffer every frame.
| `RunFrame()` → `JS.InvokeVoidAsync("nesInterop.drawFrame", framebuffer)` | .NET→JS | 60 Hz | Full framebuffer (pixel array) | Marshalled array | Likely largest single copy per frame.
| APU note events (`noteCallback` → `nesInterop.noteEvent`) | .NET→JS | Potentially dozens–hundreds / sec (burst) | 5 small primitives | Individual async invokes | Each call alloc alloc + dispatch.
| Input updates (`initTouchController` touch moves → `[JSInvokable] UpdateInput(bool[8])`) | JS→.NET | Variable (user) but can spike | bool[8] array | Marshalled array | Could pack into 1 byte.
| Shader registration loop (startup) | .NET→JS | One-time | Many strings | OK (init only) | Not hot.
| IndexedDB get/set preferences | Both | Sporadic | Small string | Fine (not in tight loop).
| Save state chunk ops (`getStateChunk` etc.) | .NET→JS | User triggered | Strings (base64) | OK unless very large states; not per frame.

Other hidden CLR boundaries: delegate invocation for note events (APU cores) and dynamic core switching using reflection; minor compared to interop but subject to micro-optimizations if still on hot path.

---

## 3. Ranked Optimization Opportunities (Highest Estimated Gain First)

### Tier A – High Impact / Strategic
1. Replace per-frame `float[]` audio marshaling with SharedArrayBuffer + AudioWorklet (pull model) – Prototype active; finalize managed writer + feature flag / fallback.
   - Estimated gain: 8–20% total frame time reduction & lower GC churn; smoother audio under load.
   - Rationale: Copying ~1–8 KB 60 times/second + scheduling JS each frame induces GC + blocking; AudioWorklet can read directly from a ring buffer in WASM memory (or a SharedArrayBuffer we fill via `Unsafe` span copy). Frees main thread and reduces latency jitter.
2. Low-Copy Framebuffer Pull Path (WebGL1) – New redesign: eliminate per-frame marshalled array; JS polls WASM memory view & uploads with texSubImage2D.
   - Estimated gain: 8–15% (depends on resolution and current copy cost). 
   - Strategy: Expose framebuffer as pinned `Span<byte>`; JS obtains a `Uint8Array` view into the WASM linear memory (via `Blazor.platform._memory` in legacy, or upcoming `dotnet.runtime.memory` APIs) and calls `texSubImage2D` with a typed array view; .NET stops sending the array parameter—only signals dirty/ready (or JS drives the loop and reads directly each RAF).
3. Batch Note Events (SoundFont) into a per-frame ring buffer (single interop call) – Not started.
   - Estimated gain: 5–12% (workload-dependent; spikes smoothed). 
   - Approach: Accumulate events in a preallocated struct array or unmanaged ring; at frame end (or audio tick), send count & maybe pointer. JS drains synchronously. Eliminates many small allocations + await overhead.
4. Invert Frame Loop Ownership (JS polls, .NET stays in compute loop) OR Coalesce Frame JS Calls – Coalesced call DONE; loop inversion TBD (may defer pending new profiling).
   - Estimated gain: 4–8%. 
   - Option A: Keep `requestAnimationFrame` in JS but collapse `playAudio` + `drawFrame` + any metrics into one invoke (one boundary crossing vs two). Option B: Run continuous .NET loop (timed) and only call a single JS `present(framePtr,audioPtr,len,...)` method.

### Tier B – Medium Impact
5. Unmarshalled Interop for Small Primitives (note events if not batching yet; packed input bitmask) – Defer until batching + packed input implemented; may be unnecessary.
   - Gain: 2–5%. Use `IJSUnmarshalledRuntime` / .NET 8 `JSMarshaler` to avoid JSON/serializer overhead for primitive bursts while interim migrating to ring buffer.
6. Pack Controller State into a Single Byte – Not started.
   - Gain: ~1–2% (micro) but reduces GC & simplifies `[JSInvokable] UpdateInput` path. Accepts `byte` bitmask instead of `bool[8]` (eight marshalled booleans currently). Combine with unmarshalled call or polling.
7. Reduce Async State Machine Allocations in Hot Path – Partial; convert remaining frame loop methods to `ValueTask`.
   - Gain: 1–3%. Convert `FrameTick` to `ValueTask`, remove `async`/`await` in `RunFrame` where possible (synchronous path except when blasting). Fire-and-forget JS calls already used; ensure they are not capturing unnecessary state.
8. Pre-sized / Reused Buffers (Audio & Frame) with Manual Length Semantics – Ongoing verification (ensure no hidden per-frame allocations remain).
   - Gain: 1–2%. Avoid new arrays if emulator already writes into stable buffers; ensure we do not allocate per frame (confirm via profiling). If currently producing a new `float[]`/`int[]` each call, reuse.

### Tier C – Lower Impact / Polish
9. Deferred / Batched Preference Writes (debounce idbSetItem) – quality of life; avoids extra micro stalls.
10. Collapse multiple shader registration invocations into a single JS bulk registration call (init only; reduces cold start). 
11. Replace `try{ JS.InvokeVoidAsync("eval", ...) }` with pre-bound function references (JIT/warm-up improvement; minor but cleaner security posture).
12. Remove double enumeration / LINQ overhead inside frame-critical sections (e.g., repeated `.Any()` in loops) – negligible but cumulative.

---

## 4. Implementation Sketches

### 4.1 Shared Audio Ring (AudioWorklet)
1. Allocate SharedArrayBuffer (e.g., capacity for 4 × target chunk). Expose its ID/address to .NET once.
2. .NET writes PCM samples sequentially (interleaved or mono) and advances a write index (shared Int32 in a control SAB).
3. AudioWorkletProcessor pulls & schedules with low jitter, reading indices atomically.
4. Remove per-frame `playAudio` invoke; JS issues zero interop during steady-state.

### 4.2 Framebuffer Zero-Copy
Option (WebGL2):
```
Startup: JS obtains Uint8Array view of WASM memory at framebuffer address (provided via a one-time interop call). Each RAF: JS calls `gl.texSubImage2D` directly from that view. Only invalidation signal required (could poll a frame counter stored in a shared Int32).
```

### 4.3 Note Event Batching
```
struct NoteEvent { byte Channel; byte Program; byte Midi; byte Velocity; byte Flags; } // pack On bit into Flags
Preallocate NoteEvent[MaxPerFrame]. On event: write, increment count. End of frame: single JS call passing (pointer, count) OR JS polls count & copies into its own queue.
```

### 4.4 Packed Input
```
JS packs directional + buttons into one byte (bit per button) and calls `UpdateInputPacked(byte state)` OR writes shared Int32 and triggers nothing; .NET reads before emulation step.
```

### 4.5 Coalesced Present Call
```
present(framePtr, width,height, audioPtr, sampleCount, metaFlags)
```
JS then performs both texture upload + audio scheduling.

### 4.6 Remove Await in Frame Loop
`FrameTick()` returns `ValueTask` and internally calls synchronous `RunFrame()`; corruption (`Blast()`) can queue tasks but not await inside hot loop; UI updates throttled on a timer.

---

## 5. Estimation Rationale
Empirical data from similar Blazor WASM emulators indicates:
- Full-frame marshalled buffer copies (video+audio) can dominate ≥25–35% CPU in medium-performance devices.
- Cutting array marshaling to zero-copy typically halves that portion.
- Batching dozens of tiny invokes (note events) into one reduces overhead due to JS interop dispatch (~10–30 µs each) + allocations (args array, task wrapper).
- Input packing saves small but recurring cost, helps GC locality.

---

## 6. Quick Wins (Do First)
1. Pack controller input to byte; add new `[JSInvokable] UpdateInputPacked(byte s)`; keep legacy for fallback.
2. Batch note events per frame (simple managed `List<NoteEvent>` + single interop call) before pursuing SAB.
3. Coalesce audio + video interop into single `presentFrame(frameBuf,audioBuf,sampleRate)` to immediately reduce calls from 2→1 per frame. (DONE)
4. Convert `FrameTick` to `ValueTask` & remove unnecessary awaits.
5. Debounce shader & preference JS calls (non-critical but trivial).

---

## 7. Medium-Term
1. Migrate audio to AudioWorklet + SharedArrayBuffer ring.
2. Implement zero-copy framebuffer (WebGL2 view) behind feature flag.
3. Replace batched note event invoke with shared memory polling (JS side scheduler reads events at audio tick cadence).
4. Introduce a single shared "interop command block" (struct with flags & offsets) polled by JS each RAF to eliminate inbound `[JSInvokable] FrameTick` (JS drives; .NET sets ready flag).

---

## 8. Long-Term / Stretch
1. Investigate WebGPU path (if available) for direct texture updates; may further reduce copy overhead vs Canvas.
2. Explore ahead-of-time (AOT) compile for critical cores (CPU/APU) to reduce CLR overhead; ensures optimization headroom once interop minimized.
3. Evaluate converting sound font synthesis fully to WASM (C# or Rust) to avoid note event JS path entirely (only final audio ring crosses boundary).
4. Consider moving persistence (IndexedDB operations) to a minimal worker with message batching to offload main thread (only after hot paths solved).

---

## 9. Measurement & Validation Plan
Metrics to collect before/after each phase:
- Frame time breakdown (Chrome Performance): % time in `dotnet.*` vs `nesInterop.*` vs GC.
- Interop call count per second (instrument wrappers).
- Audio glitch incidence (underruns) - expected to drop after SAB migration.
- Memory allocations per frame (Event listener, note events) via `dotnet counters` (if using WASM debug) or custom counters.

Instrumentation tasks:
1. Wrap `IJSRuntime.Invoke*` with counting decorator (DI) to log frequency.
2. Add frame counter & timestamp in shared memory for JS to compute latency.
3. Add optional env flag `?diagInterop=1` to enable verbose logging once per N frames.

Success criteria for Tier A:
- ≥15% reduction total CPU time on mid-tier device @ 60 FPS test ROM.
- No increase in dropped audio frames; ideally fewer.
- GC allocations per second reduced by ≥30%.

---

## 10. Risks & Mitigations
| Risk | Impact | Mitigation |
|------|--------|------------|
| SharedArrayBuffer requires COOP/COEP headers | SAB blocked | Already likely set for PWA; confirm headers in `index.html`/hosting config. Add fallback to legacy path. |
| AudioWorklet complexity / browser support | Delays | Progressive enhancement: keep existing path until worklet stable. |
| Race conditions when JS polls buffer | Visual/audio artifacts | Use atomic counters (Int32) & double-buffering strategy. |
| Increased code complexity | Maintainability | Centralize interop abstractions (e.g., `InteropChannels` class). |
| AOT size increase | Load time | Offer toggle build config; gate behind perf mode. |

---

## 11. Actionable Work Breakdown (Task Board)
Legend: Priority (H=High, M=Medium, L=Low). Each task has granular subtasks with checkboxes. Add status (e.g. ✅, 🚧) inline as work proceeds.

### Theme A: Boundary Reduction (Core Hot Paths)

#### HOTPOT-01 (M) Packed Input Bitmask
- [ ] Design
   - [ ] Define bit layout (e.g. Up=0, Down=1, Left=2, Right=3, A=4, B=5, Select=6, Start=7) (match existing map)
   - [ ] Confirm no future buttons need >8 bits; otherwise reserve 1 nibble for expansion
- [ ] .NET Changes
   - [ ] Add `[JSInvokable] public void UpdateInputPacked(byte state)` alongside existing `UpdateInput`
   - [ ] Refactor internal `inputState` fill using bit test vs array assignment
   - [ ] (Optional) Deprecation warning path for legacy bool[8]
- [ ] JS Changes
   - [ ] In `initTouchController`, build bitmask instead of bool array in `setState`
   - [ ] Call `invokeMethodAsync('UpdateInputPacked', mask)` (fallback if error → old method)
- [ ] Testing
   - [ ] Unit test: Bitmask translation correctness (simulate all buttons)
   - [ ] Manual: Rapid multi-touch to ensure no missed transitions
- [ ] Metrics
   - [ ] Record interop count before/after (should be identical but payload smaller)

#### HOTPOT-02 (M→H quick) Coalesced Frame Present Call ✅ (Implemented)
- [ ] Design
   - [ ] Decide API signature `presentFrame(frameBuffer, audioBuffer, sampleRate, meta)` OR pointer-based (phase 2)
   - [ ] Identify metadata bits (e.g. flags: FastForward, CorruptApplied)
- [ ] .NET Changes
   - [ ] Replace two invokes (`playAudio`, `drawFrame`) with one `presentFrame`
   - [ ] Ensure fire-and-forget still safe (exception shielding)
   - [ ] Remove redundant queue logic if moved to JS
- [ ] JS Changes
   - [ ] Implement `nesInterop.presentFrame` calling existing `playAudioInternal` + `drawFrameInternal`
   - [ ] Preserve timing logic order (audio scheduling before draw)
- [ ] Testing
   - [ ] Visual: Frame integrity vs baseline screenshots
   - [ ] Audio: Continuous playback (no pops) across 5 min run
- [ ] Metrics
   - [ ] Interop calls/frame reduced from 2→1

#### HOTPOT-03 (H) Note Event Batching (Phase 1: Marshalled List) ⭕
- [ ] Design
   - [ ] Define struct `NoteEv { byte Ch; byte Prog; byte Midi; byte Vel; byte Flags; }`
   - [ ] Determine max events/frame (e.g. 512) & fallback if overflow (drop vs flush early)
- [ ] .NET Changes
   - [ ] Add per-frame buffer (array + index) zeroed at frame start
   - [ ] Replace direct `JS.InvokeVoidAsync('noteEvent', …)` with buffer append
   - [ ] At end of `RunFrame()` call `JS.InvokeVoidAsync('nesInterop.dispatchNoteEvents', batchArray, count)`
- [ ] JS Changes
   - [ ] Implement `dispatchNoteEvents` loop translating packed flags to existing synth handlers
   - [ ] Reuse current per-event logic; later optimize
- [ ] Overflow Handling
   - [ ] Count drops; expose counter in debug report
- [ ] Testing
   - [ ] Synthetic stress (generate >400 note events/frame) ensure no crash
   - [ ] Compare audible result vs legacy path (diff counters)
- [ ] Metrics
   - [ ] Interop note event calls: N → 1
   - [ ] GC alloc/frame reduction (record)

#### HOTPOT-04 (H) Audio SharedArrayBuffer + AudioWorklet (Phase 1 Prototype) 🚧
- [ ] Pre-Req
   - [ ] Confirm COOP/COEP headers present (SAB allowed)
   - [ ] Add feature flag `?featureAudioSAB=1`
- [ ] JS Infra
   - [ ] Create `audio-worklet.js` + processor registering `process(inputs, outputs)` reading ring buffer
   - [ ] Initialize SharedArrayBuffer (e.g. Float32 size: sampleRate * 0.25s)
   - [ ] Control block SAB: Int32[3] => writeIndex, readIndex, frameCounter
- [ ] .NET Changes
   - [ ] On init, request SAB id via interop; map using `Span<float>` with `Unsafe` or fix handle
   - [ ] Replace `presentFrame` audio buffer send with ring write (advance writeIndex atomically)
- [ ] Synchronization
   - [ ] Implement wrap-around copy for chunk > remaining capacity
   - [ ] Provide backpressure metric (bytesAhead)
- [ ] Fallback
   - [ ] If SAB unsupported or flag absent → legacy path
- [ ] Testing
   - [ ] Latency measurement (lead ms) vs legacy
   - [ ] Underrun counter in processor (log)
- [ ] Metrics
   - [ ] CPU usage diff (Chrome Performance sampling)
   - [ ] Interop audio calls removed (0/frame)

#### HOTPOT-05 (H) Low-Copy Framebuffer (WebGL1-Compatible Pull Path) 🔄
Goal: Remove large per-frame marshalled byte[] transfer while remaining 100% WebGL1 compatible. Achieve near zero JS interop payload, minimize CPU copy & enable optional bandwidth reductions (palette / dirty scanlines) incrementally.

Principles:
1. Progressive enhancement: baseline (Phase 1) lands quick win with minimal risk; later phases gated by profiling.
2. Always maintain a trivial fallback (legacy marshalled path) behind a feature flag / runtime heuristic.
3. Avoid WebGL2 / PBO dependencies; rely only on tex(Sub)Image2D + Uint8Array views.
4. Keep architecture modular so palette or tile optimizations can be bolted on without touching core emulator logic.

Data Model:
| Name | Purpose | Location |
|------|---------|----------|
| Framebuffer | RGBA8888 (256x240) | Fixed unmanaged / pinned byte[] (stable address) |
| Control Block | 32 bytes: frameCounter (Int32), status (byte), fbGeneration (Int32), optional dirtyRowCount, feature flags | Adjacent to framebuffer or separate static |
| Optional Dirty Bitset | 240 bits (~30 bytes) marking modified scanlines | After control block |

Phases:
Phase 0 (Current): Legacy marshalled array each frame via presentFrame().
Phase 1 (Low-Copy Push): Keep presentFrame() invoke but drop framebuffer array parameter. Instead pass only counter + audio (temporary). JS holds a persistent Uint8Array(memory.buffer, fbAddr, len) and always uploads that memory on signal. (Removes large array marshal; 1 invoke remains.)
Phase 2 (Pure Pull): Remove per-frame invoke entirely. JS rAF loop polls frameCounter (atomic int) from control block. When changed -> texSubImage2D upload + draw. Audio path already coalesced separately / or integrated if SAB ready.
Phase 3 (Dirty Rows): Populate dirty bitset per scanline during PPU writes. JS groups consecutive dirty spans; if dirty coverage < threshold (e.g., 60%) perform batched texSubImage2D sub-rectangle uploads (row runs). Else full frame upload.
Phase 4 (Palette Mode Optional): Switch internal storage to 8-bit index + 256xRGBA palette (uploaded rarely). JS shader expands color. Upload cost drops from 256*240*4=245,760 bytes to 61,440 bytes/frame worst-case. Guard behind capability + perf flag.
Phase 5 (Tile Delta Optional): Hash 8x8 tiles; upload only changed tiles into an atlas texture. Extra CPU hashing; only enable if profiling on low-bandwidth devices shows bottleneck after earlier phases.

Synchronization Strategy:
Write ordering: Emulator writes full frame, then increments frameCounter (volatile write) then sets status=1 (ready). JS: reads frameCounter; if new and status=1 -> upload; optionally sets status=0 post-upload (or rely solely on counter).
Tearing avoidance: Optionally use double buffering (two framebuffers + active index) if partial updates ever observable; likely unnecessary with sequential write then counter store.
WASM Memory Growth Handling: JS every rAF compares cachedBuffer !== runtime.getMemory(); if changed, recreate view. If fbGeneration changed (emulator reallocated), request fresh metadata via single interop call getFbMeta().

API Surface (initial minimal):
`getFbMeta()` -> { addr, length, width, height, controlAddr, flags }
Optional: `enableLowCopyFb(mode)` to toggle fallback.

Heuristics / Auto Fallback:
If average upload time (moving window 120 frames) > 4ms OR WebGL context lost twice in <30s → revert to legacy for session.
If memory growth occurs >3 times in 5 minutes while low-copy active → log & fallback (indicates instability / fragmentation).

Dirty Row Implementation Notes (Phase 3):
PPU marks dirty row i when first pixel of row differs from previous frame OR any sprite/background write touches that row. Bitset OR operations only; cleared at frame start.
JS groups runs: For each contiguous run R: gl.texSubImage2D with height=runLength starting at y=startRow.
Threshold: If (uploadedRows * width) >= 0.6 * totalPixels OR numberOfRuns > 40 → fallback to single full-frame upload that frame.

Palette Mode (Phase 4) Details:
Internal PPU continues producing RGBA; conversion pass builds index buffer & palette while computing a 256-entry usage histogram. If unique colors > 256 OR conversion cost >1.2ms (guard) revert to RGBA for that frame. Cache palette CRC to avoid re-upload when unchanged.
Shader fragment snippet:
```
uniform sampler2D uIndexTex; // LUMINANCE or ALPHA based, width=256,height=240
uniform sampler2D uPalTex;   // 256x1 RGBA
vec4 fetchColor(vec2 uv){
   float idx = texture2D(uIndexTex, uv).r * 255.0;
   float x = (idx + 0.5) / 256.0;
   return texture2D(uPalTex, vec2(x, 0.5));
}
```

Metrics to Capture per Phase:
| Metric | Target Improvement |
|--------|--------------------|
| JS interop bytes/frame | -100% vs legacy (after Phase 1) |
| CPU time copying framebuffer (.NET) | Near zero (single in-place write) |
| Upload time (gl.tex(Sub)Image2D) | <2.0 ms on mid device (full) / <1.0 ms with palette or dirty rows |
| GC alloc/frame (video path) | Drop large byte[] alloc (should already be amortized) |
| Frame time stddev | No regression >5% |

Testing Plan:
1. Pixel Equivalence: Capture 10 sequential frames both paths (legacy vs low-copy) → byte diff tool (expected identical).
2. Long Soak: 10 min autoplay; record max diff in frame interval vs baseline.
3. Memory Growth: Force a WASM memory growth (load large ROM) mid-run; ensure view rebind correctly.
4. Dirty Row Efficacy: Simulated test ROM writing only HUD region; confirm <20% rows uploaded.
5. Palette Mode Guard: Test ROM with >256 colors (intentional corruption) triggers automatic fallback.

Task Breakdown:
- [ ] Phase 1 Implementation (push variant)  
   - [ ] Expose GetFrameBufferAddress()+length+control block pointer  
   - [ ] Add frameCounter increment in RunFrame() after rendering  
   - [ ] JS: getFbMeta(), create persistent view, modify presentFrame to ignore framebuffer arg when low-copy active  
   - [ ] Feature flag (?lcfb=1)  
- [ ] Phase 2 Pure Pull  
   - [ ] Remove per-frame invoke (JS rAF polls counter)  
   - [ ] Add fallback timer (if counter not advanced >500ms while running → re-request interop path for diagnostics)  
- [ ] Phase 3 Dirty Rows  
   - [ ] Add dirty bitset + instrumentation (#dirtyRows/frame)  
   - [ ] JS sub-run uploader + heuristic threshold  
- [ ] Phase 4 Palette Mode (optional)  
   - [ ] Color quantization & palette build (fast path early exit)  
   - [ ] Dual-texture shader path & registration  
   - [ ] Auto-enable only if upload time > target AND unique colors <=256 median last 120 frames  
- [ ] Phase 5 Tile Delta (stretch)  
   - [ ] 8x8 tile hashing + changed tile atlas uploads  
   - [ ] Compare CPU hashing cost vs bandwidth saving  
- [ ] Metrics & Telemetry  
   - [ ] Record upload bytes/frame, upload ms, dirty coverage %, fallback reasons  
   - [ ] Expose in debug overlay  
- [ ] Documentation  
   - [ ] Update README & hotpot doc after Phase 2 stable  

Risks & Mitigations:
| Risk | Impact | Mitigation |
|------|--------|-----------|
| WASM memory growth invalidates buffer | Black frame / stale view | Buffer identity check + auto rebind; generation counter validation |
| Dirty row overhead > savings | Wasted CPU | Heuristic threshold + instrumentation gating; auto disable if no win |
| Palette quantization jitter | Color flicker | CRC stable palette; require color set stability window before enabling |
| Extra complexity | Maintenance | Strict phase gates; keep legacy path for rapid disable |
| Mobile GPU anomalies (subimage perf) | Slower than full upload | Detect average subimage cost > full; fallback that frame |

Exit Criteria (declare HOTPOT-05 complete): Phase 2 stable + measured ≥8% total frame time reduction OR GPU upload <1.5 ms @ 60 FPS on reference mid device with no visual regressions.

#### HOTPOT-06 (M) Frame Loop Ownership Inversion (JS Polling)
- [ ] Design
   - [ ] Evaluate whether removing `[JSInvokable] FrameTick` saves measurable overhead after above steps
   - [ ] Provide `StepFrameIfReady()` JS function calling into .NET or .NET continuous loop with shared flag
- [ ] Implementation (Option A: JS Poll)
   - [ ] JS RAF: if `nextFrameDue <= performance.now()` then invoke `.NET StepFrame`
   - [ ] .NET StepFrame: synchronous run, update `nextFrameDue = now + (1000/60)` stored in shared Int32
- [ ] Implementation (Option B: Continuous .NET)
   - [ ] Spawn dedicated loop `while(running){ RunFrame(); spin/sleep }` using `Task.Yield` throttle
- [ ] Testing
   - [ ] Jank measurement (stddev frame interval) vs baseline
   - [ ] CPU idle % when tab backgrounded (no runaway loop)

#### HOTPOT-07 (M) Remove Async State Machines in Hot Loop 🚧
- [ ] Convert `FrameTick` to `ValueTask`
- [ ] Change `RunFrame()` to non-async; move corruption async features off critical path (queue to background if needed)
- [ ] Replace `await` with synchronous where no I/O
- [ ] Benchmark allocations/frame before & after

### Theme B: Efficiency & Cleanups

#### HOTPOT-08 (M) Unmarshalled Interop for Packed Primitives (Interim) (Deferred until after HOTPOT-03/01 to reassess necessity)
- [ ] Assess feasibility in current .NET version (IJSUnmarshalledRuntime still supported?)
- [ ] Implement specialized path for `UpdateInputPacked` (if not replaced by polling)
- [ ] Potentially for batched note events (phase 1 -> JSON, phase 2 -> unmarshalled)
- [ ] Measure delta; if <1% gain, consider dropping to reduce complexity

#### HOTPOT-09 (L) Debounce Preference Writes
- [ ] Implement client-side debounce (300–500ms) for idbSetItem calls
- [ ] Batch multiple writes into one `setPrefs({...})`
- [ ] Confirm persistence correctness (navigate away immediately after change)

#### HOTPOT-10 (L) Bulk Shader Registration
- [ ] Gather all shader definitions into single JS call `registerShaders(list)`
- [ ] Remove looped invokes
- [ ] Measure cold start improvement (log init ms)

#### HOTPOT-11 (L) Remove `eval` Calls
- [ ] Replace `JS.InvokeVoidAsync('eval', '...')` with dedicated interop API methods
- [ ] Pre-bind references: `nesInterop.enableSampleMode()` etc.
- [ ] Security: Confirm CSP compatibility improvement

#### HOTPOT-12 (L) Reflection / LINQ Micro-Optimizations
- [ ] Cache core ID lookups once on init
- [ ] Replace repeated `.Any()` inside loops with boolean fields updated incrementally
- [ ] Validate no regression in dynamic core switching

### Theme C: Advanced / Stretch

#### HOTPOT-13 (Stretch) Shared Command Block (Interop Channels)
- [ ] Design struct layout (cache-line aligned 64 bytes)
- [ ] JS polls commands (frame ready, new prefs, shader change) instead of multiple invokes
- [ ] Remove numerous one-off invokes (idb optional)

#### HOTPOT-14 (Stretch) Full SoundFont in WASM
- [ ] Evaluate porting JS synth to C# or Rust (feasibility / licensing)
- [ ] Prototype minimal note-on/off in WASM toggling samples
- [ ] Compare latency & CPU vs JS implementation

#### HOTPOT-15 (Stretch) AOT Build Optimization Pass
- [ ] Produce AOT build variant
- [ ] Profile size vs perf gain (CPU frame time)
- [ ] Decide gating flag / distribution plan

### Cross-Cutting Tasks

#### HOTPOT-X1 Metrics & Instrumentation Foundation
- [ ] Wrapper service around `IJSRuntime` counting calls & bytes (DI)
- [ ] Periodic flush to debug overlay (opt-in flag)
- [ ] Expose counters: interopCalls/s, noteEventsBatched/frame, droppedNoteEvents, audioLeadMs

#### HOTPOT-X2 Automated Bench Harness
- [ ] Add deterministic test ROM playback script (fixed seed) for reproducible profiling
- [ ] CLI mode capturing frame times & writing JSON report
- [ ] CI job threshold (fail if regression >10%)

#### HOTPOT-X3 Documentation & Developer Guide Updates
- [ ] Update README: performance modes & feature flags
- [ ] Document fallback paths & how to disable SAB if issues
- [ ] Add troubleshooting (headers required for SAB)

---

## 12. Execution Order Recommendation (Phase Plan)
Phase 0 (Instrumentation): HOTPOT-X1, baseline metrics.
Phase 1 (Quick Wins): HOTPOT-01, HOTPOT-02 (DONE), HOTPOT-03.
Phase 2 (Strategic Buffers): HOTPOT-04, HOTPOT-05.
Phase 3 (Loop & Async Cleanup): HOTPOT-07, HOTPOT-06 (decide necessity after gains), HOTPOT-08 (if still beneficial).
Phase 4 (Polish): HOTPOT-09, HOTPOT-10, HOTPOT-11, HOTPOT-12.
Phase 5 (Stretch / Future): HOTPOT-13 → HOTPOT-15, plus X2, X3 continuous.

---

## 13. Summary Ranking (Condensed)
1. SharedArrayBuffer AudioWorklet (High)
2. Zero-copy Framebuffer (High)
3. Batched Note Events (High)
4. Coalesced presentFrame (Medium→High, quick)
5. Unmarshalled + Packed Input (Medium)
6. Remove async allocations in frame path (Medium)
7. Reflection/core selection micro-optimizations (Low)
8. Debounced preference writes / bulk shader registration (Low)

---

## 14. Appendix – Implementation Hints
* Unmarshalled interop (legacy): cast to `IJSUnmarshalledRuntime` (only in WASM) for primitive spans; ensure fallback path on server prerender.
* Shared memory access: In modern .NET WASM, use `globalThis.getDotnetRuntime().getMemory()` (experimental) or maintain a small JS helper to locate the ArrayBuffer.
* Ensure GC safety: Pin managed buffers only while referencing from JS or allocate via `GCHandle.Alloc(..., GCHandleType.Pinned)`; unpin on dispose.
* Batch boundaries early—even if zero-copy not ready—so later migration only swaps transport mechanism.

---

Prepared for: Performance tuning initiative to push frame stability & headroom for future features (e.g., advanced shaders, higher audio polyphony).

---

### Appendix: AudioWorklet Test Procedure (HotPot Early Validation)
1. Open app with dev tools console.
2. Start emulation; wait 2–3 seconds for ring to fill.
3. Run `nesInterop.audioDiag()` – expect `{ enabled:true, failed:false, underruns:0 }` (underruns may spike briefly at start then stabilize).
4. Capture baseline (legacy path) by forcing fallback: temporarily set `window.nesInterop._awFailed=true` and refresh; compare CPU usage in Performance panel over 10s.
5. Measure:
   - Interop calls marked `playAudio` should disappear (search in performance flame graph).
   - GC pressure: fewer array allocations per frame.
6. Stress: enable fast-forward and ensure underruns remain near zero (acceptable occasional increment <1 per second). If sustained underruns, consider increasing capacity.
