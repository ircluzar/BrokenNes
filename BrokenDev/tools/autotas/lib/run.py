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
  python -I run.py grid --ch 1-2                     TEXT view of the screen (framebuffer cells + OAM objects + VRAM tile ids), no frames run
  python -I run.py peek --ch 1-2 "Right+B:20 Right+B+A:15 none:20" [--grid]
                                                     plays a plan, prints RAM after each token (+ grid at the end), then puts
                                                     everything back: nothing enters the log (a look into the future)
  python -I run.py rewind --ch 1-2 N                 back N frames on the current line (nearest checkpoint + replay); the log is cut
  python -I run.py seek --ch 1-2 F                   to frame F of the current line (F <= now)
  python -I run.py probe --ch 1-2 [HOLD] [--vals Up,Right,A]  what each button does here (maps, menus): settle time, RAM changes, a picture each
  python -I run.py explore --ch 1-2 [HOLD] --vals Up,Down,Left,Right,A --key mapx,mapy --want 'mode != 0' [--apply]
                                                     breadth-first search over button presses (maps, menus) to a state where --want holds
  python -I run.py search ... --want 'air == 0' --all   REACH: every distinct spot the jump family lands on (x, y, frames)
  python -I run.py discover --ch 1-2 [N]            find RAM variables (x, y/air, speed, timers) by input experiments from here
  python -I run.py do --ch 1-2 "Right+B@x>=400 Right+B+A:14 none@air==0"   MOVEMENT SCRIPT, for real (peek takes the same script)
  python -I run.py map --ch 1-2 [WIDTH] [--cell 16]  the level map stitched from every grid seen so far
  python -I run.py tree --ch 1-2                     checkpoints as a branch tree (which ones are on the current line)
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

MCP_EXE = os.environ.get("AUTOTAS_MCP_EXE", r"C:\BrokenNes-work\enh_autotas_mcp\BrokenNes.Mcp.exe")


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
    ap.add_argument("--settle", type=int, default=0, help="search: the want must still be true this many frames later (no jump-apex false hits)")
    ap.add_argument("--patience", type=int, default=0, help="advance: when stuck, wait 30 frames and retry up to N times (auto-scrollers)")
    ap.add_argument("--stop-when", dest="stopwhen", help="step: also stop early when this expression is true")
    ap.add_argument("--wait", default="auto", help="search: auto = on an auto-scroller, also try waiting 0-120 frames first; off = never")
    ap.add_argument("--all", action="store_true", help="search: list every distinct landing spot (reach map) instead of the fastest")
    ap.add_argument("--key", help="explore: the RAM names that tell states apart (default: all)")
    ap.add_argument("--grid", action="store_true", help="step/look/peek: also print the text grid of the screen")
    ap.add_argument("--cell", type=int, default=8, help="grid: cell size in pixels (8 default; 16 = a quarter of the text)")
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
        st0 = os.path.join(wdir(pkg, ch), "_start.state"); b.call("save_state", session=sess, path=st0); r["start_state"] = st0
        r["lives0"] = b.call("play_step", session=sess, frames=0, scale=0, ram=["l=" + cfg["lives"]])["ram"]["l"]
        r["table0"] = table(b, sess, cfg.get("progress_table") or {"addr": 0, "len": 0})
        if a.goal == "level" and cfg.get("in_level"):   # starting inside a level: already armed (else the first step stops after 1 frame)
            r["armed"] = bool(b.call("play_step", session=sess, frames=0, scale=0, ram=["k=" + cfg["in_level"]])["ram"]["k"])
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
        # later checkpoints are kept: each stores its own inputs, so `resume` to one of them still works
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
        if a.grid: print(gridtext(b, r, cfg, pkg, ch, a.cell))
    elif a.cmd == "look":
        res = b.call("play_step", session=r["session"], frames=0, scale=0, ram=cfg["ram"])
        show(res, None, r, f"\n  look: {shot(b, r, pkg, ch)}")
        if a.grid: print(gridtext(b, r, cfg, pkg, ch, a.cell))
    elif a.cmd == "grid":
        print(gridtext(b, r, cfg, pkg, ch, a.cell))
    elif a.cmd == "peek":
        peek(b, r, cfg, pkg, ch, a)
    elif a.cmd in ("rewind", "seek"):
        target = r["frames"] - int(a.args[0]) if a.cmd == "rewind" else int(a.args[0])
        seek(b, r, cfg, pkg, ch, target)
    elif a.cmd == "tree":
        tree(r)
    elif a.cmd == "map":
        print_map(r, pkg, ch, a.cell, int(a.args[0]) if a.args else 120)
    elif a.cmd == "do":
        do_script(b, r, cfg, pkg, ch, a)
    elif a.cmd == "discover":
        discover(b, r, cfg, pkg, ch, a)
    elif a.cmd == "explore":
        explore(b, r, cfg, pkg, ch, a)
    elif a.cmd == "probe":
        probe(b, r, cfg, pkg, ch, a)
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


