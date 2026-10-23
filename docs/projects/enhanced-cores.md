# Stock and enhanced cores: one roster for every console

The user's request (2026-10-22): improve and unify the roster of alternate chips. The Game Boy picture chip had three variants
(DMG, DMGX big screen, DMGS layers), the NES picture chip's layers (FIXS) were made for the SNES and not adapted to the Master System and the
Genesis, and the "invert colours" toggle of the Game Boy look drew one of the variants inverted. The aim: every console's most accurate core in
two variants per slot, **stock** (the chip as itself; on another console it shows what that chip natively can) and **enhanced** (the
cross-console help: the guest's layers, a picture the guest's size, more channel banks), the enhancement adapting to the guest console's spec
through one generic contract that later SFC / SMS / MD enhanced chips can implement too. The NES gimmick cores stay, after the best cores.

## 1. The roster

Every PPU and APU menu (and, for the order, every CPU menu) is three sections (`CoreSection` in `Windows/NesEmulator/systems/ConsoleSystems.cs`):

1. **The console's own best cores**: stock, then enhanced.
2. **The other consoles' best cores**: families in the order NES, SNES, Game Boy, Master System, Genesis; stock before enhanced in each.
3. **The rest**, in the order it always had: the NES gimmick cores (CUBE, LQ, HI2X, WF, ...), the SNES's silent HLE unit, the SM83 clock variants.

The labels of the best cores begin with their grade: `FIX (stock)`, `FIX (enhanced)`, `Game Boy (stock)`, `Game Boy (enhanced)`, `SNES (stock)`,
`Master System (stock)`, `Master System FM (stock)` (the Japanese console's YM2413), `Genesis (stock)`; what follows the dash is what the entry does.
The desktop draws a heading per section (`CoreCatalog.SectionTitle`), Lite an option group per section. **Ids did not change** (saved settings,
NES save states, stockpiles, FL projects and the HTTP API keep working), with one retirement:

| Grade | NES board id | On another console | What it is |
|---|---|---|---|
| FIX stock | `FIX` | `NES:FIX` | PPU_FIX / APU_FIX, the accuracy target |
| FIX enhanced | `FIXS` | `NES:FIXS` | PPU_FIXS / APU_FIXS: the cross-console layers, extra channel banks |
| Game Boy stock | `DMG` | `NES:DMG` | the stock Game Boy picture chip (PPU_GB) showing the middle 160x144; APU_DMG on APU_GB |
| Game Boy enhanced | `DMGS` | `NES:DMGS` | the enhanced Game Boy picture chip (PPU_GBE): the whole picture and the layers; APU_DMGS on APU_GBS |
| (retired) | `DMGX` | `NES:DMGX` | the big screen without the layers: **moved to DMGS** wherever it was stored |
| Game Boy enhanced sound on the Game Boy | - | `GBS` (Game Boy menu) | APU_GBS |

The SNES (SFC), Master System / Game Gear (SMS, GG) and Genesis (MD) have stock chips only; their enhanced chips are the next phase (section 5).
The Game Boy console's own picture menu has no enhanced entry: on its own console the enhanced chip would draw exactly as the stock one.

### The Game Boy picture chips

`gb/PPU_GB.cs` is the stock chip. `gb/PPU_GBE.cs` is the one enhanced chip: it replaced `PPU_GBX` (a bigger screen) and `PPU_GBXS` (the bigger
screen and the SNES-support layers), so the Game Boy has a stock and an enhanced chip as the NES has FIX and FIX enhanced. On the NES board,
`mix/PPU_DMG.cs` is the bridge to the stock chip (id DMG) and `PPU_DMGS` the bridge to the enhanced chip (id DMGS); `mix/GbScreen.cs` chooses.

### Migrations (never a silent reset to the default)

`CoreIdMigration` (systems/ConsoleSystems.cs) and `CoreRegistry.MigratePpuId` (board/CoreRegistry.cs, for the board code the old Web build
compiles without the systems folder): `DMGX`, `DMGXI`, `DMGSI` -> `DMGS`, `NES:DMGX` (and the `NES:` forms of the others) -> `NES:DMGS`; the
retired inverted-sprite chips also switch the Game Boy look's sprite inversion on. Where a pick can be stored, it goes through them:

| Store | Where the migration runs |
|---|---|
| desktop `config.json` (`selectedPpuCore`, `consoleCores`, the other slots too) | `MainForm.ApplyGbLook` at start-up, through `CoreIdMigration.Migrate`, the file written back |
| Lite IndexedDB (`pref_core_{key}_{slot}`, `pref_ppuCore`) | `Emulator.LoadConsolePrefsAsync` and the `pref_ppuCore` read in `Emulator.OnAfterRenderAsync`, written back |
| a NES save state's core type name (`PPU_DMGX`) | `Bus.SetPpuCoreById` (also every other way an id reaches the board) |
| a stockpile's machine (`NES:DMGX`) | `CoreCatalog.Resolve` (`AnchorStart.Fresh`) |
| the HTTP API (`POST /api/console/core/ppu/DMGX`) | `CoreCatalog.Find` |
| test scripts (`--console-run --ppu NES:DMGX`, the mix lab) | `CoreCatalog.Resolve`, `CoreRegistry.MigratePpuId` |
| the FL plugin's chip indexes | nothing to migrate: no APU id was renamed or removed (the indexes are built from the NES board's APU ids by family) |

