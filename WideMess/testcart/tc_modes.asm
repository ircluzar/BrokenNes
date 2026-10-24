; Test 4's live pages (the SNES modes drawn 400 wide): what each changes in vblank. Their scripts and pictures: gen_testcart.py.

AnimInit:
.ACCU 8
.INDEX 16
  ldx #$0000
  stx anim
  rts

; HofsA: the 16-bit A (10 bits) to the SNES scroll register at $2100 + X (two writes: low, high).
.ACCU 16
HofsA:
  pha
  sep #$20
.ACCU 8
  sta $2100,x
  xba
  and #$03
  sta $2100,x
  rep #$20
.ACCU 16
  pla
  rts
.ACCU 8

; mode 1: the hills 1 pixel a frame (the 64-tile map's two halves pass across the 400), the clouds 1 every 2
Mode1Vblank:
  rep #$20
.ACCU 16
  inc anim
  lda anim
  ldx #<BG1HOFS
  jsr HofsA
  lsr a
  ldx #<BG2HOFS
  jsr HofsA
  sep #$20
.ACCU 8
  rts

; mode 0: the four layers at 4 speeds, BG2 the other way
Mode0Vblank:
  rep #$20
.ACCU 16
  inc anim
  lda anim
  ldx #<BG1HOFS
  jsr HofsA
  lsr a
  ldx #<BG3HOFS
  jsr HofsA
  lsr a
  ldx #$0013            ; BG4HOFS
  jsr HofsA
  lda anim
  asl a
  eor #$FFFF
  inc a
  ldx #<BG2HOFS
  jsr HofsA
  sep #$20
.ACCU 8
  rts

; mode 5: BG1 half a pixel a frame (one half-pixel step of the 800), BG2 a quarter
Mode5Vblank:
  rep #$20
.ACCU 16
  inc anim
  lda anim
  lsr a
  ldx #<BG1HOFS
  jsr HofsA
  lsr a
  ldx #<BG2HOFS
  jsr HofsA
  sep #$20
.ACCU 8
  rts

; mode 2: the offset-per-tile table moves one tile column every 4 frames (BG3HOFS: the wave travels), the mosaic grows and shrinks
Mode2Vblank:
  rep #$20
.ACCU 16
  inc anim
  lda anim
  and #$FFFC
  asl a
  ldx #<BG3HOFS
  jsr HofsA
  lda anim
  lsr a
  lsr a
  lsr a
  lsr a
  and #$000F
  cmp #$0008
  bcc +
  eor #$000F            ; 0-7, then 7-0
+
  sep #$20
.ACCU 8
  asl a
  asl a
  asl a
  asl a
  ora #$02              ; BG2
  sta MOSAIC
  rts

; Mode 7: the plane turns (one 256th a frame from the still picture's angle): A = D = cos / scale, B = sin / scale, C = -B
Mode7Vblank:
  rep #$20
.ACCU 16
  inc anim
  lda anim
  clc
  adc #M7_ANGLE0
  and #$00FF
  asl a
  tax
  lda M7Cos.w,x
  sta nmitmp
  lda M7Sin.w,x
  sta nmitmp+2
  sep #$20
.ACCU 8
  lda nmitmp
  sta M7A
  lda nmitmp+1
  sta M7A
  lda nmitmp+2
  sta M7B
  lda nmitmp+3
  sta M7B
  rep #$20
.ACCU 16
  lda nmitmp+2
  eor #$FFFF
  inc a
  sep #$20
.ACCU 8
  sta M7C
  xba
  sta M7C
  lda nmitmp
  sta M7D
  lda nmitmp+1
  sta M7D
  rts
