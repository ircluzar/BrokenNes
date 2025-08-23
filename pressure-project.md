# Frontend Pressure Project

Goal: reduce main-thread pressure, smooth 60 FPS video and glitch-free audio on mid-range mobile, by shrinking JS/WASM interop overhead, optimizing frame/audio delivery, and dialing shader/UI costs.

Success criteria
- [ ] 60 FPS sustained on Pixel 6 / iPhone 12 for SMB1, shaders: "Default (light)".
- [ ] No audio underruns during 3-minute play sessions (buffer XRuns < 1/minute).
- [ ] Main-thread long tasks (>50ms) reduced by 80% vs current baseline.
- [ ] Battery/CPU usage visibly lower (Chrome perf panel: main thread < 70% avg while running).

Notes
- Video sink canvas id: `nes-canvas`. Primary per-frame interop: `nesInterop.presentFrame(canvasId, framebuffer, audioBuffer?, sampleRate)`.
- Other calls exist for preview/bench (`nesInterop.drawFrame`), IDB, input, shaders, and audio control.
- WebAudio uses an AudioWorklet and message passing; SoundFont note events cross the JS/.NET boundary.

---

## Prioritized work items

### Hot loop optimizations spotlight (from `wwwroot/nesInterop.js`)
- Two interop crossings per frame right now: JS RAF calls .NET `FrameTick` and then .NET calls back `presentFrame(...)`. Collapse to a single crossing (JS→.NET with a return payload) to halve interop churn.
- Extra memory copy per frame: `drawFrame` copies `framebuffer` into `imageData.data` before `gl.tex(Sub)Image2D(...)`. Upload directly from `framebuffer` to remove this copy.
- Inputs and notes send many tiny interop calls (`UpdateInput` on each key event; `noteEvent` per note). Batch them once per frame.
- Audio SAB ring exists but is behind a query flag and cross-origin isolation. Default-enable when available; fall back to legacy scheduling otherwise.

### 1) Single-crossing per frame: JS RAF returns frame payload (eliminate extra interop)
Hypothesis: Current loop performs two crossings each frame (JS→.NET `FrameTick`, then .NET→JS `presentFrame`). Have JS invoke `FrameTick` and return `{ framebuffer, audio?, sampleRate }` so presentation happens entirely in JS within the same call chain.
Impact: Very High (halve interop per frame; -10–30% main-thread/GC overhead). Effort: M. Risk: Low–Med.
Dependencies: JS glue in `wwwroot/nesInterop.js`, .NET `FrameTick` signature and serialization policy.
Acceptance
- [ ] Exactly one interop crossing per frame during emulation (JS→.NET).
- [ ] JS receives a payload object and calls internal present once; no `.NET → JS` `presentFrame` during run.
- [ ] Perf markers show interop time per frame reduced ≥40% vs baseline.
Tasks
- [ ] Change JS `startEmulationLoop` to await a payload: `const r = await dotNetRef.invokeMethodAsync('FrameTick');`
- [ ] Define a compact return DTO in .NET (e.g., `{ byte[] fb; float[]? audio; int sr; }`, or pointers when available).
- [ ] In .NET `FrameTick`, run CPU, fill buffers, return the DTO instead of calling `presentFrame`.
- [ ] In JS, replace external `presentFrame` with an internal helper (reusing existing draw/audio paths) and remove in-run calls to `presentFrame`.
- [ ] Keep `drawFrame` for paused/preview-only flows; add guards to ensure it’s not used while the loop is active.

### 2) Remove ImageData copy: upload framebuffer directly with WebGL
Hypothesis: `drawFrame` copies `framebuffer` into `imageData.data` then uploads that array. Remove the copy: pass `framebuffer` directly to `gl.tex(Sub)Image2D`.
Impact: Very High on mobile (one full 256x240x4 copy/frame removed). Effort: S. Risk: Low.
Dependencies: `wwwroot/nesInterop.js` draw path.
Acceptance
- [ ] WebGL path never calls `imageData.data.set(framebuffer)` when WebGL is available.
- [ ] `gl.texSubImage2D(..., framebuffer)` used directly; `imageData` retained only for 2D fallback.
- [ ] Perf markers show ≥0.3–0.8 ms/frame improvement on mid devices.
Tasks
- [ ] Branch early: if WebGL available, upload from `framebuffer` directly; skip ImageData mutation.
- [ ] Keep 2D fallback intact for non-WebGL contexts.
- [ ] Add JS perf marks around upload to quantify gains.

