; The test cartridge's screens: the dispatch tables, the title, the menu, the run-all and its summary, the "not available" screen, the SNES
; screen, test 8 (identity), and what every test shares (its header, its checks, the results table).

ScreenEnter:
  .DW TitleEnter, MenuEnter, T1Enter, T2Enter, T3Enter, T4Enter, T5Enter, T6Enter, T7Enter, T8Enter, RunAllEnter, SummaryEnter, NaEnter, RefEnter
ScreenFrame:
  .DW TitleFrame, MenuFrame, T1Frame, T2Frame, T3Frame, T4Frame, T5Frame, T6Frame, T7Frame, T8Frame, Nothing, SummaryFrame, BackOnB, RefFrame

Nothing:
  rts

; ================================================================================================ what the tests share

; The features each test needs (all of them; tests 6 and 7 test what there is and need any one of theirs: NeedAny).
NeedAll:
  .DB FEAT_PICTURE, FEAT_PICTURE | FEAT_SPRITES, FEAT_PICTURE | FEAT_SPRITES, FEAT_PICTURE | FEAT_SPRITES, FEAT_SOUND, 0, 0, 0
NeedAny:
  .DB 0, 0, 0, 0, 0, FEAT_FASTCPU | FEAT_FASTDMA, FEAT_BIGWRAM | FEAT_PICTURE | FEAT_SPRITES | FEAT_SOUND, 0

; MarkUnavailable: each test whose features the console lacks gets status 'N' (at power-on, and again at each run-all).
MarkUnavailable:
.ACCU 8
.INDEX 16
  ldx #$0000
-
  lda NeedAll.w,x
  and RES_FEAT
  cmp NeedAll.w,x
  bne @na
  lda NeedAny.w,x
  beq @ok
  and RES_FEAT
  bne @ok
@na:
  lda #'N'
  sta RES_STATUS,x
@ok:
  inx
  cpx #8
  bne -
  rts

; TestGate: A = the test (1-8). Not available: the "not available" screen (in a run-all, the next test). Else returns.
TestGate:
.ACCU 8
  sta testn
  dec a
  rep #$20
.ACCU 16
  and #$00FF
  tax
  sep #$20
.ACCU 8
  lda RES_STATUS,x
  cmp #'N'
  beq +
  rts
+
  lda runall
  beq +
  jmp RunAllNext
+
  lda #SC_NA
  jmp GoScreen

; BeginTest: the test in testn starts: no checks yet, status 'R'.
BeginTest:
.ACCU 8
  stz checks
  stz fails
  stz nocount
  jsr TestIndex
  lda #'R'
  sta RES_STATUS,x
  rts
; EndTest: its status ('P' when every check passed, else 'F') and its counts into the results table.
EndTest:
  jsr TestIndex
  lda checks
  sta RES_CHECKS,x
  lda fails
  sta RES_FAILS,x
  lda fails
  bne +
  lda #'P'
  bra ++
+
  lda #'F'
++
  sta RES_STATUS,x
  rts
TestIndex:
  lda testn
  dec a
  rep #$20
.ACCU 16
  and #$00FF
  tax
  sep #$20
.ACCU 8
  rts

; Judge: carry set = the check passed. Counts it and prints PASS or FAIL at X.
Judge:
.ACCU 8
  bcc @fail
  lda nocount
  bne +
  inc checks
+
  lda #'P'
  jmp PrintStatus
@fail:
  lda nocount
  bne +
  inc checks
  inc fails
  lda #'F'
  jmp PrintStatus
+
  phx
  jsr TestIndex
  lda #'F'
  sta RES_STATUS,x      ; a later round failed: the test fails
  plx
  jmp PrintStatus

; BackOnB: B or START goes back to the menu.
BackOnB:
.ACCU 8
  lda pad+1
  and #>(PAD_B | PAD_START)
  beq +
  jmp BackToMenu
+
  rts

