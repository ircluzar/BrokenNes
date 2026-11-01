"""Pad every song stream array in a music_data.fab to N bytes: the template ROM's fixed-size "Exact song region".

usage: python -I pad_streams.py <music_data.fab> <N>

Each channel gets its own filler byte, so NESFab cannot merge two identical arrays. The filler sits after the stream's loop
opcode (0xFF) and is never executed.
"""
import re
import sys

FILLER = {'square1': 0xA1, 'square2': 0xA2, 'triangle': 0xA3, 'noise': 0xA4}


def main():
    path, n = sys.argv[1], int(sys.argv[2])
    text = open(path, encoding='utf-8').read()

    def pad(m):
        ch = m.group(2)
        data = [int(v) for v in m.group(4).split(',')]
        if len(data) > n:
            raise SystemExit(f'{m.group(1)} {ch}: {len(data)} bytes does not fit {n}')
        data += [FILLER[ch]] * (n - len(data))
        return f'[] music_{m.group(1)}_{ch}\n        U[{n}](' + ', '.join(str(v) for v in data) + ')'

    new, count = re.subn(r'\[\]\s+music_(\w+?)_(square1|square2|triangle|noise)\s*\n\s*U\[(\d+)\]\(([^)]*)\)', pad, text)
    if count == 0:
        raise SystemExit(f'{path}: no song stream arrays found')
    open(path, 'w', encoding='utf-8').write(new)
    print(f'padded {count} stream arrays to {n} bytes')


main()
