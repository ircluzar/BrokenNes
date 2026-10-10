"""Probe ROM sources for the Genesis VDP slot model (Track B3a). Each builder returns assembly text for asm68k.build_rom.

Conventions: results go to work RAM from $FF0000 (words); a probe ends in a spin loop so BlastEm can be paused after it; registers a1 = HV counter, a2 = status, a3 = data port, a4 = control port.
Every probe here was used to compare this emulator with BlastEm 1.0.0; see docs/projects/sega-track-b3a.md for what each one showed.
"""

VDP_REGS = {0: 0x04, 1: 0x44, 2: 0x30, 3: 0x2C, 4: 0x07, 5: 0x54, 7: 0x00, 10: 0xFF, 11: 0x00, 13: 0x2B, 15: 0x02, 16: 0x01, 17: 0x00, 18: 0x00}


def prologue(h40=True, display=True, regs=None, v30=False, extra=''):
    r = dict(VDP_REGS)
    r[12] = 0x81 if h40 else 0x00
    r[1] = (0x54 if display else 0x14) | (0x08 if v30 else 0)       # mode 5, DMA enabled, display on or off, no vertical interrupt
    if regs: r.update(regs)
    lines = ['    lea ($C00004).l,a2', '    lea ($C00008).l,a1', '    lea ($C00000).l,a3', '    lea ($C00004).l,a4']
    for k in sorted(r): lines.append('    move.w #$%04X,(a4)' % (0x8000 | k << 8 | r[k]))
    return '\n'.join(lines) + '\n' + extra


def sync_vblank_fall():
    return '''
    lea ($C00004).l,a2
s1: move.w (a2),d0
    btst #3,d0
    beq.s s1
s2: move.w (a2),d0
    btst #3,d0
    bne.s s2
'''


def sync_vblank_rise():
    return '''
    lea ($C00004).l,a2
s1: move.w (a2),d0
    btst #3,d0
    bne.s s1
s2: move.w (a2),d0
    btst #3,d0
    beq.s s2
'''


SPIN = '_spin: bra.s _spin\n'


def wait_v(target, label):
    """poll the V counter's byte until it equals `target` (about 26 cycles a lap)"""
    return '''%(l)s: move.b (a1),d1
    cmpi.b #$%(t)02X,d1
    bne.s %(l)s
''' % {'l': label, 't': target}


def wait_not_v(target, label):
    return '''%(l)s: move.b (a1),d1
    cmpi.b #$%(t)02X,d1
    beq.s %(l)s
''' % {'l': label, 't': target}


def colour_word(n):
    """CRAM word for a 9-bit index n: R = n & 7, G = n >> 3 & 7, B = n >> 6 & 7 (0000BBB0GGG0RRR0)"""
    return (n & 7) << 1 | (n >> 3 & 7) << 5 | (n >> 6 & 7) << 9


def colour_table(count, label='table'):
    """a table of `count` colour words 0,1,2,...,511,0,1,... (n mod 512 encoded as a CRAM colour): the colour of a pixel then says which word of the table was written last"""
    out = [label + ':']
    for i in range(0, count, 8):
        out.append('    dc.w ' + ','.join('$%04X' % colour_word((i + j) % 512) for j in range(min(8, count - i))))
    return '\n'.join(out) + '\n'


def dma_regs(src_bytes, length_words, fill=False):
    """VDP register writes (as asm lines, using a4) setting a 68000-to-VDP DMA of `length_words` words from byte address `src_bytes`"""
    s = src_bytes >> 1
    return '\n'.join('    move.w #$%04X,(a4)' % v for v in (
        0x9300 | length_words & 0xFF, 0x9400 | length_words >> 8 & 0xFF,
        0x9500 | s & 0xFF, 0x9600 | s >> 8 & 0xFF, 0x9700 | s >> 16 & 0x7F)) + '\n'


# ---------------------------------------------------------------------------------------------------------------------
#  HV counter
# ---------------------------------------------------------------------------------------------------------------------

def hv_scan(h40=True, count=4000, rise=False, pad_pattern=None, v30=False):
    """HV counter samples in a straight line of `move.w (a1),(a0)+` (12 cycles apart); pad_pattern is a list of extra instruction texts inserted after sample k (k mod len) to vary the spacing."""
    body = ''
    for k in range(count):
        body += '    move.w (a1),(a0)+\n'
        if pad_pattern and pad_pattern[k % len(pad_pattern)]: body += pad_pattern[k % len(pad_pattern)] + '\n'
    return prologue(h40, True, v30=v30) + '    lea ($FF0000).l,a0\n' + (sync_vblank_rise() if rise else sync_vblank_fall()) + body + SPIN


