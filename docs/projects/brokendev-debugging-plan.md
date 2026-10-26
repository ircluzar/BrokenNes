# BrokenDev debugging toolkit: a debugger, hardware checks and an MCP server

Plan of 2026-10-23, written after an interview with the user. It starts **after WideMess milestone 1** (the week of 2026-10-26).
**Milestone D1 (the debugger engine) is built** (2026-10-24): see "D1 as built" after the milestones. **Milestone D4 (checks, budgets,
profiler) is built** (2026-10-24): see "D4 as built". **Milestone D2 (the Game Boy hello world) is built** (2026-10-24): see "D2 as built";
its accepted findings list is empty. **Milestone D3 (the MCP server) is built** (2026-10-24): see "D3 as built". **Milestone D5 (movies, diff, bisect) is built** (2026-10-25):
see "D5 as built"; the movie format's name is a working name ("BrokenNes movie", `.bnm`: open question 2). **Milestone D6 (bug capture) is built**
(2026-10-25): see "D6 settled" and "D6 as built"; the key is F9 for now (open question 3). The rest is not.

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
| **D2** | **Game Boy hello world** | GBDK-2020 installed (after approval). `VRUN_VOC_dev` builds a dual-mode hello world that runs on BrokenNes (desktop and Lite), Mesen and BizHawk, as a BrokenDev project. The D1 debugger reads its symbols. **Built 2026-10-24** ("D2 as built"): GBDK-2020 4.5.0 pinned; project `vrun-voc` verifies (0 findings, every budget in) and works on BrokenNes, Mesen, BizHawk Gambatte and SameBoy; desktop and Lite proven; the debug port is a placeholder until D7. |
| **D3** | **MCP server** | Headless and live sessions with the tools above, registered at user level, used by an agent to find a planted bug in a test ROM without Mesen. The client test passes. **Built 2026-10-24** (see "D3 as built"). |
| **D4** | **Checks, budgets, profiler** | The first catalogue for the three consoles, each rule with its test ROM and negative control. Accepted findings recorded for VRUN, Nested VRUN, Super VRUN, the SMB1 remake and the hello world. `verify` fails on new findings and over budget. |
| **D5** | **Movies, diff, bisect** | Our movie format with `.fm2` / `.bk2` export, divergence finder, state diff by symbol, step back, promoted movie tests in verify, bisect over a game's commits (proven on a planted regression). **Built 2026-10-25** (see "D5 as built"): the format's working name "BrokenNes movie" (`.bnm`); exports played by BizHawk and FCEUX exactly as the same input; bisect found the planted regression in a 17-commit throwaway game; VRUN's first promoted movie. |
| **D6** | **Bug capture** | The key in the desktop app and Lite (download). Bundles land in the game's `bugs\`; `brokendev bug` replays them exactly. Lite still holds 60 fps with it on. **Built 2026-10-25** (see "D6 as built"): F9 and the note box in the desktop app, the Bug button's zip in Lite; every capture of the tests (VRUN, Super VRUN, the Game Boy hello world, the test ROMs) replays exactly; Lite 172 fps recording; `bugs` checks every workspace keeps them out of git. |
| **D7** | **Debug port** | Print, assert, timers and markers on the three consoles, with NESFab, PVSnesLib and GBDK libraries, stripped from release builds. Other emulators run the dev builds unharmed. |
| **D8** | **Viewers** | The desktop windows (debugger, memory, tiles, maps, sprites, palettes, event view, sound monitor), then the BrokenDev web pages. |
| **D9** | **Checkup, chaos, coverage** | `brokendev checkup` over every game, `brokendev chaos`, the coverage pages. |
| next | **VRUN: Vaults of Obsolete Code** | Its own plan: `docs/projects/vrun-voc-plan.md`. |

## D1 as built (2026-10-24)

- **Engine:** `Windows/NesEmulator/debug` (compiled by the desktop app and Workshop; the plugin, Lite, Core and the benches compile only
  the hooks). `Debugger` (breakpoints, watches, tracepoints, conditions, hit counts, stepping, call stack, trace ring, trace files,
  symbols, variables by name), one `DebugMachine` per console (`NesDebugMachine`, `SnesDebugMachine` with the 65816 and the SPC700, also
  the WideMess, `GbDebugMachine`), disassemblers for the 6502, 65816, SPC700 and SM83, `DebugRunner` (the headless driver D3 builds on).
- **Mirrors** are canonical keys: every access is folded to the place it reaches (NES RAM mirrors, PRG ROM by offset; SNES work RAM
  through every system bank and the `$2180` port, registers through every system bank, ROM / SRAM through every mirror, VRAM / CGRAM /
  OAM through their ports; Game Boy echo RAM, ROM / RAM / VRAM / work RAM by bank). DMA, HDMA (and its table fetches), NES OAM DMA,
  Game Boy OAM and VRAM DMA are accesses with their source.
- **Breaking parks the emulation thread** at an instruction boundary (a watch breaks after the instruction that made the access); the
  run loops are untouched, so a game runs frame-identically with the debugger attached and breaking (checked on every console and on
  VRUN and Super VRUN).
- **Zero cost when off:** the hooks are fields that stay null until something needs them (`NES.DebugHook`, `Bus.DebugTap` on the
  NES bus's slow path, `BOARD_SFC.DebugHook` / `DebugTap` (DMA, HDMA), `BOARD_GB.DebugHook` / `DebugTap` (OAM / VRAM DMA)). The SNES
  and Game Boy CPUs' own accesses are watched by a bus the debugger interposes in front of the CPU (`CPU_SFC.DebugInterpose`,
  `CPU_GB.DebugInterpose`), so the boards' `Read` / `Write`, which the JIT inlines into the CPUs, are byte for byte as before. The SPC700
  goes through the existing `APU_SFC.SmpHook`, its watches predicted from each instruction's operands, so the sound path is unchanged.
- **Workshop:** `--debug-test` (the conformance suite: test ROMs per console assembled in `Workshop/Debug/DebugTestRoms.cs`, a negative
  control per check, the real games when present), `--debug --rom <file> [--sym <file>] --do "break sub2; run; stack; trace 20"`.
- **HTTP:** `/api/memory/*` answer for every console; `/api/debug/*` (reading always, control behind `enableDebuggerControl`), listed in
  `Windows/webapi/README.md`; `UAT\entrypoints\debugger-desktop-smoke.ps1` drives them in the real app with a scratch config.
- **Left for later milestones:** the MCP server on these routes and on `DebugRunner` (D3); step back (D5, from save states and
  replay); the checks, profiler and coverage built on the same hooks (D4, D9); the windows (D8). Lite does not wire the debugger
  (single-threaded WASM cannot park a thread: Lite's debugging comes through D3's web pages).

## D4 settled (2026-10-24): the first check catalogue

