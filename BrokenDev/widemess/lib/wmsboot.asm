; wmsboot.asm: the WideMess devkit's start-up clear of work RAM (BrokenDev's WideMess devkit, see docs/projects/widemess-devkit.md).
;
; Why: a WideMess powers on as a SNES (widemess-spec.md section 1), and a SNES's work RAM powers on in any state. PVSnesLib's crt0 clears
; one .bss section only (wlalink names every object's .bss ".bss" and SECTIONSTART_/SECTIONEND_.bss resolve to the first), so the rest of a
; game's C globals, the library's, and 816-tcc's registers held power-on garbage: BrokenDev's D4 checks found 8 variables of the sample game
; read before written (game_frame, rec_pos, tcc__registers_nmi_isr, ...). Super VRUN fixed the same thing in its main()
; (src/bootclear.asm); the devkit does it before anything runs, so every game made from the template gets it.
;
; The RESET vector (hdr.asm) points here. Before PVSnesLib's crt0 (tcc__start) runs, with no stack, no NMI and no IRQ in use:
;   1. the unlock (WMUNLOCK $57 $4D) and fast DMA (WMDMA = 1): a byte every 4 master clocks. On a SNES both writes go nowhere.
;   2. the SNES's 128 KB ($7E:0000-$7F:FFFF, low RAM and the stack included): two 64 KB DMA fills from one zero byte through WMDATA ($2180).
;   3. the far work RAM the game declares: every .RAMSECTION in banks $70-$7D of the game's and the library's assembly, from the table
;      wms_far_clear that wmsbuild.ps1 writes (build/gen/wmsclear.asm, one entry per section: its SECTIONSTART_ label and size, which the
;      linker fills in), each a DMA fill through the 20-bit WRAM port. Far RAM no section declares is not cleared.
;   4. back to the reset state: WMDMA = 0, the WRAM port's address 0, 8-bit registers, emulation mode; then crt0, as the RESET vector did.
;      The console stays unlocked (only a reset locks it); wmsInit() unlocks it again anyway.
; Cost: (128 KB + the far sections) x 4 master clocks: 1.5 frames for the template, 2.3 for the sample, once, at power-on.
;
; -D WMS_NOUNLOCK (the negative control that never unlocks): no unlock, so no fast DMA and no 20-bit port: the 128 KB are cleared at
; 8 clocks a byte and the far work RAM is not. -D WMS_NOCLEAR: no clear at all, the start-up before this file (the checks' negative
; control: its work RAM is read before written again).
;
; Bank 0 of a big game is nearly full (the sample has 35 bytes free), so only a 4-byte jump sits there, where the RESET vector can reach it
; ($00:8000-FFFF); the clear itself is SUPERFREE and runs from its own bank.

.include "hdr.asm"

.ACCU 8
.INDEX 8

.BANK 0 SLOT 0
.ORG $8000
.SECTION ".wms_boot_vector" SEMIFREE
wms_boot:
    jml wms_boot_clear
.ENDS

.SECTION ".wms_boot_text" SUPERFREE

wms_boot_zero:
    .db 0

.ACCU 8
.INDEX 8
wms_boot_clear:
.ifndef WMS_NOCLEAR
    sei
    clc
    xce                         ; native mode, A and X/Y still 8-bit
.ifndef WMS_NOUNLOCK
    lda.b #$57
    sta.l $004220
    lda.b #$4D
    sta.l $004220               ; WMUNLOCK: 'W' 'M'
    lda.b #$01
    sta.l $004226               ; WMDMA: fast general DMA
.endif
    ; DMA channel 0: DMAP $08 (A-bus address fixed, one byte to one B-bus register), BBAD $80 (WMDATA), source wms_boot_zero
    lda.b #$08
    sta.l $004300
    lda.b #$80
    sta.l $004301
    lda.b #<wms_boot_zero
    sta.l $004302
    lda.b #>wms_boot_zero
    sta.l $004303
    lda.b #:wms_boot_zero
    sta.l $004304

    ; the SNES's 128 KB: work-RAM address $00000, 2 x 64 KB (a size of 0 is 64 KB; the port's address runs on)
    lda.b #$00
    sta.l $002181
    sta.l $002182
    sta.l $002183
    sta.l $004305
    sta.l $004306
    lda.b #$01
    sta.l $00420B               ; MDMAEN: channel 0
    lda.b #$00
    sta.l $004305
    sta.l $004306
    lda.b #$01
    sta.l $00420B

.ifndef WMS_NOUNLOCK
    ; the far work RAM's sections: entries of 5 bytes (24-bit address, 16-bit size; 0 = 64 KB), ended by a bank byte of 0
    rep #$10
.INDEX 16
    ldx.w #$0000
_wb_next:
    lda.l wms_far_clear+2,x     ; the section's bank, $70-$7D
    beq _wb_far_done
    sec
    sbc.b #$6E
    sta.l $002183               ; work-RAM bank = bank - $6E: $70 is $2, $7D is $F (spec 2)
    lda.l wms_far_clear,x
    sta.l $002181
    lda.l wms_far_clear+1,x
    sta.l $002182
    lda.l wms_far_clear+3,x
    sta.l $004305
    lda.l wms_far_clear+4,x
    sta.l $004306
    lda.b #$01
    sta.l $00420B
    inx
    inx
    inx
    inx
    inx
    bra _wb_next
_wb_far_done:
    sep #$10
.INDEX 8
    lda.b #$00
    sta.l $004226               ; WMDMA: normal DMA, as at reset
.endif
    lda.b #$00
    sta.l $002181
    sta.l $002182
    sta.l $002183               ; the WRAM port's address back to 0
    sec
    xce                         ; emulation mode, as at reset
.endif
    jml tcc__start              ; PVSnesLib's crt0

.ENDS
