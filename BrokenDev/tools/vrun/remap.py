"""Shift and remap an input script: python remap.py <script-file> <shift> <map> ; map like "A=B,B=Y" (simultaneous)."""
import sys

text = open(sys.argv[1]).read().strip()
shift = int(sys.argv[2])
m = dict(kv.split("=") for kv in sys.argv[3].split(",")) if len(sys.argv) > 3 and sys.argv[3] else {}
out = []
for step in text.split(","):
    f, b = step.split(":", 1)
    bs = [m.get(x, x) for x in b.split("+")] if b else []
    out.append("%d:%s" % (int(f) + shift, "+".join(bs)))
print(",".join(out))
