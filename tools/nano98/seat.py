# seat.py: nano98 seat tools (run with the pyfatfs venv: C:/BrokenNes-work/tools/pyfat_venv/Scripts/python.exe -I seat.py ...)
#   seat.py install DISK.img          put C:\N98\N98LAUNCH.EXE on an installed Win98 disk and make it the shell (SYSTEM.INI shell=)
#                                     (+ seat_look.py: the nano98 boot screen, black desktop)
#   seat.py cmd OUT.txt run=PATH [args=...] [dir=...] [exit=poweroff|shell|restart]
#                                     write the command text for the COMMAND PORT (x86boot --n98cmd OUT.txt)
import os, struct, sys

HERE = os.path.dirname(os.path.abspath(__file__))
PART = 63 * 512   # build_base.py's partition start

def fsinfo_unknown(img):
    with open(img, 'r+b') as d:   # free count "unknown": Windows recounts (ScanDisk trap after host-side writes)
        for bs in (0, 6): d.seek(PART + (bs + 1) * 512 + 488); d.write(struct.pack('<II', 0xffffffff, 0xffffffff))

def install(img):
    from pyfatfs.PyFatFS import PyFatFS
    f = PyFatFS(img, offset=PART)
    f.makedir('/N98', recreate=True)
    if f.exists('/N98/N98LAUNCH.EXE'): f.remove('/N98/N98LAUNCH.EXE')   # (pyfatfs cannot overwrite in place)
    with f.openbin('/N98/N98LAUNCH.EXE', 'w') as o: o.write(open(os.path.join(HERE, 'launcher', 'N98LAUNCH.EXE'), 'rb').read())
    ini = f.openbin('/WINDOWS/SYSTEM.INI').read().decode('latin1')
    lines = ini.split('\r\n'); out = []; done = False
    for l in lines:
        if l.lower().startswith('shell='): l = 'shell=C:\\N98\\N98LAUNCH.EXE'; done = True
        out.append(l)
    if not done:
        i = next(k for k, l in enumerate(out) if l.strip().lower() == '[boot]'); out.insert(i + 1, 'shell=C:\\N98\\N98LAUNCH.EXE')
    with f.openbin('/WINDOWS/SYSTEM.INI', 'w') as o: o.write('\r\n'.join(out).encode('latin1'))
    f.close(); fsinfo_unknown(img)
    import seat_look; seat_look.apply(img)
    print(f'{img}: shell = C:\\N98\\N98LAUNCH.EXE')

def cmd(out, kv):
    text = 'N98CMD1\r\n' + ''.join(k + '\r\n' for k in kv)
    b = text.encode('latin1')
    if len(b) > 4095: sys.exit('command too long (4 KB)')
    open(out, 'wb').write(b); print(f'{out}: {len(b)} bytes of command')

if __name__ == '__main__':
    if len(sys.argv) >= 3 and sys.argv[1] == 'install': install(sys.argv[2])
    elif len(sys.argv) >= 4 and sys.argv[1] == 'cmd': cmd(sys.argv[2], sys.argv[3:])
    else: sys.exit(__doc__ or 'seat.py install DISK | cmd OUT k=v...')
