"""Nested GB -> SNES (proof of concept): a Game Boy ROM becomes a SNES ROM (HiROM, FastROM).

    python gb2snes.py convert  <in.gb> <out.sfc> [--pcs run.pcs] [--count] [--report r.json]
    python gb2snes.py feedback <in.gb> <out.sfc> [--frames N] [--workshop exe] [--count] [--report r.json]

Static recompilation (docs/projects/nested-fantasy-research.md, Nested GB): every SM83 instruction the front end
(sm83.py) finds becomes a block of 65816 code; the GB registers live in direct page (C B E D L H SPlo SPhi F A at
$00-$09), the GB's 64KB address space in WRAM bank $7F (data bank $7F: GB address = 65816 absolute address), the GB
ROM is copied there at boot. Flags are computed exactly (ZNHC in GB layout) but only where a later instruction can
read them (a forward liveness scan). Indirect control flow (ret, jp (hl), calls into RAM) goes through a dispatcher:
4-byte entries per GB address in tables ($D1 ROM bank 0, $D2 bank 1, $D3 $C000-$FFFF), 0 = not translated -> a small
SM83 interpreter built from the same templates (self-modifying RAM code, unseen paths). RAM code the game copies from
ROM (found by the feedback) is translated at its RAM address.

Picture: GB tiles are SNES 2bpp tiles as they are. GB VRAM writes mark 128-byte chunks dirty; the NMI DMAs up to 24
dirty chunks a frame to two character areas (unsigned $8000 / signed $8800 tile numbering, picked with BG12NBA) and the
two maps (BG1SC), low bytes only; BGP -> CGRAM 0-3; SCX/SCY -> BG1 scroll (the GB picture centred). LY comes from the
SNES V counter, P1 from the auto-read joypad, serial bytes are logged at $7E:4000 (count $7E:3FFE).

--count builds a measuring ROM: each translated or interpreted GB instruction adds 1 to a 24-bit counter at $18.
The runtime notes when the game reaches a 'jr $' / 'jp $' end loop: $20 = 1, $21-22 = NMI count, $23-24 = V counter.
"""
import argparse, json, os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import sm83
from asm816 import Assembler, assemble

R8 = {'b': '$01', 'c': '$00', 'd': '$03', 'e': '$02', 'h': '$05', 'l': '$04', 'a': '$09'}
RP = {'bc': '$00', 'de': '$02', 'hl': '$04', 'sp': '$06', 'af': '$08'}
CODE_BANKS = list(range(0xC2, 0xD0))
GBROM_BANK = 0xD0
TAB_BANK = {0: 0xD1, 1: 0xD2, 3: 0xD3}
BFIX = ['xba', 'lda #$00', 'xba']          # the hidden B accumulator back to 0 (TAX / TAY stay clean)

# ------------------------------------------------------------------------------------------------- flag liveness

def flag_rw(op, b1):
    """(reads, writes) flag masks (Z=8 N=4 H=2 C=1) of an SM83 instruction, and whether flow continues plainly."""
    ALL = 15
    if op == 0xCB:
        x = b1 >> 6
        if x == 0: return (1 if (b1 >> 3) & 7 in (2, 3) else 0), ALL
        if x == 1: return 0, 14
        return 0, 0
    if 0x80 <= op <= 0xBF or op in (0xC6, 0xCE, 0xD6, 0xDE, 0xE6, 0xEE, 0xF6, 0xFE):
        y = (op >> 3) & 7
        return (1 if y in (1, 3) else 0), ALL
    if op in (0x07, 0x0F): return 0, ALL
    if op in (0x17, 0x1F): return 1, ALL
    if op == 0x27: return 7, 11
    if op == 0x2F: return 0, 6
    if op == 0x37: return 0, 7
    if op == 0x3F: return 1, 7
    if op & 0xC7 in (0x04, 0x05): return 0, 14
    if op in (0x09, 0x19, 0x29, 0x39): return 0, 7
    if op in (0xE8, 0xF8, 0xF1): return 0, ALL
    if op == 0xF5: return ALL, 0
    if op in (0x20, 0x28, 0xC2, 0xCA, 0xC0, 0xC8, 0xC4, 0xCC): return ALL, 0     # conditional: the target may read any
    if op in (0x30, 0x38, 0xD2, 0xDA, 0xD0, 0xD8, 0xD4, 0xDC): return ALL, 0
    return 0, 0


def flags_needed(an, bank, pc, n):
    """Mask of the flags this instruction writes that some later instruction may read (scan the fallthrough)."""
    need, written = 0, 0
    p = pc + n
    for _ in range(12):
        if (bank, p) not in an.insn:
            return 15 & ~written | need
        qn, qm, qfl = an.insn[(bank, p)]
        rd = an.reader_for(bank, p)
        op, b1 = rd(p), rd(p + 1)
        r, w = flag_rw(op, b1)
        need |= r & ~written
        written |= w
        if written | need == 15:
            return need
        if not qfl[0] or qfl[2] in ('call', 'rst', 'jp', 'jr', 'ret', 'ind', 'halt', 'stop', 'illegal') and op not in (0x18,):
            return need | (15 & ~written)
        if op == 0x18:
            return need | (15 & ~written)
        p += qn
    return need | (15 & ~written)


# ------------------------------------------------------------------------------------------------- code generation

