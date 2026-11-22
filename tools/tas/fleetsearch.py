"""T3 fleet input search driver (docs/projects/tas-format.md "Search").

    python -I tools/tas/fleetsearch.py --run RUN.tasrun.json --chapter ID --spec SEARCH.json --rom ROM --name NAME
        [--shards 20] [--bin tas3a] [--frame 0] [--n64] [--ws PATH] [--scratch DIR] [--wall 60] [--cap 2] [--pack-on-fleet]

1. pack: the chapter's start (state at --frame) + a one-chapter run + ramdb/refs/ROM (Workshop --tas search-pack; locally, or on a fleet PC
   with --pack-on-fleet: N64 chapters that start deep in a power-on run).
2. per round R: NAME_rR = --shards agent-tier jobs via fleet\\submit.py n64 (generic bash on the PC kit; NES ones flagged soft_rdp so every
   PC takes them); each runs `--tas search PACK --spec --parents --round R --shard i/N` with the Workshop build C:\\brokennes\\x86\\bin\\<bin>
   (push a build first: fleet\\x86sync.py --bin NAME=pubdir). Results are pulled by the keeper to C:\\BrokenNes-work\\n64\\fleet\\NAME_rR.
3. merge locally (--tas search-merge, every round so far): the top K become the next round's parents; the last round writes the best chapter
   back (replayed, trimmed at the score goal + tail, played twice by the runner and blessed): SCRATCH\\final\\best\\edited.tasrun.json.
"""
import argparse, glob, json, os, shutil, subprocess, sys, time

FLEET = r"C:\BrokenNes-work\fleet"
N64OUT = r"C:\BrokenNes-work\n64\fleet"
G2 = r"C:\BrokenNes-work\g2"
NO_VULKAN = {"AMBER", "QUARTZ"}   # fleetq: these run N64 only with the software RDP


def ws(a, *args):
    cmd = [a.ws] + list(args) if a.ws.endswith(".exe") else ["dotnet", a.ws] + list(args)
    print("+", " ".join(cmd), flush=True)
    r = subprocess.run(cmd, text=True, capture_output=True)
    sys.stdout.write(r.stdout[-4000:]); sys.stderr.write(r.stderr[-2000:])
    if r.returncode:
        sys.exit(f"workshop failed rc={r.returncode}")
    return r.stdout


def holders(binname):
    """PCs whose x86 kit holds this Workshop build (fleet_inventory.json)."""
    try:
        inv = json.load(open(os.path.join(FLEET, "x86_inventory.json"), encoding="utf-8"))
        try:
            held = set(json.load(open(os.path.join(FLEET, "dedicated.json"), encoding="utf-8")))   # PCs kept for one test: nothing scheduled there
        except (OSError, ValueError):
            held = set()
        return sorted(k for k, v in inv.items() if f"bin/{binname}.zip" in v.get("verified", []) and k not in held)
    except (OSError, ValueError):
        return []


def submit(a, name, items):
    pcs = [p for p in holders(a.bin) if not (a.n64 and p in NO_VULKAN)]   # N64: one renderer for every shard (hashes differ between RDPs)
    if pcs:   # pin each item to a PC that has the build (round robin)
        items = [dict(it, pc=pcs[k % len(pcs)]) for k, it in enumerate(items)]
    print(f"  {name}: {len(items)} items on {pcs or 'any PC'}", flush=True)
    sp = os.path.join(a.scratch, name + "_items.json")
    json.dump(items, open(sp, "w"), indent=1)
    cmd = [sys.executable, "-I", os.path.join(FLEET, "submit.py"), "n64", name, "--spec", sp, "--cap", str(a.cap), "--note", "TAS T3 search"] + (["--pcs", a.pcs] if a.pcs else [])
    print("+", " ".join(cmd), flush=True)
    subprocess.run(cmd, check=True)


