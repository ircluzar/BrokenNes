"""Re-scores T2 records from their saved movies with the shift-tolerant reference score (no agent needed).

    python rescore.py list [--before 2026-11-16T00:31:00Z]          T2 Jank records whose reference was not computed by the fixed server
    python rescore.py run --sha1 S [--pc ZIRCON] [--write]          replay one movie + Mesen, print the verdict (and write it)
    python rescore.py batch --pcs ZIRCON,EMERALD,DIAMOND [--per-pc 2] [--write] [--only S1,S2]

The movie (.bnm, text: `i <frame> <pad1> <pad2>`) is replayed from power-on through play_step in steps of at most 20 frames (the T2
agents' step size), with the reference opened and aligned as the agents did (reference open, align_frames 240). A run of 3+ steps with
score > 0.12 keeps Jank; otherwise the grade becomes OK. Results go to C:/BrokenNes-work/cc_rescore/<sha1>.json.
"""
import argparse, json, os, subprocess, sys, threading, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import catalog  # noqa: E402

BRIDGE = os.environ.get("BN_FLEET_BRIDGE", "C:/BrokenNes-work/cc_pilot/bridge/mcp.mjs")
OUT = "C:/BrokenNes-work/cc_rescore"
TAS = "T:/BrokenDev/Catalog/tas/nes"


def candidates(before=None):
    out = []
    for r in catalog.iter_records("nes"):
        if r.get("grade") != "Jank" or r.get("tier") != 2:
            continue
        if (r.get("reference") or {}).get("computedBy"):
            continue                                   # graded with the fixed server already
        if before and r.get("updated", "") >= before:
            continue
        out.append(r)
    return out


def bn(port, tool, args, shots):
    p = subprocess.run(["node", BRIDGE, "--port", str(port), "--out", shots, tool, json.dumps(args)], capture_output=True, text=True, timeout=900)
    try:
        return json.loads(p.stdout)
    except Exception:
        return {"isError": True, "text": (p.stdout + p.stderr)[-400:]}


def movie_events(path):
    ev, total = [], 0
    for line in open(path, encoding="utf-8"):
        if line.startswith("frames "):
            total = int(line.split()[1])
        elif line.startswith("i "):
            parts = line.split()
            ev.append((int(parts[1]), parts[2], parts[3] if len(parts) > 3 else "-"))
    return ev, total


