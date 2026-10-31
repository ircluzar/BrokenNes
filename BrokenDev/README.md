# BrokenDev

**BrokenDev is the developer-only devkit module of BrokenNes.** It is not a feature for BrokenNes players: it is where the
developers of the homebrew games (the user's own NES and SNES projects) restore, build and prove those games on the BrokenNes
emulators, and where the music path for those games (compose in FL Studio with the BrokenNes VST, preview in the emulator,
export a file the games import) will grow. See `docs/projects/brokendev-music-pipeline.md` for that design.

The app shows nothing of BrokenDev unless it is switched on: set the environment variable `BROKENNES_DEV=1`, or
`"brokenDevEnabled": true` in `%APPDATA%\BrokenNes\config.json`.

## What is in the box

| Project id | What | Kind | Built with |
|---|---|---|---|
| `vrun-nes` | VRUN CTW, the NES demake of Corrupt The World | NES, mapper 30 | NESFab 1.8 (shipped), `./build.sh game` |
| `vrun-nested` | the same game on the SNES through Project Nested | SNES (nested) | `./build.sh game --nested`, then the `m30pipe` NES to SNES pipeline |
| `vrun-nested-enhanced` | the SNES variant with enhanced graphics (shaded sprites and blocks, custom font) | SNES (nested) | `vrun-nested` plus `tools/snes_enhance.py` |
| `smb1-nes` | the frame-accurate NESFab remake of Super Mario Bros. | NES, mapper 30 | patched NESFab 1.8 (shipped), `./build.sh game` |
| `smb1-nested` | the remake on the SNES through Project Nested | SNES (nested) | `smb1-nes`, then `m30pipe` |
| `supervrun` | Super VRUN, the current native SNES project | SNES | PVSnesLib 4.6.0 (shipped), `bash build.sh` |
| `widemess-sample` | Wide Blaster, the WideMess devkit's sample game (`WideMess/sample` in this repository) | WideMess | PVSnesLib + the WideMess devkit, `widemess/tools/wmsbuild.ps1` |
| `vrun-voc` | VRUN: Vaults of Obsolete Code, today its V2 tutorial (`docs/projects/vrun-voc-plan.md`), in its live workspace `PROJECTS\VRUN_VOC_dev` | Game Boy (dual mode: console `gbc`, also run as `gb`), MBC5 1 MB + 32 KB battery RAM | GBDK-2020 4.5.0 (pinned, `toolchains.json`), the workspace's `build.ps1` |

Each project is a manifest, `projects/<id>/project.json`: where its source comes from (an archive and a path), the tools it
needs, the build steps and outputs, how to verify it on BrokenNes, and what is known to be wrong with it. The scripts in this
folder interpret the manifests; nothing about a particular game is hard-coded in them.

**SMB1 is Nintendo's game.** The remake is built from data of the original ROM. The original ROM, anything built from it and
anything extracted from it never goes into git or into the docs; the repository holds scripts, manifests and the knowledge
the reverse engineering produced, in words. The ROM is the user's own copy, found through `SMB1_ORIG_ROM`.

## The commands

```powershell
# from the repository root (Windows PowerShell 5.1 or PowerShell 7)
BrokenDev\brokendev.ps1 doctor                 # is this machine ready? (tools, toolchains, disk)
BrokenDev\brokendev.ps1 restore all            # extract the three project archives into BROKENDEV_HOME
BrokenDev\brokendev.ps1 build-workshop         # the headless BrokenNes runner that verify uses
BrokenDev\brokendev.ps1 list                   # the projects: restored / built / verified
BrokenDev\brokendev.ps1 build vrun-nes         # build one project (dependencies first); outputs in BROKENDEV_HOME\out\<id>
BrokenDev\brokendev.ps1 verify vrun-nes        # run the built ROM on BrokenNes and record checks, hashes, audio, speed
BrokenDev\brokendev.ps1 build all ; BrokenDev\brokendev.ps1 verify all
BrokenDev\brokendev.ps1 diff vrun-nes          # differential replay against Mesen 2 as a black-box oracle (NES: the user's tracer, kept in tools\vrun\mesen)
BrokenDev\brokendev.ps1 run supervrun          # open the built ROM in BrokenNes (desktop app through its HTTP API)
BrokenDev\brokendev.ps1 tools                  # the editors and checkers of the games (see "The tools" below)
BrokenDev\brokendev.ps1 tool <id> [args]       # start one
BrokenDev\brokendev.ps1 emulators              # the other emulators the games are tested on (see "Other emulators" below)
BrokenDev\brokendev.ps1 emulators install all  # Mesen, BizHawk, FCEUX, Snes9x: the pinned official releases, portable
BrokenDev\brokendev.ps1 toolchains             # the pinned toolchains (GBDK-2020): ready, or what is wrong (never downloads)
BrokenDev\brokendev.ps1 compat vrun-nes        # the game on BrokenNes (the reference) and every installed emulator of its console
BrokenDev\brokendev.ps1 play vrun-nes -Emu fceux   # open the built ROM in one of them, by hand
BrokenDev\brokendev.ps1 new widemess my-game   # a new WideMess game from the devkit's template (its folder and projects\my-game)
BrokenDev\brokendev.ps1 mcp install            # build the BrokenNes MCP server and register it for Claude Code at user scope (see below)
BrokenDev\brokendev.ps1 mcp test               # its scripted MCP client test and the planted-bug demo
BrokenDev\brokendev.ps1 movie record vrun-nes -Scenario play -Course app_state,current_room   # a BrokenNes movie of the built ROM (see "Movies" below)
BrokenDev\brokendev.ps1 movie promote vrun-nes -Movie play -Name first-room -Why "..."        # it becomes a verify test
BrokenDev\brokendev.ps1 timeflow record vrun-nes -Scenario play -Course app_state             # a timeline (a state every frame) of the built ROM, headless
BrokenDev\brokendev.ps1 timeflow resume <timeline> -Frame 600                                 # back to frame 600, the recorded inputs replayed and checked
BrokenDev\brokendev.ps1 timeflow profile <timeline> -From 600 -To 780 -Query "writes player_hp; histogram"   # T3: that span at full depth, the answers only
BrokenDev\brokendev.ps1 timeflow record vrun-nes -Scenario play -Index                         # T4: with the search index (coverage, access counts, checks)
BrokenDev\brokendev.ps1 timeflow search vrun-nes -Query "state [enemy_alive] != 0 and not color #982220"   # T4: the frames where it held, over the project's timelines
BrokenDev\brokendev.ps1 timeflow patch <timeline> -Frame 600 -Patch "fix_x+1=21>10" -Verdict "never write hp by fill_table"   # T5: byte patches from frame 600, the replay, the divergence, the verdict
BrokenDev\brokendev.ps1 timeflow rebuild-resume <timeline> -Frame 600 -Verdict "never state [hp] == 0"   # T5: the project rebuilt, frame 600 resumed on it (the symbol-map check; the manifest's timeflow.safe)
BrokenDev\brokendev.ps1 timeflow verdict-promote <timeline> -Verdict "never ..." -Name hp-safe -Why "..."   # T5: a verdict into the project's verify, by this command onlyBrokenDev\brokendev.ps1 bisect vrun-nes -Movie first-room -Test "[player_hp] >= 1 at 900"     # the first of the game's commits where it breaks

# the music path, Phase 0 (no plugin): a MIDI file -> VRUN's converter -> a one-song ROM -> BrokenNes, checked frame by frame
BrokenDev\tools\music-replay-check.ps1 -Mid some.mid -Name mysong -Frames 2400 -Template
# the template the plugin's "Test MIDI export" carries (Plugin/BrokenNes2.Plugin/Resources/musictest.*), and a full one-song rebuild (the reference), in a scratch copy
BrokenDev\tools\music\build-musictest.ps1 -Out <folder>
BrokenDev\tools\music\build-musictest.ps1 -Mode song -Mid some.mid -Name mysong -Out <folder>
BrokenDev\tests\run-tests.ps1                  # the scripts' own tests (each has a negative control)
BrokenDev\tests\bisect-proof.ps1 -WorkshopExe <exe>     # bisect on a throwaway game repository with a planted regression (run-tests runs it)
BrokenDev\tests\movie-cross.ps1 [-Project vrun-nes]     # the .bk2 / .fm2 exports played by BizHawk and FCEUX (cross-check; the emulators installed)
BrokenDev\tests\apu-regress.ps1 -Workshop <Workshop.exe>   # APU_FIX guards measured on Mesen
```

Inside BrokenNes (developers only): `BrokenNes.Workshop.exe --brokendev list` and `--brokendev verify <id>` (needs
`BROKENNES_DEV=1`), and a **BrokenDev** menu in the desktop app that opens the built ROMs.

## Hardware checks, budgets and the profile (in verify)

A manifest with a `checks` section has its verify scenarios run a second time with BrokenNes's **hardware checks** on (37 rules for the
NES, the SNES and WideMess, the Game Boy: RAM read before written, picture writes during rendering, unofficial opcodes, lag frames, flash
writes, DMA and HDMA conflicts, echo-buffer overruns, the HALT bug...; the catalogue is in `docs/projects/brokendev-debugging-plan.md`).
The first, unchecked run keeps its hashes and speed; the checked run must end on the same picture.

