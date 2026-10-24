; The test cartridge's video helpers: every register to its reset value, DMA into video RAM and the colours, the title and menu backdrop.

.MACRO VRAM_COPY ARGS src, dst, len
  ldx #(src & $FFFF)
  stx ptr
  lda #(src >> 16)
  sta ptr+2
  ldx #(dst & $FFFF)
  stx vdst
  lda #(dst >> 16)
  sta vdst+2
  ldx #(len & $FFFF)
  stx dlen
  jsr VramDma
.ENDM

.MACRO CG_COPY ARGS src, first, count
  ldx #(src & $FFFF)
  stx ptr
  lda #(src >> 16)
  sta ptr+2
  ldx #first
  stx vdst
  ldx #(count * 2)
  stx dlen
  jsr CgDma
.ENDM

; PRINT row, column, label: the string at label (any bank) at (row, column) of the text buffer
.MACRO PRINT ARGS row, col, str
  ldx #\3
  stx strptr
  lda #:\3
  sta strptr+2
  lda #row
  ldy #col
  jsr At
  jsr Print
.ENDM

; PRINTX label: the string at label printed at X
.MACRO PRINTX ARGS str
  ldy #\1
  sty strptr
  lda #:\1
  sta strptr+2
  jsr Print
.ENDM

; ResetVideo: forced blank, HDMA off, every picture register at its power-on value (the SNES's; WideMess's $21A0-$21EF are 0 except
; WMOBJPRI0-3 = 12, 8, 4, 0: spec section 3).
ResetVideo:
.ACCU 8
.INDEX 16
  lda #$80
  sta INIDISP
  stz HDMAEN
  ldx #$0000
-
  lda ResetTable.w,x
  beq +
  rep #$20
.ACCU 16
  and #$00FF
  tay
  sep #$20
.ACCU 8
  lda ResetTable.w+1,x
  sta $2100,y
  inx
  inx
  bra -
+
  rts
ResetTable:
  .DB $01,0, $02,0, $03,0, $05,0, $06,0, $07,0, $08,0, $09,0, $0A,0, $0B,0, $0C,0
  .DB $0D,0, $0D,0, $0E,0, $0E,0, $0F,0, $0F,0, $10,0, $10,0, $11,0, $11,0, $12,0, $12,0, $13,0, $13,0, $14,0, $14,0
  .DB $15,$80, $16,0, $17,0, $1A,0
  .DB $1B,0, $1B,0, $1C,0, $1C,0, $1D,0, $1D,0, $1E,0, $1E,0, $1F,0, $1F,0, $20,0, $20,0
  .DB $21,0, $23,0, $24,0, $25,0, $26,0, $27,0, $28,0, $29,0, $2A,0, $2B,0, $2C,0, $2D,0, $2E,0, $2F,0, $30,0, $31,0, $32,$E0, $33,0
  .DB $B4,0, $B0,0, $B0,0, $B1,0, $B1,0, $B2,0, $B2,0, $B3,0, $B3,0
  .DB $A0,0, $A1,0, $A2,0, $A3,0, $A6,12, $A7,8, $A8,4, $A9,0
  .DB $B5,0, $B6,0, $B7,0, $B8,0, $B9,0, $BA,0, $BB,0, $BC,0, $BD,0, $BE,0
  .DB $C0,0, $C1,0, $C2,0, $C3,0, $C4,0, $C6,0, $C7,0
  .DB $D0,0, $D1,0, $D2,0, $D3,0, $D4,0, $D5,0, $D6,0, $D7,0, $D8,0, $D9,0, $DA,0, $DB,0, $DC,0, $DD,0, $DE,0, $DF,0
  .DB $E0,0, $E0,0, $E1,0, $E1,0, $E2,0, $E2,0, $E3,0, $E3,0, $E4,0, $E4,0, $E5,0, $E5,0, $E6,0, $E6,0, $E7,0, $E7,0
  .DB $E8,0, $E8,0, $E9,0, $E9,0, $EA,0, $EA,0, $EB,0, $EB,0, $EC,0, $EC,0, $ED,0, $ED,0, $EE,0, $EE,0, $EF,0, $EF,0
  .DB 0

; VramDma: dlen bytes (0 = 65,536) from [ptr] to video RAM byte address vdst, through WMVDATA (DMA channel 0). In forced blank or vblank.
VramDma:
.ACCU 8
.INDEX 16
  lda vdst
  sta WMVADDL
  lda vdst+1
  sta WMVADDM
  lda vdst+2
  sta WMVADDH
  stz DMAP0
  lda #<WMVDATA
  sta BBAD0
  ldx ptr
  stx A1T0
  lda ptr+2
  sta A1B0
  ldx dlen
  stx DAS0
  lda #$01
  sta MDMAEN
  rts

