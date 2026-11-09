"""D1 (Timeflow T6): writes a pad script that plays, for comparison.play's "input" (a frame:buttons list, the Movie script form).

  python playscript.py --start "60:A,61:" --from 120 --frames 6000 --seed 7 [--buttons A,B] [--step 20] [--avoid Start]

From --from on, every --step frames a new choice from the seed (a xorshift, so the same arguments give the same script): walk right
(most often), left or stand, with jump (the first --buttons entry) a third of the time, fire (the second) a quarter, Up / Down now and
then. Start is never pressed after --start (it pauses the ports). --hold "f0-f1:Buttons" lines override spans (a shop's Up, a menu).
"""
import argparse


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--start', default='')
    ap.add_argument('--from', dest='frm', type=int, default=120)
    ap.add_argument('--frames', type=int, default=6000)
    ap.add_argument('--seed', type=int, default=1)
    ap.add_argument('--step', type=int, default=20)
    ap.add_argument('--buttons', default='A,B')
    ap.add_argument('--right', type=int, default=2, help='weight of Right among Right/Left/none (out of 4: Right gets this many)')
    ap.add_argument('--hold', action='append', default=[])
    a = ap.parse_args()
    jump, fire = (a.buttons.split(',') + ['', ''])[:2]
    x = (a.seed * 2654435761 + 1) & 0xFFFFFFFF or 1

    def rnd():
        nonlocal x
        x ^= (x << 13) & 0xFFFFFFFF; x ^= x >> 17; x ^= (x << 5) & 0xFFFFFFFF
        return x

    holds = []
    for h in a.hold:
        span, b = h.split(':', 1)
        f0, f1 = (int(v) for v in span.split('-'))
        holds.append((f0, f1, b))
    out = [s for s in a.start.split(',') if s]
    last = None
    for f in range(a.frm, a.frames, a.step):
        hb = next((b for f0, f1, b in holds if f0 <= f < f1), None)
        if hb is not None:
            p = hb
        else:
            r = rnd()
            d = r % 4
            p = ['Right'] if d < a.right else (['Left'] if d < 3 else [])
            if jump and (r >> 8) % 3 == 0: p.append(jump)
            if fire and (r >> 12) % 4 == 0: p.append(fire)
            if (r >> 16) % 8 == 0: p.append('Up')
            elif (r >> 20) % 8 == 0: p.append('Down')
            p = '+'.join(p)
        if p != last:
            out.append(f'{f}:{p}')
            last = p
    print(','.join(out))


if __name__ == '__main__':
    main()
