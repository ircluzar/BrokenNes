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
    import sparse
    if sparse.is_n98d(path):
        return sparse.header(path)[1]   # an N98D names its disk by the p256 in its header
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
        A = fat32.Fat32(fat32.open_ro(bef[drv])) if drv in bef else None
        B = fat32.Fat32(fat32.open_ro(aft[drv]))
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
    # the launch exe must be a captured file (or already on the capture base): fix its case to the disk's, refuse a miss
    L = man['launch']
    if L.get('exe'):
        have = {f['path'].upper(): f['path'] for f in man['files']}
        e = L['exe'].replace(chr(92), '/')
        if e.upper() in have:
            L['exe'] = have[e.upper()]
        else:
            d0, _, rel = e.partition(':/')
            if d0 not in bef or fat32.Fat32(bef[d0]).lookup(rel)[0] is None:
                raise SystemExit(f'launch exe {e} is neither in the package nor on the base; fix the profile')
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
    if man.get('format') != 'n98' or man.get('schema', 0) > 2:
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
    args = L.get('args', '')
    if getattr(a, 'preset', None):   # a preset's extra launch args (its video_arg is for the emulator, not the guest)
        pr = (man.get('presets') or {}).get(a.preset)
        if pr is None:
            raise SystemExit(f'package has no preset {a.preset} (has: {", ".join(man.get("presets") or {})})')
        args = (args + ' ' + pr.get('args', '')).strip()
    if args: kv.append('args=' + args)
    if L.get('cwd'): kv.append('dir=' + win(L['cwd']))
    kv.append('exit=' + a.exit)
    seat.cmd(a.out, kv)


STD_DRIVE_MB = 2000   # the standard game drive (N98Seat.StandardDriveMb)


