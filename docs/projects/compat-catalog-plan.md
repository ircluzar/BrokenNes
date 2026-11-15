# The Great Compatibility Catalog: plan

Started 2026-11-14. Goal: make the general compatibility of every emulator in BrokenNes as good as possible, by having a swarm of Haiku 5.5 agents play every game on every supported console, record each play as a TAS, take notes, and compare against a reference emulator. N64 and x86 are out of the game catalog; x86 gets a test-program benchmark track instead.

Inventory of what exists today (consoles, cores, ROM counts, tools, TAS formats, gaps): `docs/projects/compat-catalog-inventory.md` (being written; numbers below get filled in from it).

## Decisions (interview 2026-11-14)

| # | Question | Decision |
|---|---|---|
| 1 | How deep is each game played? | **Tiered.** T1: every game gets a scripted boot, title and first-minutes check. T2: games that pass get a longer agent play (first level, 10-30 min) recorded as a TAS. T3: chosen games get a full end-to-end TAS. |
| 2 | Which library? | **One dump per title**: one verified dump per unique game ([!], prefer USA/World, then Europe/Japan); no hacks, translations or bad dumps; revisions only where they matter. |
| 3 | Console order | **NES first** (most mature tooling: FIX cores, Mesen lockstep, fm2), then SNES, Genesis, SMS/GG, GB/GBC, WideMess. |
| 4 | Fleet | **7 Intel Core i3-7100T boxes** (2 cores / 4 threads, 16 GB each) **plus this machine**. |
| 5 | Fleet setup | **Network share from this PC**: Windows boxes read ROMs and tools from shares of X: and T:, write results to a shared results folder; this box coordinates. |
| 6 | When a game is broken | **Catalog first, fix in waves**: Haiku agents only play, record and note. Problems are grouped by cause; stronger agents on this box fix cores in waves, golden-tested; the swarm re-checks the affected games. |
| 7 | Reference emulator | **Live side by side**: the agent plays BrokenNes and the reference emulator at the same time (Mesen for NES/SNES/GB/SMS/GG, Genesis Plus GX for Genesis). |
| 8 | Where the catalog lives | **BrokenDev + live artifact**: data on T: (database, TAS files, notes, screenshots); a BrokenDev view to browse it; a live dashboard artifact with progress per console. |
| 9 | Cores tested | **Accuracy core only** per console (FIX family on NES, the SFC / GB / Sega defaults). Gimmick and art cores are out of scope. |
| 10 | Budget | **Pilot first**: ~50 NES games through all tiers, measure tokens and hours per game per tier, then set the pace. |

## Phases

### P0. Inventory and gap fixing (this box)
- Finish the inventory: unique-title counts per console, tool list, TAS formats, determinism status, reference emulators.
- Fix what blocks a swarm before it starts: movie recording/replay on every console's accuracy core, savestates, a fast agent loop over MCP (frame advance, screenshot, input, RAM read), progress signals (RAM watches for level/lives/score per game where known), and a way to drive the reference emulator in lock step with BrokenNes for the live side-by-side play.
- Build the title list per console: dedupe the GoodTools sets to one dump per title, with the console's game db ids.

### P1. Catalog format and storage
- One record per game: title, console, dump hash, tier reached, grade (BrokenNes's own scale: Nothing / Broken / Jank / OK / Certified, plus Not tested), the agent's notes, screenshots at checkpoints, the TAS file, the reference comparison result, problem tags (picture, sound, timing, crash, input, mapper/board, save), and the core/version tested.
- Storage on T: (catalog database + TAS files + screenshots). TAS files hold inputs only, never ROM data. Commercial ROMs and their screenshots never go in git.
- BrokenDev view to browse and filter it; live dashboard artifact updated as games complete.

### P2. Fleet packaging
- A self-installing worker for the i3 boxes: BrokenNes headless build, the MCP server, the reference emulators, the agent runner, all reading from the shares. One install script, pinned versions.
- A job queue on this box: game, tier, console; workers pull jobs, report results, retry on failure. Each i3 box runs 1-2 games at a time (4 threads, 16 GB).
- Health checks: worker heartbeat, stuck-game timeout, result validation before a record is accepted.

### P3. Swarm agents
- **T1 checker** (mostly scripted, little or no model use): boot, wait for title, press Start, run a few minutes of a generic input script, compare against the reference, grade.
- **T2 player** (Haiku 5.5): plays the first level/area by looking at screenshots and RAM, side by side with the reference emulator, records the TAS, writes notes on anything that differs or looks wrong.
- **T3 runner** (Haiku 5.5, long-running): full playthroughs for chosen games, saving progress checkpoints so a run can resume on another box.
- Prompts and per-console playbooks (controls, how to start a game, common menus) written once and reused.

### P4. Pilot (NES, ~50 games)
- All tiers on a spread of mappers and genres, including known-good and known-broken games from the certification campaign.
- Measure tokens per game per tier, hours per game per tier per box, false-alarm rate of the side-by-side comparison, and how often agents get stuck.
- Then set the pace and the fleet split with the user.

### P5. Full NES run, then each console in order
- Fan out across the fleet; the dashboard tracks progress.
- Fix waves on this box: group problems by cause, fix the accuracy core, golden-test, re-queue the affected games.

### P6. x86 test-program benchmark
- Run x86 test suites and benchmark programs on BrokenNes's x86 console to push its accuracy and speed; no game catalog.

## Open questions (for later interviews)
- Which games get T3 full playthroughs, and how they're picked.
- What exactly is compared in the live side-by-side play (pictures, RAM, sound), and the tolerance before a game is flagged.
- WideMess has no commercial library: whether it's covered by SNES games running on it, or only by our own games.
- How the TAS archive is replayed in BrokenNes later (the long-term goal of a full scripted archive).
- Whether the catalog later feeds the BrokenNes app's game browser.
