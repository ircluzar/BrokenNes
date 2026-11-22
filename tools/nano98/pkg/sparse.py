"""N98D: nano98's sparse + compressed disk container (read by DiskImage.ReadImageFile in the app, by every nano98 tool here).

    python -I sparse.py pack IMG OUT.n98d      # store only the non-zero sectors, deflated
    python -I sparse.py unpack IN.n98d OUT.img  # back to the raw image (byte-identical)
    python -I sparse.py info IN.n98d

Layout (little endian): 'N98D', int32 version 1, int64 disk length (bytes), byte n + the disk's identity hash (n bytes,
"p256:HEX", the same hash DiskImage.BaseHash gives the raw image), int32 chunk count, then per chunk: int64 first sector,
int32 sector count, int32 compressed length, the zlib (deflate, level 9) of those sectors. Chunks are runs of non-zero sectors,
at most 2048 sectors (1 MB) each, in LBA order; everything else reads as zero. Same image -> same bytes (deterministic).
The guest sees the full disk with its free space; the container only skips what reads as zero.
"""
import hashlib, os, struct, sys, zlib

MAGIC = b'N98D'
SECTOR = 512
CHUNK = 2048
PIECE = 16 << 20


def is_n98d(path):
    try:
        with open(path, 'rb') as f:
            return f.read(4) == MAGIC
    except OSError:
        return False


def p256_stream(read, length):
    """DiskImage's p256: SHA-256 over the SHA-256s of the 16 MB pieces."""
    parts = b''
    left = length
    while left > 0:
        b = read(min(PIECE, left)); left -= len(b)
        parts += hashlib.sha256(b).digest()
    return 'p256:' + hashlib.sha256(parts).hexdigest().upper()


def write(img, out):
    length = os.path.getsize(img)
    assert length % SECTOR == 0
    z = SECTOR * b'\0'
    chunks = []
    with open(img, 'rb') as f:
        h = p256_stream(f.read, length)
        f.seek(0)
        s, run, first = 0, [], 0
        while True:
            buf = f.read(1 << 24)
            if not buf:
                break
            for o in range(0, len(buf), SECTOR):
                sec = buf[o:o + SECTOR]
                if sec != z:
                    if not run:
                        first = s
                    run.append(sec)
                    if len(run) == CHUNK:
                        chunks.append((first, run)); run = []
                elif run:
                    chunks.append((first, run)); run = []
                s += 1
        if run:
            chunks.append((first, run))
    hb = h.encode()
    used = 0
    with open(out + '.tmp', 'wb') as o:
        o.write(MAGIC + struct.pack('<iq', 1, length) + bytes([len(hb)]) + hb + struct.pack('<i', len(chunks)))
        for first, run in chunks:
            c = zlib.compress(b''.join(run), 9)
            o.write(struct.pack('<qii', first, len(run), len(c))); o.write(c)
            used += len(run)
    os.replace(out + '.tmp', out)
    print(f'{out}: {length / 1e6:.0f} MB disk, {used * SECTOR / 1e6:.1f} MB non-zero in {len(chunks)} chunks, '
          f'{os.path.getsize(out) / 1e6:.1f} MB on file, {h}')
    return h