; Header: the test's number and name (row 1), "B: MENU" on the right, a panel from row 3 to row 25.
Header:
.ACCU 8
.INDEX 16
  lda #S_YELLOW
  jsr SetStyle
  lda #1
  ldy #2
  jsr At
  lda testn
  ora #'0'
  jsr PutChar
  inx
  inx
  inx
  inx
  jsr ItemName
  jsr Print
  PRINT 1, 41, TxtBMenu
  lda #3
  sta box_r0
  lda #1
  sta box_c0
  lda #25
  sta box_r1
  lda #48
  sta box_c1
  jmp Panel

; ItemName: strptr = the name of menu item testn - 1 (0-8).
ItemName:
  lda testn
  dec a
ItemNameA:
  rep #$20
.ACCU 16
  and #$00FF
  asl a
  tay
  lda ItemNames.w,y
  sta strptr
  sep #$20
.ACCU 8
  stz strptr+2          ; the names are in bank 0
  rts

; ShowAWhile: in a run-all, the screen stays up for a second before the next test.
ShowAWhile:
.ACCU 8
  lda runall
  beq +
  ldx #60
  stx waitn
-
  jsr WaitFrame
  ldx waitn
  dex
  stx waitn
  bne -
+
  rts

ItemNames:
  .DW TxtT1, TxtT2, TxtT3, TxtT4, TxtT5, TxtT6, TxtT7, TxtT8, TxtRunAll
TxtT1:     .DB "EIGHT LAYERS OF 256 COLOURS", EOS
TxtT2:     .DB "SPRITES: THE 256-SPRITE TABLE", EOS
TxtT3:     .DB "WINDOWS AND COLOUR MATH AT 400", EOS
TxtT4:     .DB "THE SNES MODES DRAWN 400 WIDE", EOS
TxtT5:     .DB "SOUND: 32 VOICES, 16-BIT PCM", EOS
TxtT6:     .DB "FAST CPU AND FAST DMA", EOS
TxtT7:     .DB "MEMORIES: 1 MB WORK RAM AND MORE", EOS
TxtT8:     .DB "IDENTITY: WMID, WMVER, WMFEAT", EOS
TxtRunAll: .DB "RUN ALL SELF-CHECKS", EOS
TxtBMenu:  .DB S_GREY, "B: MENU", EOS

; ================================================================================================ the title

TitleEnter:
.ACCU 8
.INDEX 16
  lda #1
  jsr SetupUI
  PRINT 13, 10, TxtTitle1
  PRINT 15, 7, TxtTitle2
  PRINT 25, 2, TxtTitle3
  jsr ScreenOn
  rts
TitleFrame:
.ACCU 8
  lda pad+1
  and #>PAD_START
  bne +
  lda pad
  and #<PAD_A
  beq ++
+
  lda #SC_MENU
  jmp GoScreen
++
  ; PRESS START blinks
  lda RES_FRAME
  and #$20
  beq +
  PRINT 20, 19, TxtPressStart
  bra ++
+
  PRINT 20, 19, TxtBlank11
++
  lda #1
  sta textdirty
  rts
TxtTitle1:     .DB S_WHITE, "A CONFORMANCE CARTRIDGE FOR SPEC ", S_YELLOW, "1.0", EOS
TxtTitle2:     .DB S_GREY, "FOR BROKENNES, A C++ PORT OR AN FPGA CORE", EOS
TxtTitle3:     .DB S_GREY, "WMFEAT $", HEX8, RES_WMFEAT & $FF, RES_WMFEAT >> 8, "   HOLD SELECT AT POWER-ON: RUN ALL", EOS
TxtPressStart: .DB S_YELLOW, "PRESS START", EOS
TxtBlank11:    .DB "           ", EOS

; ================================================================================================ the menu

MenuEnter:
.ACCU 8
.INDEX 16
  stz runall
  lda #ST_MENU
  sta RES_STATE
  lda #0
  jsr SetupUI
  PRINT 1, 2, TxtMenuHead
  PRINT 1, 36, TxtMenuFeat
  lda #3
  sta box_r0
  lda #1
  sta box_c0
  lda #23
  sta box_r1
  lda #48
  sta box_c1
  jsr Panel
  jsr MenuDraw
  jsr MenuHighlight
  PRINT 25, 2, TxtMenuFoot1
  PRINT 26, 2, TxtMenuFoot2
  jsr ScreenOn
  rts

