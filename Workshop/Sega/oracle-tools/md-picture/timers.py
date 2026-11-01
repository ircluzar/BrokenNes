"""timers.py ramdir lo hi: bytes/words of work RAM that change in a regular way between steps lo..hi (counting up or down by one per step) and the steps at which each byte changes at all (for finding the timer behind a delay)"""
import sys, os, struct
d, lo, hi = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
rams = {n: open(os.path.join(d, 'r%05d.bin' % n), 'rb').read() for n in range(lo, hi + 1)}
steps = sorted(rams)
# byte-wise change counts
changes = {}
for i in range(65536):
    seq = [rams[n][i] for n in steps]
    if len(set(seq)) > 1:
        changes[i] = seq
print('%d bytes change between steps %d and %d' % (len(changes), lo, hi))
# bytes that count by exactly +-1 per step for a run of >= 10 steps
for i, seq in sorted(changes.items()):
    run = 0; best = 0; start = 0; cur_start = 0
    for k in range(1, len(seq)):
        dd = (seq[k] - seq[k - 1]) & 0xFF
        if dd in (1, 255):
            if run == 0: cur_start = k - 1
            run += 1
            if run > best: best = run; start = cur_start
        else: run = 0
    if best >= 10:
        print('  $FF%04X counts by 1 for %d steps from step %d (value %02X -> %02X)' % (i, best, steps[start], seq[start], seq[min(len(seq) - 1, start + best)]))
