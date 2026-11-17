"""Nested compat sweep (tools/compat/sweep.py of the Nested fork) on the E:\\fleet PCs.

    python nestedfleet.py kit [--fork <fork dir>]            fork tools + injector build -> C:\\brokennes\\nested\\fork on every PC
    python nestedfleet.py run --tag r7 --core <core dir> [--roster roster.json] [--only a,b] [--per-pc 3] [--names OPAL,..]
                              [--sweep-args "--no-autoplay ..."] [--root <local root>]
        shards the roster over the PCs (one sweep.py --jobs 1 per job), pushes ROMs + core, runs, waits, pulls every job's root
        back and merges them into the local --root (default C:\\BrokenNes-work\\fleet\\nested\\<tag>): snes\\, meas\\, logs\\,
        profile\\, feedback\\, builds.json, meas.json; then runs grade.py there -> results.json / summary.json, the same as a local
        sweep.py run.
    python nestedfleet.py poll --tag r7 / collect --tag r7     (state; pull+merge again without re-running)

The kit's Workshop is the self-contained build from BrokenNes main (C:\\brokennes\\kit\\workshop); pwsh 7 is a copy of this PC's
(C:\\brokennes\\kit\\pwsh, put first on PATH for the job); Python is the kit's portable 3.11.
"""
import argparse, json, os, shutil, subprocess, sys, time
sys.path.insert(0, os.environ.get("FLEET_DIR", r"E:\fleet"))
from fleet import boxes, run, enc  # noqa: E402
from jobs import put, job, poll  # noqa: E402
from pull import pull  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
FORK = r"C:\Users\philt\OneDrive\Documents\PROJECTS\VRUN_Nes_Dev\Nested_VRUN\Project-Nested-core"
ROSTER = r"C:\BrokenNes-work\nested_compat\roster.json"
STAGE = os.path.join(HERE, "nested_stage")
RF = r"C:\brokennes\nested"


def cmd_kit(a):
    st = os.path.join(STAGE, "fork")
    shutil.rmtree(st, ignore_errors=True)
    shutil.copytree(os.path.join(a.fork, "tools"), os.path.join(st, "tools"),
                    ignore=shutil.ignore_patterns("__pycache__", "out", "*.smc", "*.nes", ".git"))
    os.makedirs(os.path.join(st, "tools", "m30pipe", "out"), exist_ok=True)
    shutil.copytree(os.path.join(a.fork, "Project Nested", "bin", "Release"), os.path.join(st, "Project Nested", "bin", "Release"))
    print("fork", put(st, RF + r"\fork", a.names))
    if a.pwsh:
        print("pwsh", put(r"C:\Program Files\PowerShell\7", r"C:\brokennes\kit\pwsh", a.names))


def tagdir(tag):
    return os.path.join(STAGE, "runs", tag)


def cmd_run(a):
    names = list(boxes(a.names))
    ro = [r for r in json.load(open(a.roster)) if r.get("slug") and r.get("rom") and not r.get("missing")]
    if a.only:
        want = set(a.only.split(",")); ro = [r for r in ro if r["slug"] in want]
    slots = [(n, i) for i in range(a.per_pc) for n in names]
    shards = {}
    for k, r in enumerate(ro):
        shards.setdefault(slots[k % len(slots)], []).append(r)
    td = tagdir(a.tag); os.makedirs(td, exist_ok=True)
    plan = {}
    corename = "core_" + a.tag
    for n in names:
        print("core", n, put(a.core, rf"{RF}\{corename}", [n]).get(n))
    for (n, i), rs in shards.items():
        sid = f"nst_{a.tag}_{i}"
        d = os.path.join(td, n, sid); shutil.rmtree(d, ignore_errors=True); os.makedirs(os.path.join(d, "roms"))
        out = []
        for r in rs:
            fn = r["slug"] + ".nes"
            shutil.copy2(r["rom"], os.path.join(d, "roms", fn))
            out.append(dict(r, rom=rf"{RF}\runs\{sid}\roms\{fn}"))
        json.dump(out, open(os.path.join(d, "roster.json"), "w"), indent=1)
        print(n, sid, len(rs), put(d, rf"{RF}\runs\{sid}", [n]).get(n))
        plan.setdefault(n, []).append(sid)
    json.dump(dict(plan=plan, root=a.root, roster=os.path.abspath(a.roster)), open(os.path.join(td, "plan.json"), "w"), indent=1)
    bs = boxes(list(plan))
    for n, sids in plan.items():
        for sid in sids:
            root = rf"{RF}\runs\{sid}\root"
            cmd = (rf"C:\brokennes\kit\py\python.exe -I {RF}\{a.fork_dir}\tools\compat\sweep.py --root {root} --roster {RF}\runs\{sid}\roster.json "
                   rf"--core {RF}\{corename} --workshop C:\brokennes\kit\workshop\BrokenNes.Workshop.exe --python C:\brokennes\kit\py\python.exe --jobs 1 {a.sweep_args}")
            env = {"PATH": r"C:\brokennes\kit\pwsh;C:\brokennes\kit\py;%PATH%"}
            env.update(dict(kv.split("=", 1) for kv in a.env))
            print("start", n, sid, job(bs[n], sid, cmd, rf"{RF}\runs\{sid}", env, limit=a.hours * 3600, low=False))
    if a.wait:
        wait(a.tag); collect(a.tag)


