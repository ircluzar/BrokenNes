"""Decoder model of VRUN's music engine, checked against a BrokenNes --trace.

What this is: the music ROM plays four opcode streams (one per NES channel), one opcode per channel per frame at most
(engine/audio/gen_music_decoders.py, the templates). The streams are baked into music_data.fab by the converter. This tool
re-implements the four decoders in about 60 lines, predicts the APU state for every frame (period, volume, triangle linear
counter, noise period), and compares that with the APU columns of a Workshop --trace of the ROM actually running on BrokenNes.

What agreement proves: the emulated replay ROM plays the streams the converter made, frame for frame (and the loop point).
What it does NOT prove: that the converter's streams are the right translation of the MIDI (that is the converter's own
verification: byte-identical against the Python original on 1,674 files), nor how it sounds.

usage:
  python -I decoder_model.py <music_data.fab> <trace> --song NAME [--scan-start 0:120 | --start N] [--frames 560]
                             [--min-field 0.999] [--corrupt N] [--other-fab F] [--json out.json]
exit code: 0 every field agrees at least --min-field, 1 below, 2 usage.
Negative controls (the verdict must turn to 1): --corrupt N flips the low bit of the first N note-on opcodes of the MODEL's
square1 stream (one semitone each); --other-fab F compares against the streams of a different song from file F.
"""
import json
import re
import sys

NOISE_CYCLES = [4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068]
CHANNELS = ('square1', 'square2', 'triangle', 'noise')
# The first frame of the decode is not compared. The ROM starts the track in that frame, and its start-up writes and the first
# decode straddle the trace's sample point: in 12 of 12 songs the only disagreement left is on that one frame (the noise and
# triangle registers still show the state before the track, a square's volume shows the engine's initial 15). Every later frame,
# including the loop point, is compared exactly.
SKIP = 1


def song_names(path):
    text = open(path, encoding='utf-8').read()
    names = []
    for m in re.finditer(r'\[\]\s+music_(\w+?)_(?:square1|square2|triangle|noise)\s*\n', text):
        if m.group(1) not in names:
            names.append(m.group(1))
    return names


def load_fab(path, song):
    text = open(path, encoding='utf-8').read()
    plo = [int(v) for v in re.search(r'PERIOD_LO\s*=\s*U\[128\]\(([^)]*)\)', text).group(1).split(',')]
    phi = [int(v) for v in re.search(r'PERIOD_HI\s*=\s*U\[128\]\(([^)]*)\)', text).group(1).split(',')]
    streams = {}
    for m in re.finditer(r'\[\]\s+music_(\w+?)_(square1|square2|triangle|noise)\s*\n\s*U\[(\d+)\]\(([^)]*)\)', text):
        if m.group(1) == song:
            streams[m.group(2)] = bytearray(int(v) for v in m.group(4).split(','))
    missing = [c for c in CHANNELS if c not in streams]
    if missing:
        raise SystemExit(f'song {song!r}: no stream for {missing} in {path}')
    return plo, phi, streams


def run(stream, kind, frames, plo, phi, init):
    """Predicted APU state after each frame's decode (the templates' wait / one-opcode / loop rules)."""
    ptr = wait = 0
    vol = 15                       # mus_sq1_vol / mus_sq2_vol start at 15 (engine/audio/audio_engine.fab)
    st = dict(init)
    out = []
    for _ in range(frames):
        if wait > 0:
            wait -= 1
        if wait == 0:
            op = stream[ptr]
            if op < 0x80:                                  # note on
                if kind == 'square':
                    st['vol'] = vol
                    st['per'] = ((phi[op] & 7) << 8) | plo[op]
                elif kind == 'triangle':
                    st['lin'] = 0x7F
                    st['per'] = ((phi[op] & 7) << 8) | plo[op]
                else:
                    st['vol'] = 15
                    st['nper'] = NOISE_CYCLES[op & 0x0F]
                ptr += 1
            elif op == 0x80:                               # note off
                if kind == 'triangle':
                    st['lin'] = 0
                else:
                    st['vol'] = 0
                ptr += 1
            elif op < 0xC0:                                # wait 1..63 frames
                wait = op - 0x80
                ptr += 1
            elif op < 0xD0:                                # volume (squares only; ignored by triangle and noise)
                if kind == 'square':
                    vol = op & 0x0F
                    st['vol'] = vol
                ptr += 1
            elif op == 0xD0:                               # long wait
                wait = stream[ptr + 1]
                ptr += 2
            else:                                          # 0xFF loop
                ptr = 0
        out.append(dict(st))
    return out


def read_trace(path):
    cols = None
    rows = []
    for line in open(path, encoding='utf-8'):
        if line.startswith('# columns:'):
            cols = line[len('# columns:'):].strip().split('|')
        elif not line.startswith('#'):
            rows.append(line.rstrip('\n').split('|'))
    if cols is None:
        raise SystemExit(f'{path}: no "# columns:" header (is it a Workshop --trace file?)')
    return {c: i for i, c in enumerate(cols)}, rows


