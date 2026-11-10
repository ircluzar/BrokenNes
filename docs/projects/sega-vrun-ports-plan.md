# VRUN on Sega: three ports (next week, from 2026-11-02)

Status: S0 built (2026-11-02, see "S0 as built" at the end); S1-S3 next.

| Port | Original | Sega console | Screen |
|---|---|---|---|
| **Vaults of Obsolete Code** | `VRUN_VOC_dev` (Game Boy, tick-identical to Eternal Degrade) | **Game Gear** | 160x144 = the Game Boy's: the same view |
| **Corrupt The World demake** | VRUN NES (`VRUN_Nes_Dev`, NESFab) | **Master System** | 256x192 vs the NES's 256x240: the original's view kept, the HUD / borders rearranged |
| **Super VRUN** | `SuperVRUN_dev` (SNES, with 2P) | **Genesis / Mega Drive** | 320x224 vs the SNES's 256x224: the original's view kept, the extra width as borders / HUD |

## Decisions (interview, 2026-10-30)

1. **Fidelity: faithful feel, adapted.** The same game, free to adapt physics and timing where the Sega hardware makes that natural
   (not tick-identical like Vaults on the Game Boy).
2. **Screens: keep each original's view;** the Genesis's extra width and the Master System's missing height are handled with borders
   or by moving the HUD.
3. **Toolchains (downloads approved):** devkitSMS + SDCC (Master System, Game Gear), SGDK (Genesis), and assembly where needed
   (WLA-DX, already pinned for the SNES). Pinned with sha256 in BrokenDev's toolchains manifest like GBDK and cc65.
4. **Hardware:** the user's Genesis flash cart, which runs all three consoles' games (the Genesis natively, Master System and Game
   Gear through its backwards compatibility).
5. **The cross-console comparison checks game logic with tolerances:** the same inputs on the original and on the port; rooms visited,
   items, deaths, clears and key positions must match within tolerances set per game; where they drift it is flagged, and the user
   decides whether it is an intended adaptation.
6. **Order:** Vaults on the Game Gear first (the same screen as the Game Boy, the Z80 family of the Master System), then the
   Corrupt The World demake on the Master System (sharing devkitSMS), then Super VRUN on the Genesis (the biggest).
7. **Sound: converted from each original.** Master System / Game Gear PSG (three squares + noise) from the NES / Game Boy versions;
   Genesis FM + PSG from the SNES version's music; effects re-fitted like Vaults' Fourier pass.
8. **Super VRUN keeps 2P** on the Genesis (two pad ports).

## The first tool: the cross-console comparison (replaces Timeflow T6)

Timeflow T6 (a self-filling gameplay corpus) is replaced by what these ports need; T7 (proof on a real bug) is these ports themselves.
- **Record on the original** (Timeflow T1: Game Boy, NES, SNES), **replay the same inputs on the port** (the Sega consoles have
  Timeflow and the debugger since T0).
- **Map the two games' variables** by name (each project's symbols) and the two tick rates (e.g. Vaults' 24 Hz ticks on both; the
  NES's 60 Hz vs the Master System's 60 Hz; SNES vs Genesis frames), then compare per game-logic event: room changes, items, deaths,
  clears, key positions, within the per-game tolerances (a manifest section).
- **Report the first drift** with both sides' state, and open both sides at that point with T3 profiling.
- **Re-check after rebuilds** (the one part of T6 kept): when a port is rebuilt, its recorded sessions replay and the first frame the
  change altered is reported.

## Milestones

| Milestone | Delivers |
|---|---|
| **S0** | Toolchains pinned (devkitSMS + SDCC, SGDK), BrokenDev projects for the three ports (build, verify, compat on BrokenNes + other emulators, Lite), the cross-console comparison tool with the re-check. |
| **S1** | **Vaults on the Game Gear**: the game, its palettes, the PSG sound converted from the Game Boy version, comparison within tolerances, the flash-cart test. |
| **S2** | **Corrupt The World demake on the Master System**: the view kept in 256x192, the game, PSG sound from the NES version, comparison, flash cart. |
| **S3** | **Super VRUN on the Genesis**: 1P and 2P, FM + PSG music from the SNES version, comparison, flash cart. |

