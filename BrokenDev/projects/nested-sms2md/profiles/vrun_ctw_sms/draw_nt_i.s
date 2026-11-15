| CTW draw_nt_i ($0DF6, bank 0): one name table cell from the NES name table copy (nt_tile/nt_pal -> the cell's BG slot).
| Hand-written 68000 version of the common case (the slot is already cached: bump its reference count and write the cell);
| the uncached case (a new slot: CALL bg_slot) goes to the translated original. Z80 state at the RET as the original leaves it.
| In: d3 = HL = cell index i (0-959).
        move.w  d3,d7
        lea     (-16049).w,a0           | $C14F nt_tile
        moveq   #0,d6
        move.b  0(a0,d7.w),d6           | B = tile
        lea     (-15025).w,a0           | $C54F nt_pal
        move.b  0(a0,d7.w),d5           | C = palette
        move.w  d6,d7
        lsl.w   #2,d7                   | HL = B * 4
        add.b   d5,d7                   | L = C + L (8-bit, as the Z80 does)
        lea     (-14095).w,a0           | $C8F1 slot cache
        move.b  0(a0,d7.w),d0           | A
        beq     ORIG_Z000_0DF6          | not cached: the translated original (state untouched so far)
        subq.b  #1,d0                   | A - 1 = the slot
        moveq   #0,d7
        move.b  d0,d7
        lea     (-11830).w,a0           | $D1CA slot reference counts (saturating)
        addq.b  #1,0(a0,d7.w)
        bne.s   1f
        subq.b  #1,0(a0,d7.w)
1:      move.w  d7,d2                   | DE = slot (D = 0)
        move.b  d6,d1                   | B:C = tile:slot, as at the original's RET
        lsl.w   #8,d1
        move.b  d0,d1
        add.w   d3,d3                   | HL = $3800 + i * 2
        addi.w  #0x3800,d3
        | OUT ($BF) x2 (VRAM write address), OUT ($BE) slot, OUT ($BE) 0
        move.b  d3,(VLOW).w
        sf      (VLATCH).w
        move.b  #1,(VCODE).w
        movea.l #VSHADOW,a6
        adda.w  d3,a6
        .if VIDEO_MODE == 5
        move.b  d0,(a6)+
        clr.b   (a6)+
        move.w  d3,d7
        lsr.w   #5,d7
        lea     (DIRTY).w,a0
        st      0(a0,d7.w)
        .else
        move.b  d0,(a6)+                | Mode 4: the whole name table entry (an even address) as one pair
        clr.b   (a6)+
        move.w  d3,d7
        jsr     (m4_pair).l
        .endif
        moveq   #0,d0                   | XOR A: A = 0, Z set
        moveq   #4,d4
        move.b  1(a5),-(sp)             | RET
        move.w  (sp)+,d7
        move.b  (a5),d7
        addq.w  #2,a5
        jra     z_dispatch
