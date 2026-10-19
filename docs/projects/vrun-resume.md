# Resuming the VRUN games: VRUN CTW (NES), Nested VRUN (SNES) and Super VRUN (SNES)

Date: 2026-10-18. Agent `vrun-dev`, BrokenNes branch `vrun-resume` (worktree under `%LOCALAPPDATA%\VRUN_Nes_Dev_work\bn_vrundev\wt`).
The user's request: make sure the three games compile on this machine and run correctly on BrokenNes, so development can resume. SMB1 was
out of scope (it is only touched where a BrokenNes change moved one of its UAT goldens).

Nothing of the user's was moved, merged, committed or overwritten. Builds ran in scratch copies; the tested ROMs are frozen in the workspace's
`builds\` folder, as its recipe says. Every decision below is the user's.

## 1. Where each game lives, and which copy is the newest

| Game | Copies on this machine | Newest | Evidence |
|---|---|---|---|
| **VRUN CTW (NES)** | live workspace `PROJECTS\VRUN_Nes_Dev\sys0\simulations\VRUN` (git, branch `vrun-history-export`, HEAD `6c561f6` 2026-09-25, 1 uncommitted file); BrokenDev restore `P:\BrokenDev\env\vrun_nes_dev\sys0\simulations\VRUN` (from `VRUN_Nes_Dev_2026-10-12.zip`, no `.git`, bundle `git\vrun-history.bundle` identical to the live one); older snapshot `P:\VRUN Nes dev 2026-09-15` | **the live workspace** | The archive tree equals the live HEAD file for file (its `main.fab` is byte-identical to `HEAD:src/game/main.fab`; the only extra file is the untracked September diagnostic `tools/backdrop_pipe_scan.lua`). The archive's own START_HERE §7 says "VRUN: no new commits". The live copy adds the user's uncommitted `src/game/main.fab` (2026-10-01 13:01: `tear_active` initialised at its declaration; built and frozen by the user as `builds\2026-10-01_tearfix`). |
| **Nested VRUN (SNES)** | live `PROJECTS\VRUN_Nes_Dev\Nested_VRUN\Project-Nested` (git, 1 commit `99a6541` on Project Nested v1.8.1, 39 uncommitted entries); BrokenDev restore `P:\BrokenDev\env\vrun_nes_dev\Nested_VRUN\Project-Nested` (same commit, same 3 tracked-file diffs) | **neither: they diverged** | Live only: profile `games\17dd5f8e` (the 2026-09-27 VRUN nested build) and the machine's `harness\paths.local.json`. Archive only (old machine, 2026-10-02..12): SMB1 work: profile `games\d257beb1`, `tools\dmc_status.py`, a "post-build steps" section in `core\STEPS.md`, `out\ship\SMB1_Repro*` builds, and a newer memory note `claude\memory\nested\snes_qol_work.md`. For **VRUN** the live copy is the one to use (it has the latest VRUN profile); the archive's additions are all SMB1. Core `out\core` is identical in both. |
| **Super VRUN (SNES)** | only the BrokenDev restore `P:\BrokenDev\env\supervrun_dev\SuperVRUN` (from `SuperVRUN_dev.zip`, 2026-10-14; git branch `two-player`, HEAD `d428c83`, last code commit `7c55bd4` 2026-10-13); a recycled duplicate restore in `P:\#recycle` (same HEAD); loose ROMs (`P:\SuperVRUN_bglab.smc` md5 `fa11f13f` = the ROM of `7c55bd4`; older `Downloads\SuperVRUN*.smc`, `P:\vrun proto roms\SuperVRUN.smc`) | **the restore** (only copy) | Searched the user folder, PROJECTS, Desktop, D:, E:, P:. The package README says it was developed on another machine (`C:\Users\pgirard\Desktop\SuperVRUN`). Its working tree is clean apart from the two folders the README says stay untracked. |

## 2. Builds on this machine (2026-10-18)

All toolchains were already present (Git Bash, Python 3.14 with Pillow and numpy, Node 25, .NET 10.0.300, Windows PowerShell 5.1, the shipped NESFab, Mesen,
Project Nested injector and PVSnesLib). No environment fix was needed to build; the latent problems found are in section 6.

