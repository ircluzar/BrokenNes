"""nano98 install capture (n98.py install / exes): the BrokenNes2 install wizard's last step (Windows/NesEmulator/x86/N98Install.cs).

The seat booted the install shell (C:\\N98\\N98INST.EXE) with the blank standard D: and the user's disc; the user ran the game's setup into
D:\\GAME. The app then wrote the two drives as they were left (C.img = seat base + Windows' and setup's writes, D.img = the game drive) and calls

    n98.py install OUT.n98 --title T --base SEAT_BASE.n98d --c C.img --d D.img --exe D:/GAME/GAME.EXE [--layer-id ID] [--cd REF...] ...

-> a schema-2 package (docs/projects/nano98-format.md):
- D: the game drive's tree, copied onto a fresh standard drive (fat32.mkfs), so the sectors Windows reads at mount stay the standard ones
  (the warm-boot swap). Anything setup put in D:'s root goes under GAME.
- C: diffed against the seat base: changed files -> c_files (the launcher puts them back at run time), Software registry + INI -> run-time ops.
- hardware: driver files (WINDOWS/SYSTEM/*.VXD/.SYS/.DRV..., WINDOWS/INF/*.INF) and Enum/Config registry ops are not the game's. With
  --layer-id they become a system dependency layer (schema 1, kind layer, beside the package or --layer-out) that the package names, so the
  variant base (and its own warm state) is rebuilt from it by any emulator build without the wizard. Without it they are counted, not kept.
The last stdout line is 'N98INSTALL {json}' (for the app, the CLI and agents).
"""
import datetime, json, os, tempfile, types, zipfile

import fat32, sparse

BS = chr(92)
DRIVER_EXT = ('VXD', 'SYS', 'DRV', 'INF', '386', 'PDR', 'MPD')
STD_DRIVE_MB = 2000
SETUPISH = ('UNINST', 'UNWISE', 'SETUP', 'INSTALL', 'ISUNINST', '_ISDEL', 'DXSETUP', 'DIRECTX', 'REGISTER', 'README')


def _software(key):
    k = key.upper()
    return k.startswith('SOFTWARE') or k.startswith('.DEFAULT' + BS + 'SOFTWARE')


