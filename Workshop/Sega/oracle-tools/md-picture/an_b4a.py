"""Analysis helpers for the Track B4a interrupt probes (see probes_b4a.py)."""

LINE = 3420


def h8_to_offset(h8, h40=True):
    """Master clocks from a line's H counter 0 to the first moment the counter's top 8 bits read h8 (the inverse of MdVdp.HCounter9 >> 1)."""
    h9 = h8 * 2
    if h40:
        if h9 < 0x16D: return h9 * 8
        return 2920 + -(-(h9 - 0x1C9) * (LINE - 2920) // 55)
    if h9 < 0x128: return h9 * 10
    return 2960 + (h9 - 0x1D2) * 10


def abs_time(hv, h40=True, ref_line=223, step=2640):
    """Master clocks since the start of line `ref_line` for an HV reading (V counter byte, H counter top byte): the V counter steps `step` clocks into a line, so a reading at or past it belongs to the line before."""
    v, h8 = hv >> 8, hv & 255
    off = h8_to_offset(h8, h40)
    line = v - (1 if off >= step else 0)
    return (line - ref_line) * LINE + off


def decode_pairs(ram, frame, pairs, h40=True, ref_line=223):
    """-> (status words, times of the status reads) of one frame of a vint_stream_hv probe; the status read k lies between HV reads k-1 and k (the first one is extrapolated by 84 clocks)."""
    base = frame * pairs * 2
    st = [ram[base + 2 * k] for k in range(pairs)]
    hv = [abs_time(ram[base + 2 * k + 1], h40, ref_line) for k in range(pairs)]
    t = []
    for k in range(pairs):
        prev = hv[k - 1] if k > 0 else hv[0] - 168
        t.append((prev + hv[k]) / 2)
    return st, t


def brackets(ram, frames, pairs, bit, h40=True, ref_line=223):
    """For a status bit: the largest time of a status read that still saw it clear and the smallest time of one that saw it set, over all frames (after the first), and the per-frame lists.
    The event happens in (lo, hi]."""
    lo, hi = -10 ** 9, 10 ** 9
    per = []
    for f in range(1, frames):
        st, t = decode_pairs(ram, f, pairs, h40, ref_line)
        k = next((i for i, x in enumerate(st) if x >> bit & 1), None)
        if k is None or k == 0: continue
        per.append((t[k - 1], t[k]))
        lo = max(lo, t[k - 1]); hi = min(hi, t[k])
    return lo, hi, per
