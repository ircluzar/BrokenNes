"""stallsweep.py build|ours|theirs WORKDIR [slot-set ...]: master clocks a lap of N nops + addq.l #1,(a0) + bra.w (n kinds), and of the same with one HV counter read (v kinds), N in NS, laps counted by the VINT
(looprate's method; the handler's cost is the same on both machines, vintlap.py). The shape of clocks-per-lap against N shows the 68000 stall model: cycle-counted refresh (2 clocks every 128 run) gives a straight line of
slope 4 * 7 * 130/128; a stall that follows the master clock rounds to access boundaries and leaves a ripple; a grid on VDP accesses (BOARD_MD.VdpQuantum) moves only the v kinds (sega-track-b4a.md section 5, 2026-11-28).
build copies the ROMs to WORKDIR/emerald/roms; theirs reads WORKDIR/hv/ss_<kind>.state."""
import sys, os, shutil
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import b4a, oracle
from probes import prologue
from probes_b4a import NL, _lines
NS = list(range(0, 16)) + [16, 20, 24, 28, 32]
KINDS = ['n%d' % n for n in NS] + ['v%d' % n for n in NS]
FRAME = 262 * 3420


def src(kind):
    n = int(kind[1:])
    s = prologue(True, True, regs={1: 0x74, 0: 0x04, 15: 2})
    s += _lines('    lea ($FF0000).l,a0', '    move.l #0,(a0)', '    move.w #0,($FF0004).l', '    lea ($C00008).l,a1', '    move.w #$2000,sr', 'main:')
    s += ('    nop' + NL) * n
    if kind[0] == 'v': s += _lines('    move.b (a1),d2')
    return s + _lines('    addq.l #1,(a0)', '    bra.w main', 'handler:', '    addq.w #1,($FF0004).l', '    cmpi.w #50,($FF0004).l', '    bne.s h1', '    move.l (a0),($FF000C).l',
                      'h1: move.l (a0),($FF0008).l', '    rte')


def decode(ram):
    for base in range(0, 12):
        now = ram[base] << 16 | ram[base + 1]; fr = ram[base + 2]; lv = ram[base + 4] << 16 | ram[base + 5]; l50 = ram[base + 6] << 16 | ram[base + 7]
        if 60 < fr < 5000 and ram[base + 3] == 0 and now >= lv >= l50 > 0 and now - lv < 100000:
            return FRAME * (fr - 50) / (lv - l50)


def ideal(kind):
    """68000 clocks of the lap by the cycle table: nop 4, move.b (a1),d2 8, addq.l #1,(a0) 20, bra.w 10."""
    n = int(kind[1:]); return 4 * n + 30 + (8 if kind[0] == 'v' else 0)


if __name__ == '__main__':
    mode, S = sys.argv[1], sys.argv[2]
    for kind in KINDS:
        name = 'ss_' + kind
        if mode == 'build':
            shutil.copy(b4a.make_rom(name, src(kind), {30: 'handler'}, 'JUE'), os.path.join(S, 'emerald', 'roms', name + '.bin')); continue
        if mode == 'ours':
            r = [decode(b4a.ram_ours(name, src(kind), {30: 'handler'}, frames=600, extra=(('--slot-set', ss) if ss else ()))) for ss in (sys.argv[3:] or [''])]
        else:
            r = [decode(oracle.state_ram(os.path.join(S, 'hv', name + '.state')))]
        c = ideal(kind)
        print('%-4s %3d clk  table %7.1f  ' % (kind, c, c * 7) + '  '.join('%9.3f (%+6.2f)' % (x, x - c * 7) for x in r))
