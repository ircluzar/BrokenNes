"""wmsgfx.py: the WideMess devkit's graphics converter (BrokenDev/widemess, docs/projects/widemess-devkit.md).

PNG -> the WideMess picture's formats (docs/projects/widemess-spec.md section 3):
  * layers: 8-bpp tiles (the SNES's planar 8-bpp format, 64 bytes a tile, so gfx4snes's 8-bpp tiles are the same bytes) and a
    WideMess tile map (16-bit entries row by row: tile 0-4095, priority bit 12, flips bits 14/15), deduplicated with flips;
  * sprites: 4- or 8-bpp tiles laid out in the WideMess sprite sheet (16 tiles wide: a sprite's tile (tx, ty) is tile + ty x 16 + tx);
  * one palette of up to 1,024 colours (four banks of 256): every layer's and sprite's colours, shared where they share a bank.

Modes
  python wmsgfx.py build <wms.json> <outdir>        every graphic of a project: <outdir>/gfx.asm, gfx.h, gfx_*.bin, gfx.json
  python wmsgfx.py layer <png> <outprefix> [--bank B] [--map 64x32] [--priority] [--quantize] [--no-flip]
  python wmsgfx.py sprite <png> <outprefix> --frame 16x16 [--bpp 4|8] [--palette P | --bank B] [--quantize]
      (the single-asset modes write <outprefix>.til / .map / .pal(.json): the same bytes the build mode puts in ROM)

The build mode's wms.json section:
  "graphics": {
    "backdrop": "#102040",                                     colour 0 of bank 0, shown where nothing draws
    "layers":  [ { "name": "sky", "png": "gfx/sky.png", "layer": 7, "bank": 3, "map": "64x32", "priority": false } ],
    "sprites": [ { "name": "ship", "png": "gfx/ship.png", "frame": "16x16", "bpp": 4, "palette": 2 },
                 { "name": "boss", "png": "gfx/boss.png", "frame": "64x64", "bpp": 8, "bank": 1 } ]
  }
Video RAM it lays out (and reports in gfx.h): the maps from $00000 (2 KB aligned), the layers' tiles from the next 64 KB unit (one pool
of up to 4,096 tiles that every layer shares, deduplicated across layers), the sprites' tiles from the 64 KB unit after them.
Colours: a pixel with alpha below 128 is transparent (index 0). A layer's or 8-bpp sprite's bank holds at most 255 colours; a 4-bpp
sprite at most 15 (in palette P, entries P x 16 + 1..15, which then belong to that sprite in bank P / 16). More is an error, unless
the asset says "quantize": true (then Pillow reduces it, and the report says so). Nothing is dropped silently.
"""
import json
import os
import sys

import numpy as np
from PIL import Image

PIECE = 32768                      # a ROM section's largest piece (a section must fit a 64 KB bank)
MAP_SIZES = {'64x32': 0, '64x64': 1, '128x32': 2, '128x64': 3}
SPR_SIZES = {8: 0, 16: 1, 32: 2, 64: 3}


class GfxError(Exception):
    pass


# ------------------------------------------------------------------------------------------------ colours and images

def bgr15(rgb):
    r, g, b = rgb
    return (r >> 3) | (g >> 3) << 5 | (b >> 3) << 10


def parse_colour(text):
    t = text.lstrip('#')
    if len(t) != 6:
        raise GfxError(f"colour '{text}' must be #RRGGBB")
    return int(t[0:2], 16), int(t[2:4], 16), int(t[4:6], 16)


def load_rgba(path, quantize_to=None):
    """The image as an H x W array of BGR15 colours, -1 where transparent. quantize_to: reduce to that many colours first."""
    im = Image.open(path).convert('RGBA')
    a = np.array(im)
    alpha = a[:, :, 3] >= 128
    note = ''
    if quantize_to is not None:
        rgb = Image.fromarray(a[:, :, :3])
        n_before = len(np.unique(a[:, :, :3][alpha].reshape(-1, 3), axis=0)) if alpha.any() else 0
        if n_before > quantize_to:
            q = rgb.quantize(colors=quantize_to, method=Image.Quantize.MEDIANCUT).convert('RGB')
            a = np.dstack([np.array(q), a[:, :, 3]])
            note = f'quantized from {n_before} to at most {quantize_to} colours'
    c = ((a[:, :, 0].astype(np.int32) >> 3) | (a[:, :, 1].astype(np.int32) >> 3) << 5 | (a[:, :, 2].astype(np.int32) >> 3) << 10)
    c[~alpha] = -1
    return c, note


