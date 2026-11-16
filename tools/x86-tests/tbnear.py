"""tbnear.py DATABASE.INI SCREEN_OR_TB.TXT [DHRY.TXT...]: our TopBench score and sub-timings vs the real PCs in TopBench's database (nearest by score),
plus Dhrystone 2.1 results as DMIPS (Dhrystones/s / 1757, the VAX 11/780 = 1 DMIPS convention). Run with python -I."""
import sys, re
db, cur = [], None
for line in open(sys.argv[1], encoding='latin1'):
    line = line.strip()
    if line.startswith('['): cur = {}; db.append(cur)
    elif cur is not None and '=' in line: k, v = line.split('=', 1); cur[k] = v
txt = open(sys.argv[2], encoding='latin1').read()
m = re.search(r'Score:\s*(\d+)', txt)
if not m: print('  TOPBENCH: no score in output'); sys.exit(0)
score = int(m.group(1))
print(f'  TOPBENCH score {score}')
rows = [r for r in db if r.get('Score', '').isdigit()]
rows.sort(key=lambda r: (abs(int(r['Score']) - score), r.get('CPU', '')))
for r in rows[:6]:
    print(f"    real PC score {r['Score']:>5}: {r.get('CPU','?')} {r.get('CPUspeed','?')} - {r.get('Machine','?')}")
for p in sys.argv[3:]:
    t = open(p, encoding='latin1').read()
    d = re.search(r'Dhrystones per Second:\s*([\d.]+)', t)
    print(f"  {p}: " + (f"{float(d.group(1)):.0f} Dhrystones/s = {float(d.group(1))/1757:.2f} DMIPS" if d else 'no result'))
