# build_seat.py: the nano98 seat base in ONE command, unattended (this PC, a fleet PC or Garage2):
#   python -I tools/nano98/build_seat.py --workshop PATH\BrokenNes.Workshop.exe --work DIR --out DIR\seat_base.n98d
#       [--iso X:/SYSTEMS/Windows/Windows98_EN.iso --accept-eula [--keyfile F] [--pyfat PYTHON]]   (from the user's Win98 SE disc)
#       [--installed hd.img]                                                                      (or from an installed Win98 disk)
# Stages (each skipped when its output is already in WORK, so a run can be resumed):
#   1 setup disk   build_base.py (needs the pyfatfs venv python: --pyfat)            -> hd_setup.img, stage2.steps
#   2 install      Workshop --x86boot: Win98 SE Setup from the CD, unattended        -> installed.img
#   3 seat         seat.py install (launcher shell, boot screen, black desktop)       -> seat.img
#   4 slim         slim.py (the cull, start-up values, free space zeroed)             -> slim.img
#   5 settle       one boot with the standard blank D: attached (Windows meets the game-drive slot once, here, not on every cold boot)
#                  and a clean power-off through the launcher                         -> settled.img
#   6 pack         slim.py again (zero-fill + N98D)                                   -> OUT (seat_base.n98d)
#   7 warm         Workshop --n98seat on an empty schema-2 package --until-warm       -> OUT.warm\<base>-<build>\warm.bin
# The base is made from the user's own Windows 98 disc: it and every package that embeds it are personal copies, never published.
import argparse, json, os, shutil, subprocess, sys, time, zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, 'pkg')); sys.path.insert(0, HERE)
import fat32, sparse  # noqa: E402

ap = argparse.ArgumentParser()
ap.add_argument('--workshop', required=True)
ap.add_argument('--work', required=True)
ap.add_argument('--out', required=True)
ap.add_argument('--iso'); ap.add_argument('--accept-eula', action='store_true'); ap.add_argument('--keyfile'); ap.add_argument('--key-env'); ap.add_argument('--pyfat', default=sys.executable)
ap.add_argument('--installed', help='an installed Win98 SE disk (skips stages 1-2)')
ap.add_argument('--machine', default='win98-p166-nano')
ap.add_argument('--sound', default='SB16_D072')   # the seat's sound card: Setup installs its driver while C:/WIN98 is still there (a later New Hardware Found asks for the CD)
ap.add_argument('--install-frames', type=int, default=1)   # frames run BEFORE the steps (x86boot runs --frames first): 1, or the CD menu times out and boots the empty C:
ap.add_argument('--settle-frames', type=int, default=60000)
a = ap.parse_args()
os.makedirs(a.work, exist_ok=True)
W = lambda n: os.path.join(a.work, n)
t0 = time.time()


def run(cmd, env=None, ok=(0,)):
    print('>', ' '.join(cmd), flush=True)
    e = dict(os.environ); e.update(env or {})
    r = subprocess.run(cmd, env=e)
    if r.returncode not in ok:
        sys.exit(f'stage failed ({r.returncode}): {cmd[0]}')


def stage(n, name, out):
    done = os.path.exists(out)
    print(f'== stage {n} {name}: {"done (" + out + ")" if done else "running"}  [{time.time() - t0:.0f} s]', flush=True)
    return not done


def scrub_key(img):
    r"""Windows keeps the product key in plain text (SYSTEM.DAT ProductKey, C:\WINDOWS\MSBATCH.INF, freed clusters of the setup disk):
    overwrite every copy on the raw disk with a same-length mask, so the base (which may be embedded in packages) never holds it."""
    key = os.environ.get(a.key_env, '') if a.key_env else (open(a.keyfile).read() if a.keyfile else '')
    key = key.strip().upper().encode('latin1')
    if len(key) < 20: print('scrub: no key given, nothing to scrub', flush=True); return
    mask = bytes(c if c == 0x2d else 0x58 for c in key)   # XXXXX-XXXXX-...
    n = 0; CH = 64 << 20
    with open(img, 'r+b') as fh:
        pos = 0
        while True:
            fh.seek(pos); buf = fh.read(CH + len(key))
            if not buf: break
            i = buf.find(key)
            while i >= 0:
                fh.seek(pos + i); fh.write(mask); n += 1
                i = buf.find(key, i + 1)
            if len(buf) <= CH: break
            pos += CH
    print(f'scrub: product key masked in {n} place(s)', flush=True)