### 2b) Zero-copy via memory offsets (optional advanced)
Hypothesis: Passing a linear-memory offset + length lets JS view WASM memory directly, avoiding Blazor marshaling copies.
Impact: High (extra copy removed; headroom for higher resolutions). Effort: M–L. Risk: Med (Blazor/WASM details).
Dependencies: Access to WASM memory buffer from Blazor; feature gate; docs.
Acceptance
- [ ] Experimental path behind a `?featureFbPtr=1` query flag or app setting.
- [ ] JS constructs `new Uint8Array(WebAssembly.Memory.buffer, offset, size)` for upload.
Tasks
- [ ] Expose frame pointer/length from .NET when available.
- [ ] Implement guarded JS path; maintain legacy marshaled fallback.

### 3) Default-enable AudioWorklet ring buffer; increase queue depth
Hypothesis: Small audio buffers and many discrete `noteEvent` calls create IPC overhead and underruns. Larger buffers and per-frame note batching smooth audio and reduce traffic.
Impact: High (10–30% fewer underruns; jank reduction). Effort: S–M. Risk: Medium.
Dependencies: SharedArrayBuffer ring requires COOP/COEP; service worker passthrough.
Acceptance
-- [ ] Audio SAB ring auto-inits on first audio; no manual query flag required.
-- [ ] Worklet queue depth ≥ 60–100 ms; underruns counter ~0 during play.
Tasks
- [ ] Add COOP/COEP headers to hosting (and SW) for cross-origin isolation.
- [ ] Remove `?featureAudioSAB` gating; enable SAB ring by default with fallback to legacy.
- [ ] Increase queue target (ring size 0.25–0.5s) and expose diagnostics in `debugReport()`.
- [ ] Pre-initialize AudioWorklet on boot to avoid first-frame spikes.

### 4) Throttle Blazor UI renders and coalesce state updates
Hypothesis: Frequent `StateHasChanged` triggers re-render churn competing with frame loop.
Impact: Medium (5–15%). Effort: S. Risk: Low.
Dependencies: None.
Acceptance
- [ ] UI state updates are coalesced (e.g., 30 Hz cap) during active emulation
- [ ] No visible UI lag for buttons/toggles
Tasks
- [ ] Gate `OnStateChanged = () => InvokeAsync(StateHasChanged)` behind a coalescer with a timestamp/flag
- [ ] Avoid calling `StateHasChanged` inside hot setters or tight loops
- [ ] Add a debug toggle to disable throttling for diagnostics

### 5) Shader cost management, lazy compile, and presets
Hypothesis: Expensive shader passes reduce frame rate on mobile; default to lighter shaders where needed.
Impact: Medium (5–20% GPU savings; CPU savings from less compile). Effort: S–M. Risk: Low.
Dependencies: JS glue for shader registry; device heuristics.
Acceptance
- [ ] “Mobile/Lite” preset selected automatically on lower-end devices
- [ ] Switching presets is smooth and doesn’t stall the frame loop
Tasks
- [ ] Tag shaders with a complexity score; pre-register metadata once
- [ ] Lazy-compile only the active shader at init; compile others on-demand
- [ ] Implement a “Graphics Quality” selector (Auto, Lite, Default, Fancy)
- [ ] Simple device heuristic (Device Memory, initial FPS probe) to choose default

### 6) Reduce per-frame GL setup costs (VAO / attribute binding)
Hypothesis: Rebinding aPos/aTex and vertexAttribPointer each frame adds overhead. Using VAO (or OES_vertex_array_object) avoids redundant work.
Impact: Low–Medium (1–5% CPU). Effort: S. Risk: Low.
Dependencies: WebGL2 (VAO) or extension on WebGL1.
Acceptance
- [ ] Attributes configured once per program; per-frame just bind VAO and draw.
Tasks
- [ ] Detect VAO support; create and bind VAO during init; remove per-frame attribute rebinding.

### 7) Single driver loop via requestAnimationFrame (avoid double timing)
Hypothesis: Competing loops (timer in .NET + RAF in JS) can cause phase drift and extra work.
Impact: Medium (5–15%). Effort: S. Risk: Low.
Dependencies: Confirm `nesInterop.startEmulationLoop` is the only driver during run.
Acceptance
- [ ] Only the JS RAF loop drives frames; no other loop advances frames while running
- [ ] Stable frame cadence in Performance panel (consistent RAF interval)
Tasks
- [ ] Audit `startEmulationLoop` implementation; ensure .NET isn’t advancing frames concurrently
- [ ] Add a frame-skipping option when CPU bound (skip present, keep audio stable)

