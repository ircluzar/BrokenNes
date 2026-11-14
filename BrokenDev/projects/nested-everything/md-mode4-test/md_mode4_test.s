| md_mode4_test.s - Genesis / Mega Drive hardware test: does the VDP draw SMS Mode 4 when the 68000 clears
| register 1 bit 2 (M5) in normal Genesis mode (no cartridge M3 / Power Base Converter reset)?
| GNU as (m68k, SGDK 2.11's binutils 2.40). Build: build.ps1. Screens: see README.md.
|
| Mode 4 rules used here (Charles MacDonald, SpritesMind "VDP odds and ends", 2013; Genesis Plus GX core/vdp_ctrl.c):
|  - every control-port word is a whole command in Mode 4 ($4000|addr VRAM write, $C000|idx CRAM write, $0000|addr read,
|    $8rvv register write); no DMA, no VSRAM; VRAM access is word-only; 16 KB of VRAM is used
|  - registers 11-23 cannot be written in Mode 4: auto-increment (reg 15) = 1 and H32 (reg 12) are set in Mode 5 first
|  - CRAM word = SMS colour in bits 0-5 (--BBGGRR); with increment 1 the CRAM index steps by 2, so each colour gets its own command
|  - interrupts are acknowledged only by reading the status port

VDP_DATA = 0xC00000
VDP_CTRL = 0xC00004
VDP_HV   = 0xC00008

| RAM
frames    = 0xFF0000
vbl       = 0xFF0002
m4        = 0xFF0003
screen    = 0xFF0004
hint_n    = 0xFF0006
hint_last = 0xFF0008
stat_vint = 0xFF000A
hv_vint   = 0xFF000C
hv_hint   = 0xFF000E
pad       = 0xFF0010
padprev   = 0xFF0011
padnew    = 0xFF0012
vscroll   = 0xFF0013
xpos      = 0xFF0014
vram_rb   = 0xFF0016
hint_v    = 0xFF0020
hexbuf    = 0xFF0040
hint_cnt  = 0xFF0050
m4b       = 0xFF0052
cur       = 0xFF0054

    .text
    .org 0
    dc.l 0x00FFFE00, start
    .rept 22
    dc.l fault
    .endr
    dc.l irq_nop, irq_nop, irq_nop, irq_nop     | 24 spurious, 25-27 levels 1-3
    dc.l hint                                   | 28 level 4 = HInt
    dc.l irq_nop                                | 29 level 5
    dc.l vint                                   | 30 level 6 = VInt
    dc.l irq_nop                                | 31 level 7
    .rept 32
    dc.l fault
    .endr

    .org 0x100
    .ascii "SEGA MEGA DRIVE "
    .ascii "(C)BRKN 2026.NOV"
    .ascii "NESTED MD MODE 4 TEST                           "
    .ascii "NESTED MD MODE 4 TEST                           "
    .ascii "GM 00000000-00"
    dc.w 0
    .ascii "J               "
    dc.l 0, 0
    dc.l 0x00FF0000, 0x00FFFFFF
    .ascii "            "
    .ascii "            "
    .ascii "                                        "
    .ascii "JUE             "

    .org 0x200
fault:
    move.w #0x2700,sr
    bra.s fault
irq_nop:
    rte

start:
    move.w #0x2700,sr
    move.b 0xA10001,d0
    andi.b #0x0F,d0
    beq.s 1f
    move.l #0x53454741,0xA14000            | TMSS: 'SEGA'
1:  lea 0xFF0000,a0
    move.w #0x3F7F,d0                      | clear RAM below the stack
2:  clr.l (a0)+
    dbra d0,2b
    lea 0xFFFE00,sp
    move.w VDP_CTRL,d0
    move.b #0x9F,0xC00011                   | PSG silent
    move.b #0xBF,0xC00011
    move.b #0xDF,0xC00011
    move.b #0xFF,0xC00011
    move.b #0x40,0xA10009                   | pad 1: TH output
    move.b #0x40,0xA10003
    move.w #1,screen
    bsr enter_screen

main:
    move.w #0x2300,sr
1:  tst.b vbl
    beq.s 1b
    move.w #0x2700,sr
    clr.b vbl
    bsr read_pad
    move.b padnew,d0
    btst #7,d0                              | Start: run this screen again
    bne.s 5f
    btst #6,d0                              | A: next
    bne.s 3f
    btst #5,d0                              | C: next
    bne.s 3f
    btst #4,d0                              | B: back
    beq.s 6f
    subq.w #1,screen
    bne.s 5f
    move.w #5,screen
    bra.s 5f
3:  addq.w #1,screen
    cmpi.w #6,screen
    bne.s 5f
    move.w #1,screen
5:  bsr enter_screen
    bra main
6:  bsr screen_update
    bra main

| ---------------------------------------------------------------- interrupts
vint:
    movem.l d0-d1/a0,-(sp)
    move.w VDP_HV,d1
    move.w VDP_CTRL,d0                      | acknowledge (Mode 4 needs it)
    move.w d0,stat_vint
    move.w d1,hv_vint
    addq.w #1,frames
    move.w hint_n,hint_last
    clr.w hint_n
    cmpi.w #3,screen
    bne.s 1f
    move.w #0x8800,VDP_CTRL                 | screen 3: top band H scroll 0
1:  st vbl
    movem.l (sp)+,d0-d1/a0
    rte

hint:
    movem.l d0-d1/a0,-(sp)
    move.w VDP_HV,d1
    move.w VDP_CTRL,d0                      | acknowledge
    addq.w #1,hint_cnt
    move.w hint_n,d0
    bne.s 1f
    move.w d1,hv_hint
1:  cmpi.w #8,d0
    bhs.s 2f
    lea hint_v,a0
    ror.w #8,d1
    move.b d1,(a0,d0.w)                     | V counter at this HInt
2:  addq.w #1,d0
    move.w d0,hint_n
    cmpi.w #3,screen
    bne.s 3f
    move.w frames,d1                        | band n scrolls at speed n
    lsr.w #1,d1
    mulu d0,d1
    andi.w #0xFF,d1
    ori.w #0x8800,d1
    move.w d1,VDP_CTRL
3:  movem.l (sp)+,d0-d1/a0
    rte

| ---------------------------------------------------------------- pad: d0 bits 0 U 1 D 2 L 3 R 4 B 5 C 6 A 7 Start
read_pad:
    move.b #0x40,0xA10003
    nop
    nop
    move.b 0xA10003,d0
    andi.b #0x3F,d0
    move.b #0x00,0xA10003
    nop
    nop
    move.b 0xA10003,d1
    andi.b #0x30,d1
    lsl.b #2,d1
    or.b d1,d0
    not.b d0
    move.b pad,d1
    move.b d0,pad
    not.b d1
    and.b d0,d1
    move.b d1,padnew
    rts

| ---------------------------------------------------------------- VRAM address helpers (d0 = address)
setw:                                       | VRAM write at d0 in the current mode
    tst.b m4b
    beq.s 0f
    move.w d0,cur                           | method B: vput writes each word itself
    rts
0:  tst.b m4
    bne.s 1f
    move.l d1,-(sp)
    move.w d0,d1
    andi.w #0x3FFF,d1
    ori.w #0x4000,d1
    swap d1
    move.w d0,d1
    rol.w #2,d1
    andi.w #3,d1
    move.l d1,VDP_CTRL
    move.l (sp)+,d1
    rts
1:  move.l d1,-(sp)
    move.w d0,d1
    andi.w #0x3FFF,d1
    ori.w #0x4000,d1
    move.w d1,VDP_CTRL
    move.l (sp)+,d1
    rts

| write d0 at the current VRAM address. Method B (screen 5): still in Mode 5, each Mode 4 word goes to the Mode 5 address
| that the VDP's Mode 4 address remap reads it from (Charles MacDonald 2013; Genesis Plus GX's 64K->16K decoding):
| P = (A & 0x3C00) | ((A & 0x1FE) << 1) | ((A >> 8) & 2)
vput:
    tst.b m4b
    bne.s 1f
    move.w d0,VDP_DATA
    rts