# ---------------------------------------------------------------------------------------------------------------------
#  Where in the line things happen, as pixels
# ---------------------------------------------------------------------------------------------------------------------

def hv_reg(h40=True, trials=200, pad_mod=30, display=False, pad_base=0, reg=7, cram=False, pad_step=1):
    """Relate the H counter to the picture's pixels with a register write, which takes effect at the output pixel with no FIFO in between: per trial (one per line) wait for the V counter to change, `pad` nops,
    read the HV counter into RAM and 12 cycles later set R7 (the backdrop colour index) to n mod 64, with colour RAM pre-filled with 64 distinct colours (entry i = colour index i+1). A row of the picture then
    shows the pixel where the register change took effect (the boundary between two backdrop colours) and RAM holds the HV value read just before it."""
    s = prologue(h40, display, regs={15: 2})
    s += '    move.l #$C0000000,(a4)\n'
    for i in range(64): s += '    move.w #$%04X,(a3)\n' % colour_word(i + 1)
    s += 'frame:\n'
    s += '    lea ($FF0000).l,a0\n'
    s += wait_v(0xFF, 'syn')
    for t in range(trials):
        s += '    move.w #$%04X,d3\n' % (0x8700 | (t % 64) if reg == 7 else 0)
        s += '    move.b (a1),d1\n'
        s += 'w%d: move.b (a1),d2\n    cmp.b d1,d2\n    beq.s w%d\n' % (t, t)
        s += '    nop\n' * (pad_base + (t * pad_step) % pad_mod)
        s += '    move.w (a1),(a0)+\n'
        s += '    move.w d3,(a4)\n'
    s += '    bra.w frame\n'
    return s


def hv_dot(h40=True, trials=200, pad_mod=30, display=False, pad_base=0):
    """Like hv_reg but the dot is a colour RAM write (which goes through the FIFO and lands in a slot)."""
    s = prologue(h40, display, regs={15: 0})
    s += '    lea ($FF0000).l,a0\n'
    s += '    move.l #$C0000000,(a4)\n'
    s += 'frame:\n'
    s += '    lea ($FF0000).l,a0\n'
    s += sync_vblank_fall() if display else wait_v(0xFF, 'syn')
    for t in range(trials):
        s += '    move.w #$%04X,d3\n' % colour_word((t * 5 + 3) % 512 or 1)
        s += '    move.b (a1),d1\n'
        s += 'w%d: move.b (a1),d2\n    cmp.b d1,d2\n    beq.s w%d\n' % (t, t)
        s += '    nop\n' * (pad_base + t % pad_mod)
        s += '    move.w (a1),(a0)+\n'
        s += '    move.w d3,(a3)\n'
    s += '    bra.w frame\n'
    return s


# ---------------------------------------------------------------------------------------------------------------------
#  FIFO and DMA slots
# ---------------------------------------------------------------------------------------------------------------------

def fifo_stall(target_v=100, nmax=24, code='$40000000', pad_nops=0, h40=True, display=True, inc=0):
    """Time VDP data port writes: trial n (n = 1..nmax) waits for V == target_v, does n back-to-back word writes (`code` = the command: $40000000 VRAM write, $C0000000 CRAM write, $40000010 VSRAM write)
    and reads the HV counter right after the last one; the elapsed time is the stalls the FIFO imposed. RAM word n-1 = that HV word."""
    s = prologue(h40, display, regs={15: inc})
    s += '    lea ($FF0000).l,a0\n    moveq #0,d0\n'
    s += '    move.l #%s,(a4)\n' % code
    for n in range(1, nmax + 1):
        s += wait_v(target_v, 'wv%d' % n)
        s += '    nop\n' * pad_nops
        s += '    move.w d0,(a3)\n' * n
        s += '    move.w (a1),(a0)+\n'
        s += wait_not_v(target_v, 'wn%d' % n)
    s += SPIN
    return s