def run_one(sha1, pc=None, write=False, chunk=20):
    rec = catalog.get("nes", sha1)
    movie = f"{TAS}/{sha1}.bnm"
    res = {"sha1": sha1, "title": rec.get("title"), "pc": pc, "at": catalog.now()}
    if not os.path.exists(movie):
        res["error"] = "no movie"; return res
    rom = (rec.get("file") or {}).get("path")
    ev, total = movie_events(movie)
    shots = f"{OUT}/shots/{sha1}"
    start = ["node", BRIDGE, "start", "--max-steps", "100000"] + (["--pc", pc] if pc else [])
    s = subprocess.run(start, capture_output=True, text=True, timeout=300)
    try:
        port = json.loads(s.stdout)["port"]
    except Exception:
        res["error"] = "bridge start: " + s.stdout[-300:]; return res
    try:
        o = bn(port, "open_rom", {"path": rom.replace("\\", "/")}, shots)
        if o.get("isError"):
            res["error"] = "open_rom: " + str(o.get("text"))[:300]; return res
        o = bn(port, "reference", {"action": "open", "align_frames": 240}, shots)
        if o.get("isError"):
            res["error"] = "reference: " + str(o.get("text"))[:300]; return res
        cur = int((o.get("text") or {}).get("align", {}).get("frame") or 0)
        res["offset"] = (o.get("text") or {}).get("reference", {}).get("offset")
        skipped = [e for e in ev if e[0] < cur and e[1] != "-"]
        if skipped:
            res["note"] = f"{len(skipped)} pad events before the aligned frame {cur} were not replayed"
        scores, run, high = [], 0, 0
        for k, (f, b1, b2) in enumerate(ev):
            nf = ev[k + 1][0] if k + 1 < len(ev) else total
            btn = [] if b1 == "-" else b1.split("+")
            btn2 = [] if b2 == "-" else b2.split("+")
            while cur < nf:
                n = min(chunk, nf - cur)
                if nf <= cur:
                    break
                r = bn(port, "play_step", {"buttons": btn, "buttons2": btn2, "frames": n, "scale": 0}, shots)
                t = r.get("text") if isinstance(r.get("text"), dict) else {}
                d = ((t.get("reference") or {}).get("diff") or {})
                if r.get("isError") or "score" not in d:
                    res["error"] = "play_step: " + str(r.get("text"))[:300]; break
                cur = int(t.get("frame", cur + n))
                sc = float(d["score"])
                scores.append((cur, sc, d.get("raw")))
                run = run + 1 if sc > 0.12 else 0
                high = max(high, run)
            if "error" in res:
                break
        bn(port, "reference", {"action": "close"}, shots)
        bn(port, "close_session", {}, shots)
    finally:
        subprocess.run(["node", BRIDGE, "--port", str(port), "stop"], capture_output=True, timeout=120)
    vals = sorted(x[1] for x in scores)
    res.update(steps=len(scores), frames=cur, highRun=high,
               median=round(vals[len(vals) // 2], 4) if vals else None, max=round(max(vals), 4) if vals else None,
               maxAt=max(scores, key=lambda x: x[1])[0] if scores else None,
               firstHigh=next((x[0] for x in scores if x[1] > 0.12), None))
    if "error" not in res and scores:
        res["grade"] = "Jank" if high >= 3 else "OK"
        if write:
            note = (f"T2 re-score {res['at']} (shift-tolerant score, movie replayed with Mesen 2.1.1, offset {res.get('offset')}): "
                    f"{len(scores)} steps of <= {chunk} frames, median {res['median']}, max {res['max']} at frame {res['maxAt']}, "
                    f"longest run over 0.12 = {high}. "
                    + ("Jank stands (a real divergence, not scroll phase; first high step at frame %s)." % res["firstHigh"] if high >= 3
                       else "Was Jank under the old exact score (scroll phase false alarm): now OK."))
            ch = {"reference": {"emulator": "Mesen 2.1.1", "frames": cur, "offset": res.get("offset"), "median": res["median"],
                                "max": res["max"], "maxAtFrame": res["maxAt"], "steps": len(scores), "highRun": high,
                                "computedBy": "rescore.py (shift-tolerant score)"},
                  "extra": {"rescore": {k: res.get(k) for k in ("at", "grade", "highRun", "median", "max", "firstHigh", "pc")}}}
            if res["grade"] != "Jank":
                ch["grade"] = res["grade"]
            catalog.update("nes", sha1, ch, append_notes=note)
            res["written"] = True
    os.makedirs(OUT, exist_ok=True)
    json.dump(res | {"scores": scores}, open(f"{OUT}/{sha1}.json", "w"), indent=1)
    return res


def main():
    ap = argparse.ArgumentParser(); sp = ap.add_subparsers(dest="cmd", required=True)
    p = sp.add_parser("list"); p.add_argument("--before")
    p = sp.add_parser("run"); p.add_argument("--sha1", required=True); p.add_argument("--pc"); p.add_argument("--write", action="store_true")
    p = sp.add_parser("batch"); p.add_argument("--pcs", required=True); p.add_argument("--per-pc", type=int, default=2)
    p.add_argument("--write", action="store_true"); p.add_argument("--only"); p.add_argument("--before")
    a = ap.parse_args()
    if a.cmd == "list":
        for r in candidates(a.before):
            print(r["sha1"], r.get("updated"), r.get("title"))
    elif a.cmd == "run":
        r = run_one(a.sha1, a.pc, a.write); print(json.dumps(r))
    else:
        todo = [s for s in a.only.split(",")] if a.only else [r["sha1"] for r in candidates(a.before)]
        todo = [s for s in todo if not os.path.exists(f"{OUT}/{s}.json") or "error" in json.load(open(f"{OUT}/{s}.json"))]
        lock = threading.Lock()
        def worker(pc):
            while True:
                with lock:
                    if not todo:
                        return
                    s = todo.pop(0)
                try:
                    r = run_one(s, pc, a.write)
                except Exception as e:
                    r = {"sha1": s, "error": repr(e)}
                with lock:
                    print(json.dumps({k: r.get(k) for k in ("sha1", "title", "grade", "highRun", "max", "error", "pc")}), flush=True)
        ts = [threading.Thread(target=worker, args=(pc,)) for pc in a.pcs.split(",") for _ in range(a.per_pc)]
        for t in ts: t.start()
        for t in ts: t.join()


if __name__ == "__main__":
    main()
