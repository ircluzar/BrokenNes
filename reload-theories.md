# Achievements don’t trigger after reload — prioritized theories and fixes

This note lists the most likely causes and concrete fixes for why achievement conditions look green before saving, but don’t re-trigger (or appear unmonitored) after loading a save. Items are ordered by likelihood of resolving the issue.

## 1) Engine bound to an old NES instance after LoadState reconstructs NES

Symptom
- After load, the debug panel shows conditions not updating. Achievements feel “detached.”

Why
- `LoadState()` can construct a new `NES` instance when the savestate embeds `romData`:
  - In `StatePersistence.LoadState`: `nes = new NesEmulator.NES(); nes.LoadROM(romBytes); BuildMemoryDomains();`
- The achievements engine holds an `IRamDomain` built from the previous `NES` instance (via `new NesRamDomain(emu.Controller.nes)` in `Nes.razor`).
- When `nes` is replaced, the engine still reads RAM from the old `NES`, so its state no longer tracks the running emulator.

Fix
- Rebuild the achievements engine after any `NES` replacement:
  - After `LoadState()` finishes constructing/restoring `nes`, create a new `AchievementsEngine(new NesRamDomain(nes))`, reload definitions, and re-call `emu.ConfigureAchievements(...)`.
  - Then restore the snapshot (`achvState`) to the new engine.
- Implementation directions
  - Option A: move achievements initialization into `Emulator` so `LoadState()` can call a local `RebuildAchievementsEngine()` with cached defs/titles.
  - Option B: expose an event or callback from `Emulator.LoadState()` that `Nes.razor` handles to rebuild the engine and restore snapshot.

## 2) ResetIf/ResetNextIf firing immediately post-restore clears progress

Symptom
- Conditions that were green pre-save are cleared right after reload.

Why
- On the first evaluation frame after restore, a `ResetIf`/`ResetNextIf` can be true based on transient RAM, resetting hits across the group. Our engine resets when those flags evaluate true.

Fixes
- Guard the first 1–2 frames after restore from applying resets (soft-disable `ResetIf`/`ResetNextIf` once) or perform a one-frame “warm” evaluation that ignores resets before normal operation.
- Alternatively, persist a small “arming” flag in the snapshot and only allow resets after a frame has passed.

## 3) Delta/Prior (d/p) semantics need a warm frame after restore

Symptom
- Achievements using `d`/`p` comparisons don’t trigger right after load, even with the correct in-game action.

Why
- We reinitialize `_ramPrev/_ramPrior` to the current RAM on restore, so initial deltas are 0. A subsequent frame must occur to get meaningful deltas.

Fixes
- Accept the one-frame warm-up, or extend snapshot to include previous/prior RAM shadows so we can restore them (bigger snapshot, but perfect continuity).

## 4) Snapshot/Save timing mismatch (now addressed)

Symptom
- Reloading a state saved before unlocking doesn’t restore achievement progress.

Why
- Initially, the paired snapshot was only written on unlock. A pre-unlock manual save had no paired achievements snapshot.

Fix
- The code now also persists the achievements snapshot on any SaveState. Verify your repro uses a build with that change.

## 5) Snapshot key mismatch between save and load

Symptom
- Restore finds no snapshot or loads the wrong one.

Why
- `gameKey` includes `{cpu|ppu|apu}` IDs and a ROM identity. Differences across save vs load (e.g., when identity computed before vs after state load) could lead to different keys.

Fixes
- Compute `gameKey` consistently post-load using the active `NES` core IDs and saved `CurrentRomName`/`GameId`.
- Log keys on save/restore during debugging to confirm parity.

## 6) Restore ordering vs engine init

Symptom
- Snapshot seems to restore, but UI/engine doesn’t reflect it.

Why
- If restore happens before the engine is (re)initialized on the current `NES`, restored state lands in a stale/temporary engine instance.

Fix
- Ensure ordering: (a) ROM/NES loaded (final instance), then (b) achievements engine built on that NES, then (c) snapshot restored.
- Collapse duplicate page-side restore and rely on a single restore that runs after engine rebuild.

## 7) Missing runtime fields in snapshot (edge)

Symptom
- Specific complex achievements don’t resume accurately.

Why
- While we serialize hits, isMet, primed, remembered, and measured, some meta-logic (e.g., multi-chain attribution subtleties) is recomputed per frame and could diverge on first frame.

Fix
- If a particular cheevo remains off, capture its formula and add targeted state to the DTO or tailor a warm-up step for that pattern.

---

## Verification checklist after fixes
- After LoadState constructs a new `NES`, achievements engine is rebuilt on that instance.
- Snapshot is restored only after engine rebuild and for the same `gameKey`.
- First frame after restore doesn’t apply Reset* (or run a warm frame before enabling resets).
- Delta/prior achievements work after one frame (or immediately if prior RAM shadows are restored).
- Debug panel updates every frame (confirm engine RAM domain points at the current `NES`).

## Optional diagnostics
- Console log on save/restore: `gameKey`, snapshot presence, and NES identity/core IDs.
- Debug panel badge showing the hash/id of the bound `NES` instance to detect stale bindings.