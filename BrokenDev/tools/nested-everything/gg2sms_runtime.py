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

VARS = ['V_MODE', 'V_ADDR', 'V_LO', 'V_PREV', 'V_START', 'V_TMP', 'V_ARG', 'V_BANK', 'V_IFF', 'V_CNT', 'V_VC', 'V_LATCH', 'V_IE']


def vars_list(settings):
    """The runtime's RAM: 11 bytes ($DFF0-$DFFA fits below the mapper registers), 13 with the shadowed control latch."""
    return VARS if settings.get('latch', 'heuristic') != 'heuristic' else VARS[:11]
CHAIN = 18                 # index bytes CHAIN+n: the rest of an unrolled OUTI chain, n OUTIs, in one call (h_chain)
CHAIN_MAX = 120
IDLE = [0xC0, 0x7F, 0xFF, 0x00, 0xFF, 0x00, 0xFF]   # port $00 (overridden by h_p0) and the link / stereo ports' idle values
HANDLERS = ['h_be', 'h_bf', 'h_p0', 'oc_a', 'oc_b', 'oc_c', 'oc_d', 'oc_e', 'oc_h', 'oc_l', 'oc_0', 'h_outi', 'h_otir', 'h_outd', 'h_otdr', 'h_inca', 'h_sr', 'h_oidj']
LD_A_NN, LD_NN_A = 0x3A, 0x32
FAR_TABLE = 0xA000         # far layout: GG 12-bit colour -> SMS byte, 4 KB at this address of the runtime's bank (page | blue, GGGGRRRR)
BLOCKS = ('h_outi', 'h_otir', 'h_outd', 'h_otdr', 'h_oidj')


def _vars(ram):
    return {name: ram + i for i, name in enumerate(VARS)}       # (V_LATCH / V_IE only exist, and are only used, with latch pairs/status)


