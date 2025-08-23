# Clock Project — Pluggable Clock Core (FMC/CLR) and Loop Ownership

Purpose: Introduce a new "Clock Core" component type that controls who owns the main frame loop (JS vs C#), with minimal overhead in hot paths. Provide auto-discovery and UI selection like other cores. Persist selection in user settings and savestates. Default remains current JS-driven loop (FMC); new C#-driven loop (CLR) is opt-in.

Lean principle: No extra work inside hot loops. The clock selection must only influence control flow at loop setup and, at most, one branch per frame boundary. Do not add per-instruction or per-pixel branches.

---

## Checklist (requirements)

- [ ] Add a new component type: Clock Core (auto-discoverable like CPU/APU/PPU/Mappers).
- [ ] Define `IClock` interface with descriptors (ID, Name, Description, Stability/Tags), matching conventions of other core types.
- [ ] Implement default clock core "FMC" (existing JS-driven RAF loop behavior; no regressions).
- [ ] Implement new clock core "CLR" (move loop ownership to C# side; browser-safe throttling).
- [ ] Ensure zero/near-zero overhead in hot loops (branch only at loop boundary; no per-instruction checks).
- [ ] Add UI selector for Clock Core (Pages/Nes.razor) alongside other core selectors.
- [ ] Persist selected Clock Core in settings (IDB/preferences) and restore on startup.
- [ ] Persist selected Clock Core in savestates; load should honor state’s selection (with safe fallback).
- [ ] Backward compatibility: if no clock recorded, default to FMC.
- [ ] Minimal docs: update README or add notes about clock cores and how to switch.

## Success criteria

- [ ] Functional parity: FMC produces identical behavior as current builds.
- [ ] CLR runs frames without jank, supports pause/resume, and respects tab/background constraints.
- [ ] Switching between FMC/CLR works at runtime (stop current loop, start target loop) without leaks.
- [ ] Selection persists across app reloads (settings) and within save/load state flows.
- [ ] No measurable regression from added clock selection plumbing in FMC mode (frame time variance within noise).

## Scope and non-goals

- In scope: New `IClock` interface, two clock implementations (FMC/CLR), discovery, UI selector, persistence, minimal loop host refactor.
- Not in scope: Rewriting the rendering/audio transport paths, zero-copy framebuffer, or SAB audio changes (covered by HotPot). CLR clock may reuse existing present/audio calls initially.

---

## Design overview

### Component model

- New interface `IClock` with attribute-based descriptors similar to other cores (e.g., `CoreId`, `DisplayName`, optional `Stability`, `Description`, `Flags`).
- Clock discovery mirrors existing core discovery: reflection scan once on startup; register by `CoreId` (string or small int).
- FMC (JS driver): Wrapper that integrates with existing `requestAnimationFrame` loop in `wwwroot/nesInterop.js` and `[JSInvokable] FrameTick()`; this remains default.
- CLR (C# driver): A C# loop host that advances frames and coordinates presentation, with browser-friendly throttling. Initial implementation uses a lightweight loop with `ValueTask`/`Task.Yield` or a timer-like cadence while active; strictly off the hot compute path.

### Minimal overhead principle

- Choose the active clock once (on emulator start or when switching); do not branch inside inner CPU/APU/PPU loops.
- At most, a single per-frame branch at the loop boundary for FMC vs CLR hand-off.
- Reuse existing frame execution entrypoint (`RunFrame()`/`FrameTick`) without adding allocations or logging in release builds.

### Interface sketch (contract)

Inputs/Outputs and responsibilities:
- `IClock` manages frame cadence and calls into emulator to compute/present frames using existing services.
- It exposes lifecycle methods for start/stop/suspend and a tiny descriptor.

Members (proposed):
- `string CoreId { get; }` // e.g., "FMC", "CLR"
- `string DisplayName { get; }`
- `string Description { get; }`
- `ClockCapabilities Capabilities { get; }` // flags (optional)
- `ValueTask StartAsync(IClockHost host, CancellationToken ct)`
- `void Stop()`
- `void OnVisibilityChanged(bool visible)` // optional hint for throttling

`IClockHost` (already present patterns in board/Emulator/UI):
- `ValueTask<FramePayload> TickAsync()` or `void RunFrame()` — whichever matches current entrypoint.
- `void Present(in FramePayload payload)` or handled in JS for FMC as today.
- Access to status flags and pause/resume.

Descriptor attributes (mirror pattern used by other cores):
- `[Core(Id="FMC", Name="Frame Manager (JS)", Kind="Clock", Stability=Stable, Order=0)]`
- `[Core(Id="CLR", Name="Managed Loop (C#)", Kind="Clock", Stability=Experimental, Order=1)]`

### FMC implementation (default)

- Thin adapter mapping to current JS RAF → `.NET FrameTick()` path.
- No change to hot code; only registers as the default Clock Core.
- UI label: "FMC (JS driver)".

### CLR implementation (managed loop)

- On `StartAsync`, spawn a lightweight loop that:
  1) Checks `running` and `paused` states.
  2) Calls emulator `RunFrame()` or `TickAsync()`.
  3) Schedules presentation (either returns payload for JS present, or calls a unified `presentFrame` once per frame as today).
  4) Yields appropriately to maintain cadence (no busy-waiting). Prefer `Task.Yield()` or short `Delay` tuned by measured frame time.
- On `Stop`, cancels the loop and releases resources.
- `OnVisibilityChanged(false)`: throttle or suspend to avoid runaway CPU when tab is backgrounded.
- UI label: "CLR (C# driver)".

