; WideMess test ROM 1: eight layers of 256 colours at 400 x 224 (docs/projects/widemess-spec.md, milestone 1).
;
; On a WideMess: unlocks the extensions, checks the ID, loads 1,024 colours and 33 tiles (8 bits per pixel) into the video RAM above the
; SNES's 64 KB, builds eight 64 x 32 tile maps (each layer covers a quarter of the screen in 2 x 2-tile blocks, offset per layer; the odd
; layers put their even block rows in front with the priority bit), turns on all eight layers and the 400-wide output, then scrolls each
; layer at its own speed (layer n at (n+1)/2 pixels per frame) from the NMI.
; On a SNES (or any emulator without WideMess): the ID check fails and the screen is plain red.
; Built with -D NOUNLOCK it skips the unlock and writes the WideMess registers anyway: the negative control. A WideMess must ignore every
; one of those writes (they are locked), so the screen stays as a SNES draws it: black.
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
  NAME "WIDEMESS 8 LAYERS    "
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

; ---- work RAM (direct page, bank $00 = $7E)
.DEFINE frame   $00      ; 16 bits
.DEFINE scroll  $10      ; 8 x 16 bits: each layer's horizontal scroll in half pixels
.DEFINE tmpx    $30
.DEFINE tmpy    $31
.DEFINE layer   $32
.DEFINE tmp     $33

; ---- WideMess registers
.DEFINE WMUNLOCK  $4220
.DEFINE WMID      $4221
.DEFINE WMCTRL    $21C0
.DEFINE WMTM      $21C1
.DEFINE WMVADDL   $21C2
.DEFINE WMVADDM   $21C3
.DEFINE WMVADDH   $21C4
.DEFINE WMVDATA   $21C5
.DEFINE WMCGADDL  $21C6
.DEFINE WMCGADDH  $21C7
.DEFINE WMCGDATA  $21C8
.DEFINE WMBGMAP   $21D0  ; + layer
.DEFINE WMBGCFG   $21D8  ; + layer
.DEFINE WMBGHOFS  $21E0  ; + 2 x layer (written twice: low, high)
.DEFINE WMBGVOFS  $21E1  ; + 2 x layer

.DEFINE PALETTE_SIZE 2048
.DEFINE TILES_SIZE   2112

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
  ldx #$00FF
-
  stz $00,x             ; clear the variables
  dex
  bpl -

.IFNDEF NOUNLOCK
  lda #$57              ; 'W'
  sta WMUNLOCK
  lda #$4D              ; 'M'
  sta WMUNLOCK
  lda WMID
  cmp #$57
  beq IsWideMess
  jmp NotWideMess
.ENDIF

IsWideMess:
  ; ---- 1,024 colours
  stz WMCGADDL
  stz WMCGADDH
  ldx #$0000
-
  lda Palette.w,x
  sta WMCGDATA
  inx
  cpx #PALETTE_SIZE
  bne -

  ; ---- tiles at video RAM $010000 (above the SNES's 64 KB)
  stz WMVADDL
  stz WMVADDM
  lda #$01
  sta WMVADDH
  ldx #$0000
-
  lda Tiles.w,x
  sta WMVDATA
  inx
  cpx #TILES_SIZE
  bne -

  ; ---- eight 64 x 32 maps at $020000 + layer x $1000
  stz layer
LayerLoop:
  stz WMVADDL
  lda layer
  asl a
  asl a
  asl a
  asl a
  sta WMVADDM
  lda #$02
  sta WMVADDH
  stz tmpy
RowLoop:
  stz tmpx
ColLoop:
  ; a 2 x 2-tile block is this layer's when (x/2 + y/2 + layer) mod 4 = 0
  lda tmpx
  lsr a
  sta tmp
  lda tmpy
  lsr a
  clc
  adc tmp
  adc layer
  and #$03
  bne EmptyCell
  ; tile = 1 + 4 x layer + ((x / 2 + y) mod 4): every block row and column brings the layer's four tiles, so all 255 of its colours show
  lda tmpx
  lsr a
  clc
  adc tmpy
  and #$03
  sta tmp
  lda layer
  asl a
  asl a
  clc
  adc tmp
  inc a
  sta WMVDATA
  ; high byte: the priority bit (bit 12 = $10 here) on odd layers, block rows 0 and 1 of every 4
  lda layer
  and #$01
  beq NoPriority
  lda tmpy
  and #$02
  bne NoPriority
  lda #$10
  bra WriteHigh
NoPriority:
  lda #$00
WriteHigh:
  sta WMVDATA
  bra NextCell
EmptyCell:
  stz WMVDATA
  stz WMVDATA
NextCell:
  inc tmpx
  lda tmpx
  cmp #64
  bne ColLoop
  inc tmpy
  lda tmpy
  cmp #32
  bne RowLoop
  inc layer
  lda layer
  cmp #8
  bne LayerLoop

  ; ---- each layer: its map, tiles at 64 KB unit 1, colour bank = layer mod 4, 64 x 32; vertical scroll = layer x 16
  ldx #$0000
  ldy #$0000
CfgLoop:
  txa
  asl a
  clc
  adc #64               ; $020000 / 2 KB = 64, then + 2 per layer
  sta WMBGMAP,x
  txa
  and #$03
  asl a
  asl a
  ora #$10
  sta WMBGCFG,x
  txa
  asl a
  asl a
  asl a
  asl a
  sta WMBGVOFS,y        ; low byte
  lda #$00
  sta WMBGVOFS,y        ; high byte
  sta WMBGHOFS,y
  sta WMBGHOFS,y
  iny
  iny
  inx
  cpx #8
  bne CfgLoop

  lda #$FF
  sta WMTM              ; all eight layers on the main screen
  lda #$03
  sta WMCTRL            ; 400 wide + the WideMess mode
  lda #$0F
  sta $2100             ; screen on, full brightness
  lda #$80
  sta $4200             ; NMI on
Forever:
  wai
  bra Forever

NotWideMess:
  ; a SNES: a plain red screen says so
  stz $2121
  lda #$1F
  sta $2122
  stz $2122
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
  sep #$20
.ACCU 8
  lda $4210             ; acknowledge the NMI
  rep #$20
.ACCU 16
  inc frame
  ; scroll[n] += n + 1 (half pixels)
  ldx #$0000
  ldy #$0001
-
  tya
  clc
  adc scroll,x
  sta scroll,x
  inx
  inx
  iny
  cpy #$0009
  bne -
  ; WMBGnHOFS = scroll[n] / 2
  ldx #$0000
-
  lda scroll,x
  lsr a
  and #$03FF
  sep #$20
.ACCU 8
  sta WMBGHOFS,x
  xba
  sta WMBGHOFS,x
  rep #$20
.ACCU 16
  inx
  inx
  cpx #$0010
  bne -
  ply
  plx
  pla
  rti

.ACCU 8
.INDEX 16
EmptyHandler:
  rti

Palette:
  .INCBIN "palette.bin"
Tiles:
  .INCBIN "tiles.bin"

.ENDS
