# BrokenDev Timeflow: rewind, deep profiling, hotpatch and a searchable gameplay corpus

Status: **T1 (the timeline format) and T2 (rewind and resume) built** on 2026-10-30, every console (see "T1 as built" and "T2 as built"
at the end); the Sega consoles have rewind without profiling until T0. Next: T0 (the debugger on the Z80 and the 68000), then T3 (profiled
spans).

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
2. Whether picture / sound search needs per-frame thumbnails or audio levels in the index, or re-rendering candidates is enough.
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

## T0 as built (2026-10-30): the debugger on the Sega CPUs

- **What it covers.** The D1 engine (`Windows/NesEmulator/debug`) now reaches every console: `DebugMachines.For` makes a `SmsDebugMachine`
  for the Master System, Game Gear and SG-1000 (one CPU, `main`, the Z80) and an `MdDebugMachine` for the Genesis (`main` the 68000, `z80`
  its sound CPU when the sound unit is the Genesis's own; an NES-chip sound bridge has no Z80 and says so in `Limitations`). Breakpoints
  (exec / read / write, conditions, hit counts, tracepoints), stepping (into / over / out, scanline, frame, run to), the call stack (CALL,
  RST, JSR, BSR, TRAP and the interrupts: `NMI` / `IRQ` on the Z80, `VINT` / `HINT` / `EXTINT` / `TRACE` on the 68000), the trace ring and
  trace files, registers read and written (the Z80's whole set with `AF2`..`HL2`, `IXH`..., `IFF1/2`, `IM`; the 68000's `D0-D7`, `A0-A7`,
  `SP`, `USP`, `SSP`, `SR`, `CCR`, `PC`: setting PC refills the prefetch queue), disassembly, symbols, the memory domains (plus `BUS`, and
  the Genesis's `Z80BUS`) and the D4 observer hooks (steps, accesses, calls, returns, writes landed) that the coverage map and Timeflow's
  index use. New files: `debug/SegaDebugMachine.cs`, `debug/SegaDisasm.cs` (`DisasmZ80`, `DisasmM68k`), `debug/checks/SegaChecks.cs`,
  `sega/SegaDebugHooks.cs` (the two hook interfaces the boards hold).
- **How each CPU is adapted (zero cost when off).** The boards gained null fields, as the NES / SNES / Game Boy did:
  - `BOARD_SMS.DebugHook` (per instruction): `RunLine` keeps its plain loop while both it and the old tracer hook are null (one more field
    test per line); `BOARD_SMS.DebugTap` (data reads, writes, ports): one field test per data access in `SmsBus.Read`, `WriteMemory` and
    the port routines. Opcode fetches are not data reads (they stay on the untouched `FetchOpcode` path).
  - `BOARD_MD.DebugHook` (one field test per 68000 instruction, beside the existing tracer hook) and `BOARD_MD.DebugTap` (one per bus
    cycle and per DMA word in `ReadBusWord`). A word read at the 68000's program counter is the prefetch or an extension word, not data.
  - `MdSoundUnit.DebugHook` / `DebugTap`: `RunInner` picks a debugger loop (the hook at each boundary, no polling-loop fast-forward)
    only while the hook is set; setting it brings a parked loop up to date first (the fast-forward is exact, so this changes nothing).
  - The cores gained read-only properties only: `Z80.BoundaryKind` (what the next T-state starts: an instruction, an NMI or interrupt
    response, a halted NOP, read from the program the boundary installed), `M68k.TraceDue`, `M68k.DecodeGroup` (the core's own decode
    table, which the disassembler uses for legality and lengths); the cartridges `DebugRomIndex` / `DebugRamIndex` / `DebugSramIndex`,
    `SmsVdp.DebugAddress` / `DebugCode`, `BOARD_MD.DebugPeek`, `MdSoundUnit.DebugWindowOpen`. No state is added to any save state.
- **Mirrors and canonical places.** Master System: work RAM `$E000-$FFFF` onto `$C000-$DFFF` (and `ram:` by offset), ROM and cartridge
  RAM by offset through the mapper's slots (a banked symbol `02:4000` is `rom:$8000` and never breaks while bank 1 is mapped), the I/O
  ports by the port the console decodes (`io:$BE` is every mirror of the data port: `OUT ($80)` hits it; the Game Gear's `$00-$06` and the
  FM unit's `$F0-$F2` are their own), and VRAM / colour RAM written through the data port (the VDP's address register says where).
  Genesis 68000: work RAM `$E00000-$FFFFFF` onto `$FF0000` (`ram:` by offset), the cartridge by ROM / battery-RAM offset (the SSF2 banks
  as mapped), `zram:` the Z80's RAM through `$A00000`; the Z80: its RAM's mirror `$2000-$3FFF`, the bank window by its 68000 address
  (`m68k:`, and `rom:` / `wram:` behind it). **Each CPU's watches see the other CPU's accesses into its memory** (source `OtherCpu`, the new
  `AccessSource` value 5): a 68000 read watch on `rom:$100` sees the Z80's bank-window read, a Z80 write watch on `zram:$5` sees the 68000
  load the sound driver. The 68000's watches see the VDP's DMA reads of ROM and work RAM (source `Dma`). A symbol in work RAM binds to
  the RAM (so a watch covers its size through every mirror). I/O ports reach the observers at `$10000 + port`.
- **Shared code, kept minimal and additive:** `DebugCpu.InstructionBytes` (a 68000 instruction runs to ten bytes, past the four a trace
  record keeps: the four disassembly listings use it; the 68000's disassembler reads the rest from memory for a trace line),
  `Debugger.OnPortAccess`, the trace line's byte span clamped to the record's four, the call-stack name for the 68000 (JSR / BSR /
  TRAP), `AccessSource.OtherCpu`, `DebugSymbols`: GNU `nm` output (SGDK's `symbol.txt`, `nm -S` sizes), WLA-DX / devkitSMS banks for
  `sms` / `gg`, Mesen's `Sms*` label types. Registers that do not fit a trace record (the Z80's IX / IY / shadows, the 68000's D0-A6)
  are kept beside the ring by a sequence number the record carries (`TraceSide`).
- **The checks (D4) on the Sega consoles**, the first rules of their catalogue: `sms.ram-uninit`, `sms.lag-frame` (also the Game Gear),
  `md.ram-uninit` (by the 68000, the Z80's bank window or a DMA), `md.zram-uninit` (the Z80 reading its RAM before anyone wrote it),
  `md.z80-bus` (the Z80's space used by the 68000 without its bus: reads give $FF, writes are lost), `md.lag-frame`; the profile. So the
  index's findings column and the MCP's `checks:true` work there too. The Sega consoles have no debug port (D7).
- **Interfaces.** MCP: the Sega headless sessions take every debugger tool (the refusal is gone; `cpu: "z80"` on the Genesis), the
  server's instructions say so; `step_back` by instructions works there. Desktop: Emulator > Debug Windows > Debugger (the CPU picker
  offers the Z80), Memory, Event View (the Master System's ports as `port $BE`, the Genesis's I/O, VDP and bus registers) through the same
  `DebuggerHub`; the symbol file beside a ROM: `.sym` / `.noi` (Master System, Game Gear), `.sym` / `.txt` / `symbol.txt` (Genesis). Workshop:
  `MovieMachine` (movies, bug replays, Timeflow) attaches the debugger on the Sega consoles, so `--timeflow record --coverage --var ...`
  and the course variables work there. HTTP: `/api/debug/*` as before (Windows/webapi/README.md).
