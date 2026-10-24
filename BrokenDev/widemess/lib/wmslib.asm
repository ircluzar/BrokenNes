; wmslib.asm: the WideMess library's register access in 65816 (BrokenDev's WideMess devkit, see widemess.h).
;
; Why assembly: 816-tcc builds a pointer from a computed address (a register base plus a layer number) with a stale bank word, so a C
; store through (vuint8 *)(0x21D0 + n) can land in any bank. Fixed-address register macros (REG_WMCTRL ...) compile correctly and stay
; in widemess.c; everything that indexes a register is here.
;
; ABI (816-tcc, read off its own call sites): arguments are pushed last first (u8 as one byte, u16 as one word, u32 and pointers as two
; words, high word first), the caller pops them, a jsl return address is 1-3,s, so the first argument is at 4,s. Results come back in
; tcc__r0 (and tcc__r1 for the high word). Every routine saves P and the registers it uses, uses long addressing for its stores (the data
; bank is the caller's), and returns with rtl.

.include "hdr.asm"

.SECTION ".wmslib_text" SUPERFREE

.ACCU 16
.INDEX 16

;-----------------------------------------------------------------------------
; void wms_poke(u16 addr, u8 value): one byte at $00:addr (a register).
;   addr 4,s  value 6,s; +3 after php/phx
;-----------------------------------------------------------------------------
wms_poke:
    php
    rep #$30
    phx
    lda 7,s
    tax
    sep #$20
    lda 9,s
    sta.l $000000,x
    rep #$20
    plx
    plp
    rtl

;-----------------------------------------------------------------------------
; void wms_dma(u8 ch, u8 mode, u8 bbad, u8 *src, u16 size): one general DMA on channel ch, A bus -> B bus $21bbad. size 0 = 64 KB.
;   src is a pointer, not a u32: 816-tcc pushes a pointer as its bank word and its address, but (u32)ptr keeps only the address
;   (sign-extended), so a cast would lose the bank.
;   ch 4,s  mode 5,s  bbad 6,s  src 7-10,s  size 11-12,s; +3 after php/phx
;-----------------------------------------------------------------------------
wms_dma:
    php
    rep #$30
    phx
    lda 7,s                 ; ch (low byte)
    and.w #$0007
    asl a
    asl a
    asl a
    asl a
    tax                     ; X = $10 x ch
    sep #$20
    lda 8,s                 ; mode
    sta.l $004300,x
    lda 9,s                 ; B-bus register
    sta.l $004301,x
    lda 12,s                ; source bank
    sta.l $004304,x
    rep #$20
    lda 10,s                ; source address
    sta.l $004302,x
    lda 14,s                ; size
    sta.l $004305,x
    lda 7,s
    and.w #$0007
    tax
    sep #$20
    lda.l wms_dma_bits,x
    sta.l $00420B           ; MDMAEN: go (the CPU waits for the transfer)
    rep #$20
    plx
    plp
    rtl

wms_dma_bits:
    .db $01, $02, $04, $08, $10, $20, $40, $80

;-----------------------------------------------------------------------------
; void wmsScrollNow(u8 n, u16 x, u16 y): layer n's WMBGnHOFS / WMBGnVOFS ($21E0 + 2n, $21E1 + 2n), each written twice (low, high).
;   n 4,s  x 5-6,s  y 7-8,s; +3 after php/phx
;-----------------------------------------------------------------------------
wmsScrollNow:
    php
    rep #$30
    phx
    lda 7,s
    and.w #$0007
    asl a
    tax                     ; X = 2n
    sep #$20
    lda 8,s
    sta.l $0021E0,x
    lda 9,s
    sta.l $0021E0,x
    lda 10,s
    sta.l $0021E1,x
    lda 11,s
    sta.l $0021E1,x
    rep #$20
    plx
    plp
    rtl

.ENDS
