"""Probe ROM sources for Track B4a (the interrupt timing and the VDP residue). Same conventions as probes.py: assembly text for asm68k.build_rom, results in work RAM from $FF0000, a spin loop at the end.

Timing method shared by the interrupt probes ("drift sampling"): the 68000 runs at master/7 and a frame is 262*3420 = 896,040 master clocks, so a program that does the same thing every frame meets the frame
five clocks later in 68000 phase each time (896,040 mod 7 = 5, and 5 is prime to every instruction length). Over a few hundred frames the event under test is sampled at every phase, and its exact time
falls out of the fraction of frames in which a fixed-spacing sampler saw it one sample later. Differences between two machines are read from those fractions, so the answer does not depend on the cycle at
which the machine begins (BlastEm's power-on phase differs from ours by 428 clocks, B3a).
"""
import probes
from probes import prologue, wait_v, SPIN

NL = '\n'


def _lines(*ls):
    return NL.join(ls) + NL


def vint_flag_stream(frames=200, samples=40, pad_nops=100, h40=True, v30=False, rise_line=223):
    """Per frame: wait for the V counter to reach `rise_line` (the last line before the VBlank flag rises), pad `pad_nops` nops, then read the status register `samples` times 12 cycles (84 master clocks)
    apart into RAM. Status bit 3 is the VBlank flag, bit 7 the vertical interrupt pending flag. The 68000 runs with its interrupt mask at 7 during the stream (so nothing is acknowledged while the samples are
    taken) and lowers it for a moment after the stream: the handler is a bare RTE, which acknowledges the interrupt and clears the flag for the next frame. VINT is enabled in the VDP.
    RAM: frame f's samples at $FF0000 + f * samples * 2."""
    s = prologue(h40, True, regs={1: 0x64 | (0x08 if v30 else 0), 0: 0x04})
    s += _lines('    lea ($FF0000).l,a0', '    move.w #%d,d7' % (frames - 1), 'frame:')
    s += wait_v(rise_line, 'wv')
    s += ('    nop' + NL) * pad_nops
    s += ('    move.w (a2),(a0)+' + NL) * samples
    s += _lines('    move.w #$2000,sr', '    nop', '    nop', '    nop', '    move.w #$2700,sr', '    dbf d7,frame', '_spin: bra.s _spin', 'handler:', '    rte')
    return s


def vint_stream_hv(frames=200, pairs=24, pad_nops=90, h40=True, v30=False, rise_line=223, vint_on=True):
    """Like vint_flag_stream, but the samples alternate status and HV counter reads (each 12 cycles = 84 clocks after the one before): the HV words put the status samples on the line, so a flag's rise
    is bracketed in H/V counter time. RAM: frame f's words at $FF0000 + f * pairs * 4 (status, HV, status, HV ...)."""
    s = prologue(h40, True, regs={1: (0x64 if vint_on else 0x44) | (0x08 if v30 else 0), 0: 0x04})
    s += _lines('    lea ($FF0000).l,a0', '    move.w #%d,d7' % (frames - 1), 'frame:')
    s += wait_v(rise_line, 'wv')
    s += ('    nop' + NL) * pad_nops
    s += ('    move.w (a2),(a0)+' + NL + '    move.w (a1),(a0)+' + NL) * pairs
    s += _lines('    move.w #$2000,sr', '    nop', '    nop', '    nop', '    move.w #$2700,sr', '    dbf d7,frame', '_spin: bra.s _spin', 'handler:', '    rte')
    return s


def hint_lines(ns, frames_per=3, h40=True, v30=False, hint_first=False):
    """Which lines the horizontal interrupt fires on. For each R10 value in `ns`: write it, then record the HV counter at the first instruction of the HINT handler for `frames_per` frames; the VINT handler adds
    the marker word $7F7F to the record at each frame (the HV words of a HINT handler never look like it). RAM: $FF0100 + 4i = the record pointer where setting i began (long), the records from $FF1000.
    The counter semantics (the line a given R10 first fires on, whether the pre-render and the first blank line count) fall out of the V values."""
    s = prologue(h40, True, regs={1: 0x64 | (0x08 if v30 else 0), 0: 0x14, 10: ns[0]})
    s += _lines('    lea ($FF1000).l,a0', '    move.w #0,($FF0002).l', '    move.w #$2000,sr')
    for i, n in enumerate(ns):
        s += _lines('    move.w #$%04X,(a4)' % (0x8A00 | n), '    move.l a0,($%X).l' % (0xFF0100 + 4 * i),
                    'w%d: cmpi.w #%d,($FF0002).l' % (i, frames_per * (i + 1) + 1), '    bcs.s w%d' % i)
    s += _lines('    move.l a0,($%X).l' % (0xFF0100 + 4 * len(ns)), '_spin: bra.s _spin',
                'hhandler:', '    move.w (a1),(a0)+', '    rte',
                'vhandler:', '    move.w #$7F7F,(a0)+', '    addq.w #1,($FF0002).l', '    rte')
    return s