def to_v2(a):
    """A schema-1 package -> schema 2 (the standard layout): the game's files go into D:\GAME on its own D: (2000 MB, sparse; fat32.mkfs layout), files it
    had installed on C: outside WINDOWS or on D: are relocated to the same path under D:\GAME and every registry / INI / launch path naming them is rewritten;
    its files under C:/WINDOWS become c_files (put back by the launcher), registry + INI ops are applied by the launcher at run time."""
    import re, sparse
    z, man = load(a.pkg)
    drop = [g.upper() for g in (a.drop or [])]
    dropped = lambda p: any(fnmatch.fnmatchcase(p.upper(), g) for g in drop)
    disk = fat32.MemDisk(STD_DRIVE_MB << 20)
    fat32.mkfs(disk, STD_DRIVE_MB)
    D = fat32.Fat32(disk, write=True)
    out = zipfile.ZipFile(a.out + '.tmp', 'w', zipfile.ZIP_DEFLATED, compresslevel=6)
    moved_dirs, c_files, nfiles, nbytes, skipped = [], [], 0, 0, []
    for f in man['files']:
        drv, _, rel = f['path'].partition(':/')
        if dropped(f['path']):
            skipped.append(f['path']); continue
        sys_file = drv == 'C' and (rel.upper().startswith('WINDOWS/') or ('/' not in rel and not f.get('dir')))
        if sys_file:
            if f.get('dir'):
                continue
            c_files.append({'path': f['path'], 'zip': f['zip'], 'size': f['size'], 'sha256': f['sha256']})
            out.writestr(f['zip'], z.read(f['zip'])); continue
        t = tuple(f.get('time', (0, 0, 0, 0, 0)))
        if f.get('dir'):
            D.mkdir('GAME/' + rel, t)
            moved_dirs.append((drv, rel))
            continue
        data = z.read(f['zip'])
        if hashlib.sha256(data).hexdigest() != f['sha256']:
            raise SystemExit(f'{f["path"]}: package content hash mismatch')
        D.write_file('GAME/' + rel, data, f.get('attr', 0x20) & 0x27, t)
        nfiles += 1; nbytes += len(data)

    def short_of(rel):
        cl, outp = D.lookup('GAME')[1], []
        for part in rel.split('/'):
            e = next((x for x in D.list_dir(cl) if x.name.upper() == part.upper()), None)
            if e is None: return None
            outp.append(D._sn(e)); cl = e.cluster
        return '/'.join(outp)
    BS = chr(92)
    forms = {'C': set(), 'D': set()}   # every folder the game added, long and 8.3 forms, per old drive
    for drv, rel in moved_dirs:
        forms[drv].add(rel.upper().replace('/', BS))
        sh = short_of(rel)
        if sh: forms[drv].add(sh.upper().replace('/', BS))
    rxs = [re.compile(d + ':' + re.escape(BS) + '(' + '|'.join(re.escape(x) for x in sorted(fs, key=len, reverse=True)) + ')(?![A-Za-z0-9_~ ])', re.I)
           for d, fs in forms.items() if fs]
    nrew = [0]

    def rew(t):
        if t is None: return t
        n2 = t
        for rx in rxs:
            n2 = rx.sub(lambda m: 'D:' + BS + 'GAME' + BS + m.group(1), n2)
        if n2 != t: nrew[0] += 1
        return n2
    for p_, e in list(D.walk()):   # text configs inside the relocated folders
        if not e.is_dir and p_.upper().endswith(('.INI', '.CFG')) and e.size < 65536:
            old = D.read_file(p_); new = rew(old.decode('latin1')).encode('latin1')
            if new != old: D.write_file(p_, new, e.attr, (e.ctime, e.cdate, e.adate, e.wtime, e.wdate))
    D.fat_dirty = True; D.close()
    reg, reg_dropped = {}, 0   # run-time registry ops: Software keys only (Enum/Config/hardware belong in a system layer)
    for name, r in man['registry'].items():
        ops = []
        for o in r['ops']:
            ku = o['key'].upper()
            if not (ku.startswith('SOFTWARE') or ku.startswith('.DEFAULT' + BS + 'SOFTWARE')):
                reg_dropped += 1; continue
            o = dict(o)
            if o['op'] == 'set' and o['type'] in (1, 2):
                raw = bytes.fromhex(o['data']); body = raw.rstrip(bytes(1)); txt = body.decode('latin1')
                nt = rew(txt)
                if nt != txt: o['data'] = (nt.encode('latin1') + raw[len(body):]).hex()
            ops.append(o)
        reg[name] = {'drive': 'C', 'root': r['root'], 'ops': ops}
    ini = [{'path': i['path'], 'ops': [dict(op, value=rew(op['value'])) for op in i['ops']]} for i in man.get('ini', []) if not dropped(i['path'])]

    def mv(p):
        if not p: return p
        w = p.replace('/', BS); w2 = rew(w)
        return w2.replace(BS, '/') if w2 != w else p
    L = dict(man['launch']); L['exe'] = mv(L.get('exe')); L['cwd'] = mv(L.get('cwd'))
    if a.args is not None: L['args'] = a.args
    dn = os.path.join(a.work, man['id'] + '.D.n98d')
    dh = sparse.write_mem(disk, dn)
    out.write(dn, 'drives/D.n98d', compress_type=zipfile.ZIP_STORED)
    v2 = {k: man[k] for k in ('format', 'id', 'title', 'hardware', 'cd') if k in man}
    v2.update({'schema': 2, 'created': datetime.datetime.now(datetime.timezone.utc).isoformat(timespec='seconds'),
               'tool': 'tools/nano98/pkg/n98.py v2', 'from_schema1': os.path.basename(a.pkg),
               'system': {'base': 'nano98-se', 'layers': a.layers or []},
               'drives': {'D': {'zip': 'drives/D.n98d', 'p256': dh, 'length': STD_DRIVE_MB << 20}},
               'launch': L, 'saves': [mv(x) for x in man.get('saves', [])], 'c_files': c_files, 'ini': ini, 'registry': reg,
               'presets': man.get('presets', {}),
               'stats': {'d_files': nfiles, 'd_bytes': nbytes, 'c_files': len(c_files), 'rewritten_paths': nrew[0],
                         'registry_ops_dropped': reg_dropped, 'files_dropped': len(skipped), 'deleted_ignored': len(man.get('deleted', []))}})
    if a.embed_base:
        if not sparse.is_n98d(a.embed_base): raise SystemExit('--embed-base takes an N98D base (slim.py --n98d)')
        out.write(a.embed_base, 'base/C.n98d', compress_type=zipfile.ZIP_STORED)
        v2['system']['embedded_base'] = sparse.header(a.embed_base)[1]
    out.writestr('manifest.json', json.dumps(v2, indent=1))
    out.close(); os.replace(a.out + '.tmp', a.out); os.remove(dn)
    print(f'{a.out}: schema 2, D: {nfiles} files {nbytes / 1e6:.1f} MB, {len(c_files)} C: files, {nrew[0]} paths C:->D:, '
          f'registry {sum(len(r["ops"]) for r in reg.values())} ops (-{reg_dropped} non-Software), {len(skipped)} files dropped; '
          f'launch {L.get("exe")}; {os.path.getsize(a.out) / 1e6:.1f} MB')


