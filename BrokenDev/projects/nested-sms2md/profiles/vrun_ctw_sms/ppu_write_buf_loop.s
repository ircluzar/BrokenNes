| CTW ppu_write_buf's loop ($1586, bank 0): for B bytes, (HL) against nt_tile[DE]; an unchanged byte is skipped here,
| a changed one goes to the translated loop (it calls nt_change and comes back to $1586). B == 0: the original's RET.
| In: d3 = HL (source), d2 = DE (cell index), d1 high byte = B.
1:      cmpi.w  #0xC000,d3
        bcs     ORIG_Z000_1586          | (source outside work RAM: the original)
        movea.w d3,a0
        move.b  (a0),d0                 | LD A,(HL)
        lea     (-16049).w,a1           | $C14F nt_tile
        move.w  d2,d7
        cmp.b   0(a1,d7.w),d0
        bne     ORIG_Z000_1586          | changed: the original calls nt_change
        addq.w  #1,d3
        addq.w  #1,d2
        subi.w  #0x100,d1
        cmpi.w  #0x100,d1
        bcc.s   1b
        moveq   #4,d4                   | (Z from the last CP)
        move.b  1(a5),-(sp)             | RET
        move.w  (sp)+,d7
        move.b  (a5),d7
        addq.w  #2,a5
        jra     z_dispatch