def hv_in_handler_limited(kind='hint', limit=20000, h40=True, extra_regs=None, loop_nops=1, v30=False, body=None):
    """As hv_in_handler, but the handler records at most `limit` readings (the count word keeps counting): the horizontal interrupt fires up to 224 times a frame and the work RAM is only 32,768 words.
    `body` (a LOOP_BODIES key) replaces the nop loop by other instructions (registers d2 = $FFFF, d3 = $7FFF0000, d4 = 7, a6 = $FF8000 are set up for them)."""
    regs = dict(extra_regs or {})
    if kind == 'vint': regs.update({1: 0x64 | (0x08 if v30 else 0), 0: 0x04})
    else: regs.update({1: 0x44 | (0x08 if v30 else 0), 0: 0x14, 10: 0})
    s = prologue(h40, True, regs=regs)
    s += _lines('    lea ($FF0002).l,a0', '    lea ($C00008).l,a1', '    lea ($FF8000).l,a6', '    move.l #$7FFF0000,d3', '    move.w #$FFFF,d2', '    move.w #7,d4', '    move.w #0,($FF0000).l', '    move.w #$2000,sr', 'main:')
    if body: s += ''.join('    ' + ins + NL for ins in LOOP_BODIES[body])
    else: s += ('    nop' + NL) * loop_nops
    s += _lines('    bra.s main', '_spin: bra.s _spin', 'sub1: rts', 'handler:', '    move.w (a1),d1', '    cmpi.w #%d,($FF0000).l' % limit, '    bcc.s done', '    move.w d1,(a0)+',
                '    addq.w #1,($FF0000).l', 'done:', '    rte')
    return s


LOOP_BODIES = {
    'nop': ['nop'],
    'move': ['move.w (a6),d1', 'move.w (a6),d1'],
    'mulu': ['mulu d2,d3'],
    'divu': ['divu d4,d3'],
    'mixed': ['move.l (a6),d1', 'addq.w #1,d1', 'lsl.l #8,d3', 'move.w d1,(a6)', 'nop'],
    'longmove': ['movem.l d0-d7,-(sp)', 'movem.l (sp)+,d0-d7'],
    # single long instructions (one instruction and a bra.s per loop): which ones does the oracle take an interrupt after later than we do?
    'movem_out': ['movem.l d0-d7,(a6)'],
    'movem_in': ['movem.l (a6),d0-d7'],
    'movem_short': ['movem.l d0-d1,(a6)'],
    'mulu1': ['moveq #1,d5', 'mulu d2,d5'],
    'divu1': ['move.l d3,d6', 'divu d4,d6'],
    'lsl8': ['lsl.l #8,d5'],
    'lslr': ['moveq #30,d5', 'lsl.l d5,d6'],
    'clr_l': ['clr.l (a6)'],
    'add_rmw': ['add.l d3,(a6)'],
    'jsr_rts': ['jsr sub1'],
}


def hv_in_handler_loop(kind='vint', body='nop', h40=True):
    """As hv_in_handler with a main loop of other instructions (LOOP_BODIES): the interrupt is taken after the instruction in progress, so the entry time depends on the instruction lengths and on the rule the
    interrupt level is sampled by; fitting both a VINT time and a sampling shift to several loops tells them apart."""
    regs = {1: 0x64, 0: 0x04} if kind == 'vint' else {1: 0x44, 0: 0x14, 10: 0}
    s = prologue(h40, True, regs=regs)
    s += _lines('    lea ($FF0002).l,a0', '    lea ($C00008).l,a1', '    lea ($FF8000).l,a6', '    move.l #$7FFF0000,d3', '    move.w #$FFFF,d2', '    move.w #7,d4', '    move.w #0,($FF0000).l',
                '    move.w #$2000,sr', 'main:')
    for ins in LOOP_BODIES[body]: s += '    ' + ins + NL
    s += _lines('    bra.s main' if len(LOOP_BODIES[body]) < 20 else '    bra.w main', '_spin: bra.s _spin', 'handler:', '    move.w (a1),(a0)+', '    addq.w #1,($FF0000).l', '    rte')
    return s


