"""Interrupt acceptance latency after long 68000 instructions (Track B4b): when, relative to the instruction that is running, does the 68000 take the vertical interrupt?

Each frame the probe waits for the VBLANK flag to rise (line 224), runs k NOPs (a computed jump into a NOP sled, 4 cycles each) and then the instruction under test; the vertical interrupt arrives somewhere
during that stretch and the handler's first instruction reads the HV counter into RAM. Scanning k = 0..K-1 moves the instruction across the moment the interrupt is requested: the curve "handler entry
against k" shows where the 68000 stops to take it. Two emulators that accept interrupts alike give the same curve up to a constant (the moment the interrupt is raised); a different rule shows as a
plateau of another length or place. Used with runprobe.ram_probe on both machines.

    python intlat.py                       # all instructions, ours and BlastEm (hold the oracle lock), prints the curves and the differences
    python intlat.py --ours-only
"""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import probes, runprobe

K = 56

# the instruction under test, with the setup it needs (registers d2 = $7FFFFFFF, d3 = $0001 for a divisor, d4 = 31 for a shift count; sp valid)
CASES = {
    'nop':        ('nop', ''),
    'addl':       ('add.l d2,d5', ''),
    'move_l_mem': ('move.l (a3),(a4)', ''),
    'mulu':       ('mulu d3,d2', '    move.w #$FFFF,d3\n'),
    'divu':       ('divu d3,d2', '    move.w #1,d3\n'),
    'movem15':    ('movem.l d0-d7/a0-a6,-(a7)\n    lea 60(a7),a7', ''),
    'movem4':     ('movem.l d0-d3,-(a7)\n    lea 16(a7),a7', ''),
    'asl31':      ('asl.l d4,d2', '    moveq #31,d4\n'),
}


def source(x, setup='', k=K, h40=True):
    lines = probes.prologue(h40=h40, display=True, regs={1: 0x74})        # display on, DMA enabled, vertical interrupt on
    return lines + '''
    lea ($C00004).l,a2
    lea ($C00008).l,a1
    lea ($FF0000).l,a6
    lea ($FF0400).l,a3
    lea ($FF0500).l,a4
    lea ($FF1000).l,a5
    moveq #0,d7
    moveq #0,d6
%(setup)s    move.w #$2000,sr
frame:
    move.l #$7FFFFFFF,d2
s1: move.w (a2),d0
    btst #3,d0
    bne.s s1
s2: move.w (a2),d0
    btst #3,d0
    beq.s s2
    move.l d7,d1
    add.l d1,d1
    lea (sled_end).l,a0
    suba.l d1,a0
    jmp (a0)
sled:
    rept 64
    nop
    endr
sled_end:
    move.w (a1),(a5)+
xstart:
    %(x)s
xend:
    rept 160
    nop
    endr
wait:
    tst.w d6
    beq.s wait
    moveq #0,d6
    addq.w #1,d7
    cmpi.w #%(k)d,d7
    bne.w frame
_spin: bra.s _spin
vint:
    move.w (a1),(a6)+
    move.l 2(sp),(a6)+
    moveq #1,d6
    rte
''' % {'x': x, 'setup': setup, 'k': k}


def unpack(w):
    """the RAM words -> (handler HV words, read-before-the-instruction HV words, stacked PCs)"""
    return ([w[3 * i] for i in range(K)], [w[0x800 + i] for i in range(K)], [w[3 * i + 1] << 16 | w[3 * i + 2] for i in range(K)])


def where(pc, syms):
    xs, xm, xe = syms['xstart'], syms.get('xmid', -1), syms['xend']
    if pc == xs: return 'B'                    # accepted before the instruction
    if pc == xm: return 'M'                    # between its two instructions
    if pc == xe: return 'A'                    # after it
    return '?' if pc < xs else '+'             # before the instruction (in the sled) / far after


def run(name, ours_only=False):
    x, setup = CASES[name]
    src = source(x, setup)
    import asm68k
    rom = os.path.join(runprobe.W, "roms", "intlat_" + name + ".bin"); os.makedirs(os.path.dirname(rom), exist_ok=True)
    # make_rom builds without a vector table entry for the interrupt: build it here with vector 30
    syms = {}
    data = asm68k.build_rom(src, vectors={30: 'vint'}, title='INTLAT ' + name, symbols_out=syms)
    run.syms = syms
    open(rom, 'wb').write(data)
    import oracle
    ours_ram = os.path.join(oracle.ROOT, 'out', 'intlat_' + name + '_ours.ram')
    os.makedirs(os.path.dirname(ours_ram), exist_ok=True)
    code, text = oracle.ours(rom, 120, None, ram=ours_ram)
    allw = oracle.ram_words(ours_ram)
    o = unpack(allw)
    if ours_only: return o, None
    out = oracle.blastem(rom, 'intlat_' + name, 'w2500,pause,snap', cache=False)
    st = os.path.join(out, 's000.state')
    b = None
    if os.path.exists(st):
        b = unpack(oracle.state_ram(st))
    return o, b


def hv_clocks(w, h40=True):
    """HV counter word -> (line, tick): the high byte is the V counter, the low byte the H counter"""
    return w >> 8, w & 0xFF


def tick(w, h40=True):
    """HV word -> an index that grows with time inside a line pair (the H counter skips its blanking gap)"""
    v, h = w >> 8, w & 0xFF
    idx = h if h <= 0xB6 else 0xB7 + (h - 0xE4)
    return v * 211 + idx


def deltas(trip):
    h, s, pcs = trip
    return [tick(a) - tick(b) for a, b in zip(h, s)]


if __name__ == '__main__':
    ours_only = '--ours-only' in sys.argv
    names = [a for a in sys.argv[1:] if not a.startswith('--')] or list(CASES)
    for n in names:
        o, b = run(n, ours_only)
        print('== %s: handler entry minus the HV counter read just before the instruction (H ticks of 16 master clocks), by k (NOPs before it)' % n)
        do = deltas(o)
        print('   ours   ', ' '.join('%4d' % x for x in do))
        print('   ours  at', ' '.join('   ' + where(p, run.syms) for p in o[2]), '  (B before the instruction, M between its two, A after it, ? earlier)')
        if b is not None:
            db = deltas(b)
            print('   BlastEm', ' '.join('%4d' % x for x in db))
            print('   BlastEm at', ' '.join('   ' + where(p, run.syms) for p in b[2]))
            xe = run.syms['xend']
            print('   ours  pc-xend (words)', ' '.join('%d' % ((p - xe) // 2) for p in o[2][:48]))
            print('   BlastEm pc-xend (words)', ' '.join('%d' % ((p - xe) // 2) for p in b[2][:48]))
            # in the stretch where the instruction is running when the interrupt comes, the value is (length of the instruction + latency) whatever time the interrupt is raised at
            inside_o = [x for x in do if x > 6]; inside_b = [x for x in db if x > 6]
            if inside_o and inside_b:
                print('   straddling: ours %d values, median %d (min %d max %d); BlastEm %d values, median %d (min %d max %d)' % (
                    len(inside_o), sorted(inside_o)[len(inside_o) // 2], min(inside_o), max(inside_o), len(inside_b), sorted(inside_b)[len(inside_b) // 2], min(inside_b), max(inside_b)))
