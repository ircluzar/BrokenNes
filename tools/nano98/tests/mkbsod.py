import struct, os
from pyfatfs.PyFatFS import PyFatFS
img = r'C:\BrokenNes-work\nano98_work\seat_bsod.img'
f = PyFatFS(img, offset=63 * 512)
if f.exists('/N98/BAD.VXD'): f.remove('/N98/BAD.VXD')
with f.openbin('/N98/BAD.VXD', 'w') as o: o.write(b'MZ' + os.urandom(4000))
ini = f.openbin('/WINDOWS/SYSTEM.INI').read().decode('latin1')
ini = ini.replace('[386Enh]\r\n', '[386Enh]\r\n' + r'device=C:\N98\BAD.VXD' + '\r\n', 1)
f.remove('/WINDOWS/SYSTEM.INI')
with f.openbin('/WINDOWS/SYSTEM.INI', 'w') as o: o.write(ini.encode('latin1'))
f.close()
with open(img, 'r+b') as d:
    for bs in (0, 6): d.seek(63 * 512 + (bs + 1) * 512 + 488); d.write(struct.pack('<II', 0xffffffff, 0xffffffff))
print('device line', 'BAD.VXD' in ini)