def make_layer(a):
    """A system DEPENDENCY LAYER (schema 1, C: only) from a package: its files under the given globs (default C:/WINDOWS/*) and ALL its
    registry ops (drivers need Enum/Config keys), INI edits on C:. Layers go between the base and the game: base + layers = a system variant."""
    z, man = load(a.pkg)
    keep = [g.upper() for g in (a.keep or ['C:/WINDOWS/*'])]
    want = lambda p: any(fnmatch.fnmatchcase(p.upper(), g) for g in keep)
    out = zipfile.ZipFile(a.out + '.tmp', 'w', zipfile.ZIP_DEFLATED, compresslevel=6)
    files = []
    for f in man['files']:
        if not want(f['path']): continue
        if not f.get('dir'): out.writestr(f['zip'], z.read(f['zip']))
        files.append(f)
    L = dict(man); L.update({'id': a.id, 'title': a.title or a.id, 'kind': 'layer', 'files': files, 'deleted': [], 'launch': {}, 'saves': [], 'cd': [],
                             'ini': [i for i in man.get('ini', []) if i['path'].upper().startswith('C:')], 'snapshot': None,
                             'created': datetime.datetime.now(datetime.timezone.utc).isoformat(timespec='seconds')})
    rg = [g.upper() for g in (a.reg or ['*'])]
    L['registry'] = {k: dict(v, ops=[o for o in v['ops'] if any(fnmatch.fnmatchcase(o['key'].upper(), g) for g in rg)])
                     for k, v in man['registry'].items() if v.get('drive', 'C') == 'C'}
    out.writestr('manifest.json', json.dumps(L, indent=1)); out.close(); os.replace(a.out + '.tmp', a.out)
    print(f'{a.out}: layer {a.id}: {sum(1 for f in files if not f.get("dir"))} files, registry ' +
          ', '.join(f'{k} {len(v["ops"])} ops' for k, v in L['registry'].items()))


def variant(a):
    """A SYSTEM VARIANT = the seat base + dependency layers, applied in order (files, INI, registry: all of it, at build time, so Windows
    boots with them) -> a new N98D base with its own identity hash, so it gets its own warm-boot state."""
    import sparse, tempfile
    cow = fat32.CowFile(a.base)
    fs = fat32.Fat32(cow, write=True)
    for lp in a.layers:
        z, m = load(lp)
        for p_ in m['deleted']:
            d, _, rel = p_.partition(':/')
            if d == 'C': fs.delete(rel)
        for f in m['files']:
            d, _, rel = f['path'].partition(':/')
            if d != 'C': continue
            t = tuple(f.get('time', (0, 0, 0, 0, 0)))
            if f.get('dir'): fs.mkdir(rel, t); continue
            data = z.read(f['zip'])
            if hashlib.sha256(data).hexdigest() != f['sha256']: raise SystemExit(f'{lp}: {f["path"]}: content hash mismatch')
            fs.write_file(rel, data, f.get('attr', 0x20) & 0x27, t)
        for ini in m.get('ini', []):
            d, _, rel = ini['path'].partition(':/')
            if d != 'C': continue
            e, _ = fs.lookup(rel)
            old = fs.read_file(rel) if e is not None else b''
            fs.write_file(rel, ini_apply(old, ini['ops']), e.attr if e is not None else 0x20, (e.ctime, e.cdate, e.adate, e.wtime, e.wdate) if e is not None else (0, 0, 0, 0, 0))
        for name, r in m['registry'].items():
            if r.get('drive', 'C') != 'C' or not r['ops']: continue
            hp = 'WINDOWS/' + name
            new = creg.Hive(fs.read_file(hp)).apply(r['ops'])
            for h in (hp, hp[:-1] + '0'):
                e, _ = fs.lookup(h)
                if e is not None: fs.write_file(h, new, e.attr, (e.ctime, e.cdate, e.adate, e.wtime, e.wdate))
        print(f'{lp}: applied')
    fs.close()
    tmp = os.path.join(a.work or tempfile.gettempdir(), os.path.basename(a.out) + '.img')
    cow.save_full(tmp)
    sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    import slim   # zero-fill the free space (the variant is packed like a base) - no culling
    f2 = fat32.Fat32(tmp, write=True); slim.zero_free(f2); f2.f.close()
    sparse.write(tmp, a.out + '.tmp'); os.replace(a.out + '.tmp', a.out); os.remove(tmp)


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
    k = sp.add_parser('cmd'); k.add_argument('pkg'); k.add_argument('out'); k.add_argument('--exit', default='poweroff'); k.add_argument('--preset')
    v = sp.add_parser('v2'); v.add_argument('pkg'); v.add_argument('out'); v.add_argument('--work', default='.')
    v.add_argument('--drop', nargs='*'); v.add_argument('--layers', nargs='*'); v.add_argument('--embed-base'); v.add_argument('--args', help='replace the launch arguments')
    y = sp.add_parser('layer'); y.add_argument('pkg'); y.add_argument('out'); y.add_argument('--id', required=True); y.add_argument('--title')
    y.add_argument('--keep', nargs='*'); y.add_argument('--reg', nargs='*', help='registry key globs to keep (default all)')
    w = sp.add_parser('variant'); w.add_argument('out'); w.add_argument('--base', required=True); w.add_argument('--layers', nargs='+', required=True)
    w.add_argument('--work')
    import n98install
    n98install.add_parsers(sp)
    a = ap.parse_args()
    if a.cmd in ('install', 'exes'):
        return getattr(n98install, a.cmd)(a, *( [sys.modules[__name__]] if a.cmd == 'install' else []))
    {'capture': capture, 'info': info, 'apply': apply, 'cmd': launch_cmd, 'v2': to_v2, 'layer': make_layer, 'variant': variant}[a.cmd](a)


if __name__ == '__main__':
    main()
