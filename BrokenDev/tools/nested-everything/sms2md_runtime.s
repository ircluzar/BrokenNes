| Nested Everything SMS -> Genesis: the 68000 runtime under the translated Z80 code (sms2md.py writes game.s and includes this file).
|
| Z80 state in 68000 registers (the translated code and this runtime share it):
|   d0.b A    d1.w BC   d2.w DE   d3.w HL   d4 F (as the 68000 CCR saved by MOVE SR: X N Z V C, see sms2md.py)
|   a2 IX     a3 IY     a5 SP     (sign-extended: work RAM $C000-$DFFF is the Genesis's $FFC000-$FFDFFF)
|   a4 BC'    a6 the SMS VDP address (pointer into the VRAM shadow)    DE' HL' A' F' in RAM
|   d5 d6 d7 a0 a1: scratch. Helpers take an address in d7.w, a value in d6.b and may clobber d5-d7/a0/a1 unless they say so.
|
| Video back ends: VIDEO_MODE=5: the SMS VDP is emulated on top of Mode 5 (tiles converted from the SMS's planar 4bpp to packed,
| name table / sprites / colours translated). VIDEO_MODE=4 (back end A): the VDP stays in SMS Mode 4 (M5 clear), which a real
| Genesis draws from Genesis mode (md-mode4-test, 2026-11-15); the game's bytes go to it almost directly, see "Mode 4 pass-through".

        .equ VDPD,      0xC00000
        .equ VDPC,      0xC00004
        .equ HVC,       0xC00008
        .equ PSGP,      0xC00011
        .equ VSHADOW,   0xFF0000        | 16 KB: the SMS VRAM as the game wrote it
        .equ CRAMBUF,   0xFF4000        | 32 bytes: SMS colour RAM as written (right after VRAM: a6 points here in CRAM mode)
        .equ SRAMBUF,   0xFF5000        | 8 KB: cartridge RAM
        .equ NTCACHE,   0xFF8000        | 5 x 2 KB: the SMS words last converted into each Genesis plane (skip unchanged entries)
        .equ DIRTY,     0xFFFFF200      | 513 bytes: a 32-byte unit of VSHADOW (512 = CRAM) written since the last flush
| variables (absolute short: $FFF000-; the 68000 stack below them. Z80 pointers into $8000-$BFFF land on $FF8000-$FFBFFF, which holds nothing)
        .equ IFF,       0xFFFFF000
        .equ VLATCH,    0xFFFFF001
        .equ VADDR,     0xFFFFF002
        .equ VCODE,     0xFFFFF004
        .equ VLOW,      0xFFFFF005
        .equ VBUF,      0xFFFFF006
        .equ VSTAT,     0xFFFFF007
        .equ LINEFLAG,  0xFFFFF008
        .equ SATDIRTY,  0xFFFFF009
        .equ PAD1,      0xFFFFF00A
        .equ PADPREV,   0xFFFFF00B
        .equ NTBASE,    0xFFFFF00C
        .equ SATBASE,   0xFFFFF00E
        .equ VREGS,     0xFFFFF010
        .equ SLOTPTR,   0xFFFFF020
        .equ SLOTDISP,  0xFFFFF030
        .equ SRAMON,    0xFFFFF040
        .equ MAPREG,    0xFFFFF041      | 4 bytes: $FFFC-$FFFF as written
        .equ HL2,       0xFFFFF046
        .equ DE2,       0xFFFFF04C
        .equ A2V,       0xFFFFF048
        .equ F2V,       0xFFFFF04A
        .equ CRAMSH,    0xFFFFF050      | 32 bytes
        .equ FRAMES,    0xFFFFF070
        .equ IRQS,      0xFFFFF074
        .equ HALTS,     0xFFFFF078
        .equ ISRCNT,    0xFFFFF07C
        .equ INISR,     0xFFFFF07D
        .equ ERRADDR,   0xFFFFF080
        .equ ERRKIND,   0xFFFFF082
        .equ ERRFLAG,   0xFFFFF084
        .equ NMIS,      0xFFFFF086
        .equ ERRFROM,   0xFFFFF094
        .equ NTSLOT,    0xFFFFF100      | 8 words: Genesis plane address for each SMS name table base ($0000-$3800), 0 = not used yet
        .equ NTNEXT,    0xFFFFF110
        .equ NTFORCE,   0xFFFFF111
        .equ LASTDISP,  0xFFFFF112
        .equ HWADDR,    0xFFFFF114      | Mode 4: the VRAM address the real VDP holds now ($FFFF = unknown)
        .equ DEFPAIR,   0xFFFFF116      | Mode 4: an even SMS byte written to the shadow but not yet sent ($FFFF = none)
        .equ VPEND,     0xFFFFF118      | Mode 4: this frame interrupt's 68000 work done, the Z80 delivery still waiting (DI)
        .equ SHFLOOR,   0xFFFFF098      | 68000 SP at the current interrupt's entry: shadow return entries below it are its own
        .equ IDLEFR,    0xFFFFF08A      | frames in which the game reached its idle point (HALT or a profile's idle_pc)
        .equ IDLEFLAG,  0xFFFFF08E
        .equ LASTFR,    0xFFFFF090      | FRAMES at the last idle
        .equ NESTCUR,   0xFFFFF0F0      | interrupt handlers running now (nesting depth)
        .equ NESTMAX,   0xFFFFF0F1      | the deepest nesting seen
        .equ EARLYV,    0xFFFFF0F2      | Mode 4: a line interrupt's register writes wait for this V counter (0 = none)
        .ifndef M4LEAD
        .equ M4LEAD,    3
        .endif
        .equ MAGIC,     0xFFFE          | Z80 return address that means "back to the 68000 interrupt handler"

        .org 0
        .long 0x00FFC000, boot          | 68000 stack: $FFC000 down to the NT cache at $FFA800 (deep interrupt nesting needs room)
        .rept 26
        .long exc_generic
        .endr
        .long hint                      | 28: level 4 = HInt
        .long exc_ignore                | 29
        .long vint                      | 30: level 6 = VInt
        .long exc_ignore                | 31
        .rept 32
        .long exc_generic
        .endr

        .org 0x100
        .ascii "SEGA MEGA DRIVE "
        .ascii "(C)BKND 2026.NOV"
        .ascii "NESTED SMS ON GENESIS                           "
        .ascii "NESTED SMS ON GENESIS                           "
        .ascii "GM 00000000-00"
        .word 0
        .ascii "J               "
        .long 0, ROMEND-1
        .long 0xFF0000, 0xFFFFFF
        .ascii "RA"
        .byte 0xF8, 0x20
        .long 0x200001, 0x203FFF
        .ascii "            "
        .ascii "                                        "
        .ascii "JUE             "

exc_ignore:
        rte
exc_generic:
        move.l  (sp),(0xFFF0E0).l       | the exception frame (address error: access, address, IR, SR, PC)
        move.l  4(sp),(0xFFF0E4).l
        move.l  8(sp),(0xFFF0E8).l
        move.l  12(sp),(0xFFF0EC).l
        move.w  #0xEE,(ERRKIND).w
        bra     fatal

| ------------------------------------------------------------------------------------------------- boot
boot:
        move    #0x2700,sr
        move.b  (0xA10001).l,d0
        andi.b  #0x0F,d0
        beq.s   1f
        move.l  #0x53454741,(0xA14000).l
1:      move.w  #0x100,(0xA11100).l     | Z80 bus held, Z80 in reset: it stays off
        move.w  #0,(0xA11200).l
        lea     (VDPC).l,a0
        move.w  (a0),d0
        lea     vdpinit(pc),a1
        moveq   #(vdpinit_end-vdpinit)/2-1,d1
2:      move.w  (a1)+,(a0)
        dbra    d1,2b
        | clear VRAM, CRAM, VSRAM
        move.l  #0x40000000,(a0)
        moveq   #0,d0
        move.w  #0x3FFF,d1
3:      move.l  d0,(VDPD).l
        dbra    d1,3b
        move.l  #0xC0000000,(a0)
        moveq   #31,d1
4:      move.l  d0,(VDPD).l
        dbra    d1,4b
        move.l  #0x40000010,(a0)
        moveq   #19,d1
5:      move.l  d0,(VDPD).l
        dbra    d1,5b
        .if VIDEO_MODE == 5
        | solid tiles: $780 = colour 15 (SMS BG colour 0), $781 = colour 14 (SMS backdrop)
        move.l  #0x70000003,(a0)        | VRAM $F000
        move.l  #0xFFFFFFFF,d0
        moveq   #7,d1
6:      move.l  d0,(VDPD).l
        dbra    d1,6b
        move.l  #0xEEEEEEEE,d0
        moveq   #7,d1
7:      move.l  d0,(VDPD).l
        dbra    d1,7b
        | plane B ($E000): solid colour 15 everywhere; window ($D000): opaque backdrop, high priority (covers lines 192-223)
        move.l  #0x60000003,(a0)
        move.w  #0x0782,d0              | plane B: a blank tile (BG colour 0 is the backdrop)
        move.w  #1023,d1
8:      move.w  d0,(VDPD).l
        dbra    d1,8b
        move.l  #0x50000003,(a0)
        move.w  #0xE781,d0
        move.w  #1023,d1
9:      move.w  d0,(VDPD).l
        dbra    d1,9b
        .endif
        | pads
        move.b  #0x40,(0xA10009).l
        move.b  #0x40,(0xA10003).l
        | RAM: all of it except the stack area in use
        lea     0xFF0000,a1
        moveq   #0,d0
        move.w  #(0xC000/4)-1,d1
10:     move.l  d0,(a1)+
        dbra    d1,10b
        lea     0xFFC000,a1
        move.w  #(0x4000/4)-1,d1
11:     move.l  d0,(a1)+
        dbra    d1,11b
        | cartridge RAM from the Genesis cartridge's battery RAM (odd bytes at $200001)
        move.b  #1,(0xA130F1).l
        lea     0x200001,a0
        lea     SRAMBUF,a1
        move.w  #0x1FFF,d1
12:     move.b  (a0),(a1)+
        addq.l  #2,a0
        dbra    d1,12b
        | SMS power-on state
        move.w  #0x3800,(NTBASE).w
        .if VIDEO_MODE == 5
        moveq   #2,d6
        move.b  #0xFF,d5
        bsr     vdp_setreg              | the power-on name table ($3800) gets its plane
        .else
        bsr     m4_enter
        .endif
        move.w  #0x3F00,(SATBASE).w
        move.b  #0xFF,(PAD1).w
        lea     (MAPREG).w,a0
        move.b  #0,(a0)+
        move.b  #0,(a0)+
        move.b  #1,(a0)+
        move.b  #2,(a0)+
        moveq   #0,d6
        move.w  #0xFFFD,d7
        bsr     mapper_set
        moveq   #1,d6
        move.w  #0xFFFE,d7
        bsr     mapper_set
        moveq   #2,d6
        move.w  #0xFFFF,d7
        bsr     mapper_set
        moveq   #0,d0
        moveq   #0,d1
        moveq   #0,d2
        moveq   #0,d3
        moveq   #0,d4
        suba.l  a2,a2
        suba.l  a3,a3
        suba.l  a4,a4
        movea.l #VSHADOW,a6             | a6 = the SMS VDP's address register, as a pointer into the shadow
        movea.w #0xDFF0,a5
        move.l  sp,(SHFLOOR).w
        move    #0x2000,sr
        jmp     Z80_RESET

vdpinit:
        .word 0x8004, 0x8164, 0x8230, 0x8334, 0x8407, 0x856C, 0x8600, 0x8700, 0x8800, 0x8900, 0x8AFF, 0x8B00
        .word 0x8C00, 0x8D37, 0x8E00, 0x8F02, 0x9000, 0x9100, 0x9298
vdpinit_end:

| ------------------------------------------------------------------------------------------------- errors
| d7.w = the Z80 address nobody translated. The picture goes red; ERRFLAG/ERRKIND/ERRADDR tell the harness.
z_untranslated:
        move.w  #1,(ERRKIND).w
        move.w  (LASTDISP).w,(ERRADDR).w
        move.w  a1,(ERRFROM).w          | with debug_trail: the Z80 instruction that jumped there
fatal:
        move    #0x2700,sr
        movem.l d0-d7/a0-a6,(0xFFF0A0).l | the registers at the stop, for the harness
        st      (ERRFLAG).w
        move.l  #0xC0000000,(VDPC).l
        move.w  #0x000E,(VDPD).l
        move.w  #0x8700,(VDPC).l
        move.w  #0x8124,(VDPC).l
1:      bra.s   1b
z_unsupported:                          | an instruction the translator does not support (d7 = its address)
        move.w  #2,(ERRKIND).w
        move.w  d7,(ERRADDR).w
        bra     fatal

| ------------------------------------------------------------------------------------------------- dispatch
| d7.w = Z80 address; jumps to its translation in the bank its slot holds now.
z_dispatch:
        move.w  d7,(LASTDISP).w         | (for the error report: d7 is scaled below)
        cmpi.w  #0xC000,d7
        bcc.s   1f
        move.w  d7,d6
        rol.w   #4,d6
        andi.w  #0x0C,d6
        lea     (SLOTDISP).w,a0
        movea.l 0(a0,d6.w),a0
        andi.l  #0x3FFF,d7
        lsl.l   #2,d7
        movea.l 0(a0,d7.l),a0
        jmp     (a0)
1:      cmpi.w  #MAGIC,d7
        bne     z_untranslated
        movea.l (SHFLOOR).w,a0          | back into isr_call, whatever shadow entries the handler left
        lea     -4(a0),sp
        rts

| z_ret_slow: a RET whose shadow entry did not match (d7 = popped return address, d5 = Z80 SP before the pop).
| Entries of frames the game left without returning (Z80 SP below this one) are dropped, then the top one is tried again.
z_ret_slow:
        addq.l  #1,(0xFFF0B0).l         | misses (diagnostic)
        movea.l (SHFLOOR).w,a0
        lea     -4(a0),a0
1:      cmpa.l  a0,sp
        bcc.s   9f
        cmp.w   6(sp),d5
        bls.s   2f
        addq.l  #8,sp
        bra.s   1b
2:      cmp.w   4(sp),d7
        bne.s   9f
        cmp.w   6(sp),d5
        bne.s   9f
        rts
9:      jra     z_dispatch

| ------------------------------------------------------------------------------------------------- memory
| rd8: d7.w address -> d6.b (d7 kept)
rd8:    cmpi.w  #0xC000,d7
        bcc.s   1f
        move.w  d7,d6
        rol.w   #4,d6
        andi.w  #0x0C,d6
        lea     (SLOTPTR).w,a0
        movea.l 0(a0,d6.w),a0
        move.w  d7,d6
        andi.w  #0x3FFF,d6
        move.b  0(a0,d6.w),d6
        rts
1:      move.w  d7,d6
        andi.w  #0xDFFF,d6
        movea.w d6,a0
        move.b  (a0),d6
        rts

| wr8: d7.w address, d6.b value (d7 kept)
wr8:    cmpi.w  #0xC000,d7
        bcs.s   2f
        move.w  d7,d5
        andi.w  #0xDFFF,d5
        movea.w d5,a0
        move.b  d6,(a0)
        cmpi.w  #0xFFFC,d7
        bcc.s   mapper_set
        rts
2:      cmpi.w  #0x8000,d7
        bcs.s   3f
        tst.b   (SRAMON).w
        beq.s   3f
        move.w  d7,d5
        andi.w  #0x3FFF,d5
        lea     SRAMBUF,a0
        move.b  d6,0(a0,d5.w)
        add.w   d5,d5
        lea     0x200001,a0
        move.b  d6,0(a0,d5.w)
3:      rts

| mapper_set: d7.w = $FFFC-$FFFF, d6.b = value (d7 kept)
mapper_set:
        cmpi.w  #0xFFFE,d7              | slot 1 / slot 2 bank (ROM): table look-ups only
        bcs.s   mapper_full
        tst.b   (SRAMON).w
        bne.s   mapper_full
        moveq   #0,d5
        move.b  d6,d5
        andi.w  #NBANKS-1,d5
        add.w   d5,d5
        add.w   d5,d5
        lea     BANKPTR,a0
        cmpi.w  #0xFFFF,d7
        beq.s   1f
        move.b  d6,(MAPREG+2).w
        move.l  0(a0,d5.w),(SLOTPTR+4).w
        lea     DTL_1,a0
        move.l  0(a0,d5.w),(SLOTDISP+4).w
        rts
1:      move.b  d6,(MAPREG+3).w
        move.l  0(a0,d5.w),(SLOTPTR+8).w
        lea     DTL_2,a0
        move.l  0(a0,d5.w),(SLOTDISP+8).w
        rts
mapper_full:
        movem.l d0-d1/a1,-(sp)
        move.w  d7,d1
        andi.w  #3,d1
        lea     (MAPREG).w,a0
        move.b  d6,0(a0,d1.w)
        tst.w   d1
        bne.s   1f
        btst    #3,d6                   | $FFFC bit 3: cartridge RAM in slot 2
        sne     (SRAMON).w
        moveq   #3,d1
        move.b  (MAPREG+3).w,d6
1:      subq.w  #1,d1                   | slot 0-2
        moveq   #0,d0
        move.b  d6,d0
        andi.w  #NBANKS-1,d0
        move.l  d0,d5
        swap    d5
        lsr.l   #2,d5                   | bank * $4000
        addi.l  #SMSROM,d5
        cmpi.w  #2,d1
        bne.s   2f
        tst.b   (SRAMON).w
        beq.s   2f
        move.l  #SRAMBUF,d5
2:      add.w   d1,d1
        add.w   d1,d1
        lea     (SLOTPTR).w,a0
        move.l  d5,0(a0,d1.w)
        lea     disptabs,a1
        movea.l 0(a1,d1.w),a1
        add.w   d0,d0
        add.w   d0,d0
        lea     (SLOTDISP).w,a0
        move.l  0(a1,d0.w),0(a0,d1.w)
        movem.l (sp)+,d0-d1/a1
        rts

| ------------------------------------------------------------------------------------------------- ports
| port_out: d7.b port, d6.b value
port_out:
        move.b  d7,d5
        andi.b  #0xC0,d5
        cmpi.b  #0x40,d5
        bne.s   1f
        move.b  d6,(PSGP).l
        rts
1:      cmpi.b  #0x80,d5
        bne.s   2f
        btst    #0,d7
        bne     vdpw_ctrl
        bra     vdpw_data
2:      rts

| port_in: d7.b port -> d6.b
port_in:
        move.b  d7,d5
        andi.b  #0xC0,d5
        cmpi.b  #0x40,d5
        bne.s   1f
        btst    #0,d7
        bne.s   z_hcount
        bra.s   z_vcount
1:      cmpi.b  #0x80,d5
        bne.s   2f
        btst    #0,d7
        bne     vdpr_stat
        bra     vdpr_data
2:      cmpi.b  #0xC0,d5
        bne.s   3f
        btst    #0,d7
        bne.s   z_pad2
        bra.s   z_pad1
3:      moveq   #-1,d6
        rts

| z_vcount: the SMS V counter as the game expects it relative to its frame interrupt. That fires at Genesis line 224
| (VInt), SMS line 192 on the console: the game sees the Genesis line minus 48, SMS-encoded ($00-$DA, then $D5-$FF).
z_vcount:
        .if VIDEO_MODE == 4
        move.b  (HVC).l,d6              | Mode 4: the VDP's own V counter runs the SMS's 192-line sequence
        rts
        .endif
        moveq   #0,d6
        move.b  (HVC).l,d6
        subi.w  #48,d6                  | (48, not 32: the VInt handler's own work takes Genesis lines 224-238, where a
        bcc.s   1f                      | polling loop would never see SMS lines 192-206)
        addi.w  #262,d6
1:      cmpi.w  #219,d6
        bcs.s   2f
        subq.w  #6,d6
2:      rts
z_hcount:
        move.b  (HVC+1).l,d6
        rts
z_pad1:
        .if VIDEO_MODE == 4
        movem.l d0-d1/a0,-(sp)          | Mode 4: read now (the frame interrupt may be off while the game polls)
        bsr     pad_raw
        move.b  d0,(PAD1).w
        move.b  d0,d6
        movem.l (sp)+,d0-d1/a0
        rts
        .endif
        move.b  (PAD1).w,d6
        rts
z_pad2: moveq   #-1,d6
        rts

| ------------------------------------------------------------------------------------------------- interrupts
| irq_line: Z flag clear when the SMS VDP's interrupt line is up
irq_line:
        btst    #7,(VSTAT).w
        beq.s   1f
        btst    #5,(VREGS+1).w
        bne.s   2f
1:      tst.b   (LINEFLAG).w
        beq.s   2f
        btst    #4,(VREGS+0).w
2:      rts

| deliver: run the game's IM 1 handler now (Z80 interrupts on). Called from the 68000 handlers, EI and HALT.
deliver:
        sf      (IFF).w
        addq.l  #1,(IRQS).w
        addq.b  #1,(ISRCNT).w
        lea     Z80_IRQ,a0
| isr_call: run the Z80 handler at a0 as an interrupt (return address MAGIC on the Z80 stack, a new shadow floor)
isr_call:
        move.b  #(MAGIC>>8),-(a5)
        move.b  #(MAGIC&0xFF),-(a5)
        move.w  sr,-(sp)
        move.l  (SHFLOOR).w,-(sp)
        move.w  a5,-(sp)
        move.w  #MAGIC,-(sp)
        move.l  sp,(SHFLOOR).w
        addq.b  #1,(NESTCUR).w          | interrupt nesting depth (harness: NESTMAX)
        move.b  (NESTCUR).w,d7
        cmp.b   (NESTMAX).w,d7
        bls.s   1f
        move.b  d7,(NESTMAX).w
1:
        .if VIDEO_MODE == 5
        move    #0x2000,sr
        .else
        move    #0x2700,sr              | Mode 4: the VDP's interrupt line stays up until a status read; IFF is off, so the
        .endif                          | 68000 takes nothing until the game's EI (z_ei) opens the mask
        jsr     (a0)
        subq.b  #1,(NESTCUR).w
        addq.l  #4,sp
        move.l  (sp)+,(SHFLOOR).w
        move.w  (sp)+,sr
        rts

        .if VIDEO_MODE == 5
vint:
        movem.l d0-d7/a0-a6,-(sp)
        addq.l  #1,(FRAMES).w
        bsr     vdp_flush
        move.l  #0x40000010,(VDPC).l
        moveq   #0,d0
        move.b  (VREGS+9).w,d0
        move.w  d0,(VDPD).l
        tst.b   (SATDIRTY).w
        beq.s   1f
        sf      (SATDIRTY).w
        bsr     sat_upload
1:      bsr     read_pad
        bset    #7,(VSTAT).w
        tst.b   d2
        beq.s   2f
        | Pause: the Z80 NMI
        addq.l  #1,(NMIS).w
        moveq   #0,d7
        move.b  (IFF).w,d7
        move.w  d7,-(sp)
        sf      (IFF).w
        movem.l 2(sp),d0-d7/a0-a6       | the interrupted Z80 registers
        lea     Z80_NMI,a0
        bsr     isr_call
        move.l  a6,2+56(sp)
        move    #0x2600,sr
        move.w  (sp)+,d7
        move.b  d7,(IFF).w
2:      tst.b   (IFF).w
        beq.s   9f
        btst    #5,(VREGS+1).w
        beq.s   9f
        movem.l (sp),d0-d7/a0-a6
        bsr     deliver
        move.l  a6,56(sp)               | the VDP address the handler left (hardware state, not a register to restore)
9:      movem.l (sp)+,d0-d7/a0-a6
        rte

hint:
        movem.l d0-d7/a0-a6,-(sp)
        move.b  (HVC).l,d0
        cmpi.b  #192,d0
        bcc.s   9f
        st      (LINEFLAG).w
        tst.b   (IFF).w
        beq.s   9f
        btst    #4,(VREGS+0).w
        beq.s   9f
        movem.l (sp),d0-d7/a0-a6
        bsr     deliver
        move.l  a6,56(sp)
9:      movem.l (sp)+,d0-d7/a0-a6
        rte

| EI: interrupts on; a pending line interrupts at once
z_ei:   st      (IFF).w
        bsr       irq_line
        beq.s   1f
        bra     deliver
1:      rts

| HALT: wait for the next delivered interrupt
z_halt: addq.l  #1,(HALTS).w
        bsr     z_idle
        move.b  (ISRCNT).w,d6
1:      tst.b   (IFF).w
        beq.s   2f
        bsr       irq_line
        beq.s   2f
        bra     deliver
2:      stop    #0x2000
        cmp.b   (ISRCNT).w,d6
        beq.s   1b
        rts
        .else
| Mode 4: the real VDP raises the 68000's level 6 (frame) and level 4 (line) interrupts and holds them until the game reads
| the status port, exactly the SMS's IRQ line. Z80 IFF off: the handler masks the interrupted code (stacked SR) and returns;
| the game's EI opens the mask again and the 68000 takes the still pending interrupt then (the Z80's own behaviour).
vint:
        movem.l d0-d7/a0-a6,-(sp)
        tst.b   (VPEND).w
        bne.s   3f                      | the same frame interrupt again (after an EI): its 68000 work is done
        st      (VPEND).w
        addq.l  #1,(FRAMES).w
        bsr     m4_flush
        bsr     read_pad
        tst.b   d2
        beq.s   3f
        | Pause: the Z80 NMI
        addq.l  #1,(NMIS).w
        moveq   #0,d7
        move.b  (IFF).w,d7
        move.w  d7,-(sp)
        sf      (IFF).w
        movem.l 2(sp),d0-d7/a0-a6
        lea     Z80_NMI,a0
        bsr     isr_call
        move.l  a6,2+56(sp)
        move    #0x2700,sr
        move.w  (sp)+,d7
        move.b  d7,(IFF).w
