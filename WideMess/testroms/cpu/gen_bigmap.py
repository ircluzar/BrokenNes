# Makes the 4 MB image of the WideMess "big map" test ROM (bigmap.asm): bank 0 is the assembled code (64 KB), banks 1-63 are data generated here.
# Bank n holds its markers at $0000 (n, 'W', 'M', n ^ $FF) and $C000 (n ^ $A5, 'H', 'I', n); the rest is a low-entropy fill (one value per 4 KB,
# so the file compresses to almost nothing in git) that differs from bank to bank. The SNES checksum in bank 0's header is recomputed over 4 MB.
# Usage: python -I gen_bigmap.py <bank0.sfc> <out.sfc> [banks]
import sys

src, out = sys.argv[1], sys.argv[2]
banks = int(sys.argv[3]) if len(sys.argv) > 3 else 64
bank0 = bytearray(open(src, 'rb').read())
if len(bank0) != 0x10000:
    sys.exit(f'{src}: expected the 64 KB code bank, got {len(bank0)} bytes')
if bank0[0:4] != bytes((0, 0x57, 0x4D, 0xFF)) or bank0[0xC000:0xC004] != bytes((0xA5, 0x48, 0x49, 0)):
    sys.exit(f'{src}: bank 0 lacks its markers')

rom = bytearray(bank0)
rom[0xFFD8] = 0x08   # the internal header's battery RAM: 256 KB (WLA-DX's SRAMSIZE stops at 128 KB)
for n in range(1, banks):
    b = bytearray(0x10000)
    for page in range(16):
        v = (n * 3 + page * 17 + 0x40) & 0xFF
        b[page * 0x1000:(page + 1) * 0x1000] = bytes([v]) * 0x1000
    b[0:4] = bytes((n, 0x57, 0x4D, n ^ 0xFF))
    b[0xC000:0xC004] = bytes((n ^ 0xA5, 0x48, 0x49, n))
    rom += b

# the SNES checksum over the whole image (complement $FFDC, checksum $FFDE)
rom[0xFFDC:0xFFE0] = bytes((0xFF, 0xFF, 0, 0))
s = sum(rom) & 0xFFFF
rom[0xFFDC:0xFFE0] = bytes(((s ^ 0xFFFF) & 0xFF, (s ^ 0xFFFF) >> 8, s & 0xFF, s >> 8))
open(out, 'wb').write(rom)
print(f'{out}: {len(rom):,} bytes ({banks} banks of 64 KB), checksum {s:04X}')
