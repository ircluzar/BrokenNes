# Project Charter — Core Instantiation & Resource Optimization

## Objective

Reduce startup/time/memory overhead by avoiding eager construction of all discovered emulator cores and eliminating unnecessary large-data retention (PPU framebuffers, APU buffers, duplicate instances). Provide safe lazy/factory-based core instantiation, correct APU reset behavior, and reduce state save size.

## Scope

- Replace eager per-Bus construction of all cores with on-demand creation.
- Cache type discovery results and expose minimal metadata/factory access in `CoreRegistry`.
- Ensure PPU/APU large buffers are allocated only when an instance becomes active (or are pooled/shared).
- Make `Bus.HardResetAPUs` actually recreate or reset APU internals.
- Prevent saving the large PPU framebuffer in normal SaveState.
- Ensure APU save/load never restores stale audio buffers; re-sync browser audio timeline on resets/savestate loads.
- Keep API compatibility for external callers (NES/Bus public methods).

## Success Criteria (acceptance)

- Bus creation time and memory usage drop significantly when multiple core implementations are present.
- Only active core instances retain large arrays (e.g., PPU framebuffer) at steady state.
- Hot-switch still works: state is transferred when switching cores and restored when loading save states.
- HardResetAPUs clears audio state between ROM loads.
- SaveState no longer serializes full framebuffer; save size reduced accordingly.
- Loading a savestate does not produce audio desync, glitches, or long-term drift; if backlog occurs after lag, audio self-corrects within ~50ms.
- No public API behavioral regressions (NES.Set*/Get* functions still work).

## High-level deliverables

- Design doc (small): lazy/factory pattern and CoreRegistry changes.
- CoreRegistry changes: cache discovered Types, expose factory functions or Type lists.
- Bus changes: store Type metadata, create-on-demand per-suffix instances, keep one instance per Bus per suffix.
- PPU changes: support allocation-on-activation or move large buffers to lazy init; add Reset/Dispose if needed.
- APU changes: lazy creation and HardResetAPUs implementation to recreate/clear APUs.
- JS/Interop changes: add timeline-reset API and wire it into savestate load/reset paths.
- NES Save/Load changes: stop serializing PPU framebuffer; ensure determinism still preserved.
- Comments/docs updated and minimal smoke tests (manual) checklist.

## Implementation plan — tasks & subtasks

### 1) Design & planning
- [ ] Draft short design doc describing lazy/factory approach, shared constraints (AOT/wasm), lifecycle rules for core instances.
  - [ ] Decide whether CoreRegistry returns Type metadata or Func<Bus,IFace> factories.
  - [ ] Define per-Bus lifecycle: one instance per suffix per Bus; when to Dispose/Recreate.
  - [ ] Define PPU/APU lazy-init behavior and minimal Reset/Dispose surface.
  - [ ] Define SaveState acceptance criteria (what must be serialized for determinism).

### 2) CoreRegistry: discovery & cached metadata
- [ ] Add cached mapping prefix -> List<Type> (or suffix->Type) on Initialize; stop repeated GetTypes() scans.
- [ ] Add API to return Type list or factory delegates: e.g. GetCpuTypes()/GetCpuFactoryTypes() and ExtractSuffix helper usage.
- [ ] Add helper to create a new instance on demand (CreateInstance<TIface>(Type t, Bus bus) or return Type so Bus can invoke ctor).
- [ ] Unit: validate that discovery still returns correct suffix ids and ordering.

### 3) Bus: adopt lazy instantiation and minimize eager allocations
- [ ] Replace `_cpuCores/_ppuCores/_apuCores` eager-dict fill with:
  - internal dictionaries mapping suffix -> Type (or factory), and
  - lazy instance dictionary suffix -> TIface? (null until requested)
- [ ] Implement GetCpu/GetPpu/GetApu that:
  - If instance exists, return it.
  - Else construct via cached Type/ctor (prefer Bus ctor), store in instance dictionary and return.
