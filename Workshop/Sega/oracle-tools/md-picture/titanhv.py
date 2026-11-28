"""Where each VINT handler entry lands, Titan Overdrive 1 (sega-track-b4a.md section 5, 2026-11-27).

The patched ROM's vector 30 points to a stub appended at $3C3800 that stores (HV counter, d5) in a ring of 512 entries at $FF1100 (index word $FF1080; both are RAM the demo leaves zero), then
`jmp $FF0102` (the demo's own RAM vector). Both emulators run the same patched ROM, so the stub's extra cycles do not bias the comparison.

    python titanhv.py build ROM OUT.bin                  # write the patched ROM
    python titanhv.py cmp BLASTEM.state OURS.ram         # entries in order (aligned at the first d5 = $D4), ** where the entry line differs
Ours: --md-run --rom OUT.bin --frames 1250 --region pal --ram-out OURS.ram. BlastEm: bl.ps1 script w24000,pause,snap (PAL, about 1150 frames).
Entries are compared in order, not by d5: the main loop sometimes increments d5 after the frame's VINT on one emulator and before it on the other."""
import struct, sys

AT, RING, IDX, MASK = 0x3C3800, 0x1100, 0x1080, 0x7FC
STUB = bytes.fromhex('48E78080' '303900FF1080' '41F900FF1100' '31B900C000080000' '31850002' '5840' '024007FC' '33C000FF1080' '4CDF0101' '4EF900FF0102')
# movem.l d0/a0,-(sp); move.w ($FF1080).l,d0; lea ($FF1100).l,a0; move.w ($C00008).l,(0,a0,d0.w); move.w d5,(2,a0,d0.w); addq.w #4,d0; andi.w #$7FC,d0; move.w d0,($FF1080).l;
# movem.l (sp)+,d0/a0; jmp ($FF0102).l


def build(src, dst):
    d = bytearray(open(src, 'rb').read())
    assert len(d) <= AT
    d += bytes(AT - len(d)) + STUB + bytes(0x40 - len(STUB))
    struct.pack_into('>I', d, 30 * 4, AT)
    open(dst, 'wb').write(d)


def ring(ram):
    idx = struct.unpack_from('>H', ram, IDX)[0]
    out = []
    for k in range(512):
        hv, d5 = struct.unpack_from('>HH', ram, RING + ((idx + 4 * k) & MASK))
        if hv or d5: out.append((d5, hv))
    return out


def cmp(state, ours, start=0xD4):
    import oracle
    st = open(state, 'rb').read(); o = oracle.state_ram_offset(st)
    be, ou = ring(st[o:o + 0x10000]), ring(open(ours, 'rb').read())
    i = next(k for k, (d, _) in enumerate(be) if d == start); j = next(k for k, (d, _) in enumerate(ou) if d == start)
    for k in range(min(len(be) - i, len(ou) - j)):
        (bd, bh), (od, oh) = be[i + k], ou[j + k]
        print('%s #%3d  BlastEm d5 %03X hv %04X   ours d5 %03X hv %04X' % ('  ' if bh >> 8 == oh >> 8 else '**', k, bd, bh, od, oh))


if __name__ == '__main__':
    {'build': build, 'cmp': cmp}[sys.argv[1]](*sys.argv[2:])
