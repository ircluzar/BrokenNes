"""Set the one-song roster's loop length in a generated music_roster.fab to a given value (the template ROM's sentinel).

usage: python -I set_loop_frames.py <music_roster.fab> <frames>

The AudioPlayer's screen shows the song position against MUSIC_LOOP_FRAMES (and counts the loops with it). The template ROM is built
twice with two different sentinel values; the bytes that differ between the two ROMs are where that constant lives, so the plugin can
write the patched song's own loop length there (BrokenDev/tools/music/build-musictest.ps1).
"""
import re
import sys


def main():
    path, frames = sys.argv[1], int(sys.argv[2])
    if not 1 <= frames <= 0xFFFF:
        raise SystemExit(f'frames must be 1..65535: {frames}')
    text = open(path, encoding='utf-8').read()
    new, count = re.subn(r'(ct UU\[1\] MUSIC_LOOP_FRAMES = UU\[1\]\()\d+(\))', lambda m: m.group(1) + str(frames) + m.group(2), text)
    if count != 1:
        raise SystemExit(f'{path}: expected one single-song MUSIC_LOOP_FRAMES table, found {count}')
    open(path, 'w', encoding='utf-8', newline='\n').write(new)
    print(f'MUSIC_LOOP_FRAMES set to {frames}')


main()
