; The BrokenDev debug port for ca65 (NES), milestone D7 of docs/projects/brokendev-debugging-plan.md ("D7 settled").
;
; BrokenNes listens to $401C-$401F while its debugger or dev mode is on; real hardware and other emulators ignore the writes (the 2A03's
; dead CPU-test range). Every macro assembles to NOTHING unless BROKENDEV_DEBUG is defined (ca65 -D BROKENDEV_DEBUG): the release build is
; the same source without the define.
;
; NOT ASSEMBLED HERE: ca65 is not installed on this machine (a download needs the user's approval). The bytes each macro writes are the
; protocol of Workshop/Debug/PortTestRoms.cs, which --port-test checks; the strip relies only on .ifdef.
;
;   DBG_PRINT "room loaded"                 a print (A is clobbered by every macro)
;   DBG_PRINT_BYTE "room %hu", room         a print with one byte from memory (%hu %hd %hx %c)
;   DBG_PRINT_WORD "score %u", score        a print with a 16-bit little-endian word from memory (%u %d %x)
;   DBG_ASSERT_FAIL "player.s: hp > max"    an assert that failed (branch around it on the condition)
;   DBG_MARK "boss phase 2"                 a marker
;   DBG_TIMER_NAME 1, "draw_hud"            once; then DBG_TIMER_START 1 ... DBG_TIMER_STOP 1 (one LDA/STA each)
;   DBG_BREAK                               the debugger stops here

BROKENDEV_DBG_DATA  = $401C
BROKENDEV_DBG_CMD   = $401D
BROKENDEV_DBG_START = $401E
BROKENDEV_DBG_STOP  = $401F

.macro DBG__TEXT text
  .repeat .strlen(text), i
    lda #.strat(text, i)
    sta BROKENDEV_DBG_DATA
  .endrepeat
.endmacro

.macro DBG_PRINT text
.ifdef BROKENDEV_DEBUG
    DBG__TEXT text
    lda #$01
    sta BROKENDEV_DBG_CMD
.endif
.endmacro

.macro DBG_PRINT_BYTE text, addr
.ifdef BROKENDEV_DEBUG
    DBG__TEXT text
    lda #$00
    sta BROKENDEV_DBG_DATA
    lda addr
    sta BROKENDEV_DBG_DATA
    lda #$01
    sta BROKENDEV_DBG_CMD
.endif
.endmacro

.macro DBG_PRINT_WORD text, addr
.ifdef BROKENDEV_DEBUG
    DBG__TEXT text
    lda #$00
    sta BROKENDEV_DBG_DATA
    lda addr
    sta BROKENDEV_DBG_DATA
    lda addr+1
    sta BROKENDEV_DBG_DATA
    lda #$01
    sta BROKENDEV_DBG_CMD
.endif
.endmacro

.macro DBG_ASSERT_FAIL text
.ifdef BROKENDEV_DEBUG
    DBG__TEXT text
    lda #$02
    sta BROKENDEV_DBG_CMD
.endif
.endmacro

.macro DBG_MARK text
.ifdef BROKENDEV_DEBUG
    DBG__TEXT text
    lda #$03
    sta BROKENDEV_DBG_CMD
.endif
.endmacro

.macro DBG_TIMER_NAME id, text
.ifdef BROKENDEV_DEBUG
    lda #id
    sta BROKENDEV_DBG_DATA
    DBG__TEXT text
    lda #$07
    sta BROKENDEV_DBG_CMD
.endif
.endmacro

.macro DBG_TIMER_START id
.ifdef BROKENDEV_DEBUG
    lda #id
    sta BROKENDEV_DBG_START
.endif
.endmacro

.macro DBG_TIMER_STOP id
.ifdef BROKENDEV_DEBUG
    lda #id
    sta BROKENDEV_DBG_STOP
.endif
.endmacro

.macro DBG_BREAK
.ifdef BROKENDEV_DEBUG
    lda #$06
    sta BROKENDEV_DBG_CMD
.endif
.endmacro
