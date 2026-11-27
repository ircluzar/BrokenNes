# fat32put.py IMG SRC NAME83: add a small file (one cluster) to the root dir of the first FAT32 partition, in place
import struct, sys
f = open(sys.argv[1], 'r+b')
def rd(off, n): f.seek(off); return f.read(n)
def wr(off, b): f.seek(off); f.write(b)
lba = struct.unpack_from('<I', rd(0, 512), 0x1c6)[0]; P = lba * 512
bs = rd(P, 512); bps, spc, rsv, nf = struct.unpack_from('<HBHB', bs, 11); fsz = struct.unpack_from('<I', bs, 36)[0]; rootc = struct.unpack_from('<I', bs, 44)[0]
fat0 = P + rsv * bps; data = fat0 + nf * fsz * bps; cs = spc * bps
src = open(sys.argv[2], 'rb').read(); assert len(src) <= cs
def fe(c): return struct.unpack('<I', rd(fat0 + c * 4, 4))[0] & 0x0fffffff
c = 1000
while fe(c) != 0: c += 1
for k in range(nf): wr(fat0 + k * fsz * bps + c * 4, struct.pack('<I', 0x0fffffff))
wr(data + (c - 2) * cs, src + bytes(cs - len(src)))
n, x = sys.argv[3].upper().split('.'); name = n.ljust(8).encode() + x.ljust(3).encode()
rc = rootc
while True:
    base = data + (rc - 2) * cs; d = rd(base, cs)
    for i in range(0, cs, 32):
        if d[i] in (0, 0xe5):
            e = name + bytes([0x20]) + bytes(8) + struct.pack('<H', c >> 16) + bytes(4) + struct.pack('<H', c & 0xffff) + struct.pack('<I', len(src))
            wr(base + i, e); print('ok cluster', c); sys.exit(0)
    rc = fe(rc); assert rc < 0x0ffffff8, 'root full'
