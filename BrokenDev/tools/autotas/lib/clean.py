"""AutoTAS clean-up pass (generic): state/<ch>.raw.state -> state/<ch>.clean.state using games/<game>/cleanup.json.

python -I clean.py --pkg P --game G --rom R [--ch boot,1-1]
Each rule {"addr": "$0736", "value": [4]} is written after loading the raw state, then 1 frame runs and the clean state is saved.
Check: the clean state is loaded again and runs --check frames holding nothing; the RAM rules must still read back
(the game did not undo them) and the player must not die. A picture of the result goes to thumb/<ch>.clean.png.
"""
import os, sys, json, argparse
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from lib.bridge import Bridge
from lib import game as gamelib
from lib.run import MCP_EXE

ap = argparse.ArgumentParser()
ap.add_argument("--pkg", required=True); ap.add_argument("--game", required=True); ap.add_argument("--rom", required=True)
ap.add_argument("--ch"); ap.add_argument("--check", type=int, default=600)
a = ap.parse_args()
g = gamelib.load(a.game)
rules = g.cleanup
cfg = json.load(open(os.path.join(g.dir, "runner.json"), encoding="utf-8"))
man = json.load(open(os.path.join(a.pkg, "autotas.json"), encoding="utf-8"))
chs = a.ch.split(",") if a.ch else [c["id"] for c in man["chapters"]]
out = []
b = Bridge(exe=MCP_EXE)
try:
    for ch in chs:
        raw = os.path.join(a.pkg, "state", ch + ".raw.state"); clean = os.path.join(a.pkg, "state", ch + ".clean.state")
        s = b.call("open_rom", path=a.rom)["session"]
        b.call("load_state", session=s, path=raw)
        for rl in rules:
            b.call("write_memory", session=s, spec=rl["addr"], data=rl["value"])
        b.call("play_step", session=s, frames=1, scale=0)
        b.call("save_state", session=s, path=clean)
        b.call("load_state", session=s, path=clean)
        res = b.call("play_step", session=s, frames=a.check, scale=0, milestones=["died=" + cfg["dead"]] if cfg.get("dead") else None, ram=cfg["ram"])
        bad = []
        for rl in rules:
            got = b.call("read_memory", session=s, spec=rl["addr"], length=len(rl["value"]))
            gb = list(bytes.fromhex(got["hex"].replace(" ", "")))
            if gb != rl["value"]: bad.append(f'{rl["addr"]}: {gb} != {rl["value"]} ({rl.get("what", "")})')
        try: b.screenshot(s, os.path.join(a.pkg, "thumb", ch + ".clean.png"))
        except Exception: pass
        b.call("close_session", session=s)
        row = {"chapter": ch, "clean": clean, "died": res.get("stoppedBy") == "milestone", "changed_back": bad, "ram": res.get("ram")}
        row["ok"] = not row["died"]
        out.append(row)
finally:
    b.stop()
print(json.dumps({"ok": all(r["ok"] for r in out), "chapters": out}, indent=1))