- [ ] Update all existing callers that assumed all _ppuCores/_apuCores keys existed (expose GetXCoreIds() from cached Type-suffix map).
- [ ] Ensure reference-equality-based hot-swap detection still works (active pointers reference created instance).
- [ ] Keep legacy public fields (cpu/ppu/apu/) updated to point to active instances.
- [ ] Add optional disposal hook for cores if appropriate (IDisposable or ClearLargeBuffers()).

### 4) PPU: avoid large per-core allocation until active
- [ ] Modify PPU implementations (start with PPU_FMC) to defer allocating `frameBuffer` and other large buffers until first Render/GetFrameBuffer call or until ctor receives a flag.
  - [ ] Add lazy init within `GetFrameBuffer()`, `Step()` entry or explicit `InitializeForBus(Bus)` called by Bus when creating instance.
  - [ ] Optionally move shared temporary buffers (bgMask, spritePixelDrawnReuse) into reusable pool or `static readonly` for reuse.
- [ ] Add `Reset()` / `ClearBuffers()` method to clear large arrays when disposing or on HardReset as needed.
- [ ] Ensure `GetState()` does not include `frame` unless a debug flag forces snapshot.

### 5) APU: lazy creation + HardResetAPUs correctness
- [ ] Apply same lazy pattern to APUs (defer ring buffers and audio queues).
- [ ] Implement `HardResetAPUs()` to actually recreate instances for known suffixes:
  - [ ] Use CoreRegistry cached Type to instantiate fresh APU instances and replace per-Bus instance entries.
  - [ ] Clear apuRegLatch state after recreation (already there).
- [ ] Add an optional `Reset()` on APU implementations to clear internal buffers if recreation is undesirable.
- [ ] Make APU savestate load deterministic and allocation-friendly:
  - [ ] In each APU `SetState`/`RestoreState`, do NOT restore ring indices/count into an assumed-valid buffer. Instead clear audio queue: `ringCount=0; ringRead=ringWrite=0; fractionalAccumulator=0;` (or call `ResetInternal()`).
  - [ ] Do not serialize raw PCM from the audio ring in `GetState()` (keeps save size small and avoids perf regressions).
  - [ ] Consider pooling temporary arrays via `ArrayPool<float>` if any transient copies are introduced (measure first).
- [ ] Performance hygiene:
  - [ ] Cap `GetAudioSamples(max)` to a stable chunk size (e.g., 512 or 1024 frames) to reduce jitter and allocation variance.
  - [ ] If ring backlog exceeds a threshold (e.g., > 3 chunks), drop oldest frames to catch up (configurable soft flush) — prefer recovery over unbounded latency.

### 6) Save/Load: remove large framebuffer serialization
- [ ] Update PPU `GetState()` to not include raw `frame` by default; include VRAM/PALETTE/OAM and register state only.
- [ ] Update NES.SaveState PlainSerialize behavior/usage so stored state is smaller (frame omitted).
- [ ] Update LoadState to not expect `frame`; ensure UpdateFrameBuffer regenerates visuals as needed.
- [ ] Add acceptance test: saved .nes state size reduced; load restores deterministic state.
- [ ] After LoadState, clear APU audio queue (per 5) and reset browser audio timeline (per 10) so host and core stay aligned.

### 7) Reuse / Bus wiring improvements
- [ ] Consider deferring Bus-specific heavy wiring from core ctor to an `Initialize(Bus)` method so cores can be created cheaply and bound later — implement if design requires.
- [ ] Document any breaking behaviors and migration notes.

### 8) Documentation & comments
- [ ] Update comments where behavior changed (e.g., HardResetAPUs comment, CoreRegistry docs).
- [ ] Add short README section describing lazy core behavior for contributors.

