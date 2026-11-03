# Next pool: planned projects (not started)

Status: plans only (the user's brief of 2026-11-01). Each project becomes its own card on the BrokenNes Next board; none starts
until the user picks it. Two of them (P3, P4) need an interview first. P4 -> P5 -> P6 is one chain: the Remastered running in WideMess Virtual is the reference for Super VRUN WideMess, and then the back-ports are done once for both Super VRUN versions.

| # | Project | When | Needs first |
|---|---|---|---|
| P1 | Vaults on the Super Game Boy: the original sound effects on the SNES's sampler | later this week or next | the original SFX files |
| P2 | Super VRUN: MS-DOS port | next week | your DOSBox-X debugging setup |
| P3 | An "x86 console" in BrokenNes (386 + DOS extenders, ported from tiny386, kept simple) | stretch | an approved FreeDOS / DOS32A download |
| P4 | WideMess Virtual: a web game as a WideMess "cartridge" | **built 2026-11-01 (desktop)** | - |
| P5 | Super VRUN on the WideMess (a fork kept in step with the SNES version) | after P4 | P4 running CTW Remastered as the reference |
| P6 | Super VRUN (SNES and WideMess together): the map, the stats, the title effect and the options UI from CTW Remastered | after P5 | the Remastered project files |

## Answers (interview, 2026-11-01)

| Question | Answer |
|---|---|
| Super VRUN's maintained versions | **SNES, WideMess, Genesis** (the DOS port, P2, is its own track). |
| P1: the original sound effects | The same files the Game Boy port's Fourier pass used (find them through the Vaults repo's history). |
| P2: the DOS target | Whatever the DOS version uses now; read when the user hands over the project and the DOSBox-X setup. |
| P4: where WideMess Virtual works first | **The desktop** (its web view); Lite later. |
| P4 / P6: CTW Remastered | The full package is on the user's Desktop: `VRUN_CTW_Remastered-full-package.zip` (1 GB: the working copy with its `.git`, the soundtracks and videos git leaves out, the built launcher). **It is a separate git** (github.com/ircluzar/VRUN_CTW_Remastered, `main` @ `fae13ea`): it may be brought into the BrokenNes solution, but any change to it must be pushed to its own repository. It is the JS replacement engine (`vrun/*.js`, Node 18+ and Edge / Chrome), not a Clickteam runtime; its own rules (in the package's `_claude-memory/`): performance changes handled its way, retro looks only from emulated hardware limits, no flavour text. |
| P5: the wide backgrounds | **Repeat each room's edges** to the sides (automatic, every room, no new art). |
| P6: unvisited rooms on the map | **Like CTW Remastered** (copy what its map does). |
| P1: the Vaults music on a Super Game Boy | **Stays on the Game Boy** channels; only the sound effects move to the SNES samples. |
| P2: 2P in the DOS version | **Kept, on two joysticks.** |
| P3: why an x86 console | **All three:** run Super VRUN DOS inside BrokenNes (debugger, Timeflow, save states), mix the x86 with the other consoles' chips, and run DOS games in general (a real new console). |
| P3: how much PC | **A 386 with DOS extenders** (most 90s DOS games). |
| P3: own or ported | **Port an open-source one** under a documented licence exception, like the Foreign APU cores. |
| P4: what you see first | **The Remastered's own title screen** (its settings and map screens included). |
| P4: 398 in 400 | **Black columns**, 1 pixel each side, picture pixel-exact. |
| P4: what must work | Picture, input, sound, pause, plus: **it scales with the emulator's viewport like an emulated game, and its volume follows BrokenNes's volume.** Nothing more (no save states, rewind, recording, or mixer routing needed). |
| P6: the stats beside the map | **The Remastered's Pro HUD set**, for each player. |
| P6: which versions | **SNES, WideMess and Genesis together.** |

---

## P1. Vaults on the Super Game Boy: the original sound effects

**Goal.** On a Super Game Boy, Vaults plays the **original Eternal Degrade sound effects** as samples on the SNES's SPC700 instead of
the Game Boy synth recreations (the Fourier-fitted ones). On an original Game Boy and a Game Boy Color nothing changes. The Game Gear
port (S1) is not affected: it has no SNES behind it.