def fifo_dots(h40=True, trials=24, first_line=8, line_step=9, n_base=5, display=True):
    """CRAM data port writes in active lines with the FIFO saturated. Trial t waits for V == first_line + line_step * t, then does n = n_base + t writes of colours 1..n (CRAM entry 0, auto-increment 0) and reads HV after
    the last one (RAM word t). In the picture each write that reached CRAM is a dot on the row where it happened: the boundary between two colours shows which slot the FIFO entry used, the HV word shows when
    the 68000 was let go."""
    s = prologue(h40, display, regs={15: 0})
    s += '    move.l #$C0000000,(a4)\n'
    s += 'frame:\n'
    s += '    lea ($FF0000).l,a0\n'
    for t in range(trials):
        n = n_base + t
        s += wait_v(first_line + line_step * t, 'wv%d' % t)
        for k in range(n):
            s += '    move.w #$%04X,(a3)\n' % colour_word(k + 1)
        s += '    move.w (a1),(a0)+\n'
    s += wait_v(0xFF, 'wfin')
    s += '    bra.w frame\n'
    return s


def dma_hv(h40=True, display=True, nmax=40, target_v=100, dest='cram', pad_nops=0, first=1, step=1, pre_writes=0, count_list=None):
    """The time of the N-th free slot after a DMA starts: trial n waits for V == target_v, (optionally does `pre_writes` VRAM word writes to saturate the FIFO so that the start is synchronised to a slot), starts a DMA
    of n words (68000 held until it ends) and reads the HV counter right after the 68000 gets the bus back (RAM word per trial). With the display on the slots are the picture line's 18 free ones; with it off a blanked
    line's. The words are colours 0,1,2,... of a table, so the picture shows where the first words landed."""
    cmd = {'cram': '$C0000080', 'vsram': '$40000090', 'vram': '$40000080'}[dest]
    table_at = 0x20000 - 2 * 600
    s = prologue(h40, display, regs={15: 0})
    s += 'frame:\n    lea ($FF0000).l,a0\n'
    ns = count_list if count_list is not None else list(range(first, nmax + 1, step))
    for t, n in enumerate(ns):
        s += wait_v(target_v, 'wv%d' % t)
        s += '    nop\n' * pad_nops
        if pre_writes:
            s += '    moveq #0,d0\n    move.l #$40000000,(a4)\n' + '    move.w d0,(a3)\n' * pre_writes
        s += dma_regs(table_at, n)
        s += '    move.l #%s,(a4)\n' % cmd
        s += '    move.w (a1),(a0)+\n'
        s += wait_not_v(target_v, 'wn%d' % t)
    s += wait_v(0xFF, 'wfin')
    s += '    bra.w frame\n'
    s += '    org $%X\n' % table_at
    s += colour_table(600)
    return s


def dma_grid(trials, h40=True, display=True, target_v=100):
    """Like dma_hv with the FIFO state set up per trial: `trials` is a list of (pre_writes, words, dest) where dest is 'cram' | 'vram' | 'vsram'; the pre-writes are VRAM word writes (their entries need two slots
    each, so with 4 or more the 68000 is held until the FIFO has room: a slot-synchronised start). RAM word t = HV right after the DMA of trial t."""
    cmds = {'cram': '$C0000080', 'vsram': '$40000090', 'vram': '$40000080'}
    table_at = 0x20000 - 2 * 600
    s = prologue(h40, display, regs={15: 0})
    s += 'frame:\n    lea ($FF0000).l,a0\n'
    for t, (pre, n, dest) in enumerate(trials):
        s += wait_v(target_v, 'wv%d' % t)
        if pre:
            s += '    moveq #0,d0\n    move.l #$40000000,(a4)\n' + '    move.w d0,(a3)\n' * pre
        s += dma_regs(table_at, n)
        s += '    move.l #%s,(a4)\n' % cmds[dest]
        s += '    move.w (a1),(a0)+\n'
        s += wait_not_v(target_v, 'wn%d' % t)
    s += wait_v(0xFF, 'wfin')
    s += '    bra.w frame\n'
    s += '    org $%X\n' % table_at
    s += colour_table(600)
    return s


