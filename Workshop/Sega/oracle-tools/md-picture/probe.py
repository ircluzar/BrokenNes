import os, subprocess, sys, time
from m68kasm import *
from pngio import read_png

ROOT = os.path.dirname(os.path.abspath(__file__))

def run_ours(rom_path, frames=30, region=None):
    png = rom_path[:-4] + '_ours.png'
    cmd = ['powershell', '-NoProfile', '-File', os.path.join(ROOT, 'ws.ps1'), '--md-run', rom_path, '--frames', str(frames), '--png', png, '--quiet', '--force']
    if region: cmd += ['--region', region]
    subprocess.run(cmd, capture_output=True, text=True, timeout=300)
    w, h, px = read_png(png)
    return decode_bars(px[100])

def run_blastem(rom_path, name):
    d = os.path.join(ROOT, 'be', name)
    f = os.path.join(d, 's000.png')
    for attempt in range(4):
        subprocess.run(['powershell', '-NoProfile', '-File', os.path.join(ROOT, 'bl.ps1'), '-Rom', rom_path, '-OutDir', d, '-Script', 'w1500,shot', '-StartupMs', '2500'], capture_output=True, text=True, timeout=300)
        if os.path.exists(f): break
        time.sleep(2)
    w, h, px = read_png(f)
    return decode_bars(px[100 + 9])

def build(name, body, h40=True, pad=0):
    a = Asm()
    vdp_setup(a, h40)
    body(a)
    emit_results(a)
    a.stop_forever()
    rom = make_rom(a.assemble(), title=name)
    p = os.path.join(ROOT, 'roms', name + '.bin')
    open(p, 'wb').write(rom)
    return p

class Slots:
    def __init__(self): self.i = 0; self.n = 0
    def label(self): self.n += 1; return 'L%d' % self.n

def poll(a, S, bit, want, pad=0):
    l = S.label()
    a.label(l); a.move_w_abs_dn(VDP_CTRL, 0)
    for _ in range(pad): a.nop()
    a.btst_imm_dn(bit, 0)
    if want == 0: a.bne(l)
    else: a.beq(l)

def store_hv(a, S):
    a.move_w_abs_dn(VDP_HV, 1)
    a.move_w_dn_dm(1, 2); a.lsr_w(8, 2); a.move_w_dn_abs(2, RAM + 2 * S.i); S.i += 1
    a.andi_w(0xFF, 1); a.move_w_dn_abs(1, RAM + 2 * S.i); S.i += 1

def p1(pad=0):
    def body(a):
        S = Slots()
        poll(a, S, 3, 0, pad); poll(a, S, 3, 1, pad); store_hv(a, S)       # VBlank rises
        poll(a, S, 3, 0, pad); store_hv(a, S)                              # VBlank falls
        poll(a, S, 2, 0, pad); poll(a, S, 2, 1, pad); store_hv(a, S)       # HBlank rises
        poll(a, S, 2, 0, pad); store_hv(a, S)                              # HBlank falls
        # remaining slots: zero
        while S.i < 15:
            a.moveq(0, 3); a.move_w_dn_abs(3, RAM + 2 * S.i); S.i += 1
    return body

def store_reg(a, S, n):
    """store the HV word held in Dn as two slots (V, H)"""
    a.move_w_dn_dm(n, 2); a.lsr_w(8, 2); a.move_w_dn_abs(2, RAM + 2 * S.i); S.i += 1
    a.move_w_dn_dm(n, 2); a.andi_w(0xFF, 2); a.move_w_dn_abs(2, RAM + 2 * S.i); S.i += 1