3:      tst.b   (IFF).w
        beq.s   8f
        sf      (VPEND).w
        movem.l (sp),d0-d7/a0-a6
        bsr     deliver
        move.l  a6,56(sp)
        movem.l (sp)+,d0-d7/a0-a6
        rte
8:      ori.w   #0x0700,60(sp)          | DI: keep the interrupted code masked until its EI
        movem.l (sp)+,d0-d7/a0-a6
        rte

| Early line interrupt: the translated handler needs a few lines before its first register write (CTW's HUD band, R2 at
| line 184, came 2 lines late), so the hardware's R10 is M4LEAD lower and the handler's first register write waits on the V
| counter for the line the SMS's interrupt would have come on (+1: the line the SMS handler's write lands in).
hint:
        tst.b   (IFF).w
        beq.s   8f
        movem.l d0-d7/a0-a6,-(sp)
        move.b  (HVC).l,d0
        addq.b  #M4LEAD+1,d0
        cmpi.b  #192,d0
        bcs.s   1f
        moveq   #0,d0
1:      move.b  d0,(EARLYV).w
        movem.l (sp),d0-d7/a0-a6
        bsr     deliver
        sf      (EARLYV).w
        move.l  a6,56(sp)
        movem.l (sp)+,d0-d7/a0-a6
        rte
