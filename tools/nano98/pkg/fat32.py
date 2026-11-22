"""Minimal pure-Python FAT32 reader/writer for nano98 disk images (MBR + one FAT32 partition).

Read: walk the tree (LFN aware), read files.  Write: create dirs, write/replace/delete files
(LFN + 8.3 alias), on an image opened read-write.  No dependencies, so it runs on every fleet PC.
"""
import os, struct

SECTOR = 512


def _lfn_checksum(short11):
    s = 0
    for b in short11:
        s = (((s & 1) << 7) + (s >> 1) + b) & 0xFF
    return s


class Entry:
    __slots__ = ('name', 'short', 'attr', 'cluster', 'size', 'wtime', 'wdate', 'ctime', 'cdate',
                 'adate', 'slots', 'is_dir')

    def __init__(self, **k):
        for a in self.__slots__:
            setattr(self, a, k.get(a))


class Fat32:
    def __init__(self, path, write=False):
        self.f = path if hasattr(path, 'read') else open(path, 'r+b' if write else 'rb')
        self.write = write
        mbr = self._raw(0, 1)
        self.part_lba = 0
        if mbr[510:512] == b'\x55\xaa' and mbr[0x1C2] in (0x0B, 0x0C):
            self.part_lba = struct.unpack_from('<I', mbr, 0x1C6)[0]
        bs = self._raw(self.part_lba, 1)
        (self.bps, self.spc, self.rsvd, self.nfats) = struct.unpack_from('<HBHB', bs, 11)
        self.fatsz = struct.unpack_from('<I', bs, 36)[0]
        self.root = struct.unpack_from('<I', bs, 44)[0]
        self.fsinfo = struct.unpack_from('<H', bs, 48)[0]
        tot16, tot32 = struct.unpack_from('<H', bs, 19)[0], struct.unpack_from('<I', bs, 32)[0]
        self.total = tot32 or tot16
        assert self.bps == 512 and self.fatsz, 'not FAT32'
        self.fat_lba = self.part_lba + self.rsvd
        self.data_lba = self.fat_lba + self.nfats * self.fatsz
        self.nclus = (self.total - (self.data_lba - self.part_lba)) // self.spc + 2
        self.csize = self.spc * 512
        self.fat = bytearray(self._raw(self.fat_lba, self.fatsz))
        self.fat_dirty = False
        self._free_hint = 2

    # ---- raw ----
    def _raw(self, lba, n):
        self.f.seek(lba * 512)
        return self.f.read(n * 512)

    def _wraw(self, lba, data):
        self.f.seek(lba * 512)
        self.f.write(data)

    def next(self, c):
        return struct.unpack_from('<I', self.fat, c * 4)[0] & 0x0FFFFFFF

    def setnext(self, c, v):
        old = struct.unpack_from('<I', self.fat, c * 4)[0]
        struct.pack_into('<I', self.fat, c * 4, (old & 0xF0000000) | (v & 0x0FFFFFFF))
        self.fat_dirty = True

    def chain(self, c):
        out = []
        while 2 <= c < 0x0FFFFFF7 and len(out) < self.nclus:
            out.append(c)
            c = self.next(c)
        return out

    def clus_lba(self, c):
        return self.data_lba + (c - 2) * self.spc

    def read_chain(self, c, size=None):
        parts = []
        for cl in self.chain(c):
            parts.append(self._raw(self.clus_lba(cl), self.spc))
        d = b''.join(parts)
        return d if size is None else d[:size]

    # ---- directories ----
    def list_dir(self, cluster):
        data = self.read_chain(cluster)
        out, lfn, slots = [], {}, []
        for i in range(0, len(data), 32):
            e = data[i:i + 32]
            if e[0] == 0:
                break
            if e[0] == 0xE5:
                lfn, slots = {}, []
                continue
            if e[11] == 0x0F:
                seq = e[0] & 0x1F
                part = e[1:11] + e[14:26] + e[28:32]
                lfn[seq] = part
                slots.append(i)
                continue
            if e[11] & 0x08:  # volume label
                lfn, slots = {}, []
                continue
            short = e[0:11]
            base = short[0:8].decode('cp437').rstrip()
            ext = short[8:11].decode('cp437').rstrip()
            if short[0] == 0x05:
                base = 'å' + base[1:]
            sname = base + ('.' + ext if ext else '')
            if e[12] & 0x08: sname = sname.lower() if not ext else base.lower() + sname[len(base):]
            name = sname
            if lfn:
                raw = b''.join(lfn[k] for k in sorted(lfn))
                n = raw.decode('utf-16-le', 'replace')
                n = n.split('\x00')[0]
                name = n
            attr = e[11]
            cl = (struct.unpack_from('<H', e, 20)[0] << 16) | struct.unpack_from('<H', e, 26)[0]
            if sname not in ('.', '..'):
                out.append(Entry(name=name, short=bytes(short), attr=attr, cluster=cl,
                                 size=struct.unpack_from('<I', e, 28)[0],
                                 ctime=struct.unpack_from('<H', e, 14)[0], cdate=struct.unpack_from('<H', e, 16)[0],
                                 adate=struct.unpack_from('<H', e, 18)[0],
                                 wtime=struct.unpack_from('<H', e, 22)[0], wdate=struct.unpack_from('<H', e, 24)[0],
                                 slots=slots + [i], is_dir=bool(attr & 0x10)))
            lfn, slots = {}, []
        return out

    def walk(self, cluster=None, prefix=''):
        """Yields (path, Entry) for every file and directory. Paths use '/' and keep case."""
        if cluster is None:
            cluster = self.root
        for e in self.list_dir(cluster):
            p = prefix + e.name
            yield p, e
            if e.is_dir and e.cluster:
                yield from self.walk(e.cluster, p + '/')

    def lookup(self, path):
        cl, ent = self.root, None
        for part in [p for p in path.split('/') if p]:
            ent = None
            for e in self.list_dir(cl):
                if e.name.upper() == part.upper() or self._sn(e).upper() == part.upper():
                    ent = e
                    break
            if ent is None:
                return None, None
            cl = ent.cluster
        return ent, cl

    @staticmethod
    def _sn(e):
        b = e.short[0:8].decode('cp437').rstrip(); x = e.short[8:11].decode('cp437').rstrip()
        return b + ('.' + x if x else '')

    def read_file(self, path):
        e, _ = self.lookup(path)
        if e is None or e.is_dir:
            raise FileNotFoundError(path)
        return self.read_chain(e.cluster, e.size) if e.cluster else b''

    # ---- writing ----
    def _alloc(self, n):
        got, c = [], self._free_hint
        while len(got) < n:
            if c >= self.nclus:
                raise IOError('disk full')
            if self.next(c) == 0 and c >= 2:
                got.append(c)
            c += 1
        self._free_hint = c
        for a, b in zip(got, got[1:]):
            self.setnext(a, b)
        if got:
            self.setnext(got[-1], 0x0FFFFFFF)
        return got

    def _free(self, c):
        for cl in self.chain(c):
            self.setnext(cl, 0)
            self._free_hint = min(self._free_hint, cl)

    def _write_clusters(self, data, chain):
        for k, cl in enumerate(chain):
            blk = data[k * self.csize:(k + 1) * self.csize]
            self._wraw(self.clus_lba(cl), blk.ljust(self.csize, b'\0'))

    def _short_name(self, name, existing):
        up = name.upper()
        if '.' in up and not up.startswith('.'):
            b, x = up.rsplit('.', 1)
        else:
            b, x = up, ''
        ok = lambda s: ''.join(ch for ch in s if ch.isalnum() or ch in "$%'-_@~`!(){}^#&")
        b2, x2 = ok(b.replace(' ', '')), ok(x.replace(' ', ''))[:3]
        exact = (b2 == b and x2 == x and 0 < len(b) <= 8 and len(x) <= 3 and name == up)
        if exact and (b.ljust(8) + x.ljust(3)).encode() not in existing:
            return (b.ljust(8) + x.ljust(3)).encode('cp437'), False
        for i in range(1, 999999):
            t = '~%d' % i
            cand = (b2[:8 - len(t)] + t).ljust(8) + x2.ljust(3)
            cb = cand.encode('cp437')
            if cb not in existing:
                return cb, True
        raise IOError('no short name')

    def _dir_entries(self, name, short, need_lfn, attr, cluster, size, times):
        ents = []
        if need_lfn:
            u = name.encode('utf-16-le')
            chars = [u[i:i + 2] for i in range(0, len(u), 2)]
            n = (len(chars) + 12) // 13
            chars += [b'\0\0'] if len(chars) % 13 else []
            chars += [b'\xff\xff'] * (n * 13 - len(chars))
            ck = _lfn_checksum(short)
            for seq in range(n, 0, -1):
                c = chars[(seq - 1) * 13: seq * 13]
                e = bytearray(32)
                e[0] = seq | (0x40 if seq == n else 0)
                e[1:11] = b''.join(c[0:5]); e[11] = 0x0F; e[13] = ck
                e[14:26] = b''.join(c[5:11]); e[28:32] = b''.join(c[11:13])
                ents.append(bytes(e))
        ctime, cdate, adate, wtime, wdate = times
        e = bytearray(32)
        e[0:11] = short; e[11] = attr
        struct.pack_into('<HHHHHHHI', e, 14, ctime, cdate, adate, cluster >> 16, wtime, wdate, cluster & 0xFFFF, size)
        ents.append(bytes(e))
        return b''.join(ents)

    def _insert_entries(self, dcl, blob):
        """Place blob (n*32 bytes) into directory at cluster dcl, extending it if needed."""
        chain = self.chain(dcl)
        data = bytearray(self.read_chain(dcl))
        need = len(blob) // 32
        run, start = 0, None
        for i in range(0, len(data), 32):
            if data[i] in (0x00, 0xE5):
                if run == 0: start = i
                run += 1
                if run == need: break
            else:
                run = 0
        if run < need:
            # find end marker position
            end = len(data)
            for i in range(0, len(data), 32):
                if data[i] == 0: end = i; break
            start = end
            while start + len(blob) > len(data):
                nc = self._alloc(1)[0]
                self.setnext(chain[-1], nc); chain.append(nc)
                data += b'\0' * self.csize
        data[start:start + len(blob)] = blob
        self._write_clusters(bytes(data), chain)

    def _existing_shorts(self, dcl):
        return {e.short for e in self.list_dir(dcl)}

    def mkdir(self, path, times=(0, 0, 0, 0, 0)):
        parts = [p for p in path.split('/') if p]
        cl = self.root
        for i, part in enumerate(parts):
            ent = next((e for e in self.list_dir(cl) if e.name.upper() == part.upper()
                        or self._sn(e).upper() == part.upper()), None)
            if ent is None:
                nc = self._alloc(1)[0]
                short, lfn = self._short_name(part, self._existing_shorts(cl))
                dot = self._dir_entries('.', b'.          ', False, 0x10, nc, 0, times)
                dd = self._dir_entries('..', b'..         ', False, 0x10, 0 if cl == self.root else cl, 0, times)
                self._write_clusters(dot + dd, [nc])
                self._insert_entries(cl, self._dir_entries(part, short, lfn, 0x10, nc, 0, times))
                cl = nc
            else:
                assert ent.is_dir, path
                cl = ent.cluster
        return cl

    def delete(self, path):
        parts = [p for p in path.split('/') if p]
        _, dcl = self.lookup('/'.join(parts[:-1])) if len(parts) > 1 else (None, self.root)
        if dcl is None:
            return False
        for e in self.list_dir(dcl):
            if e.name.upper() == parts[-1].upper() or self._sn(e).upper() == parts[-1].upper():
                if e.is_dir:
                    for sub in self.list_dir(e.cluster):
                        self.delete(path + '/' + sub.name)
                if e.cluster:
                    self._free(e.cluster)
                chain = self.chain(dcl)
                data = bytearray(self.read_chain(dcl))
                for s in e.slots:
                    data[s] = 0xE5
                self._write_clusters(bytes(data), chain)
                return True
        return False

    def write_file(self, path, content, attr=0x20, times=(0, 0, 0, 0, 0)):
        parts = [p for p in path.split('/') if p]
        self.delete(path)
        dcl = self.mkdir('/'.join(parts[:-1])) if len(parts) > 1 else self.root
        n = (len(content) + self.csize - 1) // self.csize
        chain = self._alloc(n) if n else []
        self._write_clusters(content, chain)
        short, lfn = self._short_name(parts[-1], self._existing_shorts(dcl))
        self._insert_entries(dcl, self._dir_entries(parts[-1], short, lfn, attr, chain[0] if chain else 0,
                                                    len(content), times))

    def close(self):
        if self.write and self.fat_dirty:
            for k in range(self.nfats):
                self._wraw(self.fat_lba + k * self.fatsz, bytes(self.fat))
            if self.fsinfo:  # invalidate free count so Windows recounts
                fi = bytearray(self._raw(self.part_lba + self.fsinfo, 1))
                if fi[0:4] == b'RRaA':
                    struct.pack_into('<II', fi, 488, 0xFFFFFFFF, 0xFFFFFFFF)
                    self._wraw(self.part_lba + self.fsinfo, bytes(fi))
        if not hasattr(self.f, 'overlay') and not hasattr(self.f, 'secs'):
            self.f.close()


