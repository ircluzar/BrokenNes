"""Nested GB -> SNES sound: the GB APU on the SPC700 (gb2snes.py includes this).

The SPC700 runs a tiny relay driver; the 65816 does the GB side once a frame (in NMI): length counters, volume
envelopes, the wave RAM, then turns each GB channel into S-DSP register values and sends only the ones that changed.
  voice 0 = pulse 1, voice 1 = pulse 2: a 16-sample BRR square per duty (12.5 / 25 / 50 / 75 %), SRCN = duty
  voice 2 = wave: a 32-sample BRR sample whose data bytes are the GB wave RAM xor $88 (4-bit unsigned -> signed)
  voice 3 = noise: the DSP noise generator (one global rate, FLG bits 0-4, nearest to the GB NR43 rate)
Pitch: P = 4096 * rate / 32000 with rate = 16 (pulse) or 32 (wave) samples per GB period; both work out to
P = 268435.456 / (2048 - x), a 2048-entry table. Volume: envelope (0-15) * (NR50 side level + 1), panned by NR51.
Envelopes tick once per SNES frame (GB: 64 Hz), lengths 4.25 per frame (GB: 256 Hz); no sweep. Approximations, by
design: this is the proof of concept's sound.

Relay protocol (APU ports): CPU writes port1 = DSP register / RAM low byte, port2 = value, port3 = 0 (DSP) or 1 (RAM
$03xx), then port0 = a new counter value; the driver does it and echoes port0.
"""

SPC_BASE = 0x0200
DRIVER = 0x0400
WAVE_BRR = 0x0340          # 2 BRR blocks (header + 8 data bytes each)


def brr_block(samples, last):
    """16 signed 4-bit samples -> one BRR block, range 11, filter 0 (end + loop on the last block)."""
    hdr = (11 << 4) | (3 if last else 0)
    out = [hdr]
    for i in range(0, 16, 2):
        out.append(((samples[i] & 15) << 4) | (samples[i + 1] & 15))
    return out


def spc_image():
    img = bytearray(DRIVER + 0x40 - SPC_BASE)
    def put(addr, bs): img[addr - SPC_BASE:addr - SPC_BASE + len(bs)] = bytes(bs)
    # squares: duty n/8 of 16 samples high (+7), the rest low (-7)
    duty_hi = [2, 4, 8, 12]
    sq = []
    for d in range(4):
        smp = [7 if i < duty_hi[d] else -7 for i in range(16)]
        sq.append(0x0300 + d * 9)
        put(0x0300 + d * 9, brr_block(smp, True))
    put(WAVE_BRR, brr_block([0] * 16, False))
    put(WAVE_BRR + 9, brr_block([0] * 16, True))
    # directory ($0200): start, loop for samples 0-3 (squares), 4 (wave)
    d = []
    for a in sq + [WAVE_BRR]:
        d += [a & 0xFF, a >> 8, a & 0xFF, a >> 8]
    put(0x0200, d)
    # the relay driver (hand-assembled SPC700)
    drv = [0xE4, 0xF4,          # 0400 mov a,$f4
           0xC4, 0x00,          # 0402 mov $00,a
           0xE4, 0xF4,          # 0404 mov a,$f4
           0x64, 0x00,          # 0406 cmp a,$00
           0xF0, 0xFA,          # 0408 beq $0404
           0xC4, 0x00,          # 040A mov $00,a
           0xE4, 0xF7,          # 040C mov a,$f7
           0xD0, 0x08,          # 040E bne $0418
           0xFA, 0xF5, 0xF2,    # 0410 mov $f2,$f5
           0xFA, 0xF6, 0xF3,    # 0413 mov $f3,$f6
           0x2F, 0x07,          # 0416 bra $041F
           0xF8, 0xF5,          # 0418 mov x,$f5
           0xE4, 0xF6,          # 041A mov a,$f6
           0xD5, 0x00, 0x03,    # 041C mov $0300+x,a
           0xE4, 0x00,          # 041F mov a,$00
           0xC4, 0xF4,          # 0421 mov $f4,a
           0x2F, 0xDF]          # 0423 bra $0404
    put(DRIVER, drv)
    return bytes(img)


def pitch_table():
    out = []
    for x in range(2048):
        p = int(round(268435.456 / (2048 - x)))
        out.append(min(p, 0x3FFF))
    return out


