# CRUD & Database System — Implementation Worksheet

This checklist sequences the work to deliver a full, in-app database editor per continue-project.md.

Progress (2025-08-25)
- /crud page exists with tabs and toolbar (export/import buttons wired to JS).
- Options page links to CRUD.
- Global `continueDb` module shipped at `wwwroot/lib/continue-db.js` and included in `index.html` (also wired in `flatpublish/index.html`).
- Blank `default-db.json` added at `wwwroot/models/default-db.json` (mirrored under `flatpublish/models/`).
- Startup now always loads `default-db.json` (immutable baseline); debug import/export can override during the session.

## 0) Foundations
- [x] Add `continueDb` JS helper module in wwwroot for IndexedDB (DB name: `continue-db`).
  - [x] `open()` with object store setup: `games(id)`, `achievements(id)` with `by_gameId`, `cards(id)`, `levels(index)`, `save(singleton)`.
  - [x] `getAll(store)`, `get(store, key)`, `put(store, value)`, `delete(store, key)`, `clear(store)`.
  - [x] Bulk ops: `putMany(store, items)`, `exportAll()` → JSON, `importAll(json)` with schema/version checks.
  - [x] Wire downloads/uploads: `exportAllToDownload()`, `importFromFileInput()`.
  - [x] Ship file at `wwwroot/lib/continue-db.js` and include it in `wwwroot/index.html` before Blazor boot scripts.
- [x] Seed-load: on app start, always load `default-db.json` (treat as immutable game data).
  - [x] Place `default-db.json` in `wwwroot/models/` (mirrored in `flatpublish/models/`).
  - [x] Implement startup load that replaces DB content each launch; import/export remains for debug authoring.

Gate test for Foundations (must pass before moving on)
- [ ] With `continueDb` loaded globally and `default-db.json` present:
  1) Load app, open DevTools, run `await continueDb.open()`; verify DB `continue-db` exists with stores: games, achievements (index by_gameId), cards, levels, save.
  2) On first load, run `await continueDb.getAll('games')` and confirm it matches `wwwroot/models/default-db.json` (empty arrays by default).
  3) Visit `/crud`; click “Export DB JSON” → verify a JSON download with meta and empty arrays.
  4) Import a modified JSON via the file control; confirm `await continueDb.getAll('games')` reflects the import.
  5) Hard refresh the page; confirm data resets to `default-db.json` (proving immutable baseline reload at startup).

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
- [x] Create/Edit drawer or inline row editing.
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
- [x] Grid list sorted by index.
- [ ] Fields: index, requiredCards[], requiredStars, isCardChallenge, challengeCardPool[]
- [ ] Derived check: every 4th level → isCardChallenge true by default.
- [ ] Guard: requiredCards must exist in Cards.

## 7) Import/Export (Doc §5.3)
- [x] Export entire content + save to a JSON blob with meta {format, exportedAt}.
- [x] Import validates `format` and merges or replaces (choose Replace for authoring mode).
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
- [x] Batch IDB ops in transactions.
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
