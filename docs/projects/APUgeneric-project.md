# Project Charter: Generic APU Registration (no hardcoded core mentions)

## Purpose
Make APU cores fully plugin-based. Adding a new .cs file that implements `IAPU` must be sufficient to discover, select, persist, and restore the core—no code edits elsewhere. The only exception is FMC, which remains the default core and is always included.

## Goals
- Zero hardcoded APU core names/enum switches in UI and emulator layers.
- Selection and persistence use stable string IDs from `CoreRegistry` (e.g., `APU_FMC`).
- Reset/ROM load/state save/restore preserve the active APU core by ID.
- FMC is the guaranteed default when no preference/state is present.

## Non-goals
- Changing the PPU/CPU generic systems (already in place).
- Altering APU DSP behavior; this is plumbing-only.

## Definitions
- APU Core ID: `APU_<Suffix>` (example: `APU_FMC`, `APU_FIX`, `APU_QN`).
- Default core: `FMC` (always built, always available).
- Public API for selection: `NES.SetApuCore(string suffixId)` and `NES.GetApuCoreId()`.

## Success criteria
- Add a new APU (new `IAPU` class) and see it listed/selectable with no other code changes.
- All UI operations (boot, start/stop, reset, ROM reload, save/load state) keep the selected APU.
- No direct references to specific APU cores in UI logic except comparing against suffix for FMC-derived flags.
- No usage of `NES.ApuCore` enum in app/UI layers; the enum path is deprecated internally.

## High-level design
- Discovery: `CoreRegistry` enumerates APU types via reflection and exposes IDs/suffixes.
- Selection: UI stores a suffix (e.g., `FMC`) in preferences. `NES.SetApuCore(string)` resolves via `CoreRegistry` and hot-swaps using shared state.
- Default: On emulator init or preference miss, force `FMC` via string path.
- Back-compat: Enum-based APIs remain temporarily with `[Obsolete]` and internally redirect to string IDs.

## Work plan (checklist)
- Inventory hardcoded references
  - [ ] Search for `NES.ApuCore` enum usage and string literals `"FMC"|"FIX"|"QN"` across repo.
  - [ ] List all fallbacks/switches that map UI values to enum calls.
- UI (`Pages/Nes.razor`)
  - [ ] Remove explicit APU mapping in `ResetEmulation()` (e.g., lines around the block setting `Jank/Modern/QuickNes`).
  - [ ] In `ApplySelectedCores()`, drop legacy enum fallback for APU; rely on `nes.SetApuCore(string)` return value only.
  - [ ] In `OnApuCoreChanged(...)`, remove switch to enum; call `nes.SetApuCore(v)` and handle failure generically.
  - [ ] Keep `famicloneOn = (suffix == "FMC")` logic (no hardcoded mapping beyond this equality check).
- Emulator API
  - [ ] Mark `NES.ApuCore` enum and any `SetApuCore(NES.ApuCore)` as `[Obsolete("Use SetApuCore(string)")]`.
  - [ ] Ensure `NES.SetApuCore(string)` and `Bus.SetApuCoreById(string)` are the single selection paths.
  - [ ] Verify `HardResetAPUs` and caches are keyed by ID, not by concrete types or enum.
- Save/Load & Reset
  - [ ] Confirm `LoadState/SaveState` persist the APU core ID; add migration for legacy enum values if present.
  - [ ] Ensure all ROM load/reset paths call the common `ApplySelectedCores()` and never special-case APU.
- Options & persistence
  - [ ] Build APU dropdown from `GetApuCoreIds()`; sort with `FMC` first.
  - [ ] Preference storage remains the suffix (e.g., `pref_apuCore = "FMC"`).
- Tests (minimum)
  - [ ] Discovery test: adding a dummy `IAPU` is discovered in `GetApuCoreIds()`.
  - [ ] Hot-swap test: set APU by suffix, run frames, swap to another, state remains coherent.
  - [ ] Default test: empty prefs => `GetApuCoreId()` ends with `APU_FMC`.
  - [ ] State test: save with A core, load back, same core active.

## Migration plan
- Phase 1 (compat window)
  - Keep enum-based methods with `[Obsolete]`, internally mapping to suffix: `Jank -> FMC`, `Modern -> FIX`, `QuickNes -> QN`.
  - Handle legacy save states and preferences by mapping to suffix on load.
- Phase 2
  - Remove enum usage from UI/app entirely (done with this project).
  - Update docs and samples to only use string-based APIs.
- Phase 3
  - Consider removing enum APIs after one release cycle.

## Acceptance tests (manual)
- Add a new APU core file implementing `IAPU` with ID `APU_TEST`.
- Build and run: `APU_TEST` appears in the APU dropdown and is selectable.
- Select `APU_TEST`, reset, reload ROM, save state, refresh page, load state: active core remains `APU_TEST`.
- Clear preferences: FMC is selected by default.

## Known hardcoded call sites to clean
- `Pages/Nes.razor`
  - Reset path block explicitly setting `Jank/Modern/QuickNes` after ROM reload (see lines ~887–892 in current file).
  - `ApplySelectedCores()` APU legacy fallback switch.
  - `OnApuCoreChanged(...)` APU legacy fallback switch.
- Search terms to use: `NES.ApuCore`, `SetApuCore(` with enum, `FMC`/`FIX`/`QN` literals.

## Risks & mitigations
- Risk: Legacy states/preferences break – Add mapping and logs.
- Risk: FMC not present – Enforce FMC build inclusion; assert during startup.
- Risk: Ordering/UI drift – Always derive options dynamically from registry.

## Timeline (suggested)
- Day 1: Inventory + UI cleanup + default enforcement.
- Day 2: Obsolete enum path + migration glue + tests.
- Day 3: Docs update + PR review.

## Deliverables
- Code cleanup (UI + emulator API obsoletes).
- Unit tests for discovery, swap, default, state.
- Docs: this charter, README “Adding an APU core” section, template snippet.

## Communication
- Track work under issue: “APU-generic: remove hardcoded APU mentions; FMC default”.
- PRs must reference this charter and check boxes in the checklist above.

## Appendix: FMC exception
- FMC is always built and is the default. The system remains generic; FMC is treated like any other core at runtime, except:
  - FMC must always be discoverable in `CoreRegistry`.
  - If selection/prefs/state are missing or invalid, select `FMC`.
