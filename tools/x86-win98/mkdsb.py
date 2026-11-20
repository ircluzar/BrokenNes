# mkdsb.py OUT.EXE: a tiny Win32 PE (no CRT) testing DirectSound: DirectSoundCreate, SetCooperativeLevel(NORMAL), a 1 s 22050 Hz 8-bit
# mono buffer (GLOBALFOCUS) filled with a 441 Hz square wave, Play(LOOPING). Then a MessageBox shows every HRESULT (the sound keeps
# looping behind the box). A failed call skips straight to the box. Built from sc98/tools/mkddb.py's PE writer.
import struct, sys

IMAGE_BASE = 0x400000
TEXT_RVA, IDATA_RVA, DATA_RVA = 0x1000, 0x2000, 0x3000
FA = 0x400
IMPORTS = [("DSOUND.DLL", ["DirectSoundCreate"]),
           ("USER32.DLL", ["RegisterClassA", "CreateWindowExA", "DefWindowProcA", "PeekMessageA", "DispatchMessageA", "MessageBoxA", "wsprintfA"])]

def build_idata():
    n = len(IMPORTS)
    off = 20 * (n + 1)
    layout = []
    for dll, funcs in IMPORTS:
        ilt = off; off += 4 * (len(funcs) + 1)
        iat = off; off += 4 * (len(funcs) + 1)
        layout.append((dll, funcs, ilt, iat))
    blob = bytearray(off); tail = bytearray()
    def add_tail(b):
        nonlocal tail
        pos = off + len(tail); tail += b
        if len(tail) % 2: tail += b'\0'
        return pos
    iat_va = {}
    for i, (dll, funcs, ilt, iat) in enumerate(layout):
        name_off = add_tail(dll.encode() + b'\0')
        struct.pack_into('<IIIII', blob, 20 * i, IDATA_RVA + ilt, 0, 0, IDATA_RVA + name_off, IDATA_RVA + iat)
        for j, f in enumerate(funcs):
            hn = add_tail(b'\0\0' + f.encode() + b'\0')
            struct.pack_into('<I', blob, ilt + 4 * j, IDATA_RVA + hn)
            struct.pack_into('<I', blob, iat + 4 * j, IDATA_RVA + hn)
            iat_va[f] = IMAGE_BASE + IDATA_RVA + iat + 4 * j
    return bytes(blob + tail), iat_va

idata, IAT = build_idata()
D = IMAGE_BASE + DATA_RVA
HWND, WC, MSG, CLS = D + 8, D + 0x80, D + 0xB0, D + 0xD0
LPDS, LPBUF, P1, N1, P2, N2, WFX, BD = D + 0x20, D + 0x24, D + 0x28, D + 0x2C, D + 0x30, D + 0x34, D + 0x40, D + 0x60
FMT, OUTB = D + 0x100, D + 0x180
HR = [D + 0x1E0 + 4 * i for i in range(5)]     # create, coop, buffer, lock, play
DATA_SIZE = 0x200

c = bytearray()
def e(*parts):
    for p in parts: c.extend(p)
def u32(v): return struct.pack('<I', v & 0xffffffff)
def call_iat(f): e(b'\xFF\x15', u32(IAT[f]))
def push32(v): e(b'\x68', u32(v))
def mov_m_imm(a, v): e(b'\xC7\x05', u32(a), u32(v))
def com_call(obj, off): e(b'\xA1', u32(obj), b'\x50', b'\x8B\x08', b'\xFF\x91', u32(off))
def jcc_back(op, target): e(op); c.extend(struct.pack('<i', target - (len(c) + 4)))
fails = []
def store_hr(i):                                            # mov [HR i], eax; test eax,eax; js report
    e(b'\xA3', u32(HR[i]), b'\x85\xC0', b'\x0F\x88'); fails.append(len(c)); e(u32(0))

