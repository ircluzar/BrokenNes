# build_base.py: nano98 base-image builder, stage 1 (host side) - run with the pyfatfs venv:
#   C:/BrokenNes-work/tools/pyfat_venv/Scripts/python.exe -I tools/nano98/build_base.py --iso X:/SYSTEMS/Windows/Windows98_EN.iso --work C:/BrokenNes-work/nano98_work
# Makes WORK/hd_setup.img: an MBR disk (one active FAT32 LBA partition at sector 63) holding the Win98 SE CABs in C:\WIN98
# and an unattended answer file C:\WIN98\MSBATCH.INF. Stage 2 (in the emulator, x86boot) boots the CD's El Torito boot disk,
# runs C:\WIN98\SETUP.EXE C:\WIN98\MSBATCH.INF and lets Setup finish on the floppy-less machine preset win98-p166-nano.
# The user's ISO is only read; media are unpacked into WORK (inside C:\BrokenNes-work, never elsewhere).
# The product key comes from --keyfile (never written to the repo). EULA: Setup's licence page is accepted by the answer
# file only with --accept-eula (user approved the Win98 SE licence for these installs on 2026-11-21).
import argparse, io, os, struct, sys, zipfile

ap = argparse.ArgumentParser()
ap.add_argument('--iso', required=True)
ap.add_argument('--work', required=True)
ap.add_argument('--size-mb', type=int, default=2000)   # (x86boot reads the disk with File.ReadAllBytes: < 2 GiB)
ap.add_argument('--keyfile', default='T:/BrokenDev/Archive/win98_m7_c/productkey.txt')
ap.add_argument('--accept-eula', action='store_true')
ap.add_argument('--zip', default='/WIN98SE.ZIP', help='the SE files inside the ISO (the user\'s disc is a 95/98/98SE/ME compilation)')
a = ap.parse_args()
os.makedirs(a.work, exist_ok=True)