; MenuDraw: the nine items and each test's status.
MenuDraw:
  stz tmp+6
@item:
  lda #S_WHITE
  jsr SetStyle
  lda tmp+6
  asl a
  clc
  adc #5
  sta tmp+7             ; its row
  ldy #4
  jsr At
  lda tmp+6
  cmp #8
  beq +
  inc a
  ora #'0'
  jsr PutChar
  bra ++
+
  lda #G_RIGHT
  jsr PutChar
++
  inx
  inx
  inx
  inx
  lda tmp+6
  jsr ItemNameA
  jsr Print
  lda tmp+6
  cmp #8
  beq +
  lda tmp+7
  ldy #42
  jsr At
  phx
  lda tmp+6
  rep #$20
.ACCU 16
  and #$00FF
  tax
  sep #$20
.ACCU 8
  lda RES_STATUS,x
  plx
  jsr PrintStatus
+
  inc tmp+6
  lda tmp+6
  cmp #9
  bne @item
  lda #1
  sta textdirty
  rts

; MenuHighlight: the bar under the selected item (and the plain fill under the others).
MenuHighlight:
  stz tmp+6
-
  lda tmp+6
  asl a
  clc
  adc #5
  ldy #PANEL_C
  pha
  lda tmp+6
  cmp sel
  bne +
  ldy #PANEL_HL
+
  pla
  jsr PanelRowTile
  inc tmp+6
  lda tmp+6
  cmp #9
  bne -
  rts

MenuFrame:
.ACCU 8
.INDEX 16
  lda pad+1
  and #>PAD_DOWN
  beq +
  lda sel
  inc a
  cmp #9
  bcc ++
  lda #0
++
  sta sel
  jsr MenuHighlight
+
  lda pad+1
  and #>PAD_UP
  beq +
  lda sel
  dec a
  bpl ++
  lda #8
++
  sta sel
  jsr MenuHighlight
+
  lda pad
  and #<PAD_A
  bne +
  lda pad+1
  and #>PAD_START
  beq ++
+
  lda sel
  cmp #8
  bne +
  lda #SC_RUNALL
  jmp GoScreen
+
  clc
  adc #SC_TEST1
  jmp GoScreen
++
  rts

TxtMenuHead:  .DB S_YELLOW, "WIDEMESS TEST CARTRIDGE 1.0", EOS
TxtMenuFeat:  .DB S_GREY, "WMFEAT ", S_WHITE, "$", HEX8, RES_FEAT & $FF, RES_FEAT >> 8, EOS
TxtMenuFoot1: .DB S_GREY, "UP, DOWN: CHOOSE    A: RUN    B, START: BACK", EOS
TxtMenuFoot2: .DB S_GREY, "HOLD SELECT AT POWER-ON TO RUN ALL SELF-CHECKS", EOS

; ================================================================================================ the run-all and its summary

RunAllOrder:
  .DB 8, 7, 6, 1, 2, 5, 3, 4, 0

RunAllEnter:
.ACCU 8
.INDEX 16
  lda #1
  sta runall
  lda #ST_RUNALL
  sta RES_STATE
  ldx #$0007
-
  stz RES_STATUS,x
  stz RES_CHECKS,x
  stz RES_FAILS,x
  dex
  bpl -
  jsr MarkUnavailable
  ldx #$0000
  stx runstep
; RunAllNext: the next test of RunAllOrder, or the summary.
RunAllNext:
.ACCU 8
.INDEX 16
  ldx runstep
  lda RunAllOrder.w,x
  beq +
  inx
  stx runstep
  clc
  adc #SC_TEST1 - 1
  jmp GoScreen
+
  stz runall
  inc RES_RUNS
  lda #ST_DONE
  sta RES_STATE
  lda #SC_SUMMARY
  jmp GoScreen