1:  movem.l d1-d2,-(sp)
    move.w cur,d1
    move.w d1,d2
    andi.w #0x3C00,d2
    move.w d2,-(sp)
    move.w d1,d2
    andi.w #0x1FE,d2
    add.w d2,d2
    or.w (sp)+,d2
    lsr.w #8,d1
    andi.w #2,d1
    or.w d1,d2
    ori.w #0x4000,d2                        | Mode 5 VRAM write command (address < 0x4000)
    swap d2
    clr.w d2
    move.l d2,VDP_CTRL
    move.w d0,VDP_DATA
    addq.w #2,cur
    movem.l (sp)+,d1-d2
    rts

| name-table address of row d0, column d1 -> d0
ntaddr:
    lsl.w #5,d0
    add.w d1,d0
    add.w d0,d0
    tst.b m4
    bne.s 1f
    addi.w #0xC000,d0
    rts
1:  addi.w #0x3800,d0
    rts

| print a0 (0-terminated) at row d0, col d1; d2 = 1: Mode 4 byte-swapped probe words
print:
    movem.l d0-d3/a0,-(sp)
    bsr ntaddr
    bsr setw
1:  moveq #0,d0
    move.b (a0)+,d0
    beq.s 9f
    subi.w #32,d0
    bsr.s cword
    bsr vput
    bra.s 1b
