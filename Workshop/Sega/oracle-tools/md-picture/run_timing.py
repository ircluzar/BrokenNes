"""How many 68000 cycles does an instruction take, on the oracle and on this emulator (HV counter before and after straight-line copies of it, probes_b4b.timing_run)?

    python run_timing.py [loop names] [--ours-only]

Prints per body the master clocks per copy (the difference between two stretches of copies, so the reads' own cost cancels). A body that differs between the two machines would also have a different loop period in the
interrupt experiments (run_rethist.py). Needs the blastem lock for the oracle side.
"""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import asm68k, oracle, probes_b4b

LEN = {'nop': 4, 'tst_abs': 12, 'tst_abs2': 24, 'move_b_abs': 12, 'move': 16, 'addq_reg': 4, 'mulu': 70, 'divu': 140, 'clr_l': 20, 'add_rmw': 20, 'jsr_rts': 36, 'movem_out': 72, 'movem_in': 76, 'movem_short': 24, 'lsl8': 24,
       'move_l_mem': 20, 'pea': 20, 'btst_reg': 10, 'cmp_mem': 8}


def counts(body):
    # 40 lines of 488.57 cycles in the first stretch, 160 in the second
    L = LEN.get(body, 10)
    n1 = max(2, int(40 * 488.57 / L)); return n1, 4 * n1


def vlines(w):
    return w & 0xFF


def run(body, ours_only):
    n1, n2 = counts(body)
    src = probes_b4b.timing_run(body, n1, n2)
    rom = asm68k.build_rom(src, title='TIM ' + body)
    name = 'timing_' + body
    os.makedirs(os.path.join(oracle.ROOT, 'roms'), exist_ok=True)
    path = os.path.join(oracle.ROOT, 'roms', name + '.bin')
    open(path, 'wb').write(rom)
    ram = os.path.join(oracle.ROOT, 'out', name + '_ours.ram')
    os.makedirs(os.path.dirname(ram), exist_ok=True)
    oracle.ours(path, 30, None, ram=ram)
    o = oracle.ram_words(ram)
    b = None
    if not ours_only:
        out = oracle.blastem(path, name, 'w1500,pause,snap', cache=False)
        st = os.path.join(out, 's000.state')
        if os.path.exists(st): b = oracle.state_ram(st)
    return o, b


if __name__ == '__main__':
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    ours_only = '--ours-only' in sys.argv
    for nm in (args or list(probes_b4b.RET_LOOPS)):
        o, b = run(nm, ours_only)
        body = [i for i in probes_b4b.RET_LOOPS[nm] if not i.startswith('bra')]
        # elapsed1 = N1 copies, elapsed2 = N2 copies: per copy = (e2 - e1) / (N2 - N1)
        n1, n2 = counts(nm)
        def pc(w):
            a, bb, c = (vlines(w[i]) for i in range(3))
            return ((c - bb) - (bb - a)) * 3420.0 / (n2 - n1) / len(body)       # master clocks per instruction of the body (a line is 3420 clocks)
        print('%-12s ours %.2f clocks (%.2f cycles) per instruction%s' % (nm, pc(o), pc(o) / 7, '' if b is None else '   BlastEm %.2f clocks (%.2f cycles)' % (pc(b), pc(b) / 7)))
