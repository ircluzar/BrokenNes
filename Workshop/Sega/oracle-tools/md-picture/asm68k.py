"""A small text assembler for 68000 probe ROMs (Motorola syntax, the subset the probes need), plus a ROM image maker with a vector table.

Why it exists: probes for the Genesis VDP need loops, tables, interrupt handlers and thousands of timed reads that the hand-encoding assembler (m68kasm.py) cannot express.
Written for this repository from the 68000 programmer's reference; no third-party assembler source was read.

    rom = build_rom(src, vectors={30: 'vint', 28: 'hint'}, title='PROBE')

Syntax: one instruction per line, `label:` at the start, `;` comments, `name equ expr`, `org expr`, `dc.b/dc.w/dc.l a,b,c`, `ds.w n`, `align n`, `rept N ... endr` (text repetition, `\\@` is the repetition index).
Numbers: `$hex`, `0xhex`, decimal, `'c'`; expressions with + - * << >> & | ( ) and labels/equs.
Effective addresses: Dn An (An) (An)+ -(An) d(An) (d,An) d(An,Dm.w) d(An,Dm.l) (xxx).w (xxx).l xxx (= absolute long) d(PC) #imm; `sp` is a7.
"""
import re, struct

SIZES = {'b': 0, 'w': 1, 'l': 2}
CONDS = {'t': 0, 'f': 1, 'hi': 2, 'ls': 3, 'cc': 4, 'hs': 4, 'cs': 5, 'lo': 5, 'ne': 6, 'eq': 7, 'vc': 8, 'vs': 9, 'pl': 10, 'mi': 11, 'ge': 12, 'lt': 13, 'gt': 14, 'le': 15}
BIT_OPS = {'btst': 0, 'bchg': 1, 'bclr': 2, 'bset': 3}
SHIFT_TYPES = {'as': 0, 'ls': 1, 'rox': 2, 'ro': 3}


class AsmError(Exception):
    pass


def _num(tok, syms):
    t = tok.strip()
    t = re.sub(r'\$([0-9a-fA-F]+)', r'0x\1', t)
    t = re.sub(r"'(.)'", lambda m: str(ord(m.group(1))), t)
    try:
        return int(eval(t, {'__builtins__': {}}, syms)) if syms is not None else int(eval(t, {'__builtins__': {}}, {}))
    except NameError as e:
        raise _Unresolved(str(e))
    except Exception as e:
        raise AsmError('bad expression %r: %s' % (tok, e))


class _Unresolved(Exception):
    pass


class EA:
    def __init__(self, mode, reg, ext=None, kind=None, value=None, size_hint=None):
        self.mode, self.reg = mode, reg
        self.ext = ext or []       # list of (size, value-or-callable) extension words: size 'w' or 'l'
        self.kind = kind           # 'dn','an','imm','abs','pcrel',...
        self.value = value         # expression text for imm/abs/pcrel (resolved at encode time)

    def field(self):
        return self.mode << 3 | self.reg


REG_RE = re.compile(r'^(d[0-7]|a[0-7]|sp)$', re.I)


def _reg(tok):
    t = tok.strip().lower()
    if t == 'sp': return ('a', 7)
    m = re.match(r'^([da])([0-7])$', t)
    return (m.group(1), int(m.group(2))) if m else None


