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
| FIX enhanced | `FIXS` | `NES:FIXS` | PPU_FIXS / APU_FIXS: the guest's picture at its own size with all its layers, as many channel banks as its voices need |
| Game Boy stock | `DMG` | `NES:DMG` | the stock Game Boy picture chip (PPU_GB) showing the middle 160x144; APU_DMG on APU_GB |
| Game Boy enhanced | `DMGS` | `NES:DMGS` | the enhanced Game Boy picture chip (PPU_GBE): the guest's picture at its own size with all its layers; APU_DMGS on APU_GBS's banks |
| (retired) | `DMGX` | `NES:DMGX` | the big screen without the layers: **moved to DMGS** wherever it was stored |
| Game Boy enhanced sound on the Game Boy | - | `GBS` (Game Boy menu) | APU_GBS |

The SNES (SFC), Master System / Game Gear (SMS, GG) and Genesis (MD) have stock chips only; their enhanced chips are the next phase (section 5).
The Game Boy console's own picture menu has no enhanced entry: on its own console the enhanced chip would draw exactly as the stock one.

### The Game Boy picture chips

`gb/PPU_GB.cs` is the stock chip. `gb/PPU_GBE.cs` is the one enhanced chip: it replaced `PPU_GBX` (a bigger screen) and `PPU_GBXS` (the bigger
screen and the SNES-support layers), so the Game Boy has a stock and an enhanced chip as the NES has FIX and FIX enhanced. On the NES board,
`mix/PPU_DMG.cs` holds both bridges: `PPU_DMG` (id DMG) runs a NES picture through the real stock pipeline, `PPU_DMGS` (id DMGS) is the enhanced
chip's front: the guest's layers composed by PPU_GBE in the Game Boy's colour rules. PPU_GBE is the stock chip's colour rules plus the layer
compositor, not a third copy of the dot pipeline (a guest's picture only ever reaches it as layers). `mix/GbScreen.cs` (the phase 1 chooser) is gone.

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
- `Workshop --enhanced-test` groups `layers` and `sound` (`Workshop/EnhancedCores/EnhancedContractTests.cs`): the two contracts of section 4,
  each on both enhanced chips, each with its stock negative control (below).
- `Workshop --picture-bridge-test` (group `alloc`): the Sega layered paths allocate 0 bytes a frame on both enhanced chips (FIXS and DMGS) and on
  the stock FIX; the scenes check guest-size placement on FIXS (`SegaScenes.Placement`: one scene's coordinates name the same pixel on both paths),
  the whole 320 x 240 of an H40 V30 Genesis picture on FIXS against the cropped one on FIX.
- `Workshop --enhanced-shots --out <dir> --label before|after`: pictures of real games on the Game Boy chips (default and inverted) and a
  Genesis game on FIX enhanced.
- `UAT/lite/test-lite-sega.mjs` group `swap` and `UAT/entrypoints/sega-preview-smoke.ps1`: a Master System game swapped onto FIXS is 256x192, a
  Genesis game 320x224 shown 320 wide; on the stock FIX the NES's 256x240.

## 4. The enhanced contract

One contract for the picture and one for the sound. A translator describes the guest's frame (its layers, its voices) once, in a form that
does not depend on the chip that draws or plays it; an enhanced chip of any console takes that description and renders it in its own rules.
The guest decides the size and the number of layers and voices; the chip decides the colours and the channel sound.

### 4.1 The picture: one layered picture, at the guest's size

**The description** is `ExtPicture` (`Windows/NesEmulator/ppus/ExtPicture.cs`): this frame's picture as layers at `Width` x `Height` (set by
`BeginFrame(width, height)`, at most 320 x 240), up to 8 tile layers (`Layer`: a map of up to 128 x 128 cells of 8x8 tiles at 2 bits a pixel,
attributes (palette, priority), per line: on, scroll, the two ladder positions of its low and high tiles, a hidden mask; column scroll for the
Genesis), a Mode 7 layer, sprite pieces (each with its first and last line, a per-line limit and what was dropped), a backdrop colour per line
(and a second one where colour math is blocked), 8 background and 8 sprite palettes of 3 colours (RGB), a black (clipped) mask, and the guest's
own name for what it shows (`Guest`, `GuestMode`, `Describe()` = "SNES mode 1, 256 x 224, 3 layers"). `ComposeLine` resolves one line into a ladder
key per pixel; the chip turns a key into its colour.

**The translators** fill it, each from its own console's video state, at the console's own size:

| Guest | Translator (layered path) | Size | Layers |
|---|---|---|---|
| SNES | `SnesToNes.RenderLayered` | 256 x 224, 256 x 239 with overscan | per line from the captured registers: mode 0 four BGs, mode 1 three, modes 2-6 two (or one), Mode 7 the Mode 7 layer; a split screen one layer per part (8 at most) |
| Genesis | `MdToNes.RenderLayered` | 320 (H40) or 256 (H32) x 224 (V28) or 240 (V30) | plane B, plane A, the window |
| Master System | `SmsToNes.RenderLayered` | 256 x 192, 224 or 240 | the background, the vertical-lock columns |
| Game Gear | `SmsToNes.RenderLayered` | 160 x 144 (the LCD) | the same |
| Game Boy (on FIX enhanced) | `GbToNes.RenderLayers` | 160 x 144 | the background, the window (its own line counter) |
| NES (on Game Boy enhanced) | `NesToLayers.Fill` | 256 x 240 | the background (both nametables, so the right edge is the next nametable's) |

**The chip side** is `IEnhancedPictureChip` (same file): `Layers` (the ExtPicture it owns), `DrawLayers()` (compose it into `Picture`),
`Picture` / `PictureWidth` / `PictureHeight` (0xFFRRGGBB at the guest's size), `EnhancedName`, `ColourLimit` (what its colours cannot show).
Implemented by:

- `PPU_FIXS : PPU_FIX` (FIX enhanced): a subclass, not a fork: a NES game plays on PPU_FIX's own pipeline, untouched (golden-hash neutral);
  `DrawLayers` maps every colour to the nearest of the 2C02's colours (`NesPalette.Nearest`, drawn as `NesFixPalette.Rgb`).
- `PPU_DMGS : PPU_DMG` (Game Boy enhanced) with `PPU_GBE` behind it: DMG shades by brightness (or the Game Boy Color's 5-bit colours), the
  Game Boy look's inversions (section 2). A NES game reaches it through `NesToLayers` (the NES chip in front, MixConfig.NesFrontPpu, keeps the
  game's PPU timing).

A translator finds the chip through `NesPictureSink.Enhanced` (the board's PPU when it is an `IEnhancedPictureChip`): enhanced -> the layered
path at the guest's size; anything else -> the flat path, a NES picture of 256 x 240 drawn by the NES chip (the Genesis's 320 columns cropped
to the middle 256 and its 240 lines to 224, counted). The result goes to the session as a `BridgeFrame` (`mix/NesPictureSink.cs`: ARGB, size,
`GuestSize`), and the sessions (`SnesSession`, `GbSession`, `SegaPictureSession`) publish its size: `FrameWidth` x `FrameHeight` is the guest's,
`DisplayWidth` keeps the console's shape (a Genesis 320 for both its widths, as its own chip shows it). The apps (desktop, Lite, the plugin's ROM
mode) already follow a session's size changing, as the Genesis and the Master System change it themselves.

**Cost.** The Sega layered paths and both enhanced chips allocate nothing a frame (the `alloc` group: 0 bytes on FIXS, DMGS and FIX). The
enhanced Game Boy chip no longer runs a Game Boy dot pipeline over a big screen: it composes the layers once.

### 4.2 The sound: one voice list, as many banks as the voices need

**The description** is the hub's voice list (`Windows/NesEmulator/sega/SegaHub.cs`): `HubVoice` (kind: pulse, triangle or noise; pitch;
level; duty; short noise; and now `Pan`, -1 left only .. +1 right only, 0 both or not known) from `IHubVoiceSource.DescribeVoices`:

| Guest | Source | Voices | Pan from |
|---|---|---|---|
| SNES | `NesApuOnSnes.DescribeVoices` | 8 (the DSP voices: a looped sample is a pulse at its pitch, a noise voice or a one-shot a noise) | VOL L / VOL R |
| Genesis | `MdSoundUnit.DescribeVoices` (`Ym2612`, `Sn76489`) | 11 (FM 1-6, the DAC, PSG tones 1-3, the PSG noise) | the YM2612's L/R bits |
| Master System FM | `SmsFmSound.DescribeVoices` | 18 (the PSG's 4, the YM2413's 9 melodic channels, 5 rhythm) | - |
| Master System / Game Gear PSG | `Sn76489.DescribeVoices` | 4 | the Game Gear's stereo port |
| Game Boy | `GbApuOnNes.DescribeVoices` | 4 | NR51 |

The NES hub is mono: the pan is the guest's own word for tools, the stems and an enhanced chip with stereo of its own to come.

**The chip side** is `IApuExtChannels` (`Windows/NesEmulator/apus/APU_FIXS.cs`): `ExtBanks` (the extra banks on offer: 1 when the chip is
made), `MaxExtBanks`, `EnsureExtBanks(n)` (grow to at least n, never shrink), `WriteExtRegister(bank, address, value)` (a register of an extra
bank, addressed as the chip's own). A bank costs nothing until it is written. Implemented by `APU_FIXS` (each bank another APU_FIX on an idle
host, 15 extra at most), `APU_GBS` (each bank another APU_GB) and `APU_DMGS` (each bank a lane of NES channels replayed on one of APU_GBS's
banks). The game-facing chip (bank 0) is the stock chip untouched: a NES or Game Boy game sounds bit-identical on the enhanced chip.

**The bridges** ask for what their voices need: `NesVoiceWriter(port, wantBanks)` with `NesVoiceWriter.BanksFor(pulses, triangles, noises)` /
`BanksForTonal(tonal, noises)` (a pulse for every tonal voice: a pulse has a volume, a triangle has not), through `INesRegisterPort.EnsureBanks`:

| Guest | Banks on FIXS / DMGS | On the stock FIX / DMG |
|---|---|---|
| Genesis (`MdSoundOnNes`) | 5 (9 tonal voices on 10 pulses, 2 noises) | 1: three tonal voices and a noise, the rest dropped and counted |
| Master System FM (`SmsFmSoundOnNes`) | 7 (14 tonal, 4 noises) | 1 |
| Master System / Game Gear PSG (`SmsSoundOnNes`) | 2 (the voices fit one bank; the chip keeps the two it has, as before) | 1 |
| SNES (`NesApuOnSnes`, its own mapping) | 2 to start, grown at each sync to what the game sounds at once (a pulse per tonal voice, a noise channel per noise), up to 8 | 1: the loudest three tonal voices and one noise |
| Game Boy (`GbApuOnNes`) | 1 (its 4 voices fit) | 1 |

The banks the chip had when the bridge was made are opened at once (as before: the Master System PSG on FIXS is unchanged to the sample); the
ones it grew by open when a voice first plays on them, so a quiet game costs what it did. Measured (a silent Genesis program, frames a second on
this machine, shared with other work): MD 652, NES:FIX 527, NES:FIXS 483, NES:DMGS 494. With every bank open (all eleven Genesis voices
sounding) each extra APU_FIX costs about what bank 0 does.

**What stays mono and lossy** on every NES-hub chip: the FM timbre (a pulse), the DAC's samples (noise hits), the pan, the SNES's samples and
echo. Those are the guest chip's own sound; an enhanced chip of that console (section 5) is where they can be kept.

## 5. For the next phase: SFC, SMS and MD enhanced chips

The SNES (SFC), Master System / Game Gear (SMS, GG) and Genesis (MD) have stock chips only. An enhanced chip of one of them attaches to the
other consoles through the same two contracts; nothing in the translators has to learn about it.

**A picture chip** (say `PPU_SFCS`, "SNES (enhanced)"):

1. Make it a subclass of the stock chip (as `PPU_FIXS : PPU_FIX`), so the console's own games draw on the stock pipeline and its golden hashes
   do not move.
2. Implement `IEnhancedPictureChip`: own an `ExtPicture`; in `DrawLayers` call `Layers.ComposeLine(line, 0, Width, z, key, math)` per line and
   turn each key into the chip's colour (`Layers.Rgb(key, line)`, `Layers.Blend` for colour math) in its own rules - the SNES's 15-bit colour
   (every guest colour nearly exact), the Master System's 6-bit or the Game Gear's 12-bit, the Genesis's 9-bit (and its shadow / highlight
   where `math` says); `ColourLimit` says what is lost. Copy `PPU_FIXS.DrawLayers` (about 40 lines) as the model.
3. Let the translators reach it. Today every translator reaches an enhanced chip through `NesPictureSink.Enhanced`, which is the NES board's
   PPU. The layered paths never touch the NES: give the translators a second constructor that takes an `IEnhancedPictureChip` directly (or a
   sink interface with `Enhanced` and, for the flat path, `Present`), and the Genesis on the SNES chip is `new MdToNes(board, chip)`.
   `GbToSnes` (the Game Boy on the SNES chip, flat today) becomes `GbToNes.RenderLayers` onto the SNES chip the same way.
4. The catalog: the chip's id on its own console (`OwnBest` with an `Enhanced` entry: the menus, the labels and `CoreOption.Grade` follow), on
   the others (`CrossBest`, e.g. `SNES:SFCS`), the session that builds the bridge (`SegaPictureSession.SetBridge`, `GbSession.SetPicture`,
   `SnesSession`), and a root in `WebLite/LinkerConfig.xml`.

**A sound chip** (say `APU_SFCS`, the S-DSP with more voices; `SMSS`, more SN76489 sets; `MDS`, more YM2612s):

1. Implement `IApuExtChannels` on it: bank 0 the game's chip untouched, banks 1.. more of the same voices, made when first written.
2. Write the voice writer for its registers (`HubVoice` -> that chip's registers), with the same bank arithmetic as `NesVoiceWriter.BanksFor`
   (a tonal voice per tonal channel, a noise per noise channel): the S-DSP can play a guest voice as a looped sample at its pitch, a YM2612 as an
   FM voice with a chosen patch, an SN76489 as a square. The guest's `Pan` then has somewhere to go.
3. The bridges that today replay the hub's voices on the NES (`MdSoundOnNes`, `SmsFmSoundOnNes`, `SmsSoundOnNes`, `NesApuOnSnes`) take that
   writer instead of `NesVoiceWriter` when the chip is not a NES one; their voice lists and their counters are already the chip-independent half.

**Tests to copy:** `--enhanced-test` groups `layers` and `sound` run every guest on both enhanced chips with a stock negative control; add the
new chip to `EnhancedChips` / the bank tables there, and to the `alloc` group of `--picture-bridge-test`.
