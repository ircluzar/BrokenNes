"""Watch the running fleet tags; when every job of a tag has ended, collect it (T1: pull + ingest; Nested: collect; gg2sms: collect).
Prints one line per event. python drive.py"""
import json, os, subprocess, sys, time
sys.path.insert(0, r"E:\fleet")
from jobs import poll  # noqa: E402
from fleet import boxes  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
PY = sys.executable
os.environ.setdefault("BNT1_VERSION", subprocess.run(["git", "-C", r"C:\BrokenNes-work\fleet_kit_wt", "rev-parse", "--short", "HEAD"], capture_output=True, text=True).stdout.strip())
# python drive.py t1:<tag>:<console>[:force] ... nested:<tag> g2s:<tag>
SPEC = [x.split(":") for x in (sys.argv[1:] or ["t1:gg:gg", "t1:sms:sms", "t1:gbc:gbc", "t1:gb:gb", "t1:snes:snes", "t1:md:md", "nested:fullnes", "g2s:w2"])]
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
    todo = [(x[0], x[1]) for x in SPEC]
    meta = {(x[0], x[1]): x for x in SPEC}
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
            con = meta[(kind, tag)][2]; force = ["--force"] if len(meta[(kind, tag)]) > 3 else []
            r = subprocess.run([PY, os.path.join(HERE, "t1fleet.py"), "pull", "--tag", tag, "--console", con, "--ingest", *force], capture_output=True, text=True, cwd=r"E:\fleet")
            if con != "nes":
                subprocess.run([PY, os.path.join(HERE, "kit", "tools", "compat-t1", "t1.py"), "regrade", "--console", con], capture_output=True, text=True)
            k = subprocess.run([PY, os.path.join(HERE, "kit", "tools", "compat-t1", "t1.py"), "report", "--console", con], capture_output=True, text=True)
            print(f"t1 {tag} ({con}) ingested: " + " ".join(k.stdout.splitlines()[:2]), flush=True)
        elif kind == "nested":
            r = subprocess.run([PY, os.path.join(HERE, "nestedfleet.py"), "collect", "--tag", tag], capture_output=True, text=True, cwd=r"E:\fleet")
            print(f"nested {tag} collected: " + (r.stdout.strip().splitlines() or ["?"])[-2][:300], flush=True)
        else:
            r = subprocess.run([PY, os.path.join(HERE, "gg2smsfleet.py"), "collect", "--tag", tag], capture_output=True, text=True, cwd=r"E:\fleet")
            print(f"g2s {tag} collected: " + (r.stdout.strip().splitlines() or ["?"])[-1][:300], flush=True)
        done.add((kind, tag))
    if len(done) == len(SPEC):
        print("all collected", flush=True)
        break
    time.sleep(300)
