"""Test inputs and probes for the WideMess devkit's converters (BrokenDev/widemess/tools), used by run-tests.ps1. Each mode exits 0 when
what it checks holds and 1 when it does not, so a test and its negative control call the same code.

  python wms-converters.py indexed <out.png>                       a 16 x 16 indexed PNG using 256 palette entries
  python wms-converters.py same-tiles <tools> <png> <pic>          wmsgfx's 8-bpp tiles of the indexed PNG equal the .pic (gfx4snes's)
  python wms-converters.py flips <tools> <dir>                     a tile and its mirror become one tile and a map entry with the flip bit
  python wms-converters.py colours <out.png> <n>                   an opaque PNG with n distinct colours
  python wms-converters.py wav <out.wav> <samples>                 a mono 16-bit 32 kHz sine
  python wms-converters.py midi <out.mid> <notes at once> [note]   that many notes at once, on MIDI channel 0
  python wms-converters.py project <dir> <mid>                     a wms.json with a sample and a song (the MIDI given), for wmssnd build
"""
import json
import os
import struct
import sys
import wave

import numpy as np
from PIL import Image


def indexed(path):
    a = np.zeros((16, 16), dtype=np.uint8)
    for y in range(16):
        for x in range(16):
            a[y, x] = (x * 16 + y * 7 + (x ^ y) * 3) & 0xFF
    im = Image.fromarray(a, 'P')
    im.putpalette([v for i in range(256) for v in (i, (i * 3) & 255, 255 - i)])
    im.save(path)
    return 0


def same_tiles(tools, png, pic):
    sys.path.insert(0, tools)
    import wmsgfx
    a = np.array(Image.open(png)).astype(np.int32)
    mine = b''.join(wmsgfx.encode_tile(a[ty * 8:ty * 8 + 8, tx * 8:tx * 8 + 8], 8)
                    for ty in range(a.shape[0] // 8) for tx in range(a.shape[1] // 8))
    theirs = open(pic, 'rb').read()
    print(f'wmsgfx {len(mine)} bytes, the .pic {len(theirs)} bytes: {"the same" if mine == theirs else "different"}')
    return 0 if mine == theirs else 1


def flips(tools, d):
    sys.path.insert(0, tools)
    import wmsgfx
    im = Image.new('RGBA', (16, 8), (0, 0, 0, 0))
    for y in range(8):
        for x in range(y + 1):           # a triangle: its mirror is another tile unless flips are found
            im.putpixel((x, y), (255, 0, 0, 255))
            im.putpixel((15 - x, y), (255, 0, 0, 255))
    png = os.path.join(d, 'flip.png')
    im.save(png)
    wmsgfx.single_layer([png, os.path.join(d, 'flip')])
    til = open(os.path.join(d, 'flip.til'), 'rb').read()
    m = struct.unpack('<2H', open(os.path.join(d, 'flip.map'), 'rb').read()[:4])
    ok = len(til) == 2 * 64 and m[0] == 1 and m[1] == (1 | 0x4000)
    print(f'{len(til) // 64} tiles (the blank one and the triangle), map entries {m[0]:04X} {m[1]:04X}: {"ok" if ok else "wrong"}')
    return 0 if ok else 1


def colours(path, n):
    """32 x 32, opaque: pixel k has colour k for k < n, colour 0 after (n <= 1,024; distinct in 15-bit BGR)."""
    n = int(n)
    im = Image.new('RGBA', (32, 32))
    for k in range(1024):
        c = k if k < n else 0
        im.putpixel((k % 32, k // 32), ((c & 31) << 3, ((c >> 5) & 31) << 3, 0x80, 255))
    im.save(path)
    print(len(set(im.getdata())), 'colours')
    return 0


def wav(path, n):
    n = int(n)
    t = np.arange(n) / 32000
    v = np.round(np.sin(2 * np.pi * 440 * t) * 20000).astype('<i2')
    with wave.open(path, 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(32000)
        w.writeframes(v.tobytes())
    return 0


def midi(path, count, note=60):
    """count notes struck together (note, note + 1, ...), released 480 ticks (a beat) later."""
    count, note = int(count), int(note)
    ev = bytearray()
    for k in range(count):
        ev += bytes([0, 0x90, note + k, 100])
    for k in range(count):
        ev += (bytes([0x83, 0x60]) if k == 0 else bytes([0])) + bytes([0x80, note + k, 0])   # the first after 480 ticks
    ev += bytes([0, 0xFF, 0x2F, 0])
    with open(path, 'wb') as f:
        f.write(b'MThd' + struct.pack('>IHHH', 6, 0, 1, 480) + b'MTrk' + struct.pack('>I', len(ev)) + bytes(ev))
    return 0


def project(d, mid):
    os.makedirs(d, exist_ok=True)
    wav(os.path.join(d, 'tone.wav'), 3200)
    cfg = {'name': 'probe', 'features': ['picture', 'sound'],
           'sound': {'samples': [{'name': 'tone', 'wav': 'tone.wav', 'format': 'pcm16', 'rate': 32000, 'root': 69}],
                     'songs': [{'name': 'song', 'mid': os.path.abspath(mid), 'instruments': {'0': 'tone'}}]}}
    json.dump(cfg, open(os.path.join(d, 'wms.json'), 'w'))
    return 0


if __name__ == '__main__':
    m = sys.argv[1]
    a = sys.argv[2:]
    sys.exit({'indexed': indexed, 'same-tiles': same_tiles, 'flips': flips, 'colours': colours, 'wav': wav, 'midi': midi,
              'project': project}[m](*a))
