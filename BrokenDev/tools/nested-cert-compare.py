"""Compare two per-poll RAM streams (2 KB per poll): BrokenNes NES/SNES (Workshop --nested-cert --raw) and Mesen (mesen-poll-lockstep.lua).

  python nested-cert-compare.py <a.ram> <b.ram> [--mlb game.mlb] [--ignore 0100-01FF] [--label-a A --label-b B] [--polls-a f --polls-b f]

Prints: polls compared, polls that differ, the first differing poll with its bytes (named by the .mlb), addresses ever differing.
--polls-a/--polls-b: "poll frame" text files (Mesen) or the nested-cert CSV (col 2 = NES frame, col 3 = SNES frame: give 'csv:nes' / 'csv:snes'
after the path, e.g. x.csv:snes) to report frames and the lag (frames with no poll) of each stream.
"""
import argparse
ap = argparse.ArgumentParser()
ap.add_argument('a'); ap.add_argument('b'); ap.add_argument('--mlb'); ap.add_argument('--ignore', default='0100-01FF')
ap.add_argument('--label-a', default='A'); ap.add_argument('--label-b', default='B')
ap.add_argument('--frames-a'); ap.add_argument('--frames-b'); ap.add_argument('--show', type=int, default=24)
a = ap.parse_args()
A = open(a.a, 'rb').read(); B = open(a.b, 'rb').read()
n = min(len(A), len(B)) // 2048
ign = set()
for r in a.ignore.split(','):
    if r:
        lo, _, hi = r.partition('-'); ign.update(range(int(lo, 16), int(hi or lo, 16) + 1))
sym = {}
if a.mlb:
    for line in open(a.mlb, encoding='utf-8', errors='replace'):
        p = line.strip().split(':')
        if len(p) >= 3 and p[0] == 'R':
            lo = int(p[1].split('-')[0], 16); hi = int(p[1].split('-')[-1], 16)
            for x in range(lo, min(hi, 2047) + 1): sym.setdefault(x, p[2])
diffpolls = 0; first = -1; ever = {}
for p in range(n):
    base = p * 2048; d = 0
    for x in range(2048):
        if x not in ign and A[base + x] != B[base + x]:
            d += 1; ever[x] = ever.get(x, 0) + 1
    if d:
        diffpolls += 1
        if first < 0: first = p
print(f'{a.label_a} vs {a.label_b}: polls {n} (A {len(A)//2048}, B {len(B)//2048}), differing polls {diffpolls}, first {first}')
if first >= 0:
    base = first * 2048
    print('first difference:', ' '.join(f'${x:03X}({sym.get(x, "?")[:16]})={A[base+x]:02X}/{B[base+x]:02X}' for x in range(2048) if x not in ign and A[base+x] != B[base+x])[:2000])
    print('ever differing (top):', ' '.join(f'${x:03X}({sym.get(x,"?")[:16]})x{c}' for x, c in sorted(ever.items(), key=lambda kv: -kv[1])[:a.show]))
def lag(path):
    c = [int(l) for l in open(path) if l.strip()]
    firstp = next((i for i, v in enumerate(c) if v), 0)
    return sum(1 for v in c[firstp:] if v == 0), len(c), [i + 1 for i, v in enumerate(c[firstp:], firstp) if v == 0][:40]
for lab, f in ((a.label_a, a.frames_a), (a.label_b, a.frames_b)):
    if f:
        l, tot, at = lag(f); print(f'{lab}: frames {tot}, lag frames after the first poll {l}, at {at}')