def p3(pad=0):
    """bracket the VBlank flag's rise and fall with two HV samples each, and the V counter's own step"""
    def body(a):
        S = Slots()
        poll(a, S, 3, 0, pad)
        l = S.label(); a.label(l)                      # until the flag is 1: HV, status
        a.move_w_abs_dn(VDP_HV, 6)
        for _ in range(pad): a.nop()
        a.move_w_abs_dn(VDP_CTRL, 0); a.btst_imm_dn(3, 0); a.beq(l)
        a.move_w_abs_dn(VDP_HV, 7)
        store_reg(a, S, 6); store_reg(a, S, 7)         # slots 0-3: before, after (VBlank rise)
        l = S.label(); a.label(l)                      # until the flag is 0
        a.move_w_abs_dn(VDP_HV, 6)
        for _ in range(pad): a.nop()
        a.move_w_abs_dn(VDP_CTRL, 0); a.btst_imm_dn(3, 0); a.bne(l)
        a.move_w_abs_dn(VDP_HV, 7)
        store_reg(a, S, 6); store_reg(a, S, 7)         # slots 4-7: before, after (VBlank fall)
        # the V counter's step: read HV until V changes (from a line in the active picture)
        poll(a, S, 3, 0, pad)
        a.move_w_abs_dn(VDP_HV, 4); a.lsr_w(8, 4)      # V now
        a.moveq(0, 5)
        l = S.label(); a.label(l)
        a.move_w_abs_dn(VDP_HV, 6)
        for _ in range(pad): a.nop()
        a.move_w_dn_dm(6, 3); a.lsr_w(8, 3); a.cmp_w(4, 3); a.beq(l)
        a.move_w_abs_dn(VDP_HV, 7)
        store_reg(a, S, 6); store_reg(a, S, 7)         # slots 8-11: the first sample with the new V, and the next one
        while S.i < 15:
            a.moveq(0, 3); a.move_w_dn_abs(3, RAM + 2 * S.i); S.i += 1
    return body

def p4(nwrites=13, vram_addr_inc=0, display_on=True):
    """the direct colour demo's lead-in: wait for the VBlank flag to fall, write nwrites words (R15 = vram_addr_inc), read HV right after, and again after 4 nops"""
    def body(a):
        S = Slots()
        a.move_w_imm_abs(0x8F00 | vram_addr_inc, VDP_CTRL)
        a.move_w_imm_abs(0x8154 if display_on else 0x8114, VDP_CTRL)
        a.move_l_imm_abs(0x40000000, VDP_CTRL)
        # be inside the picture first, then wait for the VBlank flag to rise and fall again
        poll(a, S, 3, 1, 0); poll(a, S, 3, 0, 0)
        a.move_w_abs_dn(VDP_HV, 6)                                  # right after the flag fell
        a.moveq(0, 0)
        for _ in range(nwrites): a.move_w_dn_abs(0, VDP_DATA)
        a.move_w_abs_dn(VDP_HV, 7)                                  # after the writes
        store_reg(a, S, 6); store_reg(a, S, 7)
        while S.i < 15:
            a.moveq(0, 3); a.move_w_dn_abs(3, RAM + 2 * S.i); S.i += 1
        a.move_w_imm_abs(0x8F02, VDP_CTRL); a.move_w_imm_abs(0x8144, VDP_CTRL)
    return body

def p5(targets, nwrites=12, display_on=True):
    """from the moment the V counter reads each target value, write nwrites words to VRAM and read HV after: the elapsed time shows how many slots the FIFO had in that line"""
    def body(a):
        S = Slots()
        a.move_w_imm_abs(0x8F00, VDP_CTRL)
        a.move_w_imm_abs(0x8154 if display_on else 0x8114, VDP_CTRL)
        a.move_l_imm_abs(0x40000000, VDP_CTRL)
        a.moveq(0, 0)
        for t in targets:
            l = S.label(); a.label(l)
            a.move_w_abs_dn(VDP_HV, 6); a.lsr_w(8, 6); a.cmpi_w(t, 6); a.bne(l)
            for _ in range(nwrites): a.move_w_dn_abs(0, VDP_DATA)
            a.move_w_abs_dn(VDP_HV, 7)
            store_reg(a, S, 7)
        while S.i < 15:
            a.moveq(0, 3); a.move_w_dn_abs(3, RAM + 2 * S.i); S.i += 1
        a.move_w_imm_abs(0x8F02, VDP_CTRL); a.move_w_imm_abs(0x8144, VDP_CTRL)
    return body

