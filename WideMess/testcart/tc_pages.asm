; The live pages' code (tc_picture.asm plays their scripts): the swarm (test 2, with its self-checks) and the spotlight (test 3).

; PageTextToBuf: the page's text layer map (PageTexts) into the text buffer, so live values print over it (forced blank: nothing is shown
; from the buffer until textdirty).
PageTextToBuf:
.ACCU 8
.INDEX 16
  lda pgpage
  rep #$20
.ACCU 16
  and #$00FF
  sta tmp
  asl a
  clc
  adc tmp
  tax
  lda.l PageTexts,x
  sta ptr
  sep #$20
.ACCU 8
  lda.l PageTexts+2,x
  sta ptr+2
  ldy #$0000
-
  lda [ptr],y
  tyx
  sta.l TEXTBUF,x
  iny
  cpy #4096
  bne -
  rts

; ReadWideOam: the WideMess sprite table into OAMBUF (2 KB through WMOAMDATA; forced blank).
ReadWideOam:
  stz WMOAMADDL
  stz WMOAMADDH
  lda #$80
  sta DMAP0
  lda #<WMOAMDATA
  sta BBAD0
  ldx #(OAMBUF & $FFFF)
  stx A1T0
  lda #(OAMBUF >> 16)
  sta A1B0
  ldx #2048
  stx DAS0
  lda #$01
  sta MDMAEN
  stz WMOAMADDL
  stz WMOAMADDH
  rts

; DmaWideOam: OAMBUF to the WideMess sprite table (in vblank).
DmaWideOam:
  stz WMOAMADDL
  stz WMOAMADDH
  stz DMAP0
  lda #<WMOAMDATA
  sta BBAD0
  ldx #(OAMBUF & $FFFF)
  stx A1T0
  lda #(OAMBUF >> 16)
  sta A1B0
  ldx #2048
  stx DAS0
  lda #$01
  sta MDMAEN
  rts

; ================================================================================================ test 2: the swarm

SwarmInit:
.ACCU 8
.INDEX 16
  jsr ReadWideOam
  jsr PageTextToBuf
  ldx #$0000
  stx anim
  stx plx_
  stx ply_
  ; each swarm sprite's phases from SwarmData (px, py)
  ldx #$0000
  ldy #$0000
-
  lda SwarmData.w+4,y
  sta.l SWARMPH,x
  lda SwarmData.w+5,y
  sta.l SWARMPH+1,x
  inx
  inx
  rep #$20
.ACCU 16
  tya
  clc
  adc #8
  tay
  sep #$20
.ACCU 8
  cpx #(SWARM_COUNT * 2)
  bne -
  rts

SwarmVblank:
  lda WMOBJSTAT         ; the flags of the frame just drawn
  sta objstat
  jmp DmaWideOam

; SwarmFrame: every swarm sprite one step along its path (x = cx + SX[phase x], y = cy + SY[phase y]; the phases move by sx, sy), the
; planet (x = 300 + SX[64 + t] / 4, y = 70 + SY[2 t] / 4); the live line.
SwarmFrame:
.ACCU 8
.INDEX 16
  ; the planet: table entry 72
  lda plx_
  inc a
  sta plx_
  clc
  adc #64
  rep #$20
.ACCU 16
  and #$00FF
  asl a
  tax
  lda SwarmSX.w,x
  cmp #$8000
  ror a
  cmp #$8000
  ror a
  clc
  adc #300
  sta.l OAMBUF + 72 * 8
  sep #$20
.ACCU 8
  xba
  and #$03
  sta.l OAMBUF + 72 * 8 + 1
  lda ply_
  clc
  adc #2
  sta ply_
  rep #$20
.ACCU 16
  and #$00FF
  asl a
  tax
  lda SwarmSY.w,x
  cmp #$8000
  ror a
  cmp #$8000
  ror a
  clc
  adc #70
  sta.l OAMBUF + 72 * 8 + 2
  sep #$20
.ACCU 8
  xba
  and #$03
  sta.l OAMBUF + 72 * 8 + 3
  ; the swarm: entries SWARM_FIRST on
  ldx #$0000            ; the sprite (x 2: its phases)
  ldy #$0000            ; its SwarmData (x 8)
  stz loop2
  stz loop2+1
@sprite:
  ; x
  lda.l SWARMPH,x
  clc
  adc SwarmData.w+6,y   ; sx
  sta.l SWARMPH,x
  phx
  rep #$20