9:  movem.l (sp)+,d0-d3/a0
    rts

| tile number d0 -> name-table word for the current mode (d2 = 1: swapped probe)
cword:
    tst.b m4
    beq.s 9f
    tst.w d2
    bne.s 1f
    rts                                     | low byte = tile at the even address (Genesis Plus GX order)
1:  lsl.w #8,d0                             | probe: high byte = tile, low byte = attr 1 (tile + 256 = font4alt)
    ori.w #1,d0                             | readable only if the VDP puts the HIGH byte at the even address
9:  rts

| d0 word -> 4 hex chars in hexbuf, then print at row d1 col d2 (args via d3: row<<8|col)
hex4:
    movem.l d0-d4/a0,-(sp)
    lea hexbuf,a0
    moveq #3,d4
1:  rol.w #4,d0
    move.w d0,d1
    andi.w #0xF,d1
    move.b hexchars(pc,d1.w),(a0)+
    dbra d4,1b
    clr.b (a0)
    lea hexbuf,a0
    moveq #0,d0
    move.b d3,d1
    andi.w #0xFF,d1
    move.w d3,d0
    lsr.w #8,d0
    moveq #0,d2
    bsr print
    movem.l (sp)+,d0-d4/a0
    rts
hexchars: .ascii "0123456789ABCDEF"

| d0 byte -> 2 hex chars at d3 (row<<8|col)
hex2:
    movem.l d0-d4/a0,-(sp)
    lea hexbuf,a0
    move.w d0,d1
    lsr.w #4,d1
    andi.w #0xF,d1
    move.b hexchars(pc,d1.w),(a0)+
    move.w d0,d1
    andi.w #0xF,d1
    move.b hexchars(pc,d1.w),(a0)+
    clr.b (a0)
    lea hexbuf,a0
    move.b d3,d1
    andi.w #0xFF,d1
    move.w d3,d0
    lsr.w #8,d0
    moveq #0,d2
    bsr print
    movem.l (sp)+,d0-d4/a0
    rts

| row of d2 tiles starting at tile d3, row d0 col d1, each tile repeated d4+1 times
tilerow:
    movem.l d0-d5,-(sp)
    bsr ntaddr
    bsr setw
    subq.w #1,d2