def gridtext(b, r, cfg, pkg, ch, cell=8):
    """Layer 0: the text view of the screen. Framebuffer cells ('.' backdrop, a symbol per colour), OAM objects (letters,
    '@' = the player when runner.json has "player_screen"), and the VRAM tile ids under the shapes (legend)."""
    from lib import view
    p = os.path.join(wdir(pkg, ch), "_grid.png"); b.screenshot(r["session"], p, scale=1)
    try: sp = b.call("sprites", session=r["session"], draw=False)["sprites"]
    except Exception: sp = None
    player = None
    if cfg.get("player_screen"):
        e = cfg["player_screen"]
        rr = b.call("play_step", session=r["session"], frames=0, scale=0, ram=["sx=" + names(e[0], cfg), ("sy=" + names(e[1], cfg)) if len(e) > 1 else "sy=0-1"])["ram"]
        player = (int(rr["sx"]), int(rr["sy"]) if int(rr["sy"]) >= 0 else None)
    lf = os.path.join(wdir(pkg, ch), "_legend.json")
    try: leg = json.load(open(lf))
    except Exception: leg = {}
    g, info = view.grid(p, sp, cell=cell, player=player, legend_in=leg.get("colours"))
    remember_columns(b, r, cfg, pkg, ch, info, player)
    leg["colours"] = info["legend"]
    out = [g, view.legend_text(info)]
    try:   # VRAM: which tile ids make the shapes (stable names for solid things, doors, hazards)
        tm = b.call("tilemap", session=r["session"])
        ids = view.tile_ids(tm, info, p)
        if ids: out.append(" VRAM tiles under each symbol (most common first): " + ids)
        out.append(scroll_note(b, r, tm, player))
    except Exception: pass
    json.dump(leg, open(lf, "w"))
    return "\n".join(out)


def script_steps(text):
    """Layer 2, movement scripts: tokens 'Buttons:N' (hold N frames) or 'Buttons@EXPR' (hold until EXPR, at most 300 frames)
    or 'Buttons@EXPR~N' (at most N). EXPR uses the runner's RAM names: 'Right+B@x>=400', 'none@air==0', 'A@y<=40~30'."""
    out = []
    for tok in text.split():
        if "@" in tok:
            bt, ex = tok.split("@", 1); mx = 300
            if "~" in ex: ex, m_ = ex.rsplit("~", 1); mx = int(m_)
            out.append((btns(bt), mx, ex))
        else:
            bt, n = tok.rsplit(":", 1); out.append((btns(bt), int(n), None))
    return out


def do_script(b, r, cfg, pkg, ch, a):
    """Plays a movement script FOR REAL (into the log), stopping at a death or goal event."""
    for bt, n, ex in script_steps(a.args[0]):
        res, ev = do_step(b, r, cfg, bt, n, names(ex, cfg) if ex else None)
        ran = int(res.get("framesRun", 0) or 0)
        if ran: r["log"].append([bt, ran]); r["frames"] += ran
        if ev == "GOAL": r["goal_frame"] = r["frames"]
        show(res, ev, r)
        if ex and res.get("stoppedBy") not in ("condition",) and not ev: print(f"  (never met {ex} within {n} frames)")
        if ev and ev != "entered level": break
    store(pkg, ch, r)
    if a.grid: print(gridtext(b, r, cfg, pkg, ch, a.cell))


