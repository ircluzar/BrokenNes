"""Which words of the Chilly Willy direct colour demo's bitmap are on each line of a screenshot (oracle or ours): dcalign.py shot.png [y_off=9] [image=1..4]   (env BN_DCDMA_SRC = the dmacolor7 folder of dmacolor7-src.zip)
For each picture line L the offset b (in words) that makes pixel x equal the colour of word L*198 + b + x//2 for most x; constant b over the lines = exactly 198 words a line."""
import sys, os, struct
sys.path.insert(0, os.environ['PYTHONPATH'])
from pngio import read_png
shot = sys.argv[1]; yoff = int(sys.argv[2]) if len(sys.argv) > 2 else 9
sp = os.environ.get('BN_DCDMA_SRC') or os.path.join(os.environ['BN_ORACLE_ROOT'], 'testroms', 'dcdma_src', 'dmacolor7')      # the unzipped md-roms/dmacolor7-src.zip: its dmacolor7 folder
L8 = [0, 49, 87, 119, 146, 174, 206, 255]
def rgb(w): return (L8[w >> 1 & 7], L8[w >> 5 & 7], L8[w >> 9 & 7])
img = read_png(shot)[2]
imgs = [int(sys.argv[3])] if len(sys.argv) > 3 else [1, 2, 3, 4]
for k in imgs:
    d = open(os.path.join(sp, 'test%d.dci3' % k), 'rb').read()
    words = struct.unpack('>%dH' % ((len(d) - 4) // 2), d[4:])
    cols = [rgb(w) for w in words]
    res = []
    for Ln in range(20, 210, 10):
        row = img[Ln + yoff]
        best = (0, 0)
        for b in range(-300, 400):
            base = Ln * 198 + b
            if base < 0 or base + 160 >= len(cols): continue
            sc = sum(1 for x in range(320) if row[x][:3] == cols[base + (x >> 1)])
            if sc > best[0]: best = (sc, b)
        res.append((Ln, best[1], best[0]))
    print('image', k, ' '.join('L%d:b%+d(%d)' % r for r in res))
