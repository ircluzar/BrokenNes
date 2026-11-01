"""VRAM fill and copy duration against the length, ours against BlastEm: fill_rate.py ours|theirs|both
A fill (or copy) of 2048, 8192, 32768 and 65535 bytes and a poll until the DMA-busy bit clears, per lap; laps per frame as in dma_rate.py. A fill is linear in its length when the oracle's long fills take as long per byte as
its short ones; Streets of Rage's boot fills 64 KB four times and its replay against the oracle is sensitive to how long that takes. Results: out/fill_rate.pkl."""
import sys, os, pickle
import b4a, probes_b4a
which = sys.argv[1] if len(sys.argv) > 1 else 'both'
kinds = (sys.argv[2].split(',') if len(sys.argv) > 2 else ['fill', 'copy'])
lengths = [2048, 8192, 32768, 65535]
pk = os.path.join(b4a.W, 'out', 'fill_rate.pkl')
res = pickle.load(open(pk, 'rb')) if os.path.exists(pk) else {}
for kind in kinds:
    for h40 in (True,):
        for display in (False, True):
            for words in lengths:
                key = (kind, 'H40' if h40 else 'H32', 'on' if display else 'off', words)
                name = 'fillr_%s_%s_%s_%d' % key
                src = probes_b4a.dma_rate(kind, h40, display, words)
                if which in ('ours', 'both'): res[key + ('ours',)] = b4a.ram_ours(name, src, {30: 'handler'}, frames=900)
                if which in ('theirs', 'both'): res[key + ('theirs',)] = b4a.ram_theirs(name, src, {30: 'handler'}, wait_ms=9000, cache=False)
                pickle.dump(res, open(pk, 'wb'))
def rate(ram):
    if ram is None: return None
    laps = ram[0] << 16 | ram[1]; frames = ram[2]
    return laps / frames if frames else None
print('%-26s %10s %10s %8s   %s' % ('kind width display bytes', 'ours', 'theirs', 'ratio', 'lines per lap ours / theirs'))
for kind in kinds:
    for w in ('H40',):
        for d in ('off', 'on'):
            for n in lengths:
                o = rate(res.get((kind, w, d, n, 'ours'))); t = rate(res.get((kind, w, d, n, 'theirs')))
                lines = (262 / o if o else 0, 262 / t if t else 0)
                print('%-26s %10s %10s %8s   %7.2f / %7.2f' % ('%s %s %s %d' % (kind, w, d, n), '%.4f' % o if o else '-', '%.4f' % t if t else '-', '%.4f' % (t / o) if o and t else '-', lines[0], lines[1]))
