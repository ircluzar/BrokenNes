"""AutoTAS chapter runner: one command per action, so a player agent only plays.

Game-agnostic. Game knowledge comes from games/<game>/runner.json (RAM to show, in-level / on-map
expressions, the progress table, the lives byte). The runner keeps its own input log and never records
while you play; `finish` replays the log from the start state in a fresh session, records the segment,
marks the milestone and writes the raw state FILE. So retries (`back`) never leave junk in the movie.

  python -I run.py new  --game smb3 --rom R --pkg P --ch 1-2 [--from S] [--goal level|until] [--until EXPR]
  python -I run.py step --ch 1-2 Right+B 60          buttons joined by +, "none" = no buttons; frames
  python -I run.py step --ch 1-2 Right+B+A 20 --shot also writes work/<ch>/shot.png (Read it to look)
  python -I run.py look --ch 1-2                     picture + RAM, no frames run
  python -I run.py save --ch 1-2 NAME                retry point (state file + log length)
  python -I run.py back --ch 1-2 NAME                back to a retry point (the log is cut there)
  python -I run.py resume --ch 1-2 NAME              new session at a retry point (after close / in a new agent)
  python -I run.py search --ch 1-2 "Right+B:{a} Right+B+A:{j}" --vals a=0..12:2,j=8..30:2 --want EXPR --then Right+B [--apply]
                                                     tries a timing grid from here, keeps the fastest no-death try reaching EXPR
  python -I run.py log  --ch 1-2                     the input log so far
  python -I run.py finish --ch 1-2 [--note TEXT]     replay -> seg/<ch>.bnm + state/<ch>.raw.state + thumb
  python -I run.py close --ch 1-2                    drop the session (no output)
Every command needs --pkg (or env AUTOTAS_PKG). Goal "level": the chapter ends on the first on-map frame
after a level was entered where a new bit appeared in the progress table. Goal "until": an expression.
A death (lives below the start value) stops the step and says DIED.
"""
import os, sys, json, argparse, shutil
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from lib.bridge import Bridge, BRIDGE
from lib import game as gamelib

MCP_EXE = os.environ.get("AUTOTAS_MCP_EXE", r"C:\BrokenNes-work\autotas_mcp\BrokenNes.Mcp.exe")


def wdir(pkg, ch): return os.path.join(pkg, "work", ch)
def runf(pkg, ch): return os.path.join(wdir(pkg, ch), "run.json")
def load(pkg, ch): return json.load(open(runf(pkg, ch), encoding="utf-8"))
def store(pkg, ch, r):
    f = runf(pkg, ch)
    import time
    with open(f + ".tmp", "w", encoding="utf-8") as o: json.dump(r, o, indent=1)
    for i in range(20):   # Windows: a reader (antivirus, indexer, another command) can hold the file a moment
        try: os.replace(f + ".tmp", f); return
        except PermissionError: time.sleep(0.25)
    os.replace(f + ".tmp", f)


def attach(port):
    b = Bridge.__new__(Bridge); b.port, b.pc = port, None; return b


def start_bridge(pc=None):
    return Bridge(pc=pc, exe=None if pc else MCP_EXE)


def btns(s): return [] if s in ("-", "", "none") else s.split("+")


def table(b, sess, t):
    out = b.call("read_memory", session=sess, spec="$%04X" % t["addr"], length=t["len"])
    return list(out["bytes"]) if isinstance(out.get("bytes"), list) else list(bytes.fromhex(out["hex"].replace(" ", "")))


def newbits(old, new): return [(i, n & ~o) for i, (o, n) in enumerate(zip(old, new)) if n & ~o]


def do_step(b, r, cfg, buttons, frames, until=None):
    """One play_step with the chapter's stop conditions; returns (result, event)."""
    ms = [f'died={cfg["lives"]} < {r["lives0"]}'] if r["lives0"] <= cfg.get("lives_max", 99) else []
    if cfg.get("dead"): ms.append("died=" + cfg["dead"])
    if r["goal"] == "until": ms.append("goal=" + r["until"])
    elif r["goal"] == "level":
        ms.append(("exit=" + cfg["on_map"]) if r.get("armed") else ("entered=" + cfg["in_level"]))
    res = b.call("play_step", session=r["session"], buttons=buttons, frames=frames, until=until,
                 max_frames=frames if until else None, milestones=ms, scale=0, ram=cfg["ram"])
    ev, mid = None, (res.get("milestone") or {}).get("id") if res.get("stoppedBy") == "milestone" else None
    if mid == "died": ev = "DIED"
    elif mid == "goal": ev = "GOAL"
    elif mid == "entered": r["armed"] = True; ev = "entered level"
    elif mid == "exit":
        nb = newbits(r["table0"], table(b, r["session"], cfg.get("progress_table") or {"addr": 0, "len": 0}))
        if nb: ev = "GOAL"; r["newbits"] = [[cfg["progress_table"]["addr"] + i, m] for i, m in nb]
        else: r["armed"] = False; ev = "back on map with NO new clear (left the level or died)"
    return res, ev