8:      ori.w   #0x0700,(sp)
        rte

| EI: interrupts on; a pending VDP interrupt is taken at once by the 68000
z_ei:   st      (IFF).w
        move    #0x2000,sr
        rts

| HALT: wait for the next delivered interrupt
z_halt: addq.l  #1,(HALTS).w
        bsr     z_idle
        move.b  (ISRCNT).w,d6
1:      stop    #0x2000
        cmp.b   (ISRCNT).w,d6
        beq.s   1b
        rts
        .endif

| z_idle: the game reached its idle point this frame (HALT or a profile idle_pc): counts frames with idle time (lag = frames without)
z_idle: move.l  (FRAMES).w,d5
        cmp.l   (LASTFR).w,d5
        beq.s   1f
        move.l  d5,(LASTFR).w
        addq.l  #1,(IDLEFR).w
        moveq   #0,d5
        move.b  (HVC).l,d5              | the line the frame's work ended on: sum for the harness's average load
        add.l   d5,(0xFFF0B4).l
1:      rts

| read_pad (in VInt, every register free): PAD1 = SMS $DC (Genesis B = button 1, C = button 2, A = button 1 too), d2 = Start newly pressed
read_pad:
        bsr.s   pad_raw
        move.b  d0,(PAD1).w
        move.b  d1,d2
        not.b   d2
        andi.b  #0x20,d2
        move.b  (PADPREV).w,d3
        move.b  d2,(PADPREV).w
        not.b   d3
        and.b   d3,d2
        rts