**How (the Super Game Boy's own mechanism).**
- The SGB lets a Game Boy game upload data into the SNES's sound RAM (`SOU_TRN`, through the same VRAM transfer the border uses)
  and trigger sounds (`SOUND` packets). The game already detects the SGB and sends packets at screen changes (V6, `src/voc_sgb.c`).
- Convert each original SFX to BRR (the SPC700's sample format) at a rate and length that fit: the SPC700 has 64 KB, shared with
  the SGB's own sound driver, so the set has to be budgeted (likely 8-16 kHz, trimmed tails). Upload them once at power-on (masked
  black screen, as the border already is).
- Two possible drivers:
  1. **Use the SGB's built-in sound driver** with uploaded samples (`SOU_TRN` data + `SOUND` packet effect numbers), if it can play
     custom samples by number. Simplest; limited by what the driver allows (voices, pitch, volume).
  2. **Upload our own small SPC700 program** (a sample player that takes a command byte), as some SGB games did. More work, full
     control (priority, voice stealing, pan).
  The first step is a spike that proves which one works on the user's real SGB, in Mesen's SGB mode and in bsnes/BizHawk.
- Game side: each SFX trigger checks "on SGB" and sends a 16-byte `SOUND` packet instead of writing the Game Boy sound registers.
  Packets cost CPU time and need gaps between them: an SFX queue that never sends two in a frame, and the music stays on the Game
  Boy channels (or mutes the SFX channel the original used, to avoid doubling).
- Must stay true: physics tick-identical to Eternal Degrade, 0 late pictures, the Game Boy and Color versions byte-identical in
  sound.

**Milestones.** P1a spike (one original SFX plays from a `SOUND` packet on the SGB in Mesen + your console) → P1b the converter
(original SFX → BRR set, size budget report) → P1c the game side (queue, triggers, no double sound) → P1d checks (the packet log
compared with the trigger log, a negative control, Mesen SGB recordings) and your console test.

**Open questions.** Where the original SFX files are (the Eternal Degrade project; the V-round used them for the Fourier fit). The
music: keep the Game Boy music on SGB, or also play the original music later (bigger, separate project).

---

## P2. Super VRUN: MS-DOS port

**Goal.** Resume the DOS port of Super VRUN (started on another computer, paused until the SNES version reached parity; the weapon
modifiers and items were that parity). Next week's pool.

**Plan.**
- **You provide** the DOS project (where it lives, its toolchain: e.g. Open Watcom / DJGPP / Borland) and the **DOSBox-X debugging
  setup** (config, how to run, its debugger). Nothing is downloaded until you say so.
- First session: a read-through: build the DOS version as it is, run it in DOSBox-X, and list how far it is from Super VRUN SNES
  today (rooms, physics, enemies, the weapon modifiers and items, 2P, sound, saves).
- Then a parity plan in slices, each checked against the SNES version: the same input on both, compared by game events (rooms,
  pickups, deaths, clears), the same idea as the Sega ports' cross-console comparison (S0). Where the DOS side can't be scripted
  the way BrokenNes can, the plan says so and we build the smallest harness DOSBox-X allows (its debugger, logging, recorded input).
- BrokenDev: a project entry for the DOS build (build, run in DOSBox-X, the checks that apply).

**Open questions.** The DOS target (real mode / DOS extender, VGA mode 13h / Mode X, Sound Blaster / AdLib), the minimum PC, and
whether 2P stays (keyboard sharing, two joysticks).

---

## P3. An "x86 console" in BrokenNes (386 + DOS extenders, kept simple)

**Decided (interview).** A 386 with DOS extenders, **ported** from an open-source emulator under a documented licence exception,
for all three reasons (Super VRUN DOS inside BrokenNes, mixing the x86 with the other consoles' chips, DOS games in general). The
user's rule on top (2026-11-01): **simplicity is king**; an x86 PC at scale is a mess, so port a small emulator and only the parts
a DOS game needs.

**Candidates looked at (2026-11-01).**

| Emulator | Licence | CPU | Picture / sound | Fit |
|---|---|---|---|---|
| **tiny386** (hchunhui) | **BSD-3** (CPU and devices; some MIT parts from QEMU / TinyEMU; the optional AdLib `fmopl` LGPL; SeaBIOS LGPL-3) | i386 written from scratch, **~6K lines of C99**; 486/586/686, FPU, MMX/SSE, amd64 optional | VGA + Bochs VBE; Sound Blaster 16, AdLib OPL2, PC speaker | **Best fit.** One C file per chip (`i386.c`, `vga.c`, `sb16.c`, `i8259.c`, `i8254.c`, `ide.c`, ...) with `pc.c` as the bus: that is our CPU / PPU / APU + bus shape. Small enough to port like the Foreign APU engines. |
| 8086tiny | MIT | 8086 only, under 25 KB of source | CGA / Hercules | Simplest of all, but no 386, so no DOS extenders. |
| emu2 | GPL-2 | 8086 / slow 286, DOS calls emulated (no real DOS) | text mode only | Too small: no graphics, no 386. |
| Halfix | GPL-3 | x86-32 with SSE2 | VGA (ET4000-like); **no sound** | Boots every DOS, but bigger, GPL-3, no sound, timing "completely off". |
| NXVM | (unchecked) | 80386, 37K lines | VGA | Runs MS-DOS; three times tiny386's size. |
| DOSBox / DOSBox-X, 86Box / PCem, Bochs | GPL / LGPL | everything | everything | Far too big for "keep it simple" (DOSBox-X is the debugging tool for P2, not a core). |

**Recommendation: tiny386, cut down.**
- **CPU slot:** its i386 core only (gen = 386/486; no amd64, SSE or the `fast/` path at first; the x87 FPU only if a game needs it).
- **Picture slot:** its VGA: text modes, mode 13h, Mode X (unchained 320x240). No VBE at first.
- **Sound slot:** Sound Blaster 16 (digital), AdLib OPL2 (music; `fmopl` is LGPL: the exception, or a later swap for one of our own
  FM cores through the mesh), PC speaker.
- **The rest (the "board"):** PIC, PIT, keyboard controller, CMOS clock, DMA, and one hard-disk image (IDE). Left out: networking
  (NE2000), the ESP32 / Android / Linux-boot parts.
- **Firmware and DOS (decided 2026-11-01): fully open, nothing for the user to provide.**
  - **Our own small BIOS in C#** instead of SeaBIOS: the BIOS services DOS and games use (INT 10h video, 13h disk, 16h keyboard,
    1Ah clock, the equipment / memory calls), answered by the console itself the way DOSBox does. No firmware file, no licence.
  - **FreeDOS** (GPL-2) on a disk image the console builds or ships beside the app (not compiled in).
  - **DOS32A** (open source) as the DOS extender for Super VRUN DOS: a drop-in for DOS4GW, which is not open.
  - Licences checked again before anything is downloaded (the user approves the download).
- **The mesh:** the x86 console gets the same contract as the others: a CPU / PPU / APU per slot and the picture / sound bridges, so
  its VGA can draw another console's game and its Sound Blaster / OPL2 can play another console's notes (and the reverse).

**Milestones.** P3a pick confirmed by a spike: the C code built as-is, FreeDOS + a small test program + one DOS4GW program running,
to measure the port's size → P3b the C# port of the CPU in Workshop, checked against tiny386 instruction by instruction (a trace
compare, the way the Foreign APU ports were checked) → P3c VGA + timers + keyboard: FreeDOS boots headless, golden pictures → P3d
SB16 / OPL2 / speaker → P3e the console in the desktop (Load ROM takes a disk image), save states → P3f Super VRUN DOS on it (P2) →
P3g the mesh (bridges both ways), Lite last (speed in WASM measured).

