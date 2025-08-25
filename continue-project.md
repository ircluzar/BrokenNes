# Continue — Workflow Engine Project Document

> Working draft for the game workflow engine (loop, state, progression goals, goal evaluation). This doc is the single source of truth for runtime/state diagrams, data contracts, and implementation details.

---

## 0) Goals & Non‑Goals

**Goals**

* Deterministic game loop that drives: level requirements → emulator session → achievement unlock → progression.
* Pluggable achievement monitoring bound to a single active emulator instance.
* Portable database that lives in IndexedDB at runtime and can be exported/imported as JSON to seed a bundled default DB in the repo.
* Clear separation between **content data** (games, achievements, cards, levels) and **player state** (save, inventory, progress).

**Non‑Goals (for now)**

* Global achievements scanning across all games simultaneously.
* Anti‑cheat beyond basic sanity checks.
* Multiplayer or cloud sync.

---

## 1) Architecture Overview

* **UI Shell (Web)**: Continue page, ROM pre-registration, level selection, emulator embedding, debug CRUD, notifications.
* **Runtime Core (C#)**: Workflow state machine + achievement watcher engine.
* **Emulator Host**: Single emulator process/instance per play session. (Exact backend—native/WASM—doesn’t matter to the contracts.)
* **Persistence**: In-memory domain models with IndexedDB store; JSON import/export to/from a bundled default DB.
* **IPC/Bridge**: JS ↔ C# messaging. Watchers live in C#; UI subscribes to events.

```
UI (TS/JS)  ⇄  Workflow Core (C#)  ⇄  Emulator Instance
             ▲            │              │
             │            ▼              │
       IndexedDB  ⇄  In-memory DB        │
             ▲            │              │
             └──────── JSON Import/Export┘
```

---

## 2) Core Concepts

### 2.1 Game Identification

* **GameID**: Stable identifier derived (preferably) from ROM header metadata.

  * Candidate: iNES header fields + SHA-1 of PRG/CHR sections (or a canonical hash of header + first N bytes).
  * Stored as `GAME_<system>_<hash>` (uppercase, `_` delimited).
* **Compatibility**: A game is *compatible* if it exists in the DB with at least one defined achievement.

### 2.2 Achievements

* Unlocked **once per gamesave**.
* **ID format**: Uppercase with `_` delimiters, e.g., `NES_SUPER_MARIO_1_1_FLAGPOLE`.
* Each achievement defines a **watch formula** (see §6) that compiles to a `Watcher` in C#.
* Only a **handful** of achievements are monitored concurrently (max N active watchers per session).

### 2.3 Cards & Console Build

* **Cards** represent emulator components/cores. A *console* for a level is the set of required + player-selected cards.
* Every **4th level** introduces a *Card Challenge*: player is given a set of component cards and must earn **1 achievement** using that build.

### 2.4 Progression & Stars

* **Stars** = total unlocked achievements (global counter in the save).
* Each level defines a **star threshold** to advance.
* Completing a level grants the forced card(s) to the player’s inventory.

---

## 3) Game Loop (Happy Path)

1. **Continue Page → Level Start**

   * Determine current level `L` from save.
   * Resolve **required card(s)** for `L`. Player can slot optional cards from inventory to complete the build.
2. **Select ROM**

   * Player chooses a ROM (from pre-registered list) that:

     * is compatible, and
     * has **uncompleted** achievements.
3. **Assign Achievements**

   * Engine samples **5 random** uncompleted achievements for that game. These become the **Active Monitored Set**.
4. **Start Emulator**

   * Boot with the selected console build + ROM. UI shows the list of 5 currently monitored achievements.
5. **Unlock**

   * When **any one** monitored achievement triggers:

     * Pause emulator.
     * Show toast/modal: *Achievement Get!*.
     * Persist unlock → increment star counter.
     * Tear down all active watchers.
     * Return to Continue page.
6. **Advance Check**

   * If `save.stars >= level[L].requiredStars`, advance to level `L+1`, grant any forced card rewards, and repeat.

**Constraint**: only **1 achievement** may be completed per emulator instance.

---

## 4) Workflow Engine — State Machine

### 4.1 States

* `Idle` → `LevelReady` → `BuildReady` → `RomSelected` → `Monitoring` → (`Unlocked` | `Aborted`) → `Idle`

### 4.2 Transitions

* `Idle → LevelReady`: load current level config.
* `LevelReady → BuildReady`: required cards applied; user picks optional cards; validate build.
* `BuildReady → RomSelected`: user picks ROM with available achievements.
* `RomSelected → Monitoring`: assign 5 achievements; instantiate watchers; boot emulator.
* `Monitoring → Unlocked`: any watcher triggers → pause → persist → tear down → notify.
* `Monitoring → Aborted`: user exits or emulator hard-stop → tear down watchers.
* `Unlocked/Aborted → Idle`: return to Continue page; check advancement.

### 4.3 Signals/Events

* `WatcherTriggered(achievementId)` (C# → JS)
* `EmulatorPaused()` (C# → JS)
* `SaveUpdated()` (C# → JS)
* `StartSession(build, romId)` (JS → C#)
* `AssignAchievements([ids])` (JS → C#)
* `StopSession()` (JS → C#)

---

## 5) Data Model

### 5.1 Content DB (bundled & editable)

* `Game`:

  * `id: string` (GameID)
  * `title: string`
  * `system: string` (e.g., `nes`)
  * `headerSignature: string` (hash)
  * `notes?: string`
* `Achievement`:

  * `id: string`
  * `gameId: string`
  * `title: string`
  * `description?: string`
  * `watchFormula: string` (DSL in §6)
  * `tags?: string[]`
  * `difficulty?: 1|2|3|4|5`
* `Card`:

  * `id: string`
  * `type: string` (e.g., `apu`, `ppu`, `mapper`)
  * `constraints?: object`
* `Level`:

  * `index: number`
  * `requiredCards: string[]` (card IDs)
  * `requiredStars: number`
  * `isCardChallenge: boolean` (true every 4th level)
  * `challengeCardPool?: string[]` (cards to grant/require on challenge)

### 5.2 Player Save

* `Save`:

  * `version: number`
  * `stars: number`
  * `currentLevel: number`
  * `unlockedAchievements: string[]`
  * `inventoryCards: string[]`
  * `romRegistry: RomEntry[]`
* `RomEntry`:

  * `id: string` (user’s local ROM id)
  * `gameId?: string` (resolved via matching)
  * `pathOrHandle: string` (opaque handle)
  * `compatible: boolean`
  * `hasAchievements: boolean`

### 5.3 JSON Export/Import

* Entire **Content DB** + **Save** can be exported as a single JSON document:

```json
{
  "meta": { "format": 1, "exportedAt": "2025-08-25T00:00:00Z" },
  "content": {
    "games": [ /* Game[] */ ],
    "achievements": [ /* Achievement[] */ ],
    "cards": [ /* Card[] */ ],
    "levels": [ /* Level[] */ ]
  },
  "save": { /* Save */ }
}
```

**Promotion Flow**: edit via CRUD → export JSON → commit as `default-db.json` in repo → app loads it as seed for IndexedDB.

### 5.4 IndexedDB Layout

* DB name: `continue-db`
* Object stores (by key):

  * `games (id)`
  * `achievements (id)` with index `by_gameId`
  * `cards (id)`
  * `levels (index)`
  * `save (singleton-key)`

---

## 6) Achievement Watch Formulas (DSL)

### 6.1 Requirements

* Authorable as **string** in CRUD.
* Parsed in C# into a `WatcherGraph`.
* Supports **memory reads**, comparisons, logical ops, timers, edge detection, and latching.

### 6.2 Proposed Mini‑DSL

Grammar sketch (informal):

```
expr := orExpr
orExpr := andExpr ("||" andExpr)*
andExpr := notExpr ("&&" notExpr)*
notExpr := ("!" notExpr) | primary
primary := cmp | group | timer | edge
cmp := read op value
op := "==" | "!=" | ">" | ">=" | "<" | "<="
read := sys ":" space "[" addr (":" size)? "]"
sys := "cpu" | "ppu" | "apu" | "ram"
space := "mem" | "reg"
addr := hex | symbol
size := 1|2|4
value := number | hex

group := "(" expr ")"
edge := "edge(" read "," value ")"   // rising edge to value
timer := "for(" expr "," ms ")"      // expr holds for ms
```

Examples:

* `ram:mem[0x075A] == 1 && edge(ram:mem[0x075A], 1)` → hit a flag when it flips to 1.
* `for(cpu:reg[PC] == 0xC123, 5000)` → PC at routine for 5s.
* `(ppu:mem[0x2002] & 0x80) == 0` → bitmask allowed via `&` literal in value side.

### 6.3 Watcher Lifecycle

* `Compile(watchFormula) -> WatcherGraph`
* `Attach(EmulatorInstance)` → subscribes to frame or cycle callbacks.
* `Tick()` on each frame/callback.
* On `Satisfied` → engine emits `WatcherTriggered(achievementId)` and disposes the watcher.

### 6.4 Performance Constraints

* Max **N = 5** concurrent watchers.
* Memory reads batched per frame; prefer coalesced ranges.

---

## 7) Emulator Integration (C#)

### 7.1 Contracts

```csharp
interface IEmulator
{
    event Action OnFrame;
    byte ReadCpuMem(ushort addr);
    ushort ReadCpuReg(CpuRegister r);
    byte ReadPpuMem(ushort addr);
    // ... other subsystems as needed
    void Pause();
}

record StartSessionRequest(ConsoleBuild Build, string RomId, string GameId);
```

### 7.2 Session Flow

1. `StartSession(build, romId)` (JS → C#).
2. Resolve `gameId` via ROM match; validate `compatible` and uncompleted pool.
3. Pick `N=5` achievements → `AssignAchievements([ids])` and compile watchers.
4. Hook `OnFrame` to batch `Tick()`.
5. On first watcher satisfied → `Pause()` → `WatcherTriggered(id)`.

### 7.3 Messaging (typings)

```ts
// JS receives
type WatcherTriggered = { type: 'watcherTriggered', achievementId: string };

// JS sends
type StartSession = { type: 'startSession', build: ConsoleBuild, romId: string };
```

---

## 8) Pre‑Registering Games (ROM Listing)

### 8.1 Flow

1. User adds ROMs (file handles or references).
2. System extracts header/hash → attempts DB match.
3. Record saved to `romRegistry` with `compatible` + `hasAchievements` flags.

### 8.2 UI (non-emulator page)

* Table with columns: Title, System, Local ID, Match Status, Achievements (#/total), Notes.
* Action: *Open Debug CRUD*, *Scan Headers*, *Export DB JSON*, *Import DB JSON*.

---

## 9) Level System

### 9.1 Definition

* `Level.index` monotonically increases from 1.
* `requiredCards` must be slotted before ROM selection.
* `requiredStars` gate progression.
* `isCardChallenge = (index % 4 == 0)`.

### 9.2 Rules

* Player must use the **required** card(s) as cores in the build.
* May add optional cards from inventory.
* On advancing to next level, grant the level’s **forced** card(s) to inventory.

### 9.3 Assignment of Achievements per Level

* After ROM selection, sample 5 **unlocked** achievements from that game.
* Sampling strategy: uniform random, excluding already-unlocked IDs.

---

## 10) UI/UX Contracts

* **Continue Page**: shows current level, required cards, stars, next threshold, and *Play* CTA.
* **Build Pane**: drag-drop cards; validation feedback.
* **ROM Picker**: filters to compatible ROMs with remaining achievements.
* **Session HUD**: under emulator viewport, list **5 monitored achievements** (title + short hint).
* **Achievement Get!**: modal with achievement title, +1 star, *Return to Continue* button.
* **Debug CRUD**: grid editors for Games, Achievements, Cards, Levels; JSON import/export buttons.

---

## 11) Progression Logic (Pseudo)

```csharp
bool TryAdvance(Save save, Level current)
{
    if (save.stars >= current.requiredStars)
    {
        save.currentLevel++;
        var next = Levels.Get(save.currentLevel);
        foreach (var forced in next.requiredCards)
            if (!save.inventoryCards.Contains(forced)) save.inventoryCards.Add(forced);
        return true;
    }
    return false;
}
```

---

## 12) Validation & Edge Cases

* **No compatible ROMs**: block *Play*, prompt to pre-register games or import DB.
* **Game without achievements**: mark as incompatible for progression (can still play casually in future modes).
* **Emulator crash/exit**: return `Aborted` → no progress.
* **Duplicate unlock**: ignore if `achievementId` in `save.unlockedAchievements`.
* **Watcher starvation**: if any watcher cannot read (e.g., uninitialized memory), it must stay `Unsatisfied` without crashing.

---

## 13) Security / Integrity (lightweight)

* Keep a per-unlock **proof** blob (timestamp, gameId, hashes of last N reads leading to satisfaction) for auditing.
* Basic rate limiting: one unlock per session enforced in engine.

---

## 14) Telemetry (optional)

* Counters: sessions started, aborts, unlock time (ms), per-achievement difficulty (empirical).

---

## 15) Open Questions / TODO

* Finalize GameID hashing scheme (iNES + PRG/CHR SHA-1 vs. alternative).
* Define exact **card** catalog and constraints.
* Authoring guidelines for the DSL, unit tests for parser and evaluation.
* UX for revealing partial hints vs. full descriptions.
* Balance for `requiredStars` per level; initial curve proposal TBD.

---

## 16) Appendix — Minimal Samples

### 16.1 Sample Achievement JSON

```json
{
  "id": "NES_SMB1_WORLD1_FLAGPOLE_5000",
  "gameId": "GAME_NES_2F6A...",
  "title": "First Flagpole!",
  "description": "Clear 1-1 and touch the flagpole.",
  "watchFormula": "edge(ram:mem[0x075A],1) && for(ram:mem[0x075A]==1, 500)",
  "difficulty": 1,
  "tags": ["progression"]
}
```

### 16.2 Sample Level JSON

```json
{
  "index": 4,
  "requiredCards": ["CARD_MAPPER_002"],
  "requiredStars": 12,
  "isCardChallenge": true,
  "challengeCardPool": ["CARD_PPU_A", "CARD_APU_B"]
}
```

### 16.3 Session Event Flow

```
JS: startSession(build, romId)
C#: validate → pick 5 → compile watchers → boot
C#: watcherTriggered(achId) → pause → persist
JS: show modal → back to Continue → tryAdvance()
```

---

## 17) Definition of Done (initial milestone)

* [ ] IndexedDB schema + JSON import/export implemented.
* [ ] Debug CRUD for Games/Achievements/Cards/Levels.
* [ ] DSL parser & runtime with unit tests (covers cmp/and/or/not/edge/for).
* [ ] Emulator bridge with batch memory reads and frame ticks.
* [ ] Workflow state machine covering all transitions.
* [ ] Continue/Build/ROM Picker/Session HUD/Modal implemented.
* [ ] One end-to-end path: unlock any sample achievement → star increments → level advance.