def handlers(a, settings, v, far, used=None, deep=None):
    """The handler bodies into Asm a. far: block outputs read (HL) through the stub's rdhl. used: the handler names the sites
    call (None = all); the others are left out and their jump-table entries point at h_be (no site uses that index)."""
    used = set(HANDLERS) if used is None else set(used) | {'h_be', 'h_bf'}
    if 'h_inca' in used:
        used.add('h_p0')
    deep = far if deep is None else deep
    need_one = any(h.startswith('oc_') or h in BLOCKS or h == 'h_chain' for h in used)
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
    if far:                                                  # far: one look-up in the 4 KB table of the runtime's bank (FAR_TABLE)
        a.op_w(LD_A_NN, v['V_LO']); a.db(0x6F)               # l = GGGGRRRR
        a.db(0x78, 0xE6, 0x0F, 0xF6, FAR_TABLE >> 8, 0x67, 0x5E)   # h = table page | blue / ld e,(hl)
    else:
        a.op_w(LD_A_NN, v['V_LO']); a.db(0x57)                   # d = GGGGRRRR
        a.db(0x78, 0xE6, 0x0F); a.op_w(0xCD, 'lut'); a.db(0x07, 0x07, 0x07, 0x07, 0x5F)          # e = blue << 4
        a.db(0x7A, 0x0F, 0x0F, 0x0F, 0x0F, 0xE6, 0x0F); a.op_w(0xCD, 'lut'); a.db(0x07, 0x07, 0xB3, 0x5F)  # e |= green << 2
        a.db(0x7A, 0xE6, 0x0F); a.op_w(0xCD, 'lut'); a.db(0xB3, 0x5F)                            # e |= red
    if not far:                                              # (far = interrupts already off: every way in turned them off)
        a.db(0xED, 0x57, 0xF5, 0xF3)                         # ld a,i / push af / di   (far: the stub already has them off)
    a.db(0x79, 0xCB, 0x3F, 0xD3, 0xBF)                       # ld a,c / srl a / out ($bf),a
    a.db(0x3E, 0xC0, 0xD3, 0xBF)                             # ld a,$c0 / out ($bf),a
    a.db(0x7B, 0xD3, 0xBE)                                   # ld a,e / out ($be),a
    if not far:                                              # (far = interrupts already off: every way in turned them off)
        a.db(0xF1); a.op_w(0xE2, 'be_done'); a.db(0xFB)      # pop af / jp po (were off) / ei
    a.label('be_done')
    a.db(0xE1, 0xD1, 0xC1, 0xF1, 0xC9)
    a.label('lut')                                           # a = 0..15 -> the 2-bit SMS level
    a.op_w(0x21, 'tab'); a.db(0x85, 0x6F, 0x30, 0x01, 0x24, 0x7E, 0xC9)
    a.label('tab')
    a.db(bytes(settings['colour_table']))

    if settings.get('latch', 'heuristic') in ('pairs', 'status'):
        a.label('h_bf')                                      # the VDP's two-byte control latch, shadowed (interrupts off meanwhile)
        a.db(0xF5, 0xED, 0x57, 0xF3, 0x3E, 0x00); a.op_w(0xE2, 'bf_po'); a.db(0x3C)
        a.label('bf_po'); a.op_w(LD_NN_A, v['V_IE']); a.db(0xF1, 0xD3, 0xBF, 0xF5)
        a.op_w(LD_A_NN, v['V_LATCH']); a.db(0xEE, 0x01); a.op_w(LD_NN_A, v['V_LATCH']); a.op_r(0x20, 'bf_1st')
        a.db(0xF1, 0xF5, 0xE6, 0xC0, 0xFE, 0xC0); a.op_r(0x28, 'bf_c')
        a.db(0xAF); a.op_w(LD_NN_A, v['V_MODE']); a.op_r(0x18, 'bf_end')
        a.label('bf_1st'); a.db(0xF1, 0xF5); a.op_w(LD_NN_A, v['V_PREV']); a.op_r(0x18, 'bf_end')
        a.label('bf_c')
        a.op_w(LD_A_NN, v['V_PREV']); a.db(0xE6, 0x3F); a.op_w(LD_NN_A, v['V_ADDR'])
        a.db(0x3E, 0x01); a.op_w(LD_NN_A, v['V_MODE'])
        a.label('bf_end')
        a.op_w(LD_A_NN, v['V_IE']); a.db(0xB7, 0x28, 0x01, 0xFB, 0xF1, 0xC9)
    else:                                                    # heuristic: any byte with both top bits set selects colour RAM (quick)
        a.label('h_bf')
        a.db(0xD3, 0xBF, 0xF5, 0xE6, 0xC0, 0xFE, 0xC0); a.op_r(0x28, 'bf_c')
        a.db(0xAF); a.op_w(LD_NN_A, v['V_MODE']); a.db(0xF1); a.op_w(LD_NN_A, v['V_PREV']); a.db(0xC9)
        a.label('bf_c')
        a.op_w(LD_A_NN, v['V_PREV']); a.db(0xE6, 0x3F); a.op_w(LD_NN_A, v['V_ADDR'])
        a.db(0x3E, 0x01); a.op_w(LD_NN_A, v['V_MODE'])
        a.db(0xF1); a.op_w(LD_NN_A, v['V_PREV']); a.db(0xC9)
    if 'h_sr' in used:
        a.label('h_sr')                                      # IN A,($BF): the status read resets the latch too (IN leaves the flags)
        a.db(0xDB, 0xBF, 0xF5, 0xAF); a.op_w(LD_NN_A, v['V_LATCH']); a.db(0xF1, 0xC9)

    if 'h_p0' in used:
        p0(a, settings, v, base, start)
    if need_one:
        oc_block(a, v, far, used, deep)
    if 'h_inca' in used:
        inca(a)
    jtab_go(a, v, used)


def p0(a, settings, v, base, start):
    a.label('h_p0')
    a.db(0xF5)
    if start in ('pause', 'both'):
        # Pause held Start for start_hold frames (counted by the VDP line counter going back), not reads: a game that polls
        # port $00 in a loop would use up a read count within one frame
        a.op_w(LD_A_NN, v['V_START']); a.db(0xB7); a.op_r(0x28, 'p0_up')
        a.db(0xC5, 0xDB, 0x7E, 0x47); a.op_w(LD_A_NN, v['V_VC']); a.db(0x4F, 0x78); a.op_w(LD_NN_A, v['V_VC'])   # b = line, c = last
        a.db(0x79, 0xB8); a.op_r(0x38, 'p0_same')                                # ld a,c / cp b / jr c: a later line, same frame
        a.op_w(LD_A_NN, v['V_START']); a.db(0x3D); a.op_w(LD_NN_A, v['V_START'])
        a.label('p0_same'); a.db(0xC1, 0x3E, base & 0x7F); a.op_r(0x18, 'p0_out')
    a.label('p0_up')
    if start in ('buttons12', 'both'):
        a.db(0xDB, 0xDC, 0xE6, 0x30); a.op_r(0x20, 'p0_rel'); a.db(0x3E, base & 0x7F); a.op_r(0x18, 'p0_out')
    a.label('p0_rel')
    a.db(0x3E, base)
    a.label('p0_out')
    a.op_w(LD_NN_A, v['V_TMP']); a.db(0xF1); a.op_w(LD_A_NN, v['V_TMP']); a.db(0xC9)