1:  move.w d4,d5
2:  move.w d3,d0
    move.l d2,-(sp)
    moveq #0,d2
    bsr cword
    move.l (sp)+,d2
    bsr vput
    dbra d5,2b
    addq.w #1,d3
    dbra d2,1b
    movem.l (sp)+,d0-d5
    rts

| ---------------------------------------------------------------- Mode 5 set-up
regs5:
    dc.w 0x8004,0x8104,0x8230,0x8334,0x8407,0x8578,0x8600,0x8700,0x8800,0x8900,0x8AFF
    dc.w 0x8B00,0x8C00,0x8D3F,0x8E00,0x8F02,0x9000,0x9100,0x9200
regs5_end:

init_mode5:
    move.w #0x8104,VDP_CTRL                 | M5 on, display off
    clr.b m4
    lea regs5,a0
    moveq #(regs5_end-regs5)/2-1,d1
1:  move.w (a0)+,VDP_CTRL
    dbra d1,1b
    move.l #0x40000000,VDP_CTRL             | clear 64 KB VRAM
    move.w #0x7FFF,d1
2:  move.w #0,VDP_DATA
    dbra d1,2b
    move.l #0x40000010,VDP_CTRL             | VSRAM
    move.l #0,VDP_DATA
    move.l #0xC0000000,VDP_CTRL             | CRAM
    lea pal5,a0
    moveq #15,d1
3:  move.w (a0)+,VDP_DATA
    dbra d1,3b
    moveq #47,d1
4:  move.w #0,VDP_DATA
    dbra d1,4b
    move.l #0x40000000,VDP_CTRL             | tiles at 0
    lea tiles5,a0
    move.w #(tiles5_end-tiles5)/2-1,d1
5:  move.w (a0)+,VDP_DATA
    dbra d1,5b
    rts

pal5:   | 0000BBB0GGG0RRR0
    dc.w 0x0400,0x0EEE,0x00EE,0x000E,0x004E,0x008E,0x00E0,0x08E0,0x0EE0,0x0E80,0x0E00,0x0E08,0x0E0E,0x080E,0x0888,0x0444

| ---------------------------------------------------------------- Mode 4 set-up (from Genesis mode, no M3 reset)
regs4:
    dc.w 0x8004,0x82FF,0x83FF,0x84FF,0x85FF,0x86FB,0x8700,0x8800,0x8900,0x8AFF
regs4_end:

init_mode4:
    bsr m4_switch
    move.w #0x4000,VDP_CTRL                 | clear 16 KB
    move.w #0x1FFF,d1
2:  move.w #0,VDP_DATA
    dbra d1,2b
    bsr m4_cram
    move.w #0x4000,VDP_CTRL                 | tiles 0-80
    lea tiles4,a0
    move.w #(tiles4_end-tiles4)/2-1,d1
4:  move.w (a0)+,VDP_DATA
    dbra d1,4b
    move.w #0x4000+0x2000,VDP_CTRL          | other-byte-order font copy at tile 256 (probe)
    lea font4alt,a0
    move.w #(font4alt_end-font4alt)/2-1,d1
5:  move.w (a0)+,VDP_DATA
    dbra d1,5b
    move.w #0x4000+0x3F00,VDP_CTRL          | SAT: no sprites
    move.w #0xD0D0,VDP_DATA
    rts

| Genesis mode -> Mode 4 (display off)
m4_switch:
    move.w #0x8104,VDP_CTRL                 | Mode 5, display off
    move.w #0x8F01,VDP_CTRL                 | regs 11-23 only writable in Mode 5: increment 1 ...
    move.w #0x8B00,VDP_CTRL
    move.w #0x8C00,VDP_CTRL                 | ... H32 (256 px) ...
    move.w #0x9000,VDP_CTRL                 | ... plane size 0
    move.w #0x8180,VDP_CTRL                 | *** M5 off: Mode 4, display off ***
    st m4
    lea regs4,a0
    moveq #(regs4_end-regs4)/2-1,d1
1:  move.w (a0)+,VDP_CTRL
    dbra d1,1b
    rts