def show(res, ev, r, extra=""):
    ram = res.get("ram") or {}
    line = f'frame {r["frames"]} (+{res.get("framesRun")}) ' + " ".join(f"{k}={v}" for k, v in ram.items())
    if ev: line += "  >>> " + ev
    print(line + extra)


def shot(b, r, pkg, ch, name="shot.png"):
    p = os.path.join(wdir(pkg, ch), name); b.screenshot(r["session"], p, scale=2); return p


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd"); ap.add_argument("args", nargs="*")
    ap.add_argument("--ch", required=True); ap.add_argument("--pkg", default=os.environ.get("AUTOTAS_PKG"))
    ap.add_argument("--game"); ap.add_argument("--rom"); ap.add_argument("--from", dest="frm")
    ap.add_argument("--goal", default="level"); ap.add_argument("--until"); ap.add_argument("--pc")
    ap.add_argument("--shot", action="store_true"); ap.add_argument("--note", default="")
    ap.add_argument("--vals"); ap.add_argument("--want"); ap.add_argument("--then", default="none")
    ap.add_argument("--within", type=int, default=120); ap.add_argument("--apply", action="store_true")
    ap.add_argument("--survive", type=int, default=30, help="search: the try must also live this many more frames holding --then")
    ap.add_argument("--patience", type=int, default=0, help="advance: when stuck, wait 30 frames and retry up to N times (auto-scrollers)")
    ap.add_argument("--stop-when", dest="stopwhen", help="step: also stop early when this expression is true")
    a = ap.parse_intermixed_args(); pkg, ch = a.pkg, a.ch
    if not pkg: sys.exit("--pkg needed")

    if a.cmd == "new":
        os.makedirs(wdir(pkg, ch), exist_ok=True)
        if os.path.exists(runf(pkg, ch)):
            try: old = load(pkg, ch); attach(old["port"]).call("close_session", session=old["session"]); attach(old["port"]).stop()
            except Exception: pass
        g = gamelib.load(a.game); cfg = json.load(open(os.path.join(g.dir, "runner.json"), encoding="utf-8"))
        b = start_bridge(a.pc)
        sess = b.call("open_rom", path=a.rom)["session"]
        if a.frm: b.call("load_state", session=sess, path=a.frm)
        r = {"game": a.game, "rom": a.rom, "from": a.frm, "goal": a.goal, "until": a.until, "port": b.port, "session": sess,
             "frames": 0, "log": [], "subs": {}, "armed": False}
        r["lives0"] = b.call("play_step", session=sess, frames=0, scale=0, ram=["l=" + cfg["lives"]])["ram"]["l"]
        r["table0"] = table(b, sess, cfg.get("progress_table") or {"addr": 0, "len": 0})
        store(pkg, ch, r)
        res = b.call("play_step", session=sess, frames=0, scale=0, ram=cfg["ram"])
        show(res, "ready (session open, nothing recorded yet)", r, f"\n  look: {shot(b, r, pkg, ch)}")
        return

    lock = os.path.join(wdir(pkg, ch), "run.lock")
    try: lk = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    except FileExistsError:
        import time
        def alive(pid):   # a lock left by a killed command (tool timeout) must not block the chapter forever
            try:
                import subprocess
                return str(pid) in subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/NH"], capture_output=True, text=True).stdout
            except Exception: return True
        try: owner = int(open(lock).read().strip() or 0)
        except Exception: owner = 0
        if owner and not alive(owner): print("(removed a stale lock: its command is gone)")
        elif time.time() - os.path.getmtime(lock) < 1200:
            sys.exit("another runner command is still working on this chapter: wait for it (never run two at once, never in the background)")
        os.remove(lock); lk = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    os.write(lk, str(os.getpid()).encode())
    try: run_cmd(a, pkg, ch)
    finally: os.close(lk); os.remove(lock)


