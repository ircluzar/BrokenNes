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
| `$4224` | WMFEAT | R | Features this console has (bit = 1): 0 picture (wide output, 8-layer mode); 1 sprites+ (the rest of the picture, section 3: the 256-sprite table, windows and colour math at 400, the SNES modes wide); 2 sound+ (section 4: voices 8-31, 16-bit PCM, 1 MB sound RAM); 3 fast CPU; 4 1 MB work RAM; 5 fast DMA; 7 debug port. |
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

## 4. Sound (milestone 3)

**Locked, the sound unit is the SNES's.** The S-SMP (SPC700) with its 64 KB, the boot loader, the timers and the four ports; the S-DSP
with voices 0-7, their registers and their timing. A SNES sound driver runs unchanged and sounds bit-identical.

**The unlock.** The main CPU's `WMUNLOCK` (section 1) unlocks the sound unit as well: the console's unlock line reaches it. A reset locks
it again. There is no second unlock on the sound side.
- An SPC700 program detects the extensions by reading `WID` (DSP register `$9D`): `$57` once unlocked.
- On a SNES, or while locked, `$9D` reads the mirror of the S-DSP's unused register `$1D`. Write 0 to `$1D` first, so the test is
  unambiguous.

**Sound RAM: 1 MB**, addresses `$00000-$FFFFF`.
- The first 64 KB is the SPC700's RAM (the SNES's 64 KB). The SPC700, the boot loader, voices 0-7, the S-DSP's sample directory (`DIR`)
  and its echo buffer see exactly what they always saw.
- The 960 KB above it is reached by the new voices (20-bit sample addresses) and by the main CPU's port.
- The SPC700 keeps its 64 KB: it has no window on the RAM above. The main CPU fills that RAM, the DSP plays it. A driver keeps its code,
  its variables and the S-DSP's samples in the 64 KB and points the new voices at samples above.
- At power-on and reset the whole 1 MB reads 0 (BrokenNes clears it, as it clears the 64 KB).

**The main CPU's port** (B-bus, live once unlocked; DMA can write it and read it):

| Address | Name | R/W | Meaning |
|---|---|---|---|
| `$21F0-$21F2` | WMSADDL / M / H | W | Sound RAM address, 20 bits (`$21F2` bits 0-3). Reads give open bus. |
| `$21F3` | WMSDATA | R/W | Write: one byte at the address, then the address + 1 (wrapping at 1 MB). Read: the byte there, then the address + 1. |
| `$21F4-$21F7` | | | Reserved: read 0, write ignored. |

- The port covers the whole 1 MB, the SPC700's 64 KB included. A game can load SPC700 code or S-DSP samples there without the boot
  loader; it must not overwrite what a running SPC700 program uses.
- **Timing:** a byte lands at the moment the CPU or the DMA writes it; the sound unit has caught up to that moment. General DMA moves a
  byte every 8 master clocks: about 5.9 KB in a vblank (twice that with fast DMA, section 2).

**32 voices.** Voices 0-7 are the S-DSP's. Voices 8-31 are new, in three banks of 8, each bank laid out like the S-DSP's register map
in a **second DSP register page**:
- Once unlocked, DSP addresses `$80-$FF` (`$F2` with bit 7 set) are the second page. On a SNES they are read-only mirrors of
  `$00-$7F`. Addresses `$00-$7F` are untouched.
- `WBANK` chooses the bank of 8 voices the page shows. A driver that already plays the S-DSP's voices plays a bank with the same code,
  `$F2` ORed with `$80`.

**The second page** (DSP address = `$80` + offset; "bank" = the registers of the bank `WBANK` shows, "every bank" = one register seen
from all three):

| Offset | Name | | Meaning |
|---|---|---|---|
| `$x0-$x9` | VOLL ... OUTX | bank | Voice 8 + 8 x `WBANK` + x: `VOLL`, `VOLR`, `PITCHL`, `PITCHH`, `SRCN`, `ADSR1`, `ADSR2`, `GAIN`, `ENVX`, `OUTX`, as the S-DSP's `$x0-$x9`. |
| `$xA`, `$xB`, `$xE` | VxADDRL / M / H | bank | Read-only: the voice's playing address, 20 bits. BRR: the block it is in. PCM: its next 4 samples. Streaming drivers use it to stay ahead of the voice. |
| `$4C` | WKON | bank | Key-on of the bank's 8 voices (bit x = voice x of the bank), as `KON`. |
| `$5C` | WKOFF | bank | Key-off, as `KOFF`. |
| `$7C` | WENDX | bank | End flags, as `ENDX`; a write clears them. |
| `$2D` | WPMON | bank | Pitch modulation, as `PMON`. Bit 0 of bank 0 is voice 8, modulated by voice 7. |
| `$3D` | WNON | bank | Noise instead of the sample, as `NON`. |
| `$4D` | WEON | bank | Into the echo, as `EON`. |
| `$0C` | WBANK | every bank | Bits 0-1: 0 = voices 8-15, 1 = 16-23, 2 = 24-31. 3 shows no voices: their registers read 0 and ignore writes. |
| `$1D` | WID | every bank | Read-only: `$57`. |
| `$5D`, `$6D` | WDIRL, WDIRH | every bank | The new voices' sample directory: address = `WDIRH` (bits 0-3) x `$10000` + `WDIRL` x `$100`. |
| others | | | Reserved: write 0. |