class CowFile:
    """A disk image opened copy-on-write: reads fall through to the base file, writes land in a sector overlay."""
    def __init__(self, base_path):
        import sparse
        self.base = sparse.open_image(base_path)
        self.base.seek(0, 2); self.length = self.base.tell()
        self.overlay = {}
        self.pos = 0

    def seek(self, p, whence=0):
        self.pos = p if whence == 0 else (self.length + p if whence == 2 else self.pos + p)
        return self.pos

    def tell(self):
        return self.pos

    def _sector(self, s):
        v = self.overlay.get(s)
        if v is not None:
            return v
        self.base.seek(s * SECTOR)
        return self.base.read(SECTOR)

    def read(self, n):
        out = bytearray()
        p, end = self.pos, min(self.pos + n, self.length)
        while p < end:
            s, o = divmod(p, SECTOR)
            if o == 0 and end - p >= SECTOR and s not in self.overlay:
                # run of base sectors at once
                e = s + 1
                lim = (end - p) // SECTOR + s
                while e < lim and e not in self.overlay:
                    e += 1
                self.base.seek(p); out += self.base.read((e - s) * SECTOR); p = e * SECTOR
                continue
            take = min(SECTOR - o, end - p)
            out += self._sector(s)[o:o + take]; p += take
        self.pos = p
        return bytes(out)

    def write(self, data):
        p, i = self.pos, 0
        while i < len(data):
            s, o = divmod(p, SECTOR)
            take = min(SECTOR - o, len(data) - i)
            sec = bytearray(self._sector(s))
            sec[o:o + take] = data[i:i + take]
            self.overlay[s] = bytes(sec)
            p += take; i += take
        self.pos = p
        return len(data)

    def drop_unchanged(self):
        for s in list(self.overlay):
            self.base.seek(s * SECTOR)
            if self.base.read(SECTOR) == self.overlay[s]:
                del self.overlay[s]

    def save_layer(self, path, base_hash):
        """N98L layer = the DSKD diff layout (base length + hash + runs of 512-byte sectors)."""
        self.drop_unchanged()
        keys = sorted(self.overlay)
        runs, i = [], 0
        while i < len(keys):
            j = i + 1
            while j < len(keys) and keys[j] == keys[j - 1] + 1:
                j += 1
            runs.append(keys[i:j]); i = j
        hb = base_hash.encode()
        with open(path, 'wb') as f:
            f.write(b'N98L' + struct.pack('<iq', 1, self.length) + bytes([len(hb)]) + hb + struct.pack('<i', len(runs)))
            for r in runs:
                f.write(struct.pack('<qi', r[0], len(r)))
                for s in r:
                    f.write(self.overlay[s])
        return len(keys)

    def save_full(self, path):
        self.drop_unchanged()
        import shutil
        self.base.seek(0)
        with open(path, 'wb') as f:
            shutil.copyfileobj(self.base, f, 1 << 22)
            for s, v in self.overlay.items():
                f.seek(s * SECTOR); f.write(v)


