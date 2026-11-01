"""Does BlastEm 1.0.0 raise address errors at all? One ROM, several odd accesses in a row, each followed by a marker write; the address error vector (3), the illegal instruction vector (4) and the line-F vector (11) count their calls.

    python run_afault.py [-o]

RAM words after 3 seconds ($FF0000...): [0] address error handler calls, [1] illegal instruction handler calls, [2] line-F handler calls, [3..] markers 1 to 5 written by the code after each fault-prone instruction (1: after a word read
from an odd address, 2: after a word write to an odd address, 3: after a long read from an odd address, 4: after a jump to an odd address (the handler resumes at the label after it), 5: after `$FFFF` (line F)).
The handlers resume past the faulting instruction where they can (they set the stacked PC), so every marker is reached when the exception is taken and also when it is not: the counts tell the two apart.
Needs the blastem lock; -o: ours only.
"""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import asm68k, oracle

# the address error frame (group 0): the stack holds the function code word, the access address (long), the instruction register word, the status register and the PC (long) at 10(sp)... the handler
# does not try to resume the faulting instruction: it adds the stacked frame size back and jumps to the next marker through a table (a jump table indexed by the call number), so every case continues
SRC = '''
    lea ($FF0000).l,a5
    move.w #0,(a5)
    move.w #0,2(a5)
    move.w #0,4(a5)
    move.l #$FF8000,sp
    move.w #$2700,sr
    lea ($FF0001).l,a0
    move.w (a0),d0
t1: move.w #$0001,6(a5)
    move.w d0,(a0)
t2: move.w #$0002,8(a5)
    move.l (a0),d0
t3: move.w #$0003,10(a5)
    lea t4_target+1,a1
    move.l a1,-(sp)
    rts
t4_target:
    nop
t4: move.w #$0004,12(a5)
    dc.w $FFFF
t5: move.w #$0005,14(a5)
_spin: bra.s _spin
aeh:
    addq.w #1,(a5)
    move.l #$FF8000,sp
    move.w (a5),d7
    cmp.w #1,d7
    beq t1
    cmp.w #2,d7
    beq t2
    cmp.w #3,d7
    beq t3
    cmp.w #4,d7
    beq t4
    bra t5
illh:
    addq.w #1,2(a5)
    bra t5
linef:
    addq.w #1,4(a5)
    bra t5
'''

if __name__ == '__main__':
    rom = asm68k.build_rom(SRC, title='ADDRESS ERROR', vectors={3: 'aeh', 4: 'illh', 11: 'linef'})
    os.makedirs(os.path.join(oracle.ROOT, 'roms'), exist_ok=True)
    path = os.path.join(oracle.ROOT, 'roms', 'afault.bin')
    open(path, 'wb').write(rom)
    out = os.path.join(oracle.ROOT, 'out', 'afault_ours.ram')
    os.makedirs(os.path.dirname(out), exist_ok=True)
    oracle.ours(path, 120, None, ram=out)
    o = oracle.ram_words(out)
    print('ours   ', ' '.join('%04X' % w for w in o[:8]))
    if '-o' not in sys.argv:
        d = oracle.blastem(path, 'afault', 'w3000,pause,snap', cache=False)
        st = os.path.join(d, 's000.state')
        if os.path.exists(st):
            w = oracle.state_ram(st)
            print('BlastEm', ' '.join('%04X' % x for x in w[:8]))
        else:
            print('BlastEm: no state')
