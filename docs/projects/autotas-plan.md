# AutoTAS plan (pilot: SMB3)

Goal: every catalogued game gets a chaptered TAS with milestone save states, built by
Haiku 5.5 LOW agents, playable from an "AutoTAS" activity in BrokenNes. Quality first,
speed second. Popular games first. Desktop first, Lite later.

## 1. Package format

Folder on T: (never in git; holds commercial-ROM states):

```
T:\BrokenDev\Catalog\tas\<console>\<game_id>\v<N>\
  autotas.json          manifest (below)
  full.bnm              stitched movie, power-on to last accepted chapter
  seg\<chapter>.bnm     one segment per chapter, starts from previous chapter's raw state
  state\<chapter>.raw.state    milestone state exactly as reached
  state\<chapter>.clean.state  clean-up pass applied (section 3)
  thumb\<chapter>.png   frame at chapter start
  notes\<chapter>.md    agent notes: route, tricks, retries, judge verdict
  cleanup.json          per-game clean-up recipe (shared by all chapters)
```

`autotas.json`:
```json
{ "game_id": "...", "rom_sha1": "...", "version": 1,
  "core": {"cpu":"CPU_FIX","ppu":"PPU_FIX","apu":"APU_FIX","build":"<git sha>","state_format":<int>},
  "chapters": [ { "id":"w1-1", "title":"World 1-1", "kind":"level|boss|area|map",
      "start_frame":1234, "end_frame":2345, "seg":"seg/w1-1.bnm",
      "raw":"state/w1-1.raw.state", "clean":"state/w1-1.clean.state",
      "keyframe_hash":"...", "quality":{"deaths":0,"frames":1111,"score":"A","notes":"notes/w1-1.md"},
      "accepted_by":"agent id", "retries":2 } ] }
```
- Chapter `start_frame` = movie frame where the chapter's raw state was saved; end = next chapter's start.
- `full.bnm` = concatenation of segments; its named marks are the chapter ids; replay of
  full.bnm from power-on must hit every chapter's keyframe hash (the verify gate).
- Versioning: states are bound to `core.state_format` + build. A core change bumps nothing
  automatically; a re-verify job replays full.bnm and re-saves states (new `v<N+1>` only if the
  movie itself had to change). The app refuses a state whose state_format mismatches and offers
  "replay to chapter" from full.bnm instead.

Catalog db (`catalog.py`), new table `autotas`: game_id, version, status
(none|building|pilot|complete), chapters_done, chapters_total, last_chapter, core_build,
state_format, path, verified_at, quality_avg. A game is "AutoTAS available" when status is
pilot or complete and verify passed.

## 2. Agent protocol (Haiku 5.5 LOW, one chapter per job)

Inputs to a job: manifest, previous chapter's raw state, its notes, the chapter plan entry
(milestone RAM condition, kind, par frames if known), game playbook.

Loop:
1. `load_state` previous raw state (chapter 0 = power-on) on a fleet emulator (bridge
   `node mcp.mjs start --pc <PC>`; local only if fleet full).
2. Record a segment: `play_step` / `record` with input per step, checking the milestone
   condition every frame batch (`run_until` on the RAM condition where possible).
3. At milestone: `record` mark `<chapter>` + named state; screenshot thumbnail.
4. Judge (same agent, fixed checklist, rejects if any fail):
   - no death, no lost power-up unless the route needs it;
   - frames <= 1.3 x par (par = best known / previous attempt);
   - no idle stretches > 3 s, no backtracking without purpose;
   - no glitch/corruption (screenshot sanity, no jank flags from checks);
   - ends in a state that makes sense to start the next chapter from.
5. Fail: retry from the same start state (or from an intra-chapter sub-state the agent saved
   at a checkpoint). Max 5 retries per chapter; then accept best attempt marked `score:"C"`
   with notes, or escalate (status `needs_help`) if it died.
6. Accept: write seg, raw state, notes (route, inputs that worked, what failed), update
   manifest. Re-replay the segment from start state once: hash must match (determinism gate).
7. Handoff: next job = next chapter, started from this raw state + these notes. Chapters run
   strictly in order per game; different games run in parallel across the fleet.

