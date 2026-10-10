"""Compare this emulator's picture with BlastEm's screenshot of the same probe, row by row, and say exactly where they differ (as run lists of word indices).
usage: python slotcmp.py ours.png blastem.png [dy] [rows]   (dy: BlastEm's picture row of our row 0, 9 for NTSC H40/H32 V28; PAL V30 differs: pass the value)"""
import sys
import slotmap


def compare(ours_png, blast_png, dy=9, maxrows=None, quiet=False, width=None):
    a = slotmap.load(ours_png, 0)
    b = slotmap.load(blast_png, dy, len(a))
    n = min(len(a), len(b), maxrows or 10 ** 9)
    bad_rows = []
    for y in range(n):
        ra, rb = a[y], b[y]
        if width: ra, rb = ra[:width], rb[:width]
        if ra != rb:
            bad_rows.append(y)
    if not quiet:
        print('rows compared %d, differing rows %d' % (n, len(bad_rows)))
        for y in bad_rows[:8]:
            ra, rb = a[y][:width] if width else a[y], b[y][:width] if width else b[y]
            xs = [x for x in range(min(len(ra), len(rb))) if ra[x] != rb[x]]
            print(' row %3d: %d pixels differ, first at x=%d' % (y, len(xs), xs[0] if xs else -1))
            print('   ours  %s' % ' '.join('%s@%d' % (v, x) for x, v in slotmap.runs(ra)[:22]))
            print('   blast %s' % ' '.join('%s@%d' % (v, x) for x, v in slotmap.runs(rb)[:22]))
    return bad_rows


if __name__ == '__main__':
    compare(sys.argv[1], sys.argv[2], int(sys.argv[3]) if len(sys.argv) > 3 else 9, int(sys.argv[4]) if len(sys.argv) > 4 else None)
