# Generates the data of the WideMess "8 layers" test ROM (layers.asm):
#   palette.bin  1,024 colours (4 banks of 256), BGR555 little-endian: what WMCGDATA takes, entry 0 first
#   tiles.bin    33 tiles in the SNES 8-bpp planar format (64 bytes each): tile 0 empty, then 4 tiles per layer whose 256 pixels
#                use colour indices 1-255, so each layer shows its whole bank of 256 colours
# Usage: python -I gen_data.py <out_dir>
import colorsys, os, sys

out = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))

def bgr555(r, g, b):
    q = lambda v: max(0, min(31, int(round(v * 31))))
    return q(r) | q(g) << 5 | q(b) << 10

# Every one of the 1,024 entries is a different 15-bit colour (smooth gradients would round neighbouring entries to the same colour at 5 bits a channel, and
# the test counts colours): red = entry mod 32, green = entry / 32, blue mixes the two. Each bank is a band of 8 green levels.
pal = []
for i in range(1024):
    r, g = i & 31, (i >> 5) & 31
    pal.append(r | g << 5 | (((r >> 1) + (31 - g)) & 31) << 10)
pal[0] = bgr555(0.03, 0.03, 0.10)   # the backdrop (colour 0 of bank 0, never drawn by a tile): a dark navy
with open(os.path.join(out, 'palette.bin'), 'wb') as f:
    for c in pal: f.write(bytes((c & 0xFF, c >> 8)))

def tile_bytes(px):   # px[y][x] = 0-255 -> SNES 8-bpp planar: bit-plane pairs (0,1) (2,3) (4,5) (6,7), 16 bytes apart
    t = bytearray(64)
    for pair in range(4):
        for y in range(8):
            lo = hi = 0
            for x in range(8):
                v = px[y][x]
                lo |= ((v >> (pair * 2)) & 1) << (7 - x)
                hi |= ((v >> (pair * 2 + 1)) & 1) << (7 - x)
            t[pair * 16 + y * 2] = lo
            t[pair * 16 + y * 2 + 1] = hi
    return bytes(t)

tiles = [tile_bytes([[0] * 8 for _ in range(8)])]
for layer in range(8):
    for t in range(4):
        tiles.append(tile_bytes([[1 + ((t * 64 + y * 8 + x) % 255) for x in range(8)] for y in range(8)]))
with open(os.path.join(out, 'tiles.bin'), 'wb') as f:
    for t in tiles: f.write(t)
print('palette.bin', len(pal) * 2, 'bytes; tiles.bin', len(tiles) * 64, 'bytes')
