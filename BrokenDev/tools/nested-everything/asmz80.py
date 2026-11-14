"""A small two-pass Z80 text assembler for the Nested Everything runtimes (gb2gg).

assemble(text, base, symbols) -> (bytes, labels). One instruction per line, 'label:' prefixes, ';' comments,
'name = expr' constants, 'db a,b,"text"', 'dw a,b', 'ds n[,fill]', '.align n'. Expressions are Python expressions
over labels/constants with $hex, %bin, 0x.. numbers. Local labels start with '.' and belong to the last global label.
"""
import re

R8 = {'b': 0, 'c': 1, 'd': 2, 'e': 3, 'h': 4, 'l': 5, '(hl)': 6, 'a': 7}
RP = {'bc': 0, 'de': 1, 'hl': 2, 'sp': 3}
RP2 = {'bc': 0, 'de': 1, 'hl': 2, 'af': 3}
CC = {'nz': 0, 'z': 1, 'nc': 2, 'c': 3, 'po': 4, 'pe': 5, 'p': 6, 'm': 7}
ALU = {'add': 0, 'adc': 1, 'sub': 2, 'sbc': 3, 'and': 4, 'xor': 5, 'or': 6, 'cp': 7}
ROT = {'rlc': 0, 'rrc': 1, 'rl': 2, 'rr': 3, 'sla': 4, 'sra': 5, 'sll': 6, 'srl': 7}
SIMPLE = {'nop': [0x00], 'rlca': [0x07], 'rrca': [0x0F], 'rla': [0x17], 'rra': [0x1F], 'daa': [0x27], 'cpl': [0x2F],
          'scf': [0x37], 'ccf': [0x3F], 'halt': [0x76], 'di': [0xF3], 'ei': [0xFB], 'exx': [0xD9], 'neg': [0xED, 0x44],
          'reti': [0xED, 0x4D], 'retn': [0xED, 0x45], 'ldir': [0xED, 0xB0], 'ldi': [0xED, 0xA0], 'outi': [0xED, 0xA3],
          'otir': [0xED, 0xB3], 'ldd': [0xED, 0xA8], 'lddr': [0xED, 0xB8], 'rld': [0xED, 0x6F], 'rrd': [0xED, 0x67]}


class AsmError(Exception):
    pass


def _num(m):
    t = m.group(0)
    if t.startswith('$'): return str(int(t[1:], 16))
    if t.startswith('%'): return str(int(t[1:], 2))
    return t


def split_args(s):
    out, depth, cur, q = [], 0, '', False
    for ch in s:
        if ch == '"': q = not q
        if not q and ch == '(': depth += 1
        if not q and ch == ')': depth -= 1
        if ch == ',' and depth == 0 and not q:
            out.append(cur.strip()); cur = ''
        else:
            cur += ch
    if cur.strip(): out.append(cur.strip())
    return out


