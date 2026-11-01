"""Vertical scroll probe scenes (Track B4a): plane A only, every pixel row of the plane identifiable from its colour, a different vertical scroll value in every 2-cell column; the oracle's screenshot then
says which VSRAM entry (and what value) each screen column really used. Decode with vsdecode(). Scenes: H40 and H32 without horizontal scroll, H40 and H32 with a fine horizontal scroll, H40 with the left
column blank.

    python vsprobe.py out.bin        (writes out.bin and out.json)

A plane row R (0..255, 32 cells of 8 rows) has colour index (R mod 15) + 1 in the palette line (cell row + 1) mod 4; colour RAM gives (line, index) a unique RGB: red = index & 7, green = (index >> 3) | line << 1.
"""
import json, os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import scenefuzz

L8 = [0, 49, 87, 119, 146, 174, 206, 255]


def vs_values(n=40, second=False):
    """the VSRAM words: plane A's entries (even) 37 c + 11 and plane B's (odd) 53 c + 101 (mod 256: every value distinct within a plane); `second` False keeps the first probe's values"""
    if not second: return [(13 * (i // 2) + 5) % 251 if i % 2 == 0 else 0x155 for i in range(n)]
    return [(37 * (i // 2) + 11) % 256 if i % 2 == 0 else (53 * (i // 2) + 101) % 256 for i in range(n)]


def make(index, h40=True, hfine=0, left_blank=False, v30=False, plane='A', second=False, win=None):
    regs = {0: 0x04 | (0x20 if left_blank else 0), 2: 0x30, 3: 0x20, 4: 0x07, 5: 0x6C, 7: 0, 10: 0xFF, 11: 0x04, 12: 0x81 if h40 else 0, 13: 0x3C, 15: 2, 16: 0x01, 17: win[0] if win else 0, 18: win[1] if win else 0}
    tiles = []
    for n in range(1, 33):
        for r in range(8):
            R = 8 * (n - 1) + r
            d = [1 + ((R >> (2 * (3 - j))) & 3) for j in range(4)]
            px = [15, d[0], d[1], d[2], d[3], 14, 14, 14]
            tiles += [px[0] << 12 | px[1] << 8 | px[2] << 4 | px[3], px[4] << 12 | px[5] << 8 | px[6] << 4 | px[7]]
    nt_a = []
    for cy in range(32):
        for cx in range(64): nt_a.append(cy + 1)
    nt_b = [0] * 2048
    cram = [0] * 64
    for c in range(1, 16): cram[c] = ((c & 7) << 1) | ((c >> 3) << 5) | ((c & 3) << 9)       # a distinct colour per index
    s = {'index': index, 'family': 'vsprobe', 'h40': h40, 'v30': v30, 'interlace': 0, 'shadow': False, 'regs': regs, 'vsram': vs_values(second=second), 'cram': cram,
         'sat': bytes(640), 'sat_addr': (0x6C & 0x7E) << 9, 'hscroll': bytes([hfine >> 8 & 3, hfine & 255, 0, 0]) if plane == 'A' else bytes([0, 0, hfine >> 8 & 3, hfine & 255]), 'hs_addr': 0x3C << 10, 'window': [0, 0], 'window_on': False, 'dma_window': index % 8,
         'extra_writes': [(0x0, [0] * 16), (0x8000, [0] * 2048), (0x20, tiles), (0xC000, nt_a if plane == 'A' else [0] * 2048), (0xE000, nt_b if plane == 'A' else nt_a)], 'hfine': hfine, 'left_blank': left_blank, 'plane': plane, 'second': second, 'win': win}
    return s


def cram_rgb(c):
    """the RGB (3-bit levels as DAC steps) colour index c shows in the probe's palette"""
    return (c & 7, c >> 3, c & 3)


def decode_row(pixels):
    """pixels: list of (r, g, b) 0-255 along one screen row. Returns a list of (x, R) for every complete marker (15, four digits, 14) found: the plane row R that the tile row at that x belongs to."""
    lv = lambda v: min(range(8), key=lambda i: abs(L8[i] - v))
    want = {cram_rgb(c): c for c in range(1, 16)}
    idx = []
    for p in pixels:
        k = (lv(p[0]), lv(p[1]), lv(p[2]))
        idx.append(want.get(k, 0))
    out = []
    for x in range(len(idx) - 5):
        if idx[x] == 15 and idx[x + 5] == 14 and all(1 <= idx[x + 1 + j] <= 4 for j in range(4)):
            R = 0
            for j in range(4): R = R << 2 | (idx[x + 1 + j] - 1)
            out.append((x, R))
    return out


def scenes2():
    s = []
    for h40, fine, plane in [(True, 1, 'A'), (True, 3, 'A'), (True, 5, 'A'), (True, 7, 'A'), (True, 29, 'A'), (False, 1, 'A'), (False, 5, 'A'), (False, 7, 'A'), (True, 5, 'B'), (False, 5, 'B'), (True, 0, 'B')]:
        s.append(make(len(s), h40, fine, plane=plane, second=True))
    s.append(make(len(s), True, 5, left_blank=True, second=True))
    return s


def scenes3():
    s = []
    for fine in (0, 5, 13, 21):
        for wn in (1, 2, 3):
            s.append(make(len(s), True, fine, second=True, win=(wn, 0)))              # a window on the left, 16 * wn pixels wide, full height (R18 = 0: no vertical part)
    for wn in (1, 2):
        s.append(make(len(s), True, 5, second=True, win=(0x80 | (20 - wn), 0)))        # a window on the right, from x = 16 * (20 - wn)
    return s


def main():
    out = sys.argv[1]
    if len(sys.argv) > 2 and sys.argv[2] == '3':
        scenes = scenes3()
        scenefuzz.build(scenes, out)
        meta = [{k: v for k, v in s.items() if k not in ('sat', 'hscroll', 'cram', 'extra_writes')} for s in scenes]
        json.dump(meta, open(os.path.splitext(out)[0] + '.json', 'w'), indent=1)
        print('wrote', out, len(scenes), 'scenes (probe 3: window)'); return
    if len(sys.argv) > 2 and sys.argv[2] == '2':
        scenes = scenes2()
        scenefuzz.build(scenes, out)
        meta = [{k: v for k, v in s.items() if k not in ('sat', 'hscroll', 'cram', 'extra_writes')} for s in scenes]
        json.dump(meta, open(os.path.splitext(out)[0] + '.json', 'w'), indent=1)
        print('wrote', out, len(scenes), 'scenes (probe 2)'); return
    scenes = [make(0, True, 0), make(1, False, 0), make(2, True, 13), make(3, False, 13), make(4, True, 0, left_blank=True)]
    scenefuzz.build(scenes, out)
    meta = [{k: v for k, v in s.items() if k not in ('sat', 'hscroll', 'cram', 'extra_writes')} for s in scenes]
    json.dump(meta, open(os.path.splitext(out)[0] + '.json', 'w'), indent=1)
    print('wrote', out, len(scenes), 'scenes')


if __name__ == '__main__':
    main()
