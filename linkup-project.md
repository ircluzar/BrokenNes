# Achievements Link-Up Project Worksheet

This worksheet captures the plan to change how our Achievements are stored and managed by linking them to the Meta (RetroAchievements) database rather than storing requirement formulas directly.

## Objective
- Stop storing raw requirement formulas in our Achievements DB.
- Store a reference to a Meta achievement instead (composite link: Game Title + Meta Achievement Name).
- Use that link to fetch formulas from the Meta database at runtime.
- Add an "Approved" toggle in the Meta tab of the CRUD to curate which Meta achievements are included in our Achievements DB.

## Context
- Our Games DB already uses game titles that match the Meta database game titles.
- The Meta database is the source of truth for RetroAchievements formulas.
- Our Achievements DB should become a curated list referencing entries in the Meta DB.

## Data Model Changes
- Current Achievements fields (simplified):
  - id (string)
  - gameId (string) → references Games DB
  - title (string)
  - requirements (List<string>) ← to be removed
- Proposed Achievements fields:
  - id (string) — stays (stable ID generation)
  - gameId (string) — stays
  - title (string, optional) — optional override/display name (can default to Meta achievement name)
  - metaAchievementName (string) — NEW: the exact name/description of the meta achievement
  - (no requirements stored locally)

Notes:
- Link key = Game Title (from Games DB record referenced by gameId) + Meta Achievement Name (string match).
- Case-insensitive, trimmed comparisons recommended.

## UI/CRUD Changes
### Achievements tab
- Remove editable Requirements field.
- Add a way to select the Meta Achievement for the selected game:
  - When editing/creating an achievement: a searchable dropdown fed by Meta achievements for the chosen game title.
  - Store `metaAchievementName`.
- Display-only formula preview (optional) fetched from Meta DB based on link.

### Meta tab
- Add an "Approved" column with a toggle checkbox for each Meta achievement row.
- Approved = the achievement exists in our Achievements DB (linked by Game Title + Meta Achievement Name).
- Toggling:
  - On → Create/update the corresponding record in Achievements DB (assign `gameId` by matching Games.title to Meta game; set `metaAchievementName`; optionally set `title`).
  - Off → Remove the corresponding record from Achievements DB.
- Visual cue: show Approved state directly in the Meta grid.

## Service/Logic Changes
- Use `MetaGamesService` to query Meta achievements and formulas.
- New helper functions:
  - FindGameByTitle(title): Games → gameId
  - IsApproved(gameTitle, metaAchName): Achievements contains record with { gameId for title, metaAchievementName }
  - Approve(gameTitle, metaAchName): upsert achievement record
  - Disapprove(gameTitle, metaAchName): delete achievement record
  - GetFormula(gameTitle, metaAchName): resolve formula from Meta DB

## Migration Plan
- Strategy: Soft migration with backward compatibility.
  - Keep reading old records; if `requirements` present and `metaAchievementName` missing:
    - Try to map by formula equivalence (best-effort; optional/advanced).
    - Otherwise, leave as-is and mark for manual mapping.
  - New UI will prefer `metaAchievementName`. Old `requirements` will be ignored in new UI but not deleted automatically.
- Optional one-time script to:
  - For each Achievements row with `requirements`, attempt to match a Meta achievement whose formula equals (or closely matches) combined lines.
  - If matched, set `metaAchievementName` and (optionally) clear `requirements`.

## Edge Cases
- Multiple Games with similar titles: we assume exact title matches. If not, require manual resolution.
- Title drift: if a Game title changes, existing links may break. Consider storing `metaGameTitle` snapshot on achievement to lock the link. For now, rely on current convention.
- Meta achievement renamed: link breaks; UI should indicate not-found state and allow re-selection.

## Acceptance Criteria
- [~] Achievements DB no longer stores requirements for new/edited achievements. (Kept empty array for backward compatibility.)
- [x] Achievements link to Meta achievements by (Game Title, Meta Achievement Name).
- [x] Meta tab shows an Approved toggle reflecting whether an entry exists in Achievements.
- [x] Toggling Approved on creates the linked achievement record; off removes it.
- [~] Formula tests use formulas fetched from Meta (Meta tab test uses Meta formulas; Achievements view does not evaluate formulas).
- [x] Backward compatibility: old records with requirements do not crash the UI.

## Tasks
### Schema & Models
- [x] Add `metaAchievementName` to Achievements model (Razor/data class) and storage writes.
- [x] Mark/remove `requirements` usage in code paths; keep read-compatible.
- [x] Add utility to fetch Meta achievements by Game Title.

### Achievements Tab (CRUD)
- [x] Replace requirements editor with Meta achievement selector (filtered by selected game).
- [x] Persist `metaAchievementName` when saving.
- [ ] Show resolved formula (read-only) for reference.

### Meta Tab (CRUD)
- [x] Add "Approved" column with checkbox.
- [x] Compute Approved state by checking Achievements DB for the link.
- [x] Hook toggle to add/remove Achievements records accordingly.
- [x] Live-refresh row state after toggle.

### Testing & Engine Integration
- [~] Update any code paths that evaluate formulas to obtain them from Meta via the link. (Meta tab uses Meta formulas; Achievements view not changed.)
- [x] Ensure single- and all-formula tests resolve through Meta correctly. (Per-title click and Test All in Meta tab.)
- [x] Keep diagnostics (green/red) behavior.

### Migration (Optional/Phased)
- [ ] Implement a passive mapping pass (best-effort) to set `metaAchievementName` based on existing `requirements`.
- [ ] Provide a manual mapping UI/flow if automatic mapping fails.

### QA Checklist
- [ ] Create a new Achievement referencing a Meta achievement; verify it displays and resolves the formula.
- [ ] Toggle Approved on a Meta row; verify a corresponding Achievement appears and is linked.
- [ ] Toggle Approved off; verify the Achievement disappears.
- [ ] Run formula tests via Achievements against Meta-resolved formulas.
- [ ] Verify behavior when Meta selection is missing/broken.

## Rollback Plan
- All changes are UI+storage-level; retain the old `requirements` field reading to revert if needed.
- If issues arise, disable Approved toggles and fallback to old Achievements editing until fixed.

## Open Questions
- Should we also store `metaGameTitle` to protect against future Game title drift? (Recommendation: Yes.)
- Do we need a stable Meta achievement ID beyond name? If available, prefer storing ID.
- Should the Achievements list be entirely derived from Approved meta entries (read-only) or remain editable? (Leaning curated-derived.)

## Milestones
- M1: Data model + UI toggles (no migration).
- M2: Achievements editor updates + formula resolution through Meta.
- M3: Migration helpers and polish.
