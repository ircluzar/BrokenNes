# ROM Manager ROM-load freeze vs. Load/State paths — ranked theories

Date: 2025-08-21

Symptom: Clicking a ROM in ROM Manager doesn’t visibly load the ROM and appears to freeze what was running. Loading via the Load/State paths works as expected.

## Ranked theories (most likely first)

1) JS frame loop not stopped before load (duplicate/dirty loop state)
- Manager path pauses via NesController.PauseEmulation with no-op callbacks and never calls `nesInterop.stopEmulationLoop`.
- Then it may start a new loop on resume, leaving the old rAF loop alive or in a weird state. Visual result: “freeze” or inconsistent updates.

2) No warm-up/draw after LoadROM (screen never updates)
- Manager path doesn’t run a frame or call `drawFrame` after `nes.LoadROM`, so the canvas still shows the previous frame.

3) Missing core re-apply and crash behavior on Manager path
- Doesn’t call `ApplySelectedCores()` or set `CrashBehavior.IgnoreErrors`, unlike the good paths; a mismatched CPU/PPU/APU can stall or misbehave.

4) Resume only if previously running
- If `wasRunning` is false, Manager path neither draws a frame nor starts emulation, so it looks frozen even though the ROM is loaded.

5) Out-of-band `IsRunning` control vs. loop ownership
- Manager toggles `IsRunning` without owning the JS loop lifecycle; the good paths coordinate pause/start with the loop owner (Emulator).

6) Possible double initialization increases sensitivity to missed stop
- Two initializers exist (Emulator and Nes.razor). If the loop isn’t fully stopped first, a second `startEmulationLoop` worsens duplication issues.

7) Memory domains not rebuilt
- Manager path receives a `buildMemoryDomains` callback but doesn’t invoke it; UI/RTC side effects can seem like nothing changed.

8) Fetch path differences minimal but still possible
- Manager path fetches via a delegate. If a filename is bad or empty result occurs, it sets an error but no visible change; appears frozen.

9) Uploaded vs. built-in code paths diverge
- Uploaded branch never warms up/draws or reapplies cores; relies on resume. If resume doesn’t occur, view stays unchanged.

10) Race: FrameTick runs during load because loop wasn’t stopped
- `IsRunning=false` prevents work but the loop still churns; interleaving can leave state half-updated.

11) Incomplete UI state sync
- Manager path updates some flags (`CurrentRomName`, size) but not APU core display/crash settings; visual cues don’t change.

12) Core/mapper mismatch without re-apply
- Failing to re-apply cores after `LoadROM` may lead to silent stalls.

13) No audio timeline reset
- Manager path doesn’t reset audio timeline; audio oddities can be perceived as a freeze.

14) Row delete click propagation unlikely but worth sanity-checking
- Delete button uses `@onclick:stopPropagation="true"`; low likelihood.

15) Upload guards (extension/size)
- Non-.nes or >4MB files skipped; if testing with such files, clicks “do nothing”. Less likely for built-ins.

## Quick fixes to try (incremental)

- Stop the JS loop in the Manager pause path: call `nesInterop.stopEmulationLoop` before `LoadROM` and ensure it’s idempotent.
- After `nes.LoadROM`, run a single warm-up frame and `nesInterop.drawFrame` to update the canvas immediately.
- Re-apply selected cores and set crash behavior: `ApplySelectedCores()` and `SetCrashBehavior(Repeat IgnoreErrors)`; sync APU selection from emu.
- If `wasRunning` is false, start emulation or at least present one frame; don’t rely solely on previous running state.
- Rebuild memory domains via the provided callback to keep RTC panel in sync.
- Optionally reset audio scheduling/timeline to avoid carry-over artifacts.

## Minimal instrumentation

- Log when Manager pause calls are made and confirm the JS loop is actually stopped.
- Log a one-shot after `LoadROM`: ROM name, size, first 16 bytes, and that a warm-up frame was drawn.
- Log before/after `ApplySelectedCores` and the active core IDs to ensure they changed as expected.