def irq_overhead(kind='vint', h40=True, frames=400, body='plain'):
    """What an interrupt costs the interrupted program, in the VDP's own time. The main loop is a straight stream of HV counter reads stored one after the other (`move.w (a1),(a0)+`); the handler logs its own
    first HV read (entry) and the nine samples around the point the PREVIOUS frame's interrupt cut into the stream (what was read before it, what was read after the return), then restarts the stream at
    the buffer's start. RAM: frame f's log at $FF0100 + 20 f: word 0 = HV at the handler's first instruction, words 1-9 = the stream samples p-5..p+3 (p = the sample position of the interrupt)."""
    regs = {1: 0x64, 0: 0x04} if kind == 'vint' else {1: 0x44, 0: 0x14, 10: 0}
    s = prologue(h40, True, regs=regs)
    s += _lines('    lea ($C00008).l,a1', '    lea ($FF2000).l,a0', '    lea ($FF2020).l,a6', '    lea ($FF0100).l,a2', '    lea ($FFA000).l,a5', '    move.w #$2000,sr', 'main:')
    s += ('    move.w (a1),(a0)+' + NL) * 40
    s += _lines('    cmpa.l a5,a0', '    bcs.s main', '    lea ($FF2000).l,a0', '    bra.w main', '_spin: bra.s _spin', 'handler:', '    move.w (a1),(a2)+')
    for k in range(-5, 4): s += '    move.w %d(a6),(a2)+\n' % (2 * k)
    s += _lines('    movea.l a0,a6', '    rte')
    return s


def hv_in_handler(kind='vint', h40=True, extra_regs=None, loop_nops=1, v30=False):
    """The interrupt handler's first instruction reads the HV counter into RAM. kind 'vint' (R1 bit 5) or 'hint' (R0 bit 4, R10 = 0: every line). The main loop is `nop * loop_nops; bra` with the
    interrupts enabled (SR $2000). RAM word 0 = interrupt count; the HV words follow ($FF0002...). The handler is a fixed 68000 sequence on both machines; the machines are compared on the same values."""
    regs = dict(extra_regs or {})
    if kind == 'vint': regs.update({1: 0x64 | (0x08 if v30 else 0), 0: 0x04})
    else: regs.update({1: 0x44 | (0x08 if v30 else 0), 0: 0x14, 10: 0})
    s = prologue(h40, True, regs=regs)
    s += _lines('    lea ($FF0002).l,a0', '    lea ($C00008).l,a1', '    move.w #0,($FF0000).l', '    move.w #$2000,sr', 'main:')
    s += ('    nop' + NL) * loop_nops
    s += _lines('    bra.s main', '_spin: bra.s _spin', 'handler:', '    move.w (a1),(a0)+', '    addq.w #1,($FF0000).l', '    rte')
    return s


def hv_scan_regs(h40=True, count=10500, regs=None, v30=False):
    """A straight line of HV counter reads (12 cycles apart, as probes.hv_scan) with extra VDP registers (the interlace bits of R12 ...), started at the fall of the VBlank flag: a whole frame of V and H values."""
    s = prologue(h40, True, regs=regs, v30=v30)
    s += '    lea ($FF0000).l,a0\n' + probes.sync_vblank_fall()
    s += ('    move.w (a1),(a0)+' + NL) * count
    s += SPIN
    return s