SummaryEnter:
.ACCU 8
.INDEX 16
  lda #0
  jsr SetupUI
  PRINT 1, 2, TxtSumHead
  PRINT 1, 41, TxtBMenu
  lda #3
  sta box_r0
  lda #1
  sta box_c0
  lda #25
  sta box_r1
  lda #48
  sta box_c1
  jsr Panel
  PRINT 4, 4, TxtSumCols
  stz tmp+6
  stz errs              ; tests failed
@row:
  lda tmp+6
  asl a
  clc
  adc #6
  sta tmp+7
  lda #S_WHITE
  jsr SetStyle
  lda tmp+7
  ldy #4
  jsr At
  lda tmp+6
  inc a
  ora #'0'
  jsr PutChar
  inx
  inx
  lda tmp+6
  jsr ItemNameA
  jsr Print
  lda tmp+7
  ldy #38
  jsr At
  phx
  lda tmp+6
  rep #$20
.ACCU 16
  and #$00FF
  tax
  sep #$20
.ACCU 8
  lda RES_STATUS,x
  cmp #'F'
  bne +
  inc errs
+
  lda RES_CHECKS,x
  sta tmp+4
  lda RES_FAILS,x
  sta tmp+5
  lda RES_STATUS,x
  plx
  jsr PrintStatus
  lda #S_GREY
  jsr SetStyle
  inx
  inx
  lda tmp+4
  jsr PrintDec8
  inc tmp+6
  lda tmp+6
  cmp #8
  bne @row
  lda errs
  bne +
  PRINT 23, 4, TxtSumPass
  bra ++
+
  PRINT 23, 4, TxtSumFail
++
  jsr ScreenOn
  rts
SummaryFrame:
  jmp BackOnB
TxtSumHead: .DB S_YELLOW, "SELF-CHECKS: THE RESULTS", EOS
TxtSumCols: .DB S_CYAN, "TEST                              RESULT CHECKS", EOS
TxtSumPass: .DB S_GREEN, "EVERY SELF-CHECK PASSED.", S_GREY, " EYE: COMPARE THE PICTURE", NL, "WITH ITS REFERENCE (README.MD).", EOS
TxtSumFail: .DB S_RED, "A SELF-CHECK FAILED:", S_GREY, " OPEN THE TEST TO SEE WHICH.", EOS

; ================================================================================================ "not available"

NaEnter:
.ACCU 8
.INDEX 16
  lda #0
  jsr SetupUI
  jsr Header
  PRINT 5, 4, TxtNa1
  ; the bits it needs that the console lacks
  jsr TestIndex
  lda NeedAll.w,x
  ora NeedAny.w,x
  sta tmp+4
  lda #7
  sta tmp+6
  stz tmp+5             ; bit number
-
  lda tmp+4
  lsr tmp+4
  bcc +
  lda tmp+5
  jsr FeatureLine
+
  inc tmp+5
  lda tmp+5
  cmp #6
  bne -
  PRINT 16, 4, TxtNa2
  jsr ScreenOn
  rts
; FeatureLine: A = a WMFEAT bit: its line on row tmp+6 (then the next row): YES or NO (from RES_FEAT), the bit, what it is.
FeatureLine:
.ACCU 8
.INDEX 16
  pha
  lda tmp+6
  ldy #4
  jsr At
  pla
  pha
  rep #$20
.ACCU 16
  and #$00FF
  tay
  sep #$20
.ACCU 8
  lda BitMasks.w,y
  and RES_FEAT
  bne +
  PRINTX TxtNo
  bra ++
+
  PRINTX TxtYes
++
  inx
  inx
  lda #S_GREY
  jsr SetStyle
  lda #'B'
  jsr PutChar
  lda #'I'
  jsr PutChar
  lda #'T'
  jsr PutChar
  inx
  inx
  pla
  pha
  ora #'0'
  jsr PutChar
  inx
  inx
  inx
  inx
  pla
  rep #$20
.ACCU 16
  and #$00FF
  asl a
  tay
  lda FeatNames.w,y
  sta strptr
  sep #$20
.ACCU 8
  stz strptr+2
  lda #S_WHITE
  jsr SetStyle
  jsr Print
  inc tmp+6
  rts