def wait(name, ids, timeout_min):
    t0 = time.time(); got = {}
    while time.time() - t0 < timeout_min * 60:
        for i in ids:
            if i in got:
                continue
            for pat in ("results.json", "pack.tasrun.json", "best.json"):
                for f in glob.glob(os.path.join(N64OUT, name, i, "*", "**", pat), recursive=True):
                    got[i] = os.path.dirname(f)
            if i not in got and glob.glob(os.path.join(N64OUT, name, i, "*", "job.log")):   # pulled at the job's end: finished without results
                got[i] = None
                print(f"  {i}: finished without results: {glob.glob(os.path.join(N64OUT, name, i, '*', 'job.log'))[0]}", flush=True)
        print(f"  {name}: {len(got)}/{len(ids)} done ({int(time.time() - t0)} s)", flush=True)
        if len(got) == len(ids):
            return got
        time.sleep(30)
    print(f"  {name}: timed out with {len(got)}/{len(ids)}", flush=True)
    return got


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--run", required=True); ap.add_argument("--chapter", required=True); ap.add_argument("--spec", required=True)
    ap.add_argument("--rom", default=""); ap.add_argument("--name", required=True); ap.add_argument("--shards", type=int, default=20)
    ap.add_argument("--bin", default="tas3a"); ap.add_argument("--frame", type=int, default=0); ap.add_argument("--n64", action="store_true")
    ap.add_argument("--ws", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Workshop", "bin", "Release", "net10.0-windows", "BrokenNes.Workshop.dll"))
    ap.add_argument("--scratch", default=""); ap.add_argument("--wall", type=int, default=60); ap.add_argument("--cap", type=int, default=2)
    ap.add_argument("--pack-on-fleet", action="store_true"); ap.add_argument("--timeout", type=int, default=240)
    ap.add_argument("--pcs", default="", help="only these PCs (A,B): the ones holding --bin")
    ap.add_argument("--retries", type=int, default=3)
    ap.add_argument("--g2", type=int, default=0, help="extra shards on Garage2 (g2run.sh run, bg slots), beside the fleet ones")
    ap.add_argument("--pack", default="", help="an existing pack dir (skip step 1)")
    a = ap.parse_args()
    a.ws = os.path.abspath(a.ws)
    a.scratch = os.path.abspath(a.scratch or os.path.join(r"T:\BrokenDev\scratch\tas3", a.name))
    os.makedirs(a.scratch, exist_ok=True)
    spec = json.load(open(a.spec)); rounds = int(spec.get("rounds", 1))
    exe = f"/c/brokennes/x86/bin/{a.bin}/BrokenNes.Workshop.exe"
    base = {"heavy": False, "wall": a.wall, "soft_rdp": not a.n64}
    pack = a.pack or os.path.join(a.scratch, "pack")
    # 1. pack
    if not a.pack:
        if a.pack_on_fleet:
            src = os.path.join(a.scratch, "src"); shutil.rmtree(src, ignore_errors=True); os.makedirs(src)
            rd = os.path.dirname(os.path.abspath(a.run))
            for f in os.listdir(rd):
                if os.path.isfile(os.path.join(rd, f)):
                    shutil.copy2(os.path.join(rd, f), src)
            files = [src] + ([a.rom] if a.rom else [])
            romarg = f" --rom $JOBDIR/{os.path.basename(a.rom)}" if a.rom else ""
            # the run's script may sit outside its folder (sm64: UAT/n64/compat/scripts): the caller copies it next to the run and points the run at it
            submit(a, a.name + "_pack", [dict(base, id="pack", files=files, cmds=[f'"{exe}" --tas search-pack "$JOBDIR/src/{os.path.basename(a.run)}" --chapter {a.chapter} --frame {a.frame}{romarg} --out "$N64_WORK/pack"'])])
            got = wait(a.name + "_pack", ["pack"], a.timeout)
            if not got.get("pack"):
                sys.exit("pack job failed")
            shutil.rmtree(pack, ignore_errors=True); shutil.copytree(got["pack"], pack)
        else:
            ws(a, "--tas", "search-pack", a.run, "--chapter", a.chapter, "--frame", str(a.frame), "--out", pack, *(["--rom", a.rom] if a.rom else []))
    if a.pack:   # the jobs find it as $JOBDIR/pack
        pack = os.path.join(a.scratch, "pack"); shutil.rmtree(pack, ignore_errors=True); shutil.copytree(a.pack, pack)
    packrun = os.path.join(pack, "pack.tasrun.json")
    shutil.copy2(a.spec, os.path.join(a.scratch, "spec.json"))
    parents = os.path.join(a.scratch, "parents_r0.json"); json.dump({"parents": [""]}, open(parents, "w"))
    dirs = []
    for r in range(rounds):
        name = f"{a.name}_r{r}"
        pdir = os.path.join(a.scratch, f"in_r{r}"); shutil.rmtree(pdir, ignore_errors=True); os.makedirs(pdir)
        shutil.copy2(parents, os.path.join(pdir, "parents.json")); shutil.copy2(a.spec, os.path.join(pdir, "spec.json"))
        items = [dict(base, id=f"s{i}", files=[pack, os.path.join(pdir, "parents.json"), os.path.join(pdir, "spec.json")],
                      cmds=[f'[ -x "{exe}" ] || {{ echo "TAS3 no build {a.bin} on this PC"; exit 3; }}', f'"{exe}" --tas search "$JOBDIR/pack/pack.tasrun.json" --spec "$JOBDIR/spec.json" --parents "$JOBDIR/parents.json" --round {r} --shard {i}/{a.shards} --out "$N64_WORK/out"'])
                 for i in range(a.shards)]
        t0 = time.time()
        total = a.shards + a.g2
        for it in items:   # the shard count includes the Garage2 ones
            it["cmds"] = [c.replace(f"/{a.shards} --out", f"/{total} --out") for c in it["cmds"]]
        g2procs = []
        if a.g2:
            g2files = [os.path.join(pack, f) for f in os.listdir(pack) if os.path.isfile(os.path.join(pack, f))] + [os.path.join(pdir, "parents.json"), os.path.join(pdir, "spec.json")]
            for k in range(a.g2):
                i = a.shards + k; tag = f"{name}_g{i}"
                args = rf"--tas search {{O}}\in\pack.tasrun.json --spec {{O}}\in\spec.json --parents {{O}}\in\parents.json --round {r} --shard {i}/{total} --out {{O}}\out"
                g2procs.append((tag, subprocess.Popen(["bash", os.path.join(G2, "g2run.sh"), "run", "garage2", tag, a.bin, args] + [f.replace("\\", "/") for f in g2files],
                                                       cwd=G2, stdout=open(os.path.join(a.scratch, tag + ".log"), "w"), stderr=subprocess.STDOUT)))
        submit(a, name, items)
        got = wait(name, [it["id"] for it in items], a.timeout)
        dirs.append(os.path.join(N64OUT, name))
        for k in range(a.retries):   # a shard that landed on a PC without the build (or died): again, under a new name
            bad = [it for it in items if not got.get(it["id"])]
            if not bad:
                break
            rn = f"{name}_retry{k}"
            print(f"round {r}: retrying {len(bad)} shards as {rn}", flush=True)
            submit(a, rn, bad)
            g2 = wait(rn, [it["id"] for it in bad], a.timeout)
            got.update({i: v for i, v in g2.items() if v})
            dirs.append(os.path.join(N64OUT, rn))
        for tag, pr in g2procs:
            pr.wait()
            if os.path.exists(os.path.join(G2, "results", tag, "out", "results.json")):
                dirs.append(os.path.join(G2, "results", tag)); got[tag] = tag
            else:
                print(f"  {tag}: Garage2 shard without results ({os.path.join(a.scratch, tag + '.log')})", flush=True)
        print(f"round {r}: {sum(1 for v in got.values() if v)}/{total} shards in {int(time.time() - t0)} s", flush=True)
        final = r == rounds - 1
        out = os.path.join(a.scratch, "final" if final else f"merge_r{r}")
        args = ["--tas", "search-merge", packrun, "--spec", a.spec, "--round", str(r), "--out", out, "--parents", parents]
        for d in dirs:
            args += ["--in", d]
        if final and not a.n64:
            args.append("--final")
        ws(a, *args)
        parents = os.path.join(out, f"parents_r{r + 1}.json")
    if a.n64:   # the write-back replays the chapter twice: on a fleet PC, not here
        out = os.path.join(a.scratch, "final")
        ins = []
        for d in dirs:
            for f in glob.glob(os.path.join(d, "**", "results.json"), recursive=True):
                dst = os.path.join(a.scratch, "allres", f"{len(ins)}"); os.makedirs(dst, exist_ok=True); shutil.copy2(f, dst); ins.append(dst)
        allres = os.path.join(a.scratch, "allres")
        submit(a, a.name + "_final", [dict(base, id="final", wall=a.wall * 2, files=[pack, a.spec, allres],
               cmds=[f'"{exe}" --tas search-merge "$JOBDIR/pack/pack.tasrun.json" --spec "$JOBDIR/{os.path.basename(a.spec)}" --round {rounds - 1} --in "$JOBDIR/allres" --out "$N64_WORK/final" --final'])])
        got = wait(a.name + "_final", ["final"], a.timeout * 2)
        print("final:", got)
    print("done:", a.scratch)


if __name__ == "__main__":
    main()
