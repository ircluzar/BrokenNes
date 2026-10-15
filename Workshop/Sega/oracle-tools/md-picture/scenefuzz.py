"""Static-scene fuzz ROMs for the Genesis renderer (Track B4a).

A scene is a complete VDP state (random VRAM, random colour RAM, random vertical scroll RAM, a structured sprite table and horizontal scroll table, a register set) that the ROM loads with the display off and
then shows until Start is pressed, then the next scene. The same ROM runs on this emulator and on BlastEm; BlastEm's screenshot of each scene is compared with our picture (Workshop --md-scenes). Because the scene is
static, nothing depends on timing: every difference is a rendering rule (plane fetch, scroll, window, priority, shadow/highlight, sprite selection and masking, interlace ...).

    python scenefuzz.py out.bin --scenes 60 --seed 1 [--family all|interlace2|...] [--list scenes.json]

The ROM: random VRAM comes from a 512 KB table in the ROM by DMA (64 KB aligned windows), everything else from immediate data in the code, so every run on every emulator sees the same bytes.
The scene list (registers, features) is written next to the ROM as JSON so a difference can be traced to its parameters.
"""
import argparse, json, os, random, struct, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import asm68k

TABLE_AT = 0x80000          # 512 KB of random bytes, 64 KB windows
ROM_SIZE = 0x100000


def hex2(v): return '$%04X' % (v & 0xFFFF)


def vram_cmd(addr):
    return 0x40000000 | (addr & 0x3FFF) << 16 | addr >> 14 & 3


def make_scene(rng, idx, family='all'):
    """The parameters of one scene as a dict."""
    s = {'index': idx, 'family': family}
    fam = family
    ISO = ('sizes', 'shnw', 'winnw')            # families that isolate one feature: H40, V28, no interlace, full scroll
    h40 = rng.random() < 0.8
    if fam in ('h32',): h40 = False
    VS = ('vsc', 'vscw')                         # column vertical scroll, with and without the window; H32 and H40
    if fam in ISO: h40 = True
    v30 = rng.random() < 0.15
    if fam in ('v30',): v30 = True
    if fam in ISO or fam in VS: v30 = False
    il = rng.choice([0, 0, 0, 1, 3])
    if fam == 'interlace2': il = 3
    elif fam == 'interlace1': il = 1
    elif fam == 'plain' or fam in ISO or fam in VS: il = 0
    shadow = rng.random() < 0.3 or fam == 'shadow'
    if fam in ISO: shadow = fam == 'shnw'
    if fam in VS: shadow = False
    s.update(h40=h40, v30=v30, interlace=il, shadow=shadow)
    ws = rng.choice([0, 1, 3]); hs = rng.choice([0, 1, 3])
    if fam == 'small': ws, hs = 0, 0
    if fam in ('shnw', 'winnw') or fam in VS: ws, hs = rng.choice([0, 1]), rng.choice([0, 1])
    hmode = rng.choice([0, 2, 3, 3]); vmode = rng.choice([0, 1])
    if fam == 'vscroll': vmode = 1
    if fam in ISO: hmode, vmode = 0, 0
    if fam in VS: hmode, vmode = rng.choice([0, 2, 3]), 1
    win = rng.random() < 0.5 or fam == 'window'
    if fam in ISO: win = fam == 'winnw'
    if fam in VS: win = fam == 'vscw'
    r17 = (rng.choice([0, 0x80]) | rng.randrange(0, 21 if h40 else 17)) if win else 0
    r18 = (rng.choice([0, 0x80]) | rng.randrange(0, 29 if not v30 else 31)) if win else 0
    regs = {
        0: 0x04 | (0x20 if rng.random() < 0.15 else 0),
        2: rng.randrange(8) << 3,
        3: rng.randrange(32) << 1,
        4: rng.randrange(8),
        5: rng.randrange(0x80),
        7: rng.randrange(64),
        10: 0xFF,
        11: hmode | vmode << 2,
        12: (0x81 if h40 else 0x00) | il << 1 | (8 if shadow else 0),
        13: rng.randrange(64),
        15: 2,
        16: ws | hs << 4,
        17: r17, 18: r18,
    }
    s['regs'] = regs
    s['vsram'] = [rng.randrange(0, 0x7FF) if rng.random() < 0.8 else rng.randrange(0, 40) for _ in range(40)]
    if vmode == 0 and rng.random() < 0.5: s['vsram'][0] = s['vsram'][1] = rng.randrange(0, 600)
    s['cram'] = [rng.randrange(0, 0x1000) & 0x0EEE for _ in range(64)]
    if rng.random() < 0.4:      # a few shared colours so that priority and shadow shows (and a plain palette 0)
        for i in range(0, 64, 16): s['cram'][i] = 0
    # sprite table: a chain of n sprites
    maxs = 80 if h40 else 64
    n = rng.choice([4, 12, 24, 40, maxs]) if fam != 'sprites' else maxs
    dbl = il == 3
    sat = bytearray(8 * maxs)
    order = list(range(n))
    chain = order[:]
    for k in range(n):
        i = chain[k]
        link = chain[k + 1] if k + 1 < n else 0
        h = rng.randrange(4); w = rng.randrange(4)
        ymax = (224 if not v30 else 240)
        yscr = rng.randrange(-24, ymax + 8)
        ypos = (yscr + (256 if dbl else 128)) & 0x3FF if not dbl else (yscr * 2 + 256) & 0x3FF
        xscr = rng.choice([rng.randrange(-24, 330), 0, 0, rng.randrange(0, 8)]) if rng.random() < 0.97 else 0
        xpos = (xscr + 128) & 0x1FF
        if rng.random() < 0.08: xpos = 0                      # an X=0 sprite (masking)
        attr = rng.randrange(0x10000)
        base = i * 8
        struct.pack_into('>HBBHH', sat, base, ypos, w << 2 | h, link, attr, xpos)
    s['sat'] = bytes(sat)
    s['sat_addr'] = (regs[5] & (0x7E if h40 else 0x7F)) << 9
    hs_tbl = bytearray()
    for _ in range(240 if hmode == 3 else (32 if hmode == 2 else 1)):
        hs_tbl += struct.pack('>HH', rng.randrange(0, 1024) if rng.random() < 0.7 else rng.randrange(0, 16), rng.randrange(0, 1024) if rng.random() < 0.7 else rng.randrange(0, 16))
    s['hscroll'] = bytes(hs_tbl)
    s['hs_addr'] = (regs[13] & 0x3F) << 10
    s['window'] = [r17, r18]
    s['window_on'] = win
    s['dma_window'] = idx % 8
    return s


