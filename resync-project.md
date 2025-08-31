# Achievements Engine Resync + Savestate Worksheet

Use this checklist to design, implement, and validate an achievement-state savestate that pairs with emulator savestates, supports Continue flows, and resets correctly on emulator reset.

## Goals
- [ ] Define a compact, versioned savestate format for the achievements engine
- [ ] Snapshot achievements state when an achievement completes (post-flag)
- [ ] Persist snapshots locally per game/ROM so they can be reloaded later
- [ ] Wire Continue flow to restore the latest game + achievement state
- [ ] On emulator Reset (with achievements enabled), resync/reset the achievements engine to power-on state
- [ ] Allow multiple cycles of save/continue without corruption or duplication

## Success criteria (acceptance)
- [ ] After earning an achievement, a paired (game savestate + achievements state) snapshot is stored locally with metadata
- [ ] Pressing Continue in `Continue.razor` loads the most recent valid snapshot and resumes both emulator and achievements engine coherently
- [ ] Emulator Reset clears achievements session state and the engine reinitializes to power-on (beginning-of-game) conditions
- [ ] Can repeat save/continue unlimited times; storage adheres to retention policy; no stale or cross-ROM leakage
- [ ] Format is versioned; incompatible versions are ignored or migrated gracefully

---

## Design overview
- [ ] Contract
  - Inputs: post-completion event (achievement id), current emulator savestate bytes, current achievements runtime state
  - Outputs: Snapshot record { metadata, game savestate blob, achievements state JSON }
  - Error modes: storage failures, oversized blobs, version mismatch; all must fail-safe without crashing gameplay
- [ ] Snapshot timing
  - [ ] Trigger on achievement-completed event AFTER marking it completed in the engine
  - [ ] Debounce/queue if multiple pop at once; still end with a single coherent snapshot
- [ ] Persistence
  - [ ] Store locally per ROM (ROM hash / title + core identifier)
  - [ ] Prefer IndexedDB for binary savestate blobs; small JSON metadata in IndexedDB or localStorage
  - [ ] Retention policy: keep last N snapshots per game (default N=5, configurable); always keep the newest
- [ ] Restore flow
  - [ ] Continue chooses the newest valid snapshot for the current ROM/core
  - [ ] Load emulator savestate first, then restore achievements state, then resume clocks
- [ ] Reset flow
  - [ ] If achievements are enabled and emulator Reset is pressed, call engine ResetToPowerOn (clears session triggers, counters, watch state)
  - [ ] Do not delete persisted snapshots on Reset; only reinit runtime

## Data model and format
- [ ] Versioning
  - [ ] Snapshot schema version (e.g., "achv-snap-v1") stored in metadata
  - [ ] Migration hook for future versions; unknown versions are skipped
- [ ] Snapshot metadata (JSON)
  - [ ] id: GUID/ULID
  - [ ] schemaVersion: string
  - [ ] rom: { sha1|md5, displayName, coreId, system="NES" }
  - [ ] timestamp: ISO8601
  - [ ] sessionFrame|ppuCycles: optional for ordering/debug
  - [ ] achievementsSummary: { completedCount, total, lastUnlockedId }
  - [ ] byteSizes: { gameState, achvState }
- [ ] Achievements state (JSON)
  - [ ] completedIds: string[] (or numeric IDs mapped to RA IDs)
  - [ ] activeTriggers: compact representation of trigger states (progress counters, hit counts)
  - [ ] sessionFlags: hardcore/softcore, leaderboard states (if any), rich presence cache (optional)
  - [ ] engineVersion: semver of achievements engine
- [ ] Game savestate (binary)
  - [ ] Raw Uint8Array stored in IndexedDB (by key id) or compressed (optional)
  - [ ] Base64 is acceptable if IndexedDB helper requires; prefer binary to avoid bloat

## Storage strategy
- [ ] Use JS interop to write/read blobs in IndexedDB (or existing storage helper in project)
- [ ] Keys
  - [ ] gameKey = `${system}:${coreId}:${rom.hash}`
  - [ ] snapshotKey = `${gameKey}:snap:${id}`
  - [ ] latestIndex = `${gameKey}:index` (array of snapshot metadata sorted desc)
- [ ] Retention
  - [ ] On insert, append metadata to index, trim to N, delete evicted snapshot blobs

## Event wiring
- [ ] Achievement completed -> Snapshot
  - [ ] Subscribe to engine event (e.g., OnAchievementUnlocked)
  - [ ] Ensure engine state reflects the completion before serializing
  - [ ] Request emulator savestate bytes (existing API) and pair with achievements state JSON
  - [ ] Persist in background (non-blocking UI); show lightweight toast on success/failure