installed = a.installed or W('installed.img')
if not a.installed:
    if not os.path.exists(installed) and stage(1, 'setup disk', W('hd_setup.img')):
        if not a.iso: sys.exit('--iso (the user\'s Win98 SE disc) or --installed is needed')
        cmd = [a.pyfat, '-I', os.path.join(HERE, 'build_base.py'), '--iso', a.iso, '--work', a.work]
        if a.accept_eula: cmd.append('--accept-eula')
        if a.keyfile: cmd += ['--keyfile', a.keyfile]
        if a.key_env: cmd += ['--key-env', a.key_env]
        run(cmd)
    if stage(2, 'install', installed):
        cmd2 = [a.workshop, '--x86boot', '--machine', a.machine, '--apu', a.sound, '--hdd', W('hd_setup.img'), '--cd', a.iso, '--bootorder', 'CH',
                '--frames', str(a.install_frames), '--stepsfile', W('stage2.steps'), '--shot', W('stage2_end.png'), '--save', installed + '.tmp']
        print('>', ' '.join(cmd2), flush=True)
        p2 = subprocess.Popen(cmd2, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, errors='replace')
        steps_ok = None
        for line in p2.stdout:   # every Setup screen check is printed; the last one is the finished desktop
            print(line, end='', flush=True)
            if line.startswith('x86boot: step '): steps_ok = ' found after ' in line and 'NOT found' not in line
        p2.wait()
        sys.path.insert(0, os.path.join(HERE, 'pkg')); import fat32 as _f
        try: _f.Fat32(installed + '.tmp').read_file('WINDOWS/SYSTEM.INI'); ok2 = bool(steps_ok)
        except FileNotFoundError: ok2 = False
        if not ok2:   # Setup did not finish: keep nothing that holds the key, say where the last screen is
            for f in ('hd_setup.img', 'stage2.steps', installed + '.tmp') + tuple(n for n in os.listdir(a.work) if n.startswith('stage2_end_step')):
                try: os.remove(W(f) if not os.path.isabs(f) else f)
                except OSError: pass
            sys.exit('stage 2: Windows Setup did not reach the desktop (a screen check failed or no C:/WINDOWS/SYSTEM.INI); last screen: ' + W('stage2_end.png'))
        scrub_key(installed + '.tmp')
        os.replace(installed + '.tmp', installed)
        for f in ('hd_setup.img', 'stage2.steps') + tuple(n for n in os.listdir(a.work) if n.startswith('stage2_end')):   # (step shots show the key page)   # (the setup disk's answer file holds the product key: gone once Windows is installed)
            try: os.remove(W(f))
            except OSError: pass

if stage(3, 'seat', W('seat.img')):
    if sparse.is_n98d(installed): sparse.unpack(installed, W('seat.img.tmp'))
    else: shutil.copyfile(installed, W('seat.img.tmp'))
    import seat
    seat.install(W('seat.img.tmp'))
    os.replace(W('seat.img.tmp'), W('seat.img'))

if stage(4, 'slim', W('slim.img')):
    run([sys.executable, '-I', os.path.join(HERE, 'slim.py'), W('seat.img'), W('slim.img.tmp'), '--list', W('removed.txt')])
    os.replace(W('slim.img.tmp'), W('slim.img'))

if stage(5, 'settle', W('settled.img')):
    blank = W('blank_d.n98d')
    m = fat32.MemDisk(2000 << 20); fat32.mkfs(m, 2000); sparse.write_mem(m, blank)
    cmdf = W('settle.cmd')
    open(cmdf, 'wb').write(b'N98CMD1\r\nrun=C:\\WINDOWS\\RUNDLL32.EXE\r\nexit=poweroff\r\n')
    run([a.workshop, '--x86boot', '--machine', a.machine, '--apu', a.sound, '--hdd', W('slim.img'), '--hdd2', blank, '--n98cmd', cmdf,
         '--frames', str(a.settle_frames), '--save', W('settled.img.tmp')], ok=(0, 1))   # (x86boot returns 1 when the machine powered off: expected)
    if not os.path.exists(W('settled.img.tmp')): sys.exit('settle boot saved no disk')
    os.replace(W('settled.img.tmp'), W('settled.img'))

if stage(6, 'pack', a.out):
    run([sys.executable, '-I', os.path.join(HERE, 'slim.py'), W('settled.img'), W('final.img'), '--n98d', a.out + '.tmp'])
    os.replace(a.out + '.tmp', a.out)
    os.remove(W('final.img'))

# 7: the shared warm-boot state, with an empty schema-2 package (no game: the run stops at the warm point)
warmpkg = W('warmup.n98')
if not os.path.exists(warmpkg):
    m = fat32.MemDisk(2000 << 20); fat32.mkfs(m, 2000); dn = W('warmup.D.n98d'); h = sparse.write_mem(m, dn)
    with zipfile.ZipFile(warmpkg, 'w') as z:
        z.write(dn, 'drives/D.n98d', compress_type=zipfile.ZIP_STORED)
        z.writestr('manifest.json', json.dumps({'format': 'n98', 'schema': 2, 'id': 'nano98-warmup', 'title': 'nano98 warm-up',
                                                'hardware': {'machine': a.machine.upper()}, 'cd': [], 'presets': {}, 'saves': [],
                                                'system': {'base': 'nano98-se', 'layers': []},
                                                'drives': {'D': {'zip': 'drives/D.n98d', 'p256': h, 'length': 2000 << 20}},
                                                'launch': {'exe': 'C:/WINDOWS/RUNDLL32.EXE'}, 'c_files': [], 'ini': [], 'registry': {}}))
    os.remove(dn)
print(f'== stage 7 warm: running  [{time.time() - t0:.0f} s]', flush=True)
os.makedirs(a.out + '.warm', exist_ok=True)
run([a.workshop, '--n98seat', warmpkg, '--until-warm', '--frames', '60000'],
    env={'BROKENNES_N98_BASE': os.path.abspath(a.out), 'BROKENNES_N98_CACHE': W('cache'),
         'BROKENNES_N98_WARMDIR': '', 'BROKENNES_SAVES_DIR': W('saves')})
# the warm state the run made (in the cache) moves beside the base: OUT.warm\<base hash 16>-<build>
wroot = os.path.join(W('cache'), 'warm')
for d in os.listdir(wroot) if os.path.isdir(wroot) else []:
    if os.path.exists(os.path.join(wroot, d, 'warm.bin')):
        dst = os.path.join(a.out + '.warm', d)
        if os.path.isdir(dst): shutil.rmtree(dst)
        shutil.move(os.path.join(wroot, d), dst)
        print(f'warm state: {dst}')
print(f'== done in {time.time() - t0:.0f} s: {a.out} ({os.path.getsize(a.out) / 1e6:.1f} MB) + {a.out}.warm')
