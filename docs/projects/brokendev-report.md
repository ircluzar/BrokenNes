# BrokenDev report: the six versions of the user's homebrew, built and run on BrokenNes

Date: 2026-10-16/17. Track `bd` of round 5. Machine: Windows 11, `BROKENDEV_HOME = P:\BrokenDev\env` (a network share), Workshop built from this branch
(`BrokenNes.Workshop`, CPU/PPU/APU FIX for the NES, SFC for the SNES). Every number below was produced by the commands in section 8 and is
recorded in `BROKENDEV_HOME\out\<id>\{build,verify,diff}.json` (not in git).

## 1. The table

ROM bytes and hashes are those of the final build on this machine. "Cores" are what the verification ran. "Checks" are the scenarios of
`BrokenDev/projects/<id>/project.json` (boot and play; details in section 3). "Diff" is the differential replay against Mesen 2.1.1
(section 4). Known issues are shortened; the manifests carry them whole.

| Project | Version | ROM (bytes, sha256 start, md5 start) | Cores | Checks | Diff vs Mesen | Known issues |
|---|---|---|---|---|---|---|
| `smb1-nes` | SMB1 remake, NES, mapper 30 | 524,304 `8fc726d4` `2b02d077` | CPU/PPU/APU FIX | boot 900 f, play 2400 f: pass (11 colours, audio peak 0.317 / 0.434, 250-310 fps). Needs zeroed RAM: with the FCEUX power-on pattern the game stalls (info scenario, 7 colours, no sound after frame 300) | **pass** after the APU fixes (2400 f; picture and PPU columns identical; 16 isolated APU blip frames of a budget of 24) | **Project bug: reads two variables it never initialises.** A first-quest remake whose parity cannot be proven by testing. Unmerged work in its pipeline notes |
| `smb1-nested` | SMB1 remake on the SNES through Project Nested | 4,194,304 `f6c48676` `25b52aac` | SFC/SFC/SFC | boot 1200 f, play 3000 f: pass (11 colours, peak 0.161 / 0.204, 680-1040 fps) | **pass** (7 checkpoints; worst residue 289 px at frame 900, best offset +5) | no DMC IRQ, no lag-frame emulation, no entry-point feedback |
| `vrun-nes` | VRUN CTW, NES demake, mapper 30 | 524,304 `3cfce10c` `a1e7bd6e` (first build `584a91bc`) | CPU/PPU/APU FIX | boot 900, play 2400, play with NTSC frame timing 2400: pass (8 / 14 colours, peak 0.318 / 0.503, 250-285 fps) | **pass** (1200 f; picture and every APU register identical) | ROM at its ceiling; **build not byte-reproducible**; one check of the project's own regression suite fails (known) |
| `vrun-nested` | VRUN on the SNES through Project Nested | 4,194,304 `8ab7360b` `0de8d5b8` | SFC/SFC/SFC | boot 1200, play 3000: pass (8 / 10 colours, peak 0.176 / 0.189, 740-850 fps) | **pass** (8 checkpoints; worst residue 32 px) | as `smb1-nested`; builds without entry-point feedback |
| `vrun-nested-enhanced` | the SNES variant with shaded sprites and blocks | 4,194,304 `102a52fc` `53b483b5` | SFC/SFC/SFC | boot 1200, play 3000: pass (19 / 36 colours, peak 0.176 / 0.196, 700-840 fps) | **pass** (8 checkpoints; worst residue 53 px) | tied to the game's tile layout |
| `supervrun` | Super VRUN, native SNES (C and 65816 on PVSnesLib) | 2,097,152 `4a56b5d8` `fa11f13f` | SFC/SFC/SFC | boot 900, play 1500: pass (22 / 39 colours, peak 0.608 / 0.625, 655-810 fps) | **pass**, 0 residual pixels at the best offset of each of 9 frames | frame budget has no slack by design; real-console DMA/HDMA behaviours Mesen and BrokenNes do not model |

`supervrun`'s ROM is byte-identical (md5 `fa11f13f`) to the one the user plays, and `smb1-nes`'s (md5 `2b02d077`) to the project's final ROM.

## 2. Builds

