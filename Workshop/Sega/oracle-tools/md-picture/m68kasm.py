"""A very small 68000 assembler for probe ROMs (the subset these probes need), plus the ROM header and a
palette readout: results are stored as 9-bit colours in CRAM and drawn as vertical bars, so a screenshot of
any emulator shows them and decode_bars() reads them back."""
import struct

class Asm:
    def __init__(self, origin=0x200):
        self.origin = origin
        self.items = []          # (kind, ...)
        self.labels = {}
        self.pos = origin

    # ---- raw
    def _w(self, *words):
        for w in words:
            self.items.append(('w', w & 0xFFFF)); self.pos += 2
    def label(self, name):
        self.labels[name] = self.pos
    def _long(self, v):
        self._w(v >> 16, v & 0xFFFF)

    # ---- instructions (just what the probes use)
    def move_w_imm_abs(self, imm, addr): self._w(0x33FC, imm); self._long(addr)
    def move_l_imm_abs(self, imm, addr): self._w(0x23FC); self._long(imm); self._long(addr)
    def move_w_abs_dn(self, addr, n): self._w(0x3039 | n << 9); self._long(addr)
    def move_b_abs_dn(self, addr, n): self._w(0x1039 | n << 9); self._long(addr)
    def move_w_dn_abs(self, n, addr): self._w(0x33C0 | n); self._long(addr)
    def move_b_dn_abs(self, n, addr): self._w(0x13C0 | n); self._long(addr)
    def move_w_dn_dm(self, n, m): self._w(0x3000 | m << 9 | n)
    def moveq(self, imm, n): self._w(0x7000 | n << 9 | imm & 0xFF)
    def btst_imm_dn(self, bit, n): self._w(0x0800 | n, bit)
    def andi_w(self, imm, n): self._w(0x0240 | n, imm)
    def ori_w(self, imm, n): self._w(0x0040 | n, imm)
    def cmpi_w(self, imm, n): self._w(0x0C40 | n, imm)
    def addq_w(self, k, n): self._w(0x5040 | (k & 7) << 9 | n)
    def subq_w(self, k, n): self._w(0x5140 | (k & 7) << 9 | n)
    def add_w(self, s, d): self._w(0xD040 | d << 9 | s)          # add.w Ds,Dd
    def sub_w(self, s, d): self._w(0x9040 | d << 9 | s)
    def or_w(self, s, d): self._w(0x8040 | d << 9 | s)
    def cmp_w(self, s, d): self._w(0xB040 | d << 9 | s)           # cmp.w Ds,Dd
    def lsl_w(self, k, n): self._w(0xE148 | (k & 7) << 9 | n)
    def lsr_w(self, k, n): self._w(0xE048 | (k & 7) << 9 | n)
    def lea_abs(self, addr, a): self._w(0x41F9 | a << 9); self._long(addr)
    def move_w_postinc_a_to_a(self, src, dst): self._w(0x30D8 | dst << 9 | src)   # move.w (As)+,(Ad)  [dst plain indirect]
    def move_w_dn_ind(self, n, a): self._w(0x3080 | a << 9 | n)                  # move.w Dn,(An)
    def move_l_dn_ind(self, n, a): self._w(0x2080 | a << 9 | n)
    def nop(self): self._w(0x4E71)
    def stop_forever(self): self.label('_spin'); self.bra('_spin')

    # branches: kind -> (opcode high byte)
    def _branch(self, op, target): self.items.append(('b', op, target, self.pos)); self.pos += 2
    def bra(self, t): self._branch(0x60, t)
    def bne(self, t): self._branch(0x66, t)
    def beq(self, t): self._branch(0x67, t)
    def bcs(self, t): self._branch(0x65, t)
    def bcc(self, t): self._branch(0x64, t)
    def bhi(self, t): self._branch(0x62, t)
    def dbf(self, n, t): self.items.append(('d', 0x51C8 | n, t, self.pos)); self.pos += 4

    def assemble(self):
        out = bytearray()
        for it in self.items:
            if it[0] == 'w': out += struct.pack('>H', it[1])
            elif it[0] == 'b':
                _, op, t, at = it
                disp = self.labels[t] - (at + 2)
                assert -128 <= disp <= 127 and disp != 0, (t, disp)
                out += struct.pack('>H', op << 8 | disp & 0xFF)
            elif it[0] == 'd':
                _, opw, t, at = it
                disp = self.labels[t] - (at + 2)
                out += struct.pack('>HH', opw, disp & 0xFFFF)
        return bytes(out)

