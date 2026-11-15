"""Nested Everything SMS -> Genesis: an ahead-of-time Z80 -> 68000 translator plus a small 68000 runtime (sms2md_runtime.s) that
stands in for the Master System's VDP, PSG, pads and Sega mapper. The result is a plain Genesis ROM: no SMS-mode reset, no Power Base.

    python sms2md.py convert  <in.sms> <out.md> [--profile p.json] [--set key=value] [--report r.json] [--sites s.json]
    python sms2md.py feedback <in.sms> <out.md> [...]        (dynamic pass on BrokenNes's SMS core first, then convert)

How it works (the Nested recipe: static analysis + entry-point feedback + per-game profiles):
  1. Dynamic pass (Workshop --sms-run --md-sites, BrokenNes's own SMS core, headless): which ROM bytes ran as code in which slot,
     the targets of indirect jumps, and per instruction which memory regions its data accesses touched.
  2. Static pass: recursive descent from the reset/IRQ/NMI vectors and every traced instruction, per (bank, slot) unit.
  3. Per unit, Z80 flag liveness (S Z V C) so the 68000 code keeps the flags in the CCR and only saves them (MOVE SR) when needed.
  4. Code generation: one 68000 sequence per Z80 instruction; Z80 registers live in 68000 registers (see the runtime's header).
     Memory accesses the trace saw only in work RAM use the 68000's sign-extended short addressing directly ($C000 -> $FFC000);
     the rest go through checked helpers (ROM slots, cartridge RAM, mirror, mapper). Indirect jumps and RET go through a
     dispatch table per (bank, slot); a Z80 address nobody translated stops the game with a red screen and its address in RAM.
  5. Link with SGDK's m68k binutils, header checksum.
"""
import argparse
import hashlib
import json
import os
import subprocess
import sys
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import z80dec  # noqa: E402
from z80dec import decode, flow  # noqa: E402

BD = os.path.normpath(os.path.join(HERE, '..', '..'))
PIPE = os.path.join(BD, 'projects', 'nested-sms2md')

S, Z, V, C = 8, 4, 2, 1          # Z80 flags as 68000 CCR bits (N Z V C)
ALLF = S | Z | V | C
CCMAP = {'NZ': ('ne', Z), 'Z': ('eq', Z), 'NC': ('cc', C), 'C': ('cs', C), 'PO': ('vc', V), 'PE': ('vs', V), 'P': ('pl', S), 'M': ('mi', S)}
INV = {'ne': 'eq', 'eq': 'ne', 'cc': 'cs', 'cs': 'cc', 'vc': 'vs', 'vs': 'vc', 'pl': 'mi', 'mi': 'pl'}
RAMBIT = 8                        # trace region bit for work RAM


def load_manifest():
    with open(os.path.join(PIPE, 'pipeline.json'), encoding='utf-8') as f:
        return json.load(f)


def default_settings():
    return {s['id']: s['default'] for s in load_manifest()['settings']}


def sgdk_bin():
    for c in (os.environ.get('BROKENDEV_SGDK'), os.path.join(os.environ.get('BROKENDEV_HOME', ''), 'toolchains', 'sgdk', '2.11', 'sgdk'),
              r'T:\BrokenDev\env\toolchains\sgdk\2.11\sgdk'):
        if c and os.path.exists(os.path.join(c, 'bin', 'as.exe')):
            return os.path.join(c, 'bin')
    raise SystemExit('SGDK 2.11 binutils not found (BrokenDev toolchain sgdk; or set BROKENDEV_SGDK)')


# ------------------------------------------------------------------------------------------------ flags

def flag_info(ins):
    """(uses, defs, full) for Z80 flag liveness; 'full' False = the instruction keeps the flags it does not define."""
    op = ins.op
    if op in ('ADD', 'SUB', 'CP', 'AND', 'XOR', 'OR', 'NEG'):
        return 0, ALLF, True
    if op in ('ADC', 'SBC'):
        return C, ALLF, True
    if op in ('INC', 'DEC'):
        return 0, S | Z | V, False
    if op == 'ADD16':
        return 0, C, False
    if op in ('ADC16', 'SBC16'):
        return C, ALLF, True
    if op in ('RLCA', 'RRCA'):
        return 0, C, False
    if op in ('RLA', 'RRA'):
        return C, C, False
    if op in ('RLC', 'RRC', 'SLA', 'SRA', 'SLL', 'SRL'):
        return 0, ALLF, True
    if op in ('RL', 'RR'):
        return C, ALLF, True
    if op == 'BIT':
        return 0, S | Z | V, False
    if op == 'DAA':
        return ALLF, ALLF, True
    if op == 'SCF':
        return 0, C, False
    if op == 'CCF':
        return C, C, False
    if op in ('LDI', 'LDD', 'LDIR', 'LDDR'):
        return 0, V, False
    if op in ('CPI', 'CPD', 'CPIR', 'CPDR', 'RLD', 'RRD'):
        return 0, S | Z | V, False
    if op in ('OUTI', 'OUTD', 'OTIR', 'OTDR', 'INI', 'IND', 'INIR', 'INDR'):
        return 0, ALLF, True
    if op == 'IN' and ins.b == ('portc',):
        return 0, S | Z | V, False
    if op in ('LDAI', 'LDAR'):
        return 0, S | Z | V, False
    if op == 'POP' and ins.a == ('rr', 'AF'):
        return 0, ALLF, True
    if op == 'PUSH' and ins.a == ('rr', 'AF'):
        return ALLF, 0, False
    if op == 'EXAF':
        return ALLF, ALLF, True
    if op in ('JP', 'JR', 'CALL', 'RET') and ins.a is not None and ins.a[0] == 'cc':
        return CCMAP[ins.a[1]][1], 0, False
    return 0, 0, False


# ------------------------------------------------------------------------------------------------ analysis

