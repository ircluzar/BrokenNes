# BrokenDev tools: the editors and checkers of the games, brought back

Date: 2026-10-20. Ask (verbatim): "I would like BrokenDev to stand on its own legs as a toolkit. we already previously created a ton of
little tools when working on CTW nes demake, i hope we can bring them back, like the tile editor and the tool for assigning sounds to
events. even if we dont need to do that kind of work right now, it would be very important to make sure that BrokenDev as a toolkit has
everything needed to edit the games like that. don't complexify it more than needed, we just want to regain support to things that we
previously made, not add new ones."

So: no new tool. Every tool below existed; BrokenDev now finds it in the **live** workspace, starts it the way it was always started, and
the record says how each was proven to work on this machine. Commands: `BrokenDev\brokendev.ps1 tools` and `tool <id>` (see
`BrokenDev\README.md`). Screenshots of every tool: `%LOCALAPPDATA%\VRUN_Nes_Dev_work\brokendev_tools\shots\` (outside git).

## 1. What changed in BrokenDev

* **Live workspaces** (`archives.json` `live` sections, `lib\Archive.ps1`): `{root:...}` resolves into `VRUN_Nes_Dev` and `SuperVRUN_dev`
  beside the repository (found from a git worktree too) instead of the restored snapshots under `BROKENDEV_HOME`;
  `BROKENDEV_LIVE_<ARCHIVE>=<folder>|off` overrides; the restored copy stays the fallback of a new machine, and so does a root the live
  workspace does not have (`svrunpkg`: Super VRUN's package folder, with its Mesen settings and `MNES.sf2`). `list` and `doctor` show
  the workspace of every project; a build records it and needs no restore when the workspace is live.
* **Tools registry** (`devtools.json`, `lib\DevTools.ps1`): 21 entries, validated like the manifests. Verbs `tools [project]`,
  `tool <id> [args]`, `-NoBrowser`, `-Stop`. A web UI is started hidden on its own port (through `cmd` by ShellExecute, so a caller that
  captures output is not held), recorded by PID in `BROKENDEV_HOME\work\tools\<id>\server.json`, and opened in the default browser; a
  copy already answering on the port is reused, another program there is reported and never stopped. A script runs in the foreground
  with its output shown live and kept in `work\tools\<id>\run.log`, its environment defaults (a variable you set wins), extra arguments
  and a timeout. A tool may ask for a Python with more modules (`minPython`, `pythonModules`) and gets the first one that has them.
* **The user's Mesen tracer** and its 19 companion scripts (VRUN accuracy campaign, 2026-09-04/05) are in `BrokenDev\tools\vrun\mesen\`,
  copied unchanged from the older `PROJECTS\sys0` clone (their only copy); `diff` uses that `state_trace.lua` unless `BROKENDEV_TRACE_LUA`
  names another.
* **The SFX configurator** is regenerated from the current game (`BrokenDev\tools\vrun\sfx-configurator\`): `template.html` is the August
  page with its data taken out, `events.json` its 44 events tied to today's call sites, `generate.py` fills it in.
* **Old-machine state of the Nested Builder** (`out\ui\state.json`, git-ignored, kept once as `state.json.oldmachine`): Mesen pointed at
  `C:\PROJECTS\...` (no harvest, no Play in Mesen) and last destinations under `C:\Users\user\Desktop\...` (a build would have created
  that folder). BrokenDev points Mesen at the live VRUN workspace's, drops destinations whose folder does not exist and lists the live
  VRUN builds first, each time it starts the tool.
* **Tests**: `BrokenDev\tests\run-tests.ps1` 19 new cases, each with a negative control; 77 of 77 pass (section 5).

## 2. Game-repository commits (local, not pushed)

Two scripts could not be fixed from outside, so they were fixed in the VRUN repository (`VRUN_Nes_Dev\sys0\simulations\VRUN`, branch
`vrun-history-export`). Nothing else in any game repository was changed.

| Commit | File | Why |
|---|---|---|
| `952a0b1` | `tools/audioplayer_configurator/server.py` | Upload and Analyze failed (`module 'build_music' has no attribute 'MIDI2NES'`): `analyze()` still ran the deleted `midi2nes.py`. It now makes the build's own call (`ensure_converter` + `run_midi2nes`, the C# converter). |
| `b239f01` | `tools/verify_pipe_joints.py` | The atlas path was hard-coded to `C:\PROJECTS\sys0\simulations\mz\...` (FileNotFoundError here); now beside VRUN, where `prebuild.sh` reads it. |

`VRUN_Nes_Dev\WORKSPACE.md` asks for the git bundles in `VRUN_Nes_Dev\git\` to be refreshed after new commits; that was not done.

## 3. The inventory

"Kept" = started by `brokendev.ps1 tool <id>`. Verified on 2026-10-20 through BrokenDev, live workspaces, Python 3.14.7 (3.10 for the music
modules), headless Edge over the DevTools protocol for the web UIs.

| Tool (id) | Came from | Lives now | Edits / checks | Started by BrokenDev as | Verified | State |
|---|---|---|---|---|---|---|
| Tile editor (`vrun-tile-editor`) | VRUN `tools/tile_editor` (2026-09-04, extended to blocks, doors, spikes, wires), old sys0 launch entry `vrun-tile-editor` | same, live VRUN repo | the 33 hand-authored art objects of `src/game/art` | `python tools/tile_editor/server.py --port 8420`, browser at `/` | every object opened in the UI (33 of 33: frames drawn, preview lit, 8 network previews rendered); API save of all 33 objects back to disk: 0 PNG md5 changed (checked first on a scratch copy); `git status` clean | kept |
| SFX configurator (`vrun-sfx-configurator`) | the one-off page "VRUN SFX Configurator" of 2026-08-24 (published artifact, in no repository) | `BrokenDev\tools\vrun\sfx-configurator` (template + generator) | which clip each sound event plays; exports JSON for applying to `main.fab` | `generate.py --vrun <VRUN> --out work\tools\vrun-sfx-configurator`, opens `index.html` | page lists all 95 `trigger_*` call sites of today's `main.fab` (55 named from the August catalogue, 40 from their function: puzzles, death and boss sequences, the SFX Test menu) plus 5 August events with no call site today; 22 clips decode in the browser, durations equal the WAVs, every clip byte-identical to an independent `render_sfx_wav.py` run; the renderer's clip bytes and PERIOD tables equal `audio_engine.fab` / `music_data.fab`; Download JSON works in a normal browser (file written, August shape `{generated, events:{id:{name, loc, previous, assigned, changed}}}`, 100 events, 0 changed, then 1 changed after a rebinding in the UI) | kept (regenerated) |
| AudioPlayer configurator (`vrun-audioplayer-configurator`) | VRUN `tools/audioplayer_configurator` (2026-09-24), old sys0 launch entry | same, live VRUN repo | `src/audioplayer/roster.json`, `src/audioplayer/midi/`; builds and plays the AudioPlayer ROM | `python tools/audioplayer_configurator/server.py --port 8421` | roster of 11 songs listed; adding a library song analysed it with the C# converter (level2: 3929 bytes, 47.06 s loop) after the fix `952a0b1`; save round trip left `roster.json` md5 unchanged; Build: "PASS - ROM matches the target cartridge spec", ROM rewritten; Download .nes = the built ROM; Play in Mesen opened it (window captured); the same ROM on BrokenNes (Workshop, FIX cores): song 02 playing, audio peak 0.42, 961 of 1140 frames non-silent | kept, one fix |
| Nested Builder (`nested-builder`) | Project-Nested `tools/m30pipe/ui` (2026-09-24), launcher `Nested_VRUN\Nested Builder.cmd` | same, live Project-Nested repo | SNES ROM of a VRUN build (Project Nested settings, recipe, harvest) | `python server.py --port 8765` in `m30pipe\ui`, browser at `/` (state repaired first) | the live `vrun_game.nes` (PRG `10c559ba`) inspected: supported, "new build of vrun" recipe based on `4ef0ec54`, harvest, save sectors $75/$7A from the `.mlb`, 35 settings shown; the live `vrun_game_nested.nes` (`17dd5f8e`) matched its profile (185 entry points); full SNES build in 5 s to a scratch folder: md5 `626411d6...`, **byte-identical to the frozen `builds\2026-09-27_nested\vrun_game_nested_SNES.smc`**; that ROM on BrokenNes: in game at frame 1500, audio peak 0.20; no new file in Project-Nested's `git status` | kept |
| CTW room designer + original CTW (`ctw-room-designer`) | CTW replacement engine `tools/replacement-engine/Launch-VRUN.mjs` + `vrun/designer.html`; old sys0 launch entry `ctw-original` (port 8766); Super VRUN's `tools/ctw/ctw_serve.py` served the same game on the same port | `VRUN_Nes_Dev\sys0\simulations\mz\CTW` (not a git repository) | CTW room templates `rooms/*.xml` (113), drafts into `dlc/rooms`, seeded maps, the original game | `node tools/replacement-engine/Launch-VRUN.mjs --port 8766 --windowed --nomusic`, browser at `/designer.html` | 113 rooms, validation PASS, 60-block palette; a room's 16x14 grid and its 7 layers; seed 66066 map: 217 rooms from 113 templates; Save draft wrote and validated `dlc/rooms/abby_AccuracyArena_draft.xml`, removed again (folder as before); `/game.nophp` runs the original CTW (title screen) | kept |
| SFX renderer (`vrun-render-sfx`) | VRUN `tools/render_sfx_wav.py` (2026-08-24) | live VRUN repo | WAVs of the 22 clips | `python tools/render_sfx_wav.py --out-dir work\tools\vrun-render-sfx` | 22 WAVs written, exit 0 | kept |
| Pipe-joint checker (`vrun-pipe-joints`) | VRUN `tools/verify_pipe_joints.py` (2026-09-07) | live VRUN repo | wire tile edges across every room | `python tools/verify_pipe_joints.py` | failed on the old `C:\PROJECTS` atlas path, fixed (`b239f01`); runs: hand-authored wires 290/621 broken joints, per-tile edge table of the 8 wires | kept, one fix |
| NES colour measurer (`vrun-measure-rgb`) | VRUN `tools/measure_nes_rgb.lua` + `.py` (2026-09-07) | live VRUN repo | the RGB Mesen paints for palette bytes | Mesen `--testRunner` with the Lua, then the Python on its shots | 6 default bytes measured, NES_RGB lines printed, exit 0 | kept |
| Room warp (`vrun-warp-room`) | VRUN `tools/warp_room.lua` | live VRUN repo | screenshot of any room | Mesen `--testRunner` | warped to room 32, `warp_room32.png`, exit 0 | kept |
| Screenshot (`vrun-screenshot`) | VRUN `tools/screenshot.lua` | live VRUN repo | a PNG at a frame | Mesen `--testRunner` (its exit 1 means done) | `capture.png` at 60, and at 900 holding Start | kept |
| Room teleport (`vrun-room-teleport`) | VRUN `tools/room_teleport.lua` | live VRUN repo | loads rooms through the real loader | Mesen `--testRunner` | runs: all 12 rooms entered, 12 screenshots; its STATIC-VS-LIVE comparison fails in every room (see gaps) | kept, tool stale |
| Corruption detector (`vrun-corruption-detector`) | VRUN `tools/corruption_detector.lua` (Phase 15) | live VRUN repo | CHR scatter, floor writes, NMI margin | Mesen `--testRunner` | runs: CHR scatter ok, margin ok (scanline 258 of 260), floor writes FAIL (650, see gaps); with `VRUN_DET_INJECT=chr` CHR fails too (negative control) | kept, one check stale |
| Regression suite (`vrun-regression-suite`) | VRUN `tools/run_regression_suite.sh` + `tools/*_check.lua` | live VRUN repo | every check of the game | `bash tools/run_regression_suite.sh` | 67 of 68 pass in about 15 minutes; the one failure is `start_at_option_check.lua`, the known, documented one (START AT spawn bug, blocked by the ROM budget) | kept |
| Laser puzzle solver (`vrun-laser-solver`) | VRUN `tools/solve_laser_puzzles.mjs` | live VRUN repo | the laser/mirror puzzle rooms | `node tools/solve_laser_puzzles.mjs` | runs, reports all 12 rooms unsolvable: it decodes the rooms as solid walls (see gaps) | kept, tool stale |
| Room reachability (`vrun-room-reachability`) | VRUN `tools/verify_room_reachability.mjs` (Fix K) | live VRUN repo | softlock guard | `node tools/verify_room_reachability.mjs` | runs, 148 failures for the same reason (see gaps) | kept, tool stale |
| 6502 disassembler (`vrun-dis6502`) | `VRUN_Nes_Dev\extras\dis6502.py` (the VRUN agent's, never in a repository) | live workspace `extras` | disassembly of NESFab code with `.mlb` labels | `python extras/dis6502.py <args>` | `rand@31_0` (PRG `07D01E`, CPU `D01E`) disassembled with `rng_state@0` labelled | kept |
| Super VRUN self-test (`svrun-selftest`) | Super VRUN `build.sh --test` | live SuperVRUN_dev | lint + build + `tests/selftest.lua` | `bash build.sh --test` | lint and build pass (ROM built, `ramcheck ... OK`); the Mesen stage **cannot run on this machine yet**: Super VRUN's own Mesen 2 has never been set up here and opens its first-run "initial setup" dialog, which a headless test cannot answer (stopped by PID after 10 minutes; `SELFTEST: FAIL (exit 127)`; the `Documents\Mesen2` folder that run created was removed again). See gaps | kept, needs a one-time Mesen setup |
| Super VRUN self-play (`svrun-selfplay`) | `tests/run_selfplay.sh` | live SuperVRUN_dev | lag proof | `bash tests/run_selfplay.sh` | not run: needs the same Mesen setup | kept, needs a one-time Mesen setup |
| Super VRUN feature check (`svrun-check <name>`) | `tests/run_*_check.sh` (16) | live SuperVRUN_dev | one feature check | `bash -c 'tests/run_<name>_check.sh'` | without a name it lists the 16 checks (exit 2 by design); the checks themselves need the same Mesen setup | kept, needs a one-time Mesen setup |
| Super VRUN negative control (`svrun-negcontrol <name>`) | `tools/negcontrol-*.sh` (9) | live SuperVRUN_dev | proves a check can fail | `bash tools/negcontrol-<name>.sh` | lists the 9; `soundbank` (no Mesen) ran: 3 of its 4 controls are rejected as required, the over-limit one can no longer be synthesised from today's `rosbif.it` (1 byte of order padding, it needs 33) and reports DECORATION (see gaps); the 8 others need the Mesen setup | kept |
| Super VRUN music modules (`svrun-music-modules`) | `tools/music/make_modules.sh` (2026-09-27 benchmark) | live SuperVRUN_dev | `audio/*.it` from VRUN's MIDIs + `MNES.sf2` | `bash tools/music/make_modules.sh` with a Python that has `mido` (3.10 here) and `MNES_SF2` = the package's `desktop/MNES.sf2` | 11 modules rebuilt in 13 s, **byte-identical** to the repository's `audio/*.it` (checked first in a scratch copy), `git status` clean | kept |
| Mesen tracer + 19 probes | sys0 clone only (`state_trace.lua`, `Run-MesenSegmented.ps1` in its unpushed commit `8536ccd`, 18 untracked) | `BrokenDev\tools\vrun\mesen\` | per-frame state traces | used by `diff vrun-nes` | `diff vrun-nes` with `BROKENDEV_TRACE_LUA` unset: **pass**, 1200 frames, 38 columns compared, 0 APU blips, Mesen 17 s (it was skipped before for want of the tracer) | kept (copied) |

Not kept, and why:

| What | Where | Why not |
|---|---|---|
| `mz-static`, `mz-server`, `mz-web`, `sys0-static`, `rabbitwine-static`, `imagine-*` launch entries | old sys0 root `launch.json` | other projects of the sys0 monorepo (the MZ web game, Imagine), not VRUN tools |
| `tools/ctw/ctw_serve.py` | Super VRUN | needs `mz/` beside it, which the live clone does not have (2.1 GB, not in git); `ctw-room-designer` serves the same game on the same port |
| asset generators (`ctw*.py`, `bgcode.py`, `hudfont.py`, `gen_floorfo.py` ...), `srccheck.py`, `ramcheck.py`, `sfxaram.py`, `spcpatch.py`, `check_modules.py`, `check-*.sh` | Super VRUN `snes-dev/supervrun/tools` | build internals: `build.sh` runs them on every build |
| `snes-corruption-guard`, `snes-corruption-sim` | Super VRUN `tools/` | C libraries compiled into the game; their checks are part of the self-test (codes 91, 93, 110) |
| `nes-assets/`, `snes-assets/` (incl. `snes-mapbake.mjs`, `parity_*`) | Super VRUN `tools/` | the early port's converters and NES-vs-SNES parity scripts, superseded by `snes-dev/supervrun/tools` |
| `tests/lab/run.sh`, `tools/mkprobe.sh`, `kill_test_mesen.sh` | Super VRUN | a menu/lab driver and a probe generator that need a hand-written script each; usable from the repository as its README says |
| `computer-control/computer.ps1` | Super VRUN `tools/` | a desktop-control helper for an agent, not a game tool |
| `chrbake.py`, `merge_sprite_sheets.py`, `gen_*`, `make_*`, `export_*_art.py`, `apply_*_art.py`, `nes-mapbake.mjs`, `verify_rom.py` | VRUN `tools/` | build internals (`prebuild.sh`, `build.sh`) or one-off art migrations |
| `diff_harness/` | Project-Nested `tools/` | the 2026-09-17 investigation's probes, full of that machine's scratch paths |
| `patch_scripts/` | Project-Nested `tools/` | the steps that build the pinned core (`core/build_core.py` runs them) |
| `m30pipe/harness` (hv, baseline, triage, bootstrap) | Project-Nested | the pipeline's own NES-vs-SNES harness, driven by the Nested Builder's harvest and `m30build.ps1`; `paths.local.json` already points at this machine |
| `tools/bncheck/`, `extras/conv_equiv.py`, `tools/flp`, `midi_bend_scan.py`, `bend_encode_proto.py` | `VRUN_Nes_Dev` | recent one-off checks of the resume and audio-bends work (bncheck overlaps `verify`/`diff`), `conv_equiv.py` has the old machine's paths |

## 4. Gaps (tools that never existed, or came back limited)

* **Super VRUN's Mesen 2 needs a one-time setup on this machine** before any of its Mesen tests run (self-test stage 2, self-play, the 16
  feature checks, 8 of the 9 negative controls): its first start opens Mesen's initial-setup dialog. The package's README (step 6) says how:
  copy the package's `mesen2_user_folder` (restored at `P:\BrokenDev\env\supervrun_dev\mesen2_user_folder`) to `Documents\Mesen2`, or
  answer the dialog once by hand. Not done here: it changes the user's Mesen setup. (BrokenDev's own `diff supervrun` is not affected: it
  runs a private copy with the package's settings.)
* **Rooms reach the NES only by hand.** A room editor did exist (the CTW designer, kept), but it saves drafts into `CTW/dlc/rooms`, while
  VRUN's map bake (`nes-mapbake.mjs`) reads only `CTW/rooms/*.xml`: a draft must be copied over a template by hand, then the game rebuilt.
  The designer shows block ids, not the NES tiles, and nothing previews a room as the NES draws it (`vrun-warp-room` screenshots a built
  room).
* **Four VRUN checkers are stale against today's map format** and report failures that are about the checker, not the game:
  `solve_laser_puzzles.mjs` and `verify_room_reachability.mjs` decode every room as solid wall (they read `room_<n>_rle` as plain RLE; the
  bake has since stored rooms raw-or-RLE and as template/instance data); `room_teleport.lua` standalone compares the static map against the
  live room without pinning `map_slot = 0` (the checks that use it as a library do), so STATIC-VS-LIVE fails in every room; the
  corruption detector's floor-writes check (650 writes on non-solid cells) probably reads the same static map. Updating them is game-repo
  work, left to the user. The regression suite itself is fine (67 of 68, the known failure).
* **Super VRUN's `negcontrol-soundbank.sh`** can no longer build its over-limit module (today's `rosbif.it` has 1 byte of order padding,
  the control needs 33), so that one control reports DECORATION; its three bank-registration controls work.
* **No editor for the sprites and enemies** (they are cropped from the CTW atlas by `gen_*_sprite.py` every build; the tile editor only edits
  what was carved out into `src/game/art`), **for palettes** (constants in `main.fab`), **for the SFX clips themselves** (opcode arrays in
  `audio_engine.fab`: the configurator assigns clips, it cannot compose one), or **for Super VRUN's art** (generated by scripts from CTW).
* **Super VRUN music has no UI**: MIDI + SoundFont to IT through `make_modules.sh` only; it needs `mido` (only Python 3.10 has it here) and
  `MNES.sf2`, which only the restored package has (`desktop/`); `sf2it.py` defaults to the old machine's `C:\Users\pgirard\Desktop\MNES.sf2`.
* **Super VRUN's test runners stop every `--testRunner` Mesen on the machine** (`kill_test_mesen.sh`), including another agent's or a
  BrokenDev `diff`: run them alone.
* **The old launchers still use the first `python` on PATH** (3.11 here, without Pillow): `Nested Builder.cmd` and VRUN's `.claude/launch.json`
  entries. BrokenDev starts the tools with the Python it found; the launchers were left as they are.
* `verify_pipe_joints.py`'s reference text ("381 broken joints of 7767, 4.9%") predates today's map; it now reads 290 of 621.

## 5. Tests and runs

* `BrokenDev\tests\run-tests.ps1`: 77 passed, 0 failed, 0 skipped (58 before this work; 19 new cases, each with its negative control:
  live workspace found from a worktree / not without its sentinels / named by `BROKENDEV_LIVE_<ARCHIVE>` / an invalid one refused / `off` /
  a build in a live workspace with nothing restored; the shipped `archives.json` and `devtools.json` validate and mutated copies are
  rejected; every shipped tool finds its files in the live workspace; a server tool starts, is reused, stops by PID, and a taken port is
  refused without touching its owner; script tools (environment defaults, arguments, exit codes, `okExitCodes`), page tools,
  `pythonModules`; the tracer copy; the Nested Builder state repair; the SFX configurator generator on a synthetic game, with a stale
  renderer flagged).
* `brokendev.ps1 build supervrun` in the live workspace: 37 s, ROM byte-identical to the reference (md5 `fa11f13f`), `git status` clean.
* `brokendev.ps1 diff vrun-nes`: pass (1200 frames, picture, PPU state and every APU register identical to Mesen; 0 blips), with the
  tracer copy in `BrokenDev\tools\vrun\mesen\` and no `BROKENDEV_TRACE_LUA`.
* Repositories afterwards: VRUN clean apart from the two commits of section 2; Project-Nested unchanged (the same 20 untracked backups and
  caches as before); SuperVRUN_dev clean; the CTW folder as before (the test draft removed); `sys0` untouched. Every web UI BrokenDev
  started was stopped again by its recorded PID; the Mesen window the AudioPlayer configurator opened was closed by its PID.
* Screenshots (outside git), `%LOCALAPPDATA%\VRUN_Nes_Dev_work\brokendev_tools\shots\`: `brokendev-tools.png` / `.txt`,
  `brokendev-list.png` / `.txt`, `tile-editor_*`, `sfx-configurator_*`, `audioplayer-configurator_*` (UI, built, Mesen window, the ROM on
  BrokenNes), `nested-builder_*` (start, new-build recipe, settings, exact profile, build done, the SNES ROM on BrokenNes),
  `ctw-room-designer_*`, `ctw-original-game.png`, `script-vrun-warp-room_room32.png`, `script-vrun-screenshot_f900.png`.
