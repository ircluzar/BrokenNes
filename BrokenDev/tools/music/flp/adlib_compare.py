"""AdLib songs heard in the running games: the importer's model of an FL song vs the OPL2 writes a game really made.

  py -3 -I adlib_compare.py <expected.txt> <expect-tick-hz> <actual.txt> <actual-tick-hz> <voice,voice,...> [--label name]

expected.txt: FruityHost `adlib-expect <song.flp> sv|vaults <ticks> <out>` (the driver's C# twin playing the importer's blob).
actual.txt:   Workshop `--x86session <exe> --opllog <out>` (x86 console: frame = 1/70.09 s), or a 95 build's OPLLOG.TXT.
Each line "tick reg value" (hex). Both streams go through a register shadow (a write of the value already there is dropped: the 95
builds do that themselves), then per voice: notes = key-ons (F-number, block), pitch changes while keyed (slides), key-offs; patch =
the voice's operator / C0 registers at its first key-on. The actual notes are compared with the expected ones from the first
key-on, in order, over as many as the run produced (the song loops)."""
import sys

OPM = [0x00, 0x01, 0x02, 0x08, 0x09, 0x0A, 0x10, 0x11, 0x12]
OPC = [0x03, 0x04, 0x05, 0x0B, 0x0C, 0x0D, 0x13, 0x14, 0x15]


def load(path):
    out = []
    for ln in open(path):
        p = ln.split()
        if len(p) == 3:
            out.append((int(p[0]), int(p[1], 16) & 0xFF if int(p[1], 16) < 0x100 else -1, int(p[2], 16)))
    return [w for w in out if w[1] >= 0]   # (38Ah bank writes of the OPL3 path are not OPL2 registers)


def patch_regs(v):
    m, c = OPM[v], OPC[v]
    return [b + m for b in (0x20, 0x40, 0x60, 0x80, 0xE0)] + [b + c for b in (0x20, 0x60, 0x80, 0xE0)] + [0xC0 + v]


def events(writes, voices):
    sh = {}
    ev = {v: [] for v in voices}
    patch = {}
    keyed = {v: False for v in voices}
    for t, r, val in writes:
        if sh.get(r) == val:
            continue
        sh[r] = val
        for v in voices:
            if r in (0xA0 + v, 0xB0 + v):
                b = sh.get(0xB0 + v, 0)
                f = sh.get(0xA0 + v, 0) | (b & 3) << 8
                blk = (b >> 2) & 7
                on = bool(b & 0x20)
                if on and not keyed[v]:
                    ev[v].append(('on', f, blk, t))
                    if v not in patch:
                        patch[v] = {x: sh.get(x) for x in patch_regs(v)}
                elif on and ev[v] and ev[v][-1][1:3] != (f, blk):
                    ev[v].append(('pitch', f, blk, t))
                elif not on and keyed[v]:
                    ev[v].append(('off', f, blk, t))
                keyed[v] = on
    return ev, patch


def m(r):
    return 0x0F if 0xC0 <= r <= 0xC8 else 0xFF


def main():
    a = sys.argv[1:]
    label = a[a.index('--label') + 1] if '--label' in a else a[2]
    exp, ehz, act, ahz, voices = a[0], float(a[1]), a[2], float(a[3]), [int(x) for x in a[4].split(',')]
    ee, ep = events(load(exp), voices)
    ae, ap = events(load(act), voices)
    total_ok = total = 0
    print(f"== {label}")
    for v in voices:
        e, g = ee[v], ae[v]
        n = min(len(e), len(g))
        mism = [(i, e[i], g[i]) for i in range(n) if e[i][:3] != g[i][:3]]
        ons = sum(1 for x in g[:n] if x[0] == 'on')
        # timing: each event's time from the voice's first key-on, actual vs expected (seconds)
        drift = max((abs((g[i][3] - g[0][3]) / ahz - (e[i][3] - e[0][3]) / ehz) for i in range(n)), default=0.0)
        total += n; total_ok += n - len(mism)
        print(f"  voice {v}: {n - len(mism)}/{n} note events match (key-ons {ons}, expected stream has {len(e)}, the run {len(g)}); "
              f"largest timing difference {drift * 1000:.0f} ms")
        for i, x, y in mism[:8]:
            print(f"    #{i}: expected {x[0]} F {x[1]:03X} block {x[2]}, got {y[0]} F {y[1]:03X} block {y[2]} (tick {y[3]})")
        pe, pa = ep.get(v), ap.get(v)
        if pe is None or pa is None:
            print(f"    patch: {'no key-on in the expected stream' if pe is None else 'no key-on in the run'}")
        else:
            diff = [f"{r:02X} {pe[r]:02X}->{pa[r]:02X}" for r in pe if (pe[r] or 0) & m(r) != (pa[r] or 0) & m(r)]   # (C0: bits 4-5 are the OPL3 output bits, ignored by the OPL2)
            print(f"    patch at the first key-on: {len(pe) - len(diff)}/{len(pe)} registers match" + (f"; differ: {', '.join(diff)}" if diff else ""))
    print(f"  TOTAL {total_ok}/{total} note events match")
    return 0 if total_ok == total and total > 0 else 1


if __name__ == '__main__':
    sys.exit(main())
