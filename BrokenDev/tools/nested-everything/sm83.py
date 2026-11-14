"""Shared Game Boy (SM83) front end for the Nested Everything GB pipelines (gb2snes, gb2gg).

  - decoder: length, mnemonic, control flow of every SM83 opcode (CB page included)
  - bank map: ROM bank layout for no-MBC / MBC1 / MBC3 / MBC5 carts (16KB banks; bank 0 at $0000, bank n at $4000)
  - analysis: recursive control-flow walk from the vectors ($0100 entry, RST $00-$38, interrupts $40-$60),
    plus entry points fed back from headless runs on BrokenNes's GB core (Workshop --gbrun --pcs <file>)
  - trace(): run that headless pass and read the executed-PC set back ("bank:pc" per line)

Addresses are (bank, cpu address) pairs: bank 0 for $0000-$3FFF, the switchable bank for $4000-$7FFF, -1 for RAM.
"""
import os, subprocess, json

R8 = ['b', 'c', 'd', 'e', 'h', 'l', '(hl)', 'a']
R16 = ['bc', 'de', 'hl', 'sp']
R16S = ['bc', 'de', 'hl', 'af']
CC = ['nz', 'z', 'nc', 'c']
ALU = ['add', 'adc', 'sub', 'sbc', 'and', 'xor', 'or', 'cp']
CBOPS = ['rlc', 'rrc', 'rl', 'rr', 'sla', 'sra', 'swap', 'srl']

LEN2 = {0x06, 0x0E, 0x16, 0x1E, 0x26, 0x2E, 0x36, 0x3E, 0x10, 0x18, 0x20, 0x28, 0x30, 0x38,
        0xC6, 0xCE, 0xD6, 0xDE, 0xE6, 0xEE, 0xF6, 0xFE, 0xE0, 0xF0, 0xE8, 0xF8, 0xCB}
LEN3 = {0x01, 0x11, 0x21, 0x31, 0x08, 0xC2, 0xC3, 0xCA, 0xD2, 0xDA, 0xC4, 0xCC, 0xCD, 0xD4, 0xDC, 0xEA, 0xFA}
ILLEGAL = {0xD3, 0xDB, 0xDD, 0xE3, 0xE4, 0xEB, 0xEC, 0xED, 0xF4, 0xFC, 0xFD}

# The opcodes whose bytes mean something else (or something with a different length) on a Z80: what gb2gg must rewrite.
Z80_DIFFERENT = {0x08, 0x10, 0x22, 0x2A, 0x32, 0x3A, 0xD9, 0xE0, 0xE2, 0xE8, 0xEA, 0xF0, 0xF2, 0xF8, 0xFA} | ILLEGAL


def length(op):
    return 3 if op in LEN3 else 2 if op in LEN2 else 1


def s8(v):
    return v - 256 if v >= 128 else v


