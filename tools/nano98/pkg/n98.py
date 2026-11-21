"""nano98 game packages (.n98): capture, inspect, apply.  Schema: docs/projects/nano98-format.md

  python -I n98.py capture OUT.n98 --title T --before C=base.img [D=data.img] --after C=inst.img [D=...]
                     [--profile profile.json] [--exclude GLOB ...] [--keep-reg-noise] [--deep]
  python -I n98.py info PKG.n98
  python -I n98.py apply PKG.n98 --base C=base.img [D=data.img] --out DIR [--full] [--saves SAVES.n98s]

capture diffs the FAT32 trees + the SYSTEM.DAT / USER.DAT hives of a disk before and after an install,
entirely from outside the guest. apply builds a game disk = base + the package layer, copy-on-write:
the base images and the package are only read. Output per drive: DIR/<drive>.n98l (sector layer over the
base, same layout as the x86 state DSKD diff) and with --full DIR/<drive>.img too.
"""
import argparse, datetime, fnmatch, hashlib, io, json, os, sys, zipfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import creg, fat32  # noqa: E402

SCHEMA = 1
CRLF = chr(13) + chr(10)
HIVES = {'WINDOWS/SYSTEM.DAT': 'HKEY_LOCAL_MACHINE', 'WINDOWS/USER.DAT': 'HKEY_USERS'}
# files that change on every boot / every session: never part of a game's install
FILE_NOISE = [
    'WINDOWS/SYSTEM.DAT', 'WINDOWS/USER.DAT', 'WINDOWS/*.DA0', 'WINDOWS/SYSTEM.CB', 'WINDOWS/WIN386.SWP',
    'BOOTLOG.*', 'SCANDISK.LOG', 'DETLOG.*', 'SETUPLOG.*', 'NETLOG.TXT', 'SUHDLOG.*', 'FRUNLOG.*',
    'WINDOWS/TEMP/*', 'WINDOWS/RECENT/*', 'WINDOWS/HISTORY/*', 'WINDOWS/TEMPORARY INTERNET FILES/*',
    'WINDOWS/COOKIES/*', 'WINDOWS/APPLOG/*', 'WINDOWS/SYSBCKUP/*', 'WINDOWS/TTFCACHE', 'WINDOWS/WININIT.BAK',
    'WINDOWS/SCHEDLOG.TXT', 'WINDOWS/INF/*.PNF', 'WINDOWS/INF/OTHER/*', 'WINDOWS/SYSTEM/IOSUBSYS/*.PDR.BAK',
    'WINDOWS/SHELLICONCACHE', 'WINDOWS/*.LOG', 'WINDOWS/SYSTEM/VMM32/*', 'WINDOWS/SYSTEM.1ST', 'SYSTEM.1ST',
    'WINDOWS/CONFIG/*', 'WINDOWS/SYSBCKUP', 'WINDOWS/SPOOL/*', '*/THUMBS.DB', 'WINDOWS/WINSTART.BAT',
    'WINDOWS/DOWNLOADED PROGRAM FILES/*', 'WINDOWS/WININIT.INI', 'WINDOWS/CATROOT/*', 'WINDOWS/NDISLOG.TXT',
    'WINDOWS/TASKS/SA.DAT', 'WINDOWS/START MENU/PROGRAMS/STARTUP/*.PIF',
]
# registry: games live under Software; Enum / Config / hardware state and shell/MRU churn is not the game
REG_KEEP = ['SOFTWARE\\', '.DEFAULT\\SOFTWARE\\']
REG_NOISE = [
    '*\\EXPLORER\\STREAMS*', '*\\EXPLORER\\STUCKRECTS*', '*\\EXPLORER\\DESKVIEW*', '*\\EXPLORER\\RUNMRU*',
    '*\\EXPLORER\\RECENTDOCS*', '*\\EXPLORER\\SHELLNOROAM*', '*\\EXPLORER\\DOC FIND SPEC MRU*', '*\\EXPLORER\\USERASSIST*',
    '*\\INTERNET EXPLORER\\DESKTOP\\OLD WORKAREAS*', '*\\MICROSOFT\\SCHEDULINGAGENT*', '*\\CURRENTVERSION\\WELCOME*',
    '*\\CURRENTVERSION\\SYSDM*', '*\\CURRENTVERSION\\CONTROLS FOLDER*', '*\\CURRENTVERSION\\SETUP\\SETUPX*',
    '*\\CURRENTVERSION\\RUNONCE*', '*\\NOTIFICATIONMGR*', '*\\DIRECTSOUND\\DEVICE PRESENCE*', 'SOFTWARE\\CLASSES\\AUTORUN*',
    '*\\CFGMGR32\\PHANTOM*', '*\\CURRENTVERSION\\EXPLORER\\TRAY*', '*\\CURRENTVERSION\\APPLETS\\REGEDIT*', '*\\WINDOWS\\CURRENTVERSION\\FONTCACHE*',
    '*\\CURRENTVERSION\\INTERNET SETTINGS\\CACHE*', '*\\CURRENTVERSION\\EXPLORER\\MENUORDER*',
]


