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
| `$4225` | WMSPEED | R/W | Bit 0: fast CPU (section 2). 0 at reset. |
| `$4226` | WMDMA | R/W | Bit 0: fast general DMA (section 2). 0 at reset. |
| `$4227-$422F` | | | Reserved: read 0, write ignored. |
| `$4230-$423F` | | | Reserved for the BrokenDev debug port (`docs/projects/brokendev-debugging-plan.md`). |

Detection, in a game: write `$57`, `$4D` to `$4220`, then read `$4221`. `$57` means WideMess; check `WMFEAT` for each feature you use.

## 2. CPU and memory (milestone 4)

**What needs the unlock.** The registers do: `WMSPEED`, `WMDMA` and the 20-bit WRAM port are live only once unlocked, and a reset locks them
again (SNES speed, normal DMA, a 17-bit port). The WideMess map does not: it is the cartridge's wiring, so on a map-2 cartridge banks `$70-$7D`
are work RAM from power-on, as a HiROM cartridge's ROM is at `$C0` from power-on. A SNES game is never on map 2: that map exists only in
`.wms` files.

**The 65816 at SNES speed** at power-on. **Fast mode** (`WMSPEED` bit 0):
- Every CPU access and internal cycle takes 2 master clocks (10.74 MHz, the SA-1's speed). That covers work RAM, ROM, battery RAM, the
  expansion areas (`$2000-$20FF`, `$2200-$3FFF`, `$4400-$5FFF` of banks `$00-$3F` / `$80-$BF`) and open bus.
- **Except the I/O registers**, which keep their SNES timing: the B bus `$2100-$21FF` 6 master clocks, the old joypad ports `$4000-$41FF`
  12, the CPU registers `$4200-$43FF` 6 (WideMess's own `$4220-$423F` included).
- `MEMSEL` (`$420D`) does nothing in fast mode: ROM takes 2 clocks either way. Its value is kept and counts again when fast mode is turned off.
- **Unchanged:** the DRAM refresh (40 master clocks a line, which the CPU waits for), DMA and HDMA (they take master clocks, not CPU cycles),
  the picture, the sound, the H/V counters and IRQs, the auto joypad read.
- **Open bus** is the SNES's: the last value on the data bus. A 2-clock cycle changes when the bus is driven, not what it holds; nothing decays.
- The switch takes effect at the access after the write to `WMSPEED`.
- **What it buys:** code that touches only ROM, work RAM and internal cycles runs 3 to 4 times faster (6 or 8 clocks a cycle become 2); code
  that reads I/O registers gains less. Test ROM `WideMess/testroms/cpu/speed.asm` counts two loops a frame:

  | Loop | Its cycles | SNES (SlowROM) | Fast | Speed-up |
  |---|---|---|---|---|
  | A: `inc dp` + `lda dp` + `beq` (16-bit) | 6 ROM, 6 work RAM, 2 internal | 108 clocks | 28 | 3.86x |
  | B: loop A + `lda $4214` (16-bit) | + 3 ROM, 2 I/O | 144 clocks | 46 | 3.13x |

**1 MB of work RAM** (on the WideMess map):
- **Work-RAM addresses** run from `$00000` to `$FFFFF`. `$00000-$1FFFF` are the SNES's 128 KB (`$7E:0000-$7F:FFFF`), `$20000-$FFFFF` are
  banks `$70-$7D`: bank `$70 + n` is work-RAM bank `n + 2`, i.e. work-RAM bank = (bank + 2) AND `$0F`. Example: `$7B:4000` is work-RAM
  address `$D4000`. The SNES's 128 KB come first so that SNES code keeps meaning what it meant (`$2183 = $00` is still `$7E`).
- **On the A bus,** banks `$70-$7F` are all work RAM: 1 MB in one block, in address order from `$70:0000` to `$7F:FFFF`. 8 master clocks an
  access, as `$7E-$7F` (2 in fast mode).
- `$00-$3F` / `$80-$BF:0000-1FFF` still mirror `$7E:0000-1FFF`.
- **The WRAM port** `$2180-$2183`: once unlocked, `$2181-$2183` hold a 20-bit work-RAM address (`$2183` bits 0-3). The port steps through all
  1 MB and wraps from `$FFFFF` to `$00000`. Before the unlock it is the SNES's 17 bits (`$2183` bit 0).
- **DMA** reaches the new RAM on the A bus like any memory.
- Cartridges with a LoROM or HiROM map keep the SNES's 128 KB and its map: `$70-$7D` are the cartridge's (LoROM battery RAM, HiROM ROM),
  and the WRAM port keeps 17 bits, unlocked or not.

**The WideMess map** (game-file map 2):
- **ROM:** 64 KB to 4 MB, in whole 64 KB banks, at `$C0-$FF`. HiROM-style mirrors: `$40-$6F` (= `$C0-$EF`), and `$00-$3F` / `$80-$BF:8000-FFFF`
  (the upper halves of ROM banks 0-63). A smaller ROM repeats. The 65816's vectors are ROM bytes `$FFE0-$FFFF` (`$00:FFE0`). An internal
  SNES header at `$FFC0` is optional and not read. Access time as on the SNES: `MEMSEL` decides `$80-$BF:8000-FFFF` and `$C0-$FF` (6 or 8
  clocks), the rest is 8 (2 in fast mode).
- **Battery RAM:** the size in the `.wms` header, 0 or a power of two from 2 KB to 256 KB. It sits at `$20-$3F` / `$A0-$BF:6000-7FFF`,
  8 KB per bank: bank `$20` holds bytes `$0000-$1FFF`, bank `$3F` the last 8 KB of 256 KB. A smaller size repeats. The header's battery flag
  makes it the game's save, and needs battery RAM. 8 clocks an access (2 in fast mode).
- **Work RAM** at `$70-$7F` (above).
- **No cartridge chips** on this map.
- Everything else (`$00-$1F` / `$80-$9F:6000-7FFF`, the system area's unused addresses) is open bus.

**Fast DMA** (`WMDMA` bit 0):
- General DMA moves a byte every 4 master clocks instead of 8. The 8 clocks that start a transfer and each channel stay 8.
- About 12 KB fits in a vblank instead of 6, enough to scroll a 400-wide screen. A vblank has 37 lines of 1,324 clocks free of refresh
  (48,988): 6,123 bytes at 8 clocks, 12,247 at 4.
- HDMA keeps its timing.
- It is independent of fast mode: fast mode alone leaves DMA at 8 clocks a byte.

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
a SNES draws, and the 72 columns either side are new. (The one exception is a line where the SNES sprite table runs over its limits:
they count sprites over the whole picture, section 3.3, so a SNES may drop different ones.) In a 256-wide picture it is column 0. The WideMess registers
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
| `$10` | 4 | Battery RAM size in bytes (map 2; maps 0 and 1 take theirs from the SNES header) |
| `$14` | 4 | Required features (`WMFEAT` bits the game needs) |
| `$18` | 1 | Region: 0 NTSC |
| `$19` | 3 | Reserved (0) |
| `$1C` | 4 | CRC-32 of the payload |
| `$20` | 32 | Title, ASCII, padded with spaces |
| `$40` | ... | Payload |

- For maps 0 and 1 the payload is an ordinary SNES image (with its internal header), so a PVSnesLib or WLA-DX build becomes a `.wms`
  by prepending the header: BrokenDev's `wms-pack` does it.
- For map 2 the payload is the ROM as section 2 maps it (64 KB to 4 MB, whole 64 KB banks; the vectors at `$FFE0-$FFFF` of its first bank),
  and the header's battery RAM size and battery flag are the cartridge's.
- BrokenNes refuses a file:
  - whose magic, size or CRC is wrong;
  - whose map disagrees with the image (map 0 or 1 when the image is the other kind);
  - on map 2, whose ROM or battery RAM the map cannot hold, or with a battery and no battery RAM;
  - that needs a newer spec or a feature this console does not have.

  It says which, rather than running it.
- The extension `.wms` is provisional: the user's call (plan, open question 1).

## 6. SNES games on WideMess (milestone 6)

- Every `.sfc` / `.smc` game boots on the WideMess console exactly as on the SNES console: the same picture, sound and timing, to the
  frame hash.
- **Enhancements are the console's, never the game's.** The player switches them on for a game; the game never does, and never
  sees one. The extension stays locked, so every register, memory and flag the game reads is what a SNES gives it. That includes
  `STAT77`'s sprite flags, which keep counting the SNES's limits on the SNES's 256 columns. Fast CPU is the one exception, by its
  nature: it changes the game's timing.
- **Only a locked SNES game is enhanced.** A `.wms` game gets none, and a SNES game that unlocks the extensions loses them at the
  unlock: from then on the picture, sound and speed are the game's to set.
- Everything off is the SNES, frame for frame (the identity check of section 6's proof).

### 6.1 The four enhancements

**Widescreen** (needs a profile): the picture is 400 x 224.
- **The centre is the SNES's picture.** Columns 72-327 are exactly the SNES's, line for line, sprites and flags included.
- **The sides** are the 72 columns either side, drawn from what the game keeps in video RAM. The game's profile gives each layer a
  rule (BG1-BG4; Mode 7's plane is BG1, with BG2 for EXTBG):
  - **extend**: the SNES modes drawn wide, section 3.2. The layer's tile map continues past the SNES edges and wraps at the map's
    width, as on the SNES: a 64-tile map shows the half the SNES keeps off screen, a 32-tile map repeats. Mode 7 continues its
    transformation.
  - **repeat**: the layer's own 256 visible columns repeat side by side. Picture column c shows the layer's column
    72 + ((c - 72) mod 256). For a 32-tile map this is the same as extend; for a 64-tile map it hides the off-screen half.
  - **fill**: the layer is transparent in the sides, so the layers behind it, or the backdrop, show there.
- **Sprites** follow one of two rules:
  - **extend**: the SNES's sprite pass draws the centre unchanged: 32 sprites and 34 slivers, counted on the SNES's 256 columns.
    The parts of sprites that fall in the side columns are drawn in addition and never counted. Their X follows section 3.3:
    9-bit X 0-327 is picture column 72 + X, and 328-511 is X - 512.
  - **fill**: no sprites in the sides. Use it for games that cull their objects at the SNES edge, or that park unused sprites
    off the left or right.
- **Masks** override the rules for part of the sides, for the things a game lays out for 256 columns: a HUD, the columns where
  it streams new tiles, a title screen. A mask is a rectangle:
  - rows `top`-`bottom`;
  - a band of the sides, `from`-`to` pixels out from the SNES edge (0 is the column next to it, 72 the picture's edge), on the
    left, the right or both sides;
  - the layers it covers;
  - a rule: fill or repeat, or **black**, which draws those columns black, every layer and the backdrop.
  Masks can never touch the centre. A later mask in the list wins over an earlier one.
- **Windows and colour math** follow section 3.4: a SNES window edge on the SNES picture's edge (0 or 255) reaches the picture's
  edge; the sub screen and the fixed colour cover the sides as they cover the centre.
- **What no rule can fix** is written in the profile and shown to the player, never hidden:
  - objects the game creates only as they reach the SNES edge (enemies that pop in at column 72 or 327);
  - tiles the game streams in just past the SNES edge (old tiles beyond them until the game scrolls further);
  - effects timed for 256 columns.

**No sprite flicker**: the SNES sprite table drawn with WideMess's own line limits, 64 sprites and 128 slivers (section 3.3),
instead of the SNES's 32 and 34.
- The game still reads the SNES's `STAT77` flags.
- A line within the SNES's limits is drawn exactly as on the SNES. Only a line over them shows more.
- A game that flickers on purpose, re-ordering its sprites every frame so that each gets its turn, keeps flickering: the console
  cannot know which sprites the game left out.
- It works with or without widescreen (256 wide without).

**Better sound**: the S-DSP's voices interpolate their samples with a 4-point cubic (Catmull-Rom) instead of the S-DSP's Gaussian
filter, which dulls the high frequencies.
- The samples, pitches, envelopes, echo and mixing are unchanged.
- More voices cannot be added to existing music: the game's driver plays 8.
- At a pitch of exactly 1.0 the cubic gives each sample as it is, where the Gaussian blurs 3 of them together.

**Fast CPU**: section 2's fast mode, forced on by the console while the game stays locked.
- Every CPU access takes 2 master clocks except the I/O registers.
- It removes slowdown and can break games that time things by counting CPU cycles. A profile marks it safe, risky or broken.

### 6.2 Profiles

**Where they live.** `WideMess/profiles/<name>.json`, one file per game. The desktop app and Workshop ship the folder beside the
program (`WideMess\profiles`).

**How a game finds its profile.** By the SHA-1 of the ROM image. A 512-byte copier header is not part of the image, so the key is
the No-Intro hash. A profile can list several hashes (revisions).

**The format** (`widemess-profile-1`). Every key is checked, and a file with an unknown key, a bad value or a hash another
profile already claims is refused with the reason. It is never half-read.

```json
{
  "format": "widemess-profile-1",
  "title": "Super Mario World",
  "sha1": ["6b47bb75d16514b6a476aa0c73a683a2a4c18765"],
  "widescreen": {
    "layers": { "bg1": "extend", "bg2": "extend", "bg3": "fill", "bg4": "extend", "sprites": "extend" },
    "masks": [
      { "why": "the status bar is laid out for 256 columns", "layers": ["bg3"], "rows": [0, 38], "side": "both", "from": 0, "to": 72, "rule": "fill" }
    ]
  },
  "enhancements": {
    "widescreen": "good",
    "noFlicker": "good",
    "betterSound": "good",
    "fastCpu": "risky"
  },
  "recommended": ["widescreen", "noFlicker"],
  "issues": ["Enemies appear at the SNES edge: the game creates them as they reach it."],
  "verified": { "date": "2026-10-24", "how": "contact sheet m6/smw.png; centre identical over 3,000 frames" }
}
```

- `widescreen` is required when `enhancements.widescreen` is anything but `"unavailable"`.
- In `enhancements`, each of the four is one of:
  - `good`: verified;
  - `risky`: works, with the issues listed;
  - `broken`: verified not to work, and still offered, labelled;
  - `unavailable`: not offered for this game, for example widescreen in a game whose sides are garbage.
- `recommended` is what "Use the profile" switches on.
- `issues` are shown to the player as they are written.

**Without a profile:**
- widescreen is not offered, and the menu says "no profile for this game";
- no flicker, better sound and fast CPU are generic and can be switched on, marked "not verified for this game";
- a broken profile file is reported with its reason, never skipped.

### 6.3 In BrokenNes

**The desktop app**: Console > **WideMess enhancements**, for a SNES game on the WideMess console.
- The game's profile: its title and when it was verified, "no profile for this game", or "the profile is broken: why".
- **Use the profile**: switches on what the profile recommends.
- One item per enhancement (widescreen, no sprite flicker, better sound, fast CPU), each labelled with the profile's verdict. Fast
  CPU always says it changes timing.
- **Known issues...**: the profile's list.
- The choices are kept per game in the app's settings and applied when the game starts again.

**The HTTP API** (for tests and tools; it changes only the running game, not the settings file):
- `GET /api/widemess/enhance`: the game's profile (or why there is none), what is on, and what is offered.
- `POST /api/widemess/enhance` with `{ "widescreen", "noFlicker", "betterSound", "fastCpu", "useProfile" }` (each optional):
  switches them. A refusal says why, for example widescreen without a profile.
- `POST /api/widemess/profile` with a profile's JSON: a scratch profile for the running game, checked like a file, held in memory
  only.

**Workshop:**
- `--wms-enhance-sheet --rom <game> --out <png> [--input <script>] [--at <frames>]`: the contact sheet, the SNES picture beside
  WideMess widescreen at several moments of play.
- `--wms-test` checks every enhancement against a negative control:
  - each switched off gives the SNES;
  - widescreen keeps the centre identical;
  - no flicker changes only the lines over the SNES's limits;
  - better sound changes the sound only when on;
  - fast CPU runs the speed ROM at fast mode's speed with the game locked.

## 7. Implementation status

| Part | Milestone | In BrokenNes |
|---|---|---|
| Unlock, ID, version, features | 1 | done (`WmsExtension`) |
| `.wms` files | 1 | done (`WmsFile`, `Workshop --wms-pack`) |
| 512 KB video RAM, 1,024 colours, the 8-layer mode, wide output | 1 | done (`PPU_WMS`; test ROM `WideMess/testroms/layers`) |
| SNES modes and Mode 7 wide, sprites, windows, colour math at 400 | 2 | done (`PPU_WMS`'s wide compositor, `WideMess.Picture.cs`; test ROMs `WideMess/testroms/picture`; `WMFEAT` bit 1) |
| Sound | 3 | not yet |
| Fast CPU, 1 MB work RAM, the WideMess map, fast DMA | 4 | done (`BOARD_SFC.WideMess.cs`, `WideMess.Memory.cs`; test ROMs `WideMess/testroms/cpu`) |
| Debug port | BrokenDev debugging toolkit | reserved |