def pad_to(c, w, h):
    out = np.full((h, w), -1, dtype=np.int32)
    out[:c.shape[0], :c.shape[1]] = c
    return out


# ------------------------------------------------------------------------------------------------ tiles

def encode_tile(idx, bpp):
    """An 8 x 8 array of colour indexes as SNES planar tile bytes: 32 (4 bpp) or 64 (8 bpp)."""
    out = bytearray(8 * bpp)
    for y in range(8):
        for p in range(bpp):
            bits = 0
            for x in range(8):
                if (int(idx[y, x]) >> p) & 1:
                    bits |= 0x80 >> x
            out[(p >> 1) * 16 + y * 2 + (p & 1)] = bits
    return bytes(out)


class TilePool:
    """Tiles deduplicated with flips. Tile 0 is the blank (all transparent) tile."""

    def __init__(self, bpp, flips=True, limit=4096):
        self.bpp, self.flips, self.limit = bpp, flips, limit
        self.tiles = [np.zeros((8, 8), dtype=np.int32)]
        self.index = {self.tiles[0].tobytes(): (0, 0)}

    def add(self, t):
        key = t.tobytes()
        if key in self.index:
            return self.index[key]
        if self.flips:
            for flip, v in ((1, t[:, ::-1]), (2, t[::-1, :]), (3, t[::-1, ::-1])):
                k = np.ascontiguousarray(v).tobytes()
                if k in self.index:
                    return self.index[k][0], flip
        n = len(self.tiles)
        if n >= self.limit:
            raise GfxError(f'more than {self.limit} distinct tiles')
        self.tiles.append(t.copy())
        self.index[key] = (n, 0)
        return n, 0

    def data(self):
        return b''.join(encode_tile(t, self.bpp) for t in self.tiles)


# ------------------------------------------------------------------------------------------------ palettes

class Palette:
    """1,024 entries in four banks. 4-bpp sprite palettes own 16 entries; layers and 8-bpp sprites share their bank's free entries."""

    def __init__(self):
        self.entries = [None] * 1024          # BGR15 or None
        self.owner = [''] * 1024
        self.bank_maps = [dict() for _ in range(4)]   # bank -> {BGR15: index in the bank}

    def reserve_sub(self, p, colours, who):
        """A 4-bpp sprite's palette p: its colours at p x 16 + 1..15. Returns {BGR15: index 1..15}."""
        if not 0 <= p < 64:
            raise GfxError(f'{who}: palette {p} must be 0-63')
        base = p * 16
        # entry 0 of a 16-colour palette is transparent for the sprite (and may be the backdrop or a bank's colour 0): not owned
        if any(self.owner[base + i] or self.entries[base + i] is not None for i in range(1, 16)):
            raise GfxError(f'{who}: palette {p} (entries {base + 1}-{base + 15}) is already used by {sorted(set(o for o in self.owner[base + 1:base + 16] if o))}')
        if len(colours) > 15:
            raise GfxError(f'{who}: {len(colours)} colours, a 4-bpp sprite has 15 (plus transparent); add "quantize": true or reduce them')
        m = {}
        for i in range(1, 16):
            self.owner[base + i] = who
        for i, col in enumerate(sorted(colours)):
            self.entries[base + 1 + i] = col
            m[col] = 1 + i
        return m

    def add_bank(self, bank, colours, who):
        """Colours for a layer or an 8-bpp sprite in bank 0-3: shared with the bank's earlier users. Returns {BGR15: index 1..255}."""
        if not 0 <= bank < 4:
            raise GfxError(f'{who}: bank {bank} must be 0-3')
        m = self.bank_maps[bank]
        for col in sorted(colours):
            if col in m:
                continue
            free = next((i for i in range(1, 256) if not self.owner[bank * 256 + i] and self.entries[bank * 256 + i] is None), None)
            if free is None:
                users = sorted(set(o for o in self.owner[bank * 256:bank * 256 + 256] if o))
                raise GfxError(f'{who}: bank {bank} is full (255 colours, shared by {users}); move it to another bank, or add "quantize": true')
            self.entries[bank * 256 + free] = col
            self.owner[bank * 256 + free] = 'bank'
            m[col] = free
        return m

    def used(self):
        return sum(1 for e in self.entries if e is not None)

    def data(self):
        out = bytearray()
        for e in self.entries:
            v = e if e is not None else 0
            out += bytes((v & 0xFF, v >> 8))
        return bytes(out)


