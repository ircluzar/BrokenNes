# BrokenDev debugging toolkit: a debugger, hardware checks and an MCP server

Plan of 2026-10-23, written after an interview with the user. It starts **after WideMess milestone 1** (the week of 2026-10-26).
Nothing is built yet.

## Why

Almost every game bug found in October was found with a tool an agent wrote by hand for that one bug, mostly Mesen Lua scripts:
- the SMB1 remake's 34 variables read before they were set;
- VRUN's room loader reading past cell 224;
- Super VRUN's 61 RAM reads before set;
- the shop lag;
- VRUN's per-frame cost profiler (`frame_cost.lua`).

Mesen also cost time of its own:
- agents fought over the one test-runner lock;
- runs hit the watchdogs;
- Super VRUN's scripts stopped other agents' runs;
- first-run dialogs blocked automation;
- write callbacks that ignore mirrors missed 45% of Super VRUN's DMA writes.

BrokenNes runs all our games, but its own debugging surface is thin. The desktop app's HTTP API can pause, read and write memory, read the CPU, grab the
picture and save states. But it has no breakpoints, no watchpoints and no trace, and its memory routes only know the NES
(`NesMemoryExtensions`: System RAM, CPU Bus, PRG ROM, PRG RAM, CHR, Nametable). A SNES or Game Boy game's memory cannot be read
through it at all.

The goal: debug our games on BrokenNes itself, with tools that agents and the user share, and that catch problems the day they
appear. A new game is coming as well: **VRUN: Vaults of Obsolete Code**, a Game Boy spinoff of Corrupt The World. BrokenDev gets
ready for it, with a Game Boy hello world built during this plan.

## Decisions (interview, 2026-10-23)

