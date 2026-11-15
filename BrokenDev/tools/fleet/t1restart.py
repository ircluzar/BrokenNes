"""restart a T1 tag's jobs from scratch (results dropped): python t1restart.py <console> <tag>"""
import json, os, sys
sys.path.insert(0, r"E:\fleet")
from jobs import job, stop  # noqa: E402
from fleet import boxes, run, enc  # noqa: E402

console, tag = sys.argv[1], sys.argv[2]
plan = json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "shards", tag, "plan.json")))
bs = boxes()
for n, sids in plan.items():
    for sid in sids:
        stop(bs[n], sid)
        run(bs[n], enc(r"Get-CimInstance Win32_Process | ? { $_.CommandLine -match '%s' } | %% { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }; "
                       r"Remove-Item C:\brokennes\roms\%s\out, C:\brokennes\work\%s -Recurse -Force -ErrorAction SilentlyContinue; 'x'" % (sid, sid, sid)), 120)
        env = {"BNT1_WORKSHOP": r"C:\brokennes\kit\workshop\BrokenNes.Workshop.exe", "BNT1_MESEN": r"C:\brokennes\kit\mesen\Mesen.exe", "BNT1_BIZHAWK": r"C:\brokennes\kit\bizhawk\EmuHawk.exe",
               "BNT1_SCRATCH": rf"C:\brokennes\work\{sid}", "BNT1_HEAVY": rf"C:\brokennes\work\{sid}\heavy", "BNT1_SLOTS": "1", "BNT1_LANE": "fleet-" + sid}
        cmd = rf"C:\brokennes\kit\py\python.exe -I C:\brokennes\kit\tools\compat-t1\t1.py work --console {console} --shard C:\brokennes\roms\{sid}"
        print(n, sid, job(bs[n], sid, cmd, r"C:\brokennes", env, limit=20 * 3600, low=False))