def decode(mem, pc):
    """(length, mnemonic, flow) of the instruction at mem[pc] (mem: a bytes-like indexed by CPU address or a callable).
    flow = (falls_through, [targets], kind) with kind in jr/jp/call/ret/reti/rst/ind/halt/stop/None."""
    rd = mem if callable(mem) else (lambda a: mem[a & 0xFFFF] if (a & 0xFFFF) < len(mem) else 0xFF)
    op = rd(pc)
    n = length(op)
    b1 = rd(pc + 1) if n > 1 else 0
    w = b1 | (rd(pc + 2) << 8) if n > 2 else 0
    x, y, z = op >> 6, (op >> 3) & 7, op & 7
    flow = (True, [], None)
    if op in ILLEGAL:
        return 1, 'db $%02X' % op, (False, [], 'illegal')
    if op == 0xCB:
        x2, y2, z2 = b1 >> 6, (b1 >> 3) & 7, b1 & 7
        m = ('%s %s' % (CBOPS[y2], R8[z2])) if x2 == 0 else '%s %d,%s' % (['', 'bit', 'res', 'set'][x2], y2, R8[z2])
        return 2, m, flow
    if x == 1:
        if op == 0x76:
            return 1, 'halt', (True, [], 'halt')
        return 1, 'ld %s,%s' % (R8[y], R8[z]), flow
    if x == 2:
        return 1, '%s %s' % (ALU[y], R8[z]), flow
    if x == 0:
        if z == 0:
            if op == 0x00: return 1, 'nop', flow
            if op == 0x08: return 3, 'ld ($%04X),sp' % w, flow
            if op == 0x10: return 2, 'stop', (True, [], 'stop')
            t = (pc + 2 + s8(b1)) & 0xFFFF
            if op == 0x18: return 2, 'jr $%04X' % t, (False, [t], 'jr')
            return 2, 'jr %s,$%04X' % (CC[y - 4], t), (True, [t], 'jr')
        if z == 1:
            if y & 1: return 1, 'add hl,%s' % R16[y >> 1], flow
            return 3, 'ld %s,$%04X' % (R16[y >> 1], w), flow
        if z == 2:
            return 1, ['ld (bc),a', 'ld a,(bc)', 'ld (de),a', 'ld a,(de)', 'ld (hl+),a', 'ld a,(hl+)', 'ld (hl-),a', 'ld a,(hl-)'][y], flow
        if z == 3:
            return 1, ('dec ' if y & 1 else 'inc ') + R16[y >> 1], flow
        if z == 4: return 1, 'inc ' + R8[y], flow
        if z == 5: return 1, 'dec ' + R8[y], flow
        if z == 6: return 2, 'ld %s,$%02X' % (R8[y], b1), flow
        return 1, ['rlca', 'rrca', 'rla', 'rra', 'daa', 'cpl', 'scf', 'ccf'][y], flow
    # x == 3
    if z == 0:
        if y < 4: return 1, 'ret ' + CC[y], (True, [], 'ret')
        return 2, ['ldh ($FF%02X),a' % b1, 'add sp,%d' % s8(b1), 'ldh a,($FF%02X)' % b1, 'ld hl,sp%+d' % s8(b1)][y - 4], flow
    if z == 1:
        if y & 1 == 0: return 1, 'pop ' + R16S[y >> 1], flow
        if op == 0xC9: return 1, 'ret', (False, [], 'ret')
        if op == 0xD9: return 1, 'reti', (False, [], 'reti')
        if op == 0xE9: return 1, 'jp (hl)', (False, [], 'ind')
        return 1, 'ld sp,hl', flow
    if z == 2:
        if y < 4: return 3, 'jp %s,$%04X' % (CC[y], w), (True, [w], 'jp')
        return {4: 1, 5: 3, 6: 1, 7: 3}[y], ['ld ($FF00+c),a', 'ld ($%04X),a' % w, 'ld a,($FF00+c)', 'ld a,($%04X)' % w][y - 4], flow
    if z == 3:
        if op == 0xC3: return 3, 'jp $%04X' % w, (False, [w], 'jp')
        if op == 0xF3: return 1, 'di', flow
        if op == 0xFB: return 1, 'ei', flow
    if z == 4: return 3, 'call %s,$%04X' % (CC[y], w), (True, [w], 'call')
    if z == 5:
        if y & 1 == 0: return 1, 'push ' + R16S[y >> 1], flow
        return 3, 'call $%04X' % w, (True, [w], 'call')
    if z == 6: return 2, '%s $%02X' % (ALU[y], b1), flow
    return 1, 'rst $%02X' % (y * 8), (True, [y * 8], 'rst')


# ------------------------------------------------------------------------------------------------- cartridge

MBC = {0x00: 'none', 0x01: 'mbc1', 0x02: 'mbc1', 0x03: 'mbc1', 0x0F: 'mbc3', 0x10: 'mbc3', 0x11: 'mbc3', 0x12: 'mbc3',
       0x13: 'mbc3', 0x19: 'mbc5', 0x1A: 'mbc5', 0x1B: 'mbc5', 0x1C: 'mbc5', 0x1D: 'mbc5', 0x1E: 'mbc5'}


