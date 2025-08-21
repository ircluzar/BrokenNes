# Final Nes.razor Refactor Plan

Goal: Finish extracting logic from `Pages/Nes.razor` into the existing C# classes (`Emulator` partials, `NesController`, `Corruptor`, state/benchmark partials) while preserving ALL existing UI markup and keeping runtime behavior identical. Only the `@code` block contents still duplicated / not yet using the extracted classes should be removed or reduced to a very thin glue layer that delegates into `Emulator`.

## Principles
- Do NOT alter Razor markup (HTML/structure, element ids, classes) to avoid UI regressions.
- Keep public / JSInvokable method names that JS expects; if moved, ensure they remain `[JSInvokable]` and accessible (e.g., via `Emulator` instance reference injected / created in the page and registered with JS as before).
- Avoid duplication: eliminate code that already lives in `Emulator`, `StatePersistence`, `UI`, `Benchmark` (if exists) etc.
- Minimize surface in `Nes.razor` to: creating / owning a single `Emulator` instance, wiring `OnStateChanged` to call `StateHasChanged`, exposing a few property pass‑throughs used in bindings.
- Preserve existing local fields ONLY if they are pure view-model state not yet migrated. Identify any such leftovers and migrate if reasonable.

## Inventory: Large Functional Areas in Nes.razor @code
1. Initialization & lifecycle (OnInitialized, OnAfterRenderAsync, navigation dispose) – migrated to `Emulator.Initialize()` + `Emulator.OnAfterRenderAsync` + `Dispose`.
2. ROM management (upload, delete, search, size formatting) – largely duplicated; core logic exists in `NesController` (LoadSelectedRom etc.). Need to swap page handlers to delegate to controller / emulator methods.
3. Emulation control (Start/Pause/Reset, FrameTick loop, scaling, fullscreen) – now in `Emulator` (StartEmulation, PauseEmulation, RunFrame, etc.). Razor should delegate.
4. Save/Load/Chunked state persistence – logic duplicated in page; canonical copy moved to `StatePersistence.cs`. Remove page copies, call `emulator.SaveStateAsync()/LoadStateAsync()/DumpStateAsync()`.
5. Corruptor / Glitch Harvester (Blast, LetItRip, GH functions) – logic mostly in `Corruptor` class; page contains UI wrappers. Replace with thin delegates to `Corruptor` methods; any NES interaction that still exists (LoadState before apply) may need helper methods in `Emulator` if not already present.
6. Benchmark system (modal state, history, comparison graph, diff animation) – A partial named `Benchmark.cs` exists; verify duplication and remove page-side implementation if already migrated (if not, consider migration or leave as is temporarily but mark TODO).
7. SoundFont / audio mode toggles – logic now in `Emulator` (AutoConfigureForApuCore, ToggleSoundFontMode etc.). Page should route events to emulator.
8. Mobile fullscreen view toggles – handled in `UI.cs` partial; remove duplicate fields/methods from page.
9. Utilities (FormatSize, ExtractInt, compression helpers) – present in other partials; remove duplicates from page.

## Remaining Actions
- [ ] Create an `EmulatorHost` (or reuse existing) service / or instantiate `Emulator` inside `Nes.razor` with DI injected dependencies (ILogger<Emulator>, IJSRuntime, HttpClient, StatusService, IShaderProvider, NavigationManager).
- [ ] In `Nes.razor @code`, replace large field set with:
  - `private Emulator emu;` plus lightweight properties referencing `emu.Controller` & `emu.Corruptor` for existing bindings (e.g., `nesController` variable name used in markup). Option: keep variable name `nesController` by returning `emu.Controller`.
  - Provide properties/expressions for fields referenced in bindings that moved (e.g., `benchModalOpen`, etc.) referencing emulator internals (confirm availability; if not yet exposed, add public getters in partial classes rather than re-implement logic in Razor).
