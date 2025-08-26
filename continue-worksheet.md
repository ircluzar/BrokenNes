# Continue — UI Worksheet for `Continue.razor`

> Design and implementation checklist for the Continue page UI. Aligns with runtime/state contracts in `continue-project.md` but focuses on the on-page interface and interactions users see.

---

## Overview

The Continue page guides the player through: viewing the current level, building the console with enforced cards, selecting a game (ROM), reviewing that game’s achievements, and starting a monitored play session. Progression is gated by stars and level rules.

Assumptions
- Clock is implicit (not shown/chooseable here).
- Core slots shown: CPU, PPU, APU, Shader. Enforced cards are auto-slotted and locked. Mapper selection is not exposed in the UI and is derived from the ROM.
- A level is considered “cleared” when the player completes any 1 achievement with the enforced cards; they may continue unlocking more achievements within the same level across multiple sessions.

---

## Layout and Components

1) Current Level Header Box (top of page)
- Content: "Level <number> — <Title>"
- Enforced Cards: textual list of the enforced/required cards for the level (e.g., CPU_X, PPU_Y, APU_Z, MAPPER_N).
  - Each enforced card name is clickable; clicking opens a full-screen modal with a zoomed representation of that card. The screen behind is darkened until close.
- Level Clear State: shows a status chip/badge: "Cleared" or "Not Cleared" for the current level.
  - Definition: Cleared if the player has completed any 1 achievement while using the enforced cards for this level.
  - Resets to Not Cleared upon advancing to the next level.
- CTA under the header box: "Go to next level (5 stars)" button.
  - Disabled if the player lacks the required stars for the current level’s threshold.

2) Core Selector (2x2 grid)
- Shows four slots: CPU, PPU, APU, Shader. The Clock is omitted.
- Each slot is displayed as a card (empty state text: "Select a CPU Core" / "Select a PPU Core" / etc.).
- Enforced cards appear pre-slotted in their corresponding slots, grayed-out, with a white Lock icon overlay. They are not removable or editable for this level.
- Non-enforced slots are player-selectable; clicking opens a picker to choose from owned/inventory cards of the matching type.
- On level advance, non-enforced slots reset to empty. Enforced slots update to the next level’s enforced cards.

3) Game (ROM) Selector
- ROM list table from continue-db with columns:
  - Title, Compat (Yes/No), Stars (completed/total). Subtitle and notes may display if available.
- Controls: "Only show compatible" filter, text search, and an Import button that opens an import modal (supports Browse and drag & drop of .nes files).
- Non-compatible entries render disabled and are not selectable for progression.
- Selecting a game updates the Achievements panel summary below.

4) Achievements Panel
- Shows a summary for the selected game: completed/total count. Full list view is planned but not yet implemented on this page.
- For the next session, the engine will sample 5 uncompleted achievements (engine wiring pending).

5) Session CTA
- Button: "Start the game".
  - Present and disabled until build is valid (all required slots, including Shader) and a compatible game is selected with > 0 achievements.
  - Click action not yet wired to engine; enabling conditions follow current UI checks.

---

## Interaction Details

Modal for Card Zoom
- Trigger: click any enforced card name in the header box.
- Behavior: full-screen overlay with darkened backdrop; shows a large card visual with card title, type, and metadata; close via [X] or backdrop click.

Level Clear Indicator
- Reads from Save: a transient per-level flag computed by checking if at least one achievement was unlocked while this level’s enforced cards were active.
- On advancing to next level, the clear indicator resets for the new current level.

Start/Next Buttons
- Start the game: becomes enabled when build constraints are satisfied and a compatible game with achievements is selected (UI only; engine hook TBD).
- Go to next level: enabled when `save.stars >= currentLevel.requiredStars`. The UI shows the star requirement on the button.

Achievement Progress Within a Level
- The player can unlock multiple achievements over multiple sessions while remaining on the same level. The level’s clear condition (any 1) is simply the minimum threshold to consider it cleared; unlocking more is allowed and contributes stars.

---

## Data & Bindings