def write_mem(disk, out):
    """write() for a fat32.MemDisk (only its non-zero sectors exist): same container bytes as packing the raw image."""
    zero_piece = hashlib.sha256(bytes(PIECE)).digest()
    keys = sorted(disk.secs)
    parts = b''
    for pc in range((disk.length + PIECE - 1) // PIECE):
        a, b = pc * PIECE // SECTOR, min((pc + 1) * PIECE, disk.length) // SECTOR
        import bisect
        if bisect.bisect_left(keys, a) == bisect.bisect_left(keys, b) and (b - a) * SECTOR == PIECE:
            parts += zero_piece
        else:
            disk.seek(a * SECTOR); parts += hashlib.sha256(disk.read((b - a) * SECTOR)).digest()
    h = 'p256:' + hashlib.sha256(parts).hexdigest().upper()
    chunks, run, first = [], [], 0
    for k in keys:
        if run and (k != first + len(run) or len(run) == CHUNK):
            chunks.append((first, run)); run = []
        if not run: first = k
        run.append(disk.secs[k])
    if run: chunks.append((first, run))
    hb = h.encode()
    with open(out + '.tmp', 'wb') as o:
        o.write(MAGIC + struct.pack('<iq', 1, disk.length) + bytes([len(hb)]) + hb + struct.pack('<i', len(chunks)))
        for first, run in chunks:
            c = zlib.compress(b''.join(run), 9)
            o.write(struct.pack('<qii', first, len(run), len(c))); o.write(c)
    os.replace(out + '.tmp', out)
    print(f'{out}: {disk.length / 1e6:.0f} MB disk, {len(keys) * SECTOR / 1e6:.1f} MB non-zero, {os.path.getsize(out) / 1e6:.1f} MB on file, {h}')
    return h


def header(path):
    with open(path, 'rb') as f:
        if f.read(4) != MAGIC:
            raise ValueError(f'{path}: not N98D')
        ver, length = struct.unpack('<iq', f.read(12))
        if ver != 1:
            raise ValueError(f'{path}: N98D version {ver}')
        h = f.read(f.read(1)[0]).decode()
        return length, h


class SparseFile:
    """Read-only file object over an N98D (seek / read / tell), decompressing chunks on demand."""
    def __init__(self, path):
        self.f = open(path, 'rb')
        if self.f.read(4) != MAGIC:
            raise ValueError(f'{path}: not N98D')
        _, self.length = struct.unpack('<iq', self.f.read(12))
        self.hash = self.f.read(self.f.read(1)[0]).decode()
        n = struct.unpack('<i', self.f.read(4))[0]
        self.idx = []   # (first, count, file offset, clen)
        for _ in range(n):
            first, cnt, cl = struct.unpack('<qii', self.f.read(16))
            self.idx.append((first, cnt, self.f.tell(), cl)); self.f.seek(cl, 1)
        self.starts = [c[0] for c in self.idx]
        self.cache = {}
        self.pos = 0

    def seek(self, p, whence=0):
        self.pos = p if whence == 0 else (self.length + p if whence == 2 else self.pos + p)
        return self.pos

    def tell(self):
        return self.pos

    def _chunk(self, i):
        d = self.cache.get(i)
        if d is None:
            if len(self.cache) > 64:
                self.cache.clear()
            _, _, off, cl = self.idx[i]
            self.f.seek(off); d = self.cache[i] = zlib.decompress(self.f.read(cl))
        return d

    def read(self, n=-1):
        import bisect
        end = self.length if n < 0 else min(self.length, self.pos + n)
        out = bytearray(max(0, end - self.pos))
        p = self.pos
        i = max(0, bisect.bisect_right(self.starts, p // SECTOR) - 1)
        while i < len(self.idx):
            first, cnt, _, _ = self.idx[i]
            a, b = first * SECTOR, (first + cnt) * SECTOR
            if a >= end:
                break
            lo, hi = max(a, p), min(b, end)
            if lo < hi:
                d = self._chunk(i)
                out[lo - p:hi - p] = d[lo - a:hi - a]
            i += 1
        self.pos = end
        return bytes(out)

    def close(self):
        self.f.close()


def open_image(path, mode='rb'):
    """A raw image, or an N98D read-only."""
    if is_n98d(path):
        if mode != 'rb':
            raise ValueError(f'{path}: an N98D container is read-only (unpack it to write)')
        return SparseFile(path)
    return open(path, mode)


def unpack(src, out):
    s = SparseFile(src)
    with open(out, 'wb') as o:
        o.truncate(s.length)
        for i, (first, cnt, _, _) in enumerate(s.idx):
            o.seek(first * SECTOR); o.write(s._chunk(i))
    print(f'{out}: {s.length / 1e6:.0f} MB ({s.hash})')


if __name__ == '__main__':
    a = sys.argv[1:]
    if len(a) == 3 and a[0] == 'pack': write(a[1], a[2])
    elif len(a) == 3 and a[0] == 'unpack': unpack(a[1], a[2])
    elif len(a) == 2 and a[0] == 'info': print(*header(a[1]))
    else: sys.exit(__doc__)
