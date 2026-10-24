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
| `$4224` | WMFEAT | R | Features this console has (bit = 1): 0 picture (wide output, 8-layer mode); 1 sprites+ ; 2 sound+ ; 3 fast CPU; 4 1 MB work RAM; 5 fast DMA; 7 debug port. |
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
- **Milestone 2 adds** sprites, windows and colour math to this mode.

**Wide output** (`WMCTRL` bit 0):
- The picture is 400 x 224 (or 239), **square pixels**: 16:9 on a 16:9 screen.
- The WideMess mode uses all 400 columns.
- The SNES modes 0-7 in wide, and Mode 7 in wide, are milestone 2. Until then WIDE without LAYERS8 draws the SNES picture at 256, as a SNES.

**Sprites** (milestone 2):
- 256 sprites, 64 per line;
- sizes 8 x 8 to 64 x 64;
- 4 or 8 bits per pixel;
- X positions over 400.

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
