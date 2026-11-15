"""AutoTAS catalog fields: python -I BrokenDev/tools/catalog/test_autotas.py [manifest.json] (a temp catalog root; exit 0 when ok)."""
import json, os, shutil, sys, tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import catalog as c

def main():
    base = tempfile.mkdtemp(prefix="bn-autotas-cat-")
    sha = "0" * 39 + "1"
    c.add(c.new_record("nes", sha, "Test Game"), base)
    pkg = os.path.join(base, "tas", "nes", "test_game", "v1")
    os.makedirs(pkg)
    man = os.path.join(pkg, "autotas.json")
    if len(sys.argv) > 1:
        with open(sys.argv[1], encoding="utf-8") as f: m = json.load(f)
    else:
        m = {"schema": 1, "console": "nes", "game_id": "test_game", "version": 1, "status": "pilot",
             "core": {"cores": "FIX/FIX/FIX", "build": "x", "state_format": "nes:CPU_FIX/PPU_FIX/APU_FIX:gzip1"},
             "verified_at": "2026-11-15T00:00:00Z", "verify": "ok",
             "chapters": [{"id": "boot", "kind": "area", "start_frame": 0, "end_frame": 10, "key_ram": "AB", "quality": {"score": "A"}},
                          {"id": "1-1", "kind": "level", "start_frame": 10, "end_frame": 50, "key_ram": "CD", "quality": {"score": "B"}}]}
    with open(man, "w", encoding="utf-8") as f: json.dump(m, f)
    fails = []
    t = c.autotas_from_manifest(man, base, "pilot")
    if t["path"] != "tas/nes/test_game/v1": fails.append("path " + t["path"])
    if len(sys.argv) == 1 and (t["chapters_done"] != 2 or t["quality_avg"] != 2.5): fails.append("counts " + json.dumps(t))
    r = c.set_autotas("nes", sha, t, base)
    if not c.autotas_available(r): fails.append("not available after a verified pilot")
    r = c.set_autotas("nes", sha, dict(t, verified_at=None), base)
    if c.autotas_available(r): fails.append("available without a verify")
    for bad in ({"status": "done"}, {"nope": 1}, {"version": "1"}, {"chapters_done": 3, "chapters_total": 2}):
        try: c.validate_autotas(bad); fails.append("accepted " + json.dumps(bad))
        except ValueError: pass
    c.main(["--root", base, "autotas-set", "--console", "nes", "--sha1", sha, "--manifest", man, "--status", "complete"])
    if c.get("nes", sha, base)["autotas"]["status"] != "complete": fails.append("cli status")
    c.rebuild_index(base)
    rows = list(c.load_index("nes", base))
    if not rows or rows[0].get("autotas", {}).get("game_id") != m["game_id"]: fails.append("index lacks autotas")
    print(json.dumps({"ok": not fails, "failures": fails, "autotas": c.get("nes", sha, base)["autotas"]}))
    shutil.rmtree(base, ignore_errors=True)
    return 0 if not fails else 1

if __name__ == "__main__":
    sys.exit(main())