class Gen:
    """65816 text for one SM83 instruction. mode 'static': operands and targets known (translation); 'interp':
    operands in $0C/$0D, the next PC in $16 (interpreter handlers)."""

    def __init__(self, label_of, count=False):
        self.label_of = label_of          # (gb address, from (bank, pc)) -> label or None
        self.k = 0
        self.count = count

    def u(self):
        self.k += 1
        return 'U%d' % self.k

    # memory -----------------------------------------------------------------------------------------------
    def rd_x(self):
        """A = GB memory at X (X = address)."""
        f, d = self.u(), self.u()
        return ['cpx #$E000', 'bcc ' + f, 'jsl rdx', 'bra ' + d, f + ':', 'lda.w $0000,x', d + ':']

    def wr_x(self):
        """GB memory at X = A (X is lost)."""
        s, d = self.u(), self.u()
        return ['cpx #$C000', 'bcc ' + s, 'cpx #$E000', 'bcs ' + s, 'sta.w $0000,x', 'bra ' + d, s + ':', 'jsl wrx', d + ':']

    def rd_const(self, a):
        if a < 0xE000 or 0xFF80 <= a: return ['lda.w $%04X' % a]
        if 0xFF00 <= a < 0xFF80 and (a & 0xFF) not in (0x00, 0x04, 0x41, 0x44): return ['lda.w $%04X' % a]
        return ['ldx #$%04X' % a, 'jsl rdx']

    def wr_const(self, a):
        if 0xA000 <= a < 0xE000 or 0xFF80 <= a < 0xFFFF: return ['sta.w $%04X' % a]
        if 0xFF00 <= a < 0xFF80 and (a & 0xFF) not in (0x01, 0x02, 0x04, 0x0F, 0x46): return ['sta.w $%04X' % a]
        return ['ldx #$%04X' % a, 'jsl wrx']

    # flags ---------------------------------------------------------------------------------------------------
    def zc(self, table, mask='#$03', extra=None):
        out = ['php', 'pla', 'and ' + mask, 'tax', 'lda.l %s,x' % table]
        if extra: out += extra
        return out + ['sta $08']

    def setc(self):
        """65816 C = GB C."""
        return ['lda $08', 'and #$10', 'cmp #$10']

    # control flow -------------------------------------------------------------------------------------------
    def goto(self, ctx, target=None, back_poll=False):
        if ctx['mode'] == 'interp':
            return ['jml dispatch']                       # X = target already
        lab = self.label_of(target, ctx['key'])
        out = []
        if back_poll and target <= ctx['pc']:
            sk = self.u()
            out += ['lda $15', 'beq ' + sk, 'ldx #$%04X' % target, 'jml irqtake', sk + ':']
        if lab: return out + ['jml ' + lab]
        return out + ['ldx #$%04X' % target, 'jml dispatch']

    def target_x(self, ctx, kind, target):
        """Interp: X = the jump target (rel: next PC + e in $0C; abs: $0C)."""
        if ctx['mode'] != 'interp': return []
        if kind == 'abs': return ['ldx $0C']
        p = self.u()
        return ['rep #$20', 'lda $0C', 'and #$00FF', 'cmp #$0080', 'bcc ' + p, 'ora #$FF00', p + ':', 'clc', 'adc $16',
                'tax', 'sep #$20'] + BFIX

    def push_const(self, ctx, value):
        out = ['ldx $06', 'dex', 'dex', 'stx $06']
        if ctx['mode'] == 'interp':
            return out + ['lda $16', 'sta.w $0000,x', 'lda $17', 'sta.w $0001,x']
        return out + ['lda #$%02X' % (value & 0xFF), 'sta.w $0000,x', 'lda #$%02X' % (value >> 8), 'sta.w $0001,x']

    def cond(self, cc, skip):
        """Branch to skip when condition cc does NOT hold."""
        mask = '#$80' if cc in ('nz', 'z') else '#$10'
        br = 'bne' if cc in ('nz', 'nc') else 'beq'
        return ['lda $08', 'and ' + mask, br + ' ' + skip]

    # one instruction -----------------------------------------------------------------------------------------
    def insn(self, op, b1, b2, ctx):
        I = ctx['mode'] == 'interp'
        pc, nxt = ctx['pc'], ctx['next']
        fl = ctx.get('flags', 15)
        imm8 = '$0C' if I else '#$%02X' % b1
        w = b1 | b2 << 8
        imm16 = '$0C' if I else '#$%04X' % w
        e = sm83.s8(b1)
        out = []
        if self.count:
            c = self.u()
            out += ['inc $18', 'bne ' + c, 'inc $19', 'bne ' + c, 'inc $1A', c + ':']
        x, y, z = op >> 6, (op >> 3) & 7, op & 7
        R = sm83.R8

        def src(r):           # A = 8-bit operand r
            if r == '(hl)': return ['ldx $04'] + self.rd_x()
            return ['lda ' + R8[r]]

        def alu(k, load):     # GB A op= operand (load: lines putting it in A)
            o = load + ['sta $0A']
            if k in (0, 1, 2, 3, 7):
                if fl: o += ['lda $09', 'eor $0A', 'sta $0B']
                if k == 1: o += self.setc()
                if k == 3: o += ['lda $08', 'and #$10', 'eor #$10', 'cmp #$10']
                o += ['lda $09']
                if k == 0: o += ['clc', 'adc $0A']
                elif k == 1: o += ['adc $0A']
                elif k in (2, 7): o += ['sec', 'sbc $0A']
                else: o += ['sbc $0A']
                if k != 7: o += ['sta $09']
                if fl:
                    o += ['php', 'eor $0B', 'and #$10', 'asl a', 'sta $0B', 'pla', 'and #$03', 'tax',
                          'lda.l %s,x' % ('ZC_ADD' if k < 2 else 'ZC_SUB'), 'ora $0B', 'sta $08']
                return o
            mn = {4: 'and', 5: 'eor', 6: 'ora'}[k]
            o += ['lda $09', mn + ' $0A', 'sta $09']
            if fl:
                a, b = self.u(), self.u()
                hz, hn = ('#$A0', '#$20') if k == 4 else ('#$80', '#$00')
                o += ['beq ' + a, 'lda ' + hn, 'bra ' + b, a + ':', 'lda ' + hz, b + ':', 'sta $08']
            return o

        def incdec(loc, dec):     # loc: a direct-page byte
            o = ['dec ' + loc if dec else 'inc ' + loc]
            if fl:
                nz, st, z0 = self.u(), self.u(), self.u()
                o += ['lda $08', 'and #$10'] + (['ora #$40'] if dec else []) + ['sta $0B', 'lda ' + loc, 'bne ' + nz,
                      'lda #$%02X' % (0x80 if dec else 0xA0), 'bra ' + st, nz + ':', 'and #$0F']
                if dec: o += ['cmp #$0F', 'bne ' + z0]
                else: o += ['bne ' + z0]
                o += ['lda #$20', 'bra ' + st, z0 + ':', 'lda #$00', st + ':', 'ora $0B', 'sta $08']
            return o

        def cbop(k, loc):         # rotate / shift / swap a direct-page byte, flags Z00C
            o = []
            if k == 0: o += ['lda ' + loc, 'cmp #$80', 'rol a', 'sta ' + loc]
            elif k == 1:
                s = self.u(); o += ['lda ' + loc, 'lsr a', 'bcc ' + s, 'ora #$80', s + ':', 'sta ' + loc, 'ora #$00']
            elif k == 2: o += self.setc() + ['rol ' + loc]
            elif k == 3: o += self.setc() + ['ror ' + loc]
            elif k == 4: o += ['asl ' + loc]
            elif k == 5: o += ['lda ' + loc, 'cmp #$80', 'ror a', 'sta ' + loc]
            elif k == 6: o += ['lda ' + loc, 'asl a', 'adc #$80', 'rol a', 'asl a', 'adc #$80', 'rol a', 'sta ' + loc]
            else: o += ['lsr ' + loc]
            if fl or ctx.get('rotA'):
                if k == 1:
                    o = o[:-1] + ['php', 'pla', 'and #$01', 'sta $0B', 'lda ' + loc, 'bne ' + self.u() + 'x']
                    # (rrc: Z from the stored value, C kept above)
                    lab = o[-1].split()[1]
                    o += ['lda #$80', 'ora $0B', 'bra ' + lab + 'y', lab + ':', 'lda $0B', lab + 'y:', 'tax', 'lda.l ZC_ADD2,x', 'sta $08']
                else:
                    o += self.zc('ZC_ADD', '#$02' if k == 6 else '#$03')
            return o

        if op == 0xCB:
            cb = b1 if not I else None
            return out + self.cb(ctx)
        if x == 1:
            if op == 0x76:
                return out + ['ldx #$%04X' % nxt if not I else 'ldx $16', 'jml halt_rt']
            d, s = R[y], R[z]
            if d == '(hl)': return out + src(s) + ['ldx $04'] + self.wr_x()
            if s == '(hl)': return out + src(s) + ['sta ' + R8[d]]
            if d == s: return out
            return out + ['lda ' + R8[s], 'sta ' + R8[d]]
        if x == 2:
            return out + alu(y, src(R[z]))
        if op in (0xC6, 0xCE, 0xD6, 0xDE, 0xE6, 0xEE, 0xF6, 0xFE):
            return out + alu(y, ['lda ' + imm8])
        if op == 0x00 or op == 0x10: return out
        # 8-bit loads
        if x == 0 and z == 6:
            d = R[y]
            if d == '(hl)': return out + ['lda ' + imm8, 'ldx $04'] + self.wr_x()
            return out + ['lda ' + imm8, 'sta ' + R8[d]]
        if op in (0x02, 0x12): return out + ['lda $09', 'ldx ' + ('$00' if op == 0x02 else '$02')] + self.wr_x()
        if op in (0x0A, 0x1A): return out + ['ldx ' + ('$00' if op == 0x0A else '$02')] + self.rd_x() + ['sta $09']
        if op in (0x22, 0x32): return out + ['lda $09', 'ldx $04'] + self.wr_x() + ['ldx $04', 'inx' if op == 0x22 else 'dex', 'stx $04']
        if op in (0x2A, 0x3A): return out + ['ldx $04'] + self.rd_x() + ['sta $09', 'ldx $04', 'inx' if op == 0x2A else 'dex', 'stx $04']
        if op in (0xE0, 0xF0, 0xE2, 0xF2, 0xEA, 0xFA):
            if not I and op in (0xE0, 0xF0, 0xEA, 0xFA):
                a = (0xFF00 | b1) if op in (0xE0, 0xF0) else w
                if op in (0xE0, 0xEA):
                    o = ['lda $09'] + self.wr_const(a)
                    if a in (0xFF0F, 0xFFFF):        # a requested, enabled interrupt is taken right after the write
                        sk = self.u()
                        o += ['lda $15', 'beq ' + sk, 'ldx #$%04X' % nxt, 'jml irqtake', sk + ':']
                    return out + o
                return out + self.rd_const(a) + ['sta $09']
            if op in (0xE0, 0xF0): addr = ['lda $0C', 'sta $0E', 'lda #$FF', 'sta $0F', 'ldx $0E']
            elif op in (0xE2, 0xF2): addr = ['lda $00', 'sta $0E', 'lda #$FF', 'sta $0F', 'ldx $0E']
            else: addr = ['ldx $0C']
            if op in (0xE0, 0xE2, 0xEA): return out + addr + ['lda $09'] + self.wr_x()
            return out + addr + self.rd_x() + ['sta $09']
        # 16-bit loads / arithmetic
        if x == 0 and z == 1 and not y & 1: return out + ['ldx ' + imm16, 'stx ' + RP[sm83.R16[y >> 1]]]
        if x == 0 and z == 3:
            r = RP[sm83.R16[y >> 1]]
            return out + ['ldx ' + r, 'dex' if y & 1 else 'inx', 'stx ' + r]
        if x == 0 and z == 1:
            r = RP[sm83.R16[y >> 1]]
            o = ['rep #$20']
            if fl: o += ['lda $04', 'eor ' + r, 'sta $0E']
            o += ['lda $04', 'clc', 'adc ' + r, 'sta $04']
            if fl:
                o += ['eor $0E', 'sta $0E', 'sep #$20'] + BFIX
                s = self.u()
                o += ['lda $08', 'and #$80', 'bcc ' + s, 'ora #$10', s + ':', 'sta $08', 'lda $0F', 'and #$10', 'asl a', 'ora $08', 'sta $08']
            else:
                o += ['sep #$20'] + BFIX
            return out + o
        if x == 0 and z in (4, 5):
            r = R[y]
            if r == '(hl)':
                return out + ['ldx $04'] + self.rd_x() + ['sta $0A'] + incdec('$0A', z == 5) + ['lda $0A', 'ldx $04'] + self.wr_x()
            return out + incdec(R8[r], z == 5)
        if op in (0x07, 0x0F, 0x17, 0x1F):
            o = cbop({0x07: 0, 0x0F: 1, 0x17: 2, 0x1F: 3}[op], '$09')
            if fl: o += ['lda $08', 'and #$10', 'sta $08']          # Z N H = 0
            return out + o
        if op == 0x27: return out + ['jsl gbdaa']
        if op == 0x2F: return out + ['lda $09', 'eor #$FF', 'sta $09', 'lda $08', 'ora #$60', 'sta $08']
        if op == 0x37: return out + ['lda $08', 'and #$80', 'ora #$10', 'sta $08']
        if op == 0x3F: return out + ['lda $08', 'and #$90', 'eor #$10', 'sta $08']
        if op == 0x08:
            a = ['ldx $0C'] if I else ['ldx #$%04X' % w]
            return out + a + ['lda $06'] + self.wr_x() + (['ldx $0C', 'inx'] if I else ['ldx #$%04X' % ((w + 1) & 0xFFFF)]) + ['lda $07'] + self.wr_x()
        if op in (0xE8, 0xF8):
            dst = '$06' if op == 0xE8 else '$04'
            o = ['lda $06', 'eor ' + imm8, 'sta $0B', 'lda $06', 'clc', 'adc ' + imm8, 'php', 'eor $0B', 'and #$10', 'asl a',
                 'sta $0B', 'pla', 'and #$01', 'tax', 'lda.l ZC_ADD,x', 'ora $0B', 'sta $08']
            if I:
                p = self.u()
                o += ['rep #$20', 'lda $0C', 'and #$00FF', 'cmp #$0080', 'bcc ' + p, 'ora #$FF00', p + ':', 'clc', 'adc $06', 'sta ' + dst, 'sep #$20'] + BFIX
            else:
                o += ['rep #$20', 'lda $06', 'clc', 'adc #$%04X' % (e & 0xFFFF), 'sta ' + dst, 'sep #$20'] + BFIX
            return out + o
        if op == 0xF9: return out + ['ldx $04', 'stx $06']
        # stack
        if x == 3 and z == 5 and not y & 1:
            r = int(RP[sm83.R16S[y >> 1]][1:], 16)
            return out + ['ldx $06', 'dex', 'dex', 'stx $06', 'lda $%02X' % r, 'sta.w $0000,x', 'lda $%02X' % (r + 1), 'sta.w $0001,x']
        if x == 3 and z == 1 and not y & 1:
            r = int(RP[sm83.R16S[y >> 1]][1:], 16)
            lo = ['lda.w $0000,x'] + (['and #$F0'] if r == 8 else [])
            return out + ['ldx $06'] + lo + ['sta $%02X' % r, 'lda.w $0001,x', 'sta $%02X' % (r + 1), 'inx', 'inx', 'stx $06']
        if op == 0xF3: return out + ['stz $14', 'stz $15']
        if op == 0xFB:
            sk = self.u()
            return out + ['lda #$01', 'sta $14', 'jsl updpend', 'lda $15', 'beq ' + sk,
                          ('ldx $16' if I else 'ldx #$%04X' % nxt), 'jml irqtake', sk + ':']
        # control flow
        if op in (0x18, 0x20, 0x28, 0x30, 0x38):
            t = (nxt + e) & 0xFFFF
            if op == 0x18 and not I and t == pc: return out + ['ldx #$%04X' % pc, 'jml done_rt']
            if op == 0x18: return out + self.target_x(ctx, 'rel', t) + self.goto(ctx, t, True)
            sk = self.u()
            return out + self.cond(sm83.CC[y - 4], sk) + self.target_x(ctx, 'rel', t) + self.goto(ctx, t, True) + [sk + ':']
        if op in (0xC3, 0xC2, 0xCA, 0xD2, 0xDA):
            if op == 0xC3 and not I and w == pc: return out + ['ldx #$%04X' % pc, 'jml done_rt']
            if op == 0xC3: return out + self.target_x(ctx, 'abs', w) + self.goto(ctx, w, True)
            sk = self.u()
            return out + self.cond(sm83.CC[y], sk) + self.target_x(ctx, 'abs', w) + self.goto(ctx, w, True) + [sk + ':']
        if op == 0xE9: return out + ['ldx $04', 'jml dispatch']
        if op in (0xCD, 0xC4, 0xCC, 0xD4, 0xDC) or (x == 3 and z == 7):
            if z == 7: t, kind = y * 8, 'const'
            else: t, kind = w, 'abs'
            sk = self.u()
            o = self.cond(sm83.CC[y], sk) if op in (0xC4, 0xCC, 0xD4, 0xDC) else []
            o += self.push_const(ctx, nxt)
            if I: o += (['ldx $0C'] if kind == 'abs' else ['ldx #$%04X' % t]) + ['jml dispatch']
            else: o += self.goto(ctx, t)
            return out + o + [sk + ':']
        if op in (0xC9, 0xD9, 0xC0, 0xC8, 0xD0, 0xD8):
            sk = self.u()
            o = self.cond(sm83.CC[y], sk) if op not in (0xC9, 0xD9) else []
            if op == 0xD9: o += ['lda #$01', 'sta $14', 'jsl updpend']
            o += ['ldx $06', 'lda.w $0001,x', 'xba', 'lda.w $0000,x', 'inx', 'inx', 'stx $06', 'tax', 'lda #$00', 'xba', 'jml dispatch']
            return out + o + [sk + ':']
        if op in sm83.ILLEGAL: return out + ['ldx #$%04X' % pc if not I else 'ldx $16', 'jml done_rt']
        raise ValueError('no template for $%02X' % op)

    def cb(self, ctx):
        """The CB page: in 'static' the byte is ctx['cb']; in interp the handler is per CB byte too."""
        b = ctx['cb']
        fl = ctx.get('flags', 15)
        x, y, z = b >> 6, (b >> 3) & 7, b & 7
        r = sm83.R8[z]
        mem = r == '(hl)'
        loc = '$0A' if mem else R8[r]
        pre = (['ldx $04'] + self.rd_x() + ['sta $0A']) if mem else []
        post = (['lda $0A', 'ldx $04'] + self.wr_x()) if mem else []
        save = ctx.get('flags')
        if x == 0:
            o = self.insn_cbrot(y, loc, fl)
            return pre + o + post
        if x == 1:
            s, d = self.u(), self.u()
            return pre + ['lda $08', 'and #$10', 'ora #$20', 'sta $0B', 'lda ' + loc, 'and #$%02X' % (1 << y), 'bne ' + s,
                          'lda #$80', 'ora $0B', 'bra ' + d, s + ':', 'lda $0B', d + ':', 'sta $08']
        m = 1 << y
        if mem:
            return pre + ['lda $0A', ('and #$%02X' % (~m & 0xFF)) if x == 2 else ('ora #$%02X' % m), 'sta $0A'] + post
        return ['lda #$%02X' % m, ('trb ' if x == 2 else 'tsb ') + loc]

    def insn_cbrot(self, k, loc, fl):
        o = []
        if k == 0: o += ['lda ' + loc, 'cmp #$80', 'rol a', 'sta ' + loc]
        elif k == 1:
            s = self.u(); o += ['lda ' + loc, 'lsr a', 'bcc ' + s, 'ora #$80', s + ':', 'sta ' + loc]
        elif k == 2: o += ['lda $08', 'and #$10', 'cmp #$10', 'rol ' + loc]
        elif k == 3: o += ['lda $08', 'and #$10', 'cmp #$10', 'ror ' + loc]
        elif k == 4: o += ['asl ' + loc]
        elif k == 5: o += ['lda ' + loc, 'cmp #$80', 'ror a', 'sta ' + loc]
        elif k == 6: o += ['lda ' + loc, 'asl a', 'adc #$80', 'rol a', 'asl a', 'adc #$80', 'rol a', 'sta ' + loc]
        else: o += ['lsr ' + loc]
        if fl:
            # C from the 65816 carry (swap: 0), Z from the stored value
            s = self.u()
            o += ['lda #$00', 'rol a'] if k != 6 else ['lda #$00']
            o += ['asl a', 'asl a', 'asl a', 'asl a', 'sta $0B', 'lda ' + loc, 'bne ' + s, 'lda #$80', 'tsb $0B', s + ':', 'lda $0B', 'sta $08']
        return o