| Build | Source | Result | Frozen in `PROJECTS\VRUN_Nes_Dev\builds\` |
|---|---|---|---|
| VRUN CTW, HEAD | scratch `git clone` of the live repo at `6c561f6` + the shipped `tools\nesfab` + `mz` beside it | `PASS - ROM matches the target cartridge spec.`, `git status` clean, 24 s, md5 `fc38c9c2…` | `2026-10-18_nes_head\vrun_game.nes` + `.mlb` |
| **VRUN CTW, working tree** (HEAD + the user's uncommitted `main.fab`) | the same clone with that one file copied in | `PASS - ROM matches the target cartridge spec.`, `git status`: only ` M src/game/main.fab`, 20 s, md5 `d25a548f…` | **`2026-10-18_nes\vrun_game.nes` + `.mlb`** (tested below) |
| VRUN CTW `--nested` (working tree) | same clone, `./build.sh game --nested` | PASS, md5 `542a0ee2…`, PRG CRC32 `79b64e20`; flash sectors `0x75,0x7A` (`tools\flash_sectors.py`, unchanged since 09-27) | `2026-10-18_nested\vrun_game_nested.nes` + `.mlb` |
| **Nested VRUN (SNES)** | scratch copy of the live Project-Nested; new profile `games\79b64e20` derived from `17dd5f8e` (settings and `flash_patch --sectors 0x75,0x7A`); `bootstrap\bootstrap.ps1` run once with a private Mesen copy | converged in 290 s: 185 entry points (NES go-explore), 8 verification runs with **0 recompile stalls ≥2 frames** (1 runtime recompile in one run), core `57c4a845` (pinned), output md5 `7f2dc32d…` | `2026-10-18_nested\vrun_game_nested_SNES.smc`, `bootstrap_report.md`, `profile_79b64e20\` (game.json + calls.txt) |
| **Nested VRUN enhanced** | `tools\snes_enhance.py <smc> <out> --chr out\font\chr32k.bin --full-bevel-from 64 --anim-png src\game\anim_*.png` | md5 `aef6df11…`. The recipe's CHR dump is still valid: a fresh BrokenNes `--brokendev dump-chr` of the new build is byte-identical to `out\font\chr32k.bin` | `2026-10-18_nested\vrun_game_nested_SNES_enhanced.smc` |
| **Super VRUN** | in place on P: (only `build\`, git-ignored, is written) and again from a local `git clone` of `two-player` + a copy of the untracked `snes-stub\toolchain` | srccheck lints all pass, `ramcheck: 69 WRAM sections, .bss ends at $7EF78D, OK`, `Build finished`, **md5 `fa11f13f…` both times** (= the ROM the user plays), `.sym` identical; 127 s on P:, 45 s locally; clone's `git status` clean | `2026-10-18_supervrun\supervrun.sfc` + `.sym` |

Also checked: the Nested core chain rebuilds byte-identically on this machine (`python core\build_core.py --out <dir>`: all 18 steps MATCH, `57c4a845`).

## 3. How each game runs on BrokenNes

Cores: NES CPU/PPU/APU FIX (the default); SNES the SFC family (default). Oracle: Mesen 2.1.1 (the copies shipped in the packages, run as private
copies). "Identical" means pixel-identical within one colour level after the harness's frame-offset alignment.

### VRUN CTW (NES), `builds\2026-10-18_nes`

| Check | Result |
|---|---|
| BrokenDev verify recipe (boot, play, play with NTSC timing, input reaches the game, colours, audio, speed) | PASS (7-14 colours, audio peak 0.32-0.48, 228-274 fps) |
| 3 minutes of scripted play (10,800 frames, app default config: NTSC timing off): title, NEW GAME, rooms, shooting, materials, 4× pause → Select | pictures right at 18 checkpoints; WAV 180 s, 0 silent seconds, mean RMS 0.057 |
| Menu route (Options, Sound Test playing 4 songs, SFX Test, START AT 4, a new game in level 4, play) | every screen right; 73 s WAV with no silence |
| **Differential vs Mesen**, 10,800 frames of play (NTSC timing on, FCEUX power-on RAM on both) | every picture/PPU column identical on every frame after the boot window (nametables, OAM, palette, CHR, PPUCTRL/MASK/STATUS, frame as palette indices) |
| Differential vs Mesen, 9,000 frames of the menu route | the same; one frame where the scroll latch `t` was sampled mid write sequence (rendering off, picture identical) |
| APU | **the APU register-write streams are identical**: 11,821 writes (play) and 9,629 (menus) in the same order with the same values. The per-frame APU state columns differ on 149 / 256 column-frames, every one a single frame: 136 / 235 are the same value one frame earlier or later, the rest values that change inside the frame (a length counter mid-decrement, a transient noise write, the frame sequencer caught at a step boundary). BrokenDev's trace gate fails these long runs on its 1% blip budget (116 of 10,800, 203 of 9,000 frames); that is the heuristic, not the emulator. |
| **Saves** (the real desktop app, `BrokenDev\tools\vrun\vrun-save-check.ps1`) | 10/10: NEW GAME → pause → Select = **save and quit** (VRUN's only save) → title on CONTINUE → `<gameId>.flash.sav` (4,136 bytes) written → app closed and restarted → the game sees its save → CONTINUE enters the saved room. A second run that would overwrite an existing save is refused (checked). |

### Nested VRUN (SNES), `builds\2026-10-18_nested`

| Check | Result |
|---|---|
| BrokenDev verify recipes (plain and enhanced) | PASS (plain 7-10 colours, enhanced 19-36; audio peak 0.18-0.22; 740-900 fps) |
| Menu route (same as the NES, SNES pad: NES A = SNES B, NES B = SNES Y) and 3 minutes of play | right on both builds; audio throughout (151 s WAVs, 1-2 silent seconds at boot) |
| **Differential vs Mesen, plain build** (with the BrokenNes fix of section 4) | title → CHR switch → game frame by frame identical; the 9,100-frame menu route identical at all 22 checkpoints; the 10,800-frame play identical at all 19 checkpoints; 2 minutes with no input identical; all at one constant frame offset |
| Differential vs Mesen, enhanced build | the title starts one game frame differently in the two emulators (BrokenNes runs two NES frames in host frame 118, Mesen one per frame: `BrokenDev\tools\vrun\mesen-snes-wram-log.lua` on `map_slot`), so the harness needs input shift 2 to reach the same map; then gameplay is identical except one animated wall tile in another animation phase (28-39 px) at 3 of 10 checkpoints. Present before the fix too (section 5). |
| **Saves** (`--console-run --battery-out` → `--battery`) | plain and enhanced: pause → Select = save and quit → SRAM exported → a cold boot with it offers CONTINUE → CONTINUE resumes the saved room. Nested's flash emulation into SRAM works. |

### Super VRUN, `builds\2026-10-18_supervrun`

| Check | Result |
|---|---|
| BrokenDev verify recipe | PASS (22-39 colours, audio peak 0.61-0.63, 640-750 fps) |
| 3 minutes of play (classic scheme: jump B, fire Y), then pause → Select (save and quit) | the game's own HUD shows `LAG 0` throughout; title lands on CONTINUE 1P |
| **Saves** | SRAM exported (20 bytes non-zero); a cold boot offers CONTINUE 1P; it resumes the saved room. The same SRAM loads in Mesen as a normal `.srm`. |
| **Differential vs Mesen**, 10,800 frames | 13 of 18 checkpoints identical; 5 show 9-279 px on the player's sprite pose or bullets right after an input change, the frames after are identical again: the two harnesses latch a pad change a frame apart now and then. Same with and without the fix. |

## 4. BrokenNes fix made (branch `vrun-resume`)

**SNES HDMA charged the CPU 8 master clocks too little per active channel per line.** Every active HDMA channel reads the next table byte on every
line (the line counter), whether or not it reloads; `BOARD_SFC.RunHdmaLine` charged that fetch only on reload lines. Nested VRUN keeps five HDMA
channels on all game (`HDMAEN = $7A`): 40 clocks a line, about 3% of the CPU, so BrokenNes finished frames that overrun vblank in Mesen.
Seen as: at the title → game CHR switch BrokenNes began the forced blank ~60 lines earlier and showed the screen one frame earlier, and in Mesen
the NMI landed 3-4 lines late on frames where BrokenNes's did not (found with `--snesrun --reg-log` against `mesen-snes-reglog.lua`).

Regression check: **`Workshop --snes-timing-test`** (`Workshop/SnesTimingTest.cs`). Probe ROMs of our own count the CPU time left during the active
display, with values measured on Mesen (`Workshop/mesen/snes-hdma-timing.lua`; `--write-roms DIR` writes the ROMs to re-measure). It **fails on
the unfixed core** (1 channel +18 iterations, 5 channels +78.8, 5 repeating +77) and passes after (+2 / −1 / −3: at most ~1.5 clocks a line,
the size of Mesen's DMA clock alignment). The loop bodies alone agree to 0.1 iteration, so the test measures HDMA, not the CPU.

Effect against Mesen (ROMs and scripts of section 3 and of BrokenDev's manifests):

| ROM | before | after |
|---|---|---|
| Nested VRUN (2026-10-18 build): transition, menu route, 3-min play | transition differs (forced blank and screen-on timing), runs diverge after the game starts | **identical everywhere** |
| Nested VRUN (BrokenDev's 10-16 build, UAT case ROM), BrokenDev oracle | worst 32 px | **0 px at every checkpoint** |
| SMB1 Nested (UAT ROM), walking scene 710-1000 | never closer than 73-175 px | 0-24 px, with one ~7-frame step at frame ~840 (a runtime recompile stall of another length; this build has no entry-point feedback) |
| Nested VRUN enhanced (2026-10-18 build), play script | 0 px except 9 at a title (shift 4) | 0 px except 9-39 px (shift 2; section 3) |
| Nested VRUN enhanced (UAT ROM, no feedback), BrokenDev oracle | ≤ 53 px at shift 4 | no shift reaches the same state (best: same map, 581 → 4,148 px). **Worse.** Section 5. |
| Star Fox / Yoshi's Island (UAT state cases) | Star Fox f600 14 px, Yoshi f900 197 px | 0 px and 66 px; their later frames are chaotic in both builds |
| Super VRUN | unchanged | unchanged (it has frame time to spare) |

Goldens re-baselined by hand (values only, files otherwise untouched), each moved by the fix alone (the baseline Workshop passes all ten):
`brokendev-vrun-nested-{boot,play}`, `brokendev-vrun-nested-enhanced-{boot,play}`, `brokendev-smb1-nested-{boot,play}` (`UAT/cases/brokendev.json`),
`state-starfox-{plain,rewind}`, `state-yoshi-{plain,rewind}` (`UAT/cases/states.json`; each rewind case still equals its plain case).

Suites on the final tree: UAT `run-suite.ps1 -Exe <fresh build>` **84 pass, 0 fail, 60 skip** (the 60 are ROMs absent here); `--snes-timing-test`
PASS (2 known); `--snes-state-test` 147/147; `--state-audit` 205 supported / 23 partial / 48 refused / **0 broken**; `--corrupt-test` 275/0/0;
65C816 `cputest-full` Success, 0 failures; BrokenDev `tests\run-tests.ps1` 58/0/0; FL plugin selftest (`certify -Only plugin-host`, the Native AOT
DLL with this change): 61 pass, the known QLQ warning, and `fm` under its 5× real-time floor (4.7×) while the machine was busy; `fm` run alone
afterwards passed twice (the Master System FM chip does not use the SNES board; main's own capture round recorded the same floor on this machine).

Also on the branch: `diff-nes.ps1` / `diff-snes.ps1` take `-MesenTimeoutSec` (Mesen's own `--timeout=`; without it Mesen's test runner stops a
script after ~100 s, about 5,800 traced VRUN frames), and `BrokenDev\tools\vrun\` (section 7).

## 5. Known issues, not fixed

BrokenNes (measured, left for the SNES core owners; both are printed as KNOWN by `--snes-timing-test`, not passed):
* **Emulation-mode read-modify-write**: in 6502 emulation mode Mesen writes the old value back during the modify cycle (8 clocks to WRAM, a
  second write to an I/O register); `CPU_SFC.ModifyMemory` uses an internal cycle (6). Fixing it was measured exact, but it changes nothing in
  the three games (they run native code), so it is not on the branch.
* **General-purpose DMA clock alignment**: ~8.75 clocks per transfer (start on the DMA unit's 8-clock grid, then back to a CPU cycle).
  Measured exact when added, but it moved six more goldens (Super VRUN, Super Mario World, Kirby Super Star) without changing any VRUN picture,
  so it is not on the branch either.
* **Nested enhanced: one lag frame at the title start** differs from Mesen (both before and after the fix). The enhanced NMI uploads up to
  2 KB of shading planes per frame; DMA, HDMA and the CPU now agree with Mesen in isolation, so the cause is something else in that NMI path.
  Its effect: the same pad script picks another map in the two emulators. For BrokenDev's old (no-feedback) enhanced ROM the oracle no longer
  passes after the fix.
* SMB1 Nested (old build): one ~7-frame difference in a runtime recompile stall (not chased: SMB1 is out of scope).
* `BrokenDev\tools\diff-nes.ps1`'s APU gate fails long runs on its blip budget although the write streams are identical: compare write
  streams instead (`BrokenDev\tools\vrun\apu-writes-compare.py`, section 7).

The games (reported, not changed):
* **Nested VRUN enhanced: a disabled CONTINUE is not greyed**: with no save, CONTINUE is drawn in the same gradient font as the enabled items
  (plain Nested greys it). The enhancement's font pass (`snes_enhance.py`) loses the grey; Mesen shows the same.
* **Super VRUN: CONTINUE costs one lag frame** (`LAG 1` on the HUD after CONTINUE, on Mesen too). The project's rule is LAG 0.
* VRUN and Super VRUN: Select while paused is **save and quit** (the only way to save), as the code says. (Older notes say "Select saves".)
* Nested pipeline preflight warnings (unchanged since 09-27): 12 unofficial-opcode sites (`Cpu.IllegalNop=True`), 1 read-modify-write on
  $8000-$FFFF whose write is dropped (plan P2.7), 9 PRG banks not statically reachable.
* VRUN's known ones stand: `start_at_option_check` fails (START AT spawn bug, ROM budget); NESFab output is not byte-reproducible (two builds of
  the same source differ; freeze what you test).

## 6. Environment notes (this machine)

* **Claude-launched processes write `%LOCALAPPDATA%` / `%APPDATA%` into the Claude app's package store** (`AppData\Local\Packages\Claude_pzs8sxrjxfjjc\LocalCache\...`): everything an agent puts under `%LOCALAPPDATA%\VRUN_Nes_Dev_work` may be invisible to your own Explorer and is lost if that app is reset. Frozen ROMs therefore went to the OneDrive `builds\` folder.
* **Live VRUN checkout: 54 tracked files are CRLF in the working tree but LF in git**, hidden because git trusts its stat cache (WORKSPACE.md says the files are LF). List: `git ls-files --eol | findstr "i/lf.*w/crlf"`. They are docs, test Lua, `.mlb`s, room JSON, `framingtest/hello` `main.fab`, `tools/nes-mapbake.mjs`, two `.py` tools. The build still passes; a future touch of the index will suddenly show 54 modified files.
* The Mesen tracers the differentials need (`state_trace.lua`, `apu_write_log.lua`, `ppu2007_pair.lua`, …) exist **only in the older `PROJECTS\sys0\simulations\VRUN\tools`** clone, not in the VRUN repository.
* Mesen's `--testRunner` stops a script after ~100 s: pass `--timeout=<s>` (the BrokenDev tools now can).
* `Nested_VRUN\...\m30pipe\out\core\manifest.json` points at the old machine (`C:\Users\user\Desktop\...`), so every build prints "core is stale, building with the OLD core". Harmless (the pinned md5 is checked and `build_core.py` reproduces it); `python core\build_core.py --out out\core` would clear it (your checkout: your call).
* **Super VRUN on P:**: git refuses the repository ("dubious ownership", the NAS owns the files) unless `safe.directory` is set; builds are ~3× slower than on C:.
* WMIC is gone on this Windows 11, so Super VRUN's `tools\kill_test_mesen.sh` kills nothing: it cannot hurt other agents, but it also cannot stop a stray test Mesen. Run one Mesen test at a time.
* Python: use `source nesdev-env.sh` (Python 3.14 first). `python3` in Git Bash is the Windows Store alias and hangs. Pillow warns that `Image.getdata` goes away in Pillow 14 (2027-10-15): VRUN's `chrbake.py` and sprite tools will need `get_flattened_data`.
* `UAT\diff-trace.ps1` needs PowerShell 7 (`pwsh`).

## 7. Exact commands

### Build

```bash
# Git Bash
source /c/Users/philt/OneDrive/Documents/PROJECTS/VRUN_Nes_Dev/nesdev-env.sh
cd "$VRUN_ROOT" && ./build.sh game            # -> build/vrun_game.nes + .mlb, "PASS - ROM matches the target cartridge spec."
./build.sh game --nested                       # -> build/vrun_game_nested.nes + .mlb
git status                                     # only your own edits; music_data.fab must not change
# freeze what you test: cp build/vrun_game.{nes,mlb} "$NESDEV_ROOT/builds/<date>_<what>/"
python "$NESDEV_ROOT/Nested_VRUN/Project-Nested/tools/m30pipe/tools/flash_sectors.py" build/vrun_game_nested.nes   # sectors for the profile
```

```powershell
# Nested (Windows PowerShell 5.1; new build = new PRG CRC32 = new profile games\<crc>\ derived from the last vrun profile)
cd C:\Users\philt\OneDrive\Documents\PROJECTS\VRUN_Nes_Dev\Nested_VRUN\Project-Nested\tools\m30pipe
powershell -NoProfile -ExecutionPolicy Bypass -File bootstrap\bootstrap.ps1 -Nes <frozen vrun_game_nested.nes> -Work <scratch>\boot -Out <scratch>\vrun_game_nested_SNES.smc
python tools\snes_enhance.py <scratch>\vrun_game_nested_SNES.smc <scratch>\vrun_game_nested_SNES_enhanced.smc --chr out\font\chr32k.bin --full-bevel-from 64 --anim-png <VRUN>\src\game\anim_*.png
```
(This round passed `-Profile`, `-MesenDir` and `-OutRoot` to keep everything in scratch; in your own checkout the defaults are right.)

```bash
# Super VRUN (Git Bash, from snes-dev/supervrun)
bash build.sh                                  # srccheck, ..., "ramcheck: ... OK", "Build finished"; md5 must equal the commit's
./build.sh --test                              # Mesen self-test (one Mesen test at a time)
```

### Test on BrokenNes

```powershell
# a Workshop of your own (never the repo's stale bin\)
dotnet build Workshop\BrokenNes.Workshop.csproj -c Release --artifacts-path $env:LOCALAPPDATA\VRUN_Nes_Dev_work\bn_vrundev\art
$ws = "$env:LOCALAPPDATA\VRUN_Nes_Dev_work\bn_vrundev\art\bin\BrokenNes.Workshop\release\BrokenNes.Workshop.exe"
# Workshop is a GUI-subsystem exe: from PowerShell use Start-Process -Wait -NoNewWindow -RedirectStandardOutput, or call it from Git Bash

