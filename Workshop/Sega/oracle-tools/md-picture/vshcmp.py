"""vshcmp.py ROMNAME COL|full [D0 STEP]: decode the oracle capture out/o_ROMNAME/s000.png and ours out/ROMNAME_ours/f000060.png side by side"""
import sys, os
sys.path.insert(0, os.environ['PYTHONPATH'])
import vsh
S = os.path.join(os.environ['BN_ORACLE_ROOT'], 'out')
name = sys.argv[1]
col = None if sys.argv[2] == 'full' else int(sys.argv[2])
d0 = int(sys.argv[3]) if len(sys.argv) > 3 else None
step = int(sys.argv[4]) if len(sys.argv) > 4 else None
ours = vsh.decode(os.path.join(S, name + '_ours', 'f000060.png'), 0, col)
op = os.path.join(S, 'o_' + name, 's000.png')
if not os.path.exists(op):
    print('no oracle capture', op); sys.exit(0)
best = None
for yoff in range(0, 20):
    try: r = vsh.decode(op, yoff, col)
    except Exception: continue
    q = sum(1 for x in r for c in x[2] if c != '?')
    if best is None or q > best[0]: best = (q, yoff, r)
q, yoff, theirs = best
print('oracle yoff', yoff, 'decoded', q)
print('  k    d   | ours lines y-1..y+3  dot x | oracle lines y-1..y+3  dot x')
for a, b in zip(ours, theirs):
    k = a[0]
    d = (d0 + step * k) if d0 is not None else ''
    print('%3d %4s   | %s  %4s-%-4s | %s  %4s-%-4s' % (k, d, a[2], a[3], a[4], b[2], b[3], b[4]))
