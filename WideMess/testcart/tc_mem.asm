; Test 7: the memories (spec sections 2, 3.3 and 4). Every memory is written with the 64 KB pattern (PATTERN, from an offset that changes
; with the bank and the pass) and read back:
;   work RAM   the 20-bit WRAM port ($2180-$2183) writes work-RAM banks 1-15 ($7F, $70-$7D), the A bus reads each back (items 0-15); the A bus
;              writes 512 bytes of each, the port reads them (item 42); the port's wrap $FFFFF -> $00000 (43); $00:0000-1FFF is $7E, not $70 (44);
;              $7E:2000-FFFF is checked in place, each word kept (45, and the $7E cell)
;   sound RAM  the port $21F0-$21F3 writes 64 KB banks 1-15 and reads them back (items 25-39; bank 0 is the running SPC700's: not touched)
;   video RAM  WMVDATA writes the 512 KB and reads it back (16-23), WMCGDATA the 1,024 colours (40), WMOAMDATA the 2 KB sprite table (41)
; Fast CPU is on for the checks when the console has it. The test runs pass after pass until B; the first pass gives its status.
; Results: RES_T7 + item (README.md).

.DEFINE MI_VRAM   16
.DEFINE MI_SOUND  24
.DEFINE MI_CGRAM  40
.DEFINE MI_WOAM   41
.DEFINE MI_PORT   42
.DEFINE MI_WRAP   43
.DEFINE MI_MIRROR 44
.DEFINE MI_KEPT   45
.DEFINE RES_T7PASSES RES_T7 + $30
.DEFINE RES_T7ERRORS RES_T7 + $32

T7Enter:
.ACCU 8
.INDEX 16
  lda #7
  jsr TestGate
  jsr BeginTest
  ldx #$0000
  stx passes
  stx RES_T7PASSES
  stx RES_T7ERRORS
  jsr MemInitItems
  jsr MemScreen
@pass:
  jsr MemPass
  ldx passes
  bne +
  jsr EndTest           ; the first pass decides the status
+
  rep #$20
.ACCU 16
  inc passes
  lda passes
  sta RES_T7PASSES
  sep #$20
.ACCU 8
  jsr MemPassLine
  lda runall
  beq @pass
  jsr ShowAWhile
  jmp RunAllNext
T7Frame:
  rts

; MemInitItems: every item to do (0), or 'N' when the console lacks what it needs, 'S' for sound RAM bank 0.
MemInitItems:
  ldx #$0000
-
  stz RES_T7,x
  inx
  cpx #MEM_ITEMS
  bne -
  lda RES_FEAT
  and #FEAT_BIGWRAM
  bne +
  lda #'N'
  ldx #$0000
-
  sta RES_T7,x          ; $70-$7D
  inx
  cpx #14
  bne -
  sta RES_T7+MI_PORT
  sta RES_T7+MI_WRAP
+
  lda RES_FEAT
  and #FEAT_SPRITES
  bne +
  lda #'N'
  sta RES_T7+MI_WOAM
+
  lda RES_FEAT
  and #FEAT_SOUND
  bne +
  lda #'N'
  ldx #$0000
-
  sta RES_T7+MI_SOUND,x
  inx
  cpx #16
  bne -
  rts
+
  lda #'S'
  sta RES_T7+MI_SOUND   ; bank 0: the SPC700's own 64 KB
  rts

MemScreen:
  lda #0
  jsr SetupUI
  jsr Header
  PRINT 4, 3, TxtMemWork
  PRINT 8, 3, TxtMemVideo
  PRINT 11, 3, TxtMemSound
  PRINT 15, 3, TxtMemLines
  ldx #$0000
-
  jsr DrawItem
  inx
  cpx #MEM_ITEMS
  bne -
  jsr MemPassLine
  jsr ScreenOn
  rts

MemPassLine:
  PRINT 23, 3, TxtMemPass
  lda RES_FEAT
  and #FEAT_FASTCPU
  beq +
  PRINT 23, 32, TxtMemFast
+
  lda #1
  sta textdirty
  rts

TxtMemWork:  .DB S_CYAN, "WORK RAM, 1 MB: BANKS $70-$7F", S_GREY, " (PORT, A BUS)", EOS
TxtMemVideo: .DB S_CYAN, "VIDEO RAM, 512 KB", S_GREY, " (WMVDATA, 64 KB UNITS)", EOS
TxtMemSound: .DB S_CYAN, "SOUND RAM, 1 MB", S_GREY, " ($21F3; BANK 0: THE SPC700)", EOS
TxtMemLines: .DB S_CYAN, "AND", NL, S_WHITE, "COLOUR RAM, 1,024 COLOURS (WMCGDATA)", NL, "SPRITE TABLE, 2 KB (WMOAMDATA)", NL
             .DB "A BUS TO THE 20-BIT WRAM PORT", NL, "THE PORT WRAPS FROM $FFFFF TO $00000", NL
             .DB "$00:0000-1FFF IS $7E, NOT $70", NL, "$7E:2000-FFFF, CHECKED IN PLACE", EOS
TxtMemPass:  .DB S_WHITE, "PASSES ", S_YELLOW, DEC16, RES_T7PASSES & $FF, RES_T7PASSES >> 8, S_WHITE, "  ERRORS ", S_YELLOW, DEC16, RES_T7ERRORS & $FF, RES_T7ERRORS >> 8, EOS
TxtMemFast:  .DB S_GREY, "(FAST CPU ON)", EOS
TxtMemBlank: .DB S_ORANGE, "VIDEO RAM NOW: THE SCREEN GOES BLACK", EOS

; DrawItem: X = an item: its cell (label and light) or its status word. Keeps X.
DrawItem:
.ACCU 8
.INDEX 16
  phx
  lda MemCellRow.w,x
  sta row
  lda MemCellCol.w,x
  sta col
  lda RES_T7,x
  sta st
  cpx #MI_CGRAM
  bcc @cell
  lda row
  ldy col
  jsr At
  lda st
  jsr PrintStatus
  lda #1
  sta textdirty
  plx
  rts
@cell:
  ; the label (3 glyphs of MemCellText)
  rep #$20
.ACCU 16
  txa
  sta tmp+2
  asl a
  clc
  adc tmp+2
  sta tmp+2             ; item x 3
  sep #$20
.ACCU 8
  lda #S_WHITE
  ldy st
  cpy #'N'
  beq +
  cpy #'S'
  bne ++
+
  lda #S_GREY
++
  jsr SetStyle
  lda row
  ldy col
  jsr At
  ldy tmp+2
  lda MemCellText.w,y
  jsr PutChar
  lda MemCellText.w+1,y
  jsr PutChar
  lda MemCellText.w+2,y
  jsr PutChar
  ; the light under it: three tiles of BG2
  lda st
  ldy #CELL_TILE + 32 + 4          ; to do: dim blue
  cmp #'R'
  bne +
  ldy #CELL_TILE + 32 + 15         ; running: bright blue
+
  cmp #'P'
  bne +
  ldy #CELL_TILE + 12              ; passed: green
+
  cmp #'F'
  bne +
  ldy #CELL_TILE + 16 + 15         ; failed: red
+
  cmp #'N'
  beq +
  cmp #'S'
  bne ++
+
  ldy #PANEL_C                     ; not tested: no light
++
  sty tmp+2
  lda row
  ldy col
  jsr At
  ldy tmp+2
  jsr PutPanel
  jsr PutPanel
  jsr PutPanel
  lda #1
  sta textdirty
  sta paneldirty
  plx
  rts

; ItemRun: X = an item: it lights up as running.
ItemRun:
  lda RES_T7,x
  cmp #'F'
  beq +                 ; a failure stays shown
  lda #'R'
  sta RES_T7,x
+
  jmp DrawItem

; ItemDone: X = an item, errs2 = what did not read back: 'P' or 'F' (a failure stays), counted as a check in the first pass, drawn.
ItemDone:
.ACCU 8
.INDEX 16
  rep #$20
.ACCU 16
  lda errs2
  beq @pass
  clc
  adc RES_T7ERRORS
  bcc +
  lda #$FFFF
+
  sta RES_T7ERRORS
  sep #$20
.ACCU 8
  lda #'F'
  sta RES_T7,x
  lda passes
  ora passes+1
  bne +
  inc checks
  inc fails
  jmp DrawItem
+
  lda #'F'
  sta RES_STATUS+6      ; a later pass failed: the test fails
  jmp DrawItem
@pass:
  sep #$20
.ACCU 8
  lda RES_T7,x
  cmp #'F'
  beq +
  lda #'P'
  sta RES_T7,x
+
  lda passes
  ora passes+1
  bne +
  inc checks
+
  jmp DrawItem

; MemPoll: a frame for the screen to show what changed; B or START leaves.
MemPoll:
  jsr WaitFrame
  jsr TakePad
  lda pad+1
  and #>(PAD_B | PAD_START)
  beq +
  stz WMSPEED
  jmp BackToMenu
+
  rts

FastOn:
  lda RES_FEAT
  and #FEAT_FASTCPU
  beq +
  lda #$01
  sta WMSPEED
+
  rts

; CalcK: patoff = where bank / unit wbank starts in the pattern this pass: (wbank x 4098 + passes x 514) AND $FFFE.
CalcK:
  rep #$20
.ACCU 16
  lda wbank
  xba
  asl a
  asl a
  asl a
  asl a                 ; x 4096
  sta tmp
  lda wbank
  asl a
  clc
  adc tmp
  sta tmp
  lda passes
  xba
  asl a                 ; x 512
  clc
  adc tmp
  sta tmp
  lda passes
  asl a
  clc
  adc tmp
  and #$FFFE
  sta patoff
  sep #$20
.ACCU 8
  rts

; PatPtr: ptr2 = PATTERN.
PatPtr:
  ldx #(PATTERN & $FFFF)
  stx ptr2
  lda #(PATTERN >> 16)
  sta ptr2+2
  rts

; DmaPattern: 64 KB of the pattern from patoff to the B-bus register A (DMA channel 0; the source wraps inside the pattern's bank).
DmaPattern:
  sta BBAD0
  stz DMAP0
  ldx patoff
  stx A1T0
  lda #(PATTERN >> 16)
  sta A1B0
  ldx #$0000
  stx DAS0
  lda #$01
  sta MDMAEN
  rts

; ReadChunk: 4,096 bytes from the B-bus register A into SCRATCH (DMA channel 0, B to A).
ReadChunk:
  sta BBAD0
  lda #$80
  sta DMAP0
  ldx #(SCRATCH & $FFFF)
  stx A1T0
  lda #(SCRATCH >> 16)
  sta A1B0
  ldx #4096
  stx DAS0
  lda #$01
  sta MDMAEN
  rts

; CmpScratch: dlen bytes of SCRATCH against [ptr2] from patoff (both by words); errs2 + 1 a word that differs.
CmpScratch:
  rep #$20
.ACCU 16
  ldx #$0000
  ldy patoff
@l:
  lda.l SCRATCH,x
  cmp [ptr2],y
  bne @bad
@ok:
  iny
  iny
  inx
  inx
  cpx dlen
  bne @l
  sep #$20
.ACCU 8
  rts
@bad:
.ACCU 16
  inc errs2
  bra @ok

; CmpScratchCg: CmpScratch for colours: 15 bits a word (a colour's bit 15 is not kept, and reads back as the spec leaves it).
CmpScratchCg:
  rep #$20
.ACCU 16
  ldx #$0000
  ldy patoff
-
  lda.l SCRATCH,x
  and #$7FFF
  sta tmp
  lda [ptr2],y
  and #$7FFF
  cmp tmp
  beq +
  inc errs2
+
  iny
  iny
  inx
  inx
  cpx dlen
  bne -
  sep #$20
.ACCU 8
  rts

; CmpBank: bank A's 64 KB in place (the A bus) against [ptr2] from patoff; errs2 + 1 a word that differs.
CmpBank:
.ACCU 8
  phb
  pha
  plb
  rep #$20
.ACCU 16
  ldx #$0000
  ldy patoff
@l:
  lda $0000.w,x
  cmp [ptr2],y
  bne @bad
@ok:
  iny
  iny
  inx
  inx
  bne @l
  sep #$20
.ACCU 8
  plb
  rts
@bad:
.ACCU 16
  inc errs2
  bra @ok

; ---------------------------------------------------------------------------------------------------- one pass

MemPass:
.ACCU 8
.INDEX 16
  jsr FastOn
  jsr PatPtr
  jsr MemKept
  lda #1
  sta portok
  lda RES_FEAT
  and #FEAT_BIGWRAM
  beq +
  jsr MemPortProbe      ; (a port short of 20 bits would fill the program's own bank: then only bank 1 is filled)
+
  jsr MemWork
  lda RES_FEAT
  and #FEAT_BIGWRAM
  beq ++
  lda portok
  bne +
  ldx #$0001            ; the port failed its probe: the checks that need it fail
  stx errs2
  ldx #MI_PORT
  jsr ItemDone
  ldx #$0001
  stx errs2
  ldx #MI_WRAP
  jsr ItemDone
  bra ++
+
  jsr MemPortCheck
  jsr MemWrap
++
  jsr MemMirror
  lda RES_FEAT
  and #FEAT_SOUND
  beq +
  jsr MemSound
+
  jsr MemVideo
  stz WMSPEED
  rts

; MemKept: $7E:2000-FFFF word by word: kept, $55AA and $AA55 written and read back, then put back (NMI off meanwhile). Items 45 and 14 ($7E).
MemKept:
  ldx #14
  jsr ItemRun
  ldx #MI_KEPT
  jsr ItemRun
  jsr MemPoll
  stz NMITIMEN
  ldx #$0000
  stx errs2
  phb
  lda #$7E
  pha
  plb
  rep #$20
.ACCU 16
  ldx #$2000
@l:
  lda $0000.w,x
  sta saved
  lda #$55AA
  sta $0000.w,x
  cmp $0000.w,x
  bne @bad
  eor #$FFFF
  sta $0000.w,x
  cmp $0000.w,x
  bne @bad
@ok:
  lda saved
  sta $0000.w,x
  inx
  inx
  bne @l
  sep #$20
.ACCU 8
  plb
  lda #$81
  sta NMITIMEN
  ldx #MI_KEPT
  jsr ItemDone
  ldx #14
  jsr ItemDone
  rts
@bad:
.ACCU 16
  inc errs2
  bra @ok

; MemPortProbe: each address bit 16-19 of the WRAM port, one byte at offset $F000 + k of work-RAM banks 1, 2, 4 and 8 ($7F, $70, $72, $76), read
; back on the A bus. A port that dropped one of those bits would have written a lower bank (the cartridge keeps only scratch at $F000 of
; $7E and $7F, so the probe harms nothing). portok = 0 when a bit failed.
PortProbeW: .DB 1, 2, 4, 8
PortProbeB: .DB $7F, $70, $72, $76
MemPortProbe:
.ACCU 8
.INDEX 16
  ldx #$0000
-
  stz ptr
  lda #$F0
  sta ptr+1
  lda PortProbeB.w,x
  sta ptr+2
  txy
  lda #$00
  sta [ptr],y           ; B:F000 + k cleared on the A bus
  txa
  sta WMADDL
  lda #$F0
  sta WMADDM
  lda PortProbeW.w,x
  sta WMADDH            ; the port at w:F000 + k
  txa
  ora #$A0
  sta WMDATA
  lda [ptr],y
  sta tmp
  txa
  ora #$A0
  cmp tmp
  beq +
  stz portok
+
  inx
  cpx #4
  bne -
  rts

; MemWork: the port writes the pattern into work-RAM banks 1-15 (bank 1 only without 1 MB), then the A bus reads each back.
MemWork:
.ACCU 8
  lda #15
  sta loop0             ; the last work-RAM bank
  lda RES_FEAT
  and #FEAT_BIGWRAM
  bne +
  lda #1
  sta loop0
+
  lda portok
  bne +
  lda #1
  sta loop0             ; the port failed its probe: bank 1 ($7F) alone, the others fail
  ldx #$0000
-
  phx
  ldy #$0001
  sty errs2
  jsr ItemDone
  plx
  inx
  cpx #14
  bne -
+
  ldx #$0001
  stx wbank
@fill:
  jsr WorkItem
  jsr ItemRun
  jsr CalcK
  stz WMADDL
  stz WMADDM
  lda wbank
  sta WMADDH            ; work-RAM address wbank:0000 (20 bits)
  lda #<WMDATA
  jsr DmaPattern
  lda wbank
  inc a
  sta wbank
  dec a
  cmp loop0
  bne @fill
  jsr MemPoll
  ldx #$0001
  stx wbank
@check:
  jsr CalcK
  ldx #$0000
  stx errs2
  lda wbank
  cmp #1
  bne +
  lda #$7F
  bra ++
+
  clc
  adc #$6E              ; work-RAM bank n + 2 is $70 + n
++
  jsr CmpBank
  jsr WorkItem
  jsr ItemDone
  jsr MemPoll
  lda wbank
  inc a
  sta wbank
  dec a
  cmp loop0
  bne @check
  rts
; WorkItem: X = the cell of work-RAM bank wbank: $70 + (wbank - 2) mod 16.
WorkItem:
  lda wbank
  sec
  sbc #2
  and #$0F
  rep #$20
.ACCU 16
  and #$00FF
  tax
  sep #$20
.ACCU 8
  rts

; MemPortCheck: the A bus writes 512 bytes at $8000 of each bank ($70-$7D, $7F), the 20-bit port reads them back (item 42).
MemPortCheck:
  ldx #MI_PORT
  jsr ItemRun
  ldx #$0000
  stx errs2
  ldx #$0001
  stx wbank
@bank:
  lda wbank
  cmp #1
  bne +
  lda #$7F
  bra ++
+
  clc
  adc #$6E
++
  phb
  pha
  plb
  ldx #$0000
-
  lda.l PATTERN + $8000,x
  eor #$5A
  sta $8000,x
  inx
  cpx #512
  bne -
  plb
  stz WMADDL
  lda #$80
  sta WMADDM
  lda wbank
  sta WMADDH
  ldx #$0000
-
  lda.l PATTERN + $8000,x
  eor #$5A
  cmp WMDATA
  beq +
  rep #$20
  inc errs2
  sep #$20
+
  inx
  cpx #512
  bne -
  lda wbank
  inc a
  sta wbank
  cmp #16
  bne @bank
  ldx #MI_PORT
  jmp ItemDone

; MemWrap: the port at $FFFFF reads $7D:FFFF, then wraps to $00000, $7E:0000 (NMI off, so $7E:0000 holds still). Item 43.
MemWrap:
  ldx #MI_WRAP
  jsr ItemRun
  ldx #$0000
  stx errs2
  stz NMITIMEN
  lda.l $7DFFFF
  sta tmp
  lda.l $7E0000
  sta tmp+1
  lda #$FF
  sta WMADDL
  sta WMADDM
  lda #$0F
  sta WMADDH
  lda WMDATA
  cmp tmp
  bne +
  lda WMDATA
  cmp tmp+1
  beq ++
+
  inc errs2
++
  lda #$81
  sta NMITIMEN
  ldx #MI_WRAP
  jmp ItemDone

; MemMirror: $00:1D00 is $7E:1D00 (and not $70:1D00, with 1 MB). Item 44.
MemMirror:
  ldx #MI_MIRROR
  jsr ItemRun
  ldx #$0000
  stx errs2
  lda RES_FEAT
  and #FEAT_BIGWRAM
  beq +
  lda #$3C
  sta.l $701D00
+
  lda #$C3
  sta $1D00
  lda.l $7E1D00
  cmp #$C3
  beq +
  inc errs2
+
  lda RES_FEAT
  and #FEAT_BIGWRAM
  beq +
  lda.l $701D00
  cmp #$3C
  beq +
  inc errs2
+
  ldx #MI_MIRROR
  jmp ItemDone

; MemSound: the port $21F0-$21F3 writes sound RAM banks 1-15, then reads each back 4 KB at a time. Items 25-39.
MemSound:
  lda #1
  sta snddirty
  ldx #$0001
  stx wbank
@fill:
  jsr SoundItem
  jsr ItemRun
  jsr CalcK
  stz WMSADDL
  stz WMSADDM
  lda wbank
  sta WMSADDH
  lda #<WMSDATA
  jsr DmaPattern
  lda wbank
  inc a
  sta wbank
  cmp #16
  bne @fill
  jsr MemPoll
  ldx #$0001
  stx wbank
@check:
  jsr CalcK
  ldx #$0000
  stx errs2
  stz WMSADDL
  stz WMSADDM
  lda wbank
  sta WMSADDH
  ldx #4096
  stx dlen
  stz loop1
-
  lda #<WMSDATA
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
  cmp #16
  bne -
  jsr SoundItem
  jsr ItemDone
  jsr MemPoll
  lda wbank
  inc a
  sta wbank
  cmp #16
  bne @check
  rts
SoundItem:
  lda wbank
  clc
  adc #MI_SOUND
  rep #$20
.ACCU 16
  and #$00FF
  tax
  sep #$20
.ACCU 8
  rts

; MemVideo: in forced blank, the 512 KB of video RAM, the colours and the sprite table; then the screen again.
MemVideo:
  PRINT 24, 3, TxtMemBlank
  ldx #MI_VRAM
-
  jsr ItemRun
  inx
  cpx #MI_VRAM + 8
  bne -
  ldx #MI_CGRAM
  jsr ItemRun
  lda RES_FEAT
  and #FEAT_SPRITES
  beq +
  ldx #MI_WOAM
  jsr ItemRun
+
  jsr MemPoll
  rep #$20
.ACCU 16
  lda #VbNone
  sta vbhook            ; nothing touches video RAM but this test now
  sep #$20
.ACCU 8
  lda #$80
  sta INIDISP
  ; video RAM: 8 units of 64 KB
  ldx #$0000
  stx wbank
-
  jsr CalcK
  stz WMVADDL
  stz WMVADDM
  lda wbank
  sta WMVADDH
  lda #<WMVDATA
  jsr DmaPattern
  inc wbank
  lda wbank
  cmp #8
  bne -
  ldx #$0000
  stx wbank
@unit:
  jsr CalcK
  ldx #$0000
  stx errs2
  stz WMVADDL
  stz WMVADDM
  lda wbank
  sta WMVADDH
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
  cmp #16
  bne -
  lda wbank
  clc
  adc #MI_VRAM
  rep #$20
.ACCU 16
  and #$00FF
  tax
  sep #$20
.ACCU 8
  jsr ItemDone
  inc wbank
  lda wbank
  cmp #8
  bne @unit
  ; the colours: 1,024 entries of 15 bits (bit 15 is not kept)
  lda #8
  sta wbank
  jsr CalcK
  stz WMCGADDL
  stz WMCGADDH
  stz DMAP0
  lda #<WMCGDATA
  sta BBAD0
  ldx patoff
  stx A1T0
  lda #(PATTERN >> 16)
  sta A1B0
  ldx #2048
  stx DAS0
  lda #$01
  sta MDMAEN
  stz WMCGADDL
  stz WMCGADDH
  lda #<WMCGDATA
  jsr ReadChunk         ; (4 KB: the 1,024 colours twice over, the address wraps; the first 2 KB are compared)
  ldx #$0000
  stx errs2
  ldx #2048
  stx dlen
  jsr CmpScratchCg
  ldx #MI_CGRAM
  jsr ItemDone
  ; the WideMess sprite table: 2 KB, its reserved bits 0 (a pattern for odd passes, another for even)
  lda RES_FEAT
  and #FEAT_SPRITES
  beq @done
  ldx #(OAMPAT_A & $FFFF)
  lda passes
  and #$01
  beq +
  ldx #(OAMPAT_B & $FFFF)
+
  stx ptr2
  stz WMOAMADDL
  stz WMOAMADDH
  stz DMAP0
  lda #<WMOAMDATA
  sta BBAD0
  stx A1T0
  lda #(OAMPAT_A >> 16)
  sta A1B0
  sta ptr2+2
  ldx #2048
  stx DAS0
  lda #$01
  sta MDMAEN
  stz WMOAMADDL
  stz WMOAMADDH
  lda #<WMOAMDATA
  jsr ReadChunk
  ldx #$0000
  stx errs2
  stx patoff
  ldx #2048
  stx dlen
  jsr CmpScratch
  jsr PatPtr
  ldx #MI_WOAM
  jsr ItemDone
@done:
  jmp MemScreen
