"""After the write that enables a VDP interrupt that is already pending, how many instructions run before the 68000 takes it? (Fatal Rewind's init enables the horizontal and then the vertical interrupt and expects
the code that follows (a jump to a routine that stores the handler's address in RAM) to run before the interrupt arrives; ours takes it two instructions later.)

    python run_irqdelay.py [variants] [-o]        variants: V (vertical, enabled late in the VBlank in which it was due), H (horizontal, due while it was disabled, enabled in the display period),
                                                  HV (R0 then R1, as the game does it: the horizontal interrupt pending, the vertical pending, mask 0), default VHJ; J = HV with the mask lowered just before
RAM words: [0..1] the return address the handler was given (the first instruction not executed), [2] the interrupt count. The write is followed by 150 NOPs (about 600 cycles, more than a line): the return
address minus the address after the write, divided by 2, is the number of NOPs that ran before the interrupt was taken. Needs the blastem lock (-o: ours only).
"""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import asm68k, oracle
from probes import prologue, sync_vblank_rise, SPIN, wait_v

NL_ = '\n'
HANDLER = '''
vint:  tst.w ($FF0104).l
    bne.s vdone
    move.l 2(sp),($FF0100).l
vdone: addq.w #1,($FF0104).l
    rte
hint:  tst.w ($FF0104).l
    bne.s hdone
    move.l 2(sp),($FF0100).l
hdone: addq.w #1,($FF0104).l
    rte
'''


def program(kind):
    if kind == 'V':
        s = prologue(True, True, regs={1: 0x54, 0: 0x04})          # display on, no interrupt enabled
        s += '    clr.l ($FF0100).l\n    clr.w ($FF0104).l\n    move.w #$2000,sr\n'
        s += sync_vblank_rise() + wait_v(0xF8, 'gC')              # late in the VBlank in which the vertical interrupt came due
        s += '    move.w #$8174,(a4)\nafter:\n'
    elif kind == 'W':     # as V, but R0 is written first (horizontal interrupt on: R10 = 0xFF so that none is due), then R1, as the game does
        s = prologue(True, True, regs={1: 0x54, 0: 0x04, 10: 0xFF})
        s += '    clr.l ($FF0100).l\n    clr.w ($FF0104).l\n    move.w #$2000,sr\n'
        s += sync_vblank_rise() + wait_v(0xF8, 'gC')
        s += '    move.w #$8014,(a4)\n    move.w #$8174,(a4)\nafter:\n'
    elif kind == 'R':     # as V, but the status register is read all through the VBlank (the game waits for the flag that way) before the enable
        s = prologue(True, True, regs={1: 0x54, 0: 0x04})
        s += '    clr.l ($FF0100).l\n    clr.w ($FF0104).l\n    move.w #$2000,sr\n'
        s += sync_vblank_rise()
        s += NL_.join(['    move.w #540,d7', 'rl: move.w (a2),d6', '    dbf d7,rl']) + NL_
        s += '    move.w #$8174,(a4)\nafter:\n'
    elif kind == 'H':
        s = prologue(True, True, regs={1: 0x54, 0: 0x04, 10: 0})   # R10 = 0: a line interrupt every line while it is enabled
        s += '    clr.l ($FF0100).l\n    clr.w ($FF0104).l\n    move.w #$2000,sr\n'
        s += sync_vblank_rise()
        s += NL_.join(['    move.w #20,d7', 'wl: dbf d7,wl']) + NL_ + wait_v(0x10, 'gC')      # a few lines into the display
        s += '    move.w #$8014,(a4)\nafter:\n'
    else:   # HV (the game's order) with the mask raised until just before, J lowers it just before R0
        s = prologue(True, True, regs={1: 0x54, 0: 0x04, 10: 0})
        s += '    clr.l ($FF0100).l\n    clr.w ($FF0104).l\n    move.w #$2700,sr\n'
        s += sync_vblank_rise()
        s += NL_.join(['    move.w #20,d7', 'wl: dbf d7,wl']) + NL_ + wait_v(0x10, 'gC')
        s += wait_v(0xFA, 'gD')                                                       # late in the next VBlank: both are pending by now if they are kept while disabled
        s += '    move.w #$2000,sr\n    move.w #$8014,(a4)\n    move.w #$8174,(a4)\nafter:\n'
    s += '    nop\n' * 150
    s += SPIN + HANDLER
    return s


def run(kind, ours_only):
    syms = {}
    name = 'irqdelay_' + kind
    rom = asm68k.build_rom(program(kind if kind in ('V', 'H', 'W', 'R') else 'HV'), vectors={30: 'vint', 28: 'hint'}, title=name, symbols_out=syms)
    os.makedirs(os.path.join(oracle.ROOT, 'roms'), exist_ok=True)
    path = os.path.join(oracle.ROOT, 'roms', name + '.bin')
    open(path, 'wb').write(rom)
    out = os.path.join(oracle.ROOT, 'out', name + '_ours.ram')
    os.makedirs(os.path.dirname(out), exist_ok=True)
    oracle.ours(path, 200, None, ram=out)
    o = oracle.ram_words(out)
    b = None
    if not ours_only:
        d = oracle.blastem(path, name, 'w2500,pause,snap', cache=False)
        st = os.path.join(d, 's000.state')
        if os.path.exists(st): b = oracle.state_ram(st)
    return syms['after'], o, b


if __name__ == '__main__':
    args = [a for a in sys.argv[1:] if not a.startswith('-')]
    kinds = args[0].replace(',', ' ').split() if args else ['V', 'H', 'HV']
    for k in kinds:
        after, o, b = run(k, '-o' in sys.argv)
        def show(w):
            if w is None: return 'none'
            ret = (w[0x80] << 16) | w[0x81]
            return 'handler entered %d time(s), returns to %06X = %d NOPs after the write (the write is followed by %06X)' % (w[0x82], ret, (ret - after) // 2, after) if ret else 'no interrupt taken (count %d)' % w[0x82]
        print('%-3s ours    %s' % (k, show(o)))
        if b is not None: print('%-3s BlastEm %s' % ('', show(b)))
