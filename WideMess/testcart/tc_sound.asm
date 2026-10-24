; Test 5: the sound chip (spec section 4). The 65816 writes the SPC700 program (tc_spc.asm), the samples and both directories into sound RAM
; through its port ($21F0-$21F3), starts the program with the boot loader's jump, and then talks to it through the mailbox ($0400: lists
; of DSP register writes) and reads its report ($0500: what the DSP shows of each voice) through the same port.
; Self-checks: WID; each of the 32 voices alone sounds (ENVX and OUTX) while the other 31 are silent; all 32 at once; a PCM voice's playing
; address is in its sample and moves; it sets its WENDX bit when it loops; the echo buffer stays 0 without WEON and fills with it.
; Then live: a grid of the 32 voices, each lit by its envelope. Results: RES_T5 (README.md).

.DEFINE RES_T5WID   RES_T5 + $00
.DEFINE RES_T5ALONE RES_T5 + $01      ; 4 bytes: bit v (voice v) when it sounded alone, the others silent
.DEFINE RES_T5ALL   RES_T5 + $05      ; 4 bytes: the voices sounding with all 32 keyed on
.DEFINE RES_T5ECHO0 RES_T5 + $09      ; 1: the echo buffer stayed 0 without WEON
.DEFINE RES_T5ECHO1 RES_T5 + $0A      ; 1: it filled with WEON
.DEFINE RES_T5PCM   RES_T5 + $0B      ; 1: voice 8's playing address was in its PCM sample and moved
.DEFINE RES_T5WENDX RES_T5 + $0C      ; WENDX of bank 0 after voice 8 looped
.DEFINE RES_T5STEPS RES_T5 + $0D      ; the checks' step (for a harness: where it stopped)
.DEFINE RES_T5LOADED RES_T5 + $0E     ; 1: the program runs
.DEFINE REPORT  $7E7F00               ; the SPC700's report, copied each frame (176 bytes)
.DEFINE MAILBUF $7E7E00               ; a mailbox list being made
.DEFINE REP_ENVX $10
.DEFINE REP_OUTX $30
.DEFINE REP_ADL  $50
.DEFINE REP_ADM  $70
.DEFINE REP_ADH  $90

; ---------------------------------------------------------------------------------------------------- the port and the mailbox

; SndAt: the sound RAM address in A (16 bits) and X (the bank, 8 bits).
SndAt:
.ACCU 16
  sep #$20
.ACCU 8
  sta WMSADDL
  xba
  sta WMSADDM
  txa
  sta WMSADDH
  rts

; SndWrite: dlen bytes from [ptr] to the sound RAM address set by SndAt (DMA channel 0).
SndWrite:
.ACCU 8
.INDEX 16
  stz DMAP0
  lda #<WMSDATA
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

.MACRO SND_PUT ARGS src, dst, len
  rep #$20
.ACCU 16
  lda #(dst & $FFFF)
  ldx #(dst >> 16)
  jsr SndAt
.ACCU 8
  ldx #(src & $FFFF)
  stx ptr
  lda #(src >> 16)
  sta ptr+2
  ldx #len
  stx dlen
  jsr SndWrite
.ENDM

; SndReport: the SPC700's report ($0500, 176 bytes) into REPORT.
SndReport:
  rep #$20
.ACCU 16
  lda #$0500
  ldx #$0000
  jsr SndAt
.ACCU 8
  lda #$80
  sta DMAP0
  lda #<WMSDATA
  sta BBAD0
  ldx #(REPORT & $FFFF)
  stx A1T0
  lda #(REPORT >> 16)
  sta A1B0
  ldx #176
  stx DAS0
  lda #$01
  sta MDMAEN
  rts

; SndList: the list in MAILBUF (a count, then register / value pairs) to the mailbox, a new number on port 0, until the SPC700 echoes it
; (a second at most: then carry clear).
SndList:
.ACCU 8
.INDEX 16
  rep #$20
