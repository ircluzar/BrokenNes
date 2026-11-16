"""Offline: manifest write/read/validate, chapter order, and every game folder's data parses."""
import os, sys, json, tempfile
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from lib.package import Package, version_dir
from lib import game

fails = []
d = version_dir("nes", "t", 1, root=tempfile.mkdtemp())
p = Package.create(d, "nes", "t", "T")
p.put_chapter("boot", "Boot", "area"); p.put_chapter("1-1", "1-1", "level", quality={"score": "A"}, notes="ok")
p.put_chapter("boot", "Boot again", "area"); p.save()
q = Package.load(d)
if [c["id"] for c in q.m["chapters"]] != ["boot", "1-1"] or q.chapter("boot")["title"] != "Boot again": fails.append("chapter order/replace")
if q.quality_avg() != 3: fails.append("quality_avg")
if not os.path.exists(os.path.join(d, "notes", "1-1.md")): fails.append("notes file")
try: q.put_chapter("x y", "bad", "level"); q.save(); fails.append("bad id accepted")
except AssertionError: pass
for g in os.listdir(game.GAMES):
    G = game.load(g)
    if not G.milestones or any("id" not in m or "until" not in m for m in G.milestones): fails.append(g + ": milestones")
    for n in G.macros: G.macro(n)
    G.ram(3)
print(json.dumps({"ok": not fails, "failures": fails})); sys.exit(1 if fails else 0)