def parse_ea(text):
    t = text.strip()
    r = _reg(t)
    if r: return EA(0 if r[0] == 'd' else 1, r[1], kind=r[0] + 'n')
    if t.startswith('#'): return EA(7, 4, kind='imm', value=t[1:])
    m = re.match(r'^\((\w+)\)\+$', t)
    if m and _reg(m.group(1)) and _reg(m.group(1))[0] == 'a': return EA(3, _reg(m.group(1))[1], kind='postinc')
    m = re.match(r'^-\((\w+)\)$', t)
    if m and _reg(m.group(1)) and _reg(m.group(1))[0] == 'a': return EA(4, _reg(m.group(1))[1], kind='predec')
    m = re.match(r'^\((\w+)\)$', t)
    if m and _reg(m.group(1)) and _reg(m.group(1))[0] == 'a': return EA(2, _reg(m.group(1))[1], kind='ind')
    # d16(An), (d16,An), d8(An,Dm.w), d(PC)
    m = re.match(r'^\(?([^(),]*?)\)?\s*\(\s*(\w+)\s*(?:,\s*(\w+)(?:\.([wl]))?\s*)?\)$', t) or re.match(r'^\(([^(),]*),\s*(\w+)\s*(?:,\s*(\w+)(?:\.([wl]))?\s*)?\)$', t)
    if m:
        disp, base, idx, isz = m.group(1) or '0', m.group(2), m.group(3), m.group(4)
        if base.lower() == 'pc':
            if idx: return EA(7, 3, kind='pcidx', value=(disp, idx, isz or 'w'))
            return EA(7, 2, kind='pcrel', value=disp)
        b = _reg(base)
        if b and b[0] == 'a':
            if idx:
                return EA(6, b[1], kind='index', value=(disp, idx, isz or 'w'))
            return EA(5, b[1], kind='disp', value=disp)
    m = re.match(r'^\((.+)\)\.([wl])$', t) or re.match(r'^(.+)\.([wl])$', t)
    if m and not _reg(m.group(1)):
        return EA(7, 0 if m.group(2) == 'w' else 1, kind='abs' + m.group(2), value=m.group(1))
    return EA(7, 1, kind='absl', value=t)      # a bare expression is an absolute long address


