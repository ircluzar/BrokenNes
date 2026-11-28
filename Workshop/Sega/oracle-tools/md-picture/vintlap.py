"""vintlap.py build|ours|theirs WORKDIR: laps a frame of 16 nops + addq.l + one V counter read, with the frames counted from the V counter's wrap (not by the VINT), so the
interrupt's own cost can be told apart (sega-track-b4a.md section 5, 2026-11-27). off = VINT disabled and masked; on = VINT on, handler = rte; work = VINT on, looprate's counting handler.
build copies the ROMs to WORKDIR/emerald/roms (for a fleet BlastEm run); theirs reads WORKDIR/hv/vl_<kind>.state."""
import sys, os, shutil
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import b4a, oracle
from probes import prologue
from probes_b4a import NL, _lines
S = sys.argv[2]
def src(kind):
    s = prologue(True, True, regs={1: 0x74 if kind != 'off' else 0x54, 0: 0x04, 15: 2})
    s += _lines('    lea ($FF0000).l,a0', '    move.l #0,(a0)', '    move.l #0,($FF0004).l', '    lea ($C00008).l,a1', '    moveq #0,d3',
                '    move.w #$2700,sr' if kind == 'off' else '    move.w #$2000,sr', 'main:')
    s += ('    nop' + NL) * 16
    s += _lines('    addq.l #1,(a0)', '    move.b (a1),d2', '    cmp.b d3,d2', '    bhs.s nw', '    cmpi.b #$80,d2', '    bhs.s nw',
                '    addq.w #1,($FF0004).l', '    move.l (a0),($FF0008).l', '    cmpi.w #50,($FF0004).l', '    bne.s nw', '    move.l (a0),($FF000C).l',
                'nw: move.b d2,d3', '    bra.w main', 'handler:')
    if kind == 'work': s += _lines('    addq.w #1,($FF0010).l', '    cmpi.w #50,($FF0010).l', '    bne.s h1', '    move.l (a0),($FF0014).l', 'h1: move.l (a0),($FF0018).l')
    return s + _lines('    rte')
def decode(ram):
    for base in range(0, 12):
        now = ram[base] << 16 | ram[base + 1]; fr = ram[base + 2]; lv = ram[base + 4] << 16 | ram[base + 5]; l50 = ram[base + 6] << 16 | ram[base + 7]
        if 60 < fr < 5000 and ram[base + 3] == 0 and now >= lv >= l50 > 0 and now - lv < 100000:
            return (lv - l50) / (fr - 50), fr
for kind in ('off', 'on', 'work'):
    name = 'vl_' + kind
    if sys.argv[1] == 'build':
        shutil.copy(b4a.make_rom(name, src(kind), {30: 'handler'}, 'JUE'), os.path.join(S, 'emerald', 'roms', name + '.bin'))
    elif sys.argv[1] == 'ours':
        r = decode(b4a.ram_ours(name, src(kind), {30: 'handler'}, frames=900)); print(kind, 'ours   ', '%.4f laps/frame' % r[0], r[1])
    else:
        r = decode(oracle.state_ram(os.path.join(S, 'hv', name + '.state'))); print(kind, 'BlastEm', '%.4f laps/frame' % r[0], r[1])
