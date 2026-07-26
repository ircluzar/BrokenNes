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
