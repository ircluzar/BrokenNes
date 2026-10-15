"""Run the B4b interrupt-pending probes (probes_b4b.py) on this emulator and on BlastEm, print the result words side by side.

    python run_b4b.py [variants]            e.g. python run_b4b.py ABDEF
Needs BN_ORACLE_ROOT / BN_ARTIFACTS (see oracle.py) and the blastem lock (Workshop\\Sega\\oracle-lock.ps1 -Acquire blastem). -o: our side only (no window, no lock).
"""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import asm68k, oracle, probes_b4b

ours_only = '-o' in sys.argv
vs = [a for a in sys.argv[1:] if not a.startswith('-')]
variants = vs[0] if vs else 'ABDEF'
for v in variants:
    name = 'vintenable_' + v
    src = probes_b4b.vint_enable(v)
    rom = asm68k.build_rom(src, title=name, vectors={30: 'vint', 28: 'hint'})
    os.makedirs(os.path.join(oracle.ROOT, 'roms'), exist_ok=True)
    path = os.path.join(oracle.ROOT, 'roms', name + '.bin')
    open(path, 'wb').write(rom)
    if ours_only:
        out = os.path.join(oracle.ROOT, 'out', name + '_ours.ram')
        os.makedirs(os.path.dirname(out), exist_ok=True)
        code, text = oracle.ours(path, 120, None, ram=out)
        o = oracle.ram_words(out)
        b = None
    else:
        o, b = oracle.both_ram(path, name, frames=120)
    show = lambda w: None if w is None else [w[0x80 + k] for k in (0, 1, 2, 3, 4)]
    print('%s: ours %s   blastem %s' % (v, show(o), show(b)))
