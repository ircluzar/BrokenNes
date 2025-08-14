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
  - [x] Decide whether CoreRegistry returns Type metadata or Func<Bus,IFace> factories. (Chose Type metadata + factory helper)
  - [ ] Define per-Bus lifecycle: one instance per suffix per Bus; when to Dispose/Recreate.
  - [ ] Define PPU/APU lazy-init behavior and minimal Reset/Dispose surface.
  - [ ] Define SaveState acceptance criteria (what must be serialized for determinism).

### 2) CoreRegistry: discovery & cached metadata
- [x] Add cached mapping prefix -> List<Type> (or suffix->Type) on Initialize; stop repeated GetTypes() scans.
- [x] Add API to return Type list or factory delegates: e.g. GetCpuTypes()/GetCpuFactoryTypes() and ExtractSuffix helper usage.
- [x] Add helper to create a new instance on demand (CreateInstance<TIface>(Type t, Bus bus) or return Type so Bus can invoke ctor).
- [ ] Unit: validate that discovery still returns correct suffix ids and ordering.

### 3) Bus: adopt lazy instantiation and minimize eager allocations
- [ ] Replace `_cpuCores/_ppuCores/_apuCores` eager-dict fill with:
  - [x] internal dictionaries mapping suffix -> Type (or factory), and (PPU only in this batch)
  - [x] lazy instance dictionary suffix -> TIface? (null until requested) (PPU only in this batch)
- [x] Implement GetCpu/GetPpu/GetApu that:
  - [x] If instance exists, return it. (PPU)
  - [x] Else construct via cached Type/ctor (prefer Bus ctor), store in instance dictionary and return. (PPU)
- [x] Update all existing callers that assumed all _ppuCores/_apuCores keys existed (expose GetXCoreIds() from cached Type-suffix map). (PPU IDs)
- [x] Ensure reference-equality-based hot-swap detection still works (active pointers reference created instance). (PPU)
- [x] Keep legacy public fields (cpu/ppu/apu/) updated to point to active instances. (PPU)
- [ ] Add optional disposal hook for cores if appropriate (IDisposable or ClearLargeBuffers()).
  
  Note: CPU/APU remain eager in this batch to minimize risk; will convert to lazy in a follow-up.

### 4) PPU: avoid large per-core allocation until active
- [ ] Modify PPU implementations (start with PPU_FMC) to defer allocating `frameBuffer` and other large buffers until first Render/GetFrameBuffer call or until ctor receives a flag.
  - [ ] Add lazy init within `GetFrameBuffer()`, `Step()` entry or explicit `InitializeForBus(Bus)` called by Bus when creating instance.
  - [ ] Optionally move shared temporary buffers (bgMask, spritePixelDrawnReuse) into reusable pool or `static readonly` for reuse.
- [ ] Add `Reset()` / `ClearBuffers()` method to clear large arrays when disposing or on HardReset as needed.
- [ ] Ensure `GetState()` does not include `frame` unless a debug flag forces snapshot.

### 5) APU: lazy creation + HardResetAPUs correctness
- [x] Apply same lazy pattern to APUs (defer ring buffers and audio queues) via cached type map and lazy instance creation in `Bus`.
  - [x] Implemented `HardResetAPUs()` to recreate instances for known suffixes (FIX, FMC, QN) by dropping cached instances and re-instantiating.
  - [x] Clear apuRegLatch state after recreation to prevent carryover writes between ROMs.
- [ ] Add an optional `Reset()` on APU implementations to clear internal buffers if recreation is undesirable.
- [ ] Make APU savestate load deterministic and allocation-friendly:
  - [ ] In each APU `SetState`/`RestoreState`, do NOT restore ring indices/count into an assumed-valid buffer. Instead clear audio queue or call an internal reset.
  - [ ] Do not serialize raw PCM from the audio ring in `GetState()` (keeps save size small and avoids perf regressions).
  - [ ] Consider pooling temporary arrays via `ArrayPool<float>` if any transient copies are introduced (measure first).