m4_cram:
    lea pal4,a0                             | CRAM: one command per colour
    moveq #0,d1
3:  move.w d1,d0
    ori.w #0xC000,d0
    move.w d0,VDP_CTRL
    move.w (a0)+,VDP_DATA
    addq.w #1,d1
    cmpi.w #32,d1
    bne.s 3b
    rts

pal4:   | --BBGGRR (SMS)
    dc.w 0x20,0x3F,0x3F,0x03,0x07,0x0B,0x0C,0x0D,0x0F,0x0E,0x08,0x18,0x30,0x33,0x2A,0x15
    dc.w 0x15,0x03,0x07,0x0F,0x3F,0x3F,0x3F,0x3F,0x3F,0x3F,0x3F,0x3F,0x3F,0x3F,0x3F,0x3F

| ---------------------------------------------------------------- screens
enter_screen:
    move.w #0x2700,sr
    clr.w hint_n
    clr.w hint_last
    clr.w hint_cnt
    clr.b vscroll
    clr.b m4b
    move.w screen,d0
    cmpi.w #1,d0
    beq scr1
    cmpi.w #5,d0
    beq scr5
    bsr init_mode4
    move.w screen,d0
    cmpi.w #2,d0
    beq scr2
    cmpi.w #3,d0
    beq scr3
    bra scr4

scr1:
    bsr init_mode5
    lea s1a,a0
    moveq #1,d0
    moveq #2,d1
    moveq #0,d2
    bsr print
    lea s1b,a0
    moveq #3,d0
    bsr print
    lea s1c,a0
    moveq #6,d0
    bsr print
    moveq #7,d0                             | colour bars 0-15, two tiles each
    moveq #0,d1
    moveq #16,d2
    moveq #64,d3
    moveq #1,d4
    bsr tilerow
    bsr help
    move.w #0x8164,VDP_CTRL                 | Mode 5 display on + VInt
    rts

scr2:
    lea s2a,a0
    moveq #1,d0
    moveq #2,d1
    moveq #0,d2
    bsr print
    lea s2b,a0
    moveq #3,d0
    bsr print
    lea s2c,a0                              | byte-order probe
    moveq #5,d0
    moveq #1,d2
    bsr print
    moveq #0,d2
    lea s2d,a0
    moveq #7,d0
    bsr print
    moveq #8,d0
    moveq #1,d1
    moveq #15,d2
    moveq #65,d3
    moveq #1,d4
    bsr tilerow
    lea s2e,a0
    moveq #10,d0
    moveq #2,d1
    bsr print
    lea s2f,a0
    moveq #16,d0
    bsr print
    bsr help
    move.w #0x81E0,VDP_CTRL                 | Mode 4 display on + frame interrupt
    rts

scr3:
    lea s3a,a0
    moveq #0,d0
    moveq #1,d1
    moveq #0,d2
    bsr print
    moveq #4,d7                             | stripes rows 4-27
1:  move.w d7,d0
    moveq #0,d1
    moveq #4,d2
    moveq #65,d3
    moveq #1,d4
    bsr tilerow
    moveq #8,d1
    bsr tilerow
    moveq #16,d1
    bsr tilerow
    moveq #24,d1
    bsr tilerow
    addq.w #1,d7
    cmpi.w #28,d7
    bne.s 1b
    lea s3e,a0
    moveq #12,d0
    moveq #4,d1
    bsr print
    move.w #0x8A1F,VDP_CTRL                 | line counter 31: HInt every 32 lines
    move.w #0x8014,VDP_CTRL                 | M4 + line interrupt enable
    move.w #0x81E0,VDP_CTRL
    rts

