"""Score ours (several slot-sets) against the stored BlastEm return-address histograms (rethist_blastem.txt, measured with run_rethist.py on BlastEm 1.0.0): per loop half the L1 distance between the two
histograms, summed over the loops. No oracle needed.

    python rh_score.py "" "braipl=0;movemipl=0" "braipl=7;movemipl=1"        (env BN_ARTIFACTS / BN_ORACLE_ROOT as for run_rethist.py; LOOPS=nop,movem_out limits the loops)

"" is the build's own defaults (since Track B4b: braipl=7, movemipl=1); "braipl=0;movemipl=0" is the old rule (the lines sampled at the start of the last bus cycle of every instruction).
"""
import os, sys, re
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import run_rethist


def parse_blastem(path):
    res = {}
    cur = None
    for line in open(path, encoding='utf-8', errors='replace'):
        m = re.match(r'^(\S+)\s+ours\s+(.*)\(n=', line)
        if m: cur = m.group(1); continue
        m = re.match(r'^\s+BlastEm\s+(.*)\(n=', line)
        if m and cur:
            d = {}
            toks = m.group(1).split()
            for i in range(0, len(toks), 2): d[toks[i]] = float(toks[i + 1])
            res[cur] = d
    return res


if __name__ == '__main__':
    blast = parse_blastem(os.path.join(HERE, 'rethist_blastem.txt'))
    names = os.environ['LOOPS'].split(',') if os.environ.get('LOOPS') else list(blast)
    for ss in sys.argv[1:] or ['']:
        if ss: os.environ['IPL_SLOTSET'] = ss
        else: os.environ.pop('IPL_SLOTSET', None)
        tot = 0.0
        rows = []
        for nm in names:
            o, _ = run_rethist.run(nm, True)
            n, h = o
            ours = {k: v / n for k, v in h.items()}
            keys = set(ours) | set(blast[nm])
            err = sum(abs(ours.get(k, 0) - blast[nm].get(k, 0)) for k in keys) / 2
            tot += err
            rows.append('%s %.3f' % (nm, err))
        print('=== %-28s total %.3f   %s' % (ss or '(defaults)', tot, '  '.join(rows)))
        sys.stdout.flush()
