"""dmashort.py ours|theirs kind words H40|H32 on|off: the duration of one short DMA plus its poll, as master clocks a lap (probes_b4a.dma_rate: setup writes, the command, a poll of the status register until the DMA-busy bit
clears, a counter; the vertical interrupt counts frames; the oracle's RAM image layout is the one of write_rate: laps long at $FF0000, frames word at $FF0004, read at an arbitrary point, so the rate is good to about 0.2 percent).
The short transfers show what the 68000's release after the DMA costs (a lap of a few hundred clocks changes by the release latency)."""
import sys
import b4a, probes_b4a

which, kind, words, w, d = sys.argv[1:6]
words = int(words)
name = 'dmas_%s_%d_%s_%s' % (kind, words, w, d)
src = probes_b4a.dma_rate(kind, w == 'H40', d == 'on', words=words)
if which == 'ours':
    import os
    ss = os.environ.get('RATE_SLOTSET')
    ram = b4a.ram_ours(name, src, {30: 'handler'}, frames=600, extra=(('--slot-set', ss) if ss else ()))
    base = 0
else:
    ram = b4a.ram_theirs(name, src, {30: 'handler'}, wait_ms=6000, cache=False)
    base = 6
if ram is None:
    print(name, which, 'no state'); sys.exit(1)
best = None
for base in ([base] + list(range(0, 12))):
    laps = ram[base] << 16 | ram[base + 1]; frames = ram[base + 2]
    if 30 < frames < 3000 and ram[base + 3] == 0 and laps > frames:
        best = (laps, frames); break
if best is None:
    print(name, which, 'no consistent layout', [hex(x) for x in ram[:16]]); sys.exit(1)
laps, frames = best
print('%s %-6s: laps %d frames %d -> %.3f laps/frame, %.1f master clocks a lap' % (name, which, laps, frames, laps / frames, 262 * 3420 / (laps / frames)))