def z80_int_repeats(ks, frames=20, h40=True, vint_on=True):
    """How long the VDP holds the Z80's interrupt line. For each k in `ks` a Z80 program is loaded that, in its IM 1 handler at $38 (push af; ld a,(counter); inc a; ld (counter),a; k nops; ei; pop af; reti), counts
    its entries in Z80 RAM $1000; the handler is about 55 + 4k Z80 cycles, and because it re-enables interrupts before RETI every entry while the line is still asserted is another one. After `frames` frames
    the 68000 reads the counter: entries per frame = how many times the handler fitted into the time the line stayed up (a line is 228 Z80 cycles). RAM: word i ($FF0100 + 2i) = the counter for ks[i]."""
    def z80(k):
        code = [0xF3, 0xED, 0x56, 0x31, 0xF0, 0x1F, 0xAF, 0x32, 0x00, 0x10, 0xFB, 0x18, 0xFE]
        code += [0x00] * (0x38 - len(code))
        code += [0xF5, 0x3A, 0x00, 0x10, 0x3C, 0x32, 0x00, 0x10] + [0x00] * k + [0xFB, 0xF1, 0xED, 0x4D]
        return code
    s = prologue(h40, True, regs={1: 0x64 if vint_on else 0x44, 0: 0x04})
    s += _lines('    lea ($A11100).l,a5', '    lea ($A11200).l,a6', '    lea ($FF0100).l,a0')
    for i, k in enumerate(ks):
        n = len(z80(k)); n += n & 1
        s += _lines('    move.w #$0100,(a5)', '    move.w #$0100,(a6)', 'g%d: move.w (a5),d1' % i, '    btst #8,d1', '    bne.s g%d' % i,
                    '    lea ($A00000).l,a1', '    lea z80prog%d,a2' % i, '    move.w #%d,d0' % (n - 1), 'c%d: move.b (a2)+,(a1)+' % i, '    dbf d0,c%d' % i,
                    '    move.w #$0000,(a6)', '    nop', '    nop', '    nop', '    nop', '    move.w #$0000,(a5)', '    move.w #$0100,(a6)', '    move.w #%d,d7' % (frames - 1),
                    'f%d: move.w (a4),d1' % i, '    btst #3,d1', '    bne.s f%d' % i,
                    'h%d: move.w (a4),d1' % i, '    btst #3,d1', '    beq.s h%d' % i, '    dbf d7,f%d' % i,
                    '    move.w #$0100,(a5)', 'r%d: move.w (a5),d1' % i, '    btst #8,d1', '    bne.s r%d' % i,
                    '    moveq #0,d1', '    move.b ($A01000).l,d1', '    move.w d1,(a0)+')
    s += '_spin: bra.s _spin\n'
    for i, k in enumerate(ks):
        code = z80(k)
        if len(code) & 1: code.append(0)
        s += 'z80prog%d:\n' % i
        for j in range(0, len(code), 8): s += '    dc.b ' + ','.join('$%02X' % b for b in code[j:j + 8]) + NL
    return s


def write_rate(kind='cram', h40=True, display=True, per=16):
    """How fast the 68000 can push words into one VDP memory: a main loop of `per` data-port writes and a counter increment, the vertical interrupt counting frames. RAM: long at $FF0000 = loop laps, word at
    $FF0004 = frames; laps per frame is the throughput (write waits for the FIFO and the access slots included), compared between the machines. kind: 'cram', 'vram' or 'vsram'; display off gives the
    blanking-rate access slots."""
    cmd = {'cram': '$C0000000', 'vram': '$40000000', 'vsram': '$40000010'}[kind]
    s = prologue(h40, display, regs={1: (0x74 if display else 0x34), 0: 0x04, 15: 2})
    s += _lines('    lea ($FF0000).l,a0', '    move.l #0,(a0)', '    move.w #0,($FF0004).l', '    move.l #%s,(a4)' % cmd, '    moveq #0,d0', '    move.w #$2000,sr', 'main:')
    s += ('    move.w d0,(a3)' + NL) * per
    s += _lines('    addq.l #1,(a0)', '    bra.s main', '_spin: bra.s _spin', 'handler:', '    addq.w #1,($FF0004).l', '    rte')
    return s


def dma_rate(kind='68k_vram', h40=True, display=True, words=2048):
    """How long a DMA of `words` words takes, in laps per frame: one DMA, then a poll until the status register's DMA-busy bit is clear, then a counter increment; the vertical interrupt counts frames. RAM: long at
    $FF0000 = laps, word at $FF0004 = frames (the same layout as write_rate). kind: '68k_vram', '68k_cram', 'fill' (VRAM fill) or 'copy' (VRAM copy)."""
    lo, hi = words & 0xFF, words >> 8 & 0xFF
    if kind == '68k_vram': r23, cmd, extra = 0x00, '$40000080', []
    elif kind == '68k_cram': r23, cmd, extra = 0x00, '$C0000080', []
    elif kind == 'fill': r23, cmd, extra = 0x80, '$40000080', ['    move.w #$1234,(a3)']
    else: r23, cmd, extra = 0xC0, '$40000080', []
    s = prologue(h40, display, regs={1: (0x74 if display else 0x34), 0: 0x04, 15: 2})
    s += _lines('    lea ($FF0000).l,a0', '    move.l #0,(a0)', '    move.w #0,($FF0004).l', '    move.w #$2000,sr', 'main:',
                '    move.w #$%04X,(a4)' % (0x9300 | lo), '    move.w #$%04X,(a4)' % (0x9400 | hi), '    move.w #$9500,(a4)', '    move.w #$9608,(a4)', '    move.w #$%04X,(a4)' % (0x9700 | r23),
                '    move.l #%s,(a4)' % cmd)
    s += ''.join(x + NL for x in extra)
    s += _lines('poll: move.w (a2),d1', '    btst #1,d1', '    bne.s poll', '    addq.l #1,(a0)', '    bra.s main', '_spin: bra.s _spin', 'handler:', '    addq.w #1,($FF0004).l', '    rte')
    return s


