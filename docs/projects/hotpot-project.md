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
* ❌ HOTPOT-05: Zero-copy framebuffer path (prototype removed / rolled back; legacy marshalled framebuffer restored as sole path).
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
4. (Removed) HOTPOT-05 Zero-copy framebuffer promotion deferred indefinitely; feature rolled back.
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
2. Zero/Low-Copy Framebuffer Transfer – Rolled back (prototype removed). Revisit only if future perf profiling justifies reintroduction.
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

#### HOTPOT-05 (H) Zero-Copy Framebuffer (WebGL2) – Phase 1 Read View 🚧
- [ ] Exploration
   - [ ] Determine current framebuffer pixel format (RGBA8888?) size constant
   - [ ] Expose pointer address via new .NET method `GetFrameBufferAddress()`
- [ ] JS Changes
   - [ ] Acquire WASM memory buffer reference (runtime API)
   - [ ] Create `Uint8Array(memory.buffer, addr, len)` each frame (or reuse singleton view if memory stable)
   - [ ] Use `texSubImage2D` with the view
- [ ] .NET Changes
   - [ ] Stop passing framebuffer array; only compute & mark dirty (increment `frameCounter` Int32)
- [ ] Sync Model
   - [ ] JS polls `frameCounter` each RAF; if changed → upload
- [ ] Fallback Path
   - [ ] Keep marshalled path behind flag toggle
- [ ] Testing
   - [ ] Visual parity pixel diff (capture canvas vs marshalled path for 10 frames)
   - [ ] Confirm no memory corruption after 5 min run
   - [ ] Mobile device test (if WebGL2 available)
- [ ] Metrics
   - [ ] Interop payload size/frame → near zero
   - [ ] CPU copy time reduced

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
