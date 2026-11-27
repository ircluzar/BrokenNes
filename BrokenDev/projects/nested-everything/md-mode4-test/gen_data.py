# Generates data.inc for md_mode4_test.s: an 8x8 font (ASCII 32-95) plus solid colour tiles and a ball sprite,
# in Mode 5 format (4bpp packed, one nybble per pixel) and in Mode 4 format (4 bit planes, one byte per plane per row),
# and a second copy of the Mode 4 font in the other byte order (the byte-order probe line on screen 2).
# Usage: python gen_data.py <out.inc>      Also: python gen_data.py --checksum <rom.bin>  (writes the header checksum)
import sys

G = {
' ':"", '!':"..#..|..#..|..#..|..#..|.....|..#..",
'(':"...#.|..#..|..#..|..#..|..#..|...#.", ')':".#...|..#..|..#..|..#..|..#..|.#...",
'+':".....|..#..|..#..|#####|..#..|..#..", ',':".....|.....|.....|.....|..#..|.#...",
'-':".....|.....|.....|#####|.....|.....", '.':".....|.....|.....|.....|.....|..#..",
'/':"....#|...#.|..#..|.#...|#....|.....",
'0':".###.|#...#|#..##|#.#.#|##..#|#...#|.###.", '1':"..#..|.##..|..#..|..#..|..#..|..#..|.###.",
'2':".###.|#...#|....#|..##.|.#...|#....|#####", '3':"####.|....#|....#|.###.|....#|....#|####.",
'4':"...#.|..##.|.#.#.|#..#.|#####|...#.|...#.", '5':"#####|#....|####.|....#|....#|#...#|.###.",
'6':".###.|#....|#....|####.|#...#|#...#|.###.", '7':"#####|....#|...#.|..#..|.#...|.#...|.#...",
'8':".###.|#...#|#...#|.###.|#...#|#...#|.###.", '9':".###.|#...#|#...#|.####|....#|....#|.###.",
':':".....|..#..|.....|.....|..#..|.....", '=':".....|#####|.....|#####|.....|.....",
'>':".#...|..#..|...#.|..#..|.#...|.....", '?':".###.|#...#|...#.|..#..|..#..|.....|..#..",
'A':".###.|#...#|#...#|#####|#...#|#...#|#...#", 'B':"####.|#...#|#...#|####.|#...#|#...#|####.",
'C':".###.|#...#|#....|#....|#....|#...#|.###.", 'D':"####.|#...#|#...#|#...#|#...#|#...#|####.",
'E':"#####|#....|#....|####.|#....|#....|#####", 'F':"#####|#....|#....|####.|#....|#....|#....",
'G':".###.|#...#|#....|#.###|#...#|#...#|.####", 'H':"#...#|#...#|#...#|#####|#...#|#...#|#...#",
'I':".###.|..#..|..#..|..#..|..#..|..#..|.###.", 'J':"..###|...#.|...#.|...#.|...#.|#..#.|.##..",
'K':"#...#|#..#.|#.#..|##...|#.#..|#..#.|#...#", 'L':"#....|#....|#....|#....|#....|#....|#####",
'M':"#...#|##.##|#.#.#|#.#.#|#...#|#...#|#...#", 'N':"#...#|##..#|#.#.#|#..##|#...#|#...#|#...#",
'O':".###.|#...#|#...#|#...#|#...#|#...#|.###.", 'P':"####.|#...#|#...#|####.|#....|#....|#....",
'Q':".###.|#...#|#...#|#...#|#.#.#|#..#.|.##.#", 'R':"####.|#...#|#...#|####.|#.#..|#..#.|#...#",
'S':".####|#....|#....|.###.|....#|....#|####.", 'T':"#####|..#..|..#..|..#..|..#..|..#..|..#..",
'U':"#...#|#...#|#...#|#...#|#...#|#...#|.###.", 'V':"#...#|#...#|#...#|#...#|#...#|.#.#.|..#..",
'W':"#...#|#...#|#...#|#.#.#|#.#.#|##.##|#...#", 'X':"#...#|#...#|.#.#.|..#..|.#.#.|#...#|#...#",
'Y':"#...#|#...#|.#.#.|..#..|..#..|..#..|..#..", 'Z':"#####|....#|...#.|..#..|.#...|#....|#####",
}

def glyph(ch):
    rows = [[0] * 8 for _ in range(8)]
    s = G.get(ch, "")
    if s:
        for y, r in enumerate(s.split('|')):
            for x, c in enumerate(r):
                if c == '#': rows[y][x + 1] = 1
    return rows

BALL = ["..2222..", ".233332.", "23311332", "23311332", "23333332", "23333332", ".233332.", "..2222.."]

def tiles():
    t = [glyph(chr(c)) for c in range(32, 96)]                       # 0-63 font
    t += [[[c] * 8 for _ in range(8)] for c in range(16)]             # 64-79 solid colour c
    t.append([[0 if ch == '.' else int(ch) for ch in r] for r in BALL])  # 80 ball
    return t

def m5(tile):
    out = []
    for r in tile:
        for x in range(0, 8, 2): out.append((r[x] << 4) | r[x + 1])
    return out

def m4(tile):
    out = []
    for r in tile:
        for p in range(4): out.append(sum(((r[x] >> p) & 1) << (7 - x) for x in range(8)))
    return out

def dcb(name, data):
    lines = [f"{name}:"]
    for i in range(0, len(data), 16):
        lines.append("    dc.b " + ",".join(f"0x{b:02X}" for b in data[i:i + 16]))
    lines.append(f"{name}_end:")
    return "\n".join(lines)

def main():
    if sys.argv[1] == '--checksum':
        p = sys.argv[2]; b = bytearray(open(p, 'rb').read())
        b += bytes(max(0, 0x20000 - len(b)))   # pad to 128 KB
        s = sum((b[i] << 8) | b[i + 1] for i in range(0x200, len(b) - 1, 2)) & 0xFFFF
        b[0x18E] = s >> 8; b[0x18F] = s & 0xFF
        end = len(b) - 1
        b[0x1A4:0x1A8] = end.to_bytes(4, 'big')
        open(p, 'wb').write(b); return
    t = tiles()
    d5 = sum((m5(x) for x in t), [])
    d4 = sum((m4(x) for x in t), [])
    f4 = sum((m4(x) for x in t[:64]), [])
    # The 68000 writes Mode 4 VRAM a word at a time. Genesis Plus GX (little-endian hosts) puts the word's LOW byte at the
    # even SMS address, so tiles4 is stored pair-swapped (a move.w of ROM bytes hi,lo lands as lo,hi). font4alt is the
    # other order (high byte at the even address), drawn by the byte-order probe line.
    def swap(d):
        r = []
        for i in range(0, len(d), 2): r += [d[i + 1], d[i]]
        return r
    d4 = swap(d4)
    f4s = f4
    with open(sys.argv[1], 'w', newline='\n') as f:
        f.write("| generated by gen_data.py - do not edit\n    .even\n")
        f.write(dcb("tiles5", d5) + "\n    .even\n")
        f.write(dcb("tiles4", d4) + "\n    .even\n")
        f.write(dcb("font4alt", f4s) + "\n    .even\n")

main()
