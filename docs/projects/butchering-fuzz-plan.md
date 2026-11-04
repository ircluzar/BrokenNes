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
- **GB framebuffer not in the savestate**: FIXED (GB state v2 carries FrameBuffer + ShadeBuffer; v1 states still load). Reload pictures exact on Trax (DMG) and 1942 (GBC); a 5-minute GB/GBC rerun left 1 rare mismatch each (one state, one picture), not chased. SNES shows a similar picture-only pattern (~5% of reload trials) and is not fixed.
- **NES reload not always exact** even with Butchering mode never on: e.g. `FUZZ_CONTROL=1 bfuzz nes 1 out 9014` (Lode Runner, all FIX): after 250 more frames, Load State + 60 frames diverges at `globalCpuCycle`. Did not reproduce in a stand-alone probe; root cause unknown.
- SMS/GG with a NES / SNES picture chip (`ppu=NES:*`): savestates do not replay (the picture bridge's state is not saved).
- `APU_FIX` zeroes its audio filter on load (deliberate, anti-pop), so audio right after a load differs by design.
- Harness-only artefacts that were ruled out: the engine object stays after Off (idle by design); a NES game that has crashed returns from `RunFrame` early.

### Re-run
Source: `tools/butcher-fuzz` (`dotnet run -c Release --project tools/butcher-fuzz -- <console> <seconds> <outdir> <seed>`; ROM folders are the user library paths at the top of Program.cs).

`bfuzz <console> <seconds> <outdir> <seed>`; `REPRO=<seed> [REPRO_UNDO=1] [REPRO_DEEP=1]` minimises (process-isolated); `FTRACE=1` traces frames, events and dumps `pre.state` / `pre.json`; `FUZZ_CONTROL=1` runs with the mode off; `NOEV=<digits>` disables harness events.

## Round 3 (2026-11-04): reload fidelity + the test battery

### Fixed
- NES savestate carries the frame being drawn and the frame on show (`liveFrame`, `shownFrame`, deflated): the first frame after a load is exact; the 9 savestate goldens went from "PASS (framebuffer-only difference, expected)" to an exact "PASS".
- NES Load State cleared nothing when the game had crashed (a bent CPU into a bad opcode): Reload State could not undo it. A load now clears the crash.
- PPU_FIX: the PPU open-bus latch and its decay timers are in the state (relative to the dot counter, which is not).
- **Leave did not fully undo** (every console): Off queued a sound restore (drop-out / drone / duty / detune release) for the next frame, which wrote the old timeline's register values into the machine the following reload had just restored. `Butcher.NoteStateLoaded()` is called by every host's Load State.
- NES: entering Butchering mode (or a Glitch Harvester base state) on a **paused** game failed ("Failed to capture atomic NES state"): with no frame running for 250 ms the state is now taken directly.
- Game Boy: after a load the session kept showing the old picture until a frame ran (Leave while paused showed the butchered frame).
- Master System / Game Gear / Genesis on a cross-console picture chip (the user's Genesis profile: `NES:DMGS`): the bridge redraws on load, and remembers the picture of the last 8 states it saved (by hash), so Reload State and Leave give the exact picture back.
- Harness: SNES "picture-only" mismatches were the harness hashing rows below the visible picture (the 512x240 buffer); hashes now cover width x height only.

### Battery
`powershell -NoProfile -File UAT\battery.ps1 [-FuzzMinutes 10] [-Soak 20000] [-Consoles nes,snes] [-Skip fuzz]` - builds to a scratch folder, then
the golden suite on 10 runners (`UAT/run-parallel.ps1`, ~100 s instead of ~12 min), the Butchering scenarios (`tools/butcher-scenarios`: preset undo, soak,
core swaps, mangled JSON; 788 checks) and a fuzz campaign, all consoles in parallel; writes `report.md` / `report.json` with a repro line per finding.
`UAT/butcher-desktop.ps1` drives the real app over HTTP on its own instance (`BROKENNES_SAVES_DIR` isolated): the panel's enter / Reload State / leave
calls and the battery freeze. First full battery: suite 137/0, scenarios 788/0, fuzz 2,080 trials; desktop 6/6 leave exact, battery frozen on Zelda + SMW.

### Still open
- Krion Conquest (NES, MMC3): reload drifts after normal play with Butchering mode never on (PPU pipeline at frame 2); not the mapper, cartridge or CPU counters.
- Rare reload mismatches with cross-console cores (NES with `cpu=SM83` or `ppu=MD`, GB `GB-NESCLOCK`): 3 in 2,080 trials.
- A bridged picture loaded from a state saved in another session is redrawn, not exact.
