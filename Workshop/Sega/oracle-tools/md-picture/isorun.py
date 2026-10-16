"""Run a scene ROM that is not made by scenefuzz's families (sprite_isolate.py's variants of one scene) on BlastEm and compare every scene with this emulator:
    python isorun.py rom.bin NAME SCENES      (hold the oracle lock; the ROM's json next to it is the scene meta; environment as oracle.py)
The oracle's screenshots land in <root>\\out\\NAME\\s000.png ..., our pictures and the differences in <root>\\out\\NAME_cmp."""
import os, subprocess, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import oracle

rom, name, scenes = sys.argv[1], sys.argv[2], int(sys.argv[3])
first = int(sys.argv[4]) if len(sys.argv) > 4 else 500      # ms before the first screenshot and between scenes (the slow scenes want 1500 / 1000)
gap = int(sys.argv[5]) if len(sys.argv) > 5 else 350
out = os.path.join(oracle.ROOT, 'out', name)
script = 'w%d,' % first + ','.join(['shot', 'k:Start', 'w%d' % gap] * scenes)
got = oracle.blastem(rom, name, script, startup_ms=2500, cache=False)
n = len([f for f in os.listdir(got) if f.startswith('s') and f.endswith('.png')])
print('BlastEm screenshots: %d (wanted %d)' % (n, scenes))
cmp_dir = os.path.join(oracle.ROOT, 'out', name + '_cmp')
cmd = [oracle.WORKSHOP, '--md-scenes', rom, '--count', str(scenes), '--shots', out, '--meta', os.path.splitext(rom)[0] + '.json', '--out', cmp_dir]
p = subprocess.run(cmd, capture_output=True, text=True, timeout=600)
print(p.stdout + p.stderr)
