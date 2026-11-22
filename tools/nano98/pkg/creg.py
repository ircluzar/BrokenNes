"""Win9x CREG registry hives (SYSTEM.DAT / USER.DAT), read and patched from outside the guest.

Layout (all little endian; checksums are 0 / ignored on Win98 SE hives):
  CREG header 0x20: 'CREG', 0x00010000, rgdb_offset, checksum, rgdb_count(word), flags(word) ...
  RGKN at 0x20: 'RGKN', size, root_off, free_off, flags, checksum ... (0x20) then 0x1C-byte key nodes:
      inuse(0 = used, 0x80000000 = free), hash (sum of upper-cased name bytes), next_free,
      parent, child, next (offsets from RGKN start, 0xFFFFFFFF none), key_id(word), block(word)
  RGDB blocks: 'RGDB', size, unused, flags(w), section(w), free_off, max_id(w), first_free_id(w), uk, checksum
      records: size, id(w), block(w), used, name_len(w), nvals(w), uk, name, values
      value:   type, uk, name_len(w), data_len(w), name, data          free record: id = block = 0xFFFF
Patching keeps every existing byte where it is: changed keys get a new record in appended RGDB blocks
(old record marked free), new keys get appended RGKN nodes. Delete = unlink + mark free.
"""
import struct

NONE = 0xFFFFFFFF


class Key:
    def __init__(self, name, off=None):
        self.name, self.off = name, off
        self.values = []          # [(name, type, data)]
        self.children = {}        # upper name -> Key (insertion order kept)
        self.rec = None           # (block, id)


def _hash(name):
    return sum(name.encode('latin1').upper()) & 0xFFFFFFFF


