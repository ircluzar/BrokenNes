"""regcmp.py oursdir theirsdir ysplit [yoff]: every oracle picture against every one of ours, differing pixels above and below the row ysplit (a picture from the frames' two layers: which of ours matches which layer of theirs)"""
import sys, os
sys.path.insert(0, os.environ['PYTHONPATH'])
from pngio import read_png
od, td, ys = sys.argv[1], sys.argv[2], int(sys.argv[3])
yo = int(sys.argv[4]) if len(sys.argv) > 4 else 9
ours = sorted(f for f in os.listdir(od) if f.startswith('o') and f.endswith('.png'))
theirs = sorted(f for f in os.listdir(td) if f.startswith('s') and f.endswith('.png'))
O = []
for f in ours:
    w, h, p = read_png(os.path.join(od, f)); O.append((f, w, h, p))
T = []
for f in theirs:
    w, h, p = read_png(os.path.join(td, f)); T.append((f, w, h, p))
print('rows: oracle picture; columns: ours', [o[0][1:6] for o in O])
for tf, tw, th, tp in T:
    row = []
    for of, ow, oh, op in O:
        a = b = 0
        for y in range(oh):
            orow, trow = op[y], tp[y + yo]
            for x in range(min(ow, tw)):
                if orow[x] != trow[x]:
                    if y < ys: a += 1
                    else: b += 1
        row.append('%d/%d' % (a, b))
    print(tf, ' '.join(row))
