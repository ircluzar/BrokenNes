#!/usr/bin/env python3
"""Animated gray static test ROM (Mapper 0 / NROM-128: 16KB PRG, 8KB CHR)

Goal: When loaded, the screen shows continuously changing TV-style gray static
so emulator post-processing/effects paths can be visually verified.

Approach:
 - 64 random CHR tiles (indices 0-63) containing 4-color noise (both planes random).
 - Background nametable continually repopulated with new random tile indices.
 - Each frame during vblank we:
         * Randomize background palette (4 gray shades from NES grayscale set)
         * Update 4 rows (128 tiles) with new random tile indices using fast 8-bit LFSR.
 - Over ~8 frames the entire 30 rows refresh, giving a lively static effect without
     exceeding vblank bandwidth.
 - Attribute table fixed to palette 0 for entire screen.

Timings: Updating 128 tiles/frame (store + PRNG logic ~16 cycles) comfortably fits
inside vblank budget (~2273 CPU cycles).
"""
import os, struct, random

PRG_ORIGIN = 0x8000  # 16KB PRG mapped at $8000-$BFFF (mirrored at $C000)
code = bytearray()

def emit(*b):
    code.extend(b)

def lda(v):
    emit(0xA9, v & 0xFF)

# Reset vector code ----------------------------------------------------------
# SEI CLD LDX #$FF TXS
emit(0x78, 0xD8, 0xA2, 0xFF, 0x9A)

# Wait for vblank (poll $2002 bit7)
loop_wait1 = len(code)
emit(0xAD,0x02,0x20,      # LDA $2002
     0x10,0xFA)           # BPL loop_wait1 (branch if bit7 clear)

# Disable rendering while we update VRAM
lda(0x00); emit(0x8D,0x01,0x20)  # PPUMASK
lda(0x00); emit(0x8D,0x00,0x20)  # PPUCTRL

# Clear full nametable + attribute ($2000-$23FF) = 1024 bytes
lda(0x20); emit(0x8D,0x06,0x20)  # PPUADDR high
lda(0x00); emit(0x8D,0x06,0x20)  # PPUADDR low
lda(0x00)
emit(0xA2,0x04)          # X = 4 pages (4 * 256 = 1024)
emit(0xA0,0x00)          # Y = 0 (inner counter)
loop_clear_page = len(code)
emit(0x8D,0x07,0x20,     # STA PPUDATA (write 0)
     0xC8,               # INY
     0xD0,0xF9,          # BNE loop_clear_page (until Y wraps after 256 writes)
     0xCA,               # DEX (next page)
     0xD0,0xF4)          # BNE loop_clear_page

# Load initial grayscale palette at $3F00: [0F, 10, 20, 30]
lda(0x3F); emit(0x8D,0x06,0x20)
lda(0x00); emit(0x8D,0x06,0x20)
for v in [0x0F,0x10,0x20,0x30]:
    lda(v); emit(0x8D,0x07,0x20)

ROWS_PER_FRAME = 4

# Zero page usage:
# $00 = LFSR seed
# $01 = current row (0..29)
# $02/$03 scratch (not persistent)

# Seed LFSR (arbitrary non-zero)
lda(0xA5); emit(0x85,0x00)
lda(0x00); emit(0x85,0x01)  # row = 0

# Build row address tables (low/high bytes of $2000 + row*32)
row_addrs = [0x2000 + r*32 for r in range(30)]
row_lo = [a & 0xFF for a in row_addrs]
row_hi = [(a >> 8) & 0xFF for a in row_addrs]

# We'll append tables later and patch their addresses here (placeholders)
def jmp_abs(addr):
    emit(0x4C, addr & 0xFF, addr >> 8)

# ---------------- Subroutines ------------------
"""We emit placeholder bytes for subroutines then patch after placing tables."""

sub_next_rand_addr = len(code)
# NextRand: (8-bit Galois LFSR polynomial 0x1D) A returns new value
emit(0xA5,0x00,      # LDA seed
     0x0A,           # ASL A
     0x90,0x02,      # BCC skip_xor
     0x49,0x1D,      # EOR #$1D
     0x85,0x00,      # STA seed
     0x60)           # RTS
code[sub_next_rand_addr + 5] = 0x02  # ensure correct branch offset (already 0x02)

