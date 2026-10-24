# WideMess: a souped-up Super Nintendo fantasy console

Plan of 2026-10-23, written after an interview with the user. Work starts the week of 2026-10-26. Nothing is built yet.

Order (decided 2026-10-23): **WideMess milestone 1 first**, then the BrokenDev debugging toolkit
(`docs/projects/brokendev-debugging-plan.md`), then the Game Boy game VRUN: Vaults of Obsolete Code (its own plan later).

## The vision (the user's words, condensed)

WideMess is a **fantasy console implemented in BrokenNes**: a Super Nintendo with superior specs. It has a wider screen, a much
bigger SPC700-like sound chip (more channels, higher-quality samples, more memory), more system memory, and a picture mode with more layers
than mode 0 at full colour. It stays **feature-compatible with the SNES**, so it boots SNES games (and can enhance them), and it runs
**homebrew made for its extra power**.

It does for **Super VRUN** what Nested VRUN did for VRUN: once Super VRUN is complete (not before), it gets a **WideMess enhanced
version with extras**, built with **BrokenDev, which becomes the WideMess devkit**.

## Decisions (interview, 2026-10-23)

| Topic | Decision |
|---|---|
| Name | **WideMess** |
| Screen | **400 x 224, square pixels**. 50 tiles of 8 px; 16:9 within half a percent; the same 224 lines as the SNES. The original CTW's 398 x 224 canvas fits with one pixel to spare on each side, and its layout (the 256 x 224 room plus 71-px side panels) maps directly. |
| SNES games | **Run and enhance.** Every SNES game boots and plays exactly as on a SNES (the accurate SFC cores). Enhancements are opt-in per game where they work: widescreen, no sprite flicker, better sound. |
| CPU | **The same 65816, switchable speed.** SNES speed by default, so SNES games stay exact; a fast mode a WideMess game turns on (for example 10.74 MHz, as the SA-1 runs: 3x). |
| Sound | **The SPC700, much bigger.** The same SPC700 instruction set and S-DSP design, so SNES sound drivers still run, with many more voices (for example 32), BRR plus 16-bit PCM samples at higher rates, and far more sound RAM (1 MB). |
| Graphics mode | **8 background layers of 256-colour tiles** (8 bpp, from the 32,768-colour palette), each with its own scroll and per-line effects. Mode 0 has 4 layers of 4 colours. |
| Memory | **Generous.** Work RAM 1 MB (SNES 128 KB), video RAM 512 KB (64 KB), sound RAM 1 MB (64 KB), 1,024 palette colours (256). |
| Sprites | **256 sprites, 64 per line, sizes up to 64 x 64, optional 256-colour sprites.** No flicker in a busy two-player scene. |
| Overall limits | **Believable 16-bit.** Limits a 1994-1996 console could plausibly have had: still a puzzle to program, and VRUN-style optimisation still matters. |
| Game files | **Its own file type** (a new extension and header; clearly not a SNES ROM). BrokenNes still boots `.sfc` / `.smc` as SNES. |
| Devkit | **PVSnesLib + WideMess extensions** (C + 65816, the toolchain Super VRUN already uses), in BrokenDev. |
| Where | **Desktop app, BrokenNes Lite, the FL plugin** (the sound chip as an instrument, WideMess games in ROM mode), and **as other consoles' chips** (WideMess's picture and sound chips selectable for NES, Game Boy, Master System, Genesis and SNES games: the widescreen chip). |
| Milestone 1 | **The written spec, then a booting console.** A WideMess console in BrokenNes that boots SNES games exactly like the SNES, and runs a small test ROM using 400-wide and 8 layers. |

## Spec draft (v0, to settle in milestone 1)

Everything here is a starting point for the spec, chosen to fit the decisions. The open design questions are listed at the end.

**Principle:** a superset of the SNES. Every SNES register, memory region and timing behaves exactly as on the SNES until a WideMess
game unlocks the extensions (an unlock write plus an ID register a game reads). A SNES game can never reach a WideMess feature by accident.

**CPU and memory**
- 65816 (the SFC core), SNES speed at power-on. A speed register selects fast mode (for example 10.74 MHz), on WideMess games only.
- Work RAM 1 MB: the SNES's 128 KB at `$7E-$7F` unchanged, the rest in a mapping to be designed (banks the SNES leaves to the cartridge,
  or a banked window). It must not collide with cartridge ROM or SRAM.
