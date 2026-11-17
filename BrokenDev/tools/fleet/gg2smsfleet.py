"""GG -> SMS wide roster (gg2sms_test.py, branch nested-gg2sms-r3) on the E:\\fleet PCs.

    python gg2smsfleet.py kit [--ws <Workshop bin dir>] [--tools <nested-everything dir>]
    python gg2smsfleet.py run --tag w1 [--roster roster-wide.json] [--done wide-r3.json] [--per-pc 3] [--names ..]
    python gg2smsfleet.py poll --tag w1
    python gg2smsfleet.py collect --tag w1 [--out merged.json] [--pictures]

The Workshop build is framework-dependent; the PCs run it on C:\\brokennes\\kit\\dotnet (a copy of this PC's .NET 10 runtime,
DOTNET_ROOT). Each job: gg2sms_test.py <shard roster> --jobs 1 --resume --results <shard>\\results.json (restartable).
"""
import argparse, json, os, shutil, sys
sys.path.insert(0, os.environ.get("FLEET_DIR", r"E:\fleet"))
from fleet import boxes  # noqa: E402
from jobs import put, job, poll  # noqa: E402
from pull import pull  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
WS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "stage_g2s_ws")   # self-contained publish (Application Control blocks plain apphosts on some PCs)
TOOLS = r"C:\BrokenNes-work\gg2sms_r3\BrokenDev\tools\nested-everything"
ROSTER = r"C:\BrokenNes-work\gg2sms_r3\BrokenDev\projects\nested-gg2sms\roster-wide.json"
DONE = r"C:\BrokenNes-work\gg2sms_r3_work\wide-r3.json"
STAGE = os.path.join(HERE, "gg2sms_stage")
RF = r"C:\brokennes\gg2sms"


def cmd_kit(a):
    print("dotnet", put(os.path.join(HERE, "stage_dotnet", "dotnet"), r"C:\brokennes\kit\dotnet", a.names))
    print("ws", put(a.ws, RF + r"\ws", a.names))
    print("tools", put(a.tools, RF + r"\BrokenDev\tools\nested-everything", a.names))
    print("project", put(os.path.join(a.tools, "..", "..", "projects", "nested-gg2sms"), RF + r"\BrokenDev\projects\nested-gg2sms", a.names))


def tagdir(tag):
    return os.path.join(STAGE, tag)


def cmd_run(a):
    ro = json.load(open(a.roster, encoding="utf-8"))
    done = {r["id"] for r in json.load(open(a.done, encoding="utf-8"))} if a.done and os.path.exists(a.done) else set()
    games = []
    for g in ro["games"]:
        rom = g["rom"] if os.path.isabs(g["rom"]) else os.path.join(ro["folder"], g["rom"])
        if g["id"] in done or not os.path.exists(rom):
            continue
        games.append((g, rom))
    names = list(boxes(a.names))
    slots = [(n, i) for i in range(a.per_pc) for n in names]
    shards = {}
    for k, gr in enumerate(games):
        shards.setdefault(slots[k % len(slots)], []).append(gr)
    plan = {}
    for (n, i), gs in shards.items():
        sid = f"g2s_{a.tag}_{i}"
        d = os.path.join(tagdir(a.tag), n, sid); shutil.rmtree(d, ignore_errors=True); os.makedirs(os.path.join(d, "roms"))
        out = []
        for g, rom in gs:
            fn = g["id"] + os.path.splitext(rom)[1]
            shutil.copy2(rom, os.path.join(d, "roms", fn))
            x = dict(g, rom=fn)
            if g.get("profile"):
                src = os.path.join(os.path.dirname(os.path.abspath(a.roster)), g["profile"])
                shutil.copy2(src, os.path.join(d, os.path.basename(src))); x["profile"] = os.path.basename(src)
            out.append(x)
        json.dump(dict(ro, folder=rf"{RF}\runs\{sid}\roms", games=out), open(os.path.join(d, "roster.json"), "w"), indent=1)
        print(n, sid, len(gs), put(d, rf"{RF}\runs\{sid}", [n]).get(n))
        plan.setdefault(n, []).append(sid)
    json.dump(plan, open(os.path.join(tagdir(a.tag), "plan.json"), "w"), indent=1)
    bs = boxes(list(plan))
    for n, sids in plan.items():
        for sid in sids:
            R = rf"{RF}\runs\{sid}"
            cmd = (rf"C:\brokennes\kit\py\python.exe -I {RF}\BrokenDev\tools\nested-everything\gg2sms_test.py {R}\roster.json --work {R}\work --jobs 1 --resume "
                   rf"--results {R}\out\results.json --workshop {RF}\ws\BrokenNes.Workshop.exe")
            env = {"DOTNET_ROOT": r"C:\brokennes\kit\dotnet", "DOTNET_MULTILEVEL_LOOKUP": "0", "PATH": r"C:\brokennes\kit\py;%PATH%"}
            print("start", n, sid, job(bs[n], sid, f'mkdir {R}\\out 2>nul & {cmd}', R, env, limit=a.hours * 3600, low=False))


def cmd_poll(a):
    bs = boxes()
    for n, sids in json.load(open(os.path.join(tagdir(a.tag), "plan.json"))).items():
        for s in sids:
            print(n, s, poll(bs[n], s, 2))


def cmd_collect(a):
    bs = boxes()
    merged = {}
    for n, sids in json.load(open(os.path.join(tagdir(a.tag), "plan.json"))).items():
        for s in sids:
            got = os.path.join(tagdir(a.tag), "pulled", s)
            shutil.rmtree(got, ignore_errors=True)
            print(n, s, pull(bs[n], rf"{RF}\runs\{s}\{'work' if a.pictures else 'out'}", got))
            if a.pictures:
                pull(bs[n], rf"{RF}\runs\{s}\out", os.path.join(got, "_out"))
            rj = os.path.join(got, "_out" if a.pictures else "", "results.json")
            if os.path.exists(rj):
                for r in json.load(open(rj, encoding="utf-8")):
                    r["fleetHost"] = n
                    merged[r["id"]] = r
    out = a.out or os.path.join(tagdir(a.tag), "results.json")
    json.dump(list(merged.values()), open(out, "w", encoding="utf-8"), indent=1)
    print(len(merged), "results ->", out)


def main():
    p = argparse.ArgumentParser()
    sp = p.add_subparsers(dest="cmd", required=True)
    k = sp.add_parser("kit"); k.add_argument("--ws", default=WS); k.add_argument("--tools", default=TOOLS); k.add_argument("--names", default=None)
    r = sp.add_parser("run"); r.add_argument("--tag", required=True); r.add_argument("--roster", default=ROSTER); r.add_argument("--done", default=DONE)
    r.add_argument("--per-pc", type=int, default=3); r.add_argument("--names", default=None); r.add_argument("--hours", type=int, default=10)
    o = sp.add_parser("poll"); o.add_argument("--tag", required=True)
    c = sp.add_parser("collect"); c.add_argument("--tag", required=True); c.add_argument("--out", default=""); c.add_argument("--pictures", action="store_true")
    a = p.parse_args()
    if getattr(a, "names", None):
        a.names = a.names.split(",")
    {"kit": cmd_kit, "run": cmd_run, "poll": cmd_poll, "collect": cmd_collect}[a.cmd](a)


if __name__ == "__main__":
    main()
