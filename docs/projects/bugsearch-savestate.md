## Savestate Freeze / Reset Investigation (Intermittent Across Multiple Games & Mappers)

This document aggregates all current theories (from code re-scan + prior notes) that could explain: after loading a savestate some games (a) continue fine, others (b) freeze (hang / soft‑lock), others (c) appear to soft‑reset. Ordered from MOST PROBABLE root cause to least. Each item has:

Checkbox  [ ] not yet confirmed, [x] confirmed, [~] partially confirmed, [!] disproved.

For each theory: Symptoms explained, Mechanism, Code Evidence, How to Confirm, Fix Approach, Risk / Side‑Effects.

---

### 1. [ ] CPU state JSON is always empty (serializer ignores auto‑properties)
**Probability:** Extremely High (primary root cause)  
**Mechanism:** `SaveState()` uses `PlainSerialize` which reflects ONLY public instance FIELDS (`type.GetFields(BindingFlags.Instance|Public)`). `CpuSharedState` exposes ONLY auto‑properties (A,X,Y,PC,SP,status, irqRequested, nmiRequested). Auto‑properties compile to private backing fields → skipped → serialized CPU JSON is `{}` (length likely 2). On load, `bus.cpu.SetState(JsonElement)` can’t find register members -> CPU registers & pending interrupt flags remain whatever the *current* or freshly reset CPU has, while RAM / mapper / PPU / APU are rewound → temporal divergence (PC & stack context mismatch).  
**Explains:** (a) Some loads “work” if divergence is benign or the game quickly re-initializes; (b) Freeze if code path was mid routine relying on preserved CPU flags/registers/IRQ; (c) Soft reset style restart when subsequent BRK/invalid opcode triggers reset-like handling or PC returns to reset vector due to mismatched stack.  
**Evidence:** `CpuSharedState.cs` has only properties; `NES.PlainSerialize` never inspects properties. Observed log lengths (if instrumented) would show tiny CPU JSON.  
**Confirm:**  
1. Add temporary log: length of `cpuJson` vs expected >0 fields.  
2. Force a savestate; inspect emitted JSON substring for `"PC"` (should be absent now).  
3. Convert properties to public fields OR enhance serializer to include properties; reload same savestate location → behavior stabilizes.  
**Fix Options:**  
– Easiest: Change `CpuSharedState` to use public fields.  
– Safer / general: Extend `PlainSerialize` to include public readable/writable properties with primitive/array types.  
– Alternative: Manually build a DTO struct with public fields only.  
**Risk:** Minimal; must ensure AOT friendliness (avoid non-public reflection).  
**Side Effect:** Will also persist irq/nmi flags, improving post-load interrupt timing consistency.  

### 2. [x] Missing serialization of APU register latch array (`apuRegLatch`)
**Probability:** High (secondary destabilizer, especially after loads then APU core swaps).  
**Mechanism:** `Bus` keeps `apuRegLatch[0x18]` (last writes $4000–$4017) to replay when swapping APU cores. Not included in `NesState`. After load, latches revert to zero; later core switch replays zeros, wiping channel configs / frame sequencer state -> audio silence or timing distortion, potentially influencing games that rely on APU frame IRQ (rare) or simply causing perceived hang if code polls audio IRQ that never arrives.  
**Evidence:** `Bus` field is private; `SaveState()` only captures JSON strings + RAM arrays + controller + core IDs, not latches.  
**Confirm:**  
1. Save → modify some APU regs (e.g., enable square channel) → load → switch APU core → observe registers reapplied as zeros (dump APU core state or audio output).  
2. Instrument to print first 8 latch bytes pre/post load.  
**Fix:** Add a public field array in `NesState` (e.g., `apuRegLatch`) and copy in/out; apply before or right after selecting APU core so initial audio core state matches.  
**Risk:** Very low.  

### 3. [x] Other subsystem state classes that might rely on properties would silently serialize as `{}`
**Probability:** High (latent / game-specific).  
**Mechanism:** Any APU / PPU / Mapper variant implementing `GetState()` returning an object whose members are properties instead of public fields will produce empty JSON. Those cores then fail to restore internal counters (frame sequencer, envelopes, scanline timers).  
**Evidence:** Current inspected primary PPU/APU state classes seem to use public fields, but alternative cores (suffixes FIX / FMC / QN / etc.) need audit.  
**Confirm:**  
1. Enumerate each `GetState()` return type and check for property usage.  
2. Log serialized length per subsystem; anomaly: near-empty (<5 chars).  
**Fix:** Same serializer enhancement (support properties) or change those state containers to public fields.  
**Risk:** Low; unify approach prevents future regressions.  

