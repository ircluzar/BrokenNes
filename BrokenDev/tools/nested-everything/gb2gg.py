"""Nested GB -> GG (proof of concept): a Game Boy ROM becomes a Game Gear ROM.

    python gb2gg.py convert  <in.gb> <out.gg> [--pcs run.pcs] [--count] [--report r.json]
    python gb2gg.py feedback <in.gb> <out.gg> [--frames N] [--input spec] [--workshop exe] [--count] [--report r.json]

How (docs/projects/nested-gb-sega-research.md): most SM83 opcodes are the same Z80 bytes, so the game's code runs as
it is on the GG's Z80; the ~16 opcodes that differ are rewritten in place (same length) or through JP trampolines that
also carry the instructions they cover (Nested-style lightweight static recompilation):
  - ldh (n),a / ldh a,(n) / ld ($FFnn) -> RST $08/$10 + n: the GB I/O registers and HRAM live in a shadow page
    (cart RAM $BF00-$BFFF); LY comes from the GG V counter, P1 from the GG pad, serial bytes go to a log ($BC00)
  - push af / pop af -> RST $18/$20: GB flag layout (ZNHC0000) <-> Z80 layout through two 256-byte tables
  - halt / jr $ -> RST $28: wait for the next frame (and draw it) when the GB has interrupts off
  - reti -> RST $30 (ei; ret)
  - ld (hl+/-) / ld a,(hl+/-) / ld (c) / ld (nn),sp / add sp / ld hl,sp+e / swap -> trampolines
  - ld ($nnnn),a / ld a,($nnnn) with RAM / VRAM addresses -> the Z80's own ld (nn),a / ld a,(nn) (same 3 bytes)
  - ld ($2000),a (MBC1 bank) -> call mbc (GB 16KB banks = GG 16KB banks, slot 1 = $4000)
GB VRAM ($8000-$9FFF) and external RAM are the GG's cartridge RAM mapped at $8000-$BFFF, so the game's own VRAM
writes need no rewriting; once a frame the runtime turns the GB background map and tiles into GG name-table entries
and 4bpp tiles (2bpp rows padded with two zero planes) and the GB palette into GG colours.
RAM code (the game copies code to WRAM and runs it there) is found by the feedback pass (Workshop --gbrun --pcs) and
patched at its ROM source, so the copy carries the patched bytes.

--count builds a measuring ROM: every executed GB instruction increments a 24-bit counter... (not used: the GG
build's instruction count is the GB core's, Z80 T-states come from Workshop --sms-run --watch-pc).
"""
import argparse, json, os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import sm83
from asmz80 import assemble

LDHW, LDHR, PUSHAF, POPAF, HALTV, OP1 = 0x08, 0x10, 0x18, 0x20, 0x28, 0x30
KINDS = {0x22: 1, 0x2A: 2, 0x32: 3, 0x3A: 4, 0xE2: 5, 0xF2: 6, 0xD9: 7, 0xFB: 8, 0x27: 9}
SH = 0xBF00          # GB I/O + HRAM shadow page
VARS = 0xBDA0        # runtime variables
OAM = 0xBD00         # OAM shadow
SER = 0xBC00         # serial log ($BC00-$BCFE, count at $BCFF)

