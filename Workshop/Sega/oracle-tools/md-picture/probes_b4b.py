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
from probes import prologue, sync_vblank_fall, sync_vblank_rise, SPIN

HANDLER = '''
vint:  addq.w #1,($FF0100).l
    rte
hint:  rte
'''


def vint_enable(variant='A', h40=True, wait_lines=40):
    s = prologue(h40, True)           # reg 1 = $54: display on, DMA on, no vertical interrupt
    s += '    clr.w ($FF0100).l\n    clr.w ($FF0102).l\n    clr.w ($FF0104).l\n    clr.w ($FF0106).l\n'
    s += '    move.w #$2000,sr\n'     # interrupts allowed in the 68000; the VDP does not ask for one
    if variant in ('A', 'B'):
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
