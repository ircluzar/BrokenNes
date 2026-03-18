# Procedural Unlock Workmap

## Goal

Rebuild DeckBuilder progression for the Windows era so every achievement star can award something meaningful, while level clears still deliver larger milestone rewards. The new system must be native-first: unlock state lives in the Windows save/backend, WinForms menus and native emulator surfaces must honor it, and webmodules consume the same authoritative progression state through the API.

## Design Summary

The old Blazor version unlocked advanced emulator modules at level milestones:

- Level 4 clear -> Savestates
- Level 8 clear -> RTC
- Level 12 clear -> Glitch Harvester
- Level 16 clear -> Imagine

In the Windows version, the clean equivalent is not to split RTC and GH as separate unlocks. The current product shape already treats RTC + GH as one practical corruption module. The new progression should therefore unlock modules in terms of webmodules and native capabilities instead of individual legacy booleans.

Recommended milestone equivalents:

1. Level 4 milestone -> RTC + Glitch Harvester
Reason: this is the priority unlock and the spiritual replacement for the old RTC/GH stack. In the current Windows system, it is already fused into one practical widget module, and it should be the first major module reward.

2. Level 8 milestone -> Time Jump
Reason: it is the closest modern equivalent to old Savestates. It is temporal, state-driven, and fits well as the second systems-expansion unlock after the player has already earned access to corruption tools.

3. Level 12 milestone -> Corruption Slop
Reason: this is a natural follow-up once the player already understands manual corruption. It escalates from hands-on corruption into automated corruption workflows.

4. Level 16 milestone -> ImagineBug
Reason: ImagineBug already works from the RTC side in this codebase and is a valid late-game advanced corruption unlock. It is the most practical replacement for the old Imagine beat right now.

Hex Editor policy:

- Hex Editor remains available from the start and is not part of procedural progression.

Optional postgame or bonus modules:

- Target the Beam as a future late-game module once it becomes part of the active Windows webmodule roster
- CorruptCloud if it becomes production-worthy

## Reward Philosophy

The new reward cadence should have three layers:

1. Every achievement star
- Randomly unlock 1 background variant
- Randomly unlock 1 null provider variant
- Present both through cards and an unlock reel/modal
- Allow the player to equip either reward directly from the unlock flow, the same way core rewards can be equipped

2. Every level clear and advance
- Unlock the level reward component cards exactly like the existing DeckBuilder reward loop
- Apply milestone webmodule unlocks on milestone levels
- Continue awarding core cards from enforced reward pairs and bonus packs
- Allow equip/apply actions from cards for any reward type that supports immediate activation

3. Special milestones and completion
- Congratulation beats
- Multi-card unlock moments
- Full collection acknowledgement when all module/background/null-provider/core inventories are complete

This gives the player constant activity after every achievement while preserving the larger emotional spike of beating a level and moving forward.

## Core Principles

1. Native authority
- The canonical progression state must live in the Windows save and Windows backend, not in localStorage-only browser state.

2. Single progression ledger
- Webmodule unlocks, backgrounds, null providers, legacy feature flags, core ownership, and pending unlock presentation should all come from one progression model.

3. Idempotent unlocks
- Unlocking the same thing twice should never create duplicate rewards or duplicate presentation.

4. Queueable unlock presentation
- Achievement unlocks and level-up rewards must be serializable into a pending reward queue so native and webmodule UIs can present them cleanly even across navigation or app restart.

5. Backward compatibility
- Existing saves with old boolean fields and existing owned core lists should migrate without destroying progress.

## Current Codebase Constraints

### What already exists