def drives(specs):
    out = {}
    for s in specs or []:
        k, _, v = s.partition('=')
        out[k.upper().rstrip(':')] = v
    return out


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for b in iter(lambda: f.read(1 << 22), b''):
            h.update(b)
    return 'sha256:' + h.hexdigest().upper()


def tree(fs):
    return {p.upper(): (p, e) for p, e in fs.walk()}


def noise(path, pats):
    u = path.upper()
    return any(fnmatch.fnmatchcase(u, p.upper()) for p in pats)


REG_VALUE_NOISE = [('*\CURRENTVERSION\RUN', 'WELCOME')]   # the Welcome tour a first boot leaves


def reg_filtered(ops, keep_noise):
    if keep_noise:
        return ops, 0
    ops = [o for o in ops if not any(fnmatch.fnmatchcase(o['key'].upper(), k) and o.get('name', '').upper() == n
                                     for k, n in REG_VALUE_NOISE)]
    kept = [o for o in ops if any(o['key'].upper().startswith(k) for k in REG_KEEP)
            and not any(fnmatch.fnmatchcase(o['key'].upper() + '\\', p) for p in REG_NOISE)]
    return kept, len(ops) - len(kept)


def ini_parse(b):
    """[(section, key, value)] in file order; section '' = before the first header."""
    out, sec = [], ''
    for ln in b.decode('latin1').splitlines():
        t = ln.strip()
        if t.startswith('[') and t.endswith(']'):
            sec = t[1:-1]; out.append((sec, None, None))
        elif '=' in t and not t.startswith(';'):
            k, _, v = t.partition('='); out.append((sec, k.strip(), v.strip()))
    return out


def ini_diff(a, b):
    """Ops {section, key, value (None = delete)} turning INI a into b (keys compared case-blind)."""
    da = {(s.upper(), k.upper()): v for s, k, v in ini_parse(a) if k is not None}
    db = {(s.upper(), k.upper()): (s, k, v) for s, k, v in ini_parse(b) if k is not None}
    ops = [{'section': s, 'key': k, 'value': v} for uk, (s, k, v) in db.items() if da.get(uk) != v]
    sa = {(s.upper(), k.upper()): (s, k) for s, k, v in ini_parse(a) if k is not None}
    ops += [{'section': sa[uk][0], 'key': sa[uk][1], 'value': None} for uk in da if uk not in db]
    return ops


def ini_apply(text, ops):
    lines = text.decode('latin1').splitlines()
    for op in ops:
        su, ku = op['section'].upper(), op['key'].upper()
        sec, hdr, hit = '', None, None
        for i, ln in enumerate(lines):
            t = ln.strip()
            if t.startswith('[') and t.endswith(']'):
                sec = t[1:-1].upper()
                if sec == su: hdr = i
            elif sec == su and '=' in t and t.partition('=')[0].strip().upper() == ku:
                hit = i
        if hit is not None:
            if op['value'] is None: del lines[hit]
            else: lines[hit] = '%s=%s' % (op['key'], op['value'])
        elif op['value'] is not None:
            if hdr is None:
                lines += ['', '[%s]' % op['section']]; hdr = len(lines) - 1
            j = hdr + 1
            while j < len(lines) and not lines[j].strip().startswith('['):
                j += 1
            while j > hdr + 1 and not lines[j - 1].strip():
                j -= 1
            lines.insert(j, '%s=%s' % (op['key'], op['value']))
    return (CRLF.join(lines) + CRLF).encode('latin1')


