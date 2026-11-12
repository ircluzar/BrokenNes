"""Nested certification: name the RAM differences of a Workshop --nested-cert --raw run.

  python nested-cert-analyze.py <dir> <stem> [--mlb game.mlb] [--ignore 0100-01FF] [--top 60] [--show sym,...]

Reads <stem>.nes.ram / <stem>.snes.ram (2 KB per controller poll, both sides, aligned by poll) and the per-poll CSV,
maps each address to its symbol (Mesen .mlb, R: lines = NES RAM), and reports per symbol: polls differing, the first
poll/frame, the longest run of consecutive differing polls, and whether the two sides agree again at the end.
Measures only.
"""
import sys, csv, argparse, collections

ap = argparse.ArgumentParser()
ap.add_argument('dir'); ap.add_argument('stem')
ap.add_argument('--mlb'); ap.add_argument('--ignore', default='0100-01FF')
ap.add_argument('--top', type=int, default=60); ap.add_argument('--show', default='')
ap.add_argument('--every', type=int, default=300)
a = ap.parse_args()
nes = open(f'{a.dir}/{a.stem}.nes.ram', 'rb').read(); sn = open(f'{a.dir}/{a.stem}.snes.ram', 'rb').read()
n = len(nes) // 2048
rows = list(csv.DictReader(open(f'{a.dir}/{a.stem}.nested-cert.polls.csv')))
ign = set()
for r in a.ignore.split(','):
    if not r: continue
    lo, _, hi = r.partition('-'); ign.update(range(int(lo, 16), int(hi or lo, 16) + 1))
sym = {}
if a.mlb:
    for line in open(a.mlb, encoding='utf-8', errors='replace'):
        p = line.strip().split(':')
        if len(p) >= 3 and p[0] == 'R':
            lo = int(p[1].split('-')[0], 16); hi = int(p[1].split('-')[-1], 16)
            for x in range(lo, hi + 1):
                if x < 2048: sym.setdefault(x, p[2] + (f'+{x-lo}' if hi > lo else ''))
def name(x): return sym.get(x, '?')
stats = {}
for x in range(2048):
    if x in ign: continue
    cnt = 0; first = -1; run = best = 0; bestEnd = -1
    for p in range(n):
        if nes[p*2048+x] != sn[p*2048+x]:
            cnt += 1; run += 1
            if first < 0: first = p
            if run > best: best = run; bestEnd = p
        else: run = 0
    if cnt: stats[x] = (cnt, first, best, bestEnd, nes[(n-1)*2048+x] == sn[(n-1)*2048+x])
print(f'polls {n}; addresses differing at least once: {len(stats)} ({sum(1 for x in stats if x in sym)} named)')
print('addr  symbol                          polls  first(poll/NESf)  longest-run(ends poll)  equal-at-end')
for x, (cnt, first, best, be, eq) in sorted(stats.items(), key=lambda kv: (-kv[1][2], kv[0]))[:a.top]:
    print(f'${x:04X} {name(x)[:30]:30} {cnt:6}  {first:6}/{rows[first]["nesFrame"]:>6}  {best:6} ({be})  {eq}')
if a.show:
    want = [s.strip() for s in a.show.split(',')]
    addrs = [x for x in range(2048) if any(name(x).startswith(w) for w in want)]
    print('\nvalues every', a.every, 'NES frames (NES/SNES):', ' '.join(name(x) for x in addrs))
    last = -1
    for p in range(n):
        b = int(rows[p]['nesFrame']) // a.every
        if b == last and p != n - 1: continue
        last = b
        print(f'  poll {p:6} f{rows[p]["nesFrame"]:>6}: ' + ' '.join(f'{nes[p*2048+x]:02X}/{sn[p*2048+x]:02X}' for x in addrs))
