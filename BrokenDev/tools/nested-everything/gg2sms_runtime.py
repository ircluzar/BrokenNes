"""The GG -> SMS runtime: the Z80 code the converted ROM calls instead of the Game Gear-only port accesses.

Handlers (each keeps every register and flag the original instruction kept):
  h_be      OUT ($BE),A   VRAM writes go straight out; while the game has colour RAM selected, GG byte pairs (RGB444) become one SMS byte (RGB222)
  h_bf      OUT ($BF),A   the control write goes out and is shadowed: a second byte with code 3 (CRAM) remembers the GG colour address
  h_p0      IN A,($00)    the GG Start/region port: Start from the SMS Pause button (NMI) and/or buttons 1+2, region bits from the settings
  oc_<r>    OUT (C),r     dispatch on C at run time (GG port: dropped, data/control: the handlers above, else the real OUT)
  h_outi/h_otir/h_outd/h_otdr   the block outputs, through the same dispatch
  h_inca    IN A,(C)      GG ports answered with the idle values, others read for real
  nmi       Pause -> Start (installed at $0066 when the game does not use that address)
  init      clears the runtime's RAM (and, far mode, sets the mapper as a BIOS would) before the game's own reset code runs
  disp      'RST v / CALL disp' followed by one byte (the handler's index): how sites reach the handlers in place (2 bytes for 2)

Two layouts:
  near  everything in one block of always-mapped ROM (bank 0, or bank 1 when the game never maps another bank there)
  far   a small stub in always-mapped ROM; the handlers in a bank of their own that the stub maps into slot 2 for the call
        (interrupts off meanwhile; the game's bank comes back from the $FFFF RAM mirror); block outputs read the game's bytes
        through the stub's rdhl with the game's bank mapped.
"""
from z80 import Asm

VARS = ['V_MODE', 'V_ADDR', 'V_LO', 'V_PREV', 'V_START', 'V_TMP', 'V_ARG', 'V_BANK', 'V_IFF']
IDLE = [0xC0, 0x7F, 0xFF, 0x00, 0xFF, 0x00, 0xFF]   # port $00 (overridden by h_p0) and the link / stereo ports' idle values
HANDLERS = ['h_be', 'h_bf', 'h_p0', 'oc_a', 'oc_b', 'oc_c', 'oc_d', 'oc_e', 'oc_h', 'oc_l', 'oc_0', 'h_outi', 'h_otir', 'h_outd', 'h_otdr', 'h_inca']
LD_A_NN, LD_NN_A = 0x3A, 0x32


def _vars(ram):
    return {name: ram + i for i, name in enumerate(VARS)}


