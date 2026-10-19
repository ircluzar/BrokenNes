"""Classify APU-column differences between a Mesen and a BrokenNes trace (state_trace format).

    python apu_shift.py <mesen.trace> <brokennes.trace> [--boot 10]

For every APU column and every frame where the two traces differ, the difference is "explained by a one-frame
sample shift" when Mesen's value at f appears in BrokenNes at f-1 or f+1 AND BrokenNes's value at f appears in Mesen
at f-1 or f+1: the two machines wrote the same values, the trace caught them on different sides of a frame
boundary. Anything else is listed as UNEXPLAINED (a real candidate for an emulation difference).
Also compares the picture/PPU columns, which must be identical.
"""
import sys

APU = ["p1per", "p1vol", "p1len", "p1dut", "p1swp", "p2per", "p2vol", "p2len", "p2dut", "p2swp", "tper", "tlen", "tlin",
       "nper", "nvol", "nlen", "nmod", "dout", "dcur", "drem", "fcs", "st", "en", "flg"]
PIC = ["ntbhash", "oamhash", "palhash", "ctrl", "mask", "stat", "chrhash", "fbhash"]


def load(p):
    hdr, rows = None, {}
    for line in open(p, encoding="utf-8"):
        if line.startswith("#"):
            if "frame|" in line:
                hdr = line.lstrip("#").strip().replace("columns: ", "").split("|")
            continue
        c = line.rstrip("\n").split("|")
        rows[int(c[0])] = c
    return hdr, rows


def main():
    a = sys.argv[1:]
    boot = int(a[a.index("--boot") + 1]) if "--boot" in a else 10
    hm, m = load(a[0])
    hb, b = load(a[1])
    n = min(len(m), len(b))
    print("frames", n)
    for c in PIC:
        i, j = hm.index(c), hb.index(c)
        bad = [f for f in range(boot, n) if m[f][i] != b[f][j]]
        print("%-8s differing frames after boot: %d %s" % (c, len(bad), bad[:10]))
    total = expl = 0
    unexplained = []
    for c in APU:
        i, j = hm.index(c), hb.index(c)
        for f in range(boot, n - 1):
            if m[f][i] == b[f][j]:
                continue
            total += 1
            mv, bv = m[f][i], b[f][j]
            ok_m = mv in (b[f - 1][j], b[f + 1][j])
            ok_b = bv in (m[f - 1][i], m[f + 1][i])
            if ok_m and ok_b:
                expl += 1
            else:
                unexplained.append((c, f, mv, bv, m[f - 1][i], m[f + 1][i], b[f - 1][j], b[f + 1][j]))
    print("APU column-frames differing: %d, explained by a one-frame sample shift: %d, unexplained: %d" % (total, expl, len(unexplained)))
    for u in unexplained[:40]:
        print("  UNEXPLAINED %s@%d mesen=%s bn=%s  (mesen f-1=%s f+1=%s, bn f-1=%s f+1=%s)" % u)


if __name__ == "__main__":
    main()
