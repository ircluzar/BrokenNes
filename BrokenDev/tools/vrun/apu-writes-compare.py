"""Compare the APU register-write streams of Mesen and BrokenNes for one NES run (the exact audio check).

    python apu-writes-compare.py <mesen.wlog.txt> <brokennes.wlog.txt>

Inputs ("frame|addr|value" per write, '#' header lines):
  Mesen:     Mesen.exe <rom> --luaScriptsToLoad <VRUN>\\tools\\apu_write_log.lua --testRunner --timeout=900
             (env VRUN_WLOG_OUT, VRUN_WLOG_FRAMES, VRUN_WLOG_INPUT, VRUN_WLOG_RAMFILL=fceux; the script lives in the user's
             older sys0 clone, PROJECTS\\sys0\\simulations\\VRUN\\tools, not in the VRUN repository)
  BrokenNes: BrokenNes.Workshop.exe --apu-audio --rom <rom> --cpu FIX --ppu FIX --apu FIX --frames N --input <script>
             --ntsc-frame-timing on --write-log <file>

The per-frame APU STATE columns of the trace differential (diff-nes.ps1) are sampled at an instant the two emulators cannot align, so a
value written and changed within a frame can show as a one-frame "blip". The write stream has no sampling instant: if the ordered
(address, value) sequences are identical, the two machines asked the sound chip for exactly the same thing. Frame labels may differ
by the tools' labelling convention (Mesen counts at its end-of-frame event): the histogram of label offsets is printed, not judged.
A write the other side never reaches (the end of the shorter run) is listed. Exit 0 = identical sequences (up to the shorter run's end).
"""
import sys
from collections import Counter


def load(path):
    out = []
    for line in open(path, encoding="utf-8"):
        if line.startswith("#") or not line.strip():
            continue
        f, a, v = line.strip().split("|")
        out.append((int(f), a.lower(), v.lower()))
    return out


def main():
    m, b = load(sys.argv[1]), load(sys.argv[2])
    n = min(len(m), len(b))
    first = next((i for i in range(n) if m[i][1:] != b[i][1:]), None)
    print(f"writes: Mesen {len(m)}, BrokenNes {len(b)}; compared {n}")
    print("frame-label offsets (BrokenNes - Mesen):", dict(Counter(b[i][0] - m[i][0] for i in range(n))))
    for side, rows in (("Mesen", m), ("BrokenNes", b)):
        if len(rows) > n:
            print(f"{side} has {len(rows) - n} more write(s) past the other's end, first: frame {rows[n][0]} ${rows[n][1]}={rows[n][2]}")
    if first is None:
        print(f"IDENTICAL: the first {n} writes agree in address, value and order")
        return 0
    print(f"DIFFERENT at write {first}: Mesen frame {m[first][0]} ${m[first][1]}={m[first][2]}, "
          f"BrokenNes frame {b[first][0]} ${b[first][1]}={b[first][2]}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
