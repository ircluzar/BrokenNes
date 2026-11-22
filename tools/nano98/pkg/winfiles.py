"""winfiles.py: the "Windows 98 SE files" disc for the install wizard.

Some game setups install Windows components (codecs, MCI drivers: Age of Empires asks for mciavi.drv) and Windows then asks for its own
CD. The seat base is culled (slim.py drops the setup CABs), so the wizard puts this disc in the CD drive when Windows asks: an ISO 9660
image (label WIN98SE) holding only WIN98\\*.CAB + the setup INFs, made from the user's own Windows 98 SE disc (a real SE disc with \\WIN98,
or a compilation ISO with WIN98SE.ZIP, as build_base.py reads it). The seat's Setup SourcePath points at E:\\WIN98 during installs.
The result is the user's own copy: kept local (%LOCALAPPDATA%\\BrokenNes\\nano98\\win98se_files.iso), never shipped.

    python -I winfiles.py WIN98_ISO OUT.iso [--zip /WIN98SE.ZIP]
"""
import argparse, io, struct, sys, zipfile

S = 2048


def read_iso(path):
    f = open(path, 'rb')
    def rd(l, n): f.seek(l * S); return f.read(n)
    p = rd(16, S)
    if p[1:6] != b'CD001': sys.exit('WINFILES ' + '{"ok": false, "error": "not an ISO 9660 disc image"}')
    files = {}
    def walk(l, n, pre):
        b = rd(l, n); i = 0
        while i < len(b):
            ln = b[i]
            if ln == 0: i = (i // S + 1) * S; continue
            el, es = struct.unpack_from('<I', b, i + 2)[0], struct.unpack_from('<I', b, i + 10)[0]
            nm = b[i + 33:i + 33 + b[i + 32]]; fl = b[i + 25]; i += ln
            if nm in (b'\0', b'\1'): continue
            name = nm.decode('latin1').split(';')[0].rstrip('.')
            if fl & 2:
                if pre.count('/') < 2: walk(el, es, pre + name + '/')
            else: files[pre + name] = (el, es)
    walk(struct.unpack_from('<I', p, 158)[0], struct.unpack_from('<I', p, 166)[0], '/')
    return rd, files


def media(path, zipname):
    rd, files = read_iso(path)
    up = {k.upper(): v for k, v in files.items()}
    out = {}
    if any(k.startswith('/WIN98/') and k.endswith('.CAB') for k in up):
        for k, (l, s) in up.items():
            if k.startswith('/WIN98/') and k.count('/') == 2: out[k[7:]] = rd(l, s)
    elif zipname.upper() in up:
        l, s = up[zipname.upper()]
        z = zipfile.ZipFile(io.BytesIO(rd(l, s)))
        for n in z.namelist():
            b = n.replace('\\', '/').split('/')[-1].upper()
            if b and (b.endswith('.CAB') or b.endswith('.INF') or b in ('SETUP.EXE',)): out[b] = z.read(n)
    if not any(k.startswith('WIN98_') for k in out) or 'BASE4.CAB' not in out and not any(k.startswith('BASE') for k in out):
        sys.exit('WINFILES {"ok": false, "error": "this is not a Windows 98 Second Edition disc (no WIN98 setup CABs found)"}')
    return out


def write_iso(out, files):
    """A plain ISO 9660 level 1 image: root -> WIN98 -> the files (8.3 names), L and M path tables."""
    names = sorted(files)
    def rec(lba, size, name, flags):
        n = name if isinstance(name, bytes) else name.encode('ascii')
        ln = 33 + len(n) + (1 - len(n) % 2)
        r = bytearray(ln); r[0] = ln
        struct.pack_into('<I', r, 2, lba); struct.pack_into('>I', r, 6, lba)
        struct.pack_into('<I', r, 10, size); struct.pack_into('>I', r, 14, size)
        r[18:25] = bytes([98, 1, 1, 0, 0, 0, 0]); r[25] = flags
        struct.pack_into('<H', r, 28, 1); struct.pack_into('>H', r, 30, 1); r[32] = len(n); r[33:33 + len(n)] = n
        return bytes(r)
    # layout: 16 PVD, 17 terminator, 18 L path table, 19 M path table, 20 root, 21.. WIN98 dir, then files
    win_recs_len = 68 + sum(33 + len(n) + 2 + (1 - (len(n) + 2) % 2) for n in names)
    win_secs = (win_recs_len + S - 1) // S + 1
    lba = 21 + win_secs
    pos = {}
    for n in names:
        pos[n] = lba; lba += (len(files[n]) + S - 1) // S
    total = lba
    root = rec(20, S, b'\0', 2) + rec(20, S, b'\1', 2) + rec(21, win_secs * S, 'WIN98', 2)
    wd = bytearray(rec(21, win_secs * S, b'\0', 2) + rec(20, S, b'\1', 2))
    for n in names:
        r = rec(pos[n], len(files[n]), n + ';1', 0)
        if len(wd) // S != (len(wd) + len(r) - 1) // S: wd += bytes(S - len(wd) % S)
        wd += r
    def ptab(be):
        P = '>' if be else '<'
        t = bytes([1, 0]) + struct.pack(P + 'I', 20) + struct.pack(P + 'H', 1) + b'\0\0'
        t += bytes([5, 0]) + struct.pack(P + 'I', 21) + struct.pack(P + 'H', 1) + b'WIN98' + b'\0'
        return t
    pt = ptab(False)
    pvd = bytearray(S); pvd[0] = 1; pvd[1:6] = b'CD001'; pvd[6] = 1
    pvd[8:40] = b'BROKENNES'.ljust(32); pvd[40:72] = b'WIN98SE'.ljust(32)
    struct.pack_into('<I', pvd, 80, total); struct.pack_into('>I', pvd, 84, total)
    struct.pack_into('<H', pvd, 120, 1); struct.pack_into('>H', pvd, 122, 1); struct.pack_into('<H', pvd, 124, 1); struct.pack_into('>H', pvd, 126, 1)
    struct.pack_into('<H', pvd, 128, S); struct.pack_into('>H', pvd, 130, S)
    struct.pack_into('<I', pvd, 132, len(pt)); struct.pack_into('>I', pvd, 136, len(pt))
    struct.pack_into('<I', pvd, 140, 18); struct.pack_into('>I', pvd, 148, 19)
    pvd[156:190] = rec(20, S, b'\0', 2)
    for o in (190, 318, 446, 574): pvd[o:o + 128] = b' ' * 128
    for o in (813, 830, 847, 864): pvd[o:o + 17] = b'0' * 16 + b'\0'
    pvd[881] = 1
    term = bytearray(S); term[0] = 255; term[1:6] = b'CD001'; term[6] = 1
    with open(out, 'wb') as f:
        f.write(bytes(16 * S)); f.write(pvd); f.write(term)
        f.write(pt.ljust(S, b'\0')); f.write(ptab(True).ljust(S, b'\0'))
        f.write(root.ljust(S, b'\0')); f.write(bytes(wd).ljust(win_secs * S, b'\0'))
        for n in names:
            d = files[n]; f.write(d); f.write(bytes((-len(d)) % S))
    return total


if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('iso'); ap.add_argument('out'); ap.add_argument('--zip', default='/WIN98SE.ZIP')
    a = ap.parse_args()
    m = media(a.iso, a.zip)
    n = write_iso(a.out, m)
    import json
    print('WINFILES ' + json.dumps({'ok': True, 'iso': a.out, 'files': len(m), 'sectors': n}))