**Decided.** tiny386's 386 core + our own BIOS + FreeDOS + DOS32A: a fully open stack.

---

## P4. WideMess Virtual (built 2026-11-01, desktop)

**Goal.** A passthrough mode: a WideMess "cartridge" that is a web game. BrokenNes shows the game's picture as the WideMess picture
and sends it the controls. The proof of concept is **VRUN Corrupt The World Remastered**.

**What the Remastered is (read from the package, 2026-11-01).**
- The original Clickteam Fusion 2 **HTML5 game** (`vrun/src/Runtime.js`) plus the remaster's own modules (`vrun-*.js`: title,
  settings, map, HUD modes, input, audio), served by a **local Node.js server** (`tools/replacement-engine/Launch-VRUN.mjs`,
  `http://127.0.0.1:8766/`, Node 18+; this machine has Node 25) that also **generates the worlds** (seeded). `VRUN.exe` is a
  launcher that hides the server and opens the game in its own browser window.
- **Picture:** several stacked layers at **398 x 224**: a background video, the game canvas (`MMFCanvas`, drawn through WebGL with
  `preserveDrawingBuffer: false`), a 2D overlay at the same size (HUD, effects), and the remaster's UI canvas (`VRUNUi`, title,
  settings, map) over a title background canvas (`VRUNBg`). The WideMess is 400 x 224: the game sits in the middle with a 1-pixel
  column on each side.
