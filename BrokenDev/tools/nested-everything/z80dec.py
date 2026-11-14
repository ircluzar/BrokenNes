"""Full Z80 decoder for the Nested Everything translators (SMS -> Genesis): every documented instruction and the common
undocumented ones (IXH/IXL/IYH/IYL, SLL), decoded to a small operand form the 68000 code generator works from.

    ins = decode(rom_bytes, index, pc)      -> Ins(pc, size, op, a, b, raw)

Operands (tuples):
    ('r', 'A'|'B'|'C'|'D'|'E'|'H'|'L'|'IXH'|'IXL'|'IYH'|'IYL')   8-bit register
    ('rr', 'BC'|'DE'|'HL'|'SP'|'AF'|'IX'|'IY')                   16-bit register
    ('n', value)                                                 8-bit immediate
    ('nn', value)                                                16-bit immediate
    ('m', 'HL'|'BC'|'DE'|'SP')                                   (rr)
    ('ix', 'IX'|'IY', disp)                                      (IX+d)
    ('abs', addr)                                                (nn)
    ('cc', 'NZ'|'Z'|'NC'|'C'|'PO'|'PE'|'P'|'M')
    ('port', n) / ('portc',)                                     (n) / (C)
    ('bit', k)
"""

R8 = ['B', 'C', 'D', 'E', 'H', 'L', None, 'A']
RP = ['BC', 'DE', 'HL', 'SP']
RP2 = ['BC', 'DE', 'HL', 'AF']
CC = ['NZ', 'Z', 'NC', 'C', 'PO', 'PE', 'P', 'M']
ALU = ['ADD', 'ADC', 'SUB', 'SBC', 'AND', 'XOR', 'OR', 'CP']
ROT = ['RLC', 'RRC', 'RL', 'RR', 'SLA', 'SRA', 'SLL', 'SRL']


class Ins:
    __slots__ = ('pc', 'size', 'op', 'a', 'b', 'raw')

    def __init__(self, pc, size, op, a=None, b=None, raw=b''):
        self.pc, self.size, self.op, self.a, self.b, self.raw = pc, size, op, a, b, raw

    def __repr__(self):
        def f(o):
            if o is None:
                return ''
            k = o[0]
            if k in ('r', 'rr', 'cc'):
                return o[1]
            if k == 'n':
                return '$%02X' % o[1]
            if k == 'nn':
                return '$%04X' % o[1]
            if k == 'm':
                return '(%s)' % o[1]
            if k == 'ix':
                return '(%s%+d)' % (o[1], o[2])
            if k == 'abs':
                return '($%04X)' % o[1]
            if k == 'port':
                return '($%02X)' % o[1]
            if k == 'portc':
                return '(C)'
            if k == 'bit':
                return str(o[1])
            return str(o)
        args = ','.join(x for x in (f(self.a), f(self.b)) if x)
        return '%04X %s %s' % (self.pc, self.op, args)

    @property
    def nxt(self):
        return (self.pc + self.size) & 0xFFFF


def _s8(v):
    return v - 256 if v >= 128 else v