DSP_NOISE_HZ = [0, 16, 21, 25, 31, 42, 50, 63, 83, 100, 125, 167, 200, 250, 333, 400, 500, 667, 800, 1000, 1300, 1600,
                2000, 2700, 3200, 4000, 5300, 6400, 8000, 10700, 16000, 32000]


def noise_table():
    """NR43 -> the nearest DSP noise rate index (GB: 524288 / r / 2^(s+1) Hz, r = 0 counts as 0.5)."""
    out = []
    for v in range(256):
        s, r = v >> 4, v & 7
        hz = 524288 / (r if r else 0.5) / (2 ** (s + 1))
        best = min(range(1, 32), key=lambda i: abs(DSP_NOISE_HZ[i] - min(hz, 32000)))
        out.append(best)
    return out


def vol_table():
    """[env 0-15][side level 0-7] -> DSP voice volume."""
    return [min(127, v * (l + 1)) for v in range(16) for l in range(8)]


def dw(vals): return '\n'.join('        dw ' + ','.join('$%04X' % v for v in vals[i:i + 16]) for i in range(0, len(vals), 16))
def db(vals): return '\n'.join('        db ' + ','.join('$%02X' % v for v in vals[i:i + 16]) for i in range(0, len(vals), 16))


# ------------------------------------------------------------------------------------------------- 65816 runtime
# state: $7E:3D00 + ch*8: +0 on, +1 vol, +2 env timer, +3/+4 length (16-bit, x4 for sub-steps); $7E:3D80 DSP shadow
# (128 bytes, $FF = unknown); $7E:3D40 wave copy; DP $90 sound ok, $91 port0 counter, $92/$93/$94 command

