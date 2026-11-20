# [UAT/n64] Compare two golden.sh outputs (cer64plus golden_cmp.py): audio bytes over the common length, frame hashes at common VI points.
import sys, wave
a, b = sys.argv[1], sys.argv[2]
wa, wb = wave.open(a + '/audio.wav'), wave.open(b + '/audio.wav')
n = min(wa.getnframes(), wb.getnframes())
da, db = wa.readframes(n), wb.readframes(n)
print('audio %.1f s identical: %s' % (n / wa.getframerate(), da == db))
if da != db:
    for i in range(0, len(da), 4):
        if da[i:i+4] != db[i:i+4]: print('  first audio diff at frame', i // 4); break
ha = dict(l.split() for l in open(a + '/framehash.txt'))
hb = dict(l.split() for l in open(b + '/framehash.txt'))
common = sorted(set(ha) & set(hb), key=int)
bad = [v for v in common if ha[v] != hb[v]]
print('frames compared %d, mismatched %d%s' % (len(common), len(bad), (' (first at VI %s)' % bad[0]) if bad else ''))
