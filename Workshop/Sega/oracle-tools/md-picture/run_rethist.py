"""After which instruction of a small loop does the 68000 take a horizontal interrupt? Ours (any slot-set in IPL_SLOTSET) against BlastEm, as fractions of the interrupts.

    python run_rethist.py [loop names] [--ours-only]

The interrupts arrive at every phase of the loop (a line is 488.57 cycles, the loops are a few dozen), so the fraction taken after an instruction is the share of the loop's time in which an interrupt ends up
there. Needs the blastem lock for the oracle side; `--ours-only` needs none.
"""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import asm68k, oracle, probes_b4b

N = 4000


def decode(words, syms, ninstr):
    cnt = words[0]
    addrs = []
    for i in range(min(cnt, N)):
        addrs.append((words[2 + 2 * i] << 16) | words[3 + 2 * i])
    # the label addresses; the return address is the instruction that was NOT executed: label i_k means "taken after instruction k-1"
    names = ['i%d' % k for k in range(ninstr)] + ['bra']
    hist = {}
    for a in addrs:
        w = None
        for nm in names:
            if syms[nm] == a: w = nm
        if w is None: w = 'other:%X' % a
        hist[w] = hist.get(w, 0) + 1
    tot = len(addrs)
    return tot, hist


def run(body, ours_only):
    syms = {}
    src = probes_b4b.ret_hist(body, N)
    rom = asm68k.build_rom(src, vectors={28: 'hint'}, title='RET ' + body, symbols_out=syms)
    name = 'rethist_' + body
    os.makedirs(os.path.join(oracle.ROOT, 'roms'), exist_ok=True)
    path = os.path.join(oracle.ROOT, 'roms', name + '.bin')
    open(path, 'wb').write(rom)
    ram = os.path.join(oracle.ROOT, 'out', name + '_ours.ram')
    os.makedirs(os.path.dirname(ram), exist_ok=True)
    extra = ('--slot-set', os.environ['IPL_SLOTSET']) if os.environ.get('IPL_SLOTSET') else ()
    oracle.ours(path, 400, None, ram=ram, extra=extra)
    o = decode(oracle.ram_words(ram), syms, len(probes_b4b.RET_LOOPS[body]))
    b = None
    if not ours_only:
        out = oracle.blastem(path, name, 'w3500,pause,snap', cache=True)
        st = os.path.join(out, 's000.state')
        if os.path.exists(st): b = decode(oracle.state_ram(st), syms, len(probes_b4b.RET_LOOPS[body]))
    return o, b


def fmt(r):
    if r is None: return 'none'
    tot, h = r
    return ' '.join('%s %.3f' % (k, v / tot) for k, v in sorted(h.items())) + '  (n=%d)' % tot


if __name__ == '__main__':
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    ours_only = '--ours-only' in sys.argv
    for nm in (args or list(probes_b4b.RET_LOOPS)):
        o, b = run(nm, ours_only)
        print('%-12s ours    %s' % (nm, fmt(o)))
        if b is not None: print('%-12s BlastEm %s' % ('', fmt(b)))
