# Stock and enhanced cores: one roster for every console

The user's request (2026-10-22): improve and unify the roster of alternate chips. The Game Boy picture chip had three variants
(DMG, DMGX big screen, DMGS layers), the NES picture chip's layers (FIXS) were made for the SNES and not adapted to the Master System and the
Genesis, and the "invert colours" toggle of the Game Boy look drew one of the variants inverted. The aim: every console's most accurate core in
two variants per slot, **stock** (the chip as itself; on another console it shows what that chip natively can) and **enhanced** (the
cross-console help: the guest's layers, a picture the guest's size, more channel banks), the enhancement adapting to the guest console's spec
through one generic contract that every console's enhanced chips implement: the NES's and the Game Boy's (phases 1 and 2), the SNES's, the Master
System's / Game Gear's and the Genesis's (phase 3, section 5). The NES gimmick cores stay, after the best cores.

## 1. The roster

Every PPU and APU menu (and, for the order, every CPU menu) is three sections (`CoreSection` in `Windows/NesEmulator/systems/ConsoleSystems.cs`):

1. **The console's own best cores**: stock, then enhanced.
2. **The other consoles' best cores**: families in the order NES, SNES, Game Boy, Master System, Genesis, WideMess (section 6; only where the WideMess console is offered); stock before enhanced in each.
3. **The rest**, in the order it always had: the NES gimmick cores (CUBE, LQ, HI2X, WF, ...), the SNES's silent HLE unit, the SM83 clock variants.