RUNTIME = r'''
S_A     = $BDA0
S_HL    = $BDA2
S_RET   = $BDA4
S_T     = $BDA6
S_LCDC  = $BDA7
S_FRAME = $BDA8
S_RR    = $BDB0
SER     = $BC00
S_FULL  = $BDAA
S_PAL   = $BDAB
S_SP    = $BDAC
S_DE    = $BDAE
S_AF    = $BDB2
S_SPS   = $BDB4
S_LYC   = $BDB6
S_IHL   = $BDB8
S_IAF   = $BDBA
S_LBGP  = $BDBC
S_LOBP  = $BDBD
S_UF    = $BDBE
S_RRS   = $BDBF
S_MKEY  = $BDF8
S_MF    = $BDFC
DIRTY   = $BDC0
MDIRTY  = $BDF0
KT      = $BE00
KF      = $BE40
KU      = $BE80
SPT     = $BEC0
SH      = $BF00

; ---------------------------------------------------------------- RST bodies
ldhw:   ld (S_A),a
        ex (sp),hl
        ld a,(hl)
        inc hl
        ex (sp),hl
        jp io_write
ldhr:   ex (sp),hl
        ld a,(hl)
        inc hl
        ex (sp),hl
        jp io_read
ldcw:   ld (S_A),a
        ld a,c
        jp io_write
ldcr:   ld a,c
        jp io_read

pushaf: ld (S_HL),hl
        pop hl
        ld (S_RET),hl
        push af
        pop hl
        ld h,hi(Z2G)
        ld l,(hl)
        ld h,a
        push hl
        ld hl,(S_RET)
        push hl
        ld hl,(S_HL)
        ret

popaf:  ld (S_HL),hl
        pop hl
        ld (S_RET),hl
        pop hl
        ld a,h
        ld h,hi(G2Z)
        ld l,(hl)
        ld h,a
        push hl
        pop af
        ld hl,(S_RET)
        push hl
        ld hl,(S_HL)
        ret

; halt (and the idle loop 'jr $', marked by the $FE after the RST): with interrupts on, a real HALT (the frame
; interrupt draws and calls the GB handler); with them off, wait for the frame and draw it here
haltb:  push af
        ld a,i
        jp po,.di
        pop af
        halt
        jr .tail
.di:    in a,($BF)
        and $80
        jr z,.di
        call sync
        ld a,(SH+$0F)
        or 1
        ld (SH+$0F),a
        pop af
.tail:  ex (sp),hl
        ld (S_T),a
        ld a,(hl)
        cp $FE
        jr nz,.ret
        ld a,(S_T)
        ex (sp),hl
        jr haltb2
.ret:   ld a,(S_T)
        ex (sp),hl
        ret
haltb2: push af
        ld a,i
        jp po,.di
        pop af
        halt
        jr haltb2
.di:    pop af
        call haltb
        jr haltb2

; ---------------------------------------------------------------- RST $30: the 1-byte opcodes that differ
; (ld (hl+/-), ld (c), reti) at sites a trampoline cannot cover: the kind comes from a page table by site address
op1:    ld (S_HL),hl
        pop hl
        push hl
        dec hl
        push af
        push de
        ld a,l
        xor h
.probe: ld e,a
        ld d,hi(HT)
        ld a,(de)
        cp l
        jr nz,.next
        inc d
        ld a,(de)
        cp h
        jr nz,.next
        inc d
        ld a,(de)
        pop de
        ld hl,(S_HL)
        jr .kind
.next:  ld a,e
        inc a
        jr .probe
.kind:
        cp 1
        jr z,.k1
        cp 2
        jr z,.k2
        cp 3
        jr z,.k3
        cp 4
        jr z,.k4
        cp 5
        jr z,.k5
        cp 6
        jr z,.k6
        cp 7
        jr z,.k7
        cp 8
        jr z,.k8
        cp 9
        jr z,.k9
;RSTKINDS
        pop af
        ret
.k8:    pop af
        jp eichk
.k9:    pop af
        jp gbdaa
.k1:    pop af
        call vmark
        ld (hl),a
        inc hl
        ret
.k2:    pop af
        ld a,(hl)
        inc hl
        ret
.k3:    pop af
        call vmark
        ld (hl),a
        dec hl
        ret
.k4:    pop af
        ld a,(hl)
        dec hl
        ret
.k5:    pop af
        jp ldcw
.k6:    pop af
        jp ldcr
.k7:    pop af
        inc sp
        inc sp
        ei
        ret

; ---------------------------------------------------------------- frame interrupt ($0038)
; the runtime's scratch (S_HL, S_A, S_T) is saved around the GB handler: the interrupt can land inside an
; op1 / ldh handler, and the GB handler's own reti / ldh would overwrite it
irq:    ld (S_IHL),hl
        push af
        pop hl
        ld (S_IAF),hl
        ld hl,(S_HL)
        push hl
        ld hl,(S_A)
        push hl
        ld hl,(S_T)
        push hl
        in a,($BF)
        call sync
        ld a,(SH+$FF)
        and 1
        jr z,.no
        ld hl,irq_tail
        push hl
        ld hl,(S_IAF)
        push hl
        pop af
        ld hl,(S_IHL)
        jp $0040
.no:    ld a,(SH+$0F)
        or 1
        ld (SH+$0F),a
        pop hl
        ld (S_T),hl
        pop hl
        ld (S_A),hl
        pop hl
        ld (S_HL),hl
        ld hl,(S_IAF)
        push hl
        pop af
        ld hl,(S_IHL)
        ei
        ret
irq_tail:
        di
        ld (S_IHL),hl
        pop hl
        ld (S_T),hl
        pop hl
        ld (S_A),hl
        pop hl
        ld (S_HL),hl
        ld hl,(S_IHL)
        ei
        ret

; ---------------------------------------------------------------- ei: a pending, enabled interrupt is taken now
eichk:  push hl
        push af
        ld (S_A),a
        ld a,(SH+$0F)
        ld l,a
        ld a,(SH+$FF)
        and l
        and $1F
        jp nz,irq_take
        pop af
        pop hl
        ei
        ret

; ---------------------------------------------------------------- GB DAA (the Z80's differs: H, invalid BCD)
gbdaa:  push hl
        push bc
        push af
        pop hl
        ld a,h
        ld c,l
        bit 1,c
        jr nz,.sub
        bit 0,c
        jr nz,.a60
        cp $9A
        jr c,.l1
.a60:   add a,$60
        set 0,c
.l1:    bit 4,c
        jr nz,.a06
        ld b,a
        and $0F
        cp $0A
        ld a,b
        jr c,.fin
.a06:   add a,$06
        jr .fin
.sub:   bit 0,c
        jr z,.s1
        sub $60
.s1:    bit 4,c
        jr z,.fin
        sub $06
.fin:   ld h,a
        ld a,c
        and $03
        ld l,a
        ld a,h
        or a
        jr nz,.nz
        set 6,l
.nz:    push hl
        pop af
        pop bc
        pop hl
        ret

; ---------------------------------------------------------------- MBC1 bank select
mbc:    push af
        and $1F
        jr nz,.n
        inc a
.n:     ld ($FFFE),a
        pop af
        ret

; ---------------------------------------------------------------- I/O write: A = port, (S_A) = value; keeps everything
io_write:
        push hl
        push af
        ld l,a
        ld h,hi(SH)
        ld a,(S_A)
        ld (hl),a
        ld a,l
        cp $02
        jr z,.sc
        cp $40
        jr z,.lcdc
        cp $46
        jr z,.dma
        cp $47
        jr z,.bgp
        cp $48
        jr z,.bgp
        cp $04
        jr z,.div
        cp $0F
        jr z,.irq
        cp $FF
        jr z,.irq
.done:  pop af
        pop hl
        ld a,(S_A)
        ret
.div:   xor a
        ld (hl),a
        jr .done
; IF / IE written with interrupts on: a requested, enabled interrupt is taken now (the handler's reti comes back
; after the ldh)
.irq:   ld a,i
        jp po,.done
@irq_take:
        ld a,(SH+$0F)
        ld l,a
        ld a,(SH+$FF)
        and l
        and $1F
        jp z,_L_io_write_done
        ld h,$40
.bit:   rrca
        jr c,.take
        ld l,a
        ld a,h
        add a,8
        ld h,a
        ld a,l
        jr .bit
.take:  ld a,h
        sub $40
        rrca
        rrca
        rrca
        ld l,a
        ld a,1
        inc l
.sh:    dec l
        jr z,.got
        add a,a
        jr .sh
.got:   cpl
        ld l,a
        ld a,(SH+$0F)
        and l
        ld (SH+$0F),a
        ld a,h
        ld (S_RET),a
        xor a
        ld (S_RET+1),a
        pop af
        pop hl
        di
        ld (S_HL),hl
        ld hl,(S_RET)
        push hl
        ld hl,(S_HL)
        ld a,(S_A)
        ret
.bgp:   ld a,1
        ld (S_PAL),a
        jr .done
.sc:    ld a,(S_A)
        and $81
        cp $81
        jr nz,.done
        push de
        ld a,(SER+$FF)
        ld e,a
        ld d,hi(SER)
        ld a,(SH+$01)
        ld (de),a
        ld a,e
        inc a
        cp $FF
        jr z,.full
        ld (SER+$FF),a
.full:  pop de
        ld a,(SH+$02)
        and $7F
        ld (SH+$02),a
        ld a,$FF
        ld (SH+$01),a
        ld a,(SH+$0F)
        or 8
        ld (SH+$0F),a
        jr .done
.lcdc:  ld a,(S_LCDC)
        cpl
        ld l,a
        ld a,(S_A)
        ld (S_LCDC),a
        and l
        and $80
        jr z,.done
        ld a,1
        ld (S_FULL),a
        jr .done
.dma:   push bc
        push de
        ld a,(S_A)
        ld h,a
        ld l,0
        ld de,$BD00
        ld bc,160
        ldir
        pop de
        pop bc
        jr .done

; ---------------------------------------------------------------- I/O read: A = port -> A = value; keeps the rest
io_read:
        push hl
        ld l,a
        ld h,hi(SH)
        push af
        ld a,l
        cp $44
        jr z,.ly
        cp $41
        jr z,.stat
        cp $00
        jr z,.joy
        cp $04
        jr z,.div
.done:  pop af
        ld a,(hl)
        pop hl
        ret
.div:   ld a,r
        ld (hl),a
        jr .done
.stat:  ld a,(hl)
        and $FC
        ld (hl),a
        call ly
        cp 144
        jr c,.done
        set 0,(hl)
        jr .done
.ly:    push hl
        ld a,i
        jp pe,.lyc
        in a,($BF)
        and $80
        jr z,.lyc
        call sync
        ld a,(SH+$0F)
        or 1
        ld (SH+$0F),a
.lyc:   pop hl
        call ly
        ld (hl),a
        jr .done
.joy:   ld a,(hl)
        and $30
        ld l,a
        ld a,$0F
        bit 4,l
        jr nz,.nodir
        push bc
        ld b,a
        in a,($DC)
        ld c,a
        ld a,$0F
        bit 3,c
        jr nz,.r
        and $0E
.r:     bit 2,c
        jr nz,.l
        and $0D
.l:     bit 0,c
        jr nz,.u
        and $0B
.u:     bit 1,c
        jr nz,.d
        and $07
.d:     pop bc
.nodir: bit 5,l
        jr nz,.nobut
        push bc
        ld b,a
        in a,($DC)
        ld c,a
        ld a,b
        bit 4,c
        jr nz,.a
        and $0E
.a:     bit 5,c
        jr nz,.b
        and $0D
.b:     ld b,a
        in a,($00)
        bit 7,a
        ld a,b
        jr nz,.st
        and $07
.st:    pop bc
.nobut: or l
        or $C0
        ld h,hi(SH)
        ld l,0
        ld (hl),a
        jp .done

; LY from the GG V counter: GG lines 24-167 are the GB's 0-143, the rest is vblank 144-153
ly:     in a,($7E)
        sub 24
        jr c,.vb
        cp 144
        ret c
.vb:    ld a,(S_LYC)
        inc a
        cp 10
        jr c,.s
        xor a
.s:     ld (S_LYC),a
        add a,144
        ret

; ---------------------------------------------------------------- the picture: GB map / tiles / palette -> GG VDP
sync:   push bc
        push de
        push hl
        ld a,(S_FRAME)
        inc a
        ld (S_FRAME),a
        ; LCDC / BGP / OBP0 may have been written through a pointer (no I/O hook): compare with what we drew
        ld a,(SH+$40)
        ld b,a
        ld a,(S_LCDC)
        cpl
        and b
        and $80
        jr z,.nf
        ld a,1
        ld (S_FULL),a
.nf:    ld a,b
        ld (S_LCDC),a
        ld a,(SH+$47)
        ld hl,S_LBGP
        cp (hl)
        jr nz,.pal
        ld a,(SH+$48)
        inc hl
        cp (hl)
        jr z,.np
.pal:   call palette
.np:    ld a,(S_LCDC)
        and $80
        ld b,$A0
        jr z,.off
        ld b,$E0
.off:   ld a,b
        out ($BF),a
        ld a,$81
        out ($BF),a
        ld a,(S_LCDC)
        and $80
        jr z,.out
        ld a,(S_FULL)
        or a
        jr z,.part
        xor a
        ld (S_FULL),a
        ld (S_RR),a
        ld (S_RR+1),a
        ld b,0
        call tiles
        ld b,128
        call tiles
        ld hl,KF
        ld b,64
.kf:    ld (hl),0
        inc hl
        djnz .kf
        ld a,1
        ld (S_MF),a
        call map
        jr .out
.part:  call dtiles
        ld a,(S_FRAME)
        and 7
        jr nz,.nf8
        ld a,1
        ld (S_MF),a
.nf8:   ld a,(SH+$43)
        and $F8
        ld b,a
        ld hl,S_MKEY
        ld a,(hl)
        cp b
        jr nz,.mk
        ld a,(SH+$42)
        and $F8
        ld b,a
        inc hl
        ld a,(hl)
        cp b
        jr nz,.mk
        ld a,(S_LCDC)
        and $18
        ld b,a
        inc hl
        ld a,(hl)
        cp b
        jr z,.mok
.mk:    ld a,(SH+$43)
        and $F8
        ld (S_MKEY),a
        ld a,(SH+$42)
        and $F8
        ld (S_MKEY+1),a
        ld a,(S_LCDC)
        and $18
        ld (S_MKEY+2),a
        ld a,1
        ld (S_MF),a
.mok:   call map
        call sprites
.out:   pop hl
        pop de
        pop bc
        ret

palette: ld a,(SH+$47)
        ld (S_LBGP),a
        ld a,(SH+$48)
        ld (S_LOBP),a
        xor a
        ld (S_PAL),a
        out ($BF),a
        ld a,$C0
        out ($BF),a
        ld a,(SH+$47)
        ld c,a
        ld b,4
.p:     ld a,c
        and 3
        add a,a
        ld e,a
        ld d,0
        ld hl,SHADES
        add hl,de
        ld a,(hl)
        out ($BE),a
        inc hl
        ld a,(hl)
        out ($BE),a
        srl c
        srl c
        djnz .p
        ld a,$20
        out ($BF),a
        ld a,$C0
        out ($BF),a
        ld a,(SH+$48)
        ld c,a
        ld b,4
.q:     ld a,c
        and 3
        add a,a
        ld e,a
        ld d,0
        ld hl,SHADES
        add hl,de
        ld a,(hl)
        out ($BE),a
        inc hl
        ld a,(hl)
        out ($BE),a
        srl c
        srl c
        djnz .q
        ret

; GB OAM shadow ($BD00, 40 x 8x8) -> the GG sprite table: y-16+24-1, x-8+48. The VDP has no sprite flips: every
; sprite pattern comes from a 64-slot cache (GG tiles 384-447, sprite patterns 128-191 with the $2000 base), keyed by
; GB tile + flip bits, filled with (bit-reversed / row-reversed) copies of the GB tile. 4 slots a frame are dropped
; so tiles the game rewrites in place come back. (No behind-BG priority: VDP limit.)
sprites: ld a,(S_FRAME)
        add a,a
        add a,a
        and 63
        add a,$40
        ld l,a
        ld h,$BE
        xor a
        ld (hl),a
        inc l
        ld (hl),a
        inc l
        ld (hl),a
        inc l
        ld (hl),a
        ld hl,$BD00
        ld b,40
.p1:    push bc
        ld a,(hl)
        or a
        jr z,.hid
        cp 160
        jr nc,.hid
        push hl
        inc hl
        inc hl
        ld c,(hl)
        inc hl
        ld a,(hl)
        and $60
        or $80
        ld d,a
        rrca
        rrca
        rrca
        add a,c
        and 63
        ld e,a
        ld b,64
.pr:    ld h,$BE
        ld l,e
        ld a,(hl)
        cp c
        jr nz,.nm
        ld a,l
        add a,$40
        ld l,a
        ld a,(hl)
        cp d
        jr z,.hit
.nm:    ld a,e
        add a,$80
        ld l,a
        ld a,(S_FRAME)
        cp (hl)
        jr nz,.ev
        ld a,e
        inc a
        and 63
        ld e,a
        djnz .pr
        pop hl
        jr .hid
.ev:    ld (hl),a
        ld l,e
        ld (hl),c
        ld a,e
        add a,$40
        ld l,a
        ld (hl),d
        call sprupl
        jr .got
.hit:   ld a,e
        add a,$80
        ld l,a
        ld a,(S_FRAME)
        ld (hl),a
.got:   pop hl
        ld a,e
        add a,128
        jr .st
.hid:   xor a
.st:    ld c,a
        ld a,l
        rrca
        rrca
        and $3F
        add a,$C0
        ld e,a
        ld d,$BE
        ld a,c
        ld (de),a
        inc hl
        inc hl
        inc hl
        inc hl
        pop bc
        djnz .p1
        ; the sprite table
        xor a
        out ($BF),a
        ld a,$7F
        out ($BF),a
        ld hl,$BD00
        ld de,SPT
        ld b,40
.y:     ld a,(de)
        or a
        jr z,.hy
        ld a,(hl)
        add a,7
        cp $D0
        jr nz,.oy
        inc a
.oy:    out ($BE),a
        jr .ny
.hy:    ld a,$E0
        out ($BE),a
.ny:    inc hl
        inc hl
        inc hl
        inc hl
        inc de
        djnz .y
        ld b,24
        ld a,$E0
.r:     out ($BE),a
        djnz .r
        ld a,$80
        out ($BF),a
        ld a,$7F
        out ($BF),a
        ld hl,$BD01
        ld de,SPT
        ld b,40
.x:     ld a,(hl)
        add a,40
        out ($BE),a
        ld a,(de)
        out ($BE),a
        inc hl
        inc hl
        inc hl
        inc hl
        inc de
        djnz .x
        ret

; slot e <- GB tile c ($8000 + c*16) with the flips in d (bit 5 x, bit 6 y)
sprupl: push bc
        push de
        push hl
        ld h,0
        ld l,e
        add hl,hl
        add hl,hl
        add hl,hl
        add hl,hl
        add hl,hl
        ld a,l
        out ($BF),a
        ld a,h
        or $70
        out ($BF),a
        ld l,c
        ld h,0
        add hl,hl
        add hl,hl
        add hl,hl
        add hl,hl
        ld a,h
        or $80
        ld h,a
        ld a,d
        ld (S_UF),a
        ld de,2
        bit 6,a
        jr z,.ny
        ld de,14
        add hl,de
        ld de,$FFFE
.ny:    ld b,8
.row:   ld a,(hl)
        call sprfx
        out ($BE),a
        inc hl
        ld a,(hl)
        call sprfx
        out ($BE),a
        dec hl
        add hl,de
        xor a
        out ($BE),a
        nop
        out ($BE),a
        djnz .row
        pop hl
        pop de
        pop bc
        ret
sprfx:  ld c,a
        ld a,(S_UF)
        bit 5,a
        ld a,c
        ret z
        push hl
        ld h,hi(REV)
        ld l,c
        ld a,(hl)
        pop hl
        ret

; VRAM write-site dirty tracking: the converted store sites (ld (hl+/-),a, ld (nn),a into VRAM) call this with HL =
; the GB address: a tile ($8000-$97FF) sets its bit in DIRTY (48 bytes), a map byte its row bit in MDIRTY (64 rows)
vmark:  push af
        ld a,h
        sub $80
        cp $20
        jr nc,.x
        push hl
        push bc
        cp $18
        jr nc,.map
        ld b,a
        ld a,l
        rlca
        and 1
        ld c,a
        ld a,b
        add a,a
        add a,c
        add a,lo(DIRTY)
        ld c,a
        ld a,l
        rrca
        rrca
        rrca
        rrca
        and 7
        jr .set
.map:   sub $18
        add a,lo(MDIRTY)
        ld c,a
        ld a,l
        rlca
        rlca
        rlca
        and 7
.set:   ld b,a
        ld a,1
        inc b
.sh:    dec b
        jr z,.or
        add a,a
        jr .sh
.or:    ld l,c
        ld h,hi(DIRTY)
        or (hl)
        ld (hl),a
        pop bc
        pop hl
.x:     pop af
        ret

; the dirty tiles first (up to 24 a frame), then 2 more of the round robin
dtiles: ld hl,DIRTY
        ld c,24
        ld e,0
.byte:  ld a,(hl)
        or a
        jr z,.nb
        ld d,a
        ld b,8
.bit:   srl d
        jr nc,.nbit
        push bc
        push de
        push hl
        ld a,(S_RR)
        ld (S_RRS),a
        ld a,(S_RR+1)
        push af
        ld l,e
        ld h,0
        add hl,hl
        add hl,hl
        add hl,hl
        ld a,8
        sub b
        or l
        ld (S_RR),a
        ld a,h
        ld (S_RR+1),a
        ld b,1
        call tiles
        pop af
        ld (S_RR+1),a
        ld a,(S_RRS)
        ld (S_RR),a
        pop hl
        pop de
        pop bc
        dec c
        jr z,.full
.nbit:  djnz .bit
        ld (hl),0
.nb:    inc hl
        inc e
        ld a,e
        cp 48
        jr nz,.byte
        ld b,2
        jp tiles
.full:  ret                         ; budget used: this byte keeps its bits (uploaded again next frame)
SHADES: db $FF,$0F,$AA,$0A,$55,$05,$00,$00

; b tiles (0 = 256) from tile (S_RR) on (wrapping at 384): GB 2bpp rows -> GG 4bpp rows
tiles:  ld a,(S_RR)
        ld l,a
        ld h,0
        ld a,(S_RR+1)
        ld h,a
.t:     push hl
        add hl,hl
        add hl,hl
        add hl,hl
        add hl,hl
        push hl
        add hl,hl
        ld a,l
        out ($BF),a
        ld a,h
        or $40
        out ($BF),a
        pop hl
        ld de,$8000
        add hl,de
        ld c,8
.row:   ld a,(hl)
        out ($BE),a
        inc hl
        ld a,(hl)
        out ($BE),a
        inc hl
        xor a
        out ($BE),a
        nop
        out ($BE),a
        dec c
        jr nz,.row
        pop hl
        inc hl
        ld a,h
        cp 1
        jr nz,.nw
        ld a,l
        cp 128
        jr nz,.nw
        ld hl,0
.nw:    djnz .t
        ld a,l
        ld (S_RR),a
        ld a,h
        ld (S_RR+1),a
        ret

; the visible 22x20 window of the GB map at (SCX/8, SCY/8) -> name table rows 2-21, columns 5-26; fine scroll
map:    ld a,(SH+$43)
        and 7
        neg
        add a,8
        out ($BF),a
        ld a,$88
        out ($BF),a
        ld a,(SH+$42)
        and 7
        add a,216
        out ($BF),a
        ld a,$89
        out ($BF),a
        ld a,(S_LCDC)
        and $10
        ld d,0
        jr nz,.us
        ld d,1
.us:    ld a,(SH+$42)
        srl a
        srl a
        srl a
        ld e,a
        ld b,0
.rowl:  push bc
        ld a,(S_MF)
        or a
        jr nz,.do
        ld a,(S_LCDC)
        and 8
        ld a,e
        and 31
        jr z,.m00
        add a,32
.m00:   ld c,a
        srl a
        srl a
        srl a
        add a,lo(MDIRTY)
        ld l,a
        ld h,hi(MDIRTY)
        ld a,c
        and 7
        ld c,a
        ld a,(hl)
        inc c
.bs:    dec c
        jr z,.bt
        rrca
        jr .bs
.bt:    and 1
        jr nz,.do
        pop bc
        jr .skip
.do:    ld a,b
        add a,2
        ld l,a
        ld h,0
        add hl,hl
        add hl,hl
        add hl,hl
        add hl,hl
        add hl,hl
        add hl,hl
        ld a,l
        add a,10
        out ($BF),a
        ld a,h
        add a,$78
        out ($BF),a
        ld a,e
        and 31
        ld l,a
        ld h,0
        add hl,hl
        add hl,hl
        add hl,hl
        add hl,hl
        add hl,hl
        ld a,(S_LCDC)
        and 8
        ld a,$98
        jr z,.m0
        ld a,$9C
.m0:    add a,h
        ld h,a
        ld a,(SH+$43)
        srl a
        srl a
        srl a
        ld c,a
        ld b,22
.col:   ld a,l
        and $E0
        or c
        ld l,a
        ld a,(hl)
        out ($BE),a
        cpl
        rlca
        and d
        out ($BE),a
        ld a,c
        inc a
        and 31
        ld c,a
        djnz .col
        pop bc
.skip:  inc e
        inc b
        ld a,b
        cp 20
        jr nz,.rowl
        xor a
        ld (S_MF),a
        ld hl,MDIRTY
        ld b,8
.cl:    ld (hl),a
        inc hl
        djnz .cl
        ret

; ---------------------------------------------------------------- boot
boot:   im 1
        ld sp,$DFF0
        ld a,$08
        ld ($FFFC),a
        ld a,0
        ld ($FFFD),a
        ld a,1
        ld ($FFFE),a
        ld hl,$8000
        ld de,$8001
        ld bc,$1FFF
        ld (hl),0
        ldir
        ld hl,$BC00
        ld de,$BC01
        ld bc,$03FF
        ld (hl),0
        ldir
        ld hl,VDPINIT
        ld b,22
        ld c,$BF
        otir
        ld a,$00
        out ($BF),a
        ld a,$7F
        out ($BF),a
        ld a,$D0
        out ($BE),a
        ld a,$91
        ld (SH+$40),a
        ld (S_LCDC),a
        ld a,$FC
        ld (SH+$47),a
        ld a,1
        ld (S_PAL),a
        ld (S_FULL),a
        ld a,$CF
        ld (SH+$00),a
        ld a,$E1
        ld (SH+$0F),a
        ld a,$7E
        ld (SH+$02),a
        ld hl,$0151
        push hl
        pop af
        ld bc,$0013
        ld de,$00D8
        ld hl,$014D
        ld sp,$DFFE
        jp $0100
VDPINIT: db $04,$80, $A0,$81, $FF,$82, $FF,$83, $FF,$84, $FF,$85, $FF,$86, $00,$87, $00,$88, $00,$89, $FF,$8A
'''