.ACCU 16
  and #$00FF
  asl a
  tax
  lda SwarmSX.w,x
  clc
  adc SwarmData.w,y     ; cx - size / 2
  sta tmp
  sep #$20
.ACCU 8
  plx
  ; y
  lda.l SWARMPH+1,x
  clc
  adc SwarmData.w+7,y   ; sy
  sta.l SWARMPH+1,x
  phx
  rep #$20
.ACCU 16
  and #$00FF
  asl a
  tax
  lda SwarmSY.w,x
  clc
  adc SwarmData.w+2,y   ; cy - size / 2
  sta tmp+2
  ; the table entry: (SWARM_FIRST + sprite) x 8
  lda loop2
  asl a
  asl a
  asl a
  clc
  adc #(SWARM_FIRST * 8)
  tax
  lda tmp
  and #$03FF
  sta.l OAMBUF,x        ; X, 10 bits (bytes 0-1)
  lda tmp+2
  and #$03FF
  sta.l OAMBUF+2,x      ; Y (bytes 2-3)
  inc loop2
  tya
  clc
  adc #8
  tay
  sep #$20
.ACCU 8
  plx
  inx
  inx
  cpx #(SWARM_COUNT * 2)
  bne @sprite
  ; the live line: WMOBJSTAT now, the self-checks' result
  PRINT 22, 2, TxtSwarmLive
  lda #23
  sta textrow           ; row 22 alone to video RAM (the sprite table takes most of the vblank)
  rts
TxtSwarmLive: .DB S_WHITE, "WMOBJSTAT $", HEX8, objstat & $FF, 0, S_GREY, " (BIT 6: OVER 64)   ", S_WHITE, "CHECKS ", STATUS, (RES_STATUS + 1) & $FF, (RES_STATUS + 1) >> 8, EOS

; ---------------------------------------------------------------------------------------------------- test 2's self-checks (spec 3.3)

; On the swarm page (its script played, the screen on), five sprite layouts on rows 100-107, each for three frames, WMOBJSTAT read in the
; vblank after each frame: 72 sprites 8 wide (more than 64 on a line: bit 6), 30 sprites 64 wide (240 slivers, more than 128: bit 7),
; exactly 64 sprites (bit 6 stays 0), exactly 16 sprites 64 wide (128 slivers: bit 7 stays 0), none (the flags clear each frame).
; Results: RES_T2 + 0-4 the five WMOBJSTAT reads, + 8-12 'P' / 'F'.
T2Layouts:            ; count, width code (0: 8, 3: 64), x step, what WMOBJSTAT AND $C0 must be
  .DB 72, 0, 5, $40
  .DB 30, 3, 11, $80
  .DB 64, 0, 5, $00
  .DB 16, 3, 21, $00
  .DB 0, 0, 0, $00

T2Checks:
.ACCU 8
.INDEX 16
  jsr BeginTest
  stz loop0             ; the layout
@layout:
  ; every entry hidden (Y = 240), then the layout's sprites at Y = 100
  ldx #$0000
-
  lda #$00
  sta.l OAMBUF,x
  sta.l OAMBUF+1,x
  sta.l OAMBUF+3,x
  sta.l OAMBUF+4,x
  sta.l OAMBUF+5,x
  sta.l OAMBUF+7,x
  lda #240
  sta.l OAMBUF+2,x
  lda #BALL_PAL
  sta.l OAMBUF+6,x
  rep #$20
.ACCU 16
  txa
  clc
  adc #8
  tax
  sep #$20
.ACCU 8
  cpx #2048
  bne -
  lda loop0
  asl a
  asl a
  rep #$20
.ACCU 16
  and #$00FF
  tay                   ; its T2Layouts entry
  sep #$20
.ACCU 8
  lda T2Layouts.w,y
  sta loop1             ; how many
  stz loop1+1
  ldx #$0000
  stz tmp               ; x
  stz tmp+1
-
  lda loop1
  beq +
  rep #$20
.ACCU 16
  lda tmp
  sta.l OAMBUF,x        ; X (and its two high bits, 0)
  lda #100
  sta.l OAMBUF+2,x      ; Y 100
  sep #$20
.ACCU 8
  lda T2Layouts.w+1,y
  ora #$30              ; its width, 8 high, priority 3
  sta.l OAMBUF+7,x
  lda tmp
  clc
  adc T2Layouts.w+2,y
  sta tmp
  lda tmp+1
  adc #0
  sta tmp+1
  rep #$20