| pad_raw: d0 = SMS $DC, d1 = the second (Start/A) read; a0 clobbered
pad_raw:
        lea     (0xA10003).l,a0
        move.b  #0x40,(a0)
        nop
        nop
        move.b  (a0),d0
        move.b  #0x00,(a0)
        nop
        nop
        move.b  (a0),d1
        move.b  #0x40,(a0)
        ori.b   #0xC0,d0
        btst    #4,d1
        bne.s   1f
        bclr    #4,d0
1:      rts

| ------------------------------------------------------------------------------------------------- block instructions
| (all keep d0-d4/a2-a6 as the Z80 state; flags: d4)
z_ldi:  move.w  d3,d7
        bsr     rd8
        move.w  d2,d7
        bsr     wr8
        addq.w  #1,d3
        addq.w  #1,d2
        subq.w  #1,d1
        bra.s   ld_pv
z_ldd:  move.w  d3,d7
        bsr     rd8
        move.w  d2,d7
        bsr     wr8
        subq.w  #1,d3
        subq.w  #1,d2
        subq.w  #1,d1
ld_pv:  bclr    #1,d4
        tst.w   d1
        beq.s   1f
        bset    #1,d4
1:      rts
| LDIR: fast when the destination is work RAM and the source work RAM or one ROM slot
z_ldir: tst.w   d1
        beq.s   9f
        cmpi.w  #0xC000,d2
        bcs.s   5f
        move.w  d2,d5
        add.w   d1,d5
        bcs.s   5f
        cmpi.w  #0xE000,d5
        bhi.s   5f
        cmpi.w  #0xC000,d3
        bcc.s   2f
        move.w  d3,d5
        add.w   d1,d5
        subq.w  #1,d5
        eor.w   d3,d5
        andi.w  #0xC000,d5
        bne.s   5f
        move.w  d3,d7
        bsr     rd8                     | leaves a0 = the slot's base
        move.w  d3,d6
        andi.w  #0x3FFF,d6
        adda.w  d6,a0
        bra.s   3f
2:      move.w  d3,d5
        add.w   d1,d5
        bcs.s   5f
        cmpi.w  #0xE000,d5
        bhi.s   5f
        movea.w d3,a0
3:      movea.w d2,a1
        move.w  d1,d6
        subq.w  #1,d6
4:      move.b  (a0)+,(a1)+
        dbra    d6,4b
        add.w   d1,d3
        add.w   d1,d2
        moveq   #0,d1
        bclr    #1,d4
        rts
5:      bsr     z_ldi
        tst.w   d1
        bne.s   5b
9:      bclr    #1,d4
        rts
z_lddr: bsr     z_ldd
        tst.w   d1
        bne.s   z_lddr
        rts
| CPI/CPIR/CPD/CPDR: Z = match, S from A-(HL), V = BC != 0, C kept
z_cpi:  moveq   #1,d5
        bra.s   cp_one
z_cpd:  moveq   #-1,d5
cp_one: move.w  d3,d7
        bsr     rd8
        add.w   d5,d3
        subq.w  #1,d1
        move.b  d0,d7
        sub.b   d6,d7
        move    sr,d6
        andi.w  #0x0C,d6                | N Z
        andi.w  #0x11,d4                | X C kept
        or.w    d6,d4
        tst.w   d1
        beq.s   1f
        bset    #1,d4
1:      rts
z_cpir: bsr.s   z_cpi
        btst    #2,d4
        bne.s   1f
        btst    #1,d4
        bne.s   z_cpir
