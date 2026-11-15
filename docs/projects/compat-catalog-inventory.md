# The great compatibility catalog: inventory (2026-11-14)

Research and inventory only, for the plan the user calls "the great compatibility catalog": Haiku 5.5 agent swarms on several
Intel boxes play every game of every supported console (not N64, not x86) through the MCP / TAS tooling, record each play as a
movie, take notes, sanity-check against a reference emulator, and feed fixes back so every BrokenNes core gets as compatible as
possible. x86 gets "benchmaxx test programs" only.

Tags: **[measured]** = run on this machine today; **[code]** = read from source; **[memory]** = from the project memory notes
(dated); **[estimate]** = my own arithmetic or judgement.

## 1. Consoles and cores

| Console | Accuracy / default core | Other cores | Where |
|---|---|---|---|
| NES | CPU_FIX / PPU_FIX / APU_FIX, default since 2026-09-25 [memory: project_fix_core_family]. The game-cert note says players' saved config may still pick FMC; FIX is the target | FMC, LQ, ULQ, CUBE, CUBEX, EXE (gimmick, frozen), harvest batch presets BAT_INES/NOFR/PNES/FCEU over CPU_FIX, cross-console chips (NES:SNES, NES:DMG, NES:SMS, NES:MD...) [memory] | `Core/` |
| SNES | SFC family: "SNES (stock)", "SNES (enhanced)" [code: docs/projects/enhanced-cores.md] | frankenstein mixes, cross-console | `Core/`, SNES runner `Workshop/SnesRunCli.cs` |
| WideMess | its own fantasy core (400x224, 8 layers) | none | `WideMess/`, `Workshop --wms-run` |
| GB / GBC | "Game Boy (stock)" (PPU_GB, APU_DMG), enhanced sound GBS [code: enhanced-cores.md] | DMG/DMGS picture variants, GB-GNUB harvest CPU [memory] | `Workshop/Gb/` |
| SMS / GG | "Master System (stock)", "Master System FM (stock)" (YM2413), enhanced SMSS [code: enhanced-cores.md] | | `Workshop/Sega/SmsRunCli.cs` |
| Genesis (md) | "Genesis (stock)" (VDP, YM2612 ymfm + PSG) [code] | | `Workshop/Sega/MdRunCli.cs` |
| x86 (separate) | PcMachine with JIT, FreeDOS boot, `--x86boot/--x86test` [code: Workshop/Program.cs] | | out of scope except benchmark programs |

Labels and ids: table in `docs/projects/enhanced-cores.md` lines 27-38 [code]. Movie consoles keys: `nes snes wms gb gbc sms gg md`
(`Workshop/Movies/MovieMachine.cs:41`) [code].

## 2. Game database and grades

- File: `Windows/Data/default-db.json` -> `data.games[]` with fields `id` (system + sha1), `title`, `status`, `note`, `system`, `romKey`,
  `headerSignature`, `size`, `createdAt` [code].
- **Measured contents: 31 games, all NES** (27 `nes` + 3 `NES` spelled upper-case, the same console): 30 `Works`, 1 `Jank`
  (Super Mario World NES port, ROM not on disk). The 16 Nothing/Broken/Jank titles of the 2026-09 cert campaign were re-graded
  there. No other console has a single graded entry. [measured]
- "Certified" is not a status value in the db: it is a campaign definition (correct on FIX/FIX/FIX vs Mesen 2.1.1 plus a golden case)
  recorded in memory `project_game_certification.md` and golden cases `UAT/cases/*.json` [memory, code].
- Other grade records: rosters (GBC 20 games "playable 19/20"; Sega 20 per console in `docs/projects/sega-rosters.md`; SNES Classic
  climb 20/21 run; Nested roster 53 NES games, `docs/projects/nested-cert/`) [memory, code]. These are markdown/notes, not a db.
- **Gap:** there is no per-console, per-ROM-hash grade store at library scale. The swarm needs one (suggest: one JSON line per ROM sha1:
  console, grade, core set, movie path, notes, reference-emulator verdict).

## 3. ROM libraries (X:\EMULATION, read only)

| Console | Folder | Files (anything-to-dos report) [measured there] | Files incl. subfolders today [measured] | GoodTools `[!]` files [measured] | Distinct base titles (tags stripped) [measured, rough] |
|---|---|---|---|---|---|
| NES | `Nintendo Entertainment System` | 758 | 758 | 86 | ~757 (mostly un-tagged one-per-title set) |
| SNES | `Super Nintendo` | 2122 (top level) | 40,198 (subfolders: Hacks, SMW Hacks, Japan, Homebrew, PAL to NTSC, NES PORTS...) | 364 | top-level ~2,000 [estimate] |
| GB | `GameBoy` (zipped) | 622 | 622 | 430 | ~616 |
| GBC | `GameBoy Color` | 529 with CGB flag | 1,035 | 3 | ~930 files' names, ~529 real CGB games |
| SMS | `Sega Master System` (.7z) | 457 ROMs in 341 archives | 342 | 0 | ~333 |
| GG | `Sega Game Gear` | 640 | 665 | 284 | ~410 |
| Genesis | `Sega Genesis` | 947 | 948 | 635 | ~937 files, ~650 titles [estimate from `[!]` count] |
| WideMess | none: only the user's own builds + `WideMess/testroms/` | | | | |

