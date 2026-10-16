"""wrv.py ours|theirs kind H40|H32 on|off : write_rate_vint on one machine; prints writes (laps*16) per frame"""
import sys, os
import b4a, probes_b4a
which, kind, w, d = sys.argv[1:5]
v30 = len(sys.argv) > 5 and sys.argv[5] == 'v30'
name = 'wrv_%s_%s_%s%s' % (kind, w, d, '_v30' if v30 else '')
src = probes_b4a.write_rate_vint(kind, w == 'H40', d == 'on', v30=v30)
if which == 'ours':
    ram = b4a.ram_ours(name, src, {30: 'handler'}, frames=900)
    base = 0
else:
    ram = b4a.ram_theirs(name, src, {30: 'handler'}, wait_ms=9000, cache=False)
    base = 6        # the oracle's state RAM image starts 12 bytes late
if ram is None: print(name, which, 'no state'); sys.exit(1)
laps_now = ram[base] << 16 | ram[base + 1]
frames = ram[base + 2]
laps_vint = ram[base + 4] << 16 | ram[base + 5]; laps50 = ram[base + 6] << 16 | ram[base + 7]
rate = (laps_vint - laps50) / (frames - 50)
print('%s %s: frames %d laps at last vint %d at 50 %d -> %.5f laps/frame %.4f writes/frame (laps now %d)' % (name, which, frames, laps_vint, laps50, rate, 16 * rate, laps_now))
