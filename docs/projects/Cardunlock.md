# Card Unlock Progression (Current DeckBuilder State)

This document describes the current, implemented progression logic for DeckBuilder/Continue unlocks.

## Source of truth used
- ContinueDB seed: `Windows/Webmodules/shared/models/default-db.json`
- Continue runtime: `Windows/Webmodules/Continue/continue.js`
- Progression save/reward authority: `Windows/webapi/ProgressionSaveService.cs`
- Progression endpoints: `Windows/webapi/WebApiServer.Endpoints.Progression.cs`
- Save merge path: `Windows/webapi/WebApiServer.Endpoints.Save.cs`
- Achievement clear flag behavior: `Windows/Webmodules/AchievementsRuntime/script.js`

## High-level progression loop
1. Player unlocks an achievement in runtime.
2. Achievement is saved into `GameSave.Achievements` (star count goes up by 1).
3. First achievement on a level marks `GameSave.LevelCleared = true`.
4. In Continue, level can advance only when:
   - `LevelCleared == true`
   - `stars >= requiredStars` for current level
5. On advance, rewards are built and applied to owned inventory, then `Level` increments by 1.

## Starting inventory at fresh save
- CPU: `FMC`
- PPU: `FMC`
- APU: `FMC`
- CLOCK: `FMC`
- SHADER: `PX`
- Unlocked webmodules (baseline): Home, Continue, DeckBuilder, Cores, Options, Story, RomManager, HexEditor
- Unlocked backgrounds (baseline): Gradient (Default), None (Black)
- Unlocked null providers (baseline): Static, Void

## Level-by-level card progression (ContinueDB)
These are the level templates currently loaded from ContinueDB (`levels` store).

| Current Level | Stars Required | Required Cards (reward candidates) |
|---|---:|---|
| 1 | 1 | CPU_FMC, PPU_FMC, APU_FMC, CLOCK_FMC, SHADER_PX |
| 2 | 2 | CPU_LOW, PPU_LOW, SHADER_TTF |
| 3 | 3 | APU_LQ, SHADER_16B, CPU_LW2 |
| 4 | 5 | APU_LOW, SHADER_RF |
| 5 | 7 | APU_LQ2, CPU_SPD |
| 6 | 8 | APU_SPD2, CPU_EIL, SHADER_LAT, PPU_LOW |
| 7 | 9 | (none) |
| 8 | 10 | APU_QN |
| 9 | 11 | APU_SPD, PPU_SPD, CPU_SPD, SHADER_BUMP |
| 10 | 13 | APU_QLOW, CPU_EIL, SHADER_SPK |
| 11 | 14 | APU_WF, SHADER_TV, PPU_LQ, CPU_LOW |
| 12 | 16 | (none) |
| 13 | 17 | APU_QLQ, CPU_EIL, PPU_EIL, SHADER_LSD |
| 14 | 18 | SHADER_LCD, PPU_BFR, APU_QLQ2, CPU_SPD |
| 15 | 19 | SHADER_EXE, APU_LQ |
| 16 | 20 | CPU_EIL, PPU_CUBE, APU_EIL, SHADER_BLD |

## Post-16 behavior
- `requiredStars` formula for level 17+:
  - `requiredStars = (currentLevel - 6) * 2`
- Since ContinueDB has no explicit level records after 16, postgame levels now use blind-bag rewards.
- Postgame blind bag rule (level 17+): unlock exactly 3 new random cores per level, from any core category, with no duplicates from already-owned cards.

## How level rewards are currently built
When advancing from level N to N+1:
1. Start from current level's `requiredCards` (if any).
2. For levels 1-16: if no enforced core cards (CPU/PPU/APU/SHADER) were present, add a random bonus pack.
3. For level 17+: skip preset logic and grant a blind bag of exactly 3 new random cores.
4. Deduplicate and add newly owned cards to save.
5. If the player completes the full core-card collection, show a congratulations notice in the unlock modal.

## Random bonus logic (current)
Random bonus card picker tries this order:
1. Cards tagged `LAST` in ContinueDB cards table (pick 2-3)
2. If not enough, cards tagged `RANDOM`
3. If still not enough, fallback pool from all unowned non-starter cores in registries

Important current state:
- In `default-db.json`, all cards are seeded with `type: "Reserved"`.
- That means `LAST` and `RANDOM` pools are effectively empty right now.
- So random bonus behavior currently relies on fallback pool selection.

## Milestone unlocks (non-card components)
When level increases, backend save merge enqueues milestone rewards for completed levels:
- After completing level 4: unlock webmodule `GlitchHarvester` (RTC stack)
- After completing level 8: unlock webmodule `TimeJump`
- After completing level 12: unlock webmodule `CorruptionSlop`
- After completing level 16: unlock webmodule `Target the Beam (ImagineBug)`

These are stored as pending unlock bundles and shown in reward modal flow.

## Achievement-based random unlocks (non-level)
On each newly unlocked achievement, backend may grant queued rewards:
- 1 deterministic background unlock candidate (if available and still locked)
- 1 deterministic null-provider unlock candidate (if available and still locked)

Notes:
- Selection is deterministic per achievement ID via SHA-256 seed.
- Rewards are queued in `PendingUnlocks` and presented/acknowledged through progression endpoints.

## Gating and unlock consumption
- API gates enforce module lock status (for rtc/gh/timejump/imagine endpoints).
- Continue modal can claim pending bundles (`/api/progression/claim-pending`) and acknowledge them (`/api/progression/acknowledge`).
- Equippable pending rewards include background and null-provider unlock entries.

## Practical summary
Current progression is a hybrid system:
- Level templates and required cards come from ContinueDB (`levels` + `cards` seed).
- Actual authority for non-card progression, pending rewards, and milestone module unlocks is the C# progression save service.
- Postgame (17+) progression is now explicit blind-bag core collection: 3 new random cores per level from any category until completion.