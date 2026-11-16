"""Nested unstick sweep, the PC side: for every built Nested SNES ROM in this shard, try several generic input scripts and record
which one reaches a moving picture. No rebuild: the same ROMs as the full-NES sweep (fast and accurate NMI builds).

    python -I unstick.py <shard dir>      (shard.json: [{slug, mode, smc}], ROMs beside it) -> <shard>\\out\\<slug>_<mode>.json

Moving picture: of the pictures at 1500/2000/2500/3000, at least two differ and are not blank (a frozen title stays one picture).
"""
import json, os, random, subprocess, sys
import numpy as np
from PIL import Image

WS = os.environ.get("UNSTICK_WORKSHOP", r"C:\brokennes\kit\workshop\BrokenNes.Workshop.exe")
FRAMES = 3000
SHOTS = [1500, 2000, 2500, 3000]
PLAY = [(120, "Right"), (20, "Right+A"), (50, "Right"), (16, "Right+B"), (40, "Right"), (24, "A"), (30, ""), (60, "Left"), (12, "B"), (30, "Down"), (30, "Up")]


def play_from(f, end=FRAMES):
    s, i = [], 0
    while f < end:
        d, b = PLAY[i % len(PLAY)]
        s.append((f, b)); f += d; i += 1
    return s


def taps(button, start, every, until, hold=6):
    s = []
    for f in range(start, until, every):
        s += [(f, button), (f + hold, "")]
    return s


def scripts():
    out = {
        "mash_start": taps("Start", 60, 20, 1400, 4) + play_from(1400),
        "hold_a_start": sum([[(f, "A+Start"), (f + 60, "")] for f in range(120, 1400, 300)], []) + play_from(1400),
        "select_start": [(120, "Select"), (126, ""), (200, "Start"), (206, ""), (420, "Select"), (426, ""), (500, "Start"), (506, ""), (700, "A"), (706, "")] + play_from(800),
        "start_2s": taps("Start", 120, 120, 1920) + play_from(1920),
    }
    names = ["A", "B", "Start", "Select", "Up", "Down", "Left", "Right", "A+Right", "B+Left"]
    for seed in (1, 2, 3):
        r = random.Random(seed)
        s = [(f, r.choice(names) if r.random() < 0.7 else "") for f in range(60, FRAMES, 10)]
        out[f"fuzz{seed}"] = s
    return {k: ",".join(f"{f}:{b}" for f, b in sorted(v)) for k, v in out.items()}


def snes_input(nes):
    out = []
    for ev in nes.split(","):
        f, _, b = ev.partition(":")
        out.append(f + ":" + "+".join({"A": "B", "B": "Y"}.get(x, x) for x in b.split("+") if x))
    return ",".join(out)


def moving(pngs):
    imgs = [np.asarray(Image.open(p).convert("L"), dtype=np.int16) for p in pngs if os.path.exists(p)]
    imgs = [i for i in imgs if np.ptp(i) > 8]
    if len(imgs) < 2:
        return False, len(imgs)
    distinct = len({i.tobytes() for i in imgs})
    return distinct >= 2, distinct


def main():
    base = os.path.abspath(sys.argv[1])
    items = json.load(open(os.path.join(base, "shard.json")))
    out = os.path.join(base, "out"); os.makedirs(out, exist_ok=True)
    sc = scripts()
    for n, it in enumerate(items):
        rp = os.path.join(out, f"{it['slug']}_{it['mode']}.json")
        if os.path.exists(rp):
            continue
        res = dict(slug=it["slug"], mode=it["mode"], scripts={})
        for name, nes in sc.items():
            d = os.path.join(base, "work", f"{it['slug']}_{it['mode']}_{name}")
            os.makedirs(d, exist_ok=True)
            cmd = [WS, "--snesrun", "--rom", os.path.join(base, it["smc"]), "--frames", str(FRAMES), "--png-at", ",".join(map(str, SHOTS)),
                   "--out-dir", d, "--input", snes_input(nes)]
            try:
                rc = subprocess.run(cmd, capture_output=True, text=True, timeout=900).returncode
            except subprocess.TimeoutExpired:
                rc = "timeout"
            pngs = sorted(os.path.join(d, f) for f in os.listdir(d) if f.endswith(".png"))
            mv, distinct = moving(pngs)
            res["scripts"][name] = dict(rc=rc, moving=mv, distinct=distinct, pictures=len(pngs))
            if mv:
                res.setdefault("winner", name)
                res["winnerInput"] = res.get("winnerInput") or nes
                os.makedirs(os.path.join(out, "pics"), exist_ok=True)
                for p in pngs[-1:]:
                    os.replace(p, os.path.join(out, "pics", f"{it['slug']}_{it['mode']}_{name}.png"))
                break   # the first script that moves is enough
        json.dump(res, open(rp, "w"), indent=1)
        print(f"[{n + 1}/{len(items)}] {it['slug']} {it['mode']}: {res.get('winner', 'stuck')}", flush=True)


if __name__ == "__main__":
    main()
