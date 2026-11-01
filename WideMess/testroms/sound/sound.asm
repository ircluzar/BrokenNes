; WideMess test ROM 2: the sound chip (docs/projects/widemess-spec.md section 4, milestone 3). The 65816 side; the SPC700 side is spc.asm.
;
; 1. Unlocks the WideMess and keeps what it reads: WMID and WMFEAT.
; 2. Uploads the SPC700 program through the SNES boot loader (so it runs on a SNES too).
; 3. Fills the sound RAM through the port $21F0-$21F3 with general DMA: 24 samples at 20-bit addresses (16-bit PCM and BRR), a one-shot
;    sample, a stream ring and the new voices' directory (gen_data.py's jobs), then reads the directory back through the port and keeps the sum.
; 4. Waits for the SPC700 program's "ready", keeps the WID it read, then sends steps 1-33 on port 0, 12 frames apart (the stream step 14,
;    rewriting the ring through the port 6 frames in, while the voice plays it). What it keeps, in work RAM (bank $7E), for Workshop --wms-test:
;      $00-$01 frame count   $02 step   $03 WMID   $04 WMFEAT   $05 the SPC700's WID   $06-$07 read-back sum   $08-$0A ports 1-3   $0B ready
; Built with -D NOUNLOCK it skips the unlock and does everything else: the negative control. A locked WideMess must sound exactly as a SNES.
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
  NAME "WIDEMESS SOUND       "
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

; ---- work RAM (direct page = bank $7E)
.DEFINE frame    $00     ; 16 bits
.DEFINE step     $02
.DEFINE cpuid    $03
.DEFINE cpufeat  $04
.DEFINE spcwid   $05
.DEFINE sum      $06     ; 16 bits
.DEFINE ports    $08     ; 3 bytes: the SPC700's ports 1-3 at the last NMI
.DEFINE ready    $0B
.DEFINE left     $0C     ; frames left in the step

; ---- WideMess registers
.DEFINE WMUNLOCK  $4220
.DEFINE WMID      $4221
.DEFINE WMFEAT    $4224
.DEFINE WMSADDL   $21F0
.DEFINE WMSADDM   $21F1
.DEFINE WMSADDH   $21F2
.DEFINE WMSDATA   $21F3

.INCLUDE "cpu_defs.inc"

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
  sta $2100             ; forced blank (the screen stays black: this ROM is about sound)
  stz $4200
  ldx #$00FF
-
  stz $00,x
  dex
  bpl -

.IFNDEF NOUNLOCK
  lda #$57
  sta WMUNLOCK
  lda #$4D
  sta WMUNLOCK
.ENDIF
  lda WMID
  sta cpuid
  lda WMFEAT
  sta cpufeat

  jsr UploadSpc
  jsr FillSoundRam

  ; ---- the directory read back through the port: the sum of its bytes
  lda #READBACK_L
  sta WMSADDL
  lda #READBACK_M
  sta WMSADDM
  lda #READBACK_H
  sta WMSADDH
  ldx #READBACK_LEN
-
  lda WMSDATA
  rep #$20
.ACCU 16
  and #$00FF
  clc
  adc sum
  sta sum
  sep #$20
.ACCU 8
  dex
  bne -

  ; ---- wait for the SPC700 program, keep the WID it read
-
  lda $2140
  cmp #$EE
  bne -
  lda $2141
  sta spcwid
  lda #$01
  sta ready

  lda #$80
  sta $4200             ; NMI on
  ldx #$0001
StepLoop:
  txa
  sta $2140             ; the step number to the SPC700
  sta step
  lda #STEP_FRAMES
  cpx #STREAM_STEP
  bne +
  lda #STREAM_FRAMES
+
  sta left
FrameLoop:
  wai
  cpx #STREAM_STEP
  bne +
  lda left
  cmp #STREAM_REWRITE_LEFT
  bne +
  jsr RewriteRing       ; in vblank: the voice keeps playing the ring while the port rewrites it
+
  dec left
  bne FrameLoop
  inx
  cpx #STEP_COUNT
  bne StepLoop
Done:
  wai
  bra Done

; ---- the SPC700 program through the boot loader: to $0200, then run from $0300
UploadSpc:
-
  lda $2140
  cmp #$AA
  bne -
-
  lda $2141
  cmp #$BB
  bne -
  lda #$01
  sta $2141             ; a transfer ...
  stz $2142
  lda #$02
  sta $2143             ; ... to $0200
  lda #$CC
  sta $2140
-
  cmp $2140
  bne -
  ldx #$0000
  ldy #$0000
UploadByte:
  lda SpcImage.w,x
  sta $2141
  tya
  sta $2140
-
  cmp $2140
  bne -
  inx
  iny
  cpx #(SpcImageEnd - SpcImage)
  bne UploadByte
  stz $2141             ; execute ...
  stz $2142
  lda #$03
  sta $2143             ; ... at $0300
  tya
  inc a                 ; one past the next index: a new command
  sta $2140
-
  cmp $2140
  bne -
  rts

; ---- the sound RAM through the port: one general DMA per job (gen_data.py)
FillSoundRam:
  ldx #$0000
JobLoop:
  rep #$20
.ACCU 16
  lda JobAddrLo.w,x
  sep #$20
.ACCU 8
  sta WMSADDL
  xba
  sta WMSADDM
  lda JobAddrHi.w,x
  sta WMSADDH
  stz $4300             ; one register, A to B, incrementing
  lda #$F3
  sta $4301             ; -> $21F3
  rep #$20
.ACCU 16
  lda JobSrc.w,x
  sta $4302
  lda JobLen.w,x
  sta $4305
  sep #$20
.ACCU 8
  stz $4304             ; bank $00
  lda #$01
  sta $420B
  inx
  inx
  cpx #(JOB_COUNT * 2)
  bne JobLoop
  rts

; ---- the stream ring's second content (a 2 kHz sine) over the first, while voice 12 plays it
RewriteRing:
  lda #RING_ADDR_L
  sta WMSADDL
  lda #RING_ADDR_M
  sta WMSADDM
  lda #RING_ADDR_H
  sta WMSADDH
  stz $4300
  lda #$F3
  sta $4301
  rep #$20
.ACCU 16
  lda #(SramData + RING_B_OFFSET)
  sta $4302
  lda #RING_LEN
  sta $4305
  sep #$20
.ACCU 8
  stz $4304
  lda #$01
  sta $420B
  rts

VBlank:
  rep #$30
.ACCU 16
.INDEX 16
  pha
  phx
  phy
  sep #$20
.ACCU 8
  lda $4210
  lda $2141
  sta ports
  lda $2142
  sta ports+1
  lda $2143
  sta ports+2
  rep #$20
.ACCU 16
  inc frame
  ply
  plx
  pla
  rti

.ACCU 8
.INDEX 16
EmptyHandler:
  rti

SpcImage:
  .INCBIN "spc.bin"
SpcImageEnd:
SramData:
  .INCBIN "sram.bin"

.INCLUDE "cpu_tables.inc"

.ENDS