def rot_a(gen, op, fl):
    """rlca / rrca / rla / rra: the CB rotation on A with Z forced to 0."""
    k = {0x07: 0, 0x0F: 1, 0x17: 2, 0x1F: 3}[op]
    o = gen.insn_cbrot(k, '$09', fl)
    if fl: o += ['lda $08', 'and #$10', 'sta $08']
    return o


# ------------------------------------------------------------------------------------------------- runtime

def runtime_text(chunks_tab, lytab):
    return r'''
; ============================================================ reset (entered at $00:8xxx, continues in FastROM $C0)
reset:  sei
        clc
        xce
        rep #$30
        .a16
        ldx #$1FFF
        txs
        lda #$0000
        tcd
        sep #$20
        .a8
        lda #$01
        sta.l $00420D
        jml fast
fast:   lda #$80
        sta.l $002100
        lda #$00
        sta.l $004200
        ; clear WRAM bank $7F, then the GB ROM image into $7F:0000-$7FFF
        rep #$30
        .a16
        lda #$0000
        sta.l $7F8000
        lda #$7FFD
        ldx #$8000
        ldy #$8001
        mvn $7F,$7F
        lda #$7FFF
        ldx #$0000
        ldy #$0000
        mvn $7F,$D0
        sep #$20
        .a8
        ; (data bank is $7F now)
        ; PPU: mode 0, BG1 2bpp, maps $2000/$2400, tiles at $0000 (unsigned) / $1000 (signed)
        lda #$00
        sta.l $002105
        lda #$20
        sta.l $002107
        lda #$00
        sta.l $00210B
        jsr oaminit
        lda #$02
        sta.l $002101
        lda #$11
        sta.l $00212C
        lda #$00
        sta.l $00212D
        ; clear VRAM (64KB) with a fixed-source DMA
        lda #$80
        sta.l $002115
        rep #$20
        .a16
        lda #$0000
        sta.l $002116
        lda #$1809
        sta.l $004300
        lda #ZERO
        sta.l $004302
        lda #$0000
        sta.l $004305
        sep #$20
        .a8
        lda #bank(ZERO)
        sta.l $004304
        lda #$01
        sta.l $00420B
        ; GB I/O defaults, registers as the boot ROM leaves them
        lda #$91
        sta.w $FF40
        lda #$FC
        sta.w $FF47
        lda #$CF
        sta.w $FF00
        lda #$E1
        sta.w $FF0F
        lda #$7E
        sta.w $FF02
        stz $14
        stz $15
        rep #$20
        .a16
        lda #$0013
        sta $00
        lda #$00D8
        sta $02
        lda #$014D
        sta $04
        lda #$FFFE
        sta $06
        lda #$01B0
        sta $08
        lda #$0000
        sta $18
        sta $1A
        sta $1C
        sta $20
        sta.l $7E3FFE
        sep #$20
        .a8
        ldx #$0000
.dirty: lda #$01
        sta $40,x
        inx
        cpx #$0040
        bne .dirty
        lda #$FF
        sta $30
        sta $31
        sta $32
        lda #$81
        sta.l $004200
        lda #$0F
        sta.l $002100
        lda #$00
        xba
        lda #$00
        ldx #$0100
        jml dispatch

; ============================================================ dispatch: X = GB address -> translated code / interpreter
dispatch:
        stx $0E
        rep #$20
        .a16
        txa
        and #$C000
        beq .r0
        cmp #$4000
        beq .r1
        cmp #$C000
        beq .r3
        sep #$20
        .a8
        jml interp
.r0:    .a16
        txa
        asl a
        asl a
        tax
        lda.l $D10000,x
        sta $10
        lda.l $D10002,x
        bra .go
.r1:    txa
        asl a
        asl a
        tax
        lda.l $D20000,x
        sta $10
        lda.l $D20002,x
        bra .go
.r3:    txa
        asl a
        asl a
        tax
        lda.l $D30000,x
        sta $10
        lda.l $D30002,x
.go:    sta $12
        sep #$20
        .a8
        xba
        lda #$00
        xba
        lda $12
        beq .int
        jml [$0010]
.int:   ldx $0E
        jml interp

; ============================================================ GB memory: reads at $E000+ and the special writes
rdx:    cpx #$FE00
        bcs .hi
        rep #$20
        .a16
        txa
        sec
        sbc #$2000
        tax
        sep #$20
        .a8
        xba
        lda #$00
        xba
        lda.w $0000,x
        rtl
.hi:    cpx #$FF00
        bcc .pl
        cpx #$FF80
        bcs .pl
        txa
        cmp #$44
        beq .ly
        cmp #$00
        beq .joy
        cmp #$41
        beq .stat
        cmp #$04
        beq .div
.pl:    lda.w $0000,x
        rtl
.div:   lda.l $00213F
        lda.l $002137
        lda.l $00213D
        sta.w $FF04
        lda.l $00213D
        lda.w $FF04
        rtl
.ly:    jsr getly
        sta.w $FF44
        rtl
.stat:  jsr getly
        cmp #144
        lda.w $FF41
        and #$FC
        bcc .s0
        ora #$01
.s0:    sta.w $FF41
        rtl
.joy:   lda.w $FF00
        and #$30
        sta $0B
        lda #$0F
        sta $0A
        lda $0B
        and #$10
        bne .nd
        lda.l $004219
        and #$0F
        tax
        lda.l DIRMAP,x
        eor #$0F
        and $0A
        sta $0A
.nd:    lda $0B
        and #$20
        bne .nb
        lda.l $004218
        and #$80
        beq .na
        lda $0A
        and #$0E
        sta $0A
.na:    lda.l $004219
        and #$80
        beq .nbb
        lda $0A
        and #$0D
        sta $0A
.nbb:   lda.l $004219
        and #$20
        beq .nsel
        lda $0A
        and #$0B
        sta $0A
.nsel:  lda.l $004219
        and #$10
        beq .nb
        lda $0A
        and #$07
        sta $0A
.nb:    lda $0A
        ora $0B
        ora #$C0
        sta.w $FF00
        ldx #$FF00
        rtl

getly:  lda.l $00213F
        lda.l $002137
        lda.l $00213D
        sta $0E
        lda.l $00213D
        and #$01
        sta $0F
        ldx $0E
        lda.l LYTAB,x
        cmp #144
        bcc .r
        ; in vblank the NMI may hide whole lines: polls step through 144-153 so 'wait for LY == n' always meets n
        lda $33
        inc a
        cmp #10
        bcc .s
        lda #$00
.s:     sta $33
        clc
        adc #144
.r:     rts

wrx:    cpx #$8000
        bcc .rom
        cpx #$A000
        bcc .vram
        cpx #$E000
        bcc .pl
        cpx #$FE00
        bcc .echo
        cpx #$FF00
        bcc .pl
        cpx #$FF80
        bcc .io
        cpx #$FFFF
        beq .ie
.pl:    sta.w $0000,x
        rtl
.rom:   rtl
.ie:    sta.w $FFFF
        jsr updp
        rtl
.echo:  pha
        rep #$20
        .a16
        txa
        sec
        sbc #$2000
        tax
        sep #$20
        .a8
        xba
        lda #$00
        xba
        pla
        sta.w $0000,x
        rtl
.vram:  sta.w $0000,x
        pha
        rep #$20
        .a16
        txa
        and #$1F80
        asl a
        xba
        tax
        sep #$20
        .a8
        xba
        lda #$00
        xba
        lda #$01
        sta $40,x
        pla
        rtl
.io:    sta.w $0000,x
        pha
        txa
        cmp #$02
        beq .sc
        cmp #$0F
        beq .if
        cmp #$04
        beq .div
        cmp #$46
        beq .dma
        pla
        rtl
.if:    jsr updp
        pla
        rtl
.div:   lda #$00
        sta.w $FF04
        pla
        rtl
.dma:   pla
        pha
        sta $0F
        stz $0E
        rep #$30
        .a16
        ldx $0E
        ldy #$FE00
        lda #$009F
        mvn $7F,$7F
        sep #$20
        .a8
        xba
        lda #$00
        xba
        pla
        rtl
.sc:    lda.w $FF02
        and #$81                    ; a transfer on the GB's own clock completes; external clock: nobody drives it
        cmp #$81
        bne .scx
        rep #$20
        .a16
        lda.l $7E3FFE
        tax
        inc a
        and #$1FFF
        sta.l $7E3FFE
        sep #$20
        .a8
        xba
        lda #$00
        xba
        lda.w $FF01
        sta.l $7E4000,x
        lda #$FF                    ; nobody on the link cable: the GB shifts in $FF
        sta.w $FF01
        lda.w $FF02
        and #$7F
        sta.w $FF02
        lda.w $FF0F
        ora #$08
        sta.w $FF0F
        jsr updp
.scx:   pla
        rtl

updpend: jsr updp
        rtl
updp:   lda $14
        beq .z
        lda.w $FFFF
        and.w $FF0F
        and #$1F
.z:     sta $15
        rts

; ============================================================ GB interrupts: X = where the GB was going
irqtake: stx $0E
        lda.w $FFFF
        and.w $FF0F
        and #$1F
        beq .none
        stz $14
        stz $15
        sta $0B
        lda #$01
        sta $0A
        ldy #$0040
.lp:    lsr $0B
        bcs .got
        asl $0A
        iny
        iny
        iny
        iny
        iny
        iny
        iny
        iny
        bra .lp
.got:   lda $0A
        eor #$FF
        and.w $FF0F
        sta.w $FF0F
        ldx $06
        dex
        dex
        stx $06
        lda $0E
        sta.w $0000,x
        lda $0F
        sta.w $0001,x
        tyx
        jml dispatch
.none:  ldx $0E
        jml dispatch

halt_rt: stx $0E
.w:     lda.w $FFFF
        and.w $FF0F
        and #$1F
        bne .wake
        wai
        bra .w
.wake:  lda $14
        beq .cont
        ldx $0E
        jml irqtake
.cont:  ldx $0E
        jml dispatch

; the game reached its end loop (jr $ / jp $): note when, then idle (interrupts still taken)
done_rt: stx $0E
        lda $20
        bne .idle
        lda #$01
        sta $20
        rep #$20
        .a16
        lda $1C
        sta $21
        sep #$20
        .a8
        lda.l $00213F
        lda.l $002137
        lda.l $00213D
        sta $23
        lda.l $00213D
        and #$01
        sta $24
        lda.l $00213C
        sta $25
        lda.l $00213C
        and #$01
        sta $26
        xba
        lda #$00
        xba
.idle:  lda $15
        beq .wai
        ldx $0E
        jml irqtake
.wai:   wai
        bra .idle

; ============================================================ GB DAA on $09 / $08
gbdaa:  lda $08
        sta $0B
        lda $09
        bit $0B
        bvs .sub
        lda $0B
        and #$10
        bne .a60
        lda $09
        cmp #$9A
        bcc .l1
.a60:   lda $09
        clc
        adc #$60
        sta $09
        lda $0B
        ora #$10
        sta $0B
.l1:    lda $0B
        and #$20
        bne .a06
        lda $09
        and #$0F
        cmp #$0A
        bcc .fin
.a06:   lda $09
        clc
        adc #$06
        sta $09
        bra .fin
.sub:   lda $0B
        and #$10
        beq .s1
        lda $09
        sec
        sbc #$60
        sta $09
.s1:    lda $0B
        and #$20
        beq .fin
        lda $09
        sec
        sbc #$06
        sta $09
.fin:   lda $0B
        and #$50
        sta $0B
        lda $09
        bne .nz
        lda #$80
        tsb $0B
.nz:    lda $0B
        sta $08
        rtl

; ============================================================ NMI: GB vblank + the picture
nmi:    rep #$30
        .a16
        pha
        phx
        phy
        phb
        lda $0A                     ; the translated code's temporaries
        pha
        lda $0C
        pha
        lda $0E
        pha
        sep #$20
        .a8
        lda #$00
        xba                         ; B = 0 (TAX indexes below)
        lda #$00
        pha
        plb
        lda.w $4210
        rep #$20
        .a16
        inc $1C
        sep #$20
        .a8
        lda.l $7FFF40
        bpl .np                     ; LCD off: no GB vblank
        lda.l $7FFF0F
        ora #$01
        sta.l $7FFF0F
        lda $14
        beq .np
        lda.l $7FFFFF
        and.l $7FFF0F
        and #$1F
        sta $15
.np:    lda.l $7FFF40
        bmi .on
        lda #$80
        sta.w $2100
        bra .cont
.on:    lda #$0F
        sta.w $2100
.cont:  lda.l $7FFF40
        and #$08
        beq .m0
        lda #$24
        bra .m1
.m0:    lda #$20
.m1:    sta.w $2107
        lda.l $7FFF40
        and #$10
        beq .sg
        lda #$00
        bra .sg1
.sg:    lda #$01
.sg1:   sta.w $210B
        lda.l $7FFF43
        sec
        sbc #48
        sta.w $210D
        lda #$00
        sbc #$00
        sta.w $210D
        lda.l $7FFF42
        sec
        sbc #40
        sta.w $210E
        lda #$00
        sbc #$00
        sta.w $210E
        lda.l $7FFF47
        cmp $30
        beq .nopal
        sta $30
        sta $0B
        lda #$00
        sta.w $2121
        ldy #$0004
.pal:   lda $0B
        and #$03
        asl a
        tax
        lda.l SHADES,x
        sta.w $2122
        lda.l SHADES+1,x
        sta.w $2122
        lsr $0B
        lsr $0B
        dey
        bne .pal
.nopal: lda.l $7FFF48
        cmp $31
        beq .nop0
        sta $31
        ldx #$0080
        jsr objpal
.nop0:  lda.l $7FFF49
        cmp $32
        beq .nop1
        sta $32
        ldx #$0090
        jsr objpal
.nop1:  ldy #10
        ldx #$0000
.lp:    lda $40,x
        beq .nx
        stz $40,x
        phx
        phy
        jsr upchunk
        ply
        plx
        dey
        beq .end
.nx:    inx
        cpx #$0040
        bne .lp
.end:   jsr sprites
        rep #$30
        .a16
        pla
        sta $0E
        pla
        sta $0C
        pla
        sta $0A
        plb
        ply
        plx
        pla
        rti
        .a8

; A = OBPn, X = CGRAM index: colours 1-3 of an OBJ palette
objpal: sta $0B
        txa
        sta.w $2121
        ldy #$0004
.p:     lda $0B
        and #$03
        asl a
        tax
        lda.l SHADES,x
        sta.w $2122
        lda.l SHADES+1,x
        sta.w $2122
        lsr $0B
        lsr $0B
        dey
        bne .p
        rts

; GB OAM ($7F:FE00, 40 x 8x8) -> SNES OAM: x+40, y+23, flips, OBP0/1 -> palette 0/1, behind-BG -> priority 0
sprites: lda #$00
        sta.w $2102
        sta.w $2103
        ldx #$0000
.s:     lda.l $7FFE00,x
        beq .hide
        cmp #160
        bcs .hide
        clc
        adc #23
        sta $0B
        lda.l $7FFE01,x
        clc
        adc #40
        sta.w $2104
        lda $0B
        sta.w $2104
        lda.l $7FFE02,x
        sta.w $2104
        lda.l $7FFE03,x
        sta $0B
        and #$60
        asl a
        sta $0C
        lda $0B
        and #$10
        lsr a
        lsr a
        lsr a
        ora $0C
        sta $0C
        lda $0B
        bmi .back
        lda #$20
        tsb $0C
.back:  lda $0C
        sta.w $2104
        bra .nx
.hide:  lda #$00
        sta.w $2104
        lda #$F0
        sta.w $2104
        sta.w $2104
        sta.w $2104
.nx:    inx
        inx
        inx
        inx
        cpx #160
        bne .s
        rts
; all 128 SNES sprites hidden, small, x bit 8 clear (once, at boot, in forced blank)
oaminit: lda #$00
        sta.l $002102
        sta.l $002103
        ldx #$0000
.rest:  lda #$F0
        sta.l $002104
        sta.l $002104
        sta.l $002104
        sta.l $002104
        inx
        inx
        inx
        inx
        cpx #512
        bne .rest
        ldx #$0000
.hi:    lda #$00
        sta.l $002104
        inx
        cpx #32
        bne .hi
        rts

; X = chunk: 128 bytes of GB VRAM at $8000 + X*128 -> its one or two places in SNES VRAM (CHUNKS: dest1, dest2, mode)
upchunk: txa
        sta $2F
        rep #$20
        .a16
        txa
        xba
        lsr a
        clc
        adc #$8000
        sta $0E
        txa
        asl a
        asl a
        asl a
        tax
        lda.l CHUNKS,x
        sta $0A
        lda.l CHUNKS+2,x
        sta $2C
        lda.l CHUNKS+4,x
        sep #$20
        .a8
        sta $2E
        jsr dma1
        lda $2C+1
        cmp #$FF
        beq .one
        rep #$20
        .a16
        lda $2C
        sta $0A
        sep #$20
        .a8
        jsr dma1
.one:   lda $2F
        cmp #$20
        bcs .noobj
        ; $8000-$8FFF also feeds the sprites: each 16-byte 2bpp tile is the first half of a 4bpp OBJ tile
        lda #$08
        sta $2D
        rep #$20
        .a16
        lda $2F
        and #$00FF
        asl a
        asl a
        asl a
        asl a
        asl a
        asl a
        asl a
        clc
        adc #$4000
        sta $0A
        sep #$20
        .a8
        lda #$80
        sta $2E
.ot:    jsr dma16
        rep #$20
        .a16
        lda $0A
        clc
        adc #$0010
        sta $0A
        lda $0E
        clc
        adc #$0010
        sta $0E
        sep #$20
        .a8
        dec $2D
        bne .ot
.noobj: rts
dma16:  lda #$80
        sta.w $2115
        rep #$20
        .a16
        lda $0A
        sta.w $2116
        lda $0E
        sta.w $4302
        lda #$0010
        sta.w $4305
        sep #$20
        .a8
        lda #$01
        sta.w $4300
        lda #$18
        sta.w $4301
        lda #$7F
        sta.w $4304
        lda #$01
        sta.w $420B
        rts
dma1:   lda $2E
        sta.w $2115
        rep #$20
        .a16
        lda $0A
        sta.w $2116
        lda $0E
        sta.w $4302
        lda #$0080
        sta.w $4305
        sep #$20
        .a8
        lda $2E
        beq .map
        lda #$01
        sta.w $4300
        bra .go
.map:   lda #$00
        sta.w $4300
.go:    lda #$18
        sta.w $4301
        lda #$7F
        sta.w $4304
        lda #$01
        sta.w $420B
        rts

irqv:   rti
cop:    rti

SHADES: dw $7FFF, $56B5, $294A, $0000
DIRMAP: db $00,$01,$02,$03,$08,$09,$0A,$0B,$04,$05,$06,$07,$0C,$0D,$0E,$0F
ZC_ADD: db $00,$10,$80,$90
ZC_SUB: db $50,$40,$D0,$C0
ZERO:   dw $0000
CHUNKS: ''' + chunks_tab + r'''
LYTAB:  ''' + lytab + '\n'