- [ ] Ensure every JSInvokable method referenced in JS (e.g., `JsSaveState`, `JsLoadState`, `JsResetGame`, mobile view events, `UpdateInput`, `FrameTick`) is present on the `Emulator` instance registered to JS. Remove duplicates in Razor.
- [ ] Register DotNetObjectReference: currently done inside `Emulator.OnAfterRenderAsync`; confirm no leftover page code still creating its own reference.
- [ ] Rewire event handlers in markup (`@onclick="StartEmulation"`, etc.) to call thin wrappers that forward to `emu.StartAsync()` etc., or rename wrappers to keep existing method names but delegate.
- [ ] Remove: duplicated save/load chunk logic, benchmark history lists, diff building, timeline logic from page if already migrated. If not migrated yet, plan sub-migration (future step) – for this pass, focus only on code already clearly duplicated.
- [ ] Update using statements: remove usings no longer needed once code block shrinks.

## Mapping of Razor Methods -> Emulator / Controller
| Razor Method | New Location | Action |
|--------------|--------------|--------|
| StartEmulation | Emulator.StartAsync | wrapper call |
| PauseEmulation | Emulator.PauseAsync | wrapper |
| ResetEmulation | (partial) need public ResetAsync if implemented; else expose existing logic | add method if required |
| SaveState / LoadState | Emulator.SaveStateAsync / LoadStateAsync | wrapper |
| DumpState | Emulator.DumpStateAsync | wrapper |
| FrameTick (JS) | Emulator.FrameTick | remove from Razor |
| UpdateInput (JS) | Emulator.UpdateInput | remove from Razor |
| Blast / LetItRip | Corruptor.Blast / LetItRip | wrappers (with NES passed via Emulator if needed) |
| Gh* methods | Corruptor.* + minimal emulator assist (LoadState etc.) | wrappers |

## Exposure Additions Likely Needed
Add public getters (if missing) in `Emulator` partials for:
- `Controller` (already public) alias `nesController` variable in Razor.
- `Corruptor` (already public) alias `corruptor` variable.
- SoundFont flags & toggles if UI binds directly (else create forwarding methods).
- Benchmark modal booleans / methods if markup references them (verify duplication vs Benchmark.cs).

## Step-by-Step Refactor Execution
1. Add this plan file (done).
2. Introduce a greatly simplified `@code` block in `Nes.razor` creating `Emulator emu; NesController nesController => emu.Controller; Corruptor corruptor => emu.Corruptor;` plus wrapper event handlers.
3. Remove massive original `@code` block content.
4. Compile; fix missing symbol references by exposing public members in `Emulator` if necessary.
5. Test: build, run; verify UI still loads, ROM loads, play/pause, load/upload, corruptor, GH, save/load, benchmark open.
6. Clean up any obsolete using directives.

## Non-Goals (This Pass)
- Moving benchmark graphing logic if not already extracted (only remove if safely duplicated elsewhere).
- Large-scale renaming of methods or variables in markup.
- Architectural redesign of `Emulator` (aim is completion of extraction, not overhaul).

## Validation Checklist After Change
- [ ] Build succeeds with no new warnings/errors related to removed code.
- [ ] JS interactions (keyboard, frame loop, fullscreen, drag/drop) still functional.
- [ ] ROM upload/delete operations function (delegating to controller logic).
- [ ] Save/Load state works (calls into `StatePersistence`).
- [ ] Corruptor auto & manual blast updates LastBlastInfo overlay.
- [ ] Glitch Harvester base add, stash, promote, replay still operational.
- [ ] Benchmarks run & history persists (if logic retained in partials).
- [ ] Mobile fullscreen controller still initializes.

## Follow-Up (Optional Future Work)
- Complete migration of any remaining benchmark/diff logic into a dedicated partial if still in Razor after this pass.
- Introduce an interface for `IEmulatorHost` to allow DI and easier testing.
- Unit tests for Corruptor domain size detection & blast layering.

---
End of plan.
