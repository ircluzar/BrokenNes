; WideMess test ROM: the pointer port (docs/projects/widemess-spec.md section 10). A 32 KB LoROM game.
;
; It unlocks WideMess (unless built with -D NOUNLOCK: the negative control) and then, every frame:
;   - in its NMI handler (the start of the frame, vertical blanking) copies WMPTRXL, WMPTRXH, WMPTRY, WMPTRST, WMPTRMOVE ($4228-$422C) to $7E:0100-$0104
;     and bumps the frame count at $7E:0106;
;   - in its main loop, all through the frame, copies the same five registers to $7E:0110-$0114 (the last read of the frame wins): the latch keeps one value
;     for the whole frame, so the two copies agree;
;   - ORs WMPTRST into $7E:0120 (every bit the port ever showed) and counts the frames with LEFT went down ($7E:0122, 16 bits).
; $7E:0108 = WMID, $7E:0109 = WMFEAT, read once after the unlock. $7E:01FF = $A5 once running.
; Assemble: build.ps1.

.MEMORYMAP
  SLOTSIZE $8000
  DEFAULTSLOT 0
  SLOT 0 $8000
.ENDME
.ROMBANKSIZE $8000
.ROMBANKS 1

.SNESHEADER
  ID "SNES"
  NAME "WIDEMESS POINTER     "
  SLOWROM
  LOROM
  CARTRIDGETYPE $00
  ROMSIZE $08
  SRAMSIZE $00
  COUNTRY $01
  LICENSEECODE $00
  VERSION $00
.ENDSNES

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

.DEFINE WMUNLOCK $4220
.DEFINE WMID     $4221
.DEFINE WMFEAT   $4224
.DEFINE WMPTR    $4228

.BANK 0 SLOT 0
.ORG 0
.SECTION "Main" FORCE

Start:
  sei
  clc
  xce
  rep #$38
.ACCU 16
.INDEX 16
  ldx #$1FFF
  txs
  lda #$0000
  tcd
  sep #$20
.ACCU 8
  lda #$80
  sta $2100             ; forced blank
  stz $4200
  ldx #$01FF
-
  stz $00,x
  dex
  bpl -
.IFNDEF NOUNLOCK
  lda #$57
  sta WMUNLOCK
  lda #$4D
  sta WMUNLOCK
  lda #$01
  sta $21C0             ; WMCTRL: wide output, so the picture (black) is 400 wide and X runs 0-399
.ENDIF
  lda WMID
  sta $0108
  lda WMFEAT
  sta $0109
  lda #$A5
  sta $01FF
  lda #$0F
  sta $2100             ; the screen on (nothing drawn: the backdrop)
  lda #$80
  sta $4200             ; NMI on

Main:
  ldx #$0000
-
  lda WMPTR,x
  sta $0110,x
  inx
  cpx #$0005
  bne -
  bra Main

VBlank:
  rep #$30
.ACCU 16
.INDEX 16
  pha
  phx
  sep #$20
.ACCU 8
  lda $4210
  ldx #$0000
-
  lda WMPTR,x
  sta $0100,x
  inx
  cpx #$0005
  bne -
  lda $0103
  ora $0120
  sta $0120
  lda $0103
  and #$08
  beq +
  rep #$20
.ACCU 16
  inc $0122
  sep #$20
.ACCU 8
+
  rep #$20
.ACCU 16
  inc $0106
  plx
  pla
  rti

EmptyHandler:
  rti

.ENDS
