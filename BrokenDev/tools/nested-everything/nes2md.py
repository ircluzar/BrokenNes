"""Nested NesGen: NES (NROM, CHR-ROM) -> Genesis / Mega Drive, by static 6502 -> 68000 translation.

    python nes2md.py convert <in.nes> <out.md> [--profile p.json] [--set key=value] [--report r.json] [--work dir]
    python nes2md.py feedback <in.nes> <out.md> ...     (convert, run headless on BrokenNes's Genesis core, add the
                                                         missed entry points it reports to the profile, repeat)

How it works (see nes2md_runtime.s for the machine side):
  * The 6502 code is found by recursive descent from the three vectors plus the profile's extra entries.  Each
    instruction becomes a short 68000 sequence; every instruction gets a label and a slot in a 32 K dispatch table
    (6502 address -> 68000 code), which RTS, RTI and JMP (ind) go through.  A dispatch miss is the dynamic
    fallback: the runtime stops with the missed address in RAM ($FF8040) and a red screen, and 'feedback' adds the
    address as an entry and translates again.
  * Registers: A=d0 X=d1 Y=d2 S=d6 (bytes, upper bits kept zero), NZ in d3.w (Z: low byte zero, N: bit 15), C in d4
    (0/1), V in RAM.  Temps d5 d7 a0 a1.  a2 = 6502 stack page, a3/a4 = VDP data/control, a5 = NES RAM, a6 = vars.
  * The NMI runs from the Genesis VInt when $2000 bit 7 is set; its RTI returns from the interrupt.
  * PPU: CHR-ROM converted at build time (two copies: BG pixels 0-3, sprite pixels 4-7), nametable writes -> plane A
    (H32, 32x64: NES nametable halves at rows 0 and 32), attributes -> palette bits, $3F00 palette -> CRAM (line p =
    BG palette p in 0-3 + sprite palette p in 4-7), $4014 -> sprite table, scroll -> VSRAM/HScroll (+8 lines crop).
  * APU: pulses + triangle -> PSG tones, noise -> PSG noise; length counters and envelopes stepped once a frame.
"""
import argparse
import hashlib
import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import m6502  # noqa: E402

NZ_W = {'LDA', 'LDX', 'LDY', 'TAX', 'TAY', 'TXA', 'TYA', 'TSX', 'PLA', 'AND', 'ORA', 'EOR', 'ADC', 'SBC', 'CMP',
        'CPX', 'CPY', 'INC', 'DEC', 'INX', 'DEX', 'INY', 'DEY', 'ASL', 'LSR', 'ROL', 'ROR', 'BIT', 'PLP', 'RTI'}
NZ_R = {'BNE', 'BEQ', 'BPL', 'BMI', 'PHP', 'BRK'}
C_W = {'ADC', 'SBC', 'CMP', 'CPX', 'CPY', 'ASL', 'LSR', 'ROL', 'ROR', 'CLC', 'SEC', 'PLP', 'RTI'}
C_R = {'ADC', 'SBC', 'ROL', 'ROR', 'BCC', 'BCS', 'PHP', 'BRK'}
V_W = {'ADC', 'SBC', 'BIT', 'CLV', 'PLP', 'RTI'}
V_R = {'BVS', 'BVC', 'PHP', 'BRK'}
REG = {'A': 'd0', 'X': 'd1', 'Y': 'd2'}

# 2C02 palette (a common measured set), 64 RGB triples
NES_RGB = [
    (84, 84, 84), (0, 30, 116), (8, 16, 144), (48, 0, 136), (68, 0, 100), (92, 0, 48), (84, 4, 0), (60, 24, 0),
    (32, 42, 0), (8, 58, 0), (0, 64, 0), (0, 60, 0), (0, 50, 60), (0, 0, 0), (0, 0, 0), (0, 0, 0),
    (152, 150, 152), (8, 76, 196), (48, 50, 236), (92, 30, 228), (136, 20, 176), (160, 20, 100), (152, 34, 32), (120, 60, 0),
    (84, 90, 0), (40, 114, 0), (8, 124, 0), (0, 118, 40), (0, 102, 120), (0, 0, 0), (0, 0, 0), (0, 0, 0),
    (236, 238, 236), (76, 154, 236), (120, 124, 236), (176, 98, 236), (228, 84, 236), (236, 88, 180), (236, 106, 100), (212, 136, 32),
    (160, 170, 0), (116, 196, 0), (76, 208, 32), (56, 204, 108), (56, 180, 204), (60, 60, 60), (0, 0, 0), (0, 0, 0),
    (236, 238, 236), (168, 204, 236), (188, 188, 236), (212, 178, 236), (236, 174, 236), (236, 174, 212), (236, 180, 176), (228, 196, 144),
    (204, 210, 120), (180, 222, 120), (168, 226, 144), (152, 226, 180), (160, 214, 228), (160, 162, 160), (0, 0, 0), (0, 0, 0)]