The labels of the best cores begin with their grade: `FIX (stock)`, `FIX (enhanced)`, `Game Boy (stock)`, `Game Boy (enhanced)`, `SNES (stock)`,
`Master System (stock)`, `Master System FM (stock)` (the Japanese console's YM2413), `Genesis (stock)`, and since phase 3 `SNES (enhanced)`,
`Master System (enhanced)`, `Game Gear (enhanced)`, `Genesis (enhanced)`; what follows the dash is what the entry does.
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
| SNES stock | `SNES` | `NES:SNES` (`SNES:SFC` from the Game Boy) | the S-PPU / S-DSP through the NES (PPU_SNES, APU_SNES) |
| SNES enhanced | `SNESS` | `SNES:SFCS` (no NES in between) | PPU_SFCS / SfcVoiceChip (S-DSP banks); `SFCS` on the SNES's own menu (PPU_SFCS, APU_SFCS) |
| Master System stock | `SMS`, `SMSFM` | `NES:SMS`, `NES:SMSFM` | the VDP / the PSG (+ YM2413) through the NES |
| Master System enhanced | `SMSS` | `SMS:SMSS` | SmsVdpS (6-bit colour) / PsgVoiceChip (SN76489 sets, mono); `SMSS` on the Master System's own menu |
| Game Gear enhanced | - | `GG:GGS` | SmsVdpS (12-bit colour) / PsgVoiceChip (SN76489 sets on the stereo port); `GGS` on the Game Gear's own menu |
| Genesis stock | `MD` | `NES:MD` | the VDP / YM2612 + PSG through the NES |
| Genesis enhanced | `MDS` | `MD:MDS` | MdVdpS (9-bit colour, shadow / highlight) / MdVoiceChip (YM2612 + PSG sets); `MDS` on the Genesis's own menu |
| WideMess enhanced (section 6) | `WMSS` | `WMS:WMSS` | WmsWideChip (the widescreen chip: 400 wide, 15-bit colour) / WmsVoiceChip (32 WideMess voices a bank, 16-bit PCM first); no entry on the WideMess's own menu (its WMS chips are the WideMess); offered only where `Consoles.WideMessEnabled` |

Every console's own menu is its stock chips then its enhanced one (the Master System: `SMS`, `SMS-FM`, `SMSS`). On its own console an enhanced chip draws and
sounds exactly as the stock one (it is a subclass of it, and nothing fills its extra layers or banks there: `--enhanced-test --only own3` proves it frame by
frame and sample by sample), so those entries are there for the menu's symmetry and the state units, not a different picture. The Game Boy console's own
picture menu still has no enhanced entry (phase 2's decision: the Game Boy's own game would draw exactly as on the stock chip). The other consoles reach the
SNES, Master System / Game Gear and Genesis enhanced chips **directly** (the routes `SNES:SFCS`, `SMS:SMSS`, `GG:GGS`, `MD:MDS`: no NES in between, section 5);
only the NES menu offers the NES board's ids of them (`SNESS`, `SMSS`, `MDS`: a NES game on them), and no other menu offers `NES:SNESS`, `NES:SMSS` or `NES:MDS`.

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
- Phase 3 (`Workshop/EnhancedCores/EnhancedPhase3Tests.cs`, every check on every one of the other consoles' enhanced chips that exists, each with the stock
  chip of the same console as its negative control): `--enhanced-test --only layers3` (every guest at its size and layer count, every pixel in the chip's
  colours, the colour math the chip's own way, a NES scene on the NES board matched pixel for pixel against the NES's own chip), `sound3` (a channel for
  every voice, the banks, the pitch, the pan, drops past the last bank; every guest's bridge; Sonic 1), `states3`, `own3` (a console's own game on its
  enhanced chips equal to its stock ones, every frame and every sample), `speed3` (information). `--picture-bridge-test --only alloc` covers the new routes and
  the NES board's new chips; `--enhanced-shots --set phase3` makes the pictures (section 5.6).

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
- Phase 3 (section 5): `PPU_SFCS : PPU_SFC` (SNES enhanced, 15-bit colour), `MdVdpS : MdVdp` (Genesis enhanced, 9-bit colour, shadow / highlight),
  `SmsVdpS : SmsVdp` (Master System enhanced, 6-bit colour; Game Gear enhanced, 12-bit), all drawing through `LayerPainter` (ppus/LayerPainter.cs: the
  compose-and-colour loop every chip of another console shares; a chip gives its colour rules).

A translator finds the chip through its sink (`IPictureSink`, mix/NesPictureSink.cs): `NesPictureSink` (a NES board's PPU: FIX enhanced and Game Boy
enhanced take the layers, the stock chips the flat NES picture) or `ChipPictureSink` (another console's enhanced chip directly, phase 3). On a NES board
`NesPictureSink.Enhanced` is the board's PPU when it is an `IEnhancedPictureChip`: enhanced -> the layered
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
echo. Those are the guest chip's own sound. The enhanced chips of the other consoles (phase 3, section 5) take the same voice list without the NES in
between (`IEnhancedSoundChip`): the pan where they have stereo, the duty of a pulse on the S-DSP, a guest's PSG square on the Genesis's own PSG.

## 5. Phase 3: the SNES, Master System / Game Gear and Genesis enhanced chips

Phase 3 (2026-10-23) gave the three other consoles' most accurate chips their enhanced variants, on the two contracts of section 4. Each enhanced picture
chip is a **subclass of the stock chip** (its console's own game draws on the stock pipeline, untouched), each enhanced sound chip carries **extra banks of
the console's own channels**, and every other console reaches them **directly**: a guest's translator fills the chip's layers and its voices play on the
chip's banks, with no NES in between.

### 5.1 The shared step: a route that is not the NES board

- **Pictures**: `IPictureSink` (mix/NesPictureSink.cs) is where a translator sends its picture: `NesPictureSink` (a NES board's PPU, exactly as before) or
  `ChipPictureSink` (an `IEnhancedPictureChip` of another console, which has no flat path and refuses a NES picture). `SnesToNes`, `SmsToNes`, `MdToNes`
  and `GbToNes` gained a constructor that takes the chip; nothing else in them changed. `GbToSnes` (the flat Super Game Boy route, `SNES:SFC`) stays the
  SNES's stock entry for a Game Boy game; the enhanced one is `GbToNes.RenderLayers` onto PPU_SFCS (`SNES:SFCS`).
- **Sound**: `IEnhancedSoundChip` (mix/EnhancedSound.cs) is the voice side of an enhanced sound chip: `Apply(voices)` (the hub's `HubVoice` list, about 960
  times a second), `RunTo(seconds)`, stereo `ReadSamples` at the rate the chip was made for, `BanksFor(inventory)` (the bank arithmetic), the counters,
  and `IApuExtChannels` (the raw registers of each bank). `HubVoiceChip` is the shared half: channels come in kinds (the S-DSP has one kind, eight a bank;
  the PSG two, three squares and a noise; the Genesis three, six FM channels, three PSG squares and a noise); a voice keeps its channel while it sounds and
  its kind does not change, a newcomer (loudest first) takes the lowest free channel of its kind, a bank is added only when no channel of that kind is
  free, and only past the last bank (`MaxBanks`) is a voice dropped, and counted. A bank costs nothing until a voice plays on it. Allocation-free.
- **The guests' bridges** (mix/EnhancedSoundBridges.cs) keep the guest's real sound unit in front (everything the game can see) and hand its voice list to
  the chip: `MdSoundOnChip` (Genesis: the 11 voices, FM levels from the carriers' total levels and envelopes, the PSG and the DAC on the same scale),
  `SmsSoundOnChip` (Master System / Game Gear PSG: 4 voices, the Game Gear's stereo port as the pan), `GbApuOnChip` (Game Boy: 4 voices, NR51's pan),
  `SnesApuOnChip` (SNES: the 8 DSP voices, VOL L / VOL R the pan; `SnesVoiceReader` is the description NesApuOnSnes gives) and `NesApuOnChip` (a NES
  game on the NES board: its 4 channels; the DMC is dropped and counted). Levels are on one scale per guest (1 = its loudest single voice), so the guest's
  balance is kept. `HubVoice` gained `Psg` (an SN76489 tone: a 50% square whatever its kind says; the NES hub ignores it).
- **The catalog** (systems/ConsoleSystems.cs): `DirectRoutes` lists the routes; `EnhancedChips` (mix/EnhancedSound.cs) builds a route's chips; a route, the
  console's own enhanced entry and the NES board's ids appear once the chips exist (`CoreCatalog.IsDirectRoute`). The NES board's ids of the other
  consoles' enhanced chips are offered on the NES menu only (`NesBoardEnhancedOfOthers`). **The sessions**: `SnesSession` (picture and sound, its
  hot swap of the sound unit, its save state: unit byte 3 for a direct route), `GbSession` (picture, sound; no save states with another console's sound
  chip, as before for the NES ones, said in its reason), `SegaPictureSession` (picture; the console's own enhanced id is a pass-through), `MdSession` /
  `SmsSession` (their own enhanced picture id), `SmsSoundFactories` / `GenesisSoundFactories` (a route is looked up before the colon split, so `SMS:...`
  never falls to the stock unit).
- **The NES board**: `NesLayeredPpu` (mix/NesLayeredPpu.cs, on `NesFrontedPpu`: FIX in front, each 8-line group's pattern tables, `NesToLayers`, then
  the chip draws) is a NES game on another console's enhanced picture chip; `NesApuOnChip` its sound. `NesPicture.Mask` is a tests-only PPUMASK override.
- The NES path did not change: the goldens of every console's own default cores are unchanged (5.5).

### 5.2 SNES enhanced (`SFCS`, `SNES:SFCS`, NES board `SNESS`)

| | Picture: `PPU_SFCS : PPU_SFC` (mix/SnesEnhanced.cs) | Sound: `SfcVoiceChip` (S-DSPs), `APU_SFCS : APU_SFC` on its own menu |
|---|---|---|
| Rules | the SNES's 15-bit colours (32,768: every channel rounded to 5 bits, shown as the S-PPU shows them at full brightness); colour math the SNES's way, in 15-bit (`PPU_SFC.Blend`: add / subtract, half) | banks of 8 S-DSP voices (each bank a `DSP_SFC` on its own 64 KB of sound RAM), every voice a looped BRR sample of its own waveform: a pulse of its duty (12.5 / 25 / 50 / 75 %), a PSG tone a 50% square, a triangle, white noise, the NES's 93-step short noise; at its pitch (32-sample periods up to 4 kHz, 8-sample ones to 16 kHz, a noise one sample a shift); level and pan on VOL L / VOL R; no echo; 8 banks at most |
| NES game (`SNESS`) | the whole 256 x 240 (the stock S-PPU route shows 239 lines), the whole 2 x 2 nametable map, the NES's 8 sprites a line | 4 voices, pulses with their duty |
| Game Boy | 160 x 144, background + window (replaces the flat `GbToSnes` on the enhanced chip) | 4 voices, the pulses' duty, NR51's pan |
| Master System / Game Gear | 256 x 192 / 224 / 240, 160 x 144 | 4 voices (1 bank), the Game Gear's pan |
| Genesis | 320 or 256 x 224 or 240 (the SNES is 256 wide), plane B, plane A, the window | 11 voices on 2 banks |

### 5.3 Genesis enhanced (`MDS`, `MD:MDS`, NES board `MDS`)

| | Picture: `MdVdpS : MdVdp` (mix/GenesisEnhanced.cs) | Sound: `MdVoiceChip` (YM2612 + PSG sets), `MdSoundUnitS : MdSoundUnit` on its own menu |
|---|---|---|
| Rules | the Genesis's 9-bit colours (512) at the VDP's own DAC levels; where the guest's colour math asks, the Genesis's own: **shadow** where it subtracts, **highlight** where it adds (the DAC's lower / upper steps) | sets of a YM2612 and an SN76489 on the Genesis's NTSC clocks (6 FM channels, 3 squares, a noise each), 6 sets at most; a guest's PSG tone on the PSG (the same square), every other tonal voice an FM voice with the patch chosen for the Genesis bridges (`APU_MD.DefaultPatch`; a triangle the sine; the "PSG square" patch puts pulses on the PSG), its level through the carriers' total level, its pitch to the FM registers' cent; the noise on the PSG (white or periodic, the exact rate through tone 3 when free); the pan on the FM L/R bits (the Genesis PSG is mono) |
| NES game (`MDS`) | the whole 256 x 240, each layer's own left-column clip (the stock VDP route can blank both layers only), the NES's 8 sprites a line (the stock route shows 16) | 4 voices: pulses on FM, the triangle the sine, the noise on the PSG |
| SNES | 256 x 224 / 239 with **all its layers** (mode 0: four, more than plane A + plane B + the window), its colour math as shadow / highlight | 8 voices on 2 sets (6 FM + 2 on the next) |
| Game Boy | 160 x 144, background + window | 4 voices |
| Master System / Game Gear | 256 x 192 / 224 / 240, 160 x 144 | its 3 tones on the Genesis's own PSG (the very same squares), the noise on its noise: 1 set |

### 5.4 Master System / Game Gear enhanced (`SMSS` / `GGS`, `SMS:SMSS` / `GG:GGS`, NES board `SMSS`)

| | Picture: `SmsVdpS : SmsVdp` (mix/MasterSystemEnhanced.cs) | Sound: `PsgVoiceChip` (SN76489 sets), `SmsSoundS : SmsSound` on the own menus |
|---|---|---|
| Rules | the Master System's 6-bit colours (64: levels 0, 82, 173, 255) or the Game Gear's 12-bit (4,096), as the VDP's colour RAM shows them; the guest's colour math blended and shown in them; the guest's size beyond the Master System's 256 x 192 / 224 and the Game Gear's 160 x 144, every layer and sprite the guest has | sets of an SN76489 (3 squares, a noise), 8 at most; every tonal voice a square (a pulse's duty, a triangle's shape and an FM timbre are not carried), notes below the PSG's 109 Hz whole octaves up (counted); the noise at its exact rate through tone 3 when tone 3 is free; the Game Gear's **stereo port** carries the pan (left / right / both), the Master System is mono |
| NES game (`SMSS`) | the whole 256 x 240 (the stock route: the Master System's one 32-column map, so the right edge repeats the left column, counted) | 4 voices on 1 set |
| SNES | 256 x 224 / 239, all its layers | 8 voices on 3 sets |
| Game Boy | 160 x 144, background + window | 4 voices |
| Genesis | 320 x 224 / 240, plane B, plane A, the window | 11 voices on 3 sets (9 tones, 2 noises) |

### 5.5 Tests and numbers (2026-10-23)

- `--enhanced-test`: 329 checks, 0 failed (198 before phase 3); phase 3's groups: `layers3`, `sound3`, `states3`, `own3`, `speed3` (section 3), every check with
  its stock negative control: on the stock chip through the NES the guest is the NES's 256 x 240, its voices squeezed into one bank (3 tonal + 1 noise,
  the rest dropped and counted); the NES scene differs from the NES's own picture in 512 pixels on the stock SNES route, while every enhanced chip
  matches it in every pixel. `own3`: Super Mario World (SNES, 600 frames), Sonic 1 (Genesis), Sonic (Master System), Columns (Game Gear) on their own
  enhanced picture and sound chips: every picture and every sample equal to the stock chips'.
- Sound: a voice at 440 Hz plays at 439.9 Hz on the S-DSP banks, 440.1 on the Genesis's FM, 440.5 on the PSG sets (the PSG's period step); a NES pulse at
  440.4 Hz (its own timer's note) within 0.5 cents on all three; a voice panned left plays left only on the SNES, Genesis and Game Gear chips and on both
  sides alike on the Master System's (mono); Sonic 1 into Green Hill plays on 2 S-DSP banks, 3 PSG sets, 9 voices at once at most, none dropped (on the
  stock NES route: 1 bank, 20,915 voice-syncs without a channel).
- `--picture-bridge-test --only alloc`: 0 bytes a frame on every new route (the Master System, Game Gear and Genesis scenes on `SNES:SFCS`, `MD:MDS`,
  `SMS:SMSS`, `GG:GGS`) and for a NES scene on the NES board's `SNESS`, `MDS`, `SMSS`, as on FIXS, DMGS and FIX.
- Speed (`speed3`, frames a second of picture + sound together, over five runs, the machine shared with other work): a Genesis program on SNES enhanced
  403-485 (on the stock SNES chip through the NES 251-298, on its own chips 614-789); a Master System program on Genesis enhanced 428-511 (stock through
  the NES 215-272, own 1,048-1,221); a Genesis program on Master System enhanced 248-286 and on Game Gear enhanced 223-286 (stock through the NES
  238-305: the three PSG sets the eleven voices take cost about what the NES hop does).
- The other suites, on a build of the final commit: `--picture-bridge-test` 252 / 0, `--swap-matrix-test` 35 / 0 (every sound unit of every menu, the
  new ones included, gives the console's sample count and pitch), `--sega-bridge-test` 356 / 0, `--smstest` 430 / 0 (6 skips: no roster folder given),
  `--snes-state-test` 147 / 0, `--md-state-test` 168 / 0, `--corrupt-test` 275 / 0, `--cpu-wall-test` 27 / 0; the UAT suite 123 pass, 2 fail, 16 skip,
  3 info, as main (the two failures are `brokendev-smb1-nested-boot` / `-play`, on main too); `UAT/entrypoints/core-roster-desktop.ps1` all pass (the NES
  menu's new head, the labels, Sonic on the Master System and the Genesis on every enhanced chip at its own size); Lite (AOT) `lite-sega -Only
  console,rom,swap,video,pickers,roster` 78 / 0 (the pickers list and label the new chips; a Master System game swaps to `SNES:SFCS` and `MD:MDS` at
  256 x 192, a Genesis game to `SNES:SFCS`, `SMS:SMSS`, `GG:GGS` at 320 x 224, its sound to `SNES:SFCS`, `SMS:SMSS`, `MDS` in place);
  `UAT/sega-gate.ps1` 18 of 19 steps pass, as main: `--md-golden`'s Titan Overdrive 1 frame 1007 (`C740C2A7D83CE55F`) fails on main too, every other golden
  equal; `--sega-test` learned the direct routes (73 / 0, a negative control beside it); the FL plugin's `selftest --only consoles,cores,swap,hub-explicit,
  psg,md,fm,rom-sms,rom-md,stems-sega,runaway-sega` 10 pass, 1 warning (QLQ's octave, intended), 0 failed (`fm` 6.3x real time: its 5x floor needs a quiet
  machine).
- Goldens: unchanged (5.7).

### 5.6 Pictures

`Workshop --enhanced-shots --set phase3` writes, to `%LOCALAPPDATA%\VRUN_Nes_Dev_work\enhanced_cores\phase3\`, one frame of Super Mario Bros. (NES), Super
Mario World (SNES), Kirby's Dream Land (Game Boy), Sonic (Master System) and Sonic 1 (Genesis) on its own chip and on each other console's stock chip (as
the guests reached it before) and enhanced chip.

### 5.7 Golden hashes, the FL plugin, Lite

- Every console's own default cores are unchanged: the UAT suite and the Sega gate show the same results as main (5.5), and `own3` shows a console's own
  game on its enhanced chips equal to its stock chips.
- **The FL plugin's chip list** is built from the NES board's APU ids and its indexes are append-only. The three new NES-board APU ids (`SNESS`, `SMSS`,
  `MDS`) are kept **out** of it (`Plugin/BrokenNes2.Plugin/Bn2Consoles.cs`, `Cx.Hidden`, beside `MNES` and `WF`), so no saved chip index moves; offering
  them in the plugin would mean appending them to `Appended`. The plugin's self-test chip-index assertions are unchanged.
- **Lite**: the NES board's new classes are rooted in `WebLite/LinkerConfig.xml` (`PPU_SNESS`, `APU_SNESS`, `PPU_MDS`, `APU_MDS`, `PPU_SMSS`, `APU_SMSS`);
  the direct routes are plain code.
- **Save states**: a state is saved with the sound unit's id in its header (`apu`), as before: a game saved with another console's enhanced sound chip
  loads back into the same choice and is refused, with the reason, by a session with another unit; the SNES's payload names the bridge (unit 3) and
  rebases the chip, a listener like the NES ones; the Game Boy has no states with another console's sound chip (its reason says so). The consoles' own
  enhanced units keep the stock unit's state (their extra banks are not part of the machine). Stockpiles and settings migrate nothing: all ids are new.
- The stock chips' classes are no longer `sealed` (PPU_SFC, APU_SFC, MdVdp, MdSoundUnit, SmsVdp, SmsSound): the enhanced chips subclass them.

### 5.8 Risks and what is not carried

- The guest's picture reaches every enhanced chip as `ExtPicture`: 2-bit tiles (3 colours + clear) in 8 palettes a kind, so a SNES or Genesis game on the
  SNES, Master System or Genesis enhanced chip has its colours reduced to 3 a tile before the chip's own colours apply. A chip with more colours per tile
  (the SNES's 4 / 8 bpp) cannot get them back through this contract; a wider ExtPicture would be the next step.
- The Genesis guest's own shadow / highlight is still not described in `ExtPicture` (counted by MdToNes, as before), so the Genesis enhanced chip shades only
  what a SNES guest's colour math asks.
- Sound: FM timbre on the S-DSP and the PSG (a square at the FM voice's pitch), the DAC's samples (a noise hit), the SNES's samples and echo, the Game Boy's
  wave RAM (a triangle), a pulse's duty on the PSG and the Genesis FM are not carried; the noise's short mode is the NES's 93 steps on the S-DSP whatever the
  guest's (the SN76489's periodic noise is 16 steps). The Master System's FM unit (`SMS-FM`) is offered on the NES chips only, not on the enhanced chips.
- The Master System / Game Gear enhanced sound costs a little more than the NES hop for an eleven-voice guest (three PSG sets run at the PSG's clock).

## 6. WideMess milestone 7b: the widescreen chip and the WideMess sound chip on the other consoles' menus

WideMess (docs/projects/widemess-plan.md, docs/projects/widemess-spec.md section 8) joined the roster on 2026-10-24: its picture chip and its sound chip are the **WideMess (enhanced)** entry of the
picture and sound menus of the SNES, Game Boy, Game Boy Color, Master System, Game Gear and Genesis (the route `WMS:WMSS`) and of the NES (the NES board's ids `WMSS`), last of the other consoles'
section. They take the two contracts of section 4; the picture contract gained the **wide view** (6.1). Offered only where the WideMess console is (`Consoles.WideMessEnabled`: the desktop app and
Workshop; Lite and the FL plugin keep them hidden until they offer the WideMess, and the menu cache follows the switch). The WideMess console's own menu has no enhanced entry: its own WMS chips are the
WideMess, and a WideMess or SNES game draws and sounds on them exactly as it would on these. It offers the other consoles' chips instead, as the SNES console does (6.5).

### 6.1 The contract's additions: the wide view

| Field | Who sets it | What it says |
|---|---|---|
| `ExtPicture.ViewWidth` | the chip (0 for every chip but the widescreen chip's 400; `BeginFrame` keeps it) | how wide the chip shows the guest; `SideColumns` = (that - the guest's width) / 2 more columns each side |
| `Layer.RealWidth` | the translator, every frame | how many columns of distinct picture the layer's map really holds: a NES background 512 with vertical mirroring (or a mapper giving the quadrants different nametables), else 256; a SNES BG its map's width (256, 512, twice that with 16 x 16 tiles); a Genesis plane 256, 512 or 1024; a Master System, Game Gear or Game Boy map 256 |
| `Layer.FixedToScreen` | the translator | the layer is laid out on the screen, not scrolled over a map: the Genesis window plane, the Game Boy window, the Master System's vertical-lock columns |
| `ExtPicture.SpritesBeyondEdges` | the translator (reset by `BeginFrame`) | the guest's sprite positions beyond its edges are real places: a SNES's 9-bit X, the Genesis's 512-pixel sprite plane, the Game Gear's VDP line around its LCD, the part of a NES sprite past the right edge; not a Game Boy's (games hide unused sprites at X = 0) nor a Master System's (its sprites never leave its 256 columns) |

With `ViewWidth` set the translators also fill the map cells the sides show (`NesToLayers`, `SnesToNes`, `MdToNes`; the Game Boy's whole map and the Master System's whole rows were filled already) and
list the sprite pieces only the sides show (`SnesToNes`, `MdToNes`, `SmsToNes` for the Game Gear), **after the guest's own**: the side cells' tiles are quantized into the palettes the guest's picture
chose (`SegaQuantizer.Fit`, `SnesToNes.Quantizer.Fit`), so the guest's picture is exactly what it is without them, and a colour that is not on the screen is drawn with the nearest one and counted
(`MdToNes.SideColourLoss`). With `ViewWidth` 0 (every other chip) nothing changes: section 3's suites give the same output as before. One translator change for every chip: the Master System's
left-column mask (VDP register 0 bit 5) moved from `Layer.Hidden` to `Layer.BlankLeft`, the field made for exactly that (the same pixels hidden; it is the screen's clip, not a window, so a wider view
does not stretch it over a side).

### 6.2 The widescreen chip (`WmsWideChip`, mix/WideMessEnhanced.cs)

- **400 wide**, the guest's own height, square pixels, the WideMess's picture width (spec section 3). 72 columns a side for a 256-wide guest, 40 for the Genesis in H40, 120 for a Game Boy or a Game Gear.
- **The middle** is the guest's own picture (`ExtPicture.ComposeLine`), in the SNES enhanced chip's 15-bit colours and colour math: pixel for pixel what `PPU_SFCS` draws of the same frame.
- **The sides**: each layer, each frame, gets one rule (`WideRule`) from what its translator reported: **extend** when its real width is wider than the guest's picture (the map continues as the
  guest's scroll gives it, wrapping at the map's width; columns past the real width repeat and are counted as repeated), **repeat** when it is not (its own visible columns side by side),
  **fill** when it is laid out on the screen or did not report its width (nothing of it: what is behind it, or the backdrop; the unreported case is counted apart and the checks require it to be 0).
  Mode 7 continues its transformation (its plane is 1024 pixels). The Genesis's column scroll continues from its edge pairs.
- **Masks** (windows, the colour window, clip to black, the backdrop's colour math) reach a side from the guest's column next to it, as the WideMess's own windows reach its edges (spec 3.4).
- **Sprites** only where `SpritesBeyondEdges`: drawn in the sides in the guest's order, never counted against its per-line limit (a piece the guest drops on a line is dropped in the side too).
- **Counted** every frame (`Extended`, `Repeated`, `Filled`, `MaskedSideColumns`, `UnreportedLines`, `SpriteLinesNotExtended`, `SideSpritePixels`; `CountInto` adds them to a bridge's counters: the
  status line and `GET /api/console`). Allocation-free.

| Guest | Layers | Sprites in the sides |
|---|---|---|
| NES (board id `WMSS`) | background **extend** with vertical mirroring (the neighbouring nametable: 144 real columns a line), **repeat** with horizontal or one-screen mirroring | the part of a sprite past the right edge |
| SNES | each BG by its map: 64 tiles **extend**, 32 **repeat**; Mode 7 **extend** | yes: X 256-327 right of the picture, 328-511 left, X = 256 (games' hiding place) never |
| Game Boy / Color | background **extend** (256 of which 160 shown: 96 real columns, 144 repeated), window **fill** | no: they stop at the screen's edges |
| Master System | background **repeat** (its map is the screen's 256), vertical-lock columns **fill** | no (they never leave the screen) |
| Game Gear | background **extend** (the VDP's 256-pixel line around the LCD's 160: 96 real, 144 repeated) | yes: the VDP draws them around the LCD |
| Genesis | planes **extend** (512 or 1024; a 256-wide plane in H32 **repeats**), window **fill** | yes: the 512-pixel sprite plane |

**What no rule fixes** (as for SNES games, widemess-spec 6.1): the columns a game streams just past its edge show old picture until it scrolls there (Super Mario Bros.' left side at the start of
1-1 is empty sky); objects a game creates at its edge pop in there; a HUD drawn into the map shows the map's row continuing beside it (Super Mario Land's status bar); a colour only the sides show
is drawn in the screen's palettes (counted).

### 6.3 The sound chip (`WmsVoiceChip`)

Banks of 32 WideMess voices: each bank is a WideMess S-DSP (`DSP_SFC` with its voices 8-31 and 1 MB of sound RAM attached, unlocked), its first 24 channels voices 8-31 playing **16-bit PCM**
(pulses of the four duties, a smooth triangle, 16-bit white noise, the NES's 93-step short noise: spec section 4's sample directory), the next 8 the S-DSP's voices 0-7 playing the same shapes in BRR.
Every guest voice on a voice of its own (`HubVoiceChip`), its pitch on the 14-bit pitch, its level and pan on VOLL / VOLR; no echo. One bank holds every guest of the other consoles (18 voices at most,
a Japanese Master System's PSG + FM); 4 banks at most. The bridges are the existing ones (`MdSoundOnChip`, `SmsSoundOnChip`, `GbApuOnChip`, `SnesApuOnChip`, `NesApuOnChip` as `APU_WMSS`).
Allocation-free.

### 6.4 The NES console

`PPU_WMSS : NesLayeredPpu` draws a NES game 400 wide; its `GetFrameBuffer` is the NES's own 256 x 240, the middle (`NesLayeredPpu` now takes the middle of any chip wider than 256: save-state pictures,
recordings, every NES-only tool see the NES's picture), and `IWidePictureSource` (ppus/ExtPicture.cs, `NES.WidePicture`) gives the whole width to an app that can show it: the desktop app's NES loop shows
it at its size (and returns to 256 x 240 when another chip draws), and `GET /api/console/framebuffer` gives it. `APU_WMSS : NesApuOnChip`: the NES's four channels on WideMess voices (the DMC dropped and
counted). The FL plugin's chip list never takes `WMSS`: its family is the WideMess's (`CoreCatalog.NesApuFamily`), which `Bn2Consoles` lists no chip of, so no saved chip index moves. Lite roots both classes
(`WebLite/LinkerConfig.xml`) for the day it offers the WideMess.

### 6.5 The other consoles' chips on the WideMess console

The WideMess console's picture and sound menus offer the other consoles' chips as the SNES console does (`NES:<chip>`, the Game Boy chips through the NES, `SMS:SMSS`, `GG:GGS`, `MD:MDS`; not the SNES's own,
which the WMS chips are). A SNES game on them is exactly the SNES console's game on them (`wmsown`: every frame and sample). The game's own sound unit inside such a bridge is the WideMess's (`APU_WMS`), so its
sound RAM port and its unlock line keep working. What those chips cannot carry of a WideMess game is counted, never silent: frames drawn in the WideMess's own picture (the 8-layer mode or 400 wide: the
other chip shows what the SNES registers say) and frames sounding voices 8-31 (the bridges replay the S-DSP's eight), both in `SnesSession.Description` (the status line).

### 6.6 Tests and numbers (2026-10-24)

- `Workshop --enhanced-test --only wmsmenu,wms,wmsown,wmsgames` (Workshop/EnhancedCores/WideMessGuestTests.cs):
  - `wmsmenu`: the route on every other console's picture and sound menus and `WMSS` on the NES's, in the right section and order; none of it with the WideMess hidden, and a running game refuses it there.
  - `wms`: a scene per guest: 400 wide; the middle equal to `SNES:SFCS` (`SNESS` on the NES board) pixel for pixel; each rule as reported; **a NES game with vertical mirroring shows the real
    neighbouring nametable** (the right side is exactly the game scrolled 72 pixels on), with horizontal mirroring the screen repeats; a Game Boy's sides are the game scrolled there (96 real columns, then
    its first again), the window fills; a Master System repeats and its left-column mask stays the screen's; a Game Gear shows the VDP's columns 208-255 and a sprite left of the LCD; a Genesis plane is the
    game scrolled 40 on, a sprite at x 330 shows, the window fills, a colour only beside the screen is counted; a SNES 64-tile map is the game scrolled 72 on, a 32-tile one repeats, a sprite at X 300
    shows, a window over columns 0-7 reaches the left side only. Deliberate faults caught: a chip forced to repeat (NES, Game Boy), forced to fill (SNES), forced to draw no side sprites (Game Gear,
    Genesis). The sound chip: 11 voices on 1 bank, 18 on 1 bank, 440 Hz within 0.2 cent on a PCM voice and on a BRR voice, stereo; every guest's bridge with no voice dropped (the stock route through the
    NES drops 4 of the SNES's 8); a NES game on `APU_WMSS`; states.
  - `wmsown`: section 6.5, with the WideMess test ROMs `layers.wms` and `sound.wms` as the counted cases.
  - `wmsgames`: 12 games of the user's library in lockstep with `SNES:SFCS` (`SNESS`): Super Mario Bros., Ice Climber, Kirby's Adventure (NES), Super Mario Land, Kirby's Dream Land, Link's Awakening
    (Game Boy), Super Mario Bros. Deluxe (Game Boy Color), Sonic and Alex Kidd (Master System), Sonic (Game Gear), Sonic 1 (Genesis), Super Mario World (SNES): the middle identical every frame (1,100 to 2,400 frames each), no layer
    unreported, the sound with no voice dropped; allocation: no object a frame beyond what the same translator allocates on `SNES:SFCS` (the synthetic scenes measure 0 bytes), the sound chip 0 bytes a frame.
- `--picture-bridge-test` (alloc group): 0 bytes a frame on `WMS:WMSS` for the Master System, Game Gear and Genesis scenes.
- `Workshop --wms-guest-sheet --out <dir>`: contact sheets of those 12 games (the game's own picture beside the same frame 400 wide, the guest's edges marked, the rules and counts under it):
  docs/projects/widemess-images/m7/.
- `UAT/entrypoints/widemess-desktop-smoke.ps1` step GUEST: the NES's test.nes on `WMSS` shown 400 x 240 by the desktop app and 256 x 240 again on FIX; Sonic (Master System) on `WMS:WMSS` picture
  and sound, 400 x 192.
