# Nes.razor → Emulator Refactor Worksheet

Goal: Eliminate remaining duplicated / UI-hosted emulator logic in `Pages/Nes.razor` by migrating it into the `Emulator` partial class set (and/or smaller focused services), leaving the Razor file as a thin view that only binds to `Emulator` public API.

Legend:
- [ ] = not started
- [~] = in progress / blocked
- [x] = done (strike through when completed)
- QS = Quick Sweep candidate (should be very small / low‑risk)

Keep batches SMALL (each item should be reviewable in < ~5–10 mins). Execute in numeric order (higher numbers assume earlier cleanup).

---
## Quick Wins / Low Risk
1. [x] QS Remove duplicate FrameTick / RunFrame in `Nes.razor` (already implemented in `Emulator`). Replace JS registration to point only to `Emulator.FrameTick`; delete local copies.
2. [ ] QS Replace local Start/Pause/Reset wrappers (`StartEmulation`, `PauseEmulation`, `ResetEmulation`) with direct calls to `emu.StartAsync()`, `emu.PauseAsync()`, `emu.ResetAsyncFacade()`; then delete local wrapper methods.
3. [ ] QS Remove local shader option refresh & registration (`RegisterShadersFromCSharp`, `RefreshShaderOptions`, `SetShader`) now duplicated in `Emulator`; call `emu.SetShaderPublic` + rely on initialization inside `Emulator.Initialize`.
4. [ ] QS Delete duplicated ROM loading helpers in `Nes.razor` (`LoadRomFromServer`, `LoadRomFromWwwroot`, `LoadSelectedRom`, `LoadRomUpload`, `LoadRomFile`, `ReloadCurrentRom`, `DeleteRom`, `ClearAllUploaded`, `TriggerFileDialog`) after exposing minimal public API on `Emulator` (e.g. `LoadUploadedRomsAsync`, `ImportRomsAsync`, `DeleteRomAsync`, `ReloadCurrentRomPublic`). Update UI bindings.
5. [ ] QS Remove duplicated memory domain building (`BuildMemoryDomains`) from `Nes.razor` (exists in `Emulator`); call a public `emu.RebuildMemoryDomains()` (to expose) if UI needs manual rebuild.
6. [ ] QS Consolidate APU/CPU/PPU core change handlers: Replace `OnCpuCoreChanged/OnPpuCoreChanged/OnApuCoreChanged` with direct calls to existing `emu.Set*CorePublic` from change events; delete local methods.
7. [ ] QS Replace local `OnShaderSelectChanged` with binding to `emu.SetShaderPublic`; delete method.
8. [ ] QS Eliminate local SoundFont toggle methods (`OnSoundFontModeChanged`, `OnSoundFontLayeringChanged`, `ToggleSampleFont`, `ToggleSfDevLogging`, `ToggleSfOverlay`, `FlushSoundFont`, `ShowSfDebug`) by wiring UI directly to `Emulator` public API (already present). Remove methods.
9. [ ] QS Replace local event scheduler handler (`OnEventSchedulerChanged`) with direct two-way binding to `emu.EventSchedulerOn`.
10. [ ] QS Remove duplicated benchmark subsystem code blocks remaining in `Nes.razor` (`bench*` fields & methods) since full implementation exists in `Benchmark.cs`; expose any missing public surface if needed, then delete locals.
11. [ ] QS Remove duplicated comparison / timeline logic (diff rows, tooltip, hover state) now in `Benchmark.cs` & `Emulator.PublicApi`.
12. [ ] QS Replace local Glitch Harvester & RTC handlers (`Gh*` methods, `Blast`, `LetItRip`, `OnBlastTypeChanged`, `ToggleAutoCorruptButton`) with calls to public API: add any missing wrappers (e.g. `emu.GhReplayEntryAsync`, `emu.BlastAsync`, `emu.LetItRipPublic`). Then remove local methods.
13. [ ] QS Remove `UpdateInput` duplication (already handled by `Emulator.UpdateInput`). Ensure JS side references only `emu` DotNetObjectReference.
14. [ ] QS Remove state persistence duplicates (`SaveState`, `LoadState`, `DumpState`, chunk/compress helpers) once equivalent implemented inside `Emulator` (verify or add). Keep only calls to `emu.SaveStateAsyncPublic` etc.
15. [ ] QS Migrate mobile fullscreen view logic: remove `mobileFsView`, `touchControllerInitialized`, `SetMobileFsView` and related JS invokable duplicates since `UI.cs` contains them.

