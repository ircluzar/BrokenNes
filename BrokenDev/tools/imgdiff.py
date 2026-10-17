"""Compare two sets of screenshots frame by frame (BrokenNes against an oracle emulator).

    python imgdiff.py <dirA> <prefixA> <dirB> <prefixB> --frames 60,300,900 [--out report.json] [--diff-dir dir] [--crop-top N]

For every frame N it reads <dirA>/<prefixA><N>.png and <dirB>/<prefixB><N>.png (a file name pattern is "<prefix>" + number + ".png"; the number may
be zero padded on one side), compares the pixels and prints how many differ. A size mismatch is reported, never papered over (a different overscan
setting is the usual cause), unless --crop-top removes rows from the taller picture's top first (and says so). With --diff-dir a picture painting
the differing pixels magenta over a dimmed copy is written for each frame that differs.

Exit code 0 when every compared frame is pixel-identical, 1 otherwise, 2 on a usage or file error.
"""
import argparse
import glob
import json
import os
import re
import sys

import numpy as np
from PIL import Image


def find(directory, prefix, n):
    for pat in (f"{prefix}{n}.png", f"{prefix}{n:05d}.png", f"{prefix}{n:04d}.png"):
        p = os.path.join(directory, pat)
        if os.path.exists(p):
            return p
    # BrokenNes names: <tag>_snes_f00300.png; accept any file ending in f<number>.png with that prefix
    for p in glob.glob(os.path.join(directory, prefix + "*.png")):
        m = re.search(r"f0*(\d+)\.png$", p)
        if m and int(m.group(1)) == n:
            return p
    return None


def load(p):
    return np.asarray(Image.open(p).convert("RGB"), dtype=np.uint8)


def crop_pair(ia, ib, a):
    note = ""
    if ia.shape != ib.shape:
        cut = a.crop_top + a.crop_bottom
        if cut and ia.shape[1] == ib.shape[1] and abs(ia.shape[0] - ib.shape[0]) == cut:
            def trim(img):
                return img[a.crop_top:img.shape[0] - a.crop_bottom]
            if ia.shape[0] > ib.shape[0]:
                ia = trim(ia)
            else:
                ib = trim(ib)
            note = f"cropped {a.crop_top} rows from the top and {a.crop_bottom} from the bottom of the taller picture"
        else:
            return None, None, f"size {ia.shape[1]}x{ia.shape[0]} vs {ib.shape[1]}x{ib.shape[0]}"
    return ia, ib, note


