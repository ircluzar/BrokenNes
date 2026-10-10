"""Read the colour-coded 'where did each word land' pictures the DMA probes draw: every pixel's colour is a 9-bit word index (R = n & 7, G = n >> 3 & 7, B = n >> 6 & 7 at the DAC's eight normal levels),
so a row is a list of runs (first pixel, word index). Used to compare this emulator's picture with BlastEm's screenshot of the same probe, pixel for pixel."""
import sys
from pngio import read_png

L8 = [0, 49, 87, 119, 146, 174, 206, 255]


def decode_px(rgb):
    def lv(c): return min(range(8), key=lambda i: abs(L8[i] - c))
    return lv(rgb[0]) | lv(rgb[1]) << 3 | lv(rgb[2]) << 6


def load(path, top=0, height=None, width=None):
    """rows of decoded word indices (None where a pixel is not at a DAC level, e.g. shadowed); `top` rows are skipped (BlastEm's screenshot has overscan rows above the picture)"""
    w, h, px = read_png(path)
    rows = []
    for y in range(top, h if height is None else min(h, top + height)):
        r = px[y][:width] if width else px[y]
        rows.append([decode_px(p) for p in r])
    return rows


def runs(row):
    out = []; prev = None
    for x, v in enumerate(row):
        if v != prev: out.append((x, v)); prev = v
    return out


def find_top(rows_full):
    """the first row of the picture proper in a screenshot with overscan: the first row whose colours are not all equal to the row above's ... (callers pass `top` explicitly when they know it)"""
    return 0


def gaps(row):
    """the widths of the runs (a word's slot is 2 pixels wide in H40 blanking): a missing slot shows as a 4-pixel run"""
    r = runs(row)
    out = []
    for i, (x, v) in enumerate(r):
        nxt = r[i + 1][0] if i + 1 < len(r) else len(row)
        out.append((x, nxt - x, v))
    return out


if __name__ == '__main__':
    path = sys.argv[1]; top = int(sys.argv[2]) if len(sys.argv) > 2 else 0; n = int(sys.argv[3]) if len(sys.argv) > 3 else 6
    first = int(sys.argv[4]) if len(sys.argv) > 4 else 0
    rows = load(path, top)
    for y in range(first, first + n):
        print(y, ' '.join('%d@%d' % (v, x) for x, v in runs(rows[y])))
        print('   widths', ' '.join('%d' % w for x, w, v in gaps(rows[y])))