## Decisions, second interview (2026-11-01)

| Topic | Decision |
|---|---|
| S1 Vaults on the Game Gear: physics | **Tick-identical to Eternal Degrade**, like the Game Boy version (this replaces "faithful feel" for S1): the same physics code (`src/phys`, exact doubles, the RNG table) built with SDCC for the Z80, and the same compare checks. |
| S1 colours | **Like the Game Boy Color version** (the same level colours). |
| S2 CTW on the Master System: 192 lines vs 240 | **Shrink the HUD**, the way Super VRUN had to; the play area stays whole. |
| S3 Super VRUN on the Genesis: the width | **Like the WideMess version (P5):** the game centred in 320, each room's edges repeated to the sides. |
| S3 staying in step | **One shared game-logic source** for Super VRUN's SNES, WideMess and Genesis versions; each console keeps its own picture / sound layer and its own speed paths (the SNES's 65816 assembly stays SNES-only; the Genesis gets 68000 equivalents where it needs them). Super VRUN's maintained versions are SNES, WideMess and Genesis; P6's back-ports reach all three. |
| S3 music | **Port Super VRUN's own music engine** to the Genesis (FM + PSG), not SGDK's XGM driver. |
| The comparison's tolerance | **Same events, small drift:** rooms, pickups, deaths and clears in the same order; positions within a few pixels. (S1 is stricter: tick-identical.) |
| Order | S0, then Game Gear, Master System, Genesis, as planned. |
| S2's source (2026-11-04) | **A C rewrite of the NESFab mapper-30 version** (its CHR-RAM upload model is the Master System's model). The NES game's MMC3 build becomes the main NES version for NES-only graphics (bank-switched animated tiles, the title tunnel, scanline splits), from the same NESFab source behind the mapper switch. |

Open questions 2-4 below are answered by this table (the Master System's rows: the HUD shrinks; the Genesis sound: our own engine;
the Game Gear: the Color's colours).

## Open questions (to settle per port, in their own interviews)

1. Per game: which adaptations are wanted (e.g. a physics rate on the Master System), and the comparison's tolerances.
2. The Master System's 192 lines vs the NES's 240: which rows of the NES view the HUD gives up, or a scrolling view.
3. The Genesis's sound driver (SGDK's XGM, or a port of the music engine).
4. Whether the Game Gear version uses the Game Gear's colours (4096) or stays close to the Game Boy Color's look.

## S0 as built (2026-11-02)

### The toolchains (BrokenDev/toolchains.json, under `BROKENDEV_HOME\toolchains`, zips kept in each `_downloads`)