scr4:
    move.w #0x4000+0x37C0,VDP_CTRL          | VRAM read-back probe
    move.w #0x1234,VDP_DATA
    move.w #0xABCD,VDP_DATA
    move.w #0x0000+0x37C0,VDP_CTRL
    move.w VDP_DATA,vram_rb
    move.w VDP_DATA,vram_rb+2
    lea s4a,a0
    moveq #1,d0
    moveq #2,d1
    moveq #0,d2
    bsr print
    lea s4b,a0
    moveq #14,d0
    bsr print
    move.w vram_rb,d0
    move.w #(14<<8)+10,d3
    bsr hex4
    move.w vram_rb+2,d0
    move.w #(14<<8)+15,d3
    bsr hex4
    bsr help
    move.w #0x8A63,VDP_CTRL                 | HInt every 100 lines
    move.w #0x8014,VDP_CTRL
    move.w #0x81E0,VDP_CTRL
    rts

| Screen 5, method B: everything loaded in Mode 5 at the remapped addresses (vput), then the switch; only CRAM is
| written in Mode 4. Static screen.
scr5:
    bsr init_mode5
    st m4
    st m4b
    clr.w cur                               | tiles 0-80
    lea tiles4,a0
    move.w #(tiles4_end-tiles4)/2-1,d1
1:  move.w (a0)+,d0
    bsr vput
    dbra d1,1b
    move.w #0x2000,cur
    lea font4alt,a0
    move.w #(font4alt_end-font4alt)/2-1,d1
2:  move.w (a0)+,d0
    bsr vput
    dbra d1,2b
    move.w #0x3F00,cur
    move.w #0xD0D0,d0
    bsr vput
    lea s5a,a0
    moveq #1,d0
    moveq #2,d1
    moveq #0,d2
    bsr print
    lea s2b,a0
    moveq #3,d0
    bsr print
    lea s2c,a0
    moveq #5,d0
    moveq #1,d2
    bsr print
    moveq #0,d2
    lea s2d,a0
    moveq #7,d0
    bsr print
    moveq #8,d0
    moveq #1,d1
    moveq #15,d2
    moveq #65,d3
    moveq #1,d4
    bsr tilerow
    lea s5b,a0
    moveq #10,d0
    moveq #2,d1
    moveq #0,d2
    bsr print
    bsr help
    clr.b m4b
    bsr m4_switch
    bsr m4_cram
    move.w #0x81E0,VDP_CTRL
    rts

help:
    lea shelp,a0
    moveq #21,d0
    moveq #1,d1
    moveq #0,d2
    bra print

| per-frame update (interrupts masked, just after VInt)
screen_update:
    move.w screen,d0
    cmpi.w #2,d0
    beq.s up2
    cmpi.w #3,d0
    beq up3
    cmpi.w #5,d0
    bne.s 0f
    rts
0:  move.w #(18<<8)+9,d3                    | screens 1 and 4: frame counter + read-back block
    move.w frames,d0
    bsr hex4
    bra readback

up2:
    move.w #(16<<8)+9,d3
    move.w frames,d0
    bsr hex4
    addq.w #1,xpos                          | 8 sprites across the screen
    move.w #0x4000+0x3F00,d0
    bsr setw
    move.l #0x60606060,VDP_DATA             | Y = 96 (drawn from line 97)
    move.l #0x60606060,VDP_DATA
    move.w #0xD0D0,VDP_DATA
    move.w #0x4000+0x3F80,d0
    bsr setw
    move.w xpos,d1
    moveq #7,d2
1:  move.w #80<<8,d0                        | [X][tile 80]: X in the low byte (even address)
    move.b d1,d0
    move.w d0,VDP_DATA
    addi.w #24,d1
    dbra d2,1b
    rts

up3:
    move.b pad,d0                           | Up / Down: V scroll
    btst #0,d0
    beq.s 1f
    subq.b #1,vscroll
1:  btst #1,d0
    beq.s 2f
    addq.b #1,vscroll
2:  moveq #0,d0
    move.b vscroll,d0
    ori.w #0x8900,d0
    move.w d0,VDP_CTRL
    lea s3b,a0
    move.w #(1<<8)+1,d3
    moveq #1,d0
    moveq #1,d1
    moveq #0,d2
    bsr print
    move.w hint_last,d0
    move.w #(1<<8)+14,d3
    bsr hex4
    lea s3c,a0
    moveq #2,d0
    moveq #1,d1
    bsr print
    lea hint_v,a1
    moveq #0,d4
