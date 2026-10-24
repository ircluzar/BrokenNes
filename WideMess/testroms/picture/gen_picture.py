# The WideMess picture test ROMs (milestone 2: docs/projects/widemess-spec.md sections 3.1-3.4) and the pictures they must draw.
#
# Each scene below is a list of register writes and DMA / HDMA transfers. The generator turns it into the script the 65816 player
# (player.asm) performs, and feeds the same writes to MODEL, a second implementation of the spec written here from the spec's text, which
# draws the picture the ROM must produce. Workshop --wms-test compares BrokenNes's picture with it, pixel for pixel.
#
#   sprites    the WideMess mode at 400: the 256-sprite table (sizes 8-64, 4 and 8 bpp, flips, X and Y past every edge, table order, the
#              sprite slots between the layers, 64 sprites and 128 slivers a line); the SNES table in the WideMess mode (variant)
#   windows    the WideMess mode at 400: two windows at 9-bit positions (one shaped line by line by HDMA), the logic of BG1-BG8, main and
#              sub screen masks, colour math (add / subtract / half, the sub screen, the fixed colour, clip to black, the math region,
#              the per-sprite math bit); the SNES's positions at 400 (variant)
#   widesnes   SNES modes 1, 0, 5 (hi-res), 2 (offset-per-tile, mosaic) and 7 drawn 400 wide, in bands set by HDMA on BGMODE: 64-wide
#              and 32-wide maps past the SNES's 256 columns, offset-per-tile left of the SNES edge, Mode 7 continued, the SNES sprite table
#              across 400 (X from -184 to 327), the SNES windows at 400; 9-bit windows and the WideMess sprite table in a SNES mode (variants)
# Every scene is built twice: as the ROM (it unlocks) and as its negative control, the same ROM built with -D NOUNLOCK, whose WideMess
# writes must all be ignored (the model draws that too).
#
# Usage: python -I gen_picture.py <work_dir> <bin_dir>
#   work_dir/<rom>/  script.bin, data.bin, layout_head.inc, layout_data.inc   (player.asm is assembled there by build.ps1)
#   bin_dir/         <rom>.expect.png, <rom>_nounlock.expect.png, <rom>.<variant>.expect.png
import copy, math, os, struct, sys, zlib

WORK = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), 'work')
BIN = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'bin')

# ------------------------------------------------------------------ colours

# 1,024 different 15-bit colours (the scheme of testroms/layers): red = entry mod 32, green = entry / 32, blue mixes them; entry 0 is a dark navy.
PALETTE = []
for i in range(1024):
    r, g = i & 31, (i >> 5) & 31
    PALETTE.append(r | g << 5 | (((r >> 1) + (31 - g)) & 31) << 10)
PALETTE[0] = 1 | 1 << 5 | 3 << 10

