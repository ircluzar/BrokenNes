"""dmaprec.py ours|theirs kind words H40|H32 on|off [slotset]: the duration of one DMA plus its busy poll (probes_b4a.dma_rate), as master clocks a lap, with the lap counter sampled by the vertical interrupt (as rate.py
does for single instructions), so the rate is exact to a lap in about 190,000 instead of dmashort.py's 0.2 percent. Wave 5, for Strider's level load: its loader waits for 20 lines at a time for a DMA to finish.
The handler copies the lap counter to $FF0008 at every frame and to $FF000C at frame 50; the rate is (laps at the last frame - laps at frame 50) / (frames - 50)."""
import sys, os
import b4a, probes_b4a

which, kind, words, w, d = sys.argv[1:6]
words = int(words)
name = 'dmap_%s_%d_%s_%s' % (kind, words, w, d)
src = probes_b4a.dma_rate(kind, w == 'H40', d == 'on', words=words)
old = "handler:\n    addq.w #1,($FF0004).l\n    rte"
new = "handler:\n    addq.w #1,($FF0004).l\n    cmpi.w #50,($FF0004).l\n    bne.s h1\n    move.l (a0),($FF000C).l\nh1: move.l (a0),($FF0008).l\n    rte"
if old not in src.replace('\r\n', '\n'): raise SystemExit('the handler text of dma_rate changed')
src = src.replace('\r\n', '\n').replace(old, new)
if which == 'ours':
    ss = sys.argv[6] if len(sys.argv) > 6 else os.environ.get('RATE_SLOTSET')
    ram = b4a.ram_ours(name, src, {30: 'handler'}, frames=900, extra=(('--slot-set', ss) if ss else ()))
else:
    ram = b4a.ram_theirs(name, src, {30: 'handler'}, wait_ms=9000, cache=False)
if ram is None:
    print(name, which, 'no state'); sys.exit(1)
best = None
for base in range(0, 12):
    now = ram[base] << 16 | ram[base + 1]; fr = ram[base + 2]; lv = ram[base + 4] << 16 | ram[base + 5]; l50 = ram[base + 6] << 16 | ram[base + 7]
    if 60 < fr < 5000 and ram[base + 3] == 0 and now >= lv >= l50 > 0 and now - lv < 100000:
        best = (base, fr, lv, l50); break
if best is None:
    print(name, which, 'no consistent layout in', [hex(x) for x in ram[:20]]); sys.exit(1)
base, frames, lv, l50 = best
rate = (lv - l50) / (frames - 50)
print('%s %-6s: frames %d laps/frame %.5f -> %.3f master clocks a lap' % (name, which, frames, rate, 262 * 3420 / rate))
