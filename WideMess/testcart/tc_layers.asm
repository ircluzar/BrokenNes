; Test 1: eight layers of 256 colours (spec section 3, the WideMess mode), milestone 1's test ROM (WideMess/testroms/layers) as it was: its
; 1,024 colours, its 33 tiles of 8 bits a pixel and its eight 64 x 32 maps (each layer a quarter of the screen in 2 x 2-tile blocks, the odd
; layers' alternate block rows in front), each layer scrolling at its own speed ((n + 1) / 2 pixels a frame), 400 x 224.
; Self-checks: the colours, the tiles and the maps read back through WMCGDATA and WMVDATA as they were written.
; By eye: every layer draws its blocks in all 255 colours of its bank; switch the chosen layer off (A) to see which blocks were its.
; The bands at the top and the bottom are the SNES picture (WMCTRL = 0, written by HDMA line by line: spec 3.1) in SNES mode 0: the legend.
; Results: RES_T1 (README.md).

.DEFINE RES_T1CG    RES_T1 + $00
.DEFINE RES_T1TILES RES_T1 + $02
.DEFINE RES_T1MAPS  RES_T1 + $04
.DEFINE RES_T1ON    RES_T1 + $06
.DEFINE RES_T1SEL   RES_T1 + $07
.DEFINE RES_T1STILL RES_T1 + $08

T1Enter:
.ACCU 8
.INDEX 16
  lda #1
  jsr TestGate
  jsr BeginTest
  jsr ResetVideo
  CG_COPY M1PAL, 0, 1024
  VRAM_COPY M1TILES, $10000, M1TILES_LEN
  VRAM_COPY M1MAPS, $20000, M1MAPS_LEN
  jsr T1Readback
  ; the eight layers: map n at $20000 + n x 4 KB, its tiles in 64 KB unit 1, colour bank n mod 4, 64 x 32; vertical scroll n x 16
  ldx #$0000
  ldy #$0000
-
  txa
  asl a
  clc
  adc #$40
  sta WMBGMAP,x
  txa
  and #$03
  asl a
  asl a
  ora #$10
  sta WMBGCFG,x
  txa
  asl a
  asl a
  asl a
  asl a
  sta WMBGVOFS,y
  lda #$00
  sta WMBGVOFS,y
  sta WMBGHOFS,y
  sta WMBGHOFS,y
  iny
  iny
  inx
  cpx #8
  bne -
  ldx #$0000
-
  stz scroll,x
  inx
  cpx #16
  bne -
  lda #$FF
  sta RES_T1ON
  sta WMTM
  stz RES_T1SEL
  stz RES_T1STILL
  ; the bands: SNES mode 0, BG1 the legend (the font at word 0, the map at word $0400), its palettes in colours 1-19 (five styles)
  jsr SnesFont
  ldx #$0000
-
  txa
  asl a
  asl a
  inc a
  sta CGADD             ; colour 4 s + 1
  phx
  rep #$20
.ACCU 16
  txa
  asl a
  asl a
  asl a
  tax                   ; SnesPalette + 8 s (bytes)
  sep #$20
.ACCU 8
  lda SnesPalette.w+2,x
  sta CGDATA
  lda SnesPalette.w+3,x
  sta CGDATA
  lda SnesPalette.w+4,x
  sta CGDATA
  lda SnesPalette.w+5,x
  sta CGDATA
  lda SnesPalette.w+6,x
  sta CGDATA
  lda SnesPalette.w+7,x
  sta CGDATA
  plx
  inx
  cpx #5
  bne -
  stz BGMODE
  lda #$04
  sta BG1SC
  stz BG12NBA
  lda #$01
  sta TM
  lda #$FF
  sta BG1VOFS
  lda #$03
  sta BG1VOFS           ; -1: screen line 0 shows the map's line 0
  jsr ClearText
  jsr T1Legend
  jsr DmaSnesText
  ; WMCTRL by HDMA (channel 1): the SNES picture, the WideMess mode at 400, the SNES picture
  stz $4310
  lda #<WMCTRL
  sta $4311
  ldx #T1Bands
  stx $4312
  stz $4314
  lda #$03
  sta WMCTRL
  lda #$02
  sta HDMAEN
  rep #$20
.ACCU 16
  lda #T1Vblank
  sta vbhook
  sep #$20
.ACCU 8
  jsr EndTest
  lda #$0F
  sta INIDISP
  jsr ShowAWhile
  lda runall
  beq +
  jmp RunAllNext
+
  rts
T1Bands:
  .DB 24, $00, 92, $03, 92, $03, 1, $00, 0

; T1Readback: the colours, the tiles and the maps through the read ports, against the ROM they came from (forced blank).
T1Readback:
  ; the colours (15 bits each)
  stz WMCGADDL
  stz WMCGADDH
  lda #<WMCGDATA
  jsr ReadChunk
  ldx #(M1PAL & $FFFF)
  stx ptr2
  lda #(M1PAL >> 16)
  sta ptr2+2
  ldx #$0000
  stx patoff
  stx errs2
  ldx #2048
  stx dlen
  jsr CmpScratchCg
  ldx errs2
  stx RES_T1CG
  lda #0
  jsr T1Judge
  ; the tiles
  stz WMVADDL
  stz WMVADDM
  lda #$01
  sta WMVADDH
  lda #<WMVDATA
  jsr ReadChunk
  ldx #(M1TILES & $FFFF)
  stx ptr2
  lda #(M1TILES >> 16)
  sta ptr2+2
  ldx #$0000
  stx patoff
  stx errs2
  ldx #M1TILES_LEN
  stx dlen
  jsr CmpScratch
  ldx errs2
  stx RES_T1TILES
  lda #1
  jsr T1Judge
  ; the maps: 32 KB in 8 reads
  stz WMVADDL
  stz WMVADDM
  lda #$02
  sta WMVADDH
  ldx #(M1MAPS & $FFFF)
  stx ptr2
  lda #(M1MAPS >> 16)
  sta ptr2+2
  ldx #$0000
  stx patoff
  stx errs2
  ldx #4096
  stx dlen
  stz loop1
-
  lda #<WMVDATA
  jsr ReadChunk
  jsr CmpScratch
  rep #$20
.ACCU 16
  lda patoff
  clc
  adc #4096
  sta patoff
  sep #$20
.ACCU 8
  inc loop1
  lda loop1
  cmp #8
  bne -
  ldx errs2
  stx RES_T1MAPS
  lda #2
  jmp T1Judge
; T1Judge: errs2 = 0 passes (the status goes to the scratch text row; the legend shows the test's status)
T1Judge:
  ldx #$0000
  rep #$20
.ACCU 16
  lda errs2
  sep #$20
.ACCU 8
  beq +
  clc
  jmp Judge
+
  sec
  jmp Judge

; T1Legend: the bands' text (columns 0-31 of rows 0-2 and 26-27), converted for the SNES layer.
T1Legend:
  PRINT 0, 0, TxtT1Title
  lda #0
  ldy #28
  jsr At
  lda RES_STATUS
  jsr PrintStatus
  PRINT 1, 0, TxtT1Layer
  ; each layer: its number (yellow when chosen) and ON / OFF below it
  stz tmp+6
@layer:
  lda tmp+6
  asl a
  clc
  adc tmp+6
  clc
  adc #6
  sta col               ; its column
  lda tmp+6
  cmp RES_T1SEL
  bne +
  lda #S_YELLOW
  bra ++
+
  lda #S_WHITE
++
  jsr SetStyle
  lda #1
  ldy col
  jsr At
  lda tmp+6
  cmp RES_T1SEL
  bne +
  lda #G_DOWN
  bra ++
+
  lda #' '
++
  jsr PutChar
  lda tmp+6
  inc a
  ora #'0'
  jsr PutChar
  lda #2
  ldy col
  jsr At
  ; on or off
  lda tmp+6
  rep #$20
.ACCU 16
  and #$00FF
  tay
  sep #$20
.ACCU 8
  lda BitMasks.w,y
  and RES_T1ON
  beq +
  PRINTX TxtT1On
  bra ++
+
  PRINTX TxtT1Off
++
  inc tmp+6
  lda tmp+6
  cmp #8
  bne @layer
  PRINT 26, 0, TxtT1Help1
  PRINT 27, 0, TxtT1Help2
  lda RES_T1STILL
  beq +
  PRINT 27, 18, TxtT1Go
+
  jmp TextToSnes

TxtT1Title: .DB S_YELLOW, "1 EIGHT LAYERS, 256 COLOURS", EOS
TxtT1Layer: .DB S_GREY, "LAYER", EOS
TxtT1On:    .DB S_GREEN, "ON", EOS
TxtT1Off:   .DB S_GREY, "--", EOS
TxtT1Help1: .DB S_GREY, G_LEFT, G_RIGHT, ": LAYER  A: ON/OFF  X: ALL", EOS
TxtT1Help2: .DB S_GREY, "Y: ALONE  SELECT: STOP  B: MENU", EOS
TxtT1Go:    .DB S_YELLOW, "SELECT: GO  ", EOS

T1Frame:
.ACCU 8
.INDEX 16
  jsr BackOnB
  lda pad+1
  and #>PAD_LEFT
  beq +
  lda RES_T1SEL
  dec a
  and #$07
  sta RES_T1SEL
+
  lda pad+1
  and #>PAD_RIGHT
  beq +
  lda RES_T1SEL
  inc a
  and #$07
  sta RES_T1SEL
+
  lda RES_T1SEL
  rep #$20
.ACCU 16
  and #$00FF
  tay
  sep #$20
.ACCU 8
  lda pad
  and #<PAD_A
  beq +
  lda BitMasks.w,y
  eor RES_T1ON
  sta RES_T1ON
+
  lda pad
  and #<PAD_X
  beq +
  lda #$FF
  sta RES_T1ON
+
  lda pad+1
  and #>PAD_Y
  beq +
  lda BitMasks.w,y
  sta RES_T1ON
+
  lda pad+1
  and #>PAD_SELECT
  beq +
  lda RES_T1STILL
  eor #$01
  sta RES_T1STILL
+
  rep #$20
.ACCU 16
  lda pad
  sep #$20
.ACCU 8
  beq +
  jsr ClearText
  jsr T1Legend
  lda #1
  sta t1dirty
+
  rts

; T1Vblank: the scrolls (layer n moves (n + 1) / 2 pixels a frame), WMTM, the legend when it changed.
T1Vblank:
.ACCU 8
.INDEX 16
  lda RES_T1ON
  sta WMTM
  lda RES_T1STILL
  bne @legend
  ldx #$0000
  ldy #$0001
-
  rep #$20
.ACCU 16
  tya
  clc
  adc scroll,x
  sta scroll,x
  lsr a
  and #$03FF
  sep #$20
.ACCU 8
  sta WMBGHOFS,x
  xba
  sta WMBGHOFS,x
  inx
  inx
  iny
  cpy #9
  bne -
@legend:
  lda t1dirty
  beq +
  stz t1dirty
  jsr DmaSnesText
+
  rts
