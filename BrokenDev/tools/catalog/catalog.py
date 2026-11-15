#!/usr/bin/env python3
"""Great Compatibility Catalog: library + CLI.

One JSON file per ROM record on a share (default T:\\BrokenDev\\Catalog), safe for
several machines writing at once. See docs/projects/compat-catalog-db.md.

  catalog.py add     --console nes --sha1 <id> --title "..." [--field k=v ...]
  catalog.py update  --console nes --sha1 <id> --grade Jank --tags picture,timing --notes "..."
  catalog.py get     --console nes --sha1 <id>
  catalog.py query   [--console nes] [--grade Broken] [--tag mapper] [--title zelda] [--json]
  catalog.py index                      (rebuild index/*.jsonl + catalog.csv; run on the catalog box)
  catalog.py stats
  catalog.py progress                   (reports/progress.json, the live-dashboard feed; index writes it too)
  catalog.py export-db --db Windows/Data/default-db.json [--write]   (dry run + diff unless --write)
  catalog.py titles  [--console nes ...] [--dry-run]   (see titles.py)

Python 3.9+, standard library only (7-Zip's 7z.exe for .7z archives in titles.py).
"""
from __future__ import annotations

import argparse
import csv
import datetime as _dt
import json
import os
import random
import socket
import sys
import time
import uuid

SCHEMA = "brokennes-catalog/v1"
CONSOLES = ["nes", "snes", "gb", "gbc", "sms", "gg", "md", "wms"]
GRADES = ["Not tested", "Nothing", "Broken", "Jank", "OK", "Certified"]
TAGS = ["picture", "sound", "timing", "crash", "input", "mapper", "save"]
TIERS = [0, 1, 2, 3]  # 0 = not played, T1 boot/title, T2 first level TAS, T3 full TAS
# catalog grade -> default-db.json status (the app's own scale)
DB_STATUS = {"Nothing": "Nothing", "Broken": "Broken", "Jank": "Jank", "OK": "Works", "Certified": "Works"}
# catalog console -> default-db.json "system" value
DB_SYSTEM = {"nes": "nes", "snes": "snes", "gb": "gb", "gbc": "gbc", "sms": "sms", "gg": "gg", "md": "md", "wms": "wms"}
LOCK_STALE_S = 60
FIELDS_EDITABLE = {"title", "region", "dump", "grade", "tier", "notes", "screenshots", "tas", "reference",
                   "tags", "core", "brokennesVersion", "gameDbId", "file", "revisionOf", "extra"}


def root() -> str:
    return os.environ.get("BROKENNES_CATALOG", r"T:\BrokenDev\Catalog")


def worker_id() -> str:
    return os.environ.get("BROKENNES_WORKER", socket.gethostname().lower())