SR_VARIANTS = {
    # name: (the instruction that lowers the interrupt mask, the instruction repeated after it)
    'move_sr_nop': ('move.w #$2000,sr', 'nop'),
    'andi_sr_nop': ('andi.w #$F8FF,sr', 'nop'),
    'move_sr_addq': ('move.w #$2000,sr', 'addq.w #1,d0'),
    'move_sr_mulu': ('move.w #$2000,sr', 'mulu d2,d3'),
    'move_sr_movem': ('move.w #$2000,sr', 'movem.l d0-d7,(a6)'),
    'move_sr_clrl': ('move.w #$2000,sr', 'clr.l (a6)'),
    'move_sr_divu': ('move.w #$2000,sr', 'divu d4,d3'),
    'move_sr_lsl': ('move.w #$2000,sr', 'lsl.l #8,d5'),
}


def sr_delay(variants=None, frames=30, h40=True, run=6):
    """How many instructions run after an interrupt mask lowering before a pending vertical interrupt is taken. For each variant: with the mask at 7 wait for the interrupt pending flag (status bit 7), then lower
    the mask with the variant's first instruction and run `run` copies of its second instruction; the handler logs the return address it was given (the first instruction not executed). Labels `v<i>m` (the mask
    instruction), `v<i>r<k>` (copy k) and `v<i>e` (after the run) let the analysis turn the logged addresses into 'after copy k'. RAM: variant i, frame f at $FF0100 + (i * frames + f) * 4 (a long)."""
    variants = variants or list(SR_VARIANTS)
    s = prologue(h40, True, regs={1: 0x74, 0: 0x04})
    s += _lines('    lea ($FF0100).l,a0', '    lea ($FF8000).l,a6', '    move.l #$7FFF0000,d3', '    move.w #$FFFF,d2', '    move.w #7,d4', '    moveq #1,d5')
    for i, name in enumerate(variants):
        mask_ins, ins = SR_VARIANTS[name]
        s += _lines('    move.w #%d,d7' % (frames - 1), 'f%d:' % i, '    move.w #$2700,sr', 'w%d: move.w (a2),d1' % i, '    btst #7,d1', '    beq.s w%d' % i, '    moveq #0,d0', 'v%dm: %s' % (i, mask_ins))
        for k in range(run): s += 'v%dr%d: %s' % (i, k, ins) + NL
        s += _lines('v%de: nop' % i, '    dbf d7,f%d' % i)
    s += _lines('_spin: bra.s _spin', 'handler:', '    move.l 2(sp),(a0)+', '    rte')
    return s

def frame_phase(il=0, h40=True):
    """The VINT handler reads the HV counter first and the status register second (field flag, bit 4), into RAM ($FF0004...: HV, status per frame). Interrupts on, a nop loop as the main program: successive HV
    values drift by the 68000's phase against the frame, and a field that is a line longer shows as a jump. `il` is R12's interlace field (0, 1 or 3)."""
    s = prologue(h40, True, regs={1: 0x64, 0: 0x04, 12: (0x81 if h40 else 0x00) | il << 1})
    s += _lines('    lea ($FF0004).l,a0', '    lea ($C00008).l,a1', '    lea ($C00004).l,a2', '    move.w #0,($FF0000).l', '    move.w #$2000,sr', 'main:', '    nop', '    bra.s main', '_spin: bra.s _spin',
                'handler:', '    move.w (a1),(a0)+', '    move.w (a2),(a0)+', '    addq.w #1,($FF0000).l', '    rte')
    return s
