# Nested Everything: research (2026-11-14)

Research only. No code was changed and nothing was downloaded.

## Recommended first step

**Build "Nested SMS -> MD" as a static Z80-to-68000 recompiler with a small 68000 runtime that translates VDP writes. Start with VRUN CTW SMS, then use the same wrapper for VRUN Vaults GG (GG -> MD adds only the palette, viewport and Start-button layer).**

Before any of that, write one tiny 68000 test ROM for the user's flash cart that answers the biggest open hardware question: **does the Genesis VDP draw SMS Mode 4 when the 68000 clears register 1 bit 2 (M5), in normal Genesis mode, without the cartridge M3 reset?** If it does on the user's console, the VDP work drops from "translate everything to Mode 5" to "keep Mode 4 and fix the differences". Until a real console says yes, the plan below assumes Mode 5.

Why this pair first: both VRUN games are our own SDCC/devkitSMS builds, so we know every VDP, sound and mapper habit they have (table at the end). That makes a static recompiler practical, the same way Nested VRUN came before universal Nested.

---

## 1. GG -> SMS wrapper

The Game Gear is an SMS with a few changes. Our Vaults GG ROM already has a runtime switch for this (`gg_on_sms`, commit 924a9b8, written after the cart showed the problems), so most of the list below is proven on hardware by us.

| Difference | GG | SMS | What the wrapper does |
|---|---|---|---|
| Palette | 12-bit CRAM, 64 bytes, written as byte pairs (latched on the odd byte) | 6-bit CRAM, 32 bytes | Trap writes when the VDP address is in CRAM mode (control word with top bits `11`). Keep a 64-byte GG shadow; on each odd byte, convert RGB444 -> RGB222 (top 2 bits of each channel) and write one SMS byte. Optional: a better rounding table. |
| Viewport | 160x144 window inside the 256x192 picture (columns 6-25, rows 3-20) | Full 256x192 | Nothing is required: GG games draw into the same name table, so the GG picture is already centred on the SMS. The rest of the screen shows whatever the game left there (garbage borders, as in many GG2SMS patches). Better: blank the border with the left-column blank bit plus a line-interrupt that turns the display off above row 24 and below row 168, or let the game's own tiles show. No scaling: the SMS has no scaler. |
| Start | Port $00 bit 7 | No such port; the Pause button raises NMI | Trap `IN A,($00)`; the NMI handler sets a "Start held for N frames" flag and the trapped read returns it. Our trap: on an SMS, port $00 reads floating bus with bit 7 = 0, i.e. "Start held forever". |
| Stereo PSG | Port $06 pans each channel L/R | Mono | Drop writes to $06 (the SMS ignores it or it hits a mirror; trap it to be safe). |
| Link port | Ports $01-$05 | Absent | Return idle values ($01=$7F, $02=$FF, $03=$00, $04=$FF, $05=$00): the values our `gg_on_sms` check uses. Two-player link games cannot work. |
| SMS-mode GG games | Some GG carts are SMS games run in the GG's SMS mode | - | Already SMS games: no wrapping, just rename. Detect by header region code. |
| Sprite/VDP | Same Mode 4; GG VDP has no TMS legacy modes and a different sprite zoom | - | Nothing for nearly all games. |

**Can it be static patching plus a small runtime? Yes.** The Z80 and VDP are the same, so no recompiling is needed. A GG game reaches the hardware with a handful of instruction patterns (`OUT ($BF),A`, `OUT ($BE),A`, `OTIR`/`OUTI` loops to $BE, `IN A,($00)`, `OUT ($06),A`). The wrapper:

1. Scans the ROM (the disassembly/trace tools we already use for Nested) for `IN`/`OUT` to $00-$06 and $BE/$BF.
2. Replaces each with `RST $xx` or `CALL` into a runtime in free ROM space (RST is 1 byte, so it fits where `OUT (n),A`, 2 bytes, was; a NOP fills the hole). Block outputs (`OTIR` to $BE) get a runtime version that checks "CRAM or VRAM".
3. Installs an NMI handler at $0066 for Pause -> Start.
4. Keeps a few bytes of RAM for the CRAM shadow, latched byte and Start timer. Most GG games leave the top of RAM ($DFF0-$DFFF area) near the stack; the safe slot must be found per game (same problem Nested has with NES RAM).

