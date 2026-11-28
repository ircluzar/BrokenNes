"""stallsim.py: a model of one lap of stallsweep.py's v loops (accesses of 4 68000 clocks, 7 master clocks each, refresh every 896 master clocks holding 14) to test 68000 stall rules against the oracle's lap times
offline (sega-track-b4a.md section 5, 2026-11-28). It reproduces ours with any setting to the constant per-frame VINT cost (H = 1079 master clocks). Of the rules tried, "q0 skip" (no VDP grid; a refresh due in a VDP
port bus cycle is dropped) fits BlastEm to 0.00 clock for 0-16 nops. Reads WORKDIR/hv/ss_*.state from a stallsweep.py theirs run; S below is that WORKDIR."""
import os as _os
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import stallsweep as s, oracle
S = _os.environ.get('STALLSIM_WORKDIR', '.')
NS = s.NS; F = 896040; H = 1079
be = {n: s.decode(oracle.state_ram(os.path.join(S, 'hv', 'ss_v%d.state' % n))) * (F - H) / F for n in NS}
ben = {n: s.decode(oracle.state_ram(os.path.join(S, 'hv', 'ss_n%d.state' % n))) * (F - H) / F for n in NS}
def lap(n, v=True):
    return [('a', 4)] * n + [('a', 4)] + ([('v', 4)] if v else []) + [('a', 4)] * 5 + [('i', 2), ('a', 4), ('a', 4)]
def run(n, vr=lambda t: t, refresh='time', vrefresh='normal', v=True, per=896, stall=14, laps=12000, ph=896):
    t = 0; nxt = ph; seq = lap(n, v); t0 = None; owe = 0
    for k in range(laps):
        if k == 1500: t0 = t
        for kind, c in seq:
            if kind == 'v': t = vr(t)
            t += 7 * c
            if t >= nxt:
                if kind == 'v' and vrefresh == 'skip':
                    nxt = (t // per + 1) * per; continue
                if kind == 'v' and vrefresh == 'defer': continue
                t += stall; nxt = (t // per + 1) * per
    return (t - t0) / (laps - 1500)
def score(name, **kw):
    d = [run(n, **kw) - be[n] for n in NS]
    print('%-34s rms %.3f ' % (name, (sum(x * x for x in d) / len(d)) ** .5) + ' '.join('%+.2f' % x for x in d), flush=True)
dn = [run(n, v=False) - ben[n] for n in NS]
print('n kinds, time refresh: rms %.3f' % ((sum(x*x for x in dn)/len(dn))**.5), ' '.join('%+.2f' % x for x in dn))
score('q0', vr=lambda t: t)
score('floor4', vr=lambda t: t - t % 4)
score('floor2', vr=lambda t: t - t % 2)
score('ceil4', vr=lambda t: -(-t // 4) * 4)
score('round4', vr=lambda t: (t + 2) // 4 * 4)
score('floor4 skip', vr=lambda t: t - t % 4, vrefresh='skip')
score('floor4 defer', vr=lambda t: t - t % 4, vrefresh='defer')
score('q0 skip', vrefresh='skip')
score('q0 defer', vrefresh='defer')
score('floor8', vr=lambda t: t - t % 8)
score('floor 7-grid', vr=lambda t: t - t % 7)
score('floor 14', vr=lambda t: t - t % 14)
score('floor 28', vr=lambda t: t - t % 28)
