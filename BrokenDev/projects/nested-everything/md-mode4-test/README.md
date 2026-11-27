# Mode 4 on the Genesis VDP, in Genesis mode (hardware test ROM)

This ROM answers one question for Nested SMS -> Genesis: will the Genesis VDP draw SMS Mode 4 when the 68000 clears
register 1 bit 2 (M5)? It runs as a normal Genesis cartridge, with no SMS-mode reset from the cart or a Power Base Converter.

ROM: `md_mode4_test.md` (128 KB, a plain Genesis cart that passes TMSS). Build: `build.ps1` (uses SGDK 2.11's m68k `as`/`ld` and Python).
`run-bizhawk.ps1` runs it on BizHawk Genplus-gx and saves screenshots.

## Buttons
- **A** or **C**: next screen
- **B**: previous screen
- **Start**: run the current screen again (it reloads the VDP from scratch)
- **Up/Down** (screen 3 only): move the V scroll

Please take a photo of each screen. A photo of screens 3 and 4 is the most useful, because they show numbers.

## Screen 1: Mode 5 sanity (proves the ROM boots)
```
  SCREEN 1  MODE 5 SANITY          white text on dark blue
  GENESIS MODE 5 BOOTS OK
  MODE 5 COLOURS 0-15:
  [16 colour bars: blue white yellow red orange ... grey]
 STATUS  xxxx
 HV X4   xxxx xxxx xxxx xxxx       4 HV counter reads in a row
 ST VINT xxxx
 HV VINT xxxx                     BizHawk/BrokenNes: E0xx (V = 224)
 HV HINT 0000  N 0000
 FRAMES  xxxx                     counts up
 A/C NEXT  B BACK  START RERUN
```
These numbers are the Mode 5 baseline to compare with screen 4.

## Screen 2: Mode 4, loaded from the 68000 while in Mode 4 (method A, the one Nested would use)
```
  SCREEN 2  MODE 4 FROM MD         white text on blue
  MODE 4 OK                        <- line A
 SWAPPED BYTES                     <- line B (byte-order probe)
  COLOURS 1-15:
  [15 bars: white white red orange ... grey]
  8 BALLS MOVE RIGHT BELOW
  o  o  o  o  o  o  o  o           8 yellow/orange balls sliding right
  FRAME xxxx                       counts up = frame interrupt works in Mode 4
  A/C NEXT  B BACK  START RERUN
```
- The picture is 256x192, inside a grey-red border (the border uses sprite colour 0). Please say whether the border shows above and below the picture, or whether the picture fills the screen.
- **Only one of lines A and B can be readable.** Line A readable means the VDP puts the LOW byte of a 68000 word at the even SMS address (the Genesis Plus GX order). Line B readable (as "SWAPPED BYTES") means the HIGH byte goes there. The unreadable line shows `!` shapes or garbage, and that is expected. If B is the readable one, the rest of the screen (font, bars, balls) will be scrambled too, but the photo still answers the question.
- Total garbage, a black screen or a frozen screen means Mode 4 does not work from Genesis mode (or not this way).

## Screen 3: Mode 4 line interrupt (HInt) and scrolling
```
 SCREEN 3  MODE 4 HINT+SCROLL      rows 0-3 never scroll sideways
 HINTS/FRAME  0006                 BizHawk: 0006
 V:  1F 3F 5F 7F 9F BF 00 00       V counter at each HInt (BizHawk: 1F 3F 5F 7F 9F BF)
 VSCROLL  00                       Up/Down changes it
 [coloured vertical stripes, cut into bands 32 lines tall; each band slides sideways at its own speed]
     UP/DOWN MOVES V SCROLL
```
- The line counter is 31, so an HInt should fire every 32 lines. Each HInt writes the H-scroll register, so the stripes shift band by band. If the stripes move as one block, HInt does not fire in Mode 4.
- Up/Down moves the whole picture up and down (Mode 4 vertical scroll).

## Screen 4: what the VDP reports in Mode 4
```
  SCREEN 4  MODE 4 READ-BACK
 STATUS  xxxx                      status read in the main loop
 HV X4   xxxx xxxx xxxx xxxx       H = low byte. If all four are equal, the H counter is frozen
 ST VINT xxxx
 HV VINT xxxx                      high byte = line of the frame interrupt (BizHawk: C0 = 192)
 HV HINT xxxx  N xxxx              HInt every 100 lines: BizHawk 63xx, N 0001
  VRAM RD 1234 ABCD  WANT 1234 ABCD   VRAM written then read back through the data port
 FRAMES  xxxx
```

## Screen 5: Mode 4 with VRAM preloaded in Mode 5 (method B, the fallback)
This is the same picture as screen 2 without the balls and the counter. Everything is written while still in Mode 5,
to the addresses the VDP's Mode 4 address remap reads from (as in Charles MacDonald's 2013 test). The ROM then switches
to Mode 4. Only the palette is written in Mode 4. If screen 2 is garbage but screen 5 is right, Mode 4 works, but
only when VRAM is loaded the Mode 5 way.

