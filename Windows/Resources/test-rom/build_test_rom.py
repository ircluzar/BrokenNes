"""Builds Windows/Resources/test.nes - BrokenNes's embedded placeholder ROM.

The app loads this ROM in its idle launcher state (with a null-provider screen drawn over it),
so it only has to be a small, *valid* NES program: NROM-128, initialises the hardware the
standard way, draws "BROKENNES TEST ROM" and then idles, with an NMI handler that slowly cycles
the text colour so every frame does real work (a frozen-looking ROM would trip the freeze
detector). The ROM it replaces executed an illegal opcode and two truncated JSRs.

Run:  py build_test_rom.py        (writes ../test.nes)
No assembler needed - the tiny two-pass assembler below knows exactly the opcodes used here.
"""
import os

OPS = {  # mnemonic, mode -> opcode
    ("SEI", "imp"): 0x78, ("CLD", "imp"): 0xD8, ("TXS", "imp"): 0x9A, ("INX", "imp"): 0xE8,
    ("TXA", "imp"): 0x8A, ("TAX", "imp"): 0xAA, ("DEY", "imp"): 0x88, ("PHA", "imp"): 0x48,
    ("PLA", "imp"): 0x68, ("RTI", "imp"): 0x40, ("LSR", "imp"): 0x4A,
    ("LDA", "imm"): 0xA9, ("LDX", "imm"): 0xA2, ("LDY", "imm"): 0xA0, ("CPX", "imm"): 0xE0,
    ("CMP", "imm"): 0xC9, ("AND", "imm"): 0x29,
    ("LDA", "zp"): 0xA5, ("INC", "zp"): 0xE6, ("STA", "zpx"): 0x95,
    ("STA", "abs"): 0x8D, ("STX", "abs"): 0x8E, ("BIT", "abs"): 0x2C, ("JMP", "abs"): 0x4C,
    ("STA", "absx"): 0x9D, ("LDA", "absx"): 0xBD,
    ("BPL", "rel"): 0x10, ("BNE", "rel"): 0xD0, ("BEQ", "rel"): 0xF0,
}
SIZE = {"imp": 1, "imm": 2, "zp": 2, "zpx": 2, "rel": 2, "abs": 3, "absx": 3}

ORG = 0xC000  # NROM-128: the 16 KB bank is mirrored at $8000 and $C000; vectors live at $FFFA

# (label | None, mnemonic, mode, operand) - operand is an int or a label name
PROGRAM = [
    ("reset", "SEI", "imp", None), (None, "CLD", "imp", None),
    (None, "LDX", "imm", 0x40), (None, "STX", "abs", 0x4017),      # APU frame IRQ off
    (None, "LDX", "imm", 0xFF), (None, "TXS", "imp", None), (None, "INX", "imp", None),
    (None, "STX", "abs", 0x2000), (None, "STX", "abs", 0x2001),    # NMI and rendering off
    (None, "STX", "abs", 0x4010),                                  # DMC IRQ off
    ("vwait1", "BIT", "abs", 0x2002), (None, "BPL", "rel", "vwait1"),
    (None, "TXA", "imp", None),
    ("clrram", "STA", "zpx", 0x00),
    *[(None, "STA", "absx", page) for page in range(0x0100, 0x0800, 0x0100)],
    (None, "INX", "imp", None), (None, "BNE", "rel", "clrram"),
    ("vwait2", "BIT", "abs", 0x2002), (None, "BPL", "rel", "vwait2"),
    # palette
    (None, "LDA", "imm", 0x3F), (None, "STA", "abs", 0x2006),
    (None, "LDA", "imm", 0x00), (None, "STA", "abs", 0x2006),
    (None, "LDX", "imm", 0x00),
    ("palloop", "LDA", "absx", "paldata"), (None, "STA", "abs", 0x2007),
    (None, "INX", "imp", None), (None, "CPX", "imm", 32), (None, "BNE", "rel", "palloop"),
    # clear nametable 0 + attributes (1 KB of tile 0 = blank)
    (None, "LDA", "imm", 0x20), (None, "STA", "abs", 0x2006),
    (None, "LDA", "imm", 0x00), (None, "STA", "abs", 0x2006),
    (None, "LDY", "imm", 4), (None, "LDX", "imm", 0),
    ("ntloop", "STA", "abs", 0x2007), (None, "INX", "imp", None), (None, "BNE", "rel", "ntloop"),
    (None, "DEY", "imp", None), (None, "BNE", "rel", "ntloop"),
    # message on row 14, centred: $2000 + 14*32 + 7
    (None, "LDA", "imm", 0x21), (None, "STA", "abs", 0x2006),
    (None, "LDA", "imm", 0xC7), (None, "STA", "abs", 0x2006),
    (None, "LDX", "imm", 0),
    ("msgloop", "LDA", "absx", "text"), (None, "CMP", "imm", 0xFF), (None, "BEQ", "rel", "msgdone"),
    (None, "STA", "abs", 0x2007), (None, "INX", "imp", None), (None, "BNE", "rel", "msgloop"),
    ("msgdone", "LDA", "imm", 0), (None, "STA", "abs", 0x2005), (None, "STA", "abs", 0x2005),
    (None, "LDA", "imm", 0x80), (None, "STA", "abs", 0x2000),      # NMI on, BG tiles at $0000
    (None, "LDA", "imm", 0x0A), (None, "STA", "abs", 0x2001),      # BG on, incl. left 8 px
    ("idle", "JMP", "abs", "idle"),

    ("nmi", "PHA", "imp", None), (None, "TXA", "imp", None), (None, "PHA", "imp", None),
    (None, "INC", "zp", 0x00),
    (None, "LDA", "zp", 0x00), (None, "LSR", "imp", None), (None, "LSR", "imp", None),
    (None, "LSR", "imp", None), (None, "AND", "imm", 7), (None, "TAX", "imp", None),
    (None, "LDA", "imm", 0x3F), (None, "STA", "abs", 0x2006),
    (None, "LDA", "imm", 0x01), (None, "STA", "abs", 0x2006),
    (None, "LDA", "absx", "colors"), (None, "STA", "abs", 0x2007),
    # $2006 writes clobber the scroll: restore nametable select and scroll 0,0
    (None, "LDA", "imm", 0x80), (None, "STA", "abs", 0x2000),
    (None, "LDA", "imm", 0), (None, "STA", "abs", 0x2005), (None, "STA", "abs", 0x2005),
    (None, "PLA", "imp", None), (None, "TAX", "imp", None), (None, "PLA", "imp", None),
    ("irq", "RTI", "imp", None),
]