class Cart:
    def __init__(self, data):
        self.data = bytes(data)
        self.title = self.data[0x134:0x144].split(b'\0')[0].decode('latin-1', 'replace').strip()
        self.type = self.data[0x147]
        self.mbc = MBC.get(self.type, 'unknown')
        self.banks = max(2, len(self.data) // 0x4000)
        self.ram_kb = {0: 0, 1: 2, 2: 8, 3: 32, 4: 128, 5: 64}.get(self.data[0x149], 0)
        self.cgb = self.data[0x143] in (0x80, 0xC0)

    def bank(self, n):
        return self.data[n * 0x4000:(n + 1) * 0x4000]

    def off(self, bank, addr):
        """File offset of CPU address addr with ROM bank `bank` in the switchable slot."""
        return addr if addr < 0x4000 else bank * 0x4000 + (addr - 0x4000)

    def reader(self, bank):
        d = self.data
        def rd(a):
            a &= 0xFFFF
            if a >= 0x8000: return 0xFF
            o = self.off(bank, a)
            return d[o] if o < len(d) else 0xFF
        return rd

    def mbc_bank_write(self, addr, value):
        """The ROM bank an MBC write selects, or None if the write is not a ROM bank select."""
        if self.mbc == 'mbc1' and 0x2000 <= addr < 0x4000:
            v = value & 0x1F
            return v or 1
        if self.mbc == 'mbc3' and 0x2000 <= addr < 0x4000:
            return (value & 0x7F) or 1
        if self.mbc == 'mbc5' and 0x2000 <= addr < 0x3000:
            return value
        return None


# ------------------------------------------------------------------------------------------------- analysis

VECTORS = [0x0100, 0x0040, 0x0048, 0x0050, 0x0058, 0x0060]


class Analysis:
    """Instructions reached from the vectors and the fed-back entry points, per (bank, address)."""

    def __init__(self, cart, entries=(), overlays=(), executed=None):
        self.cart = cart
        self.overlays = list(overlays)   # (ram_lo, ram_hi, file_off): RAM code copied from ROM (static image)
        self.volatile = set()            # RAM PCs whose bytes changed between executions: interpreter / native only
        self.executed = executed         # (bank, addr) set from the feedback, when there is one
        self.insn = {}          # (bank, addr) -> (len, mnemonic, flow)
        self.labels = set()     # (bank, addr) that something jumps / calls / returns to
        self.indirect = []      # (bank, addr) of jp (hl) / ret sites: their targets come from the feedback
        self.ram_code = set()   # RAM addresses executed (from the feedback): code the static pass never sees
        self.bank_writes = []   # (bank, addr, value or None) of writes that can switch the ROM bank
        work = [(0, v) for v in VECTORS]
        for e in entries:
            work.append(e)
        for b, a in work:
            self.labels.add((b, a))
        self.walk(work)

    def norm(self, bank, a):
        return (0, a) if a < 0x4000 else (bank, a) if a < 0x8000 else (-1, a)

    def walk(self, work):
        work = list(work)
        while work:
            bank, pc = work.pop()
            bank, pc = self.norm(bank, pc)
            if bank < 0:
                self.ram_code.add(pc)
                if pc in self.volatile or not self.overlay_of(pc):
                    continue
            rd = self.cart.reader(max(bank, 1)) if bank >= 0 else self.overlay_reader(pc)
            lim = 0x8000 if bank >= 0 else self.overlay_of(pc)[1]
            while (bank, pc) not in self.insn and pc < lim and not (bank < 0 and pc in self.volatile):
                n, m, fl = decode(rd, pc)
                self.insn[(bank, pc)] = (n, m, fl)
                op = rd(pc)
                # constant-operand ROM bank switch: ld a,n / ld ($2000),a, or ld ($2xxx),a with an unknown A
                if op == 0xEA:
                    w = rd(pc + 1) | rd(pc + 2) << 8
                    if w < 0x8000:
                        self.bank_writes.append((bank, pc, None))
                ft, targets, kind = fl
                for t in targets:
                    tb, ta = self.norm(bank, t)
                    self.labels.add((tb, ta))
                    if (tb, ta) not in self.insn:
                        work.append((tb, ta))
                if kind in ('ind', 'ret', 'reti'):
                    self.indirect.append((bank, pc))
                if kind == 'call' or kind == 'rst':
                    # a call that ran but never came back to the next byte (print_str "inline string", no-return
                    # helpers): the bytes after it are data, not code
                    if self.executed is not None and (bank, pc) in self.executed and (bank, pc + n) not in self.executed:
                        break
                    self.labels.add((bank, pc + n))        # where the callee returns
                if not ft:
                    break
                pc += n

    def overlay_of(self, a):
        for o in self.overlays:
            if o[0] <= a < o[1]:
                return o
        return None

    def overlay_reader(self, a):
        lo, hi, off = self.overlay_of(a)
        d = self.cart.data
        return lambda x: d[off + (x & 0xFFFF) - lo] if lo <= (x & 0xFFFF) < hi else 0xFF

    def reader_for(self, bank, a):
        return self.cart.reader(max(bank, 1)) if bank >= 0 else self.overlay_reader(a)

    def file_off(self, bank, a):
        if bank >= 0:
            return self.cart.off(bank, a)
        lo, hi, off = self.overlay_of(a)
        return off + a - lo

    def add_entries(self, entries):
        # an executed PC the static walk already reached is not a label (it is reached by falling through or by a
        # jump the walk saw); one it did not reach came through a computed jump: a label
        new = [self.norm(*e) for e in entries if self.norm(*e) not in self.insn]
        for e in new:
            self.labels.add(e)
        self.walk(new)
        return len(new)

    def ordered(self, bank):
        return sorted(a for (b, a) in self.insn if b == bank)

    def summary(self):
        banks = sorted({b for b, _ in self.insn})
        return {'instructions': len(self.insn), 'labels': len(self.labels), 'indirect_sites': len(self.indirect),
                'ram_code': len(self.ram_code), 'banks': banks, 'bank_writes': len(self.bank_writes),
                'z80_different_sites': sum(1 for (b, a), (n, m, f) in self.insn.items()
                                           if self.cart.reader(max(b, 1))(a) in Z80_DIFFERENT)}


# ------------------------------------------------------------------------------------------------- feedback

HERE = os.path.dirname(os.path.abspath(__file__))


def workshop_exe(arg=None):
    for c in (arg, os.environ.get('BROKENDEV_WORKSHOP'),
              os.path.join(HERE, '..', '..', '..', 'Workshop', 'bin', 'Release', 'net10.0-windows', 'BrokenNes.Workshop.exe')):
        if c and os.path.exists(c):
            return os.path.abspath(c)
    raise SystemExit('BrokenNes.Workshop.exe (with --gbrun --pcs) not found: build Workshop, or set BROKENDEV_WORKSHOP / --workshop')


def trace(rom, out_txt, frames=600, inputs='', exe=None, stop_pc=None):
    """Run the GB original headless on BrokenNes's GB core and read the executed PCs: [(bank, addr)], plus the run's
    summary line (instructions, serial output)."""
    args = [workshop_exe(exe), '--gbrun', '--rom', rom, '--frames', str(frames), '--pcs', out_txt]
    if inputs:
        args += ['--input', inputs]
    if stop_pc is not None:
        args += ['--stop-pc', '%04X' % stop_pc]
    r = subprocess.run(args, capture_output=True, text=True, timeout=600)
    return (read_pcs(out_txt) if os.path.exists(out_txt) else []), r.stdout


def analyse(cart, pcs=()):
    """The static walk plus the feedback: overlays for RAM code copied from ROM, the executed PCs as entry points."""
    an = Analysis(cart, overlays=find_overlays(cart, pcs) if pcs else (), executed=pcs_set(pcs) if pcs else None)
    if pcs:
        an.add_entries(entries_from_trace(an, pcs))
    return an


def read_pcs(path):
    """A --pcs file: [(bank, addr, ram_bytes or None, volatile)]."""
    out = []
    for line in open(path):
        f = line.strip().split(':')
        if len(f) < 2: continue
        out.append((int(f[0], 16), int(f[1], 16), bytes.fromhex(f[2]) if len(f) > 2 else None, len(f) > 3 and f[3] == 'v'))
    return out


def pcs_set(pcs):
    return {((0 if a < 0x4000 else b) if a < 0x8000 else -1, a) for b, a, by, v in pcs}


def find_overlays(cart, pcs):
    """RAM code the game copied from ROM: group the executed RAM PCs into runs and find each run's source in the ROM
    by its bytes (the delta that explains the most PCs). Returns [(ram_lo, ram_hi, file_off)]."""
    ram = sorted((a, b) for bk, a, b, v in pcs if bk == 0xFF and b and not v and (a < 0xFE00 or 0xFF80 <= a < 0xFFFF))
    runs, cur = [], []
    for a, b in ram:
        if cur and a - cur[-1][0] > 0x100:
            runs.append(cur); cur = []
        cur.append((a, b))
    if cur: runs.append(cur)
    d = cart.data
    out = []
    for run in runs:
        votes = {}
        for a, b in run[:64]:
            key = b[:3]
            i = d.find(key)
            while i >= 0:
                votes[i - a] = votes.get(i - a, 0) + 1
                i = d.find(key, i + 1)
        if not votes: continue
        delta, n = max(votes.items(), key=lambda kv: kv[1])
        if n * 2 < min(len(run), 64): continue
        lo, hi = run[0][0], run[-1][0] + 3
        # widen to the whole copied block: as far as the ROM bytes keep matching what ran
        lo &= ~0xFF; hi = (hi + 0xFF) & ~0xFF
        hram = run[0][0] >= 0xFF80
        lo = max(lo, -delta, 0xFF80 if hram else 0xC000 if lo >= 0xC000 else 0x8000)
        out.append((lo, min(hi, 0xFFFF if hram else 0xFE00), lo + delta))
    # runs copied by the same loop (same delta) become one overlay, gaps included
    out.sort()
    merged = []
    for o in out:
        if merged and merged[-1][2] - merged[-1][0] == o[2] - o[0] and o[0] - merged[-1][1] <= 0x1000:
            merged[-1] = (merged[-1][0], o[1], merged[-1][2])
        else:
            merged.append(o)
    return merged


def entries_from_trace(an, pcs):
    """Executed PCs the static walk did not reach: the new entry points (ROM and RAM overlays); volatile RAM is noted."""
    ent = []
    for b, a, by, vol in pcs:
        if b == 0xFF:
            if vol: an.volatile.add(a)
            ent.append((-1, a))
        elif an.norm(b, a) not in an.insn:
            ent.append(an.norm(b, a))
    return ent


def disasm(cart, bank, lo, hi):
    rd = cart.reader(max(bank, 1))
    pc, out = lo, []
    while pc < hi:
        n, m, _ = decode(rd, pc)
        out.append('%02X:%04X  %-9s %s' % (bank, pc, ' '.join('%02X' % rd(pc + i) for i in range(n)), m))
        pc += n
    return out


if __name__ == '__main__':
    import sys
    cart = Cart(open(sys.argv[1], 'rb').read())
    pcs = read_pcs(sys.argv[4]) if len(sys.argv) > 4 else []
    an = analyse(cart, pcs)
    print(cart.title, cart.mbc, cart.banks, 'banks', an.overlays, json.dumps(an.summary()))
    from collections import Counter
    cnt = Counter()
    for (b, a), (n, m, f) in an.insn.items():
        op = an.reader_for(b, a)(a)
        if op in Z80_DIFFERENT or op == 0xCB and 0x30 <= an.reader_for(b, a)(a + 1) <= 0x37 or op in (0xF5, 0xF1, 0x76, 0x27):
            cnt[m.split()[0] + ' %02X' % op] += 1
    print(dict(cnt))
    if len(sys.argv) > 2:
        lo, hi = int(sys.argv[2], 16), int(sys.argv[3], 16)
        print('\n'.join(disasm(cart, 1 if lo >= 0x4000 else 0, lo, hi)))
