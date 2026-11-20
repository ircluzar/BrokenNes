import sys, zlib
d = bytearray(open(sys.argv[1], 'rb').read()); m = bytes(d[:4])
if m == b'\x37\x80\x40\x12': d[0::2], d[1::2] = d[1::2], d[0::2]
elif m == b'\x40\x12\x37\x80':
    for i in range(0, len(d) - 3, 4): d[i:i+4] = d[i:i+4][::-1]
elif m != b'\x80\x37\x12\x40': print("warning: unknown ROM magic " + m.hex())
open(sys.argv[2], 'wb').write(d)
cic = {0x6170A4A1: "6101", 0x009E9EA3: "7102", 0x90BB6CB5: "6102/7101", 0x0B050EE0: "6103/7103", 0x98BC2C86: "6105/7105", 0xACC8580A: "6106/7106"}
crc = zlib.crc32(d[0x40:0x1000]) & 0xFFFFFFFF
print(f"name={d[0x20:0x34].decode('ascii','replace').strip()}\tcode={d[0x3B:0x3F].decode('ascii','replace')}\tcic={cic.get(crc,'unknown')}\tsize={len(d)>>20}MB")
