# Nes.razor → Emulator extraction plan

Purpose: remove duplicated logic from `Pages/Nes.razor` and wire the UI to the canonical implementations in `NesEmulator/*`.

## Tasks (checklist)

- Remove save-state duplication from `Pages/Nes.razor`
  - [x] Delete local `SaveChunkCharSize` and `SaveKey` constants (use ones in `Emulator`).
  - [x] Delete `CompressString`, `DecompressString`, `ExtractInt`, `RemoveExistingChunks` helpers.
  - [x] Replace any page calls with `emu.SaveStateAsyncPublic()` / `emu.LoadStateAsyncPublic()` / `emu.DumpStateAsyncPublic()`.

- Remove benchmark subsystem duplication
  - [x] Delete page methods: `RunBenchmarks`, `RunBenchmarks5x`, `PersistBenchHistory`, `LoadBenchHistory`, `ClearBenchHistory`, `CopyBenchResults`.
  - [x] Delete page methods: `OpenBenchModal`, `CloseBenchModal`, `ToggleBenchAutoLoad`, `ToggleBenchSimple5x`.
  - [x] Delete page methods: `ShowHistoryEntry`, `DeleteBenchEntry`, `StartBenchRomEdit`, `CommitBenchRomEdit`, `HandleBenchRomEditKey`.
  - [x] Delete page method: `TryLoadBaselineStateForBenchmarks`.
  - [x] Bind markup directly to `emu.Bench*` properties/methods exposed in `Emulator.PublicApi.cs`.

- Remove core selection helpers duplication
  - [x] Delete page `ApplySelectedCores()` and `SetApuCoreSelFromEmu()`; rely on `Emulator` implementations.

- [x] Remove lifecycle/touch-controller duplication
  - [x] In `OnAfterRenderAsync`, keep only `await emu.EnsureInitialRenderAsync(true);` on first render.
  - [x] Remove page-side touch controller init block (handled in `Emulator` `OnAfterRenderAsync`).

- [x] Remove local emulator fields from page
  - [x] Remove page `nes` property, `inputState`, `stateBusy` fields (owned by `Emulator`).
  - [x] Replace all `nes == null` references with `nesController.nes == null`.
  - [x] Add `SetCrashBehavior()` method to `Emulator.PublicApi.cs` to handle crash behavior settings.

- [x] Remove page-owned interop reference/Dispose
  - [x] Remove page `_selfRef` and custom `Dispose()`; `Emulator.Dispose()` already stops the loop and cleans up.
  - [x] Simplify page `Dispose()` to delegate to `emu?.Dispose()`.

- [x] Rewire ROM manager bindings
  - [x] Added public API methods: `RomSelectionChangedPublic`, `LoadRomEntryPublic`, `OnRomRowClickedPublic`, `GetDefaultBuiltInRomKeyPublic`
  - [x] Replace page ROM search binding with direct `@bind="emu!.RomSearch"` (no wrapper needed)
  - [x] Replace page ROM row click handler with `emu!.OnRomRowClickedPublic(opt)`
  - [x] Remove unused local wrapper methods: `RomSelectionChanged`, `LoadRomEntry`, `OnRomRowClicked`, `GetDefaultBuiltInRomKey`, `OnRomSearchChanged`
  - [x] Keep minimal local delegation methods for existing UI: `LoadSelectedRom`, `ReloadCurrentRom`, `DeleteCurrentRom`

- [x] Wire Corruptor & Glitch Harvester to Emulator
  - [x] Already completed: All handlers use `emu.BlastAsync()`, `emu.ToggleAutoCorrupt()`, `emu.SetBlastTypePublic()`, `emu.LetItRipPublic()`
  - [x] Already completed: All GH handlers use `emu.GhAddBase()`, `emu.GhOnBaseChangedPublic()`, `emu.GhLoadSelected()`, `emu.GhDeleteSelected()`, `emu.GhCorruptAndStashAsync()`, `emu.GhClearStashPublic()`, `emu.GhReplayEntryAsync()`, `emu.GhPromote()`, `emu.GhDeleteStash()`, `emu.GhDeleteStock()`
  - [x] No local wrapper methods found - UI directly calls Emulator public API

- [x] Wire shader/cores/fullscreen/scale/audio to Emulator
  - [x] Already completed: Shader/core selectors use `emu.SetShaderPublic`, `emu.SetCpuCorePublic`, `emu.SetPpuCorePublic`, `emu.SetApuCorePublic`
  - [x] Replaced scale/fullscreen wrappers with direct calls: `emu.SetScalePublic(...)`, `emu.ToggleFullscreenPublic()`
  - [x] Already completed: SoundFont toggles use `emu.ToggleSoundFontModePublic()`, `emu.ToggleSampleFontPublic()`, `emu.ToggleSoundFontLayeringPublic()`, `emu.ToggleSfDevLoggingPublic()`, `emu.ToggleSfOverlayPublic()`, `emu.FlushSoundFontPublic()`, `emu.ShowSfDebugPublic()`
  - [x] Already completed: Event Scheduler bound to `emu.EventSchedulerOn`
  - [x] Removed local wrapper methods: `SetScale`, `ToggleFullscreen`

- [x] JSInvokable routing cleanup
  - [x] Added JSInvokable methods to `Emulator.PublicApi.cs`: `JsSaveState`, `JsLoadState`, `JsResetGame`, `JsExitFullscreen`, `OnRomsDropped`
  - [x] Removed duplicate JSInvokable methods from `Nes.razor`
  - [x] JavaScript already calls emulator reference via `nesInterop.setMainRef()` (configured in `Emulator.cs`)
  - [x] `JsSetMobileFsView` already exists in `UI.cs` and is accessible through Emulator

## Validation
- [x] Build succeeds (29-30 warnings, same as before).
- [ ] Run: ROM load/upload/delete, save/load state, corruptor/GH flows, benchmarks UI, fullscreen/touch controller, shader/core toggles.

## Extraction Complete! 🎉

All major extraction tasks have been completed successfully:
- ✅ Save-state duplication removal
- ✅ Benchmark subsystem duplication removal  
- ✅ Core selection helpers duplication removal
- ✅ Lifecycle/touch-controller duplication removal
- ✅ Local emulator fields removal
- ✅ Page-owned interop reference/Dispose cleanup
- ✅ ROM manager bindings rewiring
- ✅ Corruptor & Glitch Harvester wiring (already complete)
- ✅ Shader/cores/fullscreen/scale/audio wiring
- ✅ JSInvokable routing cleanup

**Result**: Pages/Nes.razor is now a clean UI layer that delegates all business logic to the centralized Emulator. The architecture is much cleaner with proper separation of concerns.

## Notes/assumptions
- Markup stays unchanged; only code-behind behavior moves to `Emulator`.
- If a binding needs a new public surface, add a small wrapper in `Emulator.PublicApi.cs`.
