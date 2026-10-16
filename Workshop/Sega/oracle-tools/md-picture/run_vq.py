"""When does a vertical interrupt that came due while it was disabled get taken once it is enabled? Fatal Rewind enables it late in a VBlank after several VBlanks in which it was disabled, with no status read in that VBlank;
BlastEm's game runs on, ours takes the interrupt one instruction after the write (the handler jumps through an empty cell). Variants (all: R1 = $44, the mask at 0 from the last moment, enable at line 240 with a write of $8164):
    1  the VBlank flag is waited for (status polled), then line 240                      (the earlier probes' shape)
    2  the VBlank flag is waited for, line 16 of the next frame is waited for (V counter), then line 240 of that frame: no status read in the VBlank of the enable (Fatal Rewind's shape)
    3  as 2, but the VBlank flag is waited for again before line 240 (status polled in that VBlank)
    4  as 2, but one status read right before the enable
    5  as 2, but the V counter is polled from line 232 on (all the VBlank)
    6  as 2 with three frames held (V counter only)
RAM words at $FF0100: [0..1] the return address the vertical handler was given, [2] count, [3] marker instructions (six after the enable) run before it. 0 = taken right after the enable, 6 = not taken within them.
python run_vq.py [variants] [-o]. Needs the blastem lock (-o: ours only).
"""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import asm68k, oracle
from probes import prologue, sync_vblank_rise, wait_v

HANDLER = '''
vint:  tst.w ($FF0104).l
    bne.s vdone
    move.l 2(sp),($FF0100).l
    move.w ($FF0108).l,($FF0106).l
vdone: addq.w #1,($FF0104).l
    rte
'''


def program(v):
    s = prologue(True, True, regs={1: 0x44, 0: 0x04, 10: 0xFF})
    s += '    clr.l ($FF0100).l\n    clr.l ($FF0104).l\n    clr.l ($FF0108).l\n    move.w #$2700,sr\n'
    if v == '1':
        s += sync_vblank_rise() + wait_v(0xF0, 'g1')
    elif v in '2345':
        s += sync_vblank_rise() + wait_v(0x10, 'g0')
        if v == '3': s += sync_vblank_rise()
        if v == '5': s += wait_v(0xE8, 'g5')
        s += wait_v(0xF0, 'g1')
        if v == '4': s += '    move.w (a2),d6\n'
    elif v == '6':
        s += 'm6: nop\n' + sync_vblank_rise() + wait_v(0x10, 'g0') + sync_vblank_rise() + wait_v(0x10, 'g00') + wait_v(0x10, 'g000')
        s += wait_v(0xF0, 'g1')
    s += '    move.w #$2000,sr\n    move.w #$8164,(a4)\n'
    s += '    addq.w #1,($FF0108).l\n' * 6
    s += '    nop\n' * 40 + '_spin: bra.s _spin\n' + HANDLER
    return s


def run(v, ours_only):
    rom = asm68k.build_rom(program(v), title='VQ %s' % v, vectors={30: 'vint'})
    name = 'vq_%s' % v
    os.makedirs(os.path.join(oracle.ROOT, 'roms'), exist_ok=True)
    path = os.path.join(oracle.ROOT, 'roms', name + '.bin')
    open(path, 'wb').write(rom)
    ram = os.path.join(oracle.ROOT, 'out', name + '_ours.ram')
    os.makedirs(os.path.dirname(ram), exist_ok=True)
    oracle.ours(path, 200, None, ram=ram)
    o = oracle.ram_words(ram)
    base = 0x100 // 2

    def fmt(w):
        return 'ret %04X%04X count %d markers before the interrupt %d' % (w[base], w[base + 1], w[base + 2], w[base + 3])
    res = 'ours    ' + fmt(o)
    if not ours_only:
        d = oracle.blastem(path, name, 'w3000,pause,snap', cache=False)
        st = os.path.join(d, 's000.state')
        res += '\n        BlastEm ' + (fmt(oracle.state_ram(st)) if os.path.exists(st) else 'no state')
    return res


if __name__ == '__main__':
    args = [a for a in sys.argv[1:] if not a.startswith('-')]
    for v in (args[0] if args else '123456'):
        print(v, run(v, '-o' in sys.argv))
        sys.stdout.flush()