sub_row_update_addr = len(code)
# RowUpdate: updates one row with 32 random tile indices
# Y is free; X used as counter
emit(
    0xA4,0x01,              # LDY row
    0xB9,0x00,0x00,         # LDA row_hi_table,Y (placeholder hi addr)
    0x8D,0x06,0x20,         # STA PPUADDR high
    0xB9,0x00,0x00,         # LDA row_lo_table,Y (placeholder lo addr)
    0x8D,0x06,0x20,         # STA PPUADDR low
    0xA2,0x20,              # LDX #32
    # loop label:
    0x20, sub_next_rand_addr & 0xFF, sub_next_rand_addr >> 8,  # JSR NextRand
    0x29,0x3F,              # AND #$3F (limit tile index)
    0x8D,0x07,0x20,         # STA PPUDATA
    0xCA,                   # DEX
    0xD0,0xF5,              # BNE loop (-11)
    0xE6,0x01,              # INC row
    0xA5,0x01,              # LDA row
    0xC9,0x1E,              # CMP #30
    0xD0,0x02,              # BNE no_reset
    0xA9,0x00,              # LDA #0
    0x85,0x01,              # STA row
    # no_reset:
    0x60                    # RTS
)
row_update_hi_placeholder1 = sub_row_update_addr + 3
row_update_lo_placeholder1 = sub_row_update_addr + 11

# Enable background rendering (show background) BEFORE entering loop
lda(0x08); emit(0x8D,0x01,0x20)  # PPUMASK: show background
lda(0x00); emit(0x8D,0x00,0x20)  # PPUCTRL: no NMI (manual vblank polls)

# ---------------- Main loop --------------------
main_loop_addr = len(code)
emit(0xAD,0x02,0x20,0x10,0xFB)  # wait vblank

# Randomize palette: $3F00 universal black + 3 random grays from table
lda(0x3F); emit(0x8D,0x06,0x20)
lda(0x00); emit(0x8D,0x06,0x20)
emit(0xA9,0x0F,0x8D,0x07,0x20)  # write universal background
gray_table_addr_placeholder = []
for _ in range(3):
    emit(0x20, sub_next_rand_addr & 0xFF, sub_next_rand_addr >> 8)  # JSR NextRand
    emit(0x29,0x03)                  # AND #3
    emit(0xA8)                       # TAY
    emit(0xB9,0x00,0x00)             # LDA gray_table,Y (placeholder)
    gray_table_addr_placeholder.append(len(code)-2)
    emit(0x8D,0x07,0x20)             # STA PPUDATA

# Update several rows
for _ in range(ROWS_PER_FRAME):
    emit(0x20, sub_row_update_addr & 0xFF, sub_row_update_addr >> 8)  # JSR RowUpdate

emit(0x4C, main_loop_addr & 0xFF, main_loop_addr >> 8)  # JMP main_loop

# Pad PRG to 16KB minus vectors
while len(code) < 0x3FFA:
    code.append(0xFF)

# Vectors (little-endian): NMI, RESET, IRQ/BRK
RESET_ADDR = PRG_ORIGIN
vectors = struct.pack('<HHH', RESET_ADDR, RESET_ADDR, RESET_ADDR)
code += vectors

# ----------------------------- CHR DATA -------------------------------------
chr_data = bytearray()
# 64 random noise tiles (indices 0..63). Each tile 16 bytes (plane0 then plane1)
random.seed(0xBEEF)
for _ in range(64):
    plane0 = bytes(random.getrandbits(8) for _ in range(8))
    plane1 = bytes(random.getrandbits(8) for _ in range(8))
    chr_data += plane0 + plane1
while len(chr_data) < 0x2000:
    chr_data += bytes([0]*16)

# ----------------------------- HEADER & FILE --------------------------------
header = bytearray(b'NES\x1A') + bytes([
    1,    # 16KB PRG
    1,    # 8KB CHR
    0x00, # flags6 (horizontal mirroring default)
    0x00, # flags7
    0x00, # PRG-RAM (deprecated field, 0 => assume 8KB)
    0x00, # flags9
    0x00, # flags10
    0,0,0,0,0  # padding
])

"""Patch placeholders for tables now that PRG body assembled except tables."""

# Append gray table (4 grayscale choices) & row address tables and patch
gray_values = [0x00,0x10,0x20,0x30]
gray_table_addr = PRG_ORIGIN + len(code)
for pos in gray_table_addr_placeholder:
    code[pos] = (gray_table_addr) & 0xFF
    code[pos+1] = (gray_table_addr >> 8) & 0xFF
code.extend(gray_values)

row_hi_addr = PRG_ORIGIN + len(code)
code.extend(row_hi)
row_lo_addr = PRG_ORIGIN + len(code)
code.extend(row_lo)

# Patch row table addresses inside RowUpdate
code[row_update_hi_placeholder1] = row_hi_addr & 0xFF
code[row_update_hi_placeholder1+1] = (row_hi_addr >> 8) & 0xFF
code[row_update_lo_placeholder1] = row_lo_addr & 0xFF
code[row_update_lo_placeholder1+1] = (row_lo_addr >> 8) & 0xFF

rom = header + code + chr_data

os.makedirs('wwwroot', exist_ok=True)
with open('wwwroot/test.nes', 'wb') as f:
    f.write(rom)

print(f'Wrote animated static test ROM -> wwwroot/test.nes ({len(rom)} bytes)')
