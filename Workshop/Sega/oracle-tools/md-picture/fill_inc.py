"""Duration of a VRAM fill against the auto-increment (R15), ours against BlastEm (Track B4a, wave 5: Streets of Rage's boot fills 64 KB with R15 = 1, the rate probes used 2):
fill_inc.py ours|theirs INC [bytes] [on|off]. Prints laps of (fill + poll) per frame and lines per lap. The oracle's RAM is read at word 0 as in fill_rate.py and, as a check, at word 6 (its quick-save image can start 12 bytes late)."""
import sys, os
import b4a, probes_b4a
which, inc = sys.argv[1], int(sys.argv[2])
words = int(sys.argv[3]) if len(sys.argv) > 3 else 65535
disp = (sys.argv[4] != 'off') if len(sys.argv) > 4 else False
fdata = int(sys.argv[5], 16) if len(sys.argv) > 5 else 0x1234
flags = sys.argv[6:]
z80 = 'z80' in flags; novint = 'novint' in flags; sorseq = 'sorseq' in flags; bootseq = 'bootseq' in flags
name = 'fillinc_%d_%d_%s_%04X%s%s%s%s' % (inc, words, 'on' if disp else 'off', fdata, '_z80' if z80 else '', '_novint' if novint else '', '_sorseq' if sorseq else '', '_bootseq' if bootseq else '')
src = probes_b4a.dma_rate('fill', True, disp, words, inc, fdata, z80, novint, sorseq, bootseq)
vectors = {} if novint else {30: 'handler'}
if which == 'ours': ram = b4a.ram_ours(name, src, vectors, frames=900)
else: ram = b4a.ram_theirs(name, src, vectors, wait_ms=9000, cache=False)
if ram is None:
    print(name, which, 'no state'); sys.exit(1)
for base in (0, 6):
    laps = ram[base] << 16 | ram[base + 1]; frames = ram[base + 2]
    if frames: print('%s %s base %d: laps %d frames %d -> %.4f laps/frame, %.2f lines per lap' % (name, which, base, laps, frames, laps / frames, 262 * frames / max(laps, 1)))
