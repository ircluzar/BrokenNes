# Core Category Project

Add a Category marker to all cores (CPU, PPU, APU, and Clock) and surface it in the Cores page. Shaders are excluded (already categorized).

Categories to use:
- Standard
- Improved
- Degraded
- Unstable
- Enhanced

Notes
- Keep Category as a short, user-facing string from the above set. If missing, fall back to "Uncategorized" in UI.
- Do not change behavior or save-state format; this is a metadata-only feature.

## Interface and contract updates

- [ ] ICPU: add `string Category { get; }`
- [ ] IPPU: add `string Category { get; }`
- [ ] IAPU: add `string Category { get; }`
- [ ] IClock: add `string Category { get; }`
- [ ] Decide whether to keep Category as `string` (consistent with Shaders) or define a small enum for internal use; UI will still show the string. For this phase, prefer `string`.

Success criteria
- Build compiles after updating all implementations listed below.

## UI wiring (Pages/Cores.razor)

- [ ] CPU/PPU/APU cards: populate and display Category in the footer (replace "UNIMPLEMENTED").
- [ ] Extend CoreMeta with `Category` and set from the core instance (safe-get with fallback to "Uncategorized").
- [ ] Optional: sort/group cores by Category similar to Shaders (primary: Category, secondary: DisplayName/Id).
- [ ] Clock cards: extend `ClockInfo` with `Category`, set from `IClock.Category`, show in footer.
- [ ] Keep defensive try/catch around reflection/instantiation as in existing code.

## Core implementations to update

Implement `string Category { get; }` on each concrete core class. Suggested mapping is included as a placeholder; adjust during implementation.

### CPUs (NesEmulator/cpus)
- [ ] CPU_FMC.cs — default Standard
- [ ] CPU_SPD.cs — Improved or Enhanced
- [ ] CPU_LOW.cs — Degraded
- [ ] CPU_LW2.cs — Degraded
- [ ] CPU_EIL.cs — Unstable or Enhanced (choose appropriately)
- [ ] CPU.cs — base/shared (add default string if it implements interface)

### PPUs (NesEmulator/ppus)
- [ ] PPU_FMC.cs — Standard
- [ ] PPU_SPD.cs — Improved or Enhanced
- [ ] PPU_LOW.cs — Degraded
- [ ] PPU_LQ.cs — Degraded
- [ ] PPU_BFR.cs — Enhanced or Improved
- [ ] PPU_EIL.cs — Unstable or Enhanced
- [ ] PPU_CUBE.cs — Enhanced or Unstable

### APUs (NesEmulator/apus)
- [ ] APU_FMC.cs — Standard
- [ ] APU_SPD.cs — Improved or Enhanced
- [ ] APU_LQ2.cs — Degraded
- [ ] APU_QLQ2.cs — Degraded
- [ ] APU_QN.cs — Degraded or Unstable
- [ ] APU_WF.cs — Enhanced or Unstable
- [ ] APU.cs — base/shared (add default string if it implements interface)

### Clocks (NesEmulator/clocks)
- [ ] CLOCK_FMC.cs — Standard
- [ ] CLOCK_CLR.cs — Enhanced or Improved
- [ ] CLOCK_TRB.cs — Unstable or Experimental (map to Unstable)

## Documentation updates

- [ ] Add a short section to `docs/core-lifecycle.md` describing the Category field and its intent.
- [ ] Add the category list and guidance to a new or existing contributor doc (keep it brief, mirror this file).

## Validation

- [ ] Build in Release: `dotnet build -c Release` (CI and local).
- [ ] Run app, open Cores page; verify each card footer shows the expected Category.
- [ ] Quick smoke: ensure save/load, hot-swap, and performance are unaffected.

## Optional follow-ups (Phase 2)

- [ ] Color badges by category in card footer.
- [ ] Filter/group UI by Category.
- [ ] Promote Category to a small enum in code and provide a helper to stringify.

## Acceptance checklist

- [ ] All core interfaces expose Category.
- [ ] Every concrete CPU/PPU/APU/Clock class returns a Category from the approved list.
- [ ] Cores page shows Category for all cores (no "UNIMPLEMENTED" footers).
- [ ] Sort order stable and user-friendly; no crashes in environments without full reflection.
