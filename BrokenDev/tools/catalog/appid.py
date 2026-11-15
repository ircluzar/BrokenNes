#!/usr/bin/env python3
"""BrokenNes's own game ids, ported 1:1 from the app, and a per-ROM check of the catalog against them.

What the app computes (read from the code, 2026-11-15):
  NES      NES.ComputeGameIdentity (Windows/NesEmulator/board/NES.cs) over Cartridge (board/Cartridge.cs):
           "nes_" + sha1(PRG || CHR). PRG = byte 4 x 16 KiB, CHR = byte 5 x 8 KiB (the NES 2.0 size MSBs in byte 9
           are NOT read), after the 16-byte header and a 512-byte trainer when flag 6 bit 2 is set. A file shorter
           than the header says fails to load (Array.Copy throws), so it has no id. The web ROM manager
           (Webmodules/RomManager/rommanager.js computeGameId) computes the same, but clamps a short file instead.
  SNES     SnesSession: GameId = "sfc" + sha1(whole file, copier header included)
  WideMess SnesSession(wideMess): "wms" + sha1(whole .wms file); a plain SNES image keeps "sfc"
  GB/GBC   GbSession: sha1(whole file)
  SMS/GG   SmsSession: sha1(whole file)
  Genesis  MdSession: sha1(whole file as loaded; an interleaved .smd is hashed before it is normalised)
The game db (default-db.json, continueDb) only ever creates ids for NES ROMs (rommanager.js gives a non-iNES file
"unknown_<time>"), so a non-NES db id is ours to define: "<system>_" + the session's sha1 (without its sfc/wms
prefix). That keeps one rule: db id = "<system>_" + the hash the app names the game by.

  appid.py check [--console snes ...] [--limit N] [--json out.json]    re-read every record's file and compare
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import zipfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import catalog as cat  # noqa: E402


def nes_app_hash(data: bytes) -> str | None:
    """sha1(PRG||CHR) exactly as Cartridge + ComputeGameIdentity; None when the app refuses the file."""
    if len(data) < 16 or data[:4] != b"NES\x1a":
        return None
    prg, chr_ = data[4] * 16384, data[5] * 8192
    off = 16
    if data[6] & 0x04:
        if len(data) < off + 512:
            return None
        off += 512
    if off + prg > len(data) or (chr_ and off + prg + chr_ > len(data)):
        return None  # Array.Copy past the end: the desktop app cannot load it
    return hashlib.sha1(data[off:off + prg] + data[off + prg:off + prg + chr_]).hexdigest()


def session_hash(console: str, data: bytes) -> str | None:
    """The hash part of the app's id for the console (None: the app cannot name it)."""
    if console == "nes":
        return nes_app_hash(data)
    return hashlib.sha1(data).hexdigest()


def session_game_id(console: str, data: bytes) -> str | None:
    h = session_hash(console, data)
    if h is None:
        return None
    if console == "nes":
        return "nes_" + h
    if console == "snes":
        return "sfc" + h
    return h  # wms files: "wms" + h (WideMess has no library)


def db_id(console: str, sha1: str) -> str:
    return f"{cat.DB_SYSTEM[console]}_{sha1}".lower()


def read_file(f: dict) -> bytes:
    if f.get("member"):
        if f["path"].lower().endswith(".7z"):
            import titles  # 7-Zip helper
            return titles.read_7z(f["path"], f["member"])
        with zipfile.ZipFile(f["path"]) as z:
            return z.read(f["member"])
    with open(f["path"], "rb") as fh:
        return fh.read()


def check(consoles=None, limit=0, base=None):
    res = {"checked": 0, "ok": 0, "mismatch": [], "unloadable": [], "unreadable": [], "dbIdDiffers": []}
    for r in cat.iter_records(None, base):
        if consoles and r["console"] not in consoles:
            continue
        if limit and res["checked"] >= limit:
            break
        res["checked"] += 1
        try:
            data = read_file(r.get("file") or {})
        except Exception as e:  # noqa: BLE001
            res["unreadable"].append({"console": r["console"], "sha1": r["sha1"], "error": str(e)})
            continue
        h = session_hash(r["console"], data)
        ent = {"console": r["console"], "sha1": r["sha1"], "title": r["title"], "app": h}
        if h is None:
            res["unloadable"].append(ent)
            continue
        if h != r["sha1"]:
            res["mismatch"].append(ent)
            continue
        if r.get("gameDbId") and r["gameDbId"].lower() != db_id(r["console"], h):
            res["dbIdDiffers"].append(ent | {"gameDbId": r["gameDbId"]})
            continue
        res["ok"] += 1
    return res


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("check")
    p.add_argument("--console", nargs="*", choices=cat.CONSOLES)
    p.add_argument("--limit", type=int, default=0)
    p.add_argument("--json")
    p.add_argument("--root")
    a = ap.parse_args(argv)
    res = check(a.console, a.limit, a.root)
    if a.json:
        cat._atomic_write(a.json, res)
    print(json.dumps({k: (v if isinstance(v, int) else len(v)) for k, v in res.items()}))
    for k in ("mismatch", "unloadable", "dbIdDiffers"):
        for e in res[k][:20]:
            print(f"  {k}: {e}")
    return 1 if res["mismatch"] or res["dbIdDiffers"] else 0


if __name__ == "__main__":
    sys.exit(main())