- A **finding** names the rule, the place (the variable or the function, by the game's symbols; the address without one), the first frame,
  the PC and the count. `checks.accepted` lists what the game accepts, each with a reason (`"known: ..."`, `"unverified: ..."`); **anything
  new fails verify**. `checks.off` switches a rule off for the game (with a reason).
- **Budgets** (`budgets`): `lagFrames` (max, from a frame, in some scenarios), `busyPercent` (the busiest frame that made it), `romBankFree`
  (free bytes per bank: padding runs), `memoryFree` (work RAM / HRAM never read, VRAM / sound RAM never written a non-zero value),
  `stackFree`. Over a limit fails, near it (90%) warns. The report shows each against the previous build (`out\<id>\budget-history.json`).
- The **profile** of each checked scenario: CPU busy time per frame, lag frames, vblank time used for uploads, DMA bytes, the top functions
  (inclusive and exclusive). The same outside verify: `BrokenNes.Workshop.exe --profile --rom <rom> --sym <symbols> --frames N --input ...`
  and `--checks` (findings too).

## Movies, divergence, step back and bisect

BrokenNes has its own movie format (milestone D5). Its name and extension (settled 2026-10-26):
**"BrokenNes movie", `.bnm`**, one constant in `Windows/NesEmulator/debug/movies/Movie.cs` (`MovieFormat`); BrokenDev asks the Workshop
(`--movie format`), so a rename changes nothing else. A movie is plain text: the ROM (name, sha256, size) and the build it was made with,
the console and cores, the NES's power-on RAM and frame timing, the start (power-on or a save state), every frame's input for two pads
(as changes: `i 120 Start -`), and every 60 frames a keyframe with the picture and work-RAM hashes and the values of the movie's **course
variables** (`var app_state [app_state]`), so a replay knows the frame it went off course.

- **Record and replay:** `brokendev.ps1 movie record <project> -Scenario <verify id>` (or `-Script "120:Start,..." -Frames N`; `-Course sym,sym`)
  records the built ROM into `out\<id>\movies`; `movie play` replays it: in sync, or the first keyframe off course. The MCP server's
  sessions record their input from the start (`movie` save) and replay a movie (`open_movie`). Workshop: `--movie record|play|info`.
- **Exports** (cross-checks only, the user's decision): `movie export -Format bk2|fm2` for BizHawk (NesHawk, BSNESv115+, Gambatte) and FCEUX;
  `--movie import --fm2` brings an FCEUX movie in. `tests\movie-cross.ps1` plays the exports in the real emulators against the same input
  given by the compat scripts (the compat spec's `movie` line); a movie recorded with BrokenNes's 60 fps frames says so on export (the others
  run NTSC frames).
- **Promoted movies are verify tests** (only those): `movie promote` copies the movie to `projects\<id>\movies` and lists it in the
  manifest's `movies` section; verify replays it as a scenario (`movie-<name>`) with its keyframes checked (`movie-in-sync`), the entry's
  `expect` (as a scenario's: pictures, `memory` by symbol, sound), and the checks and budgets. `sync`: `same-rom` (default: the keyframes
  must match on the build the movie was recorded with; on another build they are information), `required`, `off`.
- **The divergence finder:** `--movie diverge --movie M --rom A [--rom-b B | --cores-b C/P/A | --ntsc-b on]` plays the movie on both in
  lockstep: the first frame whose picture or RAM differs, then that frame again with the trace on, the first instruction where the main CPU
  parts (or the same instructions at another scanline and dot), and the **state diff by symbol** before and after it ("lives: 1 against 0").
  `--against <compat run folder>` compares with another emulator's run (the game's variables every frame, the boot shift found).
- **Step back:** the MCP tool `step_back` (instructions or frames) and Workshop `--debug` (`history; ... back 5; back frames 2`): a state
  every 60 frames (the last 30) and every frame's input; going back loads the newest state before the place and replays the input to the
  exact instruction. Running on replays the same input; a new input rewrites the future.
- **Bisect over a game's commits** (`brokendev.ps1 bisect <project> -Movie M -Test T [-Good c] [-Bad c]`): with today's BrokenNes, a scratch
  clone of the game's repository (in `BROKENDEV_HOME\work\<id>\bisect`, its remote removed: it can never push; the user's folder is only
  read) is built halfway back each time, the movie replayed and tested: `no-crash`, `no-lag [from N]`, `picture at N` (the recording's
  keyframe), `<expression> at N` (`[lives] >= 1 at 600`). A commit that does not build is skipped and reported; a commit where the replay
  loses its course (a course variable differs from the recording before the test frame: a different menu) is **can't tell**, never a
  guess. Endpoints that do not hold stop it with no answer. Proven on a 17-commit planted regression (`tests\bisect-proof.ps1`).

## Bug captures

The desktop app and Lite keep the last 30 s of the game being played (milestone D6). **F9** (Emulator > Capture Bug) in the desktop app,
the **Bug** button in Lite, saves them as a bundle: a movie from a save state that replays exactly, the very ROM, the sound (`audio.wav`),
a picture a second (and `strip.png`, all of them on one sheet), the last picture, the game's symbols, the note typed after the key
(`bug.json` says the rest: the cores, how long, why the history starts where it does). The desktop app puts it in the game workspace's
`bugs\<date_time>\` when the ROM is a BrokenDev project's build (the build record's `bugs`), else `%LOCALAPPDATA%\BrokenNes\bugs`; Lite gives
a `.zip` download. Every workspace keeps `bugs\` out of git (`bugs` checks it).

```powershell
BrokenDev\brokendev.ps1 bugs [vrun-nes]                 # the captures, and whether each workspace ignores bugs\ (exit 1 when one does not)
BrokenDev\brokendev.ps1 bug 2026-10-25_194721           # replay one: in sync (every keyframe, the last picture), the hardware checks
BrokenDev\brokendev.ps1 bug <Lite zip> -Debugger -Script "run 1800; back frames 60; where; stack"   # in the headless debugger
```

The MCP server's `bugs` tool lists, replays and opens them (`open` gives a headless session at the moment of the capture: read memory,
breakpoints, `step_back` through the 30 s).

## The MCP server (agents drive BrokenNes directly)

`BrokenDev/mcp` is an MCP server (C#, the official SDK `ModelContextProtocol` 2.2.0, stdio) that lets a Claude Code session drive
BrokenNes: it is how agents debug the games on BrokenNes instead of Mesen (Mesen stays for cross-checks).

```powershell
BrokenDev\brokendev.ps1 mcp install     # builds it into BROKENDEV_HOME\bnbuild\mcp, then: claude mcp add --scope user brokennes -- <exe> --repo <main checkout>
BrokenDev\brokendev.ps1 mcp status      # built? registered? (claude mcp get brokennes)
BrokenDev\brokendev.ps1 mcp test        # the client test (75 checks) and the planted-bug demo (6)
BrokenDev\brokendev.ps1 mcp uninstall   # claude mcp remove brokennes --scope user (nothing else is touched)
BrokenDev\tests\mcp-live.ps1 -App <BrokenNes.Windows.exe> -Mcp <BrokenNes.Mcp.exe>   # live sessions against two desktop apps with scratch configs
```

It is registered **once, at user scope**, so every Claude Code session (in BrokenNes and in every game repo) has the tools as
`mcp__brokennes__<tool>`; new sessions pick it up. The CLI is `claude` on PATH, else the one the Claude desktop app keeps in
`%APPDATA%\Claude\claude-code\<version>`; without a CLI the installer writes the one documented entry `mcpServers.brokennes` in
`%USERPROFILE%\.claude.json`. Reinstalling rebuilds the same folder: close Claude Code sessions that use it first (Windows keeps a running
exe locked).

- **Headless sessions** (`open_rom`, `open_project`): BrokenNes's emulator inside the server, invisible and deterministic, as many as
  wanted; every console (NES with the FIX cores, SNES, WideMess, Game Boy / Color, Master System, Game Gear, Genesis). The D1 debugger
  covers the NES, the SNES with its SPC700, WideMess and the Game Boy; `checks: true` runs the D4 checks and the profiler from power-on.
  `open_project` takes a project's last build (ROM, symbols, console, cores, frame-wait symbols).
- **Live sessions** (`attach_live` by PID, `list_instances`): the user's running desktop app through its HTTP API. **Read freely, control
  when asked**: a live session opens read-only; `control: true` (pause, step, breakpoints, writes, input, the quick-state load) is only for
  when the user asked in chat, and the app must also have `"enableDebuggerControl": true`. A live session never saves, never touches
  progression, never stops the process; closing a control session detaches the debugger and lets the game go.

| Group | Tools |
|---|---|
| sessions | `open_rom`, `open_project`, `list_instances`, `attach_live`, `list_sessions`, `status`, `close_session` |
| running | `run` (frames, with an optional pad script), `run_until` (frame, scanline, an address executed / read / written with a condition, a condition per frame), `pause`, `continue`, `reset` |
| input | `input` (press, hold, release, a scripted pad `0:Start,10:,60:Right+A`) |
| memory | `read_memory` / `write_memory` (by spec: `$0010`, `7E:0010`, `wram:$10`, `spc:$0200`, a symbol; or by domain), `search_memory` (a RAM search), `list_domains`, `symbols` |
| debugger | `breakpoint_add` (exec, mirror-aware watches incl. DMA, tracepoints, conditions, hit counts), `breakpoint_list`, `breakpoint_remove`, `watch` (expressions shown with every result), `step` (into, over, out, scanline, frame, run to), `call_stack`, `registers`, `set_register`, `trace`, `trace_frame` (one whole frame to a file), `disassemble`, `evaluate` |
| picture | `screenshot` (an image), `layers` (SNES / WideMess: only some layers), `tiles`, `sprites` (OAM list and boxes) |
| sound | `record_sound` (levels and a .wav) |
| states | `save_state`, `load_state` (in the server's memory only), `diff_states` (by symbol) |
| analysis | `checks` (start, report, stop: the D4 findings), `profile` |
| movies (D5) | `step_back` (instructions or frames), `open_movie` (a movie replayed in a new session, keyframes checked), `movie` (save a session's movie, info, play, export fm2 / bk2, diverge, test), `bisect` |
| BrokenDev | `brokendev` (build, verify, compat, run, list, status, doctor) |

Results are JSON (also as structured content), pictures PNG images. Session files (traces, .wav) go to
`%LOCALAPPDATA%\BrokenNes\mcp\sessions\`. The plan's section 8 and "D3 as built": `docs/projects/brokendev-debugging-plan.md`.

## The WideMess devkit

BrokenDev is also the devkit of WideMess, BrokenNes's fantasy console: `widemess/` holds PVSnesLib's WideMess extensions (`lib/`), the
converters and the build (`tools/`), the sound driver (`driver/`) and the template `new widemess` copies (`template/`). A WideMess project's
manifest has kind and console `wms` and its source in its own folder (`source.folder`); `verify` runs it on the WideMess console and checks
its `expect.wms`; `compat` has BrokenNes's column only. The guide: `docs/projects/widemess-devkit.md`.

## The Game Boy (GBDK-2020, `vrun-voc`)

- **The toolchain** is pinned in `toolchains.json` like the emulators: GBDK-2020 4.5.0 at `BROKENDEV_HOME\toolchains\gbdk\4.5.0\gbdk`, its
  release zip kept in `toolchains\gbdk\_downloads` (size and sha256 pinned), ten key files by size and sha256, the ChangeLog's version.
  `doctor` and `brokendev.ps1 toolchains` check it; a manifest needs it with `requires.tools` `toolchain:gbdk` and names its folder
  `{toolchain:gbdk}`. BrokenDev never downloads or unpacks a toolchain.
- **A Game Boy project** has kind `gb` and console `gb` or `gbc` (a dual-mode or Color game). `vrun-voc`'s source is `{live:VRUN_VOC_dev}`:
  a game's own live workspace, the folder of that name beside the repository or one of its parents (`BROKENDEV_LIVE_VRUN_VOC_DEV` names
  another). BrokenDev's own tests do not follow the live game: they build the V0 hello world from the game repo's commit `f828fbe`
  (exported with git's archive command, read only) and verify it with the frozen manifest `tests/fixtures/projects/vrun-voc-v0`.
  `tests/voc-v0.ps1` takes those sources and builds them once to `BROKENDEV_HOME\out\vrun-voc-v0` (the Workshop's `--port-test` and the
  UAT hello-world scripts read the ROM there); `debugport/gbdk/build-proof.ps1` without `-Game` uses the same sources.
- **verify** runs it on BrokenNes's Game Boy Color, and a scenario's `"console": "gb"` runs the same cartridge on the original Game Boy.
  Three more scenario keys (any console): `"batteryFrom": "<scenario>"` powers on with the battery save an earlier scenario left (a boot
  counter, a save game); `expect.memory` reads the game's variables by the build's symbols (`{ "symbol": "_boot_count", "size": 2, "is": 2 }`,
  NES system RAM or the Game Boy's bus); `expect.maxDistinctColors`. The hardware checks are the Game Boy's 12 rules.
- **compat** has four Game Boy columns: BrokenNes, Mesen (its Game Boy Color or Game Boy model, from `gbModels`), BizHawk Gambatte and BizHawk
  SameBoy (`bizhawk-gambatte`, `bizhawk-sameboy`: one core pinned for EmuHawk's systems GB and GBC). Reads take a CPU address or a GBDK symbol
  (`.noi`, or the bgb `.sym`) and become BrokenDev's Game Boy domains: `wram` (`$C000` + bank x `$1000`), `hram` (`$FF80`), `cartram` (bank x
  `$2000`). The other emulators play their boot animations first (BrokenNes starts at the documented post-boot state), so the scenarios
  wait before pressing anything.

## The tools (editors and checkers of the games)

The tools made while developing the games (the tile editor, the SFX configurator, the AudioPlayer configurator, the Nested Builder, the
CTW room designer and the check scripts) start from BrokenDev, against the **live** workspaces:

```powershell
BrokenDev\brokendev.ps1 tools                       # every tool: id, project, how it starts, what you edit or check with it
BrokenDev\brokendev.ps1 tools vrun-nes              # the tools of one project
BrokenDev\brokendev.ps1 tool vrun-tile-editor       # a web UI: started on its own port, opened in your browser (-NoBrowser prints the address)
BrokenDev\brokendev.ps1 tool vrun-tile-editor -Stop # stops the one BrokenDev started (never anything else on the port)
BrokenDev\brokendev.ps1 tool vrun-sfx-configurator  # regenerates the SFX configurator page from the current game and opens it
BrokenDev\brokendev.ps1 tool vrun-dis6502 build\vrun_game.nes 07D01E D01E 30 build\vrun_game.mlb   # a script: arguments pass through
```

| Tool | Starts | What you edit or check |
|---|---|---|
| `vrun-tile-editor` | web UI, port 8420 | the hand-authored art PNGs of VRUN (`src/game/art`: bullet, blocks, doors, spikes, wires, HUD, logo), with colour and network previews |
| `vrun-sfx-configurator` | generated page | which of the 22 SFX clips plays on each of the game's sound events (every `trigger_sfx` call of `main.fab`); exports JSON to apply |
| `vrun-audioplayer-configurator` | web UI, port 8421 | the AudioPlayer song roster (upload MIDI), build the AudioPlayer ROM, play it in Mesen |
| `nested-builder` | web UI, port 8765 | builds the SNES (Project Nested) ROM of a VRUN build: compatibility, recipe, every Nested setting, harvest |
| `ctw-room-designer` | web UI, port 8766 | CTW room templates (the rooms VRUN's map bake reads), seeded maps, and the original CTW game |
| `vrun-*` scripts | run once | SFX WAV renders, pipe joints, NES colour measuring, room warp / teleport / screenshot, corruption detector, regression suite, laser puzzle solver, room reachability, 6502 disassembler |
| `svrun-*` scripts | run once | Super VRUN self-test, self-play, feature checks and negative controls by name, music modules (MIDI + SoundFont to IT) |

The registry is `devtools.json` (validated, like the manifests, by `tests/run-tests.ps1`); each entry is the command the tool was always
started with, so a tool started by hand still works the old way. The record of where each tool came from, how it was verified and what is
missing is `docs/projects/brokendev-tools.md` (repository root).

## Other emulators (compat)

BrokenNes is the first-class emulator of the games; `compat` checks that a game also works on the emulators players use. The registry is
`emulators.json` (validated by `tests/run-tests.ps1`); each emulator has a small adapter (`lib/adapters/<name>.ps1`, its script in
`tools/compat/`) that speaks one contract, written at the top of `lib/Compat.ps1`: a spec in (ROM, frames, pad script, bytes to record,
frames to picture), a `result.txt` and pictures out.

| Emulator id | Consoles | How it is driven | Its column can check |
|---|---|---|---|
| `brokennes` | NES, SNES, WideMess, Game Boy | the Workshop, `--brokendev run` (the same cores as `verify`) | everything: state, pictures, sound, saves (the reference) |
| `mesen` | NES, SNES, Game Boy | Mesen 2.1.1, Lua through `--testRunner`, a private home | state, pictures, NES sound activity, saves; lists RAM read before written |
| `bizhawk-neshawk` | NES | BizHawk 2.11.1 EmuHawk, `--lua`, a scratch `--config` pinning NesHawk | state, pictures, saves |
| `fceux` | NES | FCEUX 2.7.0, `-lua` on a private copy | state, pictures, sound activity, saves |
| `snes9x` | SNES | Snes9x 1.63 standalone (no scripting): its own menu commands by window message, a .smv movie, Frame Advance, its screenshots and save states | state, pictures, saves |
| `bizhawk-bsnes` | SNES | BizHawk 2.11.1 EmuHawk, `--lua`, a scratch `--config` pinning BSNES | state, pictures, saves |
| `bizhawk-gambatte` | Game Boy | BizHawk 2.11.1 EmuHawk, `--lua`, a scratch `--config` pinning Gambatte (GB and GBC) | state, pictures, saves |
| `bizhawk-sameboy` | Game Boy | BizHawk 2.11.1 EmuHawk, `--lua`, a scratch `--config` pinning SameBoy (GB and GBC) | state, pictures, saves |

A project's manifest has a `compat` section: the bytes to read (by the game's own symbols, `.mlb` or `.sym`), and scenarios (a `verify`
scenario reused, or their own input) with checks: a state reached or held in a frame window, a heartbeat that keeps changing, pictures with
enough colours or like BrokenNes' picture. Emulators boot a few frames apart, so states are compared with a frame tolerance; each emulator
runs with its own defaults (its power-on RAM, its region), which is the point. The verdict per emulator is `works`, `differs` (a state too
many frames from BrokenNes', a picture unlike it), `fails` (a crash, a hang, a check that failed) or `cannot be tested` (with the reason).
Results: `out/<id>/compat/compat.json`, `compat.html` (the contact sheet: rows = pictures, columns = BrokenNes then each emulator) and the
pictures; `out/compat.html` (and `compat-matrix.json`) is the matrix of every project's latest run, linking to each sheet. The emulators, how each is driven, what each column can and cannot check, and the results: `docs/projects/brokendev-emulators.md`.

## Live workspaces

The projects are built and the tools run in the user's **live** workspaces when they sit beside the repository (`VRUN_Nes_Dev`,
`SuperVRUN_dev`, `VRUN_VOC_dev`; a git worktree finds them too), not in the copies `restore` extracted into `BROKENDEV_HOME`. `list` shows which one each
project uses. `BROKENDEV_LIVE_<ARCHIVE>` (for example `BROKENDEV_LIVE_VRUN_NES_DEV`) names another folder, or `off` for the restored
copy; a machine with no live workspace (a new one) uses the restored copy, as before. Builds in a live workspace write to its git-ignored
`build` folders, exactly like the user's own builds.

## Where things live

* **In git (this folder):** `brokendev.ps1`, `doctor.ps1`, `lib/` (with `lib/adapters/`), `projects/*/project.json`, `archives.json`, `devtools.json`, `emulators.json`,
  `tools/` (with `tools/vrun/mesen/`, the user's 20 VRUN Mesen tracer and probe scripts, and `tools/vrun/sfx-configurator/`), `docs/`,
  `tests/`. Light: no ROM, no archive, no toolchain, no build output, no file over 1 MB.
* **Outside git, in `BROKENDEV_HOME` (default `P:\BrokenDev\env`):** the restored projects, `out/<id>/` (built ROMs, build and
  verify records, screenshots, logs, `compat/`), the Workshop build, work folders of the pipelines, `work/tools/<id>/` (what the tools write:
  logs, the generated SFX page, renders, screenshots, the PID of a web UI BrokenDev started), `emulators/` (the installed emulators and
  `_downloads/`, the verified zips), `work/compat/` (each compat run's ROM copies, configs and saves) and `work/play/` (hand-testing saves).
* **The live workspaces** beside the repository: the user's own folders, which the builds and tools work on (see above).
* **The archives** (`VRUN_Nes_Dev_2026-10-12.zip`, `SuperVRUN_dev.zip`, `SMB1 Topaz\SMB1_Remake_Project_20261011.zip`) stay where the user
  keeps them (`P:\BrokenDev`, or `BROKENDEV_ARCHIVES`). The user's own source stays in those archives.

## Environment

| Variable | Meaning | Default |
|---|---|---|
| `BROKENDEV_HOME` | working folder, must be outside the repository | `P:\BrokenDev\env` |
| `BROKENDEV_ARCHIVES` | the folder holding the archives | the parent of `BROKENDEV_HOME` |
| `BROKENDEV_PYTHON` | force one Python | the first 3.10+ with pillow and numpy |
| `BROKENDEV_WORKSHOP` | force a Workshop executable | the one `build-workshop` built |
| `BROKENDEV_APP` | the BrokenNes desktop exe `run` should launch | `BROKENDEV_HOME\bnbuild\app\...` if built |
| `BROKENDEV_LIVE_<ARCHIVE>` | a live workspace folder for that archive (`BROKENDEV_LIVE_VRUN_NES_DEV`, `BROKENDEV_LIVE_SUPERVRUN_DEV`), or `off` | the folder of that name beside the repository, else the restored copy |
| `BROKENDEV_TRACE_LUA` | another Mesen tracer for `diff` of the NES projects | BrokenDev's copy of the user's `state_trace.lua`, `tools\vrun\mesen\` |
| `BROKENDEV_DEVTOOLS` | another tools registry (the tests use it) | `devtools.json` |
| `BROKENDEV_EMULATORS` | another emulator registry (the tests use it) | `emulators.json` |
| `BROKENDEV_TOOLCHAINS` | another toolchain registry (the tests use it) | `toolchains.json` |
| `BROKENDEV_LIVE_<FOLDER>` | the live workspace a `{live:<folder>}` source names (`BROKENDEV_LIVE_VRUN_VOC_DEV`) | the folder of that name beside the repository |
| `BROKENDEV_SNES9X_WORK` | a local folder for Snes9x's per-frame pictures and save states during compat | the run's scratch, or `%TEMP%\brokendev-snes9x` when that is on a network share |
| `SMB1_ORIG_ROM` | the user's own original SMB1 ROM | `P:\BrokenDev\SMB1 Topaz\smb1.nes` |
| `BROKENNES_DEV` | `1` switches BrokenDev on inside BrokenNes | off |

More: `docs/START_HERE.md` (a new machine, step by step), `docs/smb1-knowledge.md` (what reverse engineering SMB1 taught),
`tests/run-tests.ps1` (the scripts' own tests, each shown able to fail).
