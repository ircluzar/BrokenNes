"""The GG -> SMS runtime: the Z80 code the converted ROM calls instead of the Game Gear-only port accesses.

Handlers (all keep every register and flag the original instruction kept, unless the original changed them):
  h_be      OUT ($BE),A   VRAM writes go straight out; while the game has colour RAM selected, GG byte pairs (RGB444) become one SMS byte (RGB222)
  h_bf      OUT ($BF),A   the control write goes out and is shadowed: a second byte with code 3 (CRAM) remembers the GG colour address
  h_p0      IN A,($00)    the GG Start/region port: Start from the SMS Pause button (NMI) and/or buttons 1+2, region bits from the settings
  oc_<r>    OUT (C),r     dispatch on C at run time (GG port: dropped, data/control: the handlers above, else the real OUT)
  h_outi/h_otir/h_outd/h_otdr   the block outputs, through the same dispatch
  h_inca    IN A,(C)      GG ports answered with the idle values, others read for real
  nmi       Pause -> Start (installed at $0066 when the game does not use that address)
  init      clears the runtime's RAM before the game's own reset code runs
"""
from z80 import Asm

VARS = ['V_MODE', 'V_ADDR', 'V_LO', 'V_PREV', 'V_START', 'V_TMP']
IDLE = [0xC0, 0x7F, 0xFF, 0x00, 0xFF, 0x00, 0xFF]   # port $00 (overridden by h_p0) and the link / stereo ports' idle values


def build(settings, ram):
    """Assemble the runtime. ram: address of len(VARS) free RAM bytes. Returns the Asm (link it at its ROM address)."""
    v = {name: ram + i for i, name in enumerate(VARS)}
    a = Asm()
    LD_A_NN, LD_NN_A = 0x3A, 0x32
    base = 0xC0 if settings['region'] == 'export' else 0x80
    start = settings['start']

    # ---- h_be
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
    a.db(0xED, 0x57, 0xF5, 0xF3)                             # ld a,i / push af / di
    a.db(0x79, 0xCB, 0x3F, 0xD3, 0xBF)                       # ld a,c / srl a / out ($bf),a
    a.db(0x3E, 0xC0, 0xD3, 0xBF)                             # ld a,$c0 / out ($bf),a
    a.db(0x7B, 0xD3, 0xBE)                                   # ld a,e / out ($be),a
    a.db(0xF1)                                               # pop af (P/V = IFF2)
    a.op_w(0xE2, 'be_done')                                  # jp po: interrupts were off
    a.db(0xFB)                                               # ei
    a.label('be_done')
    a.db(0xE1, 0xD1, 0xC1, 0xF1, 0xC9)
    a.label('lut')                                           # a = 0..15 -> the 2-bit SMS level
    a.op_w(0x21, 'tab'); a.db(0x85, 0x6F, 0x30, 0x01, 0x24, 0x7E, 0xC9)   # ld hl,tab / add a,l / ld l,a / jr nc,+1 / inc h / ld a,(hl) / ret
    a.label('tab')
    a.db(bytes(settings['colour_table']))

    # ---- h_bf
    a.label('h_bf')
    a.db(0xD3, 0xBF, 0xF5, 0xE6, 0xC0, 0xFE, 0xC0); a.op_r(0x28, 'bf_c')
    a.db(0xAF); a.op_w(LD_NN_A, v['V_MODE']); a.db(0xF1); a.op_w(LD_NN_A, v['V_PREV']); a.db(0xC9)
    a.label('bf_c')
    a.op_w(LD_A_NN, v['V_PREV']); a.db(0xE6, 0x3F); a.op_w(LD_NN_A, v['V_ADDR'])
    a.db(0x3E, 0x01); a.op_w(LD_NN_A, v['V_MODE'])
    a.db(0xF1); a.op_w(LD_NN_A, v['V_PREV']); a.db(0xC9)

    # ---- h_p0
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

    # ---- nmi (Pause -> Start)
    a.label('nmi')
    a.db(0xF5, 0x3E, settings['start_hold'] & 0xFF); a.op_w(LD_NN_A, v['V_START']); a.db(0xF1, 0xED, 0x45)

    # ---- OUT (C),r dispatch
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
    # block outputs
    for name, step in (('h_outi', 0x23), ('h_outd', 0x2B)):
        a.label(name)
        a.db(0xF5, 0x7E); a.op_w(0xCD, 'oc_one'); a.db(step, 0xF1, 0x05, 0xC9)     # push af / ld a,(hl) / call / inc|dec hl / pop af / dec b
    for name, step in (('h_otir', 0x23), ('h_otdr', 0x2B)):
        a.label(name)
        a.db(0xF5)
        a.label(name + '_l')
        a.db(0x7E); a.op_w(0xCD, 'oc_one'); a.db(step); a.op_r(0x10, name + '_l')
        a.db(0xF1, 0x04, 0x05, 0xC9)                          # pop af / inc b / dec b (Z=1, N=1, as OTIR leaves them)
    # IN A,(C)
    a.label('h_inca')
    a.db(0x79, 0xFE, 0x07); a.op_r(0x30, 'inca_real')
    a.db(0xB7); a.op_w(0xCA, 'h_p0_and')                     # or a / jp z (port 0)
    a.db(0xE5); a.op_w(0x21, 'idle'); a.db(0x85, 0x6F, 0x30, 0x01, 0x24, 0x7E, 0xE1, 0xA7, 0xC9)
    a.label('inca_real'); a.db(0xED, 0x78, 0xC9)
    a.label('h_p0_and'); a.op_w(0xCD, 'h_p0'); a.db(0xA7, 0xC9)
    a.label('idle'); a.db(bytes(IDLE))

    # ---- init (from the reset trampoline)
    a.label('init')
    a.db(0xF5, 0xAF)
    for name in VARS:
        a.op_w(LD_NN_A, v[name])
    a.db(0xF1, 0xC9)
    return a


def colour_table(kind):
    """The GG 4-bit level -> SMS 2-bit level table."""
    if kind == 'truncate':
        return [x >> 2 for x in range(16)]
    if kind == 'round':
        return [min(3, int(x * 3 / 15 + 0.5)) for x in range(16)]
    if isinstance(kind, list) and len(kind) == 16:
        return [int(x) & 3 for x in kind]
    raise ValueError(f'colour table: round | truncate | a list of 16 levels (got {kind!r})')
