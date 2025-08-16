# Project Work Document: APU_MNES & APU_WF SoundFont / Note-Event Improvements

Date Initialized: 2025-08-16  
Repository: BrokenNes  
Project Goal: Maximize correctness, performance, and maintainability of SoundFont (note-event) playback for both MNES (SF2 / FluidSynth) and WF (web oscillator/sample) cores while eliminating duplication and unintended layering.  
Owners: Audio / APU maintainers

---
## 1. High-Level Outcomes (Definitions of Done)
- [ ] Only one SoundFont synth path produces audio at a time (unless deliberate layering explicitly toggled by user feature flag).
- [ ] Enabling/disabling SoundFont mode never leaves stuck notes or residual tails beyond acceptable fade (< 250ms reverb tail when applicable).
- [ ] Switching cores incurs minimal CPU spike (< target threshold defined informally) and does not reload already-warm assets unnecessarily.
- [ ] PCM legacy mixing fully suppressed while SoundFont mode active (no double energy in mix bus).
- [ ] Clear user-visible indication of which SoundFont core is active.
- [ ] Program / channel mappings documented in one canonical location.
- [ ] Defensive routing guards prevent misdirection of note events even if UI scripts fail to set active core.
- [ ] Resource usage (worklet + oscillator graphs) minimized: inactive synth fully quiescent.

---
## 2. Current Architecture (Snapshot)
Reference (no action required): APU_* -> note delegate -> `nesInterop.noteEvent` -> (currently both) `nesSoundFont` & `mnesSf2`. This document replaces the old audit by converting its findings into tasks.

---
## 3. Core Workstreams & Task Breakdown

### A. Routing & Gating (Prevent Double Playback)
- [x] A1: Introduce global active-core flag (e.g., `window._nesActiveSoundFontCore`). (Implemented in `nesInterop.js` with `_nesActiveSoundFontCore` + layering flag `_nesAllowLayering`.)
- [x] A2: Add `setActiveSoundFontCore(coreId)` API to `nesInterop`. (Added with eager enable + optional flush.)
- [x] A3: Refactor `nesInterop.noteEvent` to dispatch only to selected synth (legacy dual-dispatch fallback if unset). (Now gates & updates counters, suppression warnings.)
- [x] A4: Implement defensive early-return guards inside `mnesSf2.handleNote` & `nesSoundFont.handleNote` when core mismatch. (Guards added to `mnesSf2.js` and `soundfont.js`.)
- [ ] A5: Ensure disable flows call a shared function to silence/flush inactive synth before switching.
- [x] A6: Add optional layering toggle (off by default) to allow deliberate dual dispatch (future-proof, keep implementation minimal, may stub UI later). (`setSoundFontLayering(bool)` implemented.)

- [x] E1: Add counters: dispatchedNoteEvents[MNES], dispatchedNoteEvents[WF]. (Implemented as `_sfCounters` in `nesInterop.js`.)
- [ ] E2: Add cumulative activeTimeMs per synth (start/stop timestamps) for profiling. (Pending.)
- [x] E3: Provide `window.nesInterop.debugReport()` returning current routing + counters. (Added.)
- [x] E4: Console warn if events received for inactive synth > threshold (e.g., 3) to surface routing bugs. (Suppression warning in `noteEvent`.)
- [ ] B3: Add `flushSoundFont()` invocation on core change to reduce lingering reverb tails (optional but recommended).
- [ ] F4: Provide troubleshooting guide: double audio symptom -> check active-core flag. (Pending – add after README update.)
- [ ] B5: Add lightweight state machine logging (dev mode) for lifecycle transitions.

Implementation Note (2025-08-16): Core gating & telemetry foundation merged. Next steps: integrate UI controls (D1-D3), add lifecycle flush hook from C# (C3), and implement activeTime tracking (E2). Ensure README and troubleshooting sections updated after UI integration.

### C. Emulator Core Integration (C#)
- [ ] C1: Centralize SoundFontMode enable/disable pathways (remove duplicate early returns if possible; ensure both cores follow consistent pattern).
- [ ] C2: Confirm `EmitAllNoteOff()` usage on disable for both cores; unify into shared helper if duplication.
- [ ] C3: Add optional hook from C# to JS to trigger flush after `SoundFontMode` false.
- [ ] C4: Ensure no stale sample data accumulates in PCM ring buffer while SoundFontMode true (sanity logic; no explicit test task here per request).

### D. UI / Razor Updates
- [ ] D1: In `Pages/Nes.razor`, set active core prior to enabling SoundFont mode.
- [ ] D2: Disable inactive synth via JS interop right after switching core.
- [ ] D3: Add minimal UI indicator (text badge or icon) reflecting active SoundFont core (MNES / WF / None).
- [ ] D4: Provide (hidden or dev) toggle to allow layering (binds to A6) – can be deferred if scope creep.
- [ ] D5: Add manual “SoundFont Flush” control (optional small button in dev panel) calling `flushSoundFont()`.

