; The WideMess test cartridge: a conformance cartridge for the WideMess console (docs/projects/widemess-spec.md, spec 1.0).
; What it tests, what each test must show and the results table it keeps in work RAM: WideMess/testcart/README.md.
;
; One 65816 program on the WideMess map (game-file map 2: 512 KB of ROM at $C0-$C7, mirrored in the upper halves of $00-$07 / $80-$87).
; The code is in ROM bank 0's upper half and runs at $00:8000-FFFF; the data banks are $C1-$C7 (tc_data.inc, made by gen_testcart.py).
; It depends on the spec alone: every value it judges is read back from the console or measured with the console's own counters.
;
; Assemble: build.ps1 (WLA-DX 65816 and SPC700 from the PVSnesLib toolchain, then BrokenNes Workshop --wms-pack --map 2).

.MEMORYMAP
  SLOTSIZE $10000
  DEFAULTSLOT 0
  SLOT 0 $0000
.ENDME
.ROMBANKSIZE $10000
.ROMBANKS 8

.HIROM

.SNESHEADER
  ID "SNES"
  NAME "WIDEMESS TEST CART   "
  SLOWROM
  HIROM
  CARTRIDGETYPE $00
  ROMSIZE $09
  SRAMSIZE $00
  COUNTRY $01
  LICENSEECODE $00
  VERSION $00
.ENDSNES

.SNESNATIVEVECTOR
  COP EmptyHandler
  BRK EmptyHandler
  ABORT EmptyHandler
  NMI NmiEntry
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

.INCLUDE "tc_gen.inc"
.INCLUDE "tc_datadefs.inc"
.INCLUDE "tc_defs.inc"

; ---- the cartridge's info block, at a fixed place: ROM bank 0, offset $FF80 ($00:FF80). README.md documents it.
.BANK 0 SLOT 0
.ORG $FF80
.SECTION "CartInfo" FORCE
CartInfo:
  .DB "WMTC"            ; +0 magic
  .DB 1, 0              ; +4 the cartridge's version, 1.0
  .DB 1                 ; +6 the results table's version
CartFeatureMask:
  .DB $FF               ; +7 WMFEAT is ANDed with this byte: a harness patches it to see the cartridge's "not available" screens
  .DB 1, 0              ; +8 the spec it tests, 1.0
.ENDS

.ORG $8000
.SECTION "Main" FORCE

; ================================================================================================ boot

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
  lda #$00
  pha
  plb                   ; data bank $00
  lda #$80
  sta INIDISP           ; forced blank
  stz NMITIMEN
  stz HDMAEN
  stz MEMSEL            ; SlowROM timing everywhere (the speed test sets MEMSEL itself)
  ldx #$1DFF            ; clear $0000-$1DFF (the stack is above)
