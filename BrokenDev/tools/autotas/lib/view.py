"""Layer 0 of the Enhanced AutoTAS toolkit: a TEXT view of the screen that works on every console.

The picture comes from the session (framebuffer, so NES/SNES/GB/SMS/GG/Genesis/x86/N64 all work); the
sprite list comes from the console's OAM (`sprites` MCP tool: NES, SNES, WideMess, GB) when it has one.
Nothing here knows a game.

grid(): the screen cut into cells (8 px default). Each cell is one character:
  '.'  mostly the backdrop colour (the most common colour on screen: sky, void)
  a symbol per dominant non-backdrop colour, ranked by how much of the screen it covers ('#' the most)
  sprite cells are letters a..z, one letter per sprite CLUSTER (touching OAM sprites = one object),
  '@' the player cluster when the game says where the player is on screen (runner.json "player_screen": [sx, sy])
  or when `player` is given; trail cells (earlier player positions) are '*'.
Coordinates: the header has column numbers every 4 cells; rows are numbered; a cell = cell_px pixels.
"""
import os, json

SYMS = "#=%+o~:;!&$^"
LETTERS = "abcdefghijklmnopqrstuvwxyz"


def _pixels(png):
    from PIL import Image
    im = Image.open(png).convert("RGB")
    return im.width, im.height, im.load()


def clusters(sprites, gap=2):
    """Groups OAM sprites that touch (within gap px) into objects: [{x,y,w,h,ids,tiles}] (8x8 sprites assumed unless sizes given)."""
    boxes = []
    for s in sprites:
        w = int(s.get("w", 16 if s.get("large") else 8)); h = int(s.get("h", 16 if s.get("large") else 8))
        boxes.append([int(s["x"]), int(s["y"]), w, h, [s.get("index")], [s.get("tile")]])
    merged = True
    while merged:
        merged = False
        for i in range(len(boxes)):
            for j in range(i + 1, len(boxes)):
                a, b = boxes[i], boxes[j]
                if a[0] - gap <= b[0] + b[2] and b[0] - gap <= a[0] + a[2] and a[1] - gap <= b[1] + b[3] and b[1] - gap <= a[1] + a[3]:
                    x0, y0 = min(a[0], b[0]), min(a[1], b[1]); x1, y1 = max(a[0] + a[2], b[0] + b[2]), max(a[1] + a[3], b[1] + b[3])
                    boxes[i] = [x0, y0, x1 - x0, y1 - y0, a[4] + b[4], a[5] + b[5]]; del boxes[j]; merged = True; break
            if merged: break
    return [{"x": b[0], "y": b[1], "w": b[2], "h": b[3], "ids": b[4], "tiles": b[5]} for b in boxes]


