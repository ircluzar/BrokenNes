"""Write a song's four opcode streams into a template ROM's stream arrays, by PRG offset taken from the ROM's .mlb.

On a file this is exactly what BrokenNes' NES.PokePrg(offset, value) (Cartridge.PokePrg) does on a running machine: PRG-ROM is
poked by its offset in the PRG area, the 16-byte iNES header not counted. This script is the stand-in the plugin's "Exact
preview" would replace with real pokes.

usage: python -I patch_streams.py <template.nes> <template.mlb> <template-song-name> <music_data.fab> <song-name> <out.nes>
"""
import re
import sys

CHANNELS = ('square1', 'square2', 'triangle', 'noise')


def main():
    rom_path, mlb_path, tname, fab_path, sname, out_path = sys.argv[1:7]
    rom = bytearray(open(rom_path, 'rb').read())
    if rom[:4] != b'NES\x1a':
        raise SystemExit(f'{rom_path}: not an iNES file')
    header = 16

    symbols = {}
    for line in open(mlb_path, encoding='utf-8'):
        m = re.match(r'NesPrgRom:([0-9A-Fa-f]+)-([0-9A-Fa-f]+):(music_\w+?)@', line)
        if m:
            symbols[m.group(3)] = (int(m.group(1), 16), int(m.group(2), 16) - int(m.group(1), 16) + 1)

    text = open(fab_path, encoding='utf-8').read()
    target = {}
    for m in re.finditer(r'\[\]\s+music_(\w+?)_(square1|square2|triangle|noise)\s*\n\s*U\[(\d+)\]\(([^)]*)\)', text):
        if m.group(1) == sname:
            target[m.group(2)] = bytes(int(v) for v in m.group(4).split(','))
    for ch in CHANNELS:
        sym = f'music_{tname}_{ch}'
        if sym not in symbols:
            raise SystemExit(f'{mlb_path}: no symbol {sym}')
        if ch not in target:
            raise SystemExit(f'{fab_path}: song {sname!r} has no {ch} stream')
        off, size = symbols[sym]
        data = target[ch]
        if len(data) > size:
            raise SystemExit(f'{ch}: {len(data)} bytes do not fit the template region of {size}')
        rom[header + off: header + off + len(data)] = data
        print(f'{sym}: PRG offset 0x{off:06X}, region {size} bytes, wrote {len(data)} bytes of {sname!r}')
    open(out_path, 'wb').write(rom)


main()
