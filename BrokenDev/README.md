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
BrokenDev\brokendev.ps1 diff vrun-nes          # differential replay against Mesen 2 as a black-box oracle (NES: needs BROKENDEV_TRACE_LUA)
BrokenDev\brokendev.ps1 run supervrun          # open the built ROM in BrokenNes (desktop app through its HTTP API)

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

## Where things live

* **In git (this folder):** `brokendev.ps1`, `doctor.ps1`, `lib/`, `projects/*/project.json`, `archives.json`, `tools/`, `docs/`,
  `tests/`. Light: no ROM, no archive, no toolchain, no build output, no file over 1 MB.
* **Outside git, in `BROKENDEV_HOME` (default `P:\BrokenDev\env`):** the restored projects, `out/<id>/` (built ROMs, build and
  verify records, screenshots, logs), the Workshop build, work folders of the pipelines.
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
| `BROKENDEV_TRACE_LUA` | the user's Mesen tracer `state_trace.lua` (his VRUN repository, `tools\`), needed by `diff` for the NES projects | none: the NES `diff` is skipped, with the reason |
| `SMB1_ORIG_ROM` | the user's own original SMB1 ROM | `P:\BrokenDev\SMB1 Topaz\smb1.nes` |
| `BROKENNES_DEV` | `1` switches BrokenDev on inside BrokenNes | off |

More: `docs/START_HERE.md` (a new machine, step by step), `docs/smb1-knowledge.md` (what reverse engineering SMB1 taught),
`tests/run-tests.ps1` (the scripts' own tests, each shown able to fail).