def handlers(a, settings, v, far):
    """The handler bodies into Asm a. far: block outputs read (HL) through the stub's rdhl."""
    base = 0xC0 if settings['region'] == 'export' else 0x80
    start = settings['start']

    a.label('h_be')
    a.db(0xF5); a.op_w(LD_A_NN, v['V_MODE']); a.db(0xB7); a.op_r(0x20, 'be_cram')
    a.db(0xF1, 0xD3, 0xBE, 0xC9)
    a.label('be_cram')
    a.db(0xF1, 0xF5, 0xC5, 0xD5, 0xE5)                       # pop af / push af,bc,de,hl
    a.db(0x47)                                               # ld b,a
    a.op_w(LD_A_NN, v['V_ADDR']); a.db(0x4F)                 # ld c,a
    a.db(0x3C, 0xE6, 0x3F); a.op_w(LD_NN_A, v['V_ADDR'])     # inc a / and $3F
    a.db(0xCB, 0x41); a.op_r(0x20, 'be_odd')                 # bit 0,c / jr nz
    a.db(0x78); a.op_w(LD_NN_A, v['V_LO']); a.op_r(0x18, 'be_done')
    a.label('be_odd')
    a.op_w(LD_A_NN, v['V_LO']); a.db(0x57)                   # d = GGGGRRRR
    a.db(0x78, 0xE6, 0x0F); a.op_w(0xCD, 'lut'); a.db(0x07, 0x07, 0x07, 0x07, 0x5F)          # e = blue << 4
    a.db(0x7A, 0x0F, 0x0F, 0x0F, 0x0F, 0xE6, 0x0F); a.op_w(0xCD, 'lut'); a.db(0x07, 0x07, 0xB3, 0x5F)  # e |= green << 2
    a.db(0x7A, 0xE6, 0x0F); a.op_w(0xCD, 'lut'); a.db(0xB3, 0x5F)                            # e |= red
    if not far:
        a.db(0xED, 0x57, 0xF5, 0xF3)                         # ld a,i / push af / di   (far: the stub already has them off)
    a.db(0x79, 0xCB, 0x3F, 0xD3, 0xBF)                       # ld a,c / srl a / out ($bf),a
    a.db(0x3E, 0xC0, 0xD3, 0xBF)                             # ld a,$c0 / out ($bf),a
    a.db(0x7B, 0xD3, 0xBE)                                   # ld a,e / out ($be),a
    if not far:
        a.db(0xF1); a.op_w(0xE2, 'be_done'); a.db(0xFB)      # pop af / jp po (were off) / ei
    a.label('be_done')
    a.db(0xE1, 0xD1, 0xC1, 0xF1, 0xC9)
    a.label('lut')                                           # a = 0..15 -> the 2-bit SMS level
    a.op_w(0x21, 'tab'); a.db(0x85, 0x6F, 0x30, 0x01, 0x24, 0x7E, 0xC9)
    a.label('tab')
    a.db(bytes(settings['colour_table']))

    a.label('h_bf')
    a.db(0xD3, 0xBF, 0xF5, 0xE6, 0xC0, 0xFE, 0xC0); a.op_r(0x28, 'bf_c')
    a.db(0xAF); a.op_w(LD_NN_A, v['V_MODE']); a.db(0xF1); a.op_w(LD_NN_A, v['V_PREV']); a.db(0xC9)
    a.label('bf_c')
    a.op_w(LD_A_NN, v['V_PREV']); a.db(0xE6, 0x3F); a.op_w(LD_NN_A, v['V_ADDR'])
    a.db(0x3E, 0x01); a.op_w(LD_NN_A, v['V_MODE'])
    a.db(0xF1); a.op_w(LD_NN_A, v['V_PREV']); a.db(0xC9)

    a.label('h_p0')
    a.db(0xF5)
    if start in ('pause', 'both'):
        a.op_w(LD_A_NN, v['V_START']); a.db(0xB7); a.op_r(0x28, 'p0_up')
        a.db(0x3D); a.op_w(LD_NN_A, v['V_START']); a.db(0x3E, base & 0x7F); a.op_r(0x18, 'p0_out')
    a.label('p0_up')
    if start in ('buttons12', 'both'):
        a.db(0xDB, 0xDC, 0xE6, 0x30); a.op_r(0x20, 'p0_rel'); a.db(0x3E, base & 0x7F); a.op_r(0x18, 'p0_out')
    a.label('p0_rel')
    a.db(0x3E, base)
    a.label('p0_out')
    a.op_w(LD_NN_A, v['V_TMP']); a.db(0xF1); a.op_w(LD_A_NN, v['V_TMP']); a.db(0xC9)

    a.label('oc_one')                                        # A = value, C = port
    a.db(0xF5, 0x79, 0xFE, 0x07); a.op_r(0x38, 'one_drop')
    a.db(0xE6, 0xC1, 0xFE, 0x80); a.op_r(0x28, 'one_data')
    a.db(0xFE, 0x81); a.op_r(0x28, 'one_ctl')
    a.db(0xF1, 0xED, 0x79, 0xC9)
    a.label('one_drop'); a.db(0xF1, 0xC9)
    a.label('one_data'); a.db(0xF1); a.op_w(0xC3, 'h_be')
    a.label('one_ctl'); a.db(0xF1); a.op_w(0xC3, 'h_bf')
    for r, ld in (('a', None), ('b', 0x78), ('c', 0x79), ('d', 0x7A), ('e', 0x7B), ('h', 0x7C), ('l', 0x7D), ('0', 0xAF)):
        a.label('oc_' + r)
        a.db(0xF5)
        if ld is not None:
            a.db(ld)
        a.op_w(0xCD, 'oc_one'); a.db(0xF1, 0xC9)

    def rd():
        if far:
            a.op_w(0xCD, 'rdhl')
        else:
            a.db(0x7E)
    for name, step in (('h_outi', 0x23), ('h_outd', 0x2B)):
        a.label(name)
        a.db(0xF5); rd(); a.op_w(0xCD, 'oc_one'); a.db(step, 0xF1, 0x05, 0xC9)
    for name, step in (('h_otir', 0x23), ('h_otdr', 0x2B)):
        a.label(name)
        a.db(0xF5)
        a.label(name + '_l')
        rd(); a.op_w(0xCD, 'oc_one'); a.db(step); a.op_r(0x10, name + '_l')
        a.db(0xF1, 0x04, 0x05, 0xC9)                          # pop af / inc b / dec b (Z=1, N=1, as OTIR leaves them)
    a.label('h_inca')
    a.db(0x79, 0xFE, 0x07); a.op_r(0x30, 'inca_real')
    a.db(0xB7); a.op_w(0xCA, 'h_p0_and')
    a.db(0xE5); a.op_w(0x21, 'idle'); a.db(0x85, 0x6F, 0x30, 0x01, 0x24, 0x7E, 0xE1, 0xA7, 0xC9)
    a.label('inca_real'); a.db(0xED, 0x78, 0xC9)
    a.label('h_p0_and'); a.op_w(0xCD, 'h_p0'); a.db(0xA7, 0xC9)
    a.label('idle'); a.db(bytes(IDLE))

    # the jump table and the indexed dispatch: V_ARG holds the handler index; HL and AF as the game left them, stack [return]
    a.label('jtab')
    for h in HANDLERS:
        a.dw_ref(h)
    a.label('go')
    a.db(0xE5, 0xF5)                                         # push hl / push af
    a.op_w(LD_A_NN, v['V_ARG']); a.db(0x87)                  # ld a,(V_ARG) / add a,a
    a.op_w(0x21, 'jtab'); a.db(0x85, 0x6F, 0x30, 0x01, 0x24) # hl = jtab + 2*index
    a.db(0x7E, 0x23, 0x66, 0x6F)                             # ld a,(hl) / inc hl / ld h,(hl) / ld l,a
    a.db(0xF1, 0xE3, 0xC9)                                   # pop af / ex (sp),hl / ret  -> the handler, which returns to [return]