Settled before coding milestone D4. Every rule has an id, a test ROM that must trigger it and a clean one that must not. A finding names
the rule, the first frame, the PC with its symbol, the address with its symbol where the rule is about memory, and how many times it
happened. Findings are keyed by (rule, place): the place is the address (memory rules) or the function the PC is in (code rules), so a
game accepts one bug once, by symbol, and a rebuild that moves addresses keeps its acceptances.

**How the checks see the machine.** They are an observer of the D1 engine: they arm the same hooks (none when off, so goldens and speed are
unchanged) and run only in headless check runs (Workshop `--checks`, `--check-test`, `brokendev verify`). A check run is a second pass of a
verify scenario, so the scenario's own hashes and speed are never measured with the checks on, and the checked pass must end on the same
picture (proof the checks did not disturb the game).

**Lag frames** (all three consoles): a frame is lagged when vblank arrives while the main program is still working. "Waiting" is a
spin loop (a backward jump to the same place with no RAM written in the loop, the stack aside), WAI (SNES), HALT (Game Boy), or a symbol the
manifest names (`checks.frameWait`). Interrupt handlers do not count as waiting or working for the main program.

### NES (11 rules)

| Rule | What it reports |
|---|---|
| `nes.ram-uninit` | Work RAM (`$0000-$07FF`) read before it was ever written: the CPU's real reads (its dummy reads excluded, they are not used) and OAM DMA's reads. |
| `nes.ppu-render-write` | `$2007` read or written, `$2003` / `$2004` written, while the PPU renders (background or sprites on, lines 0-239 and the pre-render line). `$2006` alone is the documented scroll-split technique and is not reported. |
| `nes.oam-dma-render` | OAM DMA (`$4014`) started while the PPU renders, or late enough in vblank to run into the pre-render line. |
| `nes.frame-irq` | An IRQ taken while the APU frame counter's IRQ is raised, whose handler returns (RTI) without acknowledging it (`$4015` read): the SMB3 red clouds. |
| `nes.dmc-conflict` | `$4016` / `$4017` / `$2007` read while a DMC sample plays (a DMC DMA can land on the read on hardware); the detail counts the reads a DMA actually hit in the run. |
| `nes.unofficial-opcode` | An unofficial (illegal) opcode executed. The place is the opcode ("$AB LAX #imm"), not the function: a game accepts each opcode it uses on purpose, and the detail says how stable it is on hardware (the highly unstable `$8B` / `$AB` with the operands seen). |
| `nes.open-bus` | A read of a write-only register (`$2000`, `$2001`, `$2003`, `$2005`, `$2006`, `$4000-$4014`) or of an address no device answers (`$4018-$5FFF`, `$6000-$7FFF` without PRG RAM: the mapper decides). |
| `nes.stack-wrap` | The stack pointer wraps (`$00` to `$FF` on a push, `$FF` to `$00` on a pull). |
| `nes.stack-overflow` | The stack grows onto a page-1 byte the game read as data while it lay below the stack pointer and the stack had never used it. |
| `nes.lag-frame` | A lag frame; the place is the function the main program was in. Where a frame runs out moves with every build, so a game accepts its lag frames as a rule and gates them with the `lagFrames` budget. |
| `nes.flash-write` | A mapper-30 flash command (a write to `$8000-$BFFF` on the self-flashing board); the place is the function that wrote, so a game accepts its save routine and anything else is new. |

### SNES and WideMess (14 rules)

| Rule | What it reports |
|---|---|
| `snes.wram-uninit` | Work RAM read before written (the CPU, DMA and the `$2180` port; WideMess: also its 896 KB). |
| `snes.vram-uninit` | VRAM read before written: through `$2139`/`$213A` or DMA, and by the picture (below). |
| `snes.cgram-uninit` | Palette read before written: through `$213B` or DMA, and by the picture (the backdrop colour, and the colours drawn tiles use). |
| `snes.oam-uninit` | OAM read before written: through `$2138` or DMA, and by the picture (all 544 bytes once sprites are on a screen: the PPU scans them all). |
| `snes.dma-hdma` | An HDMA transfer (or table fetch) during a general DMA: what freezes the first 5A22 revision. |
| `snes.hdma-midframe` | An HDMA channel switched on mid-picture (after the line-0 init, before vblank): it starts from stale table pointers. |
| `snes.ppu-write-active` | VRAM, CGRAM or OAM written during the picture without forced blank (dropped, or landing at the address the PPU is reading), by the CPU or by a DMA started outside vblank. HDMA to CGRAM lands in h-blank and is fine. |
| `snes.dma-overrun` | A DMA started in vblank whose VRAM / CGRAM / OAM bytes run into the picture. |
| `snes.muldiv-early` | `$4214-$4217` read fewer than 8 CPU cycles after a multiply (`$4203`) or 16 after a divide (`$4206`). |
| `snes.spc-handshake` | The 65816 polls an APU port (`$2140-$2143`) from one place for a second or more without the value changing: the SPC700 never answered. |
| `snes.echo-overrun` | With echo writes on (FLG bit 5 clear), the SPC700 runs code or reads data inside the echo buffer (`ESA` x 256, `EDL` x 2 KB, at least 4 bytes), or a playing voice's samples or sample directory lie there. |
| `snes.stack-overflow` | The 65816 stack grows onto a byte the game read as data while it lay below the stack pointer and the stack had never used it (data written inside the live stack is not a finding: compiled code keeps its locals there). |
| `snes.open-bus` | A read of a write-only register or an address nothing answers. The WideMess detection read of `$4221` (the spec's protocol) is not reported; on the WideMess console `$4220-$423F` are registers. |
| `snes.lag-frame` | A lag frame (WAI counts as waiting). |

**The picture's own reads** (VRAM, CGRAM, OAM) are checked once per frame, at the start of vblank, from the registers then: the visible
tile-map entries of every background on the main or sub screen, the tiles they name, the colours those tiles draw with, the on-screen
sprites' tiles. This is a model of the PPU's fetches, not a hook in the renderer (the hottest code there is): **modes 0-4 only** (modes 5-7
and the offset-per-tile tables are not modelled), and **HDMA-driven per-line register changes are not followed** (the end-of-picture
registers stand for the frame). On the WideMess console the picture model is off (its 512 KB VRAM and 8-layer mode are not modelled); the
port and DMA reads are still checked.

**WideMess debug port:** writes to `$4230-$423F` reach a reserved hook in the checks (counted, decoded by D7), never an open-bus finding.

### Game Boy and Game Boy Color (12 rules)

| Rule | What it reports |
|---|---|
| `gb.ram-uninit` | Work RAM or HRAM read before written; OAM never written when sprites are shown (LCD and OBJ on: the PPU scans all 40 entries). |
| `gb.vram-mode3` | VRAM written or read by the CPU in mode 3 with the LCD on (the write is dropped, the read gives `$FF`). |
| `gb.oam-mode23` | OAM written or read by the CPU in modes 2-3 with the LCD on, or during OAM DMA. |
| `gb.oam-dma-hram` | During OAM DMA the CPU touches memory outside HRAM and I/O (an instruction fetched from ROM or work RAM, a stack in work RAM): the bus belongs to the DMA. |
| `gb.lcd-off` | The LCD switched off outside vblank (it can damage an original Game Boy). |
| `gb.unusable` | `$FEA0-$FEFF` read or written. |
| `gb.cgb-palette-mode3` | Game Boy Color palette data (`$FF69`/`$FF6B`) written or read in mode 3. |
| `gb.speed-switch` | KEY1 armed for a speed switch without a STOP to perform it, or STOP with IE not `$00` or JOYP not `$30`. |
| `gb.cart-ram-disabled` | `$A000-$BFFF` read or written while the cartridge RAM is disabled, or on a cartridge without RAM. |
| `gb.halt-bug` | HALT with IME clear and an interrupt pending (the next byte is read twice); EI then HALT is not the bug. |
| `gb.stack-overflow` | The stack grows onto a byte the game read as data while it lay below SP and the stack had never used it. |
| `gb.lag-frame` | A lag frame (HALT counts as waiting). |

### Not built, and why

- **The picture chip's reads on the NES** (CHR or nametables never written): the NES boots with CHR ROM or with CHR RAM a game must fill
  before showing it, and every one of our NES games clears its nametables; a renderer hook for it would sit in the hottest NES code.
- **Exact DMC collisions as the only test**: a collision depends on where the DMC's timer starts at power-on, which differs between consoles
  and runs, so the strict rule reports every read that *can* collide and the run's actual collisions are only counted.
- **"HDMA just after a DMA ends"** (the other trigger of the 5A22 v1 freeze): its window is not documented to a cycle, so only an HDMA
  during the DMA is reported.

## D4 as built (2026-10-24)

- **Engine:** `Windows/NesEmulator/debug/checks` (compiled by the desktop app and Workshop, like D1). The checks are a `DebugObserver` of the
  D1 `Debugger`: it arms the hooks they ask for and calls them before its breakpoint matching (D1 gained observers and four events: a write
  after it landed, the 65816's idle cycles, call frames pushed and popped, the NES DMC halt). `ConsoleChecks` (findings by rule and place,
  frames and lag frames, the profiler, stack collisions), `NesChecks`, `SnesChecks` (also the WideMess, with the reserved debug-port hook at
  `$4230-$423F`), `GbChecks`, `CheckRun` (attach, the profile summary, the report, ROM bank free space).
- **Zero cost when off:** the boards gained read-only accessors that nothing calls with the checks off, and `Bus.PreciseTick` one null test
  inside its rare DMC-halt branch. The ALttP golden is IDENTICAL, SMW's result is unchanged (video `e0fe6fe55bd52156`, audio
  `3d7b8c7da7a360eb`), `--debug-test` 101 of 101, and the speed A/B is within noise (below).