def scan(a, frames):
    lo, hi = (int(x) for x in a.scan.split(":"))
    table = {}   # frame -> {offset: differing pixels}
    for n in frames:
        pb = find(a.dir_b, a.prefix_b, n)
        if not pb:
            print(f"  frame {n:>6}: MISSING picture of {a.label_b}")
            return 1
        ib = load(pb)
        table[n] = {}
        for k in range(lo, hi + 1):
            pa = find(a.dir_a, a.prefix_a, n + k)
            if not pa:
                continue
            ia, ib2, note = crop_pair(load(pa), ib, a)
            if ia is None:
                print(f"  frame {n:>6}: {note}")
                return 1
            table[n][k] = int(np.any(np.abs(ia.astype(np.int16) - ib2.astype(np.int16)) > a.tolerance, axis=2).sum())
    offsets = sorted({k for t in table.values() for k in t})
    print("  differing pixels of B(N) against A(N+offset), per frame:")
    print("  " + "frame".rjust(8) + "".join(f"{('off ' + str(k)):>10}" for k in offsets))
    for n in frames:
        print("  " + str(n).rjust(8) + "".join(f"{table[n].get(k, -1):>10}" for k in offsets))
    constant = [k for k in offsets if all(table[n].get(k, 10**9) <= a.max_pixels for n in frames)]
    best = {n: min(table[n], key=lambda k: table[n][k]) for n in frames}
    every = all(min(table[n].values()) <= a.max_pixels for n in frames)
    summary = {"offsets": offsets, "table": {str(n): {str(k): v for k, v in t.items()} for n, t in table.items()}, "constantIdenticalOffsets": constant, "bestOffsetPerFrame": {str(n): k for n, k in best.items()}, "maxPixels": a.max_pixels, "worstResidualAtBestOffset": max(min(t.values()) for t in table.values()), "everyFrameIdenticalAtSomeOffset": every, "allIdentical": every}
    if a.out:
        with open(a.out, "w", encoding="utf-8") as f:
            json.dump(summary, f, indent=2)
    if every and constant:
        print(f"  RESULT: every frame is identical at the constant offset {constant[0]} ({a.label_a} picture at count N+{constant[0]} equals {a.label_b} after N frames)")
        return 0
    if every:
        print(f"  RESULT: every frame is identical at ITS OWN best offset, but not at one constant offset: {best} (a timing step of a frame or more between the emulators somewhere in the run; the pictures themselves agree)")
        return 0
    bad = [n for n in frames if min(table[n].values()) > a.max_pixels]
    print(f"  RESULT: frames {bad} are not identical at any offset in the window; best offsets: {best}")
    return 1


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dir_a"); ap.add_argument("prefix_a"); ap.add_argument("dir_b"); ap.add_argument("prefix_b")
    ap.add_argument("--frames", required=True)
    ap.add_argument("--out", default="")
    ap.add_argument("--diff-dir", default="")
    ap.add_argument("--crop-top", type=int, default=0, help="rows to remove from the top of the TALLER picture when the heights differ")
    ap.add_argument("--crop-bottom", type=int, default=0, help="rows to remove from the bottom of the TALLER picture when the heights differ")
    ap.add_argument("--label-a", default="A"); ap.add_argument("--label-b", default="B")
    ap.add_argument("--max-pixels", type=int, default=0, help="a frame counts as matching when at most this many pixels differ (animation-phase residue); the numbers are always printed")
    ap.add_argument("--tolerance", type=int, default=0, help="a pixel differs only when some channel differs by MORE than this (1 forgives the 5-bit to 8-bit expansion convention: x*255/31 against (x<<3)|(x>>2))")
    ap.add_argument("--scan", default="", help="lo:hi - A holds pictures at counts N+lo..N+hi (<prefixA><count>.png); report the offset at which B(N) matches A(N+offset)")
    a = ap.parse_args()
    frames = [int(x) for x in a.frames.split(",") if x.strip()]
    if a.scan:
        return scan(a, frames)
    rows, worst = [], 0
    any_bad = False
    for n in frames:
        pa, pb = find(a.dir_a, a.prefix_a, n), find(a.dir_b, a.prefix_b, n)
        if not pa or not pb:
            rows.append({"frame": n, "status": "missing", "detail": f"{a.label_a}: {pa}, {a.label_b}: {pb}"})
            any_bad = True
            continue
        ia, ib = load(pa), load(pb)
        note = ""
        if ia.shape != ib.shape:
            cut = a.crop_top + a.crop_bottom
            if cut and ia.shape[1] == ib.shape[1] and abs(ia.shape[0] - ib.shape[0]) == cut:
                def trim(img):
                    return img[a.crop_top:img.shape[0] - a.crop_bottom]
                if ia.shape[0] > ib.shape[0]:
                    ia = trim(ia)
                else:
                    ib = trim(ib)
                note = f"cropped {a.crop_top} rows from the top and {a.crop_bottom} from the bottom of the taller picture"
            else:
                rows.append({"frame": n, "status": "size", "detail": f"{a.label_a} {ia.shape[1]}x{ia.shape[0]} vs {a.label_b} {ib.shape[1]}x{ib.shape[0]}"})
                any_bad = True
                continue
        diff = np.any(np.abs(ia.astype(np.int16) - ib.astype(np.int16)) > a.tolerance, axis=2)
        count = int(diff.sum())
        worst = max(worst, count)
        row = {"frame": n, "status": "identical" if count == 0 else "differs", "differingPixels": count, "of": int(diff.size), "note": note}
        if count:
            any_bad = True
            ys, xs = np.nonzero(diff)
            row["bounds"] = [int(xs.min()), int(ys.min()), int(xs.max()), int(ys.max())]
            if a.diff_dir:
                os.makedirs(a.diff_dir, exist_ok=True)
                canvas = (ib.astype(np.float32) * 0.35).astype(np.uint8)
                canvas[diff] = (255, 0, 255)
                Image.fromarray(canvas).save(os.path.join(a.diff_dir, f"diff_f{n:05d}.png"))
        rows.append(row)
    for r in rows:
        if r["status"] == "identical":
            print(f"  frame {r['frame']:>6}: identical ({r['of']} pixels){'  ' + r['note'] if r['note'] else ''}")
        elif r["status"] == "differs":
            print(f"  frame {r['frame']:>6}: {r['differingPixels']} of {r['of']} pixels differ, bounds {r['bounds']}")
        else:
            print(f"  frame {r['frame']:>6}: {r['status'].upper()}: {r.get('detail', '')}")
    summary = {"frames": rows, "allIdentical": not any_bad, "worstDifferingPixels": worst}
    if a.out:
        with open(a.out, "w", encoding="utf-8") as f:
            json.dump(summary, f, indent=2)
    return 1 if any_bad else 0


if __name__ == "__main__":
    sys.exit(main())
