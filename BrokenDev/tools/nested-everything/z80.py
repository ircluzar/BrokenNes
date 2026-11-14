"""Z80 helpers for the Nested Everything converters: an instruction-length / control-flow decoder and a tiny assembler
(only what the GG -> SMS runtime needs: labels, fix-ups, the instructions written in gg2sms_runtime.py)."""

# ----------------------------------------------------------------------------------------------- decoder

_LEN2 = {0x06, 0x0E, 0x16, 0x1E, 0x26, 0x2E, 0x36, 0x3E, 0x10, 0x18, 0x20, 0x28, 0x30, 0x38,
         0xC6, 0xCE, 0xD6, 0xDE, 0xE6, 0xEE, 0xF6, 0xFE, 0xD3, 0xDB, 0xCB}
_LEN3 = {0x01, 0x11, 0x21, 0x31, 0x22, 0x2A, 0x32, 0x3A,
         0xC2, 0xC3, 0xCA, 0xD2, 0xDA, 0xE2, 0xEA, 0xF2, 0xFA,
         0xC4, 0xCC, 0xCD, 0xD4, 0xDC, 0xE4, 0xEC, 0xF4, 0xFC}
_IDX_MEM = {0x34, 0x35, 0x36, 0x46, 0x4E, 0x56, 0x5E, 0x66, 0x6E, 0x7E, 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x77,
            0x86, 0x8E, 0x96, 0x9E, 0xA6, 0xAE, 0xB6, 0xBE}
_ED4 = {0x43, 0x4B, 0x53, 0x5B, 0x63, 0x6B, 0x73, 0x7B}


def length(b, i):
    """Length of the instruction at b[i]."""
    op = b[i]
    if op == 0xED:
        return 4 if i + 1 < len(b) and b[i + 1] in _ED4 else 2
    if op in (0xDD, 0xFD):
        if i + 1 >= len(b):
            return 1
        o2 = b[i + 1]
        if o2 == 0xCB:
            return 4
        if o2 in (0xDD, 0xFD, 0xED):
            return 1          # a lone prefix acts as a NOP
        n = 1 + (3 if o2 in _LEN3 else 2 if o2 in _LEN2 else 1)
        if o2 in _IDX_MEM:
            n += 1
        return n
    return 3 if op in _LEN3 else 2 if op in _LEN2 else 1


def flow(b, i, pc):
    """Control flow of the instruction at b[i] executed at CPU address pc: (falls_through, [absolute targets], kind).
    kind: 'jr' / 'djnz' (PC-relative, needs fixing when moved), 'abs', 'ret', 'ind' (computed), 'rst', None."""
    op = b[i]
    n = length(b, i)
    if op == 0x18:
        return False, [(pc + 2 + _s8(b[i + 1])) & 0xFFFF], 'jr'
    if op in (0x20, 0x28, 0x30, 0x38):
        return True, [(pc + 2 + _s8(b[i + 1])) & 0xFFFF], 'jr'
    if op == 0x10:
        return True, [(pc + 2 + _s8(b[i + 1])) & 0xFFFF], 'djnz'
    if op == 0xC3:
        return False, [b[i + 1] | b[i + 2] << 8], 'abs'
    if op in (0xC2, 0xCA, 0xD2, 0xDA, 0xE2, 0xEA, 0xF2, 0xFA, 0xCD, 0xC4, 0xCC, 0xD4, 0xDC, 0xE4, 0xEC, 0xF4, 0xFC):
        return True, [b[i + 1] | b[i + 2] << 8], 'abs'
    if op == 0xC9:
        return False, [], 'ret'
    if op in (0xC0, 0xC8, 0xD0, 0xD8, 0xE0, 0xE8, 0xF0, 0xF8):
        return True, [], 'ret'
    if op == 0xE9 or (op in (0xDD, 0xFD) and n == 2 and b[i + 1] == 0xE9):
        return False, [], 'ind'
    if op == 0xED and b[i + 1] in (0x45, 0x4D, 0x55, 0x5D, 0x65, 0x6D, 0x75, 0x7D):
        return False, [], 'ret'
    if op & 0xC7 == 0xC7:
        return False, [op & 0x38], 'rst'     # RST: may be followed by inline data, so the descent stops here
    return True, [], None


def _s8(v):
    return v - 256 if v >= 128 else v


# ----------------------------------------------------------------------------------------------- assembler

class Asm:
    """A two-pass-free assembler: code is emitted as bytes with symbolic fix-ups resolved by link(base)."""

    def __init__(self):
        self.code = bytearray()
        self.labels = {}
        self.fix = []          # (pos, kind, label): 'w' = 16-bit absolute, 'r' = 8-bit relative, 'h' = high byte, 'l' = low byte

    def here(self):
        return len(self.code)

    def label(self, name):
        assert name not in self.labels, name
        self.labels[name] = len(self.code)

    def db(self, *bs):
        for v in bs:
            if isinstance(v, (bytes, bytearray, list, tuple)):
                self.code += bytes(v)
            else:
                self.code.append(v & 0xFF)

    def dw_ref(self, label):
        self.fix.append((len(self.code), 'w', label)); self.code += b'\0\0'

    def op_w(self, opbytes, target):
        """opbytes followed by a 16-bit operand: an int (absolute) or a label name."""
        self.db(opbytes)
        if isinstance(target, int):
            self.db(target & 0xFF, target >> 8)
        else:
            self.dw_ref(target)

    def op_r(self, op, label):
        self.db(op); self.fix.append((len(self.code), 'r', label)); self.code.append(0)

    def link(self, base, extern=None):
        extern = extern or {}
        out = bytearray(self.code)
        for pos, kind, lab in self.fix:
            if lab in self.labels:
                addr = base + self.labels[lab]
            elif lab in extern:
                addr = extern[lab]
            else:
                raise KeyError(f'undefined label {lab}')
            if kind == 'w':
                out[pos] = addr & 0xFF; out[pos + 1] = addr >> 8 & 0xFF
            elif kind == 'r':
                d = addr - (base + pos + 1)
                if not -128 <= d <= 127:
                    raise ValueError(f'relative jump to {lab} out of range ({d})')
                out[pos] = d & 0xFF
        return bytes(out)

    def addr(self, base, name):
        return base + self.labels[name]
