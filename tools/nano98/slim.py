# slim.py: nano98 base slimming (reproducible step after seat.py install). Pure Python, no dependencies:
#   python -I tools/nano98/slim.py IN.img OUT.img [--n98d OUT.n98d] [--list REMOVED.txt]
# Copies IN to OUT, deletes everything the game seat does not need (CULL below), drops the Run/RunServices start-up programs
# (the launcher is the shell: nothing else should start), zero-fills every free cluster (so a sparse container stores only used
# data and the result is byte-reproducible), and optionally writes the sparse/compressed container (sparse.py).
# Kept on purpose: DirectX (DDRAW/DSOUND/D3D*/DINPUT/DPLAY), the S3 / Voodoo / GeForce paths (VXDs, INF, OPENGL32, setup engine
# for Add New Hardware), SB16 drivers, codecs + QUARTZ (game intros), MFC/MSVCRT runtimes, networking (DirectPlay IPX/TCP), core fonts.
import fnmatch, os, shutil, sys
BS = chr(92)

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), 'pkg'))
import fat32, creg  # noqa: E402

# path globs (upper case, '/' separated, relative to C:\); a matching directory goes whole
CULL = [
    'WIN98', 'WINDOWS/WIN386.SWP', 'WINDOWS/SYSBCKUP', 'WINDOWS/HELP', 'WINDOWS/JAVA', 'WINDOWS/SYSTEM/OOBE', 'WINDOWS/MEDIA',
    'WINDOWS/WEB', 'WINDOWS/APPLICATION DATA', 'WINDOWS/TEMPORARY INTERNET FILES', 'WINDOWS/HISTORY', 'WINDOWS/COOKIES',
    'WINDOWS/FAVORITES', 'WINDOWS/OFFLINE WEB PAGES', 'WINDOWS/DOWNLOADED PROGRAM FILES', 'WINDOWS/TEMP/*', 'WINDOWS/APPLOG',
    'WINDOWS/DRWATSON', 'WINDOWS/TASKS', 'WINDOWS/SYSTEM32/DRIVERS/GM.DLS',   # (INF PNFs + DRVIDX.BIN stay: without them a new device rebuilds the driver database)
    'PROGRAM FILES/ONLINE SERVICES', 'PROGRAM FILES/INTERNET EXPLORER', 'PROGRAM FILES/OUTLOOK EXPRESS', 'PROGRAM FILES/WINDOWS MEDIA PLAYER',
    'PROGRAM FILES/UNINSTALL INFORMATION', 'PROGRAM FILES/PLUS!', 'PROGRAM FILES/ACCESSORIES', 'PROGRAM FILES/COMMON FILES/MICROSOFT SHARED',
    'PROGRAM FILES/COMMON FILES/SYSTEM/OLE DB', 'PROGRAM FILES/COMMON FILES/SYSTEM/MSADC', 'PROGRAM FILES/COMMON FILES/SYSTEM/ADO',
    'SETUPLOG.TXT', 'DETLOG.TXT', 'BOOTLOG.TXT', 'BOOTLOG.PRV', 'NETLOG.TXT', 'SUHDLOG.DAT', 'SYSTEM.1ST', 'SCANDISK.LOG',
    # WINDOWS: applets, tours, wizards, help, readmes, screen savers, wallpapers
    'WINDOWS/*.TXT', 'WINDOWS/*.HLP', 'WINDOWS/*.CHM', 'WINDOWS/*.SCR', 'WINDOWS/*.BMP', 'WINDOWS/*.GIF', 'WINDOWS/*.HTM',
    'WINDOWS/WINREP.EXE', 'WINDOWS/WINHLP32.EXE', 'WINDOWS/WINHELP.EXE', 'WINDOWS/WELCOME.EXE', 'WINDOWS/DEFRAG.EXE', 'WINDOWS/TOUR98.EXE',
    'WINDOWS/JVIEW.EXE', 'WINDOWS/WJVIEW.EXE', 'WINDOWS/MPLAYER.EXE', 'WINDOWS/WINFILE.EXE', 'WINDOWS/WSCRIPT.EXE', 'WINDOWS/DRWATSON.EXE',
    'WINDOWS/SIGVERIF.EXE', 'WINDOWS/CLEANMGR.EXE', 'WINDOWS/HWINFO.*', 'WINDOWS/PROGMAN.EXE', 'WINDOWS/TUNEUP.EXE', 'WINDOWS/CDPLAYER.EXE',
    'WINDOWS/TELNET.EXE', 'WINDOWS/PACKAGER.EXE', 'WINDOWS/CVTAPLOG.EXE', 'WINDOWS/DOSREP.EXE', 'WINDOWS/MORICONS.DLL', 'WINDOWS/TASKMAN.EXE',
    'WINDOWS/SCANDSKW.EXE', 'WINDOWS/DXDIAG.EXE', 'WINDOWS/WRITE.EXE', 'WINDOWS/HHUPD.EXE', 'WINDOWS/HH.EXE', 'WINDOWS/WINVER.EXE',
    'WINDOWS/WINPOPUP.EXE', 'WINDOWS/TASKMON.EXE', 'WINDOWS/MSOWS409.DLL', 'WINDOWS/CSCRIPT.EXE', 'WINDOWS/BPCPOST.EXE',
    # WINDOWS/SYSTEM: Jet/ODBC/DAO/ADO, scripting, Java VM, IE/HTML/DHTML, Active Desktop, media player, VB5, task scheduler, ICW
    'WINDOWS/SYSTEM/MSJET*', 'WINDOWS/SYSTEM/MSJT*', 'WINDOWS/SYSTEM/MSRD*X*', 'WINDOWS/SYSTEM/MSREPL*', 'WINDOWS/SYSTEM/MSEXCH*',
    'WINDOWS/SYSTEM/MSEXCL*', 'WINDOWS/SYSTEM/MSPBDE*', 'WINDOWS/SYSTEM/MSXBDE*', 'WINDOWS/SYSTEM/MSLTUS*', 'WINDOWS/SYSTEM/MSTEXT*',
    'WINDOWS/SYSTEM/MSJTES*', 'WINDOWS/SYSTEM/MSJINT*', 'WINDOWS/SYSTEM/MSWDAT*', 'WINDOWS/SYSTEM/MSWSTR*', 'WINDOWS/SYSTEM/VFPODBC.DLL',
    'WINDOWS/SYSTEM/ODBC*', 'WINDOWS/SYSTEM/SQLSRV32.*', 'WINDOWS/SYSTEM/INSTCAT.SQL', 'WINDOWS/SYSTEM/*.HLP', 'WINDOWS/SYSTEM/*.CHM',
    'WINDOWS/SYSTEM/*.CNT', 'WINDOWS/SYSTEM/MSJETOLEDB40.DLL', 'WINDOWS/SYSTEM/DAO*', 'WINDOWS/SYSTEM/DDAO*', 'WINDOWS/SYSTEM/EXPSRV.DLL',
    'WINDOWS/SYSTEM/VBSCRIPT.DLL', 'WINDOWS/SYSTEM/JSCRIPT.DLL', 'WINDOWS/SYSTEM/SCRRUN.DLL', 'WINDOWS/SYSTEM/SCROBJ.DLL', 'WINDOWS/SYSTEM/WSHOM.OCX',
    'WINDOWS/SYSTEM/MSJAVA.DLL', 'WINDOWS/SYSTEM/JAVA*', 'WINDOWS/SYSTEM/JIT.DLL', 'WINDOWS/SYSTEM/MSAWT.DLL', 'WINDOWS/SYSTEM/DX3J.DLL',
    'WINDOWS/SYSTEM/JDBGMGR.EXE', 'WINDOWS/SYSTEM/VMHELPER.DLL', 'WINDOWS/SYSTEM/JGDW400.DLL', 'WINDOWS/SYSTEM/DANIM.DLL', 'WINDOWS/SYSTEM/DXTMSFT*',
    'WINDOWS/SYSTEM/DXTRANS.DLL', 'WINDOWS/SYSTEM/DATIME.DLL', 'WINDOWS/SYSTEM/DAXCTLE.OCX', 'WINDOWS/SYSTEM/MSHTML.TLB', 'WINDOWS/SYSTEM/MSHTMLED.DLL',
    'WINDOWS/SYSTEM/MSDXM.OCX', 'WINDOWS/SYSTEM/DXMASF.DLL', 'WINDOWS/SYSTEM/MSVBVM50.DLL', 'WINDOWS/SYSTEM/MSTASK.*', 'WINDOWS/SYSTEM/MSTINIT.EXE',
    'WINDOWS/SYSTEM/WEBCHECK.DLL', 'WINDOWS/SYSTEM/MOBSYNC.*', 'WINDOWS/SYSTEM/CDFVIEW.DLL', 'WINDOWS/SYSTEM/WEBVW.DLL', 'WINDOWS/SYSTEM/MSIEFTP.DLL',
    'WINDOWS/SYSTEM/REGWIZC.DLL', 'WINDOWS/SYSTEM/MSMS001.VWP', 'WINDOWS/SYSTEM/MVOICED.VWP', 'WINDOWS/SYSTEM/ICWSCRPT.EXE', 'WINDOWS/SYSTEM/INETCFG.DLL',
    'WINDOWS/SYSTEM/EYEDOG.OCX', 'WINDOWS/SYSTEM/FTSRCH.DLL', 'WINDOWS/SYSTEM/ITIRCL.DLL', 'WINDOWS/SYSTEM/MSXML.DLL', 'WINDOWS/SYSTEM/MAPI32.DLL',
    'WINDOWS/SYSTEM/MMEFXE.OCX', 'WINDOWS/SYSTEM/QDVD.DLL', 'WINDOWS/SYSTEM/MSRATING.DLL', 'WINDOWS/SYSTEM/MSMSGS.EXE', 'WINDOWS/SYSTEM/BPCPOST.EXE',
    # fonts: the .FON system fonts, Arial, Times, Courier, Tahoma, Marlett, Symbol, Wingdings stay
    'WINDOWS/FONTS/VERDANA*', 'WINDOWS/FONTS/IMPACT.TTF', 'WINDOWS/FONTS/COMIC*', 'WINDOWS/FONTS/WEBDINGS.TTF', 'WINDOWS/FONTS/ARIBLK.TTF',
    'WINDOWS/FONTS/LUCON.TTF',
]
# start-up programs to drop (the launcher is the shell)
RUN_KEYS = ['Software\\Microsoft\\Windows\\CurrentVersion\\Run', 'Software\\Microsoft\\Windows\\CurrentVersion\\RunServices']
KEEP_NAMES = {'.', '..'}


