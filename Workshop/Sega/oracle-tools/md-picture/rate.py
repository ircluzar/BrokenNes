"""rate.py ours|theirs body H40|H32 on|off [per] [v30] : probes_b4b2.body_rate_vint on one machine; prints bodies per frame and master clocks per body.
For both machines at once and several bodies, rate_all.py loops over this (the oracle part holds the blastem lock outside)."""
import sys
import b4a, probes_b4b2

which, body, w, d = sys.argv[1:5]
per = int(sys.argv[5]) if len(sys.argv) > 5 else 16
v30 = 'v30' in sys.argv[5:]
name = 'rate_%s_%s_%s_%d%s' % (body, w, d, per, '_v30' if v30 else '')
src = probes_b4b2.body_rate_vint(body, per, w == 'H40', d == 'on', v30=v30)
if which == 'ours':
    import os
    ss = os.environ.get('RATE_SLOTSET')
    ram = b4a.ram_ours(name, src, {30: 'handler'}, frames=900, extra=(('--slot-set', ss) if ss else ()))
else:
    ram = b4a.ram_theirs(name, src, {30: 'handler'}, wait_ms=9000, cache=False)
if ram is None:
    print(name, which, 'no state'); sys.exit(1)
# the oracle's quick-save RAM image starts at a different offset from run to run (0 or 6 words late): the layout is laps (long), frames (word), pad, laps at the last vint (long), laps at frame 50 (long); take the offset where it is consistent
best = None
for base in range(0, 12):
    now = ram[base] << 16 | ram[base + 1]; fr = ram[base + 2]; lv = ram[base + 4] << 16 | ram[base + 5]; l50 = ram[base + 6] << 16 | ram[base + 7]
    if 60 < fr < 5000 and ram[base + 3] == 0 and now >= lv >= l50 > 0 and now - lv < 100000:
        best = (base, fr, lv, l50)
        break
if best is None:
    print(name, which, 'no consistent layout in', [hex(x) for x in ram[:20]]); sys.exit(1)
base, frames, laps_vint, laps50 = best
rate = (laps_vint - laps50) / (frames - 50)
lines = 262 if not v30 else 313
clocks_frame = lines * 3420
print('%s %-6s %s: frames %d laps/frame %.5f -> %.4f bodies/frame, %.4f master clocks per body (lap %.3f)' % (name, which, body, frames, rate, per * rate, clocks_frame / (per * rate), clocks_frame / rate))