def capture(a):
    bef, aft = drives(a.before), drives(a.after)
    profile = json.load(open(a.profile)) if a.profile else {}
    excl = FILE_NOISE + (a.exclude or [])
    man = {
        'format': 'n98', 'schema': SCHEMA, 'id': a.id or a.title.lower().replace(' ', '-'), 'title': a.title,
        'created': datetime.datetime.now(datetime.timezone.utc).isoformat(timespec='seconds'),
        'tool': 'tools/nano98/pkg/n98.py', 'base': {'os': profile.get('os', 'win98se'), 'drives': {}},
        'hardware': profile.get('hardware', {'machine': 'WIN98-P166', 'video': 'S3', 'ram_mb': 64, 'min_cpu': 'P133'}),
        'launch': profile.get('launch', {}), 'cd': profile.get('cd', []), 'presets': profile.get('presets', {}),
        'saves': profile.get('saves', []), 'snapshot': None,
        'files': [], 'deleted': [], 'ini': [], 'dlls': [], 'registry': {}, 'stats': {},
    }
    z = zipfile.ZipFile(a.out, 'w', zipfile.ZIP_DEFLATED, compresslevel=6)
    nbytes = 0
    for drv in sorted(aft):
        A = fat32.Fat32(bef[drv]) if drv in bef else None
        B = fat32.Fat32(aft[drv])
        if drv in bef:
            man['base']['drives'][drv] = {'image': os.path.basename(bef[drv]), 'sha256': sha256_file(bef[drv]),
                                          'length': os.path.getsize(bef[drv])}
        ta = tree(A) if A else {}
        tb = tree(B)
        skipped = 0
        for up, (p, e) in sorted(tb.items()):
            if noise(p, excl):
                skipped += not e.is_dir
                continue
            old = ta.get(up)
            if e.is_dir:
                if old is None:
                    man['files'].append({'path': f'{drv}:/{p}', 'dir': True, 'attr': e.attr,
                                         'time': [e.ctime, e.cdate, e.adate, e.wtime, e.wdate]})
                continue
            same = old is not None and not old[1].is_dir and (old[1].size, old[1].wtime, old[1].wdate) == (e.size, e.wtime, e.wdate)
            data = None
            if same and a.deep:
                data = B.read_chain(e.cluster, e.size) if e.cluster else b''
                same = data == (A.read_chain(old[1].cluster, old[1].size) if old[1].cluster else b'')
            if same:
                continue
            if data is None:
                data = B.read_chain(e.cluster, e.size) if e.cluster else b''
            if old is not None and not old[1].is_dir:
                prev = A.read_chain(old[1].cluster, old[1].size) if old[1].cluster else b''
                if prev == data:
                    continue          # touched, same bytes
                if p.upper().endswith('.INI') and not a.whole_ini:
                    ops = ini_diff(prev, data)
                    man['ini'].append({'path': f'{drv}:/{p}', 'ops': ops})
                    continue
            arc = f'files/{drv}/{p}'
            z.writestr(arc, data)
            nbytes += len(data)
            ent = {'path': f'{drv}:/{p}', 'size': len(data), 'sha256': hashlib.sha256(data).hexdigest(),
                   'attr': e.attr, 'time': [e.ctime, e.cdate, e.adate, e.wtime, e.wdate], 'zip': arc,
                   'change': 'modified' if old else 'added'}
            man['files'].append(ent)
            if p.upper().startswith('WINDOWS/SYSTEM/') and p.upper().rsplit('.', 1)[-1] in ('DLL', 'OCX', 'VXD', 'DRV', 'CPL', 'TLB'):
                man['dlls'].append(ent['path'])
        for up, (p, e) in sorted(ta.items()):
            if up not in tb and not noise(p, excl):
                parent = up.rsplit('/', 1)[0] if '/' in up else None
                if parent and parent in ta and parent not in tb:
                    continue
                man['deleted'].append(f'{drv}:/{p}')
        man['stats'][drv] = {'noise_files_skipped': skipped}
        # registry: only the system drive has hives
        for hp, root in HIVES.items():
            if A is None or tb.get(hp.upper()) is None or ta.get(hp.upper()) is None:
                continue
            ops = creg.diff(creg.Hive(A.read_file(hp)), creg.Hive(B.read_file(hp)))
            kept, dropped = reg_filtered(ops, a.keep_reg_noise)
            name = hp.split('/')[-1]
            man['registry'][name] = {'drive': drv, 'root': root, 'ops': kept, 'noise_ops_dropped': dropped}
            z.writestr(f'registry/{name}.reg', creg.to_reg(kept, root).encode('latin1'))
    man['stats']['payload_bytes'] = nbytes
    if a.snapshot:
        z.write(a.snapshot, 'snapshot/' + os.path.basename(a.snapshot))
        man['snapshot'] = {'zip': 'snapshot/' + os.path.basename(a.snapshot), 'sha256': sha256_file(a.snapshot),
                           'emulator': profile.get('emulator', '')}
    z.writestr('manifest.json', json.dumps(man, indent=1))
    z.close()
    print(f'{a.out}: {sum(1 for f in man["files"] if not f.get("dir"))} files ({nbytes:,} B), '
          f'{len(man["deleted"])} deleted, {len(man["dlls"])} system DLLs, registry ' +
          ', '.join(f'{k} {len(v["ops"])} ops (-{v["noise_ops_dropped"]} noise)' for k, v in man['registry'].items()))


