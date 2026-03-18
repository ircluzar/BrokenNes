# Procedural Unlock Worksheet

## Execution Mode

- [ ] Work autonomously through the full progression implementation unless blocked by missing assets, missing modules, or a hard architectural conflict.
- [ ] Treat the Windows save/backend as the canonical source of truth for all unlock state.
- [ ] Preserve backward compatibility with existing saves while migrating into the new progression ledger.

## Product Decisions Locked In

- [ ] RTC + Glitch Harvester is the first milestone webmodule unlock.
- [ ] Hex Editor remains available from the start.
- [ ] ImagineBug is the current late-game replacement for the old Imagine unlock.
- [ ] Every achievement should try to unlock one background and one null provider.
- [ ] Backgrounds and null providers must be equippable from the reward flow like other core rewards.

## Phase 1: Progression Schema

- [ ] Extend the save model with unlock inventories.
  - [ ] Add `UnlockedWebmodules`.
  - [ ] Add `UnlockedBackgrounds`.
  - [ ] Add `UnlockedNullProviders`.
  - [ ] Add `PreferredBackgroundId`.
  - [ ] Add `PreferredNullProviderId`.
  - [ ] Add `PendingUnlocks` queue.
- [ ] Extend DTOs and browser-side compatibility models.
  - [ ] Update Windows web API save DTOs.
  - [ ] Update native save models.
  - [ ] Update shared webmodule save migration logic.
- [ ] Add migration behavior.
  - [ ] Preserve legacy core inventories.
  - [ ] Preserve level and achievement progress.
  - [ ] Backfill active background/null-provider if possible.
  - [ ] Derive legacy feature booleans from the new progression state.

## Phase 2: Progression Service

- [ ] Introduce a dedicated native progression service.
  - [ ] Load and save progression state.
  - [ ] Add add-if-missing helpers.
  - [ ] Add reward deduplication helpers.
  - [ ] Add equip helpers for backgrounds and null providers.
  - [ ] Add pending reward queue helpers.
- [ ] Define generalized reward item models.
  - [ ] Reward bundle model.
  - [ ] Reward item type enum/schema.
  - [ ] Equip/apply metadata.
  - [ ] Presented/acknowledged state.

## Phase 3: Achievement Rewards

- [ ] Hook the achievement path into the progression service.
  - [ ] Detect first-time achievement unlocks.
  - [ ] Roll one random locked background.
  - [ ] Roll one random locked null provider.
  - [ ] Persist unlocked assets.
  - [ ] Enqueue a reward bundle.
- [ ] Make reward rolls deterministic enough for retries.
  - [ ] Choose a stable event seed or reward counter.
  - [ ] Prevent duplicate rewards on replay.
  - [ ] Handle exhausted pools safely.

## Phase 4: Level-Up Rewards

- [ ] Refactor level-up rewards into the same reward engine.
  - [ ] Keep existing core-card reward behavior.
  - [ ] Preserve bonus pack logic.
  - [ ] Emit milestone module rewards as generalized reward items.
  - [ ] Save and enqueue in one path.
- [ ] Apply the milestone order.
  - [ ] Level 4 -> RTC + Glitch Harvester.
  - [ ] Level 8 -> Time Jump.
  - [ ] Level 12 -> Corruption Slop.
  - [ ] Level 16 -> ImagineBug.

## Phase 5: Native Enforcement

- [ ] Gate milestone webmodules in WinForms menus.
  - [ ] Keep baseline modules always available.
  - [ ] Keep Hex Editor always available.
  - [ ] Disable or hide locked milestone modules consistently.
- [ ] Gate module launching at the native loader.
  - [ ] Add a final permission check before load.
  - [ ] Prevent deep-link or menu bypass.
- [ ] Gate native emulator surfaces.
  - [ ] RTC/GH native UI hidden until unlocked.
  - [ ] ImagineBug-related advanced RTC features hidden until unlocked.
  - [ ] Legacy flags populated from progression state.
- [ ] Gate background and null-provider menus.
  - [ ] Disable or hide locked entries.
  - [ ] Force fallback to unlocked defaults.
  - [ ] Reflect equipped background/null-provider selection.

## Phase 6: API Surface

- [ ] Add progression endpoints.
  - [ ] `GET /api/progression`.
  - [ ] `POST /api/progression/claim-pending`.
  - [ ] `POST /api/progression/acknowledge`.
  - [ ] `GET /api/progression/roster`.
- [ ] Expand roster metadata endpoints.
  - [ ] Background metadata.
  - [ ] Null-provider metadata.
  - [ ] Webmodule metadata.
- [ ] Make existing setter endpoints progression-aware.
  - [ ] Reject locked backgrounds.
  - [ ] Reject locked null providers.
  - [ ] Add explicit equip endpoints if needed.

