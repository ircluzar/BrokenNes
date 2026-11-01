# BrokenDev: testing the homebrew games on other emulators (compat)

The user's request (2026-10-22): "we need to integrate more emulators in BrokenDev, specifically for testing our homebrew games on other
emulators and improve compatibility ... mesen, bizhawk (neshawk) and fceux for nes; snes9x, bizhawk (bsnes) for snes. of course brokennes
is a first class citizen, but i really want the development to be able to, at some point in dev, to test homebrew games being developped
using those other emulators to ensure it works in them well".

This page is the record of that work: the emulators, how each one is driven, what each column of a result can and cannot check, and the
compatibility matrix of the six BrokenDev projects. The commands are in `BrokenDev/README.md` and `BrokenDev/docs/START_HERE.md` (3b).

## 1. The pieces

| File | What it is |
|---|---|
| `BrokenDev/emulators.json` | the registry: 4 packages pinned by release URL, size and sha256; 6 emulator ids (BrokenNes is the reference); per id the consoles, adapter, what its column can check, its defaults |
| `BrokenDev/lib/Emulators.ps1` | registry validation, `emulators` (list) and `emulators install` (verify or download the pinned zip, unpack portable) |
| `BrokenDev/lib/Compat.ps1` | the adapter contract (top of the file), the watched process, picture measures, symbols, checks, verdicts, `compat` and `play`, the contact sheet |
| `BrokenDev/lib/adapters/*.ps1` | one adapter per emulator family: `workshop` (BrokenNes), `mesen`, `bizhawk`, `fceux`, `snes9x` |
| `BrokenDev/tools/compat/*.lua` | the scripts run inside Mesen, BizHawk and FCEUX; `tools/compat/snes9x/` the Snes9x helpers |
| `Workshop/BrokenDevCli.cs` | `--brokendev run --spec` : the BrokenNes side of the same contract |
| `BrokenDev/projects/*/project.json` | each project's `compat` section: the bytes to read (by the game's own symbols) and the scenarios with their checks |

### The contract (every emulator, BrokenNes included)

BrokenDev writes a **spec** (ROM, frames, pad script, bytes to record every frame, frames to picture, the save folder); the adapter runs
the emulator and writes **`result.txt`**: one line per completed frame with the recorded bytes (`t <frame> <v> ...`), the pictures
(`shot <frame> <file>`), sound activity when the emulator can tell (`audio`), notes (the settings it ran with), and `end done <frame>`.
Without an `end` line BrokenDev decides from the process: `timeout` (the run's limit), `hung` (alive but the result file stopped growing),
`crashed` (it exited). Frames: input line `input f X` is held while frame f+1 runs; frame N = N frames completed since power-on, as the
emulator counts them. Emulators do not boot at the same moment, which is why checks have windows and a frame tolerance.

### Checks and verdicts

A check is one of: a recorded byte that **reaches** a value in a frame window (its first frame is recorded; with `compare` it is compared
with BrokenNes'), that **holds** a value over a window, that **changes** at least N times in a window (a heartbeat: the game is running,
not frozen), a **picture** with at least N colours, two pictures that **differ**, a picture **similar** to BrokenNes' (structure, palette
aside). `after` makes a window relative to an earlier check; `soft` turns a failure into a difference (for what may legitimately vary).
A memory check on an emulator that cannot read memory is `n/a`, never a pass.

| Verdict | When |
|---|---|
| **works** | every run reached its last frame and every check passed; compared states within the tolerance (30 frames) |
| **differs** | all required checks passed, but a state came more than the tolerance from BrokenNes', a picture is unlike BrokenNes', or a soft check failed |
| **fails** | a run crashed, hung or timed out, or a required check failed |
| **cannot be tested** | not installed, the console is not one it runs, the adapter cannot run here (e.g. a Workshop older than `run`), or nothing was checkable |

### Pictures

Every picture is measured the same way whatever made it: size, an FNV-1a hash of its RGB pixels, its number of colours. Emulators never
share a NES palette (nor always a picture size), so a picture is compared with BrokenNes' by **structure**: the share of pixels that agree
under the best colour-to-colour mapping, both ways (1.0 = the same picture up to palette). A 512-wide frame is halved, a taller one is
cropped equally top and bottom. The title screens score 0.99+ between emulators; gameplay of VRUN scores 0.55-0.65 because the first room
is another one (its map seed rotates every title frame, by design).

