"""VDP write throughput of the 68000, ours against BlastEm: wr_rate.py ours|theirs|both [kind,...]   (probes_b4a.write_rate: laps of 16 data-port writes per frame, by memory, width and display state)
Results are kept in out/wr_rate.pkl so that 'ours' can be rerun after a change and compared with the stored oracle numbers."""
import sys, os, pickle
import b4a, probes_b4a
which = sys.argv[1] if len(sys.argv) > 1 else 'both'
kinds = sys.argv[2].split(',') if len(sys.argv) > 2 else ['cram', 'vram', 'vsram']
pk = os.path.join(b4a.W, 'out', 'wr_rate.pkl')
res = pickle.load(open(pk, 'rb')) if os.path.exists(pk) else {}
for kind in kinds:
    for h40 in (True, False):
        for display in (True, False):
            key = (kind, 'H40' if h40 else 'H32', 'on' if display else 'off')
            name = 'wr_%s_%s_%s' % key
            src = probes_b4a.write_rate(kind, h40, display)
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