def build(settings, ram, far_bank=None):
    """Assemble the runtime. Returns {'fixed': Asm, 'far': Asm|None}; labels: disp (the RST / CALL target), nmi, init, and in near mode
    every handler. Link the fixed part with extern = the far part's addresses and the other way round."""
    v = _vars(ram)
    fixed = Asm()
    far = Asm() if far_bank is not None else None
    # disp: stack [ret -> index byte]; read the index (in the game's bank), step the return address over it
    fixed.label('disp')
    fixed.db(0xE3, 0xF5, 0x7E, 0x23); fixed.op_w(LD_NN_A, v['V_ARG']); fixed.db(0xF1, 0xE3)   # ex (sp),hl / push af / ld a,(hl) / inc hl / ld (V_ARG),a / pop af / ex (sp),hl
    if far is None:
        fixed.op_w(0xC3, 'go')
        handlers(fixed, settings, v, False)
    else:
        fixed.db(0xF5, 0xED, 0x57, 0x3E, 0x00); fixed.op_w(0xE2, 'd_off'); fixed.db(0x3C)   # push af / ld a,i / ld a,0 / jp po / inc a
        fixed.label('d_off')
        fixed.op_w(LD_NN_A, v['V_IFF']); fixed.db(0xF3)                                    # ld (V_IFF),a / di
        fixed.op_w(LD_A_NN, 0xFFFF); fixed.op_w(LD_NN_A, v['V_BANK'])                      # the game's slot-2 bank (RAM mirror of the mapper)
        fixed.db(0x3E, far_bank); fixed.op_w(LD_NN_A, 0xFFFF)
        fixed.db(0xF1); fixed.op_w(0xCD, 'go')                                             # pop af / call go (far)
        fixed.db(0xF5); fixed.op_w(LD_A_NN, v['V_BANK']); fixed.op_w(LD_NN_A, 0xFFFF)
        fixed.op_w(LD_A_NN, v['V_IFF']); fixed.db(0xB7, 0x28, 0x01, 0xFB, 0xF1, 0xC9)      # or a / jr z,+1 / ei / pop af / ret
        fixed.label('rdhl')                                                                # A = (HL) with the game's bank in slot 2
        fixed.db(0xC5); fixed.op_w(LD_A_NN, v['V_BANK']); fixed.op_w(LD_NN_A, 0xFFFF)
        fixed.db(0x46, 0x3E, far_bank); fixed.op_w(LD_NN_A, 0xFFFF); fixed.db(0x78, 0xC1, 0xC9)
        handlers(far, settings, v, True)
    fixed.label('nmi')
    fixed.db(0xF5, 0x3E, settings['start_hold'] & 0xFF); fixed.op_w(LD_NN_A, v['V_START']); fixed.db(0xF1, 0xED, 0x45)
    fixed.label('init')
    fixed.db(0xF5, 0xAF)
    for name in VARS:
        fixed.op_w(LD_NN_A, v[name])
    if far is not None:                                       # the mapper as the BIOS leaves it, so the $FFFF mirror is right from the start
        fixed.op_w(LD_NN_A, 0xFFFC); fixed.op_w(LD_NN_A, 0xFFFD); fixed.db(0x3C); fixed.op_w(LD_NN_A, 0xFFFE); fixed.db(0x3C); fixed.op_w(LD_NN_A, 0xFFFF)
    fixed.db(0xF1, 0xC9)
    return {'fixed': fixed, 'far': far}


def colour_table(kind):
    """The GG 4-bit level -> SMS 2-bit level table."""
    if kind == 'truncate':
        return [x >> 2 for x in range(16)]
    if kind == 'round':
        return [min(3, int(x * 3 / 15 + 0.5)) for x in range(16)]
    if isinstance(kind, list) and len(kind) == 16:
        return [int(x) & 3 for x in kind]
    raise ValueError(f'colour table: round | truncate | a list of 16 levels (got {kind!r})')
