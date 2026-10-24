; WideMess test ROM 3: the WideMess map (docs/projects/widemess-spec.md section 2, milestone 4). A 4 MB game on map 2 with 256 KB of battery RAM.
;
; This file is ROM bank 0 (64 KB: the code, the vectors, bank 0's markers). gen_bigmap.py appends banks 1-63 (each with its markers) to make the
; 4 MB image, and build.ps1 packs it as a .wms with map 2. It checks, writing every result to work RAM at $7E:0100 for Workshop --wms-test:
;   1. before the unlock: the work RAM at $70-$7D is there (the map is the cartridge's wiring), and the WRAM port has the SNES's 17 bits;
;   2. after the unlock: the port takes 20 bits ($2183 bits 0-3: work-RAM address $2:1000 is $70:1000);
;   3. a pattern over all of $70-$7F (offsets $2000-$FF00, every $100) written and read back with long addressing;
;   4. the port across a bank boundary (256 bytes from $71:FF80 into $72:0000), the port's wrap ($FFFFF then $00000: $7D:FFFF then $7E:0000),
;      and a byte written on the A bus read back through the port;
;   5. $00:0000-1FFF mirrors $7E:0000-1FFF, not $70;
;   6. DMA reads and writes the new work RAM ($75:0000 -> colour RAM, colour RAM -> $76:0000, 512 bytes each way);
;   7. the battery RAM: 32 distinct banks of 8 KB at $20-$3F:6000-7FFF, the $A0-$BF mirror, and a boot counter that survives through the save file;
;   8. the 64 ROM banks: each bank's markers at $C0-$FF:0000 and :C000, through the $00-$3F:8000-FFFF mirror, and the $40-$6F mirror.
; Then it turns on fast mode and counts loop iterations per frame into $7B:0000 (+2 x frame), and writes each frame's number through the 20-bit port
; at work-RAM address $E1000 + frame ($7C:1000 + frame): the save-state check needs both.
;
; Results ($7E:0100): +$01 WMID  +$02 RAM found at $70/$77/$7D before the unlock (of 3)  +$03 port before the unlock (1 = landed in $7E, 2 = in $70)
;   +$04 port after the unlock (the same)  +$06 pattern mismatches (16 bits)  +$08 port-crossing mismatches (16 bits; $FFFF = skipped: no 20-bit port)
;   +$0A port wrap (bit 0 $7D:FFFF, bit 1 $7E:0000)  +$0B A bus -> port (1 = read back)  +$0C mirror (bit 0 $00:1500 is $7E:1500, bit 1 $70:1500 untouched)
;   +$0E DMA mismatches (16 bits)  +$10 battery RAM bank mismatches  +$11 boot count  +$12 1 = $A5:6123 is $25:6123
;   $7E:0200 + n: ROM bank n's marker at $C0+n:0000 (1 = right); $0240 + n: at $C0+n:C000; $0280 + n: at n:C000; $02C0 + n (n < 48): at $40+n:0000.
;   $7E:01FF = $A5 when done.

.MEMORYMAP
  SLOTSIZE $10000
  DEFAULTSLOT 0
  SLOT 0 $0000
.ENDME
.ROMBANKSIZE $10000
.ROMBANKS 1

.HIROM

.SNESHEADER
  ID "SNES"
  NAME "WIDEMESS BIG MAP     "
  SLOWROM
  HIROM
  CARTRIDGETYPE $02
  ROMSIZE $0C
  SRAMSIZE $07          ; WLA-DX stops at 128 KB; gen_bigmap.py sets $08 (256 KB, as the .wms header says)
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

; ---- work RAM (direct page at $0000 = $7E:0000; $00-$0F are scratch: the port's wrap test writes $7E:0000)
.DEFINE nmiflag  $10     ; 16 bits
.DEFINE counter  $12     ; 16 bits
.DEFINE errs     $14     ; 16 bits
.DEFINE frame    $16     ; 16 bits
.DEFINE ptr      $18     ; 24-bit pointer
.DEFINE bankn    $1C     ; 16 bits (high byte 0): the ROM bank being checked
.DEFINE tmp      $1E
.DEFINE markbuf  $20     ; 4 bytes: the marker expected

.DEFINE RESULTS  $0100
.DEFINE DONE     $01FF
.DEFINE HIA      $0200
.DEFINE HIC      $0240
.DEFINE LOC      $0280
.DEFINE MIDA     $02C0

.DEFINE WMUNLOCK $4220
.DEFINE WMID     $4221
.DEFINE WMSPEED  $4225

.BANK 0 SLOT 0

; ---- bank 0's markers (gen_bigmap.py writes the other banks' the same way)
.ORG $0000
.SECTION "MarkerA" FORCE
  .DB $00, $57, $4D, $FF          ; n, 'W', 'M', n ^ $FF
.ENDS
.ORG $C000
.SECTION "MarkerC" FORCE
  .DB $A5, $48, $49, $00          ; n ^ $A5, 'H', 'I', n
.ENDS

.ORG $8000
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
  ldx #$02FF
-
  stz $00,x
  dex
  bpl -

  ; ---- 1. before the unlock
  stz tmp
  lda #$70
  ldx #$2000
  jsr ProbeAt
  lda #$77
  ldx #$8000
  jsr ProbeAt
  lda #$7D
  ldx #$FFFF
  jsr ProbeAt
  lda tmp
  sta RESULTS+$02
  ldx #$1000
  jsr PortProbe          ; work-RAM address $2:1000
  sta RESULTS+$03

  lda #$57
  sta WMUNLOCK
  lda #$4D
  sta WMUNLOCK
  lda WMID
  sta RESULTS+$01

  ; ---- 2. after the unlock
  ldx #$1100
  jsr PortProbe
  sta RESULTS+$04

  ; ---- 3. a pattern over $70-$7F: [b:hh00] = b ^ hh ^ $3C for hh = $20-$FF
  stz ptr
  lda #$70
  sta ptr+2
--
  lda #$20
  sta ptr+1
-
  lda ptr+2
  eor ptr+1
  eor #$3C
  sta [ptr]
  inc ptr+1
  bne -
  inc ptr+2
  lda ptr+2
  cmp #$80
  bne --
  ldx #$0000
  stx errs
  lda #$70
  sta ptr+2
--
  lda #$20
  sta ptr+1
-
  lda ptr+2
  eor ptr+1
  eor #$3C
  cmp [ptr]
  beq +
  ldx errs
  inx
  stx errs
+
  inc ptr+1
  bne -
  inc ptr+2
  lda ptr+2
  cmp #$80
  bne --
  ldx errs
  stx RESULTS+$06

  ; ---- 4. the port: across a bank boundary, its wrap, and the A bus read back through it (only with the 20-bit port: with 17 bits the
  ;      crossing would run through $7E:0000, this program's own variables)
  ldx #$FFFF
  stx RESULTS+$08
  lda RESULTS+$04
  cmp #$02
  beq +
  jmp PortDone
+
  lda #$80              ; work-RAM address $3FF80 = $71:FF80
  sta $2181
  lda #$FF
  sta $2182
  lda #$03
  sta $2183
  ldx #$0000
-
  txa
  eor #$77
  sta $2180
  inx
  cpx #$0100
  bne -
  ldx #$0000
  stx errs
  ldx #$0000
-
  txa
  eor #$77
  cmp.l $71FF80,x       ; $71:FF80 + x crosses into $72:0000 for x >= $80 (long indexed addressing carries into the bank)
  beq +
  ldy errs
  iny
  sty errs
+
  inx
  cpx #$0100
  bne -
  ldx errs
  stx RESULTS+$08
  ; the wrap: $FFFFF ($7D:FFFF), then $00000 ($7E:0000)
  lda #$00
  sta.l $7DFFFF
  sta.l $7E0000
  lda #$FF
  sta $2181
  sta $2182
  lda #$0F
  sta $2183
  lda #$E1
  sta $2180
  lda #$E2
  sta $2180
  stz tmp
  lda.l $7DFFFF
  cmp #$E1
  bne +
  lda #$01
  tsb tmp
+
  lda.l $7E0000
  cmp #$E2
  bne +
  lda #$02
  tsb tmp
+
  lda tmp
  sta RESULTS+$0A
  ; the A bus, read through the port: $7C:1234 is work-RAM address $E1234
  lda #$E7
  sta.l $7C1234
  lda #$34
  sta $2181
  lda #$12
  sta $2182
  lda #$0E
  sta $2183
  lda $2180
  cmp #$E7
  bne PortDone
  lda #$01
  sta RESULTS+$0B
PortDone:

  ; ---- 5. $00:0000-1FFF mirrors $7E:0000-1FFF, not $70
  stz tmp
  lda #$00
  sta.l $701500
  lda #$B4
  sta.l $001500
  lda.l $7E1500
  cmp #$B4
  bne +
  lda #$01
  tsb tmp
+
  lda.l $701500
  bne +
  lda #$02
  tsb tmp
+
  lda tmp
  sta RESULTS+$0C

  ; ---- 6. DMA through the new work RAM: $75:0000 -> colour RAM -> $76:0000 (512 bytes of (i ^ $C5) & $7F: colour RAM keeps 15 bits a colour, and
  ;      bit 7 of a high byte reads back as the PPU's open bus, i.e. bit 7 of the low byte read before it, 0 here)
  ldx #$0000
-
  txa
  eor #$C5
  and #$7F
  sta.l $750000,x
  lda #$00
  sta.l $760000,x
  inx
  cpx #$0200
  bne -
  stz $2121             ; colour 0
  stz $4300             ; A -> B, one register
  lda #$22
  sta $4301             ; $2122
  ldx #$0000
  stx $4302
  lda #$75
  sta $4304             ; from $75:0000
  ldx #$0200
  stx $4305
  lda #$01
  sta $420B
  stz $2121
  lda #$80
  sta $4300             ; B -> A
  lda #$3B
  sta $4301             ; from $213B
  ldx #$0000
  stx $4302
  lda #$76
  sta $4304             ; to $76:0000
  ldx #$0200
  stx $4305
  lda #$01
  sta $420B
  ldx #$0000
  stx errs
-
  lda.l $750000,x
  cmp.l $760000,x
  beq +
  ldy errs
  iny
  sty errs
+
  inx
  cpx #$0200
  bne -
  ldx errs
  stx RESULTS+$0E

  ; ---- 7. the battery RAM
  stz ptr
  lda #$F0
  sta ptr
  lda #$7F
  sta ptr+1
  lda #$20
  sta ptr+2             ; $20:7FF0
-
  lda ptr+2
  eor #$96
  sta [ptr]
  inc ptr+2
  lda ptr+2
  cmp #$40
  bne -
  stz tmp
  lda #$20
  sta ptr+2
-
  lda ptr+2
  eor #$96
  cmp [ptr]
  beq +
  inc tmp
+
  inc ptr+2
  lda ptr+2
  cmp #$40
  bne -
  lda tmp
  sta RESULTS+$10
  lda #$3B
  sta.l $A56123
  lda.l $256123
  cmp #$3B
  bne +
  lda #$01
  sta RESULTS+$12
+
  ; the boot counter: 'WMSV' at $20:6000, the count at $20:6004
  lda.l $206000
  cmp #$57
  bne Fresh
  lda.l $206001
  cmp #$4D
  bne Fresh
  lda.l $206002
  cmp #$53
  bne Fresh
  lda.l $206003
  cmp #$56
  bne Fresh
  lda.l $206004
  inc a
  sta.l $206004
  bra Counted
Fresh:
  lda #$57
  sta.l $206000
  lda #$4D
  sta.l $206001
  lda #$53
  sta.l $206002
  lda #$56
  sta.l $206003
  lda #$01
  sta.l $206004
Counted:
  lda.l $206004
  sta RESULTS+$11

  ; ---- 8. the 64 ROM banks
  ldx #$0000
  stx bankn
BankLoop:
  ; marker A: n, 'W', 'M', n ^ $FF
  lda bankn
  sta markbuf
  lda #$57
  sta markbuf+1
  lda #$4D
  sta markbuf+2
  lda bankn
  eor #$FF
  sta markbuf+3
  ldx #$0000
  stx ptr
  lda bankn
  clc
  adc #$C0
  sta ptr+2
  jsr CheckMarker        ; $C0+n:0000
  bne +
  ldx bankn
  lda #$01
  sta HIA,x
+
  lda bankn
  cmp #$30
  bcs +
  adc #$40
  sta ptr+2
  jsr CheckMarker        ; $40+n:0000 (n < 48)
  bne +
  ldx bankn
  lda #$01
  sta MIDA,x
+
  ; marker C: n ^ $A5, 'H', 'I', n
  lda bankn
  eor #$A5
  sta markbuf
  lda #$48
  sta markbuf+1
  lda #$49
  sta markbuf+2
  lda bankn
  sta markbuf+3
  ldx #$C000
  stx ptr
  lda bankn
  clc
  adc #$C0
  sta ptr+2
  jsr CheckMarker        ; $C0+n:C000
  bne +
  ldx bankn
  lda #$01
  sta HIC,x
+
  lda bankn
  sta ptr+2
  jsr CheckMarker        ; n:C000, the $00-$3F:8000-FFFF mirror
  bne +
  ldx bankn
  lda #$01
  sta LOC,x
+
  inc bankn
  lda bankn
  cmp #64
  beq +
  jmp BankLoop
+

  lda #$A5
  sta DONE

  ; ---- then: fast mode, and each frame's loop count into $7B:0000 + 2 x frame, the frame's number through the 20-bit port
  lda #$01
  sta WMSPEED
  lda #$80
  sta $4200
  rep #$20
.ACCU 16
Idle:
  inc counter
  bra Idle

.ACCU 8
.INDEX 16
; Is there RAM at A:X? tmp + 1 when a byte written there reads back.
ProbeAt:
  stx ptr
  sta ptr+2
  lda [ptr]
  eor #$FF
  sta [ptr]
  sta errs
  lda [ptr]
  cmp errs
  bne +
  inc tmp
+
  rts

; Writes $5A through the WRAM port at work-RAM address $2:X (with $7E:X and $70:X cleared first). Returns A = 1 when it landed in $7E (the SNES's
; 17 bits: $2183 bit 0 only), 2 when it landed in $70 (20 bits: work-RAM address $20000 is $70:0000), 0 when neither.
PortProbe:
  lda #$00
  sta.l $7E0000,x
  sta.l $700000,x
  stx $2181             ; $2181 = low, $2182 = high (a 16-bit store)
  lda #$02
  sta $2183
  lda #$5A
  sta $2180
  lda.l $700000,x
  cmp #$5A
  beq +
  lda.l $7E0000,x
  cmp #$5A
  beq ++
  lda #$00
  rts
+
  lda #$02
  rts
++
  lda #$01
  rts

; Z set when the 4 bytes at [ptr] are markbuf.
CheckMarker:
  ldy #$0000
-
  lda [ptr],y
  cmp markbuf.w,y
  bne +
  iny
  cpy #$0004
  bne -
+
  rts

VBlank:
  rep #$30
.ACCU 16
  pha
  phx
  sep #$20
.ACCU 8
  lda $4210
  rep #$20
.ACCU 16
  lda frame
  and #$00FF
  asl a
  tax
  lda counter
  sta.l $7B0000,x
  stz counter
  sep #$20
.ACCU 8
  lda frame
  sta $2181
  lda #$10
  sta $2182
  lda #$0E
  sta $2183             ; work-RAM address $E1000 + frame = $7C:1000 + frame (17 bits: $7E:1000 + frame); below the pattern's $2000
  lda frame
  sta $2180
  rep #$20
.ACCU 16
  inc frame
  plx
  pla
  rti

.ACCU 8
EmptyHandler:
  rti

.ENDS