def z2g(f):
    return ((f >> 6) & 1) << 7 | ((f >> 1) & 1) << 6 | ((f >> 4) & 1) << 5 | (f & 1) << 4


def g2z(g):
    return ((g >> 7) & 1) << 6 | ((g >> 6) & 1) << 1 | ((g >> 5) & 1) << 4 | ((g >> 4) & 1)


def free_runs(rom, an, lo, hi, protected):
    used = bytearray(len(rom))
    for (b, a), (n, m, f) in an.insn.items():
        o = an.file_off(b, a)
        for i in range(n): used[o + i] = 1
    for lo2, hi2 in protected:
        for i in range(lo2, hi2): used[i] = 1
    runs, i = [], lo
    while i < hi:
        if used[i] or rom[i] not in (0x00, 0xFF):
            i += 1; continue
        j = i
        while j < hi and not used[j] and rom[j] == rom[i]: j += 1
        if j - i >= 32: runs.append((i + 4, j - 4))      # keep a margin from what is around
        i = j
    return runs


class Alloc:
    def __init__(self, runs):
        self.runs = sorted(runs, key=lambda r: -(r[1] - r[0]))

    def take(self, n, align=1):
        for k, (lo, hi) in enumerate(self.runs):
            a = (lo + align - 1) // align * align
            if a + n <= hi:
                self.runs[k] = (a + n, hi)
                return a
        raise SystemExit('gb2gg: out of free space in bank 0 (need %d bytes)' % n)