| Topic | Decision |
|---|---|
| Consoles | **NES, SNES and Game Boy** (the SNES with its sound CPU, the SPC700). The Game Boy is in because of VRUN: Vaults of Obsolete Code. Master System, Game Gear and Genesis later. |
| Audience | **Both, agents first.** The engine and the MCP server first; then debugger windows in the desktop app on the same engine. |
| MCP | **Headless and live.** Headless: the server runs its own invisible emulators. Live: it attaches to the desktop app the user is playing. |
| Live control | **Read freely, control when asked.** Agents read memory, the picture, the trace and the checks at any time; pausing, stepping, breakpoints, writing memory or loading a state only on the user's request in chat. Saves and progression are never touched. |
| MCP scope | **BrokenNes and every game repo**: registered once in the user-level Claude settings (the user's answer is the permission). |
| Viewers | **Both:** desktop windows first, then web pages in BrokenDev (for Lite and the phone). |
| Check strictness | **The strictest real hardware, with opt-outs per game.** A finding a game accepts carries a written reason. |
| Verify gate | **Fail on new findings.** Each game keeps its list of accepted findings; anything new fails `brokendev verify`. |
| Budgets | **Fail over budget, warn near it** (warn at 90% of a limit). Limits per game in `project.json`. |
| Sound CPUs | **Yes:** the debugger and the checks cover the SPC700 and the Game Boy's sound registers too. |
| Bug capture | **The last 30 s, with sound.** Saved in **the game's workspace, outside git**. In **the desktop app and Lite** (Lite offers it as a download). |
| Movies | **Our own format, with exports** to FCEUX `.fm2` and BizHawk `.bk2`. |
| Movie tests | **The ones we promote** become tests that `brokendev verify` replays every time. |
| Bisect | **Over the game's commits**, with today's BrokenNes. |
| Debug port | **Print messages, asserts, named timers, event markers.** **Stripped from release builds.** |
| Regular run | **Before every hand-off and on request** (no scheduled task). |
| Chaos bots | **Only when asked.** |
| Coverage map | **Yes.** |
| Room gallery | Not in this plan. |
| Mesen and the others | **Cross-check only.** Agents debug on BrokenNes. Mesen, BizHawk, FCEUX and Snes9x stay for compat runs and as a second opinion. The Mesen tracers stay in BrokenDev, frozen. |
| Game Boy toolchain | **GBDK-2020** (C plus assembly, the PVSnesLib model). |
| Game Boy target | **Game Boy Color, also playing on the original** (one ROM: colours on a Color, greys on an original). |
| Game Boy compat | **Mesen and BizHawk** (already installed; BizHawk's Gambatte and SameBoy cores). |
| Game Boy workspace | **`PROJECTS\VRUN_VOC_dev`**, its own git, local commits only, never pushed. |
| Order | **WideMess first, then this plan, then the Game Boy game** (its plan: `docs/projects/vrun-voc-plan.md`). |

## The pieces, in detail

### 1. The debugger engine (NES, SNES, Game Boy)

Built into the emulator engine shared by the desktop app, Workshop and Lite.

- **Breakpoints** on execute, read and write:
  - on an address, a range or a symbol name;
  - with conditions (`A == 3 && [player_hp] < 2`) and hit counts;
  - as "tracepoints" that log and keep running.
- **Watchpoints that see every mirror.** A watch on a RAM byte fires on every address that reaches it:
  - NES: `$0000-$1FFF`;
  - SNES: `$00-$3F` / `$80-$BF:0000-1FFF` reach `$7E:0000-1FFF`, and register mirrors like `$80:420B`;
  - Game Boy: echo RAM.

  DMA and HDMA reads and writes count as accesses, so a watch on VRAM sees a DMA upload.
- **Stepping and history:**
  - step by instruction, step over, step out, by scanline or by frame; run to a line or frame;
  - **step back**, from save states plus replay;
  - a **call stack**, tracked from JSR/JSL/RTS/RTL/RTI and interrupt entries.
- **Trace log:**
  - a ring buffer of the last instructions (registers, cycle, scanline, symbol), always ready while debugging;
  - filtered traces to a file (by range, bank, function or frames);
  - the same columns as Mesen's tracer, so a BrokenNes trace diffs against a Mesen trace for cross-checks.
- **Symbols:**
  - NESFab `.mlb` (VRUN, Nested VRUN, SMB1 remake);
  - WLA-DX `.sym` (PVSnesLib: Super VRUN, WideMess);
  - GBDK's symbol output (VRUN: Vaults of Obsolete Code).

  Variables are read and written by name, with their sizes.
- **Memory domains for all three consoles:**
  - SNES: WRAM, VRAM, CGRAM, OAM, sound RAM, SRAM, ROM, registers;
  - Game Boy: WRAM, VRAM, OAM, HRAM, cartridge RAM, ROM, I/O.

  This fixes the NES-only HTTP memory routes.
- **The sound CPUs:** breakpoints, trace and memory on the SPC700, and the Game Boy's sound registers as a domain.
- **Zero cost when off.** With no breakpoint, watch or trace set:
  - every golden hash is unchanged;
  - speed is measured unchanged with the interleaved A/B method of the SNES speed pass;
  - the FL plugin and Lite pay nothing.

### 2. Hardware checks

Rules that watch a running game and report what would break on real hardware. They run in the debugger, in headless runs and in
every `brokendev verify`.

**The strictest real hardware.**
- A finding names:
  - the rule;
  - the first frame, the PC and the symbol;
  - how many times it happened.
- A game accepts a finding in its `project.json` with a reason. Anything new fails verify.

First catalogue (to settle at the start of the track):

- **NES:**
  - RAM read before it was written (real RAM powers on with random contents);
  - picture-chip writes during rendering that corrupt (`$2006`/`$2007`, OAM);
  - OAM DMA outside vblank;
  - the frame-counter IRQ left on without a handler (the SMB3 red clouds);
  - DMC DMA colliding with `$2007` / `$4016` reads;
  - unofficial opcodes;
  - open-bus reads;
  - stack wrap;
  - an NMI that arrives before the frame's work is done (a lag frame);
  - mapper 30 flash writes outside the save routine.
- **SNES:**
  - work RAM, VRAM, CGRAM or OAM read before written;
  - DMA started while HDMA is running (freezes the early CPU revision);
  - HDMA switched on mid-picture;
  - VRAM, CGRAM or OAM writes during the picture without forced blank (dropped);
  - DMA overrunning vblank;
  - multiply or divide results read too early;
  - SPC700 handshakes that never answer;
  - sound RAM overrun into the echo buffer;
  - stack overflow into variables;
  - open-bus reads.
- **Game Boy:**
  - RAM read before written;
  - VRAM writes while the picture draws, OAM writes during modes 2-3 (dropped);
  - OAM DMA run from outside HRAM;
  - **the LCD switched off outside vblank** (it can damage a real original Game Boy);
  - the unusable `$FEA0-$FEFF` area;
  - Color palette writes during mode 3;
  - the double-speed switch without its STOP sequence;
  - cartridge RAM written without being enabled;
  - the HALT bug;
  - stack overflow.

Every rule gets a small test ROM that must trigger it and a clean one that must not (the negative control), as the 65816 tests
and the enhanced-chip checks do.

### 3. Budgets and the profiler

- **The profiler:**
  - CPU cycles per frame;
  - cycles per function (from the symbols: inclusive and exclusive);
  - lag frames;
  - vblank time spent on uploads;
  - DMA bytes per frame;
  - named timers from the debug port.

  It replaces the hand-written Lua profilers.
- **Budgets** in `project.json`, for example: no lag frame in normal play, worst frame under 95%, 2 KB free per ROM bank, work RAM / VRAM /
  sound RAM / HRAM headroom.
  - Verify warns at 90% of a limit and fails past it.
  - The report shows the trend since the previous build.

### 4. Movies, replay, diff and bisect

- **Our own movie format** for all consoles. It records the ROM and build, the cores, the power-on RAM, the start point (power-on,
  a state or "start at room"), every frame's input and the debug-port markers. Every few seconds it also stores a picture and RAM
  hash, so a replay knows the moment it goes off course.
  - Exports to `.fm2` (FCEUX) and `.bk2` (BizHawk), for cross-emulator replays.
  - Workshop already reads and writes `.fm2` (`Workshop/Tas`).
- **Divergence finder:** two runs (two builds, two cores, or BrokenNes against another emulator's trace) give the first frame,
  then the first instruction, where they differ.
- **State diff by symbol:** "these 6 variables differ", not a hex dump.
- **Movie tests:** a replay or bug capture we promote becomes a test in the game's `project.json`. `brokendev verify` replays
  it and checks its picture, chosen variables, budgets and findings.
- **Bisect** over the game's commits. Given a replay and a test ("any lag frame", "this variable", "this picture", "no crash"),
  BrokenDev builds the game halfway back in its history, replays and tests, halving toward the first bad commit: about 6 builds
  for 60 commits.
  - Commits that no longer build are skipped and reported.
  - When a replay goes off course in an older version (a different menu, say), bisect says "can't tell" instead of guessing.
  - BrokenNes stays at today's build.

### 5. Bug capture

- **One key** in the desktop app and Lite. The app always keeps the last **30 seconds**:
  - one save state per second;
  - every input;
  - a screenshot per second;
  - the audio.
- Pressing the key saves a bundle with:
  - the movie from the oldest state;
  - the screenshots and a `.wav`;
  - the ROM hash, build id, symbols, cores and settings;
  - an optional one-line note.
- **Where:**
  - a ROM that belongs to a BrokenDev project goes to `<game workspace>\bugs\<date_time>\`, outside git;
  - any other ROM goes to `%LOCALAPPDATA%\BrokenNes\bugs`;
  - Lite offers a `.zip` download.
- **Opening one:** `brokendev bugs [project]` lists them; `brokendev bug <id>` replays one headless and shows the last frame and
  the findings. The MCP server opens them too.
- **Cost:** a preallocated ring buffer with no allocation per frame. Lite must keep 60 fps with it on (measured).

### 6. The debug port (homebrew)

A few unused registers that a game writes to; BrokenNes acts on them. Other emulators and real hardware ignore the writes.

- **Print:** printf-style text and numbers into the log, the trace and the MCP results ("room 12 loaded, 3 enemies").
- **Assert:** `assert(hp <= max_hp)`. On failure the game pauses, showing the message and where it happened; a headless run records a finding instead.
- **Named timers:** start and stop by name. BrokenNes reports cycles per call, the worst case and per frame ("draw_hud").
- **Event markers:** "room entered", "boss phase 2", so timelines, profiles, chaos bots and movies know where the game is.
- **Libraries:**
  - NESFab (VRUN, Nested VRUN, SMB1 remake);
  - PVSnesLib (Super VRUN, WideMess);
  - GBDK (VRUN: Vaults of Obsolete Code).

  In a release build the calls compile to nothing (a `brokendev build` dev profile turns them on).
- **Registers:** an open question (below). Candidates:
  - the NES's unused `$401C-$401F`;
  - an unused SNES B-bus range;
  - on the Game Boy, the debug-message convention other Game Boy emulators already know, where it fits.

### 7. Viewers

- **Desktop windows first,** on the debugger engine, updating every frame:
  - **debugger:** breakpoints, disassembly with symbols, registers, step buttons, call stack, trace;
  - **memory:** live hex with symbols;
  - **tiles:** CHR / VRAM by palette;
  - **maps:** nametables and tile maps with the visible area outlined; on the SNES per layer and mode; on the Game Boy the background and window;
  - **sprites:** the OAM list and boxes on the picture;
  - **palettes**;
  - **event view:** register writes per scanline and dot, DMA / HDMA, interrupts;
  - **sound monitor:** channel registers, levels and waveforms, mute and solo per channel; the SNES's voices and sample RAM; the Game Boy's 4 channels.
- **Then web pages in BrokenDev**, fed by the MCP / HTTP connection, so they also work with Lite and on a phone.

### 8. The MCP server

A program in BrokenDev, written in C# with the official MCP SDK for .NET. It talks to Claude Code over stdio and is
**registered in the user-level Claude settings**, so every session in BrokenNes and the game repos has it.

- **Headless sessions:**
  - its own emulators, in process, on the Workshop engine;
  - many at once, no windows, no Mesen lock;
  - deterministic.
- **Live sessions:**
  - attach to a running desktop app by its PID (`%LOCALAPPDATA%\BrokenNes\instances\<pid>.json`) through the HTTP API, which gains the debugger routes;
  - **open read-only.** Control (pause, step, breakpoints, writes, state loads) needs a session opened for control, which an agent does only when the user asked in chat;
  - never touches saves or progression;
  - never stops a process.
- **Tools, grouped:**
  - **sessions:** open (a BrokenDev project or a ROM, console, cores), list, close;
  - **running:** run frames, run until (a breakpoint, a marker, a condition), pause, reset;
  - **input:** press, hold, play a movie;
  - **memory:** by address or symbol, by domain, search for a value;
  - **debugger:** breakpoints, step, call stack, registers, trace around a frame;
  - **picture:** screenshot (returned as an image), layers, tiles, sprites;
  - **sound:** channels, record N seconds;
  - **states:** save, load, diff by symbol;
  - **analysis:** checks, profile;
  - **BrokenDev:** build, verify, compat, run, bugs, bisect, coverage.
- Results come back structured (JSON plus images), not as text to parse.
- **Tests:** a scripted MCP client in `BrokenDev/tests`.

### 9. Checkup, chaos bots and the coverage map

- **`brokendev checkup`** (name to confirm), **before every hand-off and on request.** For every game it runs:
  - build;
  - verify (checks, budgets, promoted movies);
  - compat on every emulator;
  - coverage.

  It reports what changed since the last checkup, and the dashboard is updated from it.
- **Chaos bots**, **only when asked:** `brokendev chaos <project> -Minutes N`.
  - Many headless runs in parallel, with random and biased input (holding directions, jump spam, menu mashing), starting from every room.
  - They look for:
    - crashes (a jammed CPU, a BRK loop, the PC somewhere it should never be);
    - softlocks (input changes but the picture and RAM do not);
    - lag spikes;
    - check findings;
    - debug-port asserts.
  - Each problem is saved as a replay, ready for bisect.
- **Coverage map:** which code ran across verify, the movies and chaos runs, by symbol:
  - functions never run;
  - branches never taken;
  - free space per ROM bank.

  One page per game.

### 10. Game Boy preparation: a hello world for VRUN: Vaults of Obsolete Code

- **GBDK-2020** in the BrokenDev toolchain, pinned with a sha256 like the emulators (the download is asked first, with its file, source and size).
- **`PROJECTS\VRUN_VOC_dev`** with its own git (local commits, never pushed) and a backup bundle.
- **The hello world:**
  - one dual-mode ROM: colours on a Game Boy Color, greys on an original;
  - a line of text, a sprite moved with the d-pad, a sound on A;
  - the debug port in use.
- **A BrokenDev project** (`projects/vrun-voc`) with build, verify, run, play and compat on:
  - the BrokenNes desktop app and Lite;
  - Mesen;
  - BizHawk (Gambatte and SameBoy).
- It is **the Game Boy test subject** for the debugger, the checks and the MCP server.
- The game itself has its own plan: `docs/projects/vrun-voc-plan.md` (written 2026-10-23). There this hello world is milestone V0,
  the skeleton, so it uses the game's final cartridge setup: MBC5, 1 MB ROM, 32 KB battery RAM, dual mode.

## How it fits BrokenNes

- **The engine** lives in the shared emulator code, with the debugger as a service of each console session (NES, SNES, Game
  Boy). The desktop app, Workshop and Lite use the same code.
- **The HTTP API** gains the memory domains of the SNES and Game Boy, and the debugger routes. Live MCP sessions use them.
- **Workshop** gains headless debug sessions, which the MCP server and BrokenDev use.
- **BrokenDev** gains:
  - checks and budgets in `verify`;
  - promoted movies;
  - `bisect`, `bugs` / `bug`, `chaos`, `checkup`, coverage;
  - the MCP server;
  - the GBDK toolchain and the Game Boy adapters for compat.
- **The FL plugin** is unchanged. Its 65 self-test checks still pass, and its chip indexes are untouched.
- **Tests:**
  - every golden hash unchanged with the debugger off;
  - the speed A/B;
  - FruityHost;
  - BrokenDev `run-tests`;
  - one test ROM and one negative control per check;
  - the MCP client test.
- **WideMess:** its spec v1 (milestone 1, before this plan) reserves the debug port registers and the check hooks, so this
  plan's tools cover WideMess as soon as it boots.

## Milestones

They follow WideMess milestone 1. Each one is merged, tested and pushed before the next starts. The dashboard is updated at each one.

| | Milestone | Done when |
|---|---|---|
| **D1** | **Debugger engine** | Breakpoints, mirror-aware watches, stepping, call stack, trace and symbols on the NES, SNES (65816 + SPC700) and Game Boy. Memory domains for all three in the HTTP API. Every golden unchanged and speed unchanged with it off. |
| **D2** | **Game Boy hello world** | GBDK-2020 installed (after approval). `VRUN_VOC_dev` builds a dual-mode hello world that runs on BrokenNes (desktop and Lite), Mesen and BizHawk, as a BrokenDev project. The D1 debugger reads its symbols. |
| **D3** | **MCP server** | Headless and live sessions with the tools above, registered at user level, used by an agent to find a planted bug in a test ROM without Mesen. The client test passes. |
| **D4** | **Checks, budgets, profiler** | The first catalogue for the three consoles, each rule with its test ROM and negative control. Accepted findings recorded for VRUN, Nested VRUN, Super VRUN, the SMB1 remake and the hello world. `verify` fails on new findings and over budget. |
| **D5** | **Movies, diff, bisect** | Our movie format with `.fm2` / `.bk2` export, divergence finder, state diff by symbol, step back, promoted movie tests in verify, bisect over a game's commits (proven on a planted regression). |
| **D6** | **Bug capture** | The key in the desktop app and Lite (download). Bundles land in the game's `bugs\`; `brokendev bug` replays them exactly. Lite still holds 60 fps with it on. |
| **D7** | **Debug port** | Print, assert, timers and markers on the three consoles, with NESFab, PVSnesLib and GBDK libraries, stripped from release builds. Other emulators run the dev builds unharmed. |
| **D8** | **Viewers** | The desktop windows (debugger, memory, tiles, maps, sprites, palettes, event view, sound monitor), then the BrokenDev web pages. |
| **D9** | **Checkup, chaos, coverage** | `brokendev checkup` over every game, `brokendev chaos`, the coverage pages. |
| next | **VRUN: Vaults of Obsolete Code** | Its own plan: `docs/projects/vrun-voc-plan.md`. |

## Open questions (to settle at the start of the track)

1. The debug port's registers on each console, checked against mapper 30, the SNES coprocessors' ranges and the Game Boy Color's
   registers, reusing a convention other emulators already show where one exists.
2. The movie format's name and file extension.
3. The bug-capture key and how the note is typed.
4. The first check catalogue per console, and which findings today's games accept (for example Super VRUN's SNESMOD sound-RAM reads).
5. The checkup command's name and where its report lives (a BrokenDev page or a dashboard).
6. Approvals when the work reaches them: the MCP SDK's NuGet package and the GBDK-2020 download (exact files, sources and sizes asked then).
