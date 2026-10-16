"""Compare our interrupt entry times for the loops of iplloops.py with the oracle's means in Track B4a's table (docs/projects/sega-track-b4a.md section 4: handler entry after the HINT, in master clocks, BlastEm 1.0.0).

    python iplfitb4a.py "iplmode=1;ipllead=-11" ["other slot-set" ...]     prints, per loop, ours minus the oracle (clocks) for each slot-set (empty string = the current rule)

Our measure has its own constant offset (the HV read and the HINT time are not the same reference as B4a's); it is taken from the nop loop of the first slot-set given with the rule unchanged ('' is always run first) so the nop row
is 0 by construction under '' and the other rows show the shape.
"""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import iplloops

# B4a's oracle means (master clocks after the HINT), and their ours for reference
ORACLE = {'nop': 460.8, 'move': 470.3, 'mulu': 778.9, 'divu': 500.3, 'mixed': 501.9, 'longmove': 963.6, 'movem_out': 752.1, 'movem_in': 765.3, 'movem_short': 564.1, 'mulu1': 693.1, 'divu1': 478.7,
          'lsl8': 569.7, 'lslr': 684.2, 'clr_l': 528.8, 'add_rmw': 528.9, 'jsr_rts': 554.6}
THEIRS_OURS = {'nop': 460.8, 'move': 462.3, 'mulu': 712.3, 'divu': 467.2, 'mixed': 502.1, 'longmove': 680.7, 'movem_out': 660.9, 'movem_in': 674.7, 'movem_short': 502.0, 'mulu1': 700.1, 'divu1': 463.8,
               'lsl8': 543.6, 'lslr': 692.8, 'clr_l': 490.6, 'add_rmw': 490.6, 'jsr_rts': 489.9}


def mean_entry(body):
    o, _ = iplloops.run(body, True)
    ws = iplloops.samples_from(o)
    t = iplloops.entry_ticks(ws)
    return sum(t) / len(t) * 16


if __name__ == '__main__':
    sets = [''] + [a for a in sys.argv[1:] if a]
    names = list(ORACLE)
    results = {}
    for s in sets:
        if s: os.environ['IPL_SLOTSET'] = s
        else: os.environ.pop('IPL_SLOTSET', None)
        results[s] = {n: mean_entry(n) for n in names}
    base = results[''][ 'nop' ] - 460.8       # our measure's constant
    print('%-12s %8s %8s' % ('loop', 'oracle', 'B4a-ours'), ' '.join('%-24s' % (s or 'current rule') for s in sets))
    for n in names:
        row = ['%+8.1f' % (results[s][n] - base - ORACLE[n]) for s in sets]
        print('%-12s %8.1f %8.1f' % (n, ORACLE[n], THEIRS_OURS[n]), ' '.join('%-24s' % r for r in row))
    for s in sets:
        res = [results[s][n] - base - ORACLE[n] for n in names]
        print('rms residual %-24s %.1f   max |%.1f|' % (s or 'current rule', (sum(r * r for r in res) / len(res)) ** 0.5, max(abs(r) for r in res)))
