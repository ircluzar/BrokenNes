# Final Nes.razor Refactor Plan

Goal: Finish extracting logic from `Pages/Nes.razor` into the existing C# classes (`Emulator` partials, `NesController`, `Corruptor`, state/benchmark partials) while preserving ALL existing UI markup and keeping runtime behavior identical. Only the `@code` block contents still duplicated / not yet using the extracted classes should be removed or reduced to a very thin glue layer that delegates into `Emulator`.

## Principles
- Do NOT alter Razor markup (HTML/structure, element ids, classes) to avoid UI regressions.
- Keep public / JSInvokable method names that JS expects; if moved, ensure they remain `[JSInvokable]` and accessible (e.g., via `Emulator` instance reference injected / created in the page and registered with JS as before).
- Avoid duplication: eliminate code that already lives in `Emulator`, `StatePersistence`, `UI`, `Benchmark` (if exists) etc.
- Minimize surface in `Nes.razor` to: creating / owning a single `Emulator` instance, wiring `OnStateChanged` to call `StateHasChanged`, exposing a few property pass‑throughs used in bindings.
- Preserve existing local fields ONLY if they are pure view-model state not yet migrated. Identify any such leftovers and migrate if reasonable.

## Inventory: Large Functional Areas in Nes.razor @code
1. ✅ Initialization & lifecycle (OnInitialized, OnAfterRenderAsync, navigation dispose) – COMPLETED: migrated to `Emulator.Initialize()` + `Emulator.OnAfterRenderAsync` + `Dispose`.
2. ✅ ROM management (upload, delete, search, size formatting) – COMPLETED: core logic exists in `NesController`; UI delegates to emulator public methods like `LoadSelectedRomPublic()`, `DeleteRomPublic()`, `OnRomRowClickedPublic()`, etc.
3. ✅ Emulation control (Start/Pause/Reset, FrameTick loop, scaling, fullscreen) – COMPLETED: now in `Emulator` control partial. Razor delegates via `emu.StartAsync()`, `emu.PauseAsync()`, `emu.ResetAsyncFacade()`.
4. ✅ Save/Load/Chunked state persistence – COMPLETED: logic moved to `StatePersistence.cs`; Razor calls `emulator.SaveStateAsyncPublic()/LoadStateAsyncPublic()/DumpStateAsyncPublic()`.
5. ✅ Corruptor / Glitch Harvester (Blast, LetItRip, GH functions) – COMPLETED: logic in `Corruptor` class and `GlitchHarvester.cs` partial. UI delegates to methods like `emu.BlastAsync()`, `emu.GhCorruptAndStashAsync()`, etc.
6. ✅ Benchmark system (modal state, history, comparison graph, diff animation) – COMPLETED: migrated to `Benchmark.cs` partial. UI uses emulator properties like `emu.BenchRunning`, `emu.BenchModalOpen` and methods like `emu.RunBenchmarksAsync()`.
7. ✅ SoundFont / audio mode toggles – COMPLETED: logic in `Emulator`. Page routes events to methods like `emu.ToggleSoundFontModePublic()`, `emu.ToggleSampleFontPublic()`, etc.
8. ✅ Mobile fullscreen view toggles – COMPLETED: handled in `UI.cs` partial; UI accesses via `emu.UI.MobileFullscreenView` and methods like `emu.UI.ViewController()`.
9. ✅ Utilities (FormatSize, ExtractInt, compression helpers) – COMPLETED: present in partials; UI uses static methods like `NesController.FormatSize()`.

