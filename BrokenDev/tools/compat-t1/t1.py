"""Great Compatibility Catalog P3/T1: the scripted Tier-1 checker (no model calls).

Per game: boot on BrokenNes's accuracy core, find the title screen (picture change detection), press Start, play a
generic per-console input script for a few minutes, run the same script on the reference emulator (Mesen 2.1.1,
headless test runner), compare checkpoint pictures palette-blind, grade (OK / Jank / Broken / Nothing + tags), keep
checkpoint screenshots + a .bnm movie, and write the catalog record (catalog.update).

    python t1.py run --console nes [--limit N] [--only SHA1,..] [--force] [--minutes 2]
    python t1.py one --console nes --sha1 <sha1>        (one game, prints the result)
    python t1.py report --console nes                   (grade counts + failure clusters from the records)

Resume: a game whose record already has extra.t1.version == T1_VERSION is skipped (unless --force), and every game's
result is written to the catalog the moment it is graded, so an interrupted run just starts again.

Heavy-job lock: every emulator run takes one of the machine-wide slots C:\\BrokenNes-work\\heavy\\slot1..4 (mkdir is
atomic) and drops it as soon as that run ends. This driver uses one slot at a time (its runs are sequential).
Only NES is wired up so far; other consoles need their own runner (ConsoleRunCli) and Mesen/BizHawk settings.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import time
from collections import Counter, defaultdict

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "catalog"))
import catalog  # noqa: E402

T1_VERSION = "t1-1"
HEAVY = r"C:\BrokenNes-work\heavy"
WORKSHOP = os.environ.get("BNT1_WORKSHOP", r"C:\BrokenNes-work\cc_t1_bin\BrokenNes.Workshop.exe")
MESEN = os.environ.get("BNT1_MESEN", r"T:\BrokenDev\env\emulators\mesen-2.1.1\Mesen.exe")
SCRATCH = os.environ.get("BNT1_SCRATCH", r"C:\BrokenNes-work\cc_t1_run\work")
LANE = os.environ.get("BNT1_LANE", "cc-t1")
CORES = "CPU_FIX/PPU_FIX/APU_FIX"
REF_NAME = "Mesen 2.1.1"
PROBE_FRAMES = 1200
PROBE_STEP = 30
WIN = 3
REF_LAG = 3   # Mesen's script counts its first frames late: the same picture is ~3 frames lower on its counter (SMB: exactly 3)


# ------------------------------------------------------------------------------------------------ heavy-job slots
class Slot:
    waited = 0.0
    held = 0   # one game's runs are one sequential job: the outer Slot holds it, inner ones reuse it

    def __enter__(self):
        Slot.held += 1
        if Slot.held > 1:
            self.path = None
            return self
        os.makedirs(HEAVY, exist_ok=True)
        t0 = time.time()
        while True:
            for i in range(1, 5):
                p = os.path.join(HEAVY, f"slot{i}")
                try:
                    os.mkdir(p)
                except FileExistsError:
                    continue
                with open(os.path.join(p, "owner.txt"), "w") as f:
                    f.write(f"{LANE} PID {os.getpid()} {time.strftime('%Y-%m-%d %H:%M:%S')}\n")
                self.path = p
                Slot.waited += time.time() - t0
                return self
            time.sleep(30)

    def __exit__(self, *a):
        Slot.held -= 1
        if self.path:
            shutil.rmtree(self.path, ignore_errors=True)


def run(cmd, timeout, env=None, cwd=None):
    t = time.time()
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, env=env, cwd=cwd,
                           creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        return p.returncode, p.stdout or "", p.stderr or "", time.time() - t, False
    except subprocess.TimeoutExpired as e:
        return None, (e.stdout or b"").decode("utf-8", "replace") if isinstance(e.stdout, bytes) else (e.stdout or ""), "", time.time() - t, True


# ------------------------------------------------------------------------------------------------ pictures
def load_png(path):
    return np.asarray(Image.open(path).convert("RGB"), dtype=np.int32)


def load_raw(path):
    with open(path, "rb") as f:
        head = f.readline().split()
        w, h = int(head[0]), int(head[1])
        a = np.frombuffer(f.read(w * h * 4), dtype="<u4").reshape(h, w)
    return np.stack([(a >> 16) & 255, (a >> 8) & 255, a & 255], axis=-1).astype(np.int32)


def luma(img, w=None, h=None):
    l = (img[..., 0] * 299 + img[..., 1] * 587 + img[..., 2] * 114) // 1000
    if w is not None and (l.shape[1] != w or l.shape[0] != h):
        ys = (np.arange(h) * l.shape[0]) // h
        xs = (np.arange(w) * l.shape[1]) // w
        l = l[ys][:, xs]
    return l


def picdiff(a, b):
    """BrokenDev/mcp/Reference.cs PicDiff.Grid, ported: palette-blind (brightness cells + edge overlap). 0 same, <=0.02 same up
    to palette, <=0.12 close, else different."""
    h, w = min(a.shape[0], b.shape[0]), min(a.shape[1], b.shape[1])
    la, lb = luma(a, w, h), luma(b, w, h)
    pixel = float(np.mean(np.abs(la - lb) > 42))

    def edges(l):
        e = np.zeros(l.shape, bool)
        e[:-1, :-1] = (np.abs(l[:-1, :-1] - l[:-1, 1:]) > 24) | (np.abs(l[:-1, :-1] - l[1:, :-1]) > 24)
        return e
    ea, eb = edges(la), edges(lb)
    either = int(np.sum(ea | eb))
    edg = 0.0 if either == 0 else 1 - float(np.sum(ea & eb)) / either
    return round((pixel + edg) / 2, 4)


def blank(img):
    return int(np.ptp(luma(img))) <= 8


def colours(img):
    return len(np.unique(img.reshape(-1, 3), axis=0))


# ------------------------------------------------------------------------------------------------ input scripts
def generic_script(start, minutes):
    """Start twice (some titles need a second press after a fade, or a menu confirm), then a walk-right/jump/fire loop with a
    little of everything (menus react to A/Start/Down). Frame numbers are absolute; a step holds its buttons until the next."""
    s = [(start, "Start"), (start + 8, ""), (start + 240, "Start"), (start + 248, ""), (start + 420, "A"), (start + 428, "")]
    f = start + 480
    end = f + int(minutes * 3600)
    loop = [(120, "Right"), (20, "Right+A"), (50, "Right"), (16, "Right+B"), (40, "Right"), (24, "A"), (30, ""),
            (60, "Left"), (12, "B"), (30, "Down"), (30, "Up"), (20, "Right+A"), (8, "")]
    i = 0
    while f < end:
        d, b = loop[i % len(loop)]
        s.append((f, b))
        f += d
        i += 1
    s.append((end, ""))
    return ",".join(f"{a}:{b}" for a, b in s), end


# ------------------------------------------------------------------------------------------------ the NES runner
def nes_romtest(rom, frames, png_at, out_dir, script=None):
    os.makedirs(out_dir, exist_ok=True)
    rep = os.path.join(out_dir, "romtest.json")
    cmd = [WORKSHOP, "--romtest", "--rom", rom, "--cpu", "FIX", "--ppu", "FIX", "--apu", "FIX", "--frames", str(frames),
           "--power-on-ram", "zeros", "--png-at", ",".join(map(str, sorted(set(png_at)))), "--png-dir", out_dir, "--json", "--out", rep]
    if script:
        cmd += ["--input", script]
    with Slot():
        code, out, err, secs, to = run(cmd, timeout=max(120, frames // 40))
    j = None
    if os.path.exists(rep):
        try:
            with open(rep, encoding="utf-8-sig") as f:
                j = json.load(f)
        except Exception:
            j = None
    pngs = {}
    for n in os.listdir(out_dir):
        if n.endswith(".png") and "_f" in n:
            try:
                pngs[int(n.rsplit("_f", 1)[1][:-4])] = os.path.join(out_dir, n)
            except ValueError:
                pass
    return dict(code=code, out=out, err=err, secs=round(secs, 1), timeout=to, report=j, pngs=pngs)


def nes_record(rom, frames, script, out, png_at, png_dir):
    """The scripted run on BrokenNes: --movie record writes the .bnm and the checkpoint pictures in one run."""
    os.makedirs(png_dir, exist_ok=True)
    cmd = [WORKSHOP, "--movie", "record", "--rom", rom, "--frames", str(frames), "--input", script, "--cores", "FIX/FIX/FIX",
           "--power-on", "zeros", "--build", T1_VERSION, "--png-at", ",".join(map(str, sorted(set(png_at)))), "--png-dir", png_dir, "--out", out]
    with Slot():
        code, o, e, secs, to = run(cmd, timeout=max(180, frames // 30))
    pngs = {}
    for n in os.listdir(png_dir):
        if n.startswith("f") and n.endswith(".png"):
            pngs[int(n[1:-4])] = os.path.join(png_dir, n)
    return dict(code=code, out=o, err=e, secs=round(secs, 1), timeout=to, pngs=pngs, ok=code == 0 and os.path.exists(out))


MESEN_SETTINGS = {
    "Version": "2.1.1", "ConfigUpgrade": 5, "Audio": {"EnableAudio": False},
    "Nes": {"Port1": {"Type": "NesController"}, "Port2": {"Type": "NesController"}, "RamPowerOnState": "AllZeros"},
    "Preferences": {"SingleInstance": False, "AutomaticallyCheckForUpdates": False, "PauseWhenInBackground": False,
                    "PauseWhenInMenusAndConfig": False, "EnableAutoSaveState": False},
    "Debug": {"ScriptWindow": {"AllowIoOsAccess": True, "AllowNetworkAccess": False}},
}


def mesen_run(rom, script, shots, out_dir, frames):
    home = os.path.join(SCRATCH, "_mesen_home")
    os.makedirs(home, exist_ok=True)
    exe = os.path.join(home, "Mesen.exe")
    if not os.path.exists(exe) or os.path.getsize(exe) != os.path.getsize(MESEN):
        shutil.copy2(MESEN, exe)
    with open(os.path.join(home, "settings.json"), "w") as f:
        json.dump(MESEN_SETTINGS, f, indent=1)
    for d in ("Saves", "SaveStates", "RecentGames"):   # every game boots clean (no battery save from an earlier run)
        shutil.rmtree(os.path.join(home, d), ignore_errors=True)
    os.makedirs(out_dir, exist_ok=True)
    env = dict(os.environ, BNT1_INPUT=script or "", BNT1_SHOTS=",".join(str(max(0, f - REF_LAG)) for f in shots), BNT1_WIN=str(WIN), BNT1_OUT=out_dir)
    secs_limit = max(60, frames // 300 + 60)
    cmd = [exe, rom, "--luaScriptsToLoad", os.path.join(HERE, "mesen_t1.lua"), "--testRunner", f"--timeout={secs_limit}"]
    with Slot():
        code, o, e, secs, to = run(cmd, timeout=secs_limit + 30, env=env, cwd=home)
    pics = {}
    for n in os.listdir(out_dir):
        if n.startswith("m_") and n.endswith(".raw"):
            pics[int(n[2:-4])] = os.path.join(out_dir, n)
    return dict(code=code, secs=round(secs, 1), timeout=to, pics=pics)


def best_match(img, ref_pics, f):
    best = None
    for k in range(f - REF_LAG - WIN, f - REF_LAG + WIN + 1):
        p = ref_pics.get(k)
        if p is None:
            continue
        s = picdiff(img, load_raw(p))
        if best is None or s < best[0]:
            best = (s, k)
    return best


def find_title(pngs):
    """The title: the first non-blank picture that then holds still (or nearly) for two samples. Fallback: the first
    non-blank picture + 120 frames. None when the picture stays blank."""
    fr = sorted(pngs)
    imgs = {f: load_png(pngs[f]) for f in fr}
    nb = [f for f in fr if not blank(imgs[f])]
    if not nb:
        return None, "blank"
    for i, f in enumerate(fr):
        if blank(imgs[f]) or i + 2 >= len(fr):
            continue
        if picdiff(imgs[f], imgs[fr[i + 1]]) <= 0.03 and picdiff(imgs[f], imgs[fr[i + 2]]) <= 0.03:
            return f, "stable"
    return min(nb[0] + 120, fr[-1]), "first-picture"


def check_nes(rec, minutes, keep_dir):
    rom = rec.get("path") or rec["file"]["path"]
    sha = rec["sha1"]
    work = os.path.join(SCRATCH, sha)
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work)
    res = dict(version=T1_VERSION, at=catalog.now())
    # pass 1: boot with no input, a picture every 30 frames -> where the title settles
    p1 = nes_romtest(rom, PROBE_FRAMES, range(PROBE_STEP, PROBE_FRAMES + 1, PROBE_STEP), os.path.join(work, "probe"))
    res["probe"] = dict(exit=p1["code"], secs=p1["secs"], timeout=p1["timeout"])
    rep = p1["report"] or {}
    mapper = (rec.get("dump") or {}).get("mapper")
    res["mapper"] = mapper
    errtxt = (p1["err"] + p1["out"] + json.dumps(rep))[-4000:]
    if p1["timeout"]:
        return grade_result(res, "Broken", ["crash", "timing"], "the boot hung (no frames for minutes)")
    if p1["code"] == 3:
        # --romtest refuses the core request (exit 3) before it says why the cart did not load: probe once on the default cores
        with Slot():
            c3, o3, e3, _, _ = run([WORKSHOP, "--romtest", "--rom", rom, "--frames", "1", "--json", "--out", os.path.join(work, "why.json")], 120)
        errtxt += o3 + e3
        try:
            with open(os.path.join(work, "why.json"), encoding="utf-8-sig") as f:
                errtxt += json.dumps(json.load(f))
        except Exception:
            pass
    if isinstance(rep, dict) and rep.get("CrashInfo"):
        return grade_result(res, "Broken", ["crash"], f"CPU crash during boot (frame {rep.get('CrashFrame')}): {str(rep['CrashInfo'])[:200]}")
    if p1["code"] not in (0, 1) or not p1["pngs"]:
        res["probe"]["error"] = errtxt[-600:]
        low = errtxt.lower()
        if "mapper" in low and ("not supported" in low or "unsupported" in low):
            return grade_result(res, "Nothing", ["mapper"], f"unsupported board (mapper {mapper})")
        crash = rep.get("CrashInfo") if isinstance(rep, dict) else None
        return grade_result(res, "Broken", ["crash"], f"romtest exit {p1['code']}: {(crash or p1['err'] or '').strip()[:200]}")
    title, how = find_title(p1["pngs"])
    res["title"] = dict(frame=title, how=how)
    start = (title if title is not None else 600) + 30
    script, end = generic_script(start, minutes)
    cps = sorted(set([max(1, start - 30), start + 120, start + 300, start + 470] + list(range(start + 480 + 1200, end, 1200)) + [end]))
    res["frames"] = end
    res["input"] = "generic-v1 start@%d" % start
    # pass 2: the script on BrokenNes (movie + checkpoint pictures), then the same script on the reference
    os.makedirs(keep_dir, exist_ok=True)
    bnm = os.path.join(keep_dir, "t1.bnm")
    p2 = nes_record(rom, end + 1, script, bnm, cps, os.path.join(work, "bn"))
    res["bn"] = dict(exit=p2["code"], secs=p2["secs"], timeout=p2["timeout"])
    res["tas"] = bnm if p2["ok"] else None
    if not p2["ok"]:
        res["bn"]["error"] = (p2["err"] + p2["out"]).strip()[-400:]
    m = mesen_run(rom, script, cps, os.path.join(work, "ref"), end)
    res["ref"] = dict(exit=m["code"], secs=m["secs"], timeout=m["timeout"], pictures=len(m["pics"]))
    # checkpoint screenshots (BrokenNes + reference, as PNG) and scores
    cmp = []
    for f in cps:
        bp = p2["pngs"].get(f)
        bimg = load_png(bp) if bp else None
        row = dict(frame=f, bn=None, ref=None, score=None, refFrame=None, bnBlank=None, refBlank=None)
        if bimg is not None:
            shutil.copy2(bp, os.path.join(keep_dir, f"bn_f{f:05d}.png"))
            row["bn"] = f"bn_f{f:05d}.png"
            row["bnBlank"] = blank(bimg)
            row["bnHash"] = hash(bimg.tobytes())
        bm = best_match(bimg, m["pics"], f) if bimg is not None else None
        rk = bm[1] if bm else (f if f in m["pics"] else None)
        if rk is not None and rk in m["pics"]:
            rimg = load_raw(m["pics"][rk])
            Image.fromarray(rimg.astype(np.uint8)).save(os.path.join(keep_dir, f"ref_f{f:05d}.png"))
            row["ref"] = f"ref_f{f:05d}.png"
            row["refBlank"] = blank(rimg)
        if bm:
            row["score"], row["refFrame"] = bm
        cmp.append(row)
    res["checkpoints"] = cmp
    out = grade_compare(res, p2, cmp, title)
    for r in cmp:
        r.pop("bnHash", None)
    shutil.rmtree(work, ignore_errors=True)
    return out


def grade_result(res, grade, tags, why):
    res.update(grade=grade, tags=tags, why=why)
    return res


def grade_compare(res, p2, cmp, title):
    if p2["timeout"]:
        return grade_result(res, "Broken", ["crash", "timing"], "hung during the input script")
    if not p2["ok"]:
        crash = res["bn"].get("error", "")[:240]
        return grade_result(res, "Broken", ["crash"], f"crashed during the input script (exit {p2['code']}): {crash}")
    have_ref = [r for r in cmp if r["score"] is not None]
    bn_rows = [r for r in cmp if r["bn"]]
    all_blank = all(r["bnBlank"] for r in bn_rows)
    ref_blank = all(r["refBlank"] for r in cmp if r["ref"]) if any(r["ref"] for r in cmp) else None
    if not have_ref:
        # no reference pictures: grade on BrokenNes alone, never OK
        if all_blank:
            return grade_result(res, "Nothing", ["picture"], "blank picture at every checkpoint (no reference run)")
        return grade_result(res, "Not tested", [], f"no reference pictures (Mesen exit {res['ref']['exit']}); BrokenNes boots")
    if all_blank:
        if ref_blank:
            return grade_result(res, "Not tested", [], "blank on BrokenNes AND the reference: the generic script does not reach a picture")
        return grade_result(res, "Nothing", ["picture"], "blank picture at every checkpoint; the reference shows a picture")
    scores = [r["score"] for r in have_ref]
    title_row = have_ref[0]
    worst = max(scores)
    first_bad = next((r for r in have_ref if r["score"] > 0.12), None)
    first_off = next((r for r in have_ref if r["score"] > 0.03), None)
    distinct_bn = len({r["bnHash"] for r in bn_rows})
    res["summary"] = dict(titleScore=title_row["score"], worst=worst, mean=round(sum(scores) / len(scores), 4),
                          same=sum(s <= 0.03 for s in scores), close=sum(0.03 < s <= 0.12 for s in scores),
                          different=sum(s > 0.12 for s in scores), n=len(scores), bnDistinct=distinct_bn)
    if worst <= 0.03:
        return grade_result(res, "OK", [], f"matches the reference at all {len(scores)} checkpoints (worst {worst})")
    if distinct_bn <= 1:
        return grade_result(res, "Broken", ["crash"], "the BrokenNes picture freezes; the reference keeps going")
    if title_row["score"] > 0.12:
        tag = "picture" if any(not r["bnBlank"] for r in bn_rows) else "crash"
        return grade_result(res, "Broken", [tag, "timing"] if tag == "picture" else [tag],
                            f"title screen differs from the reference (score {title_row['score']} at frame {title_row['frame']})")
    if first_bad is None:
        return grade_result(res, "Jank", ["picture"],
                            f"close to the reference but not the same from frame {first_off['frame']} (worst {worst}): palette/scroll/sprite detail")
    # the title matches and later play diverges: timing / RNG / input reaction
    early = first_bad is have_ref[1] if len(have_ref) > 1 else False
    tags = ["timing"] + (["input"] if early else [])
    return grade_result(res, "Jank", tags,
                        f"same title, then diverges from the reference at frame {first_bad['frame']} (score {first_bad['score']}; "
                        f"{res['summary']['different']}/{len(scores)} checkpoints different)")


# ------------------------------------------------------------------------------------------------ catalog
def write_record(console, rec, res, keep_dir, version):
    changes = {
        "grade": res["grade"], "tier": max(1, int(rec.get("tier") or 0)) if res["grade"] != "Not tested" else max(1, int(rec.get("tier") or 0)),
        "tags": sorted(set(res["tags"])), "screenshots": keep_dir, "core": CORES, "brokennesVersion": version,
        "extra": {"t1": res},
    }
    if res.get("tas"):
        changes["tas"] = res["tas"]
    s = res.get("summary")
    if s:
        changes["reference"] = {"emulator": REF_NAME, "frames": res.get("frames"), "diff": s["worst"], "mean": s["mean"],
                                "verdict": "match" if s["worst"] <= 0.03 else "close" if s["worst"] <= 0.12 else "differs",
                                "checkpoints": s["n"]}
    catalog.update(console, rec["sha1"], changes, append_notes=f"T1 {res['grade']}: {res['why']}")


def git_version():
    try:
        return subprocess.run(["git", "-C", HERE, "rev-parse", "--short", "HEAD"], capture_output=True, text=True).stdout.strip()
    except Exception:
        return ""


def keep_dir_for(console, sha):
    return os.path.join(catalog.root(), "screenshots", console, sha, "t1")


def cmd_run(a):
    recs = sorted(catalog.iter_records(a.console), key=lambda r: r["title"].lower())
    if a.only:
        want = set(a.only.split(","))
        recs = [r for r in recs if r["sha1"] in want or r["title"] in want]
    version = git_version()
    log = os.path.join(SCRATCH, f"t1-{a.console}.jsonl")
    os.makedirs(SCRATCH, exist_ok=True)
    n = 0
    for rec in recs:
        if a.limit and n >= a.limit:
            break
        if rec.get("revisionOf"):
            continue
        if not a.force and ((rec.get("extra") or {}).get("t1") or {}).get("version") == T1_VERSION:
            continue
        n += 1
        t = time.time()
        kd = keep_dir_for(a.console, rec["sha1"])
        try:
            with Slot():
                res = check_nes(rec, a.minutes, kd)
        except Exception as e:  # a driver bug must not stop the sweep; recorded, not graded
            res = dict(version=T1_VERSION + "-error", grade="Not tested", tags=[], why=f"T1 driver error: {type(e).__name__}: {e}")
        res["seconds"] = round(time.time() - t, 1)
        res["slotWait"] = round(Slot.waited, 1); Slot.waited = 0.0
        try:
            write_record(a.console, rec, res, kd, version)
        except Exception as e:
            print(f"  catalog write failed: {e}", flush=True)
        with open(log, "a", encoding="utf-8") as f:
            f.write(json.dumps({"sha1": rec["sha1"], "title": rec["title"], "grade": res["grade"], "tags": res["tags"],
                                "why": res["why"], "mapper": res.get("mapper"), "seconds": res["seconds"]}) + "\n")
        print(f"[{n}] {rec['title']}: {res['grade']} {res['tags']} - {res['why']} ({res['seconds']}s)", flush=True)


def cmd_one(a):
    rec = catalog.get(a.console, a.sha1)
    kd = keep_dir_for(a.console, a.sha1) if a.write else os.path.join(SCRATCH, "_one", a.sha1)
    t = time.time()
    res = check_nes(rec, a.minutes, kd)
    res["seconds"] = round(time.time() - t, 1); res["slotWait"] = round(Slot.waited, 1)
    print(json.dumps(res, indent=1, default=str))
    if a.write:
        write_record(a.console, rec, res, kd, git_version())


def cmd_report(a):
    recs = list(catalog.iter_records(a.console))
    t1 = [r for r in recs if ((r.get("extra") or {}).get("t1") or {}).get("version", "").startswith("t1-")]
    print(f"{len(t1)} / {len(recs)} records have a T1 result")
    print("grades:", dict(Counter(r["grade"] for r in t1)))
    by_mapper = defaultdict(Counter)
    sym = Counter()
    for r in t1:
        x = r["extra"]["t1"]
        m = x.get("mapper") if x.get("mapper") is not None else (r.get("dump") or {}).get("mapper")
        by_mapper[m][r["grade"]] += 1
        key = r["grade"] + " " + "+".join(sorted(x.get("tags") or []))
        why = x.get("why", "")
        sym[(key, why.split(":")[0].split("(")[0].strip()[:60])] += 1
    print("\nby mapper (non-OK):")
    for m, c in sorted(by_mapper.items(), key=lambda kv: -sum(v for g, v in kv[1].items() if g != "OK")):
        bad = sum(v for g, v in c.items() if g != "OK")
        if bad:
            print(f"  mapper {m}: {dict(c)}")
    print("\nsymptom clusters:")
    for (k, w), c in sym.most_common(25):
        print(f"  {c:4d}  {k:28s} {w}")


def main():
    p = argparse.ArgumentParser()
    sp = p.add_subparsers(dest="cmd", required=True)
    for name in ("run", "one", "report"):
        q = sp.add_parser(name)
        q.add_argument("--console", default="nes")
        q.add_argument("--minutes", type=float, default=1.0)
        if name == "run":
            q.add_argument("--limit", type=int, default=0)
            q.add_argument("--only", default="")
            q.add_argument("--force", action="store_true")
        if name == "one":
            q.add_argument("--sha1", required=True)
            q.add_argument("--write", action="store_true")
    a = p.parse_args()
    if a.console != "nes" and a.cmd != "report":
        sys.exit("only the NES is wired up in T1 so far")
    {"run": cmd_run, "one": cmd_one, "report": cmd_report}[a.cmd](a)


if __name__ == "__main__":
    main()
