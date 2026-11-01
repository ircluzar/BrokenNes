; WideMess picture test ROMs (milestone 2): the register player every ROM of this folder shares (docs/projects/widemess-spec.md, sections 3.1-3.4).
;
; The 65816 sets up the picture by playing a script of register writes and DMA transfers that gen_picture.py generates for each ROM, then turns
; NMI on and waits. The same generator draws, from its own model of the spec, the picture the ROM must produce: that picture is the test.
; Every write goes through the 65816 or the DMA unit to the B-bus, as a game's would.
;
; Script (bank 0, label Script): one opcode byte, then its operands, until opcode 0.
;   $00                         end: NMI on, then wait for ever
;   $01 rr vv                   write vv to $21rr (a B-bus register)
;   $02 ll hh vv                write vv to $hhll in bank 0 (the CPU registers $42xx / $43xx: HDMA set-up, NMITIMEN...)
;   $03 mm bb sl sh sb nl nh    DMA channel 0: mode mm (DMAP) to $21bb, from sb:shsl, nhnl bytes (0 = 65,536)
;   $04                         the unlock: $57, $4D to WMUNLOCK, then WMID must read $57 (else a red screen). Built with -D NOUNLOCK
;                               (the negative control) it is skipped, so every WideMess write that follows must change nothing.
; The NMI handler copies WMOBJSTAT ($21A5) to $7E:0010 and STAT77 ($213E) to $7E:0011 every frame, and counts frames in $7E:0012.
;
; layout_head.inc and layout_data.inc are written by gen_picture.py (the ROM's size, its header name, and its data banks); build.ps1 assembles this file once per ROM.

.INCLUDE "layout_head.inc"

.SNESNATIVEVECTOR
  COP EmptyHandler
  BRK EmptyHandler
  ABORT EmptyHandler
  NMI VBlank
  IRQ EmptyHandler
.ENDNATIVEVECTOR

.SNESEMUVECTOR
  COP EmptyHandler
  ABORT EmptyHandler
  NMI EmptyHandler
  RESET Start
  IRQBRK EmptyHandler
.ENDEMUVECTOR

.EMPTYFILL $00
.COMPUTESNESCHECKSUM

.DEFINE objstat $10
.DEFINE stat77  $11
.DEFINE frame   $12

.BANK 0 SLOT 0
.ORG 0
.SECTION "Player" FORCE

Start:
  sei
  clc
  xce                   ; native mode
  rep #$38
.ACCU 16
.INDEX 16
  ldx #$1FFF
  txs
  lda #$0000
  tcd                   ; direct page $0000
  sep #$20
.ACCU 8
  phk
  plb                   ; data bank = 0
  lda #$80
  sta $2100             ; forced blank
  stz $4200             ; no NMI yet
  stz $420C             ; no HDMA yet
  ldx #$00FF
-
  stz $00,x
  dex
  bpl -
  ldy #$0000            ; Y = the script position

Next:
  lda Script.w,y
  iny
  cmp #$01
  beq OpBBus
  cmp #$02
  beq OpAbs
  cmp #$03
  beq OpDma
  cmp #$04
  bne +
  jmp OpUnlock
+
  cmp #$00
  bne Broken
  jmp Done
Broken:                 ; an opcode the player does not know: a blue screen says so
  stz $2121
  stz $2122
  lda #$7C
  sta $2122
  lda #$0F
  sta $2100
-
  bra -

OpBBus:
  rep #$20
.ACCU 16
  lda Script.w,y        ; low byte: the register, high byte: the value
  iny
  iny
  sep #$20
.ACCU 8
  xba
  pha                   ; the value
  xba
  rep #$20
.ACCU 16
  and #$00FF
  tax
  sep #$20
.ACCU 8
  pla
  sta $2100.w,x
  jmp Next

OpAbs:
  rep #$20
.ACCU 16
  lda Script.w,y        ; the address
  tax
  iny
  iny
  sep #$20
.ACCU 8
  lda Script.w,y
  iny
  sta $0000.w,x
  jmp Next

OpDma:
  lda Script.w,y
  sta $4300             ; DMAP
  iny
  lda Script.w,y
  sta $4301             ; BBAD
  iny
  rep #$20
.ACCU 16
  lda Script.w,y
  sta $4302             ; A1T
  iny
  iny
  sep #$20
.ACCU 8
  lda Script.w,y
  sta $4304             ; A1B
  iny
  rep #$20
.ACCU 16
  lda Script.w,y
  sta $4305             ; DAS
  iny
  iny
  sep #$20
.ACCU 8
  lda #$01
  sta $420B             ; go (channel 0)
  jmp Next

OpUnlock:
.IFNDEF NOUNLOCK
  lda #$57              ; 'W'
  sta $4220
  lda #$4D              ; 'M'
  sta $4220
  lda $4221
  cmp #$57
  beq +
  jmp NotWideMess
+
.ENDIF
  jmp Next

Done:
  lda #$80
  sta $4200             ; NMI on
Forever:
  wai
  bra Forever

NotWideMess:            ; a SNES: a plain red screen says so
  lda #$80
  sta $2100
  stz $420C
  stz $2121
  lda #$1F
  sta $2122
  stz $2122
  stz $212C             ; nothing on the main screen: the backdrop (colour 0) fills it
  stz $2130
  stz $2131
  stz $2105
  lda #$0F
  sta $2100
-
  wai
  bra -

VBlank:
  rep #$30
.ACCU 16
.INDEX 16
  pha
  phx
  phy
  phb
  sep #$20
.ACCU 8
  phk
  plb
  lda $4210             ; acknowledge the NMI
  lda $21A5
  sta objstat
  lda $213E
  sta stat77
  inc frame
  rep #$30
.ACCU 16
  plb
  ply
  plx
  pla
  rti

.ACCU 8
.INDEX 16
EmptyHandler:
  rti

Script:
  .INCBIN "script.bin"

.ENDS

.INCLUDE "layout_data.inc"