A later optimise pass (optional, any agent) may redo a chapter that beats the accepted one on
frames with equal quality; it must re-run all later chapters' replays, so it happens only per
world boundary.

## 3. Clean-up pass ("jump forward in time")

The clean state should look like a fresh, reasonable player arriving there, not "continuing
someone's run". Rules: reset counters to defaults (lives = start value, score = 0 or a plausible
par, coins = 0, timer = level default), power-up = the game's default for that point (SMB3: Big
Mario, not Small, no hammer/frog suits), inventory = small sensible set, progression flags = exactly
what the route requires (cleared levels on map, opened locks), RNG untouched.

`cleanup.json`:
```json
{ "rules": [ {"name":"lives","addr":"0x0736","value":4},
             {"name":"score","addr":"0x0715","len":3,"value":0},
             {"name":"coins","addr":"0x7DA2","value":0},
             {"name":"powerup","addr":"0x00ED","value":1,"only_kind":["map"]} ],
  "apply_at": "map|level_start", "notes": "..." }
```
Apply = `load_state raw` -> `write_memory` each rule -> `run 1` -> `save_state clean`.
Prefer chapter starts on the world map (SMB3) so writes happen while the game is not
mid-level (fewer derived copies to miss).

Verify each clean state: (a) run 600 frames with no input + with the chapter's segment input;
segment must still reach the milestone (desync allowed only if it's from intentional RAM
changes, e.g. power-up; then agent re-records a clean-start segment flagged `clean_seg`);
(b) screenshot HUD shows the cleaned values; (c) checks report clean. All addresses are
verified with `search_memory` before first use (step 0 of the pilot), then frozen in the file.

## 4. SMB3 pilot chapter plan

RAM (verify at step 0; Data Crystal / SMB3 disassembly names): world `$0727` (0 = W1),
lives `$0736`, power-up `$00ED`, map-cleared bits `$7D00+` (per-world completion table),
level-in-progress `$0014`-style mode flags; "Level exit" when the game returns to map mode and
the cleared bit for the tile becomes set. Agent step 0 finds the exact mode byte and cleared
table with `search_memory` diffs before/after clearing 1-1 and writes them into the plan's
`milestones.json`.

| # | id | kind | milestone condition |
|---|----|------|---------------------|
| 0 | boot | area | title -> world map W1 first frame (map mode, world=0) |
| 1 | w1-1 | level | map mode, cleared bit 1-1 set |
| 2 | w1-2 | level | cleared bit 1-2 set |
| 3 | w1-3 | level | cleared bit 1-3 set |
| 4 | w1-fort | boss | fortress cleared (lock removed) |
| 5 | w1-4 | level | cleared bit 1-4 set |
| 6 | w1-5 | level | cleared bit 1-5 set |
| 7 | w1-6 | level | cleared bit 1-6 set |
| 8 | w1-ship | boss | world becomes 1 (W2 map first frame) after king scene |

Map moves are part of the following chapter's segment (chapter starts on map, ends back on
map); toad houses skipped unless the route needs an item. Minimum = World 1 complete
(chapters 0-8). Continue into World 2 with the same protocol if World 1 averages score A/B and
determinism + clean verify pass for every chapter; stop at the end of whichever world is in
progress when quality slips or at the usage cap.

## 5. AutoTAS activity (desktop)

- Offered: on ROM load, look up rom sha in catalog `autotas`; if available, the Activities
  menu shows "AutoTAS" (plus a small toolbar badge). Never shown otherwise.
- Built like the existing side-panel activities (Butchering/Timeflow pattern in Windows/):
  docked right panel, toggled from the Activities menu or F-key.
- Panel:
  - Header: game, TAS version, core match status (green / "replay needed").
  - Transport: Play TAS from here, Pause, Stop, speed (1x/2x/4x/max).
  - Chapter list: thumbnail, title, kind icon, duration; current chapter highlighted.
  - Per chapter: Jump (clean) [default], Jump (raw), Watch from here.
  - Take control: stops movie playback, hands the pad to the player at the current frame
    (state kept so "Return" restores it).
  - Return: back to the state/frame before the last jump or take-over.
  - Notes toggle: shows the chapter's notes.md quality notes.
- Jump = load state (no movie rewind needed); if state_format mismatches, it replays
  full.bnm at max speed to the chapter mark with a progress bar.