# 1. recipes: boot / play / input / colours / audio / speed (ids: vrun-nes, vrun-nested, vrun-nested-enhanced, supervrun)
$env:BROKENNES_DEV = '1'
& $ws --brokendev verify vrun-nes --manifests BrokenDev\projects --home <scratch> --rom <builds\...\vrun_game.nes> --png-dir <dir> --out <dir>\verify.json

# 2. long scripted play with pictures and sound
python BrokenDev\tools\vrun\playscript.py nes 10800 > play.txt          # profiles: nes | nested | svrun  (--seed, --save-every, --play-only)
& $ws --mixlab nes --rom <rom> --frames 10800 --png-at 300,900,... --input (Get-Content play.txt) --out-dir <dir> --wav <dir>\play.wav
& $ws --console-run --rom <smc> --frames 10800 --png-at ... --input ... --out <dir> --wav <dir>\play.wav --battery-out <dir>\save.srm

# 3. differential against Mesen
pwsh -File BrokenDev\tools\diff-nes.ps1 -Rom <rom> -Work <dir> -MesenDir <VRUN>\tools\mesen -Workshop $ws `
     -TraceLua C:\Users\philt\OneDrive\Documents\PROJECTS\sys0\simulations\VRUN\tools\state_trace.lua -InputScript (Get-Content play.txt) -Frames 10800 -MesenTimeoutSec 900
python BrokenDev\tools\vrun\apu_shift.py <dir>\diff.mesen.trace.txt <dir>\diff.brokennes.trace.txt      # classify APU blips
pwsh -File BrokenDev\tools\diff-snes.ps1 -Rom <smc> -Work <dir> -MesenDir P:\BrokenDev\env\supervrun_dev\SuperVRUN\tools\Mesen2 `
     -MesenSettings P:\BrokenDev\env\supervrun_dev\mesen2_user_folder\settings.json -Workshop $ws -Python <python> `
     -Frames 600,1200,... -InputScript ... -CropTop 7 -CropBottom 8 -InputShift <n> -MesenTimeoutSec 600
#   input shifts measured on the fixed build: Nested VRUN (2026-10-18) 1, its enhanced build 2, Super VRUN 2

# 4. exact audio check (NES): the APU write streams
#   Mesen: Mesen.exe <rom> --luaScriptsToLoad <sys0 VRUN>\tools\apu_write_log.lua --testRunner --timeout=900   (env VRUN_WLOG_OUT/FRAMES/INPUT/RAMFILL=fceux)
& $ws --apu-audio --rom <rom> --cpu FIX --ppu FIX --apu FIX --frames 10800 --input ... --ntsc-frame-timing on --write-log bn.wlog.txt
python BrokenDev\tools\vrun\apu-writes-compare.py mesen.wlog.txt bn.wlog.txt

# 5. saves
pwsh -File BrokenDev\tools\vrun\vrun-save-check.ps1 -Exe <BrokenNes.Windows.exe> -Rom <vrun_game.nes> -ConfigDir <scratch>    # NES, the real app
& $ws --console-run --rom <smc> --frames 900 --battery <save.srm> --input "300:Down,306:,340:Start,346:" --png-at 500,900 --out <dir>   # SNES CONTINUE

# 6. BrokenNes's own SNES timing check
& $ws --snes-timing-test
```