This is exactly how the community GG2SMS patches work, by hand, one game at a time (section 3). An automatic tool needs a fallback for code it cannot see (code in RAM, computed jumps): a run-time check in the BrokenNes headless harness that logs any port $00/$06 or CRAM write that did not go through the runtime, like Nested's feedback profiles.

---

## 2. SMS/GG -> Genesis without the hardware SMS mode

### How the flash cart (and the Power Base Converter) do it

The Genesis contains the SMS hardware: the Z80, the VDP has a Mode 4, the PSG is in the VDP. A cartridge (or the Power Base Converter) pulls the cartridge's M3 line at power-on/reset; the console then boots with the 68000 held off, the Z80 as the main CPU and the memory map of an SMS. This mode is fixed until the next power cycle or reset. The user's cart does it by switching mode and resetting the console from its menu.

Why it is janky for the VRUN Collection:
- It is a one-way trip: once in SMS mode the 68000 is off, so a Genesis menu cannot launch the game and come back. A "collection" menu has to be an SMS program, or the user goes back through the cart menu.
- A GG game runs as an SMS game (no GG palette, no Start button). We already hit this: Vaults GG needed the 924a9b8 fix to run at all.
- The Genesis 3 (and some later boards) do not have the SMS mode; neither do most clones. A wrapped ROM would run on all of them.
- No FM: the Mark III/Japanese SMS FM unit is not present.