def grid(png, sprites=None, cell=8, player=None, trail=(), bg_frac=0.7, legend_in=None):
    """Returns (text, info). png: a screenshot at scale 1. sprites: the OAM list (screen coordinates) or None.
    player: (sx, sy) screen pixel of the player or None. trail: earlier (sx, sy) player pixels.
    legend_in: a previous legend {rgb_hex: sym} so symbols stay the same between calls (pass info['legend'] back)."""
    W, H, px = _pixels(png)
    cols, rows = W // cell, H // cell
    from collections import Counter
    total = Counter(px[x, y] for y in range(0, H, 2) for x in range(0, W, 2))
    backdrop = total.most_common(1)[0][0]
    cellcol = [[None] * cols for _ in range(rows)]
    usage = Counter()
    for r in range(rows):
        for c in range(cols):
            cnt = Counter(px[c * cell + i, r * cell + j] for j in range(0, cell, 2) for i in range(0, cell, 2))
            n = sum(cnt.values()); bgn = cnt.get(backdrop, 0)
            if bgn >= bg_frac * n: continue
            del cnt[backdrop]
            col = cnt.most_common(1)[0][0]; cellcol[r][c] = col; usage[col] += 1
    hexc = lambda c: "%02x%02x%02x" % c
    legend = dict(legend_in or {})
    free = [s for s in SYMS if s not in legend.values()]
    for col, _ in usage.most_common():
        h = hexc(col)
        if h not in legend: legend[h] = free.pop(0) if free else "?"
    g = [["." if cellcol[r][c] is None else legend[hexc(cellcol[r][c])] for c in range(cols)] for r in range(rows)]
    objs = clusters(sprites or [])
    pl = None
    if player is not None:   # the cluster that contains (or is nearest to) the player's pixel
        best = None
        for o in objs:
            dx = max(o["x"] - player[0], 0, player[0] - (o["x"] + o["w"])); dy = 0 if player[1] is None else max(o["y"] - player[1], 0, player[1] - (o["y"] + o["h"]))
            d = dx + dy
            if best is None or d < best[0]: best = (d, o)
        if best and best[0] <= 12: pl = best[1]
    for (tx, ty) in trail:
        c, r = int(tx) // cell, int(ty) // cell
        if 0 <= r < rows and 0 <= c < cols and g[r][c] == ".": g[r][c] = "*"
    names = {}
    k = 0
    for o in objs:
        if o is pl: ch = "@"
        else: ch = LETTERS[k % 26]; k += 1
        names[ch] = o
        for r in range(max(0, o["y"] // cell), min(rows, (o["y"] + o["h"] - 1) // cell + 1)):
            for c in range(max(0, o["x"] // cell), min(cols, (o["x"] + o["w"] - 1) // cell + 1)):
                g[r][c] = ch
    if pl is None and player is not None and player[1] is not None:
        c, r = int(player[0]) // cell, int(player[1]) // cell
        if 0 <= r < rows and 0 <= c < cols: g[r][c] = "@"
    head = "    " + "".join(str((c // 4) % 10) if c % 4 == 0 else " " for c in range(cols))
    lines = [head] + [f"{r:3d} " + "".join(g[r]) for r in range(rows)]
    info = {"rows_text": ["".join(row) for row in g], "cell": cell, "cols": cols, "rows": rows, "backdrop": hexc(backdrop), "legend": legend,
            "objects": {ch: {"x": o["x"], "y": o["y"], "w": o["w"], "h": o["h"], "tiles": o["tiles"][:4]} for ch, o in names.items()}}
    return "\n".join(lines), info


def legend_text(info):
    inv = {v: k for k, v in info["legend"].items()}
    used = "".join(sorted(set(inv)))
    objs = " ".join(f"{ch}@({o['x']},{o['y']} {o['w']}x{o['h']})" for ch, o in info["objects"].items())
    return f"cell={info['cell']}px  '.'=backdrop #{info['backdrop']}  " + " ".join(f"{s}=#{inv[s]}" for s in used) + ("\n objects: " + objs if objs else "")


def stitch(prev_rows, new_rows, max_shift=None):
    """Finds the horizontal shift (cells) that best lines up new_rows with the right part of prev_rows (sprite letters and '@'
    ignored). Generic map stitching from text grids: returns (shift, score)."""
    def solid(ch): return ch not in ".*@" and not ch.isalpha()
    W = len(prev_rows[0]); best = (0, -1.0)
    for s in range(0, (max_shift or W) + 1):
        agree = tot = 0
        for a, b in zip(prev_rows, new_rows):
            for c in range(W - s):
                x, y = a[c + s], b[c]
                if x.isalpha() or y.isalpha() or x in "*@" or y in "*@": continue
                tot += 1; agree += (x == y)
        if tot and agree / tot > best[1] + 1e-9: best = (s, agree / tot)
    return best


def tile_ids(tm, info, png=None):
    """For each framebuffer symbol: the VRAM tile ids found under its cells (from the `tilemap` tool's visible window),
    most common first. Tile ids are the game's own names for things (a door tile, a spike tile), stable across scroll."""
    from collections import Counter, defaultdict
    cell, tiles = info["cell"], [row.split() for row in tm.get("tiles", [])]
    tc = int(tm.get("cell", 8)); fx, fy = int(tm.get("fineX", 0)), int(tm.get("fineY", 0))
    if not tiles: return ""
    by = defaultdict(Counter)
    for r, row in enumerate(info["rows_text"]):
        for c, ch in enumerate(row):
            if ch == "." or ch.isalpha() or ch in "@*": continue
            px, py = c * cell + cell // 2, r * cell + cell // 2
            tr, tcn = (fy + py) // tc, (fx + px) // tc
            if tr < len(tiles) and tcn < len(tiles[tr]): by[ch][tiles[tr][tcn]] += 1
    return "  ".join(f"{ch}:" + ",".join(t for t, _ in cnt.most_common(6)) for ch, cnt in sorted(by.items(), key=lambda kv: -sum(kv[1].values())))
