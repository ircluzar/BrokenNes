"""A small two-pass 65816 text assembler for the Nested Everything GB -> SNES back end (gb2snes).

Assembler(symbols).assemble(text, base) -> bytes; .labels afterwards. One instruction per line, 'label:' prefixes
(locals start with '.', scoped to the last global; '@name:' is a global that keeps the local scope), ';' comments,
'name = expr', 'db', 'dw', 'dl' (24-bit), 'ds n[,fill]', '.a8 .a16 .i8 .i16' (immediate sizes).
Operand size: a mnemonic suffix forces it (lda.b = direct page, lda.w = absolute, lda.l = long); otherwise a number
<= $FF is direct page, <= $FFFF absolute, else long; a symbol not yet defined is absolute (jml/jsl: long).
Expressions: Python over the symbols, $hex, %bin; lo(), hi(), bank().
"""
import re

GRP = {'ora': 0x00, 'and': 0x20, 'eor': 0x40, 'adc': 0x60, 'sta': 0x80, 'lda': 0xA0, 'cmp': 0xC0, 'sbc': 0xE0}
GRP_MODE = {'imm': 0x09, 'dp': 0x05, 'abs': 0x0D, 'long': 0x0F, 'dpx': 0x15, 'absx': 0x1D, 'absy': 0x19,
            'longx': 0x1F, 'ind': 0x12, 'indl': 0x07, 'indy': 0x11, 'indly': 0x17, 'indx': 0x01, 'sr': 0x03}
OTHER = {
    'ldx': {'imm': 0xA2, 'dp': 0xA6, 'abs': 0xAE, 'dpy': 0xB6, 'absy': 0xBE},
    'ldy': {'imm': 0xA0, 'dp': 0xA4, 'abs': 0xAC, 'dpx': 0xB4, 'absx': 0xBC},
    'stx': {'dp': 0x86, 'abs': 0x8E, 'dpy': 0x96},
    'sty': {'dp': 0x84, 'abs': 0x8C, 'dpx': 0x94},
    'stz': {'dp': 0x64, 'abs': 0x9C, 'dpx': 0x74, 'absx': 0x9E},
    'cpx': {'imm': 0xE0, 'dp': 0xE4, 'abs': 0xEC},
    'cpy': {'imm': 0xC0, 'dp': 0xC4, 'abs': 0xCC},
    'inc': {'acc': 0x1A, 'dp': 0xE6, 'abs': 0xEE, 'dpx': 0xF6, 'absx': 0xFE},
    'dec': {'acc': 0x3A, 'dp': 0xC6, 'abs': 0xCE, 'dpx': 0xD6, 'absx': 0xDE},
    'asl': {'acc': 0x0A, 'dp': 0x06, 'abs': 0x0E, 'dpx': 0x16, 'absx': 0x1E},
    'rol': {'acc': 0x2A, 'dp': 0x26, 'abs': 0x2E, 'dpx': 0x36, 'absx': 0x3E},
    'lsr': {'acc': 0x4A, 'dp': 0x46, 'abs': 0x4E, 'dpx': 0x56, 'absx': 0x5E},
    'ror': {'acc': 0x6A, 'dp': 0x66, 'abs': 0x6E, 'dpx': 0x76, 'absx': 0x7E},
    'bit': {'imm': 0x89, 'dp': 0x24, 'abs': 0x2C, 'dpx': 0x34, 'absx': 0x3C},
    'tsb': {'dp': 0x04, 'abs': 0x0C}, 'trb': {'dp': 0x14, 'abs': 0x1C},
    'jmp': {'abs': 0x4C, 'ind': 0x6C, 'indx': 0x7C, 'long': 0x5C, 'indl': 0xDC},
    'jml': {'long': 0x5C, 'indl': 0xDC}, 'jsr': {'abs': 0x20, 'indx': 0xFC, 'long': 0x22}, 'jsl': {'long': 0x22},
    'pea': {'abs': 0xF4}, 'pei': {'ind': 0xD4},
}
IMP = {'clc': 0x18, 'sec': 0x38, 'cli': 0x58, 'sei': 0x78, 'clv': 0xB8, 'cld': 0xD8, 'sed': 0xF8, 'tax': 0xAA,
       'tay': 0xA8, 'txa': 0x8A, 'tya': 0x98, 'txs': 0x9A, 'tsx': 0xBA, 'tcd': 0x5B, 'tdc': 0x7B, 'tcs': 0x1B,
       'tsc': 0x3B, 'txy': 0x9B, 'tyx': 0xBB, 'xba': 0xEB, 'xce': 0xFB, 'pha': 0x48, 'pla': 0x68, 'php': 0x08,
       'plp': 0x28, 'phx': 0xDA, 'plx': 0xFA, 'phy': 0x5A, 'ply': 0x7A, 'phb': 0x8B, 'plb': 0xAB, 'phd': 0x0B,
       'pld': 0x2B, 'phk': 0x4B, 'inx': 0xE8, 'iny': 0xC8, 'dex': 0xCA, 'dey': 0x88, 'nop': 0xEA, 'wai': 0xCB,
       'stp': 0xDB, 'rts': 0x60, 'rtl': 0x6B, 'rti': 0x40}