All six were built with `BrokenDev\brokendev.ps1 build <id>` (or `build all`) from the archives restored into `BROKENDEV_HOME`; no source of the user was
copied into the repository. Build times: `smb1-nes` 32 s, `vrun-nes` 41 s, `vrun-nested` 33 s (NES build 26 s + pipeline 7 s), `smb1-nested` 7 s (the
NES ROM is the `smb1-nes` output), `vrun-nested-enhanced` 4 s (dump + enhance on top of `vrun-nested`), `supervrun` 156 s.
Two rebuilds checked determinism: `smb1-nes` and `smb1-nested` came out byte-identical; `vrun-nes` did not (sha256 `584a91bc...` then
`3cfce10c...`, same pictures at the end of both UAT cases). NESFab is the suspect; the project's own `verify.sh` already rebuilds three times.
A build whose output was not rewritten fails ("stale ROM guard", tested with a negative control).
The Nested builds ran **without** the entry-point harvest (10 to 20 minutes and Mesen); that path is implemented in `BrokenDev/tools/nested-build.ps1` and
was **not run**.

## 3. Verification on BrokenNes

`BrokenDev\brokendev.ps1 verify <id>` runs scenarios in the headless Workshop (`--brokendev verify`): scripted input, N frames, a picture hash at
checkpoints (a PNG of each is kept for looking), the colour count, the audio peak/RMS and number of non-silent frames from the real mixer, and the speed.
A scenario fails on: a crash, fewer colours than expected, a picture that does not change between two checkpoints where it must, a ROM that ignores
the pad ("input reached the game": the play picture must differ from the boot picture at the same frame), silence where sound is expected, or running
below a real-time factor. Results (final run): see the table. Each of the six has a boot and a play scenario; `vrun-nes` also runs the same input with
"NTSC-accurate frame timing" (BrokenNes runs the NES 48.5 CPU cycles per frame long unless it is on; the app default is off).

How the verifier was shown able to fail (`BrokenDev/tests/run-tests.ps1`, 58 checks under Windows PowerShell 5.1 and under PowerShell 7, 0 failed): a ROM
that draws nothing fails; a ROM that ignores the pad fails the input check; a silent ROM fails an audio expectation; a manifest asking for more colours
than the picture has fails; an unknown power-on mode is refused rather than ignored; a missing ROM exits 2.

**SMB1 needs zeroed RAM** (project bug, section 6). `smb1-nes` runs its two scenarios with `powerOnRam: zeros`; a third, informational scenario runs with the
FCEUX pattern BrokenNes uses by default and records the stall (the picture is the same at frames 300, 900 and 2400: seven colours, no sound after frame 300). The
desktop app's BrokenDev menu has a Power-on RAM lever for this.

UAT: `UAT/cases/brokendev.json`, 12 cases (boot and play of each project) with goldens recorded and reproduced over two runs each. They skip, with the
reason, on a machine without the built ROMs (shown: pointed at a folder that does not exist the 12 SKIP and the suite exits 0; with `-StrictMissing`
the same 12 FAIL, exit 1). The whole suite after the APU fixes: **64 pass, 0 fail, 60 skip** (52 pre-existing passes unchanged + 12 new).

## 4. Differential replay against Mesen

`BrokenDev\brokendev.ps1 diff <id>`. Mesen 2.1.1 (the copy shipped in the VRUN archive) is a black-box oracle: the same ROM and the same pad script
are run in both emulators and a record per frame is compared. It is not a proof of hardware accuracy, it is a proof of agreement with Mesen.

