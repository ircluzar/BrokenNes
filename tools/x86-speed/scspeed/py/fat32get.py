# fat32get.py IMG PATH OUT: read one file from the first FAT32 partition of a raw disk image (8.3 names, case-insensitive)
import struct, sys
f = open(sys.argv[1], 'rb')
def rd(off, n): f.seek(off); return f.read(n)
mbr = rd(0, 512); lba = struct.unpack_from('<I', mbr, 0x1c6)[0]; P = lba * 512
bs = rd(P, 512); bps, spc, rsv, nf = struct.unpack_from('<HBHB', bs, 11); fsz = struct.unpack_from('<I', bs, 36)[0]; rootc = struct.unpack_from('<I', bs, 44)[0]
fat0 = P + rsv * bps; data = fat0 + nf * fsz * bps; cs = spc * bps
def nxt(c): return struct.unpack('<I', rd(fat0 + c * 4, 4))[0] & 0x0fffffff
def chain(c):
    while 2 <= c < 0x0ffffff8: yield c; c = nxt(c)
def readc(c, size=None):
    b = b''.join(rd(data + (x - 2) * cs, cs) for x in chain(c)); return b if size is None else b[:size]
c = rootc
for part in sys.argv[2].replace('/', chr(92)).strip(chr(92)).split(chr(92)):
    d = readc(c); found = None
    for i in range(0, len(d), 32):
        e = d[i:i+32]
        if e[0] == 0: break
        if e[0] == 0xe5 or e[11] == 0x0f: continue
        n = e[:8].decode('latin1').strip(); x = e[8:11].decode('latin1').strip(); nm = n + ('.' + x if x else '')
        if nm.upper() == part.upper(): found = e; break
    assert found, part
    c = struct.unpack_from('<H', found, 20)[0] << 16 | struct.unpack_from('<H', found, 26)[0]; size = struct.unpack_from('<I', found, 28)[0]
open(sys.argv[3], 'wb').write(readc(c, size)); print('ok', size)
