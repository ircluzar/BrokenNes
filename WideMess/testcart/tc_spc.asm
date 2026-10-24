; The WideMess test cartridge's SPC700 program (docs/projects/widemess-spec.md section 4). The 65816 writes it to sound RAM $0200 through the
; main CPU's port ($21F0-$21F3), with the samples and both directories, then starts it with the boot loader's jump.
;
; It plays nothing by itself. The 65816 writes a list of DSP register writes into the mailbox ($0400: a count, then register / value pairs;
; register $FE is a pause of value x 256 SPC700 loops, for KOFF before KON) and a new number on port 0; the program makes the writes and echoes
; the number. In between, it reports, in its RAM at $0500, what only the SPC700 can read: every voice's ENVX and OUTX (voices 8-31 through
; the second DSP page and WBANK), the new voices' playing addresses, ENDX and WENDX, and the WID it read at the start. The 65816 reads the
; report through the same port.
;
; Report ($0500): +$00 WID ($9D after 0 into $1D)  +$01 $A5 (running)  +$02 a 16-bit count of reports  +$04 ENDX  +$05-$07 WENDX of banks 0-2
;   +$10 ENVX of voices 0-31  +$30 OUTX of voices 0-31  +$50 / +$70 / +$90 the playing address (low, middle, high) of voices 0-31 (8-31 only)
;
; Assemble: build.ps1 (wla-spc700 + wlalink -b).

.MEMORYMAP
  SLOTSIZE $10000
  DEFAULTSLOT 0
  SLOT 0 $0000
.ENDME
.ROMBANKSIZE $10000
.ROMBANKS 1
.EMPTYFILL $00

.DEFINE LastCmd $10
.DEFINE Count   $11
.DEFINE Tmp     $12
.DEFINE Reports $14      ; 16 bits

.DEFINE Mailbox $0400
.DEFINE Report  $0500
.DEFINE RepEnvx $0510
.DEFINE RepOutx $0530
.DEFINE RepAdL  $0550
.DEFINE RepAdM  $0570
.DEFINE RepAdH  $0590

.BANK 0 SLOT 0
.ORGA $0200
.SECTION "Code" FORCE
Start:
  clrp
  mov x, #$EF
  mov sp, x
  mov $f1, #$30            ; clear the input ports, timers off, the boot ROM off
  mov LastCmd, #$00
  mov Reports, #$00
  mov Reports+1, #$00
  ; WID: 0 into the S-DSP's unused $1D, then $9D
  mov $f2, #$1D
  mov $f3, #$00
  mov $f2, #$9D
  mov a, $f3
  mov !Report+0, a
  mov a, #$A5
  mov !Report+1, a
  mov $f4, #$00

MainLoop:
  mov a, $f4
  cmp a, LastCmd
  beq Idle
  mov LastCmd, a
  call !RunList
  mov a, LastCmd
  mov $f4, a               ; done
Idle:
  call !Reporting
  bra MainLoop

; ---- the mailbox's writes
RunList:
  mov a, !Mailbox
  beq @done
  mov Count, a
  mov x, #$01
@next:
  mov a, !Mailbox+x
  cmp a, #$FE
  beq @pause
  mov $f2, a
  inc x
  mov a, !Mailbox+x
  mov $f3, a
  inc x
  dbnz Count, @next
@done:
  ret
@pause:
  inc x
  mov a, !Mailbox+x
  inc x
  mov Tmp, a
@outer:
  mov y, #$00
@inner:
  dbnz y, @inner
  dbnz Tmp, @outer
  dbnz Count, @next
  ret

; ---- the report
Reporting:
  mov x, #$00
@sdsp:                     ; voices 0-7: $x8 ENVX, $x9 OUTX
  mov a, x
  xcn a
  or a, #$08
  mov $f2, a
  mov a, $f3
  mov !RepEnvx+x, a
  inc $f2
  mov a, $f3
  mov !RepOutx+x, a
  inc x
  cmp x, #$08
  bne @sdsp
@wide:                     ; voices 8-31: the second page, bank (v - 8) / 8, offset ((v - 8) mod 8) x 16
  mov a, x
  setc
  sbc a, #$08
  mov Tmp, a
  lsr a
  lsr a
  lsr a
  mov $f2, #$8C            ; WBANK
  mov $f3, a
  mov a, Tmp
  and a, #$07
  xcn a
  or a, #$88               ; $80 + offset x 16 + 8: ENVX
  mov $f2, a
  mov a, $f3
  mov !RepEnvx+x, a
  inc $f2                  ; OUTX
  mov a, $f3
  mov !RepOutx+x, a
  inc $f2                  ; $xA: the playing address, low
  mov a, $f3
  mov !RepAdL+x, a
  inc $f2                  ; $xB: middle
  mov a, $f3
  mov !RepAdM+x, a
  mov a, $f2
  clrc
  adc a, #$03              ; $xE: high
  mov $f2, a
  mov a, $f3
  mov !RepAdH+x, a
  inc x
  cmp x, #32
  bne @wide
  mov $f2, #$7C            ; ENDX
  mov a, $f3
  mov !Report+4, a
  mov x, #$00
@wendx:
  mov $f2, #$8C
  mov $f3, x
  mov $f2, #$FC            ; WENDX
  mov a, $f3
  mov !Report+5+x, a
  inc x
  cmp x, #$03
  bne @wendx
  incw Reports
  mov a, Reports
  mov !Report+2, a
  mov a, Reports+1
  mov !Report+3, a
  ret
.ENDS