BitMasks:  .DB 1, 2, 4, 8, 16, 32, 64, 128
FeatNames: .DW TxtF0, TxtF1, TxtF2, TxtF3, TxtF4, TxtF5, TxtF6, TxtF7
TxtF0: .DB "PICTURE: 400 WIDE, 8 LAYERS", EOS
TxtF1: .DB "SPRITES+: TABLE, WINDOWS, MODES", EOS
TxtF2: .DB "SOUND+: 32 VOICES, PCM, 1 MB", EOS
TxtF3: .DB "FAST CPU", EOS
TxtF4: .DB "1 MB WORK RAM", EOS
TxtF5: .DB "FAST DMA", EOS
TxtF6: .DB "(RESERVED)", EOS
TxtF7: .DB "DEBUG PORT", EOS
TxtYes: .DB S_GREEN, "YES", EOS
TxtNo:  .DB S_RED, "NO ", EOS
TxtNa1: .DB S_RED, "NOT AVAILABLE ON THIS CONSOLE.", S_WHITE, " IT NEEDS:", EOS
TxtNa2: .DB S_GREY, "THE CARTRIDGE READS WMFEAT ($4224) AND RUNS ONLY", NL, "WHAT THE CONSOLE HAS: NOTHING OF THIS TEST RAN.", NL, NL, "ITS STATUS IN THE RESULTS TABLE IS 'N'.", EOS

; ================================================================================================ the SNES screen: not a WideMess, or one without the picture