def chunks_table():
    rows = []
    for c in range(64):
        a = 0x8000 + c * 128
        if a < 0x8800: d1, d2, m = (a - 0x8000) // 2, 0xFFFF, 0x80
        elif a < 0x9000: d1, d2, m = (a - 0x8000) // 2, 0x1000 + 0x400 + (a - 0x8800) // 2, 0x80
        elif a < 0x9800: d1, d2, m = 0x1000 + (a - 0x9000) // 2, 0xFFFF, 0x80
        else: d1, d2, m = 0x2000 + (a - 0x9800), 0xFFFF, 0x00
        rows.append('dw $%04X,$%04X,$%04X,$0000' % (d1, d2, m))
    return '\n        '.join(rows)


def ly_table():
    v = []
    for line in range(512):
        if line < 225: ly = line * 144 // 225
        elif line < 262: ly = 144 + (line - 225) * 10 // 37
        else: ly = 153
        v.append(min(ly, 153))
    return '\n        '.join('db ' + ','.join('$%02X' % x for x in v[i:i + 32]) for i in range(0, 512, 32))


# ------------------------------------------------------------------------------------------------- interpreter

def interp_text(count):
    g = Gen(lambda t, k: None, count)
    parts = [r'''
interp: stx $16
        lda.w $0000,x
        pha
        lda.w $0001,x
        sta $0C
        lda.w $0002,x
        sta $0D
        pla
        rep #$20
        .a16
        and #$00FF
        tax
        lda.l OPLEN,x
        and #$00FF
        clc
        adc $16
        sta $16
        txa
        asl a
        tax
        sep #$20
        .a8
        xba
        lda #$00
        xba
        jmp (OPTAB,x)
''']
    names = []
    for op in range(256):
        name = 'I_%02X' % op
        names.append(name)
        if op == 0xCB:
            body = ['rep #$20', 'lda $0C', 'and #$00FF', 'asl a', 'tax', 'sep #$20'] + BFIX + ['jmp (CBTAB,x)']
        else:
            body = g.insn(op, 0, 0, {'mode': 'interp', 'pc': 0, 'next': 0, 'key': None, 'flags': 15})
            if op in (0x07, 0x0F, 0x17, 0x1F):
                body = rot_a(g, op, 15)
                if count: body = ['inc $18', 'bne .c', 'inc $19', 'bne .c', 'inc $1A', '.c:'] + body
            body = body + ['ldx $16', 'jml dispatch']
        parts.append(name + ':\n        ' + '\n        '.join(body))
    cbn = []
    for b in range(256):
        name = 'C_%02X' % b
        cbn.append(name)
        body = g.cb({'mode': 'interp', 'cb': b, 'flags': 15}) + ['ldx $16', 'jml dispatch']
        parts.append(name + ':\n        ' + '\n        '.join(body))
    parts.append('OPTAB:  dw ' + ','.join('lo(%s)|hi(%s)<<8' % (n, n) for n in names))
    parts.append('CBTAB:  dw ' + ','.join('lo(%s)|hi(%s)<<8' % (n, n) for n in cbn))
    parts.append('OPLEN:  db ' + ','.join(str(sm83.length(o)) for o in range(256)))
    return '\n'.join(parts) + '\n'


