"""Where does the 68000 take an interrupt, instruction by instruction? (Track B4b; the companion of intlat.py with many more samples and no timing sync.)

The horizontal interrupt fires every line (register 10 = 0), 224 times a frame; its handler stores the return address that the interrupt pushed (the address of the instruction that would have run next) and
returns. The main program is a loop of one instruction under test followed by NOPs. The lines do not divide the loop, so over a few frames the interrupts arrive at every phase of the loop; the histogram of
return addresses says, for each instruction of the loop, how much of the loop's time ended in an interrupt taken right after it. If an interrupt is taken at the end of the instruction it arrives in, the long
instruction (in cycles) gets the weight of its own length at the address after it; a rule that takes it one instruction later moves that weight one instruction on. Both emulators are shown the same ROM.

    python inthist.py                 # all loops, ours and BlastEm (hold the oracle lock)
    python inthist.py movem15 --ours-only
"""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import probes, runprobe, asm68k, oracle

NOPS = 40
SAMPLES = 1400

CASES = {
    'nop':      ('nop', '', 4),
    'move_l':   ('move.l (a3),(a4)', '', 20),
    'asl31':    ('asl.l d4,d2', '    moveq #31,d4\n', 70),
    'asl15':    ('asl.l d4,d2', '    moveq #15,d4\n', 38),
    'mulu':     ('mulu d3,d2', '    move.w #$FFFF,d3\n', 70),
    'divu':     ('divu d3,d2', '    move.w #1,d3\n', 136),
    'movem4':   ('movem.l d0-d3,-(a7)\n    lea 16(a7),a7', '', 44),
    'movem15':  ('movem.l d0-d7/a0-a6,-(a7)\n    lea 60(a7),a7', '', 140),
    'move_to_mem': ('move.l d2,(a4)', '', 12),
    'add_to_mem':  ('add.l d2,(a4)', '', 20),
    'clr_l':       ('clr.l (a4)', '', 20),
    'move_w_pre':  ('move.w d2,-(a4)\n    addq.l #2,a4', '', 8),
    'movem_load':  ('movem.l (a7)+,d0-d7/a0-a6\n    lea -60(a7),a7', '', 140),
    'pea':         ('pea (a3)\n    addq.l #4,a7', '', 12),
}


def source(x, setup):
    lines = probes.prologue(h40=True, display=True, regs={0: 0x14, 1: 0x54, 10: 0})      # horizontal interrupt every line, no vertical one
    return lines + '''
    lea ($FF1000).l,a6
    lea ($FF%(end)04X).l,a5
    lea ($FF0400).l,a3
    lea ($FF0500).l,a4
    move.l #$7FFFFFFF,d2
%(setup)s    move.w #$2000,sr
loop:
x0:
    %(x)s
x1:
    rept %(n)d
    nop
    endr
    bra.w loop
_spin: bra.s _spin
hint:
    cmpa.l a5,a6
    bcc.s hdone
    move.l 2(sp),(a6)+
hdone:
    rte
''' % {'x': x, 'setup': setup, 'n': NOPS, 'end': 0x1000 + SAMPLES * 4}


def returns(words):
    return [words[0x800 + 2 * i] << 16 | words[0x800 + 2 * i + 1] for i in range(SAMPLES)]


def run(name, ours_only=False, frames=12):
    x, setup, _ = CASES[name]
    src = source(x, setup)
    syms = {}
    data = asm68k.build_rom(src, vectors={28: 'hint'}, title='INTHIST ' + name, symbols_out=syms)
    rom = os.path.join(runprobe.W, 'roms', 'inthist_' + name + '.bin'); os.makedirs(os.path.dirname(rom), exist_ok=True)
    open(rom, 'wb').write(data)
    ours_ram = os.path.join(oracle.ROOT, 'out', 'inthist_' + name + '_ours.ram'); os.makedirs(os.path.dirname(ours_ram), exist_ok=True)
    extra = ['--slot-set', os.environ['INTHIST_SLOTSET']] if os.environ.get('INTHIST_SLOTSET') else []
    oracle.ours(rom, frames, None, ram=ours_ram, extra=extra)
    o = returns(oracle.ram_words(ours_ram))
    if ours_only: return syms, o, None
    out = oracle.blastem(rom, 'inthist_' + name, 'w3000,pause,snap', cache='--refresh' not in sys.argv)
    st = os.path.join(out, 's000.state')
    b = returns(oracle.state_ram(st)) if os.path.exists(st) else None
    return syms, o, b


def histogram(pcs, x1, n_instr=6):
    """counts of return addresses by instruction index after the instruction under test: 0 = x1 (taken right after it), 1 = after the first NOP, ... and -1 = taken before it (its own address or earlier)"""
    from collections import Counter
    c = Counter()
    for p in pcs:
        if p == 0: continue
        c[(p - x1) // 2] += 1
    return c


if __name__ == '__main__':
    ours_only = '--ours-only' in sys.argv
    names = [a for a in sys.argv[1:] if not a.startswith('--')] or list(CASES)
    for n in names:
        syms, o, b = run(n, ours_only)
        x1 = syms['x1']; x0 = syms['x0']
        # the instruction(s) under test span x0..x1; index 0 is the first address after them
        ho = histogram(o, x1)
        hb = histogram(b, x1) if b else None
        keys = sorted(set(ho) | set(hb or {}))
        keys = [k for k in keys if k <= 6]
        print('== %s (instruction at %X..%X, %d NOPs after; return address = index of the NOP after which the interrupt was taken, 0 = right after the instruction(s) under test, negative = inside them)' % (n, x0, x1, NOPS))
        print('   index      ' + ' '.join('%5d' % k for k in keys))
        print('   ours       ' + ' '.join('%5d' % ho.get(k, 0) for k in keys) + '   total %d' % sum(ho.values()))
        if hb is not None: print('   BlastEm    ' + ' '.join('%5d' % hb.get(k, 0) for k in keys) + '   total %d' % sum(hb.values()))
