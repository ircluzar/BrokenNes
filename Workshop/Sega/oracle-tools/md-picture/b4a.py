"""Track B4a driver: build a probe ROM, run it on this emulator and on BlastEm 1.0.0, return both work RAMs as lists of words.

    import b4a
    ours, theirs = b4a.ram_both('vflag_h40', probes_b4a.vint_flag_stream(200), vectors={30: 'handler'}, frames=300)

Environment (as oracle.py): BN_ORACLE_ROOT = the private working folder holding blastem\\ (screenshots to be_shots, saves to be_save), BN_ARTIFACTS = the --artifacts-path the Workshop was built with.
BlastEm is a window program: hold the oracle lock (Workshop\\Sega\\oracle-lock.ps1 -Acquire blastem -Owner b4a) while any function here that runs it is called.
"""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import asm68k, oracle

W = oracle.ROOT


def make_rom(name, src, vectors=None, region='JUE', **kw):
    rom = asm68k.build_rom(src, vectors=vectors, title=name[:40], region=region, **kw)
    os.makedirs(os.path.join(W, 'roms'), exist_ok=True)
    p = os.path.join(W, 'roms', name + '.bin')
    open(p, 'wb').write(rom)
    return p


def ram_ours(name, src, vectors=None, frames=300, region=None, region_letters='JUE', extra=(), **kw):
    """Run on this emulator only; returns the 32768 RAM words."""
    p = make_rom(name, src, vectors, region_letters, **kw)
    os.makedirs(os.path.join(W, 'out'), exist_ok=True)
    ram = os.path.join(W, 'out', name + '_ours.ram')
    code, text = oracle.ours(p, frames, region, ram=ram, extra=extra)
    if code != 0: raise RuntimeError('workshop failed: %s %s' % (code, text[-500:]))
    return oracle.ram_words(ram)


def ram_theirs(name, src, vectors=None, wait_ms=6000, region_letters='JUE', cache=True, **kw):
    """Run on BlastEm only (paused after wait_ms of running, quick-save read); returns the 32768 RAM words, or None."""
    p = make_rom(name, src, vectors, region_letters, **kw)
    out = oracle.blastem(p, name, 'w%d,pause,snap' % wait_ms, cache=cache)
    st = os.path.join(out, 's000.state')
    return oracle.state_ram(st) if os.path.exists(st) else None


def ram_both(name, src, vectors=None, frames=300, wait_ms=6000, region=None, region_letters='JUE', extra=(), cache=True, **kw):
    return ram_ours(name, src, vectors, frames, region, region_letters, extra, **kw), ram_theirs(name, src, vectors, wait_ms, region_letters, cache, **kw)