## Phase 7: Reward Presentation

- [ ] Build a generic reward presentation contract.
  - [ ] Type/id/title/subtitle/description.
  - [ ] Card art metadata.
  - [ ] Equip-now metadata.
  - [ ] Equipped-state metadata.
- [ ] Add pending reward inbox flow.
  - [ ] Claim pending rewards.
  - [ ] Present cards in order.
  - [ ] Acknowledge completion.
  - [ ] Resume after restart if interrupted.
- [ ] Use Continue as the primary reward presentation host.
  - [ ] Achievement return flow.
  - [ ] Level-up intermission flow.
  - [ ] Fallback inbox from Home if needed.

## Phase 8: Equip Flow For Backgrounds And Null Providers

- [ ] Add active/equipped background state.
  - [ ] Save selected background.
  - [ ] Expose selected background via API.
  - [ ] Apply selected background natively.
- [ ] Add active/equipped null-provider state.
  - [ ] Save selected null provider.
  - [ ] Expose selected null provider via API.
  - [ ] Apply selected null provider natively.
- [ ] Support equipping from reward cards.
  - [ ] Equip background immediately from reward modal.
  - [ ] Equip null provider immediately from reward modal.
  - [ ] Reflect equipped state in menus and inventory UI.

## Phase 9: Card Expansion

- [ ] Add card taxonomy for non-core rewards.
  - [ ] Module cards.
  - [ ] Background cards.
  - [ ] Null-provider cards.
  - [ ] Feature cards if still needed.
- [ ] Create module reward cards.
  - [ ] RTC + Glitch Harvester.
  - [ ] Time Jump.
  - [ ] Corruption Slop.
  - [ ] ImagineBug.
- [ ] Create background reward cards.
  - [ ] Add metadata roster.
  - [ ] Add visuals and labels.
  - [ ] Add equip-state rendering.
- [ ] Create null-provider reward cards.
  - [ ] Add metadata roster.
  - [ ] Add visuals and labels.
  - [ ] Add equip-state rendering.
- [ ] Generalize SVG rendering.
  - [ ] Support new reward domains.
  - [ ] Support equip badges.
  - [ ] Support rarity/border/icon variants.

## Phase 10: Reward Pool Curation

- [ ] Curate baseline backgrounds.
  - [ ] Keep default background available from start.
  - [ ] Exclude baseline entries from random reward pools.
- [ ] Curate baseline null providers.
  - [ ] Keep safe defaults available from start.
  - [ ] Exclude baseline entries from random reward pools.
- [ ] Curate module classes.
  - [ ] Baseline modules.
  - [ ] Milestone unlock modules.
  - [ ] Future modules.
  - [ ] Non-progression debug/test modules.

## Phase 11: Frontend Integration

- [ ] Update Continue webmodule.
  - [ ] Read generalized reward bundles.
  - [ ] Render mixed reward types.
  - [ ] Support equip-now for backgrounds/null providers.
  - [ ] Acknowledge processed rewards.
- [ ] Update inventory surfaces.
  - [ ] Show modules collection.
  - [ ] Show backgrounds collection.
  - [ ] Show null providers collection.
  - [ ] Show active/equipped markers.
- [ ] Update Home and Options.
  - [ ] Respect locked modules.
  - [ ] Surface pending reward fallback if needed.

## Phase 12: Testing

- [ ] Unit-test progression state changes.
  - [ ] Unlock deduplication.
  - [ ] Reward queue generation.
  - [ ] Equip-state persistence.
  - [ ] Migration behavior.
- [ ] Integration-test achievement progression.
  - [ ] Achievement unlock -> reward queue.
  - [ ] Reward queue -> presentation.
  - [ ] Presentation -> equip actions.
  - [ ] Equip actions -> native state updates.
- [ ] Integration-test native gating.
  - [ ] Locked modules blocked.
  - [ ] Hex Editor always available.
  - [ ] Locked backgrounds/null providers rejected by API.
- [ ] Integration-test persistence and restart recovery.
  - [ ] Rewards survive restart.
  - [ ] Partially processed queues resume.
  - [ ] Equipped selections survive restart.

## Completion Checklist

- [ ] Every achievement can produce a visible reward beat.
- [ ] Backgrounds and null providers are collectible and equippable.
- [ ] RTC + Glitch Harvester is the first milestone unlock.
- [ ] Hex Editor remains baseline.
- [ ] ImagineBug is wired in as the current late-game advanced corruption unlock.
- [ ] Native menus, native emulator UI, and webmodules all honor the same progression state.
- [ ] All reward types can be represented as cards.
- [ ] Existing saves migrate cleanly.