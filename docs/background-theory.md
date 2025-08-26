# MMC5 (Mapper 5) background issue in Castlevania III — fix theories

This note lists concrete theories for why backgrounds don’t appear in Castlevania III (MMC5) and how to fix them, ordered from most to least likely. The reference behavior is based on the BizHawk `ExROM` (MMC5) implementation provided.

## Requirements covered
- Create `background-theory.md` with theories on fixing the background issue
- Use `ExROM.cs` as a reference
- Order items by likelihood of fixing the issue

---

## 1) Missing MMC5 ExRAM Mode 1 BG-CHR banking (highest likelihood)

Status: Implemented
- Mapper5: Mode 1 per-tile banking in `PPURead(<$2000)` when `exramMode == 1 && !ppuFetchIsSprite` uses `exram[lastNtReadIndex]` + `$5130` to select the 4KB bank, derive 1KB segment, and fetch CHR.
- PPU: Calls `IMapper.PpuNtFetch(tileAddr)` before each BG tile fetch to update `lastNtReadIndex`.
- Files: `NesEmulator/mappers/Mapper5.cs` (PPURead/PPUWrite, `PpuNtFetch`), `NesEmulator/ppus/PPU_SPD.cs` (`RenderBackground` calls `PpuNtFetch`).

Symptom
- Background tiles render as blank/missing because the PPU BG pattern fetches aren’t redirected to the MMC5’s per-tile CHR bank table in ExRAM.

Reference behavior (from `ExROM.cs`)
- When `exram_mode == 1` and PPU phase is BG, MMC5 uses ExRAM entry selected by the last nametable read (`last_nt_read`) to choose the 4 KB CHR bank for the BG tile.
- Code path: `MapCHR()` checks `exram_mode == 1 && NES.ppu.ppuphase == PPU.PPU_PHASE_BG` and then:
  - Reads `EXRAM[last_nt_read] & 0x3F` to get the 4 KB bank index
  - Applies the high CHR bits (`chr_reg_high`) from `$5130`
  - Converts to 1 KB bank addressing and combines with the low address bits from PPU

What to implement/check in `Mapper5.cs`
- Track `last_nt_read` each time the PPU reads a nametable tile index (0x2000–0x23BF/… area).
- In CHR address mapping for PPU reads under BG phase, if `$5104` sets ExRAM Mode 1, override the BG CHR bank using `EXRAM[last_nt_read]` (+ `$5130` hi bits) as in BizHawk.
- Ensure the per-phase handling (BG vs OBJ) is respected for bank selection.

Impact
- This is the core feature CV3 uses for MMC5 split CHR banking per tile. Without it, BG tiles point to the wrong CHR (often bank 0), appearing blank.

## 2) Missing ExRAM-based attribute output in Mode 1 (very likely)

Status: Implemented
- PPU tries MMC5 override first: `GetMmc5Mode1BgPaletteIndex()`; if >=0, it uses that palette index instead of CIRAM attributes.
- Mapper5 computes palette from top 2 bits of `exram[lastNtReadIndex]` in Mode 1.
- Files: `PPU_SPD.cs` (two BG paths use `GetMmc5Mode1BgPaletteIndex()`), `Mapper5.cs` (method returns `(ex >> 6) & 3`).

Symptom
- Backgrounds appear with wrong palettes or look invisible because attribute bytes always resolve to 0 or wrong quadrant, effectively forcing backdrop color.

Reference behavior
- In attribute fetches (0x23C0–0x23FF per NT), MMC5 Mode 1 derives attribute bits from the ExRAM entry used for that tile. BizHawk returns a synthesized attribute byte based on top 2 bits of `EXRAM[last_nt_read]` shifted to the correct quadrant.
- Code path: `ReadPpu()` and `PeekPPU()` special-case attribute fetch when `exram_mode == 1`.

What to implement/check
- When the PPU fetches from the attribute table and Mode 1 is active, return the computed attribute value (extract 2-bit attribute from `EXRAM[last_nt_read]`, shift according to tile quadrant).
- Make sure `last_nt_read` is set on the preceding nametable index fetch.

Impact
- Without correct attributes, the BG can look like it’s “not there” even when pattern data is present.

## 3) Not honoring `$5105` per-nametable source (NT modes) and Fill Mode (likely)

Status: Implemented
- `$5105` value stored; `TryPpuNametableRead/Write` route NT reads/writes per quadrant: 0/1=CIRAM (fall back to PPU VRAM), 2=ExRAM, 3=Fill (uses `$5106/$5107`).
- Fill attributes are pre-packed to 8-bit pattern in `$5107` handler.
- Files: `Mapper5.cs` (`nametableControl`, `GetNtModeForAddress`, `TryPpuNametableRead/Write`).

