"""looprate.py ours|theirs body [H40|H32] [on|off]: how long (master clocks) one lap of a piece of real code takes on one machine (the lap-counting method of probes_b4a.write_rate_vint: the vertical interrupt
copies the lap counter, so the rate is exact to a lap in many thousands). The bodies are straight-line copies of the instruction mix of a game's inner loop (Strider's data decompressor at $2059E, the
loop that makes its intro start one frame earlier on BlastEm than here), with the data-dependent branches taken out; none of the bodies' own shift counts changes between laps.
    body: dec (the decompressor's nibble step), decw (the same plus a 32-bit write of the VDP data port per lap; the oracle's RAM image has another layout for it, no result), shifts (just the shift instructions), tbl (indexed byte reads from
    ROM), rom (byte reads from ROM), alu (register arithmetic), vdpw / vdpwl (16 data port writes a lap, word / long: the cost of a VDP data port write, which is what Strider's VRAM clear loops are made of)
For both machines at once and several bodies: looprate_all.ps1 (the oracle part holds the blastem lock).
Registers at the start of main: a0 = $FF0000 (lap counter), a3 = VDP data port, a4 = control port, a5 = $0400 (ROM table), a6 = $0800 (ROM data), d5 = $A55A, d6 = 16, d3 = 8, d4 = 0."""
import sys
import b4a
from probes import prologue
from probes_b4a import NL, _lines

DEC = ['move.w d6,d7', 'subq.w #8,d7', 'move.w d5,d1', 'lsr.w d7,d1', 'cmpi.b #$fc,d1', 'andi.w #$ff,d1', 'add.w d1,d1', 'move.b (a5,d1.w),d0', 'ext.w d0', 'cmpi.w #9,d6',
       'asl.w #8,d5', 'move.b (a6),d5', 'move.b 1(a5,d1.w),d1', 'move.w d1,d0', 'andi.w #$f,d1', 'andi.w #$f0,d0', 'lsr.w #4,d0', 'lsl.l #4,d4', 'or.b d1,d4', 'subq.w #1,d3']
BODIES = {
    'dec': DEC,
    'decw': DEC + ['move.l d4,(a3)'],
    'shifts': ['lsr.w d7,d1', 'asl.w #8,d5', 'lsr.w #4,d0', 'lsl.l #4,d4'],
    'tbl': ['move.b (a5,d1.w),d0', 'move.b 1(a5,d1.w),d1'],
    'rom': ['move.b (a6),d5', 'move.b 1(a6),d1'],
    'vdpw': ['move.w d0,(a3)', 'move.w d0,(a3)', 'move.w d0,(a3)', 'move.w d0,(a3)'],              # 16 data port word writes a lap (the blanked VRAM clear loops of games)
    'vdpwl': ['move.l d0,(a3)', 'move.l d0,(a3)', 'move.l d0,(a3)', 'move.l d0,(a3)'],            # 16 long writes (Strider's loop is move.l d1,(a1) then dbf)
    'alu': ['andi.w #$ff,d1', 'add.w d1,d1', 'ext.w d0', 'cmpi.w #9,d6', 'andi.w #$f,d1', 'andi.w #$f0,d0', 'or.b d1,d4', 'subq.w #1,d3', 'move.w d6,d7', 'move.w d1,d0'],
}


def probe(body, h40=True, display=True, per=4):
    s = prologue(h40, display, regs={1: 0x74 if display else 0x34, 0: 0x04, 15: 2})
    s += _lines('    lea ($FF0000).l,a0', '    move.l #0,(a0)', '    move.w #0,($FF0004).l', '    move.l #$40000000,(a4)', '    moveq #0,d0', '    lea ($400).l,a5', '    lea ($800).l,a6',
                '    move.w #$A55A,d5', '    move.w #16,d6', '    move.w #8,d7', '    moveq #0,d4', '    moveq #8,d3', '    moveq #0,d1', '    move.w #$2000,sr', 'main:')
    s += ''.join('    %s' % x + NL for x in BODIES[body]) * per
    s += _lines('    addq.l #1,(a0)', '    bra.w main', '_spin: bra.s _spin', 'handler:', '    addq.w #1,($FF0004).l', '    cmpi.w #50,($FF0004).l', '    bne.s h1', '    move.l (a0),($FF000C).l', 'h1: move.l (a0),($FF0008).l', '    rte')
    return s


if __name__ == '__main__':
    which, body = sys.argv[1:3]
    w = sys.argv[3] if len(sys.argv) > 3 else 'H40'
    d = sys.argv[4] if len(sys.argv) > 4 else 'on'
    per = 4
    name = 'lr_%s_%s_%s' % (body, w, d)
    src = probe(body, w == 'H40', d == 'on', per)
    if which == 'ours':
        import os
        ss = os.environ.get('RATE_SLOTSET')
        ram = b4a.ram_ours(name, src, {30: 'handler'}, frames=900, extra=(('--slot-set', ss) if ss else ()))
    else:
        ram = b4a.ram_theirs(name, src, {30: 'handler'}, wait_ms=9000, cache=False)
    if ram is None:
        print(name, which, 'no state'); sys.exit(1)
    best = None
    for base in range(0, 12):
        now = ram[base] << 16 | ram[base + 1]; fr = ram[base + 2]; lv = ram[base + 4] << 16 | ram[base + 5]; l50 = ram[base + 6] << 16 | ram[base + 7]
        if 60 < fr < 5000 and ram[base + 3] == 0 and now >= lv >= l50 > 0 and now - lv < 100000:
            best = (base, fr, lv, l50)
            break
    if best is None:
        print(name, which, 'no consistent layout in', [hex(x) for x in ram[:20]]); sys.exit(1)
    base, frames, laps_vint, laps50 = best
    rate = (laps_vint - laps50) / (frames - 50)
    print('%s %-6s %-6s: frames %d laps/frame %.5f -> %.4f master clocks per lap' % (name, which, body, frames, rate, 262 * 3420 / rate))
