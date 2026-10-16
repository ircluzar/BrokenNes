"""run_long.py <rom file name in the library> <seconds> : BlastEm runs the game without input for that long, is paused and gives its picture and its quick-save RAM (be_shots, out\\long_<n>.state-ram): did the game go on?
Needs the blastem lock."""
import os, sys, shutil
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import oracle
name = sys.argv[1]; secs = float(sys.argv[2])
rom = os.path.join('X:\\EMULATION\\Sega Genesis', name)
d = oracle.blastem(rom, 'long', 'w%d,pause,k:P,adv1,w1250,snap' % int(secs * 1000), cache=False)
print(sorted(os.listdir(d)))
st = os.path.join(d, 's000.state')
if os.path.exists(st):
    w = oracle.state_ram(st)
    print('RAM words 0..8', ' '.join('%04X' % x for x in w[:8]), 'cell $E03E', '%04X%04X' % (w[0xE03E // 2], w[0xE040 // 2]))