- **The catalogue as built:** the 37 rules of "D4 settled" (11 NES, 14 SNES and WideMess, 12 Game Boy), none dropped. Not built: the NES
  picture chip's own reads, exact-only DMC collisions, the "HDMA just after a DMA" freeze (reasons in "D4 settled").
- **Workshop:** `--check-test` (109 checks: every rule's test ROM triggers it, naming the rule, the first frame, the PC and its symbol, and its
  negative control stays silent; the clean templates break no rule; the 6502's dummy reads are not reads; the profiler's busy time, lag
  frames, DMA bytes and vblank use; checks on frame-identical to checks off on the NES, SNES, WideMess, Game Boy, VRUN and Super VRUN),
  `--checks` and `--profile` (`--rom --sym --frames --input --wait --frames-csv --json`), which replace the hand-written Lua profilers.
- **verify:** a manifest's `checks` section runs every (or the listed) scenario a second time with the checks on; anything not in
  `checks.accepted` fails; `budgets` fail over and warn near; the report has the previous build's values (`out/<id>/budget-history.json`).
  Cost with the checks on: VRUN x1.5, Super VRUN x4.5 (114 checked frames a second); a project's verify takes 20 to 90 s more.
- **The user's games, today's findings (all accepted, with reasons, in their manifests):**
  - VRUN: lag frames only (boot; the first room load; 5 frames at a room change). Budgets: lag 5 of 8 in play from frame 130, the busiest
    frame 58.5% of 95%.
  - The SMB1 remake: 14 unofficial opcodes (NESFab's unofficial ISA), among them the **highly unstable `$AB` LAX #imm, used only with
    `#$00`** (safe as used); lag at boot and 4 frames per level reload (12 in play from frame 300, budget 16).
  - Super VRUN: lag while booting and loading, **0 in play** (its own rule; budget 0); one unverified picture finding (BG1 draws colour 27,
    never written, during the title-to-game fade). No work RAM is read before it is written in today's build (October's 61 were fixed or seen by a Mesen script that missed DMA writes).
  - Nested VRUN and its enhanced build: the Nested engine's reset does **`ROL $420D` on a write-only register** (the FastROM bit it writes
    back is open-bus luck); one read of INIDISP in play; 0 lag in play once its frame wait (`$80A739`) is declared.
  - The SMB1 Nested build: the same `ROL $420D`; 382 SNES vblanks outside the engine's frame wait in play (unverified, not budgeted).
  - The WideMess sample: **8 variables read before written: the devkit's start-up does not clear work RAM** (BrokenNes powers it on as
    zeros; a WideMess powers on as a SNES); a game made from the template inherits it. **Fixed 2026-10-24:** the devkit's RESET now
    DMA-clears the 128 KB and the far work-RAM sections before crt0 (`BrokenDev/widemess/lib/wmsboot.asm`, widemess-devkit.md "The
    start-up clears work RAM"); 0 findings, the 8 acceptances removed, `-D WMS_NOCLEAR` is the negative control (`--check-test`, group wms).
- **Left open:** the Game Boy hello world's accepted list (D2); named timers in the profile (D7's debug port); the SNES picture model's
  limits (modes 0-4, no HDMA); RAM headroom by allocation from the symbols (today: bytes read, a lower bound of use); the WideMess's VRAM and
  sound RAM headroom; where the SMB1 Nested build waits.

## D2 as built (2026-10-24)

