## MMC5 (Mapper 5) sprite/tile issues — most likely causes

This note compares our `NesEmulator/mapper/Mapper5.cs` against BizHawk’s golden `ExROM.cs` to explain why some sprites/tiles still fail to load or display correctly. Items are ordered by likelihood.

### 1) A/B CHR bank selection vs 8×16 “tall sprite” mode

Status: Implemented in `Mapper5.cs` (Aug 26, 2025). When 8×16 sprites are OFF, both BG and OBJ map from A-bank; when ON, BG uses B-bank and OBJ uses A-bank. Hooked via `PpuPhaseHint(isSpriteFetch, objSize16, ...)` and applied in PPU CHR read path.

- In ExROM.MapCHR:
  - If 8×16 sprites are enabled (`reg_2000.obj_size_16` and PPU is doing pattern fetch), OBJ fetches use the A-bank and BG fetches use the B-bank.
  - Otherwise (8×8 sprites), everything maps from the A-bank regardless of BG/OBJ.
- In our Mapper5:
  - We always choose A for sprites and B for background based purely on `ppuFetchIsSprite` hints, ignoring 8×8 vs 8×16 mode.
- Why it breaks: In 8×8 mode, BG still comes from the A-bank on MMC5. Using B for BG in this mode can select empty/incorrect CHR pages, causing missing tiles/objects.
- Fix direction: During CHR mapping, if 8×16 is OFF, route both BG and OBJ through the A-bank; only split A/B when 8×16 is ON and the PPU is in the corresponding BG/OBJ pattern phase (mirroring ExROM’s logic).

### 2) $5130 high CHR bits are retroactively re-applied (shouldn’t be)

Status: Implemented in `Mapper5.cs` (Aug 26, 2025). `$5130` now only updates `chrHigh` for future CHR bank writes; it no longer re-applies to existing regs retroactively.

- ExROM behavior:
  - `$5130` sets `chr_reg_high` that is OR’d into CHR regs only at the time `$5120-$512B` are written.
  - Changing `$5130` later does not retroactively modify previously-written CHR regs.
- Our Mapper5:
  - On `$5130` writes, we re-apply the high bits across existing CHR regs and rebuild mapping.
- Why it breaks: Some games update `$5130` for subsequent CHR writes only. Retroactively changing older regs selects different CHR pages than hardware, making sprites/tiles disappear or become scrambled after certain updates.
- Fix direction: Match ExROM: store `chrHigh` and only combine high bits when CHR regs are written; do not backfill existing regs on `$5130` writes.

### 3) BG CHR bank mapping tables differ in 4KB/2KB modes

Status: Implemented in `Mapper5.cs` (Aug 26, 2025). Mode 1 (4KB) now uses BG reg[3] for both halves; Mode 2 (2KB) now uses BG reg[1] for first 4KB and BG reg[3] for second 4KB, matching ExROM.

- ExROM.SyncCHRBanks nuances:
  - Mode 1 (4KB): BG side uses `regs_b[3]` for both halves.
  - Mode 2 (2KB): BG side uses `regs_b[1]` and `regs_b[3]` for the two 2KB segments.
- Our Mapper5:
  - Mode 1: expands BG `chrRegsBgRaw[0]` to slots 0–3 and `[1]` to slots 4–7.
  - Mode 2: expands BG `[0],[1],[2],[3]` sequentially.
- Why it breaks: If a game expects the MMC5/BizHawk pattern (using only specific BG regs in these modes), our broader/sequential mapping can point BG to unintended CHR banks, causing wrong or blank tiles.
- Fix direction: Replicate ExROM’s exact BG indexing in Mode 1 and Mode 2, including using the same register numbers for each segment.

### 4) A/B selection for non-pattern PPU cycles and $2007 CHR-RAM uploads

- ExROM uses `ab_mode` (set by writing `$5120-$5127` → A, `$5128-$512B` → B) when PPU isn’t actively fetching patterns; ambiguous CHR accesses (like CPU-driven `$2007` CHR-RAM writes) follow the most-recent bank set.
- Our Mapper5 uses `lastChrIoSprite` only for PPUWrite path; PPURead always keys off `ppuFetchIsSprite`.
- Why it breaks: If a game writes BG regs last and then uploads sprite pattern data via `$2007`, data may be written to the BG bank instead of the sprite (A) bank, leading to invisible sprites.
- Fix direction: Track and apply an `ab_mode` like ExROM for ambiguous/non-pattern phases (CPU `$2007`), and mirror the “last-write decides A/B” rule in both read and write paths.