def decode(b, i, pc):
    """Decode the instruction at b[i] (b: bytes-like, i: index) that runs at CPU address pc."""
    def byte(k):
        return b[i + k] if i + k < len(b) else 0

    op = byte(0)
    idx = None
    pre = 0
    if op in (0xDD, 0xFD):
        idx = 'IX' if op == 0xDD else 'IY'
        nxt = byte(1)
        if nxt in (0xDD, 0xFD, 0xED):
            return Ins(pc, 1, 'NOP', raw=bytes([op]))
        pre = 1
        op = nxt
    if op == 0xCB:
        if idx:
            d = _s8(byte(pre + 1))
            o2 = byte(pre + 2)
            n = 4
            mem = ('ix', idx, d)
        else:
            o2 = byte(1)
            n = 2
            mem = ('m', 'HL')
        x, y, z = o2 >> 6, (o2 >> 3) & 7, o2 & 7
        tgt = mem if (idx or z == 6) else ('r', R8[z])
        # undocumented DD CB d xx with z != 6 also copies the result to a register; rare, ignored (treated as the memory form)
        raw = bytes(b[i:i + n])
        if x == 0:
            return Ins(pc, n, ROT[y], tgt, raw=raw)
        if x == 1:
            return Ins(pc, n, 'BIT', ('bit', y), tgt, raw=raw)
        return Ins(pc, n, 'RES' if x == 2 else 'SET', ('bit', y), tgt, raw=raw)
    if op == 0xED and not idx:
        return _decode_ed(b, i, pc, byte)

    # unprefixed (or DD/FD-modified) opcode
    x, y, z, p, q = op >> 6, (op >> 3) & 7, op & 7, (op >> 4) & 3, (op >> 3) & 1
    k = pre + 1     # bytes so far (prefix + opcode)
    usesmem = False

    def r8(j, other_is_mem=False):
        nm = R8[j]
        if nm is None:
            return None
        if idx and nm in ('H', 'L') and not other_is_mem:
            return ('r', idx + nm)
        return ('r', nm)

    def hl():
        return ('rr', idx) if idx else ('rr', 'HL')

    def memhl():
        nonlocal k, usesmem
        usesmem = True
        if idx:
            d = _s8(byte(k))
            k += 1
            return ('ix', idx, d)
        return ('m', 'HL')

    def imm8():
        nonlocal k
        v = byte(k)
        k += 1
        return ('n', v)

    def imm16():
        nonlocal k
        v = byte(k) | (byte(k + 1) << 8)
        k += 2
        return ('nn', v)

    def fin(opname, a=None, bb=None):
        return Ins(pc, k, opname, a, bb, bytes(b[i:i + k]))

    if x == 1:
        if op == 0x76:
            return fin('HALT')
        if z == 6:
            m = memhl()
            return fin('LD', r8(y, True), m)
        if y == 6:
            m = memhl()
            return fin('LD', m, r8(z, True))
        return fin('LD', r8(y), r8(z))
    if x == 2:
        if z == 6:
            src = memhl()
        else:
            src = r8(z)
        return fin(ALU[y], ('r', 'A'), src)
    if x == 0:
        if z == 0:
            if y == 0:
                return fin('NOP')
            if y == 1:
                return fin('EXAF')
            d = _s8(byte(k))
            k += 1
            tgt = (pc + k + d) & 0xFFFF
            if y == 2:
                return fin('DJNZ', ('nn', tgt))
            if y == 3:
                return fin('JR', ('nn', tgt))
            return fin('JR', ('cc', CC[y - 4]), ('nn', tgt))
        if z == 1:
            if q == 0:
                nn = imm16()
                return fin('LD16', hl() if p == 2 else ('rr', RP[p]), nn)
            return fin('ADD16', hl(), hl() if p == 2 else ('rr', RP[p]))
        if z == 2:
            if q == 0:
                if p == 0:
                    return fin('LD', ('m', 'BC'), ('r', 'A'))
                if p == 1:
                    return fin('LD', ('m', 'DE'), ('r', 'A'))
                nn = imm16()
                if p == 2:
                    return fin('LD16', ('abs', nn[1]), hl())
                return fin('LD', ('abs', nn[1]), ('r', 'A'))
            if p == 0:
                return fin('LD', ('r', 'A'), ('m', 'BC'))
            if p == 1:
                return fin('LD', ('r', 'A'), ('m', 'DE'))
            nn = imm16()
            if p == 2:
                return fin('LD16', hl(), ('abs', nn[1]))
            return fin('LD', ('r', 'A'), ('abs', nn[1]))
        if z == 3:
            return fin('INC16' if q == 0 else 'DEC16', hl() if p == 2 else ('rr', RP[p]))
        if z in (4, 5):
            if y == 6:
                m = memhl()
                return fin('INC' if z == 4 else 'DEC', m)
            return fin('INC' if z == 4 else 'DEC', r8(y))
        if z == 6:
            if y == 6:
                m = memhl()
                v = imm8()
                return fin('LD', m, v)
            return fin('LD', r8(y), imm8())
        if z == 7:
            return fin(['RLCA', 'RRCA', 'RLA', 'RRA', 'DAA', 'CPL', 'SCF', 'CCF'][y])
    # x == 3
    if z == 0:
        return fin('RET', ('cc', CC[y]))
    if z == 1:
        if q == 0:
            return fin('POP', hl() if p == 2 else ('rr', RP2[p]))
        if p == 0:
            return fin('RET')
        if p == 1:
            return fin('EXX')
        if p == 2:
            return fin('JPIND', hl())
        return fin('LDSP', hl())
    if z == 2:
        nn = imm16()
        return fin('JP', ('cc', CC[y]), nn)
    if z == 3:
        if y == 0:
            return fin('JP', imm16())
        if y == 2:
            return fin('OUT', ('port', imm8()[1]), ('r', 'A'))
        if y == 3:
            return fin('IN', ('r', 'A'), ('port', imm8()[1]))
        if y == 4:
            return fin('EXSP', hl())
        if y == 5:
            return fin('EXDEHL')
        if y == 6:
            return fin('DI')
        return fin('EI')
    if z == 4:
        nn = imm16()
        return fin('CALL', ('cc', CC[y]), nn)
    if z == 5:
        if q == 0:
            return fin('PUSH', hl() if p == 2 else ('rr', RP2[p]))
        if p == 0:
            return fin('CALL', imm16())
        return fin('NOP')   # DD/ED/FD handled above
    if z == 6:
        return fin(ALU[y], ('r', 'A'), imm8())
    return fin('RST', ('nn', y * 8))


