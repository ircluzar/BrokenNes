"""Run the SR-lowering probe (probes_b4b.sr_delay) on this emulator and on BlastEm and print, per variant, after how many copies of the second instruction the pending vertical interrupt was taken.

    python run_srdelay.py [--ours-only]        (IPL_SLOTSET="iplmode=1" for an experiment switch of ours; the oracle needs the blastem lock)
"""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import asm68k, oracle, probes_b4b

FRAMES = 30
RUN = 6


def decode(words, syms, variants):
    out = {}
    for i, name in enumerate(variants):
        base = 0x80 + i * FRAMES * 2          # word index of $FF0100
        hist = {}
        for f in range(FRAMES):
            a = (words[base + 2 * f] << 16) | words[base + 2 * f + 1]
            where = None
            for k in range(RUN):
                if a == syms['v%dr%d' % (i, k)]: where = k
            if a == syms['v%de' % i]: where = RUN
            if where is None:
                where = 'other(%X)' % a
            hist[where] = hist.get(where, 0) + 1
        out[name] = hist
    return out


if __name__ == '__main__':
    ours_only = '--ours-only' in sys.argv
    variants = list(probes_b4b.SR_VARIANTS)
    src = probes_b4b.sr_delay(variants, FRAMES, True, RUN)
    syms = {}
    rom = asm68k.build_rom(src, vectors={30: 'handler'}, title='SRDELAY', symbols_out=syms)
    name = 'srdelay'
    os.makedirs(os.path.join(oracle.ROOT, 'roms'), exist_ok=True)
    path = os.path.join(oracle.ROOT, 'roms', name + '.bin')
    open(path, 'wb').write(rom)
    ours_ram = os.path.join(oracle.ROOT, 'out', name + '_ours.ram')
    os.makedirs(os.path.dirname(ours_ram), exist_ok=True)
    extra = ('--slot-set', os.environ['IPL_SLOTSET']) if os.environ.get('IPL_SLOTSET') else ()
    code, text = oracle.ours(path, 600, None, ram=ours_ram, extra=extra)
    o = decode(oracle.ram_words(ours_ram), syms, variants)
    b = None
    if not ours_only:
        out = oracle.blastem(path, name, 'w4000,pause,snap', cache=False)
        st = os.path.join(out, 's000.state')
        if os.path.exists(st): b = decode(oracle.state_ram(st), syms, variants)
    print('after how many copies of the second instruction the interrupt was taken (0 = right after the mask instruction): histogram over %d frames' % FRAMES)
    for v in variants:
        print('%-16s ours %-28s blastem %s' % (v, o[v], None if b is None else b[v]))