def oc_block(a, v, far, used, deep=False):
    if 'h_chain' in used:
        chain(a, v, far, deep)
    a.label('oc_one')                                        # A = value, C = port
    a.db(0xF5, 0x79, 0xFE, 0x07); a.op_r(0x38, 'one_drop')
    a.db(0xE6, 0xC1, 0xFE, 0x80); a.op_r(0x28, 'one_data')
    a.db(0xFE, 0x81); a.op_r(0x28, 'one_ctl')
    a.db(0xF1, 0xED, 0x79, 0xC9)
    a.label('one_drop'); a.db(0xF1, 0xC9)
    a.label('one_data'); a.db(0xF1); a.op_w(0xC3, 'h_be')
    a.label('one_ctl'); a.db(0xF1); a.op_w(0xC3, 'h_bf')
    for r, ld in (('a', None), ('b', 0x78), ('c', 0x79), ('d', 0x7A), ('e', 0x7B), ('h', 0x7C), ('l', 0x7D), ('0', 0xAF)):
        if 'oc_' + r not in used:
            continue
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
        if name not in used:
            continue
        a.label(name)
        a.db(0xF5); rd(); a.op_w(0xCD, 'oc_one'); a.db(step, 0xF1, 0x05, 0xC9)
    if 'h_oidj' in used:                                     # OUTI / DJNZ back to it: the whole loop in one call (B counted twice a turn)
        a.label('h_oidj')
        a.db(0xF5)
        a.label('h_oidj_l')
        rd(); a.op_w(0xCD, 'oc_one'); a.db(0x23, 0x05, 0x05); a.op_r(0x20, 'h_oidj_l')
        a.db(0xF1, 0xC9)
    for name, step in (('h_otir', 0x23), ('h_otdr', 0x2B)):
        if name not in used:
            continue
        a.label(name)
        a.db(0xF5)
        a.label(name + '_l')
        rd(); a.op_w(0xCD, 'oc_one'); a.db(step); a.op_r(0x10, name + '_l')
        a.db(0xF1, 0x04, 0x05, 0xC9)                          # pop af / inc b / dec b (Z=1, N=1, as OTIR leaves them)


def chain(a, v, far, deep):
    # h_chain: the site is the first of n = V_ARG - CHAIN OUTIs left in an unrolled chain: do them all, return past the last one
    a.label('h_chain')
    # the site's return address sits under d_go's when the way in was safe (deep)
    a.db(0xE5, 0xF5, 0x21, 4 + (2 if deep else 0), 0x00, 0x39)   # push hl / push af / ld hl,n / add hl,sp  -> the return address
    a.op_w(LD_A_NN, v['V_ARG']); a.db(0xD6, CHAIN + 1, 0x87)      # a = 2 * (n - 1)
    a.db(0x86, 0x77, 0x23); a.op_r(0x30, 'ch_nc'); a.db(0x34)     # add a,(hl) / ld (hl),a / inc hl / jr nc / inc (hl)
    a.label('ch_nc')
    a.db(0xF1, 0xE1, 0xF5)                                        # pop af / pop hl / push af
    a.op_w(LD_A_NN, v['V_ARG']); a.db(0xD6, CHAIN); a.op_w(LD_NN_A, v['V_CNT'])
    a.op_w(LD_A_NN, v['V_MODE']); a.db(0xB7); a.op_r(0x20, 'ch_l')   # colour RAM: byte by byte through the dispatch
    a.db(0x79, 0xE6, 0xC1, 0xFE, 0x80); a.op_r(0x20, 'ch_l')         # not the data port: the same
    if far:
        a.db(0x7C, 0xFE, 0x80); a.op_r(0x30, 'ch_l')                 # (far: the game's slot-2 bytes are not mapped here)
    a.op_w(LD_A_NN, v['V_CNT'])
    a.label('ch_f')                                                  # VRAM: the real OUTIs, at full speed
    a.db(0xED, 0xA3, 0x3D); a.op_r(0x20, 'ch_f')
    a.op_r(0x18, 'ch_end')
    a.label('ch_l')
    if far:
        a.op_w(0xCD, 'rdhl')
    else:
        a.db(0x7E)
    a.op_w(0xCD, 'oc_one'); a.db(0x23, 0x05)                 # inc hl / dec b
    a.op_w(LD_A_NN, v['V_CNT']); a.db(0x3D); a.op_w(LD_NN_A, v['V_CNT']); a.op_r(0x20, 'ch_l')
    a.label('ch_end')
    a.db(0xF1, 0x04, 0x05, 0xC9)                             # pop af / inc b / dec b (Z, N as the last OUTI) / ret