**Samples.** A new voice's `SRCN` picks an entry of its directory: 256 entries of 16 bytes (4 KB, anywhere in the 1 MB on a 256-byte
boundary).

| Offset | Size | Field |
|---|---|---|
| +0 | 3 | Start address, 20 bits (byte +2 bits 0-3; its top bits are ignored). |
| +3 | 1 | Format. Bit 0: 16-bit PCM (0: BRR). Bit 1: a PCM sample loops (0: it plays once). Bits 2-7: reserved, 0. |
| +4 | 3 | Loop address. |
| +7 | 1 | Reserved. |
| +8 | 3 | End address. PCM: the address just past the last sample. BRR: ignored (the blocks' end flags say). |
| +11 | 5 | Reserved. |

- **BRR:** the S-DSP's format (9-byte blocks of 16 samples, its filters, its end and loop flags) at a 20-bit address.
- **16-bit PCM:** signed little-endian samples, read 4 at a time (8 bytes).
  - After each group, if the next address is at or past the end, the voice goes to the loop address and sets its `WENDX` bit. Without the
    loop bit it also goes silent, as after a BRR end block without the loop flag.
  - A sample's length and its loop point are whole groups of 4 samples. A sample must not cross the end of the 1 MB.
- **The DSP reads an entry when it needs it:** the start and the format at key-on, the end after each PCM group, the loop at the end. A
  streaming driver can move a playing sample's end.
- **Both formats go through the S-DSP's pipeline:** the 4-point Gaussian interpolation, the 14-bit pitch (up to 4x: a 32 kHz sample up
  to 128 kHz), the ADSR and GAIN envelopes, the volumes. As on the S-DSP, the interpolation's 16-bit partial sum can wrap within a few
  steps of full scale: keep PCM samples within about ±32,700.

**The mix.**
- The 32 voices add into the S-DSP's main and echo sums in voice order, 0 to 31, with the S-DSP's 16-bit saturation after each voice.
  32 loud voices clip: the driver keeps the sum in range, as on the S-DSP with 8.
- Then the S-DSP's own stages apply to the whole sum: `MVOL`, the echo (`EVOL`, `EFB`, the FIR, the buffer in the first 64 KB), and
  `FLG`'s mute. One stereo output.
- `FLG`'s soft reset keys off all 32 voices, and its noise clock is the one noise generator they all use.
- Pitch modulation runs down the chain 0 to 31.

**Output: 32 kHz.** 48 kHz was considered and dropped. The S-DSP's envelope rates, echo delay and noise, and the SPC700's timers, all
count 32 kHz samples, so a 48 kHz WideMess would no longer be a SNES on voices 0-7. The hosts resample, as they do for the SNES.

**Believable 1994-96 hardware.** The S-DSP's design at 4 times its clock (12.288 MHz, from the same 24.576 MHz crystal) serves 32 voices
in the same 32 kHz period. The 1 MB of sound RAM is DRAM; 24 PCM voices at 4x pitch read about 6 MB/s. A register write reaches its voice
at the next sample, as with BrokenNes's S-DSP.

**Save states** carry the sound RAM above 64 KB, the second page and the new voices.

**Detection:** `WMFEAT` bit 2.

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
| SNES modes and Mode 7 wide, sprites, windows, colour math at 400 | 2 | done (`PPU_WMS`'s wide compositor, `WideMess.Picture.cs`; test ROMs `WideMess/testroms/picture`; `WMFEAT` bit 1) |
| Sound: 32 voices, BRR + 16-bit PCM, 1 MB sound RAM, the port | 3 | done (`APU_WMS`, `DSP_SFC.WideMess.cs`; test ROM `WideMess/testroms/sound`) |
| Fast CPU, 1 MB work RAM, the WideMess map, fast DMA | 4 | done (`BOARD_SFC.WideMess.cs`, `WideMess.Memory.cs`; test ROMs `WideMess/testroms/cpu`) |
| Debug port | BrokenDev debugging toolkit | reserved |