Rule for the swarm: one dump per title, `[!]` preferred, else the USA/World revision; hacks, PAL-to-NTSC and homebrew folders as a
later lane. **Estimated de-duplicated work list: NES ~700, SNES ~1,800, GB ~550, GBC ~500, SMS ~300, GG ~350, Genesis ~650 = ~4,850
titles** [estimate]. Test-ROM suites: NES blargg/AccuracyCoin via `UAT/cases` + `Workshop --romtest`; SNES `--snestest` (65816
tests); GB `--gbtest` (test ROM location in memory project_gb_cores); SMS `--smstest`, `--sega-test`; Genesis `--md-test`,
`--m68ktest`; WideMess `--wms-test`, `--wms-testcart`; x86 `--x86test` [code: Workshop/Program.cs].

## 4. Tooling a swarm would use

**brokennes MCP server** (`BrokenDev/mcp/Tools.cs`, `TimeflowTools.cs`; installed exe `P:\BrokenDev\env\bnbuild\mcp\bin\BrokenNes.Mcp\release\BrokenNes.Mcp.exe`) [code]:
- Sessions: `open_rom` (headless, deterministic, many at once, every console), `open_project` (BrokenDev build + symbols), `attach_live`
  (read-only, control only on user request), `list_sessions`, `list_instances`, `close_session`, `status`.
- Running: `run` (N frames or until break, optional pad script), `run_until` (frame / scanline / address / condition), `pause`,
  `continue`, `step`, `step_back` (rewind via states + logged input), `reset`.
- Input: `input` (press N frames / hold / release / script `"0:Start,10:,60:Right+A"`).
- Memory: `list_domains`, `read_memory`, `write_memory`, `search_memory` (RAM search eq/changed/increased...), `watch` (expressions
  returned with every run result: the progress detector), `evaluate`.
- Pictures/sound: `screenshot` (PNG + hash + colour count), `layers` (SNES/WideMess), `tiles`, `sprites`, `record_sound` (levels + wav).
- States: `save_state` / `load_state` (in-server memory, never files), `diff_states`.
- Debugger: `breakpoint_add/list/remove`, `call_stack`, `registers`, `set_register`, `trace`, `trace_frame`, `disassemble`, `symbols`
  (NES, SNES+SPC700, WideMess, GB only, per server instructions).
- Checks: `checks` (D4 hardware rules from power-on with `checks:true`), `profile` (CPU busy, lag frames, vblank, DMA).
- Movies: `movie` (info / play / export fm2|bk2 / diverge / test), `bugs` (F9 captures), `timeflow_record/list/jump/resume/patch/verdict/compare/recheck/profile/profile_query/search`, `brokendev` (build/verify/compat/run verbs).

**Headless CLIs** (`Workshop\bin\Release\net10.0-windows\BrokenNes.Workshop.exe`; WinExe, pipe through `Out-String` in PowerShell) [code]:
`--romtest` (NES; exit codes 0 ok/1 crash/3 core/4 mapper; FNV fb hash; `--input`), `--snesrun` (`--frames --input --png-at --sram --wav --dump-wram`...),
`--md-run` (`--frames --input/--pad-script --png-at --hash-at --pic-hash-at --ram-watch --save-in/out --until-pc --watchdog`...), `--sms-run`, `--gbrun`,
`--wms-run`, `--console-run`, sweeps `--md-sweep`, `--sms-sweep`, oracles `--md-oracle`, `--fm-oracle-test`, `--max-boot-frames`, `--x86boot`,
movie/TAS `--playmovie`, `--index-tas-library`, `--tas-baseline-batch`, `--compare-dumps`, `--movie-test`.
**HTTP control API**: ~148 loopback endpoints in the desktop app, `Windows/webapi/README.md`, client `UAT/lib/ApiClient.ps1` [memory].
**Golden suites / battery**: `UAT/run-suite.ps1` (cases `UAT/cases/*.json`), `UAT/run-parallel.ps1`, `UAT/battery.ps1` (suite + butcher
scenarios + fuzz, ~100 s) [memory]. A green run proves "unchanged", not "correct".
**Certification / lockstep**: lived in session scratchpads (`Invoke-GameCheck.ps1`, `exec_probe.lua`, `Compare-NmiTiming.ps1`) — not in the
repo [memory]; **BrokenDev compat**: `brokendev.ps1 compat <p|all>`, `BrokenDev/lib/Compat.ps1`, adapters `BrokenDev/lib/adapters/{workshop,mesen,bizhawk,fceux,snes9x}.ps1`, Lua in `tools/compat` [code].

## 5. TAS tooling