def inca(a):
    a.label('h_inca')
    a.db(0x79, 0xFE, 0x07); a.op_r(0x30, 'inca_real')
    a.db(0xB7); a.op_w(0xCA, 'h_p0_and')
    a.db(0xE5); a.op_w(0x21, 'idle'); a.db(0x85, 0x6F, 0x30, 0x01, 0x24, 0x7E, 0xE1, 0xA7, 0xC9)
    a.label('inca_real'); a.db(0xED, 0x78, 0xC9)
    a.label('h_p0_and'); a.op_w(0xCD, 'h_p0'); a.db(0xA7, 0xC9)
    a.label('idle'); a.db(bytes(IDLE))



def jtab_go(a, v, used):
    # the jump table and the indexed dispatch: V_ARG holds the handler index; HL and AF as the game left them, stack [return]
    a.label('jtab')
    for h in HANDLERS:
        a.dw_ref(h if h in used else 'h_be')
    a.label('go')
    a.db(0xE5, 0xF5)                                         # push hl / push af
    a.op_w(LD_A_NN, v['V_ARG'])
    if 'h_chain' in used:
        a.db(0xFE, CHAIN); a.op_r(0x30, 'go_ch')
    a.db(0x87)                                               # add a,a
    a.op_w(0x21, 'jtab'); a.db(0x85, 0x6F, 0x30, 0x01, 0x24) # hl = jtab + 2*index
    a.db(0x7E, 0x23, 0x66, 0x6F)                             # ld a,(hl) / inc hl / ld h,(hl) / ld l,a
    a.db(0xF1, 0xE3, 0xC9)                                   # pop af / ex (sp),hl / ret  -> the handler, which returns to [return]
    if 'h_chain' in used:
        a.label('go_ch'); a.db(0xF1, 0xE1); a.op_w(0xC3, 'h_chain')


def lookup(a, v, far, table):
    """lk: Z set when HL (a return address) is one of the game's own calls of the hooked RST. table: [(start, length, page)]: the return
    addresses start, start+2, ... below start+length (an unrolled OUTI chain is one entry); page $FF = any bank, else compared with
    the game's slot-2 bank (addresses >= $8000) or its slot-1 bank. Keeps BC, DE, HL; A is lost."""
    assert 0 < len(table) < 256
    a.label('lk')
    a.db(0xC5, 0xD5); a.op_w(0x11, 'lk_t'); a.db(0x06, len(table))
    a.label('lk_l')
    a.db(0xC5, 0x1A, 0x4F, 0x13, 0x1A, 0x47, 0x13)            # push bc / bc = start
    a.db(0xE5, 0xB7, 0xED, 0x42); a.op_r(0x38, 'lk_m4')       # push hl / hl -= start / below: next
    a.db(0x1A, 0x4F, 0x13, 0x1A, 0x47, 0x13)                  # bc = length
    a.db(0xB7, 0xED, 0x42); a.op_r(0x30, 'lk_m2')             # past the end: next
    a.db(0x09, 0xCB, 0x45); a.op_r(0x20, 'lk_m2')             # odd: next
    a.db(0xE1, 0x1A, 0x3C); a.op_r(0x28, 'lk_hit')            # pop hl / page $FF: any bank
    a.db(0x3D, 0x4F, 0x7C, 0xFE, 0x80); a.op_r(0x38, 'lk_1')  # dec a / ld c,a / ld a,h / cp $80 / jr c
    a.op_w(LD_A_NN, v['V_BANK'] if far else 0xFFFF); a.op_r(0x18, 'lk_c')
    a.label('lk_1'); a.op_w(LD_A_NN, 0xFFFE)
    a.label('lk_c'); a.db(0xB9); a.op_r(0x28, 'lk_hit'); a.op_r(0x18, 'lk_n')
    a.label('lk_m4'); a.db(0x13, 0x13)
    a.label('lk_m2'); a.db(0xE1)
    a.label('lk_n'); a.db(0x13, 0xC1); a.op_r(0x10, 'lk_l')
    a.db(0xD1, 0xC1, 0x3E, 0x01, 0xB7, 0xC9)                  # NZ: not a site
    a.label('lk_hit'); a.db(0xC1, 0xD1, 0xC1, 0xAF, 0xC9)
    a.label('lk_t')
    for start, length, page in table:
        a.db(start & 0xFF, start >> 8, length & 0xFF, length >> 8, page)


