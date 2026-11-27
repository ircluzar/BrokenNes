"""A vertical interrupt that is pending under a mask, and the frame's own VINT: one handler run or two? (Titan Overdrive 1 frame 1007, sega-track-b4a.md section 5, 2026-11-27.)

Each trial: interrupts masked for one whole frame (its VINT stays pending), then at the next VBlank flag a delay of D dbra laps plus V nops, `move #$2000,sr`, and interrupts stay open
until the flag falls (the end of the blank). The handler counts its runs and stores the HV counter at each. If the unmask comes before the frame's own VINT, the stale VINT runs the
handler and the frame's VINT runs it again (two runs); after it, the two have merged (one run). The delay where two becomes one is the VINT time measured from the moment the main loop
sees the VBlank flag, on the same kind of loop Titan Overdrive uses (`btst #3,($C00005).l`).

    python staleint.py ours [vinth40=N ...]     # ours only (slot-set values to compare)
    python staleint.py both                     # ours and BlastEm (hold the oracle lock)
    python staleint.py theirs                   # BlastEm only, then print
Results: one line per (D, V): runs, HV of each run.
"""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import probes, runprobe, asm68k, oracle

DMAX = 24          # dbra laps 0..DMAX-1 (10 cycles a lap)
VMAX = 3           # 0..VMAX-1 extra nops (4 cycles each): with the laps a 2- to 4-cycle grid... the lap is 10, so nops 0..2 give 0, 4, 8
FRAMES = DMAX * VMAX * 2 + 30
RES = 0xFF1000      # 4 words a trial: runs, hv1, hv2, spare


def source():
    lines = probes.prologue(h40=True, display=True, regs={0: 0x04, 1: 0x74, 10: 0xFF})      # VINT on, no HINT
    blocks = []
    for v in range(VMAX):
        blocks.append('''
    moveq #0,d5
dl%(v)d:
    move.w #$2700,sr
    move.w d5,d0
    bsr.w trial_pre
    %(nops)s
    bsr.w trial_post
    addq.w #1,d5
    cmpi.w #%(dmax)d,d5
    bne.w dl%(v)d
''' % {'v': v, 'dmax': DMAX, 'nops': '\n    '.join(['nop'] * v) if v else ''})
    return lines + '''
    lea ($%(res)06X).l,a6
    move.w #$2700,sr
%(blocks)s
done: bra.s done

; masked for a whole frame (its VINT stays pending), then the next frame's VBlank flag, then D laps
trial_pre:
w1: btst #3,($C00005).l
    bne.s w1
w2: btst #3,($C00005).l
    beq.s w2
w3: btst #3,($C00005).l
    bne.s w3
w4: btst #3,($C00005).l
    beq.s w4
dly: dbra d0,dly
    rts

trial_post:
    lea 2(a6),a5
    move.w #$2000,sr
w5: btst #3,($C00005).l
    bne.s w5
    move.w #$2700,sr
    addq.l #8,a6
    rts

vint:
    move.w (a1),d7
    addq.w #1,(a6)
    cmpi.w #2,(a6)
    bhi.s vfull
    move.w d7,(a5)+
vfull:
    rte
''' % {'res': RES, 'blocks': ''.join(blocks)}


def rom():
    data = asm68k.build_rom(source(), vectors={30: 'vint'}, title='STALEINT')
    path = os.path.join(runprobe.W, 'roms', 'staleint.bin'); os.makedirs(os.path.dirname(path), exist_ok=True)
    open(path, 'wb').write(data)
    return path


def decode(words):
    out = []
    for v in range(VMAX):
        for d in range(DMAX):
            i = (0x800 + 4 * (v * DMAX + d))
            out.append((d, v, words[i], words[i + 1], words[i + 2]))
    return out


def show(name, rows):
    print('==', name)
    for d, v, n, h1, h2 in sorted(rows, key=lambda r: r[0] * 10 + r[1] * 4):
        print('  delay %3d cyc  runs %d  hv %04X %04X' % (d * 10 + v * 4 + 4, n, h1, h2 if n > 1 else 0))


if __name__ == '__main__':
    mode = sys.argv[1] if len(sys.argv) > 1 else 'ours'
    path = rom()
    if mode in ('ours', 'both'):
        for ss in (sys.argv[2:] or ['']):
            ram = os.path.join(oracle.ROOT, 'out', 'staleint_ours.ram'); os.makedirs(os.path.dirname(ram), exist_ok=True)
            oracle.ours(path, FRAMES, None, ram=ram, extra=(['--slot-set', ss] if ss else []))
            show('ours ' + (ss or 'default'), decode(oracle.ram_words(ram)))
    if mode in ('theirs', 'both'):
        out = oracle.blastem(path, 'staleint', 'w%d,pause,snap' % (FRAMES * 17 + 2000), cache='--refresh' not in sys.argv)
        st = sorted(f for f in os.listdir(out) if f.endswith('.state'))
        show("BlastEm", decode(oracle.state_ram(os.path.join(out, st[-1]))))