def cull(fs):
    removed, total = [], 0
    for p, e in sorted(fs.walk(), key=lambda x: x[0]):
        u = p.upper()
        if any(u.startswith(r + '/') for r, _ in removed):
            continue
        if any(fnmatch.fnmatchcase(u, g) for g in CULL):
            size = sum(x.size for q, x in fs.walk() if q.upper().startswith(u + '/') and not x.is_dir) if e.is_dir else e.size
            removed.append((u, size)); total += size
    for u, _ in removed:
        assert fs.delete(u), u
    return removed, total


def drop_run(fs, removed):
    hp = 'WINDOWS/SYSTEM.DAT'
    h = creg.Hive(fs.read_file(hp))
    ops = []
    flat = h.flat()
    # static VxDs whose file the cull removed (JAVASUP.VXD): Windows stops at boot with "cannot find a device file"
    gone = {u.split('/')[-1] for u, _ in removed}
    vx = 'SYSTEM\\CURRENTCONTROLSET\\SERVICES\\VXD\\'
    for k, vals in flat.items():
        if k.upper().startswith(vx) and k.count(BS) == 4 and 'STATICVXD' in vals:
            f = vals['STATICVXD'][2].rstrip(bytes(1)).decode('latin1').split(',')[0].strip()
            if f and '*' not in f:
                if f.split(BS)[-1].upper() in gone:
                    ops.append({'op': 'delkey', 'key': k})
    for k in RUN_KEYS:
        for vn, _, _ in flat.get(k, {}).values():
            ops.append({'op': 'delval', 'key': k, 'name': vn})
    if not ops:
        return 0
    new = creg.Hive(fs.read_file(hp)).apply(ops)
    for name in (hp, 'WINDOWS/SYSTEM.DA0'):
        e, _ = fs.lookup(name)
        if e is not None:
            fs.write_file(name, new, e.attr, (e.ctime, e.cdate, e.adate, e.wtime, e.wdate))
    return len(ops)


