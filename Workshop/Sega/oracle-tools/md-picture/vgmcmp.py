"""Two VGM logs of the same game, ours (`--md-run --vgm`) and BlastEm's (its VGM logger, `vgmlog.ps1`): are the FM notes the same, and when do they fall?

    python vgmcmp.py ours.vgm blastem.vgm [--steps]     (or --batch DIR [--ours NAME]: DIR\\vNN\\ours.vgm against DIR\\vNN\\blastem.vgm for every NN found)

The two logs do not begin at the same instant (BlastEm's logger is switched on by a key press after about a second), so the FM key-ons (channel, operator mask, F-number and block, in order) are aligned: the offset k
(ours skips k key-ons, or BlastEm skips -k) with the longest run of identical notes is found, and over that run the timing is the difference of the intervals from the first matched key-on, in milliseconds
(positive: ours falls later). `--steps` lists where that difference changes by a whole frame (16.7 ms): a step is one emulator's scene or fade ending a frame earlier or later than the other's, not a wrong note.
This is the comparison behind the sound table of `docs/projects/sega-track-b4b.md`; `--ym-render --compare-logs` in the Workshop does the same without the alignment search.
"""
import os, struct, sys


def parse(path):
    d = open(path, 'rb').read()
    off = struct.unpack('<I', d[0x34:0x38])[0] + 0x34 if len(d) > 0x38 else 0x40
    if off == 0x34: off = 0x40
    i = off; t = 0; ev = []
    while i < len(d):
        c = d[i]
        if c in (0x52, 0x53): ev.append((t, c - 0x52, d[i + 1], d[i + 2])); i += 3
        elif c == 0x50: ev.append((t, 'psg', d[i + 1], 0)); i += 2
        elif c == 0x61: t += struct.unpack('<H', d[i + 1:i + 3])[0]; i += 3
        elif c == 0x62: t += 735; i += 1
        elif c == 0x63: t += 882; i += 1
        elif 0x70 <= c <= 0x7F: t += (c & 15) + 1; i += 1
        elif 0x80 <= c <= 0x8F: t += c & 15; ev.append((t, 0, 0x2A, 'bank')); i += 1
        elif c == 0x66: break
        elif c == 0x67: i += 7 + struct.unpack('<I', d[i + 3:i + 7])[0]
        elif c == 0xE0: i += 5
        else: i += 1
    return ev


def notes(path):
    low = [0] * 6; high = [0] * 6; out = []
    for (t, p, r, v) in parse(path):
        if p == 'psg' or v == 'bank': continue
        if r in (0xA0, 0xA1, 0xA2): low[r - 0xA0 + 3 * p] = v
        elif r in (0xA4, 0xA5, 0xA6): high[r - 0xA4 + 3 * p] = v
        elif p == 0 and r == 0x28 and (v & 0xF0):
            c = v & 7
            if (c & 3) == 3: continue
            ch = (c & 3) + (3 if c & 4 else 0)
            out.append((t, ch, v >> 4, (high[ch] & 7) << 8 | low[ch], high[ch] >> 3 & 7))
    return out


def align(a, b):
    na = [x[1:] for x in a]; nb = [x[1:] for x in b]
    best = (0, 0)
    for k in range(-40, 41):
        run = 0
        while True:
            ia, ib = (run + k, run) if k >= 0 else (run, run - k)
            if ia >= len(na) or ib >= len(nb) or na[ia] != nb[ib]: break
            run += 1
        if run > best[0]: best = (run, k)
    return best


def timing(a, b, run, k):
    ds = []
    for i in range(run):
        ia, ib = (i + k, i) if k >= 0 else (i, i - k)
        ds.append(((a[ia][0] - a[max(k, 0)][0]) - (b[ib][0] - b[max(-k, 0)][0])) / 44.1)
    return ds


def one(po, pb, steps=False, label=''):
    a = notes(po); b = notes(pb)
    run, k = align(a, b)
    if run == 0: print('%s ours %d, BlastEm %d key-ons: no alignment (the first notes differ)' % (label, len(a), len(b))); return
    comp = min(len(a) - max(k, 0), len(b) - max(-k, 0))
    ds = timing(a, b, run, k)
    print('%s ours %d, BlastEm %d key-ons: from k=%d, %d of %d identical (%.1f%%), timing mean |d| %.2f ms, largest %.2f ms' % (label, len(a), len(b), k, run, comp, 100.0 * run / max(1, comp), sum(abs(d) for d in ds) / len(ds), max(ds, key=abs)))
    if steps:
        prev = None
        for i, d in enumerate(ds):
            lvl = round(d / 16.7)
            if lvl != prev:
                print('   key-on %d: ours - BlastEm = %.2f ms (%d frames)' % (i, d, lvl)); prev = lvl


if __name__ == '__main__':
    args = [x for x in sys.argv[1:] if not x.startswith('--')]
    if '--batch' in sys.argv:
        root = args[0]; name = sys.argv[sys.argv.index('--ours') + 1] if '--ours' in sys.argv else 'ours.vgm'
        for d in sorted(os.listdir(root)):
            po = os.path.join(root, d, name); pb = os.path.join(root, d, 'blastem.vgm')
            if os.path.exists(po) and os.path.exists(pb): one(po, pb, '--steps' in sys.argv, d)
    else:
        one(args[0], args[1], '--steps' in sys.argv)