## Remaining Actions
- [x] ✅ Instantiate `Emulator` inside `Nes.razor` with dependencies (ILogger, IJSRuntime, HttpClient, StatusService, IShaderProvider, NavigationManager).
- [x] ✅ Replace direct controller / corruptor fields with delegating properties (`nesController`, `corruptor`).
- [x] ✅ Delegate core control handlers (Start/Pause/Reset) to emulator (Batch A complete – wrappers now call `emu.StartAsync()`, `emu.PauseAsync()`, `emu.ResetAsyncFacade()`).
- [x] ✅ Expose / alias benchmark & UI state via emulator getters (COMPLETED: properties like `emu.BenchRunning`, `emu.UI.MobileFullscreenView` are used throughout UI).
- [x] ✅ Delegate state persistence handlers (SaveState, LoadState, DumpState + JS wrappers) to emulator (COMPLETED: wrappers call `emulator.SaveStateAsyncPublic()`, etc.).
- [x] ✅ Delegate shader/core selection, fullscreen, scale & soundfont toggles to emulator.
- [x] ✅ Delegate benchmark modal actions & state; remove duplicate benchmark fields/methods.
- [x] ✅ Delegate corruptor & Glitch Harvester methods; prune duplicates.
- [x] ✅ Remove duplicated save/load (chunked) implementation & helpers from Razor once no references remain.
- [x] ✅ Remove remaining utility helpers now duplicated (compression, ExtractInt) once unused.
- [x] ✅ Confirm all JSInvokable methods exist only on emulator and are registered; remove Razor copies.
- [x] ✅ Purge obsolete navigation & disposal code replaced by emulator.
- [x] ✅ Delete unused private fields and trim using directives.
- [x] ✅ Build & manual test after each delegation batch.

### Detailed Delegation Breakdown (Upcoming Batches)
✅ Batch A (Core Control / Loop) [COMPLETED]:
- ✅ Delegated Start/Pause/Reset to emulator. Full reset logic moved into new `Emulator.ResetAsync` (Control partial). Page now only calls wrappers.
- ✅ Frame loop & input JS callbacks entirely migrated to emulator (`FrameTick`/`UpdateInput` are `[JSInvokable]` methods on Emulator).

✅ Batch B (State Persistence & Ancillary Safe Delegations) [COMPLETED]:
- ✅ Step 1: Wrapped `SaveState`, `LoadState`, `DumpState`, `JsSaveState`, `JsLoadState` to call emulator public API.
- ✅ Step 2: Removed local persistence helpers/constants after benchmark migration completed.
- ✅ Step 3: UI dump binding now uses `emu.DebugDumpText` and dropped local `debugDump` field.

✅ Batch C (Benchmarks) [COMPLETED]:
- ✅ Swapped all uses of benchmark modal booleans, history, comparison data to emulator public projections (`emu.BenchRunning`, `emu.BenchModalOpen`, `emu.BenchHistory`, etc.).
- ✅ Replaced all benchmark modal handlers with emulator wrappers (`emu.OpenBenchmarks()`, `emu.RunBenchmarksAsync()`, `emu.RunBenchmarks5xAsync()`, etc.).
- ✅ Removed benchmark local fields and methods after markup updated.

✅ Batch D (Corruptor & Glitch Harvester) [COMPLETED]:
- ✅ Replaced `Blast`, `LetItRip`, auto-corrupt toggle, intensity / blast type setters with emulator public surface (`emu.BlastAsync()`, `emu.LetItRipPublic()`, `emu.ToggleAutoCorrupt()`, property accessors).
- ✅ Replaced GH methods (GhAddBaseState, GhCorruptAndStash, GhReplayEntry, rename/edit flows) with emulator `Gh*Public` counterparts.
- ✅ Removed local corruptor/GH fields/methods after bindings swapped.

✅ Batch E (Cores / Shader / Audio / View) [COMPLETED]:
✅ Completed delegation of core (CPU/PPU/APU) selection, shader selection, fullscreen toggle, scale setter, soundfont toggles & overlay/logging, plus event scheduler toggle. Removed local soundfont fields & debug dump duplication.

✅ Batch F (ROM Management) [COMPLETED]:
- ✅ Delegated ROM upload, delete, search, selection, and drag-drop operations to emulator public methods.
- ✅ UI now uses `emu.FilteredRomOptionsPublic`, `emu.OnRomRowClickedPublic()`, `emu.DeleteRomPublic()`, etc.
- ✅ Removed duplicate ROM management logic from Razor.

✅ Batch G (Misc / Cleanup) [COMPLETED]:
- ✅ Removed navigation/location change handlers superseded by emulator.
- ✅ Only one `DotNetObjectReference` exists (in emulator); removed `_selfRef` usage from Razor.
- ✅ Removed remaining utility methods replaced by emulator.
- ✅ Final pass completed to trim usings and leftover comments.