def tramp_text(an, bank, pc, n, rd):
    """Z80 text doing what the GB instruction at pc does (in a trampoline: absolute targets), or None if it is the
    same Z80 instruction (copied verbatim)."""
    op = rd(pc)
    b1 = rd(pc + 1)
    w = b1 | rd(pc + 2) << 8
    e = sm83.s8(b1)
    # push af / pop af without touching memory below SP (games use SP as a data pointer): convert in the slot
    if op == 0xF5: return 'push af\nld (S_HL),hl\npop hl\nld h,hi(Z2G)\nld l,(hl)\nld h,a\npush hl\nld hl,(S_HL)'
    if op == 0xF1: return 'ld (S_HL),hl\npop hl\nld a,h\nld h,hi(G2Z)\nld l,(hl)\nld h,a\nld (S_AF),hl\nld (S_SPS),sp\nld sp,S_AF\npop af\nld sp,(S_SPS)\nld hl,(S_HL)'
    if op == 0x27: return 'call gbdaa'
    if op == 0xFB: return 'call eichk'
    if op == 0x22: return 'call vmark\nld (hl),a\ninc hl'
    if op == 0x2A: return 'ld a,(hl)\ninc hl'
    if op == 0x32: return 'call vmark\nld (hl),a\ndec hl'
    if op == 0xEA and 0x8000 <= w < 0xA000: return 'ld ($%04X),a\npush hl\nld hl,$%04X\ncall vmark\npop hl' % (w, w)
    if op == 0x3A: return 'ld a,(hl)\ndec hl'
    if op == 0xE2: return 'rst $%02X\ndb 0' % LDHW if False else 'call ldcw'
    if op == 0xF2: return 'call ldcr'
    if op == 0x08: return 'ld ($%04X),sp' % w
    if op == 0xE8: return 'ld (S_HL),hl\nld hl,%d\nadd hl,sp\nld sp,hl\nld hl,(S_HL)' % (e & 0xFFFF)
    if op == 0xF8: return 'ld hl,%d\nadd hl,sp' % (e & 0xFFFF)
    if op == 0xCB and 0x30 <= b1 <= 0x37:
        r = sm83.R8[b1 & 7]
        if r == 'a': return 'rrca\nrrca\nrrca\nrrca\nor a'
        return 'ld (S_T),a\nld a,%s\nrrca\nrrca\nrrca\nrrca\nld %s,a\nor a\nld a,(S_T)' % (r, r)
    if op in (0x18, 0x20, 0x28, 0x30, 0x38):
        t = (pc + 2 + e) & 0xFFFF
        return ('jp $%04X' % t) if op == 0x18 else 'jp %s,$%04X' % (sm83.CC[(op >> 3) - 4], t)
    return None