def build(settings, ram, far_bank=None, used=None, hook=None, fast=True):
    """Assemble the runtime. Returns {'pieces': [Asm], 'far': Asm|None}: the pieces go in always-mapped ROM, each wherever it fits
    (relative jumps stay inside a piece), the far part in a bank of its own. Labels: disp (the RST target), nmi, init, hk (a hooked
    RST's entry) and the handlers; link every piece with extern = the addresses of all the others.
    used: the handlers the sites call (None = all). hook: {'emit': f(asm) writing the hooked vector's relocated first instructions and
    the jump back, 'table': [(lo, hi, page)] the sites' return addresses}: the RST vector stays the game's, the sites behind it are
    told apart by their return address."""
    v = _vars(ram)
    used = set(HANDLERS) if used is None else set(used)
    far = Asm() if far_bank is not None else None
    pieces = []
    # Every way in starts with interrupts off (the state remembered in V_IFF): the game's interrupt handler may reach a site itself, and
    # V_ARG / V_BANK must not change under a site in progress. d_go: stack [return], AF/HL the game's, V_ARG the index.
    def pro(a):
        a.op_w(0xCD, 'pro')                                                                    # interrupts off, their state in V_IFF
    safe = far is not None or hook is not None        # plain near: the old quick way in (a site cannot be reached between its two halves)
    d = Asm(); pieces.append(d)
    d.label('disp')
    # the hot case first: a data-port write (index 0) while VRAM is selected goes straight out; the first byte of a colour pair
    # is only remembered. Left out when the always-mapped room is too small (fast=False).
    if fast:
        d.db(0xE3, 0xF5, 0x7E, 0xB7); d.op_r(0x20, 'd_slow')             # ex (sp),hl / push af / ld a,(hl) / or a / jr nz
        d.op_w(LD_A_NN, v['V_MODE']); d.db(0xB7); d.op_r(0x20, 'd_cram')
        d.db(0xF1, 0x23, 0xE3, 0xD3, 0xBE, 0xC9)                          # pop af / inc hl / ex (sp),hl / out ($be),a / ret
        d.label('d_cram')
        d.op_w(LD_A_NN, v['V_ADDR']); d.db(0x0F); d.op_r(0x38, 'd_slow')  # rrca / jr c (odd: the full way)
        d.db(0x07, 0x3C, 0xE6, 0x3F); d.op_w(LD_NN_A, v['V_ADDR'])        # rlca / inc a / and $3f
        d.db(0xF1); d.op_w(LD_NN_A, v['V_LO']); d.db(0x23, 0xE3, 0xC9)    # pop af / ld (V_LO),a / inc hl / ex (sp),hl / ret
        d.label('d_slow')
        d.db(0xF1, 0xE3)                                                  # pop af / ex (sp),hl: as the site left them
    if safe:
        pro(d)
    d.db(0xE3, 0xF5, 0x7E, 0x23); d.op_w(LD_NN_A, v['V_ARG'])          # ex (sp),hl / push af / ld a,(hl) / inc hl / ld (V_ARG),a
    if far is not None:
        d.op_w(0xCD, 'enter')
    d.db(0xF1, 0xE3)                                                  # pop af / ex (sp),hl
    if not safe:
        d.op_w(0xC3, 'go')
    d.label('d_go')
    d.op_w(0xCD, 'go'); d.db(0xF5); d.op_w(0xCD, 'leave'); d.db(0xF1, 0xC9)
    e = Asm(); pieces.append(e)
    if far is not None:
        e.label('enter')                                              # the game's slot-2 bank saved, ours in (A lost)
        e.op_w(LD_A_NN, 0xFFFF); e.op_w(LD_NN_A, v['V_BANK']); e.db(0x3E, far_bank); e.op_w(LD_NN_A, 0xFFFF); e.db(0xC9)
    if safe:
        e.label('pro')                                                # push af / ld a,i / di / ld a,0 / jp po / inc a / ld (V_IFF),a / pop af
        e.db(0xF5, 0xED, 0x57, 0xF3, 0x3E, 0x00); e.op_w(0xE2, 'pro_o'); e.db(0x3C)
        e.label('pro_o'); e.op_w(LD_NN_A, v['V_IFF']); e.db(0xF1, 0xC9)
    e.label('leave')                                                  # the game's bank back, interrupts as they were (A lost)
    if far is not None:
        e.op_w(LD_A_NN, v['V_BANK']); e.op_w(LD_NN_A, 0xFFFF)
    e.op_w(LD_A_NN, v['V_IFF']); e.db(0xB7, 0xC8, 0xFB, 0xC9)         # or a / ret z / ei / ret
    if far is None:
        h = Asm(); pieces.append(h)
        handlers(h, settings, v, False, used, deep=safe)
        if hook and hook['table']:
            lookup(h, v, False, hook['table'])
    else:
        if any(x in used for x in BLOCKS):
            r = Asm(); pieces.append(r)
            r.label('rdhl')                                                    # A = (HL) with the game's bank in slot 2
            r.db(0xC5); r.op_w(LD_A_NN, v['V_BANK']); r.op_w(LD_NN_A, 0xFFFF)
            r.db(0x46, 0x3E, far_bank); r.op_w(LD_NN_A, 0xFFFF); r.db(0x78, 0xC1, 0xC9)
        handlers(far, settings, v, True, used)
        if hook and hook['table']:
            lookup(far, v, True, hook['table'])
    if hook:
        # the hooked vector: the game's own RST calls are the known few (their return addresses in the table); anything else whose
        # index byte names a handler is a site.
        k = Asm(); pieces.append(k)
        k.label('hk')
        pro(k)
        k.db(0xE3, 0xF5, 0x7E); k.op_w(LD_NN_A, v['V_ARG'])                  # ex (sp),hl / push af / the index byte, in case it is a site
        if far is not None:
            k.op_w(0xCD, 'enter')
        if hook['table']:
            k.op_w(0xCD, 'lk'); k.op_r(0x28, 'hk_game')
        k.op_w(LD_A_NN, v['V_ARG']); k.db(0xFE, CHAIN + CHAIN_MAX + 1 if 'h_chain' in used else len(HANDLERS)); k.op_r(0x38, 'hk_site')
        k.label('hk_game')
        k.op_w(0xCD, 'leave')
        k.db(0xF1, 0xE3)                                                       # the game's own RST: its first instructions, then back
        hook['emit'](k)
        k.label('hk_site')
        k.db(0x23, 0xF1, 0xE3); k.op_w(0xC3, 'd_go')                           # inc hl / pop af / ex (sp),hl: the return steps over the index
    n = Asm(); pieces.append(n)
    n.label('nmi')
    n.db(0xF5, 0x3E, settings['start_hold'] & 0xFF); n.op_w(LD_NN_A, v['V_START']); n.db(0xF1, 0xED, 0x45)
    return {'pieces': pieces, 'far': far}


