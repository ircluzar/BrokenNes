"""Where on a line a vertical scroll RAM write takes effect, with the write time controlled to a few CPU cycles (Track B4a; the successor of vsmid.py, whose polled H counter wait quantises the write
time to a loop period of about 13 H counter steps).
The 68000 sleeps in `stop #$2000`; a horizontal interrupt every line (R10 = 0) wakes it at a fixed time; the handler reads the V counter and looks the line up in tables: what to write (0 nothing, 1 VSRAM[entry] = 13,
2 a white colour into CRAM entry 63) and how long to wait first (a count of `dbf` laps of 10 cycles and a count of nops of 4, so any even number of cycles). On the measured lines (16 + 8 k, k = 0 to 19) it writes VSRAM
`D0 + S k` cycles after the handler's fixed part; on the lines 20 + 8 k it writes CRAM after the same delay with identical instruction timing: the white dot on that line is where the display was at that time ("the colour
RAM dot"), so the VSRAM threshold can be read in pixels on each machine whatever its interrupt latency is. At the start of its next run the handler writes 0 back into the VSRAM entry.
    vsh.py build out.bin [--col C] [--d0 N] [--step S] [--h32]      build the ROM (--col: the 2-cell column scroll, the entry of that column; without it the full-screen scroll)
    vsh.py decode shot.png [--yoff N] [--col C]                      per measured line: which of the lines y-1 .. y+3 show the scrolled row (N) or not (o), and the x of the dot of the line y + 4"""
import sys, os
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import scenefuzz, vsprobe, asm68k, random

NK = 20
LINES = [16 + 8 * k for k in range(NK)]
VNEW = 13
DOT = 0x0EEE


def delays(d0, step):
    """the delay of measured line k, d0 + step k cycles (even), as (laps of the dbf loop (10 cycles each), nops (4 cycles each, 0 to 4)): d = 10 n + 4 m"""
    out = []
    for k in range(NK):
        d = d0 + step * k
        assert d % 2 == 0 and d >= 16
        m = {0: 0, 2: 3, 4: 1, 6: 4, 8: 2}[d % 10]
        out.append(((d - 4 * m) // 10, m))
    return out


def build(out, col=None, d0=60, step=4, h40=True):
    s = vsprobe.make(0, h40, 0, second=False)
    s['regs'][11] = 0 if col is None else 4
    s['regs'][0] = 0x14                                  # horizontal interrupt on
    s['regs'][10] = 0                                    # every line
    s['vsram'] = [0] * 40
    asm = scenefuzz.scene_asm(s, 0, None)
    asm = asm[:asm.index('wt0:')]
    dl = delays(d0, step)
    ktab = [0] * 256; ntab = [0] * 256; mtab = [0] * 256
    for k, (n, m) in enumerate(dl):
        for y, kind in ((LINES[k], 1), (LINES[k] + 4, 2)):
            ktab[y] = kind; ntab[y] = n; mtab[y] = m
    vscmd = 0x40000010 | ((4 * (col or 0)) << 16)
    src = '\n'.join(['    move.b #$40,($A10009).l', '    lea ($C00004).l,a4', '    lea ($C00000).l,a3', '    lea ($C00008).l,a1']) + '\n' + asm
    src += '\n'.join([
        '    move.l #$%08X,d6' % vscmd, '    moveq #0,d7', '    moveq #0,d5', '    move.w #$2000,sr',
        'idle: stop #$2000', '    bra.s idle',
        'hint:', '    tst.b d7', '    beq.s np', '    move.l d6,(a4)', '    move.w d5,(a3)', '    moveq #0,d7',
        'np: moveq #0,d0', '    move.b (a1),d0', '    lea ktab(pc),a5', '    adda.w d0,a5', '    moveq #0,d4', '    move.b (a5),d4', '    beq.s done',
        '    lea ntab(pc),a5', '    adda.w d0,a5', '    moveq #0,d1', '    move.b (a5),d1',
        '    lea mtab(pc),a5', '    adda.w d0,a5', '    moveq #0,d2', '    move.b (a5),d2', '    add.w d2,d2',
        '    lea adrtab(pc),a5', '    add.w d4,d4', '    add.w d4,d4', '    adda.w d4,a5', '    move.l (a5),d3',
        '    lsr.w #1,d4', '    lea dattab(pc),a5', '    adda.w d4,a5', '    move.w (a5),d0',
        '    move.l d3,(a4)',
        'dl: dbf d1,dl',
        '    lea sledend(pc),a6', '    suba.w d2,a6', '    jmp (a6)',
        '    nop', '    nop', '    nop', '    nop',
        'sledend: move.w d0,(a3)', '    moveq #1,d7',
        'done: rte', '    align 2',
        'adrtab: dc.l 0,$%08X,$C07E0000' % vscmd, 'dattab: dc.w 0,%d,$%04X' % (VNEW, DOT),
        'ktab: dc.b ' + ','.join(str(x) for x in ktab), 'ntab: dc.b ' + ','.join(str(x) for x in ntab), 'mtab: dc.b ' + ','.join(str(x) for x in mtab), '    align 2', '_spin: bra.s _spin']) + '\n'
    rom = bytearray(asm68k.build_rom(src, vectors={28: 'hint'}, title='VSH', size=scenefuzz.ROM_SIZE))
    rnd = random.Random(0x5EED)
    rom[scenefuzz.TABLE_AT:scenefuzz.TABLE_AT + 0x80000] = bytes(rnd.getrandbits(8) for _ in range(0x80000))
    open(out, 'wb').write(rom)
    print('wrote', out, 'delays (laps, nops):', dl[:3], '...', dl[-1])


def decode(shot, yoff=0, col=None):
    sys.path.insert(0, os.environ.get('PYTHONPATH', HERE))
    from pngio import read_png
    w, h, px = read_png(shot)
    res = []
    for k in range(NK):
        y = LINES[k]; got = ''
        for yy in range(y - 1, y + 4):
            tiles = vsprobe.decode_row([tuple(p[:3]) for p in px[yy + yoff][:320]])
            if col is None: r = [R for x, R in tiles]
            else: r = [R for x, R in tiles if 16 * col <= x < 16 * col + 16]
            got += 'N' if (yy + VNEW) % 256 in r else 'o' if yy % 256 in r else '?'
        # the dot: white pixels on the CRAM line (4 lines further down)
        dots = []
        for yy in (y + 4, y + 5, y + 3):
            row = px[yy + yoff][:320]
            dots = [x for x, p in enumerate(row) if min(p[:3]) >= 250]
            if dots: break
        res.append((k, y, got, dots[0] if dots else None, dots[-1] if dots else None))
    return res


if __name__ == '__main__':
    a = sys.argv[1:]
    def opt(name, default=None):
        return a[a.index(name) + 1] if name in a else default
    if a[0] == 'build':
        col = int(opt('--col')) if '--col' in a else None
        build(a[1], col, int(opt('--d0', 60)), int(opt('--step', 4)), h40='--h32' not in a)
    elif a[0] == 'decode':
        col = int(opt('--col')) if '--col' in a else None
        for k, y, got, d0, d1 in decode(a[1], int(opt('--yoff', 0)), col): print('k %2d line %3d (lines y-1..y+3): %s  dot x %s-%s' % (k, y, got, d0, d1))
