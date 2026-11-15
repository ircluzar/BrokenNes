#!/usr/bin/env python3
"""Concurrency check for the catalog store: N processes x M locked appends to one test record.

  smb_stress.py [--root T:\\BrokenDev\\Catalog\\_stress] [--workers 8] [--each 10]

Passes when the record ends with exactly N*M note lines and rev == N*M + 1. Uses its own root (default
<catalog>\\_stress) and removes it afterwards.
"""
import argparse
import multiprocessing as mp
import os
import shutil
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import catalog as cat  # noqa: E402

SHA = "0" * 39 + "1"


def work(args):
    root, w, each = args
    os.environ["BROKENNES_WORKER"] = f"stress{w}"
    for i in range(each):
        cat.update("nes", SHA, {}, base=root, append_notes=f"w{w}-{i}", add_tags=["timing"])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=os.path.join(cat.root(), "_stress"))
    ap.add_argument("--workers", type=int, default=8)
    ap.add_argument("--each", type=int, default=10)
    a = ap.parse_args()
    shutil.rmtree(a.root, ignore_errors=True)
    cat.add(cat.new_record("nes", SHA, "stress"), a.root)
    with mp.Pool(a.workers) as p:
        p.map(work, [(a.root, w, a.each) for w in range(a.workers)])
    r = cat.get("nes", SHA, a.root)
    lines = len(r["notes"].splitlines())
    want = a.workers * a.each
    ok = lines == want and r["rev"] == want + 1
    print(f"{'PASS' if ok else 'FAIL'}: notes {lines}/{want}, rev {r['rev']} (want {want + 1}), root {a.root}")
    shutil.rmtree(a.root, ignore_errors=True)
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