## Mapping of Razor Methods -> Emulator / Controller
| Razor Method | New Location | Action | Status |
|--------------|--------------|--------|---------|
| StartEmulation | Emulator.StartAsync | wrapper call | ✅ COMPLETED |
| PauseEmulation | Emulator.PauseAsync | wrapper | ✅ COMPLETED |
| ResetEmulation | Emulator.ResetAsyncFacade | wrapper call | ✅ COMPLETED |
| SaveState / LoadState | Emulator.SaveStateAsyncPublic / LoadStateAsyncPublic | wrapper | ✅ COMPLETED |
| DumpState | Emulator.DumpStateAsyncPublic | wrapper | ✅ COMPLETED |
| FrameTick (JS) | Emulator.FrameTick | JSInvokable method | ✅ COMPLETED |
| UpdateInput (JS) | Emulator.UpdateInput | JSInvokable method | ✅ COMPLETED |
| Blast / LetItRip | Emulator.BlastAsync / LetItRipPublic | wrappers | ✅ COMPLETED |
| Gh* methods | Emulator.Gh*Public methods | wrappers | ✅ COMPLETED |
| ROM management | Emulator ROM public methods | wrappers | ✅ COMPLETED |
| Benchmark operations | Emulator.RunBenchmarksAsync, etc. | wrappers | ✅ COMPLETED |

## Exposure Additions (Completed)
✅ Added public getters in `Emulator` partials for:
- ✅ `Controller` (already public) alias `nesController` variable in Razor.
- ✅ `Corruptor` (already public) alias `corruptor` variable.
- ✅ SoundFont flags & toggles via forwarding methods.
- ✅ Benchmark modal booleans / methods via public properties and methods.
- ✅ UI state via `UI` property returning `this` (Emulator).

## Step-by-Step Refactor Execution (Completed)
✅ 1. Add this plan file (done).
✅ 2. Introduce a greatly simplified `@code` block in `Nes.razor` creating `Emulator emu; NesController nesController => emu.Controller; Corruptor corruptor => emu.Corruptor;` plus wrapper event handlers.
✅ 3. Remove massive original `@code` block content.
✅ 4. Compile; fix missing symbol references by exposing public members in `Emulator` if necessary.
✅ 5. Test: build, run; verify UI still loads, ROM loads, play/pause, load/upload, corruptor, GH, save/load, benchmark open.
✅ 6. Clean up any obsolete using directives.

## Non-Goals (This Pass)
- Moving benchmark graphing logic if not already extracted (only remove if safely duplicated elsewhere).
- Large-scale renaming of methods or variables in markup.
- Architectural redesign of `Emulator` (aim is completion of extraction, not overhaul).

## Validation Checklist After Change (Completed)
- ✅ Build succeeds with no new warnings/errors related to removed code.
- ✅ JS interactions (keyboard, frame loop, fullscreen, drag/drop) still functional.
- ✅ ROM upload/delete operations function (delegating to controller logic).
- ✅ Save/Load state works (calls into `StatePersistence`).
- ✅ Corruptor auto & manual blast updates LastBlastInfo overlay.
- ✅ Glitch Harvester base add, stash, promote, replay still operational.
- ✅ Benchmarks run & history persists (logic in `Benchmark.cs` partial).
- ✅ Mobile fullscreen controller still initializes.

## Follow-Up (Optional Future Work)
- ✅ Complete migration of benchmark/diff logic into `Benchmark.cs` partial.
- Introduce an interface for `IEmulatorHost` to allow DI and easier testing.
- Unit tests for Corruptor domain size detection & blast layering.

---
## REFACTOR STATUS: ✅ COMPLETED

**Summary:** The refactor has been successfully completed. The original massive `Nes.razor` @code block has been completely replaced with a minimal initialization that creates an `Emulator` instance and delegates all functionality through it. All major subsystems have been properly extracted into partials:

- **Control & Lifecycle:** `Emulator.cs` + `Emulator.Control.cs` 
- **State Persistence:** `StatePersistence.cs`
- **Benchmarks:** `Benchmark.cs`
- **Corruptor & Glitch Harvester:** `Corruptor.cs` + `GlitchHarvester.cs`
- **UI & Mobile Views:** `UI.cs`
- **Public API:** `Emulator.PublicApi.cs`

The final `Nes.razor` @code block is now only ~20 lines, containing just:
- Emulator instance creation with dependency injection
- Initialization and lifecycle delegation 
- State change event wiring

All original functionality is preserved while achieving the goal of extracting complex logic from the Razor page into maintainable, testable C# classes.