### 8) Move heavy storage work off critical path (IDB/state)
Hypothesis: IndexedDB reads/writes on the hot path create jank.
Impact: Medium (stutter reduction). Effort: S. Risk: Low.
Dependencies: None.
Acceptance
- [ ] No IDB access during active emulation loop except explicit user actions
- [ ] Prefs cached in memory after initial load
Tasks
- [ ] Cache `idbGetItem` results at startup; write-behind `idbSetItem`
- [ ] Ensure save/load state chunking only runs when paused

### 9) Input event aggregation (keydown/keyup/touch)
Hypothesis: Many small input interop calls increase overhead; bundle them per frame.
Impact: Low–Medium (2–8%). Effort: S. Risk: Low.
Dependencies: Existing `registerInput` and `[JSInvokable] UpdateInput`.
Acceptance
- [ ] At most one `UpdateInput` call per RAF tick
Tasks
- [ ] Change `registerInput` to set a dirty flag and only flush state once per RAF (e.g., inside `startEmulationLoop` step).
- [ ] Unify touch controller updates with the same aggregator.
- [ ] Verify .NET handler does O(1) copy into the 8-button state

### 10) Allocation hygiene (GC pressure)
Hypothesis: Per-frame allocations (arrays, strings) increase GC and hitches.
Impact: Low–Medium (2–10%). Effort: S. Risk: Low.
Dependencies: None.
Acceptance
- [ ] Zero Gen0 spikes correlated with per-frame work in traces
Tasks
- [ ] Reuse frame/audio buffers on both .NET and JS sides
- [ ] Avoid JSON and string formatting in hot paths; move logging behind sampling

### 11) Built-in perf telemetry and guardrails
Hypothesis: Targeted timing and counters accelerate iteration and prevent regressions.
Impact: Enabler. Effort: S. Risk: Low.
Dependencies: None.
Acceptance
- [ ] Perf overlay showing: interop time, GL upload ms, RAF FPS, audio queue depth, underruns
- [ ] CI smoke check runs a headless perf probe (if feasible)
Tasks
- [ ] Add `performance.mark/measure` in JS around uploads/present
- [ ] Add counters in .NET for interop calls/frame; expose to UI
- [ ] Optional: lightweight benchmark mode to capture 5s traces

---

## Quick wins (low effort, high ROI)
- [ ] Collapse to one interop per frame: return payload from `FrameTick`; stop calling `presentFrame` during run
- [ ] WebGL upload from `framebuffer` directly; remove `imageData.data.set(framebuffer)` in hot path
- [ ] Default-enable AudioWorklet SAB ring; target ≥ 60–100 ms queue; pre-init on boot
- [ ] Batch SoundFont note events per frame (array payload) instead of per-note invocations
- [ ] Aggregate input changes; flush `UpdateInput` once per RAF
- [ ] Debounce `StateHasChanged` to ≤ 30 Hz during active emulation
- [ ] Cache preferences from IDB at startup; avoid reads during play
- [ ] Lazy-compile only the active shader at init; compile others on-demand
- [ ] Confirm WebGL path is used (no `putImageData` in hot path); `gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1)` and a single reused texture
- [ ] Add simple perf overlay to watch FPS/underruns while iterating

---

## Dependencies and environment
- SharedArrayBuffer (SAB) ring buffers require cross-origin isolation:
  - COOP: `same-origin`, COEP: `require-corp` headers served for all pages/workers.
  - Service Worker must pass through these headers. If hosting cannot provide them, keep the postMessage batching fallback.
- OffscreenCanvas in a Worker is optional; Safari support is limited. Keep main-thread WebGL as default, Worker path as progressive enhancement.
 - For zero-copy framebuffer (optional): gated feature flag; ensure compatibility with Blazor WASM memory access; document rollback path (HOTPOT-05 context noted in JS).

---

## Validation steps
- [ ] Add perf markers and counters (JS + .NET) and verify baselines
- [ ] Compare: before vs after for Items 1–3 (interop/frame upload time, RAF FPS stability, underruns)
- [ ] Mobile field test: Pixel and iPhone, 3-minute sessions with Lite shader preset
- [ ] Document results in `docs/projects/pressure-results.md`

---

## References (code touchpoints)
- Per-frame interop: `NesEmulator/board/Emulator.cs` → `nesInterop.presentFrame(...)`
- Preview/bench paint: `Benchmark.cs`, `StatePersistence.cs`, `corruptor/GlitchHarvester.cs` → `drawFrame(...)`
- Shader registration/selection: `Emulator.cs` + `nesInterop.registerShader/setShader`
- Audio control: `Emulator.cs` + `nesInterop.ensureAudioContext/resetAudioTimeline/flushSoundFont/noteEvent`

