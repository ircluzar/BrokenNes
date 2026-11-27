# full.py OUT [VARIANT]: the whole bot run from st_sbmenu (menu + every segment), no savestates; VARIANT tweaks seg3d via env
import sys, os; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import seg
from sclib import S
menu = [l for l in open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'menu.steps')).read().splitlines() if l and 'save:' not in l]
s = S(); s.x, s.y = 320, 240
for f in seg.SEGS: f(s)
s.lines = menu + [l for l in s.lines if 'save:' not in l] + ['@6000=>']
s.write(sys.argv[1])