- **The toolchain:** GBDK-2020 4.5.0, downloaded with the user's approval, unpacked at `P:\BrokenDev\env\toolchains\gbdk\4.5.0\gbdk`.
  `BrokenDev/toolchains.json` pins it like the emulators: the zip (`toolchains/gbdk/_downloads/gbdk-win64-4.5.0.zip`, 8,636,233 bytes, sha256
  `266854ce...5c890`), ten key files by size and sha256 (lcc, sdcc, sdasgb, sdldgb, makebin, romusage, crt0, the libraries, gb.h) and the
  ChangeLog's version line. `lib/Toolchains.ps1` checks it (`doctor`, a `toolchains` verb, `requires.tools` `toolchain:gbdk`, the build
  placeholder `{toolchain:gbdk}`); BrokenDev never downloads or unpacks one.
- **The workspace:** `PROJECTS\VRUN_VOC_dev`, its own git (local commits only, no remote), a README, a backup bundle
  (`P:\BrokenDev\env\backups\VRUN_VOC_dev-2026-10-24.bundle`). `build.ps1` (lcc on `src/*.c` and
  `src/*.s`; makebin `-yc -yt0x1B -yo64 -ya4 -yS`; it checks the header: `$80` dual mode, `$1B` MBC5+RAM+battery, `$05` 1 MB, `$03` 32 KB) writes
  `build/vrun_voc.gbc` with `.noi`, `.sym` (bgb format) and `.map`.
- **The hello world** (`src/main.c`): a few lines of text in its own 5 x 7 font (GBDK's console is not used: its font loader switches the LCD
  on mid-start-up, which made lag frames), an `@` sprite moved with the d-pad (`move_player`), an 880 Hz beep on A (`play_beep`), a
  battery-RAM boot counter with a magic and a checksum (`count_boot`), colours on a Color and four shades on an original (`is_color`).
  **Clean start-up:** GBDK's crt0 zeroes the C variables' area (`_DATA`) but, since 4.x, not the rest of work RAM; `src/start_up.s` clears the
  rest (to `$DEFF`, and banks 2-7 on a Color with interrupts off and no stack use while another bank is in). The screen is built with the LCD
  off; the LCD goes off only in vblank (GBDK's `DISPLAY_OFF`); the cartridge RAM is enabled only around its use.
- **The debug port:** `src/debug.h` is a placeholder (print, assert, timers, markers as macros that compile to nothing): D7 has not defined
  the port. GBDK 4.5.0 ships `gbdk/emu_debug.h` (the bgb / Emulicious message convention), a candidate for open question 1.
- **BrokenDev project `vrun-voc`** (kind `gb`, console `gbc`, source `{live:VRUN_VOC_dev}`: a new placeholder for a game's own live
  workspace found beside the repository, `BROKENDEV_LIVE_VRUN_VOC_DEV` overrides it). **verify** (Workshop `--brokendev verify`, which now runs
  the Game Boy): `color` (5 colours, the sprite moves 60 + 40 pixels, two beeps, boot 1), `original` (the same cartridge on a Game Boy:
  `"console": "gb"` on a scenario, at most 4 colours), `idle` (no sound, no movement), `second-boot` and `third-boot-original`
  (`"batteryFrom"`: a power-on with the save an earlier scenario left; boot 2, then 3 on the other model), all checked by symbol
  (`expect.memory`); the hardware checks: **0 findings, nothing accepted**; budgets: 0 lag frames from frame 0, the busiest frame 2.9%, 13.5 KB
  free in bank 0, 8 KB of work RAM and 113 bytes of HRAM never read, 7.9 KB of stack. **compat:** BrokenNes, Mesen 2.1.1 (its Game Boy Color
  model), BizHawk 2.11.1 Gambatte and SameBoy (new registry entries; the Mesen and BizHawk adapters learned the Game Boy) all **work**: the
  sprite at x 144, one beep, boot 1 then 2 from each emulator's own battery file, pictures structurally identical to BrokenNes's; each
  starts with its own power-on RAM (Mesen random, SameBoy pseudo-random, Gambatte a fixed fill) and SameBoy plays its boot animation (189
  frames) first. **run / play:** the desktop app (`run`), Mesen and BizHawk (`play -Emu ...`).
- **The D1 debugger on its symbols:** `Workshop --debug --rom vrun_voc.gbc --sym vrun_voc.noi --do "break move_player; ..."` hits at frame 9;
  `watch player_x w` stays silent for 120 idle frames and fires once `pad Right` holds the d-pad (84, 85, 86); `break play_beep` hits on A.
  `--debug` gained `pad <buttons>`, and a C name finds SDCC's `_name` symbol.
- **The desktop app** (`UAT/entrypoints/vrun-voc-desktop-smoke.ps1`, HTTP API, a scratch `BROKENNES_CONFIG_DIR` and the new
  `BROKENNES_SAVES_DIR` test hook for the console battery saves): colour on the Color, Right moves the sprite, A beeps (NR52 channel 1 on), the
  save written on close gives boot 2, the Game Boy console shows 3 shades and boot 3; the user's `%APPDATA%\BrokenNes` is unchanged (names,
  sizes, times). **Lite** (`UAT/lite/test-lite-vrun-voc.mjs`, the AOT build in Edge): the Color console in colour, Right moves the sprite, A
  sounds, the IndexedDB battery save (`VOC0`, boot 1, then 2 in a fresh page), 3 shades on the Game Boy console.