def load(pkg):
    z = zipfile.ZipFile(pkg)
    man = json.loads(z.read('manifest.json'))
    if man.get('format') != 'n98' or man.get('schema', 0) > SCHEMA:
        raise SystemExit(f'{pkg}: not an n98 package this tool knows (schema {man.get("schema")})')
    return z, man


def info(a):
    z, man = load(a.pkg)
    files = [f for f in man['files'] if not f.get('dir')]
    print(f'{man["title"]} ({man["id"]}), schema {man["schema"]}, created {man["created"]}')
    print('hardware', json.dumps(man['hardware']), ' launch', json.dumps(man['launch']))
    print(f'files {len(files)} ({sum(f["size"] for f in files):,} B), dirs {len(man["files"]) - len(files)}, '
          f'deleted {len(man["deleted"])}, dlls {len(man["dlls"])}, cds {len(man["cd"])}')
    for k, v in man['registry'].items():
        print(f'registry {k}: {len(v["ops"])} ops')


def apply(a):
    z, man = load(a.pkg)
    base = drives(a.base)
    os.makedirs(a.out, exist_ok=True)
    layers = [(z, man)]
    if a.saves:
        layers.append(load(a.saves))
    touched = {d.split(':')[0] for _, m in layers for d in [f['path'] for f in m['files']] + m['deleted']}
    touched |= {i['path'].split(':')[0] for _, m in layers for i in m.get('ini', [])}
    touched |= {v['drive'] for _, m in layers for v in m['registry'].values()}
    for drv in sorted(touched):
        if drv not in base:
            raise SystemExit(f'the package writes drive {drv}: give --base {drv}=IMAGE')
        bh = sha256_file(base[drv])
        want = man['base']['drives'].get(drv, {}).get('sha256')
        if want and want != bh:
            print(f'{drv}: note: base differs from the capture base ({want[:19]}...), applying anyway')
        cow = fat32.CowFile(base[drv])
        fs = fat32.Fat32(cow, write=True)
        for zz, m in layers:
            for p in m['deleted']:
                d, _, rel = p.partition(':/')
                if d == drv:
                    fs.delete(rel)
            for f in m['files']:
                d, _, rel = f['path'].partition(':/')
                if d != drv:
                    continue
                t = tuple(f.get('time', (0, 0, 0, 0, 0)))
                if f.get('dir'):
                    fs.mkdir(rel, t)
                    continue
                data = zz.read(f['zip'])
                if hashlib.sha256(data).hexdigest() != f['sha256']:
                    raise SystemExit(f'{f["path"]}: package content hash mismatch')
                fs.write_file(rel, data, f.get('attr', 0x20) & 0x27, t)
            for ini in m.get('ini', []):
                d, _, rel = ini['path'].partition(':/')
                if d != drv:
                    continue
                e, _ = fs.lookup(rel)
                old = fs.read_file(rel) if e is not None else b''
                t = (e.ctime, e.cdate, e.adate, e.wtime, e.wdate) if e is not None else (0, 0, 0, 0, 0)
                fs.write_file(rel, ini_apply(old, ini['ops']), e.attr if e is not None else 0x20, t)
            for name, r in m['registry'].items():
                if r['drive'] != drv or not r['ops']:
                    continue
                hp = 'WINDOWS/' + name
                new = creg.Hive(fs.read_file(hp)).apply(r['ops'])
                e, _ = fs.lookup(hp)
                fs.write_file(hp, new, e.attr, (e.ctime, e.cdate, e.adate, e.wtime, e.wdate))
                # the backup Windows restores from when it dislikes the hive: keep it in step
                bak = hp[:-1] + '0'
                if fs.lookup(bak)[0] is not None:
                    be = fs.lookup(bak)[0]
                    fs.write_file(bak, new, be.attr, (be.ctime, be.cdate, be.adate, be.wtime, be.wdate))
        fs.close()
        n = cow.save_layer(os.path.join(a.out, drv + '.n98l'), bh)
        print(f'{drv}: layer {n} sectors ({n * 512 / 1048576:.1f} MB) over {os.path.basename(base[drv])}')
        if a.full:
            cow.save_full(os.path.join(a.out, drv + '.img'))
            print(f'{drv}: full image {os.path.join(a.out, drv + ".img")}')