def open_ro(path):
    """A raw image or an N98D sparse container, read-only (for Fat32(...))."""
    import sparse
    return sparse.open_image(path)


class MemDisk:
    """A sparse in-memory disk (only written sectors are kept; the rest reads as zero): build a drive without a 2 GB file."""
    def __init__(self, length):
        self.length, self.secs, self.pos = length, {}, 0

    def seek(self, p, whence=0):
        self.pos = p if whence == 0 else (self.length + p if whence == 2 else self.pos + p)
        return self.pos

    def tell(self):
        return self.pos

    def truncate(self, n=None):
        pass

    def read(self, n):
        out = bytearray(); p, end = self.pos, min(self.pos + n, self.length)
        while p < end:
            s, o = divmod(p, SECTOR); take = min(SECTOR - o, end - p)
            out += self.secs.get(s, bytes(SECTOR))[o:o + take]; p += take
        self.pos = p
        return bytes(out)

    def write(self, data):
        p, i = self.pos, 0
        while i < len(data):
            s, o = divmod(p, SECTOR); take = min(SECTOR - o, len(data) - i)
            if o == 0 and take == SECTOR:
                sec = bytes(data[i:i + SECTOR])
            else:
                sec = bytearray(self.secs.get(s, bytes(SECTOR))); sec[o:o + take] = data[i:i + take]; sec = bytes(sec)
            if sec.count(0) == SECTOR: self.secs.pop(s, None)
            else: self.secs[s] = sec
            p += take; i += take
        self.pos = p
        return len(data)

    def close(self):
        pass