def scene_asm(s, i, press):
    L = []
    a = L.append
    h40 = s['h40']; v30 = s['v30']
    r1_off = 0x14 | (0x08 if v30 else 0)           # mode 5, DMA enable, display off
    r1_on = 0x54 | (0x08 if v30 else 0)
    a('scene%d:' % i)
    a('    move.w #$%04X,(a4)' % (0x8100 | r1_off))
    # registers first (the sprite attribute table address among them: the VDP's sprite cache follows writes to the table the register names), then the memories
    for r, v in sorted(s['regs'].items()): a('    move.w #$%04X,(a4)' % (0x8000 | r << 8 | v))
    # the 64 KB of VRAM by DMA from the table window
    src = TABLE_AT + s['dma_window'] * 0x10000
    for v in (0x9300 | 0x00, 0x9400 | 0x80, 0x9500 | (src >> 1) & 0xFF, 0x9600 | (src >> 9) & 0xFF, 0x9700 | (src >> 17) & 0x7F, 0x8F02):
        a('    move.w #$%04X,(a4)' % v)
    a('    move.l #$40000080,(a4)')
    # CRAM
    a('    move.l #$C0000000,(a4)')
    for c in s['cram']: a('    move.w #$%04X,(a3)' % c)
    # VSRAM
    a('    move.l #$40000010,(a4)')
    for v in s['vsram']: a('    move.w #$%04X,(a3)' % v)
    # sprite table and hscroll table, word by word
    a('    move.l #$%08X,(a4)' % vram_cmd(s['sat_addr']))
    sat = s['sat']
    for k in range(0, len(sat), 2): a('    move.w #$%04X,(a3)' % (sat[k] << 8 | sat[k + 1]))
    a('    move.l #$%08X,(a4)' % vram_cmd(s['hs_addr']))
    hs = s['hscroll']
    for k in range(0, len(hs), 2): a('    move.w #$%04X,(a3)' % (hs[k] << 8 | hs[k + 1]))
    # extra controlled writes (a probe scene's patterns and tables over the random memory): (VRAM address, [words])
    for addr, words in s.get('extra_writes', []):
        a('    move.l #$%08X,(a4)' % vram_cmd(addr))
        for w in words: a('    move.w #$%04X,(a3)' % (w & 0xFFFF))
    # display on
    a('    move.w #$%04X,(a4)' % (0x8100 | r1_on))
    # wait for Start (pressed, then released)
    a('wt%d:' % i)
    a('    move.b #$00,($A10003).l')
    a('    nop\n    nop')
    a('    move.b ($A10003).l,d0')
    a('    btst #5,d0')
    a('    bne.s wt%d' % i)
    a('wr%d:' % i)
    a('    move.b #$00,($A10003).l')
    a('    nop\n    nop')
    a('    move.b ($A10003).l,d0')
    a('    btst #5,d0')
    a('    beq.s wr%d' % i)
    return '\n'.join(L) + '\n'


def build(scenes, out):
    src = ['    move.b #$40,($A10009).l', '    lea ($C00004).l,a4', '    lea ($C00000).l,a3']
    body = ''.join(scene_asm(s, i, None) for i, s in enumerate(scenes))
    body += 'done: bra.s done\n'
    src = '\n'.join(src) + '\n' + body + '_spin: bra.s _spin\n'
    # the random table at TABLE_AT
    rom = bytearray(asm68k.build_rom(src, title='SCENE FUZZ', size=ROM_SIZE))
    rnd = random.Random(0x5EED)
    rom[TABLE_AT:TABLE_AT + 0x80000] = bytes(rnd.getrandbits(8) for _ in range(0x80000))
    codelen = len(asm68k.Asm68k(0x200).assemble(src)) + 0x200
    assert codelen < TABLE_AT, 'code too big: %X' % codelen
    open(out, 'wb').write(rom)
    return len(rom)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('out')
    ap.add_argument('--scenes', type=int, default=40)
    ap.add_argument('--seed', type=int, default=1)
    ap.add_argument('--family', default='all')
    ap.add_argument('--start-index', type=int, default=0)
    a = ap.parse_args()
    rng = random.Random(a.seed)
    scenes = [make_scene(rng, a.start_index + i, a.family) for i in range(a.scenes)]
    n = build(scenes, a.out)
    meta = []
    for s in scenes:
        m = {k: v for k, v in s.items() if k not in ('sat', 'hscroll', 'cram', 'vsram')}
        meta.append(m)
    json.dump(meta, open(os.path.splitext(a.out)[0] + '.json', 'w'), indent=1)
    print('wrote %s (%d bytes, %d scenes)' % (a.out, n, len(scenes)))


if __name__ == '__main__':
    main()