Symptom
- PPU reads only from CIRAM (internal nametables), ignoring ExRAM-as-nametable or Fill Mode, leading to empty BG tiles.

Reference behavior
- `$5105` sets a 2-bit mode for each NT quadrant: 0=CIRAM A, 1=CIRAM B, 2=ExRAM, 3=Fill Mode. `$5106`/`$5107` provide tile and attribute fill values.
- Code path: `nt_modes[]` in `ExROM.cs` select per-NT mapping; PPU read/write routes to CIRAM, ExRAM, or returns fill values.

What to implement/check
- Parse `$5105` and route PPU read/write for each nametable quadrant accordingly.
- Implement Fill Mode returns for tile vs attribute regions using `$5106`/`$5107`.
- Allow PPU to read/write ExRAM as nametable when mode=2 (unless protected by other modes).

Impact
- CV3 uses these MMC5 nametable configurations; missing this results in BG blankness or incorrect tile indices.

## 4) Missing A/B CHR register split and BG/OBJ phase selection (likely)

Status: Implemented
- A (sprite) regs `$5120–$5127` -> `chrRegsSprite[8]`; B (background) regs `$5128–$512B` -> `chrRegsBgRaw[4]`, expanded per `chrMode` to 8×1KB slots.
- PPU sets phase via `PpuPhaseHint(isSpriteFetch, objSize16, ...)`; mapper chooses sprite vs background CHR slots accordingly.
- Note: `objSize16` isn’t used by the mapper (not needed for A/B choice here).
- Files: `Mapper5.cs` (`RebuildChrMapping`, `PpuPhaseHint`, `PPURead`).

Symptom
- BG uses the wrong CHR banks (sprite banks or a fixed bank) because the per-phase selection isn’t implemented.

Reference behavior
- `$5120–$5127` are the ‘A’ CHR regs; `$5128–$512B` are the ‘B’ regs. MMC5 chooses between A/B depending on PPU phase and tall-sprite mode.
- Code path: `MapCHR()` selects `_aBanks1K` or `_bBanks1K` based on `NES.ppu.ppuphase` and `obj_size_16`.

What to implement/check
- Implement the full set of CHR bank regs and the selection logic for BG vs OBJ, including the special handling for 8x16 sprites.
- Ensure that when Mode 1 is not active, BG uses A or B as per MMC5 rules (BizHawk has pragmatic handling here that works in practice).

Impact
- Incorrect bank selection will make BG tiles pull wrong patterns.

## 5) Ignoring `$5130` CHR high bits (likely)

Status: Implemented
- `$5130` low 2 bits are applied when writing `$512x` regs and also re-applied dynamically on `$5130` writes to all A/B CHR regs, then `RebuildChrMapping()` is called.
- Mode 1 path also incorporates `$5130` when forming the per-tile bank.
- Files: `Mapper5.cs` (handlers for `$5120–$512B`, `$5130`; Mode 1 CHR mapping in `PPURead`).

Symptom
- Only low CHR banks are accessible; higher banks needed by CV3 aren’t selected, making BG tiles blank or incorrect when the game expects >256x1KB CHR.

Reference behavior
- `$5130` contributes the top 2 bits of CHR bank index; BizHawk ORs these into the bank values for A/B regs and in Mode 1 path.

What to implement/check
- Apply `$5130 & 0b11` to extend CHR bank numbers anywhere banks are set or computed (A regs, B regs, Mode 1 path).

Impact
- Required when CHR size >128 KB or when layouts rely on upper banks.

## 6) EXRAM read/write protection by mode (plausible)

Status: Implemented (simplified BizHawk model)
- CPU reads $5C00–$5FFF return 0xFF unless `exramMode >= 2`; CPU writes are inhibited when `exramMode == 3`.
- PPU ExRAM-as-NT honors similar constraints for reads/writes.
- Files: `Mapper5.cs` (CPURead/CPUWrite cases, `TryPpuNametableRead/Write`).

Symptom
- Game writes its BG tables to ExRAM but emulator blocks writes (Mode 3) or returns 0xFF on reads, so the table stays empty -> blank BG.

Reference behavior
- `$5104` controls ExRAM modes; BizHawk allows CPU writes except Mode 3 and restricts reads from the EXP mirror depending on the mode.

