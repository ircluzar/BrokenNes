"""CRAM and VSRAM DMA duration against the number of words, ours against BlastEm: cram_rate.py ours|theirs|both [kinds]
One DMA from the 68000 into CRAM, then a poll until the DMA-busy bit clears, per lap (laps per frame as in dma_rate.py); the length goes 32, 128, 512 and 2048 words, so a per-word cost and a per-DMA
overhead can be told apart. Results are kept in out/cram_rate.pkl; 'ours' can be rerun after a change and compared with the stored oracle numbers."""
import sys, os, pickle
import b4a, probes_b4a
which = sys.argv[1] if len(sys.argv) > 1 else 'both'
kinds = (sys.argv[2].split(',') if len(sys.argv) > 2 else ['68k_cram', '68k_vram'])
lengths = [32, 128, 512, 2048]
pk = os.path.join(b4a.W, 'out', 'cram_rate.pkl')
res = pickle.load(open(pk, 'rb')) if os.path.exists(pk) else {}
for kind in kinds:
    for h40 in (True, False):
        for display in (True, False):
            for words in lengths:
                key = (kind, 'H40' if h40 else 'H32', 'on' if display else 'off', words)
                name = 'cram_%s_%s_%s_%d' % key
                src = probes_b4a.dma_rate(kind, h40, display, words)
                if which in ('ours', 'both'): res[key + ('ours',)] = b4a.ram_ours(name, src, {30: 'handler'}, frames=600)
                if which in ('theirs', 'both'): res[key + ('theirs',)] = b4a.ram_theirs(name, src, {30: 'handler'}, wait_ms=6000, cache=False)
                pickle.dump(res, open(pk, 'wb'))
def rate(ram):
    if ram is None: return None
    laps = ram[0] << 16 | ram[1]; frames = ram[2]
    return laps / frames if frames else None
print('%-26s %10s %10s %8s   %s' % ('kind width display words', 'ours', 'theirs', 'ratio', 'lines per lap ours / theirs'))
for kind in kinds:
    for w in ('H40', 'H32'):
        for d in ('on', 'off'):
            for n in lengths:
                o = rate(res.get((kind, w, d, n, 'ours'))); t = rate(res.get((kind, w, d, n, 'theirs')))
                lines = (262 / o if o else 0, 262 / t if t else 0)
                print('%-26s %10s %10s %8s   %7.3f / %7.3f' % ('%s %s %s %d' % (kind, w, d, n), '%.3f' % o if o else '-', '%.3f' % t if t else '-', '%.4f' % (t / o) if o and t else '-', lines[0], lines[1]))