## 2. The emulators

All four packages are the official GitHub releases, downloaded with the user's permission on 2026-10-22 into
`BROKENDEV_HOME\emulators\_downloads` and installed by `brokendev.ps1 emulators install all` (which found them there, checked size and
sha256, and unpacked them; nothing was downloaded twice). The user's own older installs (BizHawk RTC/Vanguard forks, Snes9x 1.60, FCEUX in
`P:\TAS`, bsnes-plus, `Documents\Mesen2`) are never used or touched.

| Package | Version | Zip, bytes, sha256 | Where |
|---|---|---|---|
| Mesen 2 | 2.1.1 | `Mesen_2.1.1_Windows.zip`, 34,058,170, `23ccc2bc...7d5cbe` (GitHub digest) | `emulators\mesen-2.1.1` |
| BizHawk | 2.11.1 | `BizHawk-2.11.1-win-x64.zip`, 97,984,101, `dd7cbd5e...a5e5717` (GitHub digest) | `emulators\bizhawk-2.11.1` |
| FCEUX | 2.7.0 | `fceux-2.7.0-win64.zip`, 4,842,676, `f851d608...df1f9ff` (GitHub digest) | `emulators\fceux-2.7.0` |
| Snes9x | 1.63 | `snes9x-1.63-win32-x64.zip`, 5,328,695, `e2bd1038...a95db8` (measured; GitHub gives none) | `emulators\snes9x-1.63` |

BizHawk's prerequisites (Visual C++ 2015-2022 x64 runtime, .NET Framework 4.8) are present on this machine (VC++ 14.51, .NET 4.8.1).

### BrokenNes (`brokennes`, the reference)

`BrokenNes.Workshop.exe --brokendev run --spec <spec> [--ram fceux|zeros|ones] [--cpu --ppu --apu] [--ntsc]` (Workshop/BrokenDevCli.cs):
the verify runners (the same cores: the project's `verify.cores`, else the app defaults; the same power-on RAM its verify scenario names)
with memory peeks (NES system RAM, SNES work RAM), the battery save read from and written back to the run's save folder (`brokennes.sav`:
a NES mapper's flash blob, a SNES cartridge's SRAM), audio activity (a 30-frame window is active when a frame of it is not silent), the
pictures. A scratch `BROKENNES_CONFIG_DIR` keeps the user's own config (deliberate core mixes) out. Deterministic.
**Can check everything.** Its defaults: NES power-on RAM 00 00 00 00 FF FF FF FF (the app default; SMB1 runs with zeros, its verify recipe),
SNES work RAM zeros, NES 256x240, SNES 256x224.

### Mesen 2.1.1 (`mesen`: NES and SNES)

`Mesen.exe <rom> --luaScriptsToLoad tools\compat\mesen.lua --testRunner --timeout=<s>` from a private home
(`work\compat\mesen-2.1.1`: a copy of Mesen.exe with its own `settings.json`, rewritten before every run: save data in the run's save
folder, other folders in scratch, single instance off, no update check, audio off, Lua I/O allowed, a pad on port 1, SNES frame skipping
off). Every other setting is Mesen's default. The Lua sets the pad on the `inputPolled` event, reads `nesInternalRam` / `snesWorkRam` on
`endFrame`, writes `emu.takeScreenshot()` PNGs, and ends with `emu.stop(0)`. NES sound activity from the APU status register ($4015
length counters, a debug read with no side effect). Mesen's stdout (a file) lists every RAM byte the game reads before writing it; compat
records them with their symbols.
**Can check:** state, pictures, NES sound activity, saves. **Cannot:** SNES sound activity. **Defaults measured:** NES RAM all zeros,
SNES work RAM random; NES 256x240, SNES 256x224. Mapper-30 saves: `<rom>.ips` (a 32-byte IPS patch of the flash). Speed: about 270 NES
frames/s, 190 SNES frames/s here.

### BizHawk 2.11.1, NesHawk (`bizhawk-neshawk`) and BSNESv115+ (`bizhawk-bsnes`)

