"""Materialize the disks of an x86 BrokenNes state (PC83 ... "DSKD" block) onto their base images.

python -I state2img.py STATE OUTDIR BASE.img [BASE.img ...]
Each drive's base is found by sha256 among the given images; output OUTDIR/<drive>.img (copy-on-write
never touches the bases). Only drives with a diff are written.
"""
import hashlib, os, shutil, struct, sys

DRIVES = ['A', 'B', 'C', 'D']


def sha(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for b in iter(lambda: f.read(1 << 22), b''):
            h.update(b)
    return 'sha256:' + h.hexdigest().upper()


def read_str(d, p):
    n = s = 0
    while True:
        b = d[p]; p += 1
        n |= (b & 0x7F) << s; s += 7
        if b < 0x80:
            break
    return d[p:p + n].decode('utf-8'), p + n


def parse(d):
    """Yields (drive, floppy, length, hash, runs[(start, bytes)]) for a state's DSKD block."""
    # the DSKD block is the last thing in the state: scan back for a parse that ends exactly at EOF
    i = len(d)
    while True:
        i = d.rfind(b'DSKD', 0, i)
        if i < 0:
            raise ValueError('no DSKD block')
        try:
            out, p = [], i + 4
            for drv in DRIVES:
                present = d[p]; p += 1
                if not present:
                    continue
                floppy = d[p]; p += 1
                length = struct.unpack_from('<q', d, p)[0]; p += 8
                h, p = read_str(d, p)
                runs = struct.unpack_from('<i', d, p)[0]; p += 4
                rl = []
                for _ in range(runs):
                    st, n = struct.unpack_from('<qi', d, p); p += 12
                    rl.append((st, n, p)); p += n * 512
                out.append((drv, bool(floppy), length, h, rl))
            if p == len(d):
                return out
        except Exception:
            pass


def main(state, outdir, bases):
    d = open(state, 'rb').read()
    hashes = {}
    for b in bases:
        hashes[sha(b)] = b
    os.makedirs(outdir, exist_ok=True)
    for drv, floppy, length, h, runs in parse(d):
        n = sum(r[1] for r in runs)
        if h not in hashes:
            print(f'{drv}: base {h} not among the given images ({n} changed sectors)')
            continue
        out = os.path.join(outdir, drv + '.img')
        shutil.copyfile(hashes[h], out)
        with open(out, 'r+b') as f:
            for st, cnt, p in runs:
                f.seek(st * 512); f.write(d[p:p + cnt * 512])
        print(f'{drv}: {out} = {os.path.basename(hashes[h])} + {n} sectors')


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2], sys.argv[3:])
