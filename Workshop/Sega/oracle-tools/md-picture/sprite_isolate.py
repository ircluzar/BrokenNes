"""Take one random scene (family, seed, index), clear the planes so that only its sprites show, and build variants of it as a scene ROM: sprite_isolate.py out.bin family seed index [--variants il2,il0,h40]
Used to find out which sprite cells the oracle drops on a line where our model cuts the sprite list (a 4-pixel difference in a fresh-seed scene). Writes out.bin and out.json (scene meta, as scenefuzz).
Variants: 'same' (the scene as it is, planes cleared), 'il0' (no interlace: sprite Y is then in lines, so the scene is rebuilt with the same SAT rows halved), 'h40' (H40 instead of H32)."""
import sys, os, json, random, copy
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import scenefuzz


def clear_planes(s):
    regs = s['regs']; h40 = s['h40']
    ws, hs = regs[16] & 3, regs[16] >> 4 & 3
    # name tables of plane A, plane B and the window: zero (tile 0, no priority); tile 0's pattern: zero (transparent)
    a = (regs[2] & 0x38) << 10; b = (regs[4] & 7) << 13; w = (regs[3] & (0x3C if h40 else 0x3E)) << 10
    ex = list(s.get('extra_writes', []))
    ex.append((0, [0] * 32))
    for base in (a, b, w): ex.append((base, [0] * 2048))
    # the zeros may overlap the sprite table or the horizontal scroll table: write those again last
    sat = s['sat']; hs = s['hscroll']
    ex.append((s['sat_addr'], [sat[k] << 8 | sat[k + 1] for k in range(0, len(sat), 2)]))
    ex.append((s['hs_addr'], [hs[k] << 8 | hs[k + 1] for k in range(0, len(hs), 2)]))
    s['extra_writes'] = ex


def main():
    out, fam, seed, idx = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4])
    variants = ['same']
    if '--variants' in sys.argv: variants = sys.argv[sys.argv.index('--variants') + 1].split(',')
    rng = random.Random(seed)
    scenes = [scenefuzz.make_scene(rng, i, fam) for i in range(idx + 1)]
    base = scenes[idx]
    outs = []
    for k, v in enumerate(variants):
        s = copy.deepcopy(base)
        s['index'] = k
        if v == 'h40': s['h40'] = True; s['regs'][12] = s['regs'][12] | 0x81
        if v == 'il0': s['interlace'] = 0; s['regs'][12] = s['regs'][12] & ~6
        clear_planes(s)
        outs.append(s)
    scenefuzz.build(outs, out)
    meta = [{kk: vv for kk, vv in s.items() if kk not in ('sat', 'hscroll', 'cram', 'vsram', 'extra_writes')} for s in outs]
    json.dump(meta, open(os.path.splitext(out)[0] + '.json', 'w'), indent=1)
    print('wrote', out, len(outs), 'scenes', variants)


if __name__ == '__main__':
    main()