* **NES** (`tools/diff-nes.ps1`, `lib/TraceGate.ps1`): the user's Mesen tracer `state_trace.lua` (his VRUN repository; `BROKENDEV_TRACE_LUA`) against
  Workshop `--trace`. Picture, nametable, OAM, palette, CHR and PPU control columns must agree exactly after a 10-frame boot window; so must every
  APU register, except **isolated single-frame blips** (a frame where one column differs but agrees on the frames before and after), which are
  listed in `diff.summary.json` and budgeted (1% of the frames, at least 5). CPU registers, the work-RAM hash and the PPU address `v` and its
  write toggle `w` are sample-skew columns (the instruction boundary and a mid-write phase cannot be aligned) and are information only.
  The gate is tested with synthetic traces and negative controls (a two-frame APU difference fails; a one-frame picture difference fails; too many
  blips fails; a trace without a columns header is refused).
  `vrun-nes`: 1200 frames, FCEUX RAM fill, picture, PPU and all APU columns identical (only `fbhash@0` differs, inside the boot window). Negative control: giving BrokenNes
  a different pad script made the gate fail.
  `smb1-nes`: 2400 frames, zeroed RAM. Picture and PPU columns identical. APU columns: before this round's APU fixes the gate failed (p1vol 43 frames,
  p2vol 142, tlin 176, ...); after them **16 isolated single-frame blips remain** on p1/p2 period, volume, length, duty, sweep and the status/flag
  columns, at frames 991, 1000, 1018, 1063, 1099, 1136, 1162, 1189, 1306, 1342, 1578, 1946, 1973, 1991, 2027 and 2374. Each is a frame where the game
  writes several sound registers inside the NMI that the trace's sample point straddles (Mesen shows the new values, BrokenNes the old, or the other way
  round); the next frame agrees. That is what Mesen's lazily updated APU state predicts (see the header of its tracer), but it was not proven for each
  blip and a real one-frame defect would be hidden by the rule. They are listed, not hidden.
* **SNES** (`tools/diff-snes.ps1`, `mesen-snes-capture.lua`, `imgdiff.py`): Mesen (a private copy with frame skipping off and RAM zeroed) saves a screenshot at
  chosen frames; BrokenNes (SFC) saves the same; the two are compared pixel by pixel within one colour level, at the best frame offset within
  -1..+5 (the two emulators apply the scripted pad some frames apart; the per-ROM `inputShift` is 2, 1, 4 and 3 frames for `supervrun`,
  `vrun-nested`, `vrun-nested-enhanced`, `smb1-nested`). Mesen's picture is 239 lines, BrokenNes's native-SNES picture 224: seven lines are cropped at the top, eight at the
  bottom. The gate is: every checkpoint identical at some offset, with a per-ROM ceiling for the leftover pixels (0, 64, 64 and 400).
  Measured: `supervrun` 0 differing pixels at the best offset of each of 9 checkpoints (offsets -1..2, so the two emulators drift by up to three frames over a run);
  `vrun-nested` worst 32 px (frame 600); `vrun-nested-enhanced` worst 53 px (frame 2400); `smb1-nested` worst 289 px (0.5% of the picture) at frame 900, zero at the other six.
  Negative control: for `supervrun`, giving BrokenNes no input made the gate fail.
  This compares a few pictures, not whole runs: **it is not frame-by-frame**.

## 5. What the work changed in BrokenNes (outside `BrokenDev/`)

Small and additive, except the first item.

1. **`Windows/NesEmulator/apus/APU_FIX.cs` (the default APU core): five accuracy fixes**, each found on the SMB1 remake against Mesen and each with a guard in
   `BrokenDev/tests/apu-regress.ps1` that **fails on the pre-fix build and passes now** (hand-assembled programs the test writes itself, values measured on Mesen 2.1.1):
   * 5-step frame sequencer: the half-frame clocks were one step early, so a game writing `$4017 = $FF` every frame ran its length counters three times a frame instead of two (guard: pulse length lasts 4 frames, Mesen 4; before: 3);
   * the sweep unit ignored its enable flag (guard: period stays `0x17C`, before: `0x0EE`);
   * the sweep update ran one half-frame late (guard: `1ac 1e2 220 266 2b4 30c`, as Mesen; before `193 1c6 200 242 28c 2df`);
   * a write to `$4000`/`$4004`/`$400C` restarted the envelope (hardware: only `$4003`/`$4007`/`$400F` does) (guard: a decayed envelope stays 0; before it came back as 8, 4, ...);
   * a write to `$4008` set the triangle's linear-counter reload flag (hardware: only `$400B` does) (guard: the counter counts down 69 65 61 ... 3d as Mesen; before it jumped back to 7c).
   While doing this I introduced and fixed a bug of my own: the new sweep divider counted below zero for ever on a game that never wrote `$4001`; the plugin selftest
   `runaway-state` caught it (a reopened project differed from a run that never left). After the fix the plugin's FruityHost selftest is 49 pass + the known
   QLQ warning, 0 failed, as before. Other games' audio can change where they relied on those quirks; that was checked only by the UAT suite (framebuffer hashes) and the plugin selftest (pitch, level, state), not by listening.
   The fixes bring the default core closer to Mesen; whether Mesen is right on hardware was not checked against a real console. The NES, SNES and Sega goldens of the UAT suite are unchanged (64 pass, 0 fail).
