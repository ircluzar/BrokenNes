import sys
from PIL import Image
# krom tests draw PASS on green and FAIL on red in the right-hand column; grade by red pixels below the header.
a, b, name = sys.argv[1], sys.argv[2], sys.argv[3]
def red(p):
    img = Image.open(p).convert('RGB').resize((640, 480)); px = img.load()
    return sum(1 for j in range(20, 480) for i in range(520, 640) if px[i, j][0] > 150 and px[i, j][1] < 80 and px[i, j][2] < 80)
try:
    ours, ref = red(a), red(b)
except Exception:
    print('%-40s NO-IMAGE' % name); sys.exit()
print('%-40s %s (red %d vs ref %d)' % (name, 'PASS' if ours <= ref + 20 else 'FAIL', ours, ref))