Found on the way: the desktop put any NES core the BrokenNes 1 campaign save did not own back to FIX at every ROM load, even with the legacy
locks off (`EnsureUnlockedProgressionSelections`), which made a pick of FIX enhanced or a Game Boy chip vanish on a machine whose save lacked
them. With the locks off a pick is now only checked for existing (`MainForm.Progression.cs`).

## 2. The inversion bug and the Game Boy look

**Cause.** The flat picture translators (SNES -> NES, Master System / Game Gear -> NES, Genesis -> NES) store each colour as a brightness level
1-3 that runs dark to bright, as NES colour indices (`SnesToNes` `Level`, `SegaQuantizer` palettes sorted darkest first). A NES chip draws them by
the palette, so the picture is right on FIX. But the stock Game Boy bridge (`PPU_DMG`, and the retired `PPU_DMGX`) drew a NES picture with
`BGP = OBP = E4`: colour index n became Game Boy shade n, and shade 0 is the lightest. Index 3, the brightest level, became the darkest shade:
the SNES, Master System, Game Gear and Genesis pictures were drawn inverted on `NES:DMG` and `NES:DMGX` by default (the "inverted" toggle made
them upright). The layered chip (`DMGS`) mapped by real brightness and was upright, except that its backdrop never followed the background
inversion. NES games were drawn by palette index (upright by luck where a game's index order happened to follow brightness).

**Fix (at the root).** Every Game Boy picture path maps a colour by its own brightness (`gb/GbColour.cs`, `ShadeOf`: luma 192 and up shade 0 ..
below 64 shade 3). The stock bridge (`PPU_DMG`, DMG mode) recodes every NES tile's colours each frame: the background palette (BGP) holds the
backdrop's shade (colour 0, which keeps its priority role: behind-the-background sprites show through it) and the three shades the picture's
background pixels use most; the two sprite palettes (OBP0, OBP1) are the pair of 3-shade sets that draw the sprite pixels with the least error,
each NES sprite palette on the better one. The enhanced chip's layers map by brightness as before.

**The toggles now** (`GbLook`, the same on every path): *Invert background* mirrors the lightness of the background - the layers **and the
backdrop** (decision: the backdrop is part of the background) - and *Invert sprites* that of the sprites. A DMG shade s becomes 3 - s; a Game
Boy Color colour has its HSL lightness mirrored with its hue and saturation kept (`GbColour.FlipLightness`: each channel x + 255 - (max + min);
dark blue becomes light blue), not a colour negative (which turned dark blue yellow). The bezel around the stock chip's window and the black
outside a picture (the Game Gear's bezel, the SNES's clip to black) never flip. Lite shows the look and the toggles whenever a Game Boy picture
chip draws, the Sega consoles included (`CoreCatalog.IsGameBoyPicture`).

## 3. Tests

- `Workshop --enhanced-test` (`Workshop/EnhancedCores/EnhancedCoresTestCli.cs`): `menu` (every console's menus in the three sections, the
  exact heads, the labels, the defaults, the ids, four negative controls on the order checker), `migrate` (the settings-store function, Resolve
  and Find, the NES board, a NES save state, a stockpile written, read and started; negative controls), `invert` (upright default and both
  toggles on every path: a NES game on Game Boy stock and enhanced, a SNES game, a Master System, Game Gear and Genesis game through the NES on
  Game Boy stock and enhanced, a Game Boy game on NES:FIX, NES:FIXS and the SNES chip, Game Boy Color colours; negative controls: the old
  index-as-shade mapping (`PPU_DMG.IndexShadesForTests`) and the old backdrop (`PPU_GBE.BackdropIgnoresInversionForTests`) are caught, the old
  CGB negative changes the hue and is caught).
- `UAT/entrypoints/core-roster-desktop.ps1`: the real desktop app with throw-away config folders: an older config.json's DMGX picks moved and
  written back, the negative control (an unknown id falls back to FIX), the menus the API serves, an old id through the API.
- `UAT/lite/test-lite-sega.mjs` group `roster`: Lite with an older build's `NES:DMGX` / `DMGX` in IndexedDB, the look shown for a Genesis game
  on a Game Boy chip (and not on its own chip); `pickers` checks the sections and the labels.
- `Workshop --enhanced-shots --out <dir> --label before|after`: pictures of real games on the Game Boy chips (default and inverted) and a
  Genesis game on FIX enhanced.

## 4. The enhanced contract

(phase 2: written with the contract.)

## 5. For the next phase: SFC, SMS and MD enhanced chips

(phase 2.)
