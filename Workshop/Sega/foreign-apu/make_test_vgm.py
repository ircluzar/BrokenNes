#!/usr/bin/env python3
"""Synthetic test songs (VGM 1.50) for smoke-testing Sega sound units, the foreign APU project's ports among them.
Writes md-test.vgm (YM2612 FM chords with real modulation + PSG lead + noise hats, 8 s) and sms-test.vgm (PSG lead +
YM2413 chords + YM2413 rhythm, 8 s). Original material generated here; no game music. Usage: make_test_vgm.py [outdir]"""
import struct, sys, os

NTSC = 3579545
YM2612_CLK = 7670453
STEP = 11025          # an 8th note at 120 bpm, in 44.1 kHz samples
PROG = [(0, 4, 7), (5, 9, 12), (7, 11, 14), (0, 4, 7), (9, 12, 16), (5, 9, 12), (7, 11, 14), (7, 11, 14)]  # I vi IV V-ish, semitones from C4
LEAD = [12, 16, 19, 16, 17, 21, 24, 21, 19, 23, 26, 23, 12, 16, 19, 24, 21, 24, 28, 24, 17, 21, 24, 21, 19, 23, 26, 23, 19, 23, 26, 31]

def hz(semi):  # semitones above C4
    return 261.6256 * 2 ** (semi / 12)

class Vgm:
    def __init__(self): self.d = bytearray(); self.t = 0
    def wait(self, n):
        self.t += n
        while n > 0:
            k = min(n, 65535); self.d += bytes([0x61, k & 0xFF, k >> 8]); n -= k
    def psg(self, b): self.d += bytes([0x50, b & 0xFF])
    def ym(self, part, a, v): self.d += bytes([0x52 + part, a & 0xFF, v & 0xFF])
    def opll(self, a, v): self.d += bytes([0x51, a & 0xFF, v & 0xFF])
    def save(self, path, sn=NTSC, opll=0, ym2612=0):
        body = self.d + bytes([0x66])
        h = bytearray(0x40)
        h[0:4] = b'Vgm '
        struct.pack_into('<IIIIIIIIIHBBII', h, 4, 0x40 + len(body) - 4, 0x150, sn, opll, 0, self.t, 0, 0, 60, 0x0009, 16, 0, ym2612, 0)
        struct.pack_into('<I', h, 0x34, 0x40 - 0x34)
        with open(path, 'wb') as f: f.write(h + body)

def psg_tone(v, ch, f, vol):
    n = max(1, min(1023, round(NTSC / (32 * f))))
    v.psg(0x80 | ch << 5 | (n & 15)); v.psg(n >> 4)
    v.psg(0x90 | ch << 5 | vol)

def psg_noise(v, vol, mode=0x04):     # white noise, rate N/512
    v.psg(0xE0 | mode); v.psg(0xF0 | vol)

def md_song(path):
    v = Vgm()
    v.ym(0, 0x22, 0); v.ym(0, 0x27, 0); v.ym(0, 0x2B, 0)
    for c in range(3):                 # algorithm 4 (two FM pairs), feedback 5: modulation is where FM cores differ
        v.ym(0, 0xB0 + c, 5 << 3 | 4); v.ym(0, 0xB4 + c, 0xC0)
        for op, (mul, tl) in zip((0, 8, 4, 12), ((1, 0x22), (2, 0x26), (1, 0x14), (1, 0x16))):   # op1, op3, op2, op4
            v.ym(0, 0x30 + op + c, mul); v.ym(0, 0x40 + op + c, tl); v.ym(0, 0x50 + op + c, 0x1C)
            v.ym(0, 0x60 + op + c, 0x08); v.ym(0, 0x70 + op + c, 0x03); v.ym(0, 0x80 + op + c, 0x47); v.ym(0, 0x90 + op + c, 0)
    for i in range(32):
        chord = PROG[i // 4]
        if i % 4 == 0:
            for c in range(3):
                f = hz(chord[c] - 12); block = 4
                fnum = round(f * 2 ** 20 / ((YM2612_CLK / 144) * 2 ** (block - 1)))
                v.ym(0, 0x28, c)
                v.ym(0, 0xA4 + c, block << 3 | fnum >> 8); v.ym(0, 0xA0 + c, fnum & 0xFF)
                v.ym(0, 0x28, 0xF0 | c)
        psg_tone(v, 0, hz(LEAD[i]), 3)
        psg_noise(v, 6 if i % 2 else 12)
        v.wait(STEP // 2); psg_noise(v, 15); v.psg(0x90 | 0 << 5 | 9); v.wait(STEP - STEP // 2)
    for c in range(3): v.ym(0, 0x28, c)
    v.psg(0x9F); v.psg(0xFF); v.wait(22050)
    v.save(path, ym2612=YM2612_CLK)

def sms_song(path):
    v = Vgm()
    insts = (2, 3, 6)                  # built-in patches: guitar, piano, organ
    v.opll(0x0E, 0x20)                 # rhythm mode on
    for ch, f, blk in ((6, 0x20, 2), (7, 0x50, 2), (8, 0xC0, 1)):     # the usual rhythm frequencies
        v.opll(0x10 + ch, f); v.opll(0x20 + ch, blk << 1)
    v.opll(0x36, 0x02); v.opll(0x37, 0x22); v.opll(0x38, 0x22)
    for i in range(32):
        chord = PROG[i // 4]
        if i % 4 == 0:
            for c in range(3):
                f = hz(chord[c]); block = 4
                fnum = round(f * 2 ** 19 / (NTSC / 72) / 2 ** (block - 1))
                v.opll(0x20 + c, 0x00)
                v.opll(0x30 + c, insts[c] << 4 | 2); v.opll(0x10 + c, fnum & 0xFF)
                v.opll(0x20 + c, 0x10 | block << 1 | fnum >> 8)
        drums = 0x10 if i % 4 == 0 else 0x08 if i % 4 == 2 else 0
        v.opll(0x0E, 0x20 | drums | 0x01)
        psg_tone(v, 0, hz(LEAD[i]), 2)
        psg_tone(v, 1, hz(LEAD[i] - 12), 8)
        v.wait(STEP // 2); v.opll(0x0E, 0x20); v.psg(0x90 | 0 << 5 | 8); v.wait(STEP - STEP // 2)
    for c in range(3): v.opll(0x20 + c, 0)
    v.psg(0x9F); v.psg(0xBF); v.wait(22050)
    v.save(path, opll=NTSC)

if __name__ == '__main__':
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
    md_song(os.path.join(out, 'md-test.vgm')); sms_song(os.path.join(out, 'sms-test.vgm'))
    print('wrote md-test.vgm and sms-test.vgm in', out)
