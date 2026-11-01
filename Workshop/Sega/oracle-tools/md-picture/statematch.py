"""statematch.py ramdir statefile...: for each oracle state, the number of differing bytes of work RAM (stack page $FF80-$FFFF left out) and of VRAM against every step of ours in ramdir (r/v files), and the best steps
(fast: the difference count is done on whole buffers)"""
import sys, os, struct, re
d = sys.argv[1]; states = sys.argv[2:]
steps = sorted(int(m.group(1)) for f in os.listdir(d) for m in [re.match(r'r(\d+)\.bin$', f)] if m)
R = {n: open(os.path.join(d, 'r%05d.bin' % n), 'rb').read()[:0xFF80] for n in steps}
V = {n: open(os.path.join(d, 'v%05d.bin' % n), 'rb').read() for n in steps}
def ndiff(a, b):
    n = len(a)
    x = (int.from_bytes(a, 'big') ^ int.from_bytes(b, 'big')).to_bytes(n, 'big')
    return n - x.count(0)
for sf in states:
    st = open(sf, 'rb').read()
    ram = st[0x10941:0x10941 + 0xFF80]; vram = st[0xAE:0xAE + 65536]
    pc = struct.unpack_from('>I', st, 0x52)[0]
    rows = sorted((ndiff(R[n], ram) + ndiff(V[n], vram), n, ndiff(R[n], ram), ndiff(V[n], vram)) for n in steps)
    print('%s pc %06X: best ' % (os.path.basename(os.path.dirname(sf)) + '/' + os.path.basename(sf), pc) + '  '.join('n=%d (ram %d, vram %d)' % (n, a, b) for _, n, a, b in rows[:4]))