# ------------------------------------------------------------------------------------------------- convert

def unit_key_label(bank, pc):
    return 'G_%s_%04X' % ('R' if bank < 0 else '%X' % bank, pc)


def convert(data, pcs, report, count=False):
    cart = sm83.Cart(data)
    if cart.banks > 2:
        report['warning'] = 'PoC back end: only 32KB ROMs (bank 0 + bank 1); MBC1 banks beyond 1 are not translated'
    an = sm83.analyse(cart, pcs)
    report['cart'] = {'title': cart.title, 'mbc': cart.mbc, 'banks': cart.banks}
    report['analysis'] = an.summary()
    report['overlays'] = ['$%04X-$%04X <- file $%05X' % o for o in an.overlays]
    keys = sorted((k for k in an.insn if k[0] in (0, 1, -1)), key=lambda k: (k[0] if k[0] >= 0 else 9, k[1]))
    keyset = set(keys)

    def label_of(t, frm):
        b = frm[0] if frm else 0
        k = an.norm(b, t)
        if k[0] == 1 and (1, t) not in keyset: return None
        return unit_key_label(*k) if k in keyset and t not in an.volatile else None

    g = Gen(label_of, count)
    units = []
    stats = {'flags_skipped': 0, 'flags_kept': 0}
    for k in keys:
        bank, pc = k
        n, m, fl = an.insn[k]
        rd = an.reader_for(bank, pc)
        op, b1, b2 = rd(pc), rd(pc + 1), rd(pc + 2)
        need = flags_needed(an, bank, pc, n)
        r, wmask = flag_rw(op, b1)
        live = (wmask & need) != 0 or op in (0xF1,)
        stats['flags_kept' if live and wmask else 'flags_skipped'] += 1 if wmask else 0
        ctx = {'mode': 'static', 'pc': pc, 'next': (pc + n) & 0xFFFF, 'key': k, 'flags': 15 if live else 0, 'cb': b1}
        if op in (0x07, 0x0F, 0x17, 0x1F):
            body = (['inc $18', 'bne .c', 'inc $19', 'bne .c', 'inc $1A', '.c:'] if count else []) + rot_a(g, op, ctx['flags'])
        else:
            body = g.insn(op, b1, b2, ctx)
        falls = fl[0] and fl[2] not in ('halt',)
        if op == 0xCB: falls = True
        units.append((k, body, falls, (pc + n) & 0xFFFF))
    report['flags'] = stats
    # sizes (labels -> dummies) and packing into the code banks
    rt_syms = runtime_symbols_dummy()
    dummy = {unit_key_label(*k): 0xC28000 for k in keys}
    dummy.update(rt_syms)
    sizes = []
    for k, body, falls, nx in units:
        b, _ = assemble(unit_key_label(*k) + ':\n' + '\n'.join(body) + ('\njml dispatch' if falls else ''), 0xC20000, dummy)
        sizes.append(len(b))
    placed, bi, cur = [], 0, 0
    for i, (k, body, falls, nx) in enumerate(units):
        if cur + sizes[i] > 0xFFF0:
            bi += 1; cur = 0
            if bi >= len(CODE_BANKS): raise SystemExit('gb2snes: translated code does not fit')
        placed.append(bi)
        cur += sizes[i]
    texts = [[] for _ in CODE_BANKS]
    for i, (k, body, falls, nx) in enumerate(units):
        lines = [unit_key_label(*k) + ':'] + body
        if falls:
            nk = an.norm(k[0], nx) if k[0] >= 0 else (-1, nx)
            follows = i + 1 < len(units) and units[i + 1][0] == nk and placed[i + 1] == placed[i]
            if not follows:
                lab = label_of(nx, k)
                lines += ['jml ' + lab] if lab else ['ldx #$%04X' % nx, 'jml dispatch']
        texts[placed[i]].append('\n'.join(lines))
    # runtime + interpreter first (their symbols are needed by the code), then the code banks (twice: forward labels)
    rom = bytearray(0x200000)
    rt_text = runtime_text(chunks_table(), ly_table())
    syms = dict(dummy)
    for rnd in range(2):
        rt, rtl = assemble(rt_text, 0xC08000, syms)
        syms.update(rtl)
        it, itl = assemble(interp_text(count), 0xC10000, syms)
        syms.update(itl)
        labels = {}
        code = []
        for bi2, t in enumerate(texts):
            if not t: code.append(b''); continue
            b, l = assemble('\n'.join(t), CODE_BANKS[bi2] << 16, syms)
            if len(b) > 0x10000: raise SystemExit('bank overflow')
            labels.update(l); code.append(b)
        syms.update(labels)
    rom[0x8000:0x8000 + len(rt)] = rt
    if len(rt) > 0x7FB0 - 0x0: pass
    rom[0x10000:0x10000 + len(it)] = it
    for bi2, b in enumerate(code):
        o = (CODE_BANKS[bi2] - 0xC0) << 16
        rom[o:o + len(b)] = b
    g_off = (GBROM_BANK - 0xC0) << 16
    rom[g_off:g_off + min(len(data), 0x8000)] = data[:0x8000]
    # dispatch tables
    for k in keys:
        bank, pc = k
        if pc in an.volatile and bank < 0: continue
        region = 0 if pc < 0x4000 else 1 if pc < 0x8000 else 3 if pc >= 0xC000 else None
        if region is None: continue
        a = syms[unit_key_label(*k)]
        o = ((TAB_BANK[region] - 0xC0) << 16) + (pc & 0x3FFF) * 4
        rom[o:o + 4] = bytes([a & 0xFF, a >> 8 & 0xFF, a >> 16 & 0xFF, 0])
    # header ($00:FFC0 = file $FFC0) and native / emulation vectors
    title = ('NESTED GB ' + cart.title)[:21].upper().ljust(21).encode('ascii', 'replace')
    rom[0xFFC0:0xFFD5] = title
    rom[0xFFD5] = 0x31          # HiROM + FastROM
    rom[0xFFD6] = 0x00
    rom[0xFFD7] = 0x0B          # 2MB
    rom[0xFFD8] = 0x00
    rom[0xFFD9] = 0x01
    rom[0xFFDA] = 0x00
    rom[0xFFDB] = 0x00
    def vec(o, lab): v = syms[lab] & 0xFFFF; rom[o] = v & 0xFF; rom[o + 1] = v >> 8
    for o in (0xFFE4, 0xFFE6, 0xFFE8, 0xFFEE): vec(o, 'irqv')
    vec(0xFFEA, 'nmi')
    for o in (0xFFF4, 0xFFF8, 0xFFFA, 0xFFFE): vec(o, 'irqv')
    vec(0xFFFC, 'reset')
    rom[0xFFDC:0xFFE0] = b'\xff\xff\x00\x00'
    s = sum(rom) & 0xFFFF
    rom[0xFFDE] = s & 0xFF; rom[0xFFDF] = s >> 8
    rom[0xFFDC] = (s ^ 0xFFFF) & 0xFF; rom[0xFFDD] = (s ^ 0xFFFF) >> 8
    if (syms['nmi'] & 0xFFFF) < 0x8000 or (syms['reset'] & 0xFFFF) < 0x8000 or len(rt) > 0x7FC0 - 0x200:
        raise SystemExit('gb2snes: runtime must sit in $C0:8000-$FFBF')
    report['units'] = len(units)
    report['code_bytes'] = sum(len(b) for b in code)
    report['code_banks'] = bi + 1
    report['runtime_bytes'] = len(rt)
    report['interp_bytes'] = len(it)
    report['bytes_per_gb_instruction'] = round(report['code_bytes'] / max(1, len(units)), 1)
    report['_labels'] = {k: v for k, v in syms.items() if k.startswith('G_') or k in ('dispatch', 'interp', 'rdx', 'wrx', 'nmi', 'irqtake', 'halt_rt', 'done_rt', 'gbdaa', 'reset')}
    report['symbols'] = {k: '$%06X' % v for k, v in syms.items() if k in ('reset', 'nmi', 'dispatch', 'interp', 'done_rt', 'irqtake')}
    return bytes(rom)