def colours_of(c):
    return set(int(v) for v in np.unique(c) if v >= 0)


def index_image(c, cmap):
    idx = np.zeros(c.shape, dtype=np.int32)
    for col, i in cmap.items():
        idx[c == col] = i
    return idx


# ------------------------------------------------------------------------------------------------ layers and sprites

def map_size_for(w, h, asked=None):
    if asked:
        if asked not in MAP_SIZES:
            raise GfxError(f"map '{asked}' must be one of {list(MAP_SIZES)}")
        mw, mh = (int(v) for v in asked.split('x'))
    else:
        mw = 64 if w <= 512 else 128
        mh = 32 if h <= 256 else 64
    if w > mw * 8 or h > mh * 8:
        raise GfxError(f'a {w} x {h} image does not fit a {mw} x {mh}-tile map ({mw * 8} x {mh * 8} pixels)')
    return mw, mh


def convert_layer(idx, pool, mw, mh, priority):
    """The tile map (row by row) of an indexed image padded to the map's size."""
    entries = np.zeros(mw * mh, dtype=np.uint16)
    for ty in range(mh):
        for tx in range(mw):
            t = idx[ty * 8:ty * 8 + 8, tx * 8:tx * 8 + 8]
            n, flip = pool.add(t)
            e = n | (0x1000 if priority else 0) | (0x4000 if flip & 1 else 0) | (0x8000 if flip & 2 else 0)
            entries[ty * mw + tx] = e
    return entries.astype('<u2').tobytes()


def split_frames(c, fw, fh, who):
    h, w = c.shape
    if fw not in SPR_SIZES or fh not in SPR_SIZES:
        raise GfxError(f'{who}: frame {fw} x {fh}: a sprite is 8, 16, 32 or 64 pixels wide and high')
    if w % fw or h % fh:
        raise GfxError(f'{who}: the image ({w} x {h}) is not a whole number of {fw} x {fh} frames')
    return [c[y:y + fh, x:x + fw] for y in range(0, h, fh) for x in range(0, w, fw)]


