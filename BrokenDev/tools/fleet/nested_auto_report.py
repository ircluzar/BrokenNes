"""Full-NES Nested sweep: an automatic first-opinion grade per game (no eye grades exist for most of the catalog) + counts per mapper +
failure clusters. Reads results.json / meas.json / builds.json / logs in this folder (nestedfleet.py collect). Writes auto.json, auto.md.

Auto grades: Unsupported mapper, Build fails (grade.py), Crash (snesrun or cert failed), Black screen (SNES picture blank at both
checkpoints), Frozen (the two SNES pictures identical), Runs (picture changes; split by lag: Runs-fast when the SNES lag frames are at
most NES lag + 120 over the 1800-frame run, else Runs-slow). An eye grade from grades.json wins where it exists (key eye)."""
import collections, json, os, re
import numpy as np
from PIL import Image

R = os.path.dirname(os.path.abspath(__file__))
res = {x["slug"]: x for x in json.load(open(os.path.join(R, "results.json")))}
meas = {x["slug"]: x for x in json.load(open(os.path.join(R, "meas.json")))}
roster = {x["slug"]: x for x in json.load(open(os.path.join(R, "roster.json")))}


def pic(slug, f):
    p = os.path.join(R, "meas", f"{slug}_fast_f{f:05d}.png")
    return np.asarray(Image.open(p).convert("L"), dtype=np.int16) if os.path.exists(p) else None


def build_error(slug):
    p = os.path.join(R, "logs", f"build_{slug}.txt")
    if not os.path.exists(p):
        return "no build log"
    lines = [l.strip() for l in open(p, encoding="utf-8", errors="replace") if l.strip()]
    for l in reversed(lines):
        if re.search(r"error|refused|not supported|exception|failed|unsupported", l, re.I):
            return re.sub(r"[0-9A-Fa-f]{4,}|\d+", "#", l)[:110]
    return re.sub(r"\d+", "#", lines[-1])[:110] if lines else "empty log"


out = []
for slug, r in roster.items():
    x = res.get(slug, {})
    m = meas.get(slug, {})
    g = x.get("grade", "missing")
    why = ""
    if g in ("Unsupported mapper", "Build fails"):
        auto = g
        why = build_error(slug)
    elif not m or m.get("built") is False:
        auto, why = "Build fails", build_error(slug)
    elif m.get("snesrun_rc") not in (0, None) or m.get("cert_rc") not in (0, 1, None):
        auto, why = "Crash", f"snesrun {m.get('snesrun_rc')} cert {m.get('cert_rc')}"
    else:
        a, b = pic(slug, 300), pic(slug, 1500)
        if b is None:
            auto, why = "Crash", "no picture"
        elif np.ptp(b) <= 8 and (a is None or np.ptp(a) <= 8):
            auto = "Black screen"
        elif a is not None and a.shape == b.shape and int(np.abs(a - b).sum()) == 0:
            auto = "Frozen"
        else:
            c = m.get("cert") or {}
            nl, sl = c.get("nesLag"), c.get("snesLag")
            auto = "Runs-fast" if (nl is not None and sl is not None and sl <= nl + 120) else "Runs-slow"
            why = f"lag {nl}/{sl}"
    out.append(dict(slug=slug, name=r.get("name"), mapper=r.get("mapper"), auto=auto, eye=g if g not in ("ungraded", "missing") else None, why=why))

json.dump(out, open(os.path.join(R, "auto.json"), "w"), indent=1)
tot = collections.Counter(o["auto"] for o in out)
bym = collections.defaultdict(collections.Counter)
for o in out:
    bym[o["mapper"]][o["auto"]] += 1
clusters = collections.Counter((o["auto"], o["why"]) for o in out if o["auto"] in ("Build fails", "Unsupported mapper", "Crash"))
cols = ["Runs-fast", "Runs-slow", "Frozen", "Black screen", "Crash", "Build fails", "Unsupported mapper"]
lines = ["# Full-NES Nested sweep (auto first opinion)", "", "Totals: " + ", ".join(f"{k} {tot[k]}" for k in cols if tot[k]), "",
         "| mapper | n | " + " | ".join(cols) + " |", "|---|---|" + "---|" * len(cols)]
for mp, c in sorted(bym.items(), key=lambda kv: -sum(kv[1].values())):
    lines.append(f"| {mp} | {sum(c.values())} | " + " | ".join(str(c[k] or "") for k in cols) + " |")
lines += ["", "## Top failure clusters", ""] + [f"- {n} x {g}: {w}" for (g, w), n in clusters.most_common(20)]
open(os.path.join(R, "auto.md"), "w", encoding="utf-8").write("\n".join(lines) + "\n")
print("\n".join(lines[:4]))
