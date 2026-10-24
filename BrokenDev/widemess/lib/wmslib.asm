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

;-----------------------------------------------------------------------------
; void wmsUpload(u8 *table): runs an upload table (wmsgfx.py's gfx_uploads, or a game's own) in forced blank. Records of 12 bytes:
;   +0 kind  +1 arg  +2 dest (24 bits)  +5 cfg  +6 src (24 bits)  +9 0  +10 size (16 bits; 0 = 64 KB)
;   kind 0 end | 1 video RAM (DMA to WMVDATA at dest) | 2 colours (WMCGADDR = dest, DMA to WMCGDATA, size in bytes)
;        3 layer arg: WMBGnMAP = dest / 2 KB, WMBGnCFG = cfg | 4 the WideMess sprite table on, its tiles at arg x 64 KB
;        5 work RAM (DMA through the WRAM port at the 20-bit dest) | 6 sound RAM (DMA through $21F3 at dest) | 7 WMTM = arg
;   table 4,s (address) 6,s (bank); +6 after php/phx/phy/phb
;-----------------------------------------------------------------------------
wmsUpload:
    php
    rep #$30
    phx
    phy
    phb
    sep #$20
    lda 12,s
    pha
    plb                     ; DB = the table's bank: records are read with lda.w n,x
    rep #$20
    lda 10,s
    tax
_up_loop:
    sep #$20
    lda.w 0,x
    bne +
    jmp _up_done
+   cmp #1
    bne _up_k2
    lda.w 2,x
    sta.l $0021C2
    lda.w 3,x
    sta.l $0021C3
    lda.w 4,x
    sta.l $0021C4
    lda #$C5
    jsr _up_dma
    jmp _up_next
_up_k2:
    cmp #2
    bne _up_k3
    lda.w 2,x
    sta.l $0021C6
    lda.w 3,x
    sta.l $0021C7
    lda #$C8
    jsr _up_dma
    jmp _up_next
_up_k3:
    cmp #3
    bne _up_k4
    rep #$20
    lda.w 3,x               ; dest bits 8-23
    lsr a
    lsr a
    lsr a                   ; low byte: dest / 2 KB
    sep #$20
    pha                     ; the map unit
    lda.w 5,x
    pha                     ; the configuration
    lda.w 1,x               ; the layer
    phx
    rep #$20
    and.w #$0007
    tax
    sep #$20
    lda 3,s
    sta.l $0021D8,x
    lda 4,s
    sta.l $0021D0,x
    plx
    pla
    pla
    jmp _up_next
_up_k4:
    cmp #4
    bne _up_k5
    lda.w 1,x
    sta.l $0021A1
    lda #$01
    sta.l $0021A0
    jmp _up_next
_up_k5:
    cmp #5
    bne _up_k6
    lda.w 2,x
    sta.l $002181
    lda.w 3,x
    sta.l $002182
    lda.w 4,x
    sta.l $002183
    lda #$80
    jsr _up_dma
    jmp _up_next
_up_k6:
    cmp #6
    bne _up_k7
    lda.w 2,x
    sta.l $0021F0
    lda.w 3,x
    sta.l $0021F1
    lda.w 4,x
    sta.l $0021F2
    lda #$F3
    jsr _up_dma
    jmp _up_next
_up_k7:
    lda.w 1,x
    sta.l $0021C1
_up_next:
    rep #$20
    txa
    clc
    adc.w #12
    tax
    jmp _up_loop
_up_done:
    rep #$30
    plb
    ply
    plx
    plp
    rtl

; X = a record, A (8 bits) = the B-bus register: DMA channel 0 from the record's source, its size.
; (.ACCU 8: WLA follows sep/rep in the order it reads them, and the code before this label ends 16-bit, so without it the
; "lda #$00" below assembles 3 bytes long and the CPU, 8-bit here, runs its third byte as a BRK.)
.ACCU 8
_up_dma:
    sta.l $004301
    lda #$00
    sta.l $004300
    lda.w 8,x
    sta.l $004304
    rep #$20
    lda.w 6,x
    sta.l $004302
    lda.w 10,x
    sta.l $004305
    sep #$20
    lda #$01
    sta.l $00420B
    rts

.ENDS