`EmuHawk.exe --chromeless --config=<scratch config.ini> --lua=tools\compat\bizhawk.lua <rom>`. The scratch config (JSON) pins the core
(`PreferredCores` NES -> NesHawk or SNES -> BSNESv115+, `DontTryOtherCores`), sound off, throttles off with frame skipping off
(`Unthrottled` would render one frame in three), `StartPaused` (so the script starts at power-on, frame 0, and unpauses), no first-boot
or update dialogs, run in background, the 88 default hotkeys blanked (EmuHawk takes the keyboard focus: a stray key would pause a run),
windows off screen, every path absolute in scratch and SaveRAM in the run's save folder. Lua: `joypad.set` with `"P1 A"`-style names,
`memory.read_u8(addr, "RAM")` (NesHawk, 2 KB) or `"WRAM"` (bsnes, 128 KB), `client.screenshot`, `client.exitCode`. SaveRAM is written when
EmuHawk closes the game, for every battery cart, named after the ROM with `_` shown as a space (`vrun game.SaveRAM`; the previous one
becomes `.SaveRAM.bak`).
**Can check:** state, pictures, saves. **Cannot:** sound activity (no Lua audio API). **Defaults measured:** NesHawk RAM = the FCEUX
pattern, pictures 256x224 (lines 8-231); bsnes v115 work RAM pseudo-random but identical every run ("Entropy Low"), 256x224. Region from
the header (NTSC). Speed: NesHawk about 165 frames/s, bsnes 220-300 frames/s; EmuHawk takes about 7 s to start from `P:`.
**Which bsnes:** in 2.11.1 the core named `BSNES` is the legacy bsnes v0.87 (LibsnesCore) and `BSNESv115+` the current bsnes (BsnesCore).
The legacy core stops the Nested ROMs on Project Nested's own "SRAM is missing" screen (it exposes no cartridge RAM for them; every frame is
a lag frame), while Super VRUN runs the same on both. `bizhawk-bsnes` pins BSNESv115+, the bsnes players use.

### FCEUX 2.7.0 (`fceux`: NES)

