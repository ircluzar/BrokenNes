"""Which of our frame boundaries has the same work RAM as BlastEm paused after WAIT ms: ramphase.py ROM NAME [wait_ms=5000] [lo=300] [hi=460] [--extra "--slot-set x=1"] [--no-blastem]
The oracle's quick-save state (work RAM, 64 KB) is read once (hold the oracle lock); our emulator is run for lo..hi frames and the number of differing RAM words is printed per boundary, best first.
A game's frame counters, timers and animation phases are in RAM, so the boundary with the fewest differences is the frame the oracle is at, in OUR count of frames since reset: if it is not the boundary the
picture scan finds (oraclerun.ps1 'f0 ='), our boot takes a different number of frames than the oracle's. Results: out/ramphase_NAME.pkl (the oracle's RAM is kept for --no-blastem reruns)."""
import sys, os, pickle, argparse
import oracle

ap = argparse.ArgumentParser()
ap.add_argument('rom'); ap.add_argument('name')
ap.add_argument('wait_ms', nargs='?', type=int, default=5000)
ap.add_argument('lo', nargs='?', type=int, default=300)
ap.add_argument('hi', nargs='?', type=int, default=460)
ap.add_argument('--extra', default='')
ap.add_argument('--no-blastem', action='store_true')
ap.add_argument('--oracle-only', action='store_true', help='only capture the oracle RAM (short lock hold); analyse later with --no-blastem')
ap.add_argument('--region', default=None)
a = ap.parse_args()
os.makedirs(os.path.join(oracle.ROOT, 'out'), exist_ok=True)
pk = os.path.join(oracle.ROOT, 'out', 'ramphase_%s.pkl' % a.name)
if a.no_blastem:
    theirs = pickle.load(open(pk, 'rb'))
else:
    out = oracle.blastem(a.rom, 'rp_' + a.name, 'w%d,pause,snap' % a.wait_ms, cache=False)
    st = os.path.join(out, 's000.state')
    if not os.path.exists(st): sys.exit('no state captured')
    theirs = oracle.state_ram(st)
    pickle.dump(theirs, open(pk, 'wb'))
    if a.oracle_only: print('captured', pk); sys.exit(0)
extra = tuple(a.extra.split()) if a.extra else ()
rows = []
for n in range(a.lo, a.hi):
    ram = os.path.join(oracle.ROOT, 'out', 'rp_%s_%d.ram' % (a.name, n))
    code, text = oracle.ours(a.rom, n, a.region, ram=ram, extra=extra)
    if code != 0: print('frame', n, 'failed', code); continue
    ours = oracle.ram_words(ram)
    d = [i for i in range(len(theirs)) if ours[i] != theirs[i]]
    rows.append((len(d), n, d[:6]))
    os.remove(ram)
rows.sort()
print('best boundaries (frames run, differing words of %d; first differing word indices):' % len(theirs))
for cnt, n, d in rows[:8]: print('  %4d frames: %5d words differ  %s' % (n, cnt, d))
