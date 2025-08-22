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

- Wire Corruptor & Glitch Harvester to Emulator
  - [ ] Replace page handlers with: `emu.BlastAsync()`, `emu.ToggleAutoCorrupt()`, `emu.SetBlastTypePublic(t)`, `emu.LetItRipPublic()`.
  - [ ] Replace GH handlers with: `emu.GhAddBase()`, `emu.GhOnBaseChangedPublic(e)`, `emu.GhLoadSelected()`, `emu.GhDeleteSelected()`, `emu.GhCorruptAndStashAsync()`, `emu.GhClearStashPublic()`, `emu.GhReplayEntryAsync(e, fromStockpile)`, `emu.GhPromote(e)`, `emu.Gh*Rename*`, `emu.GhDeleteStash(id)`, `emu.GhDeleteStock(id)`.

- Wire shader/cores/fullscreen/scale/audio to Emulator
  - [ ] Use `emu.SetShaderPublic`, `emu.SetCpuCorePublic`, `emu.SetPpuCorePublic`, `emu.SetApuCorePublic`.
  - [ ] Use `emu.ToggleFullscreenPublic()`, `emu.SetScalePublic(...)`.
  - [ ] Use `emu.ToggleSoundFontModePublic()`, `emu.ToggleSampleFontPublic()`, `emu.ToggleSoundFontLayeringPublic()`, `emu.ToggleSfDevLoggingPublic()`, `emu.ToggleSfOverlayPublic()`, `emu.FlushSoundFontPublic()`, `emu.ShowSfDebugPublic()`.
  - [ ] Bind Event Scheduler to `emu.EventSchedulerOn`.

- JSInvokable routing cleanup
  - [ ] Move `[JSInvokable]` methods `JsSaveState`, `JsLoadState`, `JsResetGame`, `OnRomsDropped`, `JsSetMobileFsView` into `Emulator` and update JS to call the emulator reference (`nesInterop.setMainRef`).
  - [ ] Remove their duplicates from `Nes.razor`.

## Validation
- [ ] Build succeeds.
- [ ] Run: ROM load/upload/delete, save/load state, corruptor/GH flows, benchmarks UI, fullscreen/touch controller, shader/core toggles.

## Notes/assumptions
- Markup stays unchanged; only code-behind behavior moves to `Emulator`.
- If a binding needs a new public surface, add a small wrapper in `Emulator.PublicApi.cs`.
