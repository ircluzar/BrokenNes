# Butchering mode stress / fuzz campaign (2026-11-04)

Goal: shake out hidden crashes, hangs and broken promises in Butchering mode across every console and core mix.

## Harness
Scratch tool `bfuzz` (session scratchpad, references `Core/BrokenNes.Core.csproj`); one process per console:
`bfuzz <nes|gb|gbc|sms|gg|md|snes|wm> <seconds> <outdir> [seed]`.

Each trial (seeded, reproducible):
- a random game from the user's library for that console (NES: the 2,847-ROM corpus),
- a random core mix (1 in 3 per slot picks any non-wall option from CoreCatalog, including cross-console cores; NES: any CPU/PPU/APU id),
- a random ButcherSettings tree built by reflection, so every effect is reached; densities 2-60%;
  1 trial in 4 is **hostile** (negative / huge rates and lengths, overlong pin arrays, out-of-range fault codes: what a bad preset or API call can send),
- applied at a random frame, then ~250 frames with random events: re-randomize, off/on, savestate round-trip while butchered, core swap, reset, reach query.

## Oracles
1. **Crash**: any exception, bucketed by type + top two frames.
2. **Hang**: a trial over 90 s.
3. **Undo**: savestate before applying; afterwards Reload State + Off must replay the next 60 frames bit-exact against the clean reference (the "leaving undoes everything" promise). Core swaps are skipped in these trials.
4. **Engine idle after off**.
5. **Determinism**: 1 trial in 5 is run twice and must hash the same.
6. **JSON round-trip** of settings; non-empty frame.

## Results
See the end of this file (filled in after the runs).
