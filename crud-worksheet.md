# CRUD & Database System — Implementation Worksheet

This checklist sequences the work to deliver a full, in-app database editor per continue-project.md.

## 0) Foundations
- [ ] Add `continueDb` JS helper module in wwwroot for IndexedDB (name: `continue-db`).
  - [ ] `open()` with object store setup: `games(id)`, `achievements(id)` with `by_gameId`, `cards(id)`, `levels(index)`, `save(singleton)`.
  - [ ] `getAll(store)`, `get(store, key)`, `put(store, value)`, `delete(store, key)`, `clear(store)`.
  - [ ] Bulk ops: `putMany(store, items)`, `exportAll()` → JSON, `importAll(json)` with schema/version checks.
  - [ ] Wire downloads/uploads: `exportAllToDownload()`, `importFromFileInput()`.
- [ ] Seed-load: on app start, load `default-db.json` when DB empty.
  - [ ] Place `default-db.json` in `wwwroot/models/` or similar.
  - [ ] Implement one-time promotion of seed to IDB.

## 1) Routing & Shell
- [x] Create `/crud` page with themed shell and tabs.
- [x] Link from Options → Edit Save area as "Open CRUD…".
- [ ] Ensure focus/keyboard navigation and ARIA labels.

## 2) Data Contracts (TS/JSON parity with doc §5)
- [ ] Define TypeScript interfaces for Game, Achievement, Card, Level, Save.
- [ ] Validation helpers (IDs, uniqueness, required fields, difficulty enum 1..5, etc.).
- [ ] Utility: stable sorting, paging, fuzzy search.

## 3) CRUD: Games
- [ ] Grid list with paging + search.
- [ ] Create/Edit drawer or inline row editing.
- [ ] Fields: id, title, system, headerSignature, notes.
- [ ] Validation: ID format `GAME_<system>_<hash>`; unique id.
- [ ] Actions: add, duplicate, delete, save, cancel.

## 4) CRUD: Achievements
- [ ] Grid list filtered by gameId with counts.
- [ ] Fields: id, gameId (select), title, description, watchFormula, tags[], difficulty.
- [ ] DSL lint: quick syntax check (client) before save.
- [ ] Actions: add, duplicate from existing, delete, save.

## 5) CRUD: Cards
- [ ] Grid list + type filter.
- [ ] Fields: id, type (apu/ppu/cpu/mapper/etc.), constraints (JSON editor with schema guard).
- [ ] Validation: known types, JSON parse/shape.

## 6) CRUD: Levels
- [ ] Grid list sorted by index.
- [ ] Fields: index, requiredCards[], requiredStars, isCardChallenge, challengeCardPool[]
- [ ] Derived check: every 4th level → isCardChallenge true by default.
- [ ] Guard: requiredCards must exist in Cards.

## 7) Import/Export (Doc §5.3)
- [ ] Export entire content + save to a JSON blob with meta {format, exportedAt}.
- [ ] Import validates `format` and merges or replaces (choose Replace for authoring mode).
- [ ] Optional: partial exports per table.

## 8) Integration with Game Save
- [ ] Show Save in CRUD (read-only initially) with button to open existing Edit Save controls.
- [ ] Button: "Promote to default-db.json" downloads content-only JSON (without save).
- [ ] Option: Reset IndexedDB to seed.

## 9) UX polish
- [ ] Keep theme consistent with Options page; reuse classes.
- [ ] Empty-state hints and keyboard shortcuts (N=add, S=save row, Del=delete row).
- [ ] Confirm dialogs for destructive actions.

## 10) Testing
- [ ] Unit test JS helpers (where feasible) and C# interop glue.
- [ ] Seed-load E2E: clear DB → seed → CRUD add/edit → export → import → verify round-trip.
- [ ] Validate watchFormula samples compile on C# side (stub until parser ready).

## 11) Performance & Safety
- [ ] Batch IDB ops in transactions.
- [ ] Debounce auto-saves; prevent large DOM reflows on grids.
- [ ] Size guard on import files; warn if excessively large.

## 12) Docs
- [ ] Authoring guide for IDs, difficulty, tags, and DSL usage.
- [ ] README for `default-db.json` promotion flow.

---

Notes
- Target DB name and stores per continue-project.md §5.4.
- Promotion flow: CRUD → Export → commit as default-db.json → app seeds on first run.
- Start lightweight: implement Games grid first, then Achievements, Cards, Levels.