def dma_resume_marker(trials, h40=True, display=True, first_line=8, line_step=9, table_at=0x1F000):
    """When does the 68000 run again, in pixels? Trial t = (pre_writes, words, dest): at line first_line + line_step * t (V counter poll), `pre_writes` VRAM writes, a DMA of `words` words from a colour table into
    CRAM entry 0 (backdrop index 0), and the instruction right after the DMA command sets R7 to 1 (CRAM entry 1 holds a fixed colour): the row shows the table's colours (word index = colour) up to the pixel where the
    68000 came back, then the fixed colour. So the dots before the marker are the words written by then."""
    cmds = {'cram': '$C0000080', 'vsram': '$40000090', 'vram': '$40000080'}
    s = prologue(h40, display, regs={15: 0, 7: 0})
    s += '    move.l #$C0020000,(a4)\n    move.w #$0EEE,(a3)\n'                  # CRAM entry 1: white
    s += 'frame:\n    move.w #$8700,(a4)\n    moveq #0,d0\n'
    for t, (pre, n, dest) in enumerate(trials):
        s += wait_v(first_line + line_step * t, 'wv%d' % t)
        if pre:
            s += '    move.l #$40000000,(a4)\n' + '    move.w d0,(a3)\n' * pre
        s += dma_regs(table_at, n)
        s += '    move.l #%s,(a4)\n' % cmds[dest]
        s += '    move.w #$8701,(a4)\n'
        s += '    move.w #$8700,(a4)\n'                                          # the marker lasts only until here (a few pixels)
    s += wait_v(0xFF, 'wfin')
    s += '    bra.w frame\n'
    s += '    org $%X\n' % table_at
    s += colour_table(700)
    return s


def dma_dots(h40=True, display=True, length=4000, table_at=None, dest='cram', inc=0, pre_nops=0, v30=False):
    """DMA colour words into CRAM entry 0 (the backdrop) so each word shows as the colour of the pixels after its write: the picture is a map of where every DMA word landed.
    The DMA starts just after the VBlank flag falls (plus pre_nops nops); the colour table lives at the end of the ROM."""
    regs = {15: inc}
    if table_at is None: table_at = 0x20000 - 2 * length
    cmd = {'cram': '$C0000080', 'vsram': '$40000090', 'vram': '$40000080'}[dest]
    s = prologue(h40, display, regs=regs, v30=v30)
    s += 'frame:\n'
    s += sync_vblank_fall()
    s += '    nop\n' * pre_nops
    s += dma_regs(table_at, length)
    s += '    move.l #%s,(a4)\n' % cmd
    s += '    bra.w frame\n'
    s += '    org $%X\n' % table_at
    s += colour_table(length)
    return s


def direct_colour_replica(nwrites=13, nops=4, length=0xAD40, h40=True, display_off_with_dma=True, hv_marks=False):
    """Chilly Willy's direct colour DMA demo's lead-in and DMA, with a colour-coded word table instead of the image: display on, wait for the VBlank flag to rise and fall, `nwrites` data port writes,
    `nops` nops, then the DMA registers and the command that also switches the display off (the demo's R1 write rides in the same long word as the source-high register)."""
    table_at = max(0x2000, 0x20000 - 2 * length) & ~1
    s = prologue(h40, True, regs={15: 0, 10: 0xFF})
    s += '    moveq #0,d0\n'
    s += 'frame:\n'
    s += '    move.w #$8154,(a4)\n'
    s += '    move.l #$40000000,(a4)\n'
    s += 'w1:  btst #3,1(a4)\n    beq.s w1\n'
    s += 'w2:  btst #3,1(a4)\n    bne.s w2\n'
    if hv_marks: s += '    lea ($FF0000).l,a0\n    move.w (a1),(a0)+\n'          # HV right after the VBlank flag fell
    s += '    move.w d0,(a3)\n' * nwrites
    if hv_marks: s += '    move.w (a1),(a0)+\n'                                      # HV right after the FIFO writes (the 68000 was held by the full FIFO)
    s += '    nop\n' * nops
    s += '    move.l #$%08X,(a4)\n' % (0x93009400 | (length & 0xFF) << 16 | length >> 8 & 0xFF)
    s += '    move.l #$%08X,(a4)\n' % (0x95009600 | (table_at >> 1 & 0xFF) << 16 | table_at >> 9 & 0xFF)
    s += '    move.l #$%08X,(a4)\n' % (0x97008100 | (table_at >> 17 & 0x7F) << 16 | (0x14 if display_off_with_dma else 0x54))
    if hv_marks: s += '    move.w (a1),(a0)+\n'                                      # HV just before the command that starts the DMA
    s += '    move.l #$C0000080,(a4)\n'
    if hv_marks: s += '    move.w (a1),(a0)+\n'                                      # HV when the DMA has finished and the 68000 runs again
    s += '    bra.w frame\n'
    s += '    org $%X\n' % table_at
    s += colour_table(length)
    return s


