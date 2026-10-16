"""DMA duration (laps of one DMA and a busy poll per frame), ours against BlastEm: dma_rate.py ours|theirs|both
Results are kept in out/dma_rate.pkl; 'ours' can be rerun after a change and compared with the stored oracle numbers."""
import sys, os, pickle
import b4a, probes_b4a
which = sys.argv[1] if len(sys.argv) > 1 else 'both'
kinds = ['68k_vram', '68k_cram', 'fill', 'copy']
pk = os.path.join(b4a.W, 'out', 'dma_rate.pkl')
res = pickle.load(open(pk, 'rb')) if os.path.exists(pk) else {}
for kind in kinds:
    for h40 in (True, False):
        for display in (True, False):
            key = (kind, 'H40' if h40 else 'H32', 'on' if display else 'off')
            name = 'dma_%s_%s_%s' % key
            src = probes_b4a.dma_rate(kind, h40, display)
            if which in ('ours', 'both'): res[key + ('ours',)] = b4a.ram_ours(name, src, {30: 'handler'}, frames=600)
            if which in ('theirs', 'both'): res[key + ('theirs',)] = b4a.ram_theirs(name, src, {30: 'handler'}, wait_ms=6000, cache=False)
            pickle.dump(res, open(pk, 'wb'))
def rate(ram):
    if ram is None: return None
    laps = ram[0] << 16 | ram[1]; frames = ram[2]
    return laps / frames if frames else None
print('%-22s %10s %10s %8s' % ('kind width display', 'ours', 'theirs', 'ratio'))
for kind in kinds:
    for w in ('H40', 'H32'):
        for d in ('on', 'off'):
            o = rate(res.get((kind, w, d, 'ours'))); t = rate(res.get((kind, w, d, 'theirs')))
            print('%-22s %10s %10s %8s' % ('%s %s %s' % (kind, w, d), '%.3f' % o if o else '-', '%.3f' % t if t else '-', '%.4f' % (t / o) if o and t else '-'))
