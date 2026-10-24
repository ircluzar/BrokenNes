; WideMess sound test ROM, the SPC700 side (docs/projects/widemess-spec.md section 4). Uploaded by sound.asm through the boot loader to $0200,
; started at $0300.
;
; It reads WID (DSP $9D, after writing 0 to $1D) and reports it on port 1, runs step 0 (the init: echo buffer, globals, both directories, all 32
; voices), says "ready" ($EE on port 0), then plays whatever step number the 65816 writes to port 0: it keys every voice off, makes the step's DSP
; register writes (spc_data.inc, made by gen_data.py) and echoes the number. Between steps it reports voice 12's playing address (second page,
; bank 0, voice 4: $CA / $CB / $CE) on ports 1-3. The same program runs on a SNES and on a locked WideMess, where every write to $80-$FF goes
; nowhere and every read of them is the S-DSP's mirror: that is the negative control.
;
; Assemble: build.ps1 (wla-spc700 + wlalink -b from the PVSnesLib toolchain).

.MEMORYMAP
  SLOTSIZE $10000
  DEFAULTSLOT 0
  SLOT 0 $0000
.ENDME
.ROMBANKSIZE $10000
.ROMBANKS 1
.EMPTYFILL $00

; ---- direct page variables (the echo buffer is at $8000, so nothing writes here but this program)
.DEFINE LastCmd $10
.DEFINE Ptr     $12      ; 16 bits
.DEFINE Count   $14      ; 16 bits

.BANK 0 SLOT 0

.INCLUDE "spc_samples.inc"

.ORGA $0300
.SECTION "Code" FORCE
Start:
  clrp
  mov x, #$EF
  mov sp, x
  mov $f1, #$30            ; clear the input ports (the boot loader's last command), timers off, boot ROM off

  ; ---- WID: 0 into the S-DSP's unused $1D, then read $9D: $57 on an unlocked WideMess, the mirror (0) elsewhere
  mov $f2, #$1D
  mov $f3, #$00
  mov $f2, #$9D
  mov a, $f3
  mov $f5, a
  mov $f6, #$00
  mov $f7, #$00

  ; ---- step 0: the init
  mov a, #$00
  call !RunStep
  mov LastCmd, #$00
  mov $f4, #$EE            ; ready

MainLoop:
  mov a, $f4
  cmp a, LastCmd
  beq Idle
  mov LastCmd, a
  call !KeyOffAll
  mov a, LastCmd
  call !RunStep
  mov a, LastCmd
  mov $f4, a               ; done
  bra MainLoop

Idle:
  mov a, LastCmd
  beq MainLoop             ; before the first step, port 1 keeps the WID report
  mov $f2, #$8C            ; WBANK = 0
  mov $f3, #$00
  mov $f2, #$CA            ; voice 12's playing address, low ...
  mov a, $f3
  mov $f5, a
  mov $f2, #$CB            ; ... middle ...
  mov a, $f3
  mov $f6, a
  mov $f2, #$CE            ; ... high
  mov a, $f3
  mov $f7, a
  bra MainLoop

; ---- A = step number: make its DSP register writes (a 16-bit count, then register, value pairs)
RunStep:
  asl a
  mov x, a
  mov a, !StepTable+x
  mov Ptr, a
  mov a, !StepTable+1+x
  mov Ptr+1, a
  mov y, #$00
  mov a, [Ptr]+y
  mov Count, a
  incw Ptr
  mov a, [Ptr]+y
  mov Count+1, a
  incw Ptr
RunLoop:
  movw ya, Count
  beq RunDone
  mov y, #$00
  mov a, [Ptr]+y
  mov $f2, a
  incw Ptr
  mov a, [Ptr]+y
  mov $f3, a
  incw Ptr
  decw Count
  bra RunLoop
RunDone:
  ret

; ---- every voice off: KOFF on the S-DSP and the three banks, a short wait (the DSP polls KOFF every other sample), then KOFF cleared
KeyOffAll:
  mov $f2, #$5C
  mov $f3, #$FF
  mov x, #$00
-
  mov $f2, #$8C
  mov $f3, x
  mov $f2, #$DC
  mov $f3, #$FF
  inc x
  cmp x, #$03
  bne -
  mov y, #$00
-
  dbnz y, -
  mov $f2, #$5C
  mov $f3, #$00
  mov x, #$00
-
  mov $f2, #$8C
  mov $f3, x
  mov $f2, #$DC
  mov $f3, #$00
  inc x
  cmp x, #$03
  bne -
  ret

.INCLUDE "spc_steps.inc"
.ENDS
