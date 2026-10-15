"""Where does the 68000 take an interrupt: the loop experiment of Track B4a/B4b, with distributions instead of means.

The horizontal interrupt fires every line (R10 = 0). The main program is a loop of one instruction (or a few) and a BRA.S; the handler's first instruction reads the HV counter into RAM. The line period (488.57
cycles) does not divide the loop, so over a few thousand interrupts every phase of the loop is met; the handler-entry time (HV counter at the first handler instruction, in the VDP's own time) as a function of
where the interrupt arrived is the whole acceptance rule: where in an instruction the 68000 looks at the interrupt lines, which instruction it takes the interrupt after, how long that instruction took.
Two machines that agree give the same HISTOGRAM of entry times. `python iplloops.py [names] [--ours-only] [--bins]` prints, per loop, mean entry time (master clocks after the HINT), and the 16-clock histograms
side by side. Needs the oracle lock for the BlastEm side (use --ours-only without it).
"""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import asm68k, oracle, probes
from probes import prologue, SPIN

N = 10000          # samples kept
NL = '\n'

LOOPS = {
    'nop': ['nop'],
    'move': ['move.w (a6),d1', 'move.w (a6),d1'],
    'mulu': ['mulu d2,d3'],
    'divu': ['divu d4,d3'],
    'mixed': ['move.l (a6),d1', 'addq.w #1,d1', 'lsl.l #8,d3', 'move.w d1,(a6)', 'nop'],
    'longmove': ['movem.l d0-d7,-(sp)', 'movem.l (sp)+,d0-d7'],
    'movem_out': ['movem.l d0-d7,(a6)'],
    'movem_in': ['movem.l (a6),d0-d7'],
    'movem_short': ['movem.l d0-d1,(a6)'],
    'mulu1': ['moveq #1,d5', 'mulu d2,d5'],
    'divu1': ['move.l d3,d6', 'divu d4,d6'],
    'lsl8': ['lsl.l #8,d5'],
    'lslr': ['moveq #30,d5', 'lsl.l d5,d6'],
    'clr_l': ['clr.l (a6)'],
    'add_rmw': ['add.l d3,(a6)'],
    'jsr_rts': ['jsr sub1'],
    # extra: single short instructions of other kinds
    'move_w_to_mem': ['move.w d1,(a6)'],
    'move_l_to_mem': ['move.l d3,(a6)'],
    'tst_l': ['tst.l (a6)'],
    'lea_pea': ['pea (a6)', 'addq.l #4,sp'],
}


def source(body, h40=True, n=N):
    s = prologue(h40, True, regs={0: 0x14, 1: 0x44, 10: 0})
    s += NL.join(['    lea ($FF0002).l,a0', '    lea ($C00008).l,a1', '    lea ($FF8000).l,a6', '    move.l #$7FFF0000,d3', '    move.w #$FFFF,d2', '    move.w #7,d4', '    move.w #0,($FF0000).l',
                  '    move.w #$2000,sr', 'main:']) + NL
    for ins in LOOPS[body]:
        s += '    ' + ins + NL
    s += NL.join(['    bra.s main', '_spin: bra.s _spin', 'sub1: rts', 'hint:', '    move.w (a1),d1', '    cmpi.w #%d,($FF0000).l' % n, '    bcc.s done', '    move.w d1,(a0)+', '    addq.w #1,($FF0000).l', 'done:', '    rte']) + NL
    return s


def tick(w):
    """HV word -> a number that grows with time (H counter blanking gap removed), in 16-master-clock units, line * 211 + position"""
    v, h = w >> 8, w & 0xFF
    idx = h if h <= 0xB6 else 0xB7 + (h - 0xE4)
    return v * 211 + idx


def samples_from(words, n=N):
    cnt = words[0]
    return [words[1 + i] for i in range(min(cnt, n))]


def entry_ticks(ws):
    """position within the line (ticks after line start), unwrapped so that a handler entry that spilled into the next line is later than one that did not: the HINT is at about tick 165 of the line"""
    out = []
    for w in ws:
        t = tick(w) % 211
        if t < 100: t += 211
        out.append(t)
    return out


def run(body, ours_only=False):
    src = source(body)
    rom = asm68k.build_rom(src, title='IPL ' + body, vectors={28: 'hint'})
    name = 'iplloop_' + body
    os.makedirs(os.path.join(oracle.ROOT, 'roms'), exist_ok=True)
    path = os.path.join(oracle.ROOT, 'roms', name + '.bin')
    open(path, 'wb').write(rom)
    ours_ram = os.path.join(oracle.ROOT, 'out', name + '_ours.ram')
    os.makedirs(os.path.dirname(ours_ram), exist_ok=True)
    extra = ('--slot-set', os.environ['IPL_SLOTSET']) if os.environ.get('IPL_SLOTSET') else ()      # experiment switches of our side, e.g. "iplmode=1;ipllead=7"
    code, text = oracle.ours(path, 400, None, ram=ours_ram, extra=extra)
    o = oracle.ram_words(ours_ram)
    b = None
    if not ours_only:
        out = oracle.blastem(path, name, 'w3500,pause,snap', cache=True)
        st = os.path.join(out, 's000.state')
        if os.path.exists(st): b = oracle.state_ram(st)
    return o, b


def summarize(label, words):
    ws = samples_from(words)
    if not ws: return '%s: no samples (count %d)' % (label, words[0])
    t = entry_ticks(ws)
    mean = sum(t) / len(t) * 16
    return '%s: %d samples, mean entry %.1f clocks (tick units %.2f)' % (label, len(ws), mean, sum(t) / len(t))


def histogram(words):
    ws = samples_from(words)
    t = entry_ticks(ws)
    h = {}
    for x in t: h[x] = h.get(x, 0) + 1
    return h


if __name__ == '__main__':
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    ours_only = '--ours-only' in sys.argv
    show_bins = '--bins' in sys.argv
    names = args or list(LOOPS)
    for nm in names:
        o, b = run(nm, ours_only)
        print('== %s' % nm)
        print('  ' + summarize('ours   ', o))
        if b is not None: print('  ' + summarize('BlastEm', b))
        if show_bins:
            ho = histogram(o); hb = histogram(b) if b is not None else {}
            keys = sorted(set(ho) | set(hb))
            print('  tick  ours  blastem')
            for k in keys: print('  %4d %5d %5d' % (k, ho.get(k, 0), hb.get(k, 0)))