- **One change to D4:** a Game Boy frame with the LCD off is no lag frame (the hardware has no vblank then); `--check-test` still all pass (109, and 111 of 111 with main's two new checks).
- **Tests:** BrokenDev `run-tests` gains the toolchain registry and state (with negative controls), the Game Boy manifest, `{live:}`, GBDK
  symbols and specs, the registry's Game Boy columns, BizHawk's Game Boy config, and the hello world end to end with a planted read before the
  RAM clear (`build.ps1 -Define VOC_PLANT_UNINIT`) that must fail verify with a new `gb.ram-uninit`.
- **Left for the game (the VOC plan's V1 on):** the debug port (D7), the real character set and tile pairs, banked code and data (only bank 0
  is used), the music and sound-effect drivers, the suspend save, the flash cartridge test.

## D3 as built (2026-10-24)

- **The server:** `BrokenDev/mcp` (`BrokenNes.Mcp.exe`, a console program), C# on the official MCP SDK for .NET, **`ModelContextProtocol`
  2.2.0, pinned** (approved by the user; NuGet pulled `ModelContextProtocol.Core` 2.2.0 and `Microsoft.Extensions.AI.Abstractions` 10.8.3 /
  `Microsoft.Extensions.*.Abstractions` 10.0.10 with it, nothing else). Stdio, tools listed and called through the SDK's low-level handlers
  (each tool's JSON schema written by hand), results as JSON text plus the same object as structured content, pictures as PNG images.
  Stdout belongs to the protocol: anything the emulator prints goes to stderr.
- **Headless sessions** run on the Workshop engine in the server's own process (a project reference to Workshop: the very same emulator
  build): the NES (the desktop app's FIX cores by default), every console session (SNES, WideMess, Game Boy / Color, Master System, Game Gear,
  Genesis) with their core menus; the D1 `Debugger` and `DebugRunner` (frames on their own thread, so a break parks mid-frame) for the
  NES, SNES with the SPC700, WideMess and Game Boy; the Sega consoles run frames, input, memory domains, the picture, sound and states with
  no debugger (said up front, and the debugger's tools refuse with the reason). Sessions are deterministic (two sessions given the same calls
  hold the same RAM; one more frame differs: the client test checks both).
- **Live sessions** attach by PID through the discovery file and the HTTP API. They open **read-only** (registers, memory by spec or
  domain, the call stack, the trace ring, breakpoints and hits, disassembly, expressions, the screenshot). **Control** (pause, continue, run
  frames, run until, step, breakpoints, symbol loads, memory and register writes, buttons, loading the quick state) needs both the session
  opened with `control: true` (the tool says: only when the user asked in chat) and the app's `enableDebuggerControl`; the server checks
  both before every control call, so even the app's ungated routes (`/api/memory/poke*`, `/api/input/*`) are never reached from a read-only
  session. Never: saves, resets, progression, stopping the process (closing a control session detaches the debugger: the game runs on).
- **The tools (41):** sessions (`open_rom`, `open_project`, `list_instances`, `attach_live`, `list_sessions`, `status`, `close_session`);
  running (`run` with an optional pad script, `run_until` a frame / scanline / address access with a condition / a condition per frame,
  `pause`, `continue`, `reset`); `input` (press, hold, release, script); memory (`read_memory`, `write_memory`, `search_memory`,
  `list_domains`, `symbols`); the debugger (`breakpoint_add` / `_list` / `_remove`, `watch` expressions returned with every result, `step`,
  `call_stack`, `registers`, `set_register`, `trace`, `trace_frame`, `disassemble` by address or code symbol, `evaluate`); the picture
  (`screenshot`, `layers` for the SNES and WideMess: one frame drawn with the chosen layers and the machine put back by a state, `tiles`,
  `sprites`); `record_sound` (levels, frames with sound, a .wav); states in the server's memory (`save_state`, `load_state`, `diff_states` by
  symbol, NESFab's `name@n` bytes as one variable); `checks` (start, report, stop) and `profile`; `brokendev` (build, verify, compat, run with
  the new PID returned, list, status, doctor). The call stack is innermost first in both kinds of session.
- **Registration:** `brokendev.ps1 mcp install` builds the server into `BROKENDEV_HOME\bnbuild\mcp` and runs Claude Code's own
  `claude mcp add --scope user brokennes -- <exe> --repo <main checkout>` (the CLI on PATH, else the one the Claude desktop app keeps under
  `%APPDATA%\Claude\claude-code`; with no CLI it writes the documented `mcpServers.brokennes` entry of `%USERPROFILE%\.claude.json` and
  nothing else); `mcp uninstall` removes that one entry; `mcp status`, `mcp test`. Registered on the user's machine on 2026-10-24
  with `brokendev.ps1 mcp install`: the CLI (`%APPDATA%\Claude\claude-code\2.1.308\...\claude.exe`) wrote the one `mcpServers.brokennes`
  entry of `%USERPROFILE%\.claude.json` (`type stdio`, command `P:\BrokenDev\env\bnbuild\mcp\bin\BrokenNes.Mcp\release\BrokenNes.Mcp.exe`,
  args `--repo C:\Users\philt\OneDrive\Documents\PROJECTS\BrokenNes`); `claude mcp get brokennes` says connected.
- **Tests:** `BrokenNes.Mcp.exe --client-test` (a scripted MCP client over stdio with the SDK's client: 60 checks, among them the
  negative controls: an unknown symbol, a past frame, an unknown state, a closed session, a Sega breakpoint, checks that are off, an unknown
  button, an unknown project are refused; the silent ROM records silence; a session one frame apart differs); `BrokenDev\tests\mcp-live.ps1`
  (two desktop apps with scratch `BROKENNES_CONFIG_DIR`s, one without and one with `enableDebuggerControl`: 15 checks: reading works
  everywhere; a read-only session refuses pause, writes, breakpoints, even where the app allows control; a control session where the app
  forbids it is refused by the app; a control session where both allow it breaks on a symbol, shows the call stack, steps, writes, presses A;
  closing it lets the game go and the process keeps running; nothing saves); `run-tests.ps1` runs the client test and the demo when given
  `-McpExe` (or `BROKENDEV_MCP`).
- **The "done when" demo** (`BrokenNes.Mcp.exe --demo`, a scripted agent session, no Mesen): `PlantedBug.cs` assembles a small NES game with
  two planted bugs and its fixed build. Told only "the game ends too early" and given the game's `.mlb`, the tools find:
  1. **a variable read before it is written:** `open_rom` with `checks: true`, `run` 300, `checks report` names `nes.ram-uninit` at
     `bonus` (`ram:$0042`), first frame 59, in `add_bonus`, stack `update > add_bonus`. A second, identical session with a read watch on
     `bonus` (`hits ==1`) stops at that read (value `$00`, the power-on RAM), stack `add_bonus <- update`; a third with a write watch shows the
     first write is `add_bonus+$5`, 7 cycles after the read: nothing initialises it (the start-up forgot `STA bonus`).
  2. **a wrong branch:** a write watch on `game_over` with `value != 0` stops at frame 59 in `set_game_over+$2` with `lives` = 2; the call
     stack names `lose_life`; `disassemble lose_life` reads `DEC lives; BEQ alive; LDA #$01`, and the trace shows the `BEQ` not taken just
     before the store: `BEQ` where `BNE` was meant (the game ends at the first hit instead of at 0 lives).
  3. **the negative control:** the fixed build (with `STA bonus`, `BNE`) under the same tools: no game over after two hits (lives 1),
     0 read-before-written findings.
