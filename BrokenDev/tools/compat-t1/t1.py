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
import socket
import subprocess
import sys
import time
from collections import Counter, defaultdict

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "catalog"))
import catalog  # noqa: E402

T1_VERSION = "t1-2"
HEAVY = os.environ.get("BNT1_HEAVY", r"C:\BrokenNes-work\heavy")
SLOTS = int(os.environ.get("BNT1_SLOTS", "4"))
WORKSHOP = os.environ.get("BNT1_WORKSHOP", r"C:\BrokenNes-work\cc_t1_bin\BrokenNes.Workshop.exe")
MESEN = os.environ.get("BNT1_MESEN", r"T:\BrokenDev\env\emulators\mesen-2.1.1\Mesen.exe")
SCRATCH = os.environ.get("BNT1_SCRATCH", r"C:\BrokenNes-work\cc_t1_run\work")
LANE = os.environ.get("BNT1_LANE", "cc-t1")
CORES = "CPU_FIX/PPU_FIX/APU_FIX"
REF_NAME = "Mesen 2.1.1"   # (md: BizHawk 2.11.1 Genplus-gx)
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
            for i in range(1, SLOTS + 1):
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


def load_ref(path):
    return load_png(path) if path.endswith(".png") else load_raw(path)


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


RANK_CONSOLES = ("gg", "gbc", "gb")   # rank compare only where Mesen's colours differ (SMS/SNES/MD: raw brightness is stricter and right)
RANK = False   # other consoles: compare brightness RANKS (Mesen's Game Gear / GBC colour correction makes the same picture darker)


