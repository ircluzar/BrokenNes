# Continue — UI Worksheet for `Continue.razor`

> Design and implementation checklist for the Continue page UI. Aligns with runtime/state contracts in `continue-project.md` but focuses on the on-page interface and interactions users see.

---

## Overview

The Continue page guides the player through: viewing the current level, building the console with enforced cards, selecting a game (ROM), reviewing that game’s achievements, and starting a monitored play session. Progression is gated by stars and level rules.

Assumptions
- Clock is fixed to `CLOCK_FMC` and not user-selectable in this UI.
- Core types shown: CPU, PPU, APU, Mapper. Enforced cards are auto-slotted and locked.
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
- Shows four slots: CPU, PPU, APU, Mapper. The Clock is omitted (implicitly `CLOCK_FMC`).
- Each slot is displayed as a card (empty state text: "Select a CPU Core" / "Select a PPU Core" / etc.).
- Enforced cards appear pre-slotted in their corresponding slots, grayed-out, with a white Lock icon overlay. They are not removable or editable for this level.
- Non-enforced slots are player-selectable; clicking opens a picker to choose from owned/inventory cards of the matching type.
- On level advance, non-enforced slots reset to empty. Enforced slots update to the next level’s enforced cards.

3) Game (ROM) Selector
- Enhanced ROM manager table/list showing entries from continue-db/IndexedDB with columns:
  - Title, System, Compatibility Level (or flag), Achievements (# available / # total), and Notes (if any).
- Filters: include only compatible games (have at least one achievement in DB) for progression. Non-compatible entries may display but are not selectable for progression.
- Selecting a game updates the Achievements panel below.

4) Achievements Panel
- Shows the available achievements for the selected game, with status (Completed / Not Completed). Indicate counts (e.g., 12/25 completed).
- For the next session, the engine will sample 5 uncompleted achievements, but this panel lists the full set for the selected game.

5) Session CTA
- Button: "Start the game".
  - Disabled until all required cores are satisfied (enforced + any required optional ones for a valid build) and a compatible game is selected.
  - Disabled if the selected game has zero achievements.

---

## Interaction Details

Modal for Card Zoom
- Trigger: click any enforced card name in the header box.
- Behavior: full-screen overlay with darkened backdrop; shows a large card visual with card title, type, and metadata; close via [X] or backdrop click.

Level Clear Indicator
- Reads from Save: a transient per-level flag computed by checking if at least one achievement was unlocked while this level’s enforced cards were active.
- On advancing to next level, the clear indicator resets for the new current level.

Start/Next Buttons
- Start the game: becomes enabled when build constraints are satisfied and a compatible game with achievements is selected.
- Go to next level (5 stars): enabled when `save.stars >= currentLevel.requiredStars`.
  - Label shows "(5 stars)" as specified; the required threshold can be dynamic under the hood, but the copy here follows the spec string.

Achievement Progress Within a Level
- The player can unlock multiple achievements over multiple sessions while remaining on the same level. The level’s clear condition (any 1) is simply the minimum threshold to consider it cleared; unlocking more is allowed and contributes stars.

---

## Data & Bindings

- Current Level: `save.currentLevel`, display paired `Level` content from DB (title, requiredCards, requiredStars).
- Enforced Cards: `level.requiredCards` mapped to their types (CPU/PPU/APU/Mapper) and slotted automatically.
- Locked State: enforced slots are non-interactive; display lock overlay.
- Inventory Cards: `save.inventoryCards` filter by type to populate pickers.
- ROM Registry: `save.romRegistry` joined to Content DB `games` for title/system and to achievements index for counts.
- Achievements List: all `achievements` for selected game, with completion marked via `save.unlockedAchievements`.
- Cleared Flag: derived per-level property based on presence of any unlocked achievement earned with this level’s enforced build.

---

## Acceptance Checklist (Tasks with Subtasks)

Top Header Box
- [ ] Render current level number and title
- [ ] Render enforced cards as clickable text chips
  - [ ] Click → open full-screen modal with zoomed card
  - [ ] Backdrop darkens; ESC/backdrop click closes
- [ ] Show level clear status chip (Cleared/Not Cleared)
  - [ ] Compute from save + per-level session unlock history
  - [ ] Reset on level advance
- [ ] "Go to next level (5 stars)" button
  - [ ] Disable if `save.stars < requiredStars`
  - [ ] On click → advance level, grant rewards, reset unlocked slots

Core Selector (2x2 Grid)
- [ ] Layout four slots (CPU, PPU, APU, Mapper)
- [ ] Auto-slot enforced cards; gray-out + white Lock overlay
- [ ] Empty slot copy: "Select a <Type> Core"
- [ ] Slot picker for non-enforced slots (filters inventory by type)
- [ ] Reset non-enforced slots on level change
- [ ] Clock omitted (implicitly `CLOCK_FMC`)

Game (ROM) Selector
- [ ] List ROM entries with Title/System/Compatibility/Achievements/Notes
- [ ] Mark and/or filter incompatible or no-achievement games
- [ ] Selecting a game updates the Achievements panel

Achievements Panel
- [ ] List achievements for selected game with completion state
- [ ] Show counts (completed/total)
- [ ] Indicate which are already unlocked in save

Session CTA
- [ ] "Start the game" button
  - [ ] Disable until build is valid (all required slots satisfied)
  - [ ] Disable if selected game has zero achievements
  - [ ] Enable when both build and game conditions are met
  - [ ] On click → begin session (engine assigns 5 random uncompleted achievements)

State/Rules Wiring
- [ ] Bind `save.currentLevel`, `save.stars`, and `levels[index]`
- [ ] Derive clear flag for this level (any 1 achievement with enforced build)
- [ ] Update inventory on level advance (grant forced cards where applicable)
- [ ] Persist selections (optional: temp build choices) per session or per level as needed

Accessibility & UX
- [ ] All interactive elements keyboard-navigable; focus states visible
- [ ] Modal has initial focus trap and ESC to close
- [ ] Labels/aria for lock icons and disabled states
- [ ] Responsive layout: header box, 2x2 core grid, and tables scale down gracefully

---

## Edge Cases
- No compatible ROMs: ROM selector shows an empty state with guidance to add ROMs or import DB.
- Selected game has no remaining uncompleted achievements: Start button disabled with tooltip.
- Inventory lacks optional cores: slot picker indicates none available.
- Emulator/session abort: return from session leaves page state intact; clear flag unaffected.

---

## Minimal Visual Wireframe (ASCII)

[ Level N — Title                         (Cleared|Not Cleared) ]
[ Enforced: CPU_X • PPU_Y • APU_Z • MAPPER_N ]  [Go to next level (5 stars)]

[ CPU Slot ]  [ PPU Slot ]
[ APU Slot ]  [ Mapper Slot ]

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
- [ ] Save/load reflects clear state and star thresholds accurately
- [ ] Light smoke test across desktop and mobile breakpoints
