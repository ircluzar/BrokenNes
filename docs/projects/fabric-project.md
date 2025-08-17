# Fabric Project (Backbone Preparation)

Status: draft / exploratory
Owner: (add maintainer)
Target Milestone: backbone phase 1

## 1. Purpose

Introduce a lean, extensible "fabric" layer that generalizes the current `NES` monolith into composable, hot‑swappable parts (board, bus core variants, existing CPU / PPU / APU cores, plus future experimental subsystems) while preserving:

* Zero (or near‑zero) perf regression vs current tight path.
* The existing reflection-driven auto discovery model (CoreRegistry style) so new core types appear automatically in UI dropdowns and persist in session / save‑state.
* Shared state transfer so components can be swapped live without losing gameplay continuity.
* Backward compatibility: existing `NES` class keeps working until the fabric path is promoted.

## 2. High-Level Concept

```
        +------------------------------+
        |          Fabric              |
        | (Orchestrator / Coordinator) |
        +------------------------------+
          |       |        |       |
          v       v        v       v
       Board   BusCore   CpuCore  PpuCore  ApuCore  (…future)
         |        |         |       |       |
    Cartridge   Memory   Execution  Video   Audio
      Mapping   Map+IO    Engine   Engine  Engine
```

The Fabric provides a **stable orchestration contract** (`IFabric`) that plugs together individually swappable core *categories*:

| Category | Interface | Prefix (Reflection) | Notes |
|----------|-----------|---------------------|-------|
| Board / System | `IBoard` | `BRD_` | Encapsulates high-level console wiring: reset sequencing, frame pacing policy hooks, exposes board-level shared state. |
| CPU | existing `ICPU` | `CPU_` | Unchanged. |
| PPU | existing `IPPU` | `PPU_` | Unchanged. |
| APU | existing `IAPU` | `APU_` | Unchanged. |
| Bus Core Variant | `IBusCore` (new) | `BUS_` | Implements memory map + read/write policy + fast paths; wraps existing `IBus` semantics. |
| Future (Scheduler, Debug, etc.) | Extension interfaces | e.g. `SCH_` | Optional categories loaded the same way. |

The current `Bus` class already exposes an `IBus` interface. We will *not* widen `IBus` (keep it tiny for inlining). Instead we introduce **bus cores** implementing `IBusCore` which internally own an `IBus` or *are* an `IBus` and provide capability discovery.

## 3. Interfaces (Initial Draft)

```csharp
public interface IFabric
{
    // Core lifecycle
    void LoadRom(byte[] rom);           // delegates to board + cartridge
    void Reset(bool hard = false);      // signals components
    void RunFrame();

    // Core selection (generic, reflection suffix IDs)
    bool SetBoard(string id);
    bool SetBus(string id);
    bool SetCpu(string id);
    bool SetPpu(string id);
    bool SetApu(string id);

    IReadOnlyList<string> GetBoardIds();
    IReadOnlyList<string> GetBusIds();
    IReadOnlyList<string> GetCpuIds();
    IReadOnlyList<string> GetPpuIds();
    IReadOnlyList<string> GetApuIds();

    // Frame data
    byte[] GetFrameBuffer();
    float[] GetAudioBuffer(int max = 2048);

    // State (save / load)
    string SaveState();
    void LoadState(string json);
}

public interface IBoard
{
    // Called after construction and each hot-swap with the active fabric
    void Initialize(IFabricHost host); // host exposes shared state & services

    // Board contributes per-frame pacing; returns cycles to execute or delegates to CPU directly
    void RunFrame();

    object GetState();
    void SetState(System.Text.Json.JsonElement state);
}

public interface IBusCore : IBus
{
    // Called when (re)attached; may reuse previous shared state for fast hot-swap
    void Initialize(IFabricHost host);
    object GetState();
    void SetState(System.Text.Json.JsonElement state);
}

// Bridge object passed to swappable components (lean, stable)
public interface IFabricHost
{
    // Access to currently active core instances
    ICPU Cpu { get; }
    IPPU Ppu { get; }
    IAPU Apu { get; }
    IBus Bus { get; }

    // Shared mutable memory blocks (RAM, PRG/CHR RAM pointers)
    byte[] SystemRam { get; }
    byte[] PrgRam { get; }
    byte[] ChrRam { get; }

    // Instrumentation & speed toggles
    SpeedConfig Speed { get; }

    // Hot-swap safe key-value scratchpad (optional)
    T? GetShared<T>(string key) where T:class;
    void SetShared(string key, object value);
}
```

### Notes:
* `IFabricHost` intentionally omits *selection* methods to prevent cycles; selection stays centralized in Fabric.
* Shared scratchpad allows new components to coordinate transitional data during hot-swap without widening interfaces (e.g., cached palette, branch predictor history, APU mixer ring buffer metadata).
* `object GetState()` pattern mirrors existing CPU/PPU/APU usage so serializer reuse is trivial.

## 4. Reflection Discovery

Reuse `CoreRegistry` by extending generic scanning to new prefixes:

* Add `BoardTypes` (suffix map) discovered by prefix `BRD_` implementing `IBoard`.
* Add `BusTypes` discovered by prefix `BUS_` implementing `IBusCore` (must also satisfy `IBus`).

Backward compatibility: If no `BRD_` implementation is found, the Fabric synthesizes a `DefaultBoard` wrapper that mimics current `NES` timing (fixed-point frame scheduler + batch flush path).

