# WideMess hardware specification, v1

The contract between WideMess games and the WideMess console in BrokenNes. It settles milestone 1's open questions from
`docs/projects/widemess-plan.md`. Version 1.0, 2026-10-24. Each section says which milestone builds it; the table at the end shows
what exists today.

## 0. Principles

- **A superset of the SNES.** At power-on a WideMess is a SNES: every register, memory region and timing is the SNES's. The
  extensions stay invisible (reads return open bus, writes do nothing) until a game **unlocks** them. A SNES game cannot reach one
  by accident.
- **Believable 16-bit hardware.** Limits a 1994-1996 console could plausibly have had. The same 21.477 MHz master clock, the same
  262 lines of 1364 master clocks, the same 60 Hz.
- **Nothing silent.** A game that asks for a feature this console does not have is refused with a message, never run halfway.

## 1. Identity and unlock (milestone 1)

CPU-side registers, in the SNES's unused `$4220-$423F` range (banks `$00-$3F` and `$80-$BF`). No SNES cartridge chip that BrokenNes
emulates decodes this range: the SA-1 is at `$2200-$23FF`, the Super FX at `$3000-$32FF`, the S-DD1 and SPC7110 at `$4800+`.

| Address | Name | R/W | Meaning |
|---|---|---|---|
| `$4220` | WMUNLOCK | W | Write `$57` ('W') then `$4D` ('M') as two consecutive writes to this register: the extensions are unlocked until the next reset. Any other value restarts the sequence. On a SNES these writes go nowhere. |
| `$4221` | WMID | R | `$57` ('W') once unlocked. A SNES returns open bus, which for `LDA $4221` is `$42` (the address's high byte), so the read tells the two apart. |
| `$4222` | WMVER | R | Spec major version (1). |
| `$4223` | WMREV | R | Spec minor version (0). |
| `$4224` | WMFEAT | R | Features this console has (bit = 1): 0 picture (wide output, 8-layer mode); 1 sprites+ (the rest of the picture, section 3: the 256-sprite table, windows and colour math at 400, the SNES modes wide); 2 sound+ ; 3 fast CPU; 4 1 MB work RAM; 5 fast DMA; 7 debug port. |
| `$4225` | WMSPEED | R/W | Bit 0: fast CPU (milestone 4). |
| `$4226` | WMDMA | R/W | Bit 0: fast general DMA (milestone 4). |
| `$4227-$422F` | | | Reserved: read 0, write ignored. |
| `$4230-$423F` | | | Reserved for the BrokenDev debug port (`docs/projects/brokendev-debugging-plan.md`). |

Detection, in a game: write `$57`, `$4D` to `$4220`, then read `$4221`. `$57` means WideMess; check `WMFEAT` for each feature you use.

## 2. CPU and memory (milestone 4)

- **The 65816 at SNES speed** at power-on. **Fast mode** (`WMSPEED` bit 0) makes every CPU access and internal cycle take 2 master
  clocks (10.74 MHz, the SA-1's speed), except the I/O registers (`$2100-$21FF`, `$4000-$43FF`), which keep their SNES timing.
  DMA, HDMA, the picture and the sound keep theirs.
- **1 MB of work RAM** on cartridges that use the WideMess map (game file, section 5):
  - banks `$70-$7F` are all work RAM, 1 MB in one block, with the SNES's own 128 KB at `$7E-$7F`;
  - `$00-$3F:0000-1FFF` still mirrors `$7E:0000-1FFF`;
  - the WRAM port `$2180-$2183` takes a 20-bit address (`$2183` bits 0-3) once unlocked.

  Cartridges with a LoROM or HiROM map keep the SNES's 128 KB and its map.
- **The WideMess map:**
  - ROM up to 4 MB at `$C0-$FF`, with HiROM-style mirrors at `$00-$3F` / `$80-$BF:8000-FFFF`;
  - battery RAM up to 256 KB at `$20-$3F` / `$A0-$BF:6000-7FFF` (8 KB per bank);
  - work RAM at `$70-$7F`.
- **Fast DMA** (`WMDMA` bit 0): general DMA moves a byte every 4 master clocks instead of 8. About 12 KB fits in a vblank instead of 6,
  enough to scroll a 400-wide screen. HDMA keeps its timing.

## 3. Picture (milestone 1 for the 8-layer mode; milestone 2 for the rest)

**Timing.** Unchanged: 262 lines of 1364 master clocks, 224 visible lines (239 with overscan, `SETINI` as on the SNES). In wide mode,
400 pixels are drawn in the same active period as the SNES's 256 (2.5 master clocks per pixel), so H/V counters, H/V IRQs and HDMA keep
their SNES values (counters count SNES dots).

**Memories.**
- Video RAM: 512 KB. The SNES's 64 KB is its first 64 KB, so the SNES modes see what they always saw.
- Colours: 1,024 entries of 15-bit BGR, the SNES's 256 being the first 256.

**The B-bus window.** WideMess's B-bus registers are live only after the unlock, and DMA and HDMA can write them. They sit in
`$21A0-$21FF`, which the SNES leaves unused; it skips `$2188-$219F`, the Satellaview's.

| Range | Part | Milestone |
|---|---|---|
| `$21A0-$21AF` | sprites (OAM port, sprite settings) | 2 |
| `$21B0-$21BF` | windows and colour math at 400 wide | 2 |
| `$21C0-$21CF` | the picture: control, video RAM and colour ports (`$21C9-$21CF` spare) | 1 |
| `$21D0-$21EF` | the 8 layers: maps, settings, scrolls | 1 |
| `$21F0-$21F7` | the sound RAM port | 3 |
| `$21F8-$21FF` | reserved | |

**Registers of the picture:**

| Address | Name | Meaning |
|---|---|---|
| `$21C0` | WMCTRL | Bit 0 WIDE: 400-pixel output. Bit 1 LAYERS8: the WideMess mode replaces `BGMODE`'s mode. A write also resets the scroll registers' byte phase. |
| `$21C1` | WMTM | WideMess mode: BG1-BG8 on the main screen (bit n = BG n+1). |
| `$21C2-$21C4` | WMVADDL / M / H | Video RAM byte address, 19 bits (`$21C4` bits 0-2). |
| `$21C5` | WMVDATA | Write: one byte at the address, then the address + 1. Read: the same, reading. |
| `$21C6-$21C7` | WMCGADDL / H | Colour entry, 10 bits (`$21C7` bits 0-1); resets the byte phase. |
| `$21C8` | WMCGDATA | Write the low byte, then the high byte; the entry is stored on the high byte and the address goes + 1. Reads in the same order. |
| `$21C9-$21CF` | | Reserved. |
| `$21D0+n` | WMBGnMAP | BG(n+1)'s tile map: byte address = value x 2 KB (anywhere in the 512 KB). |
| `$21D8+n` | WMBGnCFG | Bits 0-1 map size (0: 64 x 32 tiles, 1: 64 x 64, 2: 128 x 32, 3: 128 x 64). Bits 2-3 colour bank (entries bank x 256 to + 255). Bits 4-6 tile base in 64 KB units. |
| `$21E0+2n` | WMBGnHOFS | BG(n+1)'s horizontal scroll, written twice (low byte, then high); 0-1023. |
| `$21E1+2n` | WMBGnVOFS | Its vertical scroll, the same way. |

**Video RAM timing.** As on the SNES: writes through `WMVDATA` and `WMCGDATA` during the active picture are ignored unless the screen is
force-blanked (`INIDISP` bit 7). Write in vblank or in forced blank.

**The WideMess mode** (`WMCTRL` bit 1):
- **Eight background layers** of 8 x 8 tiles in **256 colours** (8 bits per pixel), each with its own map, tiles, colour bank and scroll.
- **Tiles:** the SNES's 8-bpp planar format, 64 bytes per tile (bit-plane pairs 0/1, 2/3, 4/5, 6/7, 16 bytes each), so the SNES tile
  converters (PVSnesLib's gfx4snes) work as they are. Colour 0 is transparent.
- **Map entries:** 16 bits.
  - bits 0-11: the tile, counted from the layer's tile base; 4,096 tiles = 256 KB;
  - bit 12: priority;
  - bit 13: reserved (0);
  - bit 14: horizontal flip;
  - bit 15: vertical flip.
- **Maps:** stored row by row: entry (x, y) is at map base + (y x width + x) x 2. A layer wraps at its map's size.
- **Scrolling:** the top visible line shows the layer's pixel row VOFS, and the left column its pixel column HOFS. Unlike the SNES modes,
  there is no one-line offset.
- **Order, front to back:**
  1. the high-priority tiles of BG1, BG2, ... BG8;
  2. the low-priority tiles of BG1 ... BG8;
  3. the backdrop: colour 0.
- **Applies:** `INIDISP`'s brightness and forced blank.
- **Sprites, windows and colour math** (milestone 2): sections 3.3 and 3.4. The sprites interleave with the 16 layer slots of the
  order above through `WMOBJPRI0-3`.

**Wide output** (`WMCTRL` bit 0):
- The picture is 400 x 224 (or 239), **square pixels**: 16:9 on a 16:9 screen.
- The WideMess mode uses all 400 columns.
- The SNES modes 0-7 are drawn 400 wide too (below, milestone 2).

### 3.1 The three pictures (milestone 2)

`WMCTRL` picks one of three pictures, line by line:

| `WMCTRL` | Picture |
|---|---|
| bits 0 and 1 clear (and always while locked) | **The SNES picture**: the SNES chip itself, 256 wide (512 half-pixels in modes 5 and 6). Nothing in `$21A0-$21BF` has any effect on it. |
| bit 1 (LAYERS8), bit 0 either way | **The WideMess mode**, 400 wide with bit 0, 256 wide without. |
| bit 0 (WIDE) without bit 1 | **A SNES mode drawn wide**: the mode `BGMODE` selects, 400 wide. |

**SNES columns and picture columns.** The SNES's registers that hold a screen position count from the **SNES picture's left edge**:
`WH0-WH3`, the X of the SNES sprite table, Mode 7's screen x, the offset-per-tile columns and the mosaic grid. In a 400-wide picture
that edge is **picture column 72**: the SNES picture is centred (72 + 256 + 72), so columns 72-327 of a wide SNES mode are exactly what
a SNES draws, and the 72 columns either side are new. In a 256-wide picture it is column 0. The WideMess registers
(`WMWH0-3`, the WideMess sprite table) count in **picture columns**, 0-399.

### 3.2 The SNES modes drawn wide (milestone 2)

`WMCTRL` = WIDE: the mode `BGMODE` selects, with every SNES register meaning what it means on a SNES, over 400 columns.
- **Layers** show the parts of their tile maps a SNES does not: picture column c shows the layer's pixel column (c - 72 + HOFS),
  wrapping at the map's width as on the SNES. A 64-tile-wide map shows the half a SNES keeps off screen; a 32-wide map repeats.
- **Offset-per-tile** (modes 2, 4, 6): the SNES's first tile column keeps its rule (never offset); the tile columns to its left continue
  the count (-1, -2, ...), so they take BG3's entries further left (BG3's map wraps).
- **Mosaic** blocks are aligned on the SNES picture's left edge.
- **Hi-res** (modes 5, 6): 800 half-pixels, averaged in pairs into 400, as the SNES chip's 256-wide picture averages 512. There is no
  sharp 800-wide picture.
- **Mode 7** continues its transformation across the 400 columns: screen x runs from -72 to 327 (M7SEL's horizontal flip makes it
  255 - x, as on the SNES), with the SNES's rules outside the plane (M7SEL bits 6-7) and EXTBG. This is bsnes-hd's widescreen Mode 7.
- **Video RAM**: the SNES modes read the first 64 KB, as always.
- **Sprites**, **windows** and **colour math**: below.

### 3.3 Sprites (milestone 2)

Two sprite tables; `WMOBJ` bit 0 picks the one the WideMess mode and the wide SNES modes draw (the 256-wide SNES picture always draws the
SNES's).

**The SNES table** (`WMOBJ` bit 0 clear): `OAM`, 128 sprites, `OBSEL`, 4 bits per pixel, colours 128-255, as on a SNES. In a 400-wide
picture a sprite's 9-bit X counts in SNES columns from -184 to 327 (X = 328-511 means X - 512): picture column 72 + X. Its limits stay
the SNES's (32 sprites and 34 slivers on a line, counted over the whole picture, the flags in `STAT77` `$213E`), and so do its priority
rotation (`OAMADD` bit 15) and its tile layout.

**The WideMess table** (`WMOBJ` bit 0 set): 256 sprites, 64 per line, 8 x 8 to 64 x 64, 4 or 8 bits per pixel, in their own 2 KB
memory (WideMess OAM), 8 bytes per sprite (sprite n at byte n x 8):

| Byte | Bits | Meaning |
|---|---|---|
| 0 | 0-7 | X, bits 0-7. |
| 1 | 0-1 | X, bits 8-9. X is a signed 10-bit **picture column** (-512 to 511): the sprite's left edge. Bits 2-7 reserved (0). |
| 2 | 0-7 | Y, bits 0-7. |
| 3 | 0-1 | Y, bits 8-9. Y is a signed 10-bit **picture row**: the sprite's top row (row 0 is the first visible line). Bits 2-7 reserved (0). |
| 4 | 0-7 | Tile, bits 0-7. |
| 5 | 0-3 | Tile, bits 8-11: counted from `WMOBJBASE` in tiles of 32 bytes (4 bpp) or 64 bytes (8 bpp). Bits 4-7 reserved (0). |
| 6 | 0-5 | Palette. 4 bpp: colours palette x 16 + 0-15 (any 16 of the 1,024). 8 bpp: colours (palette / 16) x 256 + 0-255 (a bank; bits 0-3 unused). |
| 6 | 6 | Horizontal flip. |
| 6 | 7 | Vertical flip. |
| 7 | 0-1 | Width: 8, 16, 32 or 64 pixels. |
| 7 | 2-3 | Height: 8, 16, 32 or 64 pixels. |
| 7 | 4-5 | Priority 0-3. |
| 7 | 6 | 8 bits per pixel (else 4). |
| 7 | 7 | Colour math: the sprite takes part in colour math when `CGADSUB` bit 4 is set (the SNES table uses palettes 4-7 for this). |

- **Tiles:** the SNES's planar formats: 4 bpp is 32 bytes (bit-plane pairs 0/1 and 2/3, 16 bytes apart), 8 bpp is 64 bytes (as the
  layers). Colour 0 is transparent. A sprite's tile (tx, ty), counted in 8 x 8 tiles from its top-left before flipping, is
  tile + ty x 16 + tx (mod 4,096): sprite sheets are 16 tiles (128 pixels) wide, as on the SNES.
- **Order:** a lower table index is in front of a higher one, whatever their priorities (the SNES rule). There is no priority rotation.
- **Line limits:** the first 64 sprites in table order that cover a line (and are not wholly off the picture) are drawn; their slivers
  (8-pixel tile rows inside the picture) are counted in table order and the ones past 128 are not drawn. `WMOBJSTAT` records both.
- **Hiding a sprite:** place it off the picture (for example Y = 240). WideMess OAM is all zeros at power-on and is not cleared by a reset.
- **Video timing:** like `WMVDATA`, writes through `WMOAMDATA` during the active picture are ignored unless the screen is
  force-blanked; the address advances either way.

**Priorities.**
- In the **SNES modes**, a sprite's priority 0-3 places it in the mode's own order, as on a SNES, whichever table it comes from.
- In the **WideMess mode**, the layers make 16 slots, front to back: the high-priority tiles of BG1 ... BG8, then the low-priority tiles of
  BG1 ... BG8. `WMOBJPRIn` says how many of those slots are in front of the sprites of priority n (0-16; above 16 counts as 16): 0 puts
  them in front of every layer, 16 behind every layer and in front of the backdrop. At reset: priority 3 at 0, 2 at 4, 1 at 8, 0 at 12.
- Only one sprite pixel reaches each column (the frontmost sprite's), and its priority places it.

**Sprite registers** (`$21A0-$21AF`):

| Address | Name | Meaning |
|---|---|---|
| `$21A0` | WMOBJ | Bit 0 TABLE: the WideMess table replaces the SNES's in the WideMess mode and the wide SNES modes. |
| `$21A1` | WMOBJBASE | Bits 0-2: the WideMess table's tiles start at this x 64 KB of video RAM. |
| `$21A2-$21A3` | WMOAMADDL / H | WideMess OAM byte address, 11 bits (`$21A3` bits 0-2). |
| `$21A4` | WMOAMDATA | Write: one byte at the address, then the address + 1. Read: the same, reading. DMA-capable. |
| `$21A5` | WMOBJSTAT | Read: bit 6 more than 64 of the WideMess table's sprites covered a line; bit 7 more than 128 of its slivers. Cleared at the start of each frame that is not force-blanked (as `STAT77`'s flags). |
| `$21A6+n` | WMOBJPRIn | The WideMess mode: the number of BG slots in front of sprites of priority n (n = 0-3). |
| `$21AA-$21AF` | | Reserved. |

### 3.4 Windows and colour math at 400 (milestone 2)

The SNES's two windows, its masks, its sub screen and its colour math (`CGWSEL`, `CGADSUB`, `COLDATA`) work over the whole picture, with
the SNES's rules: a window covers the picture columns from its left to its right position inclusive (none when left > right); where the
sub screen shows only its backdrop, the fixed colour is added and the result is not halved.
- **Window positions:** `WMWCTL` bit 0 set: `WMWH0-3`, 9-bit picture columns. Clear: the SNES's `WH0-WH3`, in SNES columns (picture
  column 72 + value in a 400-wide picture), except that an edge on the SNES picture's edge reaches the picture's edge: a left position
  of 0 means column 0 and a right position of 255 means column 399. So a SNES window that spans the SNES picture (0-255, as games keep
  for their colour window) spans the wide picture, and one inside it stays where the SNES draws it.
- **The wide SNES modes** use the SNES's registers exactly as a SNES does (`TS`, `W12SEL`, `W34SEL`, `WOBJSEL`, `WBGLOG`, `WOBJLOG`,
  `TMW`, `TSW`, `CGWSEL`, `CGADSUB`, `COLDATA`); only the positions come from `WMWCTL`'s choice.