- Webmodules are first-class discoverable units in WinForms via [Windows/WebModuleInfo.cs](Windows/WebModuleInfo.cs).
- WinForms already exposes all webmodules in menus and can load them by metadata via [Windows/MainForm/MainForm.Initialization.cs](Windows/MainForm/MainForm.Initialization.cs#L407) and [Windows/MainForm/MainForm.ViewModes.cs](Windows/MainForm/MainForm.ViewModes.cs#L325).
- Backgrounds and null providers are already enumerable and switchable natively via [Windows/MainForm/MainForm.Initialization.cs](Windows/MainForm/MainForm.Initialization.cs#L262) and [Windows/MainForm/MainForm.Initialization.cs](Windows/MainForm/MainForm.Initialization.cs#L283).
- The web API already exposes background and null-provider lists plus setters via [Windows/webapi/WebApiServer.Endpoints.Emulator.cs](Windows/webapi/WebApiServer.Endpoints.Emulator.cs#L133).
- The game save is already native and persisted to AppData through [Windows/webapi/WebApiServer.Endpoints.Save.cs](Windows/webapi/WebApiServer.Endpoints.Save.cs#L8) and modeled as [Windows/webapi/WebApiModels.cs](Windows/webapi/WebApiModels.cs#L142).
- RTC/GH/Imagine/savestate legacy booleans still exist in the save model for compatibility.

### What is missing

- No roster-based unlock system for webmodules
- No owned/unlocked inventories for backgrounds or null providers
- No authoritative unlock queue for presentation
- No card taxonomy for non-core rewards
- No WinForms menu filtering based on progression
- No unified reward engine that can process both achievement rewards and level-up rewards

## Proposed Progression Model

Extend the save model from simple booleans into inventory-based progression.

### New save sections

Add these conceptual sections to the save schema:

```json
{
  "UnlockedWebmodules": ["GlitchHarvester", "TimeJump", "CorruptionSlop", "ImagineBug"],
  "UnlockedBackgrounds": ["Gradient", "Bubble", "Wave"],
  "UnlockedNullProviders": ["Static", "Void", "Aurora"],
  "PreferredBackgroundId": "Gradient",
  "PreferredNullProviderId": "Static",
  "UnlockedFeatures": {
    "Savestates": true,
    "RTC": true,
    "GH": true,
    "Imagine": false,
    "Debug": false
  },
  "PendingUnlocks": [
    {
      "id": "reward-20260318-001",
      "source": "achievement",
      "achievementId": "12345",
      "items": [
        { "type": "background", "id": "Bubble" },
        { "type": "nullProvider", "id": "Aurora" }
      ],
      "presented": false,
      "createdAtUtc": "2026-03-18T00:00:00Z"
    }
  ]
}
```

### Reward item taxonomy

Use a generalized unlock item schema instead of bespoke booleans:

- `core`
- `webmodule`
- `background`
- `nullProvider`
- `feature`
- `cosmetic`
- `meta`

This lets one card renderer and one reward queue present all unlocks uniformly.

For reward types that can become active immediately, the contract should also support equip/apply semantics:

- `canEquip`
- `equipAction`
- `isEquipped`

### Legacy compatibility rules

Map old booleans into the new system:

- `SavestatesUnlocked` implies `feature:Savestates`
- `RtcUnlocked` and `GhUnlocked` imply the RTC corruption stack is available
- `ImagineUnlocked` implies the late-game advanced corruption stack is available
- Keep old flags for native emulator gating, but derive them from the new progression state during migration and save writes

## Recommended Unlock Equivalence Map

### Tier map

1. Old RTC + Old GH combined -> New RTC + Glitch Harvester module
- Primary unlock surface: webmodule widget
- Secondary native capability: enable RTC/GH controls in emulator UI and menus

2. Old Savestates -> New Time Jump
- Primary unlock surface: webmodule
- Secondary native capability: save-state history and temporal tooling API

3. Old late-game corruption escalation -> New Corruption Slop
- Primary unlock surface: automation overlay/activity
- Secondary native capability: optional native shortcuts or menu entry

4. Old Imagine -> New ImagineBug
- Primary unlock surface: advanced late-game webmodule or RTC-adjacent advanced corruption feature
- Secondary native capability: advanced corruption/repair entry points exposed from the RTC stack

5. Hex Editor
- Baseline tool
- Available from the start
- Not part of procedural unlock progression

### Why not split RTC and GH again

- The Windows app already presents them as one coherent corruption workflow module.
- Reintroducing separate progression states adds friction without meaningful player benefit.
- The important progression beat is unlocking corruption as a discipline, not clicking two adjacent panels at different times.
- Native gating becomes simpler if one milestone flips the whole corruption toolchain from hidden to visible.

## Reward Schedule Proposal

### On every achievement unlock

Always attempt:

1. Roll 1 random locked background
2. Roll 1 random locked null provider
3. Add both to the save if available
4. Enqueue cards for presentation
5. Allow the reward modal to equip one or both immediately into the preferred active background/null-provider slots

If either pool is exhausted:

- Skip exhausted pool
- Optionally replace with currency-like filler reward later if needed
- Never award duplicates unless you intentionally introduce reroll currency later

### On level advance

Keep the current DeckBuilder component reward logic and add milestone module rewards:

1. Award enforced/bundle core cards
2. Award bonus pack if current rules say so
3. If milestone level, add webmodule/feature unlock reward item
4. Enqueue all reward cards as one reward bundle

### On full-collection events

Add one-time moments for:

- All core components unlocked
- All backgrounds unlocked
- All null providers unlocked
- All webmodules unlocked
- Full progression complete

## Architecture Plan

### Phase 1: Progression Data Model

#### Task 1. Define the new progression schema
- Add unlock inventories for webmodules, backgrounds, and null providers to the native save model.
- Add preferred active background and preferred active null-provider fields so these rewards can be equipped like core cards.
- Add a generic pending unlock queue.
- Add migration logic from legacy boolean-only saves.
- Add helper methods for add-if-missing and reward deduplication.

#### Task 2. Unify save DTOs and webmodule save shape
- Extend [Windows/webapi/WebApiModels.cs](Windows/webapi/WebApiModels.cs#L142).
- Extend Windows and root `GameSave` models so native and webmodule layers speak the same shape.
- Extend `Windows/Webmodules/shared/gameSave.js` migration logic so browser consumers preserve the new inventories.

#### Task 3. Add progression service layer
- Introduce a dedicated native service such as `ProgressionUnlockService`.
- Responsibilities:
  - compute reward rolls
  - persist unlocks
  - set and validate equipped background/null-provider selections
  - enqueue pending presentations
  - translate new progression state into legacy booleans where needed

### Phase 2: Authoritative Reward Engine

#### Task 4. Move achievement reward logic into a native-first unlock pipeline
- Hook into the existing achievement registration path in [Windows/NesEmulator/board/Emulator.cs](Windows/NesEmulator/board/Emulator.cs#L582).
- After saving the achievement and setting `LevelCleared`, call the progression service.
- The progression service should:
  - detect first-time achievement only
  - roll random background reward
  - roll random null-provider reward
  - write the save
  - enqueue the pending reward bundle
  - expose equip actions for newly unlocked backgrounds/null providers

#### Task 5. Route level-up rewards through the same engine
- Refactor Continue reward logic so it produces generalized reward items, not just direct save mutations.
- Preserve existing component unlock behavior.
- Add milestone module rewards to the same reward bundle.
- Save and enqueue through one shared pathway.

#### Task 6. Guarantee deterministic, replay-safe reward rolls
- Random selection must be save-aware and duplicate-safe.
- Prefer secure-enough deterministic behavior per event bundle so retries do not create reward drift.
- Decide whether the RNG seed comes from save state, event timestamp, or an incrementing reward counter.

### Phase 3: WinForms-Native Enforcement

#### Task 7. Gate webmodules in native menus
- Filter or disable menu items for locked modules in WinForms.
- Apply gating where modules are listed in [Windows/MainForm/MainForm.Initialization.cs](Windows/MainForm/MainForm.Initialization.cs#L407) and [Windows/MainForm/MainForm.Initialization.cs](Windows/MainForm/MainForm.Initialization.cs#L526).
- Decide UX:
  - hidden until unlocked
  - visible but disabled with lock icon/tooltip

Recommended: visible but disabled for milestone modules, hidden for debug/experimental ones.

#### Task 8. Gate module launching at the native loader layer
- Add a final allow-check before `LoadWebModule` in [Windows/MainForm/MainForm.ViewModes.cs](Windows/MainForm/MainForm.ViewModes.cs#L325).
- Prevent bypass through direct menu invocation or future deep links.

#### Task 9. Gate native emulator UI surfaces consistently
- For the RTC/GH combined unlock, hide or disable native RTC/GH panels unless unlocked.
- Keep legacy flags available for this because the emulator UI already keys off them.
- Derive those flags from the new progression state.

#### Task 9a. Keep Hex Editor baseline
- Do not gate Hex Editor behind progression.
- Treat it as a permanently available tool in native menus and webmodule discovery.

#### Task 10. Gate background and null-provider selection menus
- Reflect the roster as usual, but disable or hide locked entries in:
  - [Windows/MainForm/MainForm.Initialization.cs](Windows/MainForm/MainForm.Initialization.cs#L262)
  - [Windows/MainForm/MainForm.Initialization.cs](Windows/MainForm/MainForm.Initialization.cs#L283)
- Ensure selected values always fall back to an unlocked default.
- Allow newly unlocked backgrounds/null providers to be equipped immediately from the reward flow and reflected in these menus.

### Phase 4: Web API Expansion

#### Task 11. Add unlock roster endpoints
- `GET /api/progression`
- `POST /api/progression/claim-pending`
- `POST /api/progression/acknowledge`
- `GET /api/progression/roster`

Suggested payloads:

- unlocked webmodules
- unlocked backgrounds
- unlocked null providers
- preferred/equipped background
- preferred/equipped null provider
- pending reward bundles
- milestone metadata

#### Task 12. Add roster metadata endpoints for presentation
- Background metadata endpoint with display name, category, rarity, default flag
- Null-provider metadata endpoint with display name, category, preview text
- Webmodule metadata endpoint with title, description, display mode, progression tier

#### Task 13. Make background/null-provider setters progression-aware
- Existing endpoints in [Windows/webapi/WebApiServer.Endpoints.Emulator.cs](Windows/webapi/WebApiServer.Endpoints.Emulator.cs#L152) and [Windows/webapi/WebApiServer.Endpoints.Emulator.cs](Windows/webapi/WebApiServer.Endpoints.Emulator.cs#L205) should reject locked selections.
- This prevents browser-side bypass of native progression.
- Add explicit equip endpoints if needed so the unlock UI can apply a newly rewarded background/null provider without relying on config-only ad hoc calls.

### Phase 5: Reward Presentation System

#### Task 14. Introduce a generic unlock presentation contract
- Every reward item should have:
  - item type
  - internal id
  - display name
  - subtitle
  - description
  - card art type
  - optional equip/apply action
  - optional `equip now` affordance for backgrounds, null providers, and any immediately activatable module/feature

#### Task 15. Build a pending reward inbox flow
- Continue/AchievementsRuntime/Home should be able to fetch pending rewards.
- The frontend presents reward cards and then acknowledges them.
- If the player quits mid-sequence, rewards remain pending.

#### Task 16. Decide where reward presentation lives

Recommended:

- Achievement-origin rewards present from Continue/Achievements return flow
- Level-up bundles present from Continue intermission modal
- Home can show a fallback inbox if pending rewards exist and the player bypassed Continue

### Phase 6: Card System Expansion

#### Task 17. Add card definitions for backgrounds
- Each background gets card metadata and SVG rendering inputs.
- Background cards must support an equip action and equipped-state badge.
- Categories could include:
  - classic
  - abstract
  - reactive
  - atmospheric

#### Task 18. Add card definitions for null providers
- Each null provider becomes a collectible pseudo-core card.
- Use a domain like `NULL` or `PROVIDER` to avoid overloading existing core domains.
- Null-provider cards must support an equip action and equipped-state badge.

#### Task 19. Add card definitions for module unlocks
- Webmodule unlocks should have dedicated reward cards:
  - RTC + GH
- Time Jump
  - Corruption Slop
- ImagineBug

#### Task 20. Add card definitions for native feature unlocks
- Some unlocks remain conceptual features rather than modules, for example:
  - Savestate capability
  - special corruption modes
  - debug or expert toggles

#### Task 21. Generalize SVG card rendering
- The current card pipeline should accept multiple reward domains, not just CPU/PPU/APU/CLOCK/SHADER.
- Add a shared schema that supports:
  - domain
  - id
  - label
  - rating/rarity
  - border style
  - iconography
  - body text

#### Task 22. Add preview and authoring support
- Extend card authoring or metadata storage so new reward types can be previewed and maintained without hardcoding every SVG.

### Phase 7: Reward Pool Curation

#### Task 23. Create unlockable background roster metadata
- Tag defaults vs unlockables.
- Prevent default background variants from being rewarded if they should be baseline.
- Add optional rarity/group tags.

#### Task 24. Create unlockable null-provider roster metadata
- Tag default safe providers like `Static` and `Void` as baseline.
- Put the rest into the achievement reward pool.

#### Task 25. Create unlockable module roster metadata
- Mark each module as:
  - baseline
  - milestone unlock
  - optional unlock
  - debug-only/non-progression

Recommended initial classification:

- baseline: Home, Continue, DeckBuilder, Cores, Options, Story, RomManager, HexEditor
- milestone unlock: GlitchHarvester, TimeJump, CorruptionSlop, ImagineBug
- future milestone unlock: Target the Beam when it exists in the Windows module roster

#### Task 26. Add exclusion policy
- Some modules should not be in progression at all:
  - ApiTest
  - AudioTest
  - XYTest
  - AchievementsTest
  - DeckBuilderCrud
  - Options
  - Story
  - Home
  - Overlay
  - RomManager unless explicitly desired

  Do not exclude HexEditor from baseline availability.

### Phase 8: Frontend Integration

#### Task 27. Update Continue webmodule to read and present new reward bundles
- Merge core rewards, module rewards, background rewards, and null-provider rewards in one modal sequence.
- Allow direct apply where it makes sense.
- Specifically support `equip now` for backgrounds and null providers from the reward cards.

#### Task 28. Update Cores/DeckBuilder UI to display expanded inventory
- Add tabs or sections for:
  - components
  - modules
  - backgrounds
  - null providers
- Add active/equipped markers and selection affordances for backgrounds and null providers.

#### Task 29. Update Home and Options to respect locked state
- Locked modules should not look like immediately available tools.
- Home can tease upcoming modules if desired.

#### Task 30. Add fallback inbox UI
- If rewards are pending and the player did not enter Continue, surface a recoverable reward inbox.

### Phase 9: Migration and Backward Compatibility

#### Task 31. Save migration
- Existing users keep:
  - core ownership
  - level
  - achievements
  - legacy feature booleans
- New inventories should backfill from current selected background/null provider where appropriate.

#### Task 32. Legacy flag derivation
- Preserve native behavior by deriving:
  - `RtcUnlocked`
  - `GhUnlocked`
  - `ImagineUnlocked`
  - `SavestatesUnlocked`
from the new progression state until all call sites are migrated.

#### Task 33. Versioning
- Add save versioning or migration markers so future unlock categories can be added cleanly.

### Phase 10: Testing and Validation

#### Task 34. Unit-test progression service
- Duplicate protection
- empty-pool handling
- milestone rewards
- queue generation
- migration behavior

#### Task 35. Integration-test achievement flow
- Achievement unlock -> save update -> reward queue -> Continue presentation -> acknowledge flow

#### Task 36. Integration-test native menu gating
- Locked modules disabled/hidden
- unlocked modules launchable
- direct setter endpoints reject locked assets

#### Task 37. Integration-test persistence
- Rewards survive restart
- partially presented queues resume correctly
- migrated saves remain valid

## Proposed Delivery Order

### Milestone A: Backend foundation
- New save schema
- progression service
- achievement reward rolls for backgrounds/null providers
- API exposure

### Milestone B: Native gating
- webmodule roster gating
- background/null-provider gating
- legacy flag derivation for RTC/GH native UI

### Milestone C: Reward presentation
- pending reward queue
- Continue-based presentation for achievement and level-up rewards
- generic reward item model

### Milestone D: Card expansion
- card metadata for modules/backgrounds/null providers
- SVG rendering support
- polish for unlock reveal sequences

### Milestone E: Content rollout
- curate module tiers
- curate background roster
- curate null-provider roster
- future Target the Beam integration

## Suggested Concrete First Pass

If the goal is to ship value quickly without boiling the ocean, implement this first:

1. Add `UnlockedWebmodules`, `UnlockedBackgrounds`, `UnlockedNullProviders`, and `PendingUnlocks` to the save.
2. Add `PreferredBackgroundId` and `PreferredNullProviderId` so those rewards can be equipped immediately.
3. On achievement unlock, award one random background and one random null provider.
4. On level 4 milestone, unlock RTC + Glitch Harvester as the first major webmodule reward.
5. Gate WinForms menus and API setters based on unlocked inventories.
6. Present new rewards in Continue using a generic reward-card modal with `equip now` actions.

That slice already delivers the new engagement loop without requiring every future module card on day one.

## Open Decisions

1. Should locked modules be hidden or visible-but-locked in WinForms menus?
2. Should background/null-provider rewards be purely random, or weighted by rarity/theme buckets?
3. Should duplicate-eligible pools eventually convert into reroll currency or dust once completed?
4. Should RTC + GH unlock also auto-enable relevant native emulator panels, or only the widget module?
5. Should rewarded backgrounds/null providers auto-equip by default, or only on explicit user action in the reward modal?
6. When Target the Beam lands, should it replace ImagineBug as the late-game milestone, or sit above it as a postgame unlock?

## Acceptance Criteria

The system is complete when:

1. Achievement unlocks can award randomized backgrounds and null providers from native-owned pools.
2. Rewarded backgrounds and null providers can be equipped directly from the unlock flow and persisted as active selections.
3. Level-up progression can unlock milestone webmodules through the same reward pipeline.
4. RTC + GH is the first milestone webmodule reward.
5. Hex Editor remains available from the start.
6. WinForms menus, native emulator UI, and webmodules all read the same progression state.
7. Locked modules/assets cannot be selected through browser-side API calls.
8. All unlock categories can be presented as cards, not just core components.
9. Existing saves migrate forward without losing progression.
10. The reward loop produces something visible and satisfying on almost every achievement star.

## Recommended Next Implementation Ticket Split

1. Save schema and migration
2. Progression unlock service
3. Achievement reward roll engine
4. WinForms module/background/null-provider gating
5. Progression API endpoints
6. Continue reward queue presentation
7. Card metadata and SVG support for new reward domains
8. Content curation for unlock pools and milestone tiers