def remember_columns(b, r, cfg, pkg, ch, info, player):
    """Layer 5: every grid seen is stitched into a level map by world position (world x of the screen's left edge =
    the player's x minus the player's screen x). Sprites are left out (they move); `map` prints it with the trail."""
    if not player or "x" not in dict(e.split("=", 1) for e in cfg["ram"]): return
    x = int(b.call("play_step", session=r["session"], frames=0, scale=0, ram=["x=" + names("x", cfg)])["ram"]["x"])
    cell = info["cell"]; left = x - int(player[0])
    f = os.path.join(wdir(pkg, ch), "_map.json")
    try: m = json.load(open(f))
    except Exception: m = {}
    cols = m.setdefault(str(cell), {})
    rows = info["rows_text"]
    for c in range(len(rows[0])):
        wc = (left + c * cell + cell // 2) // cell
        cols[str(wc)] = "".join("." if (ch_.isalpha() or ch_ in "@*") else ch_ for ch_ in (row[c] for row in rows))
    m.setdefault("trail", []).append([x, r["frames"]]); m["trail"] = m["trail"][-400:]
    json.dump(m, open(f, "w"))


def print_map(r, pkg, ch, cell, width):
    f = os.path.join(wdir(pkg, ch), "_map.json")
    try: m = json.load(open(f))
    except Exception: sys.exit("map: no grid seen yet (run grid / look --grid / step --grid as you go)")
    cols = {int(k): v for k, v in m.get(str(cell), {}).items()}
    if not cols: sys.exit(f"map: no grid at --cell {cell} seen yet")
    lo, hi = min(cols), max(cols)
    if width and hi - lo + 1 > width: lo = max(lo, hi - width + 1)
    H = len(next(iter(cols.values())))
    last = m["trail"][-1][0] // cell if m.get("trail") else None
    print(f"level map from grids seen, columns {lo}..{hi} ({cell} px each, world x = column*{cell}); '?' = never seen, '|' = you now")
    print("     " + "".join(str((c // 10) % 10) if c % 10 == 0 else " " for c in range(lo, hi + 1)))
    for rr in range(H):
        print(f"{rr:3d}  " + "".join("|" if c == last and cols.get(c, "?" * H)[rr] == "." else cols.get(c, "?" * H)[rr] for c in range(lo, hi + 1)))


def autoscrolls(b, r):
    try:
        tm = b.call("tilemap", session=r["session"])
        return "AUTO-SCROLL" in scroll_note(b, r, tm, None)
    except Exception: return False


def scroll_note(b, r, tm, player):
    """Does the screen move by itself? Runs 16 frames with no input, reads the scroll again, puts the machine back.
    An auto-scroller says so (and how far the player is from the screen edge that pushes)."""
    import tempfile
    tmp = os.path.join(tempfile.gettempdir(), f"_scroll_{os.getpid()}.state")
    b.call("save_state", session=r["session"], path=tmp)
    try:
        b.call("play_step", session=r["session"], frames=16, scale=0)
        t2 = b.call("tilemap", session=r["session"])
    finally:
        b.call("load_state", session=r["session"], path=tmp)
    W, H = int(tm["mapCols"]) * int(tm["cell"]), int(tm["mapRows"]) * int(tm["cell"])
    dx = (int(t2["scrollX"]) - int(tm["scrollX"]) + W // 2) % W - W // 2
    dy = (int(t2["scrollY"]) - int(tm["scrollY"]) + H // 2) % H - H // 2
    if dx == 0 and dy == 0: return " scroll: the screen stands still with no input (it follows the player, or a fixed room)"
    note = f" scroll: AUTO-SCROLL - the screen moves {dx:+d},{dy:+d} px per 16 frames with no input: things past the screen edge arrive later (wait for them), the trailing edge pushes/crushes you"
    if player and dx > 0: note += f"; you are {player[0]} px from the left edge"
    return note


def probe(b, r, cfg, pkg, ch, a):
    """What does each button do HERE? (maps, menus, cutscenes). For each button: hold it H frames, then no input until the
    picture stops changing (max 240); prints the frames to settle, the RAM names that changed, and a picture per button.
    Nothing is logged. probe [H] [--vals Up,Down,...]"""
    hold = int(a.args[0]) if a.args else 8
    buttons = (a.vals or "Up,Down,Left,Right,A,B,Start,Select").split(",")
    tmp = os.path.join(wdir(pkg, ch), "_probe.state")
    b.call("save_state", session=r["session"], path=tmp)
    base = b.call("play_step", session=r["session"], frames=0, scale=0, ram=cfg["ram"])["ram"]
    def phash():
        t = b.raw("screenshot", session=r["session"], scale=1)
        j = t.get("text") if isinstance(t.get("text"), dict) else {}
        for im in t.get("images") or []:
            try: os.remove(im)
            except OSError: pass
        return j.get("hash")
    h0 = phash()
    try:
        for bt in buttons:
            b.call("load_state", session=r["session"], path=tmp)
            b.call("play_step", session=r["session"], buttons=btns(bt), frames=hold, scale=0)
            # settle on the RAM names (pictures animate forever on many maps): 3 equal reads 8 frames apart
            prev, still, n = None, 0, hold
            while n < hold + 240 and still < 3:
                ram = b.call("play_step", session=r["session"], frames=8, scale=0, ram=cfg["ram"])["ram"]; n += 8
                still = still + 1 if ram == prev else 0; prev = ram
            last = phash()
            ch_ = " ".join(f"{k}:{base[k]}->{v}" for k, v in ram.items() if base.get(k) != v)
            p = shot(b, r, pkg, ch, f"probe_{bt}.png")
            print(f"{bt:>7}: settled {n} frames after the press began; RAM {ch_ or 'same'}  ({p})")
    finally:
        b.call("load_state", session=r["session"], path=tmp)
    print("(probe: machine put back, nothing logged; to act: step <button> <hold> then step none <settle>)")


def workram(b, sess):
    """The console's work RAM (the 'Work' domains, up to 64 KB), read whole: {domain: bytes}."""
    out = {}
    for d in b.call("list_domains", session=sess)["domains"]:
        if d.get("kind") == "Work" and not d.get("unavailable") and d["size"] <= 65536:
            buf = []
            for off in range(0, d["size"], 4096):
                m = b.call("read_memory", session=sess, domain=d["id"], address=off, length=min(4096, d["size"] - off))
                buf += m["bytes"] if isinstance(m.get("bytes"), list) else list(bytes.fromhex(m["hex"].replace(" ", "")))
            out[d["id"]] = buf
    return out


def discover(b, r, cfg, pkg, ch, a):
    """Layer 3, variable discovery (any game, any console): plays short input experiments from HERE (then puts the machine
    back) and ranks work-RAM bytes by how they answer: position X (up while Right is held, down with Left), position Y /
    in-air (moves with A), speed (grows while Right is held, back to 0 when released), timers (count every frame, no
    matter the input). Prints candidates as runner.json expressions to check against the picture (grid '@')."""
    n = int(a.args[0]) if a.args else 24
    tmp = os.path.join(wdir(pkg, ch), "_discover.state")
    b.call("save_state", session=r["session"], path=tmp)
    def run(plan):  # noqa: the experiments
        b.call("load_state", session=r["session"], path=tmp); snaps = [workram(b, r["session"])]
        for bt, k in plan:
            b.call("play_step", session=r["session"], buttons=btns(bt), frames=k, scale=0); snaps.append(workram(b, r["session"]))
        return snaps
    try:
        right = run([("Right", n // 3)] * 3); left = run([("Left", n // 3)] * 3); idle = run([("none", n // 3)] * 3)
        jump = run([("A", 4), ("A", 4), ("A", 4), ("none", 8), ("none", 8), ("none", 8), ("none", 8)]); stop = run([("Right", n), ("none", n)])
    finally:
        b.call("load_state", session=r["session"], path=tmp)
    def s8(v): return v - 256 if v > 127 else v
    found = {"x": [], "y/air": [], "speed": [], "timer": []}
    for dom, base in right[0].items():
        for i in range(len(base)):
            R = [sn[dom][i] for sn in right]; L = [sn[dom][i] for sn in left]; I = [sn[dom][i] for sn in idle]
            J = [sn[dom][i] for sn in jump]; S = [sn[dom][i] for sn in stop]
            dr = [s8((R[k + 1] - R[k]) & 255) for k in range(3)]; dl = [s8((L[k + 1] - L[k]) & 255) for k in range(3)]
            name = f"[{dom}:${i:04X}]" if dom != "RAM" else f"[${i:04X}]"
            if I[0] != I[-1] and len(set(I)) == 4 and R[1:] == I[1:] and L[1:] == I[1:] and J[:2] == I[:2]:   # the same with any input
                found["timer"].append((1, name, I)); continue
            if all(x > 0 for x in dr) and all(x <= 0 for x in dl) and max(dr) <= 32:   # Left may be blocked (a screen edge)
                found["x"].append((sum(dr) - sum(dl), name, R))
            elif len(set(I)) == 1 and J[1:] != I[1:] and R[1:] == I[1:] and L[1:] == I[1:]:
                dj = [s8((J[k + 1] - J[k]) & 255) for k in range(len(J) - 1)]
                if max(abs(x) for x in dj) <= 48:   # a body moves a little a frame; big jumps are random numbers / pointers
                    sg = [x > 0 for x in dj if x != 0]
                    flips = sum(1 for k in range(len(sg) - 1) if sg[k] != sg[k + 1])
                    # a jump is an ARC: one way, then back (one sign flip, mostly back near the start) = y; few values = an in-air flag
                    if flips == 1 and abs(s8((J[-1] - J[0]) & 255)) <= max(8, abs(sum(dj[:3])) // 2): score = 0
                    elif len(set(J)) <= 3: score = 1
                    else: score = 2 + flips
                    found["y/air"].append((score, name, J))
            elif S[0] == I[0] and S[1] != S[0] and S[2] == S[0] and R[1] != R[0] and I[1:] == I[:1] * 3:
                found["speed"].append((abs(s8((S[1] - S[0]) & 255)), name, S))
    for k, lst in found.items():
        lst.sort(key=lambda t: t[0] if k == "y/air" else -t[0])   # y/air: flags and smooth arcs first
        print(f"{k:6}: " + ("  ".join(f"{nm} {vals}" for _, nm, vals in lst[:8]) or "none found here (try where the player can move freely)"))
    print("(discover: machine put back; an x high byte is the byte that steps by 1 when the low byte wraps - pair them as hi*256+lo)")


def explore(b, r, cfg, pkg, ch, a):
    """Breadth-first search over discrete moves (maps, menus, grid puzzles): a move = hold a button H frames, then no input
    until the --key RAM names stop changing. States are told apart by --key. Stops at the first state where --want is true
    (or lists every state reached). --apply plays the found path into the log.
    explore [H] --vals Up,Down,Left,Right --key mapx,mapy --want 'mode != 0' [--within 40 states] [--apply]"""
    hold = int(a.args[0]) if a.args else 8
    moves = (a.vals or "Up,Down,Left,Right").split(",")
    keys = (a.key or ",".join(e.split("=", 1)[0] for e in cfg["ram"])).split(",")
    m = dict(e.split("=", 1) for e in cfg["ram"])
    kram = [f"{k}={m[k]}" if k in m else f"{k}={k}" for k in keys]
    want = names(a.want, cfg) if a.want else None
    limit = a.within if a.within != 120 else 40
    d = os.path.join(wdir(pkg, ch), "_explore"); os.makedirs(d, exist_ok=True)
    def settle():
        prev, still, n = None, 0, 0
        while n < 400 and still < int(os.environ.get("AUTOTAS_SETTLE", 6)):   # 6 equal reads 8 apart: a walk between map nodes changes nothing for ~30 frames
            v = b.call("play_step", session=r["session"], frames=8, scale=0, ram=kram)["ram"]; n += 8
            still = still + 1 if v == prev else 0; prev = v
        return n, tuple(prev.values())
    def wanted():
        return bool(want) and bool(b.call("play_step", session=r["session"], frames=0, scale=0, ram=["w=" + want])["ram"]["w"])
    root = os.path.join(d, "s0.state"); b.call("save_state", session=r["session"], path=root)
    k0 = tuple(b.call("play_step", session=r["session"], frames=0, scale=0, ram=kram)["ram"].values())
    seen = {k0: (root, [])}; queue = [k0]; found = None
    try:
        while queue and not found and len(seen) <= limit:
            k = queue.pop(0); st, path = seen[k]
            for mv in moves:
                b.call("load_state", session=r["session"], path=st)
                b.call("play_step", session=r["session"], buttons=btns(mv), frames=hold, scale=0)
                n, k2 = settle(); p2 = path + [[btns(mv), hold], [[], n]]
                if wanted(): found = (k2, p2, mv); break
                if k2 not in seen:
                    f = os.path.join(d, f"s{len(seen)}.state"); b.call("save_state", session=r["session"], path=f)
                    seen[k2] = (f, p2); queue.append(k2)
    finally:
        b.call("load_state", session=r["session"], path=root)
    def show_path(p): return " ".join(f"{'+'.join(x[0]) or 'none'}:{x[1]}" for x in p)
    if not found:
        print(f"explore: {len(seen)} states reached ({', '.join(keys)}), none meets --want" if want else f"explore: {len(seen)} states reached:")
        for kk, (f, p) in list(seen.items())[:40]: print(f"  {dict(zip(keys, kk))}  via {show_path(p) or '(here)'}")
        return
    print(f"FOUND after {sum(x[1] for x in found[1])} frames: {show_path(found[1])}  -> {dict(zip(keys, found[0]))}")
    if want:   # tighten: shorten each hold and wait while the path still reaches --want (a TAS wastes no frames)
        path = [x[:] for x in found[1]]
        def ok(pth):
            b.call("load_state", session=r["session"], path=root)
            for bt, n in pth:
                if n > 0: b.call("play_step", session=r["session"], buttons=bt, frames=n, scale=0)
            return wanted()
        try:
            for i in range(len(path)):
                lo, hi = (1 if path[i][0] else 0), path[i][1]
                if i == len(path) - 1:   # the last wait: just until want turns true
                    b.call("load_state", session=r["session"], path=root)
                    for bt, n in path[:-1]: b.call("play_step", session=r["session"], buttons=bt, frames=n, scale=0)
                    res = b.call("play_step", session=r["session"], frames=hi, until=want, max_frames=hi, scale=0)
                    path[i][1] = int(res.get("framesRun", hi) or hi); continue
                while lo < hi:
                    mid = (lo + hi) // 2; t = [x[:] for x in path]; t[i][1] = mid
                    if ok(t): hi = mid
                    else: lo = mid + 1
                path[i][1] = hi
        finally:
            b.call("load_state", session=r["session"], path=root)
        found = (found[0], [x for x in path if x[1] > 0], found[2])
        print(f"tightened to {sum(x[1] for x in found[1])} frames: {show_path(found[1])}")
    if a.apply:
        for bt, n in found[1]:
            res, ev = do_step(b, r, cfg, bt, n)
            ran = int(res.get("framesRun", 0) or 0)
            if ran: r["log"].append([bt, ran]); r["frames"] += ran
            if ev and ev != "entered level": print(">>> " + ev); break
        store(pkg, ch, r)
        res = b.call("play_step", session=r["session"], frames=0, scale=0, ram=cfg["ram"])
        show(res, "applied the path", r)


def seek(b, r, cfg, pkg, ch, target):
    """Time machine on the current line: load the latest checkpoint at or before frame `target` whose inputs are a prefix of
    the log, replay the log up to `target`, cut the log there."""
    if target < 0 or target > r["frames"]: sys.exit(f"seek: frame {target} is not on the current line (0..{r['frames']})")
    best = None
    for name, s in r["subs"].items():
        ins = s.get("inputs")
        if ins is None or s["frames"] > target or ins != r["log"][:len(ins)]: continue
        if best is None or s["frames"] > best[1]["frames"]: best = (name, s)
    if best:
        b.call("load_state", session=r["session"], path=best[1]["path"]); at = best[1]["frames"]; done = len(best[1]["inputs"]); r["armed"] = best[1]["armed"]
    else:
        if not r.get("start_state") or not os.path.exists(r["start_state"]): sys.exit("seek: no checkpoint on this line before that frame (save one first)")
        b.call("load_state", session=r["session"], path=r["start_state"]); at = 0; done = 0; r["armed"] = False
    newlog = [x[:] for x in r["log"][:done]]
    for bt, n in r["log"][done:]:
        if at >= target: break
        k = min(n, target - at)
        b.call("play_step", session=r["session"], buttons=bt, frames=k, scale=0); newlog.append([bt, k]); at += k
    r["log"] = newlog; r["frames"] = at; r.pop("goal_frame", None)
    store(pkg, ch, r)
    res = b.call("play_step", session=r["session"], frames=0, scale=0, ram=cfg["ram"])
    show(res, f"at frame {at} (from {'checkpoint ' + best[0] if best else 'the start'})", r)


def tree(r):
    """Checkpoints as a tree: a checkpoint is the child of the latest earlier one whose inputs are a prefix of its own."""
    subs = sorted(((n, s) for n, s in r["subs"].items() if "inputs" in s), key=lambda t: t[1]["frames"])
    par = {}
    for n, s in subs:
        p = None
        for m, t in subs:
            if t["frames"] < s["frames"] and t["inputs"] == s["inputs"][:len(t["inputs"])]:
                if p is None or t["frames"] > r["subs"][p]["frames"]: p = m
        par[n] = p
    def on_line(s): return s["inputs"] == r["log"][:len(s["inputs"])]
    def walk(p, depth):
        for n, s in subs:
            if par[n] == p:
                print("  " * depth + f"{n} @{s['frames']}" + ("  *" if on_line(s) else ""))
                walk(n, depth + 1)
    print(f"now: frame {r['frames']}  (* = on the current line)")
    walk(None, 0)


def peek(b, r, cfg, pkg, ch, a):
    """Plays a plan from here and reports, then restores the machine: nothing is logged. Plan tokens: Buttons:frames."""
    tmp = os.path.join(wdir(pkg, ch), "_peek.state")
    b.call("save_state", session=r["session"], path=tmp)
    dead = [f'died={cfg["dead"]}'] if cfg.get("dead") else []
    t = 0
    try:
        for (bt_, n, ex), tok in zip(script_steps(a.args[0]), a.args[0].split()):
            res = b.call("play_step", session=r["session"], buttons=bt_, frames=n, until=names(ex, cfg) if ex else None,
                         max_frames=n if ex else None, scale=0, milestones=dead or None, ram=cfg["ram"])
            t += int(res.get("framesRun", 0) or 0)
            ram = res.get("ram") or {}
            print(f"  +{t:4d} {tok:<16} " + " ".join(f"{k}={v}" for k, v in ram.items()) + ("  >>> DIES" if res.get("stoppedBy") == "milestone" else ""))
            if res.get("stoppedBy") == "milestone": break
        if a.grid: print(gridtext(b, r, cfg, pkg, ch, a.cell))
        if a.shot: print("  look (future): " + shot(b, r, pkg, ch, "peek.png"))
    finally:
        b.call("load_state", session=r["session"], path=tmp)
    print("(peek: machine put back, nothing logged)")


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
    if getattr(a, "wait", "auto") != "off" and not pat.split()[0].startswith("none:") and "w" not in vals and autoscrolls(b, r):
        # an auto-scroller: what to land on may not have arrived yet, so every try may first wait (layer 4, wait-aware search)
        pat = "none:{w} " + pat; vals["w"] = list(range(0, 121, 30)); print("  (auto-scroll: trying waits of 0-120 frames first)")
    keys = list(vals)
    import math
    n = math.prod(len(v) for v in vals.values()) if vals else 1
    if n > 2000: sys.exit(f"search: {n} combos is too many: use coarser steps (at most ~1500)")
    tmp = os.path.join(wdir(pkg, ch), "_search.state")
    b.call("save_state", session=r["session"], path=tmp)
    dead = [f'died={cfg["dead"]}'] if cfg.get("dead") else []
    results, died, missed = [], 0, []
    combos = [dict(zip(keys, c)) for c in (itertools.product(*[vals[k] for k in keys]) if keys else [()])]
    def plan_steps(env):
        st = []
        for tok in pat.split():
            bt, k = tok.rsplit(":", 1); k = int(k.format(**env))
            if k > 0: st.append([btns(bt), k])
        return st
    try:   # fast path: the MCP try_plans tool runs the whole grid in-process (one call per 400 tries)
        for c0 in range(0, len(combos), 400):
            chunk = combos[c0:c0 + 400]
            plans = [" ".join(("+".join(bt) or "none") + f":{k}" for bt, k in plan_steps(env)) or "none:0" for env in chunk]
            out = b.call("try_plans", session=r["session"], plans=plans, then=btns(a.then), within=a.within, want=a.want,
                         dead=cfg.get("dead"), settle=getattr(a, "settle", 0) or 0, survive=a.survive if cfg.get("dead") else 0, ram=cfg["ram"])
            for res in out["results"]:
                env = chunk[res["i"]]; v = res["verdict"]; st = plan_steps(env)
                if v == "ok":
                    tot = int(res["frames"]); pf = sum(k for _, k in st)   # frames = the plan + the wait for want
                    results.append((tot, env, st + [[btns(a.then), tot - pf]], res.get("ram")))
                elif v in ("died", "doomed"): died += 1
                else: missed.append((env, None))
        combos = []
    except Exception as e:
        if "try_plans" not in str(e) and "unknown tool" not in str(e).lower(): raise
        results, died, missed = [], 0, []
    for combo in [tuple(c.values()) for c in combos]:
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
        if getattr(a, "settle", 0):   # reject jump-apex false hits: the want must still hold a few frames later
            st = b.call("play_step", session=r["session"], buttons=btns(a.then), frames=a.settle, scale=0, milestones=dead or None, ram=["w=" + a.want])
            if st.get("stoppedBy") == "milestone": died += 1; continue
            if not st["ram"]["w"]: missed.append((env, res.get("ram"))); continue
            b.call("load_state", session=r["session"], path=tmp)
            for bt, n in steps: b.call("play_step", session=r["session"], buttons=bt, frames=n, scale=0)
            b.call("play_step", session=r["session"], buttons=btns(a.then), frames=int(res.get("framesRun", 0) or 0), scale=0)
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
    if getattr(a, "all", False):   # layer 4 (reach): every distinct place the tries land, not just the fastest
        spots = {}
        for t, env, _, ram in results:
            ram = ram or {}
            key = tuple((k, int(v) // 16) for k, v in ram.items() if k in ("x", "y"))
            if key not in spots or t < spots[key][0]: spots[key] = (t, env, ram)
        print(f"reach: {len(spots)} distinct landing spots ({len(results)} live tries, {died} died):")
        for key, (t, env, ram) in sorted(spots.items(), key=lambda kv: [-v for _, v in kv[0]]):
            print(f"  {' '.join(f'{k}={ram[k]}' for k, _ in key)}  in {t} frames  {env}")
        if not a.apply: return
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
    if ad.get("ready"):   # the level may still be loading (black screen, junk x): let it finish before any hop
        for _ in range(40):
            if b.call("play_step", session=r["session"], frames=0, scale=0, ram=["k=" + names(ad["ready"], cfg)])["ram"]["k"]: break
            res, ev = do_step(b, r, cfg, [], 10)
            ran = int(res.get("framesRun", 0) or 0)
            if ran: r["log"].append([[], ran]); r["frames"] += ran
        store(pkg, ch, r)
    import time
    t0 = time.time(); cap = float(os.environ.get("AUTOTAS_ADVANCE_SECONDS", 420))
    for i in range(n):
        if time.time() - t0 > cap:   # return before the agent's 10-minute tool timeout (a backgrounded advance loses the player)
            print(f"advance: time cap ({int(cap)} s) after {i} hops - run advance again to continue"); return
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
        if r["frames"] == before and pat == 0 and autoscrolls(b, r):   # an auto-scroller: the next platform may not be there yet
            pat = 12; print("  (the screen scrolls by itself: waiting for what comes next)")
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