## Medium Complexity
16. [ ] Extract any remaining direct field access (e.g. `nesController.framebuffer`, `nesController.inputState`) from the view: expose read-only projections or small dispatcher methods on `Emulator` and update Razor to use them. Remove implicit access.
17. [ ] Introduce a slim view-model interface (`IEmulatorViewModel`) implemented by `Emulator` to formalize what the Razor component consumes (stabilizes public surface, eases testability). Update `Nes.razor` to depend on the interface only.
18. [ ] Move constants (`SaveKey`, `SaveChunkCharSize`) into `Emulator` (already) then delete duplicates from `Nes.razor`; replace any inlined strings with central usage.
19. [ ] Encapsulate ROM table click/drag-drop logic: provide `emu.ImportDroppedRomsAsync(UploadedRom[])` and route JS interop directly there. Remove `[JSInvokable] OnRomsDropped` duplication if moved.
20. [ ] Unify savestate baseline benchmark load path: ensure `TryLoadBaselineStateForBenchmarks` exists only once (currently duplicated); delete `Nes.razor` copy.
21. [ ] Provide single source of truth for corruption memory domain selection; migrate domain size probing helpers and remove duplicates (`GetApproxSize`).
22. [ ] Wrap all direct JS interop calls with narrow internal methods on `Emulator` (e.g. `PresentFrameAsync`, `InitTouchControllerAsync`) to centralize error handling & logging; update view to call none directly (except maybe extremely simple toggles).
23. [ ] Collapse multiple `[JSInvokable]` attributes in `Nes.razor` into `Emulator` (mobile FS actions, save/load/reset). Remove from view.
24. [ ] Add unit tests for `Emulator` public API covering: ROM load, core switching, benchmark run, corruption toggle, savestate round-trip (logic-level only, JS calls mocked).

## Higher Complexity / Final Cleanup
25. [ ] Remove direct `nesController` alias in `Nes.razor`; instead reference `emu.Controller` only where unavoidable (or better, add view-model properties). Then delete alias property.
26. [ ] Remove direct `corruptor` alias similarly.
27. [ ] Create dedicated component(s) for: ROM Manager panel, Corruptor panel, Glitch Harvester panel, Benchmark modals. Each receives `Emulator` (or interface) as a cascading parameter. Shrink `Nes.razor` markup length drastically.
28. [ ] Introduce a state change event aggregator (or simple `Action` callbacks) for sub-panels to request emulator actions, reducing `Nes.razor` plumbing code.
29. [ ] Implement lazy loading / virtualization for large benchmark history or stockpile lists (opt-in improvement; verify perf need first).
30. [ ] Replace direct `JS.InvokeVoidAsync("eval", ...)` usage with typed JS interop wrappers registered in `wwwroot/nesInterop.js` (removes eval usage, improves analyzability & CSP compatibility).
31. [ ] Formalize save-state format versioning inside `Emulator` (manifest JSON upgrades, backward compat) and drop any format-handling logic from Razor.
32. [ ] Add cancellation / throttling for high-frequency UI update paths (FPS/stat refresh) within `Emulator`, exposing rate-limited observable properties.
33. [ ] Remove any residual comments referencing "ORIGINAL" or migration notes from `Nes.razor` after parity verification; retain concise top summary comment only.
34. [ ] Final pass: ensure `Nes.razor` contains zero C# logic except parameter wiring and minimal event handler lambdas calling `emu.*`. Document in `core-lifecycle.md`.
35. [ ] Delete now-unused helper methods / dead code flagged by IDE after completion; run static analysis to confirm no duplicate logic remains.
36. [ ] Add a regression checklist to README (or docs) describing emulator lifecycle responsibility boundaries post-refactor.

## Verification Steps (Apply Iteratively)
- Build passes (`dotnet build -c Release`).
- Launch and verify: ROM load, core switches, corruption, benchmarks, savestate load/save, fullscreen toggle, touch controller, shader selection.
- Search codebase for removed symbol names to confirm elimination (e.g. `RunFrame(` in `Nes.razor`).
- Ensure no public API drift broke existing UI markup.

## Deferred / Nice-to-Have (Track separately if scope grows)
- DI register `Emulator` as a scoped service & convert `Nes.razor` to inject it (instead of per-page creation) enabling cross-route persistence.
- Telemetry hooks (perf counters) emitted via an optional observer.
- Headless test harness for savestate compression chunk logic.

---

Progress Notes:
(Record date, item numbers completed, reviewer, and any follow-up tasks.)

- 2025-08-20: Worksheet created.