def far_table(settings):
    t = settings['colour_table']
    return bytes(t[x & 15] | t[x >> 4 & 15] << 2 | t[i >> 8] << 4 for i in range(4096) for x in [i & 0xFF])


def init_inline(a, ram, far_bank=None, n=None):
    """The power-on part, written inline in the reset trampoline: no stack (SP is not set yet at reset: a push at SP=0 lands
    on the mapper registers $FFFE/$FFFF). Clears the runtime's RAM; far: puts the slot-2 register where the BIOS leaves it."""
    a.op_w(0x21, ram); a.db(0x06, n or len(VARS), 0xAF, 0x77, 0x23, 0x10, 0xFC)   # ld hl,ram / ld b,n / xor a / ld (hl),a / inc hl / djnz
    # (far: the slot-2 register is left to the game, which sets it before it banks; the runtime saves and restores whatever it holds)


def colour_table(kind):
    """The GG 4-bit level -> SMS 2-bit level table."""
    if kind == 'truncate':
        return [x >> 2 for x in range(16)]
    if kind == 'round':
        return [min(3, int(x * 3 / 15 + 0.5)) for x in range(16)]
    if isinstance(kind, list) and len(kind) == 16:
        return [int(x) & 3 for x in kind]
    raise ValueError(f'colour table: round | truncate | a list of 16 levels (got {kind!r})')
