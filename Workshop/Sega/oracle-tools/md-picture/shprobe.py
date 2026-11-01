"""Shadow/highlight sprite probe scenes (Track B4a): sprites of one colour at many X positions, one per row band, over empty or filled planes, so the brightness BlastEm gives a sprite pixel can be read per position.
Scenes: 0 low-priority sprites over empty planes; 1 high-priority sprites over empty planes; 2 low-priority sprites over low-priority plane A pixels; 3 low-priority sprites over high-priority plane A pixels;
4 the same as 0 with a fine horizontal scroll (so that the plane cells are misaligned); 5 as 0 in H32.

    python shprobe.py out.bin
"""
import json, os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import scenefuzz

XS = [0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 28, 31, 32, 33, 36, 40, 48, 64, 100]
SPR_PAL, SPR_COL = 1, 5


def make(index, prio=False, plane_fill=None, hfine=0, h40=True):
    regs = {0: 0x04, 2: 0x30, 3: 0x20, 4: 0x07, 5: 0x6C, 7: 0, 10: 0xFF, 11: 0x00, 12: (0x81 if h40 else 0) | 8, 13: 0x3C, 15: 2, 16: 0x01, 17: 0, 18: 0}
    tiles = []
    for n in range(1, 3):
        for r in range(8):
            c = 3 if n == 1 else SPR_COL
            tiles += [c << 12 | c << 8 | c << 4 | c, c << 12 | c << 8 | c << 4 | c]
    nt_a = [0] * 2048
    if plane_fill is not None:
        for i in range(2048): nt_a[i] = 1 | (0x8000 if plane_fill == 'hi' else 0)          # tile 1 (colour 3 of palette 0), priority bit as asked
    sat = bytearray(640)
    for i, x in enumerate(XS):
        y = 4 + 10 * i
        base = i * 8
        ypos = y + 128; xpos = x + 128
        sat[base:base + 2] = bytes([ypos >> 8 & 3, ypos & 255]); sat[base + 2] = 0; sat[base + 3] = i + 1 if i + 1 < len(XS) else 0
        attr = 2 | SPR_PAL << 13 | (0x8000 if prio else 0)
        sat[base + 4:base + 6] = bytes([attr >> 8, attr & 255]); sat[base + 6:base + 8] = bytes([xpos >> 8 & 1, xpos & 255])
    cram = [0] * 64
    cram[0 * 16 + 3] = 0x0E40            # plane colour
    cram[SPR_PAL * 16 + SPR_COL] = 0x04E8      # sprite colour: R4 G6 B2 in 3-bit steps
    cram[63] = 0
    cram[0] = 0x0222
    return {'index': index, 'family': 'shprobe', 'h40': h40, 'v30': False, 'interlace': 0, 'shadow': True, 'regs': regs, 'vsram': [0] * 40, 'cram': cram, 'sat': bytes(sat), 'sat_addr': (0x6C & 0x7E) << 9,
            'hscroll': bytes([hfine >> 8 & 3, hfine & 255, 0, 0]), 'hs_addr': 0x3C << 10, 'window': [0, 0], 'window_on': False, 'dma_window': index % 8,
            'extra_writes': [(0x0, [0] * 16), (0x20, tiles), (0xC000, nt_a), (0xE000, [0] * 2048)], 'prio': prio, 'plane_fill': plane_fill, 'hfine': hfine}


def scenes():
    return [make(0), make(1, prio=True), make(2, plane_fill='lo'), make(3, plane_fill='hi'), make(4, hfine=5), make(5, h40=False)]


def main():
    out = sys.argv[1]
    sc = scenes()
    scenefuzz.build(sc, out)
    meta = [{k: v for k, v in s.items() if k not in ('sat', 'hscroll', 'cram', 'extra_writes')} for s in sc]
    json.dump(meta, open(os.path.splitext(out)[0] + '.json', 'w'), indent=1)
    print('wrote', out, len(sc), 'scenes')


if __name__ == '__main__':
    main()