def p7(words=0xAD40, nwrites=13, nops=4):
    """the direct colour demo's own sequence with the real DMA length: HV when the 68000 gets the bus back"""
    def body(a):
        S = Slots()
        a.move_w_imm_abs(0x8F00, VDP_CTRL)
        a.move_w_imm_abs(0x8154, VDP_CTRL)
        a.move_l_imm_abs(0x40000000, VDP_CTRL)
        poll(a, S, 3, 1, 0); poll(a, S, 3, 0, 0)
        a.moveq(0, 0)
        for _ in range(nwrites): a.move_w_dn_abs(0, VDP_DATA)
        for _ in range(nops): a.nop()
        a.move_l_imm_abs(0x93009400 | (words & 0xFF) << 16 | (words >> 8), VDP_CTRL)
        a.move_l_imm_abs(0x95009600, VDP_CTRL)
        a.move_l_imm_abs(0x97008114, VDP_CTRL)
        a.move_l_imm_abs(0xC0000080, VDP_CTRL)
        a.move_w_abs_dn(VDP_HV, 7)
        store_reg(a, S, 7)
        while S.i < 15:
            a.moveq(0, 3); a.move_w_dn_abs(3, RAM + 2 * S.i); S.i += 1
        a.move_w_imm_abs(0x8F02, VDP_CTRL); a.move_w_imm_abs(0x8144, VDP_CTRL)
    return body

def p9(targets, nwrites=4, wait_extra=0):
    """from the moment the V counter reads each target value: write nwrites VRAM words, then poll until the FIFO reports empty and read HV: when the FIFO emptied"""
    def body(a):
        S = Slots()
        a.move_w_imm_abs(0x8F00, VDP_CTRL)
        a.move_w_imm_abs(0x8154, VDP_CTRL)
        a.move_l_imm_abs(0x40000000, VDP_CTRL)
        a.moveq(0, 0)
        for t in targets:
            l = S.label(); a.label(l)
            a.move_w_abs_dn(VDP_HV, 6); a.lsr_w(8, 6); a.cmpi_w(t, 6); a.bne(l)
            for _ in range(nwrites): a.move_w_dn_abs(0, VDP_DATA)
            l2 = S.label(); a.label(l2)
            a.move_w_abs_dn(VDP_CTRL, 1); a.btst_imm_dn(9, 1); a.beq(l2)       # until the FIFO is empty (status bit 9)
            a.move_w_abs_dn(VDP_HV, 7)
            store_reg(a, S, 7)
        while S.i < 15:
            a.moveq(0, 3); a.move_w_dn_abs(3, RAM + 2 * S.i); S.i += 1
        a.move_w_imm_abs(0x8F02, VDP_CTRL); a.move_w_imm_abs(0x8144, VDP_CTRL)
    return body

def p10(target=0xEF, nwrites=40, port='data'):
    """per-write cost of the 68000's accesses to a VDP port in a blank line: wait for V == target, do nwrites writes, read HV"""
    def body(a):
        S = Slots()
        a.move_w_imm_abs(0x8F00, VDP_CTRL)
        a.move_w_imm_abs(0x8154, VDP_CTRL)
        a.move_l_imm_abs(0x40000000, VDP_CTRL)
        a.moveq(0, 0)
        l = S.label(); a.label(l)
        a.move_w_abs_dn(VDP_HV, 6); a.lsr_w(8, 6); a.cmpi_w(target, 6); a.bne(l)
        a.move_w_abs_dn(VDP_HV, 5)                        # HV at the start (the same read as the detection costs)
        for _ in range(nwrites):
            if port == 'data': a.move_w_dn_abs(0, VDP_DATA)
            elif port == 'status': a.move_w_abs_dn(VDP_CTRL, 1)
            else: a.move_w_abs_dn(VDP_HV, 1)
        a.move_w_abs_dn(VDP_HV, 7)
        store_reg(a, S, 5); store_reg(a, S, 7)
        while S.i < 15:
            a.moveq(0, 3); a.move_w_dn_abs(3, RAM + 2 * S.i); S.i += 1
        a.move_w_imm_abs(0x8F02, VDP_CTRL); a.move_w_imm_abs(0x8144, VDP_CTRL)
    return body

def hv_to_clocks(v, h):
    off = 16 * h if h <= 0xB6 else 16 * (h - 45)
    line = v if v <= 234 else v + 6
    if off >= 2640: pass
    return line * 3420 + off

