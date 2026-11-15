"""End to end over the bridge (default: on a fleet PC): two chapters of the planted movie-test game.

python -I tests/test_fleet_e2e.py --rom <autotas_test.nes> --out <dir> [--pc CORAL | --local]
The ROM is the MCP's own planted test game (BrokenNes.Mcp.exe --autotas-test --out D writes D/autotas_test.nes).
Checks: save_state path / record mark state_path write LOCAL files through the bridge, load_state path pushes them,
a segment recorded after load_state starts from that state, milestones stop play_step, stitch + verify from power-on.
"""
import os, sys, json, shutil, argparse
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from lib.bridge import Bridge
from lib.package import Package, version_dir
from lib import chapter

ap = argparse.ArgumentParser(); ap.add_argument("--rom", required=True); ap.add_argument("--out", required=True)
ap.add_argument("--pc", default="CORAL"); ap.add_argument("--local", action="store_true"); a = ap.parse_args()
if os.path.exists(a.out): shutil.rmtree(a.out)
pkg_dir = version_dir("nes", "autotas_test", 1, root=os.path.join(a.out, "tas"))
pkg = Package.create(pkg_dir, "nes", "autotas_test", "AutoTAS test game")
json.dump({"milestones": [{"id": "score", "until": "[$13] >= 40"}]}, open(os.path.join(pkg_dir, "milestones.json"), "w"))
fails = []
def check(c, w):
    if not c: fails.append(w)

with Bridge(pc=None if a.local else a.pc) as b:
    c0 = chapter.Chapter(b, pkg_dir, "boot", a.rom)
    c0.begin()
    c0.step(frames=30)
    hit = c0.play_until(["boot=[$11]==1"], buttons=["Start"], max_frames=60)
    check(hit == "boot", f"boot milestone: {c0.last}")
    sub = c0.save_sub_state("after_boot")
    check(os.path.getsize(sub) > 100, "save_state path wrote no local file")
    c0.finish()
    check(os.path.getsize(chapter.raw_state_path(pkg_dir, "boot")) > 100, "record mark state_path wrote no local file")
    check(os.path.getsize(chapter.thumb_path(pkg_dir, "boot")) > 100, "no thumbnail")
    pkg.put_chapter("boot", "Boot", "area")

    c1 = chapter.Chapter(b, pkg_dir, "score", a.rom, ram=["score=[$13]"])
    c1.begin(from_state=chapter.raw_state_path(pkg_dir, "boot"))
    ms = json.load(open(os.path.join(pkg_dir, "milestones.json")))["milestones"]
    hit = c1.play_until([f'{m["id"]}={m["until"]}' for m in ms], buttons=["A"], max_frames=200)
    check(hit == "score" and c1.last["ram"]["score"] >= 40, f"score milestone: {c1.last}")
    c1.finish()
    pkg.put_chapter("score", "Score 40", "level", quality={"deaths": 0, "frames": c1.frames, "score": "A", "notes": ""})
pkg.save()

st = chapter.stitch(pkg_dir); check(st.get("ok"), f"stitch: {st}")
v = chapter.verify(pkg_dir, a.rom); check(v.get("ok"), f"verify: {v}")
print(json.dumps({"ok": not fails, "failures": fails, "pc": None if a.local else a.pc, "stitch": st,
                  "verify": {k: v.get(k) for k in ("ok", "keyframes", "chaptersOk", "framesRun")}, "package": pkg_dir}))
sys.exit(1 if fails else 0)