.ACCU 16
  lda #$0400
  ldx #$0000
  jsr SndAt
.ACCU 8
  lda.l MAILBUF
  rep #$20
.ACCU 16
  and #$00FF
  asl a
  inc a
  sta dlen
  sep #$20
.ACCU 8
  ldx #(MAILBUF & $FFFF)
  stx ptr
  lda #(MAILBUF >> 16)
  sta ptr+2
  jsr SndWrite
  inc sndcmd
  lda sndcmd
  bne +
  inc sndcmd            ; (never 0: the program's first "last command")
  lda sndcmd
+
  sta APUIO0
  ldx #$0000
-
  cmp APUIO0
  beq +
  inx
  cpx #$FFFF
  bne -
  clc
  rts
+
  sec
  rts

; SndListRom: the list at [ptr2] (in ROM) copied into MAILBUF, then SndList.
SndListRom:
  lda [ptr2]
  rep #$20
.ACCU 16
  and #$00FF
  asl a
  tay                   ; the last byte's index
  sep #$20
.ACCU 8
-
  lda [ptr2],y
  tyx
  sta.l MAILBUF,x
  dey
  bpl -
  jmp SndList

; MbStart / MbAdd: making a list in MAILBUF. MbAdd: A = the register, then the value in tmp+7.
MbStart:
  lda #$00
  sta.l MAILBUF
  rts
MbAdd:
  pha
  lda.l MAILBUF
  rep #$20
.ACCU 16
  and #$00FF
  asl a
  tax
  sep #$20
.ACCU 8
  pla
  sta.l MAILBUF+1,x
  lda tmp+7
  sta.l MAILBUF+2,x
  lda.l MAILBUF
  inc a
  sta.l MAILBUF
  rts
.MACRO MB ARGS reg, value
  lda #value
  sta tmp+7
  lda #reg
  jsr MbAdd
.ENDM

; VoiceReg: A = voice v (0-31), tmp+6 = a voice register (0-$0F): A = its DSP address; for v >= 8 the list first selects its bank (WBANK).
VoiceReg:
  cmp #8
  bcs +
  asl a
  asl a
  asl a
  asl a
  ora tmp+6
  rts
+
  pha
  sec
  sbc #8
  lsr a
  lsr a
  lsr a
  sta tmp+7
  lda #$8C
  jsr MbAdd             ; WBANK
  pla
  sec
  sbc #8
  and #$07
  asl a
  asl a
  asl a
  asl a
  ora tmp+6
  ora #$80
  rts

; MbVoice: voice A's register tmp+6 = tmp+5.
MbVoice:
  jsr VoiceReg
  pha
  lda tmp+5
  sta tmp+7
  pla
  jmp MbAdd

; MbKey: voice A keyed on (KON / WKON bit) when tmp+4 is $4C, keyed off when $5C (the register's offset within a page).
MbKey:
  pha
  cmp #8
  bcs +
  rep #$20
.ACCU 16
  and #$00FF
  tax
  sep #$20
.ACCU 8
  lda BitMasks.w,x
  sta tmp+7
  lda tmp+4
  jsr MbAdd
  pla
  rts
+
  sec
  sbc #8
  pha
  lsr a
  lsr a
  lsr a
  sta tmp+7
  lda #$8C
  jsr MbAdd
  pla
  and #$07
  rep #$20
.ACCU 16
  and #$00FF
  tax
  sep #$20
.ACCU 8
  lda BitMasks.w,x
  sta tmp+7
  lda tmp+4
  ora #$80
  jsr MbAdd
  pla
  rts

; NoteOn: voice A at its own pitch, volume tmp+3 (both sides): KOFF of its page cleared, VOLL, VOLR, KON.
NoteOn:
  sta tmp+2
  jsr MbStart
  lda tmp+2
  jsr KeyOffClear
  lda #0
  sta tmp+6
  lda tmp+3
  sta tmp+5
  lda tmp+2
  jsr MbVoice           ; VOLL
  lda #1
  sta tmp+6
  lda tmp+2
  jsr MbVoice           ; VOLR
  lda #$4C
  sta tmp+4
  lda tmp+2
  jsr MbKey
  jmp SndList

; NoteOff: voice A keyed off.
NoteOff:
  sta tmp+2
  jsr MbStart
  lda #$5C
  sta tmp+4
  lda tmp+2
  jsr MbKey
  jmp SndList

; KeyOffClear: KOFF (or its bank's WKOFF) back to 0 for voice A's page, so a key-on sounds.
KeyOffClear:
  cmp #8
  bcs +
  MB $5C, $00
  rts
+
  sec
  sbc #8
  lsr a
  lsr a
  lsr a
  sta tmp+7
  lda #$8C
  jsr MbAdd
  MB $DC, $00
  rts

; AllOff: every voice keyed off, then KOFF cleared.
AllOff:
  jsr MbStart
  MB $5C, $FF
  MB $8C, 0
  MB $DC, $FF
  MB $8C, 1
  MB $DC, $FF
  MB $8C, 2
  MB $DC, $FF
  jsr SndList
  jsr WaitFrame
  jsr WaitFrame
  jsr MbStart
  MB $5C, $00
  MB $8C, 0
  MB $DC, $00
  MB $8C, 1
  MB $DC, $00
  MB $8C, 2
  MB $DC, $00
  jmp SndList

; ---------------------------------------------------------------------------------------------------- loading

; SndLoad: the program (unless it runs), the samples, the directories; the boot loader's jump; the DSP's set-up.
SndLoad:
.ACCU 8
.INDEX 16
  jsr SndReport
  lda.l REPORT+1
  cmp #$A5
  beq @samples          ; it runs already (sound RAM $0501 is 0 until it does)
  SND_PUT SPC_AT, $000200, SPC_LEN
@samples:
  SND_PUT SND_DIR, $000600, 4
  SND_PUT SND_BRR_LOW, $002000, SND_BRR_LOW_LEN
  SND_PUT SND_WDIR, $010000, SND_WDIR_LEN
  SND_PUT SND_PCM, $010100, SND_PCM_LEN
  SND_PUT SND_BRR, $010200, SND_BRR_LEN
  stz snddirty
  lda.l REPORT+1
  cmp #$A5
  beq @init
  ; the boot loader: $AA $BB, then the jump to $0200
  ldx #$0000
-
  inx
  beq @fail
  lda APUIO0
  cmp #$AA
  bne -
  lda APUIO1
  cmp #$BB
  bne -
  stz APUIO2
  lda #$02
  sta APUIO3            ; address $0200
  stz APUIO1            ; 0: jump, not a transfer
  lda #$CC
  sta APUIO0
  ; until the program's report says it runs
  ldx #120
  stx waitn
-
  jsr WaitFrame
  jsr SndReport
  lda.l REPORT+1
  cmp #$A5
  beq @init
  ldx waitn
  dex
  stx waitn
  bne -
@fail:
  clc
  rts
@init:
  lda #1
  sta RES_T5LOADED
  ldx #DspInit0
  stx ptr2
  stz ptr2+2
  jsr SndListRom
  ldx #DspInit1
  stx ptr2
  jsr SndListRom
  ldx #DspInit2
  stx ptr2
  jsr SndListRom
  sec
  rts

; ---------------------------------------------------------------------------------------------------- the test

T5Enter:
.ACCU 8
.INDEX 16
  lda #5
  jsr TestGate
  jsr BeginTest
  lda #0
  jsr SetupUI
  jsr Header
  PRINT 3, 2, TxtSnd1
  PRINT 13, 2, TxtSnd2
  PRINT 26, 2, TxtSndHelp1
  PRINT 27, 2, TxtSndHelp2
  stz sel5
  stz echomask
  stz echomask+1
  stz echomask+2
  stz echomask+3
  jsr DrawGrid
  jsr ScreenOn
  stz RES_T5STEPS
  jsr SndLoad
  bcs +
  PRINT 16, 2, TxtSndDead
  sec
  bra @dead
+
  jsr SndChecks
  clc
@dead:
  bcc +
  inc checks
  inc fails
+
  jsr EndTest
  jsr ShowChecks
  jsr ShowAWhile
  lda runall
  beq +
  jsr AllOff
  jmp RunAllNext
+
  rts

TxtSnd1: .DB S_CYAN, "32 VOICES, EACH LIT BY ITS ENVELOPE (ENVX)", EOS
TxtSnd2: .DB S_WHITE, "ROW 1: THE S-DSP'S VOICES 0-7 ", S_CYAN, "(BRR, IN 64 KB)", NL
         .DB S_WHITE, "ROWS 2-4: VOICES 8-31, ", S_GREEN, "EVEN 16-BIT PCM, ", S_ORANGE, "ODD BRR", EOS
TxtSndHelp1: .DB S_GREY, "D-PAD: VOICE  A: PLAY  X: ALL 32  Y: ECHO ON/OFF", EOS
TxtSndHelp2: .DB S_GREY, "SELECT: PCM, THEN BRR, ONE NOTE   B: MENU", EOS
TxtSndDead:  .DB S_RED, "THE SPC700 PROGRAM DID NOT START: NO SOUND CHECKS", EOS

; SndChecks: every self-check (counted), the grid lit as it goes.
SndChecks:
  ; WID
  lda #1
  sta RES_T5STEPS
  jsr SndReport
  lda.l REPORT+0
  sta RES_T5WID
  cmp #$57
  jsr JudgeQuiet
  ; each voice alone
  lda #2
  sta RES_T5STEPS
  stz loop0
@voice:
  lda #40
  sta tmp+3
  lda loop0
  jsr NoteOn
  stz loop1             ; its ENVX seen
  stz loop1+1           ; its OUTX seen
  stz loop2             ; the others' ENVX seen
  ldx #5
  stx waitn
-
  jsr WaitFrame
  jsr SndReport
  jsr DrawGridLive
  jsr VoiceSeen
  ldx waitn
  dex
  stx waitn
  bne -
  lda loop0
  jsr NoteOff
  jsr WaitFrame
  jsr WaitFrame
  ; alone: its ENVX and OUTX were not 0, the others' ENVX were
  lda loop1
  beq @bad
  lda loop1+1
  beq @bad
  lda loop2
  bne @bad
  jsr SetAloneBit
  lda #0
  bra +
@bad:
  lda #1
+
  cmp #0                ; (equal: it passed)
  jsr JudgeQuiet
  inc loop0
  lda loop0
  cmp #32
  bne @voice
  ; all 32 at once (quieter)
  lda #3
  sta RES_T5STEPS
  jsr AllOff            ; (KOFF of every page back to 0: each NoteOff left its voice's bit)
  jsr MbStart
  MB $5C, $00
  stz loop0
-
  lda #0
  sta tmp+6
  lda #10
  sta tmp+5
  lda loop0
  jsr MbVoice
  lda #1
  sta tmp+6
  lda loop0
  jsr MbVoice
  inc loop0
  lda loop0
  cmp #32
  bne -
  jsr SndList
  jsr MbStart
  MB $4C, $FF
  MB $8C, 0
  MB $CC, $FF
  MB $8C, 1
  MB $CC, $FF
  MB $8C, 2
  MB $CC, $FF
  jsr SndList
  jsr WaitFrame
  jsr WaitFrame
  jsr WaitFrame
  jsr SndReport
  jsr DrawGridLive
  ; the voices sounding: bit v when ENVX(v) > 0
  ldx #$0000
-
  stz RES_T5ALL,x
  inx
  cpx #4
  bne -
  ldx #$0000
-
  lda.l REPORT+REP_ENVX,x
  beq +
  phx
  jsr SetAllBit
  plx
+
  inx
  cpx #32
  bne -
  lda RES_T5ALL
  and RES_T5ALL+1
  and RES_T5ALL+2
  and RES_T5ALL+3
  cmp #$FF
  jsr JudgeQuiet
  jsr AllOff
  ; voice 8's playing address in its PCM sample, moving; its WENDX bit
  lda #4
  sta RES_T5STEPS
  lda #40
  sta tmp+3
  lda #8
  jsr NoteOn
  jsr WaitFrame
  lda #1
  sta RES_T5PCM         ; until a read says otherwise
  stz loop2             ; reads that differ from the first
  stz loop1+1
  lda #6
  sta loop1             ; reads left
-
  jsr WaitFrame
  jsr SndReport
  lda.l REPORT+REP_ADH+8
  and #$0F
  cmp #$01              ; ($1xxxx)
  bne @out
  lda.l REPORT+REP_ADM+8
  xba
  lda.l REPORT+REP_ADL+8
  rep #$20
.ACCU 16
  cmp #PCM_AT_L
  bcc @out16
  cmp #PCM_END_L
  bcs @out16
  ldx loop1
  cpx #6
  bne +
  sta saved             ; the first read
  bra ++
+
  cmp saved
  beq ++
  inc loop2             ; it moved
++
  sep #$20
.ACCU 8
  bra @in
@out16:
  sep #$20
.ACCU 8
@out:
  stz RES_T5PCM
@in:
  dec loop1
  bne -
  lda loop2
  bne +
  stz RES_T5PCM         ; it never moved
+
  lda RES_T5PCM
  cmp #1
  jsr JudgeQuiet
  lda.l REPORT+5        ; WENDX of bank 0
  sta RES_T5WENDX
  and #$01
  cmp #$01
  jsr JudgeQuiet
  jsr AllOff
  ; the echo: the buffer cleared, echo writes on, EVOL up; voice 8 without WEON (the buffer stays 0), then with it (it fills)
  lda #5
  sta RES_T5STEPS
  rep #$20
.ACCU 16
  lda #$4000
  ldx #$0000
  jsr SndAt
.ACCU 8
  ldx #2048
  lda #<WMSDATA
  jsr FillB             ; zeros (a fixed source)
  jsr MbStart
  MB $2C, $30
  MB $3C, $30
  MB $8C, 0
  MB $CD, $00
  MB $6C, $00           ; FLG: echo writes on
  jsr SndList
  lda #40
  sta tmp+3
  lda #8
  jsr NoteOn
  ldx #6
  jsr Frames
  jsr EchoSum
  stz RES_T5ECHO0
  bne +
  inc RES_T5ECHO0
+
  lda RES_T5ECHO0
  cmp #1
  jsr JudgeQuiet
  jsr MbStart
  MB $8C, 0
  MB $CD, $01           ; WEON: voice 8 into the echo
  jsr SndList
  ldx #6
  jsr Frames
  jsr EchoSum
  stz RES_T5ECHO1
  beq +
  inc RES_T5ECHO1
+
  lda RES_T5ECHO1
  cmp #1
  jsr JudgeQuiet
  jsr MbStart
  MB $8C, 0
  MB $CD, $00
  MB $6C, $20           ; echo writes off again
  MB $2C, $00
  MB $3C, $00
  jsr SndList
  jsr AllOff
  lda #6
  sta RES_T5STEPS
  rts

; JudgeQuiet: the flags of a CMP: equal passes; counted, not printed (ShowChecks prints).
JudgeQuiet:
  beq +
  inc checks
  inc fails
  rts
+
  inc checks
  rts

; Frames: X frames.
Frames:
  stx waitn
-
  jsr WaitFrame
  ldx waitn
  dex
  stx waitn
  bne -
  rts

; EchoSum: the echo buffer ($4000-$47FF) through the port into SCRATCH; A = 0 when every byte is 0 (else 1).
EchoSum:
  rep #$20
.ACCU 16
  lda #$4000
  ldx #$0000
  jsr SndAt
.ACCU 8
  lda #<WMSDATA
  jsr ReadChunk         ; (4 KB read; the first 2 KB are the buffer)
  ldx #$0000
  lda #$00
-
  ora.l SCRATCH,x
  inx
  cpx #2048
  bne -
  cmp #$00
  beq +
  lda #$01
+
  rts

; VoiceSeen: from REPORT, voice loop0's ENVX / OUTX into loop1 / loop1+1 (ORed), the others' ENVX into loop2.
VoiceSeen:
  ldx #$0000
-
  lda.l REPORT+REP_ENVX,x
  cpx loop0
  beq +
  ora loop2
  sta loop2
  bra ++
+
  ora loop1
  sta loop1
  lda.l REPORT+REP_OUTX,x
  ora loop1+1
  sta loop1+1
++
  inx
  cpx #32
  bne -
  rts

SetAloneBit:
  lda loop0
  jsr BitOf
  ora RES_T5ALONE,x
  sta RES_T5ALONE,x
  rts
SetAllBit:              ; X = the voice
  txa
  jsr BitOf
  ora RES_T5ALL,x
  sta RES_T5ALL,x
  rts
; BitOf: A = voice v: X = v / 8, A = 1 << (v mod 8)
BitOf:
  pha
  lsr a
  lsr a
  lsr a
  rep #$20
.ACCU 16
  and #$00FF
  tax
  sep #$20
.ACCU 8
  pla
  and #$07
  phx
  rep #$20
.ACCU 16
  and #$00FF
  tax
  sep #$20
.ACCU 8
  lda BitMasks.w,x
  plx
  rts

; ShowChecks: the self-checks' results (rows 16-18).
ShowChecks:
  PRINT 16, 2, TxtSndC1
  PRINT 17, 2, TxtSndC2
  lda #1
  sta textdirty
  rts
TxtSndC1: .DB S_WHITE, "WID $", HEX8, RES_T5WID & $FF, RES_T5WID >> 8, "  ALONE $", HEX8, (RES_T5ALONE + 3) & $FF, (RES_T5ALONE + 3) >> 8, HEX8, (RES_T5ALONE + 2) & $FF, (RES_T5ALONE + 2) >> 8
          .DB HEX8, (RES_T5ALONE + 1) & $FF, (RES_T5ALONE + 1) >> 8, HEX8, RES_T5ALONE & $FF, RES_T5ALONE >> 8, "  ALL 32 $", HEX8, (RES_T5ALL + 3) & $FF, (RES_T5ALL + 3) >> 8
          .DB HEX8, (RES_T5ALL + 2) & $FF, (RES_T5ALL + 2) >> 8, HEX8, (RES_T5ALL + 1) & $FF, (RES_T5ALL + 1) >> 8, HEX8, RES_T5ALL & $FF, RES_T5ALL >> 8, EOS
TxtSndC2: .DB S_WHITE, "PCM ADDRESS ", DEC8, RES_T5PCM & $FF, RES_T5PCM >> 8, "  WENDX $", HEX8, RES_T5WENDX & $FF, RES_T5WENDX >> 8
          .DB "  ECHO ", DEC8, RES_T5ECHO0 & $FF, RES_T5ECHO0 >> 8, "/", DEC8, RES_T5ECHO1 & $FF, RES_T5ECHO1 >> 8, "   ", STATUS, (RES_STATUS + 4) & $FF, (RES_STATUS + 4) >> 8, EOS

; ---------------------------------------------------------------------------------------------------- the grid

; DrawGrid: the 32 cells' labels (voice number and note); DrawGridLive: their lights from REPORT (ENVX / 8: 16 levels; blue the S-DSP's,
; green PCM, amber BRR), the selected one marked.
DrawGrid:
  stz cellv
-
  jsr CellAt
  lda cellv
  cmp sel5
  bne +
  lda #S_YELLOW
  bra ++
+
  lda #S_WHITE
++
  jsr SetStyle
  lda cellv
  cmp #10
  bcs +
  lda #' '
  jsr PutChar
  lda cellv
  ora #'0'
  jsr PutChar
  bra ++
+
  lda cellv
  jsr TwoDigits
++
  inx
  inx
  lda cellv
  asl a
  rep #$20
.ACCU 16
  and #$00FF
  tay
  sep #$20
.ACCU 8
  lda VoiceNote.w,y
  jsr PutChar
  lda VoiceNote.w+1,y
  jsr PutChar
  inc cellv
  lda cellv
  cmp #32
  bne -
  lda #1
  sta textdirty
  rts
; TwoDigits: A (10-31) as two digits at X.
TwoDigits:
  ldy #0
-
  cmp #10
  bcc +
  sbc #10
  iny
  bra -
+
  pha
  tya
  ora #'0'
  jsr PutChar
  pla
  ora #'0'
  jmp PutChar
; CellAt: X = the text buffer offset of voice cellv's cell: row 5 + 2 x (v / 8), column 2 + 6 x (v mod 8).
CellAt:
  lda cellv
  lsr a
  lsr a
  lsr a
  asl a
  clc
  adc #5
  pha
  lda cellv
  and #$07
  sta tmp
  asl a
  clc
  adc tmp
  asl a
  clc
  adc #2
  rep #$20
.ACCU 16
  and #$00FF
  tay
  sep #$20
.ACCU 8
  pla
  jmp At

DrawGridLive:
  stz cellv
-
  jsr CellAt
  ldx cellv
  lda.l REPORT+REP_ENVX,x
  lsr a
  lsr a
  lsr a
  sta tmp+5             ; the level 0-15
  lda cellv
  cmp #8
  bcs +
  lda #32               ; blue: the S-DSP's
  bra ++
+
  and #$01
  beq +
  lda #48               ; amber: BRR
  bra ++
+
  lda #0                ; green: PCM
++
  clc
  adc tmp+5
  rep #$20
.ACCU 16
  and #$00FF
  clc
  adc #CELL_TILE
  sta saved             ; the tile (CellAt takes Y)
  sep #$20
.ACCU 8
  jsr CellAt
  ldy saved
  jsr PutPanel
  jsr PutPanel
  jsr PutPanel
  jsr PutPanel
  jsr PutPanel
  inc cellv
  lda cellv
  cmp #32
  bne -
  lda #1
  sta paneldirty
  rts

; ---------------------------------------------------------------------------------------------------- live

T5Frame:
.ACCU 8
.INDEX 16
  lda pad+1
  and #>(PAD_B | PAD_START)
  beq +
  jsr AllOff
  jmp BackToMenu
+
  jsr SndReport
  jsr DrawGridLive
  ; the d-pad: the selected voice
  lda sel5
  sta tmp+1
  lda pad+1
  and #>PAD_RIGHT
  beq +
  inc sel5
+
  lda pad+1
  and #>PAD_LEFT
  beq +
  dec sel5
+
  lda pad+1
  and #>PAD_DOWN
  beq +
  lda sel5
  clc
  adc #8
  sta sel5
+
  lda pad+1
  and #>PAD_UP
  beq +
  lda sel5
  sec
  sbc #8
  sta sel5
+
  lda sel5
  and #$1F
  sta sel5
  cmp tmp+1
  beq +
  jsr DrawGrid
+
  ; A: the selected voice's note
  lda pad
  and #<PAD_A
  beq +
  lda #40
  sta tmp+3
  lda sel5
  jsr NoteOn
+
  ; X: all 32 (quieter)
  lda pad
  and #<PAD_X
  beq +
  stz loop0
-
  lda #10
  sta tmp+3
  lda loop0
  jsr NoteOn
  inc loop0
  lda loop0
  cmp #32
  bne -
+
  ; Y: echo on / off for the selected voice
  lda pad+1
  and #>PAD_Y
  beq +
  jsr ToggleEcho
+
  ; SELECT: the same note as 16-bit PCM (voice 8), then BRR (voice 9 at voice 8's pitch)
  lda pad+1
  and #>PAD_SELECT
  beq +
  jsr PcmThenBrr
+
  rts

; ToggleEcho: the selected voice's EON / WEON bit; echo writes and EVOL on while any voice echoes.
ToggleEcho:
  lda sel5
  jsr BitOf
  eor echomask,x
  sta echomask,x
  jsr MbStart
  lda echomask
  sta tmp+7
  lda #$4D
  jsr MbAdd             ; EON (voices 0-7)
  ldx #$0000
-
  txa
  sta tmp+7
  lda #$8C
  phx
  jsr MbAdd
  plx
  lda echomask+1,x
  sta tmp+7
  lda #$CD
  phx
  jsr MbAdd             ; WEON of bank x
  plx
  inx
  cpx #3
  bne -
  lda echomask
  ora echomask+1
  ora echomask+2
  ora echomask+3
  beq +
  MB $6C, $00
  MB $2C, $30
  MB $3C, $30
  bra ++
+
  MB $6C, $20
  MB $2C, $00
  MB $3C, $00
++
  jsr SndList
  PRINT 19, 2, TxtSndEcho
  rts
TxtSndEcho: .DB S_WHITE, "ECHO ON: $", HEX8, (echomask + 3) & $FF, 0, HEX8, (echomask + 2) & $FF, 0, HEX8, (echomask + 1) & $FF, 0, HEX8, echomask & $FF, 0, "   ", EOS

; PcmThenBrr: voice 8 (16-bit PCM) for 40 frames, then voice 9 (BRR, the same tone) at voice 8's pitch for 40 frames.
PcmThenBrr:
  PRINT 21, 2, TxtSndPcm
  lda #1
  sta textdirty
  lda #40
  sta tmp+3
  lda #8
  jsr NoteOn
  ldx #40
  jsr FramesLive
  lda #8
  jsr NoteOff
  ; voice 9 at voice 8's pitch
  jsr MbStart
  lda #2
  sta tmp+6
  lda VoicePitch.w+16
  sta tmp+5
  lda #9
  jsr MbVoice
  lda #3
  sta tmp+6
  lda VoicePitch.w+17
  sta tmp+5
  lda #9
  jsr MbVoice
  jsr SndList
  PRINT 21, 2, TxtSndBrr
  lda #1
  sta textdirty
  lda #40
  sta tmp+3
  lda #9
  jsr NoteOn
  ldx #40
  jsr FramesLive
  lda #9
  jsr NoteOff
  ; voice 9's own pitch back
  jsr MbStart
  lda #2
  sta tmp+6
  lda VoicePitch.w+18
  sta tmp+5
  lda #9
  jsr MbVoice
  lda #3
  sta tmp+6
  lda VoicePitch.w+19
  sta tmp+5
  lda #9
  jsr MbVoice
  jsr SndList
  PRINT 21, 2, TxtSndClear
  lda #1
  sta textdirty
  rts
FramesLive:
  stx waitn
-
  jsr WaitFrame
  jsr SndReport
  jsr DrawGridLive
  ldx waitn
  dex
  stx waitn
  bne -
  rts
TxtSndPcm:   .DB S_GREEN, "NOW: VOICE 8, 16-BIT PCM  ", EOS
TxtSndBrr:   .DB S_ORANGE, "NOW: VOICE 9, BRR, THE SAME NOTE", EOS
TxtSndClear: .DB "                                ", EOS
