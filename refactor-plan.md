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
- [x] Instantiate `Emulator` inside `Nes.razor` with dependencies (ILogger, IJSRuntime, HttpClient, StatusService, IShaderProvider, NavigationManager).
- [x] Replace direct controller / corruptor fields with delegating properties (`nesController`, `corruptor`).
- [ ] Expose / alias benchmark & UI state via emulator getters (partially done; finish when swapping bindings).
- [ ] Delegate core control handlers (StartEmulation, PauseEmulation, ResetEmulation, SaveState, LoadState, DumpState) to emulator public API.
- [ ] Delegate shader/core selection & soundfont toggles to emulator.
- [ ] Delegate benchmark modal actions & state; remove duplicate benchmark fields/methods.
- [ ] Delegate corruptor & Glitch Harvester methods; prune duplicates.
- [ ] Remove duplicated save/load (chunked) implementation in Razor.
- [ ] Remove utility helpers now duplicated (compression, FormatSize, ExtractInt) once unused.
- [ ] Confirm all JSInvokable methods exist only on emulator and are registered; remove Razor copies.
- [ ] Purge obsolete navigation & disposal code replaced by emulator.
- [ ] Delete unused private fields and trim using directives.
- [ ] Build & manual test after each delegation batch.

### Detailed Delegation Breakdown (Upcoming Batches)
Batch A (Core Control / Loop):
- Replace body of `StartEmulation` with call to `emu.StartAsync()` (if method name differs, add wrapper in Emulator) and remove RAF loop JS calls from Razor (handled in emulator).
- Replace `PauseEmulation` with `emu.PauseAsync()`; remove manual SoundFont flush (emulator handles / add if missing).
- Replace `ResetEmulation` logic with `emu.HardResetAsync()` (expose if not present) or a new `ResetAsync()` that encapsulates pause, ROM reload, corruption disable, core reapply, resume.
- Update `JsResetGame` to call emulator public method; remove local reset function after bindings updated.
- Remove local frame scheduling / `[JSInvokable] FrameTick` (if still present) post verification.

Batch B (State Persistence):
- Replace `SaveState` body with `await emu.SaveStateAsyncPublic()`.
- Replace `LoadState` body with `await emu.LoadStateAsyncPublic()`.
- Replace `DumpState` with `emu.DumpStateAsyncPublic()` and expose `emu.DebugDumpText` for UI binding.
- Remove chunking/compression helpers (CompressString, DecompressString, RemoveExistingChunks, ExtractInt, constants like `SaveChunkCharSize`, `SaveKey`) once Razor no longer references them (verify equivalents exist in `StatePersistence.cs`).
- Update `JsSaveState` / `JsLoadState` to delegate to emulator public methods.

Batch C (Benchmarks):
- Swap all uses of `benchRunning`, `benchModalOpen`, `benchResultsText`, `benchWeight`, `benchAutoLoadState`, `benchSimple5x`, history and diff collections to emulator public projections (`emu.BenchRunning`, etc.).
- Replace `OpenBenchModal`, `RunBenchmarks`, `RunBenchmarks5x`, `CloseBench*`, compare modal handlers, diff animation, history edit/delete methods with emulator wrappers (`OpenBenchmarks`, `RunBenchmarksAsync`, `RunBenchmarks5xAsync`, etc.).
- Remove benchmark local fields and methods after markup updated.

Batch D (Corruptor & Glitch Harvester):
- Replace `Blast`, `LetItRip`, auto-corrupt toggle, intensity / blast type setters with emulator public surface (`emu.BlastAsync()`, `emu.LetItRipPublic()`, `emu.ToggleAutoCorrupt()`, property accessors).
- Replace GH methods (GhAddBaseState, GhCorruptAndStash, GhReplayEntry, rename/edit flows) with emulator `Gh*Public` counterparts.
- Remove local corruptor/GH fields/methods after bindings swapped.

Batch E (Cores / Shader / Audio / View):
- Swap core selection handlers with `emu.SetCpuCorePublic`, `emu.SetPpuCorePublic`, `emu.SetApuCorePublic`.
- Swap shader selection with `emu.SetShaderPublic` and remove local persistence JS calls.
- Replace fullscreen toggle with `emu.ToggleFullscreenPublic`.
- Replace scale change logic with `emu.SetScalePublic`.
- Replace SoundFont toggles & debug actions with `emu.ToggleSoundFontModePublic`, `emu.ToggleSampleFontPublic`, `emu.ToggleSoundFontLayeringPublic`, etc.
- Remove local soundfont state fields (soundFontMode, sampleFont, layering, overlay, logging) after swap.

Batch F (Misc / Cleanup):
- Remove navigation/location change handlers superseded by emulator.
- Ensure only one `DotNetObjectReference` (in emulator) and remove `_selfRef` usage from Razor if redundant.
- Remove remaining utility methods replaced by emulator (BuildMemoryDomains, ApplySelectedCores, SetApuCoreSelFromEmu) if fully internalized.
- Final pass to trim usings and leftover comments.

Execution Order Rationale: Start with least disruptive (Batch A) to stabilize core loop delegation, then persistence (B) to consolidate state logic, proceed to benchmarks and corruptor which have broader field usage, finishing with audio/view and cleanup.

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
Progress Note: Step 1 complete (Emulator integrated). Beginning Step 2 (delegations & pruning) with a batch approach: control handlers -> persistence -> benchmarks -> corruptor -> utilities cleanup.

End of plan (updated).
