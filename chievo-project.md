# Achievement Unlock Flow — Worksheet (Checkboxes)

Goal: When an achievement triggers, execute a consistent UX and persistence flow.

Scope
- [x] Applies to `Nes.razor` gameplay session via `Emulator` class
- [x] Uses existing AchievementsEngine and GameSave storage

Acceptance criteria (Definition of Done)
- [x] On unlock, do all of the following in order:
   - [x] Save state to the same slot as the top Save/Load buttons
   - [x] Pause emulation
   - [x] Persist achievement by unique ID into GameSave
   - [x] Show a blocking modal without buttons
   - [x] Modal shows: "Achievement unlocked" + the achievement title
   - [x] After 5 seconds, redirect to Continue page
- [x] Continue page reflects stars from unique IDs in save
- [x] DeckBuilder star count updates from save.Achievements.Count

Implementation tasks
1) Hook unlock events
   - [x] Locate `_achEngine.EvaluateFrame()` call site in `NesEmulator/board/Emulator.cs`
   - [x] On returned IDs, trigger the unlock flow for the first ID only (ignore same-frame multiples)

2) Implement unlock flow in `Emulator`
   - [x] Save state (reuse StatePersistence slot)
   - [x] Pause emulation
   - [x] Load `GameSave`, add ID if not already present (case-insensitive), save back
   - [x] Set modal state (open, id, title)
   - [x] Schedule 5s timeout (JS) to navigate to `./continue`

3) UI modal in `Pages/Nes.razor`
   - [x] Render overlay when `emu.IsAchievementModalOpen`
   - [x] Large headline: "Achievement unlocked"
   - [x] Subtitle: unlocked achievement title
   - [x] High z-index, darkened backdrop, no buttons

4) Persistence shape
   - [x] Use `GameSave.Achievements: List<string>` (IDs are unique and stable)
   - [x] Verify `Continue.razor` builds `UnlockedAchievementIds` from save
   - [x] Verify `DeckBuilder.razor` uses `save.Achievements.Count`

5) Redirect behavior
   - [x] No user interaction on the modal
   - [x] Auto-redirect after 5s to Continue page
   - [x] Emulation remains paused

Edge cases
- [x] Multiple unlocks in the same frame: only the first triggers the flow
- [x] If savestate is busy or NES is null, proceed best-effort with remaining steps
- [x] If save cannot be written, still show modal and redirect

Dev notes
- [x] Public properties exposed on `Emulator` to project modal state into Razor
- [x] Flow implemented in `TriggerAchievementUnlockFlowAsync` and called from frame loop
- [x] Save slot reuses `StatePersistence.cs` (`SaveKey = "nes_state_slot0"`) via `SaveStateAsync()`

QA validation checklist
- [x] Load a ROM with a trivially satisfiable achievement
- [x] Play until the achievement triggers
- [x] Verify: state saved, emulation paused, modal shown with correct title
- [x] After 5s, verify redirect to Continue page
- [x] On Continue page, verify the star count increased and achievement shows unlocked
- [x] Verify DeckBuilder star count matches total achievements

Follow-ups (optional)
- [ ] Play a chime/SFX when the modal appears
- [ ] Animate the modal (fade/scale) and/or add a subtle 5s progress bar
- [ ] Debounce unlocks across frames (additional guard) to prevent duplicate persistence