- **Input:** `vrun-input.js` listens to the keyboard and gamepad first and turns bound inputs into the runtime's original key codes
  (arrows, Z, X, Space, Enter, I/J/K/L) as synthetic events; menus get navigation events. That is the one place a host pad plugs in.
- **Timing:** the game keeps its 60 ticks a second even when the browser draws at 30 fps.
- **Sound:** Howler.js (Web Audio) for the music (soundtracks A-E and Vaults, streamed) and sound effects, plus mespeak voices;
  the background videos are HTML5 video.
- **Its own test** (`tools/uat`) already drives the real game in a headless Edge/Chrome (cheats in `agent.js`): the model for
  driving it from BrokenNes.

**The plan (desktop first).**
1. **Host:** BrokenNes starts the Remastered's Node server on a free port (the package path from a small cartridge manifest, e.g.
   a `.wmv` JSON: the folder, the start command, the seed) and opens the page in an **offscreen WebView2** (the desktop already
   ships WebView2 for the overlay cards). It stops the server when the cartridge is ejected.
2. **Picture:** a small in-page compositor draws the stacked layers into one 398 x 224 frame each animation frame and hands the
   pixels to BrokenNes (WebView2's shared buffer, or a post message if that's fast enough); BrokenNes puts it in the middle of the
   WideMess's 400 x 224 output, so the shaders, scaling and recording work as for any game. Fallbacks if that costs too much: the
   DevTools screencast, or showing the WebView2 itself where the picture goes (then the shaders don't apply).
3. **Input:** BrokenNes's pad (keyboard or gamepad, 1P) becomes the runtime's keys through `vrun-input.js` (a "host" input source
   it accepts from the page bridge), so the Remastered's own bindings stay the reference.
4. **Sound:** first, the page plays its own sound through WebView2 (BrokenNes's volume sets the page's volume). Routing it into
   BrokenNes's mixer (for recording, the synth tools) is a later step.
5. **What BrokenNes can't do with it:** save states, rewind / Timeflow, core swaps and corruption need a machine state; the game
   isn't emulated, so these are greyed out with a reason (no silent fallbacks). Pause = the page's own pause.
6. **Checks:** a Workshop/UAT case that boots the cartridge, reaches the title, starts a seeded world and sees the picture change
   and respond to input; frame pacing and input latency measured.

**Changes to the Remastered** (a few lines: the compositor hook and the host input source in `vrun-input.js`) are made in its own
repository and pushed there (`ircluzar/VRUN_CTW_Remastered`), never only in BrokenNes. BrokenNes refers to the package folder; it
doesn't copy the game into its repo.

**Decided (interview).** It boots into the Remastered's own title screen; 398 sits in 400 with a black column each side; it must
scale with the emulator's viewport like an emulated game and follow BrokenNes's volume, nothing more. So step 4 stays the simple
one (the page's own sound, its volume set from BrokenNes), and recording / mixer routing are not needed.

**As built (2026-11-01, desktop).** Status: done and checked; Lite later.

- **The cartridge:** a `.wmv` JSON manifest (`Windows/Virtual/VirtualCartManifest.cs`); the one for the Remastered is
  `WideMess/virtual/VRUN-CTW-Remastered.wmv` (`"folder": "../../../VRUN_CTW_Remastered"`, i.e. the package unpacked at
  `PROJECTS\VRUN_CTW_Remastered` beside the BrokenNes checkout; edit the folder if it lives elsewhere). Fields: `format`
  (`widemess-virtual`), `version` 1, `title`, `folder` (absolute or relative to the manifest), `command` (`node`), `args` (`{port}`,
  `{folder}` filled in), `ready` (a line of the server's output), `page`, `width` / `height` (398 x 224), `layers` (the stacked
  layers, back to front). Load ROM (and `POST /api/emulator/load-rom`) takes it and selects the WideMess; the file dialog lists
  "WideMess virtual cartridges (*.wmv)". No game file is copied into BrokenNes.
- **Host** (`VirtualCartServer`, `VirtualCartSession`): the server on a free loopback port inside a kill-on-close **job object** (it
  dies with the cartridge's eject, Close ROM, exit, and also when BrokenNes is killed: tested); the page in a WebView2 on its own STA
  thread, in a window parked off-screen (tool window, never activated), with occlusion / background throttling switched off and
  autoplay allowed; its own profile (`%LOCALAPPDATA%\BrokenNes\WebView2-virtual`, or under `BROKENNES_CONFIG_DIR`). If the server or
  the page's process dies, the cartridge is ejected and you are told why.
- **Picture:** a script injected by BrokenNes (`VirtualCartScript.cs`; **no change to the Remastered**) forces `preserveDrawingBuffer`
  on WebGL, draws the layers back to front with their computed opacity into one 398 x 224 canvas at most 60 times a second, and copies
  the RGBA into a **WebView2 shared buffer** (two slots), then posts one short message with the slot. The session puts it in the
  middle of a 400 x 224 frame (columns 0 and 399 black) and the app shows it through the normal console-session display path, so the
  viewport scaling and shaders apply as for any game. (postMessage of the pixels was not needed: the shared buffer holds 60 fps.)
- **Input:** `navigator.getGamepads()` reports one standard-mapping pad that is BrokenNes's pad (1P): B 0, A 1, Y 2, X 3, L 4, R 5,
  Select 8, Start 9, d-pad 12-15. `vrun-input.js` reads it like any gamepad, so the Remastered's own pad bindings and menus (title,
  settings) work unchanged; its title shows "GAMEPAD READY". Real gamepads go through BrokenNes's own bindings.
- **Sound:** the page plays its own sound. Every Web Audio context gets a host gain (the music bus, the sound effects and the menu
  sounds all go through Web Audio) set from BrokenNes's volume; BrokenNes's mute mutes the whole web view. BrokenNes's own mixer
  gets silence of the right length (it paces sessions by sound).
- **Pause:** when BrokenNes stops asking for frames (pause, or anything that stops the emulation loop) for 250 ms, the page is muted,
  hidden and **suspended** (`TrySuspendAsync`: scripts, timers and animation stop); the next frame resumes it.
- **Refused with the reason** ("a web game shown through BrokenNes, not an emulated machine..."): save states (Emulator menu items
  greyed out, API refuses), Timeflow REC (greyed out), core menus (one greyed-out line), core swaps (refused, no restart), corruption
  (refused); the console status reports the cores as "none (virtual cartridge)". Reset reloads the page.
- **Checks:** `BrokenNes.Windows.exe --wmv-check <cart.wmv> --out <dir> --bad <bad.wmv> --bad-server <dead.wmv> [--play]` (offscreen, no app
  window: title reached and animating, 400 x 224 + black columns, frame pacing, the pad moves the title menu and opens / closes the
  settings, pause, page errors, the server gone after the eject, a bad folder and a dead server refused) and `UAT/wmv-check.ps1
  -Exe <exe> -Cartridge <cart.wmv> -Bad <bad.wmv>` (the real app over HTTP: picture, input, pause, refusals, Close ROM and app exit
  stop the server, bad cartridge refused). Numbers on this machine: server up in 0.65-0.85 s, title in about 3 s, **60.0 page frames
  a second, 60 shown, 0 repeated, 0 overwritten**, pad change to the page's next frame 8-10 ms (plus the page's own frame and
  BrokenNes's display frame: roughly 2-3 frames end to end). In the game (`--play`: START, past the engine's title and story, the
  WebGL layer read back every frame): 60.2 page frames a second; with the machine busy (other agents' test runs), about 13 a second
  of BrokenNes's 60 showed the previous frame again while the next arrived a moment later: the page's and BrokenNes's 60 Hz are two
  clocks that are not locked together (title on an idle machine: 0). Locking them (BrokenNes asking the page for each frame) is the
  next step if that judder is visible.
- **Fixed on the way:** `POST /api/emulator/close-rom` ignored console sessions (the SNES / Game Boy / Sega game kept running); it now
  closes them.
- **Not done (not asked):** Lite; routing the page's sound into BrokenNes's mixer (recording, synth); HTML5 media elements that are not
  routed through Web Audio are covered only by the mute, not the volume (the Remastered routes its music through Web Audio).

---

## P5. Super VRUN on the WideMess

**Goal.** A WideMess version of Super VRUN: a fork of the SNES game for the WideMess fantasy console (almost the same spec: the
SNES's chips plus the 400 x 224 picture, more layers, the big SPC700). Done at this stage on purpose: the user keeps maintaining
all of Super VRUN's versions, and the next step (P6) changes the SNES and WideMess versions together.

**Scope for now (the user's words: "nothing more").** The game centred in the middle of the 400-wide picture, and the
backgrounds extended to fill the full width. Same game, same physics, same rooms, same 2P, same sound.

**Plan.**
- With P4 done, CTW Remastered runs in WideMess Virtual beside it: the reference for how wide play should look.
- One source tree, two builds (a WideMess build flag / target in SuperVRUN_dev, not a copy that drifts): the shared game code,
  the WideMess differences (screen width, the side backgrounds, the WideMess header) behind the flag.
- Checks: the SNES build byte-identical before and after the fork work (its golden hash), the WideMess build's self-test and
  continue checks, LAG 0, both builds on BrokenNes (the WideMess console) and the SNES build on the other emulators as today.

**Decided.** The extended backgrounds repeat each room's edge tiles to the sides.

**As built (2026-11-03, partial: step 1 of 2).** SuperVRUN_dev branch `widemess` (from `weapon-mods` 8953dbf, the newest branch),
commit 345a941: `./build.sh --widemess` builds `build-wms/supervrun.wms` from the same tree (`SV_WMS`; `tools/wmspack.py` writes
the header: map 0 LoROM, battery, feature bit 0). The game unlocks the WideMess and sets `WMCTRL` WIDE: it runs centred in the
400-wide picture on BrokenNes's `wms` console. The SNES ROM is byte-identical (md5 `2b1389428f70a98457ef0406836341ee` before and
after). **Not done: the edge repeat.** The sides show the 32-wide maps wrapping (the room's far side, the HUD three times).
Blocker: a 64-wide room map needs a second 1K-word screen beside `$6C00`, and both neighbours are in use (`$6800-$6BFF` the
HUD / shadow / visor tiles, `$7000` the boss blocks); the wide SNES modes read only the first 64 KB, and VRAM has no free 2 KB.
Windows cannot hide the sides either (the 2P colour window HDMAs `WH0-WH3`). The design for after the space is found, and the
resume list, are in Super VRUN's memory note `project_widemess_p5.md` and `%LOCALAPPDATA%\VRUN_Nes_Dev_work\wms_progress\p5.log`.
No BrokenDev project was added yet.

---

## P6. Super VRUN (SNES and WideMess): back-ports from CTW Remastered

**Goal.** Bring the Remaster's best UI to Super VRUN, the SNES and WideMess versions in one pass (the reason P5 comes first), with the
Remastered running in WideMess Virtual (P4) as the reference:
1. **The pause screen** becomes the map, styled like the Remastered map, with **both players' stats** on the sides of the screen.
2. **The title screen effect** from the Remastered title.
3. **The options UI** adapted from the Remastered one.

**Plan.**
- Read the Remastered project (Clickteam source or the HTML5 export): the map's look and data (visited rooms, current room, items,
  the run's progress), the stats it shows, the title effect (how it's built: layers, scrolling, palette effects), the options layout.
- Map each to the SNES: the pause screen has a free frame budget, so the map can be drawn into BG3/BG1 from the run's visited-rooms
  record (already in the save since the weapon round), stats on the sides as BG3 text; the title effect with HDMA / palette
  cycling / BG scroll where the original uses canvas effects; the options as a BG3 menu like the Remastered layout.
- Checks: screenshots against the Remastered (the user judges the look), the self-test and continue checks, LAG 0 on the title and
  pause screens, 2P stats per player, and a console ROM.

**Open questions.** Where CTW Remastered lives; whether the map shows unvisited rooms (fog) like the Remastered; which stats (time,
deaths, kills, coins, items, upgrades).
