; Tests 2-4: the picture pages (spec section 3). Every page is a script of register writes and DMA that gen_testcart.py made and fed to
; milestone 2's model of the spec, which drew the page's reference picture (WideMess/testroms/bin/widemess-testcart.<page>.expect.png).
; The cartridge restores the power-on state (every picture register at its reset value; the video RAM, the colours and both sprite tables
; cleared), plays the script (milestone 2's register player, WideMess/testroms/picture/player.asm), then, on a live page, animates it.
; SELECT: on a live page, still (the script's picture, which is the reference); on a reference page, its variants (register writes after
; the script, each with its own picture). LEFT / RIGHT: the test's pages. B or START: the menu.
; Results: RES_T2 / RES_T3 / RES_T4 + $10 the page shown, + $11 still, + $12 the variant (README.md).

.DEFINE PG_SHOWN   $10
.DEFINE PG_STILL   $11
.DEFINE PG_VARIANT $12

; the pages of each test (gen_testcart.py's page numbers)
TestPages:
  .DW T2Pages, T3Pages, T4Pages
T2Pages: .DB 1, PG_REF_SPRITES
T3Pages: .DB 1, PG_REF_WINDOWS
T4Pages: .DB 1, PG_REF_WIDESNES
; each page's animation (VbNone: none) and what its frame does besides the keys (Nothing: nothing)
PageHooks:
  .DW VbNone, VbNone, VbNone
PageFrames:
  .DW Nothing, Nothing, Nothing

ZeroByte: .DB 0

; ---------------------------------------------------------------------------------------------------- the tests

T2Enter:
.ACCU 8
.INDEX 16
  lda #2
  jsr TestGate
  lda #'V'
  sta RES_STATUS+1
  bra PageTestEnter
T3Enter:
  lda #3
  jsr TestGate
  lda #'V'              ; nothing to judge by itself: by eye, or a picture against its reference
  sta RES_STATUS+2
  bra PageTestEnter
T4Enter:
  lda #4
  jsr TestGate
  lda #'V'
  sta RES_STATUS+3
; PageTestEnter: the test's first page. In a run-all, each of its pages for a second, then the next test.
PageTestEnter:
  stz pgidx
  stz pgvariant
  stz pgstill
  jsr ShowPage
  lda runall
  bne +
  rts
+
-
  jsr ShowAWhile
  lda pgidx
  inc a
  jsr PagesOfTest
  cmp pgcount
  bcs +
  sta pgidx
  jsr ShowPage
  bra -
+
  jmp RunAllNext

T2Frame:
T3Frame:
T4Frame:
.ACCU 8
.INDEX 16
  jsr BackOnB
  jsr PagesOfTest
  lda pad+1
  and #>PAD_RIGHT
  beq +
  lda pgidx
  inc a
  cmp pgcount
  bcc ++
  lda #0
++
  sta pgidx
  stz pgvariant
  stz pgstill
  jmp ShowPage
+
  lda pad+1
  and #>PAD_LEFT
  beq +
  lda pgidx
  dec a
  bpl ++
  lda pgcount
  dec a
++
  sta pgidx
  stz pgvariant
  stz pgstill
  jmp ShowPage
+
  lda pad+1
  and #>PAD_SELECT
  beq +
  jsr PageSelect
  jmp ShowPage
+
  ldx pgcur
  jmp (PageFrames,x)

; PagesOfTest: ptr2 = the test's page list, pgcount its length (keeps A).
PagesOfTest:
  pha
  lda testn
  sec
  sbc #2
  asl a
  rep #$20
.ACCU 16
  and #$00FF
  tax
  lda TestPages.w,x
  sta ptr2
  sep #$20
.ACCU 8
  stz ptr2+2
  lda (ptr2)
  sta pgcount
  pla
  rts

; PageSelect: the next variant of a reference page, or still / live on a live page.
PageSelect:
  jsr PageNumber
  rep #$20
.ACCU 16
  and #$00FF
  asl a
  tax
  lda PageVariants.w,x
  sta ptr
  sep #$20
.ACCU 8
  stz ptr+2
  lda (ptr)             ; its variants
  beq @still
  inc a
  sta tmp
  lda pgvariant
  inc a
  cmp tmp
  bcc +
  lda #0
+
  sta pgvariant
  rts
@still:
  lda pgstill
  eor #$01
  sta pgstill
  rts

; PageNumber: A = the page shown (from the test's list and pgidx).
PageNumber:
  jsr PagesOfTest
  rep #$20
.ACCU 16
  lda pgidx
  and #$00FF
  inc a
  tay
  sep #$20
.ACCU 8
  lda (ptr2),y
  rts

; ShowPage: the page pgidx of the test: the power-on state, its script, its variant; then its animation unless still.
ShowPage:
.ACCU 8
.INDEX 16
  jsr PageNumber
  sta pgpage
  rep #$20
.ACCU 16
  and #$00FF
  asl a
  sta pgcur             ; page x 2 (PageHooks, PageFrames)
  lda #VbNone
  sta vbhook
  sep #$20
.ACCU 8
  jsr PowerOnPicture
  ; the script
  lda pgpage
  rep #$20
.ACCU 16
  and #$00FF
  sta tmp
  asl a
  clc
  adc tmp               ; x 3
  tax
  lda.l PageScripts,x
  sta scrptr
  sep #$20
.ACCU 8
  lda.l PageScripts+2,x
  sta scrptr+2
  jsr PlayScript
  ; its variant: the (register, value) writes after the script
  lda pgvariant
  beq @variantdone
  lda pgpage
  rep #$20
.ACCU 16
  and #$00FF
  asl a
  tax
  lda PageVariants.w,x
  sta ptr
  sep #$20
.ACCU 8
  stz ptr+2
  ldy #$0001            ; past the count
  lda pgvariant
  sta tmp
-
  dec tmp
  beq +
  lda (ptr),y           ; skip a variant: its writes
  iny
  rep #$20
.ACCU 16
  and #$00FF
  asl a
  sta tmp+2
  tya
  clc
  adc tmp+2
  tay
  sep #$20
.ACCU 8
  bra -
+
  lda (ptr),y
  sta tmp
  iny
-
  lda (ptr),y           ; the register's low byte
  iny
  rep #$20
.ACCU 16
  and #$00FF
  tax
  sep #$20
.ACCU 8
  lda (ptr),y
  iny
  sta $2100,x
  dec tmp
  bne -
@variantdone:
  ; the results table: what is shown
  jsr PageRes
  lda pgpage
  sta RES,x
  lda pgstill
  sta RES+1,x
  lda pgvariant
  sta RES+2,x
  ; the animation
  lda pgstill
  bne +
  ldx pgcur
  rep #$20
.ACCU 16
  lda PageHooks.w,x
  sta vbhook
  sep #$20
.ACCU 8
+
  rts
; PageRes: X = the test's block + PG_SHOWN, from RES (RES_T2 / T3 / T4 + $10).
PageRes:
  lda testn
  dec a
  rep #$20
.ACCU 16
  and #$00FF
  xba
  lsr a
  lsr a                 ; (test - 1) x $40
  clc
  adc #(RES_DETAIL - RES + PG_SHOWN)
  tax
  sep #$20
.ACCU 8
  rts

; PowerOnPicture: forced blank, HDMA off, every picture register at its reset value, the 512 KB of video RAM, the 1,024 colours and both
; sprite tables 0 (what a script assumes, as a ROM does at power-on).
PowerOnPicture:
.ACCU 8
.INDEX 16
  jsr ResetVideo
  ldx #ZeroByte
  stx ptr
  stz ptr+2
  ldx #$0000
  stx dlen
  stz vdst
  stz vdst+1
  stz vdst+2
-
  jsr VramFill          ; 64 KB
  inc vdst+2
  lda vdst+2
  cmp #8
  bne -
  ; the colours
  stz WMCGADDL
  stz WMCGADDH
  lda #<WMCGDATA
  ldx #2048
  jsr FillB
  ; the SNES sprite table
  stz OAMADDL
  stz OAMADDH
  lda #<OAMDATA
  ldx #544
  jsr FillB
  lda RES_FEAT
  and #FEAT_SPRITES
  beq +
  stz WMOAMADDL
  stz WMOAMADDH
  lda #<WMOAMDATA
  ldx #2048
  jsr FillB
  stz WMOAMADDL
  stz WMOAMADDH
+
  stz OAMADDL
  stz OAMADDH
  rts
; FillB: X bytes of 0 to the B-bus register A (DMA channel 0, a fixed source).
FillB:
  sta BBAD0
  lda #$08
  sta DMAP0
  stx DAS0
  ldx #ZeroByte
  stx A1T0
  stz A1B0
  lda #$01
  sta MDMAEN
  rts

; PlayScript: the script at [scrptr] (milestone 2's player, player.asm: $01 rr vv a B-bus write; $02 ll hh vv a write in bank 0 (the CPU's
; $42xx / $43xx registers, or work RAM); $03 mm bb sl sh sb nl nh a DMA on channel 0; $04 the unlock; $00 the end).
PlayScript:
.ACCU 8
.INDEX 16
  ldy #$0000
@next:
  lda [scrptr],y
  iny
  cmp #$01
  beq @bbus
  cmp #$02
  beq @abs
  cmp #$03
  beq @dma
  cmp #$04
  beq @unlock
  rts                   ; $00 (and anything else) ends it
@bbus:
  lda [scrptr],y
  iny
  rep #$20
.ACCU 16
  and #$00FF
  tax
  sep #$20
.ACCU 8
  lda [scrptr],y
  iny
  sta $2100,x
  bra @next
@abs:
  rep #$20
.ACCU 16
  lda [scrptr],y
  tax
  iny
  iny
  sep #$20
.ACCU 8
  lda [scrptr],y
  iny
  sta $0000.w,x
  bra @next
@dma:
  lda [scrptr],y
  iny
  sta DMAP0
  lda [scrptr],y
  iny
  sta BBAD0
  rep #$20
.ACCU 16
  lda [scrptr],y
  iny
  iny
  sta A1T0
  sep #$20
.ACCU 8
  lda [scrptr],y
  iny
  sta A1B0
  rep #$20
.ACCU 16
  lda [scrptr],y
  iny
  iny
  sta DAS0
  sep #$20
.ACCU 8
  lda #$01
  sta MDMAEN
  bra @next
@unlock:
  lda #$57
  sta WMUNLOCK
  lda #$4D
  sta WMUNLOCK
  bra @next