### 4. [ ] Savestate snapshot is non-atomic (tearing between subsystems)
**Probability:** Medium-High (exacerbates after Theory 1 but not sole cause).  
**Mechanism:** `SaveState()` sequentially: serialize CPU → PPU → APU → Mapper → clone RAM / PRG / CHR. No global pause; if `RunFrame()` could interleave (UI thread vs event loop) one or more subsystems may advance. Even without threading, capturing CPU first and RAM later mid-instruction can mismatch PC vs memory contents (e.g., instruction partially applied).  
**Evidence:** No lock or `bus` freeze call; CPU executes instructions in `RunFrame()`. WASM single-threaded mitigates race but instruction boundary mid-state capture still inconsistent.  
**Confirm:**  
1. Instrument per-step timestamp; if time deltas show potential >frame micro-slices or any asynchronous yield, suspect tear.  
2. Save during heavy mapper IRQ activity; compare restored vs immediate subsequent IRQ fire timing.  
**Fix:** Introduce `bus.BeginAtomicSnapshot()` which halts execution loops (or sets a flag) until snapshot complete; optionally capture a unified cycle counter first.  
**Risk:** Minimal; slight performance cost.  

### 5. [ ] Cartridge rebuild + CPU reset before applying CPU JSON amplifies divergence
**Probability:** Medium.  
**Mechanism:** On load, if ROM differs or no cartridge, code rebuilds `Cartridge` + `Bus`, then calls `bus.cpu.Reset()` before applying CPU state. Given empty CPU JSON (Theory 1), machine effectively hard resets PC to Reset vector while other subsystem states are time-shifted → perceived soft reset or logical dead-end.  
**Confirm:** Force load with same ROM vs mismatched; observe difference. After fixing CPU serialization, resets should disappear.  
**Fix:** After rebuild, apply CPU JSON FIRST (once valid). Only call `Reset()` if no CPU state is present.  
**Risk:** Low; ensure reset path still used for incompatible / missing state.  

### 6. [ ] Pending IRQ/NMI flags lost (irqRequested/nmiRequested)
**Probability:** Medium. (Currently part of Theory 1; listed separately for visibility.)  
**Mechanism:** Flags indicate an interrupt will occur on next boundary. Dropping them can strand code waiting for NMI (vblank) or mapper IRQ, leading to polling loop hang.  
**Confirm:** Breakpoint before save when an NMI is imminent; inspect flags; load state; verify if ISR triggers next frame.  
**Fix:** Included with CPU state serialization fix.  

### 7. [ ] Mapper IRQ timing slight phase drift (e.g., MMC3 `irqCounter` reload edge cases)
**Probability:** Medium-Low.  
**Mechanism:** Mapper4 saves `irqCounter`, `irqLatch`, `irqReloadPending`, `irqEnable`, `irqAsserted`. If snapshot occurs between decrement & reload logic vs CPU cycle alignment, restored state might shift next IRQ by 1 scanline or miss an assert, possibly altering split-screen code. Generally yields visual glitches, rarely full freeze unless game’s main loop depends on precise IRQ ordering.  
**Confirm:** Save just before known IRQ (status bar); reload; compare raster effect line.  
**Fix:** (Optional) Save a global CPU cycle count and/or PPU dot to re-synchronize relative phase; or save a “cyclesUntilNextMapperIrq” derivative.  

### 8. [ ] Stack pointer (SP) mismatch vs cloned stack page ($0100) corrupts return flow
**Probability:** Medium-Low (subsumed by Theory 1 but enumerated).  
**Mechanism:** Stack bytes restored from RAM clone, but SP not restored (because CPU JSON empty) → returns/NMIs use stale or reset SP producing wrong return addresses → jump into zeroed memory → hang/reset.  
**Confirm:** Compare SP before save and after load (currently likely different). After CPU fix, should match.  
**Fix:** CPU state serialization.  

### 9. [ ] Controller shift/strobe restored while CPU registers not → input polling loops desync
**Probability:** Low-Medium.  
**Mechanism:** Input latch state rewound to earlier frame but CPU PC not; code reading controller may get unexpected bit sequence and loop waiting for strobe pattern that no longer aligns.  
**Confirm:** Save mid multi-read of 0x4016; inspect controllerShift pre/post.  
**Fix:** CPU fix; optionally snapshot at instruction boundary (atomic).  

### 10. [ ] cycleRemainder alone insufficient for cross-subsystem temporal alignment
**Probability:** Low-Medium.  
**Mechanism:** Only a fractional frame CPU cycle remainder is kept; no unified absolute cycle counter. Some timing calculations (APU frame sequencer, mapper IRQ scheduling) may depend on total elapsed cycles modulo frame; restoring just remainder may shift first interrupt relative ordering.  
**Confirm:** Add monotonic `long totalCpuCycles`; persist; measure interrupt delta difference with vs without.  
**Fix:** Persist master cycle count; optionally derive other timers from it during restore.  