def run_cmd(a, pkg, ch):
    r = load(pkg, ch)
    if a.cmd == "resume":
        # a new session at a saved retry point (after close, a crash or another player's run): the log is cut there
        sname = a.args[0]; sub = r["subs"][sname]
        try: attach(r["port"]).stop()   # the old daemon of this chapter, if still running
        except Exception: pass
        b = start_bridge(a.pc); sess = b.call("open_rom", path=r["rom"])["session"]
        b.call("load_state", session=sess, path=sub["path"])
        r.update(port=b.port, session=sess, log=[x[:] for x in sub["inputs"]] if "inputs" in sub else r["log"][:sub["log"]], frames=sub["frames"], armed=sub["armed"])
        r.pop("goal_frame", None); r.pop("finished", None)
        r["subs"] = {k: v for k, v in r["subs"].items() if v["log"] <= sub["log"]}
        store(pkg, ch, r)
        cfg = json.load(open(os.path.join(gamelib.load(r["game"]).dir, "runner.json"), encoding="utf-8"))
        res = b.call("play_step", session=sess, frames=0, scale=0, ram=cfg["ram"])
        show(res, f"resumed at {sname}", r, "\n  look: " + shot(b, r, pkg, ch)); return
    b = attach(r["port"])
    cfg = json.load(open(os.path.join(gamelib.load(r["game"]).dir, "runner.json"), encoding="utf-8"))
    if a.cmd == "step":
        bt, n = btns(a.args[0]), int(a.args[1]) if len(a.args) > 1 else 15
        res, ev = do_step(b, r, cfg, bt, n, names(a.stopwhen, cfg))
        ran = int(res.get("framesRun", 0) or 0)
        if ran: r["log"].append([bt, ran]); r["frames"] += ran
        if ev == "GOAL": r["goal_frame"] = r["frames"]
        store(pkg, ch, r)
        show(res, ev, r, f"\n  look: {shot(b, r, pkg, ch)}" if a.shot or ev else "")
    elif a.cmd == "look":
        res = b.call("play_step", session=r["session"], frames=0, scale=0, ram=cfg["ram"])
        show(res, None, r, f"\n  look: {shot(b, r, pkg, ch)}")
    elif a.cmd == "save":
        name = a.args[0]; p = os.path.join(wdir(pkg, ch), name + ".state")
        b.call("save_state", session=r["session"], path=p)
        r["subs"][name] = {"path": p, "log": len(r["log"]), "inputs": [x[:] for x in r["log"]], "frames": r["frames"], "armed": r["armed"]}
        store(pkg, ch, r); print(f"saved {name} at frame {r['frames']}")
    elif a.cmd == "back":
        s = r["subs"][a.args[0]]
        b.call("load_state", session=r["session"], path=s["path"])
        r["log"] = [x[:] for x in s["inputs"]] if "inputs" in s else r["log"][:s["log"]]; r["frames"] = s["frames"]; r["armed"] = s["armed"]; r.pop("goal_frame", None)
        store(pkg, ch, r)
        res = b.call("play_step", session=r["session"], frames=0, scale=0, ram=cfg["ram"])
        show(res, f"back at {a.args[0]}", r)
    elif a.cmd == "search":
        search(b, r, cfg, pkg, ch, a)
    elif a.cmd == "advance":
        advance(b, r, cfg, pkg, ch, a)
    elif a.cmd == "log":
        print(json.dumps(r["log"]))
    elif a.cmd == "close":
        try: b.call("close_session", session=r["session"])
        finally: b.stop()
        print("closed")
    elif a.cmd == "finish":
        if "goal_frame" not in r: sys.exit("not at the goal yet: finish only after a step said GOAL")
        finish(b, r, cfg, pkg, ch, a.note)
    else:
        sys.exit("unknown command " + a.cmd)


def names(expr, cfg):
    """Lets expressions use the runner.json RAM names (x >= 1340 && air == 0) instead of [$0075]..., which shells mangle ($0 = the shell)."""
    import re
    if not expr: return expr
    m = {k: v for k, v in (e.split("=", 1) for e in cfg["ram"])}
    return re.sub(r"(?<![\w$])([A-Za-z_]\w*)", lambda t: "(" + m[t.group(1)] + ")" if t.group(1) in m else t.group(0), expr)


