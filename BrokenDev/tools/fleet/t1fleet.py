"""Catalog T1 sweep on the E:\\fleet PCs.

    python t1fleet.py kit                       copy the kit (Workshop self-contained, portable Python, Mesen, t1 tools) to every PC
    python t1fleet.py plan --console nes --per-pc 3 [--reverse] [--names OPAL,..] [--limit N] [--tag a]
                                                pick the not-yet-done titles, export shards, push ROMs, start the jobs
    python t1fleet.py poll --tag a              job state on every PC
    python t1fleet.py pull --tag a [--ingest]   results back -> C:\\BrokenNes-work\\fleet\\results\\<tag>\\..., then t1.py ingest

Layout on a PC: C:\\brokennes\\kit (tools), C:\\brokennes\\roms\\<shard> (ROMs + shard.json), out in C:\\brokennes\\roms\\<shard>\\out,
per-job scratch C:\\brokennes\\work\\<shard>. All recorded with hold.py (dirs C:\\brokennes; procs BrokenNes.Workshop Mesen python).
"""
import argparse, json, os, subprocess, sys, time
sys.path.insert(0, os.environ.get("FLEET_DIR", r"E:\fleet"))
from fleet import boxes, run, enc  # noqa: E402
from jobs import put, job, poll  # noqa: E402
from pull import pull  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.join(HERE, "kit")
T1 = os.path.join(KIT, "tools", "compat-t1", "t1.py")
LOCALPY = sys.executable
SHARDS = os.path.join(HERE, "shards")
RESULTS = os.path.join(HERE, "results")


def t1_local(*args):
    env = dict(os.environ, BNT1_VERSION=subprocess.run(["git", "-C", r"C:\BrokenNes-work\fleet_kit_wt", "rev-parse", "--short", "HEAD"], capture_output=True, text=True).stdout.strip())
    return subprocess.run([LOCALPY, T1, *args], env=env, capture_output=True, text=True)


def pending(console, reverse):
    sys.path.insert(0, os.path.join(KIT, "tools", "catalog"))
    import catalog
    sys.path.insert(0, os.path.join(KIT, "tools", "compat-t1"))
    recs = sorted(catalog.load_index(console), key=lambda r: r["title"].lower())
    out = []
    for r in recs:
        if r.get("revisionOf"):
            continue
        full = catalog.get(console, r["sha1"]) or r
        if ((full.get("extra") or {}).get("t1") or {}).get("version") == "t1-2":
            continue
        out.append(r)
    return out[::-1] if reverse else out


def cmd_kit(a):
    print(put(KIT, r"C:\brokennes\kit", a.names))


def cmd_plan(a):
    names = list(boxes(a.names))
    todo = pending(a.console, a.reverse)
    if a.limit:
        todo = todo[:a.limit]
    slots = [(n, i) for i in range(a.per_pc) for n in names]
    shards = {s: [] for s in slots}
    for k, r in enumerate(todo):
        shards[slots[k % len(slots)]].append(r["sha1"])
    plan = {}
    for (n, i), shas in shards.items():
        if not shas:
            continue
        sid = f"{a.console}_{a.tag}_{i}"
        d = os.path.join(SHARDS, a.tag, n, sid)
        os.makedirs(d, exist_ok=True)
        lst = os.path.join(d, "list.txt")
        open(lst, "w").write("\n".join(shas))
        p = t1_local("export", "--console", a.console, "--list", lst, "--out", d)
        if p.returncode:
            sys.exit(p.stderr)
        os.remove(lst)
        plan.setdefault(n, []).append((sid, d, len(shas)))
    for n, items in plan.items():
        for sid, d, cnt in items:
            print(n, sid, cnt, put(d, rf"C:\brokennes\roms\{sid}", [n]).get(n))
    json.dump({n: [x[0] for x in v] for n, v in plan.items()}, open(os.path.join(SHARDS, a.tag, "plan.json"), "w"), indent=1)
    bs = boxes(list(plan))
    for n, items in plan.items():
        for sid, d, cnt in items:
            env = {"BNT1_WORKSHOP": r"C:\brokennes\kit\workshop\BrokenNes.Workshop.exe", "BNT1_MESEN": r"C:\brokennes\kit\mesen\Mesen.exe",
                   "BNT1_SCRATCH": rf"C:\brokennes\work\{sid}", "BNT1_HEAVY": rf"C:\brokennes\work\{sid}\heavy", "BNT1_SLOTS": "1",
                   "BNT1_LANE": "fleet-" + sid}
            cmd = rf"C:\brokennes\kit\py\python.exe -I C:\brokennes\kit\tools\compat-t1\t1.py work --console {a.console} --shard C:\brokennes\roms\{sid}"
            ok = job(bs[n], sid, cmd, r"C:\brokennes", env, limit=a.hours * 3600, low=False)
            print("start", n, sid, cnt, ok)


def load_plan(tag):
    return json.load(open(os.path.join(SHARDS, tag, "plan.json")))


def cmd_poll(a):
    bs = boxes()
    tot = 0
    for n, sids in load_plan(a.tag).items():
        for sid in sids:
            rc, tail = poll(bs[n], sid, 1)
            cnt = run(bs[n], enc(rf"(Get-ChildItem C:\brokennes\roms\{sid}\out -Filter result.json -Recurse -ErrorAction SilentlyContinue).Count"), 60)[1]
            tot += int(cnt or 0)
            print(f"{n:9s} {sid:14s} rc={rc} done={cnt} {tail[-150:]}")
    print("total results:", tot)


def cmd_pull(a):
    bs = boxes()
    for n, sids in load_plan(a.tag).items():
        for sid in sids:
            dst = os.path.join(RESULTS, a.tag, n, sid)
            print(n, sid, pull(bs[n], rf"C:\brokennes\roms\{sid}\out", dst))
            if a.ingest:
                p = t1_local("ingest", "--console", a.console, "--results", dst)
                print("  ", (p.stdout + p.stderr).strip()[-300:])


def main():
    p = argparse.ArgumentParser()
    sp = p.add_subparsers(dest="cmd", required=True)
    k = sp.add_parser("kit"); k.add_argument("--names", default=None)
    q = sp.add_parser("plan"); q.add_argument("--console", default="nes"); q.add_argument("--per-pc", type=int, default=3)
    q.add_argument("--reverse", action="store_true"); q.add_argument("--names", default=None); q.add_argument("--limit", type=int, default=0)
    q.add_argument("--tag", required=True); q.add_argument("--hours", type=int, default=12)
    o = sp.add_parser("poll"); o.add_argument("--tag", required=True)
    u = sp.add_parser("pull"); u.add_argument("--tag", required=True); u.add_argument("--console", default="nes"); u.add_argument("--ingest", action="store_true")
    a = p.parse_args()
    if getattr(a, "names", None):
        a.names = a.names.split(",")
    {"kit": cmd_kit, "plan": cmd_plan, "poll": cmd_poll, "pull": cmd_pull}[a.cmd](a)


if __name__ == "__main__":
    main()