### 5) Mode 1 (ExRAM-driven) background bank/attribute coupling timing

- ExROM updates `last_nt_read` only on real nametable tile fetches and synthesizes attribute bits from EXRAM for those tiles. Pattern fetches then use the same EXRAM entry to select the 4KB BG bank (plus `$5130` high bits).
- Our Mapper5 relies on the PPU to call `PpuNtFetch()` exactly at tile index reads. If this hook is missed or mis-timed, we use a stale `lastNtReadIndex`, selecting the wrong BG CHR bank and attribute—leading to miscolored or missing tiles.
- Fix direction: Ensure `PpuNtFetch()` is called for every visible tile fetch (not peeks/attributes), and never for attribute reads; validate with a tracing build that `lastNtReadIndex` lines up with BG pattern fetches.

### 6) PRG bank “RAM vs ROM” bit (bit7) not honored for $8000–$FFFF windows

- ExROM: PRG regs’ bit7 selects ROM (1) vs WRAM (0) for each 8KB window.
- Our Mapper5: treats PRG regs as ROM bank numbers only; WRAM is only at $6000–$7FFF.
- Why it can break: Some MMC5 content maps WRAM into $8000+ ranges (for data streams or overlays). If a game expects WRAM there and we serve ROM, assets referenced by sprite/tile upload code can be wrong or missing.
- Fix direction: Implement bit7 semantics per 8KB slot; route reads/writes to PRG-RAM when bit7=0, ROM when bit7=1, matching ExROM’s `PRGGetBank`.

### 7) Attribute emulation for Mode 1 (palette nibble placement)

- ExROM fakes attribute bytes by extracting the top 2 bits of the EXRAM tile entry and shifting them into the correct quadrant position when the PPU reads from the attribute table region.
- Our Mapper5 provides `GetMmc5Mode1BgPaletteIndex()` but relies on the PPU to plumb this into the palette selection without consuming the attribute bus read.
- Why it can break: If the PPU core doesn’t use this hook consistently, BG tiles may use wrong palettes or appear “missing” due to color 0/transparent mismatches.
- Fix direction: Confirm the PPU uses the mapper-provided palette index for BG tiles in Mode 1 and that attribute bus reads are returned as in ExROM when needed (fill vs exram vs ciram cases).

### 8) Nametable quadrant mapping ($5105) only partially honored

- ExROM uses per-quadrant `nt_modes` to route reads/writes to CIRAM A/B, ExRAM, or Fill—byte-accurate to hardware.
- Our Mapper5 heuristically sets global mirroring and exposes `TryPpuNametableRead/Write`, but correctness depends on how the PPU calls into these helpers.
- Why it can break: If the PPU doesn’t fully delegate NT reads/writes per-quadrant, tiles can come from the wrong table or fill region, yielding missing tiles/garbage.
- Fix direction: Ensure the PPU always consults `GetMmc5NtModeForAddress` and uses `TryPpuNametableRead/Write` for $2000–$2FFF, including attribute subpages and Mode 3 (fill).

---

## Quick validation ideas

- Log whether 8×16 is enabled and temporarily force BG to A-bank when 8×16 is OFF. If sprites/tiles appear, cause (1) is confirmed.
- Instrument `$5130` writes. If changing `$5130` mid-frame “fixes” visuals in our build but not in BizHawk, cause (2) is likely.
- In Mode 1, trace `PpuNtFetch` indices vs subsequent BG pattern fetches for a frame and compare with BizHawk state; desync implies (5).
- Add a build flag to mirror ExROM’s exact BG reg selection for CHR modes 1/2 and compare screenshots for (3).

## Minimal patch sketch (not code, intent only)

- Gate BG/OBJ bank split on 8×16: when OFF, use A for both.
- Remove retroactive `$5130` propagation; only apply on `$5120-$512B` writes.
- Mirror ExROM’s CHR mode 1/2 BG register usage.
- Add `ab_mode` and apply for non-pattern cycles and `$2007` CHR-RAM accesses.
- Implement PRG bit7 RAM/ROM selection for $8000–$FFFF.

These align our behavior with ExROM and are the most likely to resolve missing/misplaced objects.
