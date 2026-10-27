; The BrokenDev debug port's ca65 sample (D7): every macro of brokendev_dbg.inc once, then each frame a timed piece of work, prints
; with a byte and a word, and a marker on the sixth frame. build-proof.ps1 builds it three ways (dev, release, and with every DBG_ line
; taken out) and proves the release ROM is byte-identical to the one without the calls. PLANT_ASSERT (with BROKENDEV_DEBUG) is the
; negative control: an assert that fails from the fourth frame.

.include "brokendev_dbg.inc"

.segment "HEADER"
    .byte "NES", $1A, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0     ; NROM-128, one 16 KB PRG bank, one 8 KB CHR bank

.segment "ZEROPAGE"
frames: .res 1
score:  .res 2

.segment "CODE"
reset:
    sei
    cld
    ldx #$FF
    txs
    lda #0
    sta $2000
    sta $2001
    sta frames
    sta score
    sta score+1
    bit $2002
:   bit $2002
    bpl :-
:   bit $2002
    bpl :-
    DBG_PRINT "hello from ca65"
    DBG_TIMER_NAME 1, "work"
    DBG_MARK "start"
    lda #$80
    sta $2000
loop:
    lda frames
:   cmp frames
    beq :-
    DBG_TIMER_START 1
    clc
    lda score
    adc #3
    sta score
    lda score+1
    adc #0
    sta score+1
    DBG_TIMER_STOP 1
    DBG_PRINT_BYTE "frame %hu", frames
    DBG_PRINT_WORD "score %u", score
.ifdef PLANT_ASSERT
    lda frames
    cmp #4
    bcc :+
    DBG_ASSERT_FAIL "main.s: frames < 4"
:
.endif
    lda frames
    cmp #6
    bne :+
    DBG_MARK "six frames"
:   jmp loop

nmi:
    inc frames
irq:
    rti

.segment "VECTORS"
    .word nmi, reset, irq

.segment "CHARS"
    .res $2000, 0
