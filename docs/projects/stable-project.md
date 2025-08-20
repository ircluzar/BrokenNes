# Stable Emulator MVP (Quick Stability & Performance)

Goal: Reach a “good enough” baseline: smooth 60 Hz visuals, clean audio (no obvious pops), responsive input — with the least code + risk. Advanced tuning, regression harnesses, rich telemetry, and adaptation are deferred.

---
## 1. MVP Success Criteria
| Area | Target (Good Enough Now) | Defer Precise Metrics |
|------|--------------------------|-----------------------|
| Video | 60 fps perceived, no visible hitching | Jitter std dev calculations |
| Audio | No audible underrun pops during 2+ minute play | Detailed underrun counts |
| Input | Feels instant (< ~1 frame perceptually) | Instrumented latency timing |
| GC / Alloc | No recurring stutter felt | Gen0 count tracking dashboards |
| Determinism | Consistent runs with same ROM | Formal determinism tests |

If any target obviously fails, fix before adding features.

---
## 2. Scope (Included vs Deferred)
Included Now:
- Fixed timestep core with cooperative slices.
- Basic high‑res timing wrapper.
- Minimal perf counters (frame time, audio buffered samples).
- SharedArrayBuffer audio ring buffer (single producer, single consumer) with simple high/low water marks.
- rAF-driven presenting (JS pulls when frameVersion changes).
- Preallocated frame + audio buffers (avoid hot-path allocations).

Deferred (MVP+ Later): adaptive slice tuning, extended overlay, metrics export, automated perf tests, stress harness, parameter auto-adjust, frameskip, warm start, resampler, extensive GC audits.

---
## 3. Core Parameters (MVP Defaults)
| Name | Value | Rationale |
|------|-------|-----------|
| CpuCyclesPerSecond | 1789773 | NTSC constant |
| SliceCycles | 1000 | Good tradeoff between overhead & latency |
| MaxWorkSliceMs | 2.0 | Prevent monopolizing main thread |
| AudioSampleRate | 44100 | Common, matches context usually |
| AudioLowWater | 1024 samples | ~23 ms safety |
| AudioHighWater | 2048 samples | Cap latency < ~50 ms |
| AudioCriticalLow | 512 samples | Boost production urgency |

Keep these hard-coded initially; tune manually only if something breaks.

---
## 4. Minimal Task Checklist
Implementation (stop once stable):
1. High‑Res Clock
    - [x] JS interop: (initially existing rAF path) managed `Stopwatch` wrapper added
    - [x] `HighResClock.NowSeconds` wrapper (double seconds)
2. Fixed Step Slice Loop
    - [x] `RunCycles(int cycles)` extraction (CPU+PPU+APU)
    - [x] Accumulate elapsed * CpuCyclesPerSecond (implemented in Blazor slice loop `_sliceAccumCycles`)
    - [x] While enough cycles: run slice, decrement accumulator
    - [x] On frame boundary: increment `SliceFrameVersion`
    - [x] Yield (`await Task.Yield()`) when loop slice budget > `MaxWorkSliceMs`
3. Audio Ring Buffer
    - [ ] Allocate SAB samples (Float32, capacity >= 4096 frames)
    - [ ] Control SAB: readIndex, writeIndex, capacity (Int32)
    - [ ] Producer: write only if buffered < HighWater; stop early if >= HighWater
    - [ ] Detect critical low (< AudioCriticalLow) to temporarily skip yields (still cap at a reasonable ms)
4. Presentation
    - [ ] Expose `getFrameVersion()` + `getFrameBufferPtr()`
    - [ ] JS rAF loop: if version advanced, blit/upload once per frame


Anything not on this list: skip for now.

---
## 5. Slim Loop Sketch (Concept)
```csharp
const int CpuCyclesPerSecond = 1789773;
int sliceCycles = 1000;
double last = HighResClock.NowSeconds;
double accumCycles = 0;

while (running) {
     var now = HighResClock.NowSeconds;
     accumCycles += (now - last) * CpuCyclesPerSecond;
     last = now;
     var sliceWindowStart = now;

     while (accumCycles >= sliceCycles) {
          RunCycles(sliceCycles);
          accumCycles -= sliceCycles;
          ProduceAudio(); // obeys high/low water marks
          if (Ppu.FrameCompleted) { frameVersion++; Ppu.FrameCompleted = false; }

          if (((HighResClock.NowSeconds - sliceWindowStart) * 1000.0) > MaxWorkSliceMs && !AudioCriticallyLow()) {
                await Task.Yield();
                sliceWindowStart = HighResClock.NowSeconds;
          }
     }

     await Task.Yield(); // Let rAF & input breathe
}
```

---
## 6. Interop (MVP Only)
Frame: `getFrameVersion()`, `getFrameBufferPtr()`.
Audio: Two SABs (samples + control). Atomics for indices.
No adaptive tuning logic yet.

---
## 7. Manual Validation Steps (Lightweight)
1. Open one ROM: confirm audible sound, no pops in first 2 minutes.
2. Observe overlay (or console logs) – FPS roughly 59–60.
3. Resize / tab switch briefly: emulator recovers without permanent desync.
4. Input (jump/shoot) feels immediate.

If failure: tweak sliceCycles (range 600–1500) OR adjust water marks (+/- 256) before adding features.

---
## 8. Post-MVP Backlog (Do Later)
Adaptive scheduling, richer metrics (jitter, underruns count), JSON export, automated perf harness, frameskip fallback, warm audio start, shader path optimization, resampler, CI perf regression gates.

---
## 9. Guiding Principle
Keep it boring & predictable first. Only add sophistication (adaptation / telemetry depth) once the simple loop is undeniably stable.

End of MVP plan.

---
### Repository Status (Aug 2025)
Implemented so far:
- HighResClock (Stopwatch-based) added.
- Slice stepping API (`RunCycles`, `SliceFrameVersion`, `UseSliceMode`) added to `NES` (non-invasive to existing frame path).

Remaining for full "stable loop" MVP:
- External fixed-step accumulator driving `RunCycles` instead of full-frame `RunFrame` calls in Blazor page.
- Cooperative yield budget & audio water-mark pacing.
- SharedArrayBuffer based audio ring (current path still pulls 2K sample arrays per frame).
- Minimal overlay (FPS + audio buffered samples) tied to slice mode.

Rationale: Landed initial primitives separately to de-risk before replacing the active scheduling loop.
