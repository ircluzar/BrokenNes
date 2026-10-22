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
BrokenDev\brokendev.ps1 compat vrun-nes        # the game on BrokenNes (the reference) and every installed emulator of its console
BrokenDev\brokendev.ps1 play vrun-nes -Emu fceux   # open the built ROM in one of them, by hand

# the music path, Phase 0 (no plugin): a MIDI file -> VRUN's converter -> a one-song ROM -> BrokenNes, checked frame by frame
BrokenDev\tools\music-replay-check.ps1 -Mid some.mid -Name mysong -Frames 2400 -Template
# the template the plugin's "Test MIDI export" carries (Plugin/BrokenNes2.Plugin/Resources/musictest.*), and a full one-song rebuild (the reference), in a scratch copy
BrokenDev\tools\music\build-musictest.ps1 -Out <folder>
BrokenDev\tools\music\build-musictest.ps1 -Mode song -Mid some.mid -Name mysong -Out <folder>
BrokenDev\tests\run-tests.ps1                  # the scripts' own tests (each has a negative control)
BrokenDev\tests\apu-regress.ps1 -Workshop <Workshop.exe>   # APU_FIX guards measured on Mesen
```

Inside BrokenNes (developers only): `BrokenNes.Workshop.exe --brokendev list` and `--brokendev verify <id>` (needs
`BROKENNES_DEV=1`), and a **BrokenDev** menu in the desktop app that opens the built ROMs.

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
| `brokennes` | NES, SNES | the Workshop, `--brokendev run` (the same cores as `verify`) | everything: state, pictures, sound, saves (the reference) |
| `mesen` | NES, SNES | Mesen 2.1.1, Lua through `--testRunner`, a private home | state, pictures, NES sound activity, saves; lists RAM read before written |
| `bizhawk-neshawk` | NES | BizHawk 2.11.1 EmuHawk, `--lua`, a scratch `--config` pinning NesHawk | state, pictures, saves |
| `fceux` | NES | FCEUX 2.7.0, `-lua` on a private copy | state, pictures, sound activity, saves |
| `snes9x` | SNES | Snes9x 1.63 standalone (no scripting): see the record | pictures (see docs) |
| `bizhawk-bsnes` | SNES | BizHawk 2.11.1 EmuHawk, `--lua`, a scratch `--config` pinning BSNES | state, pictures, saves |

A project's manifest has a `compat` section: the bytes to read (by the game's own symbols, `.mlb` or `.sym`), and scenarios (a `verify`
scenario reused, or their own input) with checks: a state reached or held in a frame window, a heartbeat that keeps changing, pictures with
enough colours or like BrokenNes' picture. Emulators boot a few frames apart, so states are compared with a frame tolerance; each emulator
runs with its own defaults (its power-on RAM, its region), which is the point. The verdict per emulator is `works`, `differs` (a state too
many frames from BrokenNes', a picture unlike it), `fails` (a crash, a hang, a check that failed) or `cannot be tested` (with the reason).
Results: `out/<id>/compat/compat.json`, `compat.html` (the contact sheet: rows = pictures, columns = BrokenNes then each emulator) and the
pictures. The emulators, how each is driven, what each column can and cannot check, and the results: `docs/projects/brokendev-emulators.md`.

## Live workspaces

The projects are built and the tools run in the user's **live** workspaces when they sit beside the repository (`VRUN_Nes_Dev`,
`SuperVRUN_dev`; a git worktree finds them too), not in the copies `restore` extracted into `BROKENDEV_HOME`. `list` shows which one each
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
| `SMB1_ORIG_ROM` | the user's own original SMB1 ROM | `P:\BrokenDev\SMB1 Topaz\smb1.nes` |
| `BROKENNES_DEV` | `1` switches BrokenDev on inside BrokenNes | off |

More: `docs/START_HERE.md` (a new machine, step by step), `docs/smb1-knowledge.md` (what reverse engineering SMB1 taught),
`tests/run-tests.ps1` (the scripts' own tests, each shown able to fail).
