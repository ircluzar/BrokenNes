#!/usr/bin/env python
# [cer64plus] compat/tools/speed_merge.py <run dir> <frames> <build>: parses speed.log (SessionProbe bench on speed.state,
# low-end profile) and adds report.json["speed"] = {msPerFrame, realtimePercent, target, meetsTarget, ...}.
# Target (compat/README.md): sm64 300%, every other game 200%. fps target from the report (50 PAL / 60 NTSC).
import json, os, re, sys
out, frames, build = sys.argv[1], int(sys.argv[2]), sys.argv[3]
log = open(os.path.join(out, "speed.log"), encoding="utf-8", errors="replace").read()
rp = os.path.join(out, "report.json"); r = json.load(open(rp, encoding="utf-8"))
m = re.search(r"FRAME msPerFrame=([0-9.]+)", log)
gid = os.path.basename(os.path.dirname(out)); target = 300 if gid == "sm64" else 200
fps = r.get("fpsTarget") or 60
sp = {"profile": "lowend (PROBE_LOWEND=1, DOTNET_EnableAVX512F=0, 4 CPUs)", "state": "speed.state", "frames": frames,
      "warmup": 120, "build": build, "target": target}
if m:
    ms = float(m.group(1)); pct = round(1000.0 / fps / ms * 100, 1)
    sp.update({"msPerFrame": ms, "realtimePercent": pct, "meetsTarget": pct >= target})
    s = re.search(r"SPLIT cpuMs=\S+ rspMs=\S+ ioMs=\S+ cpuInstr=(\d+) rspSteps=(\d+)", log)
    if s: sp.update({"cpuInstr": int(s.group(1)), "rspSteps": int(s.group(2))})
    c = re.search(r"CPUREC compiled=(\d+) steps=(\d+) calls=(\d+) interpSteps=(\d+)", log)
    if c: sp.update({"cpuRecSteps": int(c.group(2)), "cpuInterpSteps": int(c.group(4))})
    sp["note"] = "machine shared with other agents: absolute ms is a load-dependent estimate"
else:
    sp["error"] = (log.strip().splitlines() or ["no output"])[-1][:200]
r["speed"] = sp
json.dump(r, open(rp, "w", encoding="utf-8"), indent=2)
print(f"SPEED {gid} " + (f"{sp['msPerFrame']} ms/frame {sp['realtimePercent']}% (target {target}%)" if m else "failed: " + sp["error"]))