-
  stz $0000,x
  dex
  bpl -

  ; ---- identity, before and after the unlock (spec section 1). The open-bus reads must come first, with nothing written to $4220 yet.
  lda WMID              ; locked: open bus, $42 (the address's high byte) on a SNES and on a locked WideMess
  sta RES_PREID
  lda WMSPEED           ; locked: open bus too
  sta RES_PRESPEED
  lda #$57              ; a broken sequence: 'W', then another value (it restarts the sequence), then 'M' alone
  sta WMUNLOCK
  stz WMUNLOCK
  lda #$4D
  sta WMUNLOCK
  lda WMID
  sta RES_BROKENID      ; still locked: $42
  lda #$57              ; the unlock: 'W', 'M'
  sta WMUNLOCK
  lda #$4D
  sta WMUNLOCK
  lda WMID
  sta RES_WMID
  lda WMVER
  sta RES_WMVER
  lda WMREV
  sta RES_WMREV
  lda WMFEAT
  sta RES_WMFEAT
  lda.l CartFeatureMask
  sta RES_MASK
  and RES_WMFEAT
  sta RES_FEAT
  ; the reserved registers $4227-$422F read 0 (once unlocked)
  lda #$01
  sta RES_RESERVED
  ldx #$0000
-
  lda $4227,x
  beq +
  stz RES_RESERVED
+
  inx
  cpx #9
  bne -

  ; ---- the results table's head
  lda #'W'
  sta RES_MAGIC
  lda #'M'
  sta RES_MAGIC+1
  lda #'T'
  sta RES_MAGIC+2
  lda #'C'
  sta RES_MAGIC+3
  lda #1
  sta RES_VERSION
  sta RES_CARTVER
  stz RES_CARTVER+1

  lda RES_WMID
  cmp #$57
  beq +
  stz RES_FEAT          ; not a WideMess: no feature at all
  lda #ST_NOTWMS
  sta RES_STATE
  jmp SnesScreen
+
  lda #1
  sta RES_CONSOLE
  lda RES_FEAT
  and #FEAT_PICTURE
  bne +
  lda #ST_NOPICTURE
  sta RES_STATE
  jmp SnesScreen
+
  jsr MarkUnavailable
  ; ---- the pad at power-on (SELECT held: run every self-check unattended)
  rep #$20
.ACCU 16
  lda #VbNone
  sta vbhook
  lda #NmiFull
  sta nmijump
  sep #$20
.ACCU 8
  lda #$81
  sta NMITIMEN          ; NMI and the automatic pad read
  jsr WaitFrame
  jsr WaitFrame
  rep #$20
.ACCU 16
  lda padnow
  sta RES_BOOTPAD
  stz padnew
  sep #$20
.ACCU 8
  lda RES_BOOTPAD+1
  and #>PAD_SELECT
  beq +
  lda #1
  sta runall
  lda #SC_RUNALL
  jmp GoScreen
+
  lda #ST_MENU
  sta RES_STATE
  lda #SC_TITLE
  jmp GoScreen

; ================================================================================================ the main loop and the screens

; GoScreen: A = the screen. Enters it (its Enter routine may run checks over several frames) and runs its Frame routine once a frame.
GoScreen:
.ACCU 8
.INDEX 16
  sta screen
  sta RES_SCREEN
  stz WMSPEED           ; every screen starts at SNES speed, normal DMA, SlowROM, HDMA off, the full NMI
  stz WMDMA
  stz MEMSEL
  stz HDMAEN
  rep #$20
.ACCU 16
  and #$00FF
  asl a
  sta screen2
  lda #VbNone
  sta vbhook            ; no vblank work while the screen sets up
  lda #NmiFull
  sta nmijump
  sep #$20
.ACCU 8
  lda #$81
  sta NMITIMEN
  ldx #$1FFF            ; a fresh stack: GoScreen never returns
  txs
  ldx screen2
  jsr (ScreenEnter,x)
MainLoop:
  jsr WaitFrame
  jsr TakePad
  ldx screen2
  jsr (ScreenFrame,x)
  bra MainLoop

; WaitFrame: until the next NMI has done its vblank work.
WaitFrame:
.ACCU 8
.INDEX 16
  lda #1
  sta vbready
-
  wai
  lda vbready
  bne -
  rts

; TakePad: pad = the buttons pressed since the last call (16 bits, the $4218 layout), padheld = the buttons down now.
TakePad:
  rep #$20
.ACCU 16
  lda padnew
  stz padnew
  sta pad
  lda padnow
  sta padheld
  sep #$20
.ACCU 8
  rts

; BackToMenu: B or START in a test (the caller checked), or the end of a run-all.
BackToMenu:
.ACCU 8
  lda #SC_MENU
  jmp GoScreen

; ================================================================================================ NMI

NmiEntry:
  jmp (nmijump)         ; nmijump: NmiFull, or NmiCount while the speed test counts (5 cycles: 3 ROM, 2 work RAM; the test's model counts them)

; The speed test's handler: speed.asm's, cycle for cycle (spec section 2's model counts it).
NmiCount:
  rep #$20
.ACCU 16
  pha
  sep #$20
.ACCU 8
  lda RDNMI
  rep #$20
.ACCU 16
  inc.b nmiflag
  pla
  rti

NmiFull:
  rep #$30
.ACCU 16
.INDEX 16
  pha
  phx
  phy
  phb
  phd
  lda #$0000
  tcd
  sep #$20
.ACCU 8
  pha                   ; (A's low byte is 0)
  plb
  lda RDNMI
  rep #$20
.ACCU 16
  inc.b nmiflag
  inc RES_FRAME
  sep #$20
.ACCU 8
  lda vbready
  beq @pad              ; the main program is still busy: no video work this time
  lda textdirty
  beq +
  stz textdirty
  jsr DmaText
+
  lda paneldirty
  beq +
  stz paneldirty
  jsr DmaPanel
+
  jsr CallVbHook        ; the screen's own vblank work
  stz vbready
@pad:
-
  lda HVBJOY
  and #$01
  bne -                 ; the automatic pad read is done
  rep #$20
.ACCU 16
  lda JOY1
  sta padnow
  lda padprev
  eor #$FFFF
  and padnow
  ora padnew
  sta padnew
  lda padnow
  sta padprev
  pld
  plb
  ply
  plx
  pla
  rti

.ACCU 8
.INDEX 16
EmptyHandler:
  rti

VbNone:
  rts

CallVbHook:
  jmp (vbhook)

; ================================================================================================ the text layer (BG1) and the panels (BG2)

; The text buffer is BG1's map (64 x 32 entries) in work RAM at $7E:2000; the NMI copies rows 0-27 to video RAM when textdirty is set.
; An entry: tile = style x 128 + code (the font's 8 styles are 128 tiles each), bit 12 the priority.
DmaText:
  lda #$00
  sta WMVADDL
  sta WMVADDM
  lda #(VR_MAPS >> 16)
  sta WMVADDH           ; BG1's map: $60000
  stz DMAP0
  lda #<WMVDATA
  sta BBAD0
  ldx #(TEXTBUF & $FFFF)
  stx A1T0
  lda #$7E
  sta A1B0
  ldx #28 * 128
  stx DAS0
  lda #$01
  sta MDMAEN
  rts

DmaPanel:
  stz WMVADDL
  lda #$10
  sta WMVADDM
  lda #(VR_MAPS >> 16)
  sta WMVADDH           ; BG2's map: $61000
  stz DMAP0
  lda #<WMVDATA
  sta BBAD0
  ldx #(PANELBUF & $FFFF)
  stx A1T0
  lda #$7E
  sta A1B0
  ldx #28 * 128
  stx DAS0
  lda #$01
  sta MDMAEN
  rts

; SetStyle: A = style 0-7.
SetStyle:
.ACCU 8
  and #$07
  sta textstyle
  lsr a
  ora #$10              ; the priority bit
  sta styhi
  lda textstyle
  lsr a
  lda #$00
  ror a                 ; bit 0 of the style -> bit 7 of the tile number
  sta stylo
  rts

; At: X = the buffer offset of (row A, column Y): A row 0-31, Y column 0-63. Keeps A and Y.
.ACCU 8
.INDEX 16
At:
  pha
  phy
  rep #$20
.ACCU 16
  and #$00FF
  xba
  lsr a                 ; row x 128
  sta tmp
  tya
  asl a
  clc
  adc tmp
  tax
  sep #$20
.ACCU 8
  ply
  pla
  rts

; PutChar: A = a glyph code at X; X += 2.
PutChar:
  ora stylo
  sta.l TEXTBUF,x
  lda styhi
  sta.l TEXTBUF+1,x
  inx
  inx
  rts

; Print: the string at [strptr] from X. Bytes: $01-$7F glyphs; $80-$87 the style; $FE a new line (back to the line's start, one row down);
; $F0 aa aa the byte at $00:aaaa in hex; $F1 aa aa the word there in hex; $F2 aa aa the word there in decimal (5 places, spaces in front);
; $F3 aa aa the byte there in decimal (3 places); $F4 aa aa the byte there as a status word (PASS, FAIL, N/A, EYE, ...); $FF the end.
; Returns X after the last glyph and strptr past the end.
Print:
.ACCU 8
.INDEX 16
  stx linestart
@loop:
  lda [strptr]
  jsr StrNext
  cmp #$FF
  beq @done
  cmp #$FE
  beq @nl
  cmp #$F0
  bcs @value
  cmp #$80
  bcs @style
  jsr PutChar
  bra @loop
@style:
  jsr SetStyle
  bra @loop
@nl:
  rep #$20
.ACCU 16
  lda linestart
  clc
  adc #128
  sta linestart
  tax
  sep #$20
.ACCU 8
  bra @loop
@value:
  pha
  rep #$20
.ACCU 16
  lda [strptr]
  sta valaddr
  sep #$20
.ACCU 8
  jsr StrNext
  jsr StrNext
  pla
  cmp #$F0
  bne +
  lda (valaddr)
  jsr PrintHex8
  bra @loop
+
  cmp #$F1
  bne +
  rep #$20
.ACCU 16
  lda (valaddr)
  sep #$20
.ACCU 8
  jsr PrintHex16
  bra @loop
+
  cmp #$F2
  bne +
  rep #$20
.ACCU 16
  lda (valaddr)
  sep #$20
.ACCU 8
  jsr PrintDec16
  bra @loop
+
  cmp #$F3
  bne +
  lda (valaddr)
  jsr PrintDec8
  bra @loop
+
  lda (valaddr)
  jsr PrintStatus
  jmp @loop
@done:
  rts

StrNext:
  pha
  rep #$20
.ACCU 16
  inc strptr
  sep #$20
.ACCU 8
  pla
  rts

; PrintAt: the string whose address (bank 0... any bank) is in the 3 bytes after the JSR, at row A, column Y. Usage:
;   lda #row / ldy #col / jsr PrintAt / .DW Label / .DB :Label
PrintAt:
.ACCU 8
.INDEX 16
  jsr At
  phx
  rep #$20
.ACCU 16
  lda 3,s               ; the return address (the byte before the inline pointer)
  inc a
  sta tmp
  clc
  adc #3
  sta 3,s               ; return past the pointer
  sep #$20
.ACCU 8
  phb
  lda #$00
  pha
  plb
  rep #$20
.ACCU 16
  lda (tmp)             ; (the code is in bank 0: the inline pointer is read from $00:xxxx)
  sta strptr
  sep #$20
.ACCU 8
  ldy #$0002
  lda (tmp),y
  sta strptr+2
  plb
  plx
  jmp Print

; Hex and decimal printing at X with the current style.
PrintHex8:
.ACCU 8
  pha
  lsr a
  lsr a
  lsr a
  lsr a
  jsr HexDigit
  pla
  and #$0F
HexDigit:
  cmp #10
  bcc +
  adc #('A' - 10 - 1)   ; (carry is set)
  jmp PutChar
+
  ora #'0'
  jmp PutChar

PrintHex16:            ; A 16-bit value in A (called with A 8-bit after rep/lda/sep: the whole 16 bits are kept in C)
  xba
  jsr PrintHex8
  xba
  jmp PrintHex8

PrintDec8:
.ACCU 8
  rep #$20
.ACCU 16
  and #$00FF
  sta numval
  sep #$20
.ACCU 8
  lda #3
  bra DecDigits
PrintDec16:
  rep #$20
.ACCU 16
  sta numval
  sep #$20
.ACCU 8
  lda #5
; DecDigits: numval in decimal, A places (3 or 5), spaces in front.
DecDigits:
  sta places
  stz started
  phy
  rep #$20
.ACCU 16
  lda places
  and #$00FF
  dec a
  asl a
  tay                   ; the power of ten to start from
@digit:
  stz digit
-
  lda numval
  cmp Powers.w,y
  bcc +
  sbc Powers.w,y
  sta numval
  inc digit
  bra -
+
  sep #$20
.ACCU 8
  lda digit
  bne @show
  lda started
  bne @show
  cpy #$0000
  beq @show             ; the last place always shows
  lda #' '
  jsr PutChar
  bra @next
@show:
  lda #1
  sta started
  lda digit
  ora #'0'
  jsr PutChar
@next:
  rep #$20
.ACCU 16
  dey
  dey
  bpl @digit
  sep #$20
.ACCU 8
  ply
  rts
Powers:
  .DW 1, 10, 100, 1000, 10000

; PrintStatus: A = a status byte: four glyphs, coloured.
PrintStatus:
.ACCU 8
  phy
  ldy #$0000
-
  cmp StatusCodes.w,y
  beq +
  iny
  cpy #STATUS_KINDS
  bne -
  ldy #$0000            ; unknown: shown as not run
+
  lda textstyle
  pha
  lda StatusStyle.w,y
  jsr SetStyle
  rep #$20
.ACCU 16
  tya
  asl a
  asl a
  tay
  sep #$20
.ACCU 8
  lda #4
  sta places
-
  lda StatusText.w,y
  jsr PutChar
  iny
  dec places
  bne -
  pla
  jsr SetStyle
  ply
  rts
.DEFINE STATUS_KINDS 7
StatusCodes:
  .DB 0, 'R', 'P', 'F', 'N', 'V', 'S'
StatusStyle:
  .DB 1, 5, 2, 3, 1, 5, 1
StatusText:
  .DB "  - ", "....", "PASS", "FAIL", "N/A ", "EYE ", "SKIP"

; ClearText / ClearPanel: empty buffers (and dirty).
ClearText:
.ACCU 8
  rep #$20
.ACCU 16
  lda #$0000
  ldx #$0FFE
-
  sta.l TEXTBUF,x
  dex
  dex
  bpl -
  sep #$20
.ACCU 8
  lda #1
  sta textdirty
  rts

ClearPanel:
  rep #$20
.ACCU 16
  lda #$0000
  ldx #$0FFE
-
  sta.l PANELBUF,x
  dex
  dex
  bpl -
  sep #$20
.ACCU 8
  lda #1
  sta paneldirty
  rts

; Panel: a box on BG2 from (row, column) = (box_r0, box_c0) to (box_r1, box_c1) inclusive.
Panel:
.ACCU 8
.INDEX 16
  lda box_r0
  sta prow
@row:
  lda prow
  ldy box_c0
  jsr At
  ; the left piece
  lda prow
  cmp box_r0
  bne +
  ldy #PANEL_TL
  bra @left
+
  cmp box_r1
  bne +
  ldy #PANEL_BL
  bra @left
+
  ldy #PANEL_L
@left:
  jsr PutPanel
  ; the middle
  lda box_c1
  sec
  sbc box_c0
  dec a
  sta pcount
-
  lda prow
  cmp box_r0
  bne +
  ldy #PANEL_T
  bra ++
+
  cmp box_r1
  bne +
  ldy #PANEL_B
  bra ++
+
  ldy #PANEL_C
++
  jsr PutPanel
  dec pcount
  bne -
  ; the right piece
  lda prow
  cmp box_r0
  bne +
  ldy #PANEL_TR
  bra @right
+
  cmp box_r1
  bne +
  ldy #PANEL_BR
  bra @right
+
  ldy #PANEL_R
@right:
  jsr PutPanel
  lda prow
  cmp box_r1
  beq @done
  inc prow
  bra @row
@done:
  lda #1
  sta paneldirty
  rts
; PutPanel: Y = a tile of the UI pool at X in the panel buffer; X += 2
PutPanel:
  rep #$20
.ACCU 16
  tya
  sta.l PANELBUF,x
  sep #$20
.ACCU 8
  inx
  inx
  rts

; PanelRowTile: the panel buffer's columns box_c0+1 .. box_c1-1 of row A set to tile Y (a highlight bar, or the plain fill back)
PanelRowTile:
.ACCU 8
  phy
  ldy box_c0
  iny
  jsr At
  ply
  lda box_c1
  sec
  sbc box_c0
  dec a
  sta pcount
-
  jsr PutPanel
  dec pcount
  bne -
  lda #1
  sta paneldirty
  rts

.INCLUDE "tc_video.asm"
.INCLUDE "tc_screens.asm"
.INCLUDE "tc_mem.asm"
.INCLUDE "tc_speed.asm"
.INCLUDE "tc_layers.asm"
.INCLUDE "tc_tables.inc"

.ENDS

.INCLUDE "tc_data.inc"