def search(b, r, cfg, pkg, ch, a):
    """Tries every timing in a small grid from HERE and keeps the fastest that reaches --want without dying.

    run.py search --ch C "Right+B:{a} Right+B+A:{j} Right+B:4" --vals a=0..12:2,j=8..30:2 --want "EXPR" [--then Right+B] [--within 120] [--apply]
    Each try: the pattern's steps (buttons:frames, {name} = a grid value), then --then held until EXPR is true (at most --within frames).
    Prints the best few; --apply plays the best one for real (it goes into the log)."""
    import itertools
    pat = a.args[0]
    a.want = names(a.want, cfg)
    if not a.want or "$" not in a.want and "[" not in a.want and "frame" not in a.want:
        sys.exit("search: --want is empty or lost its $addresses (the shell ate $0...): use RAM names like x >= 1340 && air == 0, or single quotes")
    vals = {}
    for part in (a.vals or "").split(","):
        if not part: continue
        k, rng = part.split("="); lo, rest = rng.split(".."); hi, st = (rest.split(":") + ["1"])[:2]
        vals[k] = list(range(int(lo), int(hi) + 1, int(st)))
    keys = list(vals)
    import math
    n = math.prod(len(v) for v in vals.values()) if vals else 1
    if n > 400: sys.exit(f"search: {n} combos is too many (~1.5 s each; a tool call times out at 10 min): use coarser steps, at most ~300")
    tmp = os.path.join(wdir(pkg, ch), "_search.state")
    b.call("save_state", session=r["session"], path=tmp)
    dead = [f'died={cfg["dead"]}'] if cfg.get("dead") else []
    results, died, missed = [], 0, []
    for combo in itertools.product(*[vals[k] for k in keys]) if keys else [()]:
        env = dict(zip(keys, combo))
        steps = []
        for tok in pat.split():
            bt, n = tok.rsplit(":", 1); n = int(n.format(**env))
            if n > 0: steps.append([btns(bt), n])
        b.call("load_state", session=r["session"], path=tmp)
        total, ok = 0, True
        for bt, n in steps:
            res = b.call("play_step", session=r["session"], buttons=bt, frames=n, scale=0, milestones=dead or None)
            total += int(res.get("framesRun", 0) or 0)
            if res.get("stoppedBy") == "milestone": ok = False; break
        if not ok: died += 1; continue
        res = b.call("play_step", session=r["session"], buttons=btns(a.then), frames=a.within, until=a.want, max_frames=a.within,
                     scale=0, milestones=dead or None, ram=cfg["ram"])
        if res.get("stoppedBy") == "milestone": died += 1; continue
        if res.get("stoppedBy") != "condition": missed.append((env, res.get("ram"))); continue
        total += int(res.get("framesRun", 0) or 0)
        if a.survive and dead:   # not a doomed spot: holding --then a little longer must not kill Mario
            b.call("save_state", session=r["session"], name="_srv")
            sv = b.call("play_step", session=r["session"], buttons=btns(a.then), frames=a.survive, scale=0, milestones=dead)
            if sv.get("stoppedBy") == "milestone": died += 1; continue
        results.append((total, env, steps + [[btns(a.then), int(res.get("framesRun", 0) or 0)]], res.get("ram")))
    b.call("load_state", session=r["session"], path=tmp)
    results.sort(key=lambda t: t[0])
    if not results:
        print(f"search: no try reached the goal without dying: {died} died, {len(missed)} alive but never met --want. Back where you were.")
        for env, ram in missed[:3]: print("  alive:", env, " ".join(f"{k}={v}" for k, v in (ram or {}).items()))
        return
    for t, env, _, ram in results[:3]:
        print(f"{t} frames  {env}  then " + " ".join(f"{k}={v}" for k, v in (ram or {}).items()))
    if a.apply:
        for bt, n in results[0][2]:
            res, ev = do_step(b, r, cfg, bt, n)
            ran = int(res.get("framesRun", 0) or 0)
            if ran: r["log"].append([bt, ran]); r["frames"] += ran
            if ev == "GOAL": r["goal_frame"] = r["frames"]
            if ev and ev != "entered level": print(">>> " + ev); break
        store(pkg, ch, r)
        res = b.call("play_step", session=r["session"], frames=0, scale=0, ram=cfg["ram"])
        show(res, "applied the best try", r, "\n  look: " + shot(b, r, pkg, ch))