class SpriteSheet:
    """The sprite tiles of one depth, 16 tiles wide, shelf by shelf (each asset starts a new shelf)."""

    def __init__(self, bpp):
        self.bpp = bpp
        self.rows = []            # rows of 16 tiles (8 x 8 index arrays or None)

    def place(self, frames_idx, fw, fh):
        tw, th = fw // 8, fh // 8
        per_row = 16 // tw
        first_row = len(self.rows)
        for k, f in enumerate(frames_idx):
            r0 = first_row + (k // per_row) * th
            c0 = (k % per_row) * tw
            while len(self.rows) < r0 + th:
                self.rows.append([None] * 16)
            for ty in range(th):
                for tx in range(tw):
                    self.rows[r0 + ty][c0 + tx] = f[ty * 8:ty * 8 + 8, tx * 8:tx * 8 + 8]
        return first_row * 16, per_row, th * 16, tw

    def tiles(self):
        return len(self.rows) * 16

    def data(self):
        blank = np.zeros((8, 8), dtype=np.int32)
        return b''.join(encode_tile(t if t is not None else blank, self.bpp) for row in self.rows for t in row)


# ------------------------------------------------------------------------------------------------ the build mode

def c_name(name):
    if not name.replace('_', 'a').isalnum() or not name[0].isalpha():
        raise GfxError(f"name '{name}' must be letters, digits and _ (it names C symbols)")
    return name.lower()


def build(cfg_path, outdir):
    cfg = json.load(open(cfg_path, encoding='utf-8'))
    root = os.path.dirname(os.path.abspath(cfg_path))
    g = cfg.get('graphics') or {}
    os.makedirs(outdir, exist_ok=True)
    pal = Palette()
    notes = []
    if 'backdrop' in g:
        pal.entries[0] = bgr15(parse_colour(g['backdrop']))
        pal.owner[0] = 'backdrop'
    else:
        pal.entries[0] = 0
        pal.owner[0] = 'backdrop'

    layers = g.get('layers', [])
    sprites = g.get('sprites', [])
    seen_layers, names = {}, set()
    # 4-bpp sprite palettes first: they own their 16 entries
    spr_src = []
    for s in sprites:
        who = c_name(s['name'])
        if who in names:
            raise GfxError(f"two graphics are called '{who}'")
        names.add(who)
        bpp = int(s.get('bpp', 4))
        if bpp not in (4, 8):
            raise GfxError(f'{who}: bpp {bpp} must be 4 or 8')
        qz = 15 if bpp == 4 else 255
        c, note = load_rgba(os.path.join(root, s['png']), qz if s.get('quantize') else None)
        if note:
            notes.append(f'{who}: {note}')
        cols = colours_of(c)
        if bpp == 4:
            cmap = pal.reserve_sub(int(s.get('palette', 0)), cols, who)
        else:
            cmap = pal.add_bank(int(s.get('bank', 0)), cols, who)
        spr_src.append((s, who, bpp, c, cmap))

    pool = TilePool(8)
    layer_out = []
    for l in layers:
        who = c_name(l['name'])
        if who in names:
            raise GfxError(f"two graphics are called '{who}'")
        names.add(who)
        n = int(l['layer'])
        if not 0 <= n < 8:
            raise GfxError(f'{who}: layer {n} must be 0-7 (BG1-BG8)')
        if n in seen_layers:
            raise GfxError(f'{who}: layer {n} is already {seen_layers[n]}')
        seen_layers[n] = who
        bank = int(l.get('bank', n & 3))
        c, note = load_rgba(os.path.join(root, l['png']), 255 if l.get('quantize') else None)
        if note:
            notes.append(f'{who}: {note}')
        h, w = c.shape
        mw, mh = map_size_for(w, h, l.get('map'))
        cmap = pal.add_bank(bank, colours_of(c), who)
        idx = index_image(pad_to(c, mw * 8, mh * 8), cmap)
        try:
            mapdata = convert_layer(idx, pool, mw, mh, bool(l.get('priority', False)))
        except GfxError as e:
            raise GfxError(f'{who}: the layers together have {e}: one tile pool holds 4,096') from None
        layer_out.append(dict(name=who, layer=n, bank=bank, mw=mw, mh=mh, w=w, h=h, map=mapdata,
                              size=f'{mw}x{mh}', priority=bool(l.get('priority', False))))

    # sprites: 4 bpp sheet, then 8 bpp
    sheets = {4: SpriteSheet(4), 8: SpriteSheet(8)}
    spr_out = []
    for s, who, bpp, c, cmap in spr_src:
        fw, fh = (int(v) for v in str(s.get('frame', '16x16')).split('x'))
        frames = [index_image(f, cmap) for f in split_frames(c, fw, fh, who)]
        first, per_row, row_step, tw = sheets[bpp].place(frames, fw, fh)
        a6 = (int(s.get('palette', 0)) & 0x3F) if bpp == 4 else ((int(s.get('bank', 0)) * 16) & 0x3F)
        a7 = SPR_SIZES[fw] | SPR_SIZES[fh] << 2 | (0x40 if bpp == 8 else 0)
        spr_out.append(dict(name=who, bpp=bpp, first=first, per_row=per_row, row_step=row_step, tw=tw, frames=len(frames),
                            fw=fw, fh=fh, a6=a6, a7=a7))

    # video RAM: maps from 0, layer tiles from the next 64 KB unit, sprite tiles after them
    addr = 0
    for lo in layer_out:
        lo['map_addr'] = addr
        addr += len(lo['map'])
        addr = (addr + 2047) & ~2047
    tile_unit = max(1, (addr + 0xFFFF) >> 16)
    tile_data = pool.data() if layer_out else b''
    spr_unit = tile_unit + ((len(tile_data) + 0xFFFF) >> 16) if tile_data else tile_unit
    s4 = sheets[4].data()
    s8 = sheets[8].data()
    off8 = (len(s4) + 63) // 64                     # the 8-bpp sheet's first tile, in 64-byte tiles
    spr_data = s4 + bytes(off8 * 64 - len(s4)) + s8
    end = (spr_unit << 16) + len(spr_data)
    if end > 512 * 1024:
        raise GfxError(f'the graphics need {end:,} bytes of video RAM (512 KB): the sprites end at ${end:05X}')
    if (len(s4) // 32) > 4096 or off8 + len(s8) // 64 > 4096:
        raise GfxError('more than 4,096 sprite tiles')

    # ---- outputs
    blobs = []          # (symbol, bytes)
    uploads = []        # (kind, arg, dest, cfg, symbol, piece size)

    def add_blob(sym, data, kind, dest):
        for i in range(0, max(1, len(data)), PIECE):
            part = data[i:i + PIECE]
            if not part:
                break
            s = f'{sym}_{i // PIECE}'
            blobs.append((s, part))
            uploads.append((kind, 0, dest + i, 0, s, len(part)))

    add_blob('gfx_palette', pal.data(), 2, 0)
    for lo in layer_out:
        add_blob(f"gfx_{lo['name']}_map", lo['map'], 1, lo['map_addr'])
        cfg_byte = MAP_SIZES[lo['size']] | (lo['bank'] & 3) << 2 | (tile_unit & 7) << 4
        lo['cfg'] = cfg_byte
        uploads.append((3, lo['layer'], lo['map_addr'], cfg_byte, None, 0))
    if tile_data:
        add_blob('gfx_layer_tiles', tile_data, 1, tile_unit << 16)
    if spr_data:
        add_blob('gfx_sprite_tiles', spr_data, 1, spr_unit << 16)
    if spr_out:
        uploads.append((4, spr_unit, 0, 0, None, 0))
    mask = 0
    for lo in layer_out:
        mask |= 1 << lo['layer']
    uploads.append((7, mask, 0, 0, None, 0))

    for sym, data in blobs:
        open(os.path.join(outdir, sym + '.bin'), 'wb').write(data)

    asm = ['; gfx.asm: written by wmsgfx.py from ' + os.path.basename(cfg_path) + '; do not edit.', '.include "hdr.asm"', '']
    for sym, data in blobs:
        asm += [f'.SECTION ".{sym}" SUPERFREE', f'{sym}:', f'.INCBIN "gen/{sym}.bin"', '.ENDS', '']
    asm += ['.SECTION ".gfx_uploads" SUPERFREE', 'gfx_uploads:']
    for kind, arg, dest, cfgb, sym, size in uploads:
        src = f'.dl {sym}' if sym else '.dl 0'
        asm.append(f'  .db {kind}, {arg}')
        asm.append(f'  .dl ${dest:06X}')
        asm.append(f'  .db ${cfgb:02X}')
        asm.append(f'  {src}')
        asm.append(f'  .db 0')
        asm.append(f'  .dw {size}')
    asm += ['  .db 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0', '.ENDS', '']
    open(os.path.join(outdir, 'gfx.asm'), 'w', newline='\n').write('\n'.join(asm))

    h = ['/* gfx.h: written by wmsgfx.py from ' + os.path.basename(cfg_path) + '; do not edit. */', '#ifndef GFX_H', '#define GFX_H',
         '#include "widemess.h"', '',
         '/* the upload table: every map, tile, the 1,024-colour palette, the layers\' settings and WMTM; call in forced blank */',
         'extern u8 gfx_uploads[];', '#define gfxLoadAll() wmsUpload(gfx_uploads)', '',
         f'#define GFX_LAYER_TILES_UNIT {tile_unit}', f'#define GFX_SPRITE_UNIT {spr_unit}', f'#define GFX_LAYERS_MASK 0x{mask:02X}',
         f'#define GFX_COLOURS {pal.used()}', '']
    for lo in layer_out:
        u = lo['name'].upper()
        h += [f"/* {lo['name']}: BG{lo['layer'] + 1}, {lo['w']} x {lo['h']} on a {lo['size']} map, bank {lo['bank']} */",
              f"#define GFX_{u}_LAYER {lo['layer']}", f"#define GFX_{u}_MAP 0x{lo['map_addr']:05X}",
              f"#define GFX_{u}_CFG 0x{lo['cfg']:02X}", f"#define GFX_{u}_W {lo['w']}", f"#define GFX_{u}_H {lo['h']}", '']
    for so in spr_out:
        u = so['name'].upper()
        base = so['first'] if so['bpp'] == 4 else off8 + so['first']
        h += [f"/* {so['name']}: {so['frames']} frames of {so['fw']} x {so['fh']}, {so['bpp']} bpp */",
              f"#define GFX_{u}_FRAMES {so['frames']}",
              f"#define GFX_{u}_TILE(k) ({base} + ((k) / {so['per_row']}) * {so['row_step']} + ((k) % {so['per_row']}) * {so['tw']})",
              f"#define GFX_{u}_ATTR6 0x{so['a6']:02X}", f"#define GFX_{u}_ATTR7 0x{so['a7']:02X}",
              f"#define GFX_{u}_W {so['fw']}", f"#define GFX_{u}_H {so['fh']}", '']
    h += ['#endif', '']
    open(os.path.join(outdir, 'gfx.h'), 'w', newline='\n').write('\n'.join(h))

    report = dict(layers=[{k: v for k, v in lo.items() if k != 'map'} for lo in layer_out], sprites=spr_out,
                  layerTiles=len(pool.tiles) if layer_out else 0, layerTilesUnit=tile_unit, spriteUnit=spr_unit,
                  spriteTiles4=len(s4) // 32, spriteTiles8=len(s8) // 64, colours=pal.used(), vramEnd=end, notes=notes,
                  rom=sum(len(d) for _, d in blobs))
    json.dump(report, open(os.path.join(outdir, 'gfx.json'), 'w'), indent=1)
    print(f"wmsgfx: {len(layer_out)} layers ({report['layerTiles']} tiles at unit {tile_unit}), {len(spr_out)} sprites "
          f"({report['spriteTiles4']} 4-bpp + {report['spriteTiles8']} 8-bpp tiles at unit {spr_unit}), {pal.used()} colours, "
          f"video RAM to ${end:05X}, {report['rom']:,} bytes of ROM")
    for n in notes:
        print('wmsgfx: note: ' + n)


# ------------------------------------------------------------------------------------------------ single-asset modes

def opt(argv, name, default=None):
    if name in argv:
        i = argv.index(name)
        return argv[i + 1]
    return default


def single_layer(argv):
    png, prefix = argv[0], argv[1]
    bank = int(opt(argv, '--bank', '0'))
    c, note = load_rgba(png, 255 if '--quantize' in argv else None)
    h, w = c.shape
    mw, mh = map_size_for(w, h, opt(argv, '--map'))
    pal = Palette()
    cmap = pal.add_bank(bank, colours_of(c), os.path.basename(png))
    pool = TilePool(8, flips='--no-flip' not in argv)
    m = convert_layer(index_image(pad_to(c, mw * 8, mh * 8), cmap), pool, mw, mh, '--priority' in argv)
    open(prefix + '.til', 'wb').write(pool.data())
    open(prefix + '.map', 'wb').write(m)
    open(prefix + '.pal', 'wb').write(pal.data()[bank * 512:bank * 512 + 512])
    print(f'wmsgfx: {png}: {len(pool.tiles)} tiles, a {mw} x {mh} map, {len(cmap)} colours in bank {bank}' + (f' ({note})' if note else ''))


def single_sprite(argv):
    png, prefix = argv[0], argv[1]
    bpp = int(opt(argv, '--bpp', '4'))
    fw, fh = (int(v) for v in opt(argv, '--frame', '16x16').split('x'))
    c, note = load_rgba(png, (15 if bpp == 4 else 255) if '--quantize' in argv else None)
    pal = Palette()
    if bpp == 4:
        p = int(opt(argv, '--palette', '0'))
        cmap = pal.reserve_sub(p, colours_of(c), os.path.basename(png))
        paldata = pal.data()[p * 32:p * 32 + 32]
    else:
        b = int(opt(argv, '--bank', '0'))
        cmap = pal.add_bank(b, colours_of(c), os.path.basename(png))
        paldata = pal.data()[b * 512:b * 512 + 512]
    sheet = SpriteSheet(bpp)
    frames = [index_image(f, cmap) for f in split_frames(c, fw, fh, png)]
    sheet.place(frames, fw, fh)
    open(prefix + '.til', 'wb').write(sheet.data())
    open(prefix + '.pal', 'wb').write(paldata)
    print(f'wmsgfx: {png}: {len(frames)} frames of {fw} x {fh}, {sheet.tiles()} tiles at {bpp} bpp, {len(cmap)} colours' + (f' ({note})' if note else ''))


def main(argv):
    if len(argv) < 3 or argv[0] not in ('build', 'layer', 'sprite'):
        print(__doc__)
        return 2
    try:
        if argv[0] == 'build':
            build(argv[1], argv[2])
        elif argv[0] == 'layer':
            single_layer(argv[1:])
        else:
            single_sprite(argv[1:])
    except (GfxError, OSError, KeyError, ValueError) as e:
        print(f'wmsgfx: ERROR: {e}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