- Own format: "BrokenNes movie" `.bnm` (record from a power-on or a save state), consoles nes, snes, wms, gb, gbc, sms, gg, md [code: MovieMachine.cs].
- Export: `.fm2` NES only; `.bk2` NES (NesHawk), SNES (BSNESv115+), GB/GBC (Gambatte, SameBoy default) — **no bk2 for SMS, GG, Genesis,
  and nothing for WideMess** (`Workshop/Movies/MovieExport.cs:111`) [code]. Import: `.fm2` playback (`Workshop/Tas/Fm2Movie.cs`); FCEUX `.fcm` unsupported [memory].
- Determinism [memory unless marked]: NES savestate desync fixed 0c11417; `--movie-test` covers record/replay NES, SNES, GB (`MovieTestCli.cs`) [code];
  fm2 cross-play with FCEUX: 192/348 library movies have their ROM, SMB1 frame-177 gap unexplained; cross-core reload mismatches fixed 2026-11-07;
  open: Krion drift (butchering), Sega savestates listed "not done" for Genesis real-game saves; SMS/GG/MD movie replay not proven by a test [estimate from code: movie-test covers 3 consoles].
- Input logging: movies log per-frame input; timeflow keeps a state every frame (keyframes + deltas) for jump/branch [code].
- ML/TAS port of ML_NesPlayer: phase 1 (fm2 playback) done, self-play harness only in the FCEUX fork, SMB1-specific [memory].

## 6. Reference emulators (BrokenDev, `BrokenDev/emulators.json`, installed into `BROKENDEV_HOME\emulators`) [code]

| Console | Reference | Headless driving | Movie replay for cross-check |
|---|---|---|---|
| NES | Mesen 2.1.1, BizHawk NesHawk, FCEUX 2.7.0 | Mesen `--testrunner` + Lua, BizHawk Lua, FCEUX Lua | fm2 on FCEUX, bk2 on BizHawk; Mesen: input via Lua only |
| SNES | Mesen, Snes9x 1.63, BizHawk bsnes | Mesen/BizHawk Lua; Snes9x weak | bk2 on BizHawk bsnes |
| GB/GBC | Mesen, BizHawk Gambatte / SameBoy | Lua | bk2 |
| SMS | Mesen, BizHawk Genesis Plus GX | Lua | no export yet |
| GG | Mesen, BizHawk SMSHawk | Lua | no export yet |
| Genesis | BizHawk Genesis Plus GX only (BlastEm used by hand earlier [memory]) | Lua | no export yet |
| WideMess | none (BrokenNes is the only implementation) | | |

## 7. Blockers for a swarm

1. **No library-scale grade db** (section 2): 31 NES rows today.
2. **Movie gaps**: no bk2 export for SMS/GG/Genesis -> no movie cross-check there; Mesen cannot replay a movie file, only Lua input.
3. **Determinism proof gaps**: replay tests exist for NES/SNES/GB only; SMS/GG/MD/WideMess movie + savestate determinism needs a test before
   swarm movies are trusted. The SMB1 fm2 frame-177 gap shows cross-emulator movie sync is not guaranteed even on NES.
4. **MCP for playing**: everything needed exists (screenshot, input, run N, save/load state, watch, RAM search). Missing: debugger on Sega
   consoles; a "record a .bnm from this session" call on a headless MCP session (movie tool plays/exports; recording is timeflow or
   desktop) [code, verify]; screenshot round-trip cost per agent decision not measured; no batch "run+screenshot" call.
5. **This-machine-only**: `X:\EMULATION` (ROMs), `P:\BrokenDev\env` = BROKENDEV_HOME (MCP exe, emulators, toolchains), `T:\BrokenDev`
   (Releases, bare remotes), `C:\BrokenNes-work` (battery build), cert harness in session scratchpads, MCP exe stale (user must run
   `brokendev.ps1 mcp install`). Other boxes need: a packaged Release build (Workshop + MCP), the pinned emulators zip, a ROM shard
   per box, and a results upload path.
6. **Mapper/chip coverage**: 16 NES mappers unimplemented (`UAT` skips); SNES 20/21 chip roster; Genesis interlace not done.

## 8. Rough throughput

Measured today, one process, cold start included, 3600 frames (60 s of game):
- NES FIX/FIX/FIX `--romtest` Mega Man 2: **16.7 s -> ~215 fps (3.6x real time)** [measured]
- SNES `--snesrun` Super Mario World: **7.5 s -> ~480 fps (8x)** [measured]
- Genesis `--md-run` Sonic 1: **15.6 s -> ~230 fps (3.9x)** [measured]
- GB/GBC, SMS/GG: not measured; assume >= NES speed [estimate].

Per game [estimate]: boot check (1800 frames, crash/black/hash) = NES ~9 s, SNES ~4 s, Genesis ~8 s. 10 minutes of play (36,000 frames)
headless with a script = NES ~170 s, SNES ~75 s, Genesis ~155 s. An LLM-driven play is dominated by the agent loop instead: at one
decision per ~3 s wall time covering ~30 frames, 10 game minutes ~= 1,200 decisions ~= 1 hour per game per agent [estimate].

Library totals (~4,850 titles): boot check of everything ~8 CPU-hours (a few minutes per box with parallel jobs); scripted 10-minute pass
~180 CPU-hours; agent-played 10-minute pass ~4,850 agent-hours (e.g. 40 agents ~5 days) [estimate].