---

## Persistence model

Settings (preferences/IDB):
- Key: `clockCore` = "FMC" | "CLR". Default: "FMC".
- Read on startup; if unknown/missing, fallback to "FMC".

Savestates:
- Manifest root adds `clockCore` string. On load: if present, prefer it; if absent, keep current selection. Provide a user setting to honor state’s clock core or ignore (optional; default honor).
- Backward compatibility: load states without `clockCore` unchanged.

---

## UI changes

- `Pages/Nes.razor`: Add a Clock Core selector near other core selectors.
  - Dropdown bound to available clocks discovered at runtime; display `DisplayName` + (tags like Experimental).
  - On change: stop current clock; switch selection; persist to settings; start new clock.
- Disable/grey-out selection while running if switching live is not supported; otherwise ensure a safe stop/start sequence.

Accessibility and UX:
- Tooltips: briefly explain JS vs C# ownership and stability.
- Persist last selection in settings immediately.

---

## Auto-discovery and wiring

- Reuse existing reflection scan used for other core registries (folder: `NesEmulator/*` registry).
- New registry (or extend existing): `ClockRegistry` providing `IReadOnlyList<CoreDescriptor>` and factory by `CoreId`.
- FMC must register as default if settings missing/invalid.
- Ensure ordering stable and deterministic for dropdown.

---

## Code touch points (expected)

- `NesEmulator/board/Emulator.cs` or `NesEmulator/UI.cs`: introduce `IClock`, `IClockHost`, loop host wiring, start/stop on run/pause.
- `Pages/Nes.razor`: UI selector + persistence glue.
- `StatusService.cs` (if used): expose current clock core id/name for overlay/diagnostics.
- `wwwroot/nesInterop.js`: no changes required for FMC; optional small helper for visibility/pause events; ensure no double-driver when CLR active.
- Savestate: `NesEmulator/NES.cs` (or wherever manifest assembled): include `clockCore` field; parse it on load.
- Settings store: IDB helpers in `nesInterop` or existing settings service to persist `clockCore`.

---

## Acceptance tests / validation

- Settings
  - [ ] Change clock in UI, reload page → selection persists.
  - [ ] Invalid `clockCore` in settings falls back to FMC.
- Savestates
  - [ ] Save with CLR, reload → CLR active.
  - [ ] Save with FMC, reload → FMC active.
  - [ ] Load legacy savestate (no `clockCore`) → FMC active (or current selection if configured).
- Behavior
  - [ ] FMC path behaves identically to current main; no extra GC or interop.
  - [ ] CLR loop does not busy-wait; respects pause/visibility; frame cadence is stable.
  - [ ] Switching while running performs clean stop/start without leaks or double loops.
- Perf guardrail
  - [ ] In FMC mode, no measurable perf regression due to clock plumbing (within ±1% noise on desktop baseline).

---

## Risks and mitigations

- Double-driver risk (both JS RAF and CLR running):
  - Mitigation: central loop host gate; only one active `IClock` at a time; JS RAF disabled/parked when CLR active.
- Background tabs consuming CPU under CLR:
  - Mitigation: visibility hook; auto-throttle or suspend.
- Timers and precision:
  - Mitigation: use monotonic timestamps; simple PI controller optional later; start with conservative Yield/Delay.
- API churn in WASM/Blazor versions:
  - Mitigation: keep CLR path minimal, behind interface; FMC remains default.

---

## Tasks (actionable to-dos)

1) Types & Registry
- [ ] Add `IClock` and `IClockHost` interfaces (`NesEmulator/board` or `NesEmulator` root).
- [ ] Add `ClockDescriptor` attribute (or reuse existing CoreDescriptor) and `ClockRegistry` with reflection discovery.

2) Implementations
- [ ] `Clock_FMC` (default): descriptor `{ Id:"FMC", Name:"FMC (JS driver)" }`; no behavior change.
- [ ] `Clock_CLR` (experimental): descriptor `{ Id:"CLR", Name:"CLR (C# driver)" }`; implement `StartAsync/Stop` loop.

3) Loop Host Wiring
- [ ] Introduce a small `ClockHost` that bridges to existing `RunFrame()`/`FrameTick` and present path.
- [ ] Ensure only one active driver; add a guard flag and stop current before switching.

4) UI Selector
- [ ] `Pages/Nes.razor`: add dropdown; bind to `ClockRegistry.Available`.
- [ ] On change: persist to settings; restart clock.

5) Persistence
- [ ] Settings: add `clockCore` read/write via existing settings store (IDB in `nesInterop` or C# service).
- [ ] Savestate: add `clockCore` field; load path honors it; fallback remains FMC.

6) Diagnostics (optional)
- [ ] Expose current clock in debug overlay; small log when switching.

7) Docs
- [ ] Update README and `docs/core-lifecycle.md` with clock core selection and modes.

---

## Rollout plan

Phase 1 (Plumbing): Types, registry, FMC as default, UI selector (disabled switching while running if necessary), settings persistence.

Phase 2 (CLR prototype): Implement CLR loop; add visibility throttle; enable switching in UI; add savestate persistence.

Phase 3 (Polish): Diagnostics, minor cadence tuning, README/docs.

---

## Notes

- This project complements HotPot’s "Frame Loop Ownership Inversion" (HOTPOT-06). Start here with clean pluggability; HotPot can later optimize the CLR path transport if chosen.
- Keep the CLR loop simple; correctness and stability first. Performance enhancements (e.g., unified present, zero-copy) must remain optional and outside hot compute loops.
