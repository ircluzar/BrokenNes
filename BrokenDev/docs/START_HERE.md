# BrokenDev: start here (a new machine, step by step)

BrokenDev turns the user's homebrew projects into a devkit that lives inside BrokenNes: restore them from their archives, build every
version with its own toolchain, and prove on the BrokenNes emulators that each one runs. This page is the whole procedure for a machine
that has never seen it. It was written from, and tested by, doing exactly this on 2026-10-16.

## 1. What you need

| Tool | Why | Checked by `doctor` |
|---|---|---|
| Windows 10 or 11, Windows PowerShell 5.1 (built in) | the scripts; the Nested pipeline only works in 5.1 | yes |
| PowerShell 7 (`pwsh`) | `UAT\diff-trace.ps1` (the differential replay) does not parse in 5.1 | warns in the diff verb |
| Git for Windows (Git Bash) | every project builds through bash scripts | yes |
| Python 3.10+ with `pillow` and `numpy` | asset baking, ROM checks, the Nested pipeline, image comparison. Any Python that has both is found; `BROKENDEV_PYTHON` forces one | yes |
| Node.js 20+ | VRUN's map bake | yes |
| .NET 10 SDK | VRUN's music converter, and building BrokenNes itself | yes |
| 7-Zip (`C:\Program Files\7-Zip\7z.exe`) | restoring the archives | yes |
| .NET Framework 4.8 (built into Windows) | Project Nested's injector | yes |
| about 12 GB free where `BROKENDEV_HOME` lives (30 GB for `-Full` restores) | the restored projects | yes |

Everything else (NESFab, Mesen, Project Nested, the PVSnesLib toolchain, a bundled Python for SMB1) ships inside the project archives.

## 2. The archives

BrokenDev never contains the user's source. Put these three files where the scripts look (default `P:\BrokenDev`, or set
`BROKENDEV_ARCHIVES`):

* `VRUN_Nes_Dev_2026-10-12.zip` (405 MB): VRUN CTW (NES), Nested VRUN (SNES), their toolchains, the live-audio-bridge spec.
* `SuperVRUN_dev.zip` (2.5 GB): the current SNES project and its dev record.
* `SMB1 Topaz\SMB1_Remake_Project_20261011.zip` (905 MB): the NESFab SMB1 remake. The original game's ROM is the user's own and stays where the
  user keeps it (`SMB1 Topaz\smb1.nes`); it is not needed to build the remake (see `docs/smb1-knowledge.md`).

## 3. The commands

```powershell
cd <repository>
$env:BROKENDEV_HOME = 'D:\BrokenDev\env'        # optional; default P:\BrokenDev\env; must be OUTSIDE the repository
BrokenDev\brokendev.ps1 doctor                  # 1. is the machine ready?
BrokenDev\brokendev.ps1 restore all             # 2. extract the three archives (about 15 minutes on a network share, less locally)
BrokenDev\brokendev.ps1 build-workshop          # 3. the headless BrokenNes runner (about 1 minute)
BrokenDev\brokendev.ps1 build all               # 4. all six versions (about 5 minutes)
BrokenDev\brokendev.ps1 verify all              # 5. run them on BrokenNes: boot, input, hashes, audio, speed
BrokenDev\brokendev.ps1 diff all                # 6. (optional) differential replay against Mesen 2 (the NES ones use the user's tracer, kept in BrokenDev\tools\vrun\mesen)
BrokenDev\brokendev.ps1 run supervrun           # open one in the BrokenNes desktop app
BrokenDev\brokendev.ps1 tools                   # 7. the editors and checkers of the games; 'tool <id>' starts one (section 3a)
BrokenDev\brokendev.ps1 emulators install all   # 8. the other emulators (section 3b): Mesen, BizHawk, FCEUX, Snes9x, pinned releases
BrokenDev\brokendev.ps1 compat all              # 9. every game on BrokenNes and on every emulator of its console
```

**Live workspaces first.** When the user's own workspaces sit beside the repository (`VRUN_Nes_Dev`, `SuperVRUN_dev`), `build` and the tools
work on them, not on the restored copies, and step 2 is not needed for those archives (`list` shows which workspace each project uses). On a
machine that has only the archives, everything runs on the restored copies as before. `BROKENDEV_LIVE_<ARCHIVE>=<folder>` points at a live
workspace elsewhere; `=off` forces the restored copy.