def inplace(an, bank, pc, n, rd, plain=False):
    """Same-length replacement bytes for the GB instruction at pc, or None (needs a trampoline), or 'same'."""
    op = rd(pc)
    b1 = rd(pc + 1)
    w = b1 | rd(pc + 2) << 8
    if op == 0xE0: return [0xC7 | LDHW, b1]
    if op == 0xF0: return [0xC7 | LDHR, b1]
    if op == 0xEA:
        if w >= 0xFF00: return [0xC7 | LDHW, w & 0xFF, 0x00]
        if 0xFE00 <= w < 0xFF00: w = OAM + (w & 0xFF)
        if w < 0x8000: return 'mbc'
        if 0x8000 <= w < 0xA000 and not plain: return None          # VRAM: a trampoline marks it dirty
        return [0x32, w & 0xFF, w >> 8]
    if op == 0xFA:
        if w >= 0xFF00: return [0xC7 | LDHR, w & 0xFF, 0x00]
        if 0xFE00 <= w < 0xFF00: w = OAM + (w & 0xFF)
        return [0x3A, w & 0xFF, w >> 8]
    if op == 0x76: return [0xC7 | HALTV] if rd(pc + 1) != 0xFE else None
    if op == 0x18 and b1 == 0xFE: return [0xC7 | HALTV, 0xFE]
    if op == 0x10: return [0x00, 0x00]
    if op in (0xCD, 0xC3, 0xC4, 0xCC, 0xD4, 0xDC, 0xC2, 0xCA, 0xD2, 0xDA) and w >= 0xFF80:
        w -= 0x4000
        return [op, w & 0xFF, w >> 8]
    # pointers into the GB I/O + HRAM page ($FFxx) or OAM ($FExx) become pointers into their shadows: the GG's own
    # $FExx/$FFxx are the RAM mirror and the mapper registers. (Accesses through them skip the I/O side effects.)
    if op in (0x01, 0x11, 0x21) and w >= 0xFE00:
        return [op, w & 0xFF, (SH >> 8) if w >= 0xFF00 else (OAM >> 8)]
    if op == 0x26 and b1 in (0xFE, 0xFF):
        return [op, (SH >> 8) if b1 == 0xFF else (OAM >> 8)]
    if op == 0x31 and w >= 0xFE00: w -= 0x2000; return [0x31, w & 0xFF, w >> 8]
    if op in (0x08, 0x22, 0x2A, 0x32, 0x3A, 0xE2, 0xF2, 0xE8, 0xF8, 0xF5, 0xF1, 0x27, 0xFB) or op == 0xCB and 0x30 <= b1 <= 0x37:
        return None
    if op in sm83.ILLEGAL: return 'same'
    return 'same'