class Assembler:
    def __init__(self, base, symbols=None):
        self.base = base
        self.ext = dict(symbols or {})

    def ev(self, e, syms, final):
        e2 = re.sub(r'\$[0-9A-Fa-f]+|%[01]+', _num, e.strip())
        e2 = re.sub(r'(?<![\w.])\.(\w+)', lambda m: '_L_' + self.glob + '_' + m.group(1), e2)
        try:
            return int(eval(e2, {'__builtins__': {}, 'lo': lambda v: v & 0xFF, 'hi': lambda v: (v >> 8) & 0xFF}, syms))
        except NameError:
            if final: raise AsmError('undefined symbol in ' + e)
            return 0

    def is_ind(self, a):
        return a.startswith('(') and a.endswith(')') and a.count('(') == 1

    def enc(self, op, args, pc, syms, final):
        ev = lambda e: self.ev(e, syms, final)
        a = [x.lower() if x.lower() in R8 or x.lower() in RP or x.lower() in RP2 or x.lower() in CC or x.lower() in
             ('(bc)', '(de)', '(sp)', '(c)', 'i', 'r', "af'") else x for x in args]
        n = len(a)
        if op in SIMPLE: return SIMPLE[op]
        if op == 'db':
            out = []
            for x in args:
                if x.startswith('"'): out += list(x[1:-1].encode('latin-1'))
                else: out.append(ev(x) & 0xFF)
            return out
        if op == 'dw':
            out = []
            for x in args: v = ev(x); out += [v & 0xFF, (v >> 8) & 0xFF]
            return out
        if op == 'ds':
            return [ev(args[1]) & 0xFF if n > 1 else 0] * ev(args[0])
        if op == 'ld':
            d, s = a
            if d in R8 and s in R8:
                if d == s == '(hl)': raise AsmError('ld (hl),(hl)')
                return [0x40 | R8[d] << 3 | R8[s]]
            if d == 'a' and s in ('(bc)', '(de)'): return [0x0A if s == '(bc)' else 0x1A]
            if s == 'a' and d in ('(bc)', '(de)'): return [0x02 if d == '(bc)' else 0x12]
            if d == 'a' and s == 'i': return [0xED, 0x57]
            if d == 'a' and s == 'r': return [0xED, 0x5F]
            if d == 'sp' and s == 'hl': return [0xF9]
            if d in R8 and not self.is_ind(s): return [0x06 | R8[d] << 3, ev(s) & 0xFF]
            if d == 'a' and self.is_ind(s): v = ev(s[1:-1]); return [0x3A, v & 0xFF, v >> 8 & 0xFF]
            if s == 'a' and self.is_ind(d): v = ev(d[1:-1]); return [0x32, v & 0xFF, v >> 8 & 0xFF]
            if d in RP and self.is_ind(s):
                v = ev(s[1:-1])
                return ([0x2A] if d == 'hl' else [0xED, 0x4B | RP[d] << 4]) + [v & 0xFF, v >> 8 & 0xFF]
            if s in RP and self.is_ind(d):
                v = ev(d[1:-1])
                return ([0x22] if s == 'hl' else [0xED, 0x43 | RP[s] << 4]) + [v & 0xFF, v >> 8 & 0xFF]
            if d in RP: v = ev(s); return [0x01 | RP[d] << 4, v & 0xFF, v >> 8 & 0xFF]
        if op in ('push', 'pop'): return [(0xC5 if op == 'push' else 0xC1) | RP2[a[0]] << 4]
        if op == 'ex':
            if a == ['de', 'hl']: return [0xEB]
            if a == ['(sp)', 'hl']: return [0xE3]
            if a[0] == 'af': return [0x08]
        if op in ALU:
            s = a[-1]
            if n == 2 and a[0] == 'hl':
                if op == 'add': return [0x09 | RP[s] << 4]
                if op == 'adc': return [0xED, 0x4A | RP[s] << 4]
                if op == 'sbc': return [0xED, 0x42 | RP[s] << 4]
            if s in R8: return [0x80 | ALU[op] << 3 | R8[s]]
            return [0xC6 | ALU[op] << 3, ev(s) & 0xFF]
        if op in ('inc', 'dec'):
            if a[0] in R8: return [(0x04 if op == 'inc' else 0x05) | R8[a[0]] << 3]
            return [(0x03 if op == 'inc' else 0x0B) | RP[a[0]] << 4]
        if op in ROT: return [0xCB, ROT[op] << 3 | R8[a[0]]]
        if op in ('bit', 'res', 'set'):
            return [0xCB, {'bit': 0x40, 'res': 0x80, 'set': 0xC0}[op] | ev(a[0]) << 3 | R8[a[1]]]
        if op == 'jp':
            if a[0] == '(hl)': return [0xE9]
            if n == 2: v = ev(a[1]); return [0xC2 | CC[a[0]] << 3, v & 0xFF, v >> 8 & 0xFF]
            v = ev(a[0]); return [0xC3, v & 0xFF, v >> 8 & 0xFF]
        if op == 'call':
            if n == 2: v = ev(a[1]); return [0xC4 | CC[a[0]] << 3, v & 0xFF, v >> 8 & 0xFF]
            v = ev(a[0]); return [0xCD, v & 0xFF, v >> 8 & 0xFF]
        if op == 'jr' and self.idx in self.relaxed:
            v = ev(a[-1])
            return ([0xC3] if n == 1 else [0xC2 | CC[a[0]] << 3]) + [v & 0xFF, v >> 8 & 0xFF]
        if op in ('jr', 'djnz'):
            t = ev(a[-1]); d = t - (pc + 2)
            if op == 'jr' and not -128 <= d <= 127 and (t != 0 or final):
                self.relaxed.add(self.idx); self.changed = True      # too far: becomes a jp (next pass)
            if final and not -128 <= d <= 127: raise AsmError('jr out of range')
            if op == 'djnz': return [0x10, d & 0xFF]
            return [0x18 if n == 1 else 0x20 | CC[a[0]] << 3, d & 0xFF]
        if op == 'ret':
            return [0xC9] if n == 0 else [0xC0 | CC[a[0]] << 3]
        if op == 'rst': return [0xC7 | ev(a[0])]
        if op == 'im': return [0xED, {0: 0x46, 1: 0x56, 2: 0x5E}[ev(a[0])]]
        if op == 'in':
            if a[1] == '(c)': return [0xED, 0x40 | R8[a[0]] << 3]
            return [0xDB, ev(a[1][1:-1]) & 0xFF]
        if op == 'out':
            if a[0] == '(c)': return [0xED, 0x41 | R8[a[1]] << 3]
            return [0xD3, ev(a[0][1:-1]) & 0xFF]
        raise AsmError('cannot encode: %s %s' % (op, ', '.join(args)))

    def assemble(self, text):
        lines = []
        for raw in text.splitlines():
            s = raw.split(';')[0].rstrip() if '"' not in raw else raw.rstrip()
            if '"' in s and ';' in s.split('"')[-1]: s = s[:s.rfind(';')]
            lines.append(s.strip())
        syms = dict(self.ext)
        self.relaxed = set()
        # passes until no jr was relaxed and no label moved, then the final (encoding) pass
        prev = None
        passes = [False] * 12 + [True]
        for k, final in enumerate(passes):
            self.changed = False
            if not final and k >= 2 and getattr(self, '_stable', False):
                continue
            pc = self.base
            out = bytearray()
            self.glob = ''
            for self.idx, ln in enumerate(lines):
                if not ln: continue
                m = re.match(r'^([.@\w]+):\s*(.*)$', ln)
                if m:
                    name = m.group(1)
                    if name.startswith('.'): name = '_L_' + self.glob + '_' + name[1:]
                    elif name.startswith('@'): name = name[1:]       # a global entry that keeps the local scope
                    else: self.glob = name
                    syms[name] = pc
                    ln = m.group(2).strip()
                    if not ln: continue
                m = re.match(r'^(\w+)\s*=\s*(.+)$', ln)
                if m:
                    syms[m.group(1)] = self.ev(m.group(2), syms, final)
                    continue
                parts = ln.split(None, 1)
                op = parts[0].lower()
                args = split_args(parts[1]) if len(parts) > 1 else []
                if op == '.align':
                    al = self.ev(args[0], syms, final)
                    while pc % al: out.append(0); pc += 1
                    continue
                try:
                    b = self.enc(op, args, pc, syms, final)
                except AsmError as e:
                    raise AsmError('%s  (line: %s)' % (e, ln))
                except Exception as e:
                    raise AsmError('%s: %s  (line: %s)' % (type(e).__name__, e, ln))
                out += bytes(v & 0xFF for v in b)
                pc += len(b)
            snap = dict(syms)
            self._stable = not self.changed and k >= 1 and snap == prev
            prev = snap
        if not final or self.changed:
            raise AsmError('labels did not settle')
        self.labels = {k: v for k, v in syms.items() if k not in self.ext}
        return bytes(out), syms


def assemble(text, base, symbols=None):
    a = Assembler(base, symbols)
    return a.assemble(text)