## 3a. The tools

`brokendev.ps1 tools` lists the tools made while developing the games, with what you edit or check with each. `brokendev.ps1 tool <id>` starts
one: a web UI on the port it always used, opened in the default browser (`-NoBrowser` prints the address; a copy already running is reused;
another program on the port is reported and left alone; `-Stop` stops the one BrokenDev started), or a script in the foreground (arguments after
the id are passed to it; environment variables such as `VRUN_TARGET_ROOM` keep working: the entry's defaults never override yours).

| Tool | What it is for |
|---|---|
| `vrun-tile-editor` | draw the hand-authored VRUN art (bullet, breakable block, door overlay, spikes, wires, HUD, logo); a normal build picks it up |
| `vrun-sfx-configurator` | choose which sound plays on which event; Copy / Download JSON, then the bindings are applied to `main.fab` by hand or by Claude |
| `vrun-audioplayer-configurator` | arrange MIDI songs into the AudioPlayer ROM, build it, play it |
| `nested-builder` | turn a VRUN build into the SNES (Project Nested) ROM |
| `ctw-room-designer` | edit CTW room templates (VRUN's rooms come from them), generate seeded maps, play the original CTW |
| `vrun-*`, `svrun-*` scripts | the check and measurement scripts of both projects (`brokendev.ps1 tools` lists them) |

Where each one came from, how it was verified on 2026-10-20 and what is still missing: `docs\projects\brokendev-tools.md`.

## 3b. Other emulators (compat)

`brokendev.ps1 emulators` lists them (installed or not, version, consoles, what each one's column can check). `emulators install <id|all>`
installs the pinned official releases portable into `BROKENDEV_HOME\emulators\<id>-<version>`: a zip already in `emulators\_downloads`
with the pinned size and sha256 is used as it is; a missing one is downloaded from the pinned GitHub release URL only; a zip that does not
match is reported and left alone. BizHawk needs the Visual C++ 2015-2022 x64 runtime and .NET Framework 4.8: `emulators` says when one is
missing; BrokenDev never installs system components.

* `compat <project|all> [-Emu id,id] [-Scenario id]` runs the project's `compat` scenarios on BrokenNes first (the reference column, through
  the Workshop: **it needs a Workshop built from this repository after 2026-10-22**, `build-workshop`; an older one is reported as
  "cannot be tested") and then on every installed emulator of its console. Open `out\<project>\compat\compat.html`, or `out\compat.html`
  for the matrix of every project.
* `play <project> -Emu <id>` opens the built ROM in one emulator for hand testing; its saves live in `work\play\<project>\<emu>\saves`
  (moved aside when the ROM changes: FCEUX's mapper-30 save is the whole PRG flash and would otherwise run the OLD build).
* Each emulator runs with its OWN defaults (power-on RAM, region): that is the point. BizHawk and FCEUX open windows during a run.
* Mesen's test runner is stopped by Super VRUN's own test scripts (they close every Mesen started with `--testRunner`): do not run them at the
  same time as `compat`.

`restore` takes an archive id (`vrun_nes_dev`, `supervrun_dev`, `smb1_topaz`), a project id, or `all`. By default it leaves out bulk that no
build needs (SuperVRUN's 1.3 GB music-lab renders and its Claude transcripts, the SMB1 parity pipeline's 5 GB of working debris); `-Full`
restores everything. A restore extracts into a staging folder first and moves into place only when 7-Zip succeeded, then writes
`.brokendev-restore.json` with the archive's size and the sentinel files it checked, so an interrupted restore never looks finished.

Outputs: `BROKENDEV_HOME\out\<project>\` holds the published ROM, `build.json` (steps, seconds, sha256, mapper, PRG CRC32), `verify.json`
(checkpoint hashes, audio, speed), `diff.json`, `shots\` (a PNG at every checkpoint: look at them) and `logs\`.

## 4. What was done on the first machine (2026-10-16), so you can compare

* Restore: three archives into `P:\BrokenDev\env` (a network share), 311 s, 420 s and about 200 s. No junction or `subst` was needed: no build or
  verify step reads an absolute path. The package's START_HERE asks for `C:\PROJECTS\sys0\...`; only the Nested Builder UI's default Mesen path
  and two side scripts (`verify_pipe_joints.py`, `mapscan.py`) use it. If you want the UI, make the junction yourself:
  `mklink /J C:\PROJECTS <BROKENDEV_HOME>\vrun_nes_dev` (no admin needed; the restored folder has `sys0\` directly inside).
* Python: the machine's `python` on PATH had no Pillow; the 3.14 install had both. The scripts pick the first interpreter that has both and put
  its folder first on PATH for the child processes (nothing is installed, nothing global changes).
* Rebuilds: SuperVRUN's ROM is byte-identical to the one the user plays (md5 `fa11f13f...`). The SMB1 remake's ROM is byte-identical to the project's
  final ROM (md5 `2b02d077...`). VRUN's NESFab output is NOT byte-reproducible (two builds of the same source gave different files, same pictures).
  Nested builds follow their NES input.

## 5. Traps this procedure already fell into

* **`python` on PATH is not necessarily the one with Pillow.** The doctor reports which interpreter it picked.
* **Never kill a process by name.** `Stop-BdProcessTree` kills one tree by PID; the build runner only ever kills what it started.
* **A failed NESFab build leaves the old `.nes` and empties the `.mlb`.** The runner requires every output to have been rewritten by the build
  (its timestamp must be newer than the build's start), so a stale ROM can never pass for a fresh one.
* **PowerShell variables are case-insensitive.** `$Post` and `$post` are one variable; a helper silently dropped its post-build steps this way
  (now guarded: the Nested helper checks the build record for every step it was asked to run).
* **The SMB1 remake needed zeroed RAM until 2026-10-22.** Builds of the archive's source still do (see `docs/smb1-knowledge.md`); the live
  workspace (`PROJECTS\SMB1_Remake_dev`) has the fix (its reset code zeroes RAM) and runs with any power-on RAM. The BrokenDev menu's
  Power-on RAM lever and the `powerOnRam` field of a verify scenario are how to check a game for this.
* **Mesen kills a test-runner script after about 100 seconds**, so differential replays are limited to a few thousand frames.
* **The NES differential needs the user's own Mesen tracer** (`state_trace.lua`, in no archive and in neither repository: its only copy was an
  older local clone, `PROJECTS\sys0`). BrokenDev keeps it, with the 19 other tracer and probe scripts of that campaign, in
  `BrokenDev\tools\vrun\mesen\`; `BROKENDEV_TRACE_LUA` can still name another one.
* **Old-machine paths.** The tools came from a machine where the projects lived in `C:\PROJECTS\sys0\...`. BrokenDev starts them with the
  paths of this machine (and the Python it found: the first `python` on PATH may lack Pillow); two scripts that hard-coded the old path or
  called a deleted script were fixed in the VRUN repository itself (see `docs\projects\brokendev-tools.md`).
* **Super VRUN's test runners stop every Mesen started with `--testRunner`** (its `tools/kill_test_mesen.sh`), not only their own: do not run
  `svrun-*` checks while another Mesen test runs (the VRUN regression suite, a `diff`, another agent's).
* **Music (Phase 0)**: `BrokenDev\tools\music-replay-check.ps1 -Mid some.mid -Name mysong -Frames 2400 -Template` builds a one-song ROM from a
  MIDI file with VRUN's own converter, runs it on BrokenNes and checks it frame by frame (see `docs\projects\brokendev-music-pipeline.md`).

## 6. Where the rest is

* `BrokenDev\README.md`: what BrokenDev is and who it is for.
* `BrokenDev\docs\smb1-knowledge.md`: what reverse engineering SMB1 taught (no Nintendo code or data).
* `docs\projects\brokendev-report.md`: the table of all six versions with hashes, cores, checks and results.
* `docs\projects\brokendev-music-pipeline.md`: the design for composing in FL Studio and replaying on a ROM.
* `docs\projects\brokendev-tools.md`: the tools (inventory, origin, verification, gaps).
* `docs\projects\brokendev-emulators.md`: the other emulators, how each is driven, what each column can check, the compat matrix.
* `docs\projects\round5-bd.md`: this track's record.