def _decode_ed(b, i, pc, byte):
    op = byte(1)
    x, y, z, p, q = op >> 6, (op >> 3) & 7, op & 7, (op >> 4) & 3, (op >> 3) & 1

    def fin(n, opname, a=None, bb=None):
        return Ins(pc, n, opname, a, bb, bytes(b[i:i + n]))

    if x == 1:
        if z == 0:
            return fin(2, 'IN', ('r', R8[y]) if y != 6 else None, ('portc',))
        if z == 1:
            return fin(2, 'OUT', ('portc',), ('r', R8[y]) if y != 6 else ('n', 0))
        if z == 2:
            return fin(2, 'SBC16' if q == 0 else 'ADC16', ('rr', 'HL'), ('rr', RP[p]))
        if z == 3:
            nn = byte(2) | (byte(3) << 8)
            if q == 0:
                return fin(4, 'LD16', ('abs', nn), ('rr', RP[p]))
            return fin(4, 'LD16', ('rr', RP[p]), ('abs', nn))
        if z == 4:
            return fin(2, 'NEG')
        if z == 5:
            return fin(2, 'RETI' if y == 1 else 'RETN')
        if z == 6:
            return fin(2, 'IM', ('n', [0, 0, 1, 2, 0, 0, 1, 2][y]))
        return fin(2, ['LDIA', 'LDRA', 'LDAI', 'LDAR', 'RRD', 'RLD', 'NOP', 'NOP'][y])
    if x == 2 and z <= 3 and y >= 4:
        names = [['LDI', 'CPI', 'INI', 'OUTI'], ['LDD', 'CPD', 'IND', 'OUTD'],
                 ['LDIR', 'CPIR', 'INIR', 'OTIR'], ['LDDR', 'CPDR', 'INDR', 'OTDR']]
        return fin(2, names[y - 4][z])
    return fin(2, 'NOP')


# ------------------------------------------------------------------------------------------- control flow

def flow(ins):
    """(falls_through, static target or None, kind) where kind is one of
    None, 'jp', 'jpc', 'call', 'callc', 'rst', 'ret', 'retc', 'ind', 'djnz', 'halt'."""
    op = ins.op
    if op == 'JP':
        if ins.a[0] == 'cc':
            return True, ins.b[1], 'jpc'
        return False, ins.a[1], 'jp'
    if op == 'JR':
        if ins.a[0] == 'cc':
            return True, ins.b[1], 'jpc'
        return False, ins.a[1], 'jp'
    if op == 'DJNZ':
        return True, ins.a[1], 'djnz'
    if op == 'CALL':
        if ins.a[0] == 'cc':
            return True, ins.b[1], 'callc'
        return True, ins.a[1], 'call'
    if op == 'RST':
        return True, ins.a[1], 'rst'
    if op in ('RET', 'RETI', 'RETN'):
        if ins.a is not None:
            return True, None, 'retc'
        return False, None, 'ret'
    if op == 'JPIND':
        return False, None, 'ind'
    return True, None, None