class Asm68k:
    def __init__(self, origin=0x200):
        self.origin = origin
        self.syms = {}

    # ---------------------------------------------------------------- text handling
    def _preprocess(self, src):
        lines = []
        for raw in src.split('\n'):
            line = raw.split(';')[0].rstrip()
            lines.append(line)
        out = []
        i = 0
        # rept ... endr
        def expand(ls):
            res = []; j = 0
            while j < len(ls):
                l = ls[j]
                m = re.match(r'^\s*rept\s+(.+)$', l, re.I)
                if m:
                    depth = 1; k = j + 1
                    while k < len(ls):
                        if re.match(r'^\s*rept\b', ls[k], re.I): depth += 1
                        if re.match(r'^\s*endr\b', ls[k], re.I):
                            depth -= 1
                            if depth == 0: break
                        k += 1
                    body = ls[j + 1:k]
                    n = _num(m.group(1), self.syms)
                    for rep in range(n):
                        res += expand([b.replace('\\@', str(rep)) for b in body])
                    j = k + 1
                else:
                    res.append(l); j += 1
            return res
        return expand(lines)

    @staticmethod
    def _split_ops(text):
        ops = []; depth = 0; cur = ''
        for ch in text:
            if ch == '(': depth += 1
            elif ch == ')': depth -= 1
            if ch == ',' and depth == 0:
                ops.append(cur.strip()); cur = ''
            else: cur += ch
        if cur.strip(): ops.append(cur.strip())
        return ops

    # ---------------------------------------------------------------- the passes
    def assemble(self, src):
        lines = self._preprocess(src)
        # pass 1 sizes labels (instruction lengths do not depend on label values: absolute operands are .l unless marked .w, branches use a fixed form), pass 2 emits
        self.syms = {}
        for pass_no in (1, 2):
            self.pc = self.origin
            self.out = bytearray()
            self.pass_no = pass_no
            for ln in lines:
                self._line(ln)
        return bytes(self.out)

    def _line(self, ln):
        if not ln.strip(): return
        m = re.match(r'^([A-Za-z_.][\w.]*)\s*:(.*)$', ln)
        if m:
            if self.pass_no == 1: self.syms[m.group(1)] = self.pc
            ln = m.group(2)
            if not ln.strip(): return
        m = re.match(r'^\s*([A-Za-z_][\w]*)\s+equ\s+(.+)$', ln, re.I)
        if m:
            try: self.syms[m.group(1)] = _num(m.group(2), self.syms)
            except _Unresolved: self.syms[m.group(1)] = 0
            return
        m = re.match(r'^\s*([a-z][\w.]*)\s*(.*)$', ln, re.I)
        if not m: raise AsmError('cannot parse: ' + ln)
        mn, rest = m.group(1).lower(), m.group(2).strip()
        ops = self._split_ops(rest)
        self._instr(mn, ops, ln)

    def _val(self, expr):
        try: return _num(expr, self.syms)
        except _Unresolved:
            if self.pass_no == 1: return 0
            raise AsmError('undefined symbol in: ' + str(expr))

    def _w(self, v):
        self.out += struct.pack('>H', v & 0xFFFF); self.pc += 2

    def _l(self, v):
        self.out += struct.pack('>I', v & 0xFFFFFFFF); self.pc += 4

    def _emit_ext(self, ea, size):
        """extension words of an effective address (immediates use the operation size)"""
        k = ea.kind
        if k == 'imm':
            v = self._val(ea.value)
            if size == 'l': self._l(v)
            else: self._w(v & 0xFF if size == 'b' else v)
        elif k == 'absw': self._w(self._val(ea.value))
        elif k == 'absl': self._l(self._val(ea.value))
        elif k == 'disp': self._w(self._val(ea.value))
        elif k == 'pcrel':
            # displacement from the extension word's own address
            self._w(self._val(ea.value) - self.pc)
        elif k in ('index', 'pcidx'):
            disp, idx, isz = ea.value
            r = _reg(idx)
            w = (0x8000 if r[0] == 'a' else 0) | r[1] << 12 | (0x0800 if isz == 'l' else 0) | (self._val(disp) & 0xFF)
            if k == 'pcidx': w = (w & 0xFF00) | ((self._val(disp) - self.pc) & 0xFF)
            self._w(w)

    @staticmethod
    def _ext_len(ea, size):
        k = ea.kind
        if k == 'imm': return 4 if size == 'l' else 2
        if k == 'absl': return 4
        if k in ('absw', 'disp', 'pcrel', 'index', 'pcidx'): return 2
        return 0

    def _instr(self, mn, ops, ln):
        # directives
        if mn == 'org':
            v = self._val(ops[0])
            if v < self.pc: raise AsmError('org moves backwards')
            pad = v - self.pc
            self.out += b'\x00' * pad; self.pc = v; return
        if mn == 'align':
            n = self._val(ops[0])
            while self.pc % n: self.out += b'\x00'; self.pc += 1
            return
        m = re.match(r'^(dc|ds)\.([bwl])$', mn)
        if m:
            sz = m.group(2)
            if m.group(1) == 'ds':
                n = self._val(ops[0]) * {'b': 1, 'w': 2, 'l': 4}[sz]
                self.out += b'\x00' * n; self.pc += n; return
            for o in ops:
                v = self._val(o)
                if sz == 'b': self.out.append(v & 0xFF); self.pc += 1
                elif sz == 'w': self._w(v)
                else: self._l(v)
            if sz == 'b' and self.pc & 1: self.out.append(0); self.pc += 1
            return
        # split mnemonic and size
        size = None
        base = mn
        if '.' in mn:
            base, s = mn.rsplit('.', 1)
            if s in SIZES: size = s
            elif s == 's' and base.startswith('b'): size = 'b'      # bra.s
            else: raise AsmError('bad size in ' + mn)
        fn = getattr(self, '_op_' + base, None)
        if fn is None:
            # Bcc / DBcc / Scc
            if base[0] == 'b' and base[1:] in CONDS and base not in ('bchg', 'bclr', 'bset', 'btst'):
                return self._branch(base[1:], size, ops)
            if base.startswith('db') and base[2:] in CONDS or base == 'dbra':
                return self._dbcc('f' if base == 'dbra' else base[2:], ops)
            if base[0] == 's' and base[1:] in CONDS and base not in ('sub', 'subq'):
                return self._scc(base[1:], ops)
            raise AsmError('unknown instruction: ' + ln)
        fn(size, ops)

    # ---------------------------------------------------------------- instruction groups
    def _ea_word(self, opword, ea, size, extra_first=False):
        self._w(opword | ea.field())
        self._emit_ext(ea, size)

    def _op_nop(self, size, ops): self._w(0x4E71)
    def _op_rts(self, size, ops): self._w(0x4E75)
    def _op_rte(self, size, ops): self._w(0x4E73)
    def _op_rtr(self, size, ops): self._w(0x4E77)
    def _op_reset(self, size, ops): self._w(0x4E70)
    def _op_illegal(self, size, ops): self._w(0x4AFC)
    def _op_stop(self, size, ops): self._w(0x4E72); self._w(self._val(ops[0].lstrip('#')))
    def _op_trap(self, size, ops): self._w(0x4E40 | self._val(ops[0].lstrip('#')) & 15)

    def _branch(self, cond, size, ops):
        c = 0 if cond == 't' else 1 if cond == 'sr' else CONDS[cond]
        target = self._val(ops[0])
        if size == 'w' or size is None and False:
            self._w(0x6000 | c << 8); self._w(target - (self.pc))
            return
        if size == 'b':
            d = target - (self.pc + 2)
            if self.pass_no == 2 and (d < -128 or d > 127 or d == 0): raise AsmError('short branch out of range: ' + str(d))
            self._w(0x6000 | c << 8 | d & 0xFF)
            return
        # default: short when it fits (pass 2 uses what pass 1 decided: sizes must not change between passes, so pass 1 always assumes short and a long form must be requested explicitly with .w)
        d = target - (self.pc + 2)
        if self.pass_no == 2 and (d < -128 or d > 127 or d == 0): raise AsmError('branch needs .w: displacement %d' % d)
        self._w(0x6000 | c << 8 | d & 0xFF)

    def _op_bra(self, size, ops): self._branch('t', size, ops)
    def _op_bsr(self, size, ops):
        target = self._val(ops[0])
        if size == 'w':
            self._w(0x6100); self._w(target - self.pc)
        else:
            d = target - (self.pc + 2)
            if self.pass_no == 2 and (d < -128 or d > 127 or d == 0): raise AsmError('bsr needs .w')
            self._w(0x6100 | d & 0xFF)

    def _dbcc(self, cond, ops):
        r = _reg(ops[0]); target = self._val(ops[1])
        self._w(0x50C8 | CONDS[cond] << 8 | r[1])
        self._w(target - self.pc)

    def _scc(self, cond, ops):
        ea = parse_ea(ops[0]); self._ea_word(0x50C0 | CONDS[cond] << 8, ea, 'b')

    def _op_jmp(self, size, ops): self._ea_word(0x4EC0, parse_ea(ops[0]), 'l')
    def _op_jsr(self, size, ops): self._ea_word(0x4E80, parse_ea(ops[0]), 'l')
    def _op_pea(self, size, ops): self._ea_word(0x4840, parse_ea(ops[0]), 'l')

    def _op_lea(self, size, ops):
        ea = parse_ea(ops[0]); a = _reg(ops[1])
        self._ea_word(0x41C0 | a[1] << 9, ea, 'l')

    def _op_moveq(self, size, ops):
        v = self._val(ops[0].lstrip('#')); r = _reg(ops[1])
        self._w(0x7000 | r[1] << 9 | v & 0xFF)

    def _op_move(self, size, ops):
        size = size or 'w'
        s, d = ops
        if s.strip().lower() in ('sr', 'ccr') or d.strip().lower() in ('sr', 'ccr', 'usp') or s.strip().lower() == 'usp':
            return self._move_special(size, s, d)
        src, dst = parse_ea(s), parse_ea(d)
        sb = {'b': 1, 'w': 3, 'l': 2}[size]
        if dst.kind == 'an':
            sb = {'w': 3, 'l': 2}[size]
        w = sb << 12 | dst.reg << 9 | dst.mode << 6 | src.field()
        self._w(w)
        self._emit_ext(src, size)
        self._emit_ext(dst, size)

    def _op_movea(self, size, ops): self._op_move(size, ops)

    def _move_special(self, size, s, d):
        sl, dl = s.strip().lower(), d.strip().lower()
        if dl == 'sr': self._ea_word(0x46C0, parse_ea(s), 'w')
        elif dl == 'ccr': self._ea_word(0x44C0, parse_ea(s), 'w')
        elif sl == 'sr': self._ea_word(0x40C0, parse_ea(d), 'w')
        elif dl == 'usp': self._w(0x4E60 | _reg(s)[1])
        elif sl == 'usp': self._w(0x4E68 | _reg(d)[1])
        else: raise AsmError('bad special move')

    def _arith(self, base_op, an_op, size, ops, name, allow_an=True):
        size = size or 'w'
        src, dst = parse_ea(ops[0]), parse_ea(ops[1])
        sz = SIZES[size]
        if dst.kind == 'an':
            if not allow_an: raise AsmError(name + ' to an address register')
            return self._ea_word(base_op | dst.reg << 9 | (3 if size == 'w' else 7) << 6, src, size)
        if src.kind == 'imm' and name in ('add', 'sub', 'cmp', 'and', 'or', 'eor'):
            # the immediate forms
            imm_op = {'add': 0x0600, 'sub': 0x0400, 'cmp': 0x0C00, 'and': 0x0200, 'or': 0x0000, 'eor': 0x0A00}[name]
            self._w(imm_op | sz << 6 | dst.field()); self._emit_ext(src, size); self._emit_ext(dst, size); return
        if dst.kind == 'dn' and name != 'eor':
            return self._ea_word(base_op | dst.reg << 9 | sz << 6, src, size)
        if src.kind == 'dn':
            return self._ea_word(base_op | src.reg << 9 | (4 + sz) << 6, dst, size)
        raise AsmError('bad operands for ' + name)

    def _op_add(self, size, ops): self._arith(0xD000, 0, size, ops, 'add')
    def _op_sub(self, size, ops): self._arith(0x9000, 0, size, ops, 'sub')
    def _op_and(self, size, ops): self._arith(0xC000, 0, size, ops, 'and', False)
    def _op_or(self, size, ops): self._arith(0x8000, 0, size, ops, 'or', False)
    def _op_adda(self, size, ops): self._arith(0xD000, 0, size, ops, 'add')
    def _op_suba(self, size, ops): self._arith(0x9000, 0, size, ops, 'sub')

    def _op_eor(self, size, ops):
        size = size or 'w'; src, dst = parse_ea(ops[0]), parse_ea(ops[1])
        if src.kind == 'imm': return self._arith(0, 0, size, ops, 'eor')
        self._ea_word(0xB000 | src.reg << 9 | (4 + SIZES[size]) << 6, dst, size)

    def _op_cmp(self, size, ops):
        size = size or 'w'; src, dst = parse_ea(ops[0]), parse_ea(ops[1])
        if src.kind == 'imm': return self._arith(0, 0, size, ops, 'cmp')
        if dst.kind == 'an': return self._ea_word(0xB000 | dst.reg << 9 | (3 if size == 'w' else 7) << 6, src, size)
        self._ea_word(0xB000 | dst.reg << 9 | SIZES[size] << 6, src, size)

    def _op_cmpa(self, size, ops): self._op_cmp(size, ops)

    def _imm_op(self, opc, size, ops):
        size = size or 'w'; src, dst = parse_ea(ops[0]), parse_ea(ops[1])
        self._w(opc | SIZES[size] << 6 | dst.field()); self._emit_ext(src, size); self._emit_ext(dst, size)

    def _op_addi(self, size, ops): self._imm_op(0x0600, size, ops)
    def _op_subi(self, size, ops): self._imm_op(0x0400, size, ops)
    def _op_cmpi(self, size, ops): self._imm_op(0x0C00, size, ops)
    def _op_andi(self, size, ops):
        if ops[1].strip().lower() == 'sr': self._w(0x027C); self._w(self._val(ops[0].lstrip('#'))); return
        self._imm_op(0x0200, size, ops)
    def _op_ori(self, size, ops):
        if ops[1].strip().lower() == 'sr': self._w(0x007C); self._w(self._val(ops[0].lstrip('#'))); return
        self._imm_op(0x0000, size, ops)
    def _op_eori(self, size, ops): self._imm_op(0x0A00, size, ops)

    def _quick(self, opc, size, ops):
        size = size or 'w'
        n = self._val(ops[0].lstrip('#')); dst = parse_ea(ops[1])
        self._ea_word(opc | (n & 7) << 9 | SIZES[size] << 6, dst, size)

    def _op_addq(self, size, ops): self._quick(0x5000, size, ops)
    def _op_subq(self, size, ops): self._quick(0x5100, size, ops)

    def _bitop(self, kind, size, ops):
        src, dst = parse_ea(ops[0]), parse_ea(ops[1])
        k = BIT_OPS[kind]
        if src.kind == 'imm':
            self._w(0x0800 | k << 6 | dst.field()); self._w(self._val(src.value)); self._emit_ext(dst, 'b')
        else:
            self._w(0x0100 | src.reg << 9 | k << 6 | dst.field()); self._emit_ext(dst, 'b')

    def _op_btst(self, size, ops): self._bitop('btst', size, ops)
    def _op_bset(self, size, ops): self._bitop('bset', size, ops)
    def _op_bclr(self, size, ops): self._bitop('bclr', size, ops)
    def _op_bchg(self, size, ops): self._bitop('bchg', size, ops)

    def _shift(self, kind, left, size, ops):
        size = size or 'w'
        t = SHIFT_TYPES[kind]
        if len(ops) == 1:
            dst = parse_ea(ops[0]); self._ea_word(0xE0C0 | t << 9 | left << 8, dst, 'w'); return
        cnt, dst = ops[0].strip(), parse_ea(ops[1])
        if cnt.startswith('#'):
            n = self._val(cnt[1:]); c = n & 7; i = 0
        else:
            c = _reg(cnt)[1]; i = 1
        self._w(0xE000 | c << 9 | left << 8 | SIZES[size] << 6 | i << 5 | t << 3 | dst.reg)

    def _op_lsl(self, size, ops): self._shift('ls', 1, size, ops)
    def _op_lsr(self, size, ops): self._shift('ls', 0, size, ops)
    def _op_asl(self, size, ops): self._shift('as', 1, size, ops)
    def _op_asr(self, size, ops): self._shift('as', 0, size, ops)
    def _op_rol(self, size, ops): self._shift('ro', 1, size, ops)
    def _op_ror(self, size, ops): self._shift('ro', 0, size, ops)

    def _unary(self, opc, size, ops):
        size = size or 'w'; ea = parse_ea(ops[0]); self._ea_word(opc | SIZES[size] << 6, ea, size)

    def _op_clr(self, size, ops): self._unary(0x4200, size, ops)
    def _op_neg(self, size, ops): self._unary(0x4400, size, ops)
    def _op_not(self, size, ops): self._unary(0x4600, size, ops)
    def _op_tst(self, size, ops): self._unary(0x4A00, size, ops)
    def _op_swap(self, size, ops): self._w(0x4840 | _reg(ops[0])[1])
    def _op_ext(self, size, ops): self._w(0x4880 | (2 if (size or 'w') == 'w' else 3) << 6 | _reg(ops[0])[1])

    def _op_exg(self, size, ops):
        a, b = _reg(ops[0]), _reg(ops[1])
        if a[0] == 'd' and b[0] == 'd': self._w(0xC140 | a[1] << 9 | b[1])
        elif a[0] == 'a' and b[0] == 'a': self._w(0xC148 | a[1] << 9 | b[1])
        elif a[0] == 'd': self._w(0xC188 | a[1] << 9 | b[1])
        else: self._w(0xC188 | b[1] << 9 | a[1])

    def _muldiv(self, opc, ops):
        src, d = parse_ea(ops[0]), _reg(ops[1])
        self._ea_word(opc | d[1] << 9, src, 'w')

    def _op_mulu(self, size, ops): self._muldiv(0xC0C0, ops)
    def _op_muls(self, size, ops): self._muldiv(0xC1C0, ops)
    def _op_divu(self, size, ops): self._muldiv(0x80C0, ops)
    def _op_divs(self, size, ops): self._muldiv(0x81C0, ops)

    def _op_movem(self, size, ops):
        size = size or 'w'
        def mask_of(text, predec):
            m = 0
            for part in text.split('/'):
                if '-' in part:
                    a, b = part.split('-'); ra, rb = _reg(a), _reg(b)
                    regs = range(ra[1] + (8 if ra[0] == 'a' else 0), rb[1] + (8 if rb[0] == 'a' else 0) + 1)
                else:
                    ra = _reg(part); regs = [ra[1] + (8 if ra[0] == 'a' else 0)]
                for r in regs: m |= 1 << (15 - r if predec else r)
            return m
        if _reg(ops[0].split('/')[0].split('-')[0]) is not None and not ops[0].startswith('('):
            dst = parse_ea(ops[1]); mask = mask_of(ops[0], dst.kind == 'predec')
            self._w(0x4880 | (1 if size == 'l' else 0) << 6 | dst.field()); self._w(mask); self._emit_ext(dst, size)
        else:
            src = parse_ea(ops[0]); mask = mask_of(ops[1], False)
            self._w(0x4C80 | (1 if size == 'l' else 0) << 6 | src.field()); self._w(mask); self._emit_ext(src, size)


