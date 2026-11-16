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

#### P0 scope (interview 2026-11-14)

| # | Item | Decision |
|---|---|---|
| 1 | Grade store | **New catalog database on T:** (one record per ROM hash: grade, tier, notes, TAS, reference result); BrokenNes's own `default-db.json` is updated from it in batches. |
| 2 | Replay determinism | **Prove and fix on every console's accuracy core** (movie and savestate round trips, frame-hash match) before the swarm reaches that console. |
| 3 | Movie export | **BizHawk `.bk2` export and import for every console** (adds SMS, GG and Genesis); keep `.fm2` for NES/FCEUX. |
| 4 | Agent loop | **A single MCP "step" call** (inputs held for N frames or until a RAM condition, returns a small screenshot plus RAM watches) **and movie recording from headless sessions**. The Sega debugger comes later. |
| 5 | Live reference | **Behind the same step call**: it drives BrokenNes and the reference emulator in lock step (Mesen through Lua/test-runner for NES, SNES, GB, SMS, GG; Genesis Plus GX through BizHawk's Lua) and returns both screenshots plus a difference score. |
| 6 | Fleet packaging | **One-script worker install** on the share: copies a pinned BrokenNes headless build, the MCP server, Mesen/BizHawk and the agent runner, maps the X:/T: shares, registers the box with the job queue on this PC. Update = rerun the script. |
| 7 | Known holes | **Implement the 16 missing NES mappers in P0** so the NES pilot isn't skewed. The WideMess reference and Genesis interlace are left to the fix waves. |
| 8 | Title lists | One dump per title per console, deduped from the GoodTools sets (decided earlier). |

#### P0 status (2026-11-15, merged to main)

All seven lanes were merged through p0-integ. Each lane's own write-up is in the doc named below.

| Scope item | Status | Where |
|---|---|---|
| 1. Grade store | Done. One JSON record per ROM on `T:\BrokenDev\Catalog\` (atomic rename writes, directory locks, per-worker logs, one-box index). 4,114 records, all Not tested: NES 752, SNES 865, GB 682, GBC 272, SMS 364, GG 375, Genesis 804. 8-writer x 10-append stress test passes over T:. `export-db` to `Windows/Data/default-db.json` is dry-run by default and never overwrites hand grades. | `BrokenDev/tools/catalog/`, `docs/projects/compat-catalog-db.md` |
| 2. Determinism | Done. `Workshop --determinism-test` (movie replay, lock-step twins, state round trips) on 33 ROMs, all 8 consoles: 0 failures. No real drift found; NES needed the APU host ring counters masked (known `load-save-not-idempotent`). Battery group "Replay determinism". | `Workshop/Movies/DeterminismTestCli.cs`, `UAT/determinism-roms.txt` |
| 3. bk2 for every console | Done. Export now covers SMS, GG, Genesis (6-button default, `--pad 3`); import (`--movie import --bk2`) for all 6 consoles. `--movie-test` 82/82, `tests/bk2-import.ps1` 30/30, `movie-cross.ps1` passes on all 6. GB log layout changed to BizHawk's own. | `Workshop/Movies/`, `BrokenDev/tests/bk2-import.ps1` |
| 4. Agent loop | Done. MCP `play_step`, `record`, `reference` tools; 17 game runs over 7 consoles, every recorded movie replays in sync. play_step (20 frames) 136-213 ms, 240-370 ms with checks. | `BrokenDev/mcp/*AgentLoop*`, `docs/projects/compat-agent-playbook.md` |
| 5. Live reference | Done inside the agent loop: Mesen 2.1.1 (NES, SNES, GB/GBC, SMS, GG) and BizHawk Genplus-gx (Genesis) lock-stepped, picture difference score per step. NES/SMS/SNES "same"; GG and Genesis "close". | `BrokenDev/tools/refstep/` |
| 6. One-script worker install | Done in simulation: file job queue on T: (rename claim, leases, reaper, validation), runner, T1 boot check for all 7 consoles, `install-worker.ps1` with sha256 manifest. Dry run with two simulated boxes: 12/12. Not yet run on a real i3 box. | `tools/compat-fleet/`, `docs/projects/compat-fleet.md` |
| 7. NES mappers | Done: 11, 34, 41, 64, 66, 67, 68, 69, 71, 79, 113, 119, 144 (extra, Death Race), 232, plus an old-iNES header rule (Transformers -> CNROM) and a CRC fixup table (Deathbots -> 79, Death Race -> 144). 13 (CPROM), 47 (NES-QJ) and 118 (TxSROM) followed on 2026-11-15 (lane nes-mappers-rest, `UAT/cases/mappers-c.json`); every NES catalog title's mapper is now implemented. Lanes A and B both built 41, 66 and 67; lane A's versions were kept (tested against all 52 library titles) and both lanes now share one CPU-clocked IRQ interface (`ICpuClockedMapper`, IRQ as a level). | `Windows/NesEmulator/mappers/`, `UAT/cases/corpus.json`, `UAT/cases/mappers-b.json` |
| 8. Title lists | Done with item 1 (one dump per title, [!] > USA/World > Europe > Japan). SNES hacks/homebrew/translations, Satellaview and other side folders not scanned yet. | `BrokenDev/tools/catalog/titles.py` |

Battery on the merged tree: golden suite 145 pass / 0 fail / 17 skip / 3 info (run from the main checkout so the corpus ROMs resolve; 94/0/71 skip from the worktree), Butchering scenarios 991 pass plus NES 0 failures when re-run where its ROM folder exists, replay determinism 297/0, x86 6/6, fuzz 515 trials with 1 finding: an SMS IndexOutOfRangeException in Ym2413.CommitSlotUpdate (Sonic 2 SMS, cpu Z80, apu NES:DMG_VBAM; repro `bfuzz sms 1 out 318257`). P0 changed no Sega code, so that finding predates P0 and is left open.

Open issues carried into P1+:
- Mapper 118 nametables need a PPU that honours the per-quadrant NT hook (FIX, FMC, LOW, SPD); the other frozen PPUs show header mirroring. Mapper 67 has no real game to test it. Mapper 69 has no Sunsoft 5B audio. Skull & Crossbones (64) bottom split is one scanline late; Caltron 6-in-1 (41) drifts from Mesen; Bible Adventures shows a black left column at times. Mapper 68 CHR nametables need PPU_FIX (frozen PPUs show wrong nametables).
- Catalog: no main machine picked to rebuild the index on a schedule; T: writes are ~1-1.4 s each. The fleet's inbox (`T:\BrokenDev\CompatCatalog\inbox\`) and the catalog store (`T:\BrokenDev\Catalog\`) were defined separately and still need one importer between them. Non-NES game-db ids in default-db.json are assumed `<system>_<sha1>`; check the first non-NES export in the app.
- Determinism: no negative control per console yet; cross-process replay not tested; only 1500-frame seeded-random play.
- bk2: Genesis X/Y/Z/Mode never pressed in a BizHawk replay; a Genesis script holding Right to the end hangs BizHawk's script mode; GG on Genplus-gx, SMS on SMSHawk, GB on Gambatte unverified (firmware); SameBoy's boot swallows the first ~189 frames.
- Reference: Game Boy Super Mario Land differs steadily (0.20) and Link's Awakening DX needs a rerun with `align_search 90`; GB aligns ~82 frames off Mesen; lag detection misses some wait loops (Castlevania); Genesis reference tested on one game; BizHawk takes 20-90 s to start.
- Fleet: T1 never grades OK/Jank (needs the reference); T2/T3 runners not built; nothing run on a real box. The user must create a read-only NAS fleet account, run `cmdkey` on each box, run `stage-release.ps1 -Pin` and `queue.ps1 init`, and keep `queue.ps1 serve` running.
- The shared BrokenNes MCP server was not reinstalled: run `brokendev.ps1 mcp install` once no other session uses the MCP server, to get play_step/record/reference.


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
- **Pilot result (2026-11-15, T2, 50 games)**: OK 16, Jank 25, Broken 1, Not tested 8. 88% reached gameplay and 16% got stuck, mostly at name-entry menus. Each game took 14.4 agent-minutes and 3.65M tokens (98% cache reads). Most Janks are a 1-2 px scroll or one-frame phase difference that the reference score weights too heavily; SMB1 is a confirmed false alarm. Dudes With Attitude (mapper 79) is a confirmed real bug. Fix first: the prompt placeholders, a shift-tolerant score, and align advancing the session. Then run 20 slots (7 i3 boxes x2 + this PC x6), about 8-9 h for the NES. Report: [compat-catalog/pilot-nes-report.md](compat-catalog/pilot-nes-report.md).

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
