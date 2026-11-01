; game.asm: Wide Blaster's one hot loop in 65816. The C version of the bullet test (20 enemies x 24 bullets a frame, in 816-tcc's
; code) ran past the frame at full fire even in fast mode; this one is a few cycles a bullet. VRUN-style optimisation still matters.
;
; ABI (widemess.c and wmslib.asm say more): arguments pushed last first, the first at 4,s after jsl; a result in tcc__r0.

.include "hdr.asm"
.include "brokendev_dbg.inc"        ; the BrokenDev debug port: a timer around bullet_hit in dev builds, nothing in release builds

.SECTION ".game_text" SUPERFREE

.ACCU 16
.INDEX 16

;-----------------------------------------------------------------------------
; u8 bullet_hit(s16 left, s16 top, u16 w, u16 h): the first live bullet (bul_on, bul_x, bul_y in far RAM) whose position is inside
; the box [left, left + w) x [top, top + h), or 255.
;   left 4,s  top 6,s  w 8,s  h 10,s; +5 after php/phx/phy
;-----------------------------------------------------------------------------
bullet_hit:
    php
    rep #$30
    DBG_TIMER_START 2
    phx
    phy
    ldx.w #0
_bh_loop:
    sep #$20
    lda.l bul_on,x
    rep #$20
    and.w #$00FF
    beq _bh_next
    txa
    asl a
    tax                     ; X = 2 x bullet
    lda.l bul_x,x
    sec
    sbc 9,s                 ; - left
    cmp 13,s                ; < w, unsigned: left <= x < left + w
    bcs _bh_miss
    lda.l bul_y,x
    sec
    sbc 11,s
    cmp 15,s
    bcs _bh_miss
    txa
    lsr a
    sta.b tcc__r0           ; the bullet
    DBG_TIMER_STOP 2
    ply
    plx
    plp
    rtl
_bh_miss:
    txa
    lsr a
    tax
_bh_next:
    inx
    cpx.w #24
    bne _bh_loop
    lda.w #255
    sta.b tcc__r0
    DBG_TIMER_STOP 2
    ply
    plx
    plp
    rtl

.ENDS