2. `Windows/EmulatorConfig.cs`: `brokenDevEnabled` (default off) and `BROKENNES_CONFIG_DIR` (so a test can run the app with its own settings folder).
3. `Windows/BrokenDev/BrokenDevGate.cs`, `Windows/MainForm/MainForm.BrokenDev.cs` (new), one line each in `MainForm.Initialization.cs` and `MainForm.RomLoading.cs`:
   the BrokenDev menu (built ROMs, Power-on RAM lever, open the folder), shown only with `BROKENNES_DEV=1` or the config flag.
4. `Workshop/BrokenDevCli.cs` (new), dispatch in `Workshop/Program.cs` and the project file: `--brokendev list | verify <id> | dump-chr`.
5. `Workshop/RomTestCli.cs`: `--power-on-ram fceux|zeros|ones`; `UAT/run-suite.ps1`: a `powerOnRam` case key. `UAT/cases/brokendev.json`: the 12 cases.

## 6. Findings about the projects (documented, not hidden)

* **SMB1 remake reads two uninitialised variables** (`chr_test_active` at `$07FC`, `fire_anim_timer` at `$07AE`). It works because the project's own Mesen powers RAM on as zeros.
  Real hardware powers RAM on undefined; BrokenNes (like FCEUX) fills it with `00 00 00 00 FF FF FF FF`, which puts `$FF` in both: the digits 1, 2, 3 are drawn with the
  test-pattern tiles and the World card never ends. Found by bisecting the RAM fill (`$07AE` alone stalls the game) and the `.mlb`. Fix in the project: clear both at boot.
  This is a bug of the user's project, not of BrokenNes; the original game was not involved.
* The remake's own notes list unmerged work (five Fire Mario levels lag, castle 061/062) and a replay suite that compared game state, never pixels.
* VRUN's NESFab output is not byte-reproducible (section 2).
* Project Nested never clears the DMC-active bit of `$4015` and never delivers the DMC IRQ (the `dmc_status` post-build step handles the first; `smb1-nested` uses it); and it cannot
  reproduce NES lag frames. The Nested work is mostly uncommitted in the user's Project-Nested repository.
* Open in BrokenNes: the one-frame APU blips of section 4 are explained, not proven; the SNES comparison is a handful of pictures; no DMC DMA cycle stealing exists (an older finding, unchanged).

## 7. Not tested

* Anything with a real controller or keyboard on the six versions in the desktop window: the headless scripted input is what was run. The desktop menu was exercised with an idle probe ROM
  (`BrokenDev/tests/app-smoke.ps1`, 9 checks), not with each of the six.
* Nobody listened. Audio is measured as peak, RMS and non-silent frames.
* Parity of the SMB1 remake with the original game (the original ROM was not used to build or to check anything; the project's own pipeline does that and it was not re-run).
* The Nested builds with the harvest (entry-point feedback); saves/flash persistence of the three Nested ROMs.
* Mesen differential beyond 1200/2400 frames (Mesen's test runner stops a script after about 100 s) and any comparison on real hardware.
* The music pipeline is a design; only its Phase 0 was run (`brokendev-music-pipeline.md`).

## 8. Reproduce

```powershell
cd <repository>
BrokenDev\brokendev.ps1 doctor ; BrokenDev\brokendev.ps1 restore all ; BrokenDev\brokendev.ps1 build-workshop
BrokenDev\brokendev.ps1 build all ; BrokenDev\brokendev.ps1 verify all
$env:BROKENDEV_TRACE_LUA = '<his VRUN repo>\tools\state_trace.lua' ; BrokenDev\brokendev.ps1 diff all
BrokenDev\tests\run-tests.ps1 ; BrokenDev\tests\apu-regress.ps1 -Workshop <Workshop.exe>
UAT\run-suite.ps1 -Exe <Workshop.exe> -Filter 'brokendev*'
```