- **The WideMess mode** has 8 layers, so its layer bits are WideMess registers, 8 bits for BG1-BG8 (bit n = BG n+1); the SNES's
  registers keep their meaning for the sprites, the colour window, the backdrop and the math controls:

| | BG1-BG8 | Sprites | Backdrop / colour window / controls |
|---|---|---|---|
| Main screen | `WMTM` | `TM` bit 4 | |
| Sub screen | `WMTS` | `TS` bit 4 | |
| Masked by their window, main / sub | `WMTMW` / `WMTSW` | `TMW` / `TSW` bit 4 | |
| Window select (the `W12SEL` layout) | `WMW12SEL` ... `WMW78SEL` | `WOBJSEL` bits 0-3 | colour window: `WOBJSEL` bits 4-7 |
| Window logic (the `WBGLOG` layout) | `WMWLOG14`, `WMWLOG58` | `WOBJLOG` bits 0-1 | colour window: `WOBJLOG` bits 2-3 |
| Colour math on | `WMCMATH` | `CGADSUB` bit 4 | backdrop `CGADSUB` bit 5; half bit 6; subtract bit 7; `CGWSEL`; `COLDATA` |

  `CGWSEL` bit 0 (direct colour) and `MOSAIC` have no effect in the WideMess mode.

**Window and colour-math registers** (`$21B0-$21BF`):

