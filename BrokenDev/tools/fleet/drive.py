"""Watch the running fleet tags; when every job of a tag has ended, collect it (T1: pull + ingest; Nested: collect; gg2sms: collect).
Prints one line per event. python drive.py"""
import json, os, subprocess, sys, time
sys.path.insert(0, r"E:\fleet")
from jobs import poll  # noqa: E402
from fleet import boxes  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
PY = sys.executable
T1 = ["gg", "sms", "gbc", "gb", "snes", "md"]
done = set()
bs = boxes()


def jobs_of(kind, tag):
    p = {"t1": os.path.join(HERE, "shards", tag, "plan.json"), "nested": os.path.join(HERE, "nested_stage", "runs", tag, "plan.json"),
         "g2s": os.path.join(HERE, "gg2sms_stage", tag, "plan.json")}[kind]
    if not os.path.exists(p):
        return None
    j = json.load(open(p))
    j = j.get("plan", j) if kind == "nested" else j
    return [(n, s) for n, ss in j.items() for s in ss]


def finished(js):
    left = 0
    for n, s in js:
        rc, _ = poll(bs[n], s)
        if rc is None:
            left += 1
    return left


while True:
    todo = [("t1", t) for t in T1] + [("nested", "fullnes"), ("g2s", "w2")]
    for kind, tag in todo:
        if (kind, tag) in done:
            continue
        js = jobs_of(kind, tag)
        if not js:
            continue
        left = finished(js)
        if left:
            continue
        print(f"{kind} {tag}: all {len(js)} jobs ended, collecting", flush=True)
        if kind == "t1":
            r = subprocess.run([PY, os.path.join(HERE, "t1fleet.py"), "pull", "--tag", tag, "--console", tag, "--ingest"], capture_output=True, text=True, cwd=r"E:\fleet")
            k = subprocess.run([PY, os.path.join(HERE, "kit", "tools", "compat-t1", "t1.py"), "report", "--console", tag], capture_output=True, text=True)
            print(f"t1 {tag} ingested: " + " ".join(k.stdout.splitlines()[:2]), flush=True)
        elif kind == "nested":
            r = subprocess.run([PY, os.path.join(HERE, "nestedfleet.py"), "collect", "--tag", tag], capture_output=True, text=True, cwd=r"E:\fleet")
            print(f"nested {tag} collected: " + (r.stdout.strip().splitlines() or ["?"])[-2][:300], flush=True)
        else:
            r = subprocess.run([PY, os.path.join(HERE, "gg2smsfleet.py"), "collect", "--tag", tag], capture_output=True, text=True, cwd=r"E:\fleet")
            print(f"g2s {tag} collected: " + (r.stdout.strip().splitlines() or ["?"])[-1][:300], flush=True)
        done.add((kind, tag))
    if len(done) == len(T1) + 2:
        print("all collected", flush=True)
        break
    time.sleep(300)