def convert(data, pcs, report, count=False):
    cart = sm83.Cart(data)
    an = sm83.analyse(cart, pcs)
    rom = bytearray(data)
    if len(rom) < 0x8000: rom += b'\xFF' * (0x8000 - len(rom))
    report['cart'] = {'title': cart.title, 'mbc': cart.mbc, 'banks': cart.banks}
    report['analysis'] = an.summary()
    report['overlays'] = ['$%04X-$%04X <- file $%05X' % o for o in an.overlays]
    # vectors we take: $00 boot, $08-$30 runtime RSTs, $38 frame interrupt, $66 NMI
    taken = [0x00, 0x08, 0x10, 0x18, 0x20, 0x28, 0x30, 0x38, 0x66]
    clash = sorted('$%02X' % v for v in taken if (0, v) in an.labels or any(b == 0 and v <= a < v + 8 for (b, a) in an.insn))
    report['vector_clashes'] = clash
    protected = [(an.file_off(-1, lo), an.file_off(-1, lo) + hi - lo) for lo, hi, off in an.overlays]
    protected += [(0, 0x150), (0x7FF0, 0x8000)]
    runs0 = free_runs(rom, an, 0x150, 0x4000, protected)
    # a game with no room in bank 0 (Tetris fills its 32KB): the runtime, tables and trampolines live in cartridge
    # RAM $A000-$BBFF (the GB's external RAM area), copied there at boot from an added ROM bank
    ram_mode = cart.banks <= 2 and sum(h - l for l, h in runs0) < 0x1800
    report['runtime_home'] = 'cart RAM $A000-$BBFF (copied from ROM bank 2 at boot)' if ram_mode else 'free space in bank 0'
    if ram_mode:
        rom += bytearray(0x10000 - len(rom))
        alloc = Alloc([(0xA000, 0xBC00)])
    else:
        alloc = Alloc(runs0)
    tabs = alloc.take(1280, 256)         # Z2G, G2Z, HT (op1 sites: a hash of address lo / hi / kind, 3 pages)
    revt = alloc.take(256, 256)          # bit-reversed bytes (x-flipped sprite rows)
    op1_sites = {}
    for i in range(256):
        rom[tabs + i] = z2g(i)
        rom[tabs + 256 + i] = g2z(i)
    # the game's own RST routines on vectors we take: moved, their 'rst n' sites become RST $30 + a kind
    used = sorted({an.reader_for(b, a)(a) & 0x38 for (b, a), (n, m, f) in an.insn.items()
                   if (an.reader_for(b, a)(a) & 0xC7) == 0xC7 and (0, an.reader_for(b, a)(a) & 0x38) in an.insn})
    reloc = [v for v in used if v in (0x00, 0x08, 0x10, 0x18, 0x20, 0x28, 0x30, 0x38)]
    report['relocated_rst'] = ['$%02X' % v for v in reloc]
    kinds = ''.join('        cp %d\n        jr z,.r%02X\n' % (16 + v // 8, v) for v in reloc)
    kinds += ''.join('        jr .rx%02X\n.r%02X:   pop af\n        jp R_%02X\n.rx%02X:\n' % (v, v, v, v) for v in reloc)
    rt_text = RUNTIME.replace(';RSTKINDS', kinds)
    syms = {'Z2G': tabs, 'G2Z': tabs + 256, 'HT': tabs + 512, 'REV': revt}
    for i in range(256): rom[revt + i] = int('{:08b}'.format(i)[::-1], 2)
    syms.update({'R_%02X' % v: 0x1000 for v in reloc})
    code, _ = assemble(rt_text, 0x0000, syms)
    rt = alloc.take(len(code) + 16)
    code, rsyms = assemble(rt_text, rt, syms)
    for v in reloc:
        lines, p = [], v
        for _ in range(48):
            if (0, p) not in an.insn: break
            qn, qm, qfl = an.insn[(0, p)]
            rd0 = an.reader_for(0, p)
            tt = tramp_text(an, 0, p, qn, rd0)
            if tt is None:
                ip = inplace(an, 0, p, qn, rd0)
                if ip == 'same': tt = 'db ' + ','.join('$%02X' % rd0(p + i) for i in range(qn))
                elif ip == 'mbc': tt = 'call mbc'
                else: tt = 'db ' + ','.join('$%02X' % x for x in ip)
            lines.append(tt)
            p += qn
            if not qfl[0]:
                p = None
                break
        if p is not None:
            lines.append('jp $%04X' % p)
        body = '\n'.join(lines)
        b0, _ = assemble(body, 0, rsyms)
        ra = alloc.take(len(b0))
        b0, _ = assemble(body, ra, rsyms)
        rom[ra:ra + len(b0)] = b0
        syms['R_%02X' % v] = ra
    code, rsyms = assemble(rt_text, rt, syms)
    rom[rt:rt + len(code)] = code
    report['runtime'] = {'at': '$%04X' % rt, 'bytes': len(code)}
    def put(addr, text):
        b, _ = assemble(text, addr, rsyms)
        rom[addr:addr + len(b)] = b
    if ram_mode:
        put(0x00, 'ld a,2\nld ($FFFE),a\njp $4000')
    else:
        put(0x00, 'di\njp boot')
    put(LDHW, 'jp ldhw'); put(LDHR, 'jp ldhr'); put(PUSHAF, 'jp pushaf'); put(POPAF, 'jp popaf')
    put(HALTV, 'jp haltb'); put(OP1, 'jp op1'); put(0x38, 'jp irq'); put(0x66, 'retn')
    # sites
    stats = {'inplace': 0, 'tramp': 0, 'failed': []}
    done = set()
    for (bank, pc) in sorted(an.insn, key=lambda k: (k[0], k[1])):
        if (bank, pc) in done: continue
        n, m, fl = an.insn[(bank, pc)]
        rd = an.reader_for(bank, pc)
        r = inplace(an, bank, pc, n, rd)
        if (rd(pc) & 0xC7) == 0xC7 and (rd(pc) & 0x38) in reloc:
            op1_sites[(bank, pc)] = 16 + (rd(pc) & 0x38) // 8; rom[an.file_off(bank, pc)] = 0xC7 | OP1; continue
        if rd(pc) == 0xD9:
            op1_sites[(bank, pc)] = 7; rom[an.file_off(bank, pc)] = 0xC7 | OP1; continue
        if r == 'same': continue
        o = an.file_off(bank, pc)
        if r == 'mbc':
            b, _ = assemble('call mbc', 0, rsyms); rom[o:o + 3] = b; stats['inplace'] += 1; continue
        if r is not None:
            rom[o:o + len(r)] = bytes(r); stats['inplace'] += 1; continue
        # trampoline: cover this and the following instructions up to 3 bytes; none of the covered may be a label
        cov, size, p = [], 0, pc
        ok = True
        while size < 3:
            if (bank, p) not in an.insn or (p != pc and ((bank, p) in an.labels or (bank, p) in done)):
                ok = False; break
            nn = an.insn[(bank, p)][0]
            cov.append(p); size += nn; p += nn
            if not an.insn[(bank, cov[-1])][2][0] and size < 3:
                ok = False; break
        if not ok:
            op = rd(pc)
            if op in (0xF5, 0xF1):
                rom[o] = 0xC7 | (PUSHAF if op == 0xF5 else POPAF); stats['rst_af'] = stats.get('rst_af', 0) + 1
            elif op == 0xEA:
                rom[o:o + 3] = bytes(inplace(an, bank, pc, n, rd, True)); stats['vram_unmarked'] = stats.get('vram_unmarked', 0) + 1
            elif op in KINDS and n == 1:
                op1_sites[(bank, pc)] = KINDS[op]; rom[o] = 0xC7 | OP1; stats['op1'] = stats.get('op1', 0) + 1
            else:
                stats['failed'].append('%s $%04X %s' % ('ram' if bank < 0 else bank, pc, m))
            continue
        lines = []
        for q in cov:
            qn, qm, qfl = an.insn[(bank, q)]
            t = tramp_text(an, bank, q, qn, rd)
            if t is None:
                ip = inplace(an, bank, q, qn, rd)
                if ip == 'same':
                    t = 'db ' + ','.join('$%02X' % rd(q + i) for i in range(qn))
                elif ip == 'mbc':
                    t = 'call mbc'
                else:
                    t = 'db ' + ','.join('$%02X' % v for v in ip)
            lines.append(t)
        last = an.insn[(bank, cov[-1])]
        if last[2][0]:
            lines.append('jp $%04X' % p)
        text = '\n'.join(lines)
        tb, _ = assemble(text, 0, rsyms)
        ta = alloc.take(len(tb))
        tb, _ = assemble(text, ta, rsyms)
        rom[ta:ta + len(tb)] = tb
        jp = bytes([0xC3, ta & 0xFF, ta >> 8])
        rom[o:o + 3] = jp
        for i in range(3, size): rom[o + i] = 0x00
        for q in cov: done.add((bank, q))
        stats['tramp'] += 1
    # the op1 page tables: CPU page -> a 256-byte page of kinds (RAM sites by their RAM address; ROM banks 1+
    # share the $4000-$7FFF pages: a clash between banks is reported)
    ht = tabs + 512
    for i in range(768): rom[ht + i] = 0
    filled = set()
    for (bank, pc), k in sorted(op1_sites.items()):
        h = ((pc & 0xFF) ^ (pc >> 8)) & 0xFF
        while h in filled:
            if rom[ht + h] == pc & 0xFF and rom[ht + 256 + h] == pc >> 8: break
            h = (h + 1) & 0xFF
        if h in filled and rom[ht + 512 + h] != k: stats['failed'].append('op1 clash $%04X' % pc)
        filled.add(h)
        rom[ht + h] = pc & 0xFF; rom[ht + 256 + h] = pc >> 8; rom[ht + 512 + h] = k
    if len(filled) > 250: raise SystemExit('gb2gg: more than 250 op1 sites')
    report['sites'] = stats
    report['free_left'] = sum(h - l for l, h in alloc.runs)
    if ram_mode:
        boot2, _ = assemble('di\nld a,$08\nld ($FFFC),a\nld hl,$4020\nld de,$A000\nld bc,$1C00\nldir\n'
                            'jp boot', 0x4000, rsyms)
        bank2 = bytearray(b'\xFF' * 0x4000)
        bank2[0:len(boot2)] = boot2
        bank2[0x20:0x20 + 0x1C00] = rom[0xA000:0xBC00]
        rom = bytearray(rom[0:0x8000]) + bank2 + bytearray(b'\xFF' * 0x4000)
    # GG header (BrokenNes does not need it; real units with a BIOS do): TMR SEGA, region GG export, 32KB
    if all(v in (0, 0xFF) for v in rom[0x7FF0:0x8000]):
        rom[0x7FF0:0x7FF8] = b'TMR SEGA'
        rom[0x7FF8:0x7FFA] = b'\0\0'
        s = sum(rom[0:0x7FF0]) & 0xFFFF
        rom[0x7FFA] = s & 0xFF; rom[0x7FFB] = s >> 8
        rom[0x7FFC:0x7FFF] = b'\0\0\0'
        rom[0x7FFF] = 0x6C
        report['header'] = 'TMR SEGA, GG export, checksum $%04X' % s
    else:
        report['header'] = '$7FF0 used by the game: no header'
    return bytes(rom), rsyms


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest='cmd', required=True)
    for name in ('convert', 'feedback'):
        p = sub.add_parser(name); p.add_argument('inp'); p.add_argument('out')
        p.add_argument('--pcs'); p.add_argument('--report'); p.add_argument('--frames', type=int, default=1800)
        p.add_argument('--input', default=''); p.add_argument('--workshop'); p.add_argument('--profile')
        p.add_argument('--set', action='append', default=[]); p.add_argument('--count', action='store_true')
    a = ap.parse_args()
    data = open(a.inp, 'rb').read()
    report = {'pipeline': 'gb2gg', 'in': a.inp, 'out': a.out}
    pcs = []
    if a.cmd == 'feedback':
        pcs_path = os.path.splitext(a.out)[0] + '.pcs'
        pcs, out = sm83.trace(a.inp, pcs_path, a.frames, a.input, a.workshop)
        report['feedback'] = {'pcs': len(pcs), 'run': out.strip().splitlines()[:3]}
    elif a.pcs:
        pcs = sm83.read_pcs(a.pcs)
    rom, syms = convert(data, pcs, report, a.count)
    open(a.out, 'wb').write(rom)
    report['symbols'] = {k: '$%04X' % v for k, v in syms.items() if k in ('boot', 'irq', 'sync', 'io_read', 'io_write', 'haltb')}
    if a.report:
        json.dump(report, open(a.report, 'w'), indent=1)
    print(json.dumps({k: report[k] for k in ('analysis', 'overlays', 'vector_clashes', 'runtime', 'sites', 'header') if k in report}, indent=1))


if __name__ == '__main__':
    main()