### 9) Manual validation & smoke checks (no tests included in charter)
- [ ] Measure memory & creation time before/after for a representative build (e.g., WASM build).
- [ ] Manually test:
  - [ ] Loading a ROM and verifying only active core memory is allocated.
  - [ ] Hot-swapping CPU/PPU/APU from UI and verifying state transfer.
  - [ ] Load/Save state roundtrip and verify visuals after load.
  - [ ] HardResetAPUs between ROM loads to ensure no audio bleed.
  - [ ] Savestate load produces no audio pop/desync; timeline reset invoked; backlog recovers quickly if induced lag occurs.
- [ ] If regressions found, create targeted bug fixes.

### 10) Audio scheduling & JS interop robustness (WebAudio)
- [ ] Add `nesInterop.resetAudioTimeline()`:
  - [ ] Ensure a single shared AudioContext via `ensureAudioContext()`.
  - [ ] Set `window._nesAudioTimeline = audioCtx.currentTime + 0.02` (small lead), without creating new contexts.
- [ ] Call `resetAudioTimeline()` from C# after:
  - [ ] Savestate load completes.
  - [ ] Emulator restart/HardResetAPUs.
  - [ ] AudioContext resume (if applicable).
- [ ] Keep audio chunk duration stable (e.g., 1024 frames @44.1kHz ≈ 23ms) to improve scheduler predictability.
- [ ] Guard: if scheduling falls behind (`timeline < currentTime`), clamp to `currentTime + 0.01` (already present), and optionally flush emulator ring to one chunk to re-sync.
- [ ] Measure: verify no extra allocations in hot path; avoid constructing new JS arrays where possible; reuse typed arrays on the .NET side if feasible for interop.

## Prioritization & estimates (rough)
- P0 (high, quick wins)
  - Cache discovered Types in CoreRegistry — 0.5 day
  - Bus: replace eager instance creation with lazy per-type map + on-demand ctor — 1.5–2 days
  - PPU lazy buffer init in main PPU implementation — 1 day
  - APU lazy init + HardResetAPUs fix — 1–1.5 days
  - APU SetState clears audio ring + JS `resetAudioTimeline()` and call sites — 0.5 day
- P1 (medium)
  - Switch other PPUs/APUs to lazy pattern and add Reset hooks — 1.5 days
  - SaveState/LoadState omit framebuffer and adjust serialization — 0.5–1 day
  - Stabilize audio chunk sizing and add backlog soft-flush — 0.5 day
- P2 (low)
  - Optional pool/reuse of temporary arrays and bus-wiring refactor — 1–2 days
  - Documentation and manual validation — 0.5 day

## Risks & mitigations
- Risk: Hot-swap state transfer may rely on implementation-specific private state.
  - Mitigation: Use shared state DTOs (e.g., `PpuSharedState`) and require SetState/GetState on interfaces; adapt cores to produce/consume that shared shape.
- Risk: AOT/WASM reflection limitations if dynamic factory use triggers AOT issues.
  - Mitigation: Keep reflection uses restricted to Type inspection and explicit ctor invocation with known signatures; avoid runtime code emission. Validate with a native flatpublish build.
- Risk: Behavioral regressions if any core expects early allocation.
  - Mitigation: Add a small compatibility layer calling core.Initialize(Bus) if needed.
- Risk: Clearing audio ring on savestate load may create a brief audible gap.
  - Mitigation: Reset JS timeline and keep chunk sizes small/stable; the gap is preferable to desync and should be inaudible with a short lead-in.
- Risk: Dropping oldest audio when backlog is large might cut sustained tones.
  - Mitigation: Use a conservative threshold and make it configurable/debug-only initially; record metrics during testing.

## Next step
Choose one of:
- [ ] Implement the full patch set (start with CoreRegistry + Bus lazy instantiation).
- [ ] Implement PPU lazy allocation first (highest memory win).
- [ ] Create a minimal proof-of-concept patch that changes only CoreRegistry to expose Types and Bus to lazy-create PPU (no other cores).

Which should be started first?