1:      rts
z_cpdr: bsr.s   z_cpd
        btst    #2,d4
        bne.s   1f
        btst    #1,d4
        bne.s   z_cpdr
1:      rts
| OUTI/OUTD/OTIR/OTDR: out (C),(HL); HL+-; B-- ; Z = B == 0
| z_outin: d5.w = n consecutive OUTIs. Port $BE with the source in one RAM range or one ROM slot: a bulk copy into the
| shadow and one dirty mark per unit; anything else: n single OUTIs.
z_outin:
        cmpi.b  #0xBE,d1
        bne.s   8f
        .if VIDEO_MODE == 4
        cmpi.b  #3,(VCODE).w            | (CRAM: one byte at a time)
        beq.s   8f
        move.w  a6,d6
        add.w   d5,d6
        cmpi.w  #0x4000,d6              | (wraps the 16 KB: one byte at a time)
        bhi.s   8f
        .endif
        move.w  d3,d7
        cmpi.w  #0xC000,d7
        bcc.s   1f
        move.w  d7,d6
        andi.w  #0x3FFF,d6
        add.w   d5,d6
        cmpi.w  #0x4000,d6
        bhi.s   8f                      | crosses the slot's end
        move.w  d7,d6
        rol.w   #4,d6
        andi.w  #0x0C,d6
        lea     (SLOTPTR).w,a0
        movea.l 0(a0,d6.w),a0
        andi.w  #0x3FFF,d7
        adda.w  d7,a0
        bra.s   2f
1:      move.w  d7,d6
        add.w   d5,d6
        cmpi.w  #0xE000,d6
        bhi.s   8f
        movea.w d7,a0
2:      sf      (VLATCH).w
        move.w  a6,d6                   | first unit
        lsr.w   #5,d6
        move.w  d5,d7
        subq.w  #1,d7
3:      move.b  (a0)+,(a6)+
        dbra    d7,3b
        .if VIDEO_MODE == 4
        move.w  d5,-(sp)
        bsr     m4_bulk
        move.w  (sp)+,d5
        .else
        move.w  a6,d7
        subq.w  #1,d7
        lsr.w   #5,d7                   | last unit
        lea     (DIRTY).w,a0
4:      st      0(a0,d6.w)
        addq.w  #1,d6
        cmp.w   d7,d6
        bls.s   4b
        .endif
        add.w   d5,d3
        lsl.w   #8,d5
        sub.w   d5,d1
        moveq   #0,d4
        cmpi.w  #0x100,d1
        bcc.s   5f
        moveq   #4,d4
5:      rts
8:      move.w  d5,-(sp)
9:      bsr     z_outi
        subq.w  #1,(sp)
        bne.s   9b
        addq.l  #2,sp
        rts

z_outi:
        .if VIDEO_MODE == 5
        cmpi.b  #0xBE,d1                | the VRAM upload case: (HL) straight into the shadow
        bne.s   5f
        move.w  d3,d7
        cmpi.w  #0xC000,d7
        bcc.s   1f
        move.w  d7,d6
        rol.w   #4,d6
        andi.w  #0x0C,d6
        lea     (SLOTPTR).w,a0
        movea.l 0(a0,d6.w),a0
        andi.w  #0x3FFF,d7
        move.b  0(a0,d7.w),(a6)+
        bra.s   2f
1:      andi.w  #0xDFFF,d7
        movea.w d7,a0
        move.b  (a0),(a6)+
2:      sf      (VLATCH).w
        move.w  a6,d5
        subq.w  #1,d5
        lsr.w   #5,d5
        lea     (DIRTY).w,a0
        st      0(a0,d5.w)
        addq.w  #1,d3
        subi.w  #0x100,d1
        moveq   #0,d4
        cmpi.w  #0x100,d1
        bcc.s   3f
        moveq   #4,d4
3:      rts
        .endif
5:      move.w  d3,d7
        bsr     rd8
        addq.w  #1,d3
        bra.s   out_one
z_outd: move.w  d3,d7
        bsr     rd8
        subq.w  #1,d3
out_one:
        subi.w  #0x100,d1
        move.b  d1,d7
        cmpi.b  #0xBE,d7
        bne.s   2f
        bsr     vdpw_data
        bra.s   3f
2:      bsr     port_out
3:
        moveq   #0,d4
        cmpi.w  #0x100,d1
        bcc.s   1f
        moveq   #4,d4
1:      rts
z_otir: bsr     z_outi
        cmpi.w  #0x100,d1
        bcc.s   z_otir
        rts
z_otdr: bsr.s   z_outd
        cmpi.w  #0x100,d1
        bcc.s   z_otdr
        rts
| INI/IND/INIR/INDR
z_ini:  move.b  d1,d7
        bsr     port_in
        move.w  d3,d7
        bsr     wr8
        addq.w  #1,d3
        bra.s   in_b
z_ind:  move.b  d1,d7
        bsr     port_in
        move.w  d3,d7
        bsr     wr8
        subq.w  #1,d3
in_b:   subi.w  #0x100,d1
        moveq   #0,d4
        cmpi.w  #0x100,d1
        bcc.s   1f
        moveq   #4,d4
1:      rts
z_inir: bsr.s   z_ini
        cmpi.w  #0x100,d1
        bcc.s   z_inir
        rts
z_indr: bsr.s   z_ind
        cmpi.w  #0x100,d1
        bcc.s   z_indr
        rts
| RLD / RRD (A and (HL)); flags from A (S Z P), C kept
z_rld:  move.w  d3,d7
        bsr     rd8
        moveq   #0,d5
        move.b  d6,d5
        lsl.w   #4,d5                   | (HL) << 4
        move.b  d0,d6
        andi.b  #0x0F,d6
        or.b    d6,d5                   | low nibble of A in
        move.w  d5,d6
        lsr.w   #8,d6                   | old high nibble of (HL)
        andi.b  #0xF0,d0
        or.b    d6,d0
        move.b  d5,d6
        bsr     wr8
        bra.s   rxd_f
z_rrd:  move.w  d3,d7
        bsr     rd8
        moveq   #0,d5
        move.b  d0,d5
        lsl.w   #8,d5
        move.b  d6,d5                   | A:(HL)
        move.b  d6,d7
        andi.b  #0x0F,d7                | low nibble of (HL) -> A
        andi.b  #0xF0,d0
        or.b    d7,d0
        lsr.w   #4,d5
        move.b  d5,d6                   | (A low << 4) | (HL) >> 4
        move.w  d3,d7
        bsr     wr8
rxd_f:  andi.w  #0x11,d4
        tst.b   d0
        move    sr,d6
        andi.w  #0x0C,d6
        or.w    d6,d4
        move.b  d0,d6
        bra     parity_v

| parity_v: d4 bit 1 (V) = even parity of d6.b (Z80 P/V after logic ops, shifts and IN)
parity_v:
        andi.w  #0xFF,d6
        lea     PARITY,a0
        bclr    #1,d4
        tst.b   0(a0,d6.w)
        beq.s   1f
        bset    #1,d4
1:      rts

| ------------------------------------------------------------------------------------------------- video back end
        .ifndef VIDEO_MODE
        .equ VIDEO_MODE, 5
        .endif

        .if VIDEO_MODE == 5
| --- Mode 5 translation (back end B) ---

| vdpw_ctrl: d6.b = byte written to $BF
vdpw_ctrl:
        tst.b   (VLATCH).w
        bne.s   1f
        move.b  d6,(VLOW).w
        st      (VLATCH).w
        rts
1:      sf      (VLATCH).w
        move.b  d6,d5
        lsr.b   #6,d5
        move.b  d5,(VCODE).w
        moveq   #0x3F,d7
        and.b   d6,d7
        lsl.w   #8,d7
        move.b  (VLOW).w,d7             | d7 = the 14-bit address
        cmpi.b  #3,d5
        bne.s   2f
        andi.w  #0x1F,d7
        ori.w   #0x4000,d7              | CRAM: a6 into CRAMBUF
2:      movea.l #VSHADOW,a6
        adda.w  d7,a6
        cmpi.b  #2,d5
        beq.s   3f
        tst.b   d5
        bne.s   4f
        move.b  (a6)+,(VBUF).w          | read setup: the read-ahead buffer