- [ ] Continue -> Restore
  - [ ] In `Continue.razor`, on "Continue game" button, load latest index for current gameKey
  - [ ] Fetch snapshot blob and metadata; validate hash/core match
  - [ ] Restore emulator savestate; then call engine.Restore(achvState)
  - [ ] Handle missing/corrupt cases with a friendly message and fallback to normal start
- [ ] Emulator Reset -> Resync
  - [ ] Hook Reset button handler; if achievements are enabled: engine.ResetToPowerOn()
  - [ ] Optionally raise a UI notification: "Achievements resynced to power-on"

## Implementation tasks

### 1) Engine surface area
- [ ] Add interface methods
  - [ ] SerializeState(): AchvStateDTO
  - [ ] RestoreState(dto: AchvStateDTO): void
  - [ ] ResetToPowerOn(): void
- [ ] Define AchvStateDTO type and JSON serialization
- [ ] Ensure deterministic ordering of arrays/sets for stable diffs

### 2) Storage helpers
- [ ] Add IStorageSnapshotService abstraction with methods
  - [ ] SaveSnapshot(gameKey, metadata, gameBlob, achvJson)
  - [ ] GetLatestSnapshot(gameKey)
  - [ ] ListSnapshots(gameKey)
  - [ ] PruneSnapshots(gameKey, keepN)
- [ ] Provide WebAssembly implementation using IndexedDB via JS interop
- [ ] Add size guards and error mapping

### 3) Snapshot creation path
- [ ] Subscribe to achievements unlocked event
- [ ] Obtain emulator savestate bytes (existing API)
- [ ] Build metadata from context (ROM hash, coreId, timestamp)
- [ ] Serialize achievements engine state AFTER marking achievement completed
- [ ] Persist snapshot; update index; enforce retention

### 4) Continue.razor integration
- [ ] Detect current ROM/core context
- [ ] Load latest snapshot
- [ ] Restore emulator state
- [ ] Restore achievements state
- [ ] Handle errors/fallbacks and UI states

### 5) Emulator Reset integration
- [ ] In Reset handler, if achievements enabled, call engine.ResetToPowerOn()
- [ ] Clear transient engine caches and rich presence session state
- [ ] Do not delete saved snapshots

### 6) Configuration and UX
- [ ] Settings toggle: enable/disable achievement snapshots (default on)
- [ ] Retention policy setting: keep last N
- [ ] Optional: manual snapshot from UI
- [ ] Toasts for snapshot save success/failure and restore outcomes

### 7) Telemetry and logging (local/dev)
- [ ] Log snapshot creation with sizes and duration
- [ ] Log restore attempts, successes, and reasons for failures

### 8) Tests
- [ ] Unit: AchvStateDTO round-trip (Serialize/Restore)
- [ ] Unit: Storage index retention trimming
- [ ] Unit: Version mismatch handling
- [ ] Integration: Achievement completion -> snapshot persisted
- [ ] Integration: Continue -> emulator+achievements restored
- [ ] Integration: Reset -> engine cleared and resynced

### 9) Docs
- [ ] Update `continue-project.md` to reference snapshots behavior
- [ ] Add developer notes on schema `achv-snap-v1`
- [ ] Add troubleshooting section (storage full, corruption)

---

## Edge cases
- [ ] Multiple achievements unlocked in the same frame (coalesce into one snapshot)
- [ ] Snapshot fails (storage quota) — show non-blocking error; game continues
- [ ] ROM changed between sessions — ignore snapshots with mismatched hash/core
- [ ] Engine version change — attempt migrate; else skip with notice
- [ ] Hardcore/softcore mode change — record in metadata and validate on restore

## Versioning and migration plan
- [ ] Start at schema `achv-snap-v1`
- [ ] Provide a migration adapter map {fromVersion -> migrator}
- [ ] If no migrator, snapshot is skipped (preserve data but don’t load)

## Rollout and validation
- [ ] Feature flag per environment
- [ ] Dogfood on a few ROMs (~3–5) and verify snapshot sizes/times
- [ ] Measure restore reliability and time-to-resume

## Operational runbook
- [ ] How to clear snapshots for a ROM (dev tool/hidden button)
- [ ] How to inspect metadata for debugging
- [ ] How to bump retention safely

---

## Quick references
- [ ] Game key format: `${system}:${coreId}:${romHash}`
- [ ] Snapshot order: newest first in index
- [ ] Restore order: emulator state, then achievements state, then resume

## Open questions
- [ ] Storage backend: IndexedDB only, or fallback to localStorage for tiny states?
- [ ] Compression: gzip/deflate for blobs? Measure if needed
- [ ] Maximum retention default (5? 10?)