What to implement/check
- Enforce correct CPU/PPU read/write rules for ExRAM across modes 0–3, matching BizHawk’s simplified model:
  - Mode 3: CPU writes inhibited
  - EXP reads return 0xFF unless mode >= 2
- Ensure `$5C00–$5FFF` CPU writes land in ExRAM when permitted.

Impact
- If writes are blocked or reads always 0xFF, Mode 1 lookup will yield zeros, blanking BG.

## 7) Nametable mirroring defaults or CIRAM routing incorrect (possible)

Status: Implemented
- Mapper exposes per-NT quadrant mode for `$2000–$2FFF`; both PPU cores honor modes 0/1 by mapping directly to CIRAM A/B, bypassing heuristic mirroring.
- `$5105` still applies a mirroring heuristic for general cases; ExRAM/Fill handled via overrides.
- Files: `Mapper5.cs` (`ApplyMirroringFrom5105`, `TryPpuNametableRead/Write`, `GetMmc5NtModeForAddress`), `PPU_SPD.cs` and `PPU_FMC.cs` (CIRAM A/B routing for modes 0/1).

Symptom
- If `$5105` is unimplemented and default mirroring is wrong, BG reads tiles/attrs from wrong CIRAM pages, effectively random/blank BG.

What to implement/check
- Make sure that in the absence of `$5105` writes, a sane default mirroring (e.g., vertical) is used, but switch to `$5105`-defined modes immediately once configured.

## 8) PPU phase/timing assumptions don’t align with fetch order (lower likelihood)

Status: Implemented
- PPU calls `PpuPhaseHint` to signal BG vs OBJ fetch phases; mapper latches a simple flag used during CHR reads/writes.
- Mode 1 disables BG pattern cache to avoid stale rows when banks change per tile.
- Files: `PPU_SPD.cs`+`PPU_FMC.cs` (`RenderBackground`/`RenderSprites` call `PpuPhaseHint`), `Mapper5.cs` (`PpuPhaseHint`, `PPURead`).

Symptom
- BG path in your mapper never activates because the emulator’s notion of BG vs OBJ phase or “tall sprite” state is missing.

Reference behavior
- BizHawk peeks PPU state (`ppuphase`, `obj_size_16`, `PPUON`) instead of counting fetches.

What to implement/check
- Provide a reliable BG/OBJ fetch-phase signal to the mapper’s CHR mapping path. For tall-sprite mode, follow the pragmatic check used by BizHawk.

## 9) IRQ behavior interfering with rendering (least likely)

Status: Implemented
- MMC5 scanline IRQ counter runs at BG fetch time (cycle 3 of visible scanlines) and asserts IRQ when `irqCounter == irqTarget+1` and enabled.
- Files: `Mapper5.cs` (`PpuScanlineHook`, `IsIrqAsserted`), `PPU_SPD.cs`+`PPU_FMC.cs` (IRQ callback and CPU request).

Symptom
- If IRQ logic suppresses rendering or causes timing glitches, BG might appear missing.

What to implement/check
- IRQ is probably unrelated to BG not appearing, but ensure it doesn’t reset rendering state inappropriately.

---

## Quick verification steps
- Log writes to `$5104–$5107`, `$5120–$512B`, `$5130` during CV3 boot and gameplay to confirm expected MMC5 setup.
- Log first 2–3 BG tile fetches per scanline: record `last_nt_read`, resulting CHR bank from ExRAM table, and attribute value returned in Mode 1.
- Inspect ExRAM contents at `$5C00–$5FFF` after the game initializes rooms; verify non-zero per-tile entries.
- Compare your `Mapper5.cs` CHR map and PPU nametable/attribute paths against the BizHawk `ExROM` code paths cited above.

## Minimal implementation target to unblock BG

Status: Done
- `$5104–$5107` + `$5130`: Implemented, including dynamic `$5130` updates across CHR regs.
- Track `last_nt_read`: Implemented via `PpuNtFetch` calls in PPU and mapper state.
- Mode 1 CHR + synthesized attributes: Implemented.
- `$5105` NT modes for CIRAM/ExRAM/Fill: Implemented.

Last verified: 2025-08-26 (Build OK)
1) Implement `$5104–$5107` and `$5130` registers and state.
2) Track `last_nt_read` during PPU nametable reads.
3) In PPU reads when phase=BG and Mode 1, map CHR using `EXRAM[last_nt_read]` plus `$5130` and return synthesized attribute bytes for attribute fetches.
4) Honor `$5105` NT modes for CIRAM/ExRAM/Fill routing.

These four items should make CV3 backgrounds appear in most cases.
