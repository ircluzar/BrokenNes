"""Master System probe programs for the VDP's data port during the picture (A4), to be run in this emulator and in Mesen 2.1.1 and compared.

  python vram-probe.py <outdir> --line 50 --count 41 --kind otir|outi_loop|out_pairs --mode vram|row

Each program sets up Mode 4 with the display on, waits for the V counter to reach --line, runs a filler of <phase> cycles (phase = 0 .. count-1: the write sequence then starts one Z80
cycle later each time, so it meets the beam at every phase), and writes 64 bytes through the data port:
  --mode vram  64 distinct bytes ($01 .. $40) to VRAM $0800 ..: after frame 4 the bytes that arrived tell which writes the chip accepted (a lost byte shifts all the later ones down by one: the address register does not advance);
  --mode row   32 name table entries (tile 1, a red tile) to row 6 of the name table: the picture shows on which line each entry took effect (the red staircase).
  --kind       otir (OTIR: a write every 21 cycles), outi_loop (64 x OUTI: every 16), out_pairs (LD A,(HL); OUT ($BE),A; INC HL: every 24, a typical game loop).
The programs are 16 KB with no data of anyone's: written by this script, nothing to keep in a repository but the script.
"""
import argparse
import os

ap = argparse.ArgumentParser()
ap.add_argument('outdir')
ap.add_argument('--line', type=int, default=50)
ap.add_argument('--count', type=int, default=41)
ap.add_argument('--kind', default='otir', choices=['otir', 'outi_loop', 'out_pairs'])
ap.add_argument('--mode', default='vram', choices=['vram', 'row'])
ap.add_argument('--row', type=int, default=6)
a = ap.parse_args()
os.makedirs(a.outdir, exist_ok=True)


def vdp_reg(reg, val):
    return [0x3E, val, 0xD3, 0xBF, 0x3E, 0x80 | reg, 0xD3, 0xBF]


def filler(cycles):
    """Instructions that take <cycles> Z80 cycles where the instruction set allows (NOP 4, INC HL 6, LD A,I 9, PUSH AF 11); 1, 2, 3, 5 and 7 cannot be made."""
    code = []
    f = cycles
    for cost, ins in ((11, [0xF5]), (9, [0xED, 0x57]), (6, [0x23]), (4, [0x00])):
        while f >= cost and (cost == 4 and f - cost not in (1, 2, 3) or cost != 4 and f - cost not in (1, 2, 3, 5, 7)):
            code += ins
            f -= cost
    return code


def build(phase):
    b = bytearray(0x4000)
    b[0:3] = bytes([0xC3, 0x00, 0x01])
    c = [0xF3, 0xED, 0x56, 0x31, 0xF0, 0xDF]                              # DI; IM 1; LD SP,$DFF0
    for reg, val in ((0, 0x04), (1, 0x80), (2, 0xFF), (3, 0xFF), (4, 0xFF), (5, 0xFF), (6, 0xFB), (7, 0x00), (8, 0), (9, 0), (10, 0xFF)):
        c += vdp_reg(reg, val)
    c += [0x3E, 0x00, 0xD3, 0xBF, 0x3E, 0xC0, 0xD3, 0xBF]                  # colour RAM: black, green, red, blue
    for v in (0x00, 0x0C, 0x03, 0x30):
        c += [0x3E, v, 0xD3, 0xBE]
    c += [0x3E, 0x00, 0xD3, 0xBF, 0x3E, 0x40, 0xD3, 0xBF]                  # tile 0 (colour 1) and tile 1 (colour 2) at VRAM 0
    for tile in ((0xFF, 0, 0, 0), (0, 0xFF, 0, 0)):
        for _ in range(8):
            for v in tile:
                c += [0x3E, v, 0xD3, 0xBE]
    c += [0x3E, 0x00, 0xD3, 0xBF, 0x3E, 0x78, 0xD3, 0xBF]                  # the name table: all tile 0
    c += [0x01, 0x00, 0x06, 0xAF, 0xD3, 0xBE, 0x0B, 0x78, 0xB1, 0x20, 0xF8]
    c += [0x3E, 0x00, 0xD3, 0xBF, 0x3E, 0x7F, 0xD3, 0xBF, 0x3E, 0xD0, 0xD3, 0xBE]   # the sprite table ends at once
    c += vdp_reg(1, 0xC0)                                                  # display on
    c += [0xDB, 0x7E, 0xFE, a.line, 0x20, 0xFA]                            # wait for the line by the V counter
    c += filler(phase)
    addr = 0x0800 if a.mode == 'vram' else 0x3800 + a.row * 64
    c += [0x3E, addr & 0xFF, 0xD3, 0xBF, 0x3E, 0x40 | (addr >> 8), 0xD3, 0xBF]
    if a.kind == 'otir':
        c += [0x21, 0x00, 0x10, 0x06, 0x40, 0x0E, 0xBE, 0xED, 0xB3]
    elif a.kind == 'outi_loop':
        c += [0x21, 0x00, 0x10, 0x06, 0x40, 0x0E, 0xBE] + [0xED, 0xA3] * 64
    else:
        c += [0x21, 0x00, 0x10] + [0x7E, 0xD3, 0xBE, 0x23] * 64
    c += [0x18, 0xFE]
    assert 0x100 + len(c) < 0x1000
    b[0x100:0x100 + len(c)] = bytes(c)
    if a.mode == 'vram':
        for i in range(64):
            b[0x1000 + i] = i + 1
    else:
        for i in range(32):
            b[0x1000 + 2 * i] = 0x01
    return bytes(b)


for ph in range(a.count):
    open(os.path.join(a.outdir, 'probe_%s_%s_l%d_p%02d.sms' % (a.mode, a.kind, a.line, ph)), 'wb').write(build(ph))
print(a.count, 'probes in', a.outdir)
