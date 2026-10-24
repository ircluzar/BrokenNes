; WideMess test ROM 2: the fast CPU and fast DMA (docs/projects/widemess-spec.md section 2, milestone 4). A 32 KB LoROM game.
;
; It unlocks WideMess, then measures, writing every result to work RAM at $7E:0100 for BrokenNes Workshop --wms-test to read:
;   - loop A (ROM, work RAM and internal cycles only) and loop B (the same plus a 16-bit read of the I/O register $4214): iterations in one frame
;     (NMI to NMI) at SNES speed and in fast mode (WMSPEED = 1). The spec predicts the ratio from each loop's mix of accesses: 108 / 28 master clocks
;     for loop A, 144 / 46 for loop B (the I/O read keeps its 6 clocks a byte in fast mode);
;   - the bytes general DMA moves in one vblank, in 512-byte transfers from ROM to the WRAM port: normal, fast DMA (WMDMA = 1), and fast CPU alone
;     (WMSPEED = 1, WMDMA = 0: DMA keeps its 8 clocks a byte);
;   - the LoROM negative probes: no RAM at $70-$7D (the WideMess map's work RAM is not there on a LoROM cartridge), and the WRAM port keeps the
;     SNES's 17 bits even unlocked ($2183 = $02 lands in $7E).
; Built with -D NOUNLOCK it skips the unlock: the negative control. Its WMSPEED and WMDMA writes go nowhere, so both halves of every measurement
; are equal, as on a SNES.
;
; Results ($7E:0100): +$00 WMID read  +$01 WMSPEED read back after writing 1  +$02/$04 loop A SNES/fast  +$06/$08 loop B SNES/fast
;   +$0A/$0C/$0E DMA bytes per vblank: normal / fast DMA / fast CPU only  +$10 RAM found at $70-$7D (of 5 probes)  +$11 1 = the port wrapped to $7E
;   $7E:01FF = $A5 when done.
;
; Assemble: build.ps1 (WLA-DX 65816 from the PVSnesLib toolchain, then BrokenNes Workshop --wms-pack).

.MEMORYMAP
  SLOTSIZE $8000
  DEFAULTSLOT 0
  SLOT 0 $8000
.ENDME
.ROMBANKSIZE $8000
.ROMBANKS 1

.SNESHEADER
  ID "SNES"
  NAME "WIDEMESS SPEED       "
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

; ---- work RAM (direct page at $0000 = $7E:0000)
.DEFINE nmiflag  $10     ; 16 bits, + 1 per NMI
.DEFINE counter  $12     ; 16 bits
.DEFINE dmabytes $14     ; 16 bits
.DEFINE tmp      $16
.DEFINE ptr      $18     ; 24-bit pointer
.DEFINE hits     $1C

.DEFINE RESULTS  $0100
.DEFINE DONE     $01FF

; ---- WideMess registers
.DEFINE WMUNLOCK $4220
.DEFINE WMID     $4221
.DEFINE WMSPEED  $4225
.DEFINE WMDMA    $4226

.BANK 0 SLOT 0
.ORG 0
.SECTION "Main" FORCE

Start:
  sei
  clc
  xce                   ; native mode
  rep #$38              ; 16-bit A, X, Y; binary mode
.ACCU 16
.INDEX 16
  ldx #$1FFF
  txs
  lda #$0000
  tcd                   ; direct page at $0000
  sep #$20
.ACCU 8
  lda #$80
  sta $2100             ; forced blank
  stz $4200             ; no NMI yet
  ldx #$01FF
-
  stz $00,x             ; clear the variables and the results
  dex
  bpl -

.IFNDEF NOUNLOCK
  lda #$57              ; 'W'
  sta WMUNLOCK
  lda #$4D              ; 'M'
  sta WMUNLOCK
.ENDIF
  lda WMID
  sta RESULTS+$00
  lda #$01
  sta WMSPEED
  lda WMSPEED
  sta RESULTS+$01
  stz WMSPEED
  stz WMDMA

  lda #$80
  sta $4200             ; NMI on: the measurements count from one NMI to the next

  ; ---- loop A at SNES speed, then fast
  jsr MeasureA
.ACCU 16
  sta RESULTS+$02
  sep #$20
.ACCU 8
  lda #$01
  sta WMSPEED
  jsr MeasureA
.ACCU 16
  sta RESULTS+$04
  sep #$20
.ACCU 8
  stz WMSPEED

  ; ---- loop B at SNES speed, then fast
  jsr MeasureB
.ACCU 16
  sta RESULTS+$06
  sep #$20
.ACCU 8
  lda #$01
  sta WMSPEED
  jsr MeasureB
.ACCU 16
  sta RESULTS+$08
  sep #$20
.ACCU 8
  stz WMSPEED

  ; ---- DMA bytes per vblank: normal, fast DMA, fast CPU alone
  jsr MeasureDma
  ldx dmabytes
  stx RESULTS+$0A
  lda #$01
  sta WMDMA
  jsr MeasureDma
  ldx dmabytes
  stx RESULTS+$0C
  stz WMDMA
  lda #$01
  sta WMSPEED
  jsr MeasureDma
  ldx dmabytes
  stx RESULTS+$0E
  stz WMSPEED

  ; ---- a LoROM cartridge has no work RAM at $70-$7D (its battery-RAM area, empty here, and ROM)
  stz hits
  lda #$70
  ldx #$0000
  jsr ProbeAt
  lda #$70
  ldx #$8000
  jsr ProbeAt
  lda #$75
  ldx #$1234
  jsr ProbeAt
  lda #$7D
  ldx #$7FFF
  jsr ProbeAt
  lda #$7D
  ldx #$FFFF
  jsr ProbeAt
  lda hits
  sta RESULTS+$10

  ; ---- the WRAM port keeps 17 bits on a LoROM cartridge, unlocked or not: $2183 = $02 is bank $7E
  lda #$00
  sta.l $7E3456
  lda #$56
  sta $2181
  lda #$34
  sta $2182
  lda #$02
  sta $2183
  lda #$C3
  sta $2180
  lda.l $7E3456
  cmp #$C3
  bne +
  lda #$01
  sta RESULTS+$11
+
  lda #$A5
  sta DONE
Forever:
  wai
  bra Forever

; Iterations of loop A in one frame. Returns with A 16 bits = the count.
.ACCU 8
.INDEX 16
MeasureA:
  rep #$20
.ACCU 16
  stz nmiflag
-
  lda nmiflag
  beq -                 ; wait for an NMI
  stz nmiflag
  stz counter
LoopA:
  inc counter           ; 2 ROM, 2 work-RAM reads, 1 internal, 2 work-RAM writes
  lda nmiflag           ; 2 ROM, 2 work RAM
  beq LoopA             ; 2 ROM, 1 internal (taken)
  lda counter
  rts

; Iterations of loop B (loop A plus a 16-bit read of $4214) in one frame. Returns with A 16 bits = the count.
.ACCU 8
MeasureB:
  rep #$20
.ACCU 16
  stz nmiflag
-
  lda nmiflag
  beq -
  stz nmiflag
  stz counter
LoopB:
  inc counter
  lda $4214             ; 3 ROM, 2 I/O (6 clocks each, fast or not)
  lda nmiflag
  beq LoopB
  lda counter
  rts

; Bytes of general DMA (ROM -> WRAM port, 512 at a time) started in one vblank. A 8 bits, X 16 bits.
.ACCU 8
MeasureDma:
  stz $4200             ; no NMI during the measurement
  stz $4300             ; A -> B, one register, the address increments
  lda #$80
  sta $4301             ; B-bus $2180, the WRAM port
  stz $4304             ; source bank $00
  stz $2181
  lda #$20
  sta $2182
  stz $2183             ; the port at $7E:2000
-
  lda $4212
  bmi -                 ; out of the current vblank
-
  lda $4212
  bpl -                 ; the next vblank starts
  ldx #$0000
  stx dmabytes
DmaLoop:
  ldx #$8000
  stx $4302             ; source $00:8000
  ldx #512
  stx $4305             ; 512 bytes
  lda #$01
  sta $420B             ; go (the CPU waits for the transfer)
  rep #$20
.ACCU 16
  lda dmabytes
  clc
  adc #512
  sta dmabytes
  sep #$20
.ACCU 8
  lda $4212
  bmi DmaLoop           ; still in vblank: one more
  lda #$80
  sta $4200
  rts

; Is there RAM at A:X? hits + 1 when a byte written there reads back. A 8 bits, X 16 bits.
ProbeAt:
  stx ptr
  sta ptr+2
  lda [ptr]
  eor #$FF
  sta [ptr]
  sta tmp
  lda [ptr]
  cmp tmp
  bne +
  inc hits
+
  rts

VBlank:
  rep #$20
.ACCU 16
  pha
  sep #$20
.ACCU 8
  lda $4210             ; acknowledge the NMI
  rep #$20
.ACCU 16
  inc nmiflag
  pla
  rti

.ACCU 8
EmptyHandler:
  rti

.ENDS