def replica_hv(ns, nwrites=13, nops=4, h40=True, display_off=True):
    """The direct colour demo's lead-in (display on, wait for the VBlank flag to rise and fall, `nwrites` data port writes to saturate the FIFO, `nops` nops, DMA registers, command with the display switched off) for
    each word count in `ns`, one per frame, reading HV when the 68000 runs again: the first word's slot is fixed by the FIFO sync, so the HV values are the times of the slots that follow it, in blanked
    lines (tail slots included). RAM word i = HV after the DMA of ns[i] words."""
    table_at = 0x20000 - 2 * 700
    s = prologue(h40, True, regs={15: 0, 10: 0xFF})
    s += '    moveq #0,d0\n    lea ($FF0000).l,a0\n'
    for i, n in enumerate(ns):
        s += '    move.w #$8154,(a4)\n'
        s += '    move.l #$40000000,(a4)\n'
        s += 'a%d:  btst #3,1(a4)\n    beq.s a%d\n' % (i, i)
        s += 'b%d:  btst #3,1(a4)\n    bne.s b%d\n' % (i, i)
        s += '    move.w d0,(a3)\n' * nwrites
        s += '    nop\n' * nops
        s += '    move.l #$%08X,(a4)\n' % (0x93009400 | (n & 0xFF) << 16 | n >> 8 & 0xFF)
        s += '    move.l #$%08X,(a4)\n' % (0x95009600 | (table_at >> 1 & 0xFF) << 16 | table_at >> 9 & 0xFF)
        s += '    move.l #$%08X,(a4)\n' % (0x97008100 | (table_at >> 17 & 0x7F) << 16 | (0x14 if display_off else 0x54))
        s += '    move.l #$C0000080,(a4)\n'
        s += '    move.w (a1),(a0)+\n'
    s += '    move.w #$8154,(a4)\n'
    s += SPIN
    s += '    org $%X\n' % table_at
    s += colour_table(700)
    return s


def fifo_wait_variant(variant=7, iterations=100, pad_step=3, display=True, h40=True):
    """Nemesis' VDP FIFO test 16 ('FIFO wait states'), variants 1 (nothing after the writes), 5 (a partial control write), 6 (a full control write), 7 (a full control write and a data port read): six word writes to
    VRAM, then sixteen status samples per iteration, at differing phases. RAM: 16 words per iteration."""
    s = prologue(h40, display, regs={15: 2})
    s += '    lea ($FF0000).l,a0\n'
    for it in range(iterations):
        s += wait_v((it * 7) % 224, 'wvi%d' % it)
        s += '    nop\n' * ((it * pad_step) % 40)
        s += '    move.l #$40000002,(a4)\n'
        s += '    move.w #$FFFF,(a3)\n' * 6
        if variant == 5: s += '    move.w #$0000,(a4)\n'
        elif variant == 6: s += '    move.l #$00000002,(a4)\n'
        elif variant == 7: s += '    move.l #$00000002,(a4)\n    move.w (a3),d4\n'
        s += '    move.w (a4),d4\n'
        for k in range(16): s += '    move.w (a4),(a0)+\n'
    s += SPIN
    return s


def fifo_wait10(iterations=100, pad_step=3, display=True, h40=True, dma_words=3, cpu_writes=3):
    """Nemesis' FIFO wait-state variant 10: DMA off, `cpu_writes` VRAM word writes, DMA on, a DMA of `dma_words` words from ROM to VRAM, then sixteen status samples per iteration at differing phases."""
    table_at = 0x1F000
    s = prologue(h40, display, regs={15: 2})
    s += '    lea ($FF0000).l,a0\n'
    for it in range(iterations):
        s += wait_v((it * 7) % 224, 'wvi%d' % it)
        s += '    nop\n' * ((it * pad_step) % 40)
        s += '    move.w #$%04X,(a4)\n' % (0x8100 | (0x04 | (0x40 if display else 0)))       # DMA disabled
        s += '    move.l #$40000002,(a4)\n'
        s += '    move.w #$FFFF,(a3)\n' * cpu_writes
        s += '    move.w #$%04X,(a4)\n' % (0x8100 | (0x14 | (0x40 if display else 0)))       # DMA enabled
        s += '    move.w (a4),d5\n'
        s += dma_regs(table_at, dma_words)
        s += '    move.l #$40000082,(a4)\n'
        s += '    move.w (a4),d4\n'
        for k in range(16): s += '    move.w (a4),(a0)+\n'
    s += SPIN
    s += '    org $%X\n' % table_at
    s += colour_table(8)
    return s
