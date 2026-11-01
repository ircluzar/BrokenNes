"""Build a probe ROM, run it on this emulator and on BlastEm, compare. Library used by the probe batch scripts.

    from runprobe import *
    r = shot_probe('dots_h40', probes.dma_dots(True, True, 4000))          # -> ProbeResult with .differing_rows, .ours_png, .blast_png
    r = ram_probe('hv_h40', probes.hv_scan(True, 3000))                     # -> (ours words, blastem words)
"""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import asm68k, probes, oracle, slotmap, slotcmp

W = oracle.ROOT


def make_rom(name, src, region='JUE', **kw):
    rom = asm68k.build_rom(src, title=name[:40], region=region, **kw)
    os.makedirs(os.path.join(W, 'roms'), exist_ok=True)
    p = os.path.join(W, 'roms', name + '.bin')
    open(p, 'wb').write(rom)
    return p


class ProbeResult:
    def __init__(self, **kw): self.__dict__.update(kw)


def shot_probe(name, src, frames=120, pal=False, wait_ms=1500, dy=None, width=None, region_letters=None, skip_blastem=False, startup_ms=2500):
    """picture probe: our last frame against BlastEm's screenshot; dy = BlastEm's shot row of our row 0 (9 for NTSC)"""
    region = region_letters or ('E' if pal else 'JUE')
    p = make_rom(name, src, region)
    os.makedirs(os.path.join(W, 'out'), exist_ok=True)
    ours_png = os.path.join(W, 'out', name + '_ours.png')
    code, text = oracle.ours(p, frames, 'pal' if pal else None, png=ours_png)
    if skip_blastem: return ProbeResult(ours_png=ours_png, blast_png=None, differing_rows=None, text=text)
    out = oracle.blastem(p, name, 'w%d,shot' % wait_ms, startup_ms=startup_ms)
    bp = os.path.join(out, 's000.png')
    if not os.path.exists(bp): return ProbeResult(ours_png=ours_png, blast_png=None, differing_rows=None, text=text + ' (no BlastEm shot)')
    if dy is None: dy = 9
    bad = slotcmp.compare(ours_png, bp, dy, quiet=True, width=width)
    return ProbeResult(ours_png=ours_png, blast_png=bp, differing_rows=bad, text=text, dy=dy)


def ram_probe(name, src, frames=200, pal=False, wait_ms=1500, region_letters=None):
    region = region_letters or ('E' if pal else 'JUE')
    p = make_rom(name, src, region)
    return oracle.both_ram(p, name, frames=frames, region='pal' if pal else None, wait_ms=wait_ms)