- Current Level: `save.Level`, display paired `levels` content from continue-db (title, requiredCards, requiredStars).
- Enforced Cards: `level.requiredCards` mapped to their types (CPU/PPU/APU/Shader) and slotted automatically. CLOCK is ignored in the header chips.
- Locked State: enforced slots are non-interactive; display lock overlay; chip click opens zoom modal.
- Inventory Cards: owned IDs per domain (CPU/PPU/APU/Shader) populate simple pickers.
- ROMs/Achievements: read from continue-db tables; aggregate per-game total and completed (from `save.Achievements`).
- Achievements UI: summary only (completed/total) for now.
- Cleared Flag: currently a placeholder derived from total stars > 0; per-level attribution TBD.

---

## Acceptance Checklist (Tasks with Subtasks)

Top Header Box
- [x] Render current level number and title
- [x] Render enforced cards as clickable chips
  - [x] Click → open full-screen modal with zoomed card
  - [x] Backdrop darkens; backdrop click closes (ESC TBD)
- [x] Show level clear status chip (Cleared/Not Cleared)
  - [ ] Compute from save + per-level session unlock history (currently placeholder: any stars > 0)
  - [x] Reset on level advance
- [x] "Go to next level" button with star requirement
  - [x] Disable if `stars < requiredStars`
  - [x] On click → advance level, grant stub rewards (FMC), reset selections

Core Selector (2x2 Grid)
- [x] Layout four slots (CPU, PPU, APU, Shader)
- [x] Auto-slot enforced cards; gray-out + Lock overlay
- [x] Empty slot copy: "Select a <Type> Core"
- [x] Slot picker for non-enforced slots (simple cycle from owned list)
- [x] Reset non-enforced slots on level change
- [x] Clock omitted

Game (ROM) Selector
- [x] List ROM entries with Title/Compat/Stars
- [x] Filter toggle for compatible only and text search
- [x] Disabled state for incompatible (no-achievement) games
- [x] Import modal (Browse + drag & drop), saves ROM and inserts minimal game record
- [x] Selecting a game updates the Achievements summary

Achievements Panel
- [x] Show counts (completed/total) for selected game
- [ ] Full list of achievements (planned)

Session CTA
- [x] "Start the game" button present
  - [x] Disabled until build is valid (includes Shader) and a compatible game is selected
  - [ ] Click wiring to start session and assign 5 random achievements

State/Rules Wiring
- [x] Bind `save.Level`, stars (via `save.Achievements.Count`), and level data from continue-db
- [ ] Derive clear flag per-level (placeholder in place)
- [x] Update inventory on level advance (stub FMC grant for CPU/PPU/APU)
- [ ] Persist slot selections between visits (not yet)

Accessibility & UX
- [ ] Keyboard navigation & focus states for all interactive elements
- [ ] Modal focus trap and ESC to close
- [x] Lock overlay uses aria-hidden and tooltip; non-interactive overlay
- [x] Responsive layout (header, grid, lists)

---

## Edge Cases
- No compatible ROMs: ROM selector shows an empty state with guidance to add ROMs or import DB.
- Selected game has zero achievements: row disabled with tooltip; cannot start.
- Inventory lacks optional cores: slot picker falls back to default where possible.
- Emulator/session abort: return from session leaves page state intact; clear flag unaffected.

---

## Minimal Visual Wireframe (ASCII)

[ Level N — Title                         (Cleared|Not Cleared) ]
[ Enforced: CPU_X • PPU_Y • APU_Z • MAPPER_N ]  [Go to next level (5 stars)]

[ CPU Slot ]  [ PPU Slot ]
[ APU Slot ]  [ Shader Slot ]

[ ROM List / Manager Table .................................... ]

[ Achievements for Selected Game ............................... ]
- [ ] Achievement A
- [x] Achievement B (completed)
...

[ Start the game ]

---

## Definition of Done (UI scope)
- [ ] All acceptance items above checked
- [ ] Integrated with workflow engine events (start/assign/stop)
- [ ] Save/load reflects clear state and star thresholds accurately (per-level clear attribution)
- [x] Light smoke test across desktop and mobile breakpoints (CSS and modal render)