# ---- ISO9660 read (primary names) ----
S = 2048
iso = open(a.iso, 'rb')
def rd(l, n): iso.seek(l * S); return iso.read(n)
def walk(lba, size, path):
    d = rd(lba, size); i = 0
    while i < len(d):
        L = d[i]
        if L == 0: i = (i // S + 1) * S; continue
        e = d[i:i + L]; el, sz = struct.unpack_from('<I', e, 2)[0], struct.unpack_from('<I', e, 10)[0]; fl = e[25]; nm = e[33:33 + e[32]]
        if nm not in (b'\0', b'\1'):
            n = path + '/' + nm.decode('latin1').split(';')[0]
            if fl & 2: yield from walk(el, sz, n)
            else: yield n, el, sz
        i += L
pvd = rd(16, S); root = pvd[156:190]
files = {n.upper(): (l, s) for n, l, s in walk(struct.unpack_from('<I', root, 2)[0], struct.unpack_from('<I', root, 10)[0], '')}
if a.zip.upper() not in files: sys.exit(f'{a.zip} not on the ISO')
l, s = files[a.zip.upper()]
z = zipfile.ZipFile(io.BytesIO(rd(l, s)))
media = {}
for zi in z.infolist():
    if zi.is_dir(): continue
    name = zi.filename.split('/')[-1].upper()
    media[name] = z.read(zi)
print(f'media: {len(media)} files, {sum(map(len, media.values())) // 1024 // 1024} MB from {a.zip}')
if 'SETUP.EXE' not in media or not any(n.startswith('WIN98_') for n in media): sys.exit('no Win98 Setup in the zip')

# ---- answer file ----
key = open(a.keyfile).read().strip() if os.path.exists(a.keyfile) else ''
if not key: print('WARNING: no product key file: Setup will stop at the key page')
msbatch = f"""[BatchSetup]
Version=3.0 (32-bit)
SaveDate=11/21/26

[Version]
Signature = "$CHICAGO$"
AdvancedINF=2.5

[Setup]
Express=1
InstallDir="C:\\WINDOWS"
InstallType=0
EBD=0

ChangeDir=0
OptionalComponents=1
Network=0
System=0
CCP=0
CleanBoot=0
Display=0
DevicePath=0
NoDirWarn=1
TimeZone="Eastern"
Uninstall=0
VRC=0
NoPrompt2Boot=1
ProductKey="{key}"

[Network]
ComputerName="NANO98"
Workgroup="WORKGROUP"
Description="nano98"
Display=0

[NameAndOrg]
Name="nano98"
Org="nano98"
Display=0

[OptionalComponents]
"Accessibility Options"=0
"Accessories"=0
"Communications"=0
"Desktop Themes"=0
"Internet Tools"=0
"Multilanguage Support"=0
"Multimedia"=1
"Online Services"=0
"System Tools"=0
"Web TV for Windows"=0
"Outlook Express"=0
"Windows Scripting Host"=0
"""
# ---- stage 2 steps (x86boot --stepsfile; frame counts measured 2026-11-21 on win98-p166-nano, deterministic) ----
NL = chr(92) + 'n'   # x86boot's Enter escape
steps = ['Enter your choice=>2' + NL,                    # CD menu: boot from CD (the compilation's 98 boot disk starts the CD's Setup)
         'To continue with Setup, press ENTER=>{f3}',    # quit that Setup (it would install the first-edition files from D:)
         '@200=>{f3}',
         '@300=>C:\WIN98\SETUP.EXE C:\WIN98\MSBATCH.INF /IS' + NL,   # SE Setup from C: with the answer file
         '@51250=>' + NL]                                # after the first restart (~52000 frames from power-on): User Information page -> Next
if a.accept_eula:
    steps += ['@800=>{alt+a}', '@60=>' + NL,           # Licence Agreement: "I accept" (user approved the Win98 SE licence 2026-11-21)
              '@600=>' + NL,                             # Product Key (filled from MSBATCH.INF) -> Next
              '@800=>' + NL,                             # Start Wizard -> Finish: hardware setup + restarts follow
              '@66000=>' + NL,                           # (hardware setup + restarts take ~50-60k frames) Enter Windows Password: user nano98, blank password -> OK
              '@3000=>{alt+f4}',                         # close the Welcome window
              '@300=>']                                  # the desktop (save the state here)
else:
    steps += ['@800=>']                                  # stop on the licence page
open(os.path.join(a.work, 'stage2.steps'), 'w').write('\n'.join(steps) + '\n')
print('stage 2: x86boot --machine win98-p166-nano --hdd hd_setup.img --cd ISO --bootorder CH --frames 1 --stepsfile stage2.steps' + ('' if a.accept_eula else '  (stops at the licence page: no --accept-eula)'))

# ---- disk: MBR + FAT32 partition ----
from pyfatfs.PyFat import PyFat
from pyfatfs.PyFatFS import PyFatFS
total = a.size_mb * 1024 * 1024 // 512
part_lba = 63; part_sec = total - part_lba
part = os.path.join(a.work, 'part.tmp')
with open(part, 'wb') as f: f.truncate(part_sec * 512)
pf = PyFat(); pf.mkfs(part, PyFat.FAT_TYPE_FAT32, size=part_sec * 512, label='NANO98'); pf.close()
with open(part, 'r+b') as f:   # BPB geometry for an LBA disk at sector 63 (pyfatfs writes 0s) + FSInfo free count "unknown" (ScanDisk trap)
    for bs in (0, 6):
        f.seek(bs * 512 + 0x18); f.write(struct.pack('<HHI', 63, 255, part_lba))
        f.seek((bs + 1) * 512 + 488); f.write(struct.pack('<II', 0xffffffff, 0xffffffff))
fs = PyFatFS(part)
fs.makedir('/WIN98')
for n, b in sorted(media.items()):
    with fs.openbin('/WIN98/' + n, 'w') as o: o.write(b)
with fs.openbin('/WIN98/MSBATCH.INF', 'w') as o: o.write(msbatch.replace('\n', '\r\n').encode('latin1'))
fs.close()
mbr = bytearray(512)
def chs(lba):
    c, r = divmod(lba, 255 * 63); h, s = divmod(r, 63); c = min(c, 1023)
    return bytes([h, ((c >> 2) & 0xc0) | (s + 1), c & 0xff])
mbr[446:462] = bytes([0x80]) + chs(part_lba) + bytes([0x0c]) + chs(total - 1) + struct.pack('<II', part_lba, part_sec)
mbr[510:512] = b'\x55\xaa'   # no boot code: Setup writes the MBR code (it cold-boots from the CD first)
out = os.path.join(a.work, 'hd_setup.img')
with open(out, 'wb') as o:
    o.write(mbr); o.write(b'\0' * 512 * (part_lba - 1))
    with open(part, 'rb') as p:
        while True:
            c = p.read(1 << 24)
            if not c: break
            o.write(c)
os.remove(part)
print(f'{out}: {a.size_mb} MB, FAT32 at LBA {part_lba}, C:\\WIN98 + MSBATCH.INF')