def ranks(l):
    u, inv = np.unique(l, return_inverse=True)
    return (inv.reshape(l.shape) * (255 // max(1, len(u) - 1))).astype(np.int32) if len(u) > 1 else np.zeros_like(l)


def picdiff(a, b):
    """BrokenDev/mcp/Reference.cs PicDiff.Grid, ported: palette-blind (brightness cells + edge overlap). 0 same, <=0.02 same up
    to palette, <=0.12 close, else different."""
    h, w = min(a.shape[0], b.shape[0]), min(a.shape[1], b.shape[1])
    la, lb = luma(a, w, h), luma(b, w, h)
    if RANK:
        la, lb = ranks(la), ranks(lb)
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


def mesen_run(rom, script, shots, out_dir, frames, console="nes"):
    home = os.path.join(SCRATCH, "_mesen_home")
    os.makedirs(home, exist_ok=True)
    exe = os.path.join(home, "Mesen.exe")
    if not os.path.exists(exe) or os.path.getsize(exe) != os.path.getsize(MESEN):
        shutil.copy2(MESEN, exe)
    with open(os.path.join(home, "settings.json"), "w") as f:
        json.dump(dict(MESEN_SETTINGS, **MESEN_EXTRA.get(console, {})), f, indent=1)
    for d in ("Saves", "SaveStates", "RecentGames"):   # every game boots clean (no battery save from an earlier run)
        shutil.rmtree(os.path.join(home, d), ignore_errors=True)
    os.makedirs(out_dir, exist_ok=True)
    env = dict(os.environ, BNT1_MAP=MESEN_MAP.get(console, ""), BNT1_INPUT=script or "", BNT1_SHOTS=",".join(str(max(0, f - REF_LAG)) for f in shots), BNT1_WIN=str(WIN), BNT1_OUT=out_dir)
    secs_limit = frames // 20 + 60   # a loaded machine can drop Mesen's test runner to ~60 fps
    cmd = [exe, rom, "--luaScriptsToLoad", os.path.join(HERE, "mesen_t1.lua"), "--testRunner", f"--timeout={secs_limit}"]
    with Slot():
        code, o, e, secs, to = run(cmd, timeout=secs_limit + 30, env=env, cwd=home)
    pics = {}
    for n in os.listdir(out_dir):
        if n.startswith("m_") and n.endswith(".raw"):
            pics[int(n[2:-4])] = os.path.join(out_dir, n)
    return dict(code=code, secs=round(secs, 1), timeout=to, pics=pics)


def fit_ref(ref, img):
    """Mesen's picture can be bigger than BrokenNes's for the same frame (Game Gear: the SMS-sized 256x192 buffer with the
    160x144 screen in its centre): crop the reference's centre to BrokenNes's size."""
    if img is None or ref.shape[:2] == img.shape[:2] or ref.shape[0] < img.shape[0] or ref.shape[1] < img.shape[1]:
        return ref
    y = (ref.shape[0] - img.shape[0]) // 2
    x = (ref.shape[1] - img.shape[1]) // 2
    return ref[y:y + img.shape[0], x:x + img.shape[1]]


BIZHAWK = os.environ.get("BNT1_BIZHAWK", r"T:\BrokenDev\env\emulatorsizhawk-2.11.1\EmuHawk.exe")
BIZHAWK_CFG = os.environ.get("BNT1_BIZHAWK_CFG", os.path.join(HERE, "bizhawk_config_md.json"))   # its paths say C:\BHSCRATCH


def bizhawk_run(rom, script, shots, out_dir, frames):
    home = os.path.join(SCRATCH, "_bizhawk_home")
    shutil.rmtree(os.path.join(home, "bizhawk", "SaveRAM"), ignore_errors=True)
    os.makedirs(home, exist_ok=True)
    cfg = open(BIZHAWK_CFG, encoding="utf-8").read().replace(json.dumps("C:" + chr(92) + "BHSCRATCH")[1:-1], json.dumps(home)[1:-1])
    with open(os.path.join(home, "config.ini"), "w", encoding="utf-8") as f:
        f.write(cfg)
    os.makedirs(out_dir, exist_ok=True)
    env = dict(os.environ, BNT1_INPUT=script or "", BNT1_SHOTS=",".join(str(max(0, f - REF_LAG)) for f in shots), BNT1_WIN=str(WIN), BNT1_OUT=out_dir)
    secs_limit = frames // 15 + 90
    cmd = [BIZHAWK, "--config=" + os.path.join(home, "config.ini"), "--chromeless", "--lua=" + os.path.join(HERE, "bizhawk_t1.lua"), rom]
    with Slot():
        code, o, e, secs, to = run(cmd, timeout=secs_limit, env=env, cwd=os.path.dirname(BIZHAWK))
    pics = {}
    for n in os.listdir(out_dir):
        if n.startswith("m_") and n.endswith(".png"):
            pics[int(n[2:-4])] = os.path.join(out_dir, n)
    if os.path.exists(os.path.join(out_dir, "error.txt")):
        e = open(os.path.join(out_dir, "error.txt")).read()
    return dict(code=code, secs=round(secs, 1), timeout=to, pics=pics, err=(e or "")[-300:])


def best_match(img, ref_pics, f):
    best = None
    for k in range(f - REF_LAG - WIN, f - REF_LAG + WIN + 1):
        p = ref_pics.get(k)
        if p is None:
            continue
        s = picdiff(img, fit_ref(load_ref(p), img))
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
    res["start"] = start
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
    cmp = compare_rows(cps, p2["pngs"], m["pics"], keep_dir)
    res["checkpoints"] = cmp
    out = grade_compare(res, p2, cmp, title)
    for r in cmp:
        r.pop("bnHash", None)
    shutil.rmtree(work, ignore_errors=True)
    return out


# ------------------------------------------------------------------------------------------------ other consoles (--console-run)
MESEN_CONSOLES = ("snes", "gb", "gbc", "sms", "gg")
# BrokenNes SMS/GG pads: A=button 1, B=button 2, Start=the Game Gear Start, Select=the SMS Pause; Mesen names them one/two/pause
MESEN_MAP = {"sms": "a=one,b=two,select=pause,start=", "gg": "a=one,b=two,start=pause,select="}   # Mesen 2.1.1 plays these; md needs another reference (not wired)
MESEN_EXTRA = {
    "snes": {"Snes": {"Port1": {"Type": "SnesController"}, "RamPowerOnState": "AllZeros"}},
    "gb": {},   # (a "Gameboy" block with Model Gameboy/Auto makes Mesen 2.1.1 exit -1 at once; its default plays .gb)
    "gbc": {"Gameboy": {"Model": "GameboyColor", "RamPowerOnState": "AllZeros"}},
    "sms": {"Sms": {"Port1": {"Type": "SmsController"}, "RamPowerOnState": "AllZeros"}},
    "gg": {"Sms": {"RamPowerOnState": "AllZeros"}},
}


def con_run(rom, console, frames, png_at, out_dir, script=None):
    os.makedirs(out_dir, exist_ok=True)
    cmd = [WORKSHOP, "--console-run", "--rom", rom, "--console", console, "--frames", str(frames),
           "--png-at", ",".join(map(str, sorted(set(png_at)))), "--out", out_dir, "--tag", "t"]
    if script:
        cmd += ["--input", script]
    with Slot():
        code, out, err, secs, to = run(cmd, timeout=max(180, frames // 20))
    pngs = {}
    for n in os.listdir(out_dir):
        if n.startswith("t_") and n.endswith(".png") and "_f" in n:
            try:
                pngs[int(n.rsplit("_f", 1)[1][:-4])] = os.path.join(out_dir, n)
            except ValueError:
                pass
    return dict(code=code, out=out, err=err, secs=round(secs, 1), timeout=to, pngs=pngs)


def check_console(rec, console, minutes, keep_dir):
    global REF_LAG, WIN, RANK
    REF_LAG, WIN, RANK = 4, 6, console in RANK_CONSOLES   # SNES pictures sit ~6 frames lower on Mesen's counter, Game Boy ~3: search f-10..f+2
    rom = rec.get("path") or rec["file"]["path"]
    work = os.path.join(SCRATCH, rec["sha1"])
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work)
    res = dict(version=T1_VERSION, at=catalog.now(), console=console)
    p1 = con_run(rom, console, PROBE_FRAMES, range(PROBE_STEP, PROBE_FRAMES + 1, PROBE_STEP), os.path.join(work, "probe"))
    res["probe"] = dict(exit=p1["code"], secs=p1["secs"], timeout=p1["timeout"])
    if p1["timeout"]:
        return grade_result(res, "Broken", ["crash", "timing"], "the boot hung (no frames for minutes)")
    if p1["code"] != 0 or not p1["pngs"]:
        txt = (p1["err"] + p1["out"]).strip()
        res["probe"]["error"] = txt[-600:]
        low = txt.lower()
        if "unknown rom" in low or "not supported" in low or "unsupported" in low:
            return grade_result(res, "Nothing", ["mapper"], f"BrokenNes does not load this cart: {txt.splitlines()[-1][:200] if txt else ''}")
        return grade_result(res, "Broken", ["crash"], f"console-run exit {p1['code']}: {txt[-200:]}")
    title, how = find_title(p1["pngs"])
    res["title"] = dict(frame=title, how=how)
    start = (title if title is not None else 600) + 30
    script, end = generic_script(start, minutes)
    cps = sorted(set([max(1, start - 30), start + 120, start + 300, start + 470] + list(range(start + 480 + 1200, end, 1200)) + [end]))
    res.update(frames=end, start=start, input="generic-v1 start@%d" % start, tas=None)
    os.makedirs(keep_dir, exist_ok=True)
    p2 = con_run(rom, console, end + 1, cps, os.path.join(work, "bn"), script)
    p2["ok"] = p2["code"] == 0 and not p2["timeout"]
    res["bn"] = dict(exit=p2["code"], secs=p2["secs"], timeout=p2["timeout"])
    if not p2["ok"]:
        res["bn"]["error"] = (p2["err"] + p2["out"]).strip()[-400:]
    if console in MESEN_CONSOLES:
        m = mesen_run(rom, script, cps, os.path.join(work, "ref"), end, console)
    elif console == "md":
        m = bizhawk_run(rom, script, cps, os.path.join(work, "ref"), end)
    else:
        m = dict(code=None, secs=0, timeout=False, pics={})
    res["ref"] = dict(exit=m["code"], secs=m["secs"], timeout=m["timeout"], pictures=len(m["pics"]))
    cmp = compare_rows(cps, p2["pngs"], m["pics"], keep_dir)
    res["checkpoints"] = cmp
    out = grade_compare(res, p2, cmp, title)
    for r in cmp:
        r.pop("bnHash", None)
    shutil.rmtree(work, ignore_errors=True)
    return out


def compare_rows(cps, bn_pngs, ref_pics, keep_dir):
    cmp = []
    for f in cps:
        bp = bn_pngs.get(f)
        bimg = load_png(bp) if bp else None
        row = dict(frame=f, bn=None, ref=None, score=None, refFrame=None, bnBlank=None, refBlank=None)
        if bimg is not None:
            shutil.copy2(bp, os.path.join(keep_dir, f"bn_f{f:05d}.png"))
            row["bn"] = f"bn_f{f:05d}.png"
            row["bnBlank"] = blank(bimg)
            row["bnHash"] = hash(bimg.tobytes())
        bm = best_match(bimg, ref_pics, f) if bimg is not None else None
        rk = bm[1] if bm else (f if f in ref_pics else None)
        if rk is not None and rk in ref_pics:
            rimg = fit_ref(load_ref(ref_pics[rk]), bimg)
            Image.fromarray(rimg.astype(np.uint8)).save(os.path.join(keep_dir, f"ref_f{f:05d}.png"))
            row["ref"] = f"ref_f{f:05d}.png"
            row["refBlank"] = blank(rimg)
        if bm:
            row["score"], row["refFrame"] = bm
        cmp.append(row)
    return cmp


def check_any(rec, console, minutes, keep_dir):
    res = check_nes(rec, minutes, keep_dir) if console == "nes" else check_console(rec, console, minutes, keep_dir)
    res["minutes"] = minutes
    return res


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
    # boot, title and the first ~8 s after Start (two Start presses + an A) are where the two emulators must agree; after that the
    # generic script plays blind and the two runs can part on any lag frame / sub-frame poll difference (1942, 720...), so a later
    # divergence is recorded (extra.t1.playDiverges) but is not by itself a defect
    early = [r for r in have_ref if r["frame"] <= res["start"] + 470]
    late = [r for r in have_ref if r["frame"] > res["start"] + 470]
    e_bad = next((r for r in early if r["score"] > 0.12), None)
    e_off = next((r for r in early if r["score"] > 0.03), None)
    if e_bad:
        return grade_result(res, "Jank", ["timing", "input"],
                            f"same title, then differs right after Start at frame {e_bad['frame']} (score {e_bad['score']}): menu/intro timing or input")
    if e_off:
        return grade_result(res, "Jank", ["picture"],
                            f"title/menu close to the reference but not the same at frame {e_off['frame']} (score {e_off['score']}): palette/scroll/sprite detail")
    l_off = next((r for r in late if r["score"] > 0.03), None)
    res["playDiverges"] = l_off["frame"] if l_off else None
    gone = [r for r in late if r["bnBlank"] and r["refBlank"] is False]
    if len(gone) >= 2 and gone[-1] is late[-1]:
        return grade_result(res, "Jank", ["picture"],
                            f"boot and title match; in play the picture goes blank from frame {gone[0]['frame']} while the reference shows one")
    if l_off and l_off["score"] <= 0.12 and all(r["score"] <= 0.12 for r in late):
        return grade_result(res, "OK", [], f"boot, title and menus match; play close to the reference (worst {worst}, from frame {l_off['frame']})")
    return grade_result(res, "OK", [], f"boot, title and menus match the reference; blind play parts from it at frame {l_off['frame']} (not frame-locked)")


# ------------------------------------------------------------------------------------------------ catalog
def write_record(console, rec, res, keep_dir, version):
    changes = {
        "grade": res["grade"], "tier": max(1, int(rec.get("tier") or 0)) if res["grade"] != "Not tested" else max(1, int(rec.get("tier") or 0)),
        "tags": sorted(set(res["tags"])), "screenshots": keep_dir, "core": CORES if console == "nes" else "default", "brokennesVersion": version,
        "extra": {"t1": res},
    }
    if res.get("tas"):
        changes["tas"] = res["tas"]
    s = res.get("summary")
    if s:
        changes["reference"] = {"emulator": "BizHawk 2.11.1 Genplus-gx" if console == "md" else REF_NAME, "frames": res.get("frames"), "diff": s["worst"], "mean": s["mean"],
                                "verdict": "match" if s["worst"] <= 0.03 else "close" if s["worst"] <= 0.12 else "differs",
                                "checkpoints": s["n"]}
    catalog.update(console, rec["sha1"], changes, append_notes=f"T1 {res['grade']}: {res['why']}")


def git_version():
    if os.environ.get("BNT1_VERSION"):
        return os.environ["BNT1_VERSION"]
    try:
        return subprocess.run(["git", "-C", HERE, "rev-parse", "--short", "HEAD"], capture_output=True, text=True).stdout.strip()
    except Exception:
        return ""


def keep_dir_for(console, sha):
    return os.path.join(catalog.root(), "screenshots", console, sha, "t1")


def cmd_run(a):
    recs = sorted(catalog.load_index(a.console), key=lambda r: r["title"].lower())
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
        rec = catalog.get(a.console, rec["sha1"]) or rec
        if not a.force and ((rec.get("extra") or {}).get("t1") or {}).get("version") == T1_VERSION:
            continue
        n += 1
        t = time.time()
        kd = keep_dir_for(a.console, rec["sha1"])
        try:
            with Slot():
                res = check_any(rec, a.console, a.minutes, kd)
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
    res = check_any(rec, a.console, a.minutes, kd)
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


# ------------------------------------------------------------------------------------------------ fleet (E:\fleet PCs)
# export (here): records + ROMs of a shard -> a folder pushed to a PC; work (PC): no catalog, results to out\<sha1>\;
# ingest (here): results -> catalog records, identical in form to `run` (a record that already has T1_VERSION is skipped).
def unpack_rom(src, dest, want_sha1=None):
    """the ROM (out of a .zip / .7z when it is one: the inner file that matches the dump's sha1, its own name kept, since Sega
    consoles read the region from it) -> dest; returns its file name"""
    import hashlib, zipfile
    os.makedirs(dest, exist_ok=True)
    src = src.split("#")[0]
    low = src.lower()
    if low.endswith(".zip") or low.endswith(".7z"):
        tmp = dest + "_x"
        shutil.rmtree(tmp, ignore_errors=True)
        if low.endswith(".zip"):
            with zipfile.ZipFile(src) as z:
                z.extractall(tmp)
        else:
            sz = os.environ.get("SEVENZIP", os.path.join(os.environ.get("ProgramFiles", "C:/Program Files"), "7-Zip", "7z.exe"))
            subprocess.run([sz, "x", "-y", "-o" + tmp, src], capture_output=True, check=True)
        files = [os.path.join(r, f) for r, _, fs in os.walk(tmp) for f in fs]
        pick = None
        if want_sha1:
            for f in files:
                if hashlib.sha1(open(f, "rb").read()).hexdigest() == want_sha1.lower():
                    pick = f
                    break
        pick = pick or max(files, key=os.path.getsize)
        name = os.path.basename(pick)
        shutil.move(pick, os.path.join(dest, name))
        shutil.rmtree(tmp, ignore_errors=True)
        return name
    name = os.path.basename(src)
    shutil.copy2(src, os.path.join(dest, name))
    return name


def cmd_export(a):
    shas = [l.strip() for l in open(a.list, encoding="utf-8") if l.strip()]
    os.makedirs(os.path.join(a.out, "roms"), exist_ok=True)
    recs = []
    for sha in shas:
        rec = catalog.get(a.console, sha)
        src = rec.get("path") or rec["file"]["path"]
        if not os.path.exists(src.split("#")[0]):
            print("missing ROM, skipped:", rec["title"], src)
            continue
        name = unpack_rom(src, os.path.join(a.out, "roms", sha), (rec.get("dump") or {}).get("fileSha1"))
        rec = dict(rec, path=os.path.join("roms", sha, name))
        rec.pop("file", None)
        recs.append(rec)
    with open(os.path.join(a.out, "shard.json"), "w", encoding="utf-8") as f:
        json.dump(dict(console=a.console, records=recs), f)
    print(f"exported {len(recs)} records to {a.out}")


def cmd_work(a):
    base = os.path.abspath(a.shard)
    sh = json.load(open(os.path.join(base, "shard.json"), encoding="utf-8"))
    out = os.path.join(base, "out")
    recs = list(enumerate(sh["records"]))
    if a.reverse:   # a second worker on the same shard from the other end (they meet in the middle; a result file is the claim)
        recs = recs[::-1]
    if a.start:     # more workers: start at this fraction of the shard (0.5 = the middle), forward, wrapping round
        k = int(len(recs) * a.start)
        recs = recs[k:] + recs[:k]
    for i, rec in recs:
        kd = os.path.join(out, rec["sha1"])
        if os.path.exists(os.path.join(kd, "result.json")):
            continue
        rec = dict(rec, path=os.path.join(base, rec["path"]))
        t = time.time()
        try:
            res = check_any(rec, sh["console"], a.minutes, kd)
        except Exception as e:
            res = dict(version=T1_VERSION + "-error", grade="Not tested", tags=[], why=f"T1 driver error: {type(e).__name__}: {e}")
        res["seconds"] = round(time.time() - t, 1)
        res["slotWait"] = round(Slot.waited, 1); Slot.waited = 0.0
        res["fleetHost"] = socket.gethostname()
        os.makedirs(kd, exist_ok=True)
        with open(os.path.join(kd, "result.json"), "w", encoding="utf-8") as f:
            json.dump(res, f, default=str)
        print(f"[{i + 1}/{len(sh['records'])}] {rec['title']}: {res['grade']} {res['tags']} - {res['why']} ({res['seconds']}s)", flush=True)


def cmd_ingest(a):
    version = git_version()
    n = skipped = 0
    for sha in sorted(os.listdir(a.results)):
        rj = os.path.join(a.results, sha, "result.json")
        if not os.path.exists(rj):
            continue
        rec = catalog.get(a.console, sha)
        if rec is None:
            print("no record", sha); continue
        if not a.force and ((rec.get("extra") or {}).get("t1") or {}).get("version") == T1_VERSION:
            skipped += 1; continue
        res = json.load(open(rj, encoding="utf-8"))
        kd = keep_dir_for(a.console, sha)
        os.makedirs(kd, exist_ok=True)
        for fn in os.listdir(os.path.join(a.results, sha)):
            if fn != "result.json":
                shutil.copy2(os.path.join(a.results, sha, fn), os.path.join(kd, fn))
        if res.get("tas"):
            res["tas"] = os.path.join(kd, "t1.bnm")
        write_record(a.console, rec, res, kd, version)
        log = os.path.join(SCRATCH, f"t1-{a.console}.jsonl")
        os.makedirs(SCRATCH, exist_ok=True)
        with open(log, "a", encoding="utf-8") as f:
            f.write(json.dumps({"sha1": sha, "title": rec["title"], "grade": res["grade"], "tags": res["tags"], "why": res["why"],
                                "mapper": res.get("mapper"), "seconds": res.get("seconds"), "host": res.get("fleetHost")}) + "\n")
        n += 1
    print(f"ingested {n}, skipped {skipped} (already T1 {T1_VERSION})")


def cmd_regrade(a):
    """re-score the kept checkpoint pictures (bn_/ref_ PNGs in the record's screenshots folder) with the current picdiff and grade again;
    no emulator runs. For records graded by an older comparison."""
    global RANK
    RANK = a.console in RANK_CONSOLES
    n = seen = 0
    # (a list first: catalog.iter_records yields inside `with open(...)`, so the record file is still open and cannot be replaced)
    for rec in list(catalog.iter_records(a.console)):
        seen += 1
        if seen % 50 == 0:
            print(f"  {seen} records, {n} changed", flush=True)
        t1 = (rec.get("extra") or {}).get("t1") or {}
        if not t1.get("version", "").startswith("t1-") or not t1.get("checkpoints"):
            continue
        kd = rec.get("screenshots") or keep_dir_for(a.console, rec["sha1"])
        cmp = t1["checkpoints"]
        for r in cmp:
            if r.get("bn") and r.get("ref") and os.path.exists(os.path.join(kd, r["bn"])) and os.path.exists(os.path.join(kd, r["ref"])):
                bimg = load_png(os.path.join(kd, r["bn"]))
                r["score"] = picdiff(bimg, load_png(os.path.join(kd, r["ref"])))
                r["bnHash"] = hash(bimg.tobytes())
            elif r.get("bn") and os.path.exists(os.path.join(kd, r["bn"])):
                r["bnHash"] = hash(load_png(os.path.join(kd, r["bn"])).tobytes())
        bn = t1.get("bn") or {}
        p2 = dict(timeout=bn.get("timeout"), ok=bn.get("exit") == 0 and not bn.get("timeout"), code=bn.get("exit"))
        old = (t1.get("grade"), t1.get("why"))
        for k in ("summary", "playDiverges"):
            t1.pop(k, None)
        res = grade_compare(t1, p2, cmp, (t1.get("title") or {}).get("frame"))
        for r in cmp:
            r.pop("bnHash", None)
        res["regraded"] = catalog.now()
        if (res["grade"], res["why"]) != old:
            for k in range(6):
                try:
                    write_record(a.console, rec, res, kd, git_version())
                    n += 1
                    break
                except PermissionError as e:
                    if k == 5:
                        print("  skipped (locked):", rec["sha1"], e)
                    time.sleep(3)
    print(f"regraded {a.console}: {n} records changed")


def main():
    p = argparse.ArgumentParser()
    sp = p.add_subparsers(dest="cmd", required=True)
    for name, args in (("export", ("list", "out")), ("work", ("shard",)), ("ingest", ("results",))):
        q = sp.add_parser(name)
        q.add_argument("--reverse", action="store_true")
        q.add_argument("--start", type=float, default=0.0)
        q.add_argument("--console", default="nes")
        q.add_argument("--minutes", type=float, default=1.0)
        q.add_argument("--force", action="store_true")
        for x in args:
            q.add_argument("--" + x, required=True)
    for name in ("run", "one", "report", "regrade"):
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
    if a.console not in ("nes", "md") + MESEN_CONSOLES and a.cmd not in ("report", "ingest", "export", "regrade"):
        sys.exit("T1 is wired up for nes, " + ", ".join(MESEN_CONSOLES) + " (md needs a reference emulator)")
    {"run": cmd_run, "one": cmd_one, "report": cmd_report, "export": cmd_export, "work": cmd_work, "ingest": cmd_ingest, "regrade": cmd_regrade}[a.cmd](a)


if __name__ == "__main__":
    main()
