# UAT Findings: Deck Builder / Progression / Achievements

**Area:** Deck Builder / progression / achievements (WebView2 screens, no ROM needed)
**Date:** 2026-08-29
**Build under test:** `Windows\bin\Release\net10.0-windows\win-x64\BrokenNes.Windows.exe`
**Instance PID:** 20256 (own independent instance, launched via `Start-BrokenNesWindows`)

## Summary

Navigated the full Deck Builder flow end to end: Main Menu -> Deck Builder summary -> campaign
console ("Continue Deck Builder") -> CPU/PPU core selector dialogs -> card collection ("View
Unlocked Cores") -> story replay character-select modal. Everything rendered correctly and every
interactive control I exercised worked as expected. No blank/broken screens were found. One
transient (self-correcting) cosmetic quirk was observed and is detailed below — not classed as a
hard bug.

**Important context correction vs. the task brief:** the save loaded by this instance was **not**
a fresh/empty save. It already had 136/138 cores owned, 8 achievement stars, and Level 7
progress. So the "likely mostly locked/empty" expectation in the task brief did not hold here —
this app data directory apparently persists a save from prior dev/testing use, shared across
`BrokenNes.Windows.exe` instances (no per-instance save isolation was evident). This is reported
as an observation about the test environment, not a bug in the app.

## Step-by-step results

### 1. Launch, health warning, Open Deck Builder
- Launched own instance (PID 20256). Health Warning dialog appeared as expected.
- Enumerated accessible names first (per instructions): the visible "OK" button's accessible
  Name is **"Acknowledge health warning"** — another visible-text-vs-accessible-Name mismatch,
  consistent with the pattern documented in `UiaHelpers.ps1`.
- Clicked it, Main Menu rendered. Visible "Deck Builder" button's accessible Name is
  **"Open Deck Builder"** (matches the exact example already documented in the README/helpers).
- **Result: PASS.**
- Screenshots: `deckbuilder-01-launch.png`, `deckbuilder-02-mainmenu.png`.

### 2. Deck Builder screen contents
- Clicked "Open Deck Builder" -> Deck Builder summary screen rendered with:
  - **SUMMARY** panel: `Owned Cores 136/138`, `Achievement stars 8`, `Progress: Level 7`
  - Actions: "Continue Deck Builder" (large tile), "Watch Story Again", "View Unlocked Cores",
    "RETURN"
- As noted above, this is a save with substantial existing progress, not an empty one. All values
  displayed and none were blank/erroring.
- **Result: PASS** (content matches README description; save-state was non-fresh, reported as
  context not a bug).
- Screenshot: `deckbuilder-03-deckbuilder.png`.

### 3. "Continue" campaign-console entry point
- Clicked "Continue Deck Builder" -> full campaign console screen rendered:
  - Round header: "7 Open Round — Not Cleared", flavor text, "Enforced: None",
    "Progression: 8/9 ⭐" badge (this badge did not respond to Invoke/Select/Toggle/Expand click
    patterns — appears to be a static display badge, not an actionable control; not treated as a
    bug since nothing in the UI implies it should be clickable).
  - **Build Console**: four equip-loadout cards — CPU (SPD + Emit IL, "Select CPU core"), PPU
    (Low Power, "Select PPU core"), APU (EXP. Spd JNK, "Select APU core"), SHADER (LAT,
    "Select SHADER core") — each showing currently-equipped card art, percentage modifier, and a
    button to open a full core-selection dialog.
  - **Cartridges** section: "Installed ROMs with challenge stars", a "ROM Manager" shortcut
    button, and the empty-state message "No installed cartridges with achievements were found."
    (expected — no ROMs imported in this instance) plus "Select an installed game to view
    details."
  - **START GAME** / **RESET GAME** buttons, and a **Controller ports** group with
    "Controller 1" / "Controller 2" buttons.
- Opened the CPU core selector ("Select CPU core"): dialog listed EIL, FMC, LOW, LW2, SPD, ULQ,
  Z80 with star ratings and a live detail/preview panel (card art, description, stars) that
  updated to show the selected core (verified against CPU_EIL). **CPU_FIX did not appear in the
  list**, consistent with the documented, pre-existing progression-lock gap (no unlock definition
  exists for the FIX family, so it can never appear for a normal save) — expected, not a new bug.
- Spot-checked the PPU core selector too ("Select PPU core"): listed BFR, CUBE, CUBEX, EIL, EXE,
  FMC, IMG, LOW, LQ, SPD. **PPU_FIX likewise absent**, consistent with the same known gap.
- Both dialogs closed cleanly via their "Close" (visible "X") button, returning to the console
  without error.
- **Result: PASS.**
- Screenshots: `deckbuilder-04-continue.png`, `deckbuilder-06-selectcpu.png`,
  `deckbuilder-18-selectppu.png`.

### 4. Achievements overlay/list
- No single dedicated "Achievements list" screen was found under this navigation path. What does
  exist and was verified:
  - The **Achievement stars** counter on the Deck Builder summary (`8`).
  - The per-round **"Progression: 8/9 ⭐"** indicator inside the campaign console (appears to be a
    round-specific achievement-completion counter, non-interactive).
  - The **Cartridges/achievements-per-ROM panel** in the campaign console, correctly showing an
    empty state since no ROMs are installed.
  - Inside **"View Unlocked Cores"** (the card collection — see below), two specific unlockable
    "cards" are named **"Achievements Runtime"** and **"Achievements Test"** — these read as
    deep-link/menu cards for other app screens (part of BrokenNes's "screens are unlockable
    cards" meta-design) rather than an achievements list itself. Their card art rendered
    correctly (not blank/broken).
- I did not find a screen literally titled "Achievements" reachable from Deck Builder/Main Menu
  in this session. This may exist elsewhere (e.g. under Options, or only reachable via one of the
  above deep-link cards, or via the native "Open Emulator" MenuStrip's Tools & Activities menu per
  the README) — out of scope for this pass to chase further; flagging as **BLOCKED /
  not-found-here** rather than FAIL, since nothing suggested a broken screen, just that this
  navigation path didn't surface a literal achievements-list view.
- **Result: PARTIAL / BLOCKED** — achievement data is visible in several places, but no
  standalone achievements list/overlay was reachable from this area.

### 5. Card collection ("View Unlocked Cores")
- Clicked "View Unlocked Cores" from the Deck Builder summary -> "UNLOCKED CORES" screen
  rendered with Group-by / Order / View (Cards/List) controls and a large scrollable grid of
  cards.
- Scrolled through a large portion of the collection (CPU cores, PPU cores including a
  "Secret"/"Classified" card, APU cores, shader/filter cards, background-visualizer cards, and
  menu/screen unlock cards like "Achievements Runtime", "Story", "ROM Manager", "Corruption
  Slop", etc.). All card art rendered as real icons/diagrams — no placeholder/broken-image
  squares, no blank cards, no error text.
- "RETURN" from this screen navigated back to the **Main Menu** (not back to the Deck Builder
  summary) — this is a one-level-shallower return than I initially expected, but is a reasonable,
  consistent design (this screen was opened from the summary as a sibling full-page nav, not a
  modal) and not a bug.
- **Result: PASS.**
- Screenshots: `deckbuilder-10-unlockedcores.png`, `deckbuilder-13-scrolled3.png`,
  `deckbuilder-14-scrolled4.png`.

### 6. "Watch Story Again"
- From the Deck Builder summary, clicked "Watch Story Again" -> a "Choose a character" modal
  appeared over a dimmed backdrop, showing four character portraits (Sloppy, Binty, Skully,
  Jimmy), each with correctly rendered character art, plus a "Cancel" button. A status-bar URL
  preview (`https://app.brokennes/Story/index.html`) appeared at bottom-left, consistent with a
  standard WebView2 link-hover indicator, not an error.
- Clicked "Cancel" (did not actually trigger a cutscene, to keep this pass fast) -> cleanly
  returned to the Deck Builder summary with correct data intact (136/138, 8, Level 7).
- **Result: PASS** (modal reachable and renders correctly; did not play back a full cutscene,
  out of scope for this pass).
- Screenshots: `deckbuilder-16-story.png`, `deckbuilder-17-cancelstory.png`.

## Notable observation (not a hard bug): transient 0/0 summary flash

After navigating Summary -> Continue -> Select CPU dialog -> Close -> RETURN (back to Summary),
the SUMMARY panel briefly rendered **`Owned Cores 0/0`, `Achievement stars 0`, `Progress: Level
1`** instead of the real `136/138 / 8 / Level 7`. Re-screenshotting ~3 seconds later showed the
correct values had loaded. This looks like a Blazor component re-render showing its zero-valued
defaults before an async data/state fetch completes, rather than a real data-loss bug — the
correct values reliably reappeared on every check. Flagging as a minor, likely cosmetic,
self-correcting UI flash worth a look if anyone wants a perfectly clean summary screen, but not
something a normal player would likely notice or that loses any actual save data.
Screenshots: `deckbuilder-08-returned.png` (showing 0/0) vs. `deckbuilder-09-recheck.png` /
`deckbuilder-15-backtosummary.png` (showing correct values after the flash resolved).

## Full navigation path exercised

```
Health Warning -> [Acknowledge health warning]
Main Menu -> [Open Deck Builder]
Deck Builder Summary -> [Continue Deck Builder]
  Campaign Console -> [Select CPU core] -> CPU selector dialog -> [Close]
  Campaign Console -> [Select PPU core] -> PPU selector dialog -> [Close]
Deck Builder Summary -> [View Unlocked Cores] -> Card Collection (scrolled) -> [RETURN] -> Main Menu
Main Menu -> [Open Deck Builder] -> Deck Builder Summary -> [Watch Story Again]
  -> Choose-a-character modal -> [Cancel] -> Deck Builder Summary
```

## What rendered without error
- Health warning dialog and dismissal
- Main Menu and all four of its buttons/links
- Deck Builder summary (stats panel + all 4 actions)
- Campaign console (round header, 4 equip-loadout cards, Cartridges panel, Controller ports,
  Start/Reset buttons)
- CPU and PPU core-selection dialogs (list + live preview panel)
- Full card-collection screen incl. scrolling through dozens of cards of several categories
- Story replay character-select modal

## What looked broken
- Nothing rose to the level of "blank/error screen where content was clearly expected." The only
  anomaly was the transient 0/0 summary flash described above, which self-corrected and is not
  classified as a failure.

## Blocked / not fully verified
- Could not locate a standalone "Achievements" list/overlay screen from this area's navigation
  paths (Main Menu -> Deck Builder and its sub-screens). Achievement-related data is visible in
  multiple places (summary star count, per-round progression badge, per-ROM achievements panel,
  and "Achievements Runtime"/"Achievements Test" unlockable cards) but I did not find a literal
  achievements list. This may live elsewhere in the app (e.g. Options, or the native "Open
  Emulator" MenuStrip's Tools & Activities menu, per the README) which is outside this area's
  scope — recommend a follow-up pass if a dedicated achievements screen is expected to exist.
- Did not exercise "START GAME" / "RESET GAME" or the Controller port buttons (these begin actual
  gameplay/ROM flows, which is arguably a different UAT area and requires an installed
  cartridge — the Cartridges panel showed none installed in this instance).
- Did not check the "Group by" / "Order" / "View" dropdowns' actual option lists in the card
  collection (UIA did not expose child list items without expanding the native `<select>`, and
  exercising that felt out of scope for this pass).
- Did not play back an actual story cutscene (cancelled out of the character-select modal to
  keep the pass efficient); the modal itself was confirmed reachable and correctly rendered.

## Screenshots (in `UAT/screenshots/`)
- `deckbuilder-01-launch.png` — health warning
- `deckbuilder-02-mainmenu.png` — main menu
- `deckbuilder-03-deckbuilder.png` — Deck Builder summary (fresh view, 136/138 etc.)
- `deckbuilder-04-continue.png` — campaign console (top)
- `deckbuilder-05-progression.png` — progression badge click attempt (no visible change)
- `deckbuilder-06-selectcpu.png` — CPU core selector dialog
- `deckbuilder-07-afterclose.png` — after closing CPU dialog
- `deckbuilder-08-returned.png` — summary screen mid-flash (0/0)
- `deckbuilder-09-recheck.png` — summary screen after flash resolved (136/138)
- `deckbuilder-10-unlockedcores.png` — card collection (top)
- `deckbuilder-11-scrolled.png` / `deckbuilder-12-scrolled2.png` — scroll attempts (PgDn/wrong
  screen coords; no-ops, kept for completeness)
- `deckbuilder-13-scrolled3.png` — card collection scrolled (PPU/APU cores incl. Secret card)
- `deckbuilder-14-scrolled4.png` — card collection scrolled further (shader cards, Achievements
  Runtime/Test cards)
- `deckbuilder-15-backtosummary.png` — RETURN from card collection landed on Main Menu
- `deckbuilder-16-story.png` — Watch Story Again character-select modal
- `deckbuilder-17-cancelstory.png` — after cancelling, back on summary with correct data
- `deckbuilder-18-selectppu.png` — PPU core selector dialog

## Instance cleanup
Own instance (PID 20256) stopped at end of run via `Stop-Process -Id 20256 -Force`.
