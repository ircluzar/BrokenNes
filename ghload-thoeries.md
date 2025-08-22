# Glitch Harvester/Stash load not re-applying cores — theories (most likely first)

This lists the most probable reasons why loading from Stash History or Glitch Harvester doesn’t appear to reload the CPU/PPU/APU cores “correctly,” while the top Load button does. Each item includes evidence from the code, likely symptoms, how to confirm, and a quick fix idea.

---

1) Post-load UI/core sync and APU bridge steps are missing in GH paths
- Evidence:
  - Top-level Load path (`NesEmulator/StatePersistence.cs` → `LoadState()`): after `nes?.LoadState(full)`, it updates UI and bridges:
    - Sets `nesController.CpuCoreSel/PpuCoreSel/ApuCoreSel` from the live NES (`ExtractSuffix(nes.Get*CoreId())`).
    - Calls `SetApuCoreSelFromEmu()` and `AutoConfigureForApuCore()` (rebinds audio/JS interop and Famiclone flags).
    - Resets audio timeline and redraws.
  - GH paths:
    - `GhLoadSelectedBase()` and `GhReplayEntry()` only call `nes.LoadState(base.State)`, set `AutoStaticSuppressed`, and draw a frame. They do not update UI core selections, Famiclone flag, audio bridges, or memory domains.
- Symptoms:
  - Emulator likely switches cores internally (because `NES.LoadState` applies `cpuCoreId/ppuCoreId/apuCoreId`), but the UI continues to show previous selections; audio bridge remains bound to the old core leading to silence/weirdness; Famiclone toggle not reflecting actual APU.
- Confirm:
  - Log `nes.Get*CoreId()` vs `nesController.*CoreSel` immediately after a GH load; they’ll disagree.
  - Compare audio worklet state after top Load vs GH load; timeline not reset in GH path.
- Fix:
  - After GH `nes.LoadState(...)`, run the same post-load steps as the top Load path:
    - Update `nesController.*CoreSel` from `nes.Get*CoreId()`.
    - Call `SetApuCoreSelFromEmu(); AutoConfigureForApuCore();`
    - Optionally `resetAudioTimeline`, warm `GetAudioBuffer()`, and `BuildMemoryDomains()`.

2) Audio pipeline reset is skipped → old APU bridge persists
- Evidence:
  - Top Load calls `resetAudioTimeline` and touches `GetAudioBuffer()`; GH paths don’t.
- Symptoms:
  - Perceived “wrong core” (no audio or wrong output) even though emulator core switched, because the previously configured worklet/bridge still references the prior core.
- Confirm:
  - After GH load, force a `resetAudioTimeline` and rebind; audio behavior should match top Load.
- Fix:
  - Mirror the top Load audio actions in GH paths.

3) UI Famiclone flag not synced to the APU actually loaded
- Evidence:
  - Top Load runs `SetApuCoreSelFromEmu()` which also updates `FamicloneOn`; GH paths don’t.
- Symptoms:
  - UI shows old FMC toggle and potentially keeps FMC-specific behavior while active APU core is different.
- Confirm:
  - Inspect `nesController.FamicloneOn` before/after GH load vs `ExtractSuffix(nes.GetApuCoreId())`.
- Fix:
  - As in (1), call the sync helpers.

4) Memory domains not rebuilt after GH base load
- Evidence:
  - Top Load calls `BuildMemoryDomains()` after reconstructing NES from ROM bytes; GH paths don’t.
- Symptoms:
  - If a base state comes from a different ROM/session (or domain sizes differ), generated or replayed writes may target stale domains/lengths, causing odd behavior interpreted as “wrong core.”
- Confirm:
  - Use a base from a different ROM or mapper; GH replay will use prior domains unless rebuilt.
- Fix:
  - Call `BuildMemoryDomains()` after GH loads when ROM or mapper may differ.

5) Older GH base states missing `*CoreId` fields fall back to defaults in NES.LoadState
- Evidence:
  - `NES.SaveState()` writes `cpuCoreId/ppuCoreId/apuCoreId`; `NES.LoadState()` prefers these. If absent (older base states), fallback logic chooses heuristics (e.g., preferred PPU list; APU bucket from enum). Top Load likely uses current, newer-format states; GH base states created long ago could lack IDs.
- Symptoms:
  - GH load uses default/heuristic cores instead of the original exact cores; UI also not updated so mismatch is visible.
- Confirm:
  - Inspect a problematic GH base JSON and check for missing `*CoreId` fields.
- Fix:
  - Regenerate bases with a recent build, or extend fallback mapping to more exact matches; still apply (1) so UI reflects what was actually selected.

6) No ROM/bus re-init when GH base belongs to a different ROM
- Evidence:
  - Top Load parses `romData` early, constructs a fresh `NES`, loads ROM, then calls `nes.LoadState(full)`.
  - `NES.LoadState` can rebuild bus if `romData` differs, but if the existing `nes` instance was created for another ROM and GH base `romData` is absent or mismatched, you may end up with partially compatible cores/domains.
- Symptoms:
  - Mapper/PPU desync or immediate crash/blank; perceived as wrong core.
- Confirm:
  - Load a GH base from another ROM; check if `romData` exists and whether `NES.LoadState`’s `needNewCartridge` path triggers.
- Fix:
  - Prefer the top-load pattern for GH: if base has `romData`, rebuild NES from it before applying the state; then run the post-load sync.

7) APU register latch replay issue after loads (low-medium relevance for GH)
- Evidence:
  - Known theory documented in `docs/projects/bugsearch-savestate.md`: APU register latch array not serialized could cause bad replays after later core switches. If GH operations involve any core switching later, stale latches could bias results.
- Symptoms:
  - Audio channels not configured as expected post-load/core-swap; sounds like the “wrong core.”
- Fix:
  - Serialize and restore the latch array; apply before/after APU selection consistently. This is likely a secondary factor vs (1)-(3).

---

Quick remediation plan for GH paths
- After any `nes.LoadState(...)` inside GH, immediately:
  - Sync UI: `nesController.CpuCoreSel/PpuCoreSel/ApuCoreSel` from `nes.Get*CoreId()`.
  - `SetApuCoreSelFromEmu(); AutoConfigureForApuCore();`
  - Reset audio timeline and warm buffer.
  - If bases can be cross-ROM, ensure the ROM rebuild dance (construct NES from `romData` first) and then `BuildMemoryDomains()`.

Why this matches observed behavior
- The top Load button already does all of the above; GH doesn’t. So top Load “fixes” the cores and UI display every time, while GH appears incorrect because the UI and bridges aren’t updated even though the emulator likely did switch cores internally.