- DMA / HDMA: the SNES's 8 channels, plus enough extra bandwidth (or vblank time) to update a 400-wide screen. Scrolling sideways needs
  about 25 new tile columns per frame instead of 17.

**Picture (the WideMess PPU)**
- SNES modes 0-7 unchanged at 256 x 224 / 239, and the same modes in **wide** at 400 x 224.
- **The WideMess mode**: 8 layers, 8-bpp tiles from 1,024 palette entries of 15-bit colour, per-layer scroll, per-line effects (HDMA),
  priorities, windows and colour math extended to 400 wide (window positions need 9 bits).
- Video RAM 512 KB, so 8 full-colour layers of 64 x 32 tiles fit.
- Sprites: 256 entries, 64 per line, 8 x 8 to 64 x 64, 4-bpp or 8-bpp. The SNES's 9-bit X already covers 400.
- Mode 7 in wide mode as well; it extends naturally, as bsnes-hd's widescreen Mode 7 shows.
- Timing (believable hardware): keep 60 Hz and 262 lines; draw the 400 visible pixels with a faster dot clock in the same line time, as
  the SNES's own hi-res modes already do with 512 half-pixels.

**Sound (the WideMess SPC700 / S-DSP)**
- The SPC700 instruction set and the S-DSP's registers unchanged for voices 0-7, so SNES sound drivers (SNESMOD, N-SPC...) run as-is.
- More voices (for example 32) in extra DSP banks; BRR and 16-bit PCM samples; higher output rates (for example 48 kHz); 1 MB sound RAM.
  The SPC700 sees 64 KB, so the rest is reached through banked windows or a DMA engine (to design).
- Pans, echo and pitch modulation per voice, as the S-DSP does today.

**Game file**
- Its own extension and header: a magic, the spec version, the required features (fast CPU, memory size, sound RAM), title, region.
  The ROM mapping comes from the header.

**Enhancing SNES games (opt-in per game)**
- **Widescreen:** show the parts of each layer that the game really keeps in video RAM (wide tile maps, Mode 7 planes); per layer
  extend, repeat or fill. The choice is explicit per game profile, never a silent fallback. Known limits: sprites culled at the original
  edge, enemies that pop in, HUDs laid out for 256, the edge where the game streams new columns.
- **No sprite flicker:** the per-line sprite limit raised.
- **Better sound:** better sample interpolation; more voices cannot be added to existing music.
- **Fast CPU:** removes slowdown, risky for game timing; per game.

## How it fits BrokenNes

- **A new console** `wms` (WideMess) with a session built on the SNES session: CPU = the SFC 65816 with the speed switch, PPU_WMS and
  APU_WMS as **subclasses of the stock SFC chips**, so SNES games stay golden-hash identical (the rule every enhanced chip already follows).
- **Stock and Enhanced:** on its own menu WideMess is the "best core" of its console. Its picture and sound chips also join the other
  consoles' menus through the enhanced contracts (`IEnhancedPictureChip`, `IEnhancedSoundChip`, `docs/projects/enhanced-cores.md`).
  For the widescreen view on other consoles, the contract gains one thing: each console's translator also reports **how wide its layers
  really are**, and the wide chip chooses per layer whether to extend, repeat or fill.
  - NES: 512 wide with vertical mirroring.
  - SNES: 512-wide maps, 1024 for Mode 7.
  - Genesis: 512 or 1024-wide planes.
  - Master System and Game Boy: 256, so their pictures can only repeat or be filled.
- **The FL plugin:** the WideMess sound chip as an instrument (many voices, 16-bit samples), with chip indexes **appended** after the
  Presets chips (append-only). WideMess games in ROM mode.
- **Lite:** linker roots for the new types; a 400 x 224, 8-layer picture must hold 60 fps in WebAssembly (measure early).
- **Tests:**
  - every SNES golden unchanged;
  - a WideMess conformance suite of small test ROMs, one per feature, each with a negative control (as the 65816 tests and the
    enhanced checks do);
  - zero allocation per frame on the new paths;
  - BrokenDev `verify` for WideMess projects. No other emulator runs WideMess, so its compat column is BrokenNes only. The SNES build
    of a game is still checked on every emulator.

## BrokenDev as the WideMess devkit

- **PVSnesLib with WideMess extensions:**
  - headers for the WideMess registers and the unlock;
  - a linker setup for the bigger memories;
  - library calls for the WideMess mode, the sprites and the sound chip.
