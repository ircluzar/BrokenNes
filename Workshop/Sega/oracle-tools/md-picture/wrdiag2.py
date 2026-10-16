"""Where does the write_rate probe stop on BlastEm? Sentinels after each stage in RAM $FF0010...; run on ours and theirs (theirs under the lock)."""
import sys, os
import b4a, probes_b4a, oracle
from probes import prologue
from probes_b4a import NL

def src(kind='cram', display=True):
    cmd = {'cram': '$C0000000', 'vram': '$40000000', 'vsram': '$40000010'}[kind]
    s = prologue(True, display, regs={1: (0x74 if display else 0x34), 0: 0x04, 15: 2})
    L = lambda *a: NL.join(a) + NL
    s += L('    lea ($FF0000).l,a0', '    move.l #0,(a0)', '    move.w #0,($FF0004).l', '    move.l #$11111111,($FF0010).l',
           '    move.l #%s,(a4)' % cmd, '    move.l #$22222222,($FF0014).l',
           '    moveq #0,d0', '    move.w #$2000,sr', '    move.l #$33333333,($FF0018).l',
           '    move.w d0,(a3)', '    move.l #$44444444,($FF001C).l')
    s += ('    move.w d0,(a3)' + NL) * 15
    s += L('    move.l #$55555555,($FF0020).l', 'main:')
    s += ('    move.w d0,(a3)' + NL) * 16
    s += L('    addq.l #1,(a0)', '    bra.s main', '_spin: bra.s _spin', 'handler:', '    addq.w #1,($FF0004).l', '    rte')
    return s

if __name__ == '__main__':
    which = sys.argv[1]
    kind = sys.argv[2] if len(sys.argv) > 2 else 'cram'
    display = (sys.argv[3] != 'off') if len(sys.argv) > 3 else True
    name = 'wrdiag2_%s_%s' % (kind, 'on' if display else 'off')
    if which == 'ours':
        ram = b4a.ram_ours(name, src(kind, display), {30: 'handler'}, frames=600)
    else:
        ram = b4a.ram_theirs(name, src(kind, display), {30: 'handler'}, wait_ms=6000, cache=False)
    if ram is None: print(name, which, 'no state')
    else:
        print(name, which, 'laps', ram[0] << 16 | ram[1], 'frames', ram[2], 'sentinels', ['%04X%04X' % (ram[8 + 2 * i], ram[9 + 2 * i]) for i in range(5)])
