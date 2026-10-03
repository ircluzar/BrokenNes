# BrokenNes Workshop

A deliberately minimal WinForms tool for developing and debugging the shared NES cores in
`Windows/NesEmulator/` — load a ROM, step it frame-by-frame or instruction-by-instruction,
inspect CPU registers and memory, switch cores, save/load state. No shaders, no achievements,
no deck builder, no corruptor, no WebView2/Webmodules. Where the desktop app (`Windows/`) is
the full game experience and `Web/` is a barebones player, Workshop exists purely to make core
development itself easier.

Workshop is also the intended vehicle for benchmarking BrokenNes's accuracy against
[AccuracyCoin](https://github.com/100thCoin/AccuracyCoin) — see the headless mode below, which
shares the exact same linked cores as the interactive UI.

## CLI modes at a glance — what you can actually run right now

`Program.cs` dispatches on `argv[0]`. Twelve modes exist. One is blocked on a missing asset; five
draw their inputs from **a separate project that is not in this repo** (and a sixth,
`--verify-selfplay-movie`, consumes one of those five's output). See the notes under the table.

| Mode (`argv[0]`) | What it does | Needs, beyond the built `.exe` | Runnable on this machine? |
| --- | --- | --- | --- |
| `--romtest` | "Does this ROM run?" gate: exit code + FNV-1a64 framebuffer hash, optional scripted input | any `.nes` | **Yes** |
| `--snestest` | SFC (SNES) core-family spec verifier: runs gilyon/snes-tests and lists every failing test with its inputs and expected output | a `.sfc` from `Windows/Resources/snes-test-roms` | **Yes** |
| `--snes` | Interactive SNES player window (SFC cores): keyboard + XInput, audio-paced, battery saves | any `.sfc`/`.smc` (bring your own) | **Yes** |
| `--snesrun` | SFC "how far does this game get?" probe: N frames, scripted input, PNGs at chosen frames, hottest PCs (hang loops), sound-CPU upload state | any `.sfc`/`.smc` (bring your own) | **Yes** |
| `--trace` | Per-frame CPU regs + work-RAM hash in a shared, emulator-independent format, for diffing against Mesen | any `.nes` | **Yes** |
| `--corrupt` | The VRUN corruption oracle: late-PPU-write / CHR-scatter / nametable-floor checks, with fault injection to prove each one fires | VRUN `game.nes` (VRUN-specific) | **Yes** |
| `--headless` | One-shot run → SHA-256 frame hash, CPU regs, optional PNG | any `.nes` | **Yes** |
| `--benchmark` | `NES.RunBenchmarks()` speed numbers (the FIX accuracy-vs-speed gate) | any `.nes` | **Yes** |
| `--diag-savestate-roundtrip` | Is `SaveState`/`LoadState` itself lossy? Frame-by-frame | any `.nes` | **Yes** |
| `--irqtrace` | Instruction ring-buffer trace / raw memory dump around a boot hang | any `.nes` | **Yes** |
| `--playmovie` | Replay an FCEUX `.fm2`; optional re-record + `nesreflex-raw-v2` dump | a `.fm2` + its ROM → **external** | **Yes** (external tree present) |
| `--index-tas-library` | MD5-pair movies ↔ ROMs into a manifest (name matching is not safe) | ROM dir + movie dir → **external** | **Yes** (external tree present) |
| `--tas-baseline-batch` | Replay every manifest pair, dump a trace each, classify OK/CRASHED/INCOMPLETE | manifest + **external** | **Yes** (external tree present) |
| `--compare-dumps` | Diff a BrokenNes trace against an FCEUX-fork trace, field by field | one FCEUX-produced `.raw` → **external** | **Yes** (external tree present) |
| `--selfplay` | Live SMB1 self-play against the NESReflex Python inference server | Python venv + model checkpoint + metadata + SMB1 ROM → **external** | **Yes** (external tree present) |
| `--verify-selfplay-movie` | Replay a self-play `.fm2` against its `.witness.json`, frame for frame | output of a `--selfplay` run | **Yes** |
| `--accuracycoin` | The 141-test [AccuracyCoin](https://github.com/100thCoin/AccuracyCoin) harness, single combo or full matrix | **`AccuracyCoin.nes`** | **No — asset absent** |

Fourteen, counting `--trace` and `--corrupt` (both added after that count was written).

**`AccuracyCoin.nes` is not on this machine.** It is MIT-licensed third-party (~40KB) and
deliberately not committed here. A whole-of-`C:` search on 2026-09-03 found only this repo's own
`AccuracyCoinCli.cs` / `AccuracyCoinRunner.cs` / `AccuracyCoinTests.cs` — zero `.nes`. Without it
`--accuracycoin` exits 2 (`Failed to read ROM`). To use it, build or download the ROM from
[100thCoin/AccuracyCoin](https://github.com/100thCoin/AccuracyCoin) and pass its path to `--rom`.
Everything else in that section below is real, working code waiting on that one file.

**"external" above means the `ML_NesPlayer` side project — present, but not part of this repo.**
It lives at `C:\Users\philt\OneDrive\Documents\PROJECTS\!!! Experiments\ML_NesPlayer` (note the
`!!! Experiments` folder — it is *not* directly under `PROJECTS\`, which is an easy way to
wrongly conclude it is missing). It is not version-controlled with BrokenNes, so treat it as a
machine-local dependency that can disappear. What the **external**-marked modes consume from it:

| Asset | Path under `…\!!! Experiments\ML_NesPlayer\` | Verified 2026-09-03 |
| --- | --- | --- |
| ROM library (758 `.nes`) | `TAS\Nintendo Entertainment System\` | present |
| `.fm2` movie library (348 in `tas\`, 407 total) | `TAS\tas\` | present |
| Custom FCEUX fork (source + built `fceux64.exe`) | `TAS\fceux_custom\` | present |
| FCEUX-produced `nesreflex-raw-v2` dumps (210 `.raw`) | `data\raw_dumps_ppu_v2\batch\` | present |
| Python venv (3.10.6, torch 2.11.0+cu128, CUDA available) | `.venv\Scripts\python.exe` | present |
| Inference server (protocol v2, unmodified) | `scripts\nesreflex_inference_server.py` | present |
| Model checkpoint (19,802,413 params, 227MB) | `data\checkpoints\overnight_ppu_v2\epoch_0001.pt` | present |
| Metadata parquet (277MB) | `data\metadata_ppu_v2\samples.parquet` | present |

That path is also hardcoded in two places: `WorkshopForm.cs:39` (the interactive "Self-Play
(SMB1)" checkbox's ROM auto-load) and `RunSmb1SelfPlay.bat` (`ML_ROOT`). If the folder moves,
those two need editing, not just your command line.

**How the table was checked (2026-09-03):** every "Yes" row was *run*, not assumed — `--romtest`
/`--headless`/`--benchmark`/`--irqtrace`/`--diag-savestate-roundtrip` on
`Windows/Data/story/page1_jimmy.nes`; `--playmovie` on `meshuggah-ghostbusters.fm2` +
`Ghostbusters (U).nes` (300 frames, dump written); `--index-tas-library` over the real library
(348 movies → 192 checksum-matched pairs); `--tas-baseline-batch` on a 2-pair manifest (both
`OK`, full movies, 19,677 and 48,401 frames); `--compare-dumps` against a real FCEUX-fork
`pair_0002.raw`; `--selfplay` for 600 frames against the live server with the real checkpoint;
`--verify-selfplay-movie` on that run's export (`Pass: true`, 600/600 frames, 0 mismatches).
`--accuracycoin` is the only mode that could not be run.

## Relationship to the other two variants

`Windows/NesEmulator/` remains the single source of truth for every core. Workshop links those
files with `<Compile Include>` — nothing is copied or forked. See
[Web/README.md](../Web/README.md) for the full sharing model (which folders are wildcard-globbed
and reach Workshop/Web automatically on the next build vs. which are explicit lists needing a
one-line csproj edit) — it applies identically here.

Unlike `Web/`, Workshop **is** in `BrokenNes.sln`: it's a plain `net10.0-windows` WinForms
executable with none of `Web/`'s AOT/IL-trimming/wasm-tools-workload complexity, so there's no
reason to keep it out. `dotnet build` at the repo root now builds both the desktop app and
Workshop.

One difference from `Web/`'s link list: `board/ClockRegistry.cs` and `clocks/**` (the
web-exclusive clock system) are **not** linked here — Workshop drives frames with a plain
`System.Windows.Forms.Timer`, the same pattern `Windows/MainForm/MainForm.Emulation.cs` already
uses. `board/NesMemoryExtensions.cs` **is** linked (Web deliberately excludes it) — it provides
the peek/poke/register plumbing the memory viewer needs.

## The one shared-core change

`NES.cs` gained two small, purely additive methods, used by both the desktop app and Web too
since it's the same file:

- **`StepInstruction()`** — executes exactly one CPU instruction and correctly flushes PPU/APU
  by the right cycle count via the existing private `FlushBatch` helper (whose own comment
  anticipated this: *"Consolidated flush helper so later event-based stepping can reuse it"*).
  It intentionally does not replicate `RunFrame()`'s event-scheduling optimizations
  (`nextPpuEventCycle`/`nextIrqCycle` bookkeeping) — those exist to batch many instructions
  efficiently and aren't needed for one instruction at a time.
- **`GetSpeedConfig()`** — exposes `Bus.SpeedConfig` (previously unreachable outside `NES.cs`).
  `SpeedConfig` ships several accuracy-for-speed shortcuts on by default (approximate OAM DMA
  stall timing, idle-loop skip, blank-scanline skip, adaptive batching, ...), applied identically
  under *every* CPU/PPU/APU core combination. Both the "Strict accuracy" checkbox in the UI and
  `--strict` in headless mode use this to turn them off — without it, an accuracy-testing failure
  could get misattributed to whichever core happened to be selected when it was actually a global
  Bus-level shortcut. Verified: toggling it changes the frame hash on a real ROM.

Both `Windows/` and `Web/` were rebuilt from clean after this change to confirm neither regressed.

## Run (interactive)

```bash
dotnet run --project Workshop/BrokenNes.Workshop.csproj
```

Boots `Windows/Resources/test.nes` automatically (same embedded smoke-test ROM the desktop app
ships) so there's always something loaded. Use "Open ROM..." for anything else.

Controls: D-pad `Arrows`/`WASD`, A `X`, B `Z`, Select `Space`, Start `Enter` — same mapping as
`Web/`, and the same index order documented in `Windows/NesEmulator/board/Input.cs:10`
(`0=A 1=B 2=Select 3=Start 4=Up 5=Down 6=Left 7=Right`).

## Run (headless)

Shares the exact same linked cores and the exact same `DirectBitmap` rendering conversion as the
interactive UI — no window, no WinForms message loop. `NES.RunFrame()`/`GetFrameBuffer()` are
pure computation (verified: no `System.Windows.Forms`/SharpDX/wall-clock dependency anywhere in
the call path), so nothing special is needed to run without a GUI.

```bash
BrokenNes.Workshop.exe --headless --rom path.nes [--frames N] [--cpu ID] [--ppu ID] [--apu ID] \
    [--out results.json] [--strict] [--screenshot out.png]
```

Emits JSON: ROM name, requested frame count, resolved core IDs, whether strict mode was applied,
crash state, a SHA-256 hash of the final framebuffer, and CPU registers. `--screenshot` saves the
final frame as a PNG using the same `DirectBitmap` path the interactive view uses — useful for
spot-checking a core/ROM combination without opening the UI.

Verified: deterministic (identical ROM + cores + frame count → identical hash across repeated
runs), core selection genuinely changes core IDs and (on ROMs with real content) the resulting
hash, and `--strict` measurably changes output on a real ROM.

## Run (the other four ROM-only modes)

**Requires: nothing but a `.nes` file.** All four run today; each has a fuller doc comment at the
top of its own `*Cli.cs`.

```bash
# Pass/fail gate. Exit 0 clean | 1 crashed | 2 usage/IO | 3 core not applied | 4 unsupported mapper | 5 unexpected.
BrokenNes.Workshop.exe --romtest --rom path.nes [--cpu ID --ppu ID --apu ID] [--frames N] \
    [--input "60:Start,90:Right+A,120:"] [--json] [--out result.json]

# Speed numbers - the FIX-core accuracy-vs-speed gate (RomTestCli/BenchmarkCli share exit codes).
BrokenNes.Workshop.exe --benchmark --rom path.nes [--cpu ID --ppu ID --apu ID] [--weight N] [--out result.json]

# Is SaveState/LoadState itself lossy? Compares every frame after a zero-elapsed round trip.
BrokenNes.Workshop.exe --diag-savestate-roundtrip --rom path.nes [--cpu ID --ppu ID --apu ID] \
    [--warmup-frames N] [--continue-frames N] [--strict]

# Instruction ring-buffer trace into a boot hang/crash; or raw bytes at a CPU address.
BrokenNes.Workshop.exe --irqtrace --rom path.nes --cpu ID --ppu ID --apu ID [--frames-before N] \
    [--ring N] [--max-instr N] [--dump-addr XXXX --dump-len N] [--watch XXXX,YYYY,...]
```

## Run (`--snestest` — the SFC core-family spec verifier)
```
BrokenNes.Workshop.exe --snestest --rom Windows\Resources\snes-test-roms\cputest\cputest-full.sfc \
    [--max-frames N] [--tests tests-full.txt] [--png screen.png] [--json] [--out result.json]
```
Runs the SNES board (`Windows/NesEmulator/snes/`: CPU_SFC, PPU_SFC, BOARD_SFC, SnesCartridge)
against gilyon/snes-tests v1.4. The ROM prints its verdict as tilemap text, so the verdict is read
straight from VRAM and never depends on the renderer. When a test fails the ROM waits for button A;
the runner presses it and carries on, so one run lists **every** failing test, each annotated from
the ROM's `tests-*.txt` (found next to the ROM automatically). Exit 0 all pass | 1 finished with
failures | 2 usage/IO | 3 timed out | 4 aborted (invalid test order / CPU hit STP) | 5 unexpected.
Status 2026-09-25: cputest-basic 1107/1107 and cputest-full 1610/1610. The verifier has been
proven able to fail: a deliberately broken `(d,X)` emulation-mode wrap produced 22 annotated
failures. `spctest.sfc` (run with `--apu SFC`) passes 1368/1368 on the SPC700. Because it
stops at its first failure, the runner reports that one failure and exits 1. Proven able to fail:
a deliberately wrong XCN was caught as test 053d with the exact expected/actual values.

All SNES modes take `--apu SFC|HLE`. SFC (the default) is the real audio unit: SPC700, S-DSP, timers
and a clean-room boot loader. HLE is the silent loader stand-in; if a game boots on HLE but not on
SFC, the bug is in the audio unit.

```
BrokenNes.Workshop.exe --snesrun --rom game.sfc [--frames N] [--png-at 300,900] [--out-dir dir] \
    [--input "500:Start,508:,700:A,708:"]
```
The game-level probe. It ends with the top PCs over the last 30 frames (a hang shows up as two or
three addresses), the audio unit's state, and, with `--apu SFC`, audio statistics: RMS, peak, DC,
audible seconds, and clipping. `--wav` also saves the audio.
2026-09-25, Super Mario World with `--apu SFC`: title → file select → Yoshi's House → overworld.
Rendering is correct, the game's own sound driver runs, and 65 of 71 s are audible with no
clipping.

### Playing: `--snes`
```
BrokenNes.Workshop.exe --snes game.sfc [--apu SFC|HLE]      # no ROM argument = file picker
```
Keys: arrows = D-pad, Z = Y, X = B, A = X, S = A, Q = L, W = R, Enter = Start, Space = Select;
P pause, F2 reset, Tab (hold) fast-forward, F12 screenshot (saved next to the ROM), Esc quit.
An XInput pad is mapped positionally (Xbox A = SNES B, and so on). With a sound-producing audio unit
the audio device's clock paces emulation; with the silent APU_HLE a stopwatch holds NTSC 60.1 fps.
Battery SRAM goes to `%APPDATA%\BrokenNes\BatterySaves\sfc<sha1-of-rom>.srm`, next to the NES
saves. It's written every 10 s when changed and on close.

## Run (`--trace` — the cross-emulator differential tracer)

**Requires: nothing but a `.nes` file.** Emits one record per frame in a format a second emulator
(Mesen) can be made to emit too, so a differ can name the exact frame where the two stop agreeing.

```bash
BrokenNes.Workshop.exe --trace --rom path.nes --out trace.txt [--cpu ID --ppu ID --apu ID] \
    [--frames N] [--input "60:Start,66:,120:Right+A,180:"] [--ntsc-frame-timing on|off] \
    [--power-on-ram fceux|zeros|ones] [--ram-dump-at N,N,... --ram-dump-dir DIR]
```

**`--ram-dump-at` is what turns a failed diff into a diagnosis.** It writes the raw 2048 bytes of
work RAM at the end of each named frame — the same instant the `ramhash` column is taken — as
`<dir>/ram_f<N>.bin`. The hash column can only say *that* two frames differ; on a ROM that touches
RAM every frame it says that on nearly every frame even when the two emulators are in the same
state, because the hash is an all-or-nothing comparison of 2048 bytes sampled at an instant two
different emulators cannot align to better than a few CPU cycles. The **count** of differing bytes
is the oracle that survives that: two or three, parked in the stack page below SP or in unallocated
zero page, means "sampled a few cycles apart"; tens or hundreds and growing means the game state
has actually forked. `UAT/diff-trace.ps1 -RamDumpA/-RamDumpB/-RamMap` consumes these directly and
names the differing addresses. Mesen's side of the same pairing is
`VRUN_TRACE_RAMDUMP=N,N,... VRUN_TRACE_RAMDUMP_DIR=dir`.

Exit codes are `--romtest`'s. `--out -` writes to stdout. The input-script parser is literally
`RomTestCli.ParseInputScript` — one parser, so the two sides of a diff cannot drift on what
"held from frame N inclusive" means.

**Format** (UTF-8, LF, lower-case hex, no `0x`), sampled at the END of each frame — immediately
after the `RunFrame()` that advanced it returns — starting at frame 0:

```
<frame>|<pc>|<a>|<x>|<y>|<sp>|<p>|<ramhash>
```

`frame` decimal 0-based; `pc` 4 hex digits; `a x y sp p` 2 each; `ramhash` = the first 16 hex chars
of SHA-256 over the 2048 bytes of work RAM (`$0000-$07FF`) in address order. Lines starting with
`#` are header and must be ignored by the differ.

**The framebuffer is deliberately not compared.** NES palettes are not standardized, so two
*correct* emulators legitimately produce different RGB. RAM + CPU state is the only oracle both
sides can agree on.

**What the header pins**, and why each line is there — an unexplained frame-0 mismatch is nearly
always one of these disagreeing rather than a real emulation defect:

- `power-on-ramhash` — the RAM hash taken *before* frame 0 runs, same hash function as the column.
  Check this first on any frame-0 disagreement. BrokenNes fills power-on RAM with the repeating
  8 bytes `00 00 00 00 FF FF FF FF` (`Bus.InitializeRamPowerOnPattern`,
  `Windows/NesEmulator/board/Bus.cs:222`, called from the ctor at `Bus.cs:198`) — FCEUX's default
  `RAMInitOption=0`. Hash: `9230156049936eb0`. **Mesen cannot produce this pattern.** It randomizes
  power-on RAM by default and its `ramPowerOnState` offers only all-zeros / all-ones / random — so
  on that pairing the two sides genuinely cannot agree at frame 0 unless one bends. `--power-on-ram`
  is where BrokenNes bends: `zeros` (hash `e5a00aa9991ac8a5`) pairs with Mesen's `AllZeros`, `ones`
  (`d0ff1b294b5288d1`) with `AllOnes`. It changes no default and the choice is recorded in the
  header. Note this is not free: FCEUX's pattern is load-bearing for `.fm2` portability, and games
  that read uninitialized RAM at boot (RNG seeding, high-score tables) will take a different path
  under a flat fill on *both* emulators — a shared, deliberate deviation rather than a silent one.
- `ntsc-frame-timing` — **defaults to `on` here, unlike everywhere else in Workshop.** BrokenNes'
  ordinary frame budget is `CpuFrequency/60` = 29829.55 CPU cycles, but real NTSC is 89341.5 PPU
  dots = 29780.5. That ~49-cycle surplus per frame drifts the frame boundary away from any
  cycle-accurate reference within a handful of frames, which shows up in a diff looking like an
  emulation defect. `--ntsc-frame-timing off` restores the default budget (and does visibly change
  the trace from frame 1 onward, which is the point).
- **Sample point** — the record is taken when `RunFrame()` returns, i.e. after a whole frame's worth
  of PPU dots (rendering *and* vblank *and* the pre-render line), not after rendering alone. Mesen's
  `emu.eventType.endFrame` fires at PPU scanline 240 cycle 0, ~2500 CPU cycles earlier, with the
  entire vblank and NMI handler in between; sampling there against this makes every frame from 1
  onward look divergent for a pure harness reason (measured on a real ROM: 1 of 600 RAM hashes
  agreed). The Mesen-side tracer therefore has `VRUN_TRACE_SAMPLE` — use `prerender` or
  `dot`/`VRUN_TRACE_SAMPLE_DOT` for anything compared against this tool, not the default `endFrame`.
- **`p` bit 5** — this column has the 6502's unused/always-set flag SET, as hardware does on any
  `PHP`/interrupt push. Mesen's `emu.getState()` `cpu.ps` reports it clear, so an unpatched Mesen
  trace differs in `p` on 100% of frames for a representation reason. `diff-trace.ps1` detects a
  whole-run constant xor on a register column and says so rather than letting it read as a bug.
- `event-scheduler=off`, `crash-behavior=redscreen` — both are the emulator's defaults, pinned so a
  changed default cannot silently alter a trace. `ImagineFix` is specifically excluded: it mutates
  CPU state on a freeze heuristic and picks among candidate fixes with a clock-seeded `Random`.
- Region is NTSC unconditionally — BrokenNes has no PAL mode at the NES level. The only `palMode`
  flags live inside APU cores that are not on this path and default to NTSC.
- Rewind, cheats and overclock do not exist in this emulator; `RunFrame` always calls
  `UpdateFrameBuffer`, so "no frameskip" is unconditional rather than a setting.

`--romtest` and `--diag-savestate-roundtrip` are also the two gated case kinds behind
`UAT/run-suite.ps1`, which adds the expected-value/diff/exit-non-zero layer these print-only
tools lack. `--benchmark` is wired in there too, informationally.

## Run (`--corrupt` — the VRUN corruption oracle)

A port of the VRUN project's own `tools/corruption_detector.lua` — the test that game's author
actually trusts, because every bug it looks for is one a human had to spot on screen and report.
Two of its three checks could not run in BrokenNes at all before the `Bus.PpuRegisterWriteObserver`
hook and the `Nametable` memory domain existed.

```bash
BrokenNes.Workshop.exe --corrupt --rom game.nes --cpu FIX --ppu FIX --apu FIX \
    [--frames 1800] [--input combat|idle|roam|script:"0:Left+B,90:Left"] \
    [--inject none|chr|nt|scroll] [--allowed-tiles CSV] [--floor-tiles CSV] [--learn] [--out r.txt]
```

| Check | What it asks | How it can fail |
| --- | --- | --- |
| late PPU write | did a `$2005` write land on a visible or pre-render scanline while rendering? | the frame overran vblank; a `$2007` write there scatters into CHR-RAM |
| CHR scatter | did any CHR tile outside the animated set change? | a write landed where it was never addressed |
| floor writes | did a non-floor tile get painted onto a cell `collision_map` calls non-solid? | the `cell_plain_terrain()` gate regressed, or a write went astray |

**`--ppu FIX` is required** — the scanline and nametable both come from `IPpuProbe`, which only
PPU_FIX implements. Any other PPU core exits 3 with an explanation rather than emitting a green
report from unavailable data. **`--cpu FIX` is required in practice too:** VRUN uses unofficial
6502 opcodes (`A7`/LAX) that only CPU_FIX implements, and the default core dies at frame 1 with
`Bad opcode A7 at C176`.

**`--inject` is the point.** A checker nobody has seen fail proves nothing, so each mode introduces
exactly one defect and exactly one check must catch it — `chr` pokes a byte into CHR tile 16,
`nt` paints a wall tile onto an empty floor cell, and `scroll` (which the Lua documents but never
implemented) issues a real `$2005` write pair down `Bus.WriteSlow`'s own PPU register path at the
instant `RunFrame` returns, when the PPU counter sits at the top of the frame instead of in vblank.
That last one exercises the whole observer chain, not just the verdict arithmetic.

**Measured on `game.nes` (2026-09-04, CPU_FIX/PPU_FIX/APU_FIX, 1800 observed frames):** CLEAN in all
three drive modes. All `$2005` writes landed on scanlines 253–260, none outside vblank; only the 12
allowlisted CHR tiles changed; all 3806 writes onto non-solid cells were floor art. Each injection
failed exactly its own check and no other. Exit 0 clean / 1 corruption detected.

**Coverage caveat the report prints on every run:** `combat` and `idle` never leave the spawn room
on this ROM, so they observe zero room transitions — and a room transition is where the Lua's own
author found the one real bug that detector has ever caught. `--input roam` buys 22 of them per
1800 frames by poking `player_x`/`player_y` onto a passable edge cell (the same trick
`UAT/vrun-shophunt.ps1` uses, for the same reason), at the cost of a perturbed run.

## Run (AccuracyCoin benchmark)

> **Requires `AccuracyCoin.nes`, which is NOT on this machine** (verified 2026-09-03 by a
> whole-`C:` search — see the table at the top). Obtain it from
> [100thCoin/AccuracyCoin](https://github.com/100thCoin/AccuracyCoin). Everything below is
> implemented and waiting on that one file; do not rewrite it.

Drives [AccuracyCoin](https://github.com/100thCoin/AccuracyCoin) (100thCoin/AccuracyCoin, a
141-test NES accuracy ROM) to completion and reads its results directly out of RAM. The whole
mechanism — how to trigger the automated run, where results are stored, how to decode a raw byte
into pass/fail/error-code — was reverse-engineered from `AccuracyCoin.asm` itself; see
`AccuracyCoinRunner.cs`'s doc comment for the exact asm line references. Not bundled into this
repo: `AccuracyCoin.nes` (MIT-licensed, ~40KB) and its `.asm` source live at the upstream repo,
not here.

```bash
# Single combo, full per-test breakdown to stdout:
BrokenNes.Workshop.exe --accuracycoin --rom AccuracyCoin.nes [--cpu ID --ppu ID --apu ID] [--out results.json]

# Full CPU x PPU x APU cross-product (or --cpus/--ppus/--apus subsets), parallelized:
BrokenNes.Workshop.exe --accuracycoin --rom AccuracyCoin.nes --matrix --out matrix.json
```

Each result reports pass/fail/not-run/skipped counts per test (with the sub-check error code for
fails), plus `RetryCount`/`AutoSkippedTests` — see the next two points for why those exist.

**Every CPU core crashes on some illegal/unofficial 6502 opcode** — verified across all 7: each
throws a different "Bad opcode" exception at a different point (`CPU_ULQ` earliest; the joke core `CPU_Z80`, since retired,
failed immediately). Real 6502 silicon never crashes on an
undefined opcode. Left unhandled, this would truncate every single combination's run within the
first ~20 of 141 tests, before almost all PPU/APU/timing tests get a chance to run at all. The
harness pre-skips the 66 "Unofficial Instructions"/"Unofficial Immediates" tests by default via
the ROM's own designed skip mechanism (a pre-existing `$FF` at a test's result address makes
`RunTest` skip it entirely, never calling the test routine) — `--include-unofficial-opcodes`
disables this to see the raw crash instead.

**That alone isn't sufficient** — illegal opcodes also hide inside "All NOP instructions" (a
`CPU Behavior` test, not one of the 66), and some tests genuinely *hang* rather than crash
(`RunningAllTests` never returns to 0). Rather than hand-auditing the remaining 75 tests for
similar hidden dependencies, the default runner (`RunSingleComboRobust`) generalizes the fix: on
any crash or hang, `PostAllTestTally` identifies exactly which test was executing, adds it to the
skip set, and retries from boot. Hangs are detected via stagnation (`PostAllTestTally` frozen for
90 frames, well above the measured ~2-5 frames/test baseline) rather than waiting out the full
frame budget every time — this is what makes the retry loop fast enough to run across a
1000+-combination matrix (cut one measured combo from 65s to 8-9s in Release).

**Parallelism**: `--matrix` runs the cross-product concurrently (`Environment.ProcessorCount - 2`
workers) within one process — each `NES` instance is fully independent, confirmed during
Workshop's design research. Two known process-wide mutable statics (`APU_WF`'s MIDI singleton and
`APU_SPD2`'s unsynchronized lazy LUT build; `CPU_Z80`'s shared `Random` was a third before that core was retired) are pre-warmed
single-threaded before the parallel run starts, so their one-time lazy init can't race. Results
checkpoint to `--out` every 50 completions so a long run doesn't lose everything if interrupted.

## Run (TAS movie playback, recording, and bulk extraction)

The first three of a four-phase TAS/self-play port (a separate side project, `ML_NesPlayer`, has a
custom FCEUX fork this mirrors — see the architecture research doc for the full source system).

> **Requires a `.fm2` movie and the exact ROM it was recorded against.** There are **zero `.fm2`
> files in this repo** — they come from the external `ML_NesPlayer` tree
> (`…\!!! Experiments\ML_NesPlayer\TAS\tas\`, 348 movies, present as of 2026-09-03; full asset
> table at the top of this file). Nothing here reads that path automatically: you always pass
> `--movie` and `--rom` explicitly, so any `.fm2` + ROM pair works.

```bash
BrokenNes.Workshop.exe --playmovie --movie path.fm2 --rom path.nes \
    [--cpu ID --ppu ID --apu ID] [--max-frames N] [--strict] [--ntsc-frame-rate] \
    [--out result.json] [--screenshot out.png] [--record-out copy.fm2] [--dump-out trace.raw] \
    [--trace-out t.log --trace-start-frame N --trace-end-frame N]
```

**Phase 1 — playback** (`Tas/Fm2Movie.cs`) parses FCEUX's FM2 format and drives it through the
exact same linked cores as everything else here. Button order is a straight reversal, not a remap
table: FM2's text columns are `RLDUTSBA` (Right,Left,Down,Up,Start,Select,B,A); BrokenNes's own
order (`Input.cs:10`) is `A,B,Select,Start,Up,Down,Left,Right` — those two happen to be exact
reverses of each other, so the decoder is just `brokenNesIndex = 7 - fm2Index`. Verified by hand
against the real sample movie (`ML_NesPlayer/TAS/tas/meshuggah-ghostbusters.fm2`: frame 47 is
`....T...` → Start only; frames 192+ are `R.......` → Right only) and end-to-end by playing that
movie's full 5,670 frames against `Ghostbusters (U).nes` (the closest available ROM — the movie
was recorded against the `(J)` revision, so it's not a frame-perfect replay of the original run,
but it boots straight through the title screen into real, recognizable gameplay purely from
decoded FM2 input).

**Phase 2 — recording** (`Tas/Fm2Writer.cs`, `--record-out`) re-emits whatever was actually played
back as a new, independently-parseable FM2 file — a replay-and-re-record round trip rather than a
live/human recording surface, but that's sufficient to prove the writer is spec-correct: verified
by recording a 50-frame playback, then replaying *that* recording from scratch and confirming an
identical final frame hash and CPU register state to the original 50-frame run. `romChecksum` is
computed via `NES.ComputeRomMd5()` (MD5 of PRG+CHR only, matching FCEUX's own `GameInfo->MD5`
convention) and written in the documented `0x<hex>` form.

**Phase 3 — bulk extraction** (`Tas/NesReflexDumpWriter.cs`, `--dump-out`) writes the exact same
"nesreflex-raw-v2" binary per-frame trace format the source project's FCEUX fork produces (magic
`NRFXRAW2`, 4,432 bytes/frame: CPU regs, RAM, PPU registers/scroll/VRAM-address/scanline/dot, OAM,
palette, nametables, a CRC32 screen hash), plus the matching `.raw.json` sidecar — so the existing
Python training pipeline needs zero changes to accept BrokenNes-produced dumps. Verified: dump file
size matches the calculated `16 + frames×4432` exactly, and spot-checked field values (CPU regs,
non-zero RAM/OAM/nametable content, plausible lag-frame counts) against the CLI's own reported
state for the same run. Two fields can't be genuinely byte-identical in *value* (not just layout)
to what FCEUX emits — documented in `NesReflexDumpWriter`'s class doc: `IRQlow` (no uniform
equivalent across BrokenNes's 7 CPU cores, written as 0) and `screen_hash` (CRC32 of an RGBA
framebuffer here vs. FCEUX's 8bpp indexed buffer there — same algorithm, different input, so only
useful as an internal dedup signal, not a cross-emulator comparison).

## Run (TAS library indexing, bulk baselines, cross-emulator diff, movie verification)

Four more modes the sections above don't cover. The first three exist to run BrokenNes against the
**FCEUX fork** at scale; the fourth checks BrokenNes against itself.

> **The first three require the external `ML_NesPlayer` tree** (ROM library, `.fm2` library, and
> for `--compare-dumps` a `.raw` produced by that project's FCEUX fork). All present as of
> 2026-09-03 — see the asset table at the top. `--verify-selfplay-movie` needs only the output of
> a `--selfplay` run, so it is self-contained once you have one.

```bash
# Pair movies to ROMs by the movie header's PRG+CHR MD5, never by filename (a same-named ROM can be
# the wrong revision - confirmed on Abadox). 348 movies -> 192 matched pairs on the real library.
BrokenNes.Workshop.exe --index-tas-library --roms-dir <dir> --movies-dir <dir> --out manifest.json

# Replay every matched pair through the FIX cores, dump a nesreflex-raw-v2 trace each, classify
# OK / CRASHED / INCOMPLETE into a CSV. One process for all games (the FCEUX side needs one per game).
BrokenNes.Workshop.exe --tas-baseline-batch --manifest manifest.json --movies-dir <dir> \
    --dump-dir <dir> --out results.csv [--cpu FIX --ppu FIX --apu FIX]

# The actual cross-emulator check: diff an FCEUX-fork trace against a BrokenNes trace of the same
# movie, field by field (IRQlow and screen_hash are excluded by design - see NesReflexDumpWriter).
BrokenNes.Workshop.exe --compare-dumps --a fceux.raw --b brokennes.raw [--out result.json] [--max-mismatches N]

# Replay an exported self-play .fm2 and compare each frame against the live session's recorded
# witness hashes. Catches dropped/duplicated frames and truncation off-by-ones, not just "it replays".
BrokenNes.Workshop.exe --verify-selfplay-movie --movie path.fm2 --rom path.nes \
    [--witness path.fm2.witness.json] [--out result.json]
```

**`--compare-dumps` runs, but do not read a green tool as a green result.** A 2026-09-03 run of
BrokenNes (FIX/FIX/FIX) vs. the FCEUX fork's `pair_0002.raw` on the same movie reported a 1-frame
count difference (7,394 vs 7,393) and mismatches on essentially every compared frame, starting at
frame 0. That is an **open, uninvestigated question about cross-emulator parity**, not a broken
CLI and not a known regression — it is recorded here only so the next person doesn't assume this
harness currently demonstrates agreement with FCEUX.

## Run (SMB1 self-play)

> **Requires the external `ML_NesPlayer` tree**: the Python venv, the inference server script, the
> 227MB model checkpoint, the 277MB metadata parquet, and an SMB1 ROM. All present and working as
> of 2026-09-03 (a 600-frame headless run connected, served every frame from the real 19.8M-param
> checkpoint, and its exported movie verified `Pass: true`). Full asset table at the top of this
> file. Nothing here is a mock — if that tree is gone, this mode has no server to talk to.

Phase 4 — live self-play against the source project's Python inference server
(`scripts/nesreflex_inference_server.py`, protocol v2, **unmodified** — this is a client for the
exact same server, not a reimplementation of it). Currently Super Mario Bros. 1 only (`game_id
133`), ported from the source's `input.cpp` SMB1 profile: RAM addresses, event detection (flag
capture, power-up gain, score/coin gain, scroll milestones, death, time-up), the checkpoint stack
(creation/promotion/reload/pruning with the same lifeline counts and cooldowns), the hold-replay
input merge with both "spazz" and "chill" play styles, and the periodic/stuck-X variety injector —
see `Tas/SelfPlay/`'s per-file doc comments for exact line references into the source.

**Interactive (a real, visible emulator window — the primary way to use this):** run
`RunSmb1SelfPlay.bat` (starts the inference server, waits for the model to load, opens the
Workshop window), or start the server yourself and launch `BrokenNes.Workshop.exe` with no
arguments. Either way, check the **"Self-Play (SMB1)"** box — it auto-loads the SMB1 ROM (from
the external `ML_NesPlayer` tree, path hardcoded at `WorkshopForm.cs:39`) if a different one is
loaded, connects to the pipe, and starts
playback. The model can never press Start itself (matching the source's default profile), so click
the game screen and press **Enter** once to begin a fresh run, the same role a person clicking
"Start Autoplay" then pressing Start plays in the source project's UI. The status bar shows live
connection state, play style, temperature, checkpoint count, and reload count.

**Headless/scripted (no window, for batch runs or CI):**

```bash
BrokenNes.Workshop.exe --selfplay --rom SuperMarioBros.nes [--cpu ID --ppu ID --apu ID] \
    [--pipe-name nesreflex_inference] [--frames N] [--checkpoint-dir dir] [--seed N] \
    [--out result.json] [--screenshot-every N --screenshot-dir dir] [--log-every N] \
    [--auto-start-frame N]
```

Same model-blocked-from-Start behavior applies — `--auto-start-frame N` injects a human-equivalent
Start press for a few frames starting at frame `N`, since there's no one at a keyboard to do it.

**Verified end-to-end against the real, unmodified production server and the actual trained
checkpoint** (`ML_NesPlayer/data/checkpoints/overnight_ppu_v2/epoch_0001.pt`, a real 19.8M-parameter
model) and the correct ROM (`Super Mario Bros. (JU) (PRG0) [!].nes`) — not a mock or synthetic
test. A 2,400-frame run: connected over the named pipe, the server logged genuine inference for
every frame ("2400 frames served"), 8 checkpoints were created from real in-game events, 2 deaths
correctly triggered reloads with re-randomized play style/temperature/top-k on each (Chill→Spazz→
Chill, matching the source's per-reload randomization), the model was correctly blocked from
pressing Start throughout (33 blocked attempts logged) while still never sending garbage input, and
screenshots confirm real rightward progress through World 1-1 (recognizable pipe-hopping section by
frame 2200) — not stuck, not crashed, not cosmetic.

Known deliberate gaps from a byte-exact mirror, both documented in `SelfPlayManager`'s class doc:
the source intercepts two RAM writes mid-frame to catch transient values before they're overwritten
again later the same frame (BrokenNes has no generic write-interception hook, so this only reads
state after each frame completes — low-risk, since the affected signals are also independently
edge-detected on the next frame's read); and two probability-shaping helpers (temperature scaling,
top-k filtering) use standard, well-known formulas rather than the source's exact implementation,
which wasn't captured verbatim during research. `IsGameOver`/`IsLevelTransition` are reconstructed
from their call-site effects rather than directly observed source, flagged inline in
`Smb1EventDetection.cs`.

New in shared code for this: `NES.Reset()` (soft CPU reset, for a movie's in-band reset command),
`NES.ComputeRomMd5()`, `NES.GetP1/P2RawInputState()`, `NES.GetOpenBusValue()` (built on the open-bus
model from the AccuracyCoin work), and `Input.ConsumeReadCount()` (drives lag-frame detection: a
frame where neither controller port was polled at all).

## Gotchas worth knowing

- **This is a `WinExe`, so PowerShell will not wait for it unless you pipe.** Measured
  2026-09-03: `& $exe --romtest ... --json` returned in **0.01s** with no output and no
  `$LASTEXITCODE` — the process detached and the script sailed on past it. The same command as
  `& $exe ... 2>&1 | Out-String -Stream` blocked for the real 1.18s, printed the JSON, and set
  `$LASTEXITCODE` correctly (spot-checked 0 / 2 / 3 against `--romtest`'s documented codes). Drain
  the whole pipeline — an early `Select-Object -First N` kills the process and leaves
  `$LASTEXITCODE` empty. Every headless mode here is affected, not just `--romtest`.
- **The same truncation trap applies to `dotnet build`, and there it is silent.** 2026-09-25:
  `dotnet build ... | Select-String ... | Select-Object -First 5` ended the command after five
  warnings while the compiler server went on writing the DLL in the background. The test run that
  came next used the *previous* binary and reported 22 failures from a bug that had already been
  reverted. Drain the build output (`| Out-String`, then filter) before running anything.
- **The boot ROM needs an explicit copy-to-output step.** Unlike `Web/`'s `wwwroot` (copied to
  the publish output automatically by the Blazor SDK), a plain WinForms project needs one — both
  `WorkshopForm.LoadBootRom()` and `HeadlessRunner` read relative to
  `AppContext.BaseDirectory` (the `.exe`'s own folder), not the project directory. A static
  `<None CopyToOutputDirectory>` item **won't work** for this: item globs are evaluated once at
  project load, before any target runs, so on a first-ever build the glob would see an empty
  `Roms/` folder. The `CopySharedRoms` target copies directly into `$(OutDir)Roms` as a build
  step, `AfterTargets="Build"`, sidestepping that ordering trap entirely.
- **`test.nes` renders pure black.** Same launcher-stub ROM `Web/` found — it's a valid smoke
  test for "does the pipeline run without crashing", not for "does anything visually render".
  Use a real ROM (e.g. `Windows/Data/story/page1_jimmy.nes`) to verify rendering.
- **`DirectBitmap.ToBitmap()` was previously unused code anywhere in the repo** (confirmed by
  grep before relying on it) — the desktop app uploads frames via SharpDX instead. Verified
  correct here with real color content via `--screenshot`, not just the black test ROM (a
  channel-swap bug would be invisible against solid black).