def now() -> str:
    return _dt.datetime.now(_dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def record_path(console: str, sha1: str, base: str | None = None) -> str:
    sha1 = sha1.lower()
    return os.path.join(base or root(), "records", console, sha1[:2], sha1 + ".json")


def new_record(console: str, sha1: str, title: str, **kw) -> dict:
    if console not in CONSOLES:
        raise ValueError(f"unknown console {console!r}; one of {CONSOLES}")
    r = {
        "schema": SCHEMA, "console": console, "sha1": sha1.lower(), "title": title,
        "region": "", "dump": {}, "grade": "Not tested", "tier": 0, "notes": "",
        "screenshots": "", "tas": "", "reference": {}, "tags": [], "core": "", "brokennesVersion": "",
        "gameDbId": "", "file": {}, "revisionOf": "", "extra": {},
        "created": now(), "updated": now(), "worker": worker_id(), "rev": 0, "history": [],
    }
    for k, v in kw.items():
        r[k] = v
    validate(r)
    return r


def validate(r: dict) -> None:
    if r.get("console") not in CONSOLES:
        raise ValueError(f"bad console {r.get('console')!r}")
    if r.get("grade") not in GRADES:
        raise ValueError(f"bad grade {r.get('grade')!r}; one of {GRADES}")
    if r.get("tier") not in TIERS:
        raise ValueError(f"bad tier {r.get('tier')!r}")
    for t in r.get("tags", []):
        if t not in TAGS:
            raise ValueError(f"bad tag {t!r}; one of {TAGS}")
    s = r.get("sha1", "")
    if len(s) != 40 or any(c not in "0123456789abcdef" for c in s):
        raise ValueError(f"bad sha1 {s!r}")


# ---------- low-level, share-safe IO ----------

def _atomic_write(path: str, obj) -> None:
    d = os.path.dirname(path)
    os.makedirs(d, exist_ok=True)
    tmp = os.path.join(d, f".tmp-{worker_id()}-{uuid.uuid4().hex}")
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:
        json.dump(obj, f, indent=1, ensure_ascii=False, sort_keys=False)
        f.write("\n")
        f.flush()
        os.fsync(f.fileno())
    for i in range(20):  # os.replace is a single SMB rename; retry if a reader has it open
        try:
            os.replace(tmp, path)
            return
        except OSError:
            time.sleep(0.05 * (i + 1))
    os.remove(tmp)
    raise PermissionError(f"could not replace {path}")


class RecordLock:
    """Per-record lock: a `<record>.lock` directory. mkdir is atomic on the SMB server (measured: O_CREAT|O_EXCL lock
    files on \\STORAGE gave spurious "access denied" / "unexpected network error" under 8-way contention). Any
    other OSError while taking it is retried. A lock older than LOCK_STALE_S is broken (a crashed worker)."""

    def __init__(self, path: str, timeout: float = 300):
        self.lock = path + ".lock"
        self.timeout = timeout

    def __enter__(self):
        os.makedirs(os.path.dirname(self.lock), exist_ok=True)
        end = time.time() + self.timeout
        while True:
            try:
                os.mkdir(self.lock)
                return self
            except OSError:
                try:
                    if time.time() - os.path.getmtime(self.lock) > LOCK_STALE_S:
                        # break it by an atomic rename: two workers that both saw it stale cannot both remove it, and the loser
                        # can no longer rmdir a fresh lock another worker took in between (it puts a fresh one back)
                        grave = f"{self.lock}.stale-{uuid.uuid4().hex}"
                        os.rename(self.lock, grave)
                        if time.time() - os.path.getmtime(grave) > LOCK_STALE_S:
                            os.rmdir(grave)
                        else:
                            try:
                                os.rename(grave, self.lock)
                            except OSError:
                                os.rmdir(grave)
                        continue
                except OSError:
                    pass
                if time.time() > end:
                    raise TimeoutError(f"record locked: {self.lock}")
                time.sleep(random.uniform(0.05, 0.3))

    def __exit__(self, *a):
        for i in range(200):  # never leave our lock behind: other workers would wait LOCK_STALE_S
            try:
                os.rmdir(self.lock)
                return
            except FileNotFoundError:
                return
            except OSError:
                time.sleep(0.05)
        print(f"warning: could not remove {self.lock}", file=sys.stderr)


def _log(entry: dict, base: str | None = None) -> None:
    """Append-only per-worker change log (one writer per file, so no contention)."""
    d = os.path.join(base or root(), "log")
    os.makedirs(d, exist_ok=True)
    with open(os.path.join(d, worker_id() + ".jsonl"), "a", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(entry, ensure_ascii=False) + "\n")


# ---------- public API ----------

def get(console: str, sha1: str, base: str | None = None) -> dict | None:
    p = record_path(console, sha1, base)
    for i in range(40):  # a concurrent os.replace can briefly deny access over SMB
        try:
            with open(p, encoding="utf-8") as f:
                return json.load(f)
        except FileNotFoundError:
            return None
        except (OSError, json.JSONDecodeError):
            if i == 39:
                raise
            time.sleep(0.05 * (1 + i % 5))


def add(rec: dict, base: str | None = None, exist_ok: bool = True) -> bool:
    """Create a record. Returns False (and leaves the existing one untouched) if it already exists."""
    validate(rec)
    p = record_path(rec["console"], rec["sha1"], base)
    with RecordLock(p):
        if os.path.exists(p):
            if exist_ok:
                return False
            raise FileExistsError(p)
        rec["rev"] = 1
        _atomic_write(p, rec)
    _log({"op": "add", "console": rec["console"], "sha1": rec["sha1"], "at": now(), "worker": worker_id()}, base)
    return True


def update(console: str, sha1: str, changes: dict, base: str | None = None, expect_rev: int | None = None,
           append_notes: str | None = None, add_tags=None) -> dict:
    """Merge changes into a record under its lock. `extra`/`reference`/`dump`/`file` dicts merge; others replace.
    append_notes / add_tags are applied under the lock too, so concurrent workers never lose each other's notes."""
    bad = set(changes) - FIELDS_EDITABLE
    if bad:
        raise ValueError(f"not editable: {sorted(bad)}")
    p = record_path(console, sha1, base)
    with RecordLock(p):
        r = get(console, sha1, base)
        if r is None:
            raise FileNotFoundError(p)
        if expect_rev is not None and r["rev"] != expect_rev:
            raise RuntimeError(f"record changed (rev {r['rev']} != {expect_rev})")
        old = {k: r.get(k) for k in changes}
        for k, v in changes.items():
            if k in ("extra", "reference", "dump", "file") and isinstance(v, dict):
                r.setdefault(k, {}).update(v)
            else:
                r[k] = v
        if append_notes:
            r["notes"] = (r["notes"] + "\n" if r.get("notes") else "") + f"[{now()} {worker_id()}] " + append_notes
            changes = changes | {"append_notes": append_notes}
        for t in add_tags or []:
            if t not in r["tags"]:
                r["tags"].append(t)
        if add_tags:
            changes = changes | {"add_tags": list(add_tags)}
        r["updated"] = now()
        r["worker"] = worker_id()
        r["rev"] = r.get("rev", 0) + 1
        if "grade" in changes or "tier" in changes:
            r.setdefault("history", []).append({"at": r["updated"], "worker": r["worker"], "grade": r["grade"],
                                                "tier": r["tier"], "core": r.get("core", "")})
        validate(r)
        _atomic_write(p, r)
    _log({"op": "update", "console": console, "sha1": sha1, "at": r["updated"], "worker": r["worker"],
          "old": old, "new": changes}, base)
    return r


def iter_records(console: str | None = None, base: str | None = None):
    rd = os.path.join(base or root(), "records")
    for c in ([console] if console else CONSOLES):
        cd = os.path.join(rd, c)
        if not os.path.isdir(cd):
            continue
        for sub in sorted(os.listdir(cd)):
            sd = os.path.join(cd, sub)
            if not os.path.isdir(sd):
                continue
            for name in sorted(os.listdir(sd)):
                if name.endswith(".json"):
                    try:
                        with open(os.path.join(sd, name), encoding="utf-8") as f:
                            yield json.load(f)
                    except (json.JSONDecodeError, OSError) as e:
                        print(f"warning: unreadable {sd}\\{name}: {e}", file=sys.stderr)


def load_index(console: str | None = None, base: str | None = None):
    """Fast read path: the rebuilt index (may lag the records by one `index` run)."""
    d = os.path.join(base or root(), "index")
    for c in ([console] if console else CONSOLES):
        p = os.path.join(d, c + ".jsonl")
        if os.path.exists(p):
            with open(p, encoding="utf-8") as f:
                for line in f:
                    if line.strip():
                        yield json.loads(line)


INDEX_KEYS = ["console", "sha1", "title", "region", "grade", "tier", "tags", "core", "updated", "worker", "rev",
              "gameDbId", "revisionOf", "aliasOf", "screenshots", "tas", "reference", "brokennesVersion"]
NOTES_PREVIEW = 300  # index rows carry the start of the notes; the full record has all of them

PROGRESS_SCHEMA = "brokennes-catalog-progress/v1"
CONSOLE_NAMES = {"nes": "NES", "snes": "SNES", "gb": "Game Boy", "gbc": "Game Boy Color", "sms": "Master System",
                 "gg": "Game Gear", "md": "Genesis", "wms": "WideMess"}


def progress(rows_by_console: dict, built: str | None = None) -> dict:
    """Per-console progress from index rows (format: docs/projects/compat-catalog-db.md, 'Progress export')."""
    out = {"schema": PROGRESS_SCHEMA, "built": built or now(), "by": worker_id(), "consoles": {}, "total": {}}
    tot = {"titles": 0, "tested": 0, "playable": 0, "certified": 0}
    for c, rows in rows_by_console.items():
        g = {k: 0 for k in GRADES}; t = {str(k): 0 for k in TIERS}; tg = {k: 0 for k in TAGS}
        ref = {"match": 0, "differs": 0, "none": 0}; last = ""
        tas = shots = 0
        for x in rows:
            g[x.get("grade") or "Not tested"] = g.get(x.get("grade") or "Not tested", 0) + 1
            t[str(x.get("tier") or 0)] = t.get(str(x.get("tier") or 0), 0) + 1
            for tag in x.get("tags") or []:
                tg[tag] = tg.get(tag, 0) + 1
            v = ((x.get("reference") or {}).get("verdict") or "").lower()
            ref["match" if v == "match" else ("none" if not v else "differs")] += 1
            tas += bool(x.get("tas")); shots += bool(x.get("screenshots"))
            if x.get("grade") != "Not tested" and (x.get("updated") or "") > last:
                last = x.get("updated") or ""
        n = len(rows); tested = n - g.get("Not tested", 0)
        playable = g.get("OK", 0) + g.get("Certified", 0)
        pct = lambda a: round(100.0 * a / n, 1) if n else 0.0
        out["consoles"][c] = {"name": CONSOLE_NAMES.get(c, c), "titles": n, "grades": g, "tiers": t, "tags": tg,
                              "reference": ref, "withTas": tas, "withScreenshots": shots,
                              "tested": tested, "playable": playable, "certified": g.get("Certified", 0),
                              "testedPct": pct(tested), "playablePct": pct(playable), "lastGraded": last}
        tot["titles"] += n; tot["tested"] += tested; tot["playable"] += playable; tot["certified"] += g.get("Certified", 0)
    n = tot["titles"]
    tot["testedPct"] = round(100.0 * tot["tested"] / n, 1) if n else 0.0
    tot["playablePct"] = round(100.0 * tot["playable"] / n, 1) if n else 0.0
    out["total"] = tot
    return out


def write_progress(base: str | None = None, rows_by_console: dict | None = None) -> str:
    b = base or root()
    if rows_by_console is None:
        rows_by_console = {}
        for c in CONSOLES:
            rows = list(load_index(c, b))
            if rows:
                rows_by_console[c] = rows
    p = os.path.join(b, "reports", "progress.json")
    os.makedirs(os.path.dirname(p), exist_ok=True)
    _atomic_write(p, progress(rows_by_console))
    return p


def rebuild_index(base: str | None = None) -> dict:
    b = base or root()
    d = os.path.join(b, "index")
    counts = {}
    allrows = []
    by_console = {}
    for c in CONSOLES:
        rows = [{k: r.get(k) for k in INDEX_KEYS} | {"aliasOf": (r.get("extra") or {}).get("aliasOf", ""),
                                                      "path": (r.get("file") or {}).get("path", ""),
                                                      "notes": (r.get("notes") or "")[:NOTES_PREVIEW]}
                for r in iter_records(c, b)]
        if not rows and not os.path.isdir(os.path.join(b, "records", c)):
            continue
        rows.sort(key=lambda x: (x["title"].lower(), x["sha1"]))
        _atomic_write_lines(os.path.join(d, c + ".jsonl"), [json.dumps(x, ensure_ascii=False) for x in rows])
        g = {}
        for x in rows:
            g[x["grade"]] = g.get(x["grade"], 0) + 1
        counts[c] = {"total": len(rows), "grades": g}
        by_console[c] = rows
        allrows += rows
    tmp = os.path.join(d, f".tmp-{uuid.uuid4().hex}.csv")
    with open(tmp, "w", encoding="utf-8", newline="") as f:
        w = csv.writer(f)
        w.writerow(["console", "sha1", "title", "region", "grade", "tier", "tags", "core", "updated", "worker", "path"])
        for x in allrows:
            w.writerow([x["console"], x["sha1"], x["title"], x["region"], x["grade"], x["tier"], "|".join(x["tags"] or []),
                        x["core"], x["updated"], x["worker"], x["path"]])
    os.replace(tmp, os.path.join(d, "catalog.csv"))
    _atomic_write(os.path.join(d, "summary.json"), {"schema": SCHEMA, "built": now(), "by": worker_id(), "consoles": counts})
    write_progress(b, by_console)
    return counts


def _atomic_write_lines(path: str, lines) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + f".tmp-{uuid.uuid4().hex}"
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:
        for ln in lines:
            f.write(ln + "\n")
    os.replace(tmp, path)


def is_primary(r: dict) -> bool:
    """The title's own dump: not a kept extra revision, not a region alias of another title (the work queue's set)."""
    return not r.get("revisionOf") and not (r.get("aliasOf") or (r.get("extra") or {}).get("aliasOf"))


def query(console=None, grade=None, tag=None, title=None, tier=None, base=None, use_index=True, primary=False):
    src = load_index(console, base) if use_index else iter_records(console, base)
    for r in src:
        if primary and not is_primary(r):
            continue
        if grade and r["grade"] != grade:
            continue
        if tag and tag not in (r.get("tags") or []):
            continue
        if title and title.lower() not in r["title"].lower():
            continue
        if tier is not None and r["tier"] != tier:
            continue
        yield r


# ---------- export to the app's default-db.json ----------

def export_db(db_path: str, write: bool = False, base: str | None = None, out=sys.stdout, override_hand: bool = False) -> dict:
    """Merge graded catalog records into default-db.json. Never drops or reorders an existing entry; an existing entry
    that was hand-graded (no `catalogUpdated` stamp) is never changed unless override_hand; one written by an earlier
    export is changed when the catalog record is newer than its stamp."""
    with open(db_path, encoding="utf-8") as f:
        db = json.load(f)
    games = db["data"]["games"]
    by_id = {g["id"].lower(): g for g in games}
    added, changed, kept = [], [], []
    for r in iter_records(None, base):
        if r["grade"] == "Not tested":
            continue
        sys_ = DB_SYSTEM[r["console"]]
        gid = (r.get("gameDbId") or f"{sys_}_{r['sha1']}").lower()
        status = DB_STATUS[r["grade"]]
        note = r["notes"].strip().splitlines()[0][:300] if r["notes"].strip() else ""
        if r["grade"] == "Certified" and not note.lower().startswith("certified"):
            note = ("Certified " + r["updated"][:10] + (": " + note if note else "")).strip()
        g = by_id.get(gid)
        if g is None:
            fn = (r.get("file") or {}).get("name", r["title"])
            entry = {"id": gid, "title": r["title"], "commonName": "", "status": status, "note": note, "system": sys_,
                     "romKey": fn, "headerSignature": (r.get("dump") or {}).get("headerSignature", ""), "builtIn": False,
                     "size": (r.get("file") or {}).get("size", 0), "createdAt": r["created"],
                     "catalogUpdated": r["updated"]}
            added.append(entry)
            continue
        if g.get("status") == status and (not note or g.get("note") == note):
            kept.append(gid)
            continue
        if not g.get("catalogUpdated") and not override_hand:
            kept.append(gid)
            print(f"  keep   {gid} {g.get('title')!r}: hand-graded {g.get('status')} kept (catalog says {r['grade']}; "
                  f"--override-hand to replace)", file=out)
            continue
        if g.get("catalogUpdated") and r["updated"][:19] <= g["catalogUpdated"][:19]:
            kept.append(gid)
            continue
        changed.append((g, status, note, r["updated"]))
    for e in added:
        print(f"  + add  {e['id']} {e['title']!r} status={e['status']}", file=out)
    for g, status, note, upd in changed:
        print(f"  ~ chg  {g['id']} {g.get('title')!r}: {g.get('status')} -> {status}"
              + (f"; note {g.get('note','')!r} -> {note!r}" if note and note != g.get("note") else ""), file=out)
    print(f"export-db: {len(added)} to add, {len(changed)} to change, {len(kept)} unchanged; existing entries: "
          f"{len(games)} (none dropped){'' if write else '  [dry run: pass --write to apply]'}", file=out)
    if write and (added or changed):
        for g, status, note, upd in changed:
            g["status"] = status
            if note:
                g["note"] = note
                if "notes" in g:
                    g["notes"] = note
            g["catalogUpdated"] = upd
        games.extend(added)
        db["exportedAt"] = now()
        tmp = db_path + ".tmp"
        with open(tmp, "w", encoding="utf-8", newline="\n") as f:
            json.dump(db, f, indent=2, ensure_ascii=False)
            f.write("\n")
        os.replace(tmp, db_path)
    return {"added": len(added), "changed": len(changed), "kept": len(kept)}


# ---------- CLI ----------

def _parse_fields(pairs):
    out = {}
    for p in pairs or []:
        k, _, v = p.partition("=")
        try:
            out[k] = json.loads(v)
        except json.JSONDecodeError:
            out[k] = v
    return out


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", help="catalog root (default $BROKENNES_CATALOG or T:\\BrokenDev\\Catalog)")
    sp = ap.add_subparsers(dest="cmd", required=True)

    def rec_args(p):
        p.add_argument("--console", required=True, choices=CONSOLES)
        p.add_argument("--sha1", required=True)

    def edit_args(p):
        p.add_argument("--title"); p.add_argument("--region"); p.add_argument("--grade", choices=GRADES)
        p.add_argument("--tier", type=int, choices=TIERS); p.add_argument("--notes"); p.add_argument("--append-notes")
        p.add_argument("--tags", help="comma list, replaces"); p.add_argument("--add-tags", help="comma list, adds")
        p.add_argument("--core"); p.add_argument("--version", dest="brokennesVersion")
        p.add_argument("--screenshots"); p.add_argument("--tas")
        p.add_argument("--reference", help='JSON, e.g. {"emulator":"Mesen 2.1.1","frames":3600,"diff":0.0,"verdict":"match"}')
        p.add_argument("--field", action="append", help="k=v (v parsed as JSON if possible)")

    p = sp.add_parser("add"); rec_args(p); edit_args(p)
    p = sp.add_parser("update"); rec_args(p); edit_args(p); p.add_argument("--expect-rev", type=int)
    p.add_argument("--agent-grade", choices=GRADES, help="the T2/T3 agent's own grade: kept in extra.t2 and checked against the stored grade")
    p = sp.add_parser("get"); rec_args(p)
    p = sp.add_parser("query"); p.add_argument("--console", choices=CONSOLES); p.add_argument("--grade", choices=GRADES)
    p.add_argument("--tag", choices=TAGS); p.add_argument("--title"); p.add_argument("--tier", type=int)
    p.add_argument("--live", action="store_true", help="scan records instead of the index"); p.add_argument("--json", action="store_true")
    p.add_argument("--primary", action="store_true", help="one record per title: skip extra revisions and region aliases")
    sp.add_parser("index"); sp.add_parser("stats")
    sp.add_parser("progress", help="write reports/progress.json from the index (index also writes it)")
    p = sp.add_parser("export-db"); p.add_argument("--db", required=True); p.add_argument("--write", action="store_true")
    p.add_argument("--override-hand", action="store_true")
    a = ap.parse_args(argv)
    base = a.root

    def changes_from(a, existing=None):
        ch = {}
        for k in ("title", "region", "grade", "tier", "notes", "core", "brokennesVersion", "screenshots", "tas"):
            v = getattr(a, k, None)
            if v is not None:
                ch[k] = v
        if a.tags is not None:
            ch["tags"] = [t for t in a.tags.split(",") if t]
        if a.reference:
            ch["reference"] = json.loads(a.reference)
        ch.update(_parse_fields(a.field))
        return ch

    if a.cmd == "add":
        ch = changes_from(a)
        if a.append_notes:
            ch["notes"] = a.append_notes
        if a.add_tags:
            ch["tags"] = sorted(set(ch.get("tags", [])) | set(a.add_tags.split(",")) - {""})
        rec = new_record(a.console, a.sha1, ch.pop("title", "") or a.sha1, **ch)
        print("added" if add(rec, base) else "exists (unchanged)")
    elif a.cmd == "update":
        ch = changes_from(a)
        warn = []
        cur = get(a.console, a.sha1, base)
        # the reference summary play_step / record computed (record stop writes <movie>.ref.json): it replaces the agent's numbers
        if a.tas and os.path.exists(a.tas + ".ref.json"):
            with open(a.tas + ".ref.json", encoding="utf-8") as f:
                rs = json.load(f)
            given = ch.get("reference", {})
            refj = {"emulator": rs.get("emulator", given.get("emulator", "")), "frames": rs.get("frames"), "offset": rs.get("offset"),
                    "median": rs.get("median"), "max": rs.get("max"), "maxAtFrame": rs.get("maxAtFrame"), "steps": rs.get("steps"),
                    "over012": rs.get("over012"), "computedBy": "play_step (shift-tolerant score)"}
            for k in ("median", "max"):
                if k in given and rs.get(k) is not None and abs(float(given[k]) - float(rs[k])) > 0.02:
                    warn.append(f"the agent wrote reference {k} {given[k]}, the tool measured {rs[k]}: the tool's value is stored")
            ch["reference"] = refj
            if cur is not None:
                nh, fs = rs.get("romSha1NoHeader"), rs.get("romSha1")
                ok = a.sha1 in (nh, fs) or (cur.get("dump", {}).get("fileSha1") in (fs,))
                if not ok:
                    warn.append(f"sha1 mismatch: the ROM played ({rs.get('rom')}) is {nh or fs} (file {fs}), the record is {a.sha1}")
                    ch.setdefault("extra", {})["sha1Mismatch"] = {"played": rs.get("rom"), "sha1": nh, "fileSha1": fs}
        if a.agent_grade:
            ch.setdefault("extra", {})["t2"] = {"agentGrade": a.agent_grade, "at": now(), "tas": a.tas}
        r = update(a.console, a.sha1, ch, base, a.expect_rev, append_notes=a.append_notes,
                   add_tags=[t for t in (a.add_tags or "").split(",") if t])
        if a.agent_grade and r["grade"] != a.agent_grade:
            warn.append(f"grade mismatch: the record keeps {r['grade']}, the agent graded {a.agent_grade} (re-check this game)")
            r = update(a.console, a.sha1, {"extra": {"gradeMismatch": {"stored": r["grade"], "agent": a.agent_grade, "at": now()}}}, base)
        for w in warn:
            print("WARNING: " + w)
        print(f"updated rev {r['rev']} grade {r['grade']}")
    elif a.cmd == "get":
        r = get(a.console, a.sha1, base)
        if r is None:
            sys.exit("not found")
        print(json.dumps(r, indent=1, ensure_ascii=False))
    elif a.cmd == "query":
        n = 0
        for r in query(a.console, a.grade, a.tag, a.title, a.tier, base, use_index=not a.live, primary=a.primary):
            n += 1
            print(json.dumps(r, ensure_ascii=False) if a.json else
                  f"{r['console']:4} {r['grade']:10} T{r['tier']} {r['sha1'][:12]} {r['title']}")
        if not a.json:
            print(f"{n} record(s)", file=sys.stderr)
    elif a.cmd == "index":
        print(json.dumps(rebuild_index(base), indent=1))
    elif a.cmd == "stats":
        p = os.path.join(base or root(), "index", "summary.json")
        print(open(p, encoding="utf-8").read() if os.path.exists(p) else "no index yet: run `catalog.py index`")
    elif a.cmd == "progress":
        print(write_progress(base))
    elif a.cmd == "export-db":
        export_db(a.db, a.write, base, override_hand=a.override_hand)


if __name__ == "__main__":
    main()
