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
Five campaigns, about 9,000 trials across the 8 consoles (NES, GB, GBC, SMS, GG, Genesis, SNES, WideMess), 15-20 minutes each, all in parallel.

### Fixed (ebd050a5 + follow-up)
| # | Bug | Where | Found by |
|---|-----|-------|----------|
| 1 | Divide by zero: sound bit-flip with only bits past the panel's five set (API / presets) | `Butcher.Bend` | crash, NES |
| 2 | GB/GBC: a pixel still queued when the line ended indexed past the frame (bent LCDC writes); the most frequent crash | `PPU_GB.OutputPixel` | crash, ~80 hits |
| 3 | 68000 reset with an odd vector threw out of `Reset` (the button / API reset while bent) | `M68k.Reset` | crash, Genesis + NES-with-M68K |
| 4 | `Math.Abs(sbyte -128)` overflow in the SNES-DSP sound bridges (a legal DSP volume) | `ApuBridges`, `EnhancedSoundBridges` (3 sites) | crash, SNES `apu=NES:*` |
| 5 | **Off during a clock stall froze the game for good** (host still asks `ClockStallNow` after the engine stops ticking); Z80 bus / reset holds were left held too | `Butcher.Apply` | undo oracle |
| 6 | Genesis infinite loop: a runaway Z80 flooding the 68000 bank window charges more stall than it pays off | `BOARD_MD.SyncSound` | hang, Mercs |
| 7 | SNES DSP-1: `Inverse` never left its normalising loops for an exponent that gives 0 / infinity (bent coprocessor data; a game could too) | `DSP1_SFC.Inverse` | hang, Super Mario Kart |

### Open findings (not Butchering mode itself)
- **GB framebuffer not in the savestate**: frames the PPU does not fully draw (after LCD on, etc.) keep pixels from before a load, so the picture after Load State depends on history. State bytes are identical. Fixing needs the ~92 KB buffer in the GB state format (Timeflow takes one per frame). SNES shows the same "picture-only" pattern (~5% of reload trials).
- **NES reload not always exact** even with Butchering mode never on: e.g. `FUZZ_CONTROL=1 bfuzz nes 1 out 9014` (Lode Runner, all FIX): after 250 more frames, Load State + 60 frames diverges at `globalCpuCycle`. Did not reproduce in a stand-alone probe; root cause unknown.
- SMS/GG with a NES / SNES picture chip (`ppu=NES:*`): savestates do not replay (the picture bridge's state is not saved).
- `APU_FIX` zeroes its audio filter on load (deliberate, anti-pop), so audio right after a load differs by design.
- Harness-only artefacts that were ruled out: the engine object stays after Off (idle by design); a NES game that has crashed returns from `RunFrame` early.

### Re-run
Source: `tools/butcher-fuzz` (`dotnet run -c Release --project tools/butcher-fuzz -- <console> <seconds> <outdir> <seed>`; ROM folders are the user library paths at the top of Program.cs).

`bfuzz <console> <seconds> <outdir> <seed>`; `REPRO=<seed> [REPRO_UNDO=1] [REPRO_DEEP=1]` minimises (process-isolated); `FTRACE=1` traces frames, events and dumps `pre.state` / `pre.json`; `FUZZ_CONTROL=1` runs with the mode off; `NOEV=<digits>` disables harness events.
