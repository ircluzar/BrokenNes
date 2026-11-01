"""Solve for the oracle's interrupt sample points from two observations per loop: the share of interrupts taken after the body (return-address histograms, rethist_1.txt) and the mean entry time (B4a's table, gap in master clocks over ours).
Loop = one body instruction X (length L cycles) + bra.s taken (10).  s_X, s_b = sample offsets from the start of X and of the bra.
"""
def window_and_mean(L, sX, sb):
    P = L + 10
    W = sX - sb + 10
    m = (W * (L - (sb - 10 + sX) / 2) + (P - W) * (P - (sX + L + sb) / 2)) / P
    return W, m

# name: (L, ours-old s_X (start of last bus cycle), ours-old s_b = 6, oracle share after X (histogram), B4a gap in clocks oracle-ours)
loops = {
    'nop':      (4, 0, 0.789, 0),
    'lsl8':     (24, 0, 0.338, 26),
    'mulu':     (70, 0, 0.193, 67),
    'clr_l':    (20, 16, 0.581, 38),
    'add_rmw':  (20, 16, 0.581, 38),
    'movem_out': (72, 68, 0.122, 91),
    'movem_in': (76, 72, 0.120, 91),
}
for name, (L, sXold, share, gap) in loops.items():
    P = L + 10
    Wo = share * P
    sb_old = 6
    Wold, Mold = window_and_mean(L, sXold, sb_old)
    Mo = Mold + gap / 7.0
    # s_X - s_b = Wo - 10; search s_b such that mean = Mo
    best = None
    for k in range(-400, 800):
        sb = k / 10.0
        sX = sb + Wo - 10
        W, m = window_and_mean(L, sX, sb)
        err = abs(m - Mo)
        if best is None or err < best[0]: best = (err, sb, sX, m)
    print('%-10s L=%2d ours-old: W %.1f mean %.2f | oracle: W %.1f mean %.2f (gap %.1f clocks) -> s_b %.1f (from the bra start), s_X %.1f (from X start; X lasts %d)' % (name, L, Wold, Mold, Wo, Mo, gap, best[1], best[2], L))