# ====================================================================================================================
#  ROM image
# ====================================================================================================================

def build_rom(src, vectors=None, title='PROBE', size=0x20000, region='JUE', stack=0xFFFE00, origin=0x200, symbols_out=None):
    """The ROM: stack and entry vectors, a vector table (`vectors` maps vector number to a label, anything else spins at `_spin` if the source defines it, else at $300), the header and the code at $200."""
    a = Asm68k(origin)
    code = a.assemble(src)
    syms = a.syms
    if symbols_out is not None: symbols_out.update(syms)
    rom = bytearray(b'\xFF' * size)
    def L(at, v): rom[at:at + 4] = struct.pack('>I', v)
    L(0, stack); L(4, origin)
    spin = syms.get('_spin', 0x300)
    for v in range(2, 64): L(v * 4, spin)
    for v, lab in (vectors or {}).items(): L(v * 4, syms[lab] if isinstance(lab, str) else lab)
    rom[0x100:0x110] = b'SEGA MEGA DRIVE '
    rom[0x110:0x120] = b'(C)PROBE 2026.OCT'[:16]
    rom[0x120:0x150] = title.ljust(48).encode()
    rom[0x150:0x180] = title.ljust(48).encode()
    rom[0x180:0x18E] = b'GM 00000000-00'
    rom[0x1A0:0x1B0] = struct.pack('>IIII', 0, size - 1, 0xFF0000, 0xFFFFFF)
    rom[0x1F0:0x200] = region.ljust(16).encode()
    rom[0x300:0x302] = b'\x60\xFE'
    if len(code) + origin > size: raise AsmError('code too big')
    rom[origin:origin + len(code)] = code
    return bytes(rom)


