"""Wave-5 probes of Track B4b: what one kind of 68000 bus cycle costs, in master clocks, on the oracle and on ours.

body_rate_vint: a loop of `per` copies of one instruction, then addq.l #1,(a0) and bra.s, with the lap counter sampled by the vertical interrupt (the method of probes_b4a.write_rate_vint: the handler copies the lap
counter to $FF0008 and counts the frame in $FF0004, at frame 50 it also stores it in $FF000C, so the rate is exact to a lap in about 190,000). The instruction under test decides what the loop measures: `nop`
(the 68000 and the DRAM refresh alone), a write or read of work RAM, a read of the VDP's status port (no FIFO), a write of the VDP's data port (the FIFO).
"""
import probes
from probes import prologue
from probes_b4a import NL, _lines

BODIES = {
    'nop': 'nop',
    'moveq': 'moveq #1,d1',
    'ramw': 'move.w d0,($FF0100).l',
    'ramr': 'move.w ($FF0100).l,d1',
    'ramwl': 'move.l d0,($FF0100).l',
    'ctrlr': 'move.w (a2),d1',
    'hvr': 'move.w (a1),d1',
    'vdpw': 'move.w d0,(a3)',
    'vdpwl': 'move.l d0,(a3)',
    'ctrlw': 'move.w d0,(a4)',
    'ctrlr_n1': 'move.w (a2),d1;nop',
    'ctrlr_n3': 'move.w (a2),d1;nop;nop;nop',
    'ctrlrl': 'move.l (a2),d1',
    'vdpw_n1': 'move.w d0,(a3);nop',
    'ctrlr_ramw': 'move.w (a2),d1;move.w d0,($FF0100).l',
    'ramw_ctrlr': 'move.w d0,($FF0100).l;move.w (a2),d1',
    'ctrlr_moveq': 'move.w (a2),d1;moveq #1,d2',
    'tstctrl': 'tst.w (a2)',
    'cmpctrl': 'cmp.w (a2),d1',
    # wave 5: Strider's tile upload (the loop at $8CC0 of its level load: control words and long data writes alternating), VRAM address $0000 each time
    'strider': 'move.w d3,(a4);move.w d4,(a4);move.l (a5),(a3);move.w d3,(a4);move.w d4,(a4);move.l (a5),(a3)',
    'ctrl2l': 'move.w d3,(a4);move.w d4,(a4);move.l (a5),(a3)',
    # wave 5: the instruction mix of Strider's fade routine at $85F4 (shifts by 8 and 3, andi, add, an indexed byte read, bclr): pure CPU, no VDP
    'fade': 'move.w #$0123,d4;move.w #$0456,d5;lsr.w #8,d4;lsr.w #3,d5;andi.w #$FFE0,d5;add.w d5,d4;move.b 0(a5,d4.w),d4;bclr #7,d4',
    'lsr': 'lsr.w #8,d4;lsr.w #3,d5',
    'idx': 'move.b 0(a5,d4.w),d4',
    'bclr': 'bclr #7,d4',
}

# the 68000's cycles of one body instruction (instruction timing without waits), for the readers of the results
CYCLES = {'ctrlr_n1': 12, 'ctrlr_n3': 20, 'ctrlrl': 12, 'vdpw_n1': 12, 'ctrlr_ramw': 24, 'ramw_ctrlr': 24, 'ctrlr_moveq': 12, 'tstctrl': 8, 'cmpctrl': 8, 'nop': 4, 'moveq': 4, 'ramw': 16, 'ramr': 16, 'ramwl': 20, 'ctrlr': 8, 'hvr': 8, 'vdpw': 8, 'vdpwl': 12, 'ctrlw': 8}


def body_rate_vint(body, per=16, h40=True, display=True, v30=False, cmd='$C0000000'):
    ins = BODIES[body]
    s = prologue(h40, display, regs={1: (0x74 if display else 0x34) | (0x08 if v30 else 0), 0: 0x04, 15: 2})
    s += _lines('    lea ($FF0000).l,a0', '    move.l #0,(a0)', '    move.w #0,($FF0004).l', '    move.l #%s,(a4)' % cmd, '    moveq #0,d0', '    lea ($FF0200).l,a5', '    move.w #$4000,d3', '    moveq #0,d4', '    move.w #$2000,sr', 'main:')
    s += ''.join('    %s' % x + NL for x in ins.split(';')) * per
    s += _lines('    addq.l #1,(a0)', '    bra.w main', '_spin: bra.s _spin', 'handler:', '    addq.w #1,($FF0004).l', '    cmpi.w #50,($FF0004).l', '    bne.s h1', '    move.l (a0),($FF000C).l', 'h1: move.l (a0),($FF0008).l', '    rte')
    return s