GLYPHS = {  # 8x8, one bit per pixel, drawn in colour 1
    "B": [0x7C, 0x66, 0x66, 0x7C, 0x66, 0x66, 0x7C, 0x00],
    "R": [0x7C, 0x66, 0x66, 0x7C, 0x6C, 0x66, 0x66, 0x00],
    "O": [0x3C, 0x66, 0x66, 0x66, 0x66, 0x66, 0x3C, 0x00],
    "K": [0x66, 0x6C, 0x78, 0x70, 0x78, 0x6C, 0x66, 0x00],
    "E": [0x7E, 0x60, 0x60, 0x7C, 0x60, 0x60, 0x7E, 0x00],
    "N": [0x66, 0x76, 0x7E, 0x7E, 0x6E, 0x66, 0x66, 0x00],
    "S": [0x3C, 0x66, 0x60, 0x3C, 0x06, 0x66, 0x3C, 0x00],
    "T": [0x7E, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x00],
    "M": [0x63, 0x77, 0x7F, 0x6B, 0x63, 0x63, 0x63, 0x00],
}
TILE = {" ": 0, **{ch: i + 1 for i, ch in enumerate(GLYPHS)}}
MESSAGE = "BROKENNES TEST ROM"

DATA = {
    "paldata": [0x02, 0x30, 0x10, 0x00] * 8,                       # dark blue ground, white text
    "colors": [0x30, 0x3C, 0x3B, 0x3A, 0x38, 0x37, 0x36, 0x34],    # the NMI's text-colour cycle
    "text": [TILE[c] for c in MESSAGE] + [0xFF],
}


def assemble():
    labels, pc = {}, ORG
    for label, mn, mode, _ in PROGRAM:  # pass 1: addresses
        if label:
            labels[label] = pc
        pc += SIZE[mode]
    for name, blob in DATA.items():
        labels[name] = pc
        pc += len(blob)
    out = bytearray()
    for _, mn, mode, arg in PROGRAM:  # pass 2: bytes
        val = labels[arg] if isinstance(arg, str) else arg
        here = ORG + len(out)
        out.append(OPS[(mn, mode)])
        if mode == "rel":
            off = val - (here + 2)
            assert -128 <= off <= 127, f"branch out of range at {here:04X}"
            out.append(off & 0xFF)
        elif SIZE[mode] == 2:
            out.append(val & 0xFF)
        elif SIZE[mode] == 3:
            out += bytes((val & 0xFF, val >> 8))
    for blob in DATA.values():
        out += bytes(blob)
    prg = bytearray(b"\xFF" * 0x4000)
    prg[: len(out)] = out
    for i, name in enumerate(("nmi", "reset", "irq")):  # $FFFA NMI, $FFFC RESET, $FFFE IRQ
        a = labels[name]
        prg[0x3FFA + 2 * i : 0x3FFC + 2 * i] = bytes((a & 0xFF, a >> 8))
    return prg


def chr_rom():
    chr_ = bytearray(0x2000)
    for ch, rows in GLYPHS.items():
        base = TILE[ch] * 16
        chr_[base : base + 8] = bytes(rows)  # plane 0 only -> colour 1; plane 1 stays 0
    return chr_


if __name__ == "__main__":
    header = b"NES\x1a" + bytes((1, 1, 0, 0)) + bytes(8)  # 16 KB PRG, 8 KB CHR, mapper 0
    rom = header + assemble() + chr_rom()
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "test.nes")
    with open(path, "wb") as f:
        f.write(rom)
    print(f"wrote {os.path.normpath(path)} ({len(rom)} bytes)")
