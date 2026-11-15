"""T2 game list for the fleet: every catalog title of a console minus the piloted ones, sorted by mapper, PCs assigned round-robin.
    python gamelist.py --console nes --exclude C:/BrokenNes-work/cc_pilot/games.json --per-pc 3 --out <list.json>
Each game: title, region, rom (path on X:, read only), member (zip member or ""), sha1 (catalog key), fileSha1, mapper, known, pc, slot.
Round-robin: game i goes to PC i % 7; slot = which of that PC's --per-pc lanes (a lane plays its games one after the other)."""
import argparse, glob, json, os

PCS = ["OPAL", "CORAL", "ZIRCON", "SAPPHIRE", "EMERALD", "PEARL", "DIAMOND"]
ap = argparse.ArgumentParser()
ap.add_argument("--console", default="nes"); ap.add_argument("--catalog", default=r"T:\BrokenDev\Catalog")
ap.add_argument("--exclude", action="append", default=[]); ap.add_argument("--per-pc", type=int, default=3)
ap.add_argument("--pcs", default=",".join(PCS)); ap.add_argument("--out", required=True)
a = ap.parse_args()
pcs = a.pcs.split(",")
skip = set()
for f in a.exclude:
    for g in json.load(open(f, encoding="utf-8")): skip.add(g["sha1"].lower())

def known(r):
    bits = []
    t1 = (r.get("extra") or {}).get("t1") or {}
    if t1: bits.append("T1 %s: %s" % (t1.get("grade", "?"), t1.get("why", "")))
    elif r.get("grade"): bits.append("catalog grade %s (tier %s)" % (r.get("grade"), r.get("tier")))
    else: bits.append("no earlier result")
    if r.get("tags"): bits.append("tags " + ",".join(r["tags"]))
    return "; ".join(bits)

games = []
for f in glob.glob(os.path.join(a.catalog, "records", a.console, "*", "*.json")):
    r = json.load(open(f, encoding="utf-8"))
    if r["sha1"].lower() in skip: continue
    d, fi = r.get("dump") or {}, r.get("file") or {}
    games.append({"title": r.get("title", ""), "region": r.get("region", ""), "rom": fi.get("path", ""), "member": fi.get("member", ""),
                  "sha1": r["sha1"], "fileSha1": d.get("fileSha1", ""), "mapper": d.get("mapper"), "known": known(r)})
games.sort(key=lambda g: (g["mapper"] if isinstance(g["mapper"], int) else 9999, g["title"].lower()))
for i, g in enumerate(games):
    g["pc"] = pcs[i % len(pcs)]; g["slot"] = (i // len(pcs)) % a.per_pc
json.dump({"console": a.console, "perPc": a.per_pc, "pcs": pcs, "excluded": len(skip), "count": len(games), "games": games},
          open(a.out, "w", encoding="utf-8"), indent=1)
print(len(games), "games ->", a.out, "(excluded", len(skip), ")")
