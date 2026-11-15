#!/usr/bin/env python3
"""Great Compatibility Catalog: the BrokenDev browse view (P1).

A small local web UI over the catalog share (read only): per-console lists with grade, tier, tags, notes, screenshots,
TAS link and reference result, filters and search, and a per-console progress summary. Started by
`brokendev.ps1 catalog view` (or `python view.py [--port 8790] [--root T:\\BrokenDev\\Catalog]`).

Reads index/<console>.jsonl (rebuilt by `catalog.py index`) for the lists, and the record file itself for the detail
pane, so a detail is never older than the last write. Screenshots and TAS files are served only from inside the
catalog root (or the folder a record names), never any other path. Python 3.9+, standard library only.
"""
from __future__ import annotations

import argparse
import json
import mimetypes
import os
import sys
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import catalog  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
PAGE = os.path.join(HERE, "view.html")
IMG_EXT = {".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"}
PROBE = "brokennes-catalog-view"


class State:
    base = ""
    cache: dict = {}   # console -> (mtime, rows)


def rows_for(console: str) -> list:
    p = os.path.join(State.base, "index", console + ".jsonl")
    try:
        m = os.path.getmtime(p)
    except OSError:
        return []
    hit = State.cache.get(console)
    if hit and hit[0] == m:
        return hit[1]
    rows = list(catalog.load_index(console, State.base))
    State.cache[console] = (m, rows)
    return rows


def consoles() -> list:
    return [c for c in catalog.CONSOLES if os.path.exists(os.path.join(State.base, "index", c + ".jsonl"))]


def summary() -> dict:
    return catalog.progress({c: rows_for(c) for c in consoles()})


def record_files(rec: dict, key: str, exts=None) -> tuple[str, list]:
    """(folder, [file names]) for the record's screenshots/tas path, when it lies inside the catalog root."""
    p = rec.get(key) or ""
    if not p:
        return "", []
    full = p if os.path.isabs(p) else os.path.join(State.base, p)
    full = os.path.normpath(full)
    if not inside_root(full):
        return full, []
    if os.path.isfile(full):
        return os.path.dirname(full), [os.path.basename(full)]
    if os.path.isdir(full):
        names = sorted(n for n in os.listdir(full) if os.path.isfile(os.path.join(full, n))
                       and (exts is None or os.path.splitext(n)[1].lower() in exts))
        return full, names
    return full, []


def inside_root(path: str) -> bool:
    try:
        r = os.path.normcase(os.path.realpath(State.base))
        p = os.path.normcase(os.path.realpath(path))
        return os.path.commonpath([r, p]) == r
    except (ValueError, OSError):
        return False


class Handler(BaseHTTPRequestHandler):
    server_version = "BrokenDevCatalog/1"

    def log_message(self, fmt, *args):  # quiet
        pass

    def send_json(self, obj, code=200):
        b = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def send_file(self, path, ctype=None, download=False):
        try:
            with open(path, "rb") as f:
                b = f.read()
        except OSError:
            return self.send_json({"error": "not found"}, 404)
        self.send_response(200)
        self.send_header("Content-Type", ctype or mimetypes.guess_type(path)[0] or "application/octet-stream")
        if download:
            self.send_header("Content-Disposition", f'attachment; filename="{os.path.basename(path)}"')
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def do_GET(self):
        u = urllib.parse.urlparse(self.path)
        parts = [urllib.parse.unquote(x) for x in u.path.strip("/").split("/") if x]
        try:
            if not parts:
                return self.send_file(PAGE, "text/html; charset=utf-8")
            if parts == ["api", "probe"]:
                return self.send_json({"tool": PROBE, "root": State.base})
            if parts == ["api", "summary"]:
                s = summary()
                s["root"] = State.base
                s["grades"] = catalog.GRADES; s["tags"] = catalog.TAGS; s["tiers"] = catalog.TIERS
                return self.send_json(s)
            if len(parts) == 3 and parts[:2] == ["api", "console"] and parts[2] in catalog.CONSOLES:
                return self.send_json({"console": parts[2], "rows": rows_for(parts[2])})
            if len(parts) == 4 and parts[:2] == ["api", "record"] and parts[2] in catalog.CONSOLES:
                rec = catalog.get(parts[2], parts[3].lower(), State.base)
                if rec is None:
                    return self.send_json({"error": "not found"}, 404)
                sd, shots = record_files(rec, "screenshots", IMG_EXT)
                td, tas = record_files(rec, "tas")
                return self.send_json({"record": rec, "screenshotFiles": shots, "tasFiles": tas,
                                       "screenshotDir": sd, "tasDir": td})
            if len(parts) == 5 and parts[0] in ("shot", "tas") and parts[1] in catalog.CONSOLES:
                rec = catalog.get(parts[1], parts[2].lower(), State.base)
                if rec is None:
                    return self.send_json({"error": "not found"}, 404)
                d, names = record_files(rec, "screenshots" if parts[0] == "shot" else "tas",
                                        IMG_EXT if parts[0] == "shot" else None)
                if parts[4] not in names:
                    return self.send_json({"error": "not found"}, 404)
                return self.send_file(os.path.join(d, parts[4]), download=parts[0] == "tas")
            if parts == ["progress.json"]:
                return self.send_json(summary())
            return self.send_json({"error": "not found"}, 404)
        except (BrokenPipeError, ConnectionResetError):
            pass


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--port", type=int, default=8790)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--root", help="catalog root (default $BROKENNES_CATALOG or T:\\BrokenDev\\Catalog)")
    a = ap.parse_args(argv)
    State.base = a.root or catalog.root()
    if not os.path.isdir(State.base):
        sys.exit(f"catalog root not found: {State.base}")
    srv = ThreadingHTTPServer((a.host, a.port), Handler)
    print(f"catalog view on http://{a.host}:{a.port}/  (root {State.base})", flush=True)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