4:      rts
3:      andi.w  #0x0F,d6
        move.b  (VLOW).w,d5
        move.w  sr,-(sp)
        ori     #0x0700,sr
        bsr.s   vdp_setreg
        move.w  (sp)+,sr
        rts

| vdp_setreg: d6.w = register, d5.b = value
vdp_setreg:
        lea     (VREGS).w,a0
        move.b  d5,0(a0,d6.w)
        add.w   d6,d6
        move.w  regtab(pc,d6.w),d6
        jmp     regtab(pc,d6.w)
regtab: .word r0-regtab, r1-regtab, r2-regtab, rnop-regtab, rnop-regtab, r5-regtab, r6-regtab, r7-regtab
        .word r8-regtab, rnop-regtab, r10-regtab, rnop-regtab, rnop-regtab, rnop-regtab, rnop-regtab, rnop-regtab
r0:     moveq   #0x10,d7
        and.b   d5,d7
        ori.w   #0x8004,d7
        move.w  d7,(VDPC).l
        st      (SATDIRTY).w
rnop:   rts
r1:     moveq   #0x40,d7
        and.b   d5,d7
        ori.w   #0x8124,d7
        move.w  d7,(VDPC).l
        st      (SATDIRTY).w
        rts
| R2: every SMS name table base keeps its own converted Genesis plane, so a switch (the HUD band's mid-frame one too)
| is only the Genesis plane A register
r2:     moveq   #0x0E,d7
        and.b   d5,d7
        lsl.w   #8,d7
        lsl.w   #2,d7
        move.w  d7,(NTBASE).w
        move.w  d7,d6
        lsr.w   #8,d6
        lsr.w   #2,d6
        lea     (NTSLOT).w,a0
        move.w  0(a0,d6.w),d5
        bne.s   2f
        moveq   #0,d5
        move.b  (NTNEXT).w,d5
        addq.b  #1,(NTNEXT).w
        cmpi.b  #5,d5
        bcs.s   1f
        moveq   #0,d5
        move.b  #1,(NTNEXT).w
1:      add.w   d5,d5
        move.w  ntplanes(pc,d5.w),d5
        move.l  d4,-(sp)
        moveq   #7,d4
11:     cmp.w   (a0),d5
        bne.s   12f
        clr.w   (a0)
12:     addq.l  #2,a0
        dbra    d4,11b
        move.l  (sp)+,d4
        lea     (NTSLOT).w,a0
        move.w  d5,0(a0,d6.w)
        move.w  d5,-(sp)
        bsr     nt_rebuild
        move.w  (sp)+,d5
2:      lsr.w   #8,d5
        lsr.w   #2,d5
        ori.w   #0x8200,d5
        move.w  d5,(VDPC).l
        rts
ntplanes:
        .word 0xC000, 0x4000, 0x6000, 0x8000, 0xA000
r5:     moveq   #0x7E,d7
        and.b   d5,d7
        lsl.w   #7,d7
        move.w  d7,(SATBASE).w
        st      (SATDIRTY).w
        rts
r6:     st      (SATDIRTY).w
        rts
r7:     moveq   #0x0F,d7
        and.b   d5,d7
        move.w  d7,d6
        lea     (CRAMSH+16).w,a0
        move.b  0(a0,d6.w),d6
        moveq   #(3*16+14),d7
        bra     cram_put
r8:     moveq   #0,d7
        move.b  d5,d7
        move.l  #0x5C000003,(VDPC).l    | H scroll table $DC00, plane A
        move.w  d7,(VDPD).l
        rts
r10:    moveq   #0,d7
        move.b  d5,d7
        ori.w   #0x8A00,d7
        move.w  d7,(VDPC).l
        rts

| vdpw_data: d6.b = byte written to $BE (the translated OUT ($BE),A does the same inline): into the shadow, unit marked dirty;
| vdp_flush converts at the next VInt
vdpw_data:
        sf      (VLATCH).w
        move.b  d6,(a6)+
        move.w  a6,d5
        subq.w  #1,d5
        lsr.w   #5,d5
        lea     (DIRTY).w,a0
        st      0(a0,d5.w)
        rts

| vdp_flush (in VInt, every register but a6 free): convert the dirty units (tiles, name table entries, the SAT, CRAM)
vdp_flush:
        lea     (DIRTY).w,a2
        moveq   #0,d2                   | unit number
1:      tst.l   (a2)
        bne.s   2f
        addq.l  #4,a2
        addq.w  #4,d2
        cmpi.w  #512,d2
        bcs.s   1b
        bra.s   8f
2:      moveq   #3,d3
3:      tst.b   (a2)
        beq.s   6f
        sf      (a2)
        move.w  d2,d4
        lsl.w   #5,d4                   | unit address
        move.w  d4,d7                   | a name table in use?
        lsr.w   #8,d7
        lsr.w   #2,d7
        andi.w  #0x0E,d7
        lea     (NTSLOT).w,a3
        tst.w   0(a3,d7.w)
        beq.s   4f
        move.w  d4,d7
        andi.w  #0x7FF,d7
        cmpi.w  #0x700,d7
        bcc.s   4f
        move.w  d4,d7
        moveq   #15,d1                  | 16 name table entries
31:     bsr     nt_put
        addq.w  #2,d7
        dbra    d1,31b
        bra.s   6f
4:      move.w  d4,d7
        sub.w   (SATBASE).w,d7
        cmpi.w  #0x100,d7
        bcc.s   5f
        st      (SATDIRTY).w
        bra.s   6f
5:      move.w  d4,d5
        addq.w  #3,d5
        moveq   #7,d1                   | 8 tile rows
51:     bsr     tile_row
        addq.w  #4,d5
        dbra    d1,51b
6:      addq.l  #1,a2
        addq.w  #1,d2
        dbra    d3,3b
        cmpi.w  #512,d2
        bcs.s   1b
8:      tst.b   (DIRTY+512).w
        beq.s   9f
        sf      (DIRTY+512).w
        | CRAM: entries that changed
        lea     CRAMBUF,a2
        lea     (CRAMSH).w,a3
        moveq   #0,d2
10:     move.b  0(a2,d2.w),d6
        cmp.b   0(a3,d2.w),d6
        beq.s   12f
        move.b  d6,0(a3,d2.w)
        move.w  d2,d7
        move.b  d6,d4
        bsr     cram_put
        tst.w   d2
        bne.s   11f
        move.b  d4,d6
        moveq   #(3*16+15),d7           | BG colour 0 -> the solid plane B
        bsr     cram_put
        bra.s   12f
11:     moveq   #0x0F,d5
        and.b   (VREGS+7).w,d5
        addi.w  #16,d5
        cmp.w   d5,d2
        bne.s   12f
        move.b  d4,d6
        moveq   #(3*16+14),d7           | backdrop colour -> the window band
        bsr     cram_put
12:     addq.w  #1,d2
        cmpi.w  #32,d2
        bcs.s   10b
9:      rts

| cram_put: d7.w = Genesis CRAM entry (0-63), d6.b = SMS colour (--BBGGRR)
cram_put:
        andi.w  #0x3F,d6
        add.w   d6,d6
        lea     GENCOL,a1
        move.w  0(a1,d6.w),d6
        add.w   d7,d7
        swap    d7
        clr.w   d7
        ori.l   #0xC0000000,d7
        move.l  d7,(VDPC).l
        move.w  d6,(VDPD).l
        rts

| nt_put: d7.w = SMS VRAM address of a name table entry (even): converted into its base's Genesis plane
| (rows 0-3 again at rows 28-31, for the SMS's 224-line wrap). Keeps d7.
nt_put: movem.l d0-d1,-(sp)
        move.w  d7,d6
        lsr.w   #8,d6
        lsr.w   #2,d6
        andi.w  #0x0E,d6
        lea     (NTSLOT).w,a0
        move.w  0(a0,d6.w),d1
        beq.s   9f
        move.w  d7,d6
        andi.w  #0x7FF,d6
        cmpi.w  #0x700,d6
        bcc.s   9f
        add.w   d6,d1
        lea     VSHADOW,a0
        adda.w  d7,a0
        move.w  d1,d0                   | the plane's cache: NTCACHE + (plane index * $800) + offset
        sub.w   #0x4000,d0
        lsr.w   #2,d0                   | $4000->0 $6000->$800 ... $C000->$2000
        andi.w  #0x3800,d0
        add.w   d6,d0
        lea     NTCACHE,a1
        adda.w  d0,a1
        move.b  (a0),d0
        lsl.w   #8,d0
        move.b  1(a0),d0
        tst.b   (NTFORCE).w
        bne.s   8f
        cmp.w   (a1),d0
        beq.s   9f                      | unchanged since its last conversion
8:      move.w  d0,(a1)
        moveq   #0,d0
        move.b  1(a0),d0
        add.w   d0,d0
        lea     NTHI,a1
        move.w  0(a1,d0.w),d0
        or.b    (a0),d0
        bsr.s   ntw
        cmpi.w  #256,d6
        bcc.s   9f
        addi.w  #0x700,d1
        bsr.s   ntw
9:      movem.l (sp)+,d0-d1
        rts
ntw:    move.l  d6,-(sp)
        moveq   #0,d6
        move.w  d1,d6
        lsl.l   #2,d6
        lsr.w   #2,d6
        swap    d6
        ori.l   #0x40000000,d6
        move.l  d6,(VDPC).l
        move.w  d0,(VDPD).l
        move.l  (sp)+,d6
        rts

| nt_rebuild: d7.w = base: the whole name table from the shadow into its plane
nt_rebuild:
        st      (NTFORCE).w
        move.w  d7,-(sp)
1:      bsr     nt_put
        addq.w  #2,d7
        move.w  d7,d6
        andi.w  #0x7FF,d6
        cmpi.w  #0x700,d6
        bcs.s   1b
        move.w  (sp)+,d7
        sf      (NTFORCE).w
        rts

| tile_row: d5 = VRAM address of the row's last byte: converts the 4 planar bytes to one packed long at the same address
tile_row:
        move.l  d0,-(sp)
        move.w  d5,d7
        andi.w  #0x3FFC,d7
        lea     VSHADOW,a0
        adda.w  d7,a0
        lea     TT,a1
        moveq   #0,d6
        move.b  (a0)+,d6
        add.w   d6,d6
        add.w   d6,d6
        move.l  0(a1,d6.w),d0
        lea     1024(a1),a1
        moveq   #0,d6
        move.b  (a0)+,d6
        add.w   d6,d6
        add.w   d6,d6
        or.l    0(a1,d6.w),d0
        lea     1024(a1),a1
        moveq   #0,d6
        move.b  (a0)+,d6
        add.w   d6,d6
        add.w   d6,d6
        or.l    0(a1,d6.w),d0
        lea     1024(a1),a1
        moveq   #0,d6
        move.b  (a0)+,d6
        add.w   d6,d6
        add.w   d6,d6
        or.l    0(a1,d6.w),d0
        swap    d7
        clr.w   d7
        ori.l   #0x40000000,d7
        move.l  d7,(VDPC).l
        move.l  d0,(VDPD).l
        move.l  (sp)+,d0
        rts

| vdpr_data / vdpr_stat: -> d6.b
vdpr_data:
        sf      (VLATCH).w
        move.b  (VBUF).w,d6
        move.b  (a6)+,(VBUF).w
        rts
vdpr_stat:
        sf      (VLATCH).w
        move.b  (VSTAT).w,d6
        sf      (VSTAT).w
        sf      (LINEFLAG).w
        rts

| sat_upload (in VInt, every register free): the SMS sprite table -> Genesis sprites (8x8 or 8x16, palette line 1, low priority)
sat_upload:
        lea     VSHADOW,a0
        adda.w  (SATBASE).w,a0
        lea     128(a0),a2
        lea     (VDPD).l,a1
        move.l  #0x58000003,(VDPC).l    | VRAM $D800
        move.w  #0x2000,d4
        btst    #2,(VREGS+6).w
        beq.s   1f
        move.w  #0x2100,d4
1:      moveq   #0,d3
        btst    #1,(VREGS+1).w
        beq.s   2f
        moveq   #1,d3
2:      moveq   #0,d2
        btst    #3,(VREGS+0).w
        beq.s   3f
        moveq   #8,d2
3:      moveq   #0,d1
        moveq   #63,d7
        move.w  d3,d5
        lsl.w   #8,d5                   | size bits in the high byte
4:      moveq   #0,d0
        move.b  (a0)+,d0
        cmpi.b  #0xD0,d0
        beq.s   8f
        addq.w  #1,d0
        cmpi.w  #0xF0,d0
        bcs.s   5f
        subi.w  #0x100,d0
5:      addi.w  #128,d0
        move.w  d0,(a1)
        addq.w  #1,d1
        move.w  d1,d6
        andi.w  #0x3F,d6
        or.w    d5,d6
        move.w  d6,(a1)
        moveq   #0,d6
        move.b  1(a2),d6
        tst.w   d3
        beq.s   6f
        andi.b  #0xFE,d6
6:      or.w    d4,d6
        move.w  d6,(a1)
        moveq   #0,d6
        move.b  (a2),d6
        addi.w  #128,d6
        sub.w   d2,d6
        move.w  d6,(a1)
        addq.l  #2,a2
        dbra    d7,4b
8:      tst.w   d1
        bne.s   9f
        moveq   #0,d0
        move.l  d0,(a1)
        move.l  d0,(a1)
        rts
9:      subq.w  #1,d1
        lsl.w   #3,d1
        addi.w  #0x1802,d1
        swap    d1
        clr.w   d1
        ori.l   #0x40000003,d1
        move.l  d1,(VDPC).l
        move.w  d5,(a1)
        rts

        .else
| --- Mode 4 pass-through (back end A) ---
| The VDP stays in Mode 4: the game's tiles, name table, sprites, scroll and colours are the hardware's own, no conversion.
| What the real Genesis showed (md-mode4-test, 2026-11-15) and what this code is built on:
|  - every control word is a whole command ($4000|a VRAM write, $C000|i CRAM write, $8rvv register); registers 11+ cannot
|    be written in Mode 4, so the increment (R15 = 1: a word steps the address by 2) is set in Mode 5 before the switch;
|  - VRAM is written a word at a time: the word's LOW byte goes to the even SMS address, the high byte to the odd one;
|  - CRAM: one command per colour ($C000|index), the SMS colour in the word's low 6 bits;
|  - frame interrupt (level 6) at line 192, line interrupt (level 4) from R10, both held until a status read.
| The SMS writes bytes; the hardware takes words. The runtime keeps the SMS VRAM in the shadow (VSHADOW, as back end B) and
| sends whole pairs: an even byte waits in DEFPAIR, the odd byte of the same pair sends the word; anything else sends the
| waiting pair first. HWADDR follows the hardware address so a stream of pairs needs one command only. Data port reads come
| from the shadow (the read-ahead buffer as on the SMS).

| m4_enter (boot): Genesis Mode 5 -> SMS Mode 4, display off, the SMS power-on registers
m4_enter:
        lea     (VDPC).l,a0
        move.w  #0x8104,(a0)            | Mode 5, display off
        move.w  #0x8F01,(a0)            | increment 1 (= 2 bytes in Mode 4), set while registers 11+ can still be written
        move.w  #0x8B00,(a0)
        move.w  #0x8C00,(a0)            | H32
        move.w  #0x9000,(a0)
        move.w  #0x8180,(a0)            | M5 clear: Mode 4
        lea     m4regs(pc),a1
        moveq   #(m4regs_end-m4regs)/2-1,d1
1:      move.w  (a1)+,(a0)
        dbra    d1,1b
        move.w  #0x4000,(a0)            | clear the 16 KB
        lea     (VDPD).l,a1
        moveq   #0,d0
        move.w  #0x1FFF,d1
2:      move.w  d0,(a1)
        dbra    d1,2b
        moveq   #0,d1                   | CRAM
3:      move.w  d1,d0
        ori.w   #0xC000,d0
        move.w  d0,(a0)
        clr.w   (a1)
        addq.w  #1,d1
        cmpi.w  #32,d1
        bne.s   3b
        move.w  #0xFFFF,(HWADDR).w
        move.w  #0xFFFF,(DEFPAIR).w
        sf      (VPEND).w
        move.w  (a0),d0                 | nothing pending
        rts
m4regs: .word 0x8004, 0x82FF, 0x83FF, 0x84FF, 0x85FF, 0x86FB, 0x8700, 0x8800, 0x8900, 0x8AFF
m4regs_end:

| vdpw_ctrl: d6.b = byte written to $BF. Address and code are kept here (a6 into the shadow, as back end B); only register
| writes go to the VDP at once (VRAM and CRAM commands are sent with the data).
vdpw_ctrl:
        tst.b   (VLATCH).w
        bne.s   1f
        move.b  d6,(VLOW).w
        st      (VLATCH).w
        rts
1:      sf      (VLATCH).w
        move.b  d6,d5
        lsr.b   #6,d5
        move.b  d5,(VCODE).w
        moveq   #0x3F,d7
        and.b   d6,d7
        lsl.w   #8,d7
        move.b  (VLOW).w,d7             | d7 = the 14-bit address
        cmpi.b  #3,d5
        bne.s   2f
        andi.w  #0x1F,d7
        ori.w   #0x4000,d7              | CRAM: a6 into CRAMBUF
2:      movea.l #VSHADOW,a6
        adda.w  d7,a6
        cmpi.b  #2,d5
        beq.s   3f
        tst.b   d5
        bne.s   4f
        move.b  (a6)+,(VBUF).w          | read setup: the read-ahead buffer
4:      rts
3:      andi.w  #0x0F,d6                | register write
        move.b  (VLOW).w,d5
        lea     (VREGS).w,a0
        move.b  d5,0(a0,d6.w)
        cmpi.b  #11,d6
        bcc.s   4b
        cmpi.b  #1,d6
        bne.s   5f
        andi.b  #0xE3,d5                | R1: M5 stays clear; DMA enable and 30-line (the SMS's M1/M3) off
        bra.s   6f
5:      tst.b   d6
        bne.s   6f
        andi.b  #0xFC,d5                | R0: the Genesis HV latch / display-off bits off
        ori.b   #0x04,d5
6:      cmpi.b  #10,d6
        bne.s   7f
        subq.b  #M4LEAD,d5              | R10: the hardware's line interrupt comes M4LEAD lines early (see m4 early line interrupt)
        bcc.s   7f
        moveq   #0,d5
7:      tst.b   (EARLYV).w
        beq.s   8f
        move.b  (EARLYV).w,d7           | a line interrupt's register write: wait for the line the SMS would write it on
9:      cmp.b   (HVC).l,d7
        bhi.s   9b
        sf      (EARLYV).w
8:      lsl.w   #8,d6
        move.b  d5,d6
        ori.w   #0x8000,d6
        move.w  sr,-(sp)
        ori     #0x0700,sr
        move.w  d6,(VDPC).l
        move.w  #0xFFFF,(HWADDR).w      | (a register write loads the address register too)
        move.w  (sp)+,sr
        rts

| vdpw_data: d6.b = byte written to $BE (d0-d4/a2-a5 kept; a6 steps)
vdpw_data:
        sf      (VLATCH).w
        cmpi.b  #3,(VCODE).w
        beq     m4_cram
        move.w  a6,d5
        andi.w  #0x3FFF,d5              | SMS address
        move.b  d6,(a6)+
        cmpa.l  #VSHADOW+0x4000,a6
        bcs.s   1f
        movea.l #VSHADOW,a6             | (the SMS address wraps at 16 KB)
1:      move.w  (DEFPAIR).w,d7
        btst    #0,d5
        bne.s   3f
        cmp.w   d5,d7                   | even byte: wait for its partner
        beq.s   2f
        move.w  d5,(DEFPAIR).w
        tst.w   d7
        bpl.s   m4_send                 | another pair was waiting: send it
2:      rts
3:      subq.w  #1,d5                   | odd byte: this pair goes now
        move.w  #0xFFFF,(DEFPAIR).w
        tst.w   d7
        bmi.s   4f
        cmp.w   d5,d7
        beq.s   4f
        move.w  d5,-(sp)                | (a different pair was waiting: it goes first)
        bsr.s   m4_send
        move.w  (sp)+,d5
4:      move.w  d5,d7
| m4_send: d7.w = even SMS address: the shadow's pair to the VDP (clobbers d5-d7/a0)
m4_send:
        lea     VSHADOW,a0
        move.b  1(a0,d7.w),d5
        lsl.w   #8,d5
        move.b  0(a0,d7.w),d5           | low byte = the even address
        move.w  sr,-(sp)
        ori     #0x0700,sr
        cmp.w   (HWADDR).w,d7
        beq.s   1f
        move.w  d7,d6
        ori.w   #0x4000,d6
        move.w  d6,(VDPC).l
1:      move.w  d5,(VDPD).l
        addq.w  #2,d7
        move.w  d7,(HWADDR).w
        move.w  (sp)+,sr
        rts
| m4_pair: d7.w = even SMS address whose two shadow bytes were just written: any waiting pair first, then this one
m4_pair:
        move.w  (DEFPAIR).w,d5
        bmi.s   m4_send
        cmp.w   d5,d7
        beq.s   1f
        move.w  d7,-(sp)
        bsr.s   m4_flush
        move.w  (sp)+,d7
        bra.s   m4_send
1:      move.w  #0xFFFF,(DEFPAIR).w
        bra.s   m4_send
| m4_flush: send the waiting even byte's pair, if any (clobbers d5-d7/a0)
m4_flush:
        move.w  (DEFPAIR).w,d7
        bmi.s   1f
        move.w  #0xFFFF,(DEFPAIR).w
        bra.s   m4_send
1:      rts
m4_cram:
        move.w  a6,d7
        andi.w  #0x1F,d7
        lea     (CRAMBUF).l,a0
        move.b  d6,0(a0,d7.w)
        lea     1(a0,d7.w),a6
        cmpi.w  #0x1F,d7
        bne.s   1f
        movea.l a0,a6
1:      ori.w   #0xC000,d7
        andi.w  #0x3F,d6
        move.w  sr,-(sp)
        ori     #0x0700,sr
        move.w  d7,(VDPC).l
        move.w  d6,(VDPD).l
        move.w  #0xFFFF,(HWADDR).w
        move.w  (sp)+,sr
        rts

| m4_bulk: the n (d5.w) bytes just copied into the shadow, ending at a6: their pairs to the VDP in one stream
m4_bulk:
        move.w  d5,-(sp)
        bsr.s   m4_flush
        move.w  (sp)+,d5
        move.w  a6,d6
        sub.w   d5,d6
        andi.w  #0x3FFE,d6              | first pair
        move.w  a6,d7
        subq.w  #1,d7
        andi.w  #0x3FFE,d7              | last pair
        sub.w   d6,d7
        lsr.w   #1,d7                   | pairs - 1
        lea     VSHADOW,a0
        adda.w  d6,a0
        move.w  sr,-(sp)
        ori     #0x0700,sr
        move.w  d6,d5
        ori.w   #0x4000,d5
        move.w  d5,(VDPC).l
        lea     (VDPD).l,a1
1:      move.b  1(a0),d5
        lsl.w   #8,d5
        move.b  (a0),d5
        move.w  d5,(a1)
        addq.l  #2,a0
        dbra    d7,1b
        move.w  a0,d6
        andi.w  #0x3FFF,d6
        move.w  d6,(HWADDR).w
        move.w  (sp)+,sr
        rts

| vdpr_data / vdpr_stat: -> d6.b
vdpr_data:
        sf      (VLATCH).w
        move.b  (VBUF).w,d6
        move.b  (a6)+,(VBUF).w
        rts
vdpr_stat:
        sf      (VLATCH).w
        move.b  (VDPC+1).l,d6           | the real status (reading it acknowledges the interrupts, as on the SMS)
        andi.b  #0xE0,d6
        sf      (VPEND).w
        rts
sat_upload:
        rts
        .endif
