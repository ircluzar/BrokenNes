"""Where in the frame does BlastEm's controller change? (wave 5)

The roster replays set the pad at line 232 of the frame BlastEm was paused in and compare pictures; B4b found that line 232 is wrong (Streets of Rage and Ecco become pixel identical with 224-231, Comix Zone wants
230-231, Strider 225) and that no single line suits every game. This probe asks BlastEm directly: a program polls controller port 1 (TH high: C and B on bits 5 and 4) in a tight loop (about 30 cycles a lap) and
stores the HV counter the moment the value changes, six changes at most: RAM words at $FF0100: [0] HV counter at the first change, [1] the port value then, [2], [3] the second change, ... The script is the roster's:
BlastEm is paused (F7), a key goes down while it is paused, N frame advances, the key goes up, advances, a state is saved. The V counter at the first change is the line at which the oracle's CPU first saw the key.

python run_padlat.py [variant] [-o]   (-o: ours only). Needs the blastem lock.
  variants: 'c6'   pause, hold C for 6 advances, release, 2 advances   (the roster's tap)
            'c1'   hold C for 1 advance
            'c6x'  as c6 with a longer wait (400 ms) between the key down and the first advance
"""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import asm68k, oracle
from probes import prologue

PROGRAM = '''
    move.b #$40,($A10009).l
    move.b #$40,($A10003).l
    moveq #0,d5
    lea ($FF0100).l,a5
    move.b ($A10003).l,d4
again:
    move.b ($A10003).l,d0
    cmp.b d4,d0
    beq.s again
    move.w ($C00008).l,d1
    move.w d1,(a5)+
    moveq #0,d2
    move.b d0,d2
    move.w d2,(a5)+
    move.b d0,d4
    addq.w #1,d5
    cmp.w #6,d5
    bne.s again
_spin: bra.s _spin
'''

SCRIPTS = {
    'c6': 'w2500,pause,adv1,w300,d:C,adv6,u:C,adv2,w300,snap',
    'c1': 'w2500,pause,adv1,w300,d:C,adv1,u:C,adv2,w300,snap',
    'c6x': 'w2500,pause,adv1,w300,d:C,w400,adv6,u:C,w400,adv2,w300,snap',
}


def program():
    return prologue(True, True) + '    clr.l ($FF0100).l\n    clr.l ($FF0104).l\n    move.w #$2700,sr\n' + PROGRAM


def decode(words):
    out = []
    for k in range(6):
        hv, val = words[0x100 // 2 + 2 * k], words[0x100 // 2 + 2 * k + 1]
        if hv == 0 and val == 0: break
        out.append('V %02X (%3d) H %02X value %02X' % (hv >> 8, hv >> 8, hv & 0xFF, val))
    return out


def run(variant, ours_only):
    rom = asm68k.build_rom(program(), title='PADLAT')
    path = os.path.join(oracle.ROOT, 'roms', 'padlat.bin')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    open(path, 'wb').write(rom)
    res = []
    if not ours_only:
        d = oracle.blastem(path, 'padlat_' + variant, SCRIPTS[variant], cache=False)
        st = os.path.join(d, 's000.state')
        if os.path.exists(st):
            res.append('BlastEm: ' + ' | '.join(decode(oracle.state_ram(st))))
        else:
            res.append('BlastEm: no state')
    return '\n'.join(res)


if __name__ == '__main__':
    args = [a for a in sys.argv[1:] if not a.startswith('-')]
    for v in (args or ['c6']):
        print(v, run(v, '-o' in sys.argv))
        sys.stdout.flush()