| Address | Name | Meaning |
|---|---|---|
| `$21B0` | WMWH0 | Window 1 left, 9 bits, written twice (the low byte, then bit 8 in bit 0). |
| `$21B1` | WMWH1 | Window 1 right, the same way. |
| `$21B2` | WMWH2 | Window 2 left. |
| `$21B3` | WMWH3 | Window 2 right. |
| `$21B4` | WMWCTL | Bit 0 WIDEWIN: the windows' positions are `WMWH0-3` (else the SNES's `WH0-WH3` in SNES columns). A write resets the four positions' byte phase. |
| `$21B5` | WMTS | The WideMess mode: BG1-BG8 on the sub screen. |
| `$21B6` | WMTMW | The WideMess mode: BG1-BG8 masked by their window on the main screen. |
| `$21B7` | WMTSW | The same on the sub screen. |
| `$21B8` | WMW12SEL | The WideMess mode: BG1's (bits 0-3) and BG2's (bits 4-7) windows: per layer bit 0 window 1 inverted, bit 1 window 1 on, bit 2 window 2 inverted, bit 3 window 2 on. |
| `$21B9` | WMW34SEL | BG3 and BG4. |
| `$21BA` | WMW56SEL | BG5 and BG6. |
| `$21BB` | WMW78SEL | BG7 and BG8. |
| `$21BC` | WMWLOG14 | How BG1-BG4 combine their two windows (2 bits each, BG1 in bits 0-1: 0 OR, 1 AND, 2 XOR, 3 XNOR). |
| `$21BD` | WMWLOG58 | The same for BG5-BG8. |
| `$21BE` | WMCMATH | The WideMess mode: colour math on BG1-BG8. |
| `$21BF` | | Reserved. |

