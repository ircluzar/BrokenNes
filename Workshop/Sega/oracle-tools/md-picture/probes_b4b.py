"""Probe ROM sources of Track B4b (interrupt pending state of the Genesis VDP). Same conventions as probes.py: results in work RAM from $FF0100 (words), a probe ends in a spin loop so BlastEm can be paused after it.

Question (Fatal Rewind / Killing Game Show): the game switches the display on with the vertical interrupt DISABLED in the VDP (reg 1 = $44), uploads for seven frames, then writes reg 1 = $64 with the 68000's interrupt
mask at 0. Is the vertical interrupt that came due while it was disabled still pending (ours: yes, until the 68000 acknowledges it), so that it fires the moment the enable bit is written?

vint_enable(variant) reports, in words at $FF0100: [0] the interrupt count when the probe ends (handler at vector 30), [1] the count 40 nops after the enable, [2] the status read before the enable (variant B/E), [3] a second status read, [4] the count 40 nops after the VDP was switched off again.
Variants (what happens between "the VBlank has begun / ended" and "the enable bit is written"):
  A  VBlank just begun (status bit 3 rose), nothing read, enable at once
  B  VBlank just begun, the status register read twice, enable
  D  display period (status bit 3 fell, 40 lines of a loop later: far from VBlank), nothing read, enable
  E  display period, the status register read twice, enable
  F  as D but the enable bit written at the VDP only after the 68000 mask was raised and lowered again (the mask makes no difference to the pending state)
"""
from probes import prologue, sync_vblank_fall, sync_vblank_rise, SPIN, wait_v

HANDLER = '''
vint:  addq.w #1,($FF0100).l
    rte
hint:  rte
'''