## 5. Hot-Swap State Transfer Flow

1. Capture previous component's `GetState()`.
2. Instantiate new component via reflection.
3. Provide `IFabricHost` (already bound to shared buffers / existing cores).
4. Call `SetState()` in a try/catch (best effort). If incompatible, component starts from clean reset.
5. Perform any buffer clearing (mirrors current PPU `ClearBuffers()` pattern) to avoid visual/audio artifacts.

For bus/board swaps, *shared memory arrays are NOT reallocated*. Only pointers/logic change, guaranteeing minimal GC churn.

## 6. Save-State Impact

Existing `NES.SaveState()` serializes CPU, PPU, APU, Mapper, RAM. Fabric must append (or replace) with:

* `boardCoreId`, `busCoreId` suffix strings.
* Serialized `board` & `bus` state JSON strings (same minimalist serializer so AOT safety is preserved).

Backward compatibility plan:

* Loader detects absence of new fields -> treat as legacy; instantiate default board & default bus core.
* When new fields present but a specific board/bus implementation is missing (build without plugin), fallback to default but *ignore* unknown state gracefully.

## 7. UI Integration

Current UI enumerates CPU/PPU/APU ids via `NES` delegating to `Bus`. Fabric will expose `GetBoardIds()` and `GetBusIds()` surfaces. UI simply adds two more dropdowns following the same pattern (selected suffix persisted in session storage & included in updated save-state JSON).

## 8. Performance Goals & Techniques

Target: < 1% overhead vs current path when using default board + default bus.

Techniques:
* Keep `IBus` as the hot path (already `[MethodImpl(AggressiveInlining)]` on `Read`/`Write` callsites).
* `Fabric` holds direct fields (`_cpu`, `_ppu`, `_apu`, `_bus`) so JIT can devirtualize in mono/wasm where possible.
* Optional: mark default board/bus classes `sealed` to enable inlining.
* Feature interfaces / capability discovery use pattern `if (component is IEventSchedulerProvider esp)` to avoid broad base interface bloat.
* Avoid allocation during frame loop: prefetch references; no LINQ; no reflection after startup.
* Keep reflection scan lazy & run once (reuse `CoreRegistry.Initialize()`).

## 9. Incremental Implementation Plan

Phase 0 (Preparation)
* Add `IBoard`, `IBusCore`, `IFabric`, `IFabricHost` interfaces (empty implementations).
* Extend `CoreRegistry` with `BoardTypes`, `BusTypes` (no UI yet).

Phase 1 (Default Wiring)
* Implement `Fabric` that internally creates current `NES` style components: wraps existing `Bus` as a `BUS_STD` implementation (extract minimal adapter).
* Implement `BRD_STD` board using existing timing logic (copy/move code from `NES.RunFrame` split into board vs fabric responsibilities).
* Add reflection-driven selection + simple test harness.

Phase 2 (UI & SaveState)
* Expose new dropdowns; persist chosen board/bus suffixes.
* Extend save-state JSON with new fields; add backward compatibility branch.

Phase 3 (Experimental Implementations)
* Create `BRD_EVT` (event scheduler heavy) and / or `BUS_FLAT` (further optimized page table experiments).
* Benchmark vs baseline; enforce <1% regression using existing instrumentation.

Phase 4 (Migration / Deprecation)
* `NES` becomes thin facade over a default `Fabric` instance or is gradually retired.

## 10. Responsibilities Split (Target End State)

| Concern | Fabric | Board | BusCore | CPU | PPU | APU | Mapper |
|---------|--------|-------|---------|-----|-----|-----|--------|
| ROM load | ✓ (delegates) | ✓ (reset sequencing) | | | | | ✓ |
| Frame pacing | (delegates) | ✓ | | | | | |
| CPU cycle execution loop | (delegates) | ✓ | | ✓ | | | |
| Memory map (R/W) | | | ✓ | R/W calls | R/W via bus | R/W via bus | PRG/CHR via bus |
| Fast OAM DMA | | | ✓ | | ✓ | | |
| Hot-swap management | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Shared state storage | ✓ (host) | (refs) | (refs) | (internal snapshot) | (internal snapshot) | (internal snapshot) | mapper state |
| Save/Load JSON | ✓ (orchestrate) | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |

## 11. Risks / Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Additional indirection harms wasm perf | Frame time regression | Seal classes; keep interfaces tiny; measure continuously. |
| State incompatibility across variants | Bad hot-swap UX | Strict versioned state shape; defensive try/catch & partial restore. |
| UI clutter | Confuses users | Group advanced components under an "Advanced" accordion if >1 extra appears. |
| Over-generalization stalls delivery | Delayed backbone goals | Timebox each phase; cut at Phase 2 if no experimental variant demand yet. |

## 12. Open Questions
* Do we need a distinct cartridge interface (`ICartridge`) for multi-system later? (Defer; current class fine.)
* Should event scheduler live in board or its own component category (`SCH_`)? (Start inside board; extract if multiple independent schedulers emerge.)
* Will we allow multiple buses simultaneously (e.g., debug overlay)? (Out of scope now.)

## 13. Immediate Action Items
1. Add new interfaces + registry extensions (Phase 0).
2. Scaffold `BRD_STD` & `BUS_STD` adapters around existing logic.
3. Provide micro-bench comparison harness (baseline vs fabric) with alert threshold.

---
Append edits / decisions below this line as project evolves.
