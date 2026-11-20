# mkiso.py OUT.ISO FILE...: a minimal ISO9660 level-1 image, all files in the root (names must be 8.3 uppercase)
import struct, sys, os
S = 2048
def both16(v): return struct.pack('<H', v) + struct.pack('>H', v)
def both32(v): return struct.pack('<I', v) + struct.pack('>I', v)
def dirrec(name, lba, size, isdir):
    n = len(name); r = struct.pack('<BB', 33 + n + (1 - n % 2), 0) + both32(lba) + both32(size) + bytes([126, 1, 1, 0, 0, 0, 0]) + bytes([2 if isdir else 0, 0, 0]) + both16(1) + bytes([n]) + name
    return r + (b'\0' if n % 2 == 0 else b'')
files = sys.argv[2:]; lba = 21; ents = []
for f in files:
    d = open(f, 'rb').read(); ents.append((os.path.basename(f).upper().encode() + b';1', lba, d)); lba += (len(d) + S - 1) // S
total = lba
root = dirrec(b'\0', 20, S, True) + dirrec(b'\1', 20, S, True) + b''.join(dirrec(n, l, len(d), False) for n, l, d in ents)
assert len(root) <= S
pt_l = struct.pack('<BBIH', 1, 0, 20, 1) + b'\0\0'; pt_m = struct.pack('>BBIH', 1, 0, 20, 1) + b'\0\0'
pvd = bytearray(S); pvd[0] = 1; pvd[1:6] = b'CD001'; pvd[6] = 1
pvd[8:40] = b' ' * 32; pvd[40:72] = b'BNTEST'.ljust(32)
pvd[80:88] = both32(total); pvd[120:124] = both16(1); pvd[124:128] = both16(1); pvd[128:132] = both16(S)
pvd[132:140] = both32(len(pt_l)); pvd[140:144] = struct.pack('<I', 18); pvd[148:152] = struct.pack('>I', 19)
pvd[156:190] = dirrec(b'\0', 20, S, True)
for o in (190, 318, 446, 574): pvd[o:o + 128] = b' ' * 128
for o in (813, 830, 847, 864): pvd[o:o + 17] = b'0' * 16 + b'\0'
pvd[881] = 1
term = bytearray(S); term[0] = 255; term[1:6] = b'CD001'; term[6] = 1
out = bytearray(S * 16) + pvd + term + pt_l.ljust(S, b'\0') + pt_m.ljust(S, b'\0') + root.ljust(S, b'\0')
for n, l, d in ents: out += d.ljust(((len(d) + S - 1) // S) * S, b'\0')
open(sys.argv[1], 'wb').write(out); print(sys.argv[1], len(out))
