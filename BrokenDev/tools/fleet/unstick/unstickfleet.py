"""Nested unstick sweep on the fleet: the frozen + black titles of the full-NES sweep, fast and accurate NMI builds, generic input scripts.
    python unstickfleet.py run [--per-pc 4]     shards -> PCs, jobs started
    python unstickfleet.py collect              results -> C:\\BrokenNes-work\\nested_compat\\unstick\\ (results.json, inputs_found.json, pics)
"""
import json, os, shutil, sys, time
sys.path.insert(0, r"E:\fleet")
from fleet import boxes  # noqa: E402
from jobs import put, job, poll  # noqa: E402
from pull import pull  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
NC = r"C:\BrokenNes-work\nested_compat"
OUT = os.path.join(NC, "unstick")
STAGE = os.path.join(HERE, "stage")


def items():
    auto = json.load(open(os.path.join(NC, "fullnes", "auto.json")))
    slugs = [x["slug"] for x in auto if x["auto"] in ("Frozen", "Black screen")]
    out = []
    for s in slugs:
        for mode, root in (("fast", "fullnes"), ("acc", "fullnes_acc")):
            smc = os.path.join(NC, root, "snes", s + "_fast.smc")
            if os.path.exists(smc):
                out.append(dict(slug=s, mode=mode, src=smc))
    return out


def cmd_run(per_pc):
    its = items()
    names = list(boxes())
    slots = [(n, i) for i in range(per_pc) for n in names]
    shards = {}
    for k, it in enumerate(its):
        shards.setdefault(slots[k % len(slots)], []).append(it)
    plan = {}
    print("tool", put(os.path.join(HERE, "unstick.py"), r"C:\brokennes\unstick"))
    for (n, i), lst in shards.items():
        sid = f"uns_{i}"
        d = os.path.join(STAGE, n, sid); shutil.rmtree(d, ignore_errors=True); os.makedirs(d)
        sh = []
        for it in lst:
            fn = f"{it['slug']}_{it['mode']}.smc"
            shutil.copy2(it["src"], os.path.join(d, fn))
            sh.append(dict(slug=it["slug"], mode=it["mode"], smc=fn))
        json.dump(sh, open(os.path.join(d, "shard.json"), "w"))
        print(n, sid, len(sh), put(d, rf"C:\brokennes\unstick\{sid}", [n]).get(n), flush=True)
        ok = job(boxes()[n], sid, rf"C:\brokennes\kit\py\python.exe -I C:\brokennes\unstick\unstick.py C:\brokennes\unstick\{sid}", r"C:\brokennes\unstick", {}, limit=10 * 3600, low=False)
        plan.setdefault(n, []).append(sid)
        print("start", n, sid, ok, flush=True)
    json.dump(plan, open(os.path.join(HERE, "plan.json"), "w"), indent=1)


def cmd_collect():
    plan = json.load(open(os.path.join(HERE, "plan.json")))
    bs = boxes()
    os.makedirs(OUT, exist_ok=True)
    res = {}
    for n, sids in plan.items():
        for sid in sids:
            d = os.path.join(HERE, "pulled", n, sid)
            shutil.rmtree(d, ignore_errors=True)
            print(n, sid, pull(bs[n], rf"C:\brokennes\unstick\{sid}\out", d))
            for f in os.listdir(d) if os.path.isdir(d) else []:
                if f.endswith(".json"):
                    r = json.load(open(os.path.join(d, f))); r["host"] = n
                    res[f"{r['slug']}_{r['mode']}"] = r
            if os.path.isdir(os.path.join(d, "pics")):
                shutil.copytree(os.path.join(d, "pics"), os.path.join(OUT, "pics"), dirs_exist_ok=True)
    json.dump(list(res.values()), open(os.path.join(OUT, "results.json"), "w"), indent=1)
    found = {}
    for r in res.values():
        if r.get("winner") and (r["slug"] not in found or r["mode"] == "fast"):
            found[r["slug"]] = dict(input=r["winnerInput"], frames=3000, script=r["winner"], nmi=r["mode"], note="unstick sweep 2026-11-15: first generic script that reaches a moving picture")
    json.dump(found, open(os.path.join(OUT, "inputs_found.json"), "w"), indent=1)
    slugs = {r["slug"] for r in res.values()}
    print(f"{len(res)} runs, {len(slugs)} titles, unstuck {len(found)}")


if __name__ == "__main__":
    if sys.argv[1] == "run":
        cmd_run(int(sys.argv[2]) if len(sys.argv) > 2 else 4)
    else:
        cmd_collect()
