# Next pool: planned projects (not started)

Status: plans only (the user's brief of 2026-11-01). Each project becomes its own card on the BrokenNes Next board; none starts
until the user picks it. Two of them (P3, P4) need an interview first.

| # | Project | When | Needs first |
|---|---|---|---|
| P1 | Vaults on the Super Game Boy: the original sound effects on the SNES's sampler | later this week or next | the original SFX files |
| P2 | Super VRUN: MS-DOS port | next week | your DOSBox-X debugging setup |
| P3 | An "x86 console" in BrokenNes (DOS games) | stretch, investigate | an interview: scope and why |
| P4 | WideMess Virtual: a web game as a WideMess "cartridge" | after an interview | the CTW Remastered HTML5 export, an interview |
| P5 | Super VRUN: the map, the stats, the title effect and the options UI from CTW Remastered | later this week or next | the Remastered project files |

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

## P3. An "x86 console" in BrokenNes (investigation only)

**Goal.** Find out whether BrokenNes could run DOS / PC x86 programs as another console, and what that would mean. Far-fetched:
the outcome may be "no", or "only a narrow slice".

**Questions for the interview.**
- Why: to run Super VRUN DOS (P2) inside BrokenNes with its debugger, Timeflow and core mixing? To mix an x86 CPU with the other
  consoles' chips (corruption, gimmick cores)? Both?
- How much PC: a real-mode 8086/286 with VGA mode 13h, a PC speaker / AdLib / Sound Blaster, and enough DOS (INT 21h) to load one
  program, is a very different size from a 386 with DOS extenders.
- Write our own (a big emulator project) or port an existing open-source one (licensing, like the Foreign APU exception)?

**First step if we go.** A one-week spike: an 8086 core + mode 13h + a minimal DOS loader in Workshop, running one small test
program, to measure the work and the speed in .NET and in Lite (WASM).

---

## P4. WideMess Virtual (needs an interview)

**Goal.** A passthrough mode: a WideMess "cartridge" that is a web game. BrokenNes hosts an HTML5 canvas at the WideMess
resolution (400 x 224), shows it as the WideMess picture, and sends the emulator's controls to the game. The proof of concept is
**VRUN Corrupt The World Remastered** (a Clickteam Fusion HTML5 export whose canvases already use the WideMess resolution).

**What's realistic, to discuss.**
- The desktop has WebView2 (it already hosts the overlay cards), so it can run the export offscreen, capture the canvas each frame
  and inject keys. Lite is itself a web page, so the game could run in an iframe or a canvas beside the emulator.
- What BrokenNes features can work: picture, input, sound (captured from the web page's audio), pause; probably not save states,
  rewind / Timeflow, core swaps or corruption (the game is not emulated, so there is no machine state). The interview decides which
  of those matter.
- Timing: the web game runs at its own clock; frame pacing and input latency need measuring.

**Open questions.** Where the Remastered HTML5 export is; desktop, Lite or both; what "running like a game in an emulator" must
include for you.

---

## P5. Super VRUN: back-ports from CTW Remastered

**Goal.** Bring the Remaster's best UI to Super VRUN SNES (the WideMess variant later gets more of them):
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
