; wmsdrv.asm: the WideMess sound driver (BrokenDev's WideMess devkit; docs/projects/widemess-devkit.md, the spec's section 4).
;
; An SPC700 program for the WideMess sound unit: songs on up to 24 channels played by voices 8-31 (BRR or 16-bit PCM samples anywhere in
; the 1 MB of sound RAM), effects on any of the 32 voices (voices 0-7, the S-DSP's own, play BRR samples from the first 64 KB).
; The main CPU loads everything through the sound RAM port ($21F0-$21F3) while the boot loader runs, then starts this program at $0200
; through the boot loader's jump command (wmssound.c). Nothing here is copied by the SPC700: the samples above 64 KB are the DSP's.
;
; Sound RAM, as wmssnd.py lays it out (the constants below must match its own):
;   $00000-$001FF  direct page and stack       $00200-$00FFF  this program (3.5 KB at most)
;   $01000-$07EFF  the sound bank: songs, pitch tables, instruments, effects (wmssnd.py)
;   $07F00-$07FFF  the S-DSP's sample directory (DIR = $7F): BRR samples for voices 0-7
;   $08000-$0FEFF  BRR samples (both directories list them)   $0FF00  a silent BRR block
;   $10000-$10FFF  the WideMess directory (WDIRH = 1, WDIRL = 0): 256 entries of 16 bytes
;   $11000-$FFFFF  16-bit PCM samples (and BRR that did not fit below 64 KB)
;
; The bank at $1000: +0 songs, +1 effects, +2 the song table, +4 the effect table, +6 the instrument table (addresses).
;   song table: a word per song, the song's header. Song header: channels (1-24), timer 0 divider (8 kHz / it = ticks a second),
;     the pitch table's address, then per channel its stream's address and its loop address (0: the channel ends).
;   instrument (4 bytes): SRCN, ADSR1, ADSR2, GAIN.    effect (8 bytes): SRCN, voice (0-31), pitch (16 bits), volume L, R, ADSR1, ADSR2.
; Channel streams (wmssnd.py writes them from MIDI):
;   $00-$7F len   note: pitch table entry, key on, then wait len ticks       $80 len   key off, wait len ticks
;   $81 i         instrument i                                              $82 l r   volumes (signed, 0-127)
;   $83 len       wait len ticks                                            $84       key off
;   $85           jump to the channel's loop address (or end there)         $86       end of the channel
; Commands from the main CPU: ports 1-3 = command, a, b, then port 0 = a new id; the driver echoes the id on port 0 when done.
;   1 a: play song a     2: stop the song     3 a b: effect a (b = a voice, or $FF for the effect's own)     4 a: master volume
; Status: port 1 = $5A once running, port 2 = WID ($57 on a WideMess), port 3 = channels sounding (heartbeat in the low bits).
;
; Assemble: wmsbuild.ps1 (wla-spc700 + wlalink -b: a plain image from $0200).

.MEMORYMAP
  SLOTSIZE $10000
  DEFAULTSLOT 0
  SLOT 0 $0000
.ENDME
.ROMBANKSIZE $10000
.ROMBANKS 1
.EMPTYFILL $00

; ---- the layout (wmssnd.py: DRIVER, BANK, SNES_DIR, WIDE_DIR)
.DEFINE BANK      $1000
.DEFINE SNESDIR   $7F
.DEFINE WDIRHIGH  $01
.DEFINE MAXCH     24

; ---- direct page
.DEFINE ptr       $00      ; 16 bits: the stream being read
.DEFINE lastcmd   $02
.DEFINE playing   $03
.DEFINE song      $04      ; 16 bits: the song header
.DEFINE nch       $06
.DEFINE ticks     $07
.DEFINE chan      $08      ; the channel being run
.DEFINE tmp       $09
.DEFINE ptab      $0A      ; 16 bits: the song's pitch table
.DEFINE itab      $0C      ; 16 bits: the instrument table
.DEFINE stab      $0E      ; 16 bits: the song table
.DEFINE etab      $10      ; 16 bits: the effect table
.DEFINE kon       $12      ; 3 bytes: key-on bits per bank of voices 8-31, this tick
.DEFINE koff      $15      ; 3 bytes: key-off bits
.DEFINE wid       $18
.DEFINE base      $19      ; the selected voice's register base
.DEFINE vbit      $1A      ; the selected voice's bit in its bank
.DEFINE vbank     $1B      ; its bank (0-2 for voices 8-31, $FF for voices 0-7)
.DEFINE heart     $1C
.DEFINE sounding  $1D
.DEFINE eptr      $1E      ; 16 bits: an effect entry
.DEFINE CHPTR     $20      ; 48 bytes: each channel's stream position
.DEFINE CHLOOP    $50      ; 48 bytes: each channel's loop address
.DEFINE CHWAIT    $80      ; 24 bytes: ticks left before its next event
.DEFINE CHON      $98      ; 24 bytes: the channel runs

.BANK 0 SLOT 0
.ORGA $0200
.SECTION "Driver" FORCE

Start:
  clrp
  mov x, #$EF
  mov sp, x
  mov $f1, #$30            ; clear the input ports, timers off, the boot ROM off
  mov a, #$00
  mov x, #$02
-
  mov (x)+, a              ; clear the direct page variables $02-$EF
  cmp x, #$F0
  bne -

  ; ---- WID: 0 into the S-DSP's unused $1D, then $9D reads $57 on an unlocked WideMess (the mirror, 0, elsewhere)
  mov $f2, #$1D
  mov $f3, #$00
  mov $f2, #$9D
  mov a, $f3
  mov wid, a

  ; ---- the S-DSP
  mov $f2, #$6C            ; FLG: no reset, not muted, echo writes off, noise clock 0
  mov $f3, #$20
  mov $f2, #$5C            ; KOFF
  mov $f3, #$00
  mov $f2, #$4C            ; KON
  mov $f3, #$00
  mov $f2, #$0C            ; MVOLL / MVOLR
  mov $f3, #$7F
  mov $f2, #$1C
  mov $f3, #$7F
  mov $f2, #$2C            ; EVOLL / EVOLR, EFB, EON, NON, PMON, EDL
  mov $f3, #$00
  mov $f2, #$3C
  mov $f3, #$00
  mov $f2, #$0D
  mov $f3, #$00
  mov $f2, #$4D
  mov $f3, #$00
  mov $f2, #$3D
  mov $f3, #$00
  mov $f2, #$2D
  mov $f3, #$00
  mov $f2, #$7D
  mov $f3, #$00
  mov $f2, #$6D            ; ESA (echo writes are off)
  mov $f3, #$FF
  mov $f2, #$5D            ; DIR
  mov $f3, #SNESDIR

  ; ---- the WideMess voices: their directory, and each bank's PMON, NON, EON, KOFF cleared
  mov a, wid
  cmp a, #$57
  bne +
  mov $f2, #$DD            ; WDIRL
  mov $f3, #$00
  mov $f2, #$ED            ; WDIRH
  mov $f3, #WDIRHIGH
  mov x, #$00
-
  mov $f2, #$8C            ; WBANK
  mov $f3, x
  mov $f2, #$AD            ; WPMON
  mov $f3, #$00
  mov $f2, #$BD            ; WNON
  mov $f3, #$00
  mov $f2, #$CD            ; WEON
  mov $f3, #$00
  mov $f2, #$DC            ; WKOFF
  mov $f3, #$00
  inc x
  cmp x, #$03
  bne -
+
  ; ---- the bank's tables
  mov a, !BANK+2
  mov stab, a
  mov a, !BANK+3
  mov stab+1, a
  mov a, !BANK+4
  mov etab, a
  mov a, !BANK+5
  mov etab+1, a
  mov a, !BANK+6
  mov itab, a
  mov a, !BANK+7
  mov itab+1, a

  ; ---- ready
  mov $f4, #$00
  mov $f6, wid
  mov $f7, #$00
  mov $f5, #$5A
  mov $fa, #50             ; timer 0: 8 kHz / 50 = 160 ticks a second (the song header sets its own)
  mov $f1, #$01

MainLoop:
  mov a, $f4
  cmp a, lastcmd
  beq +
  call !Command
+
  mov a, $fd               ; timer 0's counter (cleared by the read)
  beq MainLoop
  mov ticks, a
-
  mov a, playing
  beq +
  call !Tick
+
  inc heart
  dbnz ticks, -
  mov a, heart
  and a, #$07
  or a, sounding
  mov $f7, a
  bra MainLoop

;================================================================================== commands
Command:
  mov lastcmd, a
  mov a, $f5
  cmp a, #$01
  bne +
  call !PlaySong
  bra CmdDone
+ cmp a, #$02
  bne +
  call !StopSong
  bra CmdDone
+ cmp a, #$03
  bne +
  call !PlayEffect
  bra CmdDone
+ cmp a, #$04
  bne CmdDone
  mov a, $f6
  mov $f2, #$0C
  mov $f3, a
  mov $f2, #$1C
  mov $f3, a
CmdDone:
  mov a, lastcmd
  mov $f4, a               ; done
  ret

; ---- song $f6: its header from the song table, every channel from its stream
PlaySong:
  call !StopSong
  mov a, $f6
  cmp a, !BANK+0
  bcs PlayDone             ; no such song
  asl a
  mov y, a
  mov a, [stab]+y
  mov song, a
  inc y
  mov a, [stab]+y
  mov song+1, a
  mov y, #$00
  mov a, [song]+y          ; channels
  cmp a, #MAXCH+1
  bcc +
  mov a, #MAXCH
+ mov nch, a
  inc y
  mov a, [song]+y          ; the timer divider
  mov $fa, a
  inc y
  mov a, [song]+y
  mov ptab, a
  inc y
  mov a, [song]+y
  mov ptab+1, a
  mov x, #$00
-
  inc y                    ; channel x: stream, loop
  mov a, [song]+y
  mov CHPTR+x, a
  inc y
  mov a, [song]+y
  mov CHPTR+1+x, a
  inc y
  mov a, [song]+y
  mov CHLOOP+x, a
  inc y
  mov a, [song]+y
  mov CHLOOP+1+x, a
  inc x
  inc x
  mov a, x
  lsr a
  cmp a, nch
  bne -
  mov x, #$00
-
  mov a, #$01
  mov CHWAIT+x, a          ; every channel reads its first events at the next tick
  mov CHON+x, a
  inc x
  cmp x, nch
  bne -
  mov playing, #$01
PlayDone:
  ret

; ---- every channel off, its voice keyed off
StopSong:
  mov playing, #$00
  mov sounding, #$00
  mov x, #$00
-
  mov a, #$00
  mov CHON+x, a
  inc x
  cmp x, #MAXCH
  bne -
  mov a, wid
  cmp a, #$57
  bne +
  mov x, #$00
-
  mov $f2, #$8C
  mov $f3, x
  mov $f2, #$DC            ; WKOFF: all of the bank
  mov $f3, #$FF
  inc x
  cmp x, #$03
  bne -
+ ret

; ---- effect $f6 on its voice, or on voice $f7
PlayEffect:
  mov a, $f6
  cmp a, !BANK+1
  bcc +
  ret                      ; no such effect
+ mov y, #$00              ; eptr = etab + 8 x effect
  mov tmp, a
  asl a
  asl a
  asl a
  clrc
  adc a, etab
  mov eptr, a
  mov a, etab+1
  adc a, #$00
  mov eptr+1, a
  mov y, #$01
  mov a, [eptr]+y          ; its voice
  mov x, a
  mov a, $f7
  cmp a, #$FF
  beq +
  mov x, a
+ call !SelectVoice
  mov y, #$00
  mov a, [eptr]+y          ; SRCN
  mov tmp, a
  mov a, base
  or a, #$04
  mov $f2, a
  mov $f3, tmp
  mov y, #$02
  mov a, [eptr]+y          ; pitch
  mov tmp, a
  mov a, base
  or a, #$02
  mov $f2, a
  mov $f3, tmp
  inc y
  mov a, [eptr]+y
  mov tmp, a
  mov a, base
  or a, #$03
  mov $f2, a
  mov $f3, tmp
  inc y
  mov a, [eptr]+y          ; volumes
  mov tmp, a
  mov a, base
  mov $f2, a
  mov $f3, tmp
  inc y
  mov a, [eptr]+y
  mov tmp, a
  mov a, base
  or a, #$01
  mov $f2, a
  mov $f3, tmp
  inc y
  mov a, [eptr]+y          ; ADSR
  mov tmp, a
  mov a, base
  or a, #$05
  mov $f2, a
  mov $f3, tmp
  inc y
  mov a, [eptr]+y
  mov tmp, a
  mov a, base
  or a, #$06
  mov $f2, a
  mov $f3, tmp
  ; key on now: KOFF cleared for the voice, then KON
  mov a, vbank
  cmp a, #$FF
  bne +
  mov $f2, #$5C
  mov $f3, #$00
  mov $f2, #$4C
  mov $f3, vbit
  bra EffDone
+ mov $f2, #$DC
  mov $f3, #$00
  mov $f2, #$CC
  mov $f3, vbit
EffDone:
  ret

;================================================================================== voices
; X = voice 0-31: base = its register base (voices 8-31: in the second page, WBANK selected), vbit, vbank. Keeps X.
SelectVoice:
  mov a, x
  and a, #$07
  mov y, a
  mov a, !Bits+y
  mov vbit, a
  mov a, y
  xcn a                    ; (voice mod 8) x 16
  mov base, a
  mov a, x
  cmp a, #$08
  bcs +
  mov vbank, #$FF          ; voices 0-7: the S-DSP's first page
  ret
+ sbc a, #$08              ; carry set: a - 8
  lsr a
  lsr a
  lsr a
  mov vbank, a
  mov $f2, #$8C            ; WBANK
  mov $f3, a
  or base, #$80
  ret

Bits:
  .db $01, $02, $04, $08, $10, $20, $40, $80

;================================================================================== one tick of the song
Tick:
  mov a, #$00
  mov kon, a
  mov kon+1, a
  mov kon+2, a
  mov koff, a
  mov koff+1, a
  mov koff+2, a
  mov chan, a
TickLoop:
  mov x, chan
  mov a, CHON+x
  beq TickNext
  dec CHWAIT+x
  bne TickNext
  call !RunChannel
TickNext:
  inc chan
  mov a, chan
  cmp a, nch
  bne TickLoop
  ; the bank's key-offs, then its key-ons (a voice keyed on this tick is not keyed off)
  mov x, #$00
-
  mov $f2, #$8C
  mov $f3, x
  mov a, kon+x
  eor a, #$FF
  and a, koff+x
  mov $f2, #$DC
  mov $f3, a
  mov a, kon+x
  mov $f2, #$CC
  mov $f3, a
  inc x
  cmp x, #$03
  bne -
  ; channels sounding: those still running (a status for the main CPU)
  mov a, #$00
  mov x, #$00
-
  clrc
  adc a, CHON+x
  inc x
  cmp x, nch
  bne -
  asl a
  asl a
  asl a
  mov sounding, a
  ret

; the channel chan's events until one that waits
RunChannel:
  mov a, chan
  asl a
  mov x, a
  mov a, CHPTR+x
  mov ptr, a
  mov a, CHPTR+1+x
  mov ptr+1, a
  mov a, chan
  clrc
  adc a, #$08
  mov x, a
  call !SelectVoice
Event:
  mov y, #$00
  mov a, [ptr]+y
  incw ptr                 ; (incw sets N and Z from the pointer: test the byte itself)
  cmp a, #$80
  bcs EvCommand
  ; a note: pitch table entry a
  asl a
  mov y, a
  mov a, [ptab]+y
  mov tmp, a
  mov a, base
  or a, #$02
  mov $f2, a
  mov $f3, tmp
  inc y
  mov a, [ptab]+y
  mov tmp, a
  mov a, base
  or a, #$03
  mov $f2, a
  mov $f3, tmp
  mov x, vbank
  mov a, kon+x
  or a, vbit
  mov kon+x, a
  jmp !EvWait
EvCommand:
  cmp a, #$80
  bne +
  mov x, vbank             ; a rest: key off, then wait
  mov a, koff+x
  or a, vbit
  mov koff+x, a
  jmp !EvWait
+ cmp a, #$81
  bne +
  mov y, #$00              ; instrument: SRCN, ADSR1, ADSR2, GAIN
  mov a, [ptr]+y
  incw ptr
  asl a
  asl a
  mov y, a
  mov a, [itab]+y
  mov tmp, a
  mov a, base
  or a, #$04
  mov $f2, a
  mov $f3, tmp
  inc y
  mov a, [itab]+y
  mov tmp, a
  mov a, base
  or a, #$05
  mov $f2, a
  mov $f3, tmp
  inc y
  mov a, [itab]+y
  mov tmp, a
  mov a, base
  or a, #$06
  mov $f2, a
  mov $f3, tmp
  inc y
  mov a, [itab]+y
  mov tmp, a
  mov a, base
  or a, #$07
  mov $f2, a
  mov $f3, tmp
  jmp !Event
+ cmp a, #$82
  bne +
  mov y, #$00              ; volumes
  mov a, [ptr]+y
  mov tmp, a
  mov a, base
  mov $f2, a
  mov $f3, tmp
  inc y
  mov a, [ptr]+y
  mov tmp, a
  mov a, base
  or a, #$01
  mov $f2, a
  mov $f3, tmp
  incw ptr
  incw ptr
  jmp !Event
+ cmp a, #$83
  bne +
  jmp !EvWait
+ cmp a, #$84
  bne +
  mov x, vbank
  mov a, koff+x
  or a, vbit
  mov koff+x, a
  jmp !Event
+ cmp a, #$85
  bne EvEnd
  mov a, chan              ; jump to the loop point (an end when there is none)
  asl a
  mov x, a
  mov a, CHLOOP+x
  mov ptr, a
  mov a, CHLOOP+1+x
  mov ptr+1, a
  or a, ptr
  beq EvEnd
  jmp !Event
EvEnd:
  mov x, chan
  mov a, #$00
  mov CHON+x, a
  mov x, vbank
  mov a, koff+x
  or a, vbit
  mov koff+x, a
  ret
EvWait:
  mov y, #$00
  mov a, [ptr]+y
  incw ptr
  mov x, chan
  mov CHWAIT+x, a
  mov a, chan
  asl a
  mov x, a
  mov a, ptr
  mov CHPTR+x, a
  mov a, ptr+1
  mov CHPTR+1+x, a
  ret

.ENDS