def mkfs(path, size_mb, label=b'NANO98GAME ', serial=0x4E393844, spc=8):
    """A new MBR disk image with one FAT32 LBA partition at sector 63 (the nano98 drive standard: same size -> byte-identical
    empty disk, fixed volume serial, FSInfo free count / next-free "unknown", so a warm-boot state taken on one game drive
    reads the same boot sector + FSInfo on every other one). No boot code (game drives are never booted)."""
    total = size_mb * 1024 * 1024 // SECTOR
    part, rsvd, nfats = 63, 32, 2
    psec = total - part
    fatsz = 1
    while True:
        nclus = (psec - rsvd - nfats * fatsz) // spc
        need = ((nclus + 2) * 4 + SECTOR - 1) // SECTOR
        if need <= fatsz:
            break
        fatsz = need
    import contextlib
    with (contextlib.nullcontext(path) if hasattr(path, 'write') else open(path, 'wb')) as f:
        f.truncate(total * SECTOR)
        mbr = bytearray(SECTOR)

        def chs(lba):
            c, r = divmod(lba, 255 * 63); h, s = divmod(r, 63); c = min(c, 1023)
            return bytes([h, ((c >> 2) & 0xc0) | (s + 1), c & 0xff])
        mbr[446:462] = bytes([0x00]) + chs(part) + bytes([0x0c]) + chs(total - 1) + struct.pack('<II', part, psec)
        mbr[440:444] = struct.pack('<I', serial)
        mbr[510:512] = b'\x55\xaa'
        f.seek(0); f.write(mbr)
        bs = bytearray(SECTOR)
        bs[0:3] = b'\xeb\x58\x90'; bs[3:11] = b'MSWIN4.1'
        struct.pack_into('<HBHBHHBHHHII', bs, 11, SECTOR, spc, rsvd, nfats, 0, 0, 0xF8, 0, 63, 255, part, psec)
        struct.pack_into('<IHHIHH', bs, 36, fatsz, 0, 0, 2, 1, 6)
        struct.pack_into('<BBBI', bs, 64, 0x80, 0, 0x29, serial)
        bs[71:82] = label[:11].ljust(11); bs[82:90] = b'FAT32   '
        bs[510:512] = b'\x55\xaa'
        fi = bytearray(SECTOR)
        fi[0:4] = b'RRaA'; fi[484:488] = b'rrAa'
        struct.pack_into('<II', fi, 488, 0xFFFFFFFF, 0xFFFFFFFF); fi[510:512] = b'\x55\xaa'
        for b0 in (0, 6):
            f.seek((part + b0) * SECTOR); f.write(bs); f.write(fi)
            f.seek((part + b0 + 2) * SECTOR); f.write(b'\0' * 510 + b'\x55\xaa')
        # The standard layout (2026-11-21, nano98 warm boot): what Windows reads when it mounts the drive must be the same on every game
        # drive: FAT sector 0 = clusters 0..127 (root = 2, the reserved file N98RSVD.SYS = 3..127, GAME = 128), and the root directory
        # (label, N98RSVD.SYS, GAME). Games live in D:\GAME; the rest of the FAT and GAME's own cluster are free to differ.
        fat = bytearray(SECTOR)
        struct.pack_into('<III', fat, 0, 0x0FFFFFF8, 0x0FFFFFFF, 0x0FFFFFFF)
        for c in range(3, 127):
            struct.pack_into('<I', fat, c * 4, c + 1)
        struct.pack_into('<I', fat, 127 * 4, 0x0FFFFFFF)
        fat1 = bytearray(SECTOR); struct.pack_into('<I', fat1, 0, 0x0FFFFFFF)   # cluster 128 (GAME): one cluster when empty
        data = part + rsvd + nfats * fatsz
        # two more places Windows reads at mount (measured): FAT sector 408 and one data sector; their clusters are marked bad on every
        # standard drive, so both read the same everywhere (nothing is ever allocated there)
        bad = bytearray(SECTOR)
        for i in range(0, SECTOR, 4): struct.pack_into('<I', bad, i, 0x0FFFFFF7)
        probe = ({4000: 7184653, 8000: 6089731, 12000: 2704453, 16000: 10121746, 32000: 5964151}.get(size_mb, 3598978) - data) // spc + 2   # per size, measured (N98Seat.ProbeSector)
        for k in range(nfats):
            f.seek((part + rsvd + k * fatsz) * SECTOR); f.write(bytes(fat)); f.write(bytes(fat1))
            f.seek((part + rsvd + k * fatsz + 408) * SECTOR); f.write(bytes(bad))
            f.seek((part + rsvd + k * fatsz) * SECTOR + probe * 4); f.write(struct.pack('<I', 0x0FFFFFF7))
        def ent(name, attr, cl, size):
            e = bytearray(32); e[0:11] = name; e[11] = attr
            struct.pack_into('<HHHHHHHI', e, 14, 0, 0x21, 0x21, cl >> 16, 0, 0x21, cl & 0xFFFF, size)   # 1980-01-01, fixed
            return bytes(e)
        root = bytearray(32); root[0:11] = label[:11].ljust(11); root[11] = 0x08
        root += ent(b'N98RSVD SYS', 0x07, 3, 125 * spc * SECTOR) + ent(b'GAME       ', 0x10, 128, 0)
        f.seek(data * SECTOR); f.write(bytes(root))
        f.seek((data + (128 - 2) * spc) * SECTOR); f.write(ent(b'.          ', 0x10, 128, 0) + ent(b'..         ', 0x10, 0, 0))
    return path