class Hive:
    def __init__(self, data):
        d = self.d = bytearray(data)
        assert d[:4] == b'CREG' and d[0x20:0x24] == b'RGKN', 'not a CREG hive'
        self.rgkn = 0x20
        self.rgkn_size = struct.unpack_from('<I', d, 0x24)[0]
        self.rgdb = struct.unpack_from('<I', d, 8)[0]
        self.blocks = []          # (offset, size)
        recs = {}
        o = self.rgdb
        while o + 4 <= len(d) and d[o:o + 4] == b'RGDB':
            bsz = struct.unpack_from('<I', d, o + 4)[0]
            self.blocks.append((o, bsz))
            p, end = o + 0x20, o + bsz
            while p + 20 <= end:
                rsz, kid, kblk, used, nl, nv, _ = struct.unpack_from('<IHHIHHI', d, p)
                if rsz == 0:
                    break
                if kid != 0xFFFF and used != NONE:
                    name = bytes(d[p + 20:p + 20 + nl]).decode('latin1')
                    q, vals = p + 20 + nl, []
                    for _ in range(nv):
                        t, _u, vnl, vdl = struct.unpack_from('<IIHH', d, q)
                        vn = bytes(d[q + 12:q + 12 + vnl]).decode('latin1')
                        vals.append((vn, t, bytes(d[q + 12 + vnl:q + 12 + vnl + vdl])))
                        q += 12 + vnl + vdl
                    recs[(kblk, kid)] = (name, vals, p)
                p += rsz
            o += bsz
        self.recs = recs
        self.root = self._load(struct.unpack_from('<I', d, self.rgkn + 8)[0], 0)

    def _node(self, off):
        return list(struct.unpack_from('<IIIIIIHH', self.d, self.rgkn + off))

    def _load(self, off, depth):
        n = self._node(off)
        kid, kblk = n[6], n[7]
        name, vals, _ = self.recs.get((kblk, kid), ('', [], None))
        k = Key(name, off)
        k.values = list(vals)
        k.rec = (kblk, kid)
        c = n[4]
        while c not in (NONE, 0) and depth < 128:
            ch = self._load(c, depth + 1)
            k.children[ch.name.upper()] = ch
            c = self._node(c)[5]
        return k

    # ---- queries ----
    def find(self, path, create=False):
        k = self.root
        for part in [p for p in path.split('\\') if p]:
            nxt = k.children.get(part.upper())
            if nxt is None:
                if not create:
                    return None
                nxt = Key(part)
                k.children[part.upper()] = nxt
                nxt._new, nxt._parent = True, k
            k = nxt
        return k

    def flat(self):
        """{path: {value_name: (type, data)}} for every key."""
        out = {}

        def rec(k, p):
            out[p] = {vn.upper(): (vn, t, v) for vn, t, v in k.values}
            for ch in k.children.values():
                rec(ch, (p + '\\' if p else '') + ch.name)
        for ch in self.root.children.values():
            rec(ch, ch.name)
        return out

    # ---- patching ----
    def apply(self, delta):
        """delta = list of ops from diff(); returns new hive bytes."""
        changed = set()
        for op in delta:
            path = op['key']
            if op['op'] == 'delkey':
                self._delkey(path)
                continue
            k = self.find(path, create=True)
            if op['op'] == 'set':
                vn = op['name']
                k.values = [v for v in k.values if v[0].upper() != vn.upper()]
                k.values.append((vn, op['type'], bytes.fromhex(op['data'])))
            elif op['op'] == 'delval':
                k.values = [v for v in k.values if v[0].upper() != op['name'].upper()]
            changed.add(id(k))
            self._mark(k, changed)
        return self._write(changed)

    def _mark(self, k, changed):
        # new keys need a record too, and so do their new ancestors
        p = k
        while getattr(p, '_new', False):
            changed.add(id(p))
            p = p._parent
        self._changed_keys = getattr(self, '_changed_keys', {})
        q = k
        while q is not None:
            self._changed_keys[id(q)] = q
            q = getattr(q, '_parent', None) if getattr(q, '_new', False) else None

    def _delkey(self, path):
        parts = [p for p in path.split('\\') if p]
        parent = self.find('\\'.join(parts[:-1]))
        if parent is None:
            return
        k = parent.children.pop(parts[-1].upper(), None)
        if k is None:
            return
        self._dels = getattr(self, '_dels', []) + [(parent, k)]

    def _write(self, changed):
        d = self.d
        rk = self.rgkn
        # 1. unlink deleted keys
        for parent, k in getattr(self, '_dels', []):
            if k.off is None:
                continue
            pn = self._node(parent.off)
            if pn[4] == k.off:
                pn[4] = self._node(k.off)[5]
                struct.pack_into('<IIIIIIHH', d, rk + parent.off, *pn)
            else:
                c = pn[4]
                while c not in (NONE, 0):
                    cn = self._node(c)
                    if cn[5] == k.off:
                        cn[5] = self._node(k.off)[5]
                        struct.pack_into('<IIIIIIHH', d, rk + c, *cn)
                        break
                    c = cn[5]
            self._free_subtree(k)
        # 2. records for changed keys go into new RGDB blocks
        keys = list(getattr(self, '_changed_keys', {}).values())
        new_blocks = []
        blk_no = len(self.blocks)
        cur, cur_id = [], 0
        for k in keys:
            if k.off is not None:
                self._free_record(k.rec)
            if cur_id >= 0xFE:
                new_blocks.append(cur); cur, cur_id = [], 0; blk_no += 1
            k.rec = (blk_no, cur_id)
            cur.append(k); cur_id += 1
        if cur:
            new_blocks.append(cur)
        # 3. new RGKN nodes (appended after the last used node; RGKN grows in 4 KB steps)
        # the top of RGKN: the free node that runs to the end of the area (Windows keeps one there); new nodes go there
        end, prev_link = self.rgkn_size, None
        f, link = struct.unpack_from('<I', d, rk + 12)[0], rk + 12
        while f not in (NONE, 0) and f < self.rgkn_size:
            fn = self._node(f)
            if fn[0] == 0x80000000 and f + fn[1] == self.rgkn_size:
                end, prev_link = f, link
                break
            link, f = rk + f + 8, fn[2]
        top = end
        news = []

        def assign(k):
            nonlocal end
            if getattr(k, '_new', False) and k.off is None:
                k.off = end; end += 0x1C; news.append(k)
            for ch in k.children.values():
                assign(ch)
        assign(self.root)
        grow = 0
        if end > self.rgkn_size:
            grow = ((end - self.rgkn_size + 0xFFF) // 0x1000) * 0x1000
        out = bytearray(d[:rk + self.rgkn_size]) + bytearray(grow) + bytearray(d[self.rgdb:])
        newsize = self.rgkn_size + grow
        for k in news:
            par = k._parent
            struct.pack_into('<IIIIIIHH', out, rk + k.off, 0, _hash(k.name), NONE, par.off, NONE, NONE, 0, 0)
            # append to the parent's child chain
            pn = list(struct.unpack_from('<IIIIIIHH', out, rk + par.off))
            if pn[4] in (NONE, 0):
                pn[4] = k.off
                struct.pack_into('<IIIIIIHH', out, rk + par.off, *pn)
            else:
                c = pn[4]
                while True:
                    cn = list(struct.unpack_from('<IIIIIIHH', out, rk + c))
                    if cn[5] in (NONE, 0):
                        cn[5] = k.off
                        struct.pack_into('<IIIIIIHH', out, rk + c, *cn)
                        break
                    c = cn[5]
        # point every changed key's node at its new record
        for k in keys:
            n = list(struct.unpack_from('<IIIIIIHH', out, rk + k.off))
            n[7], n[6] = k.rec
            struct.pack_into('<IIIIIIHH', out, rk + k.off, *n)
        struct.pack_into('<I', out, rk + 4, newsize)
        # unlink the old top free node, then put a fresh one after the new nodes (head of the list)
        if prev_link is not None:
            struct.pack_into('<I', out, prev_link, self._node(top)[2])
        if end + 0x1C <= newsize:
            struct.pack_into('<IIIIIIHH', out, rk + end, 0x80000000, newsize - end,
                             struct.unpack_from('<I', out, rk + 12)[0], NONE, NONE, NONE, 0, 0)
            struct.pack_into('<I', out, rk + 12, end)
        # 4. emit the new RGDB blocks
        for bi, ks in enumerate(new_blocks):
            body = bytearray()
            for k in ks:
                nm = k.name.encode('latin1')
                vals = b''.join(struct.pack('<IIHH', t, 0, len(vn.encode('latin1')), len(v)) + vn.encode('latin1') + v
                                for vn, t, v in k.values)
                rlen = 20 + len(nm) + len(vals)
                body += struct.pack('<IHHIHHI', rlen, k.rec[1], k.rec[0], rlen, len(nm), len(k.values), 0) + nm + vals
            total = 0x20 + len(body) + 20
            bsz = ((total + 0xFFF) // 0x1000) * 0x1000
            freeoff = 0x20 + len(body)
            body += struct.pack('<IHHIHHI', bsz - freeoff, 0xFFFF, 0xFFFF, NONE, 0, 0, 0)
            body += bytes(bsz - 0x20 - len(body))
            hdr = struct.pack('<4sIIHHIHHII', b'RGDB', bsz, bsz - freeoff, 8, self.blocks.__len__() + bi,
                              freeoff, len(ks) - 1, len(ks), 0, 0)
            out += hdr + body
        struct.pack_into('<I', out, 8, rk + newsize)
        struct.pack_into('<H', out, 0x10, len(self.blocks) + len(new_blocks))
        return bytes(out)

    def _free_record(self, rec):
        """Free a key record the way Windows does: id = block = FFFF, 'used' = next free record offset, and the block's
        free list head (+0x10) and unused byte count (+8) updated. Registry Checker rejects a hive whose chain is off."""
        if rec not in self.recs:
            return
        p = self.recs.pop(rec)[2]
        o = next(bo for bo, bs in self.blocks if bo <= p < bo + bs)
        rsz = struct.unpack_from('<I', self.d, p)[0]
        head = struct.unpack_from('<I', self.d, o + 0x10)[0]
        struct.pack_into('<HHI', self.d, p + 4, 0xFFFF, 0xFFFF, head)
        struct.pack_into('<I', self.d, o + 0x10, p - o)
        struct.pack_into('<I', self.d, o + 8, struct.unpack_from('<I', self.d, o + 8)[0] + rsz)

    def _free_subtree(self, k):
        if k.off is None:
            return
        for ch in k.children.values():
            self._free_subtree(ch)
        n = self._node(k.off)
        # free node: inuse 0x80000000, +4 = free bytes, +8 = next free node; pushed on the RGKN free list (+0x0C)
        head = struct.unpack_from('<I', self.d, self.rgkn + 12)[0]
        n[0], n[1], n[2] = 0x80000000, 0x1C, head
        struct.pack_into('<IIIIIIHH', self.d, self.rgkn + k.off, *n)
        struct.pack_into('<I', self.d, self.rgkn + 12, k.off)
        self._free_record(k.rec)


def diff(a, b):
    """Ops that turn hive a into hive b (parents before children; deletes of whole subtrees once)."""
    fa, fb = a.flat(), b.flat()
    ua = {p.upper(): p for p in fa}
    ub = {p.upper(): p for p in fb}
    ops = []
    for up, p in ub.items():
        if up not in ua:
            ops.append({'op': 'key', 'key': p})
            for vn, t, v in fb[p].values():
                ops.append({'op': 'set', 'key': p, 'name': vn, 'type': t, 'data': v.hex()})
            continue
        va, vb = fa[ua[up]], fb[p]
        for n, (vn, t, v) in vb.items():
            if n not in va or va[n][1:] != (t, v):
                ops.append({'op': 'set', 'key': p, 'name': vn, 'type': t, 'data': v.hex()})
        for n, (vn, _, _) in va.items():
            if n not in vb:
                ops.append({'op': 'delval', 'key': p, 'name': vn})
    gone = sorted((p for up, p in ua.items() if up not in ub), key=len)
    done = []
    for p in gone:
        if any(p.upper().startswith(g.upper() + '\\') for g in done):
            continue
        ops.append({'op': 'delkey', 'key': p}); done.append(p)
    return ops


def to_reg(ops, root):
    """REGEDIT4 text of a delta (for reading / in-guest fallback). root e.g. HKEY_LOCAL_MACHINE."""
    lines, cur = ['REGEDIT4', ''], None
    for op in ops:
        if op['op'] == 'delkey':
            lines += ['', '[-%s\\%s]' % (root, op['key'])]; cur = None; continue
        if op['key'] != cur:
            lines += ['', '[%s\\%s]' % (root, op['key'])]; cur = op['key']
        if op['op'] == 'key':
            continue
        n = '@' if op['name'] == '' else '"%s"' % op['name'].replace('\\', '\\\\').replace('"', '\\"')
        if op['op'] == 'delval':
            lines.append('%s=-' % n); continue
        v = bytes.fromhex(op['data'])
        if op['type'] == 1 and v.endswith(b'\0') and b'\0' not in v[:-1]:
            s = v[:-1].decode('latin1').replace('\\', '\\\\').replace('"', '\\"')
            lines.append('%s="%s"' % (n, s))
        elif op['type'] == 4 and len(v) == 4:
            lines.append('%s=dword:%08x' % (n, struct.unpack('<I', v)[0]))
        else:
            tag = 'hex' if op['type'] == 3 else 'hex(%x)' % op['type']
            lines.append('%s=%s:%s' % (n, tag, ','.join('%02x' % c for c in v)))
    return '\r\n'.join(lines) + '\r\n'
