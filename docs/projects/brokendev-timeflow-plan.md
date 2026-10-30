# BrokenDev Timeflow: rewind, deep profiling, hotpatch and a searchable gameplay corpus

Status: **T1 (the timeline format), T2 (rewind and resume), T3 (profiled spans) and T4 (search) built** on 2026-10-30 (see "T1 as
built" to "T4 as built" at the end): rewind on every console; profiling and breakpoint searches on the NES, SNES (with its SPC700), WideMess and
Game Boy; the Sega consoles take state, picture and sound searches and are refused for profiling and breakpoints until T0 (the debugger on the
Z80 and the 68000, built in parallel). Next: T0, then T5 (hotpatch).

## The idea (the user's words, condensed)

In Eternal Degrade, debugging became "a piece of cake" because the world machine saved the whole world on a timeflow: the user could
profile a bug from the trace, rewind to just before it, hotpatch the .NET executable and resume play to see whether the fix worked.
That worked because (1) the timeflow behaved like an emulator saving states into a rewind queue and (2) the world ticked in coherent,
frame-like steps. An emulator gives us both for free. BrokenDev already records inputs (D5 movies); Timeflow is the debugging-focused
expansion of that, in levels:

| Level | Name | What it stores | When |
|---|---|---|---|
| 0 | **Movie spec** | inputs (+ D5's hashes every 60 frames) | exists (D5, `.bnm`) |
| 1 | **Rewind spec** | a full machine state for every frame, as deltas | while recording is on |
| 2 | **Profiler spec** | every instruction, its registers, every memory access, video/sound chip events, named variables | on demand, for a span, temporary |
| - | **Hotpatch** | a patched ROM (byte patches or a rebuild) resumed from a rewound frame | on demand |
| - | **Corpus + search** | a pool of recorded timelines, searchable for "where would this breakpoint have fired", jump straight there | always, within a cap |

The end goal: an agent (or the user) hits a bug, finds the exact frame with a search over the corpus, profiles that span to see the
cause, hotpatches, resumes, and checks the fix with a test, without having to play the game again to get back there.

## Decisions (interview, 2026-10-30)

1. **Consoles:** all BrokenNes consoles (NES, SNES with its SPC700 and coprocessors, WideMess, Game Boy / Color, Master System, Game
   Gear, Genesis). The Sega consoles have states but no debugger today, so profiling them needs the D1 debugger extended to the Z80 and
   the 68000 first (milestone T0).
2. **Hotpatch:** both. Byte patches on the running ROM for quick experiments, and rebuild-from-source (BrokenDev builds the project)
   for the real fix; either resumes from a rewound frame.
3. **Corpus sources:** the user's playtests, promoted movies and D6 bug captures, bots (chaos bots / explorers), agent sessions, and
   **an agent may ask the user to play until the bug happens**, which gives a direct path to a reproduction.
4. **Consent:** recording and profiling never happen without the user knowing. Recording is **off until switched on** (by the user, or
   by the user accepting an agent's request); while on, an always-visible **REC** mark shows, with the space used. Agents can request,
   never switch on silently.
5. **Disk:** a **fixed cap per game, 5 GB by default** (adjustable per project). The oldest / least-used timelines retire first.
   Profiles are temporary on top of the cap and deleted after use.
6. **New builds:** when the game is rebuilt, old timelines **re-sync**: their inputs replay on the new ROM, and a timeline is kept while
   it stays in sync and retired from the first frame it drifts (the drift point is reported: it shows where the change mattered).
7. **Profile depth:** everything: every instruction with registers (every CPU of the console), every memory access (address, value,
   the instruction that made it), video/sound chip events (register writes, DMA, interrupts, scanline/dot), and named game variables.
8. **Interfaces, first:** MCP tools (agents), a desktop window (a timeline scrubber in Debug Windows), BrokenDev CLI.
9. **Search conditions:** code and memory breakpoints (with conditions), game-state expressions on named variables, picture / sound
   conditions (a sprite missing, a colour on screen, a channel silent), and D4 hardware-check findings.
10. **The fix verdict:** **a test you write**: after a hotpatch, the agent or the user writes a condition that must hold over the
    replayed span (e.g. "enemy alive implies its sprite is drawn"); the verdict is that test. (Optionally promoted into verify by hand.)
11. **No automatic regression pinning:** a fixed bug's timeline is not kept as a permanent test; timelines stay temporary.

## The design

### Level 1: the timeline (rewind spec)

- **What a frame record holds:** the machine state minus the ROM (the ROM is stored once per build, by sha256; today's NES state
  carries the whole ROM, so the state format needs a "no ROM" form), the frame's input, and a small per-frame index row.
- **Deltas:** every N frames a full keyframe (N = 60 to start); in between, each memory domain is split into fixed pages (e.g. 256
  bytes) and only the pages that changed since the previous frame are stored (dirty pages found by comparing page hashes, or by
  write tracking in the cores' memory paths where that is cheap), then compressed in blocks of frames with .NET's built-in Brotli or
  Deflate (no new dependency). CPU / chip registers are tiny and stored whole every frame. Expected size: work RAM changes little
  per frame, so most frames cost a few hundred bytes to a few KB; VRAM uploads are the big ones. Measure per console in T1 and set N
  and the page size from the numbers.
- **Determinism is the contract:** any frame is restored by loading the nearest keyframe and applying deltas, and the restored state
  must hash-equal what was recorded (checked on a sample of frames continuously while recording; a mismatch stops the recording and
  says why, never silently).
- **The per-frame index** (kept for every frame, small, uncompressed for fast scans): frame number, the D5 picture / RAM hashes,
  the D9 coverage bitmap of code run that frame (sparse, compressed), counters of writes per memory region, the D4 check findings
  of the frame, the values of the project's watched variables (from the symbols / the manifest).
- **Rewind** = jump to any frame; **resume** = continue from a restored frame with live input (a branch of the timeline) or with the
  recorded inputs (a replay).

### Level 2: profiled spans (profiler spec)

- A span is re-run from the nearest restored state with the D1 debugger's full tracing on: every instruction of every CPU (PC, opcode,
  registers, flags, cycle), every memory access, chip events (the D8 event observer), and named variables on change.
- **It is never stored as a whole by default.** Queries stream over it ("every write to $7E1234 in frames 500-560", "the call stack at
  the moment X", "what changed this byte last"), and only the answers are kept. When a span must be kept (e.g. for the desktop
  window's scrubbing), it goes to a temporary store under the project's Timeflow folder with its own size limit and a time-to-live; a
  session's profiles are deleted when the session ends unless the user keeps them.
- Cost is only paid for the span asked for; a span of a few seconds is the typical unit.

### Search over the corpus

Two phases, so the corpus stays cheap:
1. **Coarse:** scan the per-frame index of every timeline for candidate frames. Game-state conditions are exact here (the variables
   or the RAM are in the frame records); code breakpoints use the coverage bitmaps; memory breakpoints use the per-region write
   counters / dirty pages; check findings are in the index; picture / sound conditions use the stored picture hashes plus a re-render
   of candidate frames (or a small per-frame thumbnail / audio level in the index if measurements show it is worth the space).
2. **Exact:** re-run only the candidate frames as profiled spans and evaluate the condition precisely (the exact instruction, the
   exact access, the exact scanline).
The result is a list of hits (timeline, frame, cycle, PC, a short context), each a jump target: restore that frame, profile around it,
hotpatch, resume.

### Hotpatch and resume

- **Byte patches:** a patch list (address, old bytes, new bytes) applied to the ROM image of a restored session; recorded with the
  branch so it is reproducible; refused if the old bytes do not match.
- **Rebuild:** BrokenDev builds the project; the session resumes at the rewound frame on the new ROM with the saved RAM and registers.
  Because code and data can move between builds, the resume checks the symbol maps of both builds: the PC and the stack's return
  addresses must map to the same functions (or the resume is refused with the reason, and the nearest safe resume point, e.g. the
  next frame boundary in the main loop or the next room / scene load, is offered).
- **After resuming:** the replayed inputs run on the patched ROM; BrokenDev shows where the new future diverges from the old one
  (first differing frame, first differing instruction, differing variables: D5's divergence finder), and evaluates **the test the
  agent or the user wrote** over the replayed span: that test is the verdict.

### The corpus

- Timelines from: the user's playtests (when recording is on), promoted movies and bug captures (turned into timelines by replaying
  them with recording on), bots, agent sessions, and **"play until it happens" requests** (an agent asks; the user accepts, plays, and
  presses the D6 bug key when the bug shows, which marks the frame).
- Stored under `BROKENDEV_HOME\timeflow\<project>\` (never in git, never in `%APPDATA%\BrokenNes`), with a per-project cap (5 GB
  default). Retirement: when the cap is reached the least valuable timelines go first (oldest, never searched, never hit, drifted on
  the current build); a timeline the user marks keep is never retired. Re-sync after every new build (decision 6).

### Interfaces

- **MCP tools:** `timeflow_record` (request; needs the user's acceptance), `timeflow_list`, `timeflow_jump` (restore a frame),
  `timeflow_search` (a condition over the corpus), `timeflow_profile` (a span, with streaming queries), `timeflow_patch` (byte patch
  or rebuild), `timeflow_resume`, `timeflow_verdict` (evaluate a written test over a span), `timeflow_request_play` (ask the user to
  play until the bug).
- **Desktop:** Debug Windows > Timeflow: the REC switch and mark, a timeline scrubber (frames, hits, markers), jump / resume, profile
  a selection, open in the other debug windows at that frame.
- **CLI:** `brokendev.ps1 timeflow record|list|search|profile|patch|resume|verdict|prune|status`.

## Milestones

| Milestone | Delivers | Done when |
|---|---|---|
| **T0** | Debugger on the Sega CPUs | The D1 engine (breakpoints, watches, trace) covers the Z80 (SMS / GG / Genesis sound) and the 68000; `--debug-test` grows Sega checks. |
| **T1** | Timeline format | Keyframes + page deltas + per-frame index, ROM-less states, Brotli blocks; restore-equals-record checked; measured bytes per frame per console; the REC switch and mark in the desktop app (off by default). |
| **T2** | Rewind and resume | Jump to any frame, resume with live input or the recorded inputs, branches; MCP + CLI + the desktop scrubber. |
| **T3** | Profiled spans | Full-depth re-run of a span with streaming queries; temporary store with size limit and TTL; cleanup proven (nothing left behind). |
| **T4** | Search | Two-phase search for all four condition kinds over many timelines; hits as jump targets; measured scan speed. |
| **T5** | Hotpatch | Byte patches and rebuild-resume with the symbol-map safety check; divergence report; written-test verdicts. |
| **T6** | Corpus | All sources (playtests, movies, bug captures, bots, agent sessions, play-until-it-happens requests), the 5 GB cap, retirement, re-sync on new builds with drift points. |
| **T7** | Proof on a real bug | An agent uses only Timeflow (no new playing) to find, profile, hotpatch and verify a planted bug in a test ROM, and then a real one in one of the user's games. |

## Open questions (to settle as the work reaches them)

1. The exact keyframe interval and page size per console (from T1's measurements). **Answered in T1:** one choice for every console, N 60, 1024-byte pages, Brotli 5 (see "T1 as built").
2. Whether picture / sound search needs per-frame thumbnails or audio levels in the index, or re-rendering candidates is enough. **Answered in T4:** re-rendering (once per distinct picture) for now; a colour summary and an audio level are T6 items, with the numbers (see "T4 as built").
3. Which of the user's games is T7's real-bug proof (Super VRUN's invisible enemies, if still open by then, is a natural candidate).
4. How the desktop app asks the user to accept an agent's recording / play request (a non-blocking prompt in the app, like the
   no-sound notice). **Answered in T1:** a yellow bar under the menu bar, Allow / Decline, the game keeps running (see "T1 as built").
5. Lite: whether Timeflow recording exists there (memory and storage are tighter in a browser) or stays desktop / Workshop / MCP.

## T1 as built (2026-10-30): the timeline format

- **The engine** (`Windows/NesEmulator/debug/timeflow`, compiled by the desktop app and Workshop with the debugger, hence the MCP server; not
  Lite, open question 5 stays open): `TimeflowState` / `TimeflowSource` (what a machine's state is in the timeline and how it is put back),
  `TimelineCodec` (records, page deltas, Brotli / Deflate), `Timeline.cs` (the description, the index rows, the extras, `TimelineWriter`,
  `TimelineReader`), `TimeflowStore` (the folders, the ROMs, the cap), `TimeflowRecorder` (the recording: the emulation thread takes the
  state, a worker thread does everything else), `TimeflowSwitch` (a host's on / off switch), `TimeflowPlayback` (T2).
- **ROM-less states.** A console session's state is its payload (the new `IConsoleSession.SaveStatePayload()`: the bytes `SaveState` wraps,
  without the container's deflate and checksum, implemented by the SNES / WideMess, Game Boy, Master System / Game Gear, Genesis sessions and
  the Sega picture wrapper; a session's layout is fixed for a game, so its pages line up frame to frame) and the ROM is not in it. The NES
  saves itself as JSON text whose length moves by a character whenever a number gains a digit, so `NesStateForm` cuts it: every long
  base64 run (the RAM, the PPU's memories, the cartridge's PRG / CHR RAM, the mapper's arrays: on VRUN's self-flashing mapper-30 board the
  512 KB flash shadow, the "whole ROM" a NES state carries) becomes a fixed-length section, the few KB left (registers, timing) the skeleton,
  stored whole every frame. It comes back byte for byte (a run is only taken out when it encodes back to the same characters; a 0x01 in
  the text is refused). The form also blanks what `NES.LoadState` never applies (`romPath`, `romName`) and APU_FIX's output side, which its
  load resets (the sound queue counters, the resampler phase, the output filters), so a state taken after a load equals the recorded one.
  The ROM is stored once per build in the game's folder: `roms\<sha256><ext>`.
- **A frame record** is the state at the frame's start (before its pads reach the machine), the pads, and an index row. Every
  **N = 60** frames a keyframe, and a block: a block is a keyframe and the 59 deltas after it, so it decodes on its own. A delta holds the
  skeleton whole and, per section, the **1024-byte pages** that changed, as XOR with the frame before (unchanged bytes are zeros); a frame
  whose layout changes is written as a keyframe. Blocks are compressed with **Brotli quality 5** (.NET's own `BrotliEncoder`, no package),
  checksummed (CRC-32 of the compressed bytes, a hash of the raw ones). Files: `timeline.json` (the description), `frames.tfb` (the
  blocks), `blocks.tfi` (where each block is), `index.tfx` (48-byte rows, uncompressed: pads, the D5 picture and RAM hashes, the state's
  hash, the record's size, where its extras are), `extra.tfx` (per frame: the changed bytes and 256-byte pages of each memory domain, the
  watched variables, the D4 findings first seen that frame, the D9 coverage of the frame as deflated ROM ranges; the last three when asked
  for: they need the debugger).
- **Restore equals record, while recording.** After each block is compressed, it is decoded back from the compressed bytes and the
  states' hashes compared with the recorded ones: the last frame and one at random of every block, every frame of every 8th block (every
  frame with `FullCheckEvery = 1`, which the tests use). A mismatch stops the recording with the reason ("restore does not equal record at
  frame ..."); the blocks before it are kept. The reader checks the block's checksum, its raw hash and every restored frame's state hash:
  a damaged block or delta is refused with where it is, never returned.
- **Where:** `BROKENDEV_HOME\timeflow\<project>\` for a ROM that is a project's build (its sha256 in a build record), else
  `BROKENDEV_HOME\timeflow\_roms\<rom name>-<sha256 prefix>\`; a `BROKENDEV_HOME` under `%APPDATA%\BrokenNes` is refused; never in git
  (BROKENDEV_HOME is outside the repository). `BROKENDEV_TIMEFLOW` replaces the root (the MCP client test's scratch folder).
- **The cap:** 5 GB per game folder (`timeflow.json` `capBytes` changes it per game). After every block the folder is measured (the open
  timeline by what its writer wrote: Windows lists an open file's size as of its last close) and the least recently used timelines retire
  until it fits; a timeline marked keep never retires, nor the one recording (nor one still marked recording, unless its description is
  10 minutes old: a crash). A recording that alone reaches the cap stops, saying so; a recording is refused when the folder is at its cap
  with everything kept.
- **Consent** (the user's rule): recording is **off until switched on**. The desktop app: Emulator > Debug Windows > **Timeflow: Record
  (REC)**, or the REC box of the Timeflow window; never saved (off at every start). While on, a red **"● REC 12.3 MB of 5 GB"** mark at the
  right of the menu bar (the folder's space) and **[REC]** in the title. An agent can only ask: `POST /api/timeflow/request {reason, from}`
  (the MCP `timeflow_record` action `request` on a live session) shows a yellow prompt under the menu bar ("<who> asks to record your play
  ...: <reason>", **Allow** / **Decline**) while the game keeps running; nothing records until the user presses Allow. There is no route
  that switches it on or off (the smoke proves 404). Headless sessions (an agent's own machine in the MCP server, Workshop) record when the
  agent says so: that is never the user's play. **Decision taken here for you to confirm:** agents may record their own headless sessions
  without asking (the timelines are listed and use the cap like any other).
- **Breaks:** anything outside the game (a state load, a reset, a core swap, a corruption, a memory or register write through the API, a
  jump, another game) closes the timeline there ("closed: a state was loaded") and the next frame begins a new one: a timeline only holds
  frames that replay into each other. The idle screen (test.nes) is never recorded.
- **Measured** (Workshop, this machine, `--timeflow measure`, 900 frames after 900 of play; the user's games and three commercial ones; the
  seeded wanderer `auto:1` as input, VRUN its promoted movie). The state is what the machine saves; "recording" is the real recorder
  writing files (N 60, Brotli 5). The bytes a frame are on disk with the final choice (N 60, page 1024, Brotli 5, every frame checked;
  `--timeflow-test`'s 1800-frame recordings):

| Game (console) | State | Taking it | fps off | fps recording | Bytes a frame on disk | 5 GB holds |
|---|---|---|---|---|---|---|
| VRUN (NES, mapper 30) | 573 KB (skeleton 3.6 KB, 7 sections, the 512 KB flash shadow) | 4.1 ms (NES.SaveState alone 2.6) | 259 | 124 | 270 | 92 h |
| Super VRUN (SNES) | 650 KB | 0.29 ms | 744 | 517 | 9,568 | 2.6 h |
| Super Mario World (SNES) | 643 KB | 0.27 ms | 690 | 512 | 8,262 | 3.0 h |
| WideMess sample (WMS) | 3.37 MB | 1.8 ms | 236 | 150 | 18,023 | 1.4 h |
| Vaults of Obsolete Code (GBC) | 83 KB | 0.10 ms | 613 | 599 | 217 | 115 h |
| Link's Awakening DX (GBC) | 83 KB | 0.09 ms | 697 | 621 | 236 (page 256: not in the test) | 105 h |
| Sonic 2 (Master System) | 339 KB | 0.86 ms | 1,028 | 517 | 4,652 | 5.3 h |
| Sonic (Game Gear) | 339 KB | 0.80 ms | 1,099 | 543 | 2,671 | 9.3 h |
| Sonic (Genesis) | 860 KB | 0.55 ms | 316 | 246 | 6,536 | 3.8 h |

  Every console records well above 60 fps; the desktop app holds its 60 (the smoke: 482 frames in 8 s off, 491 recording, VRUN). The
  SNES, WideMess and Sega states carry their published picture (the SNES S-PPU saves its 256-wide frame), so their deltas are the picture's
  changes: that is most of their bytes.
- **The choice, from the numbers** (open question 1, answered): **Brotli 5** (on the big states 5 to 8 times smaller than Brotli 1:
  Super VRUN 10.5 KB against 82.9 KB a frame, WideMess 19.2 KB against 141.6 KB, VRUN 290 against 428 bytes; 0.1 to 4.5 ms a frame on the
  worker thread; and its blocks restore faster); **N = 60** (N 120 saves 20 to 25 % but doubles a restore: Super VRUN 25 against 13 ms;
  N 30 costs 40 to 60 % more); **1024-byte pages** (with Brotli 5 the larger pages are smaller still on the big states: Super VRUN 9.6 KB
  against 10.5 KB at 256, WideMess 17.6 against 19.2, Sonic 2 4.2 against 4.5; the same on the NES and Game Boy; 4096 is smaller again on
  WideMess but larger on the Game Boy and slower to restore). Deflate fastest was larger than Brotli 1 everywhere. The full grids (every
  page size from 64 to 4096, N 30 / 60 / 120 / 300, seven compressions) are what `--timeflow measure` prints.
- **Found on the way (a real bug, fixed):** APU_FIX's save state did not carry its frame sequencer's step (`frameStep`,
  `nextFrameEventCycle`): after any load the sequencer kept the step of the machine it was loaded into, so its quarter and half frame
  clocks (envelopes, sweeps, length counters) ran at other cycles; the MCP client test's planted game diverged in pulse 1's envelope 30
  frames after a jump while its RAM and picture still matched. Now saved, and derived from `frameCycle` for older states
  (`--timeflow-test` group `apu`). The other NES APU cores (frozen) are unchanged: a replay on one says when it goes off.
- **Left for later:** Lite (open question 5); the per-frame coverage and findings need the debugger (NES, SNES, WideMess, Game Boy):
  the Sega consoles get rewind without them until T0; the VRUN capture's 4 ms a frame is the NES's own JSON state (2.6 ms) plus its cutting:
  a binary NES state would make it a few tenths of a millisecond; the WideMess and Genesis recordings show a worst frame of 100+ ms
  (garbage collection of the large per-frame arrays) in the headless measurement: not seen as dropped frames on the desktop's NES, not yet
  measured on the desktop with WideMess.

## T2 as built (2026-10-30): rewind and resume

- **The engine** (`TimeflowPlayback`): **jump** restores a frame (keyframe + deltas, its hash checked), loads it into the machine, and takes
  the machine's state again: it must hash the same as the recorded one, else the jump is refused with both hashes and where the two differ
  (the skeleton's text around the first difference, or the section). **Replay** runs the recorded pads from the jumped frame and checks
  every frame's picture and RAM hashes and the whole state at every keyframe against the recording: in sync to the end, or the first frame
  off and what differed. The NES keeps no picture in its state (the render target is not saved), so the first frame after a jump shows the
  picture before it: that one frame's picture is not compared (its RAM is; said as `pictureHeld`). **Branch**: live input after a jump,
  recorded as a new timeline whose description names its parent and the frame it left at (`parentId`, `parentFrame`; its frames are numbered
  from there). Listings show the tree (a branch under its parent). A jump into another console or another ROM is refused (another build is
  T5's hotpatch).
- **Workshop:** `--timeflow record|list|info|verify|jump|resume|prune|keep|status|measure` (Workshop/Timeflow), `--timeflow-test`.
- **BrokenDev:** `brokendev.ps1 timeflow record <project> [-Scenario id | -Script S -Frames N | -Movie m] [-Course sym,sym] [-Coverage]`,
  `list [project]`, `info|verify <timeline>`, `jump <timeline> -Frame F [-Path out.png]`, `resume <timeline> -Frame F` (replay) or
  `-Script S -Frames N [-Record]` (a branch), `prune [project]`, `keep|unkeep <timeline>`, `status` (BrokenDev/lib/Timeflow.ps1).
- **MCP** (51 tools): `timeflow_record` (headless: start / stop / status of the session's own recording; live: **request** / status /
  cancel only, start is refused: "only the user does"), `timeflow_list` (the store, or one timeline), `timeflow_jump` (a new headless
  session on the timeline's own stored ROM and cores, or an open one of the same ROM), `timeflow_resume` (mode replay, checked; mode live:
  the session's own input, a branch when recording). A headless session's recording breaks (ends the timeline, begins a new one) on
  load_state, reset, write_memory, set_register, step back and a movie start; after a jump, its frames are a branch of that timeline.
- **The desktop app:** Emulator > Debug Windows > **Timeflow** (`Windows/DebugWindows/TimeflowWindow.cs`): the REC box, what is recorded
  and the space used, the running game's timelines as a tree, a scrubber (a track bar and the frame number) with the chosen frame's pads,
  changed bytes and watched variables, **Jump** (the game at that frame, paused, proved equal), **Resume (live)** (a branch while REC is on),
  **Replay recorded** (the recorded pads handed to the game, checked frame by frame; the result in the window and `/api/timeflow/status`).
  The window's timer lives only while it is open; nothing is saved. Routes: `GET /api/timeflow/status`, `GET /api/timeflow/timelines`,
  `POST /api/timeflow/request`, `POST /api/timeflow/request/cancel` (Windows/webapi/README.md).
- **Tests** (each with a negative control):
  - `--timeflow-test` **130 passed, 0 failed, 0 not run** (the group `index` was added after the full run's 124: the watched variables
    by frame, the frame's coverage, the changed bytes and pages of the work RAM, findings once each (the planted game has none: the path is
    exercised, not yet with a finding), negative: without them none, and the debugger's hooks leave the recording frame-identical): the NES form (and a run that is not canonical base64 stays text, a 0x01 is
    refused, the ROM's folder does not change the hash); page deltas at 64 / 256 / 4096 (a delta on another frame gives another state);
    **every console over 1800 frames** (the NES, SNES and Game Boy test ROMs, VRUN, Super VRUN, Super Mario World, the WideMess sample, VOC,
    Sonic 2 SMS, Sonic GG, Sonic Genesis): every frame checked while recording, every frame restored from the files equals the record,
    four frames loaded into a fresh machine hash as recorded, a replay from the middle to the end in sync (negative: other input, or a RAM
    byte on the test ROM that ignores input, goes off); **damage**: a flipped byte is found by the block checksum, a changed delta with its
    checksums made to match is found by the state hash from that frame on (the frames before restore); **stop**: a damaged encoder's block
    stops the recording with "restore does not equal record", the host is told, the blocks before are kept (negative: the same recording
    without the fault runs to the end; a machine without states is refused); **cap**: the oldest unkept timeline retires, the kept one
    stays, the folder ends within its cap (negative: under the cap nothing retires; all kept: refused with the reason; a timeline that
    alone reaches the cap stops, saying so); **off**: running without a recorder writes nothing, and recording leaves the NES, SNES and
    Game Boy frame-identical (400 of 400 frames' picture and RAM; negative: a RAM byte changed at frame 200 is seen from frame 200);
    **branch** on the NES and SNES (jump, the branch's parent / frame / first state, its later frames its own, the parent still in sync, the
    tree; negative: another ROM and a frame outside are refused); **places**; **apu**.
  - The MCP client test **112 of 112** (13 new: off by default, record, list, jump into a new session, replay in sync with and without
    the call stack, a branch, and the refusals: outside frame, unknown timeline, another machine, resume without a jump, a RAM write after a
    jump goes off) and the live checks in `--live-test` (`BrokenDev\tests\mcp-live.ps1`, **18 of 18**, 3 new): a request on the user's app
    records nothing (still pending 1.5 s later, no timeline), start is refused ("only the user does"), cancel.
  - `UAT/entrypoints/timeflow-desktop-smoke.ps1` (the real app, scratch config / saves / bugs / BrokenDev home, no sound device, the
    user's `%APPDATA%\BrokenNes` listed before and after): off by default and nothing written; no route switches it on (404); a request
    shows the prompt, the game keeps running, nothing records, Decline records nothing; Allow records (the timeline in the scratch
    BROKENDEV_HOME, [REC] in the title, the mark "● REC 0.7 MB of 5 GB"); a request while on is not needed; the user's REC box switches it
    off and nothing more is written; the desktop's timeline verified by the Workshop (restore equals record, loads into a machine) and
    replayed in sync; the window lists it, jumps (paused there) and replays in sync; 60 fps held recording.
  - **Regression on the branch** (main c65d7d3, nothing newer to merge): `--debug-test` 103 of 103, `--movie-test` 69 of 69, `--bug-test`
    48 of 48, `--snes-state-test` 147 of 147, `--md-state-test` 168 of 168 (the sessions' new `SaveStatePayload`), UAT **125 pass 0 fail
    16 skip 3 info**, BrokenDev `run-tests` **150 passed, 0 failed, 0 skipped** (with `-WorkshopExe` and `-McpExe`; the new case drives
    every `brokendev.ps1 timeflow` action on a planted project: its timeline in `timeflow\<project>`, the course variable, verify 600 of 600,
    jump, replay in sync, a branch with its parent, keep / status / prune; negative: a frame outside, an unknown timeline, an unknown action)
    (the goldens unchanged with APU_FIX's state change; the story and corpus ROM folders linked from the main checkout for the
    run). Recording off costs nothing on the cores: Workshop's runners call no Timeflow code unless recording; the desktop's frame loop makes
    three field tests a frame; the UAT goldens and the bug capture's speed A/B (Super VRUN 867 / 849 fps) are as before.
- **What T3 needs** (profiled spans): a span is re-run from the nearest keyframe with `TimelineReader.State` + `TimeflowPlayback.Jump` and
  the recorded pads (`Replay`'s loop with the D1 debugger's full tracing armed: `DebugObserver`s for steps, accesses, chip events); the
  index already gives the candidate frames' extras (changed pages per domain, the frame's coverage, its findings, the watched variables);
  the temporary store (with its own limit and time to live) goes beside `timelines\` as `profiles\`; the Sega consoles need T0 first.
  Two things learned for it: capture the state before the frame's pads reach the machine (a desktop recording that took it after
  `SetInputs` went off at the first keyframe whose pads changed: fixed), and the NES's first picture after a load is the old one.

## T3 as built (2026-10-30): profiled spans

- **The engine** (`Windows/NesEmulator/debug/timeflow`): `TimeflowProfile.cs` (the records, `ProfileSink`, `ProfileContext`, the
  `ProfileRecorder` observer, `TimeflowProfiler.Run`), `TimeflowProfileQueries.cs` (the query language and its streaming sinks),
  `TimeflowProfileStore.cs` (kept profiles: `ProfileStore`, `ProfileWriter`, `ProfileReader`, the record codec), `TimeflowProfileView.cs`
  (`ProfileMachine`: a timeline's own machine opened by the engine, for the desktop; `ProfileView`: a kept profile a frame at a time, for the
  windows). One small change outside: `Debugger.SymbolAtKeys` (a symbol from canonical keys taken earlier), so a kept profile names the bank
  an instruction ran in, whatever is mapped when it is read. Nothing in the CPU cores or the debugger's per-CPU adapters changed.
- **The re-run.** `TimeflowProfiler.Run(playback, target, span, sinks)`: the keyframe at or before `From` is restored, loaded and proved equal
  to the record (T2's `Jump`), the recorded pads replay with the call stack tracked from there (`Debugger.TrackCallStack`, not profiled),
  and from `From` to `To` a `ProfileRecorder` (a D1 `DebugObserver` that wants every step and every access of every CPU) feeds the sinks.
  Every frame's picture and RAM are compared with the recording and the whole state at every keyframe (with T2's explanation of where it
  differs): a re-run that goes off is reported with the frame and what differed, stops there by default, and every answer carries the
  warning ("what follows that frame is not the recorded game"). The sinks can stop the re-run early (a `last-write ... before F` is done at
  F; a `stack N` once it saw N). The Sega consoles are refused before anything runs ("needs the debugger on its Z80 / 68000, which is
  milestone T0: no profile is made"), never an empty profile.
- **What is recorded** (in the order it happened; positions `#N` count every CPU's instructions from 0 at the span's first): every
  instruction and interrupt entry of every CPU the debugger covers (the NES 6502; the SNES / WideMess 65816 **and SPC700**; the Game Boy
  SM83): PC, bytes, the registers and flags before it ran (the adapter's own `FillTrace`), the effective address and its value, the machine's
  cycle, scanline and dot, the PC's canonical keys; every memory access (CPU and DMA / HDMA / OAM DMA / VRAM DMA, with the channel): address,
  value, read or write, the canonical keys of the place (every mirror folds to the same key), the instruction that made it; the call stack's
  pushes and pops, and each CPU's whole stack at every frame's start (so a stored frame decodes on its own); the debug port's markers (D7);
  the **named variables on change**: every data symbol of the loaded symbols (work RAM, cartridge RAM, high RAM, the NES's internal RAM; up
  to 4096, the first by address, more said), 1-4 bytes as a number, larger ones byte by byte, old and new value and the instruction whose
  write changed it. Chip events (D8's event view) are the register writes by scanline / dot, the DMA runs and the interrupts of these records
  (`ChipRegisters`). Known gaps, said in the answers: call frames entered before the keyframe are not on the stack ("stackNote"); NES OAM
  DMA is seen as its 256 reads (`OamDma` source), its writes into OAM are not observer accesses; the SPC700's writes are predicted from its
  operands (as D1's watches).
- **The queries** (`ProfileQuery.Parse`, the same text everywhere: Workshop `--q`, MCP `queries`, BrokenDev `-Query "q; q"`):
  `writes|reads|accesses <addr|range|symbol> [limit N] [cpu spc]` (every one, all mirrors and DMA, the first N in full with the
  instruction, registers and call stack, all counted by function and by value), `last-write <spec> [before F[:CYCLE] | before cycle C]`,
  `stack <N>`, `between <a> <b>` (debug-port marker texts, `#N`, `frame:F`: the instructions, cycles, calls, writes, functions and variable
  changes in between), `became <register|variable> <op> <value>` (the instruction that made `A == 3` or `hp != 5` true; registers read from
  each CPU's own trace columns, so the Game Boy's pairs and every flags register work), `when <expression>` (any D1 expression, evaluated on
  the running machine at every instruction boundary: a re-run only, refused on a kept profile with the reason), `histogram [cpu C] [top N]`
  (per function: the innermost call frame's target, else the code symbol: exclusive and inclusive instructions, cycles, calls), `events
  <F>[-<G>]`, `trace <N> [before K] [after K]` / `trace frame <F> [count K]` (Mesen's columns, then the position and the symbol), `vars
  [name]`, `summary`. An unknown query, symbol or CPU is an error in that answer, never an empty list.
- **Examples** (the planted corruption, `Workshop/Timeflow/TimeflowProfileRoms.cs`: `fill_table` compares its ring index with `#$21` instead
  of `#$10`, so its `STA table,X` reaches `hp` at `$0050` on the 33rd update):
  - `writes hp` over frames 20-60: `total 1`, `byFunction {"fill_table": 1}`, the hit `{"at": 127857, "frame": 32, "scanline": 241, "pc":
    "$C072", "in": "ft_store", "instruction": "STA table,X", "value": "$21", "stack": ["fill_table <- update+$1B (JSR, frame 32)",
    "update <- main_loop+$8 (JSR, frame 32)"]}`; `writes $0850` (a mirror) finds the same write.
  - `last-write hp before 60`: the same write, with `"registers": "A:21 X:20 Y:00 S:FB P:nvUbdIzc"` (X = $20: the index ran past the table).
  - `became hp != 5`: `{"in": "ft_store", "frame": 32, "old": "$05", "new": "$21"}`; `when [hp] != 5` names the same STA from a re-run.
  - `stack 127857`; `trace 127857 before 3 after 1` (its `>` line: `C072  STA table,X [0050] = $05 ... Fr:32 Cyc:980469 #127857 ; ft_store`);
    `between fill done` (the two debug-port markers around the call: 19 instructions in `update` and `fill_table`); `vars hp` (one change,
    5 -> $21 by `ft_store`); `events 32` (the NMI and the debug port's `$401C` / `$401D` writes by scanline and dot).
  - The negative control, the fixed build under the same queries: `writes hp` total 0, `last-write` null, `became` null, `vars hp` 0
    changes, while `writes idx` still counts its 41 writes.
- **Kept profiles** (only when asked: `--store` / `store: true` / the desktop's Profile selection; a profile for streaming queries writes no
  file at all): `<game folder>\profiles\<id>\` beside `timelines\` (`BROKENDEV_HOME\timeflow\<project>\profiles\` for a project's build),
  **outside the game's 5 GB cap** (`TimeflowStore.GameBytes` leaves `profiles\` out), with their own limits: **512 MB a profile** (past it
  the frames after are not stored, the status says `truncated` and from where; the streaming queries of the same re-run still see every
  frame), **2 GB a profiles folder** (`timeflow.json` `profilesCapBytes`; over it the least recently used unkept one goes, a kept one never;
  still over, the new one stops storing, said), and a **time to live of 60 minutes after the last use** (7 days for a kept one; every query
  or view is a use). `profile.json` (the span, the status, the owner process and its start time, the variables, the symbol files, the
  expiry), `records.bin` (each frame's records, Brotli quality 4, a CRC-32 each: a damaged frame is refused with its number, never
  answered), `frames.idx`. The same queries read a kept profile back (`ProfileReader.Feed`) with the same answers as the stream (7 of 7
  identical in the test).
- **Cleanup**: a profile belongs to the session that made it (the process: a Workshop command, the MCP server, the desktop app) and is deleted
  when that session ends unless it is kept (`ProfileStore.EndSession`: as a Workshop command ends, as the MCP server stops, as the app
  closes, at process exit); a session that was killed or crashed leaves its folder and the next sweep removes it ("process ... is gone: a
  crash or a kill"), never one whose process still runs. A sweep runs before every profile is made, when profiles are listed, when the MCP
  server starts and when the desktop app starts. An empty `profiles\` is removed. The MCP SDK's client kills the server as it closes, so an MCP
  server's unkept profiles go at the next server start (checked), not at its own end.
- **Interfaces**:
  - Workshop: `--timeflow profile --timeline T --from A --to B [--q Q]... [--sym F]... [--var NAME]... [--store [--keep] [--ttl-min M]
    [--limit-mb N]] [--json]`, `profile-query --profile P --q Q...`, `profiles`, `profile-keep [--off]`, `profile-drop --profile P | --all`,
    `profile-sweep`, `profile-measure --rom R`, `profile-rom --out F [--fixed]` (the planted corruption and its `.mlb`).
    `Workshop/Timeflow/TimeflowProfileCli.cs` holds the CLI and the calls the MCP tools make (the same code, the same answers).
  - BrokenDev: `brokendev.ps1 timeflow profile <timeline> -From A -To B -Query "writes hp; vars hp" [-Store] [-Keep] [-TtlMinutes M]
    [-LimitMb N]` (the project's own symbols from its build record), `profile-query <profile> -Query "..."`, `profiles`, `profile-keep`,
    `profile-unkeep`, `profile-drop <profile|all>`, `profile-sweep`. Each brokendev call is its own session: `-Keep` to query it later.
  - MCP (53 tools): `timeflow_profile` (timeline, from, to, queries, symbols, vars, store, keep, ttl_minutes, limit_mb; a new headless machine
    of the timeline's own ROM and cores: no open session is touched) and `timeflow_profile_query` (action query / list / keep / unkeep /
    drop / sweep).
  - The desktop app: Emulator > Debug Windows > Timeflow: **Profile selection** (from the chosen frame, a number of frames, "keep after the
    app closes"). The span re-runs on a headless machine of the timeline's ROM in the background (the user's game plays on; the app's own
    debugger stays detached and unarmed), is kept for the session, and opens in **the Debugger window** (profile mode: the frame's trace of
    the chosen CPU, the chosen instruction's registers and call stack, a frame scrubber, "go to #N") and **the Event View** (profile mode: the
    frame's register writes, DMA runs and interrupts, a frame scrubber). `GET /api/timeflow/profile` reads the last result and the kept
    profiles; no route starts a profile.
- **Measured** (Workshop `--timeflow-test --only pmeasure --pframes 180` and a 120-frame run before it; this machine is shared with other
  agents, so the ranges are the two runs: 180 frames after 300 of the seeded wanderer; "x real time" below 1 is faster than real time; the
  plain replay is the same frames with no debugger hook):

| Game (console) | Instructions + accesses a frame | Streaming (queries only) | Kept (stored) | Bytes a stored frame (raw) | Stored queries |
|---|---|---|---|---|---|
| VRUN (NES, mapper 30) | 9,836 + 29,573 | 125-134 fps = 0.45-0.48x real time (1.9x the plain replay) | 29-57 fps = 1.1-2.1x | 93-106 KB (907 KB) | 7.5-9.5 M records/s |
| NES planted game | 9,934 + 29,830 | 85-127 fps = 0.47-0.71x | 72-74 fps = 0.82-0.84x | 44 KB (838 KB) | 8.1-9.1 M/s |
| Super VRUN (SNES) | 8,576-10,201 + 21,333-25,601 | 70-100 fps = 0.6-0.85x | 24-44 fps = 1.4-2.6x | 132-158 KB (628-757 KB) | 6.6-8.1 M/s |
| Super Mario World (SNES) | 18,831 + 41,939 | 79 fps = 0.76-0.77x | 36-37 fps = 1.6-1.7x | 107-128 KB (1.17 MB) | 7.3-8.3 M/s |
| SNES test ROM (65816 + SPC700) | 13,286 + 43,409 | 91-104 fps = 0.58-0.66x | 40 fps = 1.5x | 123 KB (1.1 MB) | 7.9-8.5 M/s |
| WideMess sample | 28,057 + 73,387 | 41 fps = 1.46x | 16 fps = 3.7x | 395 KB (2.08 MB) | 7.8 M/s |
| Vaults of Obsolete Code (GBC) | 2,578-3,641 + 5,239-7,204 | 239-275 fps = 0.22-0.25x | 95-119 fps = 0.5-0.63x | 60-81 KB (196-273 KB) | 4.8-5.3 M/s |
| Game Boy test ROM | 4,620 + 15,718 | 343-388 fps = 0.15-0.17x | 141-150 fps = 0.4x | 30 KB (482 KB) | 8.0-9.0 M/s |

  So the typical unit (3 s, 180 frames) re-runs in 0.5 to 4.5 s, and kept takes 5 to 20 MB (WideMess 71 MB), well inside the 512 MB limit.
  A stored query reads 5 to 9 million records a second (a frame's 40 to 100 thousand records in 5 to 15 ms). The cost is the hooks (every
  instruction and access through the debugger), not the queries; the SNES pays most against its plain speed (12 to 24x) because its plain
  replay is so fast. The desktop's Profile selection ran VRUN at 17 to 43 fps beside the running game.
- **Found on the way (a T2 finding, fixed in T4: see "T4 as built"):** a NES loaded with its **power-on state** (frame 0 of a timeline that starts at power-on) runs
  its first frame one CPU cycle longer than the real power-on did, so a replay from frame 0 goes off at the keyframe 60 (`overshootCarry`
  and `globalCpuCycle` differ; the picture and RAM do not); from frame 1 on every jump replays in sync. It is something the NES state does
  not hold at power-on (not the debugger: a plain replay does the same). T3 does not hit it: a span in the first block re-runs from the
  machine's own power-on, its state checked equal to the recorded frame 0 (`ProfileTarget.PoweredOn`, said in the report's notes). T2's
  `Replay` from frame 0 still has it.
- **For T0 (the Sega debugger, built in parallel):** the profiler is console-neutral; once `DebugMachines.For` covers the Master System /
  Game Gear / Genesis sessions and `MovieMachine` opens them with the debugger, remove the two refusals (`TimeflowProfiling.NoProfileWhy` in
  `Workshop/Timeflow/TimeflowProfileCli.cs`, the first lines of `ProfileMachine.Open`), add the consoles to `ChipRegisters` (the VDP / PSG /
  YM2612 ports) and to `HistogramQuery`'s cycle unit, and turn `--timeflow-test`'s `pconsoles` Sega check from "refused" to "profiled".
- **Tests** (each with a negative control):
  - `--timeflow-test` groups `profile` (the planted corruption: 23 checks, above), `psync` (a RAM byte changed before frame 75 of the
    re-run: off at frame 75, the warning on every answer, stopped there; PRG RAM changed before frame 100, seen by neither hash: off at the
    keyframe 120, "section prgRAM differs"; negative: the same re-run untouched stays in sync to the end), `pstore` (stored = streamed 7 of
    7; `when` refused on a kept profile; a flipped byte refused with its frame; the size limit; the TTL; the cap: the oldest unkept retires,
    the kept one stays; the session's end; a Workshop child that ended normally left nothing; a child killed while holding its profile left
    it and the next sweep removed it; negative: a sweep leaves a live child's profile; nothing left, `profiles\` removed), `pconsoles` (the
    SNES test ROM with both CPUs' instructions, the Game Boy test ROM; a Master System timeline refused with "T0", nothing written), and
    `pmeasure` (opt-in: the table above).
  - The MCP client test: 11 new checks (the corruption found, last-write / became, nothing stored, store / query / list / drop, an unknown
    profile and an unknown query refused, the Sega refusal, the profile gone after the session).
  - BrokenDev `run-tests`: the T3 case (a project built from `profile-rom`, recorded, profiled with its own symbols; `-Store` without
    `-Keep` leaves nothing after its command; `-Keep`, `profile-query`, `profiles`, `profile-drop`; negative: the fixed build has no write
    to hp, an unknown profile and a span without `-From` fail; nothing left). `run-tests.ps1 -Only <text>` runs just the cases whose name has
    it.
  - `UAT/entrypoints/timeflow-profile-desktop-smoke.ps1` (the real app, scratch config / saves / bugs / BrokenDev home, no sound device,
    the user's `%APPDATA%\BrokenNes` listed before and after): **19 of 19**: the window lists a headless timeline of VRUN, Profile selection
    of frames 200-229 in sync, kept under the scratch home's `profiles\`, the game ran on meanwhile, the app's debugger untouched, the
    Debugger window's trace (9,809 lines of frame 200), a line's registers and stack (`runtime_wait_nmi@31_0 <- step_boss_boom@0_0+$BC`),
    its frame scrubber; the Event View's frame and scrubber; a kept second profile; closing the app deletes the unkept one and keeps the
    kept one; a killed app leaves its profile and the next start sweeps it; nothing left after a drop; the user's APPDATA unchanged.
  - **The runs** (main bd74250, nothing newer to merge): `--timeflow-test` **185 passed, 0 failed, 0 not run** (T1/T2's 130 and T3's 55),
    `--debug-test` 103 of 103, `--movie-test` 69 of 69, the MCP client test **123 of 123**, BrokenDev `run-tests` **151 passed, 0 failed, 0
    skipped** (`-WorkshopExe`, `-McpExe`), UAT **125 pass 0 fail 16 skip 3 info** (the goldens unchanged; the corpus and story ROM folders
    linked from the main checkout for the run, then unlinked), `timeflow-desktop-smoke` (T2) passes, `debug-windows-desktop-smoke` 79 of 79
    (the Debugger and Event View windows gained their profile mode as partial classes; the live mode is unchanged).

## T4 as built (2026-10-30): search over the corpus

- **First, the T2 finding T3 left open (fixed):** the value the NES state did not hold is the **pending reset sequence** (`NES.resetSequencePending`:
  set by power-on and a reset, consumed by the first frame, which on the FIX cores runs the 6502's 8 reset cycles through the PPU and APU before
  the first opcode fetch). A loaded power-on state cleared it, so its first frame's instructions fell 8 cycles off the PPU and the frame ended a
  cycle off (`overshootCarry` 1 instead of 0): the keyframe-60 desync. `NES.SaveState` now writes `"resetSequencePending":true` (first in the
  JSON) only while it is pending, so every other state is byte for byte what it was and an older state (which never has it) loads as before (not
  pending); `LoadState` restores it. `--timeflow-test` group `poweron`: the frame-0 state has the field and frame 1's does not; a replay from a
  loaded frame 0 stays in sync past the keyframes 60, 120 and 180 (200 frames, 203 checks); **negative**: the same state with the field removed
  goes off at the keyframe 60 ("cycleRemainder 1, overshootCarry 1" against 0); a state after a frame has no field, one right after a reset has
  it. T3's power-on re-run (`ProfileTarget.PoweredOn`) is **kept**: it no longer guards the sync, but it still guards frame 0's picture (a loaded
  NES shows the picture from before the load for one frame; only a re-run from the power-on itself compares frame 0's picture), and a timeline
  recorded before T4 (whose frame 0 lacks the field) falls back to the loaded state, said in the notes.
- **Also fixed (the coordinator's report):** `--timeflow-test`'s pstore check "profiles are outside the game's cap" was flaky by one byte (138285
  vs 138286). The byte was `timeline.json` itself: every jump / replay / profile rewrites its `lastUsedUtc`, and System.Text.Json drops a time's
  trailing fraction zeros, so the description's size moved with the clock. Times are now written at a fixed width (7 fraction digits, any older
  form still reads). New checks: the description has one size for a round second and a 7-digit time; negative control: the game folder's whole
  size grows by exactly the profile's bytes. 4 of 4 runs green.
- **The engine** (`Windows/NesEmulator/debug/timeflow`): `TimeflowSearchCondition.cs` (the language), `TimeflowSearchMemory.cs` (`SearchMachine`,
  the state map, the state clause's names), `TimeflowSearch.cs` (the two phases, the hits, the numbers, the JSON). The recorder gained an
  optional index part: `FrameAccesses` (a D1 observer on the main CPU's accesses: per frame, the reads and writes per 256-byte page of every
  canonical place each access touches, its mirrors folded) in a tagged trailer of `extra.tfx` (a T1-T3 record ends before it and still reads;
  `TimelineMeta.Accesses` says it is there). `TimelineReader.ExtrasScan` (the extras file read once) and `StateView` (a frame's state without a copy,
  its hash still checked) serve the scan. `ProfileMachine.Open(dir, symbols, requireDebugger: false)` opens a Sega timeline without the debugger
  (the desktop's search machine).
- **The condition language** (the same text everywhere): clauses joined by `and`, each may start with `not`; every clause is about one frame:
  - `exec <addr|range|symbol> [cpu C] [if <expr>]`: an instruction there ran in the frame (D1's specs: all mirrors, banked symbols, `prg:` offsets);
  - `read|write|access <spec> [cpu C] [if <expr>]`: a memory breakpoint (CPU and DMA; `value`, `address`, `source` in the expression);
  - `state <expr>`: a D1 expression on memory **at the end of the frame**, and on the timeline's watched variables by name (`alive == 1`); memory
    by CPU address, by the debugger's spaces (`[wram:$10]`) or by a memory domain (`[WRAM:$10]`, the only way on the Sega consoles until T0).
    Registers and scanline / dot / cycle are refused ("a state is memory at the end of a frame: a register at an instruction is a breakpoint's
    condition");
  - `finding [<text>]`: a D4 finding first seen in the frame;
  - `picture changed|still`; `color #RRGGBB [in X Y W H] [min N] [tol T]`: at least N pixels of the colour (each channel within T);
  - `sound silent [below L] | sound below L | sound above L`: the frame's sound peak (0 to 1); `channel <name> silent|playing`: D8's channel
    monitor at the frame's end.
  A hit is a frame where every clause holds; with a positive breakpoint clause (the anchor), each of its firings in such a frame is a hit.
- **Phase 1, coarse** (the index and the recorded states; each clause says No, Maybe or Yes for each frame, cheapest clause first, a No stops):
  code breakpoints by the frame's **coverage** (the ROM code the main CPU ran: No when the range is not in it; a non-ROM address or another CPU:
  Maybe, said); memory breakpoints by the **access counts** (No when no access of the kind touched the range's 256-byte pages; a timeline without
  them: Maybe, said; the changed bytes were not used: a write of the same value changes nothing and would be missed); `state` **exactly from the
  recorded states**: the state at the frame's end is the next frame's recorded state, and each memory byte the expression reads is located in it
  by measuring on the machine (the byte changed, the state taken, the one state byte that changed with the new value is its place, the byte put
  back and the state proved unchanged; a byte that is not exactly one state byte, a register, ROM, a port, is unmappable, said, and that clause
  goes to the re-run; one map per state layout); `finding` and `picture changed|still` **exactly from the index**; `color`, `sound`, `channel`:
  Maybe. A frame every clause says Yes for (and no anchor) is a hit **from the index alone** (`via: index`, no re-run).
- **Phase 2, exact** (only the candidates): the unsure frames (and, with an anchor, every candidate, to find each firing) are grouped into runs
  (a gap of up to N = 60 frames is run through) and re-run from the frame before the run on the game's machine (one per game and cores):
  with a breakpoint clause as a **T3 profiled span** (`TimeflowProfiler.Run` with a `SearchSink`: every instruction and access of the span, the
  `if` evaluated on the running machine at the firing, the call stack known from the keyframe; no variables followed), else as a plain replay
  (T2's jump and the recorded pads). Every re-run frame is checked against the recording (picture and RAM, the whole state at keyframes): a desync
  stops that timeline's search, said. At each frame's end: a `state` on the machine's own memory, a `color` on its picture (once per distinct
  picture hash: a frame whose picture was already rendered is decided from the cache without a re-run; a picture that is not the recorded one,
  the NES's held picture after a load, is undecided, said), a `sound` on the frame's samples, a `channel` on D8's monitor. Self-checks on every
  run: a frame the coarse phase ruled out that a re-run finds true is counted (`coarseMissed`, must be 0, reported as a bug) and a Yes / No the
  exact phase contradicts (`contradicted`, must be 0).
- **A hit**: timeline, frame, `via` (index / re-run), and for a firing the cycle, CPU, PC, scanline, dot, the instruction, the access (address,
  symbol, value, by DMA), the registers, the call stack (4 frames) and its T3 position `#N`; for a frame-level hit the machine's cycle and PC at
  the frame's end; and what each clause saw ("[enemy_alive] != 0 = 1", "0 pixel(s) of #982220", "sound peak 0.000", "finding: ..."). Each is a
  jump target (`jump: {timeline, frame}`: T2's jump puts the machine at the frame's start) and a span to profile (`profile: {timeline, from, to}`).
  The report also gives, per clause, how the coarse phase decided it, its No / Maybe / Yes counts and its **false-candidate rate** (of the frames
  it was unsure of, how many did not hold), per timeline what happened (skipped and why, desync), and the cost (frames scanned and the coarse fps,
  candidates, frames re-run and their fps, the time to the first hit and in all). `max_hits` (100) stops the exact phase and counts the candidates
  left; `coarse_only` gives the candidates and the index's own hits with nothing re-run; `from` / `to` limit the frames.
- **What is refused, said, never guessed:** a breakpoint or a channel on a Sega console ("needs the debugger on the sms's CPU, which is milestone
  T0": the timeline is skipped, its state / picture / sound clauses work); a CPU address on a Sega console ("name a memory domain, [RAM:$10]");
  a `finding` on a timeline recorded without the checks (skipped: "record with the checks"); an unknown clause, CPU, symbol or channel (the
  search's error, with the language or the channels).
- **Interfaces:**
  - Workshop: `--timeflow search --q "CONDITION" [--timeline T]... [--project P | --game-dir D | --rom R] [--sym F]... [--max-hits N] [--coarse]
    [--from A] [--to B] [--json]` (no timeline / project / game / ROM: the whole corpus; exit 0 with hits, 1 none), `record ... --accesses` (the
    access counts; `--coverage`, `--checks` as before), `search-measure` (below), `search-rom --out F` (the search test game and its .mlb).
    `Workshop/Timeflow/TimeflowSearchCli.cs` is the code the MCP tool calls.
  - BrokenDev: `brokendev.ps1 timeflow search [project|timeline] -Query "CONDITION" [-From A] [-To B] [-MaxHits N] [-CoarseOnly]` (the project's
    own symbols), `timeflow record <project> ... -Index` (coverage, access counts and the checks).
  - MCP (54 tools): `timeflow_search` (condition, timeline[], project, game_dir, symbols, from, to, max_hits, coarse_only; new headless machines of
    each timeline's own ROM and cores, no open session touched), and `timeflow_record` action start with `index: true` on a headless session.
  - The desktop app: Emulator > Debug Windows > Timeflow: a **search box** (Enter or Search; Cancel while it runs) over the running game's
    timelines; the re-runs happen on headless machines in the background (the user's game plays on, the app's debugger untouched); the hits
    arrive in a **hit list** as they are found and are **marked on the scrubber** (a strip under it; a click goes to the nearest hit); choosing a hit
    chooses its timeline and puts the scrubber at its frame (then Jump, or Profile selection). `GET /api/timeflow/search` reads the last search; no
    route starts one.
- **The test game** (`Workshop/Timeflow/TimeflowSearchRoms.cs`, `search-rom`): NROM, an enemy sprite drawn red at line 100 while alive (64 frames
  alive, 64 dead); **the planted bug**: alive with `(ct & $3F)` in $20-$27, `hide_enemy` moves the sprite off screen (alive but not visible), writes
  `bug_mark` $5A and reads `secret`; the near misses: dead, `hide_enemy_dead` (a neighbouring routine) also hides it, and in the same window of a
  dead period `bug_mark` is written $A5; `same_byte` is written 7 every 16 frames (never changing it); pulse 1 plays for 16 frames of 128; at
  `ct == $50` it reads $0300, never written (`nes.ram-uninit`); `never_called` and `never` ($0600) never run / are never written.
- **Measured** (`Workshop --timeflow search-measure --timelines 20 --frames 3600 --real`, this machine shared with other agents and with this
  branch's own regression runs, so the speeds are lower bounds): a corpus of 20 timelines x 3600 frames of the search test game, recorded with
  the full index (coverage, access counts, checks: 104 fps), 72,000 frames (20 minutes of play), 15.1 MB (210 bytes a frame). The coarse phase
  over the whole corpus; the exact phase over two of its timelines (7,200 frames; sound and channel over one); the first hit over the whole corpus:

| Condition (kind) | Coarse over 72,000 frames | Candidates (of 7,200) | Hits | Frames re-run | False candidates | First hit | Total (7,200 frames) |
|---|---|---|---|---|---|---|---|
| `exec hide_enemy` (code) | 266k fps (271 ms) | 446 | 448 (firings) | 2,242 | 0 % | 205 ms | 13.4 s |
| `exec never_called` (code, never) | 426k fps | 0 | 0 | 0 | - | - | 19 ms |
| `exec hide_enemy if [enemy_alive] == 0` (never true) | 519k fps | 446 | 0 | 2,242 | 100 % | - | 13.0 s |
| `write bug_mark if value == $5A` (memory) | 391k fps | 895 | 448 | 7,103 | 50 % (the $A5 near misses) | 473 ms | 77 s |
| `write same_byte` (writes that change nothing) | 389k fps | 451 | 451 | 7,185 | 0 % | 116 ms | 81 s |
| `read secret` | 391k fps | 447 | 449 | 2,242 | 0 % | 52 ms | 32 s |
| `write never` | 430k fps | 0 | 0 | 0 | - | - | 23 ms |
| `state [enemy_alive] == 1 && [enemy_y] == $F0` | 49k fps (1.5 s, the states decoded) | 448 | 446 | 4 | (1 frame: the last) | 71 ms | 0.4 s |
| `finding ram-uninit` | 368k fps | 4 | 4 | 0 | 0 % | 9 ms | 19 ms |
| `picture changed` | 8.8M fps | 225 | 225 | 0 | 0 % | 1 ms | 2 ms |
| `color #982220` | 2.2M fps | 7,200 | 3,169 | 8 (2 distinct pictures) | 56 % | 84 ms | 0.25 s |
| `state [enemy_alive] != 0 and not color #982220` (the user's example) | 15k fps | 3,620 | 563 | 11 | 0 % / 16 % | 167 ms | 1.4 s |
| `sound above 0.05` | 3.1M fps | 3,600 | 484 | 3,600 | 87 % | 105 ms | 21 s |
| `channel "Pulse 1" playing` | 1.9M fps | 3,600 | 222 | 3,600 | 94 % | 129 ms | 22 s |
| `write bug_mark and state [enemy_alive] == 0` | 33k fps | 449 | 449 | 2,223 | 0 % / 0 % | 92 ms | 26 s |

  So the coarse scan reads the index at 266k to 520k frames a second (1.3 to 2.4 hours of play a second) and the recorded states at 15k to 49k;
  code, state, finding and picture-changed clauses leave almost no false candidates; a memory clause's false candidates are the frames whose page
  was touched by other bytes or values (here 50 %); the exact phase is the cost: a profiled re-run runs ~170 frames a second, and memory
  candidates every 8 frames make it re-run nearly everything (the runs merge over 60-frame gaps). Self-checks over every search: `coarseMissed` 0,
  `contradicted` 0. (Hits can exceed candidates for breakpoints: several firings in a frame.)
- **Open question 2 (thumbnails / audio levels in the index), answered with numbers: not in T4.** The user's games, 3600 frames each with the
  full index (`--real`; recording fps here: VRUN plain 56 / with coverage 45 / with coverage and access counts 38, Super VRUN 196 / 115 / 66, both
  slower than T1's because of this machine's load; bytes a frame VRUN 272 / 650 / 1,050, Super VRUN 9.6 / 10.2 / 10.9 KB):
  - pictures: VRUN shows 1,302 distinct pictures in 3,600 frames (36 %), Super VRUN 2,191 (61 %); a colour search re-renders each distinct
    picture once at 5.7 / 6.3 ms (VRUN `color #D48820`: 634 hits, 7.5 s; Super VRUN `color #8B10B4`: 2,776 hits, 13.9 s). A per-frame colour
    summary (a 256-bit set of the frame's colours, 32 bytes a frame) would rule out 82 % of VRUN's frames (2,966 of 3,600) but only 23 % of Super
    VRUN's (824), costs 0.28-0.30 ms a frame to make while recording (VRUN's capture is already 4 ms) and helps only whole-picture colour clauses
    (not a region, not "this sprite drawn"). The decision: **re-render, deduplicated by picture hash, is the T4 method**; a picture clause usually
    comes with a state or breakpoint clause that narrows the candidates first (the user's example: 11 frames re-run of 7,200). Worth adding with
    T6's corpus if whole-corpus picture-only searches on NES-like games become common (the numbers above: 5x faster on VRUN for +12 % bytes).
  - sound: a sound clause re-runs every candidate frame (VRUN 120 fps, Super VRUN 345 fps: 600 frames in 5.0 / 1.8 s); a per-frame audio level
    would make it index-only, but the recorder never sees the sound (the host drains it for its device), so it needs a tap in every host: left for
    T6 too, with this cost as the reason. Picture changed / still from the stored hashes is exact and free (5-9M frames a second).
- **Tests** (each with a negative control):
  - `--timeflow-test` group `search` (39 checks, the search test game, 600 frames recorded with coverage, access counts, checks and a watched
    variable; every expected frame comes from a plain run of the game read directly (its RAM, its frame buffer, its sound, the channel monitor),
    never from the search code): code (`exec hide_enemy` exactly the frames it ran, at its first instruction, with the stack, the coverage making
    the candidates exactly those frames; near miss: `exec hide_enemy_dead` only dead frames; never: `exec never_called` 0 hits, 0 candidates,
    nothing re-run; `exec hide_enemy if [enemy_alive] == 0` 0 hits, its 39 candidates re-run and rejected); memory (`write bug_mark if value == $5A`
    exactly the planted writes, the $A5 near misses candidates and rejected; `write same_byte` finds writes that never change the byte; `read
    secret` the planted reads (and the reset's dummy read, STA abs,X, in frame 0); never: `write never`, a value never written); state (exact from
    the recorded states, only the last frame re-run; a watched variable by name; never: `== 7`; a register refused); finding (the one frame, from
    the index; never: `stack-overflow`); picture (`picture changed` from the index; `color` re-rendered once per distinct picture; near miss: the
    colour in a region left of the enemy; never: a colour not on screen); **the user's example** `state [enemy_alive] != 0 and not color #982220`:
    exactly the alive-and-not-drawn frames, 268 alive-and-drawn and 272 dead-and-hidden near misses not reported, every planted frame from its
    window's third on a hit (the picture lags the state by two frames, and those lag frames are the others); sound (`sound above 0.05` against
    the plain run's peaks; `channel "Pulse 1" playing` against the monitor; never: above 0.99; an unknown channel refused); combined (a breakpoint
    filtered by a state: the near miss only; `exec update and not exec hide_enemy and state ... and not color ...`: only the lag frames); the hit
    limit (5 hits, the candidates left counted, the first hit's time); coarse only; a timeline **without** coverage / access counts (every frame
    re-run, the same hits, said) and without checks (a finding search skips it, saying why); from / to. Group `sconsoles` (14 checks): the SNES
    and Game Boy test ROMs and a Master System program: `state [<domain>:$X] == V` exactly (by the recorded states, not re-runs), `and picture
    still`, never (`== $1FF`); Master System: `exec` skipped "T0", a CPU address skipped "name a memory domain". Group `poweron` (4 checks, the T2
    fix) and the pstore fix's 2 new checks.
  - The MCP client test: 7 new checks (`timeflow_record` with `index`, `timeflow_search` exec / the user's example / never / the near miss / a bad
    condition, the Sega breakpoint skipped until T0 and a Sega picture search).
  - BrokenDev `run-tests`: the T4 case (a project built from `search-rom`, recorded with `-Index`, searched by project and by timeline with its
    own symbols, text and JSON; negative: a condition that never holds (exit 1, nothing re-run), the near miss not reported, an unknown clause fails).
  - `UAT/entrypoints/timeflow-search-desktop-smoke.ps1` (the real app, scratch config / saves / bugs / BrokenDev home, no sound device, the user's
    `%APPDATA%\BrokenNes` listed before and after): **13 of 13**: the window lists a headless timeline with the index; `exec hide_enemy` gives the
    16 planted frames (hide_enemy's first instruction, $C102), listed, the game ran on meanwhile (99 frames), choosing the first hit puts the frame
    box at 33, the hit strip; the user's example (each frame once); never 0 hits; a bad condition refused with the language; the app's debugger
    untouched; the user's APPDATA unchanged.
  - **The runs** (main c9c031b, nothing newer to merge): `--timeflow-test` **244 passed, 0 failed, 0 not run** (T1-T3's 185 and T4's 59),
    `--debug-test` 103 of 103, `--movie-test` 69 of 69, `--bug-test` 48 of 48, the MCP client test **131 of 131** (with `--repo`), BrokenDev `run-tests` **152 passed, 0 failed, 0 skipped** (`-WorkshopExe`, `-McpExe`),
    UAT **125 pass 0 fail 16 skip 3 info** (the goldens unchanged) (the corpus and story ROM folders linked from the main checkout for the run, then unlinked), timeflow-search-desktop-smoke 13 of 13.
    `--snes-state-test` / `--md-state-test` not run: nothing in the SNES or Sega states changed.
- **For T0 (the Sega debugger, in parallel):** the search is console-neutral; once `MovieMachine` / `ProfileMachine` open the Sega consoles with
  the debugger (and `DebugMachines.For` covers them), `TimeflowSearching.OpenMachine` and `ProfileMachine.Open(..., requireDebugger: false)` pass it
  through unchanged: breakpoint and channel clauses then work there (the bind refusal in `TimeflowSearch.Bind` keys on `HasDebugger`, no console
  list), `FrameCoverage` / `FrameAccesses` record their index, and CPU addresses in a state clause are measured like the others. `DebugSound` needs
  a PSG / YM2612 monitor for `channel`. Turn `sconsoles`' two "T0" checks into searches that find.
- **Left for later:** a colour summary and an audio level in the index (open question 2's numbers above: T6); the exact phase's re-run cost for
  memory candidates on busy pages (finer pages, or the access counts by exact address for the symbols a project watches); searching a running
  game the user plays (a search is over recorded timelines only); a `timeflow_search` that keeps its re-runs as profiles (today it re-runs and
  answers; profile a hit with `timeflow_profile`).
