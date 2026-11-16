"""Pull the fleet fuzz logs (C:\\brokennes\\fuzz\\out_<seed>) and list the findings like battery.ps1 does (same filters).
python fuzzcollect.py  -> C:\\BrokenNes-work\\fleet\\fuzz\\<PC>\\..., findings.md"""
import os, re, sys
sys.path.insert(0, r"E:\fleet")
from fleet import boxes, run, enc  # noqa: E402
from pull import pull  # noqa: E402

OUT = r"C:\BrokenNes-work\fleet\fuzz"
trials, findings = 0, []
for n, b in boxes().items():
    d = os.path.join(OUT, n)
    pull(b, r"C:\brokennes\fuzz", d) if False else None
    rc, out, _ = run(b, enc(r"(Get-ChildItem C:\brokennes\fuzz -Directory -Filter out_*).FullName"), 60)
    for rd in out.split():
        pull(b, rd, os.path.join(d, os.path.basename(rd)))
    for root, _, fs in os.walk(d):
        for f in fs:
            if not f.endswith(".log") or ".child" in f:
                continue
            lines = open(os.path.join(root, f), encoding="utf-8", errors="replace").read().splitlines()
            for l in lines:
                m = re.match(r"^DONE (\d+) trials", l)
                if m:
                    trials += int(m.group(1))
            for i, l in enumerate(lines):
                m = re.match(r"^NEW (.+)$", l)
                if not m:
                    continue
                k = m.group(1)
                if k.startswith("info |") or "load-save-not-idempotent" in k or k.startswith("create | NotSupportedException"):
                    continue
                ctx = lines[i + 1] if i + 1 < len(lines) else ""
                seed = re.search(r"seed (\d+)", ctx)
                findings.append(f"- {n} {f[:-4]}: {k} [{ctx[:100]}] repro: bfuzz {f[:-4]} 1 out {seed.group(1) if seed else '?'}")
os.makedirs(OUT, exist_ok=True)
open(os.path.join(OUT, "findings.md"), "w", encoding="utf-8").write(f"# Fleet fuzz\n\n{trials} trials, {len(findings)} finding(s)\n\n" + "\n".join(findings) + "\n")
print(trials, "trials,", len(findings), "findings")
print("\n".join(findings[:20]))