def install(a, n98):
    work = a.work or tempfile.gettempdir()
    pid = a.id or a.title.lower().replace(' ', '-')
    s1 = os.path.join(work, pid + '.s1.n98')
    n98.capture(types.SimpleNamespace(out=s1, title=a.title, id=pid, before=['C=' + a.base], after=['C=' + a.c], profile=None,
                                      exclude=['N98/*', 'WINDOWS/SYSTEM/CATROOT/*'] + (a.exclude or []), keep_reg_noise=True, deep=False, whole_ini=False, snapshot=None))
    z, man = n98.load(s1)
    # D: the game drive
    src = fat32.Fat32(fat32.open_ro(a.d))
    disk = fat32.MemDisk(STD_DRIVE_MB << 20)
    fat32.mkfs(disk, STD_DRIVE_MB)
    D = fat32.Fat32(disk, write=True)
    nfiles = nbytes = 0
    moved = []
    for p_, e in list(src.walk()):
        up = p_.upper()
        if up in ('N98RSVD.SYS', 'GAME') or n98.noise(p_, n98.FILE_NOISE):
            continue
        dst = p_ if up.startswith('GAME/') else 'GAME/' + p_
        if not up.startswith('GAME/') and '/' not in p_:
            moved.append(p_)
        t = (e.ctime, e.cdate, e.adate, e.wtime, e.wdate)
        if e.is_dir:
            D.mkdir(dst, t)
            continue
        data = src.read_chain(e.cluster, e.size) if e.cluster else b''
        D.write_file(dst, data, e.attr & 0x27, t)
        nfiles += 1
        nbytes += len(data)
    D.fat_dirty = True
    D.close()
    if nfiles == 0:
        raise SystemExit('N98INSTALL ' + json.dumps({'ok': False, 'error': 'the game drive D: is empty: install the game to D:' + BS + 'GAME'}))
    exe = a.exe.replace(BS, '/')
    rel = exe[3:] if exe.upper().startswith('D:/') else None
    if rel is not None and not rel.upper().startswith('GAME/'):
        rel = 'GAME/' + rel; exe = 'D:/' + rel
    if rel is None or fat32.Fat32(disk).lookup(rel)[0] is None:
        raise SystemExit('N98INSTALL ' + json.dumps({'ok': False, 'error': f'the game program {a.exe} is not on the game drive (D:/GAME/...)'}))
    # C: the files setup changed; driver files split off
    c_files, drv_files = [], []
    for f in man['files']:
        if f.get('dir'):
            continue
        r = f['path'].partition(':/')[2].upper()
        is_drv = r.rsplit('.', 1)[-1] in DRIVER_EXT and (r.startswith('WINDOWS/SYSTEM/') or r.startswith('WINDOWS/INF/'))
        (drv_files if is_drv else c_files).append(f)
    reg, hw = {}, {}
    for name, r in man['registry'].items():
        # the install seat's own setting (Setup SourcePath = E:\WIN98, N98SeatInstall.cs) is not the game's
        ops = [o for o in r['ops'] if not (o['key'].upper().endswith(BS + 'CURRENTVERSION' + BS + 'SETUP') and o.get('name', '').upper() in ('SOURCEPATH', 'SERVICEPACKSOURCEPATH'))]
        r = dict(r, ops=ops)
        sw = n98.reg_filtered([o for o in r['ops'] if _software(o['key'])], False)[0]
        hw[name] = [o for o in r['ops'] if not _software(o['key'])]
        reg[name] = {'drive': 'C', 'root': r['root'], 'ops': sw}
    hw_count = sum(len(v) for v in hw.values())
    layers = list(a.layers or [])
    layer_path = None
    # Windows parts the setup installed (codecs: SYSTEM.INI [drivers32] names their DLLs) go with the hardware/system changes into the layer
    sys_ini = [i for i in man.get('ini', []) if i['path'].upper() == 'C:/WINDOWS/SYSTEM.INI']
    named = {str(op.get('value') or '').upper() for i in sys_ini for op in i['ops']}
    if a.layer_id:
        for f in list(c_files):
            if f['path'].upper().startswith('C:/WINDOWS/SYSTEM/') and f['path'].rsplit('/', 1)[-1].upper() in named:
                c_files.remove(f); drv_files.append(f)
    if a.layer_id and (drv_files or hw_count):
        layer_path = a.layer_out or os.path.join(os.path.dirname(os.path.abspath(a.out)), a.layer_id + '.n98')
        lz = zipfile.ZipFile(layer_path + '.tmp', 'w', zipfile.ZIP_DEFLATED, compresslevel=6)
        for f in drv_files:
            lz.writestr(f['zip'], z.read(f['zip']))
        L = {'format': 'n98', 'schema': 1, 'kind': 'layer', 'id': a.layer_id, 'title': a.layer_id, 'tool': 'tools/nano98/pkg/n98.py install',
             'created': datetime.datetime.now(datetime.timezone.utc).isoformat(timespec='seconds'), 'base': man['base'],
             'hardware': {'machine': a.machine or ''}, 'launch': {}, 'cd': [], 'presets': {}, 'saves': [], 'snapshot': None,
             'files': drv_files, 'deleted': [], 'dlls': [], 'ini': [i for i in man.get('ini', []) if i['path'].upper() == 'C:/WINDOWS/SYSTEM.INI'],
             'registry': {k: {'drive': 'C', 'root': man['registry'][k]['root'], 'ops': v} for k, v in hw.items()}, 'stats': {}}
        lz.writestr('manifest.json', json.dumps(L, indent=1))
        lz.close()
        os.replace(layer_path + '.tmp', layer_path)
        layers.append(a.layer_id)
        ini = [i for i in man.get('ini', []) if i['path'].upper() != 'C:/WINDOWS/SYSTEM.INI']
    else:
        c_files += drv_files
        ini = list(man.get('ini', []))
    out = zipfile.ZipFile(a.out + '.tmp', 'w', zipfile.ZIP_DEFLATED, compresslevel=6)
    for f in c_files:
        out.writestr(f['zip'], z.read(f['zip']))
    dn = os.path.join(work, pid + '.D.n98d')
    dh = sparse.write_mem(disk, dn)
    out.write(dn, 'drives/D.n98d', compress_type=zipfile.ZIP_STORED)
    W = lambda p: p.replace(BS, '/')
    cwd = W(a.cwd) if a.cwd else exe.rsplit('/', 1)[0]
    v2 = {'format': 'n98', 'schema': 2, 'id': pid, 'title': a.title, 'tool': 'tools/nano98/pkg/n98.py install',
          'created': datetime.datetime.now(datetime.timezone.utc).isoformat(timespec='seconds'),
          'hardware': {'machine': a.machine or 'WIN98-P166-NANO', 'video': 'S3', 'ram_mb': 64, 'min_cpu': 'P133', 'sound': a.sound or 'SB16_D072'},
          'system': {'base': 'nano98-se', 'layers': layers},
          'drives': {'D': {'zip': 'drives/D.n98d', 'p256': dh, 'length': STD_DRIVE_MB << 20}},
          'launch': {'exe': exe, 'args': a.args or '', 'cwd': cwd},
          'cd': [{'label': os.path.splitext(c)[0].upper()[:11], 'ref': c, 'required': True, 'note': "the user's own disc; never shipped"} for c in (a.cd or [])],
          'presets': {'default': {}}, 'saves': [W(x) for x in (a.saves or [])],
          'c_files': [{'path': f['path'], 'zip': f['zip'], 'size': f['size'], 'sha256': f['sha256']} for f in c_files],
          'ini': ini, 'registry': reg,
          'stats': {'d_files': nfiles, 'd_bytes': nbytes, 'c_files': len(c_files), 'driver_files': len(drv_files),
                    'hardware_registry_ops': hw_count, 'hardware_kept': bool(layer_path), 'root_moved_to_game': moved}}
    out.writestr('manifest.json', json.dumps(v2, indent=1))
    out.close()
    os.replace(a.out + '.tmp', a.out)
    z.close()
    os.remove(s1)
    os.remove(dn)
    res = {'ok': True, 'package': os.path.abspath(a.out), 'title': a.title, 'exe': exe, 'd_files': nfiles, 'd_bytes': nbytes, 'c_files': len(c_files),
           'registry_ops': sum(len(r['ops']) for r in reg.values()), 'driver_files': len(drv_files), 'hardware_registry_ops': hw_count,
           'layer': layer_path, 'layers': layers, 'bytes': os.path.getsize(a.out)}
    print('N98INSTALL ' + json.dumps(res))


