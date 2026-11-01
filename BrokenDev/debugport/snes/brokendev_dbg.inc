; The BrokenDev debug port for WLA-DX 65816 assembly (the SNES and the WideMess), milestone D7 of
; docs/projects/brokendev-debugging-plan.md ("D7 settled"). BrokenNes listens to $4230-$423F while its debugger or dev mode is on; real
; hardware and other emulators ignore the writes. Every macro assembles to NOTHING unless BROKENDEV_DEBUG is defined (wla-65816 -D
; BROKENDEV_DEBUG, or a .DEFINE before the .include; the WideMess devkit's -Define BROKENDEV_DEBUG puts it in hdr.asm): a release build
; is the same source without the define.
;
; FOR 16-BIT A (.ACCU 16: 816-tcc's code, the WideMess library's). The macros use no SEP / REP, because WLA-DX follows them and would
; size the caller's immediates wrongly afterwards; each 16-bit store writes two registers on purpose: DATA $4230 + $4231 (two bytes of
; the text's address), DATA $4231 + COMMAND $4232 (the bank, then the command, in one store), TIMER $4234 / $4236 + the ignored byte
; after. In 8-bit code, wrap a macro in rep #$20 / .ACCU 16 ... sep #$20 / .ACCU 8. A is kept (PHA / PLA); X, Y and P are not touched.
;
; The text goes inline after the macro's code (a BRA skips it) and BrokenNes reads it through the pointer command ($81-$83).
;
;   DBG_PRINT "room loaded"
;   DBG_ASSERT_FAIL "player.asm: hp > max"   (branch around it on the condition)
;   DBG_MARK "boss phase 2"
;   DBG_TIMER_START 1  ...  DBG_TIMER_STOP 1    (name it once from C, DBG_TIMER_NAME, or the timer is "timer 1")
;   DBG_BREAK

.IFNDEF BROKENDEV_DBG_INC
.DEFINE BROKENDEV_DBG_INC 1

.MACRO BDBG_SAY ARGS cmd, text
.IFDEF BROKENDEV_DEBUG
    pha
    lda.w #bdbg_text\@
    sta.l $004230
    lda.w #(:bdbg_text\@) | (cmd << 8)
    sta.l $004231
    pla
    bra bdbg_end\@
bdbg_text\@:
    .DB text, 0
bdbg_end\@:
.ENDIF
.ENDM

.MACRO DBG_PRINT ARGS text
    BDBG_SAY $81, text
.ENDM

.MACRO DBG_ASSERT_FAIL ARGS text
    BDBG_SAY $82, text
.ENDM

.MACRO DBG_MARK ARGS text
    BDBG_SAY $83, text
.ENDM

.MACRO DBG_TIMER_START ARGS id
.IFDEF BROKENDEV_DEBUG
    pha
    lda.w #id
    sta.l $004234
    pla
.ENDIF
.ENDM

.MACRO DBG_TIMER_STOP ARGS id
.IFDEF BROKENDEV_DEBUG
    pha
    lda.w #id
    sta.l $004236
    pla
.ENDIF
.ENDM

.MACRO DBG_BREAK
.IFDEF BROKENDEV_DEBUG
    pha
    lda.w #$0006
    sta.l $004232
    pla
.ENDIF
.ENDM

.ENDIF