def advance(b, r, cfg, pkg, ch, a):
    """Greedy autopilot: repeats search toward the game's 'forward' goal (runner.json "advance") in steps, saving a checkpoint
    after each, until stuck or the step limit. run.py advance --ch C [--steps 6]. Game data: runner.json
    "advance": {"pattern": "...", "vals": "...", "want": "x >= {X} && air == 0", "start": "x", "stride": 130, "then": "Right+B", "within": 90}."""
    ad = cfg.get("advance")
    if not ad: sys.exit("this game's runner.json has no advance recipe")
    n = int(a.args[0]) if a.args else 6
    stride = int(ad["stride"])
    for i in range(n):
        cur = b.call("play_step", session=r["session"], frames=0, scale=0, ram=cfg["ram"])["ram"]
        target = int(cur[ad["start"]]) + stride
        a2 = argparse.Namespace(**vars(a)); a2.args = [ad["pattern"]]; a2.vals = ad["vals"]; a2.want = ad["want"].replace("{X}", str(target))
        fin = None
        if ad.get("finale"):   # level-end bytes (SMB3: the card slots): a hop that changes one has reached the goal
            fin = " || ".join(f"[{x}] != {table(b, r['session'], {'addr': int(x.strip('$'), 16), 'len': 1})[0]}" for x in ad["finale"])
            a2.want = f"({names(a2.want, cfg)}) || {fin}"
        if ad.get("end_expr"): a2.want = f"({names(a2.want, cfg)}) || ({names(ad['end_expr'], cfg)})"   # reaching the level end counts as a hop
        a2.then = ad.get("then", "none"); a2.within = int(ad.get("within", 90)); a2.apply = True
        before = r["frames"]
        search(b, r, cfg, pkg, ch, a2)
        if fin and r["frames"] != before:
            hit = b.call("play_step", session=r["session"], frames=0, scale=0, ram=["f=" + fin])["ram"]["f"]
            if hit:
                print("advance: reached the level end (finale) - now step none until >>> GOAL"); store(pkg, ch, r); return
        if ad.get("end_expr") and r["frames"] != before:   # the level-end sequence started (SMB1: flagpole): no input matters, play it out
            if b.call("play_step", session=r["session"], frames=0, scale=0, ram=["e=" + names(ad["end_expr"], cfg)])["ram"]["e"]:
                print("advance: level end sequence - playing it out")
                res, ev = do_step(b, r, cfg, [], int(ad.get("end_frames", 1500)))
                ran = int(res.get("framesRun", 0) or 0)
                if ran: r["log"].append([[], ran]); r["frames"] += ran
                if ev == "GOAL": r["goal_frame"] = r["frames"]
                store(pkg, ch, r); show(res, ev, r); return
        if r["frames"] == before and ad.get("stall_expr"):   # a cutscene-ish state where only a push helps (SMB1: pipe entry state 7)
            if b.call("play_step", session=r["session"], frames=0, scale=0, ram=["s=" + names(ad["stall_expr"], cfg)])["ram"]["s"]:
                bt, n = ad.get("stall_input", "Right:30").rsplit(":", 1)
                res, ev = do_step(b, r, cfg, btns(bt), int(n))
                ran = int(res.get("framesRun", 0) or 0)
                if ran: r["log"].append([btns(bt), ran]); r["frames"] += ran
                print(f"  (stall state: pushed {bt} {n})")
                if ev == "GOAL": r["goal_frame"] = r["frames"]
                if ev: store(pkg, ch, r); show(res, ev, r); return
                before = -1
        if r["frames"] == before and ad.get("wait_pattern"):   # nothing reachable now: maybe the screen/platform must come first
            a3 = argparse.Namespace(**vars(a2)); a3.args = [ad["wait_pattern"]]; a3.vals = ad["wait_vals"]
            print("  (trying with a wait first)"); search(b, r, cfg, pkg, ch, a3)
        if r["frames"] == before and ad.get("up_want"):   # a step-up: any landing on a higher platform ahead (treetops, stairs)
            a4 = argparse.Namespace(**vars(a2)); a4.vals = ad.get("up_vals", ad["vals"])
            a4.want = ad["up_want"].replace("{X}", str(int(cur[ad["start"]]) + 16)).replace("{Y}", str(int(cur["y"]) - 24))
            print("  (trying a step up to a higher platform)"); search(b, r, cfg, pkg, ch, a4)
        if r["frames"] == before and stride > int(ad.get("min_stride", 32)):
            stride //= 2; print(f"  (no hop of that size: trying stride {stride})"); continue
        pat = int(getattr(a, "patience", 0) or ad.get("patience", 0))
        if r["frames"] == before and pat > 0:   # auto-scrollers: the next platform is not on screen yet, so stand still a bit and retry
            hopped = False
            for k in range(pat):
                res, ev = do_step(b, r, cfg, [], 30)
                ran = int(res.get("framesRun", 0) or 0)
                if ran: r["log"].append([[], ran]); r["frames"] += ran
                if ev == "DIED": print(">>> DIED while waiting"); store(pkg, ch, r); return
                mark = r["frames"]; search(b, r, cfg, pkg, ch, a2)
                if r["frames"] != mark: hopped = True; print(f"  (waited {k + 1}x30 frames, then hopped)"); break
            if not hopped:
                print(f"advance: still stuck after waiting {pat}x30 frames (the waits stay in the log: back to a checkpoint)"); store(pkg, ch, r); return
        if before == -1: before = r["frames"] - 1
        if r["frames"] != before: stride = int(ad["stride"])
        if r["frames"] == before or r.get("goal_frame"):
            print(f"advance: stopped after {i} hops (stuck before {ad['start']}={target}: look at the picture and solve this spot with search)")
            return
        name = f"a{r['frames']}"
        p = os.path.join(wdir(pkg, ch), name + ".state"); b.call("save_state", session=r["session"], path=p)
        r["subs"][name] = {"path": p, "log": len(r["log"]), "inputs": [x[:] for x in r["log"]], "frames": r["frames"], "armed": r["armed"]}
        store(pkg, ch, r); print(f"  saved checkpoint {name}")
    print(f"advance: {n} hops done")