Can Mode 4 be entered without the reset? **Partly, and this is the hardware test we should do first.** The Genesis VDP in normal mode treats register 1 bit 2 (M5) = 0 as Mode 4. Genesis Plus GX emulates "Mode 4 in Mega Drive mode", which some test ROMs use, but there are known differences: the CRAM is read as the 9-bit Genesis format, the screen is still 224 lines tall timing-wise, the SAT address and other registers keep Genesis meanings in places, and behaviour may differ between VDP revisions. Our own BrokenNes Genesis VDP (`Windows\NesEmulator\md\GpgxRender.cs`, ported from GPGX's Mode 5 path) only draws Mode 5, so we cannot test this in BrokenNes today; BizHawk's Genesis Plus GX in BrokenDev can, and the user's real console is the final word. Even if Mode 4 works, the Z80 memory map, the CPU and the input ports are still Genesis ones, so the CPU problem (below) is the same.

### The options

| | a. Recompile Z80 -> 68000 | b. Game code on the Genesis's own Z80 | c. Z80 interpreter in 68000 | d. Mode 4 + recompile (hybrid of a) |
|---|---|---|---|---|
| Idea | Nested-style: translate the game's Z80 code to 68000 code ahead of time (plus a run-time translator for code not seen), runtime translates VDP/PSG/IO | Run the original bytes on the Z80, 68000 polls a mailbox and does the VDP work | Classic emulator loop on the 68000 | As a, but leave the VDP in Mode 4 if the test ROM proves it works |
| Feasibility | Good for our own games (SDCC code, no self-modifying code, known banks); hard but possible in general (Nested proves the method) | **Poor.** The Genesis Z80 has 8 KB RAM at $0000-$1FFF and sees the cart only through a 32 KB banked window at $8000-$FFFF (bank set by 9 serial writes to $6000). An SMS game needs ROM at $0000-$BFFF and 8 KB RAM at $C000: the map does not fit, so the code must be relocated, i.e. rewritten anyway. Every window access also stalls both CPUs. | Works, but too slow (row below) | Same as a; only if the hardware test passes |
| Speed estimate | 68000 at 7.67 MHz vs Z80 at 3.58 MHz = 2.1x the clock. Z80 instructions average ~4-11 T-states; translated, most become 1-3 68000 instructions (8-20 cycles) when flags are computed only where they are read (lazy flags, the main trick). Rough: 0.8x-1.5x real time for straight game code; VDP writes cost more (see below). | Z80 speed is native, but every ROM/VDP access through the window costs extra cycles and the 68000 must be stopped (BUSREQ) or polled | 30-60 68000 cycles per Z80 instruction for fetch/decode/flags = roughly 1/4-1/8 of real time. Too slow for games near their frame budget. | As a, faster VDP side |
| Accuracy risks | Flags (half-carry, P/V, the undocumented bits) where code depends on them; `DAA`; timing loops tied to Z80 cycles; code that jumps into the middle of instructions or runs from RAM; mid-frame VDP effects whose timing shifts | Bus stalls change timing; sound-driver-like code only | Slow so effectively wrong | Mode 4 quirks on the Genesis VDP; revision differences |
| Effort | Large (months for universal; weeks for our two games) | Large and still ends in recompiling | Medium, but dead end | Large, smaller VDP part |
| What carries over from Nested | Almost everything: static + dynamic recompile, entry-point feedback profiles (`nested-build -Feedback brokennes`), per-game profiles, step-by-step md5 golden, "accurate vs fast" per game, mapper hooks (Sega mapper $FFFC-$FFFF -> 68000 address offsets), the headless measuring sweep | Little | The idea of a runtime only | As a |

Known Z80-on-68000 prior art is thin (section 3); we found no shipped SMS emulator running at full speed on a stock Genesis, which matches the speed estimate for option c.

### The VDP translation (option a, Mode 5)

| SMS Mode 4 | Genesis Mode 5 | Translation |
|---|---|---|
| Tiles 4bpp **planar** (4 bytes per row, one per bit plane) | 4bpp **packed** (one nybble per pixel) | Keep a 16 KB SMS VRAM shadow in 68000 RAM. On VRAM writes mark the tile row dirty; convert dirty rows (table lookup, 4 bytes in -> 4 bytes out) and DMA them in vblank. Tile uploads are the main cost. |
| Name table 32x28, 16-bit entries (9-bit tile, hflip, vflip, palette select, priority) | Plane A/B entries (11-bit tile, hflip, vflip, 2-bit palette, priority) | Bit shuffle via a table; write to plane A. |
| 2 palettes x 16, 6-bit colour | 4 x 16, 9-bit colour | 1:1 map of 6-bit -> 9-bit (GG: 12-bit -> 9-bit). |
| 64 sprites, 8 per line, 8x8 or 8x16 (one size for all), tiles from 256-tile half | 80 sprites, 20 per line, link list, many sizes | Rebuild the sprite table each frame from the SAT shadow. Genesis shows more per line, so SMS flicker games show less flicker (an enhancement, or emulate the limit for accuracy). The SMS "sprite X shift left 8" bit maps to an offset. |
| Line interrupt (reg 10 counter), frame interrupt | HInt (reg 10, same down-counter idea), VInt | Direct map, mapped to the 68000's level 4 and 6 handlers which call the translated Z80 IM1 handler ($0038). Line numbers shift by the vertical centring offset. |
| H scroll whole screen; top 2 rows lock (reg 0 bit 6); right 8 columns lock V scroll (bit 7); left column blank (bit 5) | Line scroll, 2-cell column V scroll, window plane | Top-rows lock: H scroll per line (we already use line mode in Super VRUN MD). Right-column lock: window plane for the right 8 columns, or column V scroll. Left blank: a sprite/window mask or plane B colour. |
| 256x192 | H32 = 256 wide, 224 tall | 16-line border top and bottom (or show more of the name table). GG: 160x144 inside, centred, optional window crop. |
| Read VRAM/status, V counter ($7E), H counter | Different V counter values in 224-line mode | Runtime returns SMS-like counter values. |

### Sound and input

- **PSG:** the Genesis has the same SN76489 at $C00011, so PSG writes go straight through (no translation). GG stereo ($06) can be dropped, or faked by panning... the Genesis PSG is mono, so drop.
- **FM unit (YM2413):** optional, the YM2612 is a different chip; a conversion table would be an enhancement, not a requirement. The VRUN games do not use FM.
- **Input:** SMS ports $DC/$DD (active-low bits) -> read the Genesis pad at $A10003, convert. Pause -> Genesis Start, delivered as the Z80 NMI handler call. GG Start -> Start.
- **Mapper:** the Sega mapper (writes to $FFFC-$FFFF) becomes a translated "bank base" for 68000 addresses; with code statically translated per bank (as Nested does per NES bank), most bank switches cost nothing.

---

## 3. What exists already

- **GG2SMS patches:** many hand-made GG -> SMS conversions exist, collected on SMS Power and ConsoleMods; they patch palette writes, Start and the borders game by game. Known limits: border garbage (Ristar v1), incomplete looks (Tails Adventure, later improved by a "Deluxe" rework).
  - [ConsoleMods: Game Gear on Master System Patches](https://consolemods.org/wiki/Master_System:Game_Gear_on_Master_System_Patches)
  - [SMS Power: Ristar GG2SMS](https://www.smspower.org/Hacks/RistarTheShootingStar-GG-GG2SMS)
  - [Indie Retro News: Tails Adventure SMS Deluxe](https://www.indieretronews.com/2016/10/tails-adventure-sms-deluxe-gg2sms-patch.html)
  - [RetroRGB: Game Gear on SMS/Genesis](https://admin.retrorgb.com/?p=1386), [Nicole Express: Sega 8-bit conversion kit](https://nicole.express/2022/sega-8-bit-conversion-kit.html), [FRS' SMS patches](https://www.frs.badcoffee.info/Sega/SMS-patches.html), [Sonic Drift 2 GG2SMS](https://sonic-chaos-gamer.itch.io/sonic-drift-2-gg2sms-sega-master-system)
- **SMS on Genesis:** every known route uses the hardware mode (Power Base Converter, flash carts). An AtariAge thread about an SMS game on a Genesis cart found the board simply contained the Power Base Converter's chip: [AtariAge: Master System ROM in a Genesis repro](https://forums.atariage.com/topic/321431-master-system-rom-in-a-genesis-repro/). Genesis Plus GX documents Mode 4 behaviour: [Genesis Plus GX wiki](https://www.github.com/ekeeke/Genesis-Plus-GX/wiki).
- **SMS emulator written for the 68000 Genesis:** none found in this search. Nested itself (NES -> SNES) is the closest model; there is no "Nested for Sega".

Open: the SMS Power development forum and SpritesMind (Genesis homebrew) are the places to search further for Mode-4-in-MD-mode tests and any 68000 Z80 recompiler experiments.

---

## 4. "Nested Everything" as a BrokenDev feature

One pipeline, one console pair per adapter:

| Pair | Method | Status | Effort |
|---|---|---|---|
| NES -> SNES | Project Nested fork (recompiler + runtime) | Working, compat climb running (29/53 Perfect/Playable) | - |
| GG -> SMS | Static patch + small Z80 runtime | Not started; our gg_on_sms code is the runtime seed | Small |
| SMS -> MD | Z80 -> 68000 recompiler + VDP/IO runtime | Not started | Large |
| GG -> MD | SMS -> MD + the GG layer (palette, Start, viewport) | Follows SMS -> MD | Small after SMS -> MD |

BrokenDev UI: ROM in -> pick the target -> profile (accurate / fast, per game, as Nested) -> wrapped ROM out + report (unhandled ports, code not translated, lag).

Testing reuses what we have:
- **Headless BrokenNes (MCP / Workshop):** run the original on the SMS/GG core and the wrapped ROM on the Genesis core with the same input script; compare pictures per frame (tools/piccmp.py style, scaled/cropped for the border), PSG output (sndcmp.py), and the game's RAM (the SMS RAM shadow at a known 68000 address vs the SMS core's RAM: the best signal, as Nested's `--nested-cert`).
- **Entry-point feedback:** the run-time translator logs code it had to translate live; feed it back into the static build (same as `nested-build -Feedback brokennes`).
- **Cross-check:** BizHawk's Genesis Plus GX in BrokenDev for the Genesis side, Mesen for SMS/GG.
- **Golden:** wrapper runtime md5 pinned per step, VRUN wrapped ROMs byte-identical, as the Nested golden.
- Gap: the BrokenNes Genesis core draws only Mode 5; the Mode 4 path (option d) could only be tested in BizHawk/GPGX and on the console.

Which pair first: **GG -> SMS is the fastest to build** (days, and it is a nice check of the pipeline). **SMS -> MD is the one the VRUN Collection needs.** Order: GG -> SMS as a small first deliverable, then SMS -> MD with CTW, then GG -> MD with Vaults.

---

## 5. VRUN Collection on Genesis: shortest path

What the two games actually do (from the sources):

| | VRUN CTW SMS (`VRUN_CTW_SMS_dev`) | VRUN Vaults GG (`VRUN_VOC_GG_dev`) |
|---|---|---|
| Code | SDCC C from `fab2c.py` + hand Z80 asm (game_fast_b21-23, nes_shim) | SDCC C physics copied from the GB + Z80 asm hot paths (sm83_to_z80.py output) |
| Mapper | Sega mapper, many banks (data banks 2-18, code banks 19-23, `__banked` calls via `___sdcc_bcall_ehl`) | Sega mapper, map data in banks ~38-47 |
| VDP | Mode 4, tall (8x16) sprites, backdrop colour, H/V scroll, frame IRQ, **line interrupt at line 183 for the HUD band** (`hud_line_isr`, only while the band shows) | Mode 4, GG viewport, GG palette (with the SMS fallback `gg_on_sms`), frame IRQ, no line interrupt found |
| Sound | PSG only (NES APU music/sfx converted, `nes_apu.c`) | PSG only (GG stereo not needed for its music) |
| Input | Pad 1, Pause | GG Start, or Pause on SMS |
| Frame budget | Close to full (room-1 steady frames were a speed problem in S2) | Physics ~59K of 148K cycles per tick (comfortable) |
| Risk for a wrapper | Speed: the recompiled code must not be slower than the Z80 | Low |

Shortest path:

1. Hardware test ROM: Mode 4 in Genesis mode on the user's console (one evening; answers option d).
2. Z80 -> 68000 static recompiler limited to what SDCC emits (both games have no self-modifying code and no code in RAM that we know of; check the banked-call trampoline), plus lazy flags. Use the SDCC map/symbol files we produce for entry points: we have the sources, so the "feedback" step starts already complete.
3. Runtime: VRAM shadow + tile converter, name table / SAT rebuild, CRAM convert, HInt for line 183, scroll locks only if used, PSG pass-through, pad -> $DC, Start -> NMI.
4. A collection menu on the 68000 side that starts either wrapped game; going back to the menu is a soft reset in our own code (the thing the flash cart's hardware mode cannot do).
5. Certify with the headless harness: SMS core vs Genesis core, same input, RAM + picture + sound compare; then BizHawk GPGX; then the cart.

Note on scope: because we own the sources, the cheapest route to "VRUN Collection on Genesis" in pure hours would be to build the two games' C against a Genesis back end (as we did NES -> SMS with `nes_shim`). The user chose wrappers on purpose, because the wrapper also gives the Genesis backwards compatibility for other games. The plan above keeps the VRUN games as the first certified titles of a general SMS -> MD adapter, the same role Nested VRUN plays for universal Nested.

## Biggest risks

1. **Speed of CTW** after recompiling: Z80 flags and VDP write translation could push its heavy frames over budget. Measure the translated cycle count per frame early (headless profiler).
2. **Tile conversion cost**: games that stream tiles every frame (CTW's room loads, animated tiles) must convert planar -> packed in time; vblank DMA bandwidth on the Genesis is good, but the 68000 conversion is not free.
3. **Mode 4 on the Genesis VDP without the reset is unproven** on the user's console, and BrokenNes's Genesis core cannot emulate it yet.
4. **General games** (beyond ours): self-modifying code, code in RAM, cycle-timed raster effects and undocumented flags; this is the same long tail Nested's compat climb is fighting.

## Files this would need the user to approve (none downloaded)

None are needed for the plan. For a wider survey later: SMS Power's GG2SMS patch archives (IPS files per game) and any Mode-4-in-MD-mode test ROMs from the Genesis Plus GX tree; each would be listed by exact file before download.
