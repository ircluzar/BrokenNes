"""The song room of the CTW SMS .flp template: how long the title song's four stream arrays can be (one length for the four)
without the game's far data needing another bank.

    python -I ctwsms_room.py <build>/gen <song> <probe region>

Reads a probe build's fab2c output (gen/fab_game.h: FAR_<label> = bank * 16384 + offset; gen/fab_bank<N>.c: each data bank's
bytes) and re-runs fab2c's far-data packing (tools/fab2c.py: every far array in source order, a whole array per bank, the next
bank when it does not fit) with the song's four arrays at each length. Prints "region <R> free <F> banks <first>-<last>": the
largest R that packs into the probe's banks, and F = the free bytes those banks had in the probe (holes at bank ends and the
last bank's tail). The build then makes the template at R and checks the arrays are where this said (build-ctwsms.ps1).
"""
import re
import sys
from pathlib import Path

BANK = 16384
gen, song, probe = Path(sys.argv[1]), sys.argv[2], int(sys.argv[3])
hdr = (gen / "fab_game.h").read_text(encoding="utf-8")
labels = sorted((int(a), n) for n, a in re.findall(r"#define FAR_(\w+) \(\(farp\)(\d+)UL\)", hdr))
banks = {}
for f in gen.glob("fab_bank*.c"):
    m = re.search(r"fab_bank(\d+)\[(\d+)\]", f.read_text(encoding="utf-8"))
    banks[int(m.group(1))] = int(m.group(2))
if not labels or not banks:
    raise SystemExit(f"{gen}: no far data (FAR_ labels / fab_bank files)")
sizes = []
for i, (addr, name) in enumerate(labels):
    bank, off = divmod(addr, BANK)
    nxt = labels[i + 1][0] if i + 1 < len(labels) else None
    end = nxt - bank * BANK if nxt is not None and nxt // BANK == bank else banks[bank]
    sizes.append((name, end - off))
mine = [f"music_{song}_{ch}" for ch in ("square1", "square2", "triangle", "noise")]
for n in mine:
    sz = dict(sizes).get(n)
    if sz != probe:
        raise SystemExit(f"{n} is {sz} bytes in the probe build, expected the probe region {probe}")
first, last = labels[0][0] // BANK, max(banks)


def pack(r):
    """The bank fab2c ends in, and each label's place, with the song's arrays at r bytes."""
    bank, used, place = first, 0, {}
    for name, sz in sizes:
        if name in mine:
            sz = r
        if used + sz > BANK:
            bank += 1
            used = 0
        place[name] = bank * BANK + used
        used += sz
    return bank, place


b0, p0 = pack(probe)
if b0 != last or any(p0[n] != a for a, n in labels):
    raise SystemExit("re-packing the probe build does not give the probe's layout: fab2c packs differently than this script assumes")
free = sum(BANK - banks.get(b, 0) for b in range(first, last + 1))
lo, hi = probe, probe + free // 4 + 1
while lo + 1 < hi:            # largest r that stays in the probe's banks
    mid = (lo + hi) // 2
    if pack(mid)[0] <= last:
        lo = mid
    else:
        hi = mid
r = lo
while r > probe and pack(r)[0] > last:
    r -= 1
_, place = pack(r)
print(f"region {r} free {free} banks {first}-{last} " + " ".join(f"{n[len('music_' + song + '_'):]}=0x{place[n]:X}" for n in mine))
