# Achievements Snapshot + Resync — MVP Worksheet

Scope: only what’s required to meet the stated goals with minimum surface area and complexity.

## MVP goals
- [ ] Create an achievement snapshot at the moment an achievement completes (after it is flagged complete)
- [ ] Pair that with an emulator savestate (same moment)
- [ ] Persist a single “latest” snapshot per ROM (overwrite on new unlock)
- [ ] Continue loads and restores the latest snapshot (game + achievements)
- [ ] Emulator Reset (when achievements are enabled) resets the achievements engine to power-on

## Acceptance (MVP)
- [ ] Unlocking an achievement produces one persisted snapshot for that ROM
- [ ] Continue resumes both gameplay and achievement state coherently
- [ ] Reset returns achievements engine to initial state without touching the saved snapshot

---

## Minimal data contract
- [ ] Schema version: "achv-snap-v1"
- [ ] Keying: gameKey = `${system}:${coreId}:${romHash}`
- [ ] Snapshot record
  - [ ] metadata: { schemaVersion, gameKey, timestampISO }
  - [ ] gameState: emulator savestate bytes (binary or base64)
  - [ ] achvState: JSON with just what we need
    - [ ] completedIds: string[] (or numeric IDs)
    - [ ] triggerProgress: minimal counters/hit counts necessary to not lose in-progress chains
    - [ ] mode: { hardcore: bool }

## Minimal storage
- [ ] Keep only the latest snapshot per gameKey (no history/retention UI)
- [ ] Storage backend: IndexedDB via existing or simple JS interop (binary-friendly)
  - If not available, temporary fallback: base64 in localStorage (acceptable for MVP; replace later)
- [ ] Keys
  - [ ] latestKey = `${gameKey}:latest`

## Minimal event wiring
- [ ] OnAchievementUnlocked (after flagging complete)
  - [ ] Request emulator savestate bytes
  - [ ] Serialize achievements state
  - [ ] Persist to `latestKey`
- [ ] Continue (in `Continue.razor`)
  - [ ] Resolve current gameKey
  - [ ] Load `latestKey`; if found: restore emulator state, then restore achievements state
- [ ] Emulator Reset button
  - [ ] If achievements enabled: engine.ResetToPowerOn()
  - [ ] Do not delete `latestKey`

## Minimal implementation steps
1) Engine API surface
   - [ ] SerializeState(): AchvStateDTO (completedIds, triggerProgress, mode)
   - [ ] RestoreState(dto): void
   - [ ] ResetToPowerOn(): void
2) Storage helper (thin)
   - [ ] SaveLatest(gameKey, snapshot)
   - [ ] LoadLatest(gameKey): snapshot | null
3) Snapshot creation
   - [ ] Subscribe to unlock event; after completion -> build dto -> save
4) Continue integration
   - [ ] On button: compute gameKey -> LoadLatest -> restore (game first, then achievements)
5) Reset integration
   - [ ] Hook Reset -> engine.ResetToPowerOn() if achievements enabled

## Minimal edge cases
- [ ] No snapshot found on Continue -> show fallback and start normally
- [ ] Mismatched gameKey (different ROM/core) -> ignore snapshot
- [ ] Storage failure -> ignore and continue gameplay

## Minimal tests (smoke only)
- [ ] AchvStateDTO round-trip (Serialize/Restore)
- [ ] Continue restores after one unlock-created snapshot

## Out of scope (for MVP)
- Retention/history (N>1)
- Migrations/advanced versioning
- Telemetry/logging, compression, UI to manage snapshots
- Manual snapshot button, leaderboards, rich presence persistence