.ACCU 16
  txa
  clc
  adc #8
  tax
  sep #$20
.ACCU 8
  dec loop1
  bra -
+
  rep #$20
.ACCU 16
  lda #CheckVblank
  sta vbhook
  sep #$20
.ACCU 8
  jsr WaitFrame
  jsr WaitFrame
  jsr WaitFrame
  ; what it read
  rep #$20
.ACCU 16
  lda loop0
  asl a
  asl a
  tay                   ; its T2Layouts entry
  sep #$20
.ACCU 8
  ldx loop0
  lda objstat
  sta RES_T2,x
  and #$C0
  cmp T2Layouts.w+3,y
  bne +
  lda #'P'
  inc checks
  bra ++
+
  lda #'F'
  inc checks
  inc fails
++
  sta RES_T2+8,x
  inc loop0
  lda loop0
  cmp #5
  beq +
  jmp @layout
+
  jsr EndTest
  jmp ShowPage          ; the page again (the layouts replaced its table)

CheckVblank:
  lda WMOBJSTAT
  sta objstat
  jmp DmaWideOam

; ================================================================================================ test 3: the spotlight

SpotInit:
.ACCU 8
.INDEX 16
  jsr PageTextToBuf
  ldx #$0000
  stx anim
  stx spotready
  ldx #SPOT_TABLE_A
  stx spotnext          ; HDMA reads table A (the script's)
  ldx #188
  stx barx
  ldx #$0001
  stx bardir
  rts

SpotVblank:
  lda spotready
  beq +
  ldx spotnext
  stx $4312             ; HDMA channel 1 reads the new table from the next frame
  stz spotready
+
  ; window 2, the bar: barx to barx + 23
  lda barx
  sta WMWH2
  lda barx+1
  sta WMWH2
  rep #$20
.ACCU 16
  lda barx
  clc
  adc #(SPOT_BAR_W - 1)
  sep #$20
.ACCU 8
  sta WMWH3
  xba
  sta WMWH3
  rts

; SpotFrame: the circle one step along its path, drawn into the table HDMA is not reading; the bar one step.
SpotFrame:
.ACCU 8
.INDEX 16
  lda spotready
  beq +
  rts                   ; the vblank has not taken the last one yet
+
  rep #$20
.ACCU 16
  inc anim
  lda anim
  and #$00FF
  asl a
  tax
  lda SpotPathX.w,x
  sta tmp               ; cx
  lda SpotPathY.w,x
  sta tmp+2             ; cy
  ; the bar: 2 pixels a frame, back and forth over 0-376
  lda bardir
  beq +
  lda barx
  inc a
  inc a
  sta barx
  cmp #(400 - SPOT_BAR_W)
  bcc ++
  stz bardir
  bra ++
+
  lda barx
  dec a
  dec a
  sta barx
  bne ++
  inc bardir
++
  ; the table not being read
  lda spotnext
  cmp #SPOT_TABLE_A
  bne +
  lda #SPOT_TABLE_B
  bra ++
+
  lda #SPOT_TABLE_A
++
  sta tmp+4
  tax
  ldy #$0000            ; the line
@line:
  sep #$20
.ACCU 8
  lda #1
  sta.l $7E0000,x       ; one line
  rep #$20
.ACCU 16
  tya
  sec
  sbc tmp+2
  bpl +
  eor #$FFFF
  inc a
+
  cmp #SPOT_R
  bcs @none
  phx
  tax
  lda SpotHalf.w,x
  and #$00FF
  sta tmp+6
  plx
  lda tmp
  sec
  sbc tmp+6
  bpl +
  lda #0
+
  sta.l $7E0001,x       ; left
  lda tmp
  clc
  adc tmp+6
  cmp #400
  bcc +
  lda #399
+
  sta.l $7E0003,x       ; right
  bra @next
@none:
  lda #$0001
  sta.l $7E0001,x       ; left 1 > right 0: no window on the line
  lda #$0000
  sta.l $7E0003,x
@next:
  txa
  clc
  adc #5
  tax
  iny
  cpy #224
  bne @line
  sep #$20
.ACCU 8
  lda #$00
  sta.l $7E0000,x       ; the end of the table
  ldx tmp+4
  stx spotnext
  lda #1
  sta spotready
  rts