def md_colour(rgb):
    r, g, b = [min(7, (c + 18) // 36) for c in rgb]
    return (b << 9) | (g << 5) | (r << 1)


def chr_to_md(chr_rom, offset):
    """NES 2bpp planar tiles -> Genesis 4bpp packed; non-zero pixels get +offset."""
    out = bytearray()
    for t in range(len(chr_rom) // 16):
        p = chr_rom[t * 16:t * 16 + 16]
        for y in range(8):
            lo, hi = p[y], p[y + 8]
            row = 0
            for x in range(8):
                v = ((lo >> (7 - x)) & 1) | (((hi >> (7 - x)) & 1) << 1)
                if v:
                    v += offset
                row = (row << 4) | v
            out += row.to_bytes(4, 'big')
    return bytes(out)


class Emitter:
    def __init__(self, prg, insns, count):
        self.prg, self.insns, self.count = prg, insns, count
        self.opprof = False
        self.out = []
        self.uid = 0
        self.order = sorted(insns)
        self.stats = {'io_sites': 0, 'generic_sites': 0}

    def e(self, s):
        self.out.append('\t' + s)

    def lab(self):
        self.uid += 1
        return '.Lu%d' % self.uid

    # ---- flag liveness (a short look-ahead along the straight-line path) ----
    def live(self, a, writers, readers):
        """Is a flag read before it is written again, following both ways of branches and into JSR targets
        (bounded; anything unknown counts as a read)."""
        budget = [40]
        seen = set()

        def go(a):
            while True:
                if budget[0] <= 0 or a in seen:
                    return budget[0] <= 0
                seen.add(a)
                budget[0] -= 1
                d = self.insns.get(a)
                if d is None:
                    return True
                m = d['m']
                if m in readers:
                    return True
                if m in writers:
                    return False
                if m in ('RTS', 'RTI', 'BRK') or (m == 'JMP' and d['mode'] != 'abs'):
                    return True
                if m in m6502.BRANCHES:
                    if go(self.prg.canon(d['tgt'])):
                        return True
                elif m in ('JMP', 'JSR'):
                    a = self.prg.canon(d['arg'])
                    continue
                a = self.prg.canon(a + d['n'])
        d = self.insns[a]
        return go(self.prg.canon(a + d['n']))

    # ---- operand addressing ----
    def region(self, addr):
        if addr < 0x2000:
            return 'ram'
        if addr < 0x4000:
            return 'ppu'
        if addr < 0x4018:
            return 'apu'
        if addr < 0x8000:
            return 'none'
        return 'rom'

    def ea(self, d):
        """Return ('ea', string) for a plain 68000 effective address, or ('io', addr) / ('gen', None) (address in d5.w)."""
        mode, arg = d['mode'], d['arg']
        if mode == 'imm':
            return ('ea', '#%d' % arg)
        if mode == 'zp':
            return ('ea', '%d(a5)' % arg)
        if mode in ('zpx', 'zpy'):
            ix = 'd1' if mode == 'zpx' else 'd2'
            if arg + 255 < 0x100 or arg == 0:
                return ('ea', '%d(a5,%s.w)' % (arg, ix))
            self.zpwrap(arg, ix)           # exact zero-page wrap: d5.w = (arg + ix) & $FF
            return ('ea', '0(a5,d5.w)')
        if mode == 'abs':
            r = self.region(arg)
            if r == 'ram':
                return ('ea', '%d(a5)' % (arg & 0x7FF))
            if r == 'rom':
                return ('ea', '(prg+%d).l' % ((arg - 0x8000) & 0x7FFF))
            return ('io', arg)
        if mode in ('abx', 'aby'):
            ix = 'd1' if mode == 'abx' else 'd2'
            r = self.region(arg)
            if r == 'ram' and (arg & 0x7FF) + 255 < 0x800:
                a = arg & 0x7FF
                if a < 128:
                    return ('ea', '%d(a5,%s.w)' % (a, ix))
                self.e('lea %d(a5),a0' % a)
                return ('ea', '0(a0,%s.w)' % ix)
            if r == 'rom' and arg + 255 <= 0xFFFF:
                self.e('lea (prg+%d).l,a0' % ((arg - 0x8000) & 0x7FFF))
                return ('ea', '0(a0,%s.w)' % ix)
            self.e('move.w #%d,d5' % arg)
            self.e('add.w %s,d5' % ix)
            return ('gen', None)
        if mode == 'izy':
            self.e('move.b %d(a5),d5' % ((arg + 1) & 0xFF))     # ($FF),Y reads $00 for the high byte
            self.e('lsl.w #8,d5')
            self.e('move.b %d(a5),d5' % arg)
            self.e('add.w d2,d5')
            return ('gen', None)
        if mode == 'izx':
            self.zpwrap(arg, 'd1')
            self.e('lea 0(a5,d5.w),a0')
            self.e('addq.b #1,d5')        # the high byte wraps in the zero page too: ($FF,X)
            self.e('move.b 0(a5,d5.w),d5')
            self.e('lsl.w #8,d5')
            self.e('move.b (a0),d5')
            return ('gen', None)
        raise ValueError(mode)

    def zpwrap(self, arg, ix):
        # moveq / move.w leave bits 8-15 zero, add.b wraps the low byte: 2 instructions instead of 3
        if arg < 0x80:
            self.e('moveq #%d,d5' % arg)
        else:
            self.e('move.w #%d,d5' % arg)
        self.e('add.b %s,d5' % ix)

    def load(self, d, dst):
        """Operand value -> register dst (a data register).  Returns nothing."""
        k, v = self.ea(d)
        if k == 'ea':
            self.e('move.b %s,%s' % (v, dst))
        elif k == 'io':
            self.stats['io_sites'] += 1
            self.e('jsr io_rd_%04x' % v if v in (0x2002, 0x2004, 0x2007, 0x4015, 0x4016, 0x4017) else 'moveq #0,d7')
            if v not in (0x2002, 0x2004, 0x2007, 0x4015, 0x4016, 0x4017):
                if v >= 0x2000:
                    self.e('move.b #%d,d7' % (v >> 8))    # open bus
            if dst != 'd7':
                self.e('move.b d7,%s' % dst)
        else:
            self.stats['generic_sites'] += 1
            l1, l2 = self.lab(), self.lab()
            self.e('cmp.w #0x2000,d5')
            self.e('bcc.s %s' % l1)
            self.e('and.w #0x7ff,d5')
            self.e('move.b 0(a5,d5.w),%s' % dst)
            self.e('bra.s %s' % l2)
            self.out.append(l1 + ':')
            l3 = self.lab()
            self.e('tst.w d5')
            self.e('bpl.s %s' % l3)
            self.e('and.w #0x7fff,d5')             # PRG data through a pointer: inline
            self.e('lea prg,a0')
            self.e('move.b 0(a0,d5.w),%s' % dst)
            self.e('bra.s %s' % l2)
            self.out.append(l3 + ':')
            self.e('jsr gen_rd')
            if dst != 'd7':
                self.e('move.b d7,%s' % dst)
            self.out.append(l2 + ':')

    def store(self, d, src):
        k, v = self.ea(d)
        if k == 'ea':
            if v.startswith('(prg'):
                return          # writes to ROM do nothing on NROM
            self.e('move.b %s,%s' % (src, v))
        elif k == 'io':
            self.stats['io_sites'] += 1
            if src != 'd7':
                self.e('move.b %s,d7' % src)
            if v in IO_WRITES:
                self.e('jsr io_wr_%04x' % v)
            elif 0x4000 <= v < 0x4018:
                self.e('move.b d7,apu+%d(a6)' % (v - 0x4000))
        else:
            self.stats['generic_sites'] += 1
            l1, l2 = self.lab(), self.lab()
            self.e('cmp.w #0x2000,d5')
            self.e('bcc.s %s' % l1)
            self.e('and.w #0x7ff,d5')
            self.e('move.b %s,0(a5,d5.w)' % src)
            self.e('bra.s %s' % l2)
            self.out.append(l1 + ':')
            if src != 'd7':
                self.e('move.b %s,d7' % src)
            self.e('jsr gen_wr')
            self.out.append(l2 + ':')

    def setnz(self, r):
        self.e('move.b %s,d3' % r)
        if self.nlive:
            self.e('ext.w d3')

    def setc(self, cond='scs'):
        self.e('%s d4' % cond)          # C is 0 / $FF (lsr.b #1 still gives X; Scc leaves the 68000 flags for the branch)

    def push_imm(self, v):
        self.e('subq.b #1,d6')                  # S first: a VInt between the two must not land on the byte
        self.e('move.b #%d,1(a2,d6.w)' % v)

    # ---- one instruction ----
    def insn(self, d):
        a, m, mode = d['a'], d['m'], d['mode']
        nz = m in NZ_W and self.live(a, NZ_W, NZ_R)
        self.nlive = nz and self.live(a, NZ_W, {'BMI', 'BPL', 'PHP', 'BRK'})
        c = m in C_W and self.live(a, C_W, C_R)
        v = m in V_W and self.live(a, V_W, V_R)
        nxt = self.insns.get(self.prg.canon(a + d['n']))
        nextc = nxt is not None and nxt['m'] in ('BCC', 'BCS')
        if self.count:
            self.e('addq.l #1,icount(a6)')
        if self.opprof:
            l = self.lab()                    # per-opcode counts inside NMIs at $FF8400 (profile builds)
            self.e('tst.b in_nmi(a6)')
            self.e('beq.s %s' % l)
            self.e('addq.l #1,%d(a6)' % (0x400 + 4 * d['op']))
            self.out.append(l + ':')
        if m in ('LDA', 'LDX', 'LDY'):
            r = REG[m[2]]
            self.load(d, r)
            if nz:
                self.setnz(r)
        elif m in ('STA', 'STX', 'STY'):
            self.store(d, REG[m[2]])
        elif m in ('TAX', 'TAY', 'TXA', 'TYA'):
            s, t = REG[m[1]], REG[m[2]]
            self.e('move.b %s,%s' % (s, t))
            if nz:
                self.setnz(t)
        elif m == 'TSX':
            self.e('move.b d6,d1')
            if nz:
                self.setnz('d1')
        elif m == 'TXS':
            self.e('move.b d1,d6')
        elif m in ('INX', 'INY', 'DEX', 'DEY'):
            r = REG[m[2]]
            self.e('%s.b #1,%s' % ('addq' if m[0] == 'I' else 'subq', r))
            if nz:
                self.setnz(r)
        elif m in ('AND', 'ORA', 'EOR'):
            op = {'AND': 'and', 'ORA': 'or', 'EOR': 'eor'}[m]
            if mode == 'imm':
                self.e('%si.b #%d,d0' % (op, d['arg']))
            else:
                k_ok = False
                if op != 'eor':
                    save = len(self.out)
                    k, ea = self.ea(d)
                    if k == 'ea':
                        self.e('%s.b %s,d0' % (op, ea))
                        k_ok = True
                    else:
                        del self.out[save:]
                if not k_ok:
                    self.load(d, 'd7')
                    self.e('%s.b d7,d0' % op)
            if nz:
                self.setnz('d0')
        elif m in ('ADC', 'SBC'):
            self.load(d, 'd7')
            if m == 'ADC':
                self.e('lsr.b #1,d4')
                self.e('addx.b d7,d0')
            else:
                self.e('not.b d4')
                self.e('lsr.b #1,d4')
                self.e('subx.b d7,d0')
            if v:
                self.e('svs vflag(a6)')
            self.setc('scs' if m == 'ADC' else 'scc')
            if nz:
                self.setnz('d0')
        elif m in ('CMP', 'CPX', 'CPY'):
            r = REG['A' if m == 'CMP' else m[2]]
            save = len(self.out)
            k, ea = self.ea(d)
            if k != 'ea':
                del self.out[save:]
                self.load(d, 'd7')
                ea = 'd7'
            if nz:
                self.e('move.b %s,d3' % r)
                self.e('sub.b %s,d3' % ea)
                self.e('scc d4')
                if self.nlive:
                    self.e('ext.w d3')
            else:
                self.e('cmp.b %s,%s' % (ea, r))
                self.setc('scc')
        elif m == 'BIT':
            self.load(d, 'd7')
            if v:
                self.e('btst #6,d7')
                self.e('sne vflag(a6)')
            self.e('move.b d7,d3')
            self.e('ext.w d3')
            self.e('and.b d0,d3')
        elif m in ('ASL', 'LSR', 'ROL', 'ROR'):
            op = {'ASL': 'lsl', 'LSR': 'lsr', 'ROL': 'roxl', 'ROR': 'roxr'}[m]
            if mode == 'acc':
                r = 'd0'
            else:
                self.load(d, 'd7')
                r = 'd7'
            if m in ('ROL', 'ROR'):
                self.e('lsr.b #1,d4')
            self.e('%s.b #1,%s' % (op, r))
            if c:
                self.setc('scs')
            if nz:
                self.setnz(r)
            if mode != 'acc':
                self.restore_rmw(d)
        elif m in ('INC', 'DEC'):
            op = 'addq' if m == 'INC' else 'subq'
            save = len(self.out)
            k, ea = self.ea(d)
            if k == 'ea' and not ea.startswith('(prg'):
                self.e('%s.b #1,%s' % (op, ea))
                if nz:
                    self.setnz(ea)
            else:
                del self.out[save:]
                self.load(d, 'd7')
                self.e('%s.b #1,d7' % op)
                if nz:
                    self.setnz('d7')
                self.restore_rmw(d)
        elif m in BR:
            t = 'L%04X' % self.prg.canon(d['tgt'])
            test, cc = BR[m]
            last = self.out[-1].strip() if self.out else ''
            prev = self.out[-2].strip() if len(self.out) > 1 else ''
            if (m in ('BEQ', 'BNE') and last.startswith('move.b') and last.endswith(',d3')) or                (m in ('BEQ', 'BNE', 'BMI', 'BPL') and last == 'ext.w d3') or                (m in ('BEQ', 'BNE') and last == 'scc d4' and prev.startswith('sub.b ') and prev.endswith(',d3')):
                test = None                  # the 68000 flags already say it
            elif m in ('BCS', 'BCC') and last in ('scs d4', 'scc d4'):
                i = len(self.out) - 1
                while i > 0 and SCC.match(self.out[i].strip()):
                    i -= 1
                if CFLAG.match(self.out[i].strip()):
                    # the 68000 carry is the 6502 carry after scs, its inverse after scc
                    test = None
                    same = last == 'scs d4'
                    cc = ('cs' if same else 'cc') if m == 'BCS' else ('cc' if same else 'cs')
            if test:
                self.e(test)
            self.e('jb%s %s' % (cc, t))
        elif m == 'JMP':
            if mode == 'abs':
                self.e('jra L%04X' % self.prg.canon(d['arg']))
            else:
                p = d['arg']
                hi = (p & 0xFF00) | ((p + 1) & 0xFF)     # the 6502 page-wrap bug
                for addr, reg in ((hi, 'd5'), (p, 'd7')):
                    r = self.region(addr)
                    if r == 'ram':
                        self.e('move.b %d(a5),%s' % (addr & 0x7FF, reg))
                    else:
                        self.e('move.b (prg+%d).l,%s' % ((addr - 0x8000) & 0x7FFF, reg))
                    if reg == 'd5':
                        self.e('lsl.w #8,d5')
                self.e('move.b d7,d5')
                self.e('jra dispatch')
        elif m == 'JSR':
            # native call with a check frame (S before the push, the 6502 return address): rts_fast returns
            # natively only when the 6502 stack agrees, else it drops the frames and goes through the table
            ret = (a + 2) & 0xFFFF
            self.e('move.w d6,-(sp)')
            self.e('subq.b #2,d6')
            self.e('move.b #%d,1(a2,d6.w)' % (ret & 0xFF))      # immediates: no lsr.w #8 (22 cycles)
            self.e('move.b #%d,2(a2,d6.w)' % (ret >> 8))
            self.e('jbsr L%04X' % self.prg.canon(d['arg']))
            if self.prg.canon(a + 3) not in self.insns:
                self.e('move.w #0x%04x,d5' % (a + 3))
                self.e('jra miss')
        elif m == 'RTS':
            # native return when S is back where the JSR left it (a PLA PLA exit or a stack trick fails the
            # check and goes through the dispatch table instead).  A changed return address with the same S
            # is not caught (no such trick in the PoC games).
            self.e('addq.b #2,d6')
            self.e('cmp.w 4(sp),d6')
            self.e('jbne rts_slow')
            self.e('move.l (sp)+,a0')
            self.e('addq.l #2,sp')
            self.e('jmp (a0)')
        elif m == 'RTI':
            self.e('jra do_rti')
        elif m == 'PHA':
            self.e('subq.b #1,d6')
            self.e('move.b d0,1(a2,d6.w)')
        elif m == 'PLA':
            self.e('move.b 1(a2,d6.w),d0')          # read before S moves (an NMI push would land on it)
            self.e('addq.b #1,d6')
            if nz:
                self.setnz('d0')
        elif m == 'PHP':
            self.e('jsr php_build')
            self.e('ori.b #0x30,d7')
            self.e('subq.b #1,d6')
            self.e('move.b d7,1(a2,d6.w)')
        elif m == 'PLP':
            self.e('move.b 1(a2,d6.w),d7')
            self.e('addq.b #1,d6')
            self.e('jsr plp_unpack')
        elif m == 'CLC':
            self.e('moveq #0,d4')
        elif m == 'SEC':
            self.e('moveq #-1,d4')
        elif m == 'CLV':
            self.e('clr.b vflag(a6)')
        elif m in ('SEI', 'CLI'):
            self.e('move.b #%d,iflag(a6)' % (1 if m == 'SEI' else 0))
        elif m in ('SED', 'CLD', 'NOP'):
            pass
        elif m == 'BRK':
            self.e('move.w #0x%04x,d5' % a)
            self.e('jra brk_hit')
        else:
            raise ValueError(m)

    def restore_rmw(self, d):
        """Write d7 back to the operand of a read-modify-write instruction (recomputes the address)."""
        self.store(d, 'd7')

    def emit_all(self):
        for i, a in enumerate(self.order):
            d = self.insns[a]
            self.out.append('L%04X:' % a)
            self.insn(d)
            if d['m'] in m6502.ENDS:
                continue
            nx = self.prg.canon(a + d['n'])
            if i + 1 >= len(self.order) or self.order[i + 1] != nx:
                if nx in self.insns:
                    self.e('jra L%04X' % nx)
                else:
                    self.e('move.w #0x%04x,d5' % nx)
                    self.e('jra miss')
        # targets the walk could not decode (a branch into a bad opcode): a miss stub each, so the link works
        used = set(re.findall(r'\bL([0-9A-F]{4})\b', '\n'.join(self.out)))
        for t in sorted(used):
            if int(t, 16) not in self.insns:
                self.out.append('L%s:' % t)
                self.e('move.w #0x%s,d5' % t)
                self.e('jra miss')
        return '\n'.join(self.out) + '\n'


SCC = re.compile(r'^s(cs|cc|vs|ne|eq) ')
CFLAG = re.compile(r'^(addx|subx|cmp|sub|lsl|lsr|roxl|roxr)\.b ')
BR = {'BEQ': ('tst.b d3', 'eq'), 'BNE': ('tst.b d3', 'ne'), 'BMI': ('tst.w d3', 'mi'), 'BPL': ('tst.w d3', 'pl'),
      'BCS': ('tst.b d4', 'ne'), 'BCC': ('tst.b d4', 'eq'), 'BVS': ('tst.b vflag(a6)', 'ne'),
      'BVC': ('tst.b vflag(a6)', 'eq')}
IO_WRITES = {0x2000, 0x2001, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x4003, 0x4007, 0x400B, 0x400F, 0x4014, 0x4015,
             0x4016}


MD_CLOCK = 7670453
NES_CLOCK = 1789773


def fm_word(f):
    rate = MD_CLOCK / 144
    for blk in range(8):
        fn = round(f * (1 << 20) / rate / (2 ** (blk - 1)))
        if fn < 2048:
            return (blk << 11) | max(1, fn)
    return (7 << 11) | 2047


def fm_tables():
    """YM2612 tables for the 2A03 -> FM path: block/fnum per period (pulse f = clk/16(t+1), triangle clk/32(t+1)),
    carrier TL per NES volume, the duty voices (modulator MUL/TL: 50% = ratio 2, odd harmonics like a square;
    12.5/25/75% = ratio 1, even harmonics too, brighter the narrower), and the register init list."""
    import math
    out = []
    for name, div in (('fmfreq_p', 16), ('fmfreq_t', 32)):
        w = [fm_word(NES_CLOCK / (div * (t + 1))) for t in range(2048)]
        out.append(name + ':')
        for i in range(0, 2048, 16):
            out.append('\t.word ' + ','.join('0x%04x' % x for x in w[i:i + 16]))
    tl = [127] + [min(127, 8 + round(20 * math.log10(15 / v) / 0.75)) for v in range(1, 16)]
    out.append('fmtl: .byte ' + ','.join(map(str, tl)))
    out.append('\t.even')
    out.append('fmduty: .word 0x0112, 0x011a, 0x021c, 0x011a')
    ini = [0x2200, 0x2700, 0x2B00]
    for ch in range(3):
        ini += [0xB004 + (ch << 8), 0xB4C0 + (ch << 8)]          # algorithm 4 (S1 -> S2), both speakers
        for op in (0, 4, 8, 12):
            r = (op + ch) << 8
            tl0 = 127 if (op != 0 or ch == 2) else 0x1c           # S1 modulates S2 (none on the triangle); S3/S4 off
            ini += [0x3001 + r, 0x4000 + r + tl0, 0x501F + r, 0x6000 + r, 0x7000 + r, 0x800F + r, 0x9000 + r]
        ini.append(0x28F0 + ch)                                    # key on for good; the level is the carrier TL
    out.append('ymini: .word ' + ','.join('0x%04x' % x for x in ini) + ',0xffff')
    return out


def toolchain_bin():
    for c in (os.environ.get('NESGEN_M68K_BIN'), r'T:\BrokenDev\env\toolchains\sgdk\2.11\sgdk\bin'):
        if c and os.path.exists(os.path.join(c, 'as.exe')):
            return c
    raise SystemExit('68000 assembler not found: set NESGEN_M68K_BIN to SGDK\'s bin (as.exe, ld.exe, objcopy.exe)')


def load_profile(path):
    if path and os.path.exists(path):
        with open(path) as f:
            return json.load(f)
    return {}


def convert(src, dst, profile_path=None, settings=None, work=None, report_path=None):
    settings = dict(settings or {})
    prof = load_profile(profile_path)
    data = open(src, 'rb').read()
    if data[:4] != b'NES\x1a':
        raise SystemExit('not an iNES file')
    prg_n, chr_n, f6, f7 = data[4], data[5], data[6], data[7]
    mapper = (f6 >> 4) | (f7 & 0xF0)
    if mapper != 0 or chr_n == 0 or prg_n > 2:
        raise SystemExit('nes2md handles NROM (mapper 0) with CHR-ROM only (this is mapper %d, %d CHR banks)' % (mapper, chr_n))
    off = 16 + (512 if f6 & 4 else 0)
    prg_raw = data[off:off + prg_n * 16384]
    chr_rom = data[off + prg_n * 16384: off + prg_n * 16384 + 8192]
    prg = m6502.Prg(prg_raw)
    vec = [prg.w(0xFFFA), prg.w(0xFFFC), prg.w(0xFFFE)]
    extra = [int(x, 16) if isinstance(x, str) else x for x in prof.get('entries', [])]
    insns, labels, bad, indirect = m6502.walk(prg, vec[:2] + extra)
    count = settings.get('count', 'off') == 'on'
    em = Emitter(prg, insns, count)
    em.opprof = settings.get('opprof', 'off') == 'on'
    code = em.emit_all()

    work = os.path.abspath(work or os.path.splitext(dst)[0] + '_build')
    os.makedirs(work, exist_ok=True)
    with open(os.path.join(work, 'prg.bin'), 'wb') as f:
        f.write(prg.prg)
    with open(os.path.join(work, 'chr.bin'), 'wb') as f:
        f.write(chr_to_md(chr_rom, 0) + chr_to_md(chr_rom, 4))
    with open(os.path.join(work, 'chrraw.bin'), 'wb') as f:
        f.write(chr_rom)
    disp = []
    for i in range(0x8000):
        a = prg.canon(0x8000 + i)
        disp.append('L%04X' % a if a in insns else 'miss_tab')
    sprattr = []
    for v in range(256):
        sprattr.append(((v & 3) << 13) | (0x800 if v & 0x40 else 0) | (0x1000 if v & 0x80 else 0) |
                       (0 if v & 0x20 else 0x8000))      # in front: high priority; behind: low (under plane A's pixels)
    pal = ', '.join('0x%04x' % md_colour(c) for c in NES_RGB)
    sound = settings.get('sound', 'fm')
    if sound not in ('fm', 'psg'):
        raise SystemExit('sound must be fm or psg')
    gen = ['\t.equ MIRBIT, %d' % (10 if f6 & 1 else 11),
           '\t.equ SOUND_FM, %d' % (sound == 'fm'),
           '\t.equ SPR0_LAG, %d' % int(settings.get('spr0lag', '0')),
           '\t.include "%s"' % os.path.join(HERE, 'nes2md_runtime.s').replace('\\', '/'),
           '\t.equ NMI_ENTRY_ADDR, 0x%04x' % vec[0],
           '\t.text', '\t.even', 'nmi_entry: jra L%04X' % prg.canon(vec[0]),
           'reset_entry: jra L%04X' % prg.canon(vec[1]),
           'miss_tab: jra miss',
           '\t.even', 'nescol: .word ' + pal,
           'sprattr: .word ' + ', '.join('0x%04x' % x for x in sprattr),
           'prg: .incbin "%s"' % os.path.join(work, 'prg.bin').replace('\\', '/'),
           '\t.even', 'chrraw: .incbin "%s"' % os.path.join(work, 'chrraw.bin').replace('\\', '/'),
           '\t.even', 'chrdata: .incbin "%s"' % os.path.join(work, 'chr.bin').replace('\\', '/'), 'chrdata_end:',
           '\t.even'] + fm_tables() + ['\t.even', 'disp:']
    for i in range(0, 0x8000, 8):
        gen.append('\t.long ' + ','.join(disp[i:i + 8]))
    gen.append('\t.even')
    asm = '\n'.join(gen) + '\n' + code
    s_path = os.path.join(work, 'game.s')
    with open(s_path, 'w') as f:
        f.write(asm)
    b = toolchain_bin()
    o_path = os.path.join(work, 'game.o')
    elf = os.path.join(work, 'game.elf')
    r = subprocess.run([os.path.join(b, 'as.exe'), '-m68000', '--register-prefix-optional', '-o', o_path, s_path],
                       capture_output=True, text=True, cwd=work)
    if r.returncode:
        raise SystemExit('assembler failed:\n' + r.stderr[-3000:])
    r = subprocess.run([os.path.join(b, "ld.exe"), "-Ttext=0", "-e", "start", '-o', elf, o_path], capture_output=True, text=True)
    if r.returncode:
        raise SystemExit('link failed:\n' + r.stderr[-3000:])
    r = subprocess.run([os.path.join(b, 'objcopy.exe'), '-O', 'binary', elf, dst], capture_output=True, text=True)
    if r.returncode:
        raise SystemExit('objcopy failed:\n' + r.stderr[-2000:])
    rom = bytearray(open(dst, 'rb').read())
    pad = (len(rom) + 0x1FFFF) & ~0x1FFFF
    rom += b'\xff' * (pad - len(rom))
    rom[0x1A4:0x1A8] = (len(rom) - 1).to_bytes(4, 'big')
    ck = 0
    for i in range(0x200, len(rom), 2):
        ck = (ck + (rom[i] << 8 | rom[i + 1])) & 0xFFFF
    rom[0x18E:0x190] = ck.to_bytes(2, 'big')
    with open(dst, 'wb') as f:
        f.write(rom)
    # symbol addresses the harness needs
    syms = {}
    r = subprocess.run([os.path.join(b, 'nm.exe'), elf], capture_output=True, text=True)
    for line in r.stdout.splitlines():
        p = line.split()
        if len(p) == 3:
            syms[p[2]] = int(p[0], 16)
    rep = {'tool': 'nes2md', 'in': os.path.basename(src), 'out': os.path.basename(dst),
           'in_sha256': hashlib.sha256(data).hexdigest(), 'out_sha256': hashlib.sha256(rom).hexdigest(),
           'mapper': mapper, 'prg_kb': prg_n * 16, 'mirroring': 'vertical' if f6 & 1 else 'horizontal',
           'vectors': ['%04X' % x for x in vec], 'entries_extra': ['%04X' % x for x in extra],
           'insns': len(insns), 'bad_decodes': ['%04X' % x for x in bad], 'indirect_jumps': ['%04X' % x for x in indirect],
           'rom_bytes': len(rom), 'code_bytes': syms.get('code_end', 0) - syms.get('L%04X' % min(insns), 0),
           'count_build': count, **em.stats,
           'symbols': {k: '%06X' % v for k, v in syms.items() if not k.startswith('L') and not k.startswith('.')}}
    if report_path:
        with open(report_path, 'w') as f:
            json.dump(rep, f, indent=1)
    return rep


def workshop_exe(arg=None):
    for c in (arg, os.environ.get('BROKENDEV_WORKSHOP'),
              os.path.join(HERE, '..', '..', '..', 'Workshop', 'bin', 'Release', 'net10.0-windows', 'BrokenNes.Workshop.exe')):
        if c and os.path.exists(c):
            return os.path.abspath(c)
    raise SystemExit('BrokenNes.Workshop.exe not found (set BROKENDEV_WORKSHOP / --workshop)')


VARS = 0xFF8000


def run_md(exe, rom, frames, work, tag, inputs='', extra=()):
    ram = os.path.join(work, tag + '_ram.bin')
    args = [exe, '--md-run', rom, '--frames', str(frames), '--ram-out', ram, '--quiet'] + list(extra)
    if inputs:
        args += ['--input', inputs]
    r = subprocess.run(args, capture_output=True, text=True, timeout=1800)
    return r, (open(ram, 'rb').read() if os.path.exists(ram) else b'')


def feedback(src, dst, profile_path, settings, work, report_path, exe, frames=1800, inputs='', rounds=12):
    prof = load_profile(profile_path)
    prof.setdefault('entries', [])
    rep = None
    for rnd in range(rounds):
        tmpp = os.path.join(work, 'profile_fb.json')
        os.makedirs(work, exist_ok=True)
        with open(tmpp, 'w') as f:
            json.dump(prof, f, indent=1)
        rep = convert(src, dst, tmpp, settings, os.path.join(work, 'build'), None)
        r, ram = run_md(exe, dst, frames, work, 'fb%d' % rnd, inputs or prof.get('feedback_input', ''))
        if len(ram) < 0x8050:
            break
        missed = ram[0x8040] << 8 | ram[0x8041]
        flag = ram[0x8042]
        if flag != 0xA5:
            break
        e = '%04X' % missed
        print('feedback round %d: dispatch miss at $%s' % (rnd, e))
        if e in prof['entries'] or missed < 0x8000:
            rep['feedback_stuck'] = e
            break
        prof['entries'].append(e)
    rep['entries_found'] = prof['entries']
    if profile_path:
        prof.setdefault('found', prof['entries'])
        with open(profile_path, 'w') as f:
            json.dump(prof, f, indent=1)
    if report_path:
        with open(report_path, 'w') as f:
            json.dump(rep, f, indent=1)
    return rep


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('mode', choices=['convert', 'feedback'])
    ap.add_argument('src')
    ap.add_argument('dst')
    ap.add_argument('--profile')
    ap.add_argument('--set', action='append', default=[])
    ap.add_argument('--report')
    ap.add_argument('--work')
    ap.add_argument('--workshop')
    a = ap.parse_args()
    st = dict(kv.split('=', 1) for kv in a.set)
    work = a.work or os.path.splitext(os.path.abspath(a.dst))[0] + '_build'
    if a.mode == 'convert':
        rep = convert(a.src, a.dst, a.profile, st, work, a.report)
    else:
        rep = feedback(a.src, a.dst, a.profile, st, work, a.report, workshop_exe(a.workshop))
    print(json.dumps({k: v for k, v in rep.items() if k != 'symbols'}, indent=1))


if __name__ == '__main__':
    main()
