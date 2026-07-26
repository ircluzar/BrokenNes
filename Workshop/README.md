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

## Run (AccuracyCoin benchmark)

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
throws a different "Bad opcode" exception at a different point (`CPU_ULQ` earliest, `CPU_Z80`
immediately, matching its known joke-core status). Real 6502 silicon never crashes on an
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
Workshop's design research. Three known process-wide mutable statics (`APU_WF`'s MIDI singleton,
`APU_SPD2`'s unsynchronized lazy LUT build, `CPU_Z80`'s shared `Random`) are pre-warmed
single-threaded before the parallel run starts, so their one-time lazy init can't race. Results
checkpoint to `--out` every 50 completions so a long run doesn't lose everything if interrupted.

## Run (TAS movie playback, recording, and bulk extraction)

Three of the four phases of a broader TAS/self-play port (a separate side project, `ML_NesPlayer`,
has a custom FCEUX fork this mirrors — see the architecture research doc for the full source
system). Not yet implemented: the live self-play/checkpoint harness (phase 4).

```bash
BrokenNes.Workshop.exe --playmovie --movie path.fm2 --rom path.nes \
    [--cpu ID --ppu ID --apu ID] [--max-frames N] [--strict] [--out result.json] [--screenshot out.png] \
    [--record-out copy.fm2] [--dump-out trace.raw]
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

New in shared code for this: `NES.Reset()` (soft CPU reset, for a movie's in-band reset command),
`NES.ComputeRomMd5()`, `NES.GetP1/P2RawInputState()`, `NES.GetOpenBusValue()` (built on the open-bus
model from the AccuracyCoin work), and `Input.ConsumeReadCount()` (drives lag-frame detection: a
frame where neither controller port was polled at all).

## Gotchas worth knowing

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
