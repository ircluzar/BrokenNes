# Core lifecycle and resource optimization

This note summarizes the lifecycle rules for cores (CPU/PPU/APU) and the lazy/factory approach used by BrokenNes, with special attention to AOT/WASM constraints.

- Discovery and factories
  - `CoreRegistry` scans the assembly for concrete core types with names `CPU_*`, `PPU_*`, and `APU_*` and caches maps: suffix -> Type.
  - `Bus` eagerly creates the CPU, but lazily creates PPU and APU instances on first use to avoid large allocations at startup (framebuffers, audio rings).
  - The same `Bus` keeps one instance per suffix; subsequent selections reuse that instance.

- Hot-swapping cores
  - PPU: capture state via `PpuSharedState`, call `ClearBuffers()` on the old core, `SetState(...)` on the new one, then `ClearBuffers()` again on the new to force a fresh redraw without stale arrays.
  - APU: selection reapplies `$4000-$4017` (except `$4014`) using an internal register latch so the new core inherits the user/game state.

- Reset/clear hooks (minimal, allocation-friendly)
  - PPU: `IPPU.ClearBuffers()` should drop large temporary arrays like the framebuffer.
  - APU: `IAPU.ClearAudioBuffers()` drops queued audio and pacing; `IAPU.Reset()` performs a minimal reset without forcing a reallocation.
  - CPU: kept eager and unchanged for now.

- Save/Load constraints (AOT-safe)
  - SaveState stores pre-serialized JSON strings for subsystems to avoid polymorphic serialization IL in AOT/WASM builds.
  - PPU framebuffers are intentionally omitted from state; they are transient and redrawn after load.
  - On load, PPU `ClearBuffers()` is called to ensure fresh drawing; APU clears audio queues to avoid stutter.

- Audio scheduling
  - Each APU core maintains a ring buffer; the host drains fixed-size chunks. Excess backlog is trimmed to prevent runaway latency.

- Acceptance reminders
  - One instance per suffix per `Bus`.
  - Hot-swap does not cause large transient allocations.
  - Save files do not include large transient arrays.
  - Works under AOT/WASM without reflection-heavy serialization.