def launch_cmd(a):
    """The seat's COMMAND PORT text for this package's launch entry (format owned by tools/nano98/seat.py)."""
    _, man = load(a.pkg)
    L = man.get('launch') or {}
    if not L.get('exe'):
        raise SystemExit('package has no launch.exe')
    sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    import seat
    win = lambda p: p.replace('/', chr(92))
    kv = ['run=' + win(L['exe'])]
    if L.get('args'): kv.append('args=' + L['args'])
    if L.get('cwd'): kv.append('dir=' + win(L['cwd']))
    kv.append('exit=' + a.exit)
    seat.cmd(a.out, kv)


def main():
    ap = argparse.ArgumentParser()
    sp = ap.add_subparsers(dest='cmd', required=True)
    c = sp.add_parser('capture'); c.add_argument('out'); c.add_argument('--title', required=True); c.add_argument('--id')
    c.add_argument('--before', nargs='+', default=[]); c.add_argument('--after', nargs='+', required=True)
    c.add_argument('--profile'); c.add_argument('--exclude', nargs='*'); c.add_argument('--keep-reg-noise', action='store_true')
    c.add_argument('--deep', action='store_true'); c.add_argument('--whole-ini', action='store_true'); c.add_argument('--snapshot')
    i = sp.add_parser('info'); i.add_argument('pkg')
    p = sp.add_parser('apply'); p.add_argument('pkg'); p.add_argument('--base', nargs='+', required=True)
    p.add_argument('--out', required=True); p.add_argument('--full', action='store_true'); p.add_argument('--saves')
    k = sp.add_parser('cmd'); k.add_argument('pkg'); k.add_argument('out'); k.add_argument('--exit', default='poweroff')
    a = ap.parse_args()
    {'capture': capture, 'info': info, 'apply': apply, 'cmd': launch_cmd}[a.cmd](a)


if __name__ == '__main__':
    main()