`fceux64.exe -lua tools\compat\fceux.lua <rom>` on a private copy of fceux64.exe and its DLLs (7.6 MB) in the run's scratch folder with a
fresh `fceux.cfg`: FCEUX keeps its config and folders beside its exe, and `-cfg` takes a file name, not a path ("A pathname can NOT be
specified", the 2.7.0 help). The config sets the battery-save folder (`odnonvol`) to the run's save folder, every other folder to scratch,
sound output off (the APU still runs: `sound.get()` reports the channels), single instance off. Every emulation setting is FCEUX's default.
Lua: `joypad.set(1, ...)` every frame (held during frame f+1 when set at `emu.framecount()` f), `memory.readbyte`,
`gui.gdscreenshot(true)` (the frame just completed, 256x240; `gui.savescreenshotas` gives the NEXT frame, cropped to 224 lines),
`sound.get()` for sound activity, `emu.speedmode("nothrottle")` (every frame rendered), `emu.exit()` (the battery save is written on close).
The script runs in a coroutine (Lua 5.1 cannot yield inside `pcall`). The adapter checks that the installed folder did not change.
**Can check:** state, pictures, sound activity, saves. **Defaults measured:** RAM 00 00 00 00 FF FF FF FF (`RAMInitOption 0`), NTSC (also for
a NES 2.0 "multiple region" header), old PPU, 8 sprites a line, the window shows lines 8-231 (pictures have 240), FCEUX's palette.
Mapper-30 saves: `<rom>.sav` is the WHOLE 512 KB PRG flash, written on every close and run from at the next power-on. Two windows (FCEUX
and its Lua console) show during a run. Speed: about 900 frames/s here.

### Snes9x 1.63 (`snes9x`: SNES)

The standalone Windows build has **no scripting and no movie, dump or max-frames switches**: `win32/wconfig.cpp` S9xParseArg knows only
`-removeregistrykeys -restore -hidemenu -fullscreen -cartb`, the shared S9xParseArgs adds sound, region, `-snapshot` and `-conf`;
`-playmovie`, `-dumpstreams` and `-maxframes` exist in the Unix port only (source of tag 1.63, read on GitHub). So `lib/adapters/snes9x.ps1`
drives **its own menu commands, sent as window messages to the main window of the process BrokenDev started** (by PID; the window starts
minimised without focus; menu ids from `win32/rsrc/resource.h`, handlers in `wsnes9x.cpp`):

1. a private copy of `snes9x-x64.exe` in scratch with its own `snes9x.conf` (Snes9x reads and rewrites its config beside its exe, and
   `-conf` only adds to it): every `Dir:` absolute, `Dir:SRAM` the run's save folder, `AddToRegistry FALSE`, `PauseWhenInactive FALSE`,
   frame skip 0, no timed battery saves, sound muted, OpenGL (the default Direct3D shows "Couldn't load selected driver" here);
2. input from a **.smv movie** BrokenDev writes (version 5, from power-on, read-only; the battery RAM is embedded and loaded at the reset),
   opened with File > Movie > Play;
3. **Pause, then Frame Advance one frame at a time**, each step preceded by Save Screenshot, which Snes9x writes at the end of that very
   frame: the picture appearing proves the frame ran, so frame numbers and pictures are exact (native size, 5-bit colour);
4. **work RAM from its own save state** (slot 0) after every frame: the freeze file is gzip with a `RAM` block of 128 KB (saving a state
   does not disturb the run: identical pictures with and without);
5. WM_CLOSE at the end: Snes9x writes the battery RAM to the save folder and exits.

Input alignment: Snes9x applies movie sample k for frame k+1 like BrokenNes, but latches the auto-joypad two lines after vblank with the new
sample where BrokenNes latches at vblank. The adapter reads the game's NMITIMEN ($4200 bit 0) from a short probe run and maps the movie for
an auto-joypad game (Super VRUN, $81) or a hand-read one (the Nested ROMs, $20); with its mapping each game replays BrokenNes' run exactly
(every recorded byte on every frame), with the other it meets another first room. A game mixing both reads would be one frame off in one.
**Can check:** state, pictures, saves. **Cannot:** sound activity; battery RAM above 128 KB is not reset by the movie (measured and noted).
**Defaults measured:** work RAM 0x55 everywhere, a fresh battery RAM 0x60, region from the header (NTSC), 256x224 (239 with overscan).
Speed: about 40 frames/s with reads (a save state per frame), 110 without, on a local disk; with those per-frame files on `P:` (a network
share) it fell to about 9 frames/s (smb1-nested: 3000 frames in 346 s), so they now go to a local folder (`%TEMP%\brokendev-snes9x`, or
`BROKENDEV_SNES9X_WORK`) when the scratch is on a network share, and are deleted after the run.

**Incident (2026-10-22, during the adapter's research, before `AddToRegistry` was known):** one probe ran Snes9x with its default
`AddToRegistry TRUE`, which at start-up rewrote three per-user file-association values that pointed at the user's own Snes9x 1.60 in
`Downloads`. They now point at a scratch copy (`...\Temp\claude\snes9x-scratch\exp1\emu\snes9x-x64.exe`, read back on 2026-10-22):
`HKCU\Software\Classes\Snes9x.Win32\DefaultIcon`, `...\Snes9x.Win32\shell\open\command`,
`HKCU\Software\Classes\Applications\snes9x-x64.exe\shell\open\command`. They were NOT repaired (a registry change is the user's to make);
the earlier values, rebuilt from Snes9x 1.60's own formats, would be:

```powershell
$exe = 'C:\Users\philt\Downloads\snes9x-1.60-win32-x64\snes9x-x64.exe'
Set-ItemProperty 'HKCU:\Software\Classes\Snes9x.Win32\DefaultIcon' '(default)' "$exe,0"
Set-ItemProperty 'HKCU:\Software\Classes\Snes9x.Win32\shell\open\command' '(default)' "`"$exe`" `"%L`""
Set-ItemProperty 'HKCU:\Software\Classes\Applications\snes9x-x64.exe\shell\open\command' '(default)' "`"$exe`" `"%L`""
```

Every later run used `AddToRegistry FALSE`, and the adapter now reads those three values before and after each run and reports any change
(`registryCheck` in the run's settings). Until they are restored, do not delete that scratch copy (double-clicking a ROM associated with
Snes9x would find nothing).

## 3. The compat matrix (2026-10-22)

`brokendev.ps1 compat all`, on the ROMs BrokenDev had built (2026-10-16/17; `brokendev.ps1 list`), BrokenNes from a Workshop built from
this branch (the shared one predates `run`). Every emulator ran with its own defaults. Records: `P:\BrokenDev\env\out\<project>\compat\`
(`compat.json`, `compat.html`, the pictures), the matrix `P:\BrokenDev\env\out\compat.html`. A dash: the emulator does not run that console.

| Project (ROM sha256) | BrokenNes | Mesen 2.1.1 | NesHawk 2.11.1 | FCEUX 2.7.0 | Snes9x 1.63 | BSNESv115+ 2.11.1 |
|---|---|---|---|---|---|---|
| vrun-nes (`3cfce10c`) | works | works | works | works | - | - |
| smb1-nes (`a80b9a59`, the power-on RAM fix, 2026-10-22 evening; was `8fc726d4`: works, works, **fails**, **fails**) | works | works | works | works | - | - |
| vrun-nested (`8ab7360b`) | works | works | - | - | works | works |
| vrun-nested-enhanced (`102a52fc`) | works | works | - | - | works | works |
| smb1-nested (`a834cff1`, from the fixed NES ROM; was `f6c48676`: the same four works) | works | works | - | - | works | works |
| supervrun (`4a56b5d8`) | works | works | - | - | works | works |

Evidence, cell by cell (first frame of each compared state; BrokenNes in brackets):

* **vrun-nes** (play, save, continue; 17 checks): Mesen game 129 [127], saved 171 [171], save `vrun_game.ips` (32 B), fresh boot finds it.
  NesHawk game 129, saved 171, `vrun game.SaveRAM` (524,800 B), fresh boot finds it. FCEUX game 126, saved 171, `vrun_game.sav`
  (524,288 B), fresh boot finds it; FCEUX also starts in BrokenNes' first room (pictures identical up to frame 600) and leaves it one frame
  earlier (609 [610]). Title pictures 0.99-1.0 like BrokenNes'; gameplay 0.53-0.65 (another room, by design).
* **smb1-nes** (10 checks): Mesen start 123 [123], World card ends 269 [268], all pass. NesHawk and FCEUX: `uninit-ram`, `world-card` and
  `scrolls` FAIL: `chr_test_active` = 255 from frame 20, `levelcard_state` stays 2, `camera_x` never moves (section 4, finding 1).
  **After the fix** (the live workspace's ROM, `a80b9a59`): all 10 checks pass on all four; start 123 everywhere [123], World card ends
  269 on Mesen and NesHawk, 270 on FCEUX [268]; `chr_test_active` 0 on every frame 20-119 on every emulator; title pictures similarity
  1.0; Mesen's uninitialised-read list is empty (was 199 bytes).
* **vrun-nested** (16 checks): Mesen game 296 [295], saved 665 [665], `.srm` 256 KB, fresh boot finds it. Snes9x game 295, saved 665,
  every picture identical to BrokenNes' (similarity 1.0: the same first room). BSNESv115+ game 296, saved 665, fresh boot finds it.
* **vrun-nested-enhanced** (16 checks): as vrun-nested (Mesen and BSNESv115+ game 297 [294], Snes9x 294).
* **smb1-nested** (10 checks): start 243 [243] everywhere; World card ends 404 / 405 / 406 [404] (Mesen / Snes9x / BSNESv115+).
  From the fixed NES ROM (`a834cff1`): all pass; start 243 everywhere; World card ends 386 / 405 / 385 [405]. Mesen and BSNESv115+ now end
  it 19-20 frames before BrokenNes and Snes9x (within the 30-frame tolerance; the NES ROM takes 145 frames from Start, Mesen's run 143): the
  Nested build recompiles NES code at run time and the fix re-packed the ROM's banks, which changes that load; not investigated further.
* **supervrun** (9 checks): story 307 [308] and game 532 [533] on Mesen and BSNESv115+ (one frame earlier: most likely where each applies a
  frame's input relative to the auto-joypad latch, the difference measured for Snes9x and compensated there; within the tolerance);
  Snes9x 308 and 533, the same recorded bytes as BrokenNes on every frame from frame 5; title 0.86 like BrokenNes' on Mesen and bsnes (an
  animated title one frame apart), 1.0 on Snes9x.

Timings (all scenarios of a project, this machine, `P:` a network share): BrokenNes 2-12 s, FCEUX 2-5 s, Mesen 9-25 s, BizHawk 15-47 s
(about 7 s of start-up per run), Snes9x 51 s (Super VRUN, local work folder) to 9 minutes (the Nested ROMs, measured before the work
folder moved off the network share).

## 4. Problems found (no game code was changed: these are for the developer to decide)

1. **SMB1 remake: fails on BizHawk NesHawk and FCEUX. A GAME BUG, already in the project's knownIssues, now seen in two real emulators.**
   The remake reads `chr_test_active` ($07FC) and `fire_anim_timer` ($07AE) without ever writing them and relies on RAM powering on as
   zeros. NesHawk and FCEUX power RAM on with the FCEUX pattern (00 00 00 00 FF FF FF FF), which puts $FF in both: `chr_test_active` reads
   255 from the first frame, the World card never ends (`levelcard_state` stays 2), the level never starts (`camera_x` never moves while
   Right is held), sound goes quiet (FCEUX: 5 of 80 30-frame windows active, against 57 on BrokenNes). Mesen powers NES RAM on as zeros, so the game works there, and
   Mesen's own uninitialised-read report lists both variables among the 199 bytes the remake reads before writing. Real hardware powers RAM
   on undefined, so a player's console can do the same. The fix is the one knownIssues names: clear both at boot (or all RAM in the reset
   code). The Nested build is immune: Project Nested's pipeline zeroes NES RAM itself (`MemoryEmulation.ZeroMemoryReset`).
   **FIXED the same evening in the user's live workspace** (`PROJECTS\SMB1_Remake_dev`, now `archives.json`'s `smb1_topaz.live`; engine
   commit `f10dbd6`). Mesen's report over all 159 replay cases lists 314 addresses: 165 never-used OAM bytes (the sprite DMA reads
   them), 2 dummy reads of NESFab's skip opcode (`IGN $01A9`, `IGN $09A9`), and **34 variables, 147 bytes** (the player timers, the PRNG's
   last five bytes, the enemy slot arrays, the music's mute flags, ...). The two named here are not enough: with only them zeroed, case 2-1k
   (the coin-room route) still fails under the FCEUX pattern and under all-FF (`ecoll_bits`, a Paratroopa one pixel off at frame 3491). The
   fix is one line, `ram-init = 1` in `game.cfg`: NESFab's reset code zeroes all 2 KB between its two vblank waits, before its runtime sets
   anything up, as the original's cold boot does (initialisers on the 34 variables re-allocated all of RAM and moved the boot's NMI enable
   past its vblank). Bank 31 free unchanged (1,098), RAM unchanged, the first NMI on the same frame. Proof: `verify.sh` all checks 3/3; the
   159-case suite gives the archived ROM's report line for line with ours' replay logs byte-identical; the suite again with the FCEUX
   pattern, all-FF and random power-on RAM (private copies of the project's Mesen, a hash of the written RAM and of the picture on every
   frame): every case the same replay and the same written RAM on all frames (pictures: below). Compat: all four NES emulators work.
   Left for the developer: with Mesen's AllOnes/Random, which also fill nametable RAM, one or two pixels of row 31 (the status-bar split
   line) show the not-yet-written second nametable (video memory, not RAM; clearing it does not fit the boot's tuned timing).
2. **VRUN reads RAM it never wrote** (Mesen's report): `redraw_backlog` ($05E0-$05FB, 28 bytes), `_save_addr@2` ($06FF), `tear_active`
   ($07CB-$07CE). No visible effect in these runs on zeros (Mesen), the FCEUX pattern (BrokenNes, FCEUX, NesHawk): every check passes on
   all four. Worth a look before it bites on a console whose RAM powers on otherwise. Not an emulator issue.
3. **Super VRUN reads work RAM it never wrote** (Mesen, whose SNES work RAM powers on random): `tccs_room.asm_chk_payload` ($7E2450),
   `tccs_currency.asm_cur_anim_acc` ($7EDC2F), `tearfx_heavy_now` ($7EDFF1), `fdb_tier` ($7EED8C), `vwq_count` ($7EEF93), `fbs_tier`
   ($7EF472), `players_n` ($7EF4F2). The game works with zeros (BrokenNes), random (Mesen) and bsnes' pseudo-random fill alike in these
   runs; `players_n` in particular deserves a look (read before NEW GAME 1P/2P sets it?).
4. **Nested VRUN takes no pause for about 95 SNES frames after the first room loads** (the NES build: about 45). A pause pressed 125
   frames after gameplay starts is ignored on BrokenNes and on BizHawk's bsnes alike; held from frame 420 it registers at 451, pressed at
   480 it registers at once. The Nested build's game frames run slower than SNES frames (the NES NMI counter skips SNES frames while code
   recompiles), so the game's own grace timer lasts longer. The Nested build's behaviour (no entry-point feedback in this recipe; no NES
   lag-frame emulation), not an emulator's; the compat save scenario pauses at frame 600.
5. **VRUN's first room differs between emulators by design** (the map seed rotates every title frame): FCEUX lands on BrokenNes' room (the
   same RAM fill and Start on the same game frame: gameplay pictures identical), Mesen and NesHawk on another (similarity 0.55-0.65). Not
   counted as a difference.
6. **Emulator facts a developer of these games should know** (not bugs):
   * FCEUX's mapper-30 save is the whole 512 KB PRG flash and is RUN at the next power-on: a `.sav` left by an older build replaces the
     new program of the same file name (measured: another ROM named `vrun_game.nes` booted VRUN's title from VRUN's `.sav`). Delete the
     `.sav` after every rebuild when testing in FCEUX. NesHawk keeps flashed 4 KB sectors over the ROM the same way (only the save sector
     here). Mesen stores an IPS patch of the changes. BrokenNes' save carries a ROM fingerprint and refuses another build's save.
   * BizHawk's core named `BSNES` (legacy bsnes v0.87) cannot run the Nested ROMs ("SRAM is missing"); BSNESv115+ runs them.
   * Power-on RAM differs by emulator (zeros, the FCEUX pattern, random, pseudo-random, 0x55): the compat matrix is how a dependence shows.
   * Super VRUN's animated palettes reach the picture a frame apart between emulators (BrokenNes' picture 900 equals Snes9x's 899 and
     Mesen's 900 Snes9x's 901, with the game state equal on every frame): emulator timing, not the game.
7. **Snes9x left the user's file associations changed** (see the Snes9x section: three HKCU values, not repaired, the commands to restore
   them are there). Not a game or emulator problem; a side effect of researching the adapter.

## 5. Traps

* **Mesen's test runner never starts when its standard output is a pipe**: the run hangs with no output. Its output goes to a file instead
  (through cmd.exe redirection), which is worth keeping: it lists RAM read before written.
* **A Mesen settings file without a pad on port 1 makes `emu.setInput` do nothing** (`getInput(0)` comes back empty): the private settings
  give port 1 a standard pad, as Mesen's first-run setup gives a player.
* **Mesen 2's Lua sandbox has `io` and `os` (AllowIoOsAccess) but no `package` library.**
* **FCEUX's `-cfg` takes a file name, not a path**; FCEUX writes its config and folders beside its exe, hence a private copy per run.
* **FCEUX's mapper-30 save is the whole 512 KB PRG flash and is run at the next power-on**: a save left by an older build replaces the new
  program. `compat` starts every run with empty save folders; `play` moves the saves aside when the ROM changes.
* **FCEUX's Lua 5.1 cannot yield inside `pcall`**: its script runs in a coroutine.
* **A PowerShell parameter named `$Input` collides with the automatic `$input`** and arrives empty (it cost the FCEUX work a round).
* **PowerShell 7's `Add-Type -ReferencedAssemblies` replaces the default reference set**: the picture helper passes `$PSHOME\ref` plus
  System.Drawing.Common and the private Windows assemblies it is built on (.NET 9+).
* **Snes9x's default `AddToRegistry TRUE` writes per-user file associations at every start-up**: the adapter's config sets it FALSE and checks
  the keys before and after each run.
* **Snes9x's default Direct3D output opens a "Couldn't load selected driver" box on this machine** (no DirectX 9 runtime): OpenGL is set.
* **Snes9x's per-frame screenshots and save states crawl on a network share** (9 frames/s on `P:` against 40 locally): they go to `%TEMP%`.
* **BizHawk's `Unthrottled` renders one frame in three**; the adapter turns the throttles off but keeps frame skipping at 0.
* **BizHawk runs one frame before a `--lua` script starts** unless it starts paused (`StartPaused`, then `client.unpause`).
* **BizHawk takes the keyboard focus**: its default hotkeys are blanked in the run's config so a stray key cannot pause a run.
* **BizHawk 2.11.1's core named `BSNES` is the legacy bsnes v0.87**; the current bsnes is `BSNESv115+`.
* **A ROM file name is part of every emulator's save name** (BizHawk even shows `_` as a space): runs that share a save folder keep the
  ROM's file name (compat copies the built ROM under its own name).
* **Super VRUN's own test scripts close every Mesen started with `--testRunner`**: never run them at the same time as `compat`.
* **The BrokenNes column needs a Workshop with `--brokendev run`** (built from this repository after 2026-10-22): an older one is reported
  as "cannot be tested", never as a failure of the game.