`BrokenDev\tools\vrun\`: `playscript.py` (deterministic long-play scripts), `remap.py` (shift/remap a script, e.g. NES → Nested `A=B,B=Y`),
`apu_shift.py`, `apu-writes-compare.py`, `mesen-snes-reglog.lua` (register-write timing, counterpart of `--snesrun --reg-log`),
`mesen-snes-wram-log.lua` (a work-RAM byte per frame, counterpart of `--snesrun --dump-wram`), `vrun-save-check.ps1`.

Mesen traps met again: never reuse VRUN's Mesen for SNES (input port 1 is None there); clear a private Mesen's `Saves\` before a run; VRUN
picks its map from the frame Start is pressed, so a one-frame input difference means another map (calibrate `-InputShift` on the title).

## 8. Decisions for the user

1. **VRUN CTW**: commit the uncommitted `src/game/main.fab` (the `tear_active` initialisation, 2026-10-01) in the live repo? It builds (PASS)
   and was the build tested here. Nothing else is uncommitted.
2. **Live VRUN checkout line endings**: restore the 54 CRLF files to the committed LF (`git checkout -- <those files>`, naming them; `main.fab`
   is not among them, so your edit is safe), or leave them.
3. **Nested**: the live Project-Nested is the one to develop VRUN in. To bring the copies together without losing anything: copy the archive's
   SMB1 additions into it (`games\d257beb1\`, `tools\dmc_status.py`, the "post-build steps" section of `core\STEPS.md`, `out\ship\SMB1_Repro*`),
   add `games\79b64e20\` from `builds\2026-10-18_nested\profile_79b64e20\` if you keep the 2026-10-18 build, then **commit the long-uncommitted
   Nested work** (the package's START_HERE asks for this first). Optionally take the archive's newer memory note `nested\snes_qol_work.md`.
4. **Super VRUN**: the only copy is the BrokenDev restore on the P: share. Pick a local home (for example `C:\Users\philt\Desktop\SuperVRUN`, the
   original layout): a `git clone -b two-player` plus the untracked `snes-stub\toolchain` and `tools\Mesen2` reproduce the ROM byte for byte
   (shown here), or extract `SuperVRUN_dev.zip` there in full. Restore its Claude memory from the zip (`claude\projects\...` was not restored)
   and reconnect its live board (`https://claude.ai/artifact/N1BpPY1PCz8i4akUr5C4yh`).
5. **BrokenNes**: merge `vrun-resume` (the HDMA fix, its test, the 10 goldens, the tools, this page): the lead's call. The two known SNES timing
   differences (emulation-mode RMW, DMA alignment) are ready to fix but move more goldens; and the enhanced build's title lag frame is open.
6. Clear the "stale core" warning (`build_core.py --out out\core`) or live with it.
7. Add a pointer to this page in `VRUN_Nes_Dev\WORKSPACE.md` (not done: it is your file).
8. Unchanged from before: NTSC-accurate frame timing is still off by default in the app (VRUN's frame budget is only faithful with it on).
