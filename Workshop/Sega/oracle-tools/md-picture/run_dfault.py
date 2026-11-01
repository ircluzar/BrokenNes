"""What does BlastEm do when the 68000 double-faults (an address error while it stacks an exception)? Ours stops the processor, as the hardware does.

    python run_dfault.py [-o]

The ROM counts its starts in work RAM ($FF0000: the RAM survives a reset), points the stack at an odd address and takes a TRAP: the address error that follows is itself an address error. Words read back after 4 seconds:
[0] the number of starts (1 = the processor stayed halted, more = the machine restarted), [1] a marker the spin loop after the fault would write if the 68000 ran on. Needs the blastem lock; -o: ours only.
"""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import asm68k, oracle

SRC = '''
    addq.w #1,($FF0000).l
    move.l #$FF0001,sp
    trap #0
    move.w #$7777,($FF0002).l
_spin: bra.s _spin
handler: rte
'''

if __name__ == '__main__':
    rom = asm68k.build_rom(SRC, title='DOUBLE FAULT', vectors={32: 'handler'})
    os.makedirs(os.path.join(oracle.ROOT, 'roms'), exist_ok=True)
    path = os.path.join(oracle.ROOT, 'roms', 'dfault.bin')
    open(path, 'wb').write(rom)
    if '-o' in sys.argv:
        out = os.path.join(oracle.ROOT, 'out', 'dfault_ours.ram')
        os.makedirs(os.path.dirname(out), exist_ok=True)
        oracle.ours(path, 240, None, ram=out)
        o = oracle.ram_words(out); b = None
    else:
        out = os.path.join(oracle.ROOT, 'out', 'dfault_ours.ram')
        os.makedirs(os.path.dirname(out), exist_ok=True)
        oracle.ours(path, 240, None, ram=out)
        o = oracle.ram_words(out)
        d = oracle.blastem(path, 'dfault', 'w2000,snap,w2000,snap,w2000,snap', cache=False)
        b = [oracle.state_ram(os.path.join(d, n)) for n in sorted(f for f in os.listdir(d) if f.endswith('.state'))]
    print('ours   [%d, %04X]' % (o[0], o[1]))
    if b: print('BlastEm after 2, 4, 6 s: ' + ', '.join('[%d, %04X]' % (w[0], w[1]) for w in b))
