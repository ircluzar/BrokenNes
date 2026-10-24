; Test 6: fast CPU and fast DMA (spec section 2), measured as WideMess/testroms/cpu/speed.asm measures them and judged against what spec
; section 2's cycle model predicts (gen_testcart.py computes the predictions; 0.5% either way for the loops):
;   loop A (ROM, work RAM and internal cycles) and loop B (the same plus a 16-bit read of the I/O register $4214): iterations in one frame,
;   NMI to NMI, at SNES speed and in fast mode (WMSPEED = 1); loop A again from FastROM (MEMSEL = 1, at $80:xxxx);
;   the bytes general DMA moves in one vblank in 512-byte transfers from ROM to the WRAM port: normal, fast DMA (WMDMA = 1), fast CPU alone.
; While it counts, the NMI is NmiCount (speed.asm's handler) behind NmiEntry's jump, HDMA is off and the pad is not read: the model counts
; exactly those cycles. It measures again and again; between rounds a frame with the full NMI shows the numbers and reads the pad.
; Results: RES_T6 (README.md).

.DEFINE RES_T6WMSPEED RES_T6 + $12
.DEFINE RES_T6WMDMA   RES_T6 + $13
.DEFINE RES_T6ROUNDS  RES_T6 + $14

T6Enter:
.ACCU 8
.INDEX 16
  lda #6
  jsr TestGate
  jsr BeginTest
  ldx #$0000
  stx passes
  stx RES_T6ROUNDS
  lda #0
  jsr SetupUI
  jsr GlassPanels
  rep #$20
.ACCU 16
  lda #VbNone
  sta vbhook            ; a still backdrop: the frames that count have no vblank work
  sep #$20
.ACCU 8
  jsr Header
  PRINT 4, 3, TxtSpdCols
  PRINT 17, 3, TxtSpdNotes
  jsr ScreenOn
@round:
  jsr SpeedMeasure
  jsr SpeedShow
  ldx passes
  bne +
  jsr EndTest
+
  lda #1
  sta nocount
  rep #$20
.ACCU 16
  inc passes
  lda passes
  sta RES_T6ROUNDS
  lda #NmiFull
  sta nmijump
  sep #$20
.ACCU 8
  lda #$81
  sta NMITIMEN
  lda runall
  beq +
  jsr ShowAWhile
  jmp RunAllNext
+
  jsr MemPoll           ; the numbers reach the screen; B or START leaves
  jsr MemPoll
  bra @round
T6Frame:
  rts

; SpeedMeasure: every measurement into RES_T6.
SpeedMeasure:
.ACCU 8
.INDEX 16
  rep #$20
.ACCU 16
  lda #NmiCount
  sta nmijump
  sep #$20
.ACCU 8
  lda #$80
  sta NMITIMEN          ; NMI on, no pad read (speed.asm's set-up)
  stz WMSPEED
  stz WMDMA
  stz MEMSEL
  ; the switches read back
  lda RES_FEAT
  and #FEAT_FASTCPU
  beq +
  lda #$01
  sta WMSPEED
  lda WMSPEED
  sta RES_T6WMSPEED
  stz WMSPEED
+
  lda RES_FEAT
  and #FEAT_FASTDMA
  beq +
  lda #$01
  sta WMDMA
  lda WMDMA
  sta RES_T6WMDMA
  stz WMDMA
+
  ; loop A
  jsr MeasureA
.ACCU 16
  sta RES_T6+$00
  sep #$20
.ACCU 8
  jsr FastOn
  beq +
  jsr MeasureA
.ACCU 16
  sta RES_T6+$02
  sep #$20
.ACCU 8
  stz WMSPEED
+
  ; loop A from FastROM
  lda #$01
  sta MEMSEL
  jsl $800000 + MeasureAFar
.ACCU 16
  sta RES_T6+$08
  sep #$20
.ACCU 8
  jsr FastOn
  beq +
  jsl $800000 + MeasureAFar
.ACCU 16
  sta RES_T6+$0A
  sep #$20
.ACCU 8
  stz WMSPEED
+
  stz MEMSEL
  ; loop B
  jsr MeasureB
.ACCU 16
  sta RES_T6+$04
  sep #$20
.ACCU 8
  jsr FastOn
  beq +
  jsr MeasureB
.ACCU 16
  sta RES_T6+$06
  sep #$20
.ACCU 8
  stz WMSPEED
+
  ; DMA in a vblank: normal, fast DMA, fast CPU alone
  jsr MeasureDma
  ldx dmabytes
  stx RES_T6+$0C
  lda RES_FEAT
  and #FEAT_FASTDMA
  beq +
  lda #$01
  sta WMDMA
  jsr MeasureDma
  ldx dmabytes
  stx RES_T6+$0E
  stz WMDMA
+
  jsr FastOn
  beq +
  jsr MeasureDma
  ldx dmabytes
  stx RES_T6+$10
  stz WMSPEED
+
  rts

; Iterations of loop A in one frame (speed.asm's, cycle for cycle). Returns with A 16 bits = the count.
.ACCU 8
.INDEX 16
MeasureA:
  rep #$20
.ACCU 16
  stz.b nmiflag
-
  lda.b nmiflag
  beq -                 ; wait for an NMI
  stz.b nmiflag
  stz.b counter
LoopA:
  inc.b counter         ; 2 ROM, 2 work-RAM reads, 1 internal, 2 work-RAM writes
  lda.b nmiflag         ; 2 ROM, 2 work RAM
  beq LoopA             ; 2 ROM, 1 internal (taken)
  lda.b counter
  rts

; MeasureA called with JSL: from $80:xxxx it runs in bank $80, where MEMSEL applies.
.ACCU 8
MeasureAFar:
  jsr MeasureA
  rtl

; Iterations of loop B (loop A plus a 16-bit read of $4214) in one frame. Returns with A 16 bits = the count.
.ACCU 8
MeasureB:
  rep #$20
.ACCU 16
  stz.b nmiflag
-
  lda.b nmiflag
  beq -
  stz.b nmiflag
  stz.b counter
LoopB:
  inc.b counter
  lda RDDIVL            ; 3 ROM, 2 I/O (6 clocks each, fast or not)
  lda.b nmiflag
  beq LoopB
  lda.b counter
  rts

; Bytes of general DMA (ROM -> the WRAM port, 512 at a time) started in one vblank. A 8 bits, X 16 bits.
.ACCU 8
MeasureDma:
  stz NMITIMEN          ; no NMI during the measurement
  stz DMAP0             ; A -> B, one register, the address increments
  lda #<WMDATA
  sta BBAD0             ; the WRAM port
  stz A1B0              ; source bank $00 (ROM)
  stz WMADDL
  lda #$80
  sta WMADDM
  stz WMADDH            ; the port at $7E:8000
-
  lda HVBJOY
  bmi -                 ; out of the current vblank
-
  lda HVBJOY
  bpl -                 ; the next vblank starts
  ldx #$0000
  stx dmabytes
@loop:
  ldx #$8000
  stx A1T0              ; source $00:8000
  ldx #512
  stx DAS0              ; 512 bytes
  lda #$01
  sta MDMAEN            ; go (the CPU waits for the transfer)
  rep #$20
.ACCU 16
  lda dmabytes
  clc
  adc #512
  sta dmabytes
  sep #$20
.ACCU 8
  lda HVBJOY
  bmi @loop             ; still in vblank: one more
  lda #$80
  sta NMITIMEN
  rts

; ---------------------------------------------------------------------------------------------------- the numbers

SpeedNames:
  .DB "LOOP A      ", "LOOP A FAST ", "LOOP B      ", "LOOP B FAST ", "FASTROM A   ", "FASTROM FAST", "DMA         ", "DMA FAST    "

SpeedShow:
.ACCU 8
.INDEX 16
  stz tmp+6             ; the row of the table
@row:
  lda tmp+6
  rep #$20
.ACCU 16
  and #$00FF
  asl a
  asl a
  asl a
  sta item              ; the entry's offset in SpeedTable
  sep #$20
.ACCU 8
  lda tmp+6
  clc
  adc #5
  sta tmp+7             ; its screen row
  ldy #3
  jsr At
  lda #S_WHITE
  jsr SetStyle
  rep #$20
.ACCU 16
  lda tmp+6
  and #$00FF
  sta tmp+2
  asl a
  asl a
  asl a
  clc
  adc tmp+2
  clc
  adc tmp+2
  clc
  adc tmp+2
  clc
  adc tmp+2             ; x 12
  tay
  sep #$20
.ACCU 8
  lda #12
  sta pcount
-
  lda SpeedNames.w,y
  jsr PutChar
  iny
  dec pcount
  bne -
  ; does the console have what this row needs?
  ldy item
  lda SpeedTable.w+7,y
  beq +
  and RES_FEAT
  bne +
  inx
  inx
  inx
  inx
  PRINTX TxtSpdNa
  jmp @next
+
  ; the measured value
  ldy item
  lda SpeedTable.w+6,y
  rep #$20
.ACCU 16
  and #$00FF
  tay
  lda RES_T6,y
  sta loop1
  sep #$20
.ACCU 8
  lda #S_YELLOW
  jsr SetStyle
  inx
  inx
  lda loop1
  xba
  lda loop1+1
  xba
  jsr PrintDec16
  ; the prediction
  lda #S_GREY
  jsr SetStyle
  inx
  inx
  ldy item
  rep #$20
.ACCU 16
  lda SpeedTable.w+4,y
  sep #$20
.ACCU 8
  jsr PrintDec16
  inx
  inx
  ; within [lo, hi]?
  ldy item
  rep #$20
.ACCU 16
  lda loop1
  cmp SpeedTable.w,y
  bcc @low
  lda SpeedTable.w+2,y
  cmp loop1
  bcc @low
  sep #$20
.ACCU 8
  sec
  bra @judge
@low:
  sep #$20
.ACCU 8
  clc
@judge:
  jsr Judge
  ; the meter: the count / 129 pixels (12,386 fills 12 tiles)
  inx
  inx
  rep #$20
.ACCU 16
  lda loop1
  sta RDDIVL - 16       ; $4204: the dividend
  sep #$20
.ACCU 8
  lda #129
  sta $4206             ; the divisor: 16 cycles later $4214 holds the quotient
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  lda tmp+6
  and #$01
  beq +
  lda #S_YELLOW
  bra ++
+
  lda #S_GREEN
++
  jsr SetStyle
  lda RDDIVL
  cmp #96
  bcc +
  lda #96
+
  sta pcount
-
  lda pcount
  cmp #8
  bcc +
  lda #G_BLOCK
  jsr PutChar
  lda pcount
  sec
  sbc #8
  sta pcount
  bra -
+
  lda pcount
  beq @next
  clc
  adc #8                ; codes 9-15: 1-7 pixels
  jsr PutChar
@next:
  inc tmp+6
  lda tmp+6
  cmp #SPD_ROWS
  beq +
  jmp @row
+
  ; fast CPU alone leaves DMA at 8 clocks a byte: within 512 bytes of the normal count
  PRINT 13, 3, TxtSpdCpuDma
  lda RES_FEAT
  and #FEAT_FASTCPU
  bne +
  jsr NaAtR
  bra @twice
+
  rep #$20
.ACCU 16
  lda RES_T6+$10
  sec
  sbc RES_T6+$0C
  bpl +
  eor #$FFFF
  inc a
+
  cmp #513
  sep #$20
.ACCU 8
  jsr JudgeAtR
@twice:
  ; fast DMA moves at least 1.8 times as much as normal DMA (normal + normal x 4 / 5)
  PRINT 14, 3, TxtSpdTwice
  lda RES_FEAT
  and #FEAT_FASTDMA
  bne +
  jsr NaAtR
  bra @reads
+
  rep #$20
.ACCU 16
  lda RES_T6+$0C
  asl a
  asl a
  sta RDDIVL - 16
  sep #$20
.ACCU 8
  lda #5
  sta $4206
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  nop
  rep #$20
.ACCU 16
  lda RDDIVL
  clc
  adc RES_T6+$0C        ; 1.8 x normal
  sta tmp
  lda RES_T6+$0E
  cmp tmp               ; carry set: fast >= 1.8 x normal
  sep #$20
.ACCU 8
  bcs +
  sec                   ; (JudgeAtR passes on carry clear)
  bra ++
+
  clc
++
  jsr JudgeAtR
@reads:
  ; WMSPEED and WMDMA read back what was written (they are R/W)
  PRINT 15, 3, TxtSpdRead
  lda RES_FEAT
  and #FEAT_FASTCPU
  bne +
  jsr NaAtR
  bra ++
+
  lda RES_T6WMSPEED
  cmp #1
  jsr JudgeEqAtR
++
  PRINT 16, 3, TxtSpdRead2
  lda RES_FEAT
  and #FEAT_FASTDMA
  bne +
  jsr NaAtR
  bra ++
+
  lda RES_T6WMDMA
  cmp #1
  jsr JudgeEqAtR
++
  PRINT 24, 3, TxtSpdRounds
  lda #1
  sta textdirty
  rts

; NaAtR: N/A at column 28 of the last PRINT's row (and the status 'N' of no check).
NaAtR:
  rep #$20
.ACCU 16
  lda linestart
  and #$FF80
  ora #(28 * 2)
  tax
  sep #$20
.ACCU 8
  lda #'N'
  jmp PrintStatus

; JudgeAtR: carry CLEAR = pass (the flags of a "less than" compare), printed at column 28 of the last PRINT's row.
JudgeAtR:
  php
  rep #$20
.ACCU 16
  lda linestart
  and #$FF80
  ora #(28 * 2)
  tax
  sep #$20
.ACCU 8
  plp
  bcc +
  clc
  jmp Judge
+
  sec
  jmp Judge
; JudgeEqAtR: the flags of a CMP, equal passes, at column 28.
JudgeEqAtR:
  php
  rep #$20
.ACCU 16
  lda linestart
  and #$FF80
  ora #(28 * 2)
  tax
  sep #$20
.ACCU 8
  plp
  beq +
  clc
  jmp Judge
+
  sec
  jmp Judge

TxtSpdCols:   .DB S_CYAN, "IN ONE FRAME COUNT  SPEC RESULT", EOS
TxtSpdNa:     .DB S_GREY, "NOT ON THIS CONSOLE", EOS
TxtSpdNa2:    .DB S_GREY, "    N/A", EOS
TxtSpdCpuDma: .DB S_WHITE, "FAST CPU: DMA UNCHANGED", EOS
TxtSpdTwice:  .DB S_WHITE, "FAST DMA: 1.8X OR MORE", EOS
TxtSpdRead:   .DB S_WHITE, "WMSPEED READS BACK 1", EOS
TxtSpdRead2:  .DB S_WHITE, "WMDMA READS BACK 1", EOS
TxtSpdRounds: .DB S_WHITE, "MEASURED ", S_YELLOW, DEC16, RES_T6ROUNDS & $FF, RES_T6ROUNDS >> 8, S_WHITE, " TIMES  ", S_GREY, "(LOOPS: SPEC +-0.5%)", EOS
TxtSpdNotes:  .DB S_GREY, "A: INC, LDA, BEQ: 108 CLOCKS (FAST: 28)", NL
              .DB "B: A + LDA $4214, I/O STAYS 6: 144 (FAST: 46)", NL
              .DB "FASTROM: MEMSEL 1, AT $80: 96 (FAST: 28)", NL
              .DB "DMA: 512-BYTE TRANSFERS IN ONE VBLANK", NL
              .DB "FITS 6,123 BYTES (8 CLOCKS), 12,247 (4 CLOCKS)", EOS
