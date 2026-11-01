"""satall.py vdpdir [dbl]: the whole sprite table (80 entries), each with the rows it covers, in link order and by index"""
import sys, os
d = sys.argv[1]; dbl = len(sys.argv) > 2 and sys.argv[2] == '1'
vram = open(os.path.join(d, 'vram.bin'), 'rb').read(); regs = open(os.path.join(d, 'regs.bin'), 'rb').read()
sat = (regs[5] & 0x7F) << 9
print('sat %04X' % sat)
for i in range(80):
    a = sat + i * 8
    y = (vram[a] << 8 | vram[a + 1]); size = vram[a + 2]; link = vram[a + 3] & 0x7F
    attr = vram[a + 4] << 8 | vram[a + 5]; x = (vram[a + 6] << 8 | vram[a + 7])
    w = (size >> 2 & 3) + 1; h = (size & 3) + 1
    top = (y & 0x3FF) - 256
    mark = ''
    if top <= 106 < top + h * 16 or top <= 107 < top + h * 16: mark = ' <-- row 106/107'
    tiles = (attr & 0x7FF, (attr & 0x7FF) + w * h - 1)
    print('%2d: y=%04X(%3d) top=%4d..%4d x=%04X(%3d) w=%d h=%d link=%2d tile %03X..%03X pal=%d pri=%d%s' % (i, y, y & 0x3FF, top, top + h * 16 - 1, x, (x & 0x1FF) - 128, w, h, link, tiles[0], tiles[1], attr >> 13 & 3, attr >> 15, mark))
