# Achievement Unlock Flow — Implementation Worksheet

Goal: When an achievement triggers, execute a consistent UX and persistence flow.

Scope
- Applies to `Nes.razor` gameplay session via `Emulator` class.
- Uses existing AchievementsEngine and GameSave storage.

Acceptance criteria
- On unlock: (1) save state, (2) pause, (3) persist achievement ID in save, (4) show blocking modal, (5) auto-redirect to Continue page after 5s.
- Modal has no buttons; shows "Achievement unlocked" + achievement title.
- Continue page shows stars based on unique achievement IDs in the save. DeckBuilder star count updates too.

Checklist
1) Hook unlock events
   - Source: `NesEmulator/board/Emulator.cs` where `_achEngine.EvaluateFrame()` is called each frame.
   - When IDs returned, trigger flow for first ID (ignore duplicates/same-frame multiples).
2) Implement flow in Emulator
   - Save state to the same slot used by Save/Load buttons.
   - Pause emulation.
   - Load GameSave, add ID if not present, save back.
   - Set modal state (id, title, open).
   - Schedule JS timeout to navigate to `./continue` in 5s.
3) UI modal in `Pages/Nes.razor`
   - Render overlay when `emu.IsAchievementModalOpen`.
   - Big headline: "Achievement unlocked"; subtitle: achievement name.
   - Darken background, centered card, high z-index.
4) Persistence shape
   - Use `GameSave.Achievements: List<string>` to store unlocked IDs (unique, case-insensitive).
   - Continue page (`Continue.razor`) already builds `UnlockedAchievementIds` from save; no code change required.
   - DeckBuilder reads `save.Achievements.Count` for star count; already wired.
5) Redirect behavior
   - No user interaction. Auto-redirect after 5s.
   - Emulation remains paused.
6) Edge cases
   - Multiple unlocks in the same frame: only the first triggers the flow; others will be re-evaluated after returning from Continue.
   - If savestate is busy or NES is null, best-effort proceed with remaining steps.
   - If save cannot be written, still show modal and redirect.

Dev notes
- Public properties added to `Emulator` to expose modal state to Razor.
- Flow implemented in `TriggerAchievementUnlockFlowAsync` and called from the frame loop.
- Save slot reuses `StatePersistence.cs` `SaveKey = "nes_state_slot0"` via `SaveStateAsync()`.

QA validation
- Load a ROM with a known trivially satisfiable achievement.
- Play until the achievement triggers.
- Observe: state saved (optional status), emu pauses, modal appears for 5s with correct title, then redirect to Continue page.
- On Continue page, confirm the star count increased and the specific achievement entry shows unlocked.
- DeckBuilder star count reflects the same total.

Follow-ups (optional)
- Play a chime/SFX when the modal appears.
- Animate the modal (fade/scale) and add a subtle progress bar for the 5s timer.
- Debounce unlocks across frames to prevent duplicate persistence (currently List.Add with Contains already guards that).