def argb(c):          # BGR555 at full brightness -> 0xAARRGGBB (the integer scaling of the SNES chip's colour table)
    r, g, b = c & 31, (c >> 5) & 31, (c >> 10) & 31
    return 0xFF000000 | (r * 255 // 31) << 16 | (g * 255 // 31) << 8 | (b * 255 // 31)

BLACK = 0xFF000000

def blend(a, b, subtract, half):
    out = 0
    for sh in (0, 5, 10):
        x, y = (a >> sh) & 31, (b >> sh) & 31
        if not subtract:
            v = x + y
            v = v >> 1 if half else min(v, 31)
        else:
            v = max(x - y, 0)
            v = v >> 1 if half else v
        out |= v << sh
    return out

def signed(v, bits):
    v &= (1 << bits) - 1
    return v - (1 << bits) if v & (1 << (bits - 1)) else v

# ------------------------------------------------------------------ tiles

def tile_bytes(px, bpp):
    """px[y][x] -> SNES planar tile: bit-plane pairs (0,1) (2,3) (4,5) (6,7), 16 bytes apart, two bytes per row."""
    t = bytearray(8 * bpp)
    for pair in range(bpp // 2):
        for y in range(8):
            lo = hi = 0
            for x in range(8):
                v = px[y][x]
                lo |= ((v >> (pair * 2)) & 1) << (7 - x)
                hi |= ((v >> (pair * 2 + 1)) & 1) << (7 - x)
            t[pair * 16 + y * 2] = lo
            t[pair * 16 + y * 2 + 1] = hi
    return bytes(t)

def solid(c, bpp):
    return tile_bytes([[c] * 8 for _ in range(8)], bpp)

def words(ws):
    return b''.join(struct.pack('<H', w & 0xFFFF) for w in ws)

# ------------------------------------------------------------------ the model of the spec

VRAM = 512 * 1024
SMALL = [(8, 8), (8, 8), (8, 8), (16, 16), (16, 16), (32, 32), (16, 32), (16, 32)]
LARGE = [(16, 16), (32, 32), (64, 64), (32, 32), (64, 64), (64, 64), (32, 64), (32, 32)]
# The SNES's front-to-back orders (fullsnes), as ('obj', priority) and ('bg', layer, priority).
ORDER = {
    0: [('obj', 3), ('bg', 0, 1), ('bg', 1, 1), ('obj', 2), ('bg', 0, 0), ('bg', 1, 0), ('obj', 1), ('bg', 2, 1), ('bg', 3, 1), ('obj', 0), ('bg', 2, 0), ('bg', 3, 0)],
    1: [('obj', 3), ('bg', 0, 1), ('bg', 1, 1), ('obj', 2), ('bg', 0, 0), ('bg', 1, 0), ('obj', 1), ('bg', 2, 1), ('obj', 0), ('bg', 2, 0)],
    2: [('obj', 3), ('bg', 0, 1), ('obj', 2), ('bg', 1, 1), ('obj', 1), ('bg', 0, 0), ('obj', 0), ('bg', 1, 0)],
    5: [('obj', 3), ('bg', 0, 1), ('obj', 2), ('bg', 1, 1), ('obj', 1), ('bg', 0, 0), ('obj', 0), ('bg', 1, 0)],
    7: [('obj', 3), ('obj', 2), ('obj', 1), ('bg', 0, 0), ('obj', 0)],
}
BPP = {0: [2, 2, 2, 2], 1: [4, 4, 2, 0], 2: [4, 4, 0, 0], 5: [4, 2, 0, 0], 7: [8, 0, 0, 0]}
DMA_PATTERN = {0: [0], 1: [0, 1], 2: [0, 0], 3: [0, 0, 1, 1], 4: [0, 1, 2, 3]}

class Model:
    def __init__(self):
        self.vram = bytearray(VRAM)
        self.cg = [0] * 1024
        self.oam = bytearray(544)
        self.woam = bytearray(2048)
        self.unlocked = False
        self.reg = [0] * 0x40
        self.reg[0] = 0x80
        self.hofs = [0] * 4; self.vofs = [0] * 4; self.sl = 0; self.hl = 0; self.m7l = 0
        self.m7 = {'a': 0, 'b': 0, 'c': 0, 'd': 0, 'x': 0, 'y': 0, 'h': 0, 'v': 0}
        self.vmain = 0; self.vmadd = 0
        self.cgadd = 0; self.cghi = False; self.cglat = 0
        self.oamadd = 0; self.oamint = 0; self.oamlat = 0
        self.coldata = 0
        # WideMess
        self.ctrl = 0; self.wmtm = 0; self.vaddr = 0; self.wcg = 0; self.wcgph = False; self.wcglo = 0
        self.mapb = [0] * 8; self.cfg = [0] * 8; self.sh = [0] * 8; self.sv = [0] * 8
        self.sph = [False] * 16; self.slo = [0] * 16
        self.objctrl = 0; self.objbase = 0; self.oaddr = 0; self.objslot = [12, 8, 4, 0]
        self.wpos = [0] * 4; self.wph = [False] * 4; self.wlo = [0] * 4; self.wctl = 0
        self.wmts = self.wmtmw = self.wmtsw = self.wmcmath = self.wlog14 = self.wlog58 = 0
        self.wsel = [0] * 4
        self.hdma = []          # (register, mode, [(rows, bytes)])
        self.tile_cache = {}

    # ---- writes
    def write_b(self, r, v):
        if r < 0x40: self.snes_write(r, v)
        elif 0xA0 <= r < 0xF0 and self.unlocked: self.wm_write(r, v)

    def vram_word_inc(self):
        self.vmadd = (self.vmadd + {0: 1, 1: 32, 2: 128, 3: 128}[self.vmain & 3]) & 0xFFFF

    def snes_write(self, r, v):
        if r == 0x02: self.oamadd = (self.oamadd & 0x100) | v; self.oamint = (self.oamadd & 0x1FF) << 1
        elif r == 0x03: self.oamadd = (self.oamadd & 0xFF) | (v & 1) << 8 | (v & 0x80) << 8; self.oamint = (self.oamadd & 0x1FF) << 1
        elif r == 0x04:
            if self.oamint < 0x200:
                if self.oamint & 1 == 0: self.oamlat = v
                else: self.oam[self.oamint - 1] = self.oamlat; self.oam[self.oamint] = v
            else: self.oam[0x200 + (self.oamint & 0x1F)] = v
            self.oamint = (self.oamint + 1) & 0x3FF
        elif r in (0x0D, 0x0F, 0x11, 0x13):
            if r == 0x0D: self.m7['h'] = signed((v << 8) | self.m7l, 13); self.m7l = v
            bg = (r - 0x0D) >> 1
            self.hofs[bg] = ((v << 8) | (self.sl & ~7) | (self.hl & 7)) & 0x3FF
            self.sl = v; self.hl = v
        elif r in (0x0E, 0x10, 0x12, 0x14):
            if r == 0x0E: self.m7['v'] = signed((v << 8) | self.m7l, 13); self.m7l = v
            bg = (r - 0x0E) >> 1
            self.vofs[bg] = ((v << 8) | self.sl) & 0x3FF
            self.sl = v
        elif r == 0x15: self.vmain = v; assert (v >> 2) & 3 == 0, 'the model does not remap VRAM addresses'
        elif r == 0x16: self.vmadd = (self.vmadd & 0xFF00) | v
        elif r == 0x17: self.vmadd = (self.vmadd & 0x00FF) | v << 8
        elif r == 0x18:
            self.vram[(self.vmadd & 0x7FFF) * 2] = v
            if not self.vmain & 0x80: self.vram_word_inc()
        elif r == 0x19:
            self.vram[(self.vmadd & 0x7FFF) * 2 + 1] = v
            if self.vmain & 0x80: self.vram_word_inc()
        elif 0x1B <= r <= 0x1E: self.m7['abcd'[r - 0x1B]] = signed(v << 8 | self.m7l, 16); self.m7l = v
        elif r in (0x1F, 0x20): self.m7['xy'[r - 0x1F]] = signed((v << 8) | self.m7l, 13); self.m7l = v
        elif r == 0x21: self.cgadd = v; self.cghi = False
        elif r == 0x22:
            if not self.cghi: self.cglat = v
            else: self.cg[self.cgadd] = ((v & 0x7F) << 8) | self.cglat; self.cgadd = (self.cgadd + 1) & 0xFF
            self.cghi = not self.cghi
        elif r == 0x32:
            if v & 0x20: self.coldata = (self.coldata & ~0x001F) | (v & 0x1F)
            if v & 0x40: self.coldata = (self.coldata & ~0x03E0) | (v & 0x1F) << 5
            if v & 0x80: self.coldata = (self.coldata & ~0x7C00) | (v & 0x1F) << 10
        self.reg[r] = v

    def wm_write(self, r, v):
        if r == 0xA0: self.objctrl = v & 1
        elif r == 0xA1: self.objbase = v & 7
        elif r == 0xA2: self.oaddr = (self.oaddr & 0x700) | v
        elif r == 0xA3: self.oaddr = (self.oaddr & 0xFF) | (v & 7) << 8
        elif r == 0xA4: self.woam[self.oaddr] = v; self.oaddr = (self.oaddr + 1) & 0x7FF
        elif 0xA6 <= r <= 0xA9: self.objslot[r - 0xA6] = v
        elif 0xB0 <= r <= 0xB3:
            i = r - 0xB0
            if not self.wph[i]: self.wlo[i] = v; self.wph[i] = True
            else: self.wpos[i] = self.wlo[i] | (v & 1) << 8; self.wph[i] = False
        elif r == 0xB4: self.wctl = v & 1; self.wph = [False] * 4
        elif r == 0xB5: self.wmts = v
        elif r == 0xB6: self.wmtmw = v
        elif r == 0xB7: self.wmtsw = v
        elif 0xB8 <= r <= 0xBB: self.wsel[r - 0xB8] = v
        elif r == 0xBC: self.wlog14 = v
        elif r == 0xBD: self.wlog58 = v
        elif r == 0xBE: self.wmcmath = v
        elif r == 0xC0: self.ctrl = v & 3; self.sph = [False] * 16
        elif r == 0xC1: self.wmtm = v
        elif r == 0xC2: self.vaddr = (self.vaddr & 0x7FF00) | v
        elif r == 0xC3: self.vaddr = (self.vaddr & 0x700FF) | v << 8
        elif r == 0xC4: self.vaddr = (self.vaddr & 0x0FFFF) | (v & 7) << 16
        elif r == 0xC5: self.vram[self.vaddr] = v; self.vaddr = (self.vaddr + 1) % VRAM
        elif r == 0xC6: self.wcg = (self.wcg & 0x300) | v; self.wcgph = False
        elif r == 0xC7: self.wcg = (self.wcg & 0xFF) | (v & 3) << 8; self.wcgph = False
        elif r == 0xC8:
            if not self.wcgph: self.wcglo = v; self.wcgph = True
            else: self.cg[self.wcg] = self.wcglo | (v & 0x7F) << 8; self.wcg = (self.wcg + 1) & 1023; self.wcgph = False
        elif 0xD0 <= r < 0xD8: self.mapb[r - 0xD0] = v
        elif 0xD8 <= r < 0xE0: self.cfg[r - 0xD8] = v
        elif 0xE0 <= r < 0xF0:
            i = r - 0xE0
            if not self.sph[i]: self.slo[i] = v; self.sph[i] = True
            else:
                val = (self.slo[i] | v << 8) & 0x3FF
                if i & 1 == 0: self.sh[i >> 1] = val
                else: self.sv[i >> 1] = val
                self.sph[i] = False

    # ---- memories
    def word(self, a):
        a &= 0x7FFF
        return self.vram[2 * a] | self.vram[2 * a + 1] << 8

    def tile_row(self, base, bpp, fy):
        """The 8 colour indices of row fy of the planar tile at byte address base (pairs of bit-planes 16 bytes apart)."""
        key = (base, bpp, fy)
        row = self.tile_cache.get(key)
        if row is None:
            row = [0] * 8
            for pair in range(bpp // 2):
                lo = self.vram[(base + pair * 16 + fy * 2) % VRAM]
                hi = self.vram[(base + pair * 16 + fy * 2 + 1) % VRAM]
                for x in range(8):
                    bit = 7 - x
                    row[x] |= ((lo >> bit) & 1) << (pair * 2) | ((hi >> bit) & 1) << (pair * 2 + 1)
            self.tile_cache[key] = row
        return row

    # ---- the picture
    def frame(self):
        self.tile_cache = {}
        rows, widths = [], []
        for row in range(224):
            self.apply_hdma(row)
            px = self.render_row(row)
            rows.append(px); widths.append(len(px))
        if max(widths) == 400:   # lines drawn 256 wide sit in the middle of a wide frame (black either side)
            rows = [r if len(r) == 400 else [BLACK] * 72 + r + [BLACK] * 72 for r in rows]
        return rows

    def apply_hdma(self, row):
        for reg, mode, entries in self.hdma:
            start = 0
            for count, data in entries:
                if row == start:
                    pattern = DMA_PATTERN[mode]
                    for k, b in enumerate(data): self.write_b(reg + pattern[k % len(pattern)], b)
                start += count

    def render_row(self, row):
        ctrl = self.ctrl
        width = 400 if ctrl & 1 else 256
        if self.reg[0] & 0x80 or self.reg[0] & 15 == 0: return [BLACK] * width
        assert self.reg[0] & 15 == 15, 'the model draws full brightness only'
        origin = 72 if width == 400 else 0
        if ctrl & 2: return self.row_widemess(row, width, origin)
        return self.row_snes(row, width, origin)

    # -- the WideMess mode
    def row_widemess(self, row, width, origin):
        tm, ts, tmw, tsw, adsub = self.reg[0x2C], self.reg[0x2D], self.reg[0x2E], self.reg[0x2F], self.reg[0x31]
        screens = {
            'main': self.wmtm | (0x100 if tm & 0x10 else 0), 'sub': self.wmts | (0x100 if ts & 0x10 else 0),
            'mainw': self.wmtmw | (0x100 if tmw & 0x10 else 0), 'subw': self.wmtsw | (0x100 if tsw & 0x10 else 0),
            'math': self.wmcmath | (0x100 if adsub & 0x10 else 0) | (0x200 if adsub & 0x20 else 0),
        }
        sel = [(self.wsel[i >> 1] >> ((i & 1) * 4)) & 15 for i in range(8)] + [self.reg[0x25] & 15, self.reg[0x25] >> 4]
        log = [((self.wlog14 if i < 4 else self.wlog58) >> ((i & 3) * 2)) & 3 for i in range(8)] + [self.reg[0x2B] & 3, (self.reg[0x2B] >> 2) & 3]
        # the 16 layer slots front to back; the sprites of priority p sit in front of slot min(WMOBJPRIp, 16)
        slots = [('bg', l, 1) for l in range(8)] + [('bg', l, 0) for l in range(8)]
        order = []
        for k in range(17):
            for p in range(4):
                if min(self.objslot[p], 16) == k: order.append(('obj', p))
            if k < 16: order.append(slots[k])
        used = screens['main'] | screens['sub']
        bg = [self.widemess_layer(l, row, width) if used >> l & 1 else None for l in range(8)]
        obj = self.sprites(row, width, origin)
        return self.compose(width, origin, bg, obj, order, screens, sel, log)

    def widemess_layer(self, l, row, width):
        cfg = self.cfg[l]
        tw, th = (128 if cfg & 2 else 64), (64 if cfg & 1 else 32)
        bank, tbase, mbase = ((cfg >> 2) & 3) * 256, ((cfg >> 4) & 7) * 65536, self.mapb[l] * 2048
        y = (row + self.sv[l]) % (th * 8)
        out = []
        for c in range(width):
            x = (c + self.sh[l]) % (tw * 8)
            ea = (mbase + ((y // 8) * tw + x // 8) * 2) % VRAM
            e = self.vram[ea] | self.vram[(ea + 1) % VRAM] << 8
            fx, fy = x % 8, y % 8
            if e & 0x4000: fx = 7 - fx
            if e & 0x8000: fy = 7 - fy
            i = self.tile_row(tbase + (e & 0xFFF) * 64, 8, fy)[fx]
            out.append(None if i == 0 else (self.cg[bank + i], (e >> 12) & 1))
        return out

    # -- the SNES modes (0, 1, 2, 5 and 7: what the scenes use)
    def row_snes(self, row, width, origin):
        line = row + 1
        mode = self.reg[5] & 7
        assert mode in ORDER, f'the model draws modes 0, 1, 2, 5 and 7, not {mode}'
        assert not self.reg[5] & 0xF8, 'the model draws 8 x 8 tiles and BG3 without its priority bit'
        tm, ts, tmw, tsw, adsub = self.reg[0x2C], self.reg[0x2D], self.reg[0x2E], self.reg[0x2F], self.reg[0x31]
        def nine(v): return (v & 15) | (0x100 if v & 0x10 else 0)
        screens = {'main': nine(tm), 'sub': nine(ts), 'mainw': nine(tmw), 'subw': nine(tsw),
                   'math': (adsub & 15) | (0x100 if adsub & 0x10 else 0) | (0x200 if adsub & 0x20 else 0)}
        w12, w34, wo, wbg, wol = self.reg[0x23], self.reg[0x24], self.reg[0x25], self.reg[0x2A], self.reg[0x2B]
        sel = [w12 & 15, w12 >> 4, w34 & 15, w34 >> 4, 0, 0, 0, 0, wo & 15, wo >> 4]
        log = [(wbg >> (2 * i)) & 3 for i in range(4)] + [0] * 4 + [wol & 3, (wol >> 2) & 3]
        order = list(ORDER[mode])
        if mode == 7:
            assert not self.reg[0x33] & 0x40, 'the model draws Mode 7 without EXTBG'
            order.insert(order.index(('bg', 0, 0)), ('bg', 0, 1))   # BG1 either priority: the same place
        used = screens['main'] | screens['sub']
        bg = [None] * 8
        for b in range(4):
            if not used >> b & 1: continue
            if mode == 7: bg[b] = self.mode7(line, width, origin) if b == 0 else None
            elif BPP[mode][b]: bg[b] = self.snes_layer(b, mode, line, width, origin)
        obj = self.sprites(row, width, origin)
        return self.compose(width, origin, bg, obj, order, screens, sel, log, same_rank_mode7=(mode == 7), hires=mode in (5, 6))

    def snes_layer(self, b, mode, line, width, origin):
        """One layer's line; in hi-res (modes 5, 6) 2 x width half-pixels, its tiles 16 half-pixels wide (the tile and the next one)."""
        bpp = BPP[mode][b]
        hires = mode in (5, 6)
        tw = 16 if hires else 8
        sc = self.reg[7 + b]
        mapbase, wide, tall = (sc & 0xFC) << 8, sc & 1, sc & 2
        nba = (self.reg[0x0B] >> (b * 4)) & 15 if b < 2 else (self.reg[0x0C] >> ((b - 2) * 4)) & 15
        charbase = nba << 12
        mw, mh = (64 if wide else 32), (64 if tall else 32)
        mos = self.reg[6]
        msize = (mos >> 4) + 1 if mos >> b & 1 else 1                 # mosaic blocks, aligned on the SNES picture's left edge and on line 1
        yline = line - (line - 1) % msize
        palbase = b * 32 if mode == 0 else 0
        out = []
        for c in range(2 * width if hires else width):
            sx = c - 2 * origin if hires else c - origin                 # counted from the SNES picture's left edge
            sx -= sx % (msize * (2 if hires else 1))                     # (Python's % is a floor modulo: negative columns too)
            hs, vs = self.hofs[b], self.vofs[b]
            if mode in (2, 4, 6) and b < 2: hs, vs = self.offset_per_tile(b, mode, sx >> 1 if hires else sx, hs, vs)
            x = (sx + (2 if hires else 1) * hs) % (mw * tw)
            y = (yline + vs) % (mh * 8)
            tx, ty = x // tw, y // 8
            screen = (1 if tx >= 32 else 0) + ((2 if wide else 1) if ty >= 32 else 0)
            e = self.word(mapbase + screen * 0x400 + (ty % 32) * 32 + (tx % 32))
            fx, fy = x % tw, y % 8
            if e & 0x4000: fx = tw - 1 - fx
            if e & 0x8000: fy = 7 - fy
            tile = (e & 0x3FF) + (1 if fx >= 8 else 0)
            i = self.tile_row(2 * ((charbase + (tile & 0x3FF) * bpp * 4) & 0x7FFF), bpp, fy)[fx % 8]
            out.append(None if i == 0 else (self.cg[(palbase + ((e >> 10) & 7) * (1 << bpp) + i) & 0xFF], (e >> 13) & 1))
        return out

    def snes_map(self, mapbase, wide, tall, col, row):
        a = mapbase + ((row & 31) << 5) + (col & 31)
        if wide and col & 32: a += 0x400
        if tall and row & 32: a += 0x800 if wide else 0x400
        return self.word(a)

    def offset_per_tile(self, b, mode, screen_x, hs, vs):
        """Modes 2, 4, 6: BG3's map row VOFS3/8 (and the next, in modes 2 and 6) gives BG1 and BG2 a scroll per tile column. The SNES picture's
        first tile column never changes; the columns left of it are numbered -1, -2, ... and read BG3's entries further left (spec 3.2)."""
        column = (screen_x + (self.hofs[b] & 7)) >> 3
        if column == 0: return hs, vs
        sc = self.reg[9]
        mapbase, wide, tall = (sc & 0xFC) << 8, sc & 1, sc & 2
        col = ((self.hofs[2] >> 3) + column - 1) & (63 if wide else 31)
        row = (self.vofs[2] >> 3) & (63 if tall else 31)
        enable = 0x2000 << b
        h = self.snes_map(mapbase, wide, tall, col, row)
        if mode == 4:
            if h & enable:
                if h & 0x8000: vs = h & 0x3FF
                else: hs = (h & 0x3F8) | (hs & 7)
            return hs, vs
        v = self.snes_map(mapbase, wide, tall, col, row + 1)
        if h & enable: hs = (h & 0x3F8) | (hs & 7)
        if v & enable: vs = v & 0x3FF
        return hs, vs

    def mode7(self, line, width, origin):
        m = self.m7
        sel = self.reg[0x1A]
        a, b, c, d, x0, y0 = m['a'], m['b'], m['c'], m['d'], m['x'], m['y']
        y = 255 - line if sel & 2 else line
        def clip13(n): return (n | ~0x3FF) if n & 0x2000 else (n & 0x3FF)
        hc, vc = clip13(m['h'] - x0), clip13(m['v'] - y0)
        psx = ((a * hc) & ~63) + ((b * vc) & ~63) + ((b * y) & ~63) + (x0 << 8)
        psy = ((c * hc) & ~63) + ((d * vc) & ~63) + ((d * y) & ~63) + (y0 << 8)
        outside = sel >> 6
        out = []
        for col in range(width):
            x = col - origin                      # the SNES column: -72 to 327 at 400 wide
            xx = 255 - x if sel & 1 else x
            px, py = (psx + a * xx) >> 8, (psy + c * xx) >> 8
            if (px | py) & ~0x3FF and outside >= 2:
                if outside == 2: out.append(None); continue
                tile = 0
            else:
                tile = self.vram[2 * ((((py >> 3) & 127) << 7) | ((px >> 3) & 127))]
            i = self.vram[2 * ((tile << 6 | (py & 7) << 3 | (px & 7)) & 0x7FFF) + 1]
            out.append(None if i == 0 else (self.cg[i], 0))
        return out

    # -- sprites
    def sprites(self, row, width, origin):
        if self.ctrl and self.objctrl & 1: return self.widemess_sprites(row, width)
        return self.snes_sprites(row, width, origin)

    def snes_sprites(self, row, width, origin):
        obsel = self.reg[1]
        assert not self.oamadd & 0x8000, 'the model draws the SNES sprite table without priority rotation'
        size, namebase, gap = obsel >> 5, (obsel & 7) << 13, (((obsel >> 3) & 3) + 1) << 12
        split = 256 + origin                         # X at or above it means X - 512
        found = []
        for n in range(128):
            hi = (self.oam[0x200 + n // 4] >> ((n % 4) * 2)) & 3
            w, h = LARGE[size] if hi & 2 else SMALL[size]
            x = self.oam[4 * n] | (hi & 1) << 8
            if x >= split: x -= 512
            if (row - self.oam[4 * n + 1]) & 0xFF >= h: continue
            if origin + x + w <= 0 and x != -256: continue
            found.append((n, x, w, h))
        assert len(found) <= 32, f'row {row}: the model does not draw the SNES sprite limits ({len(found)} sprites)'
        assert sum(1 for (_, x, w, _) in found for t in range(w // 8) if -8 < origin + x + 8 * t < width) <= 34, f'row {row}: over 34 slivers'
        out = [None] * width
        for n, x, w, h in reversed(found):           # a lower index is in front: draw it last
            tile, attr = self.oam[4 * n + 2], self.oam[4 * n + 3]
            pal, prio = (attr >> 1) & 7, (attr >> 4) & 3
            uy = (row - self.oam[4 * n + 1]) & 0xFF
            if attr & 0x80: uy = h - 1 - uy
            table = namebase + (gap if attr & 1 else 0)
            for i in range(w):
                col = origin + x + i
                if not 0 <= col < width: continue
                ux = w - 1 - i if attr & 0x40 else i
                chr_ = ((((tile >> 4) + uy // 8) & 15) << 4) | ((tile + ux // 8) & 15)
                v = self.tile_row(2 * ((table + chr_ * 16) & 0x7FFF), 4, uy % 8)[ux % 8]
                if v: out[col] = (self.cg[128 + pal * 16 + v], prio, pal >= 4)
        return out

    def widemess_sprites(self, row, width):
        """Spec 3.3: the first 64 sprites in table order that cover the row (and are not wholly off the picture); 128 slivers, counted in table order."""
        out = [None] * width
        count = slivers = 0
        self.range_over = self.time_over = False
        base = self.objbase * 65536
        for n in range(256):
            s = self.woam[8 * n: 8 * n + 8]
            x, y = signed(s[0] | (s[1] & 3) << 8, 10), signed(s[2] | (s[3] & 3) << 8, 10)
            w, h = 8 << (s[7] & 3), 8 << ((s[7] >> 2) & 3)
            if not 0 <= row - y < h: continue
            if x + w <= 0 or x >= width: continue
            if count == 64: self.range_over = True; break
            count += 1
            if self.time_over: continue
            bpp8, tile, pal, prio, math_ = s[7] & 0x40, s[4] | (s[5] & 15) << 8, s[6] & 0x3F, (s[7] >> 4) & 3, bool(s[7] & 0x80)
            colours = (pal >> 4) * 256 if bpp8 else pal * 16
            uy = h - 1 - (row - y) if s[6] & 0x80 else row - y
            for k in range(w // 8):                  # the sprite's 8-pixel columns, left to right
                if not -8 < x + 8 * k < width: continue
                if slivers == 128: self.time_over = True; break
                slivers += 1
                for i in range(8 * k, 8 * k + 8):
                    col = x + i
                    if not 0 <= col < width or out[col] is not None: continue
                    ux = w - 1 - i if s[6] & 0x40 else i
                    t = (tile + (uy // 8) * 16 + ux // 8) & 0xFFF
                    v = self.tile_row(base + t * (64 if bpp8 else 32), 8 if bpp8 else 4, uy % 8)[ux % 8]
                    if v: out[col] = (self.cg[colours + v], prio, math_)
        return out

    # -- windows, screens, colour math
    def positions(self, width, origin):
        if self.wctl & 1: return tuple(self.wpos)
        a, b, c, d = self.reg[0x26], self.reg[0x27], self.reg[0x28], self.reg[0x29]
        return (0 if a == 0 else origin + a, width - 1 if b == 255 else origin + b, 0 if c == 0 else origin + c, width - 1 if d == 255 else origin + d)

    @staticmethod
    def inside(sel, logic, x, pos):
        en1, en2 = sel & 2, sel & 8
        if not en1 and not en2: return False
        w1 = (pos[0] <= x <= pos[1]) != bool(sel & 1)
        w2 = (pos[2] <= x <= pos[3]) != bool(sel & 4)
        if not en2: return w1
        if not en1: return w2
        return (w1 or w2, w1 and w2, w1 != w2, w1 == w2)[logic]

    def compose(self, width, origin, bg, obj, order, screens, sel, log, same_rank_mode7=False, hires=False):
        """Front-most layer per pixel on the main and sub screens, then clip-to-black and colour math. In hi-res the layers come in half-pixels
        (sprites and windows in pixels) and each pixel is the average of its two half-pixels."""
        rank = {item: i for i, item in enumerate(order)}
        if same_rank_mode7: rank[('bg', 0, 1)] = rank[('bg', 0, 0)]
        pos = self.positions(width, origin)
        cgw, adsub = self.reg[0x30], self.reg[0x31]
        black_mode, region_mode = (cgw >> 6) & 3, (cgw >> 4) & 3
        out = []
        for hx in range(2 * width if hires else width):
            x = hx >> 1 if hires else hx
            def front(on, masked):
                best = None
                for l in range(8):
                    p = bg[l][hx] if bg[l] is not None else None
                    if p is None or not on >> l & 1: continue
                    if masked >> l & 1 and self.inside(sel[l], log[l], x, pos): continue
                    r = rank.get(('bg', l, p[1]))
                    if r is not None and (best is None or r < best[0]): best = (r, p[0], l, False)
                o = obj[x]
                if o is not None and on >> 8 & 1 and not (masked >> 8 & 1 and self.inside(sel[8], log[8], x, pos)):
                    r = rank[('obj', o[1])]
                    if best is None or r < best[0]: best = (r, o[0], 8, o[2])
                return best
            m = front(screens['main'], screens['mainw'])
            layer, colour, obj_math = (m[2], m[1], m[3]) if m else (9, self.cg[0], False)
            cw = self.inside(sel[9], log[9], x, pos)
            black = black_mode == 3 or (black_mode == 1 and not cw) or (black_mode == 2 and cw)
            if black: colour = 0
            region = region_mode == 0 or (region_mode == 1 and cw) or (region_mode == 2 and not cw)
            takes = (screens['math'] >> 8 & 1 and obj_math) if layer == 8 else bool(screens['math'] >> layer & 1)
            if region and takes:
                half = bool(adsub & 0x40) and not black
                addend = self.coldata
                if cgw & 2:
                    s = front(screens['sub'], screens['subw'])
                    if s is None: half = False        # the sub screen shows only its backdrop: the fixed colour, not halved
                    else: addend = s[1]
                colour = blend(colour, addend, bool(adsub & 0x80), half)
            out.append(colour)
        if hires:
            out = [sum(((((out[2 * x] >> sh) & 31) + ((out[2 * x + 1] >> sh) & 31)) >> 1) << sh for sh in (0, 5, 10)) for x in range(width)]
        return [argb(c) for c in out]

# ------------------------------------------------------------------ a ROM: the script, its data, and the model fed the same writes

class Rom:
    def __init__(self, name, title, nounlock=False):
        self.name, self.title, self.nounlock = name, title, nounlock
        self.script = bytearray()
        self.data = bytearray()
        self.placed = {}
        self.m = Model()

    def unlock(self):
        self.script.append(4)
        if not self.nounlock: self.m.unlocked = True

    def b(self, reg, v):
        assert 0x2100 <= reg <= 0x21FF
        self.script += bytes([1, reg & 0xFF, v & 0xFF])
        self.m.write_b(reg & 0xFF, v & 0xFF)

    def b2(self, reg, v):            # a register written twice: the low byte, then the high
        self.b(reg, v & 0xFF); self.b(reg, (v >> 8) & 0xFF)

    def cpu(self, addr, v):
        self.script += bytes([2, addr & 0xFF, addr >> 8, v & 0xFF])

    def put(self, blob):
        blob = bytes(blob)
        if blob in self.placed: return self.placed[blob]
        assert len(blob) <= 0x8000
        off = len(self.data)
        if off // 0x8000 != (off + len(blob) - 1) // 0x8000:   # a transfer never crosses a bank
            self.data += bytes(0x8000 - off % 0x8000); off = len(self.data)
        self.data += blob
        where = (1 + off // 0x8000, 0x8000 + off % 0x8000)
        self.placed[blob] = where
        return where

    def dma(self, reg, blob, mode=0):
        n = len(blob)
        assert 0 < n <= 0x8000
        bank, addr = self.put(blob)
        self.script += bytes([3, mode, reg & 0xFF, addr & 0xFF, addr >> 8, bank, n & 0xFF, (n >> 8) & 0xFF])
        pattern = DMA_PATTERN[mode]
        for k, v in enumerate(blob): self.m.write_b((reg & 0xFF) + pattern[k % len(pattern)], v)

    def hdma(self, channel, reg, mode, entries):
        """An HDMA channel (1-7): entries of (rows, the bytes written on the first of them); turned on by hdma_on()."""
        table = bytearray()
        for rows, data in entries:
            assert 0 < rows < 128 and len(data) == len(DMA_PATTERN[mode])
            table.append(rows); table += bytes(data)
        table.append(0)
        bank, addr = self.put(table)
        base = 0x4300 + channel * 16
        self.cpu(base, mode); self.cpu(base + 1, reg & 0xFF); self.cpu(base + 2, addr & 0xFF); self.cpu(base + 3, addr >> 8); self.cpu(base + 4, bank)
        self.m.hdma.append((reg & 0xFF, mode, entries))

    # common set-ups
    def colours(self, wide=True):
        self.b(0x2121, 0); self.dma(0x2122, words(PALETTE[:256]))             # the SNES's 256, through its own port
        if wide:
            self.b(0x21C6, 0); self.b(0x21C7, 1); self.dma(0x21C8, words(PALETTE[256:]))   # 256-1023 through WMCGDATA

    def vram_wm(self, addr, blob):
        self.b(0x21C2, addr & 0xFF); self.b(0x21C3, (addr >> 8) & 0xFF); self.b(0x21C4, addr >> 16)
        for k in range(0, len(blob), 0x8000): self.dma(0x21C5, blob[k:k + 0x8000])

    def vram_snes(self, word, blob, vmain=0x80, mode=1, reg=0x2118):
        self.b(0x2115, vmain); self.b(0x2116, word & 0xFF); self.b(0x2117, word >> 8)
        for k in range(0, len(blob), 0x8000): self.dma(reg, blob[k:k + 0x8000], mode)

    def end(self):
        self.script.append(0)

def oam_entry(x, y, tile, pal, w, h, prio, bpp8=False, hf=False, vf=False, math_=False):
    sizes = {8: 0, 16: 1, 32: 2, 64: 3}
    return bytes([x & 0xFF, (x >> 8) & 3, y & 0xFF, (y >> 8) & 3, tile & 0xFF, (tile >> 8) & 15,
                  (pal & 0x3F) | (0x40 if hf else 0) | (0x80 if vf else 0),
                  sizes[w] | sizes[h] << 2 | prio << 4 | (0x40 if bpp8 else 0) | (0x80 if math_ else 0)])

HIDDEN = oam_entry(0, 240, 0, 0, 8, 8, 0)

def widemess_oam(entries):
    assert len(entries) <= 256
    return b''.join(entries) + HIDDEN * (256 - len(entries))

def snes_oam(sprites):
    """sprites: (x, y, tile, pal, prio, large, attr flags); the rest hidden at Y = 240."""
    low, high = bytearray(), [0] * 128
    for n in range(128):
        if n < len(sprites):
            x, y, tile, pal, prio, large, flags = sprites[n]
            low += bytes([x & 0xFF, y & 0xFF, tile & 0xFF, (tile >> 8) & 1 | pal << 1 | prio << 4 | flags])
            high[n] = (x >> 8) & 1 | (2 if large else 0)
        else:
            low += bytes([0, 240, 0, 0])
    hb = bytes(high[4 * k] | high[4 * k + 1] << 2 | high[4 * k + 2] << 4 | high[4 * k + 3] << 6 for k in range(32))
    return bytes(low) + hb

# The sprite sheets (spec 3.3: 16 tiles a row): 4 bpp tiles 0-255 solid, colour ((column + row) mod 15) + 1, so every 8 x 8 block of a sprite
# shows which tile it took; tiles 256-511 an asymmetric pattern (flips show); 8 bpp tiles 256-319 gradients over 255 colours.
def sheet4():
    t = bytearray()
    for n in range(512):
        if n < 256: t += solid(((n & 15) + (n >> 4)) % 15 + 1, 4)
        else: t += tile_bytes([[1 + ((x * 3 + y * 5 + n) % 15) for x in range(8)] for y in range(8)], 4)
    return bytes(t)

def sheet8():
    return b''.join(tile_bytes([[1 + ((n * 37 + y * 8 + x) % 255) for x in range(8)] for y in range(8)], 8) for n in range(64))

# ------------------------------------------------------------------ the scenes

def scene_sprites(rom):
    rom.unlock()
    rom.colours()
    # BG1: a wall at x 192-255 (and a block at x 368-383, y 104-135), BG2 a wall at x 256-319; high-priority tiles in rows 0-111, low below
    rom.vram_wm(0x10000, solid(0, 8) + solid(17, 8) + solid(34, 8))
    m1, m2 = [0] * (64 * 32), [0] * (64 * 32)
    for ty in range(28):
        prio = 0x1000 if ty < 14 else 0
        for tx in range(24, 32): m1[ty * 64 + tx] = 1 | prio
        for tx in range(32, 40): m2[ty * 64 + tx] = 2 | prio
    for ty in range(13, 17):
        for tx in range(46, 48): m1[ty * 64 + tx] = 1 | 0x1000
    rom.vram_wm(0x18000, words(m1)); rom.vram_wm(0x19000, words(m2))
    rom.b(0x21D0, 0x18000 >> 11); rom.b(0x21D1, 0x19000 >> 11)
    rom.b(0x21D8, 0x10); rom.b(0x21D9, 0x10)              # 64 x 32, colour bank 0, tiles at 64 KB
    # the WideMess table's tiles at 128 KB (WMOBJBASE 2); the SNES table's at SNES video RAM word 0 (OBSEL: 16 x 16 / 32 x 32, names at 0)
    rom.vram_wm(0x20000, sheet4()); rom.vram_wm(0x20000 + 256 * 64, sheet8())
    rom.vram_snes(0, sheet4()[:64 * 32])
    s = []
    s.append(oam_entry(360, -8, 0x40, 5, 16, 16, 3))                 # 0: Y = -8: its lower half at the top
    s.append(oam_entry(-8, 200, 0x42, 6, 16, 16, 3))                 # 1: X = -8: its right half at the left edge
    s.append(oam_entry(-63, 216 - 8, 0x44, 7, 64, 8, 3))             # 2: X = -63, 64 wide: one column
    s.append(oam_entry(-64, 196, 0x46, 8, 64, 8, 3))                 # 3: X = -64, 64 wide: wholly off, not counted
    s.append(oam_entry(380, 24, 0x48, 9, 32, 16, 3))                 # 4: X = 380, 32 wide: 20 columns
    s.append(oam_entry(396, 48, 0x4A, 10, 64, 16, 3))                # 5: X = 396, 64 wide: 4 columns
    s.append(oam_entry(400, 72, 0x4C, 11, 16, 16, 3))                # 6: X = 400: off the right
    s.append(oam_entry(511, 96, 0x4E, 12, 16, 16, 3))                # 7: X = 511: off the right
    s.append(oam_entry(-512, 100, 0x50, 13, 64, 16, 3))              # 8: X = -512: off the left
    for hi, (y, h) in enumerate([(8, 8), (20, 16), (40, 32), (76, 64)]):     # 9-24: the 16 sizes
        for wi, (x, w) in enumerate([(4, 8), (16, 16), (36, 32), (72, 64)]):
            s.append(oam_entry(x, y, wi * 2 + hi * 32, 1 + wi + hi * 4, w, h, 3))
    for k, (x, hf, vf) in enumerate([(4, 0, 0), (24, 1, 0), (44, 0, 1), (64, 1, 1)]):   # 25-28: 8 bpp, bank 1, flips
        s.append(oam_entry(x, 144, 256, 0x10, 16, 16, 3, bpp8=True, hf=hf, vf=vf))
    s.append(oam_entry(88, 144, 258, 0x20, 32, 32, 3, bpp8=True))     # 29: 8 bpp 32 x 32, bank 2
    for k, (x, hf, vf) in enumerate([(4, 0, 0), (24, 1, 0), (44, 0, 1), (64, 1, 1)]):   # 30-33: 4 bpp asymmetric, flips
        s.append(oam_entry(x, 180, 256 + 2 * k, 20 + k, 16, 16, 3, hf=hf, vf=vf))
    s.append(oam_entry(364, 112, 0x10, 30, 16, 16, 0))               # 34: priority 0 (behind every layer), in front of 35
    s.append(oam_entry(372, 118, 0x12, 31, 16, 16, 3))               # 35: priority 3, behind 34 where they overlap
    for half, y0 in enumerate([8, 112]):                            # 36-59: each priority across backdrop, BG1, BG2, backdrop
        for p in range(4):
            for k, x in enumerate([168, 232, 296]):
                s.append(oam_entry(x, y0 + p * 24, 0x20 + 2 * k + 8 * half, 32 + p * 3 + k, 64, 16, p))
    s.append(oam_entry(320, 216, 0x14, 40, 16, 16, 3))               # 60: Y = 216: its top half at the bottom
    for k in range(70):                                              # 61-130: 70 sprites on rows 216-223: the last ones over 64 are dropped
        s.append(oam_entry(4 * k, 216, (k * 3) & 0xFF, 41 + k % 20, 8, 8, 3))
    for k in range(30):                                              # 131-160: 30 sprites 64 wide on rows 0-7: slivers past 128 dropped
        s.append(oam_entry(13 * k, 0, 0x60 + (k & 7) * 2, 1 + k % 50, 64, 8, 2))
    rom.b(0x21A2, 0); rom.b(0x21A3, 0); rom.dma(0x21A4, widemess_oam(s))
    rom.b(0x2102, 0); rom.b(0x2103, 0)
    rom.dma(0x2104, snes_oam([(0, 40, 0, 1, 3, True, 0), (300, 60, 4, 2, 2, False, 0), (452, 80, 8, 3, 0, True, 0),
                              (256, 100, 2, 0, 1, False, 0), (200, 120, 32, 5, 1, True, 0)]))
    rom.b(0x2101, 0x60)
    rom.b(0x21A1, 2); rom.b(0x21A0, 1)                                # WMOBJBASE 2, the WideMess table
    for p, slot in enumerate([16, 8, 1, 0]): rom.b(0x21A6 + p, slot)  # priority 0 behind all, 1 behind the high tiles, 2 behind BG1's high tiles, 3 in front
    rom.b(0x212C, 0x10)                                              # TM: sprites on the main screen
    rom.b(0x21C1, 0x03)                                              # WMTM: BG1, BG2
    rom.b(0x21C0, 0x03)                                              # 400 wide, the WideMess mode
    rom.b(0x2100, 0x0F)
    rom.end()

def scene_windows(rom):
    rom.unlock()
    rom.colours()
    # 8 layers, all opaque everywhere, one shared map (tile 1 everywhere); BG(n+1)'s tile 1 is at n x 64 KB, a solid colour of bank n mod 4
    for l in range(8):
        rom.vram_wm(l * 0x10000 + 64, solid(20 + 25 * l, 8))
    rom.vram_wm(0x78000, words([1] * (64 * 32)))
    for l in range(8):
        rom.b(0x21D0 + l, 0x78000 >> 11)
        rom.b(0x21D8 + l, (l << 4) | ((l & 3) << 2))
    rom.b(0x21C1, 0xFF)                                              # WMTM: all 8
    # window 1 shaped by HDMA (a diamond, 102-297 at its widest), window 2 at 280-399; the SNES's own positions for the variant
    rom.b(0x21B4, 0x01)                                              # WMWCTL: 9-bit positions
    rom.b2(0x21B2, 280); rom.b2(0x21B3, 399)
    rom.b(0x2126, 0); rom.b(0x2127, 150); rom.b(0x2128, 60); rom.b(0x2129, 255)
    diamond = []
    for k in range(28):
        hw = 20 + 6 * min(k, 27 - k)
        l, r = 200 - hw, 199 + hw
        diamond.append((8, [l & 0xFF, l >> 8, r & 0xFF, r >> 8]))
    rom.hdma(1, 0x21B0, 3, diamond)
    # each layer's window (A = outside both windows, B = window 1 only, C = window 2 only, D = both):
    #   BG1 W1 or W2 (shows in A), BG2 W1 (C), BG3 W1 xnor W2 (B), BG4 W1 xor W2 (D);
    #   BG5 W1 or W2 (A), BG6 W1 and not W2 (C, D), BG7 W2 (B); BG8 W2, on the sub screen only
    rom.b(0x21B8, 0x2A); rom.b(0x21B9, 0xAA); rom.b(0x21BA, 0xEA); rom.b(0x21BB, 0x88)
    rom.b(0x21BC, 3 << 4 | 2 << 6); rom.b(0x21BD, 1 << 2)
    rom.b(0x21B6, 0x7F)                                              # WMTMW: BG1-BG7 masked on the main screen
    # the main screen per band (HDMA on WMTM): BG1-BG4; BG5-BG8; BG1 and BG8; BG1-BG4
    rom.hdma(3, 0x21C1, 0, [(56, [0x0F]), (56, [0xF0]), (56, [0x81]), (56, [0x0F])])
    rom.b(0x21B5, 0x80); rom.b(0x21B7, 0x80)                         # WMTS: BG8 on the sub screen, masked there by window 2
    rom.b(0x21BE, 0x55)                                              # WMCMATH: BG1, BG3, BG5, BG7
    rom.b(0x2125, 0x20)                                              # WOBJSEL: the colour window is window 1
    rom.b(0x2132, 0x20 | 6); rom.b(0x2132, 0x40 | 10); rom.b(0x2132, 0x80 | 4)   # COLDATA
    # four bands of colour math (HDMA on CGWSEL, CGADSUB): none; add the sub screen, halved; subtract the fixed colour;
    # add the sub screen inside the colour window, black outside it
    rom.hdma(2, 0x2130, 1, [(56, [0x00, 0x00]), (56, [0x02, 0x70]), (56, [0x00, 0xB0]), (56, [0x52, 0x30])])
    # sprites: in each band one with the colour-math bit and one without, in front of everything
    rom.vram_wm(0x20000, sheet4())
    s = []
    for band in range(4):
        s.append(oam_entry(20, band * 56 + 20, 0x00, 3, 16, 16, 3, math_=True))
        s.append(oam_entry(44, band * 56 + 20, 0x00, 3, 16, 16, 3, math_=False))
    rom.b(0x21A2, 0); rom.b(0x21A3, 0); rom.dma(0x21A4, widemess_oam(s))
    rom.b(0x2102, 0); rom.b(0x2103, 0); rom.dma(0x2104, snes_oam([]))
    rom.b(0x21A1, 2); rom.b(0x21A0, 1)
    rom.b(0x212C, 0x10)
    rom.b(0x21C0, 0x03)
    rom.cpu(0x420C, 0x0E)                                            # HDMA channels 1, 2 and 3
    rom.b(0x2100, 0x0F)
    rom.end()

def scene_widesnes(rom):
    rom.unlock()
    rom.colours(wide=False)
    # Mode 7 at words 0-0x3FFF: the 128 x 128 map in the low bytes (tiles 1-15 in 4 x 4 blocks), 16 tiles in the high bytes (tile 0, outside the plane, a check)
    m7map = bytes(1 + ((tx // 4 + ty // 4) % 15) for ty in range(128) for tx in range(128))
    rom.vram_snes(0, m7map, vmain=0x00, mode=0, reg=0x2118)
    m7tiles = bytearray()
    for t in range(16):
        for y in range(8):
            for x in range(8):
                m7tiles.append((250 + ((x // 2 + y // 2) & 1)) if t == 0 else (16 * t + ((x + y) & 15)) or 1)
    rom.vram_snes(0, bytes(m7tiles), vmain=0x80, mode=0, reg=0x2119)
    # Mode 1: BG1 4 bpp (tiles 0-15 solid) on a 64 x 32 map, BG2 4 bpp (tiles 32-35 asymmetric) on a 32 x 32 map with flips, BG3 2 bpp
    bg1 = b''.join(solid(1 + t % 15, 4) for t in range(16))
    bg2 = b''.join(tile_bytes([[1 + ((x * 3 + y * 5 + t) % 15) if (x + y + t) % 5 else 0 for x in range(8)] for y in range(8)], 4) for t in range(4))
    rom.vram_snes(0x4000, bg1); rom.vram_snes(0x4200, bg2)
    rom.vram_snes(0x4400, sheet4()[:64 * 32])                         # sprites: names 64-127 (OBSEL base 0x4000)
    bg3 = solid(0, 2) + b''.join(tile_bytes([[(c if (x < 6 and y < 6) else 0) for x in range(8)] for y in range(8)], 2) for c in (1, 2, 3, 1))
    rom.vram_snes(0x5000, bg3)
    map1 = []
    for scr in range(2):
        for ty in range(32):
            for tx in range(32):
                gx = scr * 32 + tx
                map1.append(((gx + ty) & 15) | ((gx >> 3) & 7) << 10 | (0x2000 if ty < 7 else 0))
    rom.vram_snes(0x6000, words(map1))
    rom.vram_snes(0x6800, words([(32 + (tx + ty) % 4) | ((tx + ty) & 3) << 14 | (ty % 8) << 10 for ty in range(32) for tx in range(32)]))
    # BG3's map: rows 0-29 are what BG3 shows in modes 0 and 1; rows 30 and 31 (never on screen) are mode 2's offset-per-tile table:
    # row 30 the horizontal offsets (bit 13 BG1, bit 14 BG2), row 31 the vertical ones; the mode 2 band points BG3's VOFS at row 30 (HDMA below)
    def bg3(tx, ty):
        if ty == 30: return ((tx * 13 + 8) & 0x3F8) | (0x2000 if tx % 2 == 0 else 0) | (0x4000 if tx % 3 == 0 else 0)
        if ty == 31: return ((tx * 7 + 3) & 0x3FF) | (0x2000 if tx % 3 == 1 else 0) | (0x4000 if tx % 2 == 1 else 0)
        return ((1 + tx % 4) if tx % 5 == 0 else 0) | (tx % 8) << 10 | (0x2000 if ty % 2 else 0)
    rom.vram_snes(0x6C00, words([bg3(tx, ty) for ty in range(32) for tx in range(32)]))
    rom.b(0x2107, 0x61); rom.b(0x2108, 0x68); rom.b(0x2109, 0x6C)
    rom.b(0x210B, 0x44); rom.b(0x210C, 0x05)
    rom.b2(0x210D, 40); rom.b2(0x210E, 0)                           # BG1 (and Mode 7) scroll
    rom.b2(0x210F, 0); rom.b2(0x2110, 3)
    rom.b2(0x2111, 5); rom.b2(0x2112, 0)
    # Mode 7: scaled by 0.75 and turned, centre (128, 200), outside the plane: tile 0
    rom.b(0x211A, 0xC0)
    rom.b2(0x211B, 0x00C0); rom.b2(0x211C, 0x0040); rom.b2(0x211D, -0x0040 & 0xFFFF); rom.b2(0x211E, 0x00C0)
    rom.b2(0x211F, 128); rom.b2(0x2120, 200)
    # sprites: the SNES table (OBSEL 16 x 16 / 32 x 32, names at 0x4000)
    rom.b(0x2101, 0x62)
    rom.b(0x2102, 0); rom.b(0x2103, 0)
    rom.dma(0x2104, snes_oam([
        (0, 10, 64, 0, 3, False, 0),        # picture column 72
        (255, 10, 66, 1, 3, False, 0),      # 327-342: into the right side
        (256, 30, 68, 2, 3, False, 0),      # X = 256 is +256 at 400 wide: column 328
        (300, 50, 72, 3, 3, True, 0),       # 372-403: cut at the right edge
        (327, 70, 74, 4, 3, False, 0),      # column 399: one column
        (328, 90, 76, 5, 3, True, 0),       # X = 328 is -184: off the left
        (440, 10, 78, 6, 3, False, 0),      # X = -72: column 0
        (412, 30, 96, 7, 3, True, 0),       # X = -100, 32 wide: 4 columns
        (100, 60, 98, 0, 0, True, 0),       # priority 0, across the mode 1 and hi-res bands
        (150, 130, 100, 1, 2, True, 0x40),  # across the hi-res and Mode 7 bands, h-flipped
        (462, 150, 102, 2, 1, True, 0x80),  # X = -50 over Mode 7, v-flipped
    ]))
    # the WideMess table (for the variant that turns it on in a SNES mode): tiles at 128 KB
    rom.vram_wm(0x20000, sheet4())
    ws = [oam_entry(-20, 5, 0x00, 7, 64, 32, 3), oam_entry(370, 100, 0x08, 9, 32, 64, 0), oam_entry(200, 150, 0x10, 11, 16, 16, 1, math_=True)]
    rom.b(0x21A2, 0); rom.b(0x21A3, 0); rom.dma(0x21A4, widemess_oam(ws))
    rom.b(0x21A1, 2)
    # windows: W1 = SNES 40-200 (picture 112-272) masks BG1; W2 = SNES 0-100 (picture 0-172: its left edge on the SNES edge) is the colour window
    rom.b(0x2126, 40); rom.b(0x2127, 200); rom.b(0x2128, 0); rom.b(0x2129, 100)
    rom.b(0x2123, 0x02); rom.b(0x212E, 0x01); rom.b(0x2125, 0x80)
    # colour math: the fixed colour added to BG2 and the backdrop inside the colour window
    rom.b(0x2130, 0x10); rom.b(0x2131, 0x22)
    rom.b(0x2132, 0x20 | 8); rom.b(0x2132, 0x40 | 2); rom.b(0x2132, 0x80 | 12)
    rom.b(0x212C, 0x17)                                              # BG1, BG2, BG3, sprites
    rom.hdma(1, 0x2105, 0, [(40, [0x01]), (40, [0x00]), (48, [0x05]), (48, [0x02]), (48, [0x07])])   # bands: mode 1, mode 0, hi-res mode 5, mode 2, Mode 7
    rom.hdma(4, 0x2106, 0, [(64, [0x00]), (64, [0x00]), (48, [0x33]), (48, [0x00])])   # mosaic 4 x 4 on BG1 and BG2 in the mode 2 band (with its offset-per-tile)
    rom.hdma(5, 0x2112, 2, [(64, [0, 0]), (64, [0, 0]), (48, [240, 0]), (48, [0, 0])])   # BG3VOFS: the offset-per-tile table (BG3's rows 30-31) in the mode 2 band
    rom.b(0x2105, 0x01)
    rom.b(0x21C0, 0x01)                                              # WMCTRL: WIDE (a SNES mode, 400 wide)
    rom.cpu(0x420C, 0x32)                                            # HDMA channels 1, 4 and 5
    rom.b(0x2100, 0x0F)
    rom.end()

SCENES = [
    ('sprites', 'WideMess sprites', scene_sprites, {
        'snes_table': lambda m: m.wm_write(0xA0, 0x00),         # WMOBJ bit 0 clear: the SNES table in the WideMess mode
        'narrow': lambda m: m.wm_write(0xC0, 0x02),             # the WideMess mode 256 wide
    }),
    ('windows', 'WideMess windows', scene_windows, {
        'snes_positions': lambda m: m.wm_write(0xB4, 0x00),     # WMWCTL bit 0 clear: the SNES's WH0-WH3 at 400
    }),
    ('widesnes', 'WideMess SNES modes wide', scene_widesnes, {
        'wide_windows': lambda m: [m.wm_write(r, v) for r, v in [(0xB4, 1), (0xB0, 10), (0xB0, 0), (0xB1, 389 & 0xFF), (0xB1, 1), (0xB2, 300 & 0xFF), (0xB2, 1), (0xB3, 399 & 0xFF), (0xB3, 1)]],
        'widemess_table': lambda m: m.wm_write(0xA0, 0x01),     # the WideMess sprite table in a SNES mode
    }),
]

# ------------------------------------------------------------------ output

def write_png(path, rows):
    h, w = len(rows), len(rows[0])
    raw = bytearray()
    for r in rows:
        raw.append(0)
        for p in r: raw += bytes(((p >> 16) & 255, (p >> 8) & 255, p & 255))
    def chunk(t, d): return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xFFFFFFFF)
    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(bytes(raw), 9)) + chunk(b'IEND', b''))

def layout(rom, banks):
    head = (".MEMORYMAP\n  SLOTSIZE $8000\n  DEFAULTSLOT 0\n  SLOT 0 $8000\n.ENDME\n.ROMBANKSIZE $8000\n"
            f".ROMBANKS {banks}\n\n.SNESHEADER\n  ID \"SNES\"\n  NAME \"{rom.title.upper()[:21].ljust(21)}\"\n  SLOWROM\n  LOROM\n"
            "  CARTRIDGETYPE $00\n  ROMSIZE $08\n  SRAMSIZE $00\n  COUNTRY $01\n  LICENSEECODE $00\n  VERSION $00\n.ENDSNES\n")
    data = ''
    for b in range(1, banks):
        n = min(0x8000, len(rom.data) - (b - 1) * 0x8000)
        data += f'.BANK {b} SLOT 0\n.ORG 0\n.SECTION "Data{b}" FORCE\n  .INCBIN "data.bin" SKIP ${(b - 1) * 0x8000:X} READ ${n:X}\n.ENDS\n\n'
    return head, data

def stats(model, name):
    """What the picture holds that the scene meant to test (printed, and checked here, so a scene that stopped testing its feature fails the build)."""
    over = []
    if model.unlocked and model.ctrl and model.objctrl & 1:
        for row in range(224):
            model.widemess_sprites(row, 400 if model.ctrl & 1 else 256)
            if model.range_over or model.time_over: over.append((row, model.range_over, model.time_over))
    return over

def main():
    os.makedirs(BIN, exist_ok=True)
    for name, title, scene, variants in SCENES:
        rom = Rom(name, title); scene(rom)
        ctl = Rom(name, title, nounlock=True); scene(ctl)
        assert rom.script == ctl.script and rom.data == ctl.data
        d = os.path.join(WORK, name)
        os.makedirs(d, exist_ok=True)
        with open(os.path.join(d, 'script.bin'), 'wb') as f: f.write(rom.script)
        banks = 1 + (len(rom.data) + 0x7FFF) // 0x8000
        with open(os.path.join(d, 'data.bin'), 'wb') as f: f.write(rom.data)
        head, data = layout(rom, banks)
        with open(os.path.join(d, 'layout_head.inc'), 'w', newline='\n') as f: f.write(head)
        with open(os.path.join(d, 'layout_data.inc'), 'w', newline='\n') as f: f.write(data)
        pictures = [(f'{name}.expect.png', rom.m), (f'{name}_nounlock.expect.png', ctl.m)]
        for v, poke in variants.items():
            m = copy.deepcopy(rom.m); poke(m); pictures.append((f'{name}.{v}.expect.png', m))
        for file, m in pictures:
            frame = m.frame()
            write_png(os.path.join(BIN, file), frame)
            colours = len({p for r in frame for p in r})
            print(f'{file}: {len(frame[0])} x {len(frame)}, {colours} colours')
        over = stats(rom.m, name)
        if name == 'sprites':
            assert any(r for r in over if r[0] >= 216 and r[1]), 'the 64-a-line band no longer overflows'
            assert any(r for r in over if r[0] < 8 and r[2]), 'the 128-sliver band no longer overflows'
            print(f'  sprite limits: range over on rows {[r[0] for r in over if r[1]]}, time over on rows {[r[0] for r in over if r[2]]}')
        print(f'  {name}: script {len(rom.script)} bytes, data {len(rom.data)} bytes, {banks} banks of 32 KB')

if __name__ == '__main__':
    main()