3:  moveq #0,d0
    move.b (a1,d4.w),d0
    move.w d4,d3
    mulu #3,d3
    addq.w #4,d3
    ori.w #(2<<8),d3
    bsr hex2
    addq.w #1,d4
    cmpi.w #8,d4
    bne.s 3b
    lea s3d,a0
    moveq #3,d0
    moveq #1,d1
    bsr print
    moveq #0,d0
    move.b vscroll,d0
    move.w #(3<<8)+10,d3
    bsr hex2
    rts

readback:
    move.w VDP_CTRL,d0                      | status, main loop
    move.w #(9<<8)+9,d3
    bsr hex4
    move.w VDP_HV,d4                        | HV counter four times in a row
    move.w VDP_HV,d5
    move.w VDP_HV,d6
    move.w VDP_HV,d7
    move.w d4,d0
    move.w #(10<<8)+9,d3
    bsr hex4
    move.w d5,d0
    move.w #(10<<8)+14,d3
    bsr hex4
    move.w d6,d0
    move.w #(10<<8)+19,d3
    bsr hex4
    move.w d7,d0
    move.w #(10<<8)+24,d3
    bsr hex4
    move.w stat_vint,d0
    move.w #(11<<8)+9,d3
    bsr hex4
    move.w hv_vint,d0
    move.w #(12<<8)+9,d3
    bsr hex4
    move.w hv_hint,d0
    move.w #(13<<8)+9,d3
    bsr hex4
    move.w hint_last,d0
    move.w #(13<<8)+17,d3
    bsr hex4
    lea srb,a0
    moveq #9,d0
    moveq #1,d1
    moveq #0,d2
    bsr print
    lea srb2,a0
    moveq #10,d0
    bsr print
    lea srb3,a0
    moveq #11,d0
    bsr print
    lea srb4,a0
    moveq #12,d0
    bsr print
    lea srb5,a0
    moveq #13,d0
    bsr print
    lea srb6,a0
    moveq #13,d0
    moveq #15,d1
    bsr print
    lea srb7,a0
    moveq #18,d0
    moveq #1,d1
    bsr print
    rts

| ---------------------------------------------------------------- text
s1a:  .asciz "SCREEN 1  MODE 5 SANITY"
s1b:  .asciz "GENESIS MODE 5 BOOTS OK"
s1c:  .asciz "MODE 5 COLOURS 0-15:"
s2a:  .asciz "SCREEN 2  MODE 4 FROM MD"
s2b:  .asciz "MODE 4 OK"
s2c:  .asciz "SWAPPED BYTES"
s2d:  .asciz " COLOURS 1-15:"
s2e:  .asciz "8 BALLS MOVE RIGHT BELOW"
s2f:  .asciz "FRAME"
s5a:  .asciz "SCREEN 5  MODE 4 PRELOADED"
s5b:  .asciz "VRAM FILLED IN MODE 5 FIRST"
s3a:  .asciz "SCREEN 3  MODE 4 HINT+SCROLL"
s3b:  .asciz "HINTS/FRAME"
s3c:  .asciz "V:"
s3d:  .asciz "VSCROLL"
s3e:  .asciz "UP/DOWN MOVES V SCROLL"
s4a:  .asciz "SCREEN 4  MODE 4 READ-BACK"
s4b:  .asciz " VRAM RD           WANT 1234 ABCD"
srb:  .asciz "STATUS"
srb2: .asciz "HV X4"
srb3: .asciz "ST VINT"
srb4: .asciz "HV VINT"
srb5: .asciz "HV HINT"
srb6: .asciz "N"
srb7: .asciz "FRAMES"
shelp: .asciz "A/C NEXT  B BACK  START RERUN"
    .even

    .include "data.inc"
