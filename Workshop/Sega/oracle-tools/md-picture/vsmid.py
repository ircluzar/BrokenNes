"""Where on a line a mid-line write of the vertical scroll RAM takes effect (Track B4a; Road Rash II's raster effect showed the oracle and ours 1 to 3 columns apart): vsmid.py build out.bin | run out.bin | decode shot.png [yoff]
The ROM is the vsprobe scene (plane A, every plane row identifiable from its colours, full-screen vertical scroll) and a main loop that every frame, for 20 lines (16 + 8 k), waits for the V counter to reach the line (the V counter steps in the tail of the line before), for the H counter to wrap to the new line, then for the
H counter's top byte to reach H0 (14 + 8 k), and writes VSRAM[0] = 13 through the data port; at the end of the line (H counter $B0) it writes 0 back. The picture shows, line by line, from which pixel the plane's row is the
scrolled one (y + 13) and not y; the same ROM on both machines gives the same polls, so the columns are compared directly. `decode` prints, per line k, the first tile column whose plane row is y + 13."""
import sys, os, json
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import scenefuzz, vsprobe, asm68k, random

NK = 20
LINES = [16 + 8 * k for k in range(NK)]
H0S = [14 + 8 * k for k in range(NK)]
VNEW = 13


def build(out, h40=True, mode='full'):
    s = vsprobe.make(0, h40, 0, second=False)
    s['regs'][11] = 0                                  # full-screen vertical scroll: VSRAM[0] (plane A) is the only entry read
    s['vsram'] = [0] * 40
    asm = scenefuzz.scene_asm(s, 0, None)
    asm = asm[:asm.index('wt0:')]                      # the scene's loader, up to the display being switched on
    loop = ['    lea ($C00008).l,a1', 'frame:']
    for k in range(NK):
        loop += ['v%d: move.b (a1),d1' % k, '    cmpi.b #$%02X,d1' % LINES[k], '    bne.s v%d' % k,
                 'w%d: move.w (a1),d1' % k, '    cmpi.b #$80,d1', '    bhs.s w%d' % k,
                 '    move.l #$40000010,(a4)',
                 'h%d: move.w (a1),d1' % k, '    cmpi.b #$%02X,d1' % H0S[k], '    bcs.s h%d' % k,
                 '    move.w #%d,(a3)' % VNEW,
                 '    move.l #$40000010,(a4)',
                 'r%d: move.w (a1),d1' % k, '    cmpi.b #$B0,d1', '    bcs.s r%d' % k,
                 '    move.w #0,(a3)']
    if mode == 'static': loop = ['_hold: bra.s _hold']       # the loader only: does the oracle show the plane?
    elif mode == 'poll': loop = [l for l in loop if 'move.w #%d,(a3)' % VNEW not in l and 'move.w #0,(a3)' not in l] + ['    bra.w frame']       # the polls, no VSRAM writes
    else: loop += ['    bra.w frame']
    src = '\n'.join(['    move.b #$40,($A10009).l', '    lea ($C00004).l,a4', '    lea ($C00000).l,a3']) + '\n' + asm + '\n'.join(loop) + '\n_spin: bra.s _spin\n'
    rom = bytearray(asm68k.build_rom(src, title='VSMID', size=scenefuzz.ROM_SIZE))
    rnd = random.Random(0x5EED)
    rom[scenefuzz.TABLE_AT:scenefuzz.TABLE_AT + 0x80000] = bytes(rnd.getrandbits(8) for _ in range(0x80000))
    open(out, 'wb').write(rom)
    print('wrote', out)


def decode(shot, yoff=0):
    sys.path.insert(0, os.environ.get('PYTHONPATH', HERE))
    from pngio import read_png
    w, h, px = read_png(shot)
    res = []
    for k in range(NK):
        y = LINES[k]
        row = [tuple(p[:3]) for p in px[y + yoff][:320]]
        tiles = vsprobe.decode_row(row)
        first = None
        for x, R in tiles:
            if R == (y + VNEW) % 256: first = x; break
        old = [x for x, R in tiles if R == y % 256]
        res.append((k, y, H0S[k], first, (max(old) if old else None), len(tiles)))
    return res


if __name__ == '__main__':
    if sys.argv[1] == 'build': build(sys.argv[2])
    elif sys.argv[1] == 'build-mode': build(sys.argv[2], mode=sys.argv[3])
    elif sys.argv[1] == 'decode':
        for r in decode(sys.argv[2], int(sys.argv[3]) if len(sys.argv) > 3 else 0): print('line %3d (k %2d, H0 %3d): the scrolled row from x %s (last old tile at x %s, %d tiles decoded)' % (r[1], r[0], r[2], r[3], r[4], r[5]))