- Lite gets the same panel later reading the package via the app's file picker.

## Pilot deliverables (order)
1. Package loader + catalog table + manifest schema (+ tests on a synthetic package).
2. SMB3 step 0 (RAM verify, milestones.json, cleanup.json).
3. Chapters 0-8 by agent jobs on the fleet.
4. Clean-up pass + verify.
5. AutoTAS side panel in desktop, tested on the SMB3 package.

## Tools (built, branch autotas-tools)

The format and the agents' calls, as built. Code: `Windows/NesEmulator/debug/movies/AutoTasPackage.cs` (package, manifest, stitcher,
milestones, state files; no emulator dependency, so the desktop panel can use it), `BrokenDev/mcp/HeadlessSession.AutoTas.cs`,
`BrokenDev/mcp/AutoTasCli.cs`, `BrokenDev/tools/catalog/catalog.py` (`autotas` record field).

**State files** (`state\<ch>.raw.state`): `bnstate 1` + header lines (console, cores, frame, rom_sha256, format) + a blank line + the
emulator's own state, gzip. The manifest's `core.state_format` is `<console>:<cores>:gzip1`.

**One chapter job (MCP, also over the fleet bridge):**

```
open_rom      path=<rom>
load_state    path=<pkg>/state/<prev>.raw.state     (chapter 0: skip, it starts at power-on)
record        action=start text=<ch>
play_step     buttons=[...] frames=20 milestone_file=<pkg>/milestones.json    (or milestones=["1-1=<expr>"]) ... repeat
              -> stoppedBy "milestone", milestone.id = the chapter that ended
record        action=mark text="milestone <ch>" state_path=<pkg>/state/<ch>.raw.state
record        action=stop path=<pkg>/seg/<ch>.bnm
```

A movie recorded after `load_state path` starts from that state (`start state`); every session movie now ends on a keyframe, which
is the chapter's end hash. Over the bridge (`--pc N`), `load_state path` is pushed to the PC, `state_path` and the segment are pulled
back to the asked path, and `milestone_file` is read on this side and sent inline. The fleet PCs need the new MCP build deployed first.

**Package:** `AutoTasPackage.PutChapter` (adds a chapter from its segment), `StitchAndSave` (full.bnm with a `chapter <id>: <title>`
mark per chapter, chapter frames and hashes fixed from it), then:

```
BrokenNes.Mcp.exe --autotas stitch --package <pkg>
BrokenNes.Mcp.exe --autotas verify --package <pkg> --rom <rom>    replays full.bnm from power-on, checks every keyframe and chapter end; writes verified_at
BrokenNes.Mcp.exe --autotas info   --package <pkg>
python BrokenDev/tools/catalog/catalog.py autotas-set --console nes --sha1 <sha1> --manifest <pkg>/autotas.json [--status pilot]
python BrokenDev/tools/catalog/catalog.py autotas-get --console nes --sha1 <sha1>       exit 0 = the app offers AutoTAS
python BrokenDev/tools/catalog/catalog.py autotas-list [--available]
```

The `autotas` object on a catalog record: game_id, version, status, chapters_done, chapters_total, core, core_build, state_format,
path (relative to the catalog root), verified_at (only when the verify passed), quality_avg (A=3, B=2, C=1). Available = status
pilot/complete and verified_at set. It is in the index rows too.

**Tests:** `BrokenNes.Mcp.exe --autotas-test` (the movie tests' own game: two chapters across two sessions, a state file, both
milestone forms, stitch, verify from power-on, a broken hash must fail) and `python -I BrokenDev/tools/catalog/test_autotas.py [manifest]`.

### Python tooling and fleet (2026-11-15)
Generic agent tooling is in `BrokenDev/tools/autotas/lib/` (bridge, chapter, package, game); per-game intel as data in
`BrokenDev/tools/autotas/games/<game>/` (SMB3 first, all entries unverified until step 0). Proven end to end on fleet PC PEARL
(`tests/test_fleet_e2e.py`: save_state path, record mark state_path and load_state path through the bridge, stitch + verify 2/2).
Only PEARL runs the new MCP build: Application Control blocked it on the other six PCs. See `BrokenDev/tools/autotas/README.md`.