## What to ask on the real console
1. Screen 1: does it show? (If not, the ROM or cart is the problem, not Mode 4.)
2. Screen 2: which line is readable, "MODE 4 OK" or "SWAPPED BYTES"? Are the colour bars clean? Do 8 balls move? Does FRAME count up? Is there a border above and below the picture?
3. Screen 3: the HINTS/FRAME value, the 8 V values, and whether the stripes move in separate bands. Does Up/Down scroll the picture?
4. Screen 4: a photo of all the numbers.
5. Screen 5: same questions as screen 2.
6. After visiting screens 2-5, does screen 1 still come back clean (press C or A to get there)?

## Emulators
- **BizHawk 2.11.1 Genplus-gx** shows garbage on screens 2, 3 and 5. The VRAM domain shows why: the data is in VRAM in the remapped (physical) layout, but this core reads it as linear, for example the colour bars land at the top row. Current Genesis Plus GX source has a dedicated Mode 4 68000 write path, and it converts VRAM when M5 is switched. BizHawk's build of the core does not seem to do either. So BizHawk is not a valid check for the picture. Its interrupts and counters still work, read from 68000 RAM: frame interrupt at V=$C0 (192 lines), 6 HInts per frame at V $1F $3F $5F $7F $9F $BF, HV H byte frozen in Mode 4 (as Genesis Plus GX models it), VRAM read-back 1234 ABCD correct.
- **BrokenNes's Genesis core** (Mode 5 only) shows screen 1 correctly. Screens 2-5 are one flat orange-red colour, with no tiles and no sprites. It keeps the Mode 5 timing: frame interrupt at V=$E0, 7 HInts at $1F...$DF. VRAM read-back gives 0000 CDAB: it uses Mode 5 addressing, so the first word is lost and the second is byte-swapped.

## Sources
- Charles MacDonald, SpritesMind "VDP odds and ends" (March 2013): https://gendev.spritesmind.net/forum/viewtopic.php?p=19131. He tested on hardware: Mode 4 draws from the 68000, every command is 16-bit, there is no DMA, VSRAM or CRAM reads, auto-increment must be $01, regs 11+ are writable only in Mode 5, only 16 KB of VRAM is used and its addresses are remapped, H40 shows junk at the right edge so use H32, interlace is unusable, and interrupts are acknowledged only by reading status.
- Genesis Plus GX `core/vdp_ctrl.c` and `core/vdp_render.c` (github.com/ekeeke/Genesis-Plus-GX): `vdp_68k_data_w_m4`, the 64K<->16K VRAM conversion on an M5 switch, regs 11-23 locked in Mode 4, the frozen H counter, and "TODO: check how interrupts are handled in Mode 4".
- Sega Retro, VDP registers / Mode 4 (M5 = 0 selects Mode 4).
