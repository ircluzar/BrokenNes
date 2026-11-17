"""start a second (reverse-order) T1 worker on every shard of the given tags: python t1reverse.py snes md gb"""
import json, os, sys
sys.path.insert(0, r"E:\fleet")
from jobs import job  # noqa: E402
from fleet import boxes  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
bs = boxes()
for tag in sys.argv[1:]:
    plan = json.load(open(os.path.join(HERE, "shards", tag, "plan.json")))
    for n, sids in plan.items():
        for sid in sids:
            rid = sid + "r"
            env = {"BNT1_WORKSHOP": r"C:\brokennes\kit\workshop\BrokenNes.Workshop.exe", "BNT1_MESEN": r"C:\brokennes\kit\mesen\Mesen.exe",
                   "BNT1_BIZHAWK": r"C:\brokennes\kit\bizhawk\EmuHawk.exe", "BNT1_SCRATCH": rf"C:\brokennes\work\{rid}",
                   "BNT1_HEAVY": rf"C:\brokennes\work\{rid}\heavy", "BNT1_SLOTS": "1", "BNT1_LANE": "fleet-" + rid}
            cmd = rf"C:\brokennes\kit\py\python.exe -I C:\brokennes\kit\tools\compat-t1\t1.py work --reverse --console {tag} --shard C:\brokennes\roms\{sid}"
            print(n, rid, job(bs[n], rid, cmd, r"C:\brokennes", env, limit=30 * 3600, low=False))
