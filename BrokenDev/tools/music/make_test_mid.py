"""Write a small, deterministic Standard MIDI File (format 1, 480 ticks per beat, 120 BPM) that stands in for what FL Studio's
"export MIDI" would produce: a lead on channel 1, a bass on channel 2 and drums on channel 10, 16 beats long.

usage: python -I make_test_mid.py <out.mid>
"""
import struct
import sys

TPQ = 480


def vlq(n):
    out = [n & 0x7F]
    n >>= 7
    while n:
        out.append((n & 0x7F) | 0x80)
        n >>= 7
    return bytes(reversed(out))


def track(events, end_tick):
    """events: list of (tick, bytes); note-offs sort before note-ons at the same tick."""
    events = sorted(events, key=lambda e: (e[0], 0 if (e[1][0] & 0xF0) == 0x80 else 1))
    data = bytearray()
    last = 0
    for tick, msg in events:
        data += vlq(tick - last) + msg
        last = tick
    data += vlq(end_tick - last) + b'\xFF\x2F\x00'
    return b'MTrk' + struct.pack('>I', len(data)) + bytes(data)


def notes(channel, seq, vel=100, at=0):
    ev = []
    t = at
    for note, length in seq:
        ev.append((t, bytes([0x90 | channel, note, vel])))
        ev.append((t + length, bytes([0x80 | channel, note, 0])))
        t += length
    return ev


def main():
    end = TPQ * 16
    tempo = track([(0, b'\xFF\x51\x03' + (500000).to_bytes(3, 'big'))], end)
    lead = track(notes(0, [(60, TPQ), (64, TPQ), (67, TPQ), (72, TPQ)] * 4), end)
    bass = track(notes(1, [(48, TPQ * 2), (43, TPQ * 2)] * 4), end)
    drum_ev = []
    for beat in range(16):
        drum_ev += notes(9, [(36 if beat % 2 == 0 else 38, 120)], vel=110, at=beat * TPQ)
    drums = track(drum_ev, end)
    header = b'MThd' + struct.pack('>IHHH', 6, 1, 4, TPQ)
    open(sys.argv[1], 'wb').write(header + tempo + lead + bass + drums)
    print('wrote', sys.argv[1])


main()