for i in range(5): mov_m_imm(HR[i], 0xEEEEEEEE)             # EEEEEEEE = not reached
mov_m_imm(CLS, 0x00425344)                                  # "DSB\0"
e(b'\xA1', u32(IAT["DefWindowProcA"]), b'\xA3', u32(WC + 4))
mov_m_imm(WC + 16, IMAGE_BASE); mov_m_imm(WC + 36, CLS)
push32(WC); call_iat("RegisterClassA")
push32(0); push32(IMAGE_BASE); push32(0); push32(0); push32(100); push32(200); push32(0); push32(0); push32(0x90000000); push32(CLS); push32(CLS); push32(0)
call_iat("CreateWindowExA"); e(b'\xA3', u32(HWND))
push32(0); push32(LPDS); push32(0); call_iat("DirectSoundCreate"); store_hr(0)
push32(1); e(b'\xFF\x35', u32(HWND)); com_call(LPDS, 0x18); store_hr(1)           # SetCooperativeLevel(hwnd, DSSCL_NORMAL)
mov_m_imm(WFX, 0x00010001); mov_m_imm(WFX + 4, 22050); mov_m_imm(WFX + 8, 22050); mov_m_imm(WFX + 12, 0x00080001); mov_m_imm(WFX + 16, 0)
mov_m_imm(BD, 20); mov_m_imm(BD + 4, 0x8000); mov_m_imm(BD + 8, 22050); mov_m_imm(BD + 12, 0); mov_m_imm(BD + 16, WFX)
push32(0); push32(LPBUF); push32(BD); com_call(LPDS, 0x0C); store_hr(2)            # CreateSoundBuffer
push32(0); push32(N2); push32(P2); push32(N1); push32(P1); push32(22050); push32(0); com_call(LPBUF, 0x2C); store_hr(3)   # Lock
e(b'\x8B\x3D', u32(P1), b'\x8B\x0D', u32(N1), b'\x33\xD2')
lp = len(c)
e(b'\xB0\xC0', b'\x83\xFA\x19', b'\x72\x02', b'\xB0\x40')  # mov al,C0; cmp edx,25; jb +2; mov al,40
e(b'\xAA', b'\x42', b'\x83\xFA\x32', b'\x72\x02', b'\x33\xD2', b'\x49')   # stosb; inc edx; cmp edx,50; jb +2; xor edx,edx; dec ecx
jcc_back(b'\x0F\x85', lp)
e(b'\xFF\x35', u32(N2), b'\xFF\x35', u32(P2), b'\xFF\x35', u32(N1), b'\xFF\x35', u32(P1)); com_call(LPBUF, 0x4C)   # Unlock
push32(1); push32(0); push32(0); com_call(LPBUF, 0x30); store_hr(4)                 # Play(0, 0, DSBPLAY_LOOPING)
for at in fails: struct.pack_into('<i', c, at, len(c) - (at + 4))
fmt = b'create %08lX coop %08lX buffer %08lX lock %08lX play %08lX\0'
for i in range(0, len(fmt), 4): mov_m_imm(FMT + i, struct.unpack('<I', fmt[i:i + 4].ljust(4, b'\0'))[0])
for i in (4, 3, 2, 1, 0): e(b'\xFF\x35', u32(HR[i]))
push32(FMT); push32(OUTB); call_iat("wsprintfA"); e(b'\x83\xC4\x1C')
push32(0); push32(CLS); push32(OUTB); push32(0); call_iat("MessageBoxA")
frame = len(c)
push32(1); push32(0); push32(0); push32(0); push32(MSG); call_iat("PeekMessageA")
e(b'\x85\xC0'); nomsg_at = len(c); e(b'\x74\x00')
push32(MSG); call_iat("DispatchMessageA")
c[nomsg_at + 1] = len(c) - (nomsg_at + 2)
e(b'\xE9'); c.extend(struct.pack('<i', frame - (len(c) + 4)))
text = bytes(c)

def pad(b, n): return b + bytes((-len(b)) % n)
assert len(text) <= FA and len(idata) <= FA
img_size = (DATA_RVA + DATA_SIZE + 0xfff) & ~0xfff
dos = bytearray(0x80); dos[0:2] = b'MZ'; struct.pack_into('<I', dos, 0x3c, 0x80)
coff = struct.pack('<4sHHIIIHH', b'PE\0\0', 0x14c, 3, 0, 0, 0, 0xE0, 0x010F)
opt = struct.pack('<HBBIIIIIIIIIHHHHHHIIIIHHIIIIII', 0x10B, 6, 0, FA, FA, (DATA_SIZE + 0xfff) & ~0xfff, TEXT_RVA, TEXT_RVA, IDATA_RVA,
                  IMAGE_BASE, 0x1000, FA, 4, 0, 0, 0, 4, 0, 0, img_size, 0x400, 0, 2, 0, 0x100000, 0x1000, 0x100000, 0x1000, 0, 16)
dirs = bytearray(16 * 8); struct.pack_into('<II', dirs, 8, IDATA_RVA, len(idata))
def sec(name, vsize, rva, rawsize, rawptr, ch): return struct.pack('<8sIIIIIIHHI', name, vsize, rva, rawsize, rawptr, 0, 0, 0, 0, ch)
hdr = bytes(dos) + coff + opt + bytes(dirs)
secs = sec(b'.text', len(text), TEXT_RVA, FA, 0x400, 0x60000020) + sec(b'.idata', len(idata), IDATA_RVA, FA, 0x800, 0xC0000040) + sec(b'.data', DATA_SIZE, DATA_RVA, 0, 0, 0xC0000080)
out = pad(hdr + secs, 0x400) + pad(text, FA) + pad(idata, FA)
open(sys.argv[1], 'wb').write(out)
print(f"{sys.argv[1]}: {len(out)} bytes, code {len(text)} bytes")