def wait(tag):
    plan = json.load(open(os.path.join(tagdir(tag), "plan.json")))["plan"]
    bs = boxes()
    left = {(n, s) for n, ss in plan.items() for s in ss}
    t0 = time.time()
    while left:
        time.sleep(30)
        for n, s in list(left):
            rc, tail = poll(bs[n], s)
            if rc is not None:
                print(f"{n} {s} rc={rc} {time.time() - t0:.0f}s {tail[-120:]}", flush=True); left.discard((n, s))


def collect(tag):
    info = json.load(open(os.path.join(tagdir(tag), "plan.json")))
    root = info["root"] or os.path.join(HERE, "nested", tag)
    os.makedirs(root, exist_ok=True)
    bs = boxes()
    meas, builds = {}, {}
    mp = os.path.join(root, "meas.json")
    if os.path.exists(mp):
        meas = {x["slug"]: x for x in json.load(open(mp))}
    for n, sids in info["plan"].items():
        for sid in sids:
            got = os.path.join(tagdir(tag), "pulled", sid)
            shutil.rmtree(got, ignore_errors=True)
            print("pull", n, sid, pull(bs[n], rf"{RF}\runs\{sid}\root", got))
            for sub in ("snes", "meas", "logs", "profile", "feedback", "exe"):
                if sub != "exe" and os.path.isdir(os.path.join(got, sub)):
                    shutil.copytree(os.path.join(got, sub), os.path.join(root, sub), dirs_exist_ok=True)
            for x in json.load(open(os.path.join(got, "meas.json"))) if os.path.exists(os.path.join(got, "meas.json")) else []:
                x["smc"] = os.path.join(root, "snes", os.path.basename(x.get("smc", "")))
                x["fleetHost"] = n
                meas[x["slug"]] = x
            if os.path.exists(os.path.join(got, "builds.json")):
                for x in json.load(open(os.path.join(got, "builds.json"))):
                    builds[x["slug"]] = x
    json.dump(list(meas.values()), open(mp, "w"), indent=1)
    if builds:
        json.dump(list(builds.values()), open(os.path.join(root, "builds.json"), "w"), indent=1)
    roster = info.get("roster") or ROSTER
    subprocess.call([sys.executable, "-I", os.path.join(FORK, "tools", "compat", "grade.py"), "--root", root, "--roster", roster])
    print("merged into", root)


def cmd_poll(a):
    plan = json.load(open(os.path.join(tagdir(a.tag), "plan.json")))["plan"]
    bs = boxes()
    for n, sids in plan.items():
        for s in sids:
            print(n, s, poll(bs[n], s, 2))


def main():
    p = argparse.ArgumentParser()
    sp = p.add_subparsers(dest="cmd", required=True)
    k = sp.add_parser("kit"); k.add_argument("--fork", default=FORK); k.add_argument("--names", default=None); k.add_argument("--pwsh", action="store_true")
    r = sp.add_parser("run"); r.add_argument("--tag", required=True); r.add_argument("--core", required=True)
    r.add_argument("--roster", default=ROSTER); r.add_argument("--only", default=""); r.add_argument("--per-pc", type=int, default=3)
    r.add_argument("--names", default=None); r.add_argument("--sweep-args", default=""); r.add_argument("--root", default="")
    r.add_argument("--hours", type=int, default=6); r.add_argument("--wait", action="store_true")
    r.add_argument("--fork-dir", default="fork", help="the fork copy on the PCs under C:\\brokennes\\nested (fork_x: the feedback/settings hooks)")
    r.add_argument("--env", action="append", default=[], help="NAME=VALUE for the jobs (fork_x: NESTED_FEEDBACK_ALL=1, NESTED_EXTRA=a=b;c=d)")
    for name in ("poll", "collect"):
        q = sp.add_parser(name); q.add_argument("--tag", required=True)
    a = p.parse_args()
    if getattr(a, "names", None):
        a.names = a.names.split(",")
    if a.cmd == "kit": cmd_kit(a)
    elif a.cmd == "run": cmd_run(a)
    elif a.cmd == "poll": cmd_poll(a)
    else: collect(a.tag)


if __name__ == "__main__":
    main()