BR = {'bpl': 0x10, 'bmi': 0x30, 'bvc': 0x50, 'bvs': 0x70, 'bcc': 0x90, 'bcs': 0xB0, 'bne': 0xD0, 'beq': 0xF0, 'bra': 0x80}
SIZE = {'imm': None, 'dp': 1, 'dpx': 1, 'dpy': 1, 'ind': 1, 'indl': 1, 'indy': 1, 'indly': 1, 'indx': 1, 'sr': 1,
        'abs': 2, 'absx': 2, 'absy': 2, 'long': 3, 'longx': 3, 'acc': 0}


class AsmError(Exception):
    pass


def _num(m):
    t = m.group(0)
    return str(int(t[1:], 16)) if t[0] == '$' else str(int(t[1:], 2))


class Assembler:
    def __init__(self, symbols=None):
        self.ext = dict(symbols or {})

    def ev(self, e, syms, final):
        e2 = re.sub(r'\$[0-9A-Fa-f]+|%[01]+', _num, e.strip())
        e2 = re.sub(r'(?<![\w.])\.(\w+)', lambda m: '_L_' + self.glob + '_' + m.group(1), e2)
        try:
            return int(eval(e2, {'__builtins__': {}, 'lo': lambda v: v & 0xFF, 'hi': lambda v: (v >> 8) & 0xFF,
                                 'bank': lambda v: (v >> 16) & 0xFF}, syms))
        except NameError:
            if final: raise AsmError('undefined symbol in ' + e)
            return None

    def mode(self, arg, suffix, op, syms, final):
        """(mode, value) of an operand."""
        a = arg.strip()
        if a == '' or a.lower() == 'a': return 'acc', 0
        if a.startswith('#'):
            return 'imm', self.ev(a[1:], syms, final) or 0
        low = a.lower().replace(' ', '')
        nos = a.replace(' ', '')
        for pat, m in ((r'^\((.+)\),y$', 'indy'), (r'^\[(.+)\],y$', 'indly'), (r'^\((.+),x\)$', 'indx'),
                       (r'^\((.+)\)$', 'ind'), (r'^\[(.+)\]$', 'indl'), (r'^(.+),s$', 'sr')):
            mm = re.match(pat, nos, re.I)
            if mm:
                return m, self.ev(mm.group(1), syms, final) or 0
        idx = ''
        if low.endswith(',x'): idx, a = 'x', a[:-2]
        elif low.endswith(',y'): idx, a = 'y', a[:-2]
        v = self.ev(a, syms, final)
        if suffix == 'b': base = 'dp'
        elif suffix == 'w': base = 'abs'
        elif suffix == 'l': base = 'long'
        elif op in ('jml', 'jsl'): base = 'long'
        elif op in ('jmp', 'jsr'): base = 'abs'
        elif v is None: base = 'abs'
        elif v <= 0xFF: base = 'dp'
        elif v <= 0xFFFF: base = 'abs'
        else: base = 'long'
        return base + idx, (v or 0)

    def enc(self, op, suffix, args, pc, syms, final):
        ev = lambda e: self.ev(e, syms, final) or 0
        if op in IMP: return [IMP[op]]
        if op == 'db':
            out = []
            for x in args:
                if x.startswith('"'): out += list(x[1:-1].encode('latin-1'))
                else: out.append(ev(x) & 0xFF)
            return out
        if op == 'dw':
            out = []
            for x in args: v = ev(x); out += [v & 0xFF, v >> 8 & 0xFF]
            return out
        if op == 'dl':
            out = []
            for x in args: v = ev(x); out += [v & 0xFF, v >> 8 & 0xFF, v >> 16 & 0xFF]
            return out
        if op == 'ds': return [ev(args[1]) & 0xFF if len(args) > 1 else 0] * ev(args[0])
        if op in ('rep', 'sep'):
            v = ev(args[0][1:])
            if op == 'rep':
                if v & 0x20: self.m16 = True
                if v & 0x10: self.x16 = True
            else:
                if v & 0x20: self.m16 = False
                if v & 0x10: self.x16 = False
            return [0xC2 if op == 'rep' else 0xE2, v & 0xFF]
        if op in BR:
            t = ev(args[0]); d = t - (pc + 2)
            if final and not -128 <= d <= 127: raise AsmError('branch out of range (%d)' % d)
            return [BR[op], d & 0xFF]
        if op == 'brl':
            d = ev(args[0]) - (pc + 3); return [0x82, d & 0xFF, d >> 8 & 0xFF]
        if op in ('mvn', 'mvp'):
            return [0x54 if op == 'mvn' else 0x44, ev(args[0]) & 0xFF, ev(args[1]) & 0xFF]
        arg = ','.join(args)
        m, v = self.mode(arg, suffix, op, syms, final)
        if op in GRP:
            if m not in GRP_MODE or (op == 'sta' and m == 'imm'): raise AsmError('mode %s' % m)
            code = GRP[op] + GRP_MODE[m]
            if op == 'sta' and m == 'imm': raise AsmError('sta #')
            if code == 0x89 + 0: pass
        elif op in OTHER:
            tab = OTHER[op]
            if m not in tab:
                if m == 'dp' and 'abs' in tab: m = 'abs'
                elif m == 'dpx' and 'absx' in tab: m = 'absx'
                elif m == 'dpy' and 'absy' in tab: m = 'absy'
                elif m == 'abs' and 'long' in tab and op in ('jml', 'jsl'): m = 'long'
                elif m == 'long' and op in ('jmp', 'jsr') and 'abs' in tab: m = 'abs'
                else: raise AsmError('mode %s for %s' % (m, op))
            code = tab[m]
        else:
            raise AsmError('unknown mnemonic ' + op)
        if m == 'imm':
            wide = self.x16 if op in ('ldx', 'ldy', 'cpx', 'cpy') else self.m16
            n = 2 if wide else 1
        elif op in ('jmp', 'jml', 'jsr') and m in ('ind', 'indl', 'indx'):
            n = 2
        else:
            n = SIZE[m]
        return [code] + [(v >> (8 * i)) & 0xFF for i in range(n)]

    def assemble(self, text, base):
        lines = []
        for raw in text.splitlines():
            s = raw
            if '"' not in s: s = s.split(';')[0]
            lines.append(s.strip())
        syms = dict(self.ext)
        self.defined = set()
        prev = None
        passes = [False] * 5 + [True]
        for k, final in enumerate(passes):
            if not final and k >= 2 and prev == {kk: vv for kk, vv in syms.items()}:
                continue
            prev = dict(syms)
            pc = base
            out = bytearray()
            self.glob = ''
            self.m16, self.x16 = False, True
            for ln in lines:
                if not ln: continue
                m = re.match(r'^([.@\w]+):\s*(.*)$', ln)
                if m:
                    name = m.group(1)
                    if name.startswith('.'): name = '_L_' + self.glob + '_' + name[1:]
                    elif name.startswith('@'): name = name[1:]
                    else: self.glob = name
                    syms[name] = pc
                    self.defined.add(name)
                    ln = m.group(2).strip()
                    if not ln: continue
                m = re.match(r'^(\w+)\s*=\s*(.+)$', ln)
                if m:
                    v = self.ev(m.group(2), syms, final)
                    if v is not None: syms[m.group(1)] = v
                    continue
                parts = ln.split(None, 1)
                op = parts[0].lower()
                if op in ('.a8', '.a16', '.i8', '.i16'):
                    if op == '.a8': self.m16 = False
                    if op == '.a16': self.m16 = True
                    if op == '.i8': self.x16 = False
                    if op == '.i16': self.x16 = True
                    continue
                suffix = ''
                if '.' in op and not op.startswith('.'):
                    op, suffix = op.split('.', 1)
                args = [x.strip() for x in re.split(r',(?![^()\[\]]*[)\]])', parts[1])] if len(parts) > 1 else []
                if op in ('db', 'dw', 'dl') and len(parts) > 1:
                    args = [x.strip() for x in re.findall(r'"[^"]*"|[^,]+', parts[1])]
                try:
                    b = self.enc(op, suffix, args, pc, syms, final)
                except AsmError as e:
                    raise AsmError('%s  (line: %s)' % (e, ln))
                except Exception as e:
                    raise AsmError('%s: %s  (line: %s)' % (type(e).__name__, e, ln))
                out += bytes(v & 0xFF for v in b)
                pc += len(b)
        self.labels = {k: v for k, v in syms.items() if k not in self.ext or k in self.defined}
        return bytes(out)


def assemble(text, base, symbols=None):
    a = Assembler(symbols)
    b = a.assemble(text, base)
    return b, a.labels
