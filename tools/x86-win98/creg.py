# creg.py SYSTEM.DAT [path-substring...]: dump a Win9x CREG registry (keys + values) as "path\name = value"
import struct, sys
d = open(sys.argv[1], 'rb').read(); assert d[:4] == b'CREG'
rgkn = 0x20; assert d[rgkn:rgkn+4] == b'RGKN'
root = struct.unpack_from('<I', d, rgkn + 8)[0]
# RGDB blocks: map (block, id) -> (name, values)
recs = {}; off = struct.unpack_from('<I', d, 8)[0]; blk = 0
while off + 4 <= len(d) and d[off:off+4] == b'RGDB':
    bsz = struct.unpack_from('<I', d, off + 4)[0]; p = off + 0x20; end = off + bsz
    while p + 20 <= end:
        rsz, kid, kblk, used, nl, nv, _ = struct.unpack_from('<IHHIHHI', d, p)
        if rsz == 0: break
        if kid != 0xffff and used:
            name = d[p+20:p+20+nl].decode('latin1'); q = p + 20 + nl; vals = []
            for _ in range(nv):
                t, _u, vnl, vdl = struct.unpack_from('<IIHH', d, q); vn = d[q+12:q+12+vnl].decode('latin1'); vd = d[q+12+vnl:q+12+vnl+vdl]
                vals.append((vn, t, vd)); q += 12 + vnl + vdl
            recs[(kblk, kid)] = (name, vals)
        p += rsz
    off += bsz; blk += 1
def fmt(t, v):
    if t == 1: return '"' + v.rstrip(b'\0').decode('latin1') + '"'
    if t == 4 and len(v) == 4: return 'dword:%08x' % struct.unpack('<I', v)[0]
    return 'hex:' + v.hex(',')
flt = [s.upper() for s in sys.argv[2:]]
def walk(o, path, depth=0):
    while o not in (0xffffffff, 0) and depth < 64:
        _z, _h, _u, par, child, nxt, kid, kblk = struct.unpack_from('<IIIIIIHH', d, rgkn + o)
        name, vals = recs.get((kblk, kid), ('?', []))
        p = path + chr(92) + name if path else name
        if not flt or any(f in p.upper() for f in flt):
            print('[' + p + ']')
            for vn, t, vd in vals: print('  ' + (vn or '@') + ' = ' + fmt(t, vd))
        if child != 0xffffffff: walk(child, p, depth + 1)
        o = nxt
walk(root, '')
