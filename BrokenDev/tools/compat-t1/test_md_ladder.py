"""Guard for t1's Genesis colour-ladder mapping (python -I test_md_ladder.py; exit 0 = pass).
BrokenNes outputs the measured MD DAC ladder, BizHawk Genplus-gx the linear one: the same picture must score 0 once mapped,
and a real picture difference must still show."""
import importlib.util, os, sys
import numpy as np
spec = importlib.util.spec_from_file_location("t1", os.path.join(os.path.dirname(os.path.abspath(__file__)), "t1.py"))
t1 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(t1)
# Streets of Rage's night-city title, its real colours: (0,0,1) sky, (0,1,2) and (0,2,2) buildings in 8-pixel blocks. The
# sky->building edge is 33 luma on BrokenNes's ladder but 24 on Genplus's: picdiff's edge threshold sits right between them
yy, xx = np.mgrid[0:224, 0:320]
pal = np.array([[0, 0, 1], [0, 1, 2], [0, 2, 2]])
lv = pal[(yy // 8 + xx // 8) % 3]
bn, gp = t1._MD_BN[lv].astype(np.int32), t1._MD_GP[lv].astype(np.int32)
fails = []
t1.MD_LADDER = False
raw = t1.picdiff(bn, gp)
if raw <= 0.03:
    fails.append(f"unmapped ladders should differ (got {raw})")
t1.MD_LADDER = True
if t1.picdiff(t1.to_ref_ladder(bn), gp) != 0:
    fails.append("mapped ladder should score 0")
moved = gp.copy(); moved[:, 160:] = np.roll(moved[:, 160:], 3, axis=0)
if t1.picdiff(t1.to_ref_ladder(bn), moved) <= 0.03:
    fails.append("a real difference must still score")
t1.MD_LADDER = False
if not np.array_equal(t1.to_ref_ladder(bn), bn):
    fails.append("other consoles must pass through")
print("FAIL: " + "; ".join(fails) if fails else "md ladder guard: pass")
sys.exit(1 if fails else 0)