def run_pair(name, body):
    path = build(name, body)
    o = run_ours(path); b = run_blastem(path, name)
    return o, b

def scan_p3(pads=range(0, 5)):
    for pad in pads:
        path = build('p3s_%d' % pad, p3(pad))
        o = run_ours(path); b = run_blastem(path, 'p3s_%d' % pad)
        print(pad, 'ours', o[:12], flush=True); print(pad, 'blst', b[:12], flush=True)

def scan_p1(pads=range(0, 8)):
    res = {}
    for pad in pads:
        path = build('p1s_%d' % pad, p1(pad))
        o = run_ours(path); b = run_blastem(path, 'p1s_%d' % pad)
        res[pad] = (o[:8], b[:8])
        print(pad, 'ours', o[:8], 'blastem', b[:8], flush=True)
    return res

if __name__ == '__main__' and len(sys.argv) > 1 and sys.argv[1] == 'scan':
    scan_p1()
elif __name__ == '__main__' and len(sys.argv) > 1 and sys.argv[1] == 'scan3':
    scan_p3()
elif __name__ == '__main__' and len(sys.argv) > 1 and sys.argv[1] == 'p10':
    for port in ('data', 'status', 'hv'):
        for n in (20, 60):
            o, b = run_pair('p10_%s_%d' % (port, n), p10(0xEF, n, port))
            def el(r): return hv_to_clocks(r[2], r[3]) - hv_to_clocks(r[0], r[1])
            print('%-6s n=%2d ours %6d clocks (%.1f/access)   blastem %6d clocks (%.1f/access)   diff/access %.1f' % (port, n, el(o), el(o) / n, el(b), el(b) / n, (el(b) - el(o)) / n), flush=True)
elif __name__ == '__main__' and len(sys.argv) > 1 and sys.argv[1] == 'p9':
    for n in (4, 8):
        targets = [100, 226, 0xFA, 0xFC, 0xFD, 0xFE, 0xFF]
        o, b = run_pair('p9_%d' % n, p9(targets, n))
        for i, t in enumerate(targets):
            print('n=%d V=%02X  ours (V=%3d H=%3d)   blastem (V=%3d H=%3d)' % (n, t, o[2 * i], o[2 * i + 1], b[2 * i], b[2 * i + 1]))
elif __name__ == '__main__' and len(sys.argv) > 1 and sys.argv[1] == 'p7':
    for words in (100, 1000, 0xAD40):
        o, b = run_pair('p7_%d' % words, p7(words))
        print(words, 'ours', o[:2], 'blst', b[:2], flush=True)
elif __name__ == '__main__' and len(sys.argv) > 1 and sys.argv[1] == 'p5':
    targets = [100, 226, 0xEF, 0xF9, 0xFD, 0xFE, 0xFF]
    o, b = run_pair('p5', p5(targets))
    for i, t in enumerate(targets):
        print('V=%02X  ours (V=%3d H=%3d)   blastem (V=%3d H=%3d)' % (t, o[2 * i], o[2 * i + 1], b[2 * i], b[2 * i + 1]))
elif __name__ == '__main__' and len(sys.argv) > 1 and sys.argv[1] == 'p4':
    for n in (0, 4, 8, 13, 20):
        o, b = run_pair('p4_%d' % n, p4(n))
        print(n, 'ours', o[:4], 'blst', b[:4], flush=True)
elif __name__ == '__main__':
    name = sys.argv[1] if len(sys.argv) > 1 else 'p1'
    pad = int(sys.argv[2]) if len(sys.argv) > 2 else 0
    path = build('%s_%d' % (name, pad), p1(pad))
    o = run_ours(path); b = run_blastem(path, '%s_%d' % (name, pad))
    labels = ['VB rise V', 'VB rise H', 'VB fall V', 'VB fall H', 'HB rise V', 'HB rise H', 'HB fall V', 'HB fall H']
    for i, l in enumerate(labels):
        print('%-10s ours %3d (%02X)   blastem %3d (%02X) %s' % (l, o[i], o[i], b[i], b[i], '' if o[i] == b[i] else '  <-- differs'))
