#!/usr/bin/env python3
"""Build the per-console title lists of the Great Compatibility Catalog.

Scans the read-only GoodTools / No-Intro sets in X:\\EMULATION, keeps one dump per title and writes one catalog
record per kept dump (grade "Not tested"). Existing records are never overwritten (a re-run only adds).

  titles.py [--console nes gb ...] [--dry-run] [--src X:\\EMULATION] [--root T:\\BrokenDev\\Catalog]

Selection rule (compat-catalog-plan.md, decision 2):
  * skip hacks [h], translations [T+/-], bad dumps [b], overdumps [o], pirates [p], fixed [f], bad checksum [x],
    trainers [t], (Hack), (PD), betas/protos/samples/demos, 32X images, and the hack/homebrew/translation folders;
    in No-Intro style folders (GBC, SMS) a file with no region tag is a hack/colourisation and is skipped
  * per title: prefer [!], then USA/World, then Europe, then Japan, then others; then no [a]/[c] variant; then the
    highest revision
  * a second dump of the same title is kept (revisionOf = the main one) only when Windows/Data/default-db.json
    already grades it (known to differ)
Python 3.9+, standard library + 7-Zip (C:\\Program Files\\7-Zip\\7z.exe) for .7z archives.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import catalog as cat  # noqa: E402

SEVENZIP = r"C:\Program Files\7-Zip\7z.exe"
ROM_EXT = {".nes", ".smc", ".sfc", ".bs", ".gb", ".gbc", ".sms", ".gg", ".bin", ".gen", ".md"}

# console sources: (folder under src, console or None = decide from the header, naming style)
SOURCES = {
    "nes": [("Nintendo Entertainment System", "nes")],
    "snes": [(r"Super Nintendo\Super Nintendo (U)", "snes"), (r"Super Nintendo\Japan", "snes"),
             (r"Super Nintendo\Satellaview", "snes"), (r"Super Nintendo\Unreleased", "snes"),
             (r"Super Nintendo\PAL to NTSC", "snes")],
    "gb": [("GameBoy", None), ("GameBoy Color", None)],
    "gbc": [("GameBoy", None), ("GameBoy Color", None)],
    "sms": [("Sega Master System", "sms")],
    "gg": [("Sega Game Gear", "gg")],
    "md": [("Sega Genesis", "md")],
}
# folders deliberately not scanned (later lanes): reported in the summary
SKIPPED_FOLDERS = [r"Super Nintendo\Hacks", r"Super Nintendo\SMW Hacks", r"Super Nintendo\Homebrew & Unlicensed",
                   r"Super Nintendo\Translations", r"Super Nintendo\NES PORTS", r"Super Nintendo\SPC700 Music"]
# side folders scanned with their own rules (cc-gaps lane, 2026-11-15). "penalty" ranks a dump after every regular
# dump of the same title, so it is only kept when it is the title's only dump.
#   Satellaview: broadcast games, their own titles (a BS remake of a cart game is not that game): key prefix "bs:"
#   Unreleased: betas/protos allowed (an unreleased game has nothing else); a released dump of the title wins
#   PAL to NTSC: PAL-only games converted to 60 Hz, the only dump of those titles in the library
FOLDER_RULES = {
    r"Super Nintendo\Satellaview": {"key_prefix": "bs:", "extra": {
        "satellaview": True, "needs": "BS-X base cartridge + Satellaview (not emulated yet)"}},
    r"Super Nintendo\Unreleased": {"allow_beta": True, "penalty": 1, "extra": {"unreleased": True}},
    r"Super Nintendo\PAL to NTSC": {"penalty": 1, "extra": {"ntscConversion": True}},
}
CURATED = os.path.join(os.path.dirname(os.path.abspath(__file__)), "curated.json")


def load_curated(path=CURATED):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except FileNotFoundError:
        return {}

BAD_FLAG = re.compile(r"\[(b|h|o|p|f|x|t|T[+-])[^\]]*\]", re.I)
BAD_PAREN = re.compile(r"\((hack|pd|beta[^)]*|proto[^)]*|prototype|sample|demo|kiosk[^)]*|pirate|unl hack)\)", re.I)
REV = re.compile(r"\((?:prg|rev|v|rev )\s*([0-9a-z.]+)\)", re.I)
GOOD_REGION = {"u": 0, "w": 0, "ju": 0, "ue": 0, "jue": 0, "uj": 0, "4": 0, "e": 1, "j": 2}
NOINTRO_REGIONS = {"usa": 0, "world": 0, "europe": 1, "japan": 2, "brazil": 3, "korea": 3, "australia": 3,
                   "france": 3, "germany": 3, "spain": 3, "italy": 3, "asia": 3, "taiwan": 3, "china": 3,
                   "sweden": 3, "netherlands": 3, "canada": 3, "uk": 3, "hong kong": 3}


def region_of(name: str):
    """(rank, label) or (None, '') when the name carries no region tag."""
    best = None
    for p in re.findall(r"\(([^)]*)\)", name):
        pl = p.strip().lower()
        if pl in GOOD_REGION:
            r = GOOD_REGION[pl]
        elif pl in ("a", "b", "k", "f", "g", "s", "i", "c", "h", "unl", "nl", "sw"):
            r = 3
        else:
            parts = [x.strip() for x in pl.split(",")]
            if parts and all(x in NOINTRO_REGIONS for x in parts):
                r = min(NOINTRO_REGIONS[x] for x in parts)
            else:
                continue
        if best is None or r < best[0]:
            best = (r, p.strip())
    return best if best else (None, "")


def title_key(name: str) -> str:
    base = os.path.splitext(name)[0]
    base = re.sub(r"\[[^\]]*\]|\([^)]*\)", " ", base)
    return re.sub(r"\s+", " ", base).strip().lower()


def display_title(name: str) -> str:
    base = os.path.splitext(name)[0]
    base = re.sub(r"\[[^\]]*\]|\([^)]*\)", " ", base)
    return re.sub(r"\s+", " ", base).strip()


def revision(name: str) -> str:
    m = REV.search(name)
    return m.group(1).lower() if m else ""


def skip_reason(name: str, nointro_folder: bool, allow_beta: bool = False, force: bool = False):
    if force:  # curated.json include_untagged: a real game whose file carries no tags
        return None
    if BAD_FLAG.search(name):
        return "bad/hack/translation flag"
    if BAD_PAREN.search(name) and not (allow_beta and re.search(r"\((beta|proto)", name, re.I)):
        return "hack/PD/proto/beta"
    if "32x" in name.lower():
        return "32X image"
    if region_of(name)[0] is None and nointro_folder and "[!]" not in name:
        return "no region tag (hack/colourisation)"
    return None


# ---------- reading ROMs ----------

def iter_files(src_dir: str):
    """Yield (display name, path, member or '', bytes-loader)."""
    for n in sorted(os.listdir(src_dir)):
        p = os.path.join(src_dir, n)
        if not os.path.isfile(p):
            continue
        ext = os.path.splitext(n)[1].lower()
        if ext in ROM_EXT:
            yield n, p, "", (lambda p=p: open(p, "rb").read())
        elif ext == ".zip":
            try:
                with zipfile.ZipFile(p) as z:
                    for m in z.namelist():
                        if os.path.splitext(m)[1].lower() in ROM_EXT:
                            yield os.path.basename(m), p, m, (lambda p=p, m=m: zipfile.ZipFile(p).read(m))
            except zipfile.BadZipFile:
                print(f"warning: bad zip {p}", file=sys.stderr)
        elif ext == ".7z":
            for m in list_7z(p):
                if os.path.splitext(m)[1].lower() in ROM_EXT:
                    yield os.path.basename(m), p, m, (lambda p=p, m=m: read_7z(p, m))


def list_7z(p):
    out = subprocess.run([SEVENZIP, "l", "-slt", "-ba", p], capture_output=True, text=True, errors="replace").stdout
    return [ln[7:] for ln in out.splitlines() if ln.startswith("Path = ")]


def read_7z(p, m):
    r = subprocess.run([SEVENZIP, "e", "-so", p, m], capture_output=True)
    if r.returncode != 0:
        raise IOError(f"7z failed on {p}:{m}")
    return r.stdout


def identify(console_hint, data: bytes):
    """Returns (console, record sha1, dump dict). Record sha1 = BrokenNes's own game id hash."""
    d = {"fileSha1": hashlib.sha1(data).hexdigest(), "crc32": f"{zlib.crc32(data) & 0xffffffff:08x}",
         "md5": hashlib.md5(data).hexdigest(), "size": len(data)}
    console = console_hint
    key = d["fileSha1"]
    if console_hint == "nes" and data[:4] == b"NES\x1a":
        h = data[:16]
        prg, chr_ = h[4], h[5]
        if (h[7] & 0x0C) == 0x08:  # NES 2.0 size MSBs
            prg |= (h[9] & 0x0F) << 8
            chr_ |= (h[9] & 0xF0) << 4
        off = 16 + (512 if h[6] & 4 else 0)
        body = data[off:off + prg * 16384 + chr_ * 8192]
        key = hashlib.sha1(body).hexdigest()  # = NES.ComputeGameIdentity (PRG+CHR)
        d["headerSignature"] = "ines_" + hashlib.sha1(h).hexdigest()
        d["mapper"] = (h[6] >> 4) | (h[7] & 0xF0) | (((h[8] & 0x0F) << 8) if (h[7] & 0x0C) == 0x08 else 0)
        d["prgKiB"], d["chrKiB"] = prg * 16, chr_ * 8
    elif console_hint == "snes":
        d["copierHeader"] = len(data) % 1024 == 512
    elif console_hint is None:  # Game Boy: decide by the CGB flag
        flag = data[0x143] if len(data) > 0x143 else 0
        d["cgbFlag"] = f"{flag:02x}"
        d["sgb"] = len(data) > 0x146 and data[0x146] == 0x03
        console = "gbc" if flag in (0x80, 0xC0) else "gb"
    return console, key, d