def make_rom(code_bytes, title='PROBE', size=0x20000, region='JUE'):
    rom = bytearray(b'\xFF' * size)
    def L(at, v): rom[at:at+4] = struct.pack('>I', v)
    L(0, 0xFFFE00); L(4, 0x200)
    for v in range(2, 64): L(v * 4, 0x300)
    rom[0x100:0x110] = b'SEGA MEGA DRIVE '
    rom[0x110:0x120] = b'(C)PROBE 2026.OCT'[:16]
    rom[0x120:0x150] = title.ljust(48).encode()
    rom[0x150:0x180] = title.ljust(48).encode()
    rom[0x180:0x18E] = b'GM 00000000-00'
    rom[0x1A0:0x1B0] = struct.pack('>IIII', 0, size - 1, 0xFF0000, 0xFFFFFF)
    rom[0x1F0:0x200] = region.ljust(16).encode()
    rom[0x300:0x302] = b'\x60\xFE'
    rom[0x200:0x200+len(code_bytes)] = code_bytes
    return bytes(rom)

VDP_DATA, VDP_CTRL, VDP_HV = 0xC00000, 0xC00004, 0xC00008


RAM = 0xFF0000

def vdp_setup(a: Asm, h40=True):
    regs = [(0, 0x04), (1, 0x44), (2, 0x30), (3, 0x2C), (4, 0x07), (5, 0x54), (7, 0x00), (10, 0xFF), (11, 0x00), (12, 0x81 if h40 else 0x00), (13, 0x2B), (15, 0x02), (16, 0x01), (17, 0), (18, 0)]
    for r, v in regs: a.move_w_imm_abs(0x8000 | r << 8 | v, VDP_CTRL)
    a.move_l_imm_abs(0x40000000, VDP_CTRL)                      # VRAM 0: tile 0 blank
    for _ in range(8): a.move_l_imm_abs(0, VDP_DATA)
    for n in range(1, 16):                                      # tiles 1..15: solid colour n
        for _ in range(8): a.move_l_imm_abs(n * 0x11111111, VDP_DATA)
    # name table A at $C000: 28 rows of 64 cells; bar n = cells (2n-2, 2n-1)
    for row in range(28):
        addr = 0xC000 + row * 128
        a.move_l_imm_abs(0x40000000 | (addr & 0x3FFF) << 16 | addr >> 14, VDP_CTRL)
        for col in range(40):
            n = col // 2 + 1
            a.move_w_imm_abs(n if n <= 15 else 0, VDP_DATA)

def emit_results(a: Asm, count=15):
    """copy RAM words 0..count-1 (each 0..255) into CRAM entries 1..count as 9-bit colours"""
    a.move_l_imm_abs(0xC0020000, VDP_CTRL)                      # CRAM write at entry 1
    for i in range(count):
        a.move_w_abs_dn(RAM + 2 * i, 0)
        a.move_w_dn_dm(0, 1); a.andi_w(7, 1); a.add_w(1, 1)
        a.move_w_dn_dm(0, 2); a.andi_w(0x38, 2); a.lsl_w(2, 2); a.or_w(2, 1)
        a.move_w_dn_dm(0, 2); a.andi_w(0xC0, 2); a.lsl_w(3, 2); a.or_w(2, 1)
        a.move_w_dn_abs(1, VDP_DATA)

L8 = [0, 49, 87, 119, 146, 174, 206, 255]
def decode_bars(pixels_row, count=15, x0=8, step=16):
    """pixels_row: list of (r,g,b) at one y; returns the list of 8-bit values"""
    out = []
    for n in range(count):
        r, g, b = pixels_row[x0 + n * step]
        def lv(c): return min(range(8), key=lambda i: abs(L8[i] - c))
        out.append(lv(r) | lv(g) << 3 | (lv(b) & 3) << 6)
    return out