def runtime_symbols_dummy():
    names = ['dispatch', 'interp', 'rdx', 'wrx', 'updpend', 'irqtake', 'halt_rt', 'done_rt', 'gbdaa', 'ZC_ADD', 'ZC_SUB', 'ZC_ADD2']
    return {n: 0xC08000 for n in names}


# ------------------------------------------------------------------------------------------------- harness hook

def test_case(exe, rom, work, frames, gb_reference, run):
    import re, subprocess
    ref = gb_reference(exe, rom, work, frames)
    res = dict(ref)
    for cnt in (False, True):
        out_rom = os.path.join(work, ref['tag'] + ('_count' if cnt else '') + '.sfc')
        for f in (out_rom, out_rom + '.json'):
            if os.path.exists(f): os.remove(f)
        args = [sys.executable, os.path.join(HERE, 'gb2snes.py'), 'convert', rom, out_rom, '--pcs', ref['pcs'], '--report', out_rom + '.json']
        if cnt: args.append('--count')
        conv = run(args)
        if not os.path.exists(out_rom): raise SystemExit('gb2snes failed: ' + conv[-2000:])
        o = run([exe, '--snesrun', '--rom', out_rom, '--frames', str(frames * 12), '--dump-wram', '0:%d' % 0x6000,
                 '--png-at', str(frames * 12), '--out-dir', work])
        w = bytearray(0x6000)
        rows = 0
        for line in o.splitlines():
            if line.startswith('wram '):
                a, hx = line[5:].split(':', 1)
                bs = bytes.fromhex(hx.strip())
                w[int(a, 16):int(a, 16) + len(bs)] = bs; rows += 1
        if not rows:
            res['snesrun' + ('_count' if cnt else '')] = o[-1500:]
            continue
        if cnt:
            res['snes_counted_instructions'] = w[0x18] | w[0x19] << 8 | w[0x1A] << 16
            continue
        res['convert'] = {k: v for k, v in json.load(open(out_rom + '.json')).items() if k in ('units', 'code_bytes', 'bytes_per_gb_instruction', 'flags', 'code_banks')}
        n = w[0x3FFE] | w[0x3FFF] << 8
        res['target_serial'] = w[0x4000:0x4000 + n].decode('latin-1').replace('\n', ' | ').strip()
        if w[0x20] == 1:
            frame = w[0x21] | w[0x22] << 8
            v = w[0x23] | (w[0x24] & 1) << 8
            master = frame * 357368 + v * 1364
            res['target_frame'] = frame
            res['target_master_cycles'] = master
            res['target_cycles'] = master // 6          # in 3.58 MHz (FastROM) CPU cycles
            res['cycles_per_gb_instruction'] = round(res['target_cycles'] / ref['gb_instructions'], 2)
            res['frame_ratio'] = round(frame / max(1, ref['gb_frame']), 2)
        else:
            res['target_frame'] = None
    res['passed'] = 'Passed' in res.get('target_serial', '') and 'Passed' in ref['serial']
    return res


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest='cmd', required=True)
    for name in ('convert', 'feedback'):
        p = sub.add_parser(name); p.add_argument('inp'); p.add_argument('out')
        p.add_argument('--pcs'); p.add_argument('--report'); p.add_argument('--frames', type=int, default=1800)
        p.add_argument('--input', default=''); p.add_argument('--workshop'); p.add_argument('--profile')
        p.add_argument('--set', action='append', default=[]); p.add_argument('--count', action='store_true')
    a = ap.parse_args()
    data = open(a.inp, 'rb').read()
    report = {'pipeline': 'gb2snes', 'in': a.inp, 'out': a.out}
    pcs = []
    if a.cmd == 'feedback':
        pcs_path = os.path.splitext(a.out)[0] + '.pcs'
        pcs, out = sm83.trace(a.inp, pcs_path, a.frames, a.input, a.workshop)
        report['feedback'] = {'pcs': len(pcs), 'run': out.strip().splitlines()[:3]}
    elif a.pcs:
        pcs = sm83.read_pcs(a.pcs)
    rom = convert(data, pcs, report, a.count)
    open(a.out, 'wb').write(rom)
    with open(os.path.splitext(a.out)[0] + '.sym', 'w') as f:      # WLA-DX symbols: the GB address of each block
        f.write('[labels]' + chr(10))
        for k, v in sorted(report.pop('_labels').items(), key=lambda kv: kv[1]):
            f.write('%02X:%04X %s' % (v >> 16, v & 0xFFFF, k) + chr(10))
    if a.report:
        json.dump(report, open(a.report, 'w'), indent=1)
    print(json.dumps({k: report[k] for k in report if k not in ('symbols',)}, indent=1))


if __name__ == '__main__':
    main()
