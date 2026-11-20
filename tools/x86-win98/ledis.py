# ledis.py VXD [find STRING | dis OBJ:OFF N]: load an LE VxD, object n based at n*0x100000, internal fixups applied
import sys, struct, capstone
d = open(sys.argv[1], 'rb').read(); le = struct.unpack_from('<I', d, 0x3c)[0]; assert d[le:le+2] == b'LE'
H = lambda o, f='<I': struct.unpack_from(f, d, le + o)[0]
psz = H(0x28); nobj = H(0x44); otab = le + H(0x40); fpt = le + H(0x68); frt = le + H(0x6c); dpg = H(0x80)
objs = []
for i in range(nobj):
    vs, rb, fl, pi, pc, _ = struct.unpack_from('<6I', d, otab + 24 * i); objs.append((vs, pi, pc))
mem = {}
for n, (vs, pi, pc) in enumerate(objs, 1):
    b = bytearray(max(vs, pc * psz))
    for k in range(pc): pg = d[dpg + (pi - 1 + k) * psz: dpg + (pi + k) * psz]; b[k * psz:k * psz + len(pg)] = pg
    mem[n] = b
for n, (vs, pi, pc) in enumerate(objs, 1):
    for k in range(pc):
        page = pi - 1 + k; a, e = struct.unpack_from('<II', d, fpt + 4 * page); p = frt + a; e = frt + e
        while p < e:
            st, fl = d[p], d[p + 1]; p += 2
            if st & 0x20: cnt = d[p]; p += 1; srcs = None
            else: srcs = [struct.unpack_from('<h', d, p)[0]]; p += 2
            if fl & 3 == 0:
                if fl & 0x40: obj = struct.unpack_from('<H', d, p)[0]; p += 2
                else: obj = d[p]; p += 1
                if st & 0xf == 2: toff = 0
                elif fl & 0x10: toff = struct.unpack_from('<I', d, p)[0]; p += 4
                else: toff = struct.unpack_from('<H', d, p)[0]; p += 2
            else: raise SystemExit('non-internal fixup %x' % fl)
            if srcs is None: srcs = [struct.unpack_from('<h', d, p + 2 * j)[0] for j in range(cnt)]; p += 2 * cnt
            for so in srcs:
                o = k * psz + so
                if st & 0xf == 7 and 0 <= o <= len(mem[n]) - 4: struct.pack_into('<I', mem[n], o, obj * 0x100000 + toff)
                elif st & 0xf == 8 and 0 <= o <= len(mem[n]) - 4: struct.pack_into('<i', mem[n], o, obj * 0x100000 + toff - (n * 0x100000 + o + 4))
if sys.argv[2] == 'find':
    s = sys.argv[3].encode()
    for n, b in mem.items():
        i = b.find(s)
        while i >= 0:
            va = n * 0x100000 + i; print('string at %08X' % va); pat = struct.pack('<I', va)
            for n2, b2 in mem.items():
                j = b2.find(pat)
                while j >= 0: print('  ref at %08X' % (n2 * 0x100000 + j)); j = b2.find(pat, j + 1)
            i = b.find(s, i + 1)
elif sys.argv[2] == 'dis':
    a = int(sys.argv[3], 16); n = a >> 20; o = a & 0xfffff; cnt = int(sys.argv[4])
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    for ins in md.disasm(bytes(mem[n][o:o + cnt * 8]), a, cnt): print('%08X  %-28s %s %s' % (ins.address, ins.bytes.hex(), ins.mnemonic, ins.op_str))