def finish(b, r, cfg, pkg, ch, note):
    """Replays the log from the start state in a fresh session, records it, marks the milestone + writes the state."""
    for d in ("seg", "state", "thumb", "notes"): os.makedirs(os.path.join(pkg, d), exist_ok=True)
    b.call("close_session", session=r["session"])
    s = b.call("open_rom", path=r["rom"])["session"]
    if r["from"]: b.call("load_state", session=s, path=r["from"])
    b.call("record", session=s, action="start", text=ch)
    if sum(n for _, n in r["log"]) != r["frames"]:
        print(f"LOG BROKEN: the inputs add up to {sum(n for _, n in r['log'])} frames, the run says {r['frames']} (two commands ran at once?)"); return
    for bt, n in r["log"]:
        b.call("play_step", session=s, buttons=bt, frames=n, scale=0, milestones=["never=0"])
    rr = dict(r, session=s, armed=False)
    # the same goal check on the replayed machine (determinism gate)
    probe = {"goal": r["goal"], "until": r["until"], "lives0": r["lives0"], "table0": r["table0"], "session": s}
    if r["goal"] == "until":
        ok = b.call("play_step", session=s, frames=0, scale=0, ram=["g=" + r["until"]])["ram"]["g"] != 0
    else:
        on = b.call("play_step", session=s, frames=0, scale=0, ram=["m=" + cfg["on_map"]])["ram"]["m"] != 0
        ok = on and bool(newbits(r["table0"], table(b, s, cfg.get("progress_table") or {"addr": 0, "len": 0})))
    if not ok:
        print("REPLAY MISMATCH: the replayed log does not end at the goal (nothing written)"); r["session"] = s; store(pkg, ch, r); return
    raw = os.path.join(pkg, "state", ch + ".raw.state")
    mk = b.call("record", session=s, action="mark", text=ch, state_path=raw)
    try: b.screenshot(s, os.path.join(pkg, "thumb", ch + ".png"))
    except Exception: pass
    seg = os.path.join(pkg, "seg", ch + ".bnm")
    b.call("record", session=s, action="stop", path=seg)
    b.call("close_session", session=s); b.stop()
    r["finished"] = {"frames": r["frames"], "seg": seg, "raw": raw, "newbits": r.get("newbits"), "mark": mk}
    store(pkg, ch, r)
    if note: open(os.path.join(pkg, "notes", ch + ".md"), "w", encoding="utf-8").write(note)
    print(json.dumps({"finished": ch, "frames": r["frames"], "seg": seg, "raw_state": raw, "newbits": r.get("newbits")}))


if __name__ == "__main__":
    main()