def score(plo, phi, streams, ix, rows, start, nframes):
    nframes = min(nframes, len(rows) - start)
    fields = [
        ('square1', 'square', 'per', 'p1per'), ('square1', 'square', 'vol', 'p1vol'),
        ('square2', 'square', 'per', 'p2per'), ('square2', 'square', 'vol', 'p2vol'),
        ('triangle', 'triangle', 'per', 'tper'), ('triangle', 'triangle', 'lin', 'tlin'),
        ('noise', 'noise', 'nper', 'nper'), ('noise', 'noise', 'vol', 'nvol'),
    ]
    preds = {}
    result = []
    for ch, kind, key, col in fields:
        if ch not in preds:
            # The model starts in the state the registers were in just before the decode began (read from the trace, one frame
            # earlier): what the ROM's own start-up left there (volume f, noise period 4, ...) is not something the streams say,
            # and a channel whose stream never writes a register (an empty noise track) keeps that value for the whole song.
            before = start - 1
            init = dict(per=0, vol=0, lin=0, nper=0)
            if before >= 0:
                g = lambda c: int(rows[before][ix[c]], 16)
                if kind == 'square':
                    init.update(per=g('p1per' if ch == 'square1' else 'p2per'), vol=g('p1vol' if ch == 'square1' else 'p2vol'))
                elif kind == 'triangle':
                    init.update(per=g('tper'), lin=0x7F if g('tlin') else 0)
                else:
                    init.update(nper=g('nper'), vol=g('nvol'))
            preds[ch] = run(streams[ch], kind, nframes, plo, phi, init)
        bad = []
        for k in range(SKIP, nframes):
            o = int(rows[start + k][ix[col]], 16)
            p = preds[ch][k][key]
            if key == 'lin':
                # The triangle linear counter only reloads on the next quarter-frame clock (4 per frame) after the $4008 write,
                # and the trace samples once per frame: the observed value can lag the write by one frame. Accept the model's
                # value one frame either side. (The pitch, tper, has no such slack and is compared exactly.)
                near = {preds[ch][j][key] for j in (k - 1, k, k + 1) if 0 <= j < nframes}
                if o in near:
                    continue
            elif p == o:
                continue
            bad.append((start + k, p, o))
        result.append(dict(channel=ch, column=col, agree=nframes - SKIP - len(bad), frames=nframes - SKIP, mismatches=bad[:6]))
    return result


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    fab, trace = sys.argv[1], sys.argv[2]
    opts = dict(zip(sys.argv[3::2], sys.argv[4::2]))
    song = opts.get('--song')
    if not song:
        print('--song is required')
        return 2
    nframes = int(opts.get('--frames', 560))
    min_field = float(opts.get('--min-field', 0.999))
    plo, phi, streams = load_fab(fab, song)
    if '--other-fab' in opts:
        # Negative control: swap in the streams of another song (the first one in that file whose streams differ from this one's).
        for other in song_names(opts['--other-fab']):
            plo2, phi2, st2 = load_fab(opts['--other-fab'], other)
            if any(bytes(st2[c]) != bytes(streams[c]) for c in CHANNELS):
                print(f'(control) using the streams of {other!r} from {opts["--other-fab"]}')
                plo, phi, streams = plo2, phi2, st2
                break
        else:
            print('--other-fab: no song there differs from the one under test')
            return 2
    if '--corrupt' in opts:
        # Negative control: flip the low bit of the first N note-on opcodes of the MODEL's square1 stream (one semitone each).
        left = int(opts['--corrupt'])
        s1, i = streams['square1'], 0
        while i < len(s1) and left > 0:
            op = s1[i]
            if op < 0x80:
                s1[i] ^= 1
                left -= 1
                i += 1
            elif op == 0xD0:
                i += 2
            elif op == 0xFF:
                break
            else:
                i += 1
    ix, rows = read_trace(trace)

    if '--start' in opts:
        starts = [int(opts['--start'])]
    else:
        lo, hi = (int(v) for v in opts.get('--scan-start', '0:120').split(':'))
        starts = list(range(lo, hi + 1))
    best = None
    for s in starts:
        if s + 1 >= len(rows):
            break
        res = score(plo, phi, streams, ix, rows, s, nframes)
        tot = sum(r['agree'] for r in res)
        if best is None or tot > best[0]:
            best = (tot, s, res)
    tot, start, res = best
    n = sum(r['frames'] for r in res)
    worst = min(r['agree'] / r['frames'] for r in res)
    for r in res:
        extra = ''
        if r['mismatches']:
            extra = '   first mismatches (frame:model/trace): ' + ', '.join(f"{f}:{p:x}/{o:x}" for f, p, o in r['mismatches'])
        print(f"{r['channel']:9s} {r['column']:5s} agree {r['agree']}/{r['frames']}{extra}")
    print(f'TOTAL {tot}/{n} = {tot / n:.4f}  worst field {worst:.4f}  (song {song}, decode begins at trace frame {start})')
    verdict = worst >= min_field
    print('MODEL MATCHES THE EMULATED ROM' if verdict else f'MODEL DOES NOT MATCH (a field is below {min_field})')
    if '--json' in opts:
        json.dump(dict(song=song, start=start, total=tot, of=n, worst=worst, min_field=min_field, match=verdict,
                       fields=[{k: v for k, v in r.items() if k != 'mismatches'} for r in res]),
                  open(opts['--json'], 'w'), indent=1)
    return 0 if verdict else 1


if __name__ == '__main__':
    sys.exit(main())