def vint_enable(variant='A', h40=True, wait_lines=40):
    s = prologue(h40, True)           # reg 1 = $54: display on, DMA on, no vertical interrupt
    s += '    clr.w ($FF0100).l\n    clr.w ($FF0102).l\n    clr.w ($FF0104).l\n    clr.w ($FF0106).l\n'
    s += '    move.w #$2000,sr\n'     # interrupts allowed in the 68000; the VDP does not ask for one
    if variant.startswith('L'):
        # enabled late in the same VBlank: the VBlank began (status bit 3 rose), then the V counter is polled up to the byte given in hex (L F0, L FA: lines 245 and 255 of a 262-line frame) and the enable bit is written there
        s += sync_vblank_rise() + wait_v(int(variant[1:], 16), 'gC')
    elif variant.startswith('G'):
        # held for N whole frames: the VBlank begins N times while the vertical interrupt is disabled (no status register reads: the V counter is polled), then the enable bit is written in the display period
        frames = int(variant[1:])
        s += NL_.join(['    move.w #%d,d7' % (frames - 1), 'g0:']) + NL_ + wait_v(0xE8, 'gA') + wait_v(0x20, 'gB') + '    dbf d7,g0' + NL_
    elif variant in ('A', 'B'):
        s += sync_vblank_rise()
    else:
        s += sync_vblank_fall()
        # a loop that lasts about wait_lines lines (488 cycles each)
        s += '    move.w #%d,d7\nwl: dbf d7,wl\n' % (wait_lines * 488 // 14)
    if variant in ('B', 'E'):
        s += '    move.w (a2),d6\n    move.w d6,($FF0104).l\n    move.w (a2),d6\n    move.w d6,($FF0106).l\n'
    if variant == 'F':
        s += '    move.w #$2700,sr\n    nop\n    nop\n    move.w #$2000,sr\n'
    s += '    move.w #$8174,(a4)\n'   # reg 1: vertical interrupt enabled
    s += '    nop\n' * 40
    s += '    move.w ($FF0100).l,($FF0102).l\n'
    s += '    move.w #$8154,(a4)\n'   # off again so that no later VBlank adds to the count
    s += '    nop\n' * 40
    s += '    move.w ($FF0100).l,($FF0108).l\n' + SPIN + HANDLER
    return s


def vint_pend(variant='P', h40=True):
    """Fatal Rewind's sequence: the vertical interrupt is ENABLED in the VDP and the 68000's mask is 7, so the interrupt that the VBlank raises stays pending; late in the same VBlank the game writes VDP registers
    and then lowers the mask. Is the interrupt still taken? RAM words: [0] the count after the mask was lowered (40 nops), [1] the count at the end.
      P  nothing between (control: pending, then the mask is lowered)
      M  the enable bit is cleared and set again (R1 = $54, nops, $74) while the interrupt is pending
      N  R0 is rewritten ($14: horizontal interrupt on) and R1 with the same value ($74)
      O  the status register is read six times
      Q  as P but the mask is lowered at once after the VBlank began (no waiting)"""
    s = prologue(h40, True, regs={1: 0x74, 0: 0x04})        # reg 1 = $74: display on, vertical interrupt on; the 68000 starts with mask 7
    s += '    clr.w ($FF0100).l\n    clr.w ($FF0102).l\n'
    s += sync_vblank_rise()
    if variant != 'Q': s += wait_v(0xE8, 'gA')
    if variant == 'M': s += '    move.w #$8154,(a4)\n    nop\n    nop\n    nop\n    nop\n    move.w #$8174,(a4)\n'
    if variant == 'N': s += '    move.w #$8014,(a4)\n    move.w #$8174,(a4)\n'
    if variant == 'O': s += '    move.w (a2),d6\n' * 6
    if variant != 'Q': s += wait_v(0xF8, 'gD')
    s += '    move.w #$2000,sr\n' + '    nop\n' * 40
    s += '    move.w ($FF0100).l,($FF0102).l\n    move.w #$2700,sr\n' + SPIN + HANDLER
    return s


def probes():
    return {v: vint_enable(v) for v in 'ABDEF'}


# ---------------------------------------------------------------------------------------------------------------------
#  How many instructions run after the interrupt mask is lowered before a pending vertical interrupt is taken
# ---------------------------------------------------------------------------------------------------------------------

# name -> (the instruction that lowers the mask, the instruction repeated after it); d6 = $2000, d2 = $FFFF, d3 = $7FFF0000
SR_VARIANTS = {
    'move_imm_addq': ('move.w #$2000,sr', 'addq.w #1,d5'),
    'move_reg_addq': ('move.w d6,sr', 'addq.w #1,d5'),
    'andi_addq': ('andi.w #$F8FF,sr', 'addq.w #1,d5'),
    'move_imm_nop': ('move.w #$2000,sr', 'nop'),
    'move_imm_mulu': ('move.w #$2000,sr', 'mulu d2,d3'),
    'move_imm_movem': ('move.w #$2000,sr', 'movem.l d0-d3,(a6)'),
}


def sr_delay(variants=None, frames=30, h40=True, run=6):
    """For each variant, `frames` times: with the 68000 mask at 7 wait until the vertical interrupt is pending (status bit 7), lower the mask with the variant's first instruction and run `run` copies of its second;
    the handler logs the return address it was given (the first instruction that was NOT executed). Labels v<i>m (the mask instruction), v<i>r<k> (copy k), v<i>e (after): the logged address says after how many
    copies the interrupt was taken (0 = right after the mask instruction). RAM: variant i, frame f: a long at $FF0100 + (i * frames + f) * 4."""
    variants = variants or list(SR_VARIANTS)
    s = prologue(h40, True, regs={1: 0x74, 0: 0x04})
    s += NL_.join(['    lea ($FF0100).l,a0', '    lea ($FF8000).l,a6', '    move.l #$7FFF0000,d3', '    move.w #$FFFF,d2', '    move.w #$2000,d6', '    moveq #1,d5']) + NL_
    for i, name in enumerate(variants):
        mask_ins, ins = SR_VARIANTS[name]
        s += NL_.join(['    move.w #%d,d7' % (frames - 1), 'f%d:' % i, '    move.w #$2700,sr', 'w%d: move.w (a2),d1' % i, '    btst #7,d1', '    beq.s w%d' % i, '    moveq #0,d0',
                       'v%dm: %s' % (i, mask_ins)]) + NL_
        for k in range(run):
            s += 'v%dr%d: %s' % (i, k, ins) + NL_
        s += NL_.join(['v%de: nop' % i, '    dbf d7,f%d' % i]) + NL_
    s += NL_.join(['_spin: bra.s _spin', 'handler:', '    move.l 2(sp),(a0)+', '    rte']) + NL_
    return s


NL_ = '\n'


# ---------------------------------------------------------------------------------------------------------------------
#  After which instruction of a loop is a horizontal interrupt taken? (return address histogram)
# ---------------------------------------------------------------------------------------------------------------------

RET_LOOPS = {
    'nop': ['nop'],
    'tst_abs': ['tst.b ($8000).w'],                       # the wait loop of Streets of Rage's intro: tst.b ($FA00).w / bne.s back (the branch costs what bra.s costs)
    'tst_abs2': ['tst.b ($8000).w', 'tst.b ($8000).w'],
    'move_b_abs': ['move.b d1,($8000).w'],
    'move': ['move.w (a6),d1', 'move.w (a6),d1'],
    'addq_reg': ['addq.w #1,d1'],
    'mulu': ['mulu d2,d3'],
    'divu': ['divu d4,d3'],
    'clr_l': ['clr.l (a6)'],
    'add_rmw': ['add.l d3,(a6)'],
    'jsr_rts': ['jsr sub1'],
    'movem_out': ['movem.l d0-d7,(a6)'],
    'movem_in': ['movem.l (a6),d0-d7'],
    'movem_short': ['movem.l d0-d1,(a6)'],
    'lsl8': ['lsl.l #8,d5'],
    'move_l_mem': ['move.l (a6),(a5)'],
    'pea': ['pea (a6)', 'addq.l #4,sp'],
    'btst_reg': ['btst #3,d1'],
    'cmp_mem': ['cmp.w (a6),d1'],
}


def timing_run(body, n1, n2, h40=True):
    """How long does an instruction take on the oracle: the V counter (one line is 3420 master clocks) read (a) before, (b) after n1 copies of the body and (c) after n2 more, straight-line code, interrupts and DMA off, display off.
    RAM words from $FF0000: V a, b, c. (c - b) - (b - a) lines are n2 - n1 copies of the body and nothing else (the reads' own cost cancels); n1 and n2 are chosen by the caller for about 40 and 160 lines."""
    s = prologue(h40, False, regs={0: 0x04, 1: 0x14, 10: 0xFF})
    s += NL_.join(['    lea ($FF0000).l,a0', '    lea ($C00008).l,a1', '    lea ($FF8000).l,a6', '    lea ($FF8100).l,a5', '    move.l #$7FFF0000,d3', '    move.w #$FFFF,d2', '    move.w #7,d4', '    moveq #0,d1',
                   '    move.w #$2700,sr', '    move.b (a1),d6', 'tw: move.b (a1),d7', '    cmp.b d6,d7', '    beq.s tw', '    move.b (a1),d6']) + NL_      # start at a line boundary (within a few cycles)
    body_text = ''.join('    %s' % ins + NL_ for ins in RET_LOOPS[body] if not ins.startswith('bra'))
    s += '    move.w d6,(a0)+' + NL_
    s += body_text * n1
    s += '    move.b (a1),d6' + NL_ + '    move.w d6,(a0)+' + NL_
    s += body_text * n2
    s += '    move.b (a1),d6' + NL_ + '    move.w d6,(a0)+' + NL_ + '_spin: bra.s _spin' + NL_ + 'sub1: rts' + NL_
    return s


def ret_hist(body, n=4000, h40=True):
    """HINT every line (R10 = 0); the loop is `body` and a BRA.S (labels i0, i1, ... on the instructions, bra on the branch); the handler stores the return address of the interrupt it was given (the first instruction NOT executed);
    RAM: the count at $FF0000, the long addresses from $FF0004."""
    s = prologue(h40, True, regs={0: 0x14, 1: 0x44, 10: 0})
    s += NL_.join(['    lea ($FF0004).l,a0', '    lea ($FF8000).l,a6', '    lea ($FF8100).l,a5', '    move.l #$7FFF0000,d3', '    move.w #$FFFF,d2', '    move.w #7,d4', '    moveq #0,d1', '    move.w #0,($FF0000).l',
                   '    move.w #$2000,sr', 'main:']) + NL_
    for i, ins in enumerate(RET_LOOPS[body]):
        s += 'i%d: %s\n' % (i, ins)
    s += NL_.join(['bra: bra.s main', '_spin: bra.s _spin', 'sub1: rts', 'hint:', '    cmpi.w #%d,($FF0000).l' % n, '    bcc.s done', '    move.l 2(sp),(a0)+', '    addq.w #1,($FF0000).l', 'done:', '    rte']) + NL_
    return s