if __name__ == '__main__':
    # a self-check against encodings worked out by hand from the reference
    t = Asm68k(0)
    cases = [
        ('move.w #$8F02,($C00004).l', '33fc8f0200c00004'),
        ('move.l d1,(a0)+', '20c1'),
        ('move.w ($C00008).l,d1', '33c1' if False else '32390 0c00008'.replace(' ', '')),
        ('moveq #5,d3', '7605'),
        ('btst #3,d0', '08000003'),
        ('lsr.w #8,d2', 'e04a'),
        ('nop', '4e71'),
        ('lea ($FF0000).l,a0', '41f900ff0000'),
        ('add.w d1,d2', 'd441'),
        ('cmpi.w #$FF,d6', '0c4600ff'),
        ('dbf d0,*', None),
        ('movem.l d0-d3,-(sp)', '48e7f000'),
        ('subq.w #1,d0', '5340'),
        ('move.w d0,4(a1)', '3340 0004'.replace(' ', '')),
        ('tst.w (a0)', '4a50'),
        ('jsr (a0)', '4e90'),
    ]
    bad = 0
    for text, want in cases:
        if want is None: continue
        a = Asm68k(0); got = a.assemble(text).hex()
        if got != want:
            bad += 1; print('MISMATCH', text, got, want)
    print('self-check: %d cases, %d mismatches' % (sum(1 for c in cases if c[1]), bad))