### 11. [ ] Lack of PPU <-> CPU alignment guarantee on NMI boundary
**Probability:** Low-Medium.  
**Mechanism:** PPU state (scanline, cycle) restored; CPU registers (missing) cause PC to reflect different pre/post NMI point. Game waiting for NMI sets a flag/polls $2002 may stall if NMI already “consumed” or was supposed to trigger one instruction later.  
**Confirm:** After CPU fix, test high NMI-reliant games (e.g., ones with busy-wait loops) for improved reliability.  
**Fix:** CPU fix plus optional: capture whether NMI pending/occurred this frame and PPU frame timing dot; restore both.  

### 12. [ ] APU frame sequencer / envelope / length counters unsafely serialized in some cores
**Probability:** Low-Medium (depends on property vs field usage; not yet audited).  
**Mechanism:** If any counters not in state object, audio frame IRQ timing drifts; some titles poll $4015 or rely on periodic IRQ -> could hang.  
**Confirm:** Audit all APU `GetState()` implementations; diff field list vs hardware counters.  
**Fix:** Add missing fields; ensure serializer captures them.  

### 13. [ ] Property omission future regression risk (new fields silently ignored)
**Probability:** Low (future-proofing).  
**Mechanism:** Developers may add timing/state as properties to any state class; silently lost -> reintroduce intermittent saves bug.  
**Confirm:** Add unit test that round-trips every `GetState()` object and asserts JSON contains expected member names.  
**Fix:** Update serializer to include public get/set properties of primitive/array types.  

### 14. [ ] Controller mid-edge capture (0x4016 write vs reads) yields impossible state post-load
**Probability:** Low.  
**Mechanism:** Snapshot between write that latches data and sequential reads could restore partially consumed shift register sequence; most games robust, so rarely manifests as freeze.  
**Confirm:** Stress test by saving every instruction during controller polling; look for anomalies.  
**Fix:** Atomic snapshot or mark input shift progress along with cycle count.  

### 15. [ ] Audio ring buffer residual influencing perceived “reset”
**Probability:** Very Low.  
**Mechanism:** Old samples flushed after load may momentarily silence audio, user perceives as reset (non-technical).  
**Confirm:** Observe that execution actually proceeds (PC advances) while only audio silent.  
**Fix:** N/A (cosmetic).  

---

## Consolidated Immediate Action Plan
1. Implement serializer support for properties OR convert all state DTOs (CPU first) to public fields. (Targets Theories 1,3,6,8,13.)
2. Serialize and restore `apuRegLatch`. (Theory 2.)
3. Introduce optional atomic snapshot barrier (set a flag halting `RunFrame()` loop or capture after finishing frame). (Theory 4.)
4. Adjust load ordering: rebuild cart -> (do NOT reset if CPU JSON present) -> apply mapper/ram/prg/chr -> apply CPU -> PPU -> APU -> latches. (Theories 5 & 6.)
5. Add logging / assertions: lengths of each subsystem JSON; warn if `{}` encountered. (Covers discovery for Theory 3.)
6. (Optional) Add master `long totalCpuCycles` to state for advanced timing alignment (Theories 7,10,11).  

## Suggested Confirmation Test Matrix
| Test | Before Fix Expected | After Fix Expected | Theories Covered |
|------|---------------------|--------------------|------------------|
| Save/Load in IRQ-heavy MMC3 game (e.g., status bar split) | Occasional reset/freeze | Stable continuation | 1,5,7 |
| Save/Load mid NMI polling loop | Possible hang waiting | Loop resumes / NMI handled | 1,6,11 |
| Save/Load then immediately switch APU core | Channels reset/muted | Audio config preserved | 2 |
| Serialize small CPU state JSON length check | `{}` | Non-empty with fields | 1 |
| Round-trip all core states property audit | Some `{}` anomalies | All contain members | 3,13 |
| Repeated save/load stress (100x) | Intermittent divergence | Deterministic state | 1,4 |

## Implementation Notes
– Keep serializer primitive-only; include properties via `GetProperties(BindingFlags.Instance|Public)` filtering `CanRead && CanWrite && (Type.IsPrimitive || Type==typeof(string) || IsArrayOfPrimitive)` to stay AOT safe.  
– After CPU fix, re-test to ensure no dynamic IL emission (avoid non-public reflection).  
– Provide versioning: include `stateVersion` field; if missing on load assume legacy (CPU empty case) and maybe fallback repair logic (skip applying empty CPU JSON).  
– Add unit/integration test harness: run known ROM, advance N frames, save, run more, load, ensure digest (`GetStateDigest`) matches previously captured digest at that frame.  

## Checklist (Execution Tracking)
- [ ] Add property support OR field conversion for `CpuSharedState`
- [ ] Log serialized CPU JSON length (temporary)
- [ ] Audit & adapt other state DTOs (APU/PPU/Mapper variants)
- [ ] Serialize `apuRegLatch`
- [ ] Reorder load sequence to avoid premature CPU reset
- [ ] Optional atomic snapshot barrier
- [ ] Add master cycle counter (optional)
- [ ] Add regression test for non-empty CPU state
- [ ] Document savestate versioning

---
Document last updated: (populate on edits).
