"""help a slow PC: copy one of its T1 shards (from the local export) to a helper PC and run a worker there from --start.
python t1help.py <console> <slow PC> <helper PC> [start=0.5]   -> job <sid>_h<helper>; results land in C:\\brokennes\\roms\\<sid>_h on the helper"""
import os, sys
sys.path.insert(0, r"E:\fleet")
from jobs import put, job  # noqa: E402
from fleet import boxes  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
console, slow, helper = sys.argv[1:4]
start = sys.argv[4] if len(sys.argv) > 4 else "0.5"
sid = f"{console}_{console}_0"
src = os.path.join(HERE, "shards", console, slow, sid)
hid = f"{sid}_h{slow[:3]}"
print(put(src, rf"C:\brokennes\roms\{hid}", [helper]))
env = {"BNT1_WORKSHOP": r"C:\brokennes\kit\workshop\BrokenNes.Workshop.exe", "BNT1_MESEN": r"C:\brokennes\kit\mesen\Mesen.exe",
       "BNT1_BIZHAWK": r"C:\brokennes\kit\bizhawk\EmuHawk.exe", "BNT1_SCRATCH": rf"C:\brokennes\work\{hid}",
       "BNT1_HEAVY": rf"C:\brokennes\work\{hid}\heavy", "BNT1_SLOTS": "1", "BNT1_LANE": "fleet-" + hid}
cmd = rf"C:\brokennes\kit\py\python.exe -I C:\brokennes\kit\tools\compat-t1\t1.py work --start {start} --console {console} --shard C:\brokennes\roms\{hid}"
print(helper, hid, job(boxes()[helper], hid, cmd, r"C:\brokennes", env, limit=20 * 3600, low=False))
