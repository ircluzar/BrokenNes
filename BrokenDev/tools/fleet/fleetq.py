"""WORK-STEALING QUEUE for the fleet: one global backlog here, every PC keeps N jobs running and takes the next small job as soon as
one ends (no fixed shards). Results are pulled and written in as each job ends.

    python queue.py [--t1 snes,md,gb,gbc,gg] [--batch 6] [--minutes 3]

Backlog (priority order): T1 3-minute redo of every non-OK title of the given consoles, in small batches.
Slots: N = 4 on a 16 GB PC, 3 on an 8 GB PC, minus the PC's other running fleet jobs (any FleetJob_* task running: fixed-shard
sweeps, the T2 sessions) so nothing is oversubscribed. State: queue_state.json (done batches are not redone after a restart).
"""
import argparse, json, os, subprocess, sys, threading, time
sys.path.insert(0, r"E:\fleet")
from fleet import boxes, run, enc  # noqa: E402
from jobs import put, job, poll  # noqa: E402
from pull import pull  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.join(HERE, "kit", "tools")
T1 = os.path.join(KIT, "compat-t1", "t1.py")
STATE = os.path.join(HERE, "queue_state.json")
LOG = os.path.join(HERE, "queue.log")
PY = sys.executable
BS = chr(92)
VER = subprocess.run(["git", "-C", r"C:\BrokenNes-work\fleet_kit_wt", "rev-parse", "--short", "HEAD"], capture_output=True, text=True).stdout.strip()
ENV = dict(os.environ, BNT1_VERSION=VER)
ingest_lock = threading.Lock()


def log(msg):
    line = time.strftime("%H:%M:%S ") + msg
    print(line, flush=True)
    open(LOG, "a", encoding="utf-8").write(line + "\n")


def backlog_t1(consoles, batch, noref=False, deep=0):
    sys.path.insert(0, os.path.join(KIT, "catalog"))
    import catalog
    items = []
    for c in consoles:
        todo = []
        for r in list(catalog.iter_records(c)):
            t = (r.get("extra") or {}).get("t1") or {}
            if r.get("revisionOf") or not t.get("version", "").startswith("t1-"):
                continue
            if deep:    # deep T1: every OK title again with a longer script (late crashes / desyncs)
                if t.get("grade") != "OK" or (t.get("minutes") or 1) >= deep or (r.get("path") or "").lower().endswith(".bs"):
                    continue
            elif noref:   # titles whose reference run gave no pictures (BizHawk's platform dialog, a hung reference): run again
                if "no reference" not in (t.get("why") or "") or (r.get("path") or "").lower().endswith(".bs"):
                    continue
            elif t.get("grade") == "OK" or (t.get("minutes") or 1) >= 3:
                continue
            todo.append(r["sha1"])
        for i in range(0, len(todo), batch):
            items.append(dict(kind="t1", console=c, id=f"{'d' if deep else 'n' if noref else 'q'}{c}{i // batch:03d}", shas=todo[i:i + batch]))
        log(f"backlog: {c} {len(todo)} titles -> {(len(todo) + batch - 1) // batch} batches")
    return items


def running_jobs(box):
    out = run(box, enc("(schtasks /query /fo csv /nh | Select-String 'FleetJob_' | Select-String 'Running').Count"), 60)[1]
    try:
        return int(out.strip().splitlines()[-1])
    except Exception:
        return 99


def prep_t1(it):
    """export one batch (catalog reads + ROM copies here): done ahead of time by a pool, so starting a job is only put + job"""
    d = os.path.join(HERE, "shards", "queue", it["id"])
    os.makedirs(d, exist_ok=True)
    lst = os.path.join(d, "list.txt")
    open(lst, "w").write(chr(10).join(it["shas"]))
    if not os.path.exists(os.path.join(d, "x", "shard.json")):
        p = subprocess.run([PY, T1, "export", "--console", it["console"], "--list", lst, "--out", os.path.join(d, "x")], capture_output=True, text=True, env=ENV)
        if p.returncode:
            log(f"export failed {it['id']}: {p.stderr[-200:]}")
            return None
    it["dir"] = os.path.join(d, "x")
    return it