All of `$21A0-$21BF` is 0 at reset except `WMOBJPRI0-3` (12, 8, 4, 0). The write-only registers read as open bus.

## 4. Sound (milestone 3; outline)

- **SNES drivers run unchanged.** The SPC700 and the S-DSP are the SNES's: the SPC700's 64 KB, voices 0-7, their registers and timing.
- **Once unlocked:**
  - 24 more voices (32 in all) in a second DSP register page;
  - samples in BRR or 16-bit PCM, read by the DSP from up to 1 MB of sound RAM through 20-bit sample addresses;
  - output at 32 or 48 kHz.
- **Filling the sound RAM:** the main CPU writes the 1 MB through the B-bus port at `$21F0-$21F7` (DMA-capable), so samples stream without the SPC700 copying them.
- **Details to settle in milestone 3:** the exact registers, how the SPC700 sees the extra RAM (a bank window), the mixer, and echo for the new voices.

## 5. The game file: `.wms` (milestone 1)

A 64-byte header, then the ROM image as the 65816 sees it under the header's map. Little-endian.

| Offset | Size | Field |
|---|---|---|
| `$00` | 8 | Magic `WIDEMESS` |
| `$08` | 1 | Spec major the game needs (1) |
| `$09` | 1 | Spec minor (0) |
| `$0A` | 1 | Map: 0 LoROM, 1 HiROM, 2 WideMess map (section 2) |
| `$0B` | 1 | Flags: bit 0 battery |
| `$0C` | 4 | ROM size in bytes (the payload) |
| `$10` | 4 | Battery RAM size in bytes |
| `$14` | 4 | Required features (`WMFEAT` bits the game needs) |
| `$18` | 1 | Region: 0 NTSC |
| `$19` | 3 | Reserved (0) |
| `$1C` | 4 | CRC-32 of the payload |
| `$20` | 32 | Title, ASCII, padded with spaces |
| `$40` | ... | Payload |

