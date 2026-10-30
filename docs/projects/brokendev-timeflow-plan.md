# BrokenDev Timeflow: rewind, deep profiling, hotpatch and a searchable gameplay corpus

Status: plan (interview of 2026-10-30). Nothing built yet.

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

1. The exact keyframe interval and page size per console (from T1's measurements).
2. Whether picture / sound search needs per-frame thumbnails or audio levels in the index, or re-rendering candidates is enough.
3. Which of the user's games is T7's real-bug proof (Super VRUN's invisible enemies, if still open by then, is a natural candidate).
4. How the desktop app asks the user to accept an agent's recording / play request (a non-blocking prompt in the app, like the
   no-sound notice).
5. Lite: whether Timeflow recording exists there (memory and storage are tighter in a browser) or stays desktop / Workshop / MCP.
