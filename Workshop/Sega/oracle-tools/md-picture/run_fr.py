"""Fatal Rewind's init in a probe: what happens between the write that enables the vertical interrupt (already pending, the mask at 0) and the interrupts? The game's code is
    move.w #$2000,sr ; bsr (Z80 bus request, two writes to the Z80's RAM, release) ; move.w #$8014,($FFC00004).l ; move.w #$8164,($FFC00004).l ; jmp routine
and the routine's fifth instruction stores the handler's address. A horizontal interrupt was enabled and disabled again earlier (a stale pending one: our VDP keeps it, B4a's HINT latch rule); the game's horizontal
handler is `tst.w ($E048).w ; beq.w` (short), the vertical handler jumps through the cell the routine fills in.

    python run_fr.py [variants] [-o]       variants: G (A without the wait for the next frame: the interrupt is newly pending), H (the frame is waited out, then the VBlank flag is waited for again), A (the reduced game order, no stale HINT), E (A with a stale pending HINT: enabled for a few lines with R10 = 0, then disabled), F (E, the HINT handler is a bare RTE)

RAM words at $FF0100: [0..1] the return address the VINT handler was given, [2] VINT count, [3] markers ($FF0108 += 1, six after the JMP) at the VINT, [5] markers at the first HINT, [6] HINT count, [7] markers
when the HINT handler returned. Needs the blastem lock (-o: ours only).
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
hint:  tst.w ($FF010C).l
    bne.s hdone
    move.w ($FF0108).l,($FF010A).l
hdone: addq.w #1,($FF010C).l
    tst.w ($FF010E).l
    beq.s hret
    nop
hret:  move.w ($FF0108).l,($FF010E).l
    rte
hint2: tst.w ($FF010C).l
    bne.s h2done
    move.w ($FF0108).l,($FF010A).l
h2done: addq.w #1,($FF010C).l
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


def program(v):
    s = prologue(True, True, regs={1: 0x44, 0: 0x04, 10: 0x00 if v in 'EFM' else 0x10})
    s += '    clr.l ($FF0100).l\n    clr.l ($FF0104).l\n    clr.l ($FF0108).l\n    clr.l ($FF010C).l\n    move.w #$0100,($A11200).l\n    move.w #$2700,sr\n'
    s += sync_vblank_rise() + ('' if v in 'GH' else wait_v(0x10, 'gB'))
    if v == 'H': s += wait_v(0x10, 'gB') + sync_vblank_rise()
    if v in 'EFM':
        s += '    move.w #$8014,(a4)\n' + '    nop\n' * 200 + '    move.w #$8004,(a4)\n'      # a few lines of an enabled horizontal interrupt (every line) with the mask at 7: it stays pending
        s += '    move.w #$8A10,(a4)\n'
    s += wait_v(0xF0, 'gC')              # late in the VBlank in which the vertical interrupt came due; it stays disabled until the write below
    s += '    move.w #$2000,sr\n' + ('' if v == 'I' else '    bsr.w zbus\n')
    s += ('' if v == 'J' else '    move.w #$8014,($FFC00004).l\n') + ('    nop\n' * 60 if v == 'M' else '') + '    move.w #$8164,($FFC00004).l\n' + ('target:\n' if v == 'K' else '    jmp target\ntarget:\n')
    s += '    addq.w #1,($FF0108).l\n' * 6
    s += 'nops:\n' + '    nop\n' * (300 if v == 'L' else 40) + '_spin: bra.s _spin\n' + HANDLER
    return s


def run(v, ours_only):
    vec = {28: 'hint2' if v == 'F' else 'hint', 30: 'vint'}
    rom = asm68k.build_rom(program(v), title='FR %s' % v, vectors=vec)
    name = 'fr_%s' % v
    os.makedirs(os.path.join(oracle.ROOT, 'roms'), exist_ok=True)
    path = os.path.join(oracle.ROOT, 'roms', name + '.bin')
    open(path, 'wb').write(rom)
    ram = os.path.join(oracle.ROOT, 'out', name + '_ours.ram')
    os.makedirs(os.path.dirname(ram), exist_ok=True)
    oracle.ours(path, 200, None, ram=ram)
    o = oracle.ram_words(ram)
    base = 0x100 // 2

    def fmt(w):
        return 'VINT ret %04X%04X count %d markers-at-vint %d | HINT count %d markers-at-hint %d markers-at-hint-return %d' % (w[base], w[base + 1], w[base + 2], w[base + 3], w[base + 6], w[base + 5], w[base + 7])
    res = 'ours    ' + fmt(o)
    if not ours_only:
        d = oracle.blastem(path, name, 'w3000,pause,snap', cache=False)
        st = os.path.join(d, 's000.state')
        res += '\n        BlastEm ' + (fmt(oracle.state_ram(st)) if os.path.exists(st) else 'no state')
    return res


if __name__ == '__main__':
    args = [a for a in sys.argv[1:] if not a.startswith('-')]
    for v in (args[0] if args else 'AEF'):
        print(v, run(v, '-o' in sys.argv))
        sys.stdout.flush()
