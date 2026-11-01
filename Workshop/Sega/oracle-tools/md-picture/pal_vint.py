"""When the vertical interrupt's handler starts on a 50 Hz (PAL) machine, ours against BlastEm, in the VDP's own time: pal_vint.py ours|theirs|both [ntsc|pal] [vinth40=N ...]
The handler's first instruction reads the HV counter (probes_b4a.hv_in_handler); a main loop of `nop; bra` interrupted every frame samples the interrupt at every phase of the 68000's instruction (the frame is not a
whole number of 68000 cycles), so the mean time of the readings after the vertical blank begins is the interrupt's time plus a constant of the handler's entry. Ours is run with the VINT delays given (clocks after
the V counter's step into the first blank line; the default is 788 in H40); the oracle (a ROM whose region letter is E, or J/U for NTSC) is run once. Results: out/pal_vint_<region>.pkl."""
import sys, os, pickle
import b4a, probes_b4a, an_b4a
which = sys.argv[1] if len(sys.argv) > 1 else 'both'
region = sys.argv[2] if len(sys.argv) > 2 and sys.argv[2] in ('ntsc', 'pal') else 'pal'
delays = [a for a in sys.argv[2:] if a.startswith('vinth40=')]
letters = 'E' if region == 'pal' else 'JUE'
pk = os.path.join(b4a.W, 'out', 'pal_vint_%s.pkl' % region)
res = pickle.load(open(pk, 'rb')) if os.path.exists(pk) else {}
src = probes_b4a.hv_in_handler('vint', True)
name = 'palvint_' + region
def stats(ram):
    if ram is None: return None
    n = ram[0]
    ts = [an_b4a.abs_time(ram[1 + i], True, 223) for i in range(min(n, 300))]
    ts = [t for t in ts if 0 <= t < 3420 * 6]            # the readings after the first blank line began
    return (len(ts), sum(ts) / len(ts) if ts else None, min(ts) if ts else None, max(ts) if ts else None, n)
if which in ('ours', 'both'):
    for d in (delays or ['']):
        extra = ('--slot-set', d) if d else ()
        res[('ours', d)] = stats(b4a.ram_ours(name, src, {30: 'handler'}, frames=330, region=region, region_letters=letters, extra=extra))
        pickle.dump(res, open(pk, 'wb'))
if which in ('theirs', 'both'):
    res[('theirs', '')] = stats(b4a.ram_theirs(name, src, {30: 'handler'}, wait_ms=6000, region_letters=letters, cache=False))
    pickle.dump(res, open(pk, 'wb'))
print('region', region, ' (samples, mean clocks after the start of line 223, min, max, interrupts counted)')
for k, v in sorted(res.items()): print(' ', k, v)
