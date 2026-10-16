"""hintvb.py [-o] r10 vline [e|l] [r10 vline [e|l] ...]: does a horizontal interrupt come at once when the enable bit (R0 bit 4) is set after the line counter has reached zero while it was disabled, on this emulator and on BlastEm?
B4b's Fatal Rewind probe (run_fr.py variant A) found that BlastEm takes a HINT right after the enable (0 instructions) where ours takes the first one in the next frame's line R10. Here: R10 = r10, HINT disabled, then wait
for the V counter byte to equal `vline` (an NTSC V28 frame has the byte 0x00-0xEA for lines 0-234 and 0xE5-0xFF for lines 235-261), mask 0, bus request round trip as the game does, R0 = $14, and (mode l, the default) R1 = $64
(the vertical interrupt enabled for the first time, as in the game); mode e: the vertical interrupt is enabled and the mask is 0 from the start, so its handler acknowledges one every frame before the enable.
RAM at $FF0100 (words): [0] VINT count, [1] HINT count, [2] the HV counter word read in the first HINT, [4] marker counter, [5] markers executed at the first VINT, [6] markers executed at the first HINT (the six `addq` after
the writes: 0 = the HINT was taken at once). Needs the blastem lock (-o: ours only)."""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import asm68k, oracle
from probes import prologue, sync_vblank_rise

HANDLER = '''
vint:  tst.w ($FF0100).l
    bne.s vdone
    move.w ($FF0108).l,($FF010A).l
vdone: addq.w #1,($FF0100).l
    rte
hint:  tst.w ($FF0102).l
    bne.s hdone
    move.w ($C00008).l,($FF0104).l
    move.w ($FF0108).l,($FF010C).l
hdone: addq.w #1,($FF0102).l
    rte
zbus:
    move.w #$0100,($A11100).l
zw: btst #0,($A11100).l
    bne.s zw
    move.b #$FF,($A01F80).l
    move.b #$00,($A01F81).l
    move.w #$0000,($A11100).l
    rts
'''


def wait_v(target, label):
    return '%(l)s: move.b (a1),d1\n    cmpi.b #$%(t)02X,d1\n    bne.s %(l)s\n' % {'l': label, 't': target}


def program(r10, vline, early=False, rewrite=None):
    s = prologue(True, True, regs={1: 0x64 if early else 0x44, 0: 0x04, 10: r10})
    s += '    clr.l ($FF0100).l\n    clr.l ($FF0104).l\n    clr.l ($FF0108).l\n    clr.l ($FF010C).l\n    move.w #$0100,($A11200).l\n    move.w #%s,sr\n' % ('$2000' if early else '$2700')
    s += sync_vblank_rise() + wait_v(0x10, 'gB')
    if rewrite is not None:
        # mode r<line>: R0 is written again with the enable bit clear at that V byte (after the first line counter zero), then the enable comes at `vline`
        s += wait_v(rewrite, 'gR') + '    move.w #$8004,($C00004).l\n'
    s += wait_v(vline, 'gC')
    s += '    move.w #$2000,sr\n    bsr.w zbus\n    move.w #$8014,($FFC00004).l\n' + ('' if early else '    move.w #$8164,($FFC00004).l\n') + '    jmp target\ntarget:\n'
    s += '    addq.w #1,($FF0108).l\n' * 6
    s += 'nops:\n' + '    nop\n' * 40 + '_spin: bra.s _spin\n' + HANDLER
    return s


def run(r10, vline, early, ours_only, rewrite=None):
    rom = asm68k.build_rom(program(r10, vline, early, rewrite), title='HVB %02X %02X' % (r10, vline), vectors={28: 'hint', 30: 'vint'})
    name = 'hvb_%02X_%02X%s%s' % (r10, vline, 'e' if early else '', '' if rewrite is None else 'r%02X' % rewrite)
    os.makedirs(os.path.join(oracle.ROOT, 'roms'), exist_ok=True)
    path = os.path.join(oracle.ROOT, 'roms', name + '.bin')
    open(path, 'wb').write(rom)
    ram = os.path.join(oracle.ROOT, 'out', name + '_ours.ram')
    os.makedirs(os.path.dirname(ram), exist_ok=True)
    ss = os.environ.get('HVB_SLOTSET')      # experiment settings for our side (--slot-set), e.g. "hintkeep=1"
    oracle.ours(path, 200, None, ram=ram, extra=('--slot-set', ss) if ss else ())
    o = oracle.ram_words(ram)
    base = 0x100 // 2

    def fmt(w):
        return 'VINT %d HINT %d | first HINT: HV %04X, markers %d | first VINT markers %d' % (w[base], w[base + 1], w[base + 2], w[base + 6], w[base + 5])
    res = 'ours    ' + fmt(o)
    if not ours_only:
        d = oracle.blastem(path, name, 'w3000,pause,snap', cache=False)
        st = os.path.join(d, 's000.state')
        res += '\n        BlastEm ' + (fmt(oracle.state_ram(st)) if os.path.exists(st) else 'no state')
    return res


if __name__ == '__main__':
    args = [a for a in sys.argv[1:] if not a.startswith('-')]
    i = 0
    while i < len(args):
        r10, vline = int(args[i], 16), int(args[i + 1], 16)
        has_mode = i + 2 < len(args) and (args[i + 2] in ('e', 'l') or args[i + 2].startswith('r'))
        mode = args[i + 2] if has_mode else 'l'
        rewrite = int(mode[1:], 16) if mode.startswith('r') else None
        print('R10=%02X enable at V byte %02X mode %s:' % (r10, vline, mode), run(r10, vline, mode == 'e' or rewrite is not None, '-o' in sys.argv, rewrite))
        sys.stdout.flush()
        i += 3 if has_mode else 2