def app_db_id(con: str, key: str) -> str:
    """The game-db id: "<system>_" + the hash the app names the game by (appid.py has the per-console rules)."""
    return f"{cat.DB_SYSTEM[con]}_{key}".lower()


def known_revision(known, con, name):
    """True when curated.json's known_revisions says this dump of its title is a revision worth its own record."""
    tk, nl = title_key(name), name.lower()
    for k in known:
        if k.get("console") == con and title_key(k["title"]) == tk and any(r.lower() in nl for r in k["keep"]):
            return True
    return False


# ---------- main ----------

def load_cache(path):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (FileNotFoundError, json.JSONDecodeError):
        return {}


def known_db_ids(db_path):
    try:
        with open(db_path, encoding="utf-8") as f:
            return {g["id"].lower() for g in json.load(f)["data"]["games"]}
    except FileNotFoundError:
        return set()


def score(c):
    r = c["regionRank"] if c["regionRank"] is not None else 4
    variant = 1 if re.search(r"\[(a|c)[0-9]*\]", c["name"], re.I) else 0
    rv = c["revision"]
    return (c.get("penalty", 0), 0 if c["verified"] else 1, r, variant, [-ord(x) for x in rv.ljust(6)], c["name"].lower())


def build(consoles, src, base, dry, db_path):
    cache_path = os.path.join(base, "cache", "scan.json")
    cache = load_cache(cache_path)
    dbids = known_db_ids(db_path)
    cur = load_curated()
    aliases = {con: {k.lower(): v.lower() for k, v in m.items()} for con, m in cur.get("aliases", {}).items()
               if not con.startswith("_")}
    include = {n.lower() for con, lst in cur.get("include_untagged", {}).items() if not con.startswith("_") for n in lst}
    known_revs = cur.get("known_revisions", [])
    folders = []
    for c in consoles:
        for f, hint in SOURCES[c]:
            if (f, hint) not in folders:
                folders.append((f, hint))
    cands = {c: {} for c in cat.CONSOLES}
    skipped = {}
    seen_hash = {}
    for folder, hint in folders:
        sd = os.path.join(src, folder)
        nointro = folder in ("GameBoy Color", "Sega Master System")
        rules = FOLDER_RULES.get(folder, {})
        if not os.path.isdir(sd):
            print(f"warning: missing folder {sd}", file=sys.stderr)
            continue
        n = 0
        for name, path, member, loader in iter_files(sd):
            n += 1
            why = skip_reason(name, nointro, rules.get("allow_beta", False), name.lower() in include)
            if why:
                skipped.setdefault((folder, why), 0)
                skipped[(folder, why)] += 1
                continue
            st = os.stat(path)
            ck = f"{path}|{member}|{st.st_size}|{int(st.st_mtime)}"
            ent = cache.get(ck)
            if ent is None:
                try:
                    data = loader()
                except Exception as e:  # noqa: BLE001
                    print(f"warning: unreadable {path} {member}: {e}", file=sys.stderr)
                    continue
                con, key, dump = identify(hint, data)
                ent = cache[ck] = {"console": con, "key": key, "dump": dump}
            con = ent["console"]
            if con not in consoles:
                continue
            if (con, ent["key"]) in seen_hash:  # same dump in two folders (GB/GBC) or archives
                continue
            seen_hash[(con, ent["key"])] = path
            rk, rl = region_of(name)
            c = {"name": name, "path": path, "member": member, "console": con, "key": ent["key"],
                 "dump": dict(ent["dump"]), "regionRank": rk, "region": rl, "verified": "[!]" in name,
                 "revision": revision(name), "title": display_title(name), "folder": folder,
                 "penalty": rules.get("penalty", 0), "extra": dict(rules.get("extra", {}))}
            if name.lower() in include:
                c["extra"]["untaggedRealGame"] = True
            if folder == "Sega Game Gear" and name.lower().endswith(".sms"):
                c["dump"]["smsModeOnGameGear"] = True
            tk = rules.get("key_prefix", "") + title_key(name)
            tk = aliases.get(con, {}).get(tk, tk)  # region-merged: the same game sold under another name
            cands[con].setdefault(tk, []).append(c)
        print(f"scanned {folder}: {n} files", file=sys.stderr)
    if not dry:
        cat._atomic_write(cache_path, cache)

    # what the catalog already holds (from the index: one read instead of one SMB call per record)
    have = {(r["console"], r["sha1"]): r for r in cat.load_index(None, base)}
    report = {}
    for con in consoles:
        groups = cands[con]
        added = existed = extra = aliased = 0
        for tk, lst in sorted(groups.items()):
            lst.sort(key=score)
            main = lst[0]
            keep = [(main, "")]
            for o in lst[1:]:
                if app_db_id(con, o["key"]) in dbids or known_revision(known_revs, con, o["name"]):
                    keep.append((o, main["key"]))
                elif (con, o["key"]) in have and have[(con, o["key"])].get("aliasOf") != main["key"]:
                    # an existing record now merged into another title (curated.json aliases): kept, never deleted,
                    # but marked so the work queue plays the main dump only
                    aliased += 1
                    if not dry:
                        cat.update(con, o["key"], {"extra": {"aliasOf": main["key"], "aliasTitle": main["title"]}}, base)
            for c, rev_of in keep:
                if rev_of:
                    extra += 1
                rec = cat.new_record(
                    con, c["key"], c["title"], region=c["region"],
                    dump=c["dump"] | {"goodName": os.path.splitext(c["name"])[0], "verified": c["verified"],
                                      "revision": c["revision"], "alternatives": len(lst) - 1},
                    file={"path": c["path"], "member": c["member"], "name": c["name"], "size": c["dump"]["size"]},
                    gameDbId=app_db_id(con, c["key"]), revisionOf=rev_of, extra=c["extra"])
                if (con, c["key"]) in have:
                    existed += 1
                elif dry:
                    added += 1
                elif cat.add(rec, base):
                    added += 1
                else:
                    existed += 1
        report[con] = {"titles": len(groups), "records_added": added, "records_existing": existed,
                       "extra_revisions": extra, "marked_alias": aliased,
                       "dumps_considered": sum(len(v) for v in groups.values())}
    report["skipped"] = {f"{k[0]}: {k[1]}": v for k, v in sorted(skipped.items())}
    report["folders_not_scanned"] = SKIPPED_FOLDERS
    return report


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--console", nargs="*", choices=list(SOURCES), default=list(SOURCES))
    ap.add_argument("--src", default=r"X:\EMULATION")
    ap.add_argument("--root", default=None)
    ap.add_argument("--db", default=os.path.join(os.path.dirname(__file__), "..", "..", "..", "Windows", "Data", "default-db.json"))
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args(argv)
    base = a.root or cat.root()
    rep = build(a.console, a.src, base, a.dry_run, a.db)
    print(json.dumps(rep, indent=1))
    if not a.dry_run:
        cat._atomic_write(os.path.join(base, "reports", "titles-" + cat.now()[:10] + ".json"), rep)


if __name__ == "__main__":
    main()