; VramFill: dlen bytes of the byte at [ptr] (a fixed source) to video RAM at vdst.
VramFill:
  lda vdst
  sta WMVADDL
  lda vdst+1
  sta WMVADDM
  lda vdst+2
  sta WMVADDH
  lda #$08              ; the A-bus address stays put
  sta DMAP0
  lda #<WMVDATA
  sta BBAD0
  ldx ptr
  stx A1T0
  lda ptr+2
  sta A1B0
  ldx dlen
  stx DAS0
  lda #$01
  sta MDMAEN
  rts

; CgDma: dlen bytes of colours (2 a colour) from [ptr] to the colour entries from vdst (0-1023), through WMCGDATA.
CgDma:
  lda vdst
  sta WMCGADDL
  lda vdst+1
  sta WMCGADDH
  stz DMAP0
  lda #<WMCGDATA
  sta BBAD0
  ldx ptr
  stx A1T0
  lda ptr+2
  sta A1B0
  ldx dlen
  stx DAS0
  lda #$01
  sta MDMAEN
  rts

; SetupUI: A = 0 the menu's backdrop, 1 the title's (with the logo). Forced blank; the font, the tiles, the 1,024 colours, the eight maps
; (BG1 the text, BG2 the panels, BG3 the logo, BG4-BG5 the mountains, BG6-BG7 the stars, BG8 the sky), 400 wide in the WideMess mode.
SetupUI:
.ACCU 8
.INDEX 16
  sta uikind
  jsr ResetVideo
  VRAM_COPY FONT8, VR_FONT, 0
  VRAM_COPY UITILES, VR_UI, UITILES_LEN
  CG_COPY UIPAL, 0, 1024
  VRAM_COPY MAP_LOGO, VR_MAPS + $2000, 4096
  VRAM_COPY MAP_MNEAR, VR_MAPS + $3000, 4096
  VRAM_COPY MAP_MFAR, VR_MAPS + $4000, 4096
  VRAM_COPY MAP_NEAR, VR_MAPS + $5000, 4096
  VRAM_COPY MAP_FAR, VR_MAPS + $6000, 4096
  VRAM_COPY MAP_SKY, VR_MAPS + $7000, 4096
  ldx #$0000
-
  txa
  asl a
  clc
  adc #MAPREG
  sta WMBGMAP,x
  lda UiCfg.w,x
  sta WMBGCFG,x
  inx
  cpx #8
  bne -
  jsr ClearText
  jsr ClearPanel
  lda uikind
  beq +
  lda #$FF              ; the title: all eight
  bra ++
+
  lda #$FB              ; the menu: all but the logo
++
  sta WMTM
  lda #$03
  sta WMCTRL            ; 400 wide, the WideMess mode
  rep #$20
.ACCU 16
  lda #UiVblank
  sta vbhook
  sep #$20
.ACCU 8
  rts
; WMBGnCFG: map 64 x 32; colour bank; tile unit (BG1 the font in unit 1, the rest unit 2)
UiCfg:
  .DB $1C, $2C, $24, $24, $24, $20, $20, $20

; ScreenOn: in forced blank, the text and the panels straight to video RAM, then the picture on.
ScreenOn:
.ACCU 8
  jsr DmaText
  jsr DmaPanel
  stz textdirty
  stz paneldirty
  lda #$0F
  sta INIDISP
  rts

; UiVblank: the backdrop's parallax (the near mountains 1/2 a pixel a frame, the far ones and the near stars 1/4, the far stars 1/8).
UiVblank:
.ACCU 8
.INDEX 16
  rep #$20
.ACCU 16
  lda RES_FRAME
  lsr a
  sta nmitmp            ; BG4
  lsr a
  sta nmitmp+2          ; BG5, BG6
  lsr a
  sta nmitmp+4          ; BG7
  sep #$20
.ACCU 8
  lda nmitmp
  sta WMBGHOFS+6
  lda nmitmp+1
  and #$03
  sta WMBGHOFS+6
  lda nmitmp+2
  sta WMBGHOFS+8
  lda nmitmp+3
  and #$03
  sta WMBGHOFS+8
  lda nmitmp+2
  sta WMBGHOFS+10
  lda nmitmp+3
  and #$03
  sta WMBGHOFS+10
  lda nmitmp+4
  sta WMBGHOFS+12
  lda nmitmp+5
  and #$03
  sta WMBGHOFS+12
  rts