def exes(a):
    """Every .EXE on the game drive (the wizard's 'pick the game program' step), the likely game programs first, biggest first."""
    fs = fat32.Fat32(fat32.open_ro(a.d))
    out = []
    for p_, e in fs.walk():
        if e.is_dir or not p_.upper().endswith('.EXE'):
            continue
        name = p_.upper().rsplit('/', 1)[-1]
        out.append({'path': 'D:/' + p_, 'size': e.size, 'likely': not any(k in name for k in SETUPISH)})
    out.sort(key=lambda x: (not x['likely'], -x['size']))
    print('N98EXES ' + json.dumps(out))


def add_parsers(sp):
    n = sp.add_parser('install', help='the install wizard capture: C:/D: after setup -> schema-2 package (+ driver layer)')
    n.add_argument('out'); n.add_argument('--title', required=True); n.add_argument('--id')
    n.add_argument('--base', required=True, help='the seat base the install ran on (C: before)')
    n.add_argument('--c', required=True, help='C: after the install (whole image)')
    n.add_argument('--d', required=True, help='D: after the install (whole image)')
    n.add_argument('--exe', required=True); n.add_argument('--args'); n.add_argument('--cwd')
    n.add_argument('--cd', nargs='*', help="disc image file names the game needs (the user's own, by reference)")
    n.add_argument('--saves', nargs='*'); n.add_argument('--machine'); n.add_argument('--sound')
    n.add_argument('--layers', nargs='*', help='system layers the install ran on'); n.add_argument('--layer-id', help='promote hardware changes to this layer')
    n.add_argument('--layer-out'); n.add_argument('--exclude', nargs='*'); n.add_argument('--work')
    x = sp.add_parser('exes', help='list the programs on a game drive (JSON)'); x.add_argument('d')