### E. Telemetry & Diagnostics (Non-Test Instrumentation)
- [ ] E1: Add counters: dispatchedNoteEvents[MNES], dispatchedNoteEvents[WF].
- [ ] E2: Add cumulative activeTimeMs per synth (start/stop timestamps) for profiling.
- [ ] E3: Provide `window.nesInterop.debugReport()` returning current routing + counters.
- [ ] E4: Console warn if events received for inactive synth > threshold (e.g., 3) to surface routing bugs.
- [ ] E5: Add simple ring buffer depth poll (if accessible) to verify PCM suppression in real time (optional overlay text). *No automated test tasks included.*

### F. Documentation & Knowledge Base
- [ ] F1: Create/Update doc section enumerating MNES.sf2 program mapping (channels, banks, special patches).
- [ ] F2: Document WF core generic program mapping (pulse, triangle, noise mapping to pseudo-GM IDs).
- [ ] F3: Add a short “SoundFont Core Switching” subsection to README referencing gating design.
- [ ] F4: Provide troubleshooting guide: double audio symptom -> check active-core flag.
- [ ] F5: Add note on layering toggle (if implemented) and performance considerations.

### G. Performance & Optimization (Non-Test)
- [ ] G1: Verify inactive synth gracefully releases AudioNodes / WorkletProcessor references (manual inspection + logs).
- [ ] G2: Cache previously loaded SF2 so reactivating MNES avoids relaunch overhead.
- [ ] G3: Defer creation of heavy nodes until first legitimate note event after active-core set (NOT before).
- [ ] G4: Add micro-throttle to enable calls (ignore duplicate enable within 1s) to avoid churn.
- [ ] G5: Provide manual profiling instructions in docs (no automated test tasks).

### J. Backlog / Future (Defer Unless Bandwidth)
- [ ] J1: DPCM channel mapping integration (Bank 128 program 0) once spec finalized.
- [ ] J2: Automatic gain normalization between WF & MNES to equalize perceived loudness.
- [ ] J3: AudioWorklet fallback shim for unsupported browsers.
- [ ] J4: Real-time patch remapping UI (drag & drop program slots) persisting to localStorage.
- [ ] J5: Visual timeline of recent note events (scrolling piano roll).

---
## 4. Task Dependencies (High-Level Guidance)
Order suggestion:
1. Routing & Gating (A) – foundation.
2. UI / Razor updates (D) + Lifecycle (B, C) – integrate gating.
3. Telemetry (E) – observe correctness while optimizing.
4. Documentation (F) – update once core behavior stable.
5. Performance (G) – iterate using telemetry.
6. Hardening (H) & optional UX (I), then backlog (J).

---
## 5. Risk Register (Action-Oriented)
- [ ] R1: Double playback persists after routing changes (monitor counters E1; if both increment when layering off, reopen A3/A4).
- [ ] R2: Inactive synth still consuming CPU (inspect worklet thread; if activeTimeMs increments while inactive -> revisit B2/B4).
- [ ] R3: Switching cores drops first note (add small pre-warm step or pending queue; handled in B1/B2 if observed).
- [ ] R4: UI fails to set active core (defensive guards A4 + console warning E4 mitigate).
- [ ] R5: Residual tails annoy users (ensure flush B3; allow user manual flush D5).

---
## 6. Implementation Notes / Pseudo Guidance (Non-Binding)
- Global flag default: empty/undefined => compatibility dual dispatch; set only after user selects core.
- Use minimal branching inside hot path `noteEvent` to keep overhead negligible.
- Provide structured object for debug report: `{ activeCore, counters: { mnes: n, wf: n }, layering: bool }`.
- Keep layering toggle behind a simple boolean `window._nesAllowLayering` (future UI can flip).
- Ensure any added JS global names are namespaced (`nesInterop.*`).


---
## 8. Backlog Parking Lot (Ideas Not Yet Scoped)
- MIDI input bridge to route external controller to active synth core.
- Adaptive voice allocation strategy (priority by channel energy) for possible future polyphony limits.
- Auto gain staging analyzer comparing RMS of both synths for normalization.

---
## 9. Original Audit (Condensed Snapshot for Context)
(Preserved minimally for historical trace; detailed narrative replaced by structured tasks.)
- Confirmed: MNES.sf2 loads; PCM suppression works; NoteOff on disable works both cores.
- Gaps: Dual routing causes possible layering & CPU overhead; no explicit active-core gating; optional flush absent.
- Risks: Dual playback, unnecessary worklet CPU, potential race on first note enable.

---
Prepared by: Automated transformation (GitHub Copilot)  
Status: Active Work Document
