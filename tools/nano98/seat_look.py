# seat_look.py: the nano98 seat's look, baked into a seat base image (pure Python, run with python -I):
#   python -I tools/nano98/seat_look.py DISK.img            write it onto an installed Win98 disk (seat.py install calls this too)
#   python -I tools/nano98/seat_look.py --bmp OUT.bmp       just write the boot screen bitmap (to look at)
# - C:\LOGO.SYS: the boot screen = black with white "nano98 is loading..." (Win98 shows a 320x400 256-colour BMP stretched to 4:3,
#   replacing the Windows 98 sky logo built into IO.SYS). The text is drawn from the 5x7 font below, so the file is byte-reproducible.
# - C:\WINDOWS\LOGOW.SYS / LOGOS.SYS (the "please wait" / "safe to turn off" shutdown screens): plain black.
# - N98/BLANK.CUR + blank Arrow/Wait/AppStarting cursors: no pointer until the launcher restores the standard ones (game window up).
# - HKEY_USERS\.DEFAULT\Control Panel\Colors Background = 0 0 0 and no wallpaper / pattern: the desktop behind the launcher is black, not teal.
import os, struct, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, 'pkg'))

TEXT = 'nano98 is loading...'
FONT = {   # 5x7, one string per row, '#' = ink
    'n': ['     ', '     ', '# ## ', '##  #', '#   #', '#   #', '#   #'],
    'a': ['     ', '     ', ' ### ', '    #', ' ####', '#   #', ' ####'],
    'o': ['     ', '     ', ' ### ', '#   #', '#   #', '#   #', ' ### '],
    '9': [' ### ', '#   #', '#   #', ' ####', '    #', '   # ', ' ##  '],
    '8': [' ### ', '#   #', '#   #', ' ### ', '#   #', '#   #', ' ### '],
    'i': ['  #  ', '     ', ' ##  ', '  #  ', '  #  ', '  #  ', ' ### '],
    's': ['     ', '     ', ' ####', '#    ', ' ### ', '    #', '#### '],
    'l': [' ##  ', '  #  ', '  #  ', '  #  ', '  #  ', '  #  ', ' ### '],
    'd': ['    #', '    #', ' ## #', '#  ##', '#   #', '#   #', ' ####'],
    'g': ['     ', ' ####', '#   #', '#   #', ' ####', '    #', ' ### '],
    '.': ['     ', '     ', '     ', '     ', '     ', ' ##  ', ' ##  '],
    ' ': ['     '] * 7,
}
W, H = 320, 400          # the logo bitmap (shown stretched to the 4:3 screen: one pixel is ~1.67x as wide as it is tall)
PX, PY = 2, 3            # font pixel = 2 x 3 bitmap pixels (square on screen)
WHITE, BLACK = 0, 1      # palette indexes; every other entry is black too, so IO.SYS's palette cycling has nothing to animate


def bmp(pixels, palette):
    """8-bit bottom-up BMP, 256-entry palette (B, G, R, 0)."""
    row = (W + 3) & ~3
    img = b''.join(bytes(pixels[y * W:(y + 1) * W]) + b'\0' * (row - W) for y in range(H - 1, -1, -1))
    pal = b''.join(bytes((b, g, r, 0)) for r, g, b in palette)
    off = 14 + 40 + len(pal)
    return (b'BM' + struct.pack('<IHHI', off + len(img), 0, 0, off)
            + struct.pack('<IiiHHIIiiII', 40, W, H, 1, 8, 0, len(img), 2835, 2835, 256, 256) + pal + img)


def logo():
    pix = bytearray([BLACK]) * (W * H)
    tw, th = len(TEXT) * 6 * PX - PX, 7 * PY
    x0, y0 = (W - tw) // 2, (H - th) // 2
    for i, ch in enumerate(TEXT):
        for r, line in enumerate(FONT[ch]):
            for c, p in enumerate(line):
                if p == '#':
                    for dy in range(PY):
                        o = (y0 + r * PY + dy) * W + x0 + (i * 6 + c) * PX
                        pix[o:o + PX] = bytes([WHITE]) * PX
    return bmp(pix, [(255, 255, 255), (0, 0, 0)] + [(0, 0, 0)] * 254)