def start_t1(name, box, it, minutes):
    R = "C:/brokennes".replace("/", BS)
    sid = it["id"]
    put(it["dir"], R + BS + "roms" + BS + sid, [name])
    env = {"BNT1_WORKSHOP": R + "/kit/workshop/BrokenNes.Workshop.exe".replace("/", BS), "BNT1_MESEN": R + "/kit/mesen/Mesen.exe".replace("/", BS),
           "BNT1_BIZHAWK": R + "/kit/bizhawk/EmuHawk.exe".replace("/", BS), "BNT1_SCRATCH": R + BS + "work" + BS + sid,
           "BNT1_HEAVY": R + BS + "work" + BS + sid + BS + "heavy", "BNT1_SLOTS": "1", "BNT1_LANE": "fleet-" + sid}
    cmd = (R + "/kit/py/python.exe -I ".replace("/", BS) + R + "/kit/tools/compat-t1/t1.py".replace("/", BS)
           + f" work --console {it['console']} --minutes {minutes} --shard " + R + BS + "roms" + BS + sid)
    return job(box, sid, cmd, R, env, limit=4 * 3600, low=False)



def finish_t1(name, box, it):
    dst = os.path.join(HERE, "results", "queue", it["id"])
    rc, msg = pull(box, rf"C:\brokennes\roms\{it['id']}\out", dst)
    with ingest_lock:
        p = subprocess.run([PY, T1, "ingest", "--console", it["console"], "--results", dst, "--force"], capture_output=True, text=True, env=ENV)
    run(box, enc(rf"Remove-Item C:\brokennes\roms\{it['id']}, C:\brokennes\work\{it['id']} -Recurse -Force -ErrorAction SilentlyContinue; 'x'"), 120)
    log(f"done {it['id']} on {name}: {(p.stdout or p.stderr).strip()[-120:]}")


def pc_worker(name, box, ready, state, minutes, lock):
    cap = 6 if (box.get("ramGB") or 16) >= 15 else 5
    active = {}
    while True:
        for sid, it in list(active.items()):
            rc, _ = poll(box, sid)
            if rc is not None:
                del active[sid]
                with lock:
                    state["done"].append(sid)
                    json.dump(state, open(STATE, "w"))
                threading.Thread(target=finish_t1, args=(name, box, it)).start()
        free = cap - running_jobs(box)
        while free > 0:
            try:
                it = ready.get(timeout=5)
            except Exception:
                break
            if it is None:
                ready.put(None)
                if not active:
                    log(f"{name}: no more work")
                    return
                break
            if start_t1(name, box, it, minutes):
                active[it["id"]] = it
                log(f"start {it['id']} ({it['console']}, {len(it['shas'])}) on {name}")
                free -= 1
            else:
                log(f"START FAILED {it['id']} on {name} (back in the queue)")
                ready.put(it)
                break
        time.sleep(15)


def main():
    import queue as Q
    from concurrent.futures import ThreadPoolExecutor
    ap = argparse.ArgumentParser()
    ap.add_argument("--t1", default="snes,md,gb,gbc,gg")
    ap.add_argument("--batch", type=int, default=6)
    ap.add_argument("--minutes", type=float, default=3)
    ap.add_argument("--deep", type=float, default=0, help="re-run every OK title with this many minutes")
    ap.add_argument("--noref", action="store_true", help="re-run titles whose reference gave no pictures")
    a = ap.parse_args()
    global STATE
    if a.noref:
        STATE = os.path.join(HERE, "queue_state_noref.json")
    if a.deep:
        STATE = os.path.join(HERE, "queue_state_deep.json")
        a.minutes = a.deep
    state = json.load(open(STATE)) if os.path.exists(STATE) else {"done": []}
    items = [it for it in backlog_t1([c for c in a.t1.split(",") if c], a.batch, a.noref, a.deep) if it["id"] not in state["done"]]
    log(f"queue: {len(items)} batches")
    ready, lock = Q.Queue(), threading.Lock()

    def feeder():
        with ThreadPoolExecutor(6) as ex:
            for it in ex.map(prep_t1, items):
                if it:
                    ready.put(it)
        ready.put(None)
    threading.Thread(target=feeder, daemon=True).start()
    ts = [threading.Thread(target=pc_worker, args=(n, b, ready, state, a.minutes, lock)) for n, b in boxes().items()]
    for t in ts:
        t.start()
    for t in ts:
        t.join()
    log("queue empty")


if __name__ == "__main__":
    main()
