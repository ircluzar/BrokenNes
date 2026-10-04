from m68kasm import *
a = Asm()
for r, v in [(0, 0x04), (1, 0x44), (12, 0x81), (7, 0x00), (15, 0x02)]:
    a.move_w_imm_abs(0x8000 | r << 8 | v, VDP_CTRL)
a.move_w_imm_abs(0x8000 | 12 << 8 | 0x81, VDP_CTRL)
# port 1: TH is an output, driven high
a._w(0x13FC, 0x0040); a._long(0xA10009)           # move.b #$40,$A10009
a._w(0x13FC, 0x0040); a._long(0xA10003)           # move.b #$40,$A10003
a.label('loop')
a.move_b_abs_dn(0xA10003, 0)
a.andi_w(0x3F, 0)
a.lsl_w(3, 0)
a.move_l_imm_abs(0xC0000000, VDP_CTRL)
a.move_w_dn_abs(0, VDP_DATA)
a.bra('loop')
code = a.assemble()
print(code.hex())
print(len(code))