- **Unchanged when the server is not used:** the emulator, the desktop app and Lite gain nothing; Workshop gains one
  `InternalsVisibleTo BrokenNes.Mcp` (the tests reuse the debugger tests' assembler and ROMs). On the branch merged with main 166090e: UAT 125 pass, 0 fail, 16 skip, 3 info;
  `--debug-test` 101 of 101; `--check-test` 111 of 111 (main's count); `--wms-test` 288 passed, 0 failed; BrokenDev `run-tests` 124 passed
  (the MCP case included); Core and Lite build; the client test 60 of 60, the demo 6 of 6, the live test 15 of 15.
- **Left open:** live sessions have no checks, sound recorder, RAM search, tiles / sprites / layers or whole-frame trace (the desktop app's
  API has no routes for them: they run in a headless session of the same ROM); `bugs`, `bisect`, `coverage` and movie playback arrive with
  D5, D6 and D9; SNES coprocessor firmware is not loaded in headless sessions (DSP-1 games use the homemade chip, as in the debugger tests);
  the SNES sprite boxes assume 8 / 16 pixels (OBSEL is write-only); a state load restarts the call stack, and the checks keep what they
  learned before it.

## D5 as built (2026-10-25)

- **The movie format** (`Windows/NesEmulator/debug/movies/Movie.cs`, compiled by the desktop app and Workshop like the debugger): plain text,
  one fact per line. The ROM (name, sha256, size) and the build it was made with (`build commit <sha>` when the game's workspace is a git
  repository: bisect's default good commit), the BrokenNes build, the console and the cores, the NES's power-on RAM (`fceux`, `zeros`, `ones`)
  and frame timing (BrokenNes's 60 fps frames or NTSC's), the start (power-on, or a save state: gzip + base64), every frame's input for two pads
  as changes (`i 120 Start -`), and every 60 frames a keyframe (`k 60 <picture> <RAM> <course values>`): FNV-1a 64 of the picture (the hash
  verify's checkpoints use) and of the work RAM (every memory domain of kind Work), and the values of the movie's **course variables**
  (`var app_state [app_state]`, debugger expressions on the symbols). **The name and extension are a working name** (open question 2):
  "BrokenNes movie", `.bnm`, magic `bnmovie`; they are the constants of `MovieFormat` and nowhere else (BrokenDev asks the Workshop:
  `--movie format`), and the reader takes the magics of earlier names (`FormerMagics`), so a rename is one edit.
- **Workshop `--movie`:** `record` (a pad script for both pads, `--var` course variables, optional start state), `play` (in sync, or the first
  keyframe off course; another build of the ROM is said), `info`, `export --fm2 | --bk2 [--core]`, `import --fm2`, `diverge`, `test`
  (bisect's verdict), `format`, `build-planted` (the tests' planted game). `MovieMachine` opens the movie's console with its cores (a core the
  menu does not offer is refused), power-on and timing; the D1 debugger is attached with no hook armed (frame-identical, checked).
- **Exports, cross-checks only:** `.fm2` (FCEUX's layout `|0|RLDUTSBA|RLDUTSBA||`, the checksum as FCEUX writes it: base64 of the MD5 of PRG +
  CHR; `Fm2Writer` gained the base64 form and the port-2 field) and `.bk2` (BizHawk 2.11.1: Header with the SHA-1 BizHawk names, the
  controllers' own names from its `defctrl.json`; NesHawk, BSNESv115+, and SameBoy for the Game Boy by default: EmuHawk plays a **Gambatte**
  movie only with the Game Boy boot ROM, "Missing Firmware" even with the movie's sync settings saying EnableBIOS false, measured; BrokenNes has
  none and asks for none). A movie recorded with BrokenNes's 60 fps frames says on export that the others run NTSC frames. **Proven in the real
  emulators** (`BrokenDev/tests/movie-cross.ps1`, the compat spec's new `movie <file>` line: EmuHawk `--movie`, FCEUX `-playmovie`, the Lua sets
  no pad and notes what the movie pressed): VRUN on BizHawk NesHawk and on FCEUX, Super VRUN on BizHawk bsnes, the Game Boy hello world on
  BizHawk SameBoy each play the exported movie **exactly** as the same input given frame by frame (every compat variable, every frame: 900,
  900, 1200, 900 frames), press the movie's buttons at the movie's frames, and the negative control (the first press one frame late) differs.
- **The divergence finder** (`--movie diverge`, the MCP `movie` tool): the movie on two runs in lockstep (two builds, two cores, two frame
  timings) gives the first frame whose picture or work RAM differs; that frame is replayed on both from the newest snapshot with the trace on,
  giving the first instruction where the main CPU's runs part (the instruction's own bytes, the registers, the byte it read; else the first
  timing difference, scanline and dot; else "the CPU ran the same: the difference is elsewhere"), the other CPUs too (the SPC700), and the
  **state diff by symbol** before and after the frame. Against another emulator's BrokenDev compat run (`--against`): the frame shift that lines
  the boots up and the first differing frame and variables (instruction level needs both runs on BrokenNes; said). On the planted regression it
  names frame 252, `CMP #$01` against `CMP #$00` at `lose_life+$2`, and `lives` 1 against 0; on VRUN, FIX against FMC parts at the `BIT $2002`
  whose vblank flag differs.
- **The state diff by symbol** is one engine class (`SymbolDiff`): variables matched by name (two builds may place them differently), NESFab's
  `name@n` bytes one variable, then the bytes no symbol names per space. The MCP server's `diff_states` uses it now (same results).
- **Step back** (`DebugHistory`, shared): a state every 60 frames (the last 30), every frame's input since the session began (its movie), the
  keyframes. Back by instructions loads the newest state before the place and replays the logged input to the exact instruction
  (`Debugger.RunToInstructionCount`, counted on the per-instruction hook: the call stack tracking must be on), breakpoints and the checks off
  during the replay; back by frames ends between frames. Running on replays the same input; a new input rewrites the future. The NES's save
  state has no frame count, so its debug machine keeps an offset (`RestoreFrame`). MCP `step_back` (and the Sega consoles by frames: no
  debugger), Workshop `--debug` (`history; ... back 5; back frames 2`).
- **Promoted movies in verify** (only those): a manifest's `movies` section (`id`, `file` beside the manifest, `why`, `sync`, `expect`); verify
  replays each as a scenario `movie-<id>` (its input for both pads, power-on, timing; cores other than the recording's are refused) with
  `movie-in-sync` (every keyframe; `sync same-rom`: required on the recorded build, information on another), the entry's `expect` and the
  checks and budgets like any scenario. `brokendev.ps1 movie record|play|export|promote|list`; promote refuses a movie that starts from a state
  or does not replay in sync today, and edits the manifest as text (every other field unchanged, checked). **VRUN's first promoted movie:**
  `projects/vrun-nes/movies/first-room.bnm` (verify's play script, 2400 frames, course `app_state`, `current_room`); `verify vrun-nes` passes
  with it in sync (40 of 40 keyframes), its expectations (colours, sound, `app_state` 1 at the end), 0 new findings.
- **Bisect over a game's commits** (`brokendev.ps1 bisect <project> -Movie -Test [-Good] [-Bad] [-Repo]`, the MCP `bisect` tool): a scratch
  clone of the game's repository in `BROKENDEV_HOME\work\<id>\bisect` with its remote removed (it can never push; the user's folder is only
  read), each commit built with the manifest's steps moved into the clone (outputs to the bisect's own folder, never the project's `out`),
  the movie replayed and tested (`no-crash`, `no-lag [from N]`, `picture at N`, `<expression> at N`): good, bad, **skip** (no build) or
  **can't tell** (a course variable differs from the recording before the test frame). Endpoints that do not hold stop it with no answer.
  **The proof** (`BrokenDev/tests/bisect-proof.ps1`, run by `run-tests`): a throwaway 17-commit repository of the planted game (a typo that does
  not build at c09, a different menu at c10, the regression at c12): bisect names c12 in 7 tests / 6 builds (c01 good, c17 bad, c09 skip,
  c08 good, c12 bad, c10 can't tell, c11 good), the throwaway repository unchanged, the clone without a remote; the negative controls (a test that
  holds everywhere, a good commit after the regression) name no commit.
- **The MCP server** (45 tools): `step_back`, `open_movie` (a movie replayed in a new session, keyframes checked as it runs), `movie` (save a
  session's movie, info, play, export, diverge, test), `bisect`; headless sessions keep the history from their start.
- **Unchanged when not used:** no core and no board changed; the debugger's additions run only inside its armed hooks (a run-to-count test in
  `CheckStep`); the desktop app, Core, Lite and the plugin build. Results below.
- **Tests:** `--movie-test` (69 of 69: the format and its refusals, record / replay on the NES, SNES and Game Boy, the exports, the divergence
  finder, bisect's verdicts, step back on the three consoles, VRUN and Super VRUN; each with a negative control); the MCP client test
  (75 of 75, 15 new), the demo (6 of 6); BrokenDev `run-tests` (137 passed, 0 failed: the movie format from the Workshop, record + promote + verify with
  the late-Start movie failing it, the verbs through `brokendev.ps1`, the bisect proof); `tests/movie-cross.ps1` (12 checks on 4 emulators);
  `--debug-test` 101 of 101, `--check-test` 111 of 111, `--wms-test` 306 passed 0 failed, UAT 125 pass 0 fail 16 skip 3 info (the goldens unchanged), all on the branch merged with main 329f98d (WideMess M6 follow-up; the one conflict, `Workshop/Debug/DebugRunCli.cs`, resolved as a union); the desktop app, Core, Lite and the plugin build.
- **Left open:** the format's name (open question 2); the desktop app and Lite record no movies yet (D6's bug capture records them, on the same
  format and `DebugHistory`); movie resets (an `.fm2` with a reset is refused); headless MCP sessions run NTSC timing or another power-on RAM
  only through `movie play` (not `open_movie`); instruction-level divergence against another emulator (their compat runs record variables,
  not instructions); a real game's bisect (the user's game repositories: VRUN's history is in its archive; none has a known regression to find).

## D6 settled (2026-10-25): the bug bundle

Refined from section 5 before the code, measured on this machine:
- **The rolling buffer** (`Windows/NesEmulator/debug/capture`, shared by the desktop app, Lite and Workshop): every 60 frames a save state, a
  picture and the keyframe hashes of D5's movies; every frame the two pads (two bytes each); every sound sample the app queued (mono floats).
  32 slots, so a capture always holds at least 30 s. Buffers made once; between the states nothing is allocated (measured: 0 bytes over 590
  frames). Cost: a VRUN state is 745 KB and takes up to 10.5 ms once a second; a Super VRUN state 92 KB, 2.6 ms; armed against off is within
  noise on the planted game, VRUN and Super VRUN (Workshop, best of three, interleaved).
- **Breaks:** something outside the game (a state load, a reset, a corruption, a memory write through the API, a core swap or the NES's
  frame timing switched) means the frames before cannot replay into the ones after: the history starts again there, and the bundle says
  when and why. Core swaps and the timing are watched by the ring itself (once a second); the others are told by the app.
- **The bundle** (a folder on the desktop, the same files as one zip in Lite): `bug.json` (the note, when, the app, the ROM, the BrokenDev
  project, the cores and settings, how long and why it starts where it does), `capture.bnm` (a D5 movie from the oldest state: replays
  exactly), `rom/` (the very ROM: the bug replays after the game is rebuilt), `symbols/` (from the project's build record), `audio.wav`,
  `pictures/fNNNNNN.png` (one a second), `strip.png` (all of them at half size), `last.png`. A console without save states (a Game Boy on
  a foreign CPU) still gets a movie from its power-on while the history reaches it; after that the bundle has pictures and sound and says
  why there is no movie.
- **Where:** a ROM whose sha256 is the ROM of a successful BrokenDev build record goes to `<game workspace>\bugs\<yyyy-MM-dd_HHmmss>\`
  (the record's `bugs`, else its live workspace); any other ROM, and a project whose record names no workspace (VRUN's frozen build), to
  `%LOCALAPPDATA%\BrokenNes\bugs\` (said in the bundle); `BROKENNES_BUGS_DIR` replaces that folder for tests. Never `%APPDATA%\BrokenNes`.
- **Open question 3, answered for now:** the key is **F9** (F12 is the screenshot, F5 / F7 the quick states), the menu item is
  Emulator > Capture Bug (last 30 s); the note is typed in a small box after the key (the game paused
  meanwhile; Enter saves, an empty note is fine, Esc discards the capture); the HTTP API takes the note in the request.

## D6 as built (2026-10-25)

- **The engine** (`Windows/NesEmulator/debug/capture`, compiled by the desktop app and Workshop with the debugger, and by Lite with only these
  files and D5's `Movie.cs`): `BugCaptureRing` (the rolling buffer of "D6 settled": 32 one-second slots of a save state, a picture and the
  keyframe hashes; two bytes a pad a frame; the sound), `BugCaptureSource` (what it reads of a NES or a console session; the cores and the NES's
  frame timing watched once a second, so a core swapped while the game runs starts the history again), `BugBundle` (the files, a folder or a
  zip), `BugPlaces` (where a bundle goes), `BugMedia` (PNG and WAV writers in plain managed code: the desktop, Lite and Workshop write the same
  bytes). The bundle's movie is a D5 movie (`capture.bnm`) from the oldest second's state, with a keyframe a second and one at the moment of
  the capture; a console without save states gets a movie from its power-on while the history reaches it, else pictures and sound and the
  reason. Three small hooks outside the engine: `NES.StateLoaded` (called after a state load: the apps' history starts again),
  `NES.RomFile` / `RomLoads` (the ROM a load was given, the count), both null or plain fields until a host uses them.
- **The desktop app** (`Windows/MainForm/MainForm.BugCapture.cs`): **F9** and Emulator > Capture Bug (last 30 s) (open question 3: F9, a note
  box with the game paused, Enter saves, Esc discards), Keep the Last 30 s for Bug Capture (config `bugCaptureEnabled`, on by default; off,
  the frame loop's two calls return at their first test), Open Bugs Folder. The ring is fed from the NES and the session frame loops on
  the emulation thread; breaks (the history starts again, the bundle says when and why) on state loads, resets, core swaps, corruptions (auto
  and manual, the Glitch Harvester's console changes), memory and register writes through the HTTP API; anything not told is caught by the
  replay (off course, said). HTTP: `GET /api/bugs/status`, `POST /api/bugs/capture {note}`, `POST /api/bugs/enabled` (`Windows/webapi/README.md`).
  **Where:** a ROM that is a BrokenDev project's build (its sha256 in a build record) goes to the record's `bugs` folder (new: every build
  writes it), else to its live workspace's `bugs\`; any other ROM, and a record with no workspace, to `%LOCALAPPDATA%\BrokenNes\bugs`
  (`BROKENNES_BUGS_DIR` for tests). One fix on the way: the NES battery saves now honour `BROKENNES_SAVES_DIR` like the consoles' (a smoke
  test with a scratch saves folder had written an empty `.sav` into the user's `BatterySaves`; it was removed, nothing else was touched).
- **Lite** (`Windows/NesEmulator/board/Emulator.BugCapture.cs`, the **Bug** button): the same ring fed from the page's frame call (a
  fast-forward tick frame by frame), the note through the browser's prompt (Cancel discards), the zip handed to the browser as a download
  (`bug_<rom>_<date_time>.zip`). `SetBugCapture`, `BugStatusJson`, `CaptureBugZip` for the page and the tests.
- **BrokenDev:** `brokendev.ps1 bugs [project]` (each workspace's captures and the default folder's that name the project or its ROM; it
  checks every workspace keeps `bugs\` out of git and fails when one does not: it found SuperVRUN_dev and the two WideMess projects in this
  repository; SuperVRUN_dev got `/bugs/` in a local commit there, `aff9ea5`, never pushed; this repository ignores `/WideMess/*/bugs/` (not a
  bare `bugs/`: on Windows git matches it without case and `Workshop/Bugs` is source); VRUN_Nes_Dev and SMB1_Remake_dev hold their `bugs\`
  outside any repository; VRUN_VOC_dev already ignored it), `brokendev.ps1 bug <id|folder|zip>` (the replay with the hardware checks: from a
  state the read-before-written rules are off, said) and `-Debugger [-Script "..."]` (Workshop `--debug --bug`: the capture's ROM, cores and
  state, its input preloaded as the history, so `run` replays it with the keyframes checked and `back` steps back through it).
  Workshop: `--bug info|play|list|make`, `--bug-test`. **The MCP server's `bugs` tool** (the one D3 left): list, info, play, open (a headless
  session at the capture's end: memory, breakpoints, `step_back` through the 30 s); 46 tools.
- **Measured:** the desktop app with VRUN: 59.7 fps recording against 59.6 off (the app's own counter, 10 s each); a VRUN state is 763 KB and
  takes 2.4 ms (10.8 ms at worst) once a second. Lite (AOT, Edge, VRUN): 5.82 ms a frame recording (172 fps), p95 9.8 ms, 3 frames of 1200
  over 16.7 ms (the once-a-second state: 6.6 ms in WebAssembly), against 5.39 ms off: it holds 60 fps (a second run on the merged head,
  the machine busier: 7.01 ms against 6.51, 143 fps, 7 frames of 1200 over 16.7 ms, the worst 23 ms: one late frame now and then, at a state). Workshop A/B (best of three,
  interleaved): within noise on the planted game, VRUN and Super VRUN.
- **Tests:** `--bug-test` (frame-identical armed against off on the NES, SNES and Game Boy, and the negative control of a capture that writes
  RAM; no allocation between states, and the measurement seeing a state's; captures replay exactly from the folder and the zip on the three
  consoles, VRUN, Super VRUN and the VOC hello world; the debugger path; breaks, core swaps, a console without states; the folders; the cost;
  each with a negative control: one Start press a frame late, the start state of another moment, a load the ring was not told of, a core swap
  not watched on VRUN), `UAT/entrypoints/bug-capture-desktop-smoke.ps1` (the real app with scratch config, saves, BrokenDev home and bugs
  folder: F9 posted to the window opens the note box, which UI Automation fills and saves; Discard saves nothing; the API capture; a quick
  load and a memory write start the history again; off records nothing; every bundle replays in sync; the user's `%APPDATA%\BrokenNes`
  unchanged), `UAT/lite/test-lite-bugs.mjs` + `UAT/entrypoints/lite-bugs.ps1` (the real Bug button on the AOT build, its zip replayed in sync;
  cancel and off give nothing; a Game Boy session; the frame times), BrokenDev `run-tests` (a planted project: its build record's bugs folder,
  bugs listing with the git check and its negative control, bug replay, the debugger, a late Start fails, an unknown id refused), the MCP client
  test (7 new checks).
- **Results on the branch merged with main aa1b401** (WideMess M6 climb; no conflict): `--bug-test` 48 passed 0 failed, `--movie-test` 69 of 69,
  `--debug-test` 101 of 101, `--check-test` 111 of 111, `--wms-test` 331 passed 0 failed, UAT 125 pass 0 fail 16 skip 3 info (the goldens
  unchanged), BrokenDev `run-tests` 138 passed 0 failed 0 skipped (with `-McpExe`: the MCP client test 82 of 82 and the demo), the desktop
  smoke 18 of 18, Lite 8 of 8; the desktop app, Workshop, the MCP server, Lite (AOT publish), Core and the plugin build.
- **Left open:** VRUN's build record today is a hand-published frozen one with no workspace, so F9 on that build goes to the default folder
  (`brokendev bugs vrun-nes` still lists it, by the ROM's sha256); the next `brokendev.ps1 build vrun-nes` writes `bugs` and F9 lands in
  `VRUN_Nes_Devugs`. A capture of a Sega game in another region or model, or of a SNES game with real coprocessor firmware, replays on
  the defaults (the settings are in `bug.json`; the replay says off course rather than guessing). The NES state carries the whole ROM (763 KB
  for VRUN): a lighter state would make Lite's once-a-second cost smaller. The key and the note box are open question 3's answer for now.

## Open questions (to settle at the start of the track)

1. The debug port's registers on each console, checked against mapper 30, the SNES coprocessors' ranges and the Game Boy Color's
   registers, reusing a convention other emulators already show where one exists.
2. The movie format's name and file extension. (D5 uses a working name, "BrokenNes movie", `.bnm`: the constants of `MovieFormat`.)
3. The bug-capture key and how the note is typed. (D6, for now: F9, a one-line note box with the game paused; Lite: the Bug button and the browser's prompt.)
4. The first check catalogue per console, and which findings today's games accept (for example Super VRUN's SNESMOD sound-RAM reads).
5. The checkup command's name and where its report lives (a BrokenDev page or a dashboard).
6. Approvals when the work reaches them: the MCP SDK's NuGet package (approved 2026-10-24: `ModelContextProtocol` 2.2.0) and the GBDK-2020 download (exact files, sources and sizes asked then).