def blank_cur():
    """A 32x32 monochrome cursor that draws nothing (AND mask all 1, XOR mask all 0)."""
    bih = struct.pack('<IiiHHIIiiII', 40, 32, 64, 1, 1, 0, 256, 0, 0, 2, 0)
    body = bih + bytes((0, 0, 0, 0, 255, 255, 255, 0)) + b'\0' * 128 + b'\xff' * 128
    return struct.pack('<HHH', 0, 2, 1) + struct.pack('<BBBBHHII', 32, 32, 0, 0, 0, 0, len(body), 22) + body


def black():
    return bmp(bytearray(W * H), [(0, 0, 0)] * 256)


REG = [  # HKEY_USERS ops (n98 registry op format)
    {'op': 'set', 'key': '.DEFAULT\\Control Panel\\Colors', 'name': 'Background', 'type': 1, 'data': b'0 0 0\0'.hex()},
    {'op': 'set', 'key': '.DEFAULT\\Control Panel\\Desktop', 'name': 'Wallpaper', 'type': 1, 'data': b'(None)\0'.hex()},
    {'op': 'set', 'key': '.DEFAULT\\Control Panel\\Desktop', 'name': 'Pattern', 'type': 1, 'data': b'(None)\0'.hex()},
    {'op': 'set', 'key': '.DEFAULT\\Control Panel\\Desktop', 'name': 'TileWallpaper', 'type': 1, 'data': b'0\0'.hex()},
] + [{'op': 'set', 'key': '.DEFAULT\\Control Panel\\Cursors', 'name': n, 'type': 1, 'data': b'C:\\N98\\BLANK.CUR\0'.hex()}
     for n in ('Arrow', 'Wait', 'AppStarting')]   # blank pointer until N98LAUNCH.EXE's cursors_on() once the game is up


def apply(img):
    import creg, fat32
    fs = fat32.Fat32(img, write=True)
    fs.write_file('LOGO.SYS', logo(), 0x20)
    fs.write_file('N98/BLANK.CUR', blank_cur(), 0x20)
    fs.write_file('N98/N98LAUNCH.EXE', open(os.path.join(HERE, 'launcher', 'N98LAUNCH.EXE'), 'rb').read(), 0x20)   # (the launcher with cursors_on)
    for n in ('WINDOWS/LOGOW.SYS', 'WINDOWS/LOGOS.SYS'):
        e, _ = fs.lookup(n)
        fs.write_file(n, black(), e.attr if e is not None else 0x20)
    # MSDOS.SYS: the boot screen on (Logo=0 would show the text-mode boot instead)
    e, _ = fs.lookup('MSDOS.SYS')
    ms = fs.read_file('MSDOS.SYS').decode('latin1')
    if 'logo=0' in ms.lower().replace(' ', ''):
        ms = '\r\n'.join(l for l in ms.split('\r\n') if l.replace(' ', '').lower() != 'logo=0')
        fs.write_file('MSDOS.SYS', ms.encode('latin1'), e.attr, (e.ctime, e.cdate, e.adate, e.wtime, e.wdate))
    hp = 'WINDOWS/USER.DAT'
    new = creg.Hive(fs.read_file(hp)).apply(REG)
    for h in (hp, 'WINDOWS/USER.DA0'):   # (the DA0 backup in step, as n98.py apply does)
        e, _ = fs.lookup(h)
        if e is not None:
            fs.write_file(h, new, e.attr, (e.ctime, e.cdate, e.adate, e.wtime, e.wdate))
    fs.fat_dirty = True   # (close() then also marks the FSInfo free count unknown)
    fs.close()
    print(f'{img}: LOGO.SYS "{TEXT}" (white on black), LOGOW/LOGOS.SYS black, desktop black, blank cursors')


if __name__ == '__main__':
    if len(sys.argv) == 3 and sys.argv[1] == '--bmp':
        open(sys.argv[2], 'wb').write(logo()); print(sys.argv[2])
    elif len(sys.argv) == 2:
        apply(sys.argv[1])
    else:
        sys.exit('seat_look.py DISK.img | --bmp OUT.bmp')