- For maps 0 and 1 the payload is an ordinary SNES image (with its internal header), so a PVSnesLib or WLA-DX build becomes a `.wms`
  by prepending the header: BrokenDev's `wms-pack` does it.
- BrokenNes refuses a file:
  - whose magic, size or CRC is wrong;
  - whose map disagrees with the image;
  - that needs a newer spec or a feature this console does not have.

  It says which, rather than running it.
- The extension `.wms` is provisional: the user's call (plan, open question 1).

## 6. SNES games on WideMess

- Every `.sfc` / `.smc` game boots on the WideMess console exactly as on the SNES console: the same picture, sound and timing, to the
  frame hash.
- Per-game enhancements (widescreen, no flicker, better sound) are opt-in profiles, milestone 6.

## 7. Implementation status

| Part | Milestone | In BrokenNes |
|---|---|---|
| Unlock, ID, version, features | 1 | done (`WmsExtension`) |
| `.wms` files | 1 | done (`WmsFile`, `Workshop --wms-pack`) |
| 512 KB video RAM, 1,024 colours, the 8-layer mode, wide output | 1 | done (`PPU_WMS`; test ROM `WideMess/testroms/layers`) |
| SNES modes and Mode 7 wide, sprites, windows, colour math at 400 | 2 | not yet |
| Sound | 3 | not yet |
| Fast CPU, 1 MB work RAM, the WideMess map, fast DMA | 4 | not yet |
| Debug port | BrokenDev debugging toolkit | reserved |