| Id | Version | Source (downloaded with the user's approval of 2026-10-30) | sha256 of the download |
|---|---|---|---|
| `sdcc` | 4.6.0 (#16555) | sourceforge.net/projects/sdcc, `sdcc-win64/4.6.0/sdcc-4.6.0-x64-setup.exe` (9,949,266 bytes; the NSIS installer unpacked with 7-Zip, never run) | `0a165e155a052fcf7c29ea703ee77d5a8eb578eba58279e79e885618dc4b2e1a` |
| `devkitsms` | commit 533ae57 (2026-09-29) | github.com/sverx/devkitSMS, the commit's archive (7,889,171 bytes); its last tagged release (v1.2) is from 2022 | `58d9a9693b7e31193e9ef771c70022342ebc4522a7b23a501556b3b45bd8c0f0` |
| `sgdk` | 2.11 (April 2025) | github.com/Stephane-D/SGDK releases, `sgdk211.7z` (54,060,415 bytes, the size GitHub lists) | `5cc704b7e3a15183c33e721a1d7f84c067cf78808754556d03bb14764df51437` |
| `wladx` | 10.7 (2026-06-28) | github.com/vhelin/wla-dx releases, `wla_dx_v10.7_Win64.zip` (3,549,451 bytes; its vc_redist installers not taken: the zip has vcruntime140.dll) | `82922a530ae989bd5f8057fa4cfef3d0022d7353b5aeb7858b9320eae9b56ebd` |

Key files are pinned by size and sha256 (`brokendev.ps1 toolchains`: all six ready). Nothing else was downloaded. **Found:** SDCC 4.6.0's linker
refuses devkitSMS's shipped `SMSlib*.lib` / `crt0_sms.rel` ("Conflicting sdcc options": built by an older SDCC that records `-mz80` where 4.6
records `-mz80 sdcccall(1)`), so the ports' `build.ps1` rebuild SMSlib and crt0 from the pinned commit's own sources with its Makefile's rules,
once, into `build\smslib-<target>` (about 110 s; a stamp of the compiler's and sources' hashes). SGDK's makefile runs Java for `sizebnd.jar`
(the machine's JDK 11) and prints `Error 127 (ignored)` from its LTO step on Windows (the ROM is complete).

### The skeletons (local git repositories, never pushed)

| Repository | ROM | What it is |
|---|---|---|
| `PROJECTS\VRUN_VOC_GG_dev` | `build\vrun_voc_gg.gg` (32 KB, region 7) | SMSlib_GG hello: text in the 160x144 window, a frame counter, a cursor Left / Right move |
| `PROJECTS\VRUN_CTW_SMS_dev` | `build\vrun_ctw_sms.sms` (32 KB, export region 4) | the same on the Master System, 256x192 |
| `PROJECTS\SuperVRUN_MD_dev` | `build\supervrun_md.bin` + `supervrun_md.nm.txt` (128 KB) | SGDK hello: the text in the SNES's 256 centred in 320, both pads read, a cursor; `src/boot/rom_head.c` with the game's names, no SRAM |

The SDSC header's date is fixed so the same sources give the same ROM. Each boots on BrokenNes's console and passes BrokenDev build + verify.
**S3's shared game logic:** the proposal is in `SuperVRUN_MD_dev\README.md` (`logic/` inside `SuperVRUN_dev\snes-dev\supervrun`, used by the
Genesis repository as a subtree or a `-Logic` path; `platform/snes|wms|md` for picture, sound, pads, timing, saves; a `logic/platform.h` contract
enforced by a lint; fixed-width types and the SNES's fixed-point formats kept; C references beside the 65816 speed paths, 68000 ones only where
the profiler says). `SuperVRUN_dev` was not changed.

### BrokenDev's Sega consoles and the three projects

- Manifests take kind = console `sms`, `gg`, `md`. `verify` runs them on BrokenNes's Master System, Game Gear and Genesis (the menus' default
  cores); `expect.memory` reads work RAM by symbol (`"endian": "big"` for the 68000). `compat`: reads in `wram` by symbol (`.noi`, SGDK's
  `nm` listing, `symbol+N`), the Sega pads' buttons (sms/gg A B Start Select(Pause); md A B C X Y Z Start Mode).