def zero_free(fs):
    z = b'\0' * fs.csize; n = 0
    for c in range(2, fs.nclus):
        if fs.next(c) == 0:
            fs._wraw(fs.clus_lba(c), z); n += 1
    return n


def used_bytes(fs):
    return sum(1 for c in range(2, fs.nclus) if fs.next(c) != 0) * fs.csize


def main():
    args = sys.argv[1:]
    if len(args) < 2:
        sys.exit(__doc__ or 'slim.py IN.img OUT.img [--n98d OUT.n98d] [--list FILE]')
    src, out = args[0], args[1]
    opt = dict(zip(args[2::2], args[3::2]))
    shutil.copyfile(src, out)
    fs = fat32.Fat32(out, write=True)
    before = used_bytes(fs)
    removed, total = cull(fs)
    runs = drop_run(fs, removed)
    e, _ = fs.lookup('N98/N98LAUNCH.EXE')   # the current launcher (schema-2 system ops)
    fs.write_file('N98/N98LAUNCH.EXE', open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'launcher', 'N98LAUNCH.EXE'), 'rb').read(),
                  0x20, (e.ctime, e.cdate, e.adate, e.wtime, e.wdate) if e else (0, 0, 0, 0, 0))
    fs.fat_dirty = True
    fs.close()
    fs = fat32.Fat32(out, write=True)
    after = used_bytes(fs)
    nz = zero_free(fs)
    fs.f.close()
    print(f'{out}: used {before / 1e6:.1f} MB -> {after / 1e6:.1f} MB; removed {len(removed)} entries ({total / 1e6:.1f} MB of files), '
          f'{runs} registry ops (start-up values, VxDs of removed files); {nz} free clusters zeroed')
    if '--list' in opt:
        with open(opt['--list'], 'w') as f:
            for u, s in removed:
                f.write(f'{s}\t{u}\n')
    if '--n98d' in opt:
        import sparse
        sparse.write(out, opt['--n98d'])


if __name__ == '__main__':
    main()