; In SNES mode 0 (the SNES's own picture, which every SNES and every WideMess draws): the font in 2 bits a pixel, BG1's map at word $0400.
SnesScreen:
.ACCU 8
.INDEX 16
  jsr ResetVideo
  stz NMITIMEN
  jsr SnesFont
  ; the eight styles as mode 0's eight 4-colour palettes of BG1
  stz CGADD
  ldx #$0000
-
  lda SnesPalette.w,x
  sta CGDATA
  inx
  cpx #64
  bne -
  ; the text, written into the text buffer by Print, then copied into BG1's map: code | style << 10
  jsr ClearText
  PRINT 2, 1, TxtSnes0
  lda RES_STATE
  cmp #ST_NOPICTURE
  beq +
  PRINT 5, 1, TxtSnes1
  bra ++
+
  PRINT 5, 1, TxtSnes2
++
  PRINT 22, 1, TxtSnes3
  jsr TextToSnes
  jsr DmaSnesText
  lda #$01
  sta TM                ; BG1
  stz BGMODE
  lda #$04
  sta BG1SC             ; map at word $0400, 32 x 32
  stz BG12NBA           ; tiles at word 0
  lda #$0F
  sta INIDISP
-
  bra -

; TextToSnes: the text buffer's columns 0-31 of rows 0-27, as SNES BG map entries (code | style << 10: the style is the palette) in SNESBUF.
TextToSnes:
.ACCU 8
.INDEX 16
  ldx #$0000
  ldy #$0000
-
  rep #$20
.ACCU 16
  lda.l TEXTBUF,x       ; tile = style x 128 + code, bit 12 the priority
  and #$007F
  sta tmp               ; the code
  lda.l TEXTBUF,x
  and #$0380            ; style x 128
  asl a
  asl a
  asl a                 ; style x 1024: the palette field (bits 10-12)
  ora tmp
  phx
  tyx
  sta.l SNESBUF,x
  plx
  iny
  iny
  inx
  inx
  txa                   ; 32 entries a row: skip the buffer's columns 32-63
  and #$007F
  cmp #$0040
  bne +
  txa
  clc
  adc #$0040
  tax
+
  sep #$20
.ACCU 8
  cpx #(28 * 128)
  bcc -
  rts

; DmaSnesText: SNESBUF to SNES video RAM word $0400 (BG1's map). In forced blank or vblank.
DmaSnesText:
  lda #$80
  sta VMAIN
  stz VMADDL
  lda #$04
  sta VMADDH
  lda #$01
  sta DMAP0             ; two registers, $2118 / $2119
  lda #<VMDATAL
  sta BBAD0
  ldx #(SNESBUF & $FFFF)
  stx A1T0
  lda #(SNESBUF >> 16)
  sta A1B0
  ldx #(28 * 64)
  stx DAS0
  lda #$01
  sta MDMAEN
  rts

; SnesFont: the 2-bit font to SNES video RAM word 0 and the eight styles as BG1's eight 4-colour palettes (mode 0: colours 0-31).
SnesFont:
  lda #$80
  sta VMAIN
  stz VMADDL
  stz VMADDH
  lda #$01
  sta DMAP0
  lda #<VMDATAL
  sta BBAD0
  ldx #(FONT2BPP & $FFFF)
  stx A1T0
  lda #(FONT2BPP >> 16)
  sta A1B0
  ldx #FONT2BPP_LEN
  stx DAS0
  lda #$01
  sta MDMAEN
  rts
TxtSnes0: .DB S_YELLOW, "WIDEMESS TEST CARTRIDGE 1.0", EOS
TxtSnes1: .DB S_WHITE, "THIS IS A WIDEMESS CARTRIDGE.", NL, NL
          .DB "THIS CONSOLE IS NOT A WIDEMESS:", NL, "WMID ($4221) READ ", S_RED, "$", HEX8, RES_WMID & $FF, RES_WMID >> 8, S_WHITE, ", NOT $57.", NL, NL
          .DB S_GREY, "RUN IT ON A WIDEMESS CONSOLE", NL, "(SPEC 1.0). IN BROKENNES: THE", NL, "CONSOLE MENU, WIDEMESS.", EOS
TxtSnes2: .DB S_WHITE, "THIS WIDEMESS (WMID $57) HAS NO", NL, "PICTURE: WMFEAT BIT 0 IS 0.", NL, NL
          .DB "THE CARTRIDGE DRAWS ITS MENU", NL, "WITH THE WIDEMESS PICTURE, SO", NL, "NOTHING RAN.", NL, NL
          .DB S_GREY, "WMFEAT $", HEX8, RES_WMFEAT & $FF, RES_WMFEAT >> 8, ", MASK $", HEX8, RES_MASK & $FF, RES_MASK >> 8, EOS
TxtSnes3: .DB S_GREY, "RESULTS TABLE: $7E:1000", EOS

; ================================================================================================ test 8: identity (spec section 1)

T8Enter:
.ACCU 8
.INDEX 16
  lda #8
  jsr TestGate
  lda #0
  jsr SetupUI
  jsr Header
  jsr BeginTest
  PRINT 4, 3, TxtIdCols
  ; unlocked
  PRINT 6, 3, TxtIdWmid
  lda RES_WMID
  cmp #$57
  jsr JudgeEq
  PRINT 7, 3, TxtIdVer
  lda RES_WMVER
  cmp #1
  jsr JudgeEq
  PRINT 8, 3, TxtIdRev
  PRINT 9, 3, TxtIdFeat
  ; locked
  PRINT 11, 3, TxtIdLocked
  PRINT 12, 3, TxtIdPreId
  lda RES_PREID
  cmp #$42
  jsr JudgeEq
  PRINT 13, 3, TxtIdPreSpeed
  lda RES_PRESPEED
  cmp #$42
  jsr JudgeEq
  PRINT 14, 3, TxtIdBroken
  lda RES_BROKENID
  cmp #$42
  jsr JudgeEq
  PRINT 15, 3, TxtIdReserved
  lda RES_RESERVED
  cmp #1
  jsr JudgeEq
  ; the feature bits
  PRINT 17, 3, TxtIdBits
  lda #18
  sta tmp+6
  lda #0
  jsr FeatureLine
  lda #1
  jsr FeatureLine
  lda #2
  jsr FeatureLine
  lda #3
  jsr FeatureLine
  lda #4
  jsr FeatureLine
  lda #5
  jsr FeatureLine
  jsr EndTest
  ; copy what it read into its block of the results table
  ldx #$000F
-
  lda RES_WMID,x
  sta RES_T8,x
  dex
  bpl -
  PRINT 24, 3, TxtIdResult
  jsr ScreenOn
  jsr ShowAWhile
  lda runall
  beq +
  jmp RunAllNext
+
  rts
T8Frame:
  jmp BackOnB

; JudgeEq: the flags of a CMP: equal passes. Prints at column 44 of the row the last PRINT used.
JudgeEq:
.ACCU 8
  php
  rep #$20
.ACCU 16
  lda linestart
  and #$FF80
  ora #(44 * 2)
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

TxtIdCols:     .DB S_CYAN, "REGISTER           READ  EXPECTED", EOS
TxtIdWmid:     .DB S_WHITE, "WMID    $4221      ", S_YELLOW, "$", HEX8, RES_WMID & $FF, RES_WMID >> 8, S_GREY, "   $57 'W'", EOS
TxtIdVer:      .DB S_WHITE, "WMVER   $4222      ", S_YELLOW, "$", HEX8, RES_WMVER & $FF, RES_WMVER >> 8, S_GREY, "   $01 (SPEC 1)", EOS
TxtIdRev:      .DB S_WHITE, "WMREV   $4223      ", S_YELLOW, "$", HEX8, RES_WMREV & $FF, RES_WMREV >> 8, S_GREY, "   ANY (1.0 IS $00)", EOS
TxtIdFeat:     .DB S_WHITE, "WMFEAT  $4224      ", S_YELLOW, "$", HEX8, RES_WMFEAT & $FF, RES_WMFEAT >> 8, S_GREY, "   USED: $", HEX8, RES_FEAT & $FF, RES_FEAT >> 8, EOS
TxtIdLocked:   .DB S_CYAN, "AT POWER-ON, BEFORE THE UNLOCK ($4220):", EOS
TxtIdPreId:    .DB S_WHITE, "WMID    $4221      ", S_YELLOW, "$", HEX8, RES_PREID & $FF, RES_PREID >> 8, S_GREY, "   $42 OPEN BUS", EOS
TxtIdPreSpeed: .DB S_WHITE, "WMSPEED $4225      ", S_YELLOW, "$", HEX8, RES_PRESPEED & $FF, RES_PRESPEED >> 8, S_GREY, "   $42 OPEN BUS", EOS
TxtIdBroken:   .DB S_WHITE, "$57,$00,$4D: WMID  ", S_YELLOW, "$", HEX8, RES_BROKENID & $FF, RES_BROKENID >> 8, S_GREY, "   $42 LOCKED", EOS
TxtIdReserved: .DB S_WHITE, "$4227-$422F", S_GREY, "   (UNLOCKED)   ALL $00", EOS
TxtIdBits:     .DB S_CYAN, "WMFEAT'S BITS", S_GREY, " (MASK $", HEX8, RES_MASK & $FF, RES_MASK >> 8, ")", EOS
TxtIdResult:   .DB S_WHITE, "RESULT  ", STATUS, (RES_STATUS + 7) & $FF, (RES_STATUS + 7) >> 8, S_GREY, "  CHECKS ", DEC8, (RES_CHECKS + 7) & $FF, (RES_CHECKS + 7) >> 8, "  FAILED ", DEC8, (RES_FAILS + 7) & $FF, (RES_FAILS + 7) >> 8, EOS

; ================================================================================================ the tests not built yet (each replaced as it is written)

RefEnter:
  jmp BackToMenu
T2Enter:
T3Enter:
T4Enter:
T5Enter:
.ACCU 8
.INDEX 16
  lda screen
  sec
  sbc #SC_TEST1 - 1
  jsr TestGate
  lda #0
  jsr SetupUI
  jsr Header
  PRINT 6, 4, TxtTodo
  jsr ScreenOn
  lda runall
  beq +
  jmp RunAllNext
+
  rts
T2Frame:
T3Frame:
T4Frame:
T5Frame:
RefFrame:
  jmp BackOnB
TxtTodo: .DB S_ORANGE, "THIS TEST IS NOT IN THIS BUILD YET.", EOS