- Emulators: Mesen 2.1.1 now runs `sms` and `gg` (`smsWorkRam`, an SmsController in port 1); BizHawk columns `bizhawk-gpgx-sms` (Genplus-gx:
  SMSHawk stops on "Missing Firmware! ... SMS+Export" for an export cartridge, a modal dialog found by listing the hung EmuHawk's windows),
  `bizhawk-smshawk-gg`, `bizhawk-gpgx` (the Genesis project publishes `.md`: an unknown `.bin` opens EmuHawk's "Choose a Platform" dialog).
- `vrun-voc-gg`, `vrun-ctw-sms`, `supervrun-md`: build, verify (`hello`: running, drawn, the pad moved the cursor to the expected column) and
  compat **works on every column** (Game Gear: BrokenNes, Mesen, SMSHawk; Master System: BrokenNes, Mesen, Genplus-gx; Genesis: BrokenNes,
  Genplus-gx). Their `comparison` sections name the originals (`vrun-voc`, `vrun-nes`, `supervrun`); the variables come with S1-S3.
- Lite: see "the runs" below.

### The cross-console comparison (replaces T6)

- **Engine:** `Workshop/Timeflow/TimeflowCompare.cs`. The original is a Timeflow power-on timeline (or recorded now from a ROM and a pad script),
  replayed on a new machine and checked frame by frame against its recording. Its pads go to the port's ROM from power-on (by time when the
  frame rates differ, through the mapping's `buttons`), recorded as the port's timeline (the port's symbols kept beside its ROM). Both games'
  variables are read every frame by the debugger's expressions on each side's own symbols.
- **The mapping** (a manifest's `comparison` section, or a file of that shape):
  ```json
  { "original": "vrun-voc",
    "rate": { "original": { "tick": "le16 _tick_count" }, "port": { "tickHz": 24, "origin": "mode=1" } },
    "tolerance": { "ticks": 1, "positions": 2 }, "sampleEveryTicks": 24, "input": "auto", "buttons": { "Select": "-" },
    "variables": [ { "name": "room", "kind": "event", "original": "le16 _voc + 448", "port": "le16 _room" },
                   { "name": "x", "kind": "position", "original": "[_voc + 459]", "port": "x_px", "scale": { "port": 0.125 }, "tolerance": 1 } ] }
  ```
  An expression is a bare symbol (its byte), `le16 x` / `be16 x` (a word), or any debugger expression. A side's time is its game's own tick
  counter (`tick`) or its frames at `tickHz` (its fps by default, or `fps`), from an `origin` event (the two clocks start at different moments).
  `kind event`: every change is an event; events must come in the same order with the same (scaled, offset) values, each within `ticks`.
  `kind position`: compared at every event and every `sampleEveryTicks`, within its tolerance.
- **The report:** no drift (the events matched), or the FIRST drift (`missing`, `extra`, `different-event`, `time`, `position`) with both sides'
  frame, tick and every mapped variable, and **both sides opened there**: a T3 profile of the `window` (30) frames before the drift on each side's
  timeline, watching the drifting variable's address range (`writes $D693-$D694`: who wrote it, from which function), with the resume command.
- **The re-check after a rebuild:** every recorded power-on session of the port (its project's timelines) replayed on the new build; unchanged,
  or the first frame whose picture or work RAM differs from the recording, with the mapped variables on the recorded build and the new one.
- **Interfaces:** Workshop `--timeflow compare (--original T | --original-rom R --input S --frames N) --port R --port-sym F (--map F | --manifest M)
  [--no-open] [--window N] [--json]`, `--timeflow recheck (--timeline T | --project P) --rom NEW --sym-new F [--map | --manifest]`;
  `brokendev.ps1 timeflow compare <port project> [-Original <timeline>] [-Scenario id | -Script S -Frames N] [-NoOpen]`, `timeflow recheck
  <port project> [-NoBuild]`; MCP `timeflow_compare` (project, or raw original / port / map), `timeflow_recheck`.

### The tests, without the ports (BrokenDev/tests/xcompare.ps1: 14 of 14)

The games were only read: Vaults' sources copied and built in scratch (the copy's ROM is byte-identical to the user's build), VRUN NES and Super
VRUN from BrokenDev's published builds. A 6000-frame Vaults session (A on the title, a seeded wandering player): 31 events (the run's start, 29
room changes, a life from 16 materials).

| Check | Result |
|---|---|
| Vaults against itself | no drift, 31 events matched, positions every 24 ticks |
| against an altered build (15 materials make a life: `voc_phys.c`) | **drift, kind time, at event #25 (lives -> 1), the first lives event, after 25 matches**: tick 1842 vs 1840; both sides profiled (the port's 2 writes to `lives`) |
| tick tolerance edges | tolerance 2 ticks: no drift; 1: the drift at #25 |
| position tolerance edges (the port's x + 1 cell) | tolerance 1: no drift; 0: a position drift at event #0 |
| a mapped tick rate (the original by its 24 Hz tick counter, the port by its frames at 24 ticks a second, both from `mode=1`) | within 1 tick on all 31 events; a wrong rate (25 Hz) drifts at event #1 (5.66 ticks) |
| re-check: the unchanged rebuild | unchanged, all 6000 frames as recorded |
| re-check: the altered rebuild | first altered frame 4816 (the work RAM), before the first lives event (4821): `lives` 0 -> 1 |
| VRUN NES / Super VRUN against themselves | no drift (111 / 5 events) |
| `brokendev.ps1 timeflow compare` / `recheck` on scratch projects | the drift at the lives event with both sides opened; recheck unchanged, then (the source switched) changed at frame 4816, `lives 1 -> 0` |

The MCP client test adds compare (self: no drift; the T5 test game vs its fixed build: the missing hp event, both sides profiled) and recheck
(unchanged / changed at frame 15): 145 of 145.

### The runs

All projects build with 0 errors (Workshop, Windows, Web, WebLite, BrokenDev/mcp); `--timeflow-test` 330 of 330, `--debug-test` 205 of 205,
`--movie-test` 69 of 69; BrokenDev `run-tests` 153 passed, 1 failed: "the repository stays light", caused by this session's own MCP build in
`BrokenDev\mcp\bin` (removed; the check then passes); MCP client test 145 of 145; `xcompare.ps1` 14 of 14. **Lite:** `UAT/entrypoints/lite-sega.ps1
-Only rom` with the three skeleton ROMs in place of its games (an AOT publish of 2026-10-23: no Lite code changed since): 18 of 18 (each loads by
`?rom=`, 256x192 / 160x144 / 320x224, moves, sound every frame, 60 fps). No UAT case was touched. Not done: a flash-cart test (S1-S3).

## S1 as built (2026-11-03; full speed, open: Mesen's A, map 2's late pictures)

Port repo VRUN_VOC_GG_dev (local git): 6b52b02 physics, 0c354ac the game + PSG sound, 52f71e8 speed and start timing.

- **The game** builds (build.ps1 + src/units.txt) and plays: title, runs, pause ("PAUSED   Start+1: save" at the bottom, Start+1 saves
  again), battery-RAM saves, GBC colours, mono PSG sound (tools/psg/convert_sfx.py: a frame model of the Game Boy sound chip ->
  PSG writes; tools/psg/convert_music.py + voc_sound.c: the Game Boy song bytes interpreted for the PSG, no envelopes/vibrato/panning).
- **Fixes found on the way:** the Z80 LD A,I bug in gg_crit_enter (interrupts left off, the frame interrupt stopped for many frames);
  SDCC's make_tile at 9-20 K cycles a tile (now Z80 rows); the run's 256 pair tiles baked (tools/gfx/make_pair_tiles.py, bank 61).
- **Start timing:** the first title keeps the GBC's 24 Hz page phase (VOC_GG_TITLE_ANCHOR) and the box follows the GBC's frame
  schedule (VOC_GG_RUN_AT 52 frames after the run is asked for, acc 1389): the run starts at the GBC's frame 239 for A at frame 60.
- **Comparison** (manifest vrun-voc-gg "comparison": _voc offsets on both sides, _tick_count origin mode=1, input by frame, ticks 0 /
  positions 0), against a GBC build with VOC_GAME_SEED=4321, VOC_GAME_MAP=7 (the release ROM's seed comes from DIV: not reproducible):
  the run-start event matches on the same tick; the first drift is at tick 72 (x 25 vs 24), because the port's ticks run late.
- **Speed (was the blocker, fixed 2026-11-03, commits 8a5d613..682d469).** The physics (portable C) cost 118,411 Z80 cycles a tick on
  average, 621,984 at worst, of 148,668 at 24 Hz, and the game ran at about half speed. Now (tests/physics/compare_gg.ps1, the harness
  with the host check kept): 58,743 average, 552,672 worst (a room change; the 5-frame latency covers it). What did it:
  - the exact doubles (add / sub / cmp / sgn): the Game Boy's SM83 assembly translated mechanically (tools/sm83_to_z80.py ->
    src/phys/voc_dbl_z80.c: (hl+) forms, SWAP as 4 RRCA, the Z80 sdcccall(1) entries), voc_dbl.c's __SDCC branch: 118K -> 87K;
  - a profile (harness: -Define VOC_GG_PROF, voc_phys.c's TP points into h_tp_acc; game: -Define VOC_GG_GPROF, gg_tp_acc): the big
    cost was the host build's per-tick check that scans the raster for the '@' (find_char): CPIR in the harness, left out of the game
    build (VOC_GG_GAME, as the Game Boy leaves it out): 84K -> 59K;
  - glitter and wall shimmer in Z80 (src/phys/voc_gg_z80.c, voc_phys.c's VOC_GG_Z80 hooks: the only edits to the copied physics);
  - the light picture's key capture in Z80 (voc_screen.c cap_light): the game reached the clock's rate (305 ticks in 758 frames).
  Each step: compare_gg.ps1 0 differ on all 91,275 ticks (8 groups) and the -Perturb control caught (1002 lines).
  GG_MAX_BEHIND (the clock waits when 6 ticks behind) is KEPT as a safety: at full speed it no longer triggers in the sweep.
- **Strict comparison passes** (Workshop --timeflow compare with the vrun-voc-gg manifest, positions/ticks tolerance 0, both sides
  fixed-seed builds VOC_GAME_SEED=4321 VOC_GAME_MAP=7; scratch s1_scratch\cmp.sh): no drift over 2000 frames (the manifest's input) and
  over 2400 frames of a busier 26-press input. The run start re-measured at full speed: VOC_GG_RUN_AT 49 (48 and 50-52 drift).
  The release's seed: page_rand() ^ (R register ^ V counter ^ frame count) << 8 at the run's start (gg_entropy), the GBC's is DIV:
  neither is reproducible, so the comparison always uses the fixed-seed builds.
- **Sweep:** maps 0, 2, 5, 7 (fixed seeds, 2400 frames, 20 presses): 953 ticks each (the clock's 952), 0 late on maps 0/5/7, 10
  single-frame late pictures on map 2 (cause not found). The diagnostic counters (gg_marks, gg_title_at, gg_isr_*) only with
  -Define VOC_GG_DIAG.
- **Compat** (brokendev.ps1 compat vrun-voc-gg, BROKENDEV_WORKSHOP = a current Workshop, the shared one lacks gg in its compat spec):
  BrokenNes works, BizHawk SMSHawk-gg works, Mesen FAILS: the title takes Right but never A (the run never starts): most likely the
  Mesen adapter's 'a' on its SmsController is not the Game Gear's button 1 (the S0 hello only checked the d-pad). Not fixed.
- **Hand-off:** C:\BrokenNes-work\gg_handoffrun_voc_gg_682d469.gg (sha256 865857fe...c320) + README.txt.

## S2 scope (2026-11-05): Corrupt The World on the Master System

Port repo `VRUN_CTW_SMS_dev` (local git), full scope in its `docs/S2-scope.md`. Source: the NESFab mapper-30 build (main.fab ~5,600
lines of code, 168 room templates, 32 baked maps, 12 tracks, 22 SFX).
- **Method:** a translator (`tools/fab2c.py`) turns main.fab + data into C over an NES-shaped platform layer (name table writes, OAM,
  APU registers, pads, CHR bank, saves); only the layer is Master-System-specific (and gets the Z80 assembly). The logic then
  compares nearly frame for frame.
- **Picture:** VDP mode 4; the 32x28 name table holds the whole 256x224 room and the 192-line view follows the player vertically
  (0-32 px); the HUD row shrinks into a strip of sprites with a small font. NES tiles converted at upload (palette in the upper bit
  planes; sprites get a flip/palette tile cache); the corruption bank flip = a second name table switched by VDP register 2.
- **Sound:** the translated decoders write a virtual APU; a PSG driver converts per frame (squares -> tones 0/1, triangle -> tone 2
  an octave down, noise -> PSG noise).
- **Pad:** 2 = A (jump), 1 = B (fire), Pause = Start; Select = button 1 while paused ("1: SAVE & QUIT"); the in-game Start+Select
  scheme cycle is dropped (Options keeps it); pad 2 for the twin schemes. **Saves:** cart SRAM, the same bytes.
- **Risks:** Z80 speed (the NES frame is nearly full; asm hot paths expected), NESFab's 8-bit wrap vs C promotion, 32 px of the room
  off screen at a time (judge on the flash cart).

## D1: play-compare (Timeflow T6, 2026-11-09)

One command per port: `brokendev.ps1 timeflow play-compare <vrun-voc-gg | vrun-ctw-sms | supervrun-md> [-NoBuild] [-NoOpen]`
(lib/TimeflowPlay.ps1). The port manifest's `comparison.play` names a fixed-seed test build of each side (`kind ps1`: the build.ps1 with
-Out/-Define; `kind archive`: a git archive of a commit, junctions for what its build reads outside the repository, then a bash command),
built into `C:\BrokenNes-work\play-compare\<port>` (BROKENNES_WORK; never the projects' build folders: the shipping ROMs are untouched; on C:
because T: takes no junctions), the pad script (`tools/playscript.py`: a scripted prefix + a seeded walker), and `expect` (event counts each
side must reach: the script really plays). Workshop's compare JSON now has `eventCounts` and `eventsPort`.

| Port | Original / seed | First result |
|---|---|---|
| vrun-voc-gg | VRUN_VOC_dev, both `VOC_GAME_SEED=4321 VOC_GAME_MAP=7` | plays (3 rooms); DRIFT: the first room change comes 1 tick earlier on the Game Gear (138 vs 139) and its x is already on the far edge (31 vs 0); every room change of every script tried does this; the old 2000-frame input never changed rooms <br>**FIXED 2026-11-10 (GREEN, positions every tick):** two things. (1) A sampling artifact: go_room sets voc.room before the room load and the move, the Game Gear's slower load let a frame end in between (room new, x 31, tick not yet counted) -> rate.<side>.settled (`[_game_mode] != 1 \|\| {_pre_done} != {_key_done}`): a frame ending inside an update keeps the last settled values; position samples only at ticks both sides showed (a side catching up runs 2 ticks in a frame). (2) A REAL port difference: GG_MAX_BEHIND 6 froze the port's clock after slow room loads, so frame-timed presses fell on later ticks (control build GG_MAX_BEHIND=6 drifts at tick 200, y 12 vs 11); now 14 (due_keys has 16 places). Also: seed builds run in a child PowerShell (sdcc stderr warnings killed the GG build under ErrorActionPreference Stop). |
| vrun-ctw-sms | VRUN NES at 439616b (git archive; HEAD e9e3a61's audio bends do not translate in fab2c), the port translated from the same tree (build.ps1 -FabRoot, VRUN_CTW_SMS 7c1dd9c); seed = Start at frame 120 | reaches room 1 on both; DRIFT: the NES player takes a hit in room 1 at tick 952 (hp 5 -> 4), the port never does (same position, x 220 y 192) |
| supervrun-md | SuperVRUN_dev at logic/SOURCE.txt's 45efdc6 (git archive, snes-stub toolchain joined); seed = Start at 300/420; mapping tools/compare_manifest.json | reaches room 1 (4 enemies) on both; DRIFT: position x 206 vs 216 (tolerance 4) in room 1 at tick 3440 |
