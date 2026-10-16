"""satline.py vdpdir row [h40] [dbl]: the sprites of the table (link order from sprite 0) that touch the image row, with x, size, flips and the running cell count; and which cover the listed x"""
import sys, os
d = sys.argv[1]; row = int(sys.argv[2]); h40 = len(sys.argv) > 3 and sys.argv[3] == '1'; dbl = len(sys.argv) > 4 and sys.argv[4] == '1'
vram = open(os.path.join(d, 'vram.bin'), 'rb').read(); regs = open(os.path.join(d, 'regs.bin'), 'rb').read()
sat = (regs[5] & (0x7E if h40 else 0x7F)) << 9
print('sat at %04X reg5=%02X' % (sat, regs[5]))
idx = 0; n = 0; cells = 0; shift = 4 if dbl else 3
maxn = 80 if h40 else 64
out = []
for n in range(maxn):
    a = sat + idx * 8
    y = (vram[a] << 8 | vram[a + 1]) & (0x3FF if dbl else 0x1FF)
    size = vram[a + 2]; link = vram[a + 3] & 0x7F
    attr = vram[a + 4] << 8 | vram[a + 5]; x = (vram[a + 6] << 8 | vram[a + 7]) & 0x1FF
    w = (size >> 2 & 3) + 1; h = (size & 3) + 1
    top = y - (128 << (1 if dbl else 0))
    on = top <= row < top + (h << shift)
    out.append((n, idx, y, x, w, h, attr, link, on))
    idx = link
    if idx == 0 or idx >= maxn: break
cum = 0
for (n, idx, y, x, w, h, attr, link, on) in out:
    if on:
        cum += w
        print('order %2d sprite %2d y=%3d x=%3d (screen %4d..%4d) w=%d h=%d tile=%03X pal=%d pri=%d vf=%d hf=%d link=%d  cum cells %d' % (n, idx, y, x, x - 128, x - 128 + w * 8 - 1, w, h, attr & 0x7FF, attr >> 13 & 3, attr >> 15, attr >> 12 & 1, attr >> 11 & 1, link, cum))