def runtime():
    img = spc_image()
    return r'''
; ============================================================ sound: IPL upload of the relay driver (DB = 0)
sndboot: stz $90
        bra .s0
.fail:  rts
.s0:    ldy #$0000
.w0:    iny
        beq .fail
        rep #$20
        .a16
        lda.w $2140
        cmp #$BBAA
        sep #$20
        .a8
        bne .w0
        rep #$20
        .a16
        lda #$0200
        sta.w $2142
        sep #$20
        .a8
        lda #$01
        sta.w $2141
        lda #$CC
        sta.w $2140
        ldy #$0000
.w1:    iny
        beq .fail
        cmp.w $2140
        bne .w1
        ldx #$0000
.b:     lda.l SPCIMG,x
        sta.w $2141
        txa
        sta.w $2140
        ldy #$0000
.w2:    iny
        beq .fail
        cmp.w $2140
        bne .w2
        inx
        cpx #SPCLEN
        bne .b
        rep #$20
        .a16
        lda #$0400
        sta.w $2142
        sep #$20
        .a8
        stz.w $2141
        txa
        clc
        adc #$02
        bne .k
        inc a
.k:     sta.w $2140
        sta $91
        ldy #$0400
.w3:    dey                         ; (give the driver a moment to start)
        bne .w3
        lda #$01
        sta $90
        ; DSP setup: DIR, volumes, echo off, noise on voice 3, GAIN fixed, sources, key on
        ldx #$0000
.ini:   lda.l DSPINIT,x
        cmp #$FF
        beq .ki
        sta $92
        lda.l DSPINIT+1,x
        sta $93
        stz $94
        jsr sndcmd
        inx
        inx
        bra .ini
.ki:    ldx #$0000
        lda #$FF
.sh:    sta.l $7E3D80,x
        inx
        cpx #$0080
        bne .sh
        ldx #$0000
        lda #$00
.st:    sta.l $7E3D00,x
        inx
        cpx #$0040
        bne .st
        lda #$FF
        ldx #$0000
.wc:    sta.l $7E3D40,x
        inx
        cpx #$0010
        bne .wc
        rts

; one relay command ($92 reg/addr, $93 value, $94 type); DB = 0
sndcmd: lda $93
        sta.w $2142
        lda $92
        sta.w $2141
        lda $94
        sta.w $2143
        lda $91
        inc a
        sta $91
        sta.w $2140
        phy
        ldy #$0000
.w:     iny
        beq .to
        cmp.w $2140
        bne .w
.to:    ply
        rts

; DSP register $92 = A when it differs from the shadow
dspset: sta $93
        phx
        lda $92
        and #$7F
        tax
        lda $93
        cmp.l $7E3D80,x
        beq .same
        sta.l $7E3D80,x
        stz $94
        jsr sndcmd
.same:  plx
        rts

; a GB sound register write (wrx, DB = $7F, X = address, the value already stored): NRx4 bit 7 triggers
sndw:   pha
        txa
        cmp #$14
        beq .t0
        cmp #$19
        beq .t1
        cmp #$1E
        beq .t2
        cmp #$23
        beq .t3
        pla
        rts
.t0:    lda #$00
        bra .tg
.t1:    lda #$01
        bra .tg
.t2:    lda #$02
        bra .tg
.t3:    lda #$03
.tg:    sta $97
        lda.w $0000,x
        bpl .no
        lda $97
        jsr sndtrig
.no:    pla
        rts

; A = channel (0-3): on, envelope start, length
sndtrig: phx
        sta $97
        asl a
        asl a
        asl a
        tax
        lda #$01
        sta.l $7E3D00,x
        lda $97
        cmp #$02
        beq .wave
        phx
        tax
        lda.l NRX2,x
        tax
        lda.l $7FFF00,x
        plx
        pha
        lsr a
        lsr a
        lsr a
        lsr a
        sta.l $7E3D01,x
        pla
        and #$07
        sta.l $7E3D02,x
        phx
        lda $97
        tax
        lda.l NRX1,x
        tax
        lda.l $7FFF00,x
        plx
        and #$3F
        eor #$3F
        inc a
        rep #$20
        .a16
        and #$00FF
        bra .ls
.wave:  .a8
        lda #$0F
        sta.l $7E3D01,x
        lda.l $7FFF1B
        eor #$FF
        inc a
        rep #$20
        .a16
        and #$00FF
        bne .ls
        lda #$0100
.ls:    asl a
        asl a
        sta.l $7E3D03,x
        lda #$0000
        sep #$20
        .a8
        plx
        rts

; once a frame (NMI, DB = 0): the GB channels -> DSP registers
sndframe: lda $90
        bne .go
        rts
.go:    lda.l $7FFF26
        bmi .on
        lda #$00
        sta.l $7E3D00
        sta.l $7E3D08
        sta.l $7E3D10
        sta.l $7E3D18
.on:    ldx #$0000
.ch:    phx
        jsr sndch
        plx
        inx
        cpx #$0004
        bne .ch
        ; noise rate (FLG bits 0-4, echo writes off)
        lda.l $7FFF22
        tax
        lda.l NOISETAB,x
        ora #$20
        pha
        lda #$6C
        sta $92
        pla
        jsr dspset
        ; the wave RAM: changed bytes into the BRR sample (xor $88)
        ldx #$0000
.wv:    lda.l $7FFF30,x
        cmp.l $7E3D40,x
        beq .wn
        sta.l $7E3D40,x
        eor #$88
        sta $93
        lda.l WAVEPOS,x
        sta $92
        lda #$01
        sta $94
        jsr sndcmd
.wn:    inx
        cpx #$0010
        bne .wv
        rts

; channel X (0-3): lengths, envelope, then VOLL / VOLR / pitch / SRCN of voice X
sndch:  txa
        sta $99
        asl a
        asl a
        asl a
        tax                         ; X = state offset
        lda.l $7E3D00,x
        bne .alive
        jmp .out
.alive: ; length counter (NRx4 bit 6): 4.25 steps a frame, in quarter steps (17)
        phx
        lda $99
        tax
        lda.l NRX4,x
        tax
        lda.l $7FFF00,x
        plx
        and #$40
        beq .env
        rep #$20
        .a16
        lda.l $7E3D03,x
        sec
        sbc #17
        bcs .lok
        lda #$0000
        sta.l $7E3D03,x
        sep #$20
        .a8
        lda #$00
        sta.l $7E3D00,x
        jmp .out
.lok:   .a16
        sta.l $7E3D03,x
        sep #$20
        .a8
.env:   lda $99
        cmp #$02
        beq .dac
        lda.l $7E3D02,x
        beq .dac
        dec a
        sta.l $7E3D02,x
        bne .dac
        phx
        lda $99
        tax
        lda.l NRX2,x
        tax
        lda.l $7FFF00,x
        plx
        pha
        and #$07
        sta.l $7E3D02,x
        pla
        and #$08
        beq .down
        lda.l $7E3D01,x
        cmp #$0F
        beq .dac
        inc a
        sta.l $7E3D01,x
        bra .dac
.down:  lda.l $7E3D01,x
        beq .dac
        dec a
        sta.l $7E3D01,x
.dac:   ; DAC off: NRx2 & $F8 == 0 (wave: NR30 bit 7)
        lda $99
        cmp #$02
        bne .d1
        lda.l $7FFF1A
        and #$80
        bra .d2
.d1:    phx
        tax
        lda.l NRX2,x
        tax
        lda.l $7FFF00,x
        plx
        and #$F8
.d2:    bne .vol
        lda #$00
        sta.l $7E3D00,x
        bra .out
.vol:   lda $99
        cmp #$02
        bne .ev
        lda.l $7FFF1C               ; wave level: 0 / 100 / 50 / 25 %
        lsr a
        lsr a
        lsr a
        lsr a
        lsr a
        and #$03
        phx
        tax
        lda.l WAVELVL,x
        plx
        bra .vset
.ev:    lda.l $7E3D01,x
.vset:  sta $98                     ; envelope volume 0-15
        bra .emit
.out:   stz $98
.emit:  ; VOLL: NR51 bit ch+4, NR50 bits 4-6; VOLR: NR51 bit ch, NR50 bits 0-2
        lda $99
        asl a
        asl a
        asl a
        asl a
        sta $92                     ; voice base
        lda $99
        tax
        lda.l BITS+4,x
        and.l $7FFF25
        beq .l0
        lda.l $7FFF24
        lsr a
        lsr a
        lsr a
        lsr a
        jsr volof
        bra .l1
.l0:    lda #$00
.l1:    jsr dspset
        inc $92
        lda $99
        tax
        lda.l BITS,x
        and.l $7FFF25
        beq .r0
        lda.l $7FFF24
        jsr volof
        bra .r1
.r0:    lda #$00
.r1:    jsr dspset
        lda $99
        cmp #$03
        beq .done
        ; pitch from the 11-bit GB frequency
        tax
        lda.l NRX4,x
        tax
        lda.l $7FFF00,x
        and #$07
        xba
        lda.l $7FFEFF,x             ; NRx3 sits right below NRx4
        rep #$20
        .a16
        asl a
        tax
        lda.l PTAB,x
        sta $95
        lda #$0000
        sep #$20
        .a8
        inc $92
        lda $95
        jsr dspset                  ; P low
        inc $92
        lda $96
        jsr dspset                  ; P high
        lda $99
        cmp #$02
        beq .done
        inc $92
        tax
        lda.l NRX1,x
        tax
        lda.l $7FFF00,x
        rol a
        rol a
        rol a
        and #$03
        jsr dspset                  ; SRCN = duty
.done:  rts

; A = NR50 side level (bits 0-2), $98 = envelope volume -> A = DSP volume
volof:  and #$07
        sta $97
        lda $98
        asl a
        asl a
        asl a
        ora $97
        phx
        tax
        lda.l VOLTAB,x
        plx
        rts

NRX1:   db $11,$16,$1B,$20
NRX2:   db $12,$17,$1A,$21
NRX4:   db $14,$19,$1E,$23
BITS:   db $01,$02,$04,$08,$10,$20,$40,$80
WAVELVL: db $00,$0F,$08,$04
WAVEPOS: db $41,$42,$43,$44,$45,$46,$47,$48,$4A,$4B,$4C,$4D,$4E,$4F,$50,$51
DSPINIT: db $5D,$02, $0C,$7F, $1C,$7F, $2C,$00, $3C,$00, $4D,$00, $3D,$08, $6C,$20, $2D,$00
        db $05,$00, $15,$00, $25,$00, $35,$00, $07,$7F, $17,$7F, $27,$7F, $37,$7F
        db $04,$02, $14,$02, $24,$04, $34,$00, $02,$00, $03,$10, $12,$00, $13,$10, $22,$00, $23,$10, $32,$00, $33,$10
        db $00,$00, $01,$00, $10,$00, $11,$00, $20,$00, $21,$00, $30,$00, $31,$00
        db $5C,$00, $4C,$0F, $FF,$FF
VOLTAB:
''' + db(vol_table()) + '''
NOISETAB:
''' + db(noise_table()) + '''
PTAB:
''' + dw(pitch_table()) + '''
SPCLEN = ''' + str(len(img)) + '''
SPCIMG:
''' + db(list(img)) + '\n'
