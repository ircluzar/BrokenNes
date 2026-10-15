"""Where a pending interrupt is taken after the mask is lowered, ours against BlastEm: sr_delay.py ours|theirs|both
For every variant (probes_b4a.SR_VARIANTS) a histogram of 'taken before copy k of the instruction run after the mask change' (k = 0 means right after the mask instruction)."""
import sys, os, pickle
import b4a, probes_b4a
which = sys.argv[1] if len(sys.argv) > 1 else 'both'
frames, run = 30, 6
variants = list(probes_b4a.SR_VARIANTS)
src = probes_b4a.sr_delay(variants, frames, True, run)
sym = {}
pk = os.path.join(b4a.W, 'out', 'sr_delay.pkl')
res = pickle.load(open(pk, 'rb')) if os.path.exists(pk) else {}
vec = {30: 'handler'}
if which in ('ours', 'both'): res['ours'] = b4a.ram_ours('sr_delay', src, vec, frames=len(variants) * frames * 40 + 400, symbols_out=sym)
if which in ('theirs', 'both'): res['theirs'] = b4a.ram_theirs('sr_delay', src, vec, wait_ms=6000, cache=False, symbols_out=sym)
if not sym:
    b4a.make_rom('sr_delay', src, vec, symbols_out=sym)
pickle.dump(res, open(pk, 'wb'))
def where(i, pc):
    names = ['v%dm' % i] + ['v%dr%d' % (i, k) for k in range(run)] + ['v%de' % i]
    addrs = [sym[n] for n in names]
    # the first instruction NOT executed is at pc: which label starts exactly there?
    for n, a in zip(names, addrs):
        if a == pc: return n
    return 'pc %X' % pc
for label, ram in res.items():
    if ram is None: print(label, 'NO DATA'); continue
    print('==', label)
    for i, name in enumerate(variants):
        hist = {}
        for f in range(frames):
            o = 0x80 + (i * frames + f) * 2       # RAM words: $FF0100 = word 0x80
            pc = ram[o] << 16 | ram[o + 1]
            w = where(i, pc)
            hist[w] = hist.get(w, 0) + 1
        print('  %-14s %s' % (name, ', '.join('%s x%d' % kv for kv in sorted(hist.items()))))