- [ ] Performance hygiene:
  - [ ] Cap `GetAudioSamples(max)` to a stable chunk size to reduce jitter and allocation variance.
  - [ ] If ring backlog exceeds a threshold, drop oldest frames to catch up — prefer recovery over unbounded latency.

### 6) Save/Load: remove large framebuffer serialization
 [x] Update PPU `GetState()` to not include raw `frame` by default; include VRAM/PALETTE/OAM and register state only.
 [x] Update NES.Save/Load changes: stop serializing PPU framebuffer; ensure determinism still preserved.
- [ ] Update LoadState to not expect `frame`; ensure UpdateFrameBuffer regenerates visuals as needed.
- [ ] Add acceptance test: saved .nes state size reduced; load restores deterministic state.
- [ ] After LoadState, clear APU audio queue (per 5) and reset browser audio timeline so host and core stay aligned.

### 7) Reuse / Bus wiring improvements
- [ ] Consider deferring Bus-specific heavy wiring from core ctor to an `Initialize(Bus)` method so cores can be created cheaply and bound later — implement if design requires.
- [ ] Document any breaking behaviors and migration notes.

### 8) Documentation & comments
- [x] Update comments where behavior changed (e.g., HardResetAPUs comment, CoreRegistry docs).
- [ ] Add short README section describing lazy core behavior for contributors.

### 9) Manual validation & smoke checks (no tests included in charter)
- [ ] Measure memory & creation time before/after for a representative build (e.g., WASM build).
- [x] Manually test:
  - [x] Build Release to ensure green.
- [ ] Later: Load a ROM, hot-swap cores, save/load state checks, audio reset checks.

### 10) Audio scheduling & JS interop robustness (WebAudio)
- [ ] Add `nesInterop.resetAudioTimeline()` and call it after savestate loads and hard resets.
- [ ] Keep audio chunk duration stable; clamp when falling behind.

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
- [x] Implement the full patch set (start with CoreRegistry + Bus lazy instantiation). (Started: CoreRegistry cached types + Bus PPU lazy creation)
- [ ] Implement PPU lazy allocation first (highest memory win).
- [ ] Create a minimal proof-of-concept patch that changes only CoreRegistry to expose Types and Bus to lazy-create PPU (no other cores).

Batch 1 summary:
- Added cached type maps and factory helpers in `CoreRegistry`.
- Switched `Bus` to lazy-create PPU instances using cached type map; CPU/APU remain eager for stability.
- Build verified in Release configuration.

### Batch 2 — Lazy APU creation + proper hard reset (Done)
- Converted APU to lazy instantiation in `Bus` by caching APU types and creating instances on first use. Default selections (FIX/FMC/QN) preserved.
- Implemented robust `HardResetAPUs` that drops cached instances for FIX/FMC/QN and recreates them via `CoreRegistry`.
- Re-selects the previously active core and clears the latched APU register mirror to avoid carryover between ROMs.
- Exposed `GetApuCoreIds()` based on cached type metadata. Public APIs preserved.
- Status: Build green; functional behavior unchanged.

Next planned: Batch 3 — PPU framebuffer/state optimization.

### Batch 3 — PPU framebuffer omitted from saves (Done)
- Removed framebuffer from PPU state serialization across all PPU cores (`PPU_FMC`, `PPU_CUBE`, `PPU_BFR`, `PPU_LQ`).
- Kept backward compatibility: `SetState` still accepts legacy states with `frame` and conditionally copies if present.
- `PpuSharedState.frame` now defaults to empty to discourage consumers from saving pixel data.
- Release build verified green.

Update (Audio post-load stutter fix):
- Implemented JS `resetAudioTimeline()` and invoked it after `NES.LoadState()` in `Pages/Nes.razor`.
- Modified all APU cores to clear their host-facing audio ring buffers and fractional accumulators on `SetState` to avoid restoring stale PCM samples. The QuickNES APU no longer serializes the raw ring buffer.
- Outcome: Eliminates the “flip-flop” hitch where loading a state could start stuttering until a second load; scheduling now restarts cleanly from the restored internal APU state.