class Program:
    def __init__(self, rom, trace, profile):
        self.rom = rom
        self.nbanks = max(1, len(rom) // 0x4000)
        self.trace = trace or {}
        self.profile = profile or {}
        self.code_seen = set()
        self.slot_banks = defaultdict(set)
        for off, slot in self.trace.get('code', []):
            pc = (off & 0x3FFF) | (slot << 14)
            self.code_seen.add((off >> 14, slot, pc))
            self.slot_banks[slot].add(off >> 14)
        self.slot_banks[0].add(0)
        self.mem = {}
        for key, r, w in self.trace.get('mem', []):
            if key >= 0:
                self.mem[key] = (r, w)
        self.ins = {}                     # (bank, slot, pc) -> Ins
        self.labels = set()
        self.notes = []

    def off(self, bank, pc):
        return bank * 0x4000 + (pc & 0x3FFF)

    def resolve(self, t, bank, slot):
        """Units a static target may land in."""
        s2 = t >> 14
        if s2 == 3:
            return []
        if s2 == slot:
            return [(bank, slot)]
        if s2 == 0 and self.slot_banks[0] == {0}:
            return [(0, 0)]
        seen = [b for b in self.slot_banks[s2] if (b, s2, t) in self.code_seen]
        return [(b, s2) for b in (seen or sorted(self.slot_banks[s2]))]

    def far_call_targets(self):
        """SDCC banked calls (LD E,bank / LD HL,address / CALL trampoline): the callee is an entry in that bank."""
        out = []
        for (b, s, pc), i in list(self.ins.items()):
            if i.op != 'LD' or i.a != ('r', 'E') or not i.b or i.b[0] != 'n':
                continue
            j = self.ins.get((b, s, i.nxt))
            if not j or j.op != 'LD16' or j.a != ('rr', 'HL') or j.b[0] != 'nn':
                continue
            k = self.ins.get((b, s, j.nxt))
            if not k or k.op != 'CALL' or k.a[0] == 'cc':
                continue
            bank, t = i.b[1], j.b[1]
            if 0x4000 <= t < 0xC000 and bank < self.nbanks:
                e = (bank, t >> 14, t)
                if e not in self.ins:
                    out.append(e)
                self.labels.add(e)
        return out

    def outi_blocks(self):
        """Unrolled OUTI blocks (16+ ED A3 in a row, SMSlib's VRAM copies) in the code banks: entered by computed jumps
        at any OUTI, so each one is an entry. Both byte alignments are tried."""
        out = []
        for (bank, slot) in {(0, 0)} | {(b, s) for (b, s, _) in self.code_seen}:
            base = bank * 0x4000
            data = self.rom[base:base + 0x4000]
            for al in (0, 1):
                k = al
                while k + 1 < len(data):
                    if data[k] == 0xED and data[k + 1] == 0xA3:
                        j = k
                        while j + 1 < len(data) and data[j] == 0xED and data[j + 1] == 0xA3:
                            j += 2
                        if (j - k) // 2 >= 16:
                            out += [(bank, slot, (slot << 14) | q) for q in range(k, j + 1, 2)]
                        k = j + 2
                    else:
                        k += 2
        return out

    def discover(self):
        work = [(0, 0, 0x0000), (0, 0, 0x0038), (0, 0, 0x0066)]
        for e in self.outi_blocks():
            work.append(e)
            self.labels.add(e)
        self.labels.update(work)
        work += sorted(self.code_seen)
        for e in self.profile.get('entries', []):
            b, s, pc = (int(x, 0) if isinstance(x, str) else x for x in e)
            work.append((b, s, pc))
            self.labels.add((b, s, pc))
        for t, opc in self.trace.get('targets', []):
            if t < 0:
                continue
            # the slot the target ran from: the code map knows
            for (b, s, pc) in self._at_off(t):
                self.labels.add((b, s, pc))
        while True:
            self._descend(work)
            work = self.far_call_targets()
            self.label_outi_runs()
            work += [k for k in self.labels if k not in self.ins and k[2] >> 14 == k[1]]
            if not work:
                break

    def _descend(self, work):
        while work:
            u = work.pop()
            if u in self.ins:
                continue
            bank, slot, pc = u
            if pc >> 14 != slot or bank >= self.nbanks:
                continue
            off = self.off(bank, pc)
            ins = decode(self.rom, off, pc)
            if (pc & 0x3FFF) + ins.size > 0x4000:
                continue
            self.ins[u] = ins
            falls, tgt, kind = flow(ins)
            if falls:
                if ins.nxt >> 14 == slot:
                    work.append((bank, slot, ins.nxt))
            if kind in ('call', 'callc', 'rst'):
                if ins.nxt >> 14 == slot:
                    self.labels.add((bank, slot, ins.nxt))
            if tgt is not None:
                for (b2, s2) in self.resolve(tgt, bank, slot):
                    work.append((b2, s2, tgt))
                    self.labels.add((b2, s2, tgt))

    def label_outi_runs(self):
        for (b, s, pc), ins in list(self.ins.items()):
            if ins.op != 'OUTI':
                continue
            # walk forward/backward through the run, decoding what the descent did not reach
            q = pc
            while (q & 0x3FFF) + 2 <= 0x4000:
                i2 = self.ins.get((b, s, q)) or decode(self.rom, self.off(b, q), q)
                if i2.op != 'OUTI':
                    break
                self.ins[(b, s, q)] = i2
                self.labels.add((b, s, q))
                q = i2.nxt
            nx = decode(self.rom, self.off(b, q), q)
            if (b, s, q) not in self.ins:
                self.ins[(b, s, q)] = nx
            self.labels.add((b, s, q))
            q = pc - 2
            while q >= (s << 14) and (q & 0x3FFF) >= 0:
                i2 = decode(self.rom, self.off(b, q), q)
                if i2.op != 'OUTI':
                    break
                self.ins[(b, s, q)] = i2
                self.labels.add((b, s, q))
                q -= 2

    def _at_off(self, off):
        out = []
        for slot in range(3):
            pc = (off & 0x3FFF) | (slot << 14)
            if (off >> 14, slot, pc) in self.code_seen:
                out.append((off >> 14, slot, pc))
        return out

    def units(self):
        u = defaultdict(list)
        for (b, s, pc), ins in self.ins.items():
            u[(b, s)].append(ins)
        for k in u:
            u[k].sort(key=lambda i: i.pc)
        return dict(u)

    def liveness(self, unit, inss):
        """live_out per pc (flags needed after each instruction)."""
        bank, slot = unit
        by = {i.pc: i for i in inss}
        live_in = {i.pc: 0 for i in inss}
        live_out = {i.pc: 0 for i in inss}
        cfk = bool(self.profile.get('calls_keep_flags'))
        retf = ALLF if self.profile.get('ret_flags', 'CZ') == 'all' else (C | Z)
        changed = True
        while changed:
            changed = False
            for i in reversed(inss):
                falls, tgt, kind = flow(i)
                uses, defs, full = flag_info(i)
                out = 0
                if kind in ('ret', 'ind'):
                    out = retf
                if kind in ('call', 'rst'):
                    nxt = live_in.get(i.nxt, ALLF) if falls else 0
                    lin = uses | (nxt if cfk else 0)
                else:
                    if falls:
                        out |= live_in.get(i.nxt, ALLF)
                    if kind == 'retc':
                        out |= retf
                    if tgt is not None and kind in ('jp', 'jpc', 'djnz'):
                        if tgt >> 14 == slot and tgt in by:
                            out |= live_in[tgt]
                        else:
                            out |= ALLF
                    if kind == 'callc':
                        pass          # taken: the callee defines; not taken: falls through (above)
                    lin = uses | (out & ~defs)
                if out != live_out[i.pc] or lin != live_in[i.pc]:
                    live_out[i.pc], live_in[i.pc] = out, lin
                    changed = True
        return live_in, live_out


# ------------------------------------------------------------------------------------------------ code generation

def lbl(bank, slot, pc):
    return 'Z%02X%d_%04X' % (bank, slot, pc)


def sx16(v):
    v &= 0xFFFF
    return v - 0x10000 if v >= 0x8000 else v


R16 = {'BC': 'd1', 'DE': 'd2', 'HL': 'd3'}
LOW8 = {'A': 'd0', 'C': 'd1', 'E': 'd2', 'L': 'd3'}
HIGH8 = {'B': 'd1', 'D': 'd2', 'H': 'd3'}
AREG = {'IX': 'a2', 'IY': 'a3', 'SP': 'a5'}


class Gen:
    def __init__(self, prog, settings):
        self.p = prog
        self.st = settings
        self.out = []
        self.stats = defaultdict(int)

    # ---- emitter state
    def reset_state(self):
        self.ccr_has = False      # the CCR holds the Z80 flags
        self.ccr_live = 0         # flags needed later that only the CCR holds
        self.x_ok = False         # X == Z80 carry

    XMN = {'add', 'addi', 'addq', 'addx', 'sub', 'subi', 'subq', 'subx', 'neg', 'negx', 'asl', 'asr', 'lsl', 'lsr', 'roxl', 'roxr',
           'abcd', 'sbcd', 'nbcd', 'jsr', 'jmp'}

    def raw(self, s):
        mn = s.split()[0].split('.')[0]
        if mn in self.XMN and not s.rstrip().endswith(('a0', 'a1', 'a2', 'a3', 'a5')):
            self.x_ok = False
        self.out.append('        ' + s)

    def emit(self, s, cc=True, x=False):
        if cc:
            self.clobber()
        if x:
            self.x_ok = False
        self.raw(s)

    def clobber(self):
        if self.ccr_live:
            self.raw('move    sr,d4')
            self.ccr_live = 0
        self.ccr_has = False

    def flush(self):
        if self.ccr_live:
            self.raw('move    sr,d4')
            self.ccr_live = 0

    def need_ccr(self):
        if not self.ccr_has:
            self.raw('move    d4,ccr')
            self.ccr_has = True
            self.x_ok = False

    def producer(self, s, live_out, defs, full, sets_x, merge_mask=None):
        """Emit a flag-setting 68000 instruction for a Z80 instruction that defines 'defs'."""
        keep = live_out & ~defs
        if keep and not full:
            self.clobber()
            self.raw(s)
            m = (defs | (0x10 if defs & C else 0)) if merge_mask is None else merge_mask
            self.raw('move    sr,d5')          # (d5: d6/d7 may hold the operand being written back)
            self.raw('andi.w  #0x%X,d5' % m)
            self.raw('andi.w  #0x%X,d4' % (0xFFFF ^ m))
            self.raw('or.w    d5,d4')
            self.ccr_has = False
            self.ccr_live = 0
            self.x_ok = sets_x
            return
        self.raw(s)
        self.ccr_has = True
        self.ccr_live = live_out & defs
        self.x_ok = sets_x and bool(defs & C)

    def parity_fix(self, live_out, val):
        """Z80 P/V = parity of 'val' after a logic op / shift / IN, when someone reads it."""
        if not (live_out & V):
            return
        self.raw('move    sr,d4')
        self.ccr_live = 0
        self.raw('move.b  %s,d6' % val)
        self.raw('jsr     (parity_v).l')
        self.ccr_has = False
        self.x_ok = False

    def carry_in(self, need_z):
        if not self.x_ok:
            self.clobber()
            self.raw('move.b  d4,d5')
            self.raw('lsr.b   #1,d5')
            self.x_ok = True
        if need_z:
            self.raw('ori     #4,ccr')

    # ---- registers
    def read8(self, r, into='d7'):
        if r in LOW8:
            return LOW8[r]
        if r in HIGH8:
            self.emit('move.w  %s,-(sp)' % HIGH8[r])
            self.emit('move.b  (sp)+,%s' % into)
            return into
        a = AREG['IX' if r.startswith('IX') else 'IY']
        if r.endswith('H'):
            self.emit('move.w  %s,-(sp)' % a)
            self.emit('move.b  (sp)+,%s' % into)
        else:
            self.emit('move.w  %s,%s' % (a, into))
        return into

    def write8(self, r, src):
        if r in LOW8:
            if src != LOW8[r]:
                self.emit('move.b  %s,%s' % (src, LOW8[r]))
            return
        if r in HIGH8:
            d = HIGH8[r]
            self.emit('move.w  %s,-(sp)' % d)
            self.emit('move.b  %s,(sp)' % src)
            self.emit('move.w  (sp)+,%s' % d)
            return
        a = AREG['IX' if r.startswith('IX') else 'IY']
        if r.endswith('H'):
            self.emit('move.w  %s,-(sp)' % a)
            self.emit('move.b  %s,(sp)' % src)
            self.raw('movea.w (sp)+,%s' % a)
        else:
            t = 'd5' if src != 'd5' else 'd7'
            self.emit('move.w  %s,%s' % (a, t))
            self.emit('move.b  %s,%s' % (src, t))
            self.raw('movea.w %s,%s' % (t, a))

    # ---- memory
    def site_ram_only(self, ins, write):
        if self.st.get('memory') == 'checked':
            return False
        k = self.p.off(self.cur[0], ins.pc)
        m = self.p.mem.get(k)
        if not m:
            return False
        r, w = m
        return (r | w) == RAMBIT and (r or w)

    def mem(self, ins, m, write=False):
        """('fast', operand) or ('chk', None) with the address in d7."""
        k = m[0]
        if k == 'abs':
            nn = m[1]
            if 0xC000 <= nn < 0xE000:
                return 'fast', '(%d).w' % sx16(nn)
            if 0xE000 <= nn < 0xFFFC or (nn >= 0xFFFC and not write):
                return 'fast', '(%d).w' % sx16(nn - 0x2000)
            if not write and nn < 0xC000:
                slot = nn >> 14
                if slot == 0 and self.p.slot_banks[0] == {0} and nn >= 0:
                    return 'fast', '(SMSROM+%d).l' % nn
                self.emit('movea.l (%d).w,a0' % sx16(0xF020 + 4 * slot), cc=False)
                return 'fast', '%d(a0)' % (nn - slot * 0x4000)
            self.emit('move.w  #%d,d7' % nn)
            return 'chk', None
        if k == 'm':
            rr = m[1]
            if rr == 'SP':
                return 'fast', '(a5)'
            if self.site_ram_only(ins, write) or self.st.get('memory') == 'trust_ram':
                self.raw('movea.w %s,a0' % R16[rr])
                self.stats['mem_fast'] += 1
                return 'fast', '(a0)'
            self.emit('move.w  %s,d7' % R16[rr])
            self.stats['mem_checked'] += 1
            return 'chk', None
        if k == 'ix':
            a = AREG[m[1]]
            d = m[2]
            if self.site_ram_only(ins, write) or self.st.get('memory') == 'trust_ram':
                self.stats['mem_fast'] += 1
                return 'fast', '%d(%s)' % (d, a)
            self.raw('lea     %d(%s),a0' % (d, a))
            self.emit('move.w  a0,d7')
            self.stats['mem_checked'] += 1
            return 'chk', None
        raise ValueError(m)

    def src8(self, ins, o, into='d6'):
        """Operand string for an 8-bit source."""
        if o[0] == 'r':
            return self.read8(o[1], into)
        if o[0] == 'n':
            return '#%d' % o[1]
        if self.st.get('guard_reads') in (True, 'true', '1', 1) and o[0] in ('m', 'ix') and o[1] != 'SP' and self.site_ram_only(ins, False):
            # guarded fast read: shared helpers (memcpy and friends) may get a ROM pointer the dynamic pass never saw
            if o[0] == 'm':
                self.emit('move.w  %s,d7' % R16[o[1]])
            else:
                self.raw('lea     %d(%s),a0' % (o[2], AREG[o[1]]))
                self.emit('move.w  a0,d7')
            self.emit('cmpi.w  #0xC000,d7')
            self.raw('bcs.s   1f')
            self.raw('movea.w d7,a0')
            self.raw('move.b  (a0),%s' % into)
            self.raw('bra.s   2f')
            self.out.append('1:')
            self.raw('jsr     (rd8).l')
            if into != 'd6':
                self.raw('move.b  d6,%s' % into)
            self.out.append('2:')
            self.stats['mem_guarded'] += 1
            return into
        kind, opnd = self.mem(ins, o)
        if kind == 'fast':
            return opnd
        self.emit('jsr     (rd8).l')
        if into != 'd6':
            self.emit('move.b  d6,%s' % into)
        return into

    def isdreg(self, s):
        return len(s) == 2 and s[0] == 'd'

    # ---- 16-bit memory
    def load16(self, ins, nn, dst):
        """dst: a data register name; loads (nn) little-endian."""
        if 0xC000 <= nn < 0xFFFF:
            a = nn if nn < 0xE000 else nn - 0x2000
            self.emit('move.b  (%d).w,-(sp)' % sx16(a + 1))
            self.emit('move.w  (sp)+,%s' % dst)
            self.emit('move.b  (%d).w,%s' % (sx16(a), dst))
            return
        self.emit('move.w  #%d,d7' % ((nn + 1) & 0xFFFF))
        self.emit('jsr     (rd8).l')
        self.emit('move.b  d6,-(sp)')
        self.emit('move.w  (sp)+,d5')
        self.emit('move.w  #%d,d7' % nn)
        self.emit('jsr     (rd8).l')
        self.emit('move.b  d6,d5')
        if dst != 'd5':
            self.emit('move.w  d5,%s' % dst)

    def store16(self, ins, nn, src):
        if 0xC000 <= nn < 0xDFFF:
            self.emit('move.b  %s,(%d).w' % (src, sx16(nn)))
            self.emit('move.w  %s,-(sp)' % src)
            self.emit('move.b  (sp)+,(%d).w' % sx16(nn + 1))
            return
        self.emit('move.w  %s,-(sp)' % src)
        self.emit('move.w  #%d,d7' % nn)
        self.emit('move.b  1(sp),d6')
        self.emit('jsr     (wr8).l')
        self.emit('move.w  #%d,d7' % ((nn + 1) & 0xFFFF))
        self.emit('move.b  (sp),d6')
        self.emit('jsr     (wr8).l')
        self.raw('addq.l  #2,sp')

    # ---- control flow
    def jump(self, t, cond=None):
        """Jump to Z80 address t (cond: 68000 condition or None)."""
        bank, slot = self.cur
        s2 = t >> 14
        static = None
        if s2 == slot and (bank, slot, t) in self.p.ins:
            static = lbl(bank, slot, t)
        elif s2 == 0 and self.p.slot_banks[0] == {0} and (0, 0, t) in self.p.ins:
            static = lbl(0, 0, t)
        if static:
            if cond:
                self.raw('jb%s    %s' % (cond, static))
            else:
                self.raw('jra     %s' % static)
            return
        self.stats['dyn_jumps'] += 1
        if cond:
            self.raw('jb%s    1f' % INV[cond])
        self.raw('move.w  #%d,d7' % t)
        self.raw('jra     z_dispatch')
        if cond:
            self.out.append('1:')

    def call(self, ret, t):
        """Z80 CALL: the return address on the Z80 stack, then a 68000 JSR with a shadow entry (Z80 return, Z80 SP) so the
        matching RET can RTS straight back (return-address prediction); a mismatch takes z_ret_slow."""
        self.push_const(ret)
        if self.st.get('ret_predict', False) in (False, 'false', 'False', '0', 0):
            self.jump(t)
            return
        self.raw('move.w  a5,-(sp)')
        self.raw('move.w  #%d,-(sp)' % ret)
        bank, slot = self.cur
        s2 = t >> 14
        static = None
        if s2 == slot and (bank, slot, t) in self.p.ins:
            static = lbl(bank, slot, t)
        elif s2 == 0 and self.p.slot_banks[0] == {0} and (0, 0, t) in self.p.ins:
            static = lbl(0, 0, t)
        if static:
            self.raw('jbsr    %s' % static)
        else:
            self.stats['dyn_jumps'] += 1
            self.raw('move.w  #%d,d7' % t)
            self.raw('jsr     (z_dispatch).l')
        self.raw('addq.l  #4,sp')
        if slot and ret >> 14 == slot:
            # banked code: the callee may have mapped another bank into this slot (a far-call trampoline);
            # then the Z80 returns into that bank's code, not this one
            self.raw('cmpi.l  #DT_%02X_%d,(%d).w' % (bank, slot, sx16(0xF030 + 4 * slot)))
            self.raw('beq.s   4f')
            self.raw('move.w  #%d,d7' % ret)
            self.raw('jra     z_dispatch')
            self.out.append('4:')

    def push_const(self, v):
        self.raw('move.b  #%d,-(a5)' % (v >> 8))
        self.raw('move.b  #%d,-(a5)' % (v & 0xFF))

    def pop_d7(self):
        self.emit('move.b  1(a5),-(sp)')
        self.emit('move.w  (sp)+,d7')
        self.emit('move.b  (a5),d7')
        self.raw('addq.w  #2,a5')

    # ---- one instruction
    def ins(self, i, live_out):
        op, a, b = i.op, i.a, i.b
        uses, defs, full = flag_info(i)
        self.stats['ins'] += 1
        if op == 'NOP' or op in ('IM', 'LDIA', 'LDRA'):
            return
        if op == 'LD':
            return self.ld(i)
        if op in ('ADD', 'ADC', 'SUB', 'SBC', 'AND', 'XOR', 'OR', 'CP'):
            return self.alu(i, live_out)
        if op in ('INC', 'DEC'):
            return self.incdec(i, live_out)
        if op in ('RLC', 'RRC', 'RL', 'RR', 'SLA', 'SRA', 'SLL', 'SRL'):
            return self.rot(i, live_out)
        if op in ('BIT', 'SET', 'RES'):
            return self.bitop(i, live_out)
        if op == 'LD16':
            return self.ld16(i)
        if op in ('ADD16', 'ADC16', 'SBC16'):
            return self.add16(i, live_out)
        if op in ('INC16', 'DEC16'):
            r = a[1]
            q = 'addq.w' if op == 'INC16' else 'subq.w'
            if r in R16:
                self.emit('%s  #1,%s' % (q, R16[r]), x=True)
            else:
                self.raw('%s  #1,%s' % (q, AREG[r]))
            return
        if op == 'PUSH':
            r = a[1]
            if r == 'AF':
                self.clobber()
                self.raw('moveq   #0x1F,d6')
                self.raw('and.w   d4,d6')
                self.raw('lea     CCR2F,a0')
                self.raw('move.b  0(a0,d6.w),d6')
                self.raw('move.b  d0,-(a5)')
                self.raw('move.b  d6,-(a5)')
                return
            src = R16.get(r) or AREG[r]
            self.emit('move.w  %s,-(sp)' % src)
            self.emit('move.b  (sp)+,-(a5)')
            if src.startswith('a'):
                self.emit('move.w  %s,d7' % src)
                src = 'd7'
            self.emit('move.b  %s,-(a5)' % src)
            return
        if op == 'POP':
            r = a[1]
            if r == 'AF':
                self.emit('moveq   #0,d6')
                self.emit('move.b  (a5)+,d6')
                self.emit('move.b  (a5)+,d0')
                self.raw('lea     F2CCR,a0')
                self.emit('move.b  0(a0,d6.w),d4')
                self.ccr_has = False
                self.ccr_live = 0
                return
            if r in R16:
                d = R16[r]
                self.emit('move.b  1(a5),-(sp)')
                self.emit('move.w  (sp)+,%s' % d)
                self.emit('move.b  (a5),%s' % d)
                self.raw('addq.w  #2,a5')
            else:
                self.pop_d7()
                self.raw('movea.w d7,%s' % AREG[r])
            return
        if op == 'EXDEHL':
            self.raw('exg     d2,d3')
            return
        if op == 'EXX':
            self.raw('exg     d1,a4')
            self.emit('move.w  (DE2).w,d7')
            self.emit('move.w  d2,(DE2).w')
            self.emit('move.w  d7,d2')
            self.emit('move.w  (HL2).w,d7')
            self.emit('move.w  d3,(HL2).w')
            self.emit('move.w  d7,d3')
            return
        if op == 'EXAF':
            self.clobber()
            self.raw('move.b  (A2V).w,d6')
            self.raw('move.b  d0,(A2V).w')
            self.raw('move.b  d6,d0')
            self.raw('move.w  (F2V).w,d6')
            self.raw('move.w  d4,(F2V).w')
            self.raw('move.w  d6,d4')
            return
        if op == 'EXSP':
            r = a[1]
            self.emit('move.b  1(a5),-(sp)')
            self.emit('move.w  (sp)+,d7')
            self.emit('move.b  (a5),d7')
            src = R16.get(r)
            if not src:
                self.emit('move.w  %s,d5' % AREG[r])
                src = 'd5'
            self.emit('move.b  %s,(a5)' % src)
            self.emit('move.w  %s,-(sp)' % src)
            self.emit('move.b  (sp)+,1(a5)')
            if r in R16:
                self.emit('move.w  d7,%s' % R16[r])
            else:
                self.raw('movea.w d7,%s' % AREG[r])
            return
        if op == 'LDSP':
            r = a[1]
            if r in R16:
                self.raw('movea.w %s,a5' % R16[r])
            else:
                self.raw('movea.l %s,a5' % AREG[r])
            return
        if op in ('RLCA', 'RRCA', 'RLA', 'RRA'):
            m = {'RLCA': 'rol.b', 'RRCA': 'ror.b', 'RLA': 'roxl.b', 'RRA': 'roxr.b'}[op]
            if op in ('RLA', 'RRA'):
                self.carry_in(False)
            self.producer('%s  #1,d0' % m, live_out, C, False, op in ('RLA', 'RRA'))
            return
        if op == 'CPL':
            self.emit('not.b   d0')
            return
        if op == 'NEG':
            self.clobber()
            self.producer('neg.b   d0', live_out, ALLF, True, True)
            return
        if op == 'SCF' or op == 'CCF':
            self.clobber()
            self.raw('%s.b  #0x11,d4' % ('ori' if op == 'SCF' else 'eori'))
            self.x_ok = False
            return
        if op == 'DAA':
            return self.unsupported(i)
        if op in ('JP', 'JR'):
            if a[0] == 'cc':
                cc = CCMAP[a[1]][0]
                self.need_ccr()
                self.flush()
                self.jump(b[1], cc)
            else:
                self.flush()
                self.jump(a[1])
            return
        if op == 'DJNZ':
            self.emit('subi.w  #0x100,d1', x=True)
            self.emit('cmpi.w  #0x100,d1')
            self.jump(a[1], 'cc')
            return
        if op == 'CALL' or op == 'RST':
            if op == 'CALL' and a[0] == 'cc':
                cc = CCMAP[a[1]][0]
                tgt = b[1]
                self.need_ccr()
                self.flush()
                self.raw('jb%s    2f' % INV[cc])
                self.call(i.nxt, tgt)
                self.out.append('2:')
                return
            self.flush()
            self.call(i.nxt, a[1])
            return
        if op in ('RET', 'RETI', 'RETN'):
            if a is not None:
                cc = CCMAP[a[1]][0]
                self.need_ccr()
                self.flush()
                self.raw('jb%s    2f' % INV[cc])
                self.ret_body()
                self.out.append('2:')
                return
            self.flush()
            self.ret_body()
            return
        if op == 'JPIND':
            r = a[1]
            self.flush()
            self.emit('move.w  %s,d7' % (R16.get(r) or AREG[r]))
            self.raw('jra     z_dispatch')
            self.stats['dyn_jumps'] += 1
            return
        if op == 'HALT':
            self.flush()
            self.emit('jsr     (z_halt).l')
            return
        if op == 'DI':
            self.raw('sf      (IFF).w')
            return
        if op == 'EI':
            self.emit('jsr     (z_ei).l')
            return
        if op in ('LDAI', 'LDAR'):
            # P/V = IFF2: code that saves the interrupt state this way (LD A,I / PUSH AF / DI ... POP AF / JP PO / EI, the
            # gg2sms runtime's colour trap does) must see interrupts on, or it never re-enables them
            self.clobber()
            self.raw('clr.b   d0' if op == 'LDAI' else 'move.b  (HVC+1).l,d0')
            self.raw('tst.b   d0')
            self.raw('move    sr,d5')
            self.raw('andi.w  #0xC,d5')
            self.raw('tst.b   (IFF).w')
            self.raw('beq.s   1f')
            self.raw('ori.w   #2,d5')
            self.out.append('1:')
            self.raw('andi.w  #0xFFF1,d4')
            self.raw('or.w    d5,d4')
            self.ccr_has = False
            self.ccr_live = 0
            return
        if op == 'OUT':
            return self.out_(i)
        if op == 'IN':
            return self.in_(i, live_out)
        helpers = {'LDI': 'z_ldi', 'LDD': 'z_ldd', 'LDIR': 'z_ldir', 'LDDR': 'z_lddr', 'CPI': 'z_cpi', 'CPD': 'z_cpd', 'CPIR': 'z_cpir',
                   'CPDR': 'z_cpdr', 'OUTI': 'z_outi', 'OUTD': 'z_outd', 'OTIR': 'z_otir', 'OTDR': 'z_otdr', 'INI': 'z_ini', 'IND': 'z_ind',
                   'INIR': 'z_inir', 'INDR': 'z_indr', 'RLD': 'z_rld', 'RRD': 'z_rrd'}
        if op in helpers:
            self.clobber()
            self.raw('jsr     (%s).l' % helpers[op])
            self.ccr_has = False
            self.x_ok = False
            return
        return self.unsupported(i)

    def unsupported(self, i):
        self.stats['unsupported'] += 1
        self.p.notes.append('unsupported %r' % i)
        self.emit('move.w  #%d,d7' % i.pc)
        self.raw('jmp     z_unsupported')

    def ret_body(self):
        if self.st.get('ret_predict', False) in (False, 'false', 'False', '0', 0):
            self.pop_d7()
            self.raw('jra     z_dispatch')
            return
        self.raw('move.w  a5,d5')
        self.pop_d7()
        self.raw('cmp.w   4(sp),d7')
        self.raw('bne.s   3f')
        self.raw('cmp.w   6(sp),d5')
        self.raw('bne.s   3f')
        self.raw('rts')
        self.out.append('3:')
        self.raw('jra     z_ret_slow')

    def ld(self, i):
        a, b = i.a, i.b
        if a[0] == 'r':
            if b[0] == 'r' and a[1] == b[1]:
                return
            if a[1] in LOW8:
                if b[0] == 'r' and b[1] in HIGH8:
                    self.read8(b[1], LOW8[a[1]])          # straight into the destination
                    return
                src = self.src8(i, b, 'd6')
                self.emit('move.b  %s,%s' % (src, LOW8[a[1]]))
            elif a[1] in HIGH8 and b[0] == 'n':
                d = HIGH8[a[1]]
                self.emit('andi.w  #0x00FF,%s' % d)
                if b[1]:
                    self.emit('ori.w   #0x%02X00,%s' % (b[1], d))
            else:
                src = self.src8(i, b, 'd6')
                self.write8(a[1], src)
            return
        # store to memory
        if b[0] == 'r':
            val = self.read8(b[1], 'd6')
        else:
            val = '#%d' % b[1]
        kind, opnd = self.mem(i, a, write=True)
        if kind == 'fast':
            self.emit('move.b  %s,%s' % (val, opnd))
            return
        if val != 'd6':
            self.emit('move.b  %s,d6' % val)
        self.emit('jsr     (wr8).l')

    def alu(self, i, live_out):
        op = i.op
        uses, defs, full = flag_info(i)
        b = i.b
        src = self.src8(i, b, 'd6')
        imm = src.startswith('#')
        if op in ('ADD', 'SUB', 'AND', 'OR', 'CP'):
            m = {'ADD': 'add', 'SUB': 'sub', 'AND': 'and', 'OR': 'or', 'CP': 'cmp'}[op]
            if imm:
                m += 'i'
            self.clobber()
            self.producer('%s.b %s,d0' % (m, src), live_out, ALLF, True, op in ('ADD', 'SUB'))
            if op in ('AND', 'OR'):
                self.parity_fix(live_out, 'd0')
            return
        if op == 'XOR':
            if imm:
                self.clobber()
                self.producer('eori.b  %s,d0' % src, live_out, ALLF, True, False)
            else:
                if not self.isdreg(src):
                    self.emit('move.b  %s,d6' % src)
                    src = 'd6'
                self.clobber()
                self.producer('eor.b   %s,d0' % src, live_out, ALLF, True, False)
            self.parity_fix(live_out, 'd0')
            return
        # ADC / SBC
        if not self.isdreg(src):
            self.emit('move.b  %s,d6' % src)
            src = 'd6'
        self.carry_in(bool(live_out & Z))
        self.producer('%s.b  %s,d0' % ('addx' if op == 'ADC' else 'subx', src), live_out, ALLF, True, True)

    def incdec(self, i, live_out):
        q = 'addq.b' if i.op == 'INC' else 'subq.b'
        a = i.a
        if a[0] == 'r':
            r = a[1]
            if r in LOW8:
                self.producer_rmw('%s  #1,%s' % (q, LOW8[r]), live_out, S | Z | V, False)
                return
            v = self.read8(r, 'd6')
            self.producer_rmw('%s  #1,d6' % q, live_out, S | Z | V, False)
            self.write8(r, 'd6')
            return
        kind, opnd = self.mem(i, a, write=True)
        if kind == 'fast':
            self.producer_rmw('%s  #1,%s' % (q, opnd), live_out, S | Z | V, False)
            return
        self.emit('jsr     (rd8).l')
        self.producer_rmw('%s  #1,d6' % q, live_out, S | Z | V, False)
        self.emit('jsr     (wr8).l')

    def producer_rmw(self, s, live_out, defs, sets_x):
        keep = live_out & ~defs
        if not keep:
            self.clobber()
        self.producer(s, live_out, defs, False, sets_x)
        self.x_ok = False

    def rot(self, i, live_out):
        op = i.op
        m = {'RLC': 'rol.b', 'RRC': 'ror.b', 'RL': 'roxl.b', 'RR': 'roxr.b', 'SLA': 'asl.b', 'SRA': 'asr.b', 'SLL': 'lsl.b', 'SRL': 'lsr.b'}[op]
        sets_x = op not in ('RLC', 'RRC')
        a = i.a
        mem = None
        if a[0] == 'r' and a[1] in LOW8:
            reg = LOW8[a[1]]
        elif a[0] == 'r':
            reg = self.read8(a[1], 'd6')
        else:
            kind, opnd = self.mem(i, a, write=True)
            if kind == 'fast':
                self.emit('move.b  %s,d6' % opnd)
                mem = ('fast', opnd)
            else:
                self.emit('jsr     (rd8).l')
                mem = ('chk', None)
            reg = 'd6'
        if op in ('RL', 'RR'):
            self.carry_in(False)
        else:
            self.clobber()
        self.producer('%s  #1,%s' % (m, reg), live_out, ALLF, True, sets_x)
        if op == 'SLL':
            self.raw('move    sr,d4')
            self.raw('ori.b   #1,%s' % reg)
            self.raw('andi.b  #0xFB,d4')
            self.ccr_has = False
            self.ccr_live = 0
        if op != 'SLL':
            self.parity_fix(live_out, reg)
        if a[0] == 'r' and a[1] not in LOW8:
            self.write8(a[1], 'd6')
        elif mem and mem[0] == 'fast':
            self.emit('move.b  d6,%s' % mem[1])
        elif mem:
            self.emit('jsr     (wr8).l')

    def bitop(self, i, live_out):
        op = i.op
        k = i.a[1]
        t = i.b
        if t[0] == 'r' and (t[1] in LOW8 or t[1] in HIGH8):
            reg = LOW8.get(t[1]) or HIGH8[t[1]]
            bit = k + (8 if t[1] in HIGH8 else 0)
            if op == 'BIT':
                self.producer_rmw('btst    #%d,%s' % (bit, reg), live_out, S | Z | V, False)
            else:
                self.emit('%s    #%d,%s' % ('bset' if op == 'SET' else 'bclr', bit, reg))
            return
        if t[0] == 'r':
            v = self.read8(t[1], 'd6')
            if op == 'BIT':
                self.producer_rmw('btst    #%d,d6' % k, live_out, S | Z | V, False)
            else:
                self.emit('%s    #%d,d6' % ('bset' if op == 'SET' else 'bclr', k))
                self.write8(t[1], 'd6')
            return
        kind, opnd = self.mem(i, t, write=(op != 'BIT'))
        if kind == 'fast':
            if op == 'BIT':
                self.producer_rmw('btst    #%d,%s' % (k, opnd), live_out, S | Z | V, False)
            else:
                self.emit('%s    #%d,%s' % ('bset' if op == 'SET' else 'bclr', k, opnd))
            return
        self.emit('jsr     (rd8).l')
        if op == 'BIT':
            self.producer_rmw('btst    #%d,d6' % k, live_out, S | Z | V, False)
        else:
            self.emit('%s    #%d,d6' % ('bset' if op == 'SET' else 'bclr', k))
            self.emit('jsr     (wr8).l')

    def ld16(self, i):
        a, b = i.a, i.b
        if a[0] == 'rr' and b[0] == 'nn':
            r, v = a[1], b[1]
            if r in R16:
                self.emit('move.w  #%d,%s' % (v, R16[r]))
            elif r == 'SP':
                if v == 0 or v >= 0xE000:
                    v = (v - 0x2000) & 0xFFFF
                self.raw('movea.w #%d,a5' % sx16(v))
            else:
                self.raw('movea.w #%d,%s' % (sx16(v), AREG[r]))
            return
        if a[0] == 'rr':
            r, nn = a[1], b[1]
            if r in R16:
                self.load16(i, nn, R16[r])
            else:
                self.load16(i, nn, 'd5')
                self.raw('movea.w d5,%s' % AREG[r])
            return
        r, nn = b[1], a[1]
        if r in R16:
            self.store16(i, nn, R16[r])
        else:
            self.emit('move.w  %s,d5' % AREG[r])
            self.store16(i, nn, 'd5')

    def add16(self, i, live_out):
        op = i.op
        d, s = i.a[1], i.b[1]
        if s in R16:
            src = R16[s]
        else:
            self.emit('move.w  %s,d6' % AREG[s])
            src = 'd6'
        if d in R16:
            dst = R16[d]
        else:
            self.emit('move.w  %s,d7' % AREG[d])
            dst = 'd7'
            if s == d:
                src = 'd7'
        if op == 'ADD16':
            keep = live_out & ~C
            if not keep:
                self.clobber()
            self.producer('add.w   %s,%s' % (src, dst), live_out, C, False, True)
        else:
            self.carry_in(bool(live_out & Z))
            self.producer('%s.w  %s,%s' % ('addx' if op == 'ADC16' else 'subx', src, dst), live_out, ALLF, True, True)
        if dst == 'd7':
            self.raw('movea.w d7,%s' % AREG[d])

    def out_(self, i):
        a, b = i.a, i.b
        if a[0] == 'port':
            p = a[1]
            cls = p & 0xC1
            if cls in (0x40, 0x41):
                self.emit('move.b  d0,(PSGP).l')
            elif cls == 0x80 and self.st.get('video') != 'mode4':
                # inline data-port write: into the shadow at the VDP address (a6), unit marked for the VInt flush
                self.raw('sf      (VLATCH).w')
                self.emit('move.b  d0,(a6)+')
                self.emit('move.w  a6,d7')
                self.emit('subq.w  #1,d7')
                self.emit('lsr.w   #5,d7')
                self.raw('lea     (DIRTY).w,a0')
                self.emit('st      0(a0,d7.w)')
            elif cls == 0x80:
                self.emit('move.b  d0,d6')
                self.emit('jsr     (vdpw_data).l')
            elif cls == 0x81:
                # control port: the first byte of a pair only latches, inline
                self.clobber()
                self.raw('tst.b   (VLATCH).w')
                self.raw('bne.s   1f')
                self.raw('move.b  d0,(VLOW).w')
                self.raw('st      (VLATCH).w')
                self.raw('bra.s   2f')
                self.out.append('1:')
                self.raw('move.b  d0,d6')
                self.raw('jsr     (vdpw_ctrl).l')
                self.out.append('2:')
            return
        val = self.read8(b[1], 'd6') if b[0] == 'r' else '#0'
        if val != 'd6':
            self.emit('move.b  %s,d6' % val)
        self.emit('move.b  d1,d7')
        self.emit('jsr     (port_out).l')

    def in_(self, i, live_out):
        a, b = i.a, i.b
        if b[0] == 'port':
            p = b[1]
            cls = p & 0xC1
            if cls == 0x40:
                self.emit('jsr     (z_vcount).l')
                self.emit('move.b  d6,d0')
            elif cls == 0x41:
                self.emit('move.b  (HVC+1).l,d0')
            elif cls == 0x80:
                self.emit('jsr     (vdpr_data).l')
                self.emit('move.b  d6,d0')
            elif cls == 0x81:
                self.emit('jsr     (vdpr_stat).l')
                self.emit('move.b  d6,d0')
            elif cls == 0xC0:
                self.emit('move.b  (PAD1).w,d0')
            else:
                self.emit('st      d0')
            return
        self.emit('move.b  d1,d7')
        self.emit('jsr     (port_in).l')
        self.clobber()
        self.producer('tst.b   d6', live_out, S | Z | V, False, False)
        self.parity_fix(live_out, 'd6')
        if a is not None:
            self.write8(a[1], 'd6')

    # ---- whole program
    def generate(self):
        p = self.p
        units = p.units()
        idle = set(int(x, 0) if isinstance(x, str) else x for x in p.profile.get('idle_pc', []))
        self.repl = {}
        pdir = os.path.dirname(self.st.get('_profile_path', '')) if self.st.get('_profile_path') else os.path.join(PIPE, 'profiles')
        for r in p.profile.get('replace', []):
            k = (r['bank'], r['slot'], int(r['pc'], 0) if isinstance(r['pc'], str) else r['pc'])
            p.labels.add(k)
            self.repl[k] = os.path.join(pdir, r['asm']).replace(os.sep, '/')
        for (bank, slot) in sorted(units):
            inss = units[(bank, slot)]
            self.cur = (bank, slot)
            live_in, live_out = p.liveness((bank, slot), inss)
            self.out.append('| ---- unit bank %d slot %d: %d instructions' % (bank, slot, len(inss)))
            self.reset_state()
            prev = None
            for i in inss:      # fall-through targets that are not the next instruction in address order need labels
                if prev is not None and flow(prev)[0] and prev.nxt != i.pc and (bank, slot, prev.nxt) in p.ins:
                    p.labels.add((bank, slot, prev.nxt))
                prev = i
            prev = None
            skip = 0
            for k, i in enumerate(inss):
                if skip:
                    skip -= 1
                    prev = i
                    continue
                key = (bank, slot, i.pc)
                contiguous = prev is not None and flow(prev)[0] and prev.nxt == i.pc
                if prev is not None and flow(prev)[0] and not contiguous:
                    self.flush()
                    self.jump(prev.nxt)
                if key in p.labels or not contiguous:
                    if contiguous:
                        self.flush()
                    if key in self.repl:
                        # a profile's hand-written 68000 routine takes the entry; the translation stays as ORIG_<label>
                        self.out.append('%s:' % lbl(bank, slot, i.pc))
                        self.out.append('        .include "%s"' % self.repl[key])
                        self.out.append('ORIG_%s:' % lbl(bank, slot, i.pc))
                        self.stats['replaced'] += 1
                    else:
                        self.out.append('%s:' % lbl(bank, slot, i.pc))
                    p.labels.add(key)
                    self.reset_state()
                self.out.append('| %r' % i)
                if self.st.get('debug_trail') in (True, 'true', '1', 1):
                    self.raw('lea     (%d).w,a1' % sx16(i.pc | (bank << 16) if False else i.pc))
                if i.pc in idle and slot == 0:
                    self.clobber()
                    self.raw('jsr     (z_idle).l')
                run = 1
                rest = 1
                if i.op == 'OUTI':
                    while (k + rest < len(inss) and inss[k + rest].op == 'OUTI' and inss[k + rest].pc == inss[k + rest - 1].nxt):
                        rest += 1
                if rest > 1 and all((bank, slot, inss[k + j].pc) in p.labels for j in range(1, rest)):
                    # every OUTI of an unrolled run is an entry (computed jumps): each one copies the rest of the run
                    self.out.append('| ... %d OUTI to the end of the run' % rest)
                    self.clobber()
                    self.raw('move.w  #%d,d5' % rest)
                    self.raw('jsr     (z_outin).l')
                    self.jump(inss[k + rest - 1].nxt)
                    self.stats['outi_run_entries'] += 1
                    prev = i
                    continue
                if i.op == 'OUTI':
                    while (k + run < len(inss) and inss[k + run].op == 'OUTI' and inss[k + run].pc == inss[k + run - 1].nxt
                           and (bank, slot, inss[k + run].pc) not in p.labels):
                        run += 1
                loop = ((i.op == 'OTIR' or (
                    i.op == 'OUTI' and run == 1 and k + 1 < len(inss) and inss[k + 1].op == 'JR' and inss[k + 1].a == ('cc', 'NZ')
                    and inss[k + 1].b[1] == i.pc and inss[k + 1].pc == i.nxt and (bank, slot, inss[k + 1].pc) not in p.labels)))
                if loop:
                    # OTIR, or OUTI / JR NZ back to it: B bytes (0 = 256) in one bulk copy
                    self.out.append('| ... OTIR loop')
                    self.clobber()
                    self.raw('moveq   #0,d5')
                    self.raw('move.w  d1,d5')
                    self.raw('lsr.w   #8,d5')
                    self.raw('bne.s   1f')
                    self.raw('move.w  #256,d5')
                    self.out.append('1:')
                    self.raw('jsr     (z_outin).l')
                    self.ccr_has = False
                    self.x_ok = False
                    self.stats['otir_loops'] += 1
                    skip = 0 if i.op == 'OTIR' else 1
                elif run > 1:
                    # a run of OUTIs (an unrolled VRAM upload): one bulk copy
                    self.out.append('| ... %d OUTI in a row' % run)
                    self.clobber()
                    self.raw('moveq   #%d,d5' % run)
                    self.raw('jsr     (z_outin).l')
                    self.ccr_has = False
                    self.x_ok = False
                    self.stats['outi_runs'] += 1
                    skip = run - 1
                else:
                    self.ins(i, live_out[i.pc])
                prev = i
            if prev is not None and flow(prev)[0]:
                self.flush()
                self.raw('move.w  #%d,d7' % prev.nxt)
                self.raw('jra     z_dispatch')
        return units

    def tables(self, units):
        p = self.p
        o = self.out
        o.append('        .align 2')
        o.append('disptabs:')
        for s in range(4):
            o.append('        .long DTL_%d' % s if s < 3 else '        .long DTL_ERR')
        for s in range(3):
            o.append('DTL_%d:' % s)
            for bnk in range(p.nbanks):
                o.append('        .long %s' % ('DT_%02X_%d' % (bnk, s) if (bnk, s) in units else 'DT_ERR'))
        o.append('BANKPTR:')
        for bnk in range(p.nbanks):
            o.append('        .long SMSROM+%d' % (bnk * 0x4000))
        o.append('DTL_ERR:')
        for bnk in range(p.nbanks):
            o.append('        .long DT_ERR')
        for (bank, slot) in sorted(units):
            o.append('DT_%02X_%d:' % (bank, slot))
            base = slot << 14
            have = {pc for (b, s, pc) in p.labels if b == bank and s == slot and (b, s, pc) in p.ins}
            run = 0
            for k in range(0x4000):
                pc = base + k
                if pc in have:
                    if run:
                        o.append('        .rept %d\n        .long z_untranslated\n        .endr' % run)
                        run = 0
                    o.append('        .long %s' % lbl(bank, slot, pc))
                else:
                    run += 1
            if run:
                o.append('        .rept %d\n        .long z_untranslated\n        .endr' % run)
        o.append('DT_ERR:')
        o.append('        .rept 16384\n        .long z_untranslated\n        .endr')
        # look-up tables
        o.append('TT:')
        for k in range(4):
            for v in range(256):
                o.append('        .long 0x%08X' % sum(((v >> j) & 1) << (j * 4 + k) for j in range(8)))
        o.append('NTHI:')
        for h in range(256):
            g = ((h & 1) << 8) | ((h >> 1) & 1) << 11 | ((h >> 2) & 1) << 12 | ((h >> 3) & 1) << 13 | ((h >> 4) & 1) << 15
            o.append('        .word 0x%04X' % g)
        lv = self.st.get('levels', [0, 2, 5, 7])
        o.append('GENCOL:')
        for c in range(64):
            r, g, b = c & 3, (c >> 2) & 3, (c >> 4) & 3
            o.append('        .word 0x%04X' % ((lv[b] << 9) | (lv[g] << 5) | (lv[r] << 1)))
        o.append('CCR2F:')
        for c in range(32):
            n, z, v, cc = (c >> 3) & 1, (c >> 2) & 1, (c >> 1) & 1, c & 1
            o.append('        .byte 0x%02X' % ((n << 7) | (z << 6) | (v << 2) | cc))
        o.append('F2CCR:')
        for f in range(256):
            s_, z, v, cc = (f >> 7) & 1, (f >> 6) & 1, (f >> 2) & 1, f & 1
            o.append('        .byte 0x%02X' % ((cc << 4) | (s_ << 3) | (z << 2) | (v << 1) | cc))
        o.append('PARITY:')
        for v in range(256):
            o.append('        .byte %d' % (1 if bin(v).count('1') % 2 == 0 else 0))


def convert(rom, out_path, settings, profile, trace, work=None, report=None):
    work = work or os.path.splitext(out_path)[0] + '.build'
    os.makedirs(work, exist_ok=True)
    p = Program(rom, trace, profile)
    p.discover()
    g = Gen(p, settings)
    units = g.generate()
    g.tables(units)
    nb = p.nbanks
    nbanks_pow = 1
    while nbanks_pow < nb:
        nbanks_pow *= 2
    romfile = os.path.join(work, 'sms.bin')
    with open(romfile, 'wb') as f:
        f.write(rom)
    lab = lambda pc: lbl(0, 0, pc) if (0, 0, pc) in p.ins else 'z_untranslated'
    head = ['        .equ NBANKS, %d' % nbanks_pow,
            '        .equ VIDEO_MODE, %d' % (4 if settings.get('video') == 'mode4' else 5),
            '        .equ M4LEAD, %d' % int(settings.get('m4_lead', 3)),
            '        .include "%s"' % os.path.join(HERE, 'sms2md_runtime.s').replace('\\', '/'),
            '        .equ Z80_RESET, %s' % lab(0), '        .equ Z80_IRQ, %s' % lab(0x38), '        .equ Z80_NMI, %s' % lab(0x66)]
    tail = ['        .align 2', 'SMSROM:', '        .incbin "%s"' % romfile.replace('\\', '/'), '        .align 2', 'ROMEND:']
    src = os.path.join(work, 'game.s')
    with open(src, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(head + g.out + tail) + '\n')
    bin_ = sgdk_bin()
    obj = os.path.join(work, 'game.o')
    r = subprocess.run([os.path.join(bin_, 'as.exe'), '-m68000', '--register-prefix-optional', '-o', obj, src], capture_output=True, text=True)
    if r.returncode:
        sys.stderr.write(r.stderr[-4000:])
        raise SystemExit('as failed')
    raw = os.path.join(work, 'game.bin')
    r = subprocess.run([os.path.join(bin_, 'ld.exe'), '-e', '0', '-Ttext=0', '--oformat', 'binary', '-o', raw, obj, '-Map', os.path.join(work, 'game.map')],
                       capture_output=True, text=True)
    if r.returncode:
        sys.stderr.write(r.stderr[-4000:])
        raise SystemExit('ld failed')
    data = bytearray(open(raw, 'rb').read())
    if len(data) % 2:
        data.append(0)
    ck = sum((data[k] << 8) | data[k + 1] for k in range(0x200, len(data), 2)) & 0xFFFF
    data[0x18E:0x190] = bytes([ck >> 8, ck & 0xFF])
    with open(out_path, 'wb') as f:
        f.write(data)
    rep = {
        'pipeline': 'sms2md', 'in_sha256': hashlib.sha256(rom).hexdigest(), 'out_sha256': hashlib.sha256(data).hexdigest(),
        'out_size': len(data), 'units': {'%d/%d' % k: len(v) for k, v in sorted(units.items())},
        'instructions': len(p.ins), 'labels': len(p.labels), 'stats': dict(g.stats), 'settings': settings, 'notes': p.notes[:50],
        'over_2mb': len(data) > 0x200000,
    }
    if report:
        with open(report, 'w', encoding='utf-8') as f:
            json.dump(rep, f, indent=1)
    return rep


# ------------------------------------------------------------------------------------------------ dynamic pass

def workshop_exe(arg=None):
    for c in (arg, os.environ.get('BROKENDEV_WORKSHOP'),
              os.path.join(HERE, '..', '..', '..', 'Workshop', 'bin', 'Release', 'net10.0-windows', 'BrokenNes.Workshop.exe')):
        if c and os.path.exists(c):
            return os.path.abspath(c)
    raise SystemExit('BrokenNes.Workshop.exe not found (build Workshop, or set BROKENDEV_WORKSHOP / --workshop)')


def run_trace(rom_path, out_json, frames, inputs, exe):
    args = [exe, '--sms-run', rom_path, '--console', 'sms', '--frames', str(frames), '--md-sites', out_json, '--out', os.path.dirname(out_json)]
    if inputs:
        args += ['--input', inputs]
    r = subprocess.run(args, capture_output=True, text=True, timeout=1800)
    if r.returncode or not os.path.exists(out_json):
        raise SystemExit('dynamic pass failed: ' + (r.stdout or '')[-800:] + (r.stderr or '')[-800:])
    with open(out_json, encoding='utf-8') as f:
        return json.load(f)


def merge_traces(ts):
    if not ts:
        return {}
    code = {}
    targets = set()
    mem = {}
    for t in ts:
        for off, slot in t.get('code', []):
            code[(off, slot)] = 1
        for x in t.get('targets', []):
            targets.add(tuple(x))
        for k, r, w in t.get('mem', []):
            a, b = mem.get(k, (0, 0))
            mem[k] = (a | r, b | w)
    return {'code': [list(k) for k in sorted(code)], 'targets': [list(x) for x in sorted(targets)], 'mem': [[k, r, w] for k, (r, w) in sorted(mem.items())]}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('mode', choices=['convert', 'feedback'])
    ap.add_argument('inp')
    ap.add_argument('out')
    ap.add_argument('--profile')
    ap.add_argument('--set', action='append', default=[])
    ap.add_argument('--report')
    ap.add_argument('--sites', action='append', default=[], help='dynamic-pass JSON file(s) to use (convert mode)')
    ap.add_argument('--workshop')
    ap.add_argument('--work')
    a = ap.parse_args()
    rom = open(a.inp, 'rb').read()
    if len(rom) % 0x4000 == 512:
        rom = rom[512:]
    settings = default_settings()
    profile = {}
    if a.profile:
        with open(a.profile, encoding='utf-8') as f:
            profile = json.load(f)
        settings.update(profile.get('settings', {}))
        settings['_profile_path'] = os.path.abspath(a.profile)
    for kv in a.set:
        k, v = kv.split('=', 1)
        settings[k] = v
    traces = []
    for s in a.sites:
        with open(s, encoding='utf-8') as f:
            traces.append(json.load(f))
    if a.mode == 'feedback':
        exe = workshop_exe(a.workshop)
        work = a.work or os.path.splitext(a.out)[0] + '.build'
        os.makedirs(work, exist_ok=True)
        manifest = load_manifest()
        inputs = profile.get('inputs') or manifest['defaultInputs']
        for inp in inputs:
            j = os.path.join(work, 'sites_%s.json' % inp['id'])
            traces.append(run_trace(os.path.abspath(a.inp), j, inp['frames'], inp['input'], exe))
    rep = convert(rom, a.out, settings, profile, merge_traces(traces), a.work, a.report)
    print(json.dumps({k: rep[k] for k in ('out_sha256', 'out_size', 'units', 'instructions', 'stats', 'over_2mb')}, indent=1))


if __name__ == '__main__':
    main()