- **Converters:** 8-bpp layers and 256-colour sprites from PNG at 400 wide; palettes of 1,024 entries; 16-bit PCM samples for the sound chip.
- **A sound driver** for the bigger SPC700 (extending SNESMOD, which Super VRUN uses, or a new one), with the FL path: compose in FL
  Studio, preview through the BrokenNes2 plugin (its Test MIDI export and Capture), build into the game.
- **A project template:** `brokendev new widemess` (the "starter kit" improvement from the BrokenDev page).
- `build`, `verify`, `run` and `play` for WideMess projects, plus a sample game that uses every feature.

## Spinoffs: an FPGA core or a C++ port (later)

The user (2026-10-24): "it would be very useful later if we were to make a spinoff of widemess as an fpga core or as a cpp port".
Neither is planned yet. Two things already built make them possible:

- **The spec** (`docs/projects/widemess-spec.md`) is the contract, written independently of BrokenNes's code.
- **The WideMess test cartridge** is built to be their conformance suite:
  - it checks itself, and shows pass or fail on screen;
  - it writes its results to a documented table in RAM, so a test harness on any implementation can read them;
  - it can run every self-checking test in sequence, unattended;
  - it relies only on the spec.

## Super VRUN: WideMess edition (later)

**Only after Super VRUN is complete.**
- **Built from the same source** with a WideMess target in BrokenDev, as Nested VRUN is built from VRUN.
- **Expected extras:**
  - CTW's original layout: the room plus the side panels, 398 x 224 inside 400 x 224;
  - two-player without lag, through the fast CPU;
  - richer music (more voices, 16-bit samples);
  - more parallax and backdrop layers from CTW.

## Milestones

| | Milestone | Done when |
|---|---|---|
| **M1** (done 2026-10-24) | **Spec v1 + a booting console** | The spec is written and agreed (registers, memory map, timing, file format, unlock). WideMess appears in BrokenNes and boots SNES games with every SNES golden unchanged. A hand-written test ROM shows 400 x 224 and 8 layers of 256 colours. **Result:** `docs/projects/widemess-spec.md` v1; the console `wms` in the desktop app and Workshop; `Workshop --wms-test` passes 21 of 21 checks plus 13 SNES games frame-identical on both consoles (SNES console and WideMess); the desktop smoke test passes 7 of 7; UAT 125 pass, 0 fail. Details: `WideMess/README.md`. |
| **M2** (done 2026-10-24) | **Picture complete** | Sprites (256, 64 per line, 256-colour), windows and colour math at 400 wide, Mode 7 wide, the conformance suite for the picture. **Result:** spec section 3 settled (3.1-3.4: the three pictures, SNES columns centred at 72, the SNES modes and Mode 7 wide, the two sprite tables and the 8-byte WideMess OAM entry, sprite slots between the 8 layers, windows and colour math at 400, registers `$21A0-$21BF`); one wide compositor in `PPU_WMS`; three test ROMs (`sprites`, `windows`, `widesnes`) checked pixel for pixel against an independent model of the spec, each with negative controls; 9 SNES games drawn wide keep their centre identical to the SNES chip line for line; `--wms-test` 70 of 70 with the 9 games; desktop smoke 9 of 9; UAT 125 pass, 0 fail. Details: `WideMess/README.md`. |
| **M3** (done 2026-10-24) | **Sound chip** | 32 voices, BRR + 16-bit PCM, 1 MB sound RAM, SNES drivers unchanged on voices 0-7; the conformance suite for sound. **Result:** spec section 4 settled (the main CPU's unlock reaches the sound unit; voices 8-31 in a second DSP register page; a 16-byte sample directory with 20-bit addresses; the port `$21F0-$21F3`; the mix into the S-DSP before its echo; 32 kHz). `APU_WMS` is the WideMess console's stock sound; `WMFEAT` bit 2 is set. Test ROM `WideMess/testroms/sound` (65816 + SPC700) with its negative controls; `--wms-test` 71 of 71, 80 of 80 with the nine SNES games (frame- and sound-identical, the same sound hashes as before); 9 of 9 deliberate bugs caught; the ALttP golden IDENTICAL; the SNES path's speed unchanged (A/B 1.006). Details: `WideMess/README.md`. |
| **M4** (done 2026-10-24) | **CPU and memory** | Fast mode, 1 MB work RAM mapping, the extra DMA bandwidth. **Result:** spec section 2 settled (the registers need the unlock, the WideMess map does not; fast mode's I/O exceptions; work-RAM addresses for the 20-bit port; the map's limits); `BOARD_SFC.WideMess.cs` and `WideMess.Memory.cs`; test ROMs `WideMess/testroms/cpu` (fast CPU within one loop iteration of the spec's cycle model, fast DMA about 2x, a 4 MB game on the WideMess map with 1 MB work RAM and 256 KB battery RAM) with negative controls; `--wms-test` 64 of 64, also on the reference paths; 9 SNES games frame-identical, the speed-pass goldens unchanged, SNES speed unchanged; UAT 125 pass, 0 fail; desktop smoke 7 of 7. Details: `WideMess/README.md`. |
| **M5** (done 2026-10-24) | **The devkit** | PVSnesLib extensions, converters, the sound driver, `brokendev new widemess`, a sample game, documentation. **Result:** `BrokenDev/widemess`: `widemess.h` and a C + 65816 library over every register of the spec (8-layer mode, video RAM and colours, the 256-sprite table, fast CPU and DMA, the 1 MB work RAM, the sound port), the WideMess map's setup on PVSnesLib's HiROM libraries; `wmsgfx.py` (PNG to 8-bpp layers and 4/8-bpp sprites, 1,024-colour palettes; tiles byte-identical to gfx4snes's) and `wmssnd.py` (WAV to 16-bit PCM or BRR, MIDI to songs); an SPC700 sound driver (songs on 24 channels = voices 8-31, effects on all 32); `brokendev new widemess` and WideMess projects in `build` / `verify` / `run` / `compat`; the sample game Wide Blaster (`WideMess/sample`: 8 layers, up to 199 sprites, 12 voices at once, fast mode, far RAM). `--wms-test` section M5 with negative controls (no unlock, the template against the sample's thresholds, the sample at SNES speed); BrokenDev's tests 115 pass, 0 fail, 0 skip. The guide: `docs/projects/widemess-devkit.md`; details: `WideMess/README.md`. |
| **M6** (done 2026-10-24) | **SNES games enhanced** | Per-game profiles: widescreen (extend / repeat / fill per layer), no flicker, better interpolation; a set of games proven. **Result:** spec section 6 settled before coding (the enhancements are the console's: the game stays locked and reads SNES values, `STAT77` included; masks with scene conditions; profiles `WideMess/profiles/*.json` keyed by the image's SHA-1, strictly checked). `PPU_WMS` draws a locked SNES game's enhanced line through the wide compositor (widescreen side rules, no flicker at WideMess's 64 / 128 limits), `DSP_SFC` gains cubic interpolation (mixed only), the board a console fast CPU. 11 games proven with contact sheets (`docs/projects/widemess-images/m6`): widescreen good for Super Mario World, F-Zero, Super Mario Kart; risky for Yoshi's Island, Donkey Kong Country, Super Metroid, A Link to the Past, Mega Man X, Contra III, Super Castlevania IV; unavailable for Chrono Trigger. Each in lockstep with the SNES console: the centre identical every frame, `STAT77` and sound identical, off frame-identical. Fast CPU: Super Mario World hangs if it is on at power-on (traced to the game's own sound-driver timing); Chrono Trigger and Contra III hang at a scene change. Desktop: Console > WideMess enhancements; HTTP API `/api/widemess/*`. Details: `WideMess/README.md`. |
| M7 | Everywhere | WideMess chips on the other consoles' menus (the widescreen chip), Lite at 60 fps, the FL plugin instrument and ROM mode. |
| M8 | Super VRUN WideMess edition | After Super VRUN is complete. |

## Open questions to settle in milestone 1

1. The file extension and header magic (the user's call, e.g. `.wms`).
2. The fast-mode clock (10.74 MHz proposed) and how it interacts with DMA and the PPU.
3. The 1 MB work RAM mapping in the 65816's 24-bit space next to cartridge ROM and SRAM.
4. The register range for WideMess features and the unlock sequence, avoiding the ranges SNES coprocessors use (the SA-1 at `$2200-$23FF`).
5. How the SPC700 reaches 1 MB of sound RAM (banked windows or DMA), and the exact voice count.
6. The wide-mode timing model (dot clock and H-blank length) and the DMA budget per frame at 400 wide.
7. Which SNES-game enhancement comes first (widescreen is the most visible; no flicker the safest).
8. Reserve the debug port registers and the hardware-check hooks in the spec, so the BrokenDev debugging toolkit (which follows
   WideMess milestone 1) covers WideMess as soon as it boots.
