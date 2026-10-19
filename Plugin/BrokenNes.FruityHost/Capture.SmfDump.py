# Round 9 (capture): an independent reader of Standard MIDI Files, written apart from the C# writer (Plugin/BrokenNes2.Plugin/Capture/CaptureSong.cs) from the SMF 1.0 specification, used by
# `FruityHost capture-test` and `selftest --only capture` to read back what a capture wrote, event by event. Strict: a chunk that lies about its length, an event cut short, a data byte with its top
# bit set, a track without its end-of-track event, a note-off with no note, a key struck again before its release, a note never released, are errors (exit 1 with the reason). Prints:
#   format F tracks N ppq P
#   tempo TICK US                     (every Set Tempo)
#   timesig TICK NUM DENPOW           (every time signature)
#   track I name NAME end TICK        (every track; END = the end-of-track event's tick)
#   rpn I CH RANGE TICK               (a pitch-bend range set with RPN 0: CC 101 = 0, CC 100 = 0, then CC 6 = RANGE; channels 1-based)
#   note I CH KEY VEL ON OFF          (every note, in the order they start)
#   bend I CH TICK VALUE              (every pitch bend, VALUE -8192..8191)
# Standard library only.
import sys


def fail(why):
    print("ERROR: " + why)
    sys.exit(1)


def vlq(b, p, end):
    v = 0
    for _ in range(4):
        if p >= end:
            fail("a variable-length number runs past the end of its track")
        c = b[p]
        p += 1
        v = (v << 7) | (c & 0x7F)
        if not c & 0x80:
            return v, p
    fail("a variable-length number longer than 4 bytes")


def main(path):
    b = open(path, "rb").read()
    if len(b) < 14 or b[0:4] != b"MThd":
        fail("no MThd header")
    if int.from_bytes(b[4:8], "big") != 6:
        fail("the header is not 6 bytes long")
    fmt = int.from_bytes(b[8:10], "big")
    ntrk = int.from_bytes(b[10:12], "big")
    div = int.from_bytes(b[12:14], "big")
    if fmt not in (0, 1) or div & 0x8000 or div == 0:
        fail("format %d / division %d not supported" % (fmt, div))
    out = ["format %d tracks %d ppq %d" % (fmt, ntrk, div)]
    p = 14
    for ti in range(ntrk):
        if b[p:p + 4] != b"MTrk":
            fail("track %d: no MTrk chunk at offset %d" % (ti, p))
        tlen = int.from_bytes(b[p + 4:p + 8], "big")
        p += 8
        end = p + tlen
        if end > len(b):
            fail("track %d: its length runs past the end of the file" % ti)
        tick = 0
        status = 0
        name = ""
        ended = None
        opened = {}
        notes = []
        rpn = {}
        while p < end:
            if ended is not None:
                fail("track %d: events after the end-of-track event" % ti)
            d, p = vlq(b, p, end)
            tick += d
            if p >= end:
                fail("track %d: an event cut short" % ti)
            c = b[p]
            if c & 0x80:
                p += 1
                if c < 0xF0:
                    status = c
            else:
                if status == 0:
                    fail("track %d: a data byte with no running status" % ti)
                c = status
            if c == 0xFF:
                if p >= end:
                    fail("track %d: a meta event cut short" % ti)
                mtype = b[p]
                p += 1
                ln, p = vlq(b, p, end)
                if p + ln > end:
                    fail("track %d: a meta event longer than its track" % ti)
                data = b[p:p + ln]
                p += ln
                if mtype == 0x2F:
                    ended = tick
                elif mtype == 0x03:
                    name = data.decode("utf-8")
                elif mtype == 0x51:
                    if ln != 3:
                        fail("track %d: a tempo event of %d bytes" % (ti, ln))
                    out.append("tempo %d %d" % (tick, int.from_bytes(data, "big")))
                elif mtype == 0x58:
                    if ln != 4:
                        fail("track %d: a time signature of %d bytes" % (ti, ln))
                    out.append("timesig %d %d %d" % (tick, data[0], data[1]))
            elif c in (0xF0, 0xF7):
                ln, p = vlq(b, p, end)
                p += ln
            else:
                cmd = c & 0xF0
                ch = c & 0x0F
                n = 1 if cmd in (0xC0, 0xD0) else 2
                if p + n > end:
                    fail("track %d: a channel event cut short" % ti)
                d1 = b[p]
                d2 = b[p + 1] if n == 2 else 0
                if d1 & 0x80 or d2 & 0x80:
                    fail("track %d: a data byte with its top bit set" % ti)
                p += n
                if cmd == 0x90 and d2 > 0:
                    if (ch, d1) in opened:
                        fail("track %d: key %d struck again on channel %d before its release (tick %d)" % (ti, d1, ch + 1, tick))
                    opened[(ch, d1)] = (tick, d2, len(notes))
                    notes.append(None)
                elif cmd == 0x80 or (cmd == 0x90 and d2 == 0):
                    if (ch, d1) not in opened:
                        fail("track %d: a note-off of key %d on channel %d with no note (tick %d)" % (ti, d1, ch + 1, tick))
                    on, vel, k = opened.pop((ch, d1))
                    if tick < on:
                        fail("track %d: a note ends before it starts" % ti)
                    notes[k] = "note %d %d %d %d %d %d" % (ti, ch + 1, d1, vel, on, tick)
                elif cmd == 0xB0:
                    if d1 in (100, 101):
                        rpn.setdefault(ch, [127, 127])[0 if d1 == 101 else 1] = d2
                    elif d1 == 6 and rpn.get(ch) == [0, 0]:
                        out.append("rpn %d %d %d %d" % (ti, ch + 1, d2, tick))
                elif cmd == 0xE0:
                    out.append("bend %d %d %d %d" % (ti, ch + 1, tick, (d1 | d2 << 7) - 8192))
        if ended is None:
            fail("track %d: no end-of-track event" % ti)
        if p != end:
            fail("track %d: its events end at %d, its length says %d" % (ti, p, end))
        if opened:
            fail("track %d: %d notes never released" % (ti, len(opened)))
        out.append("track %d name %s end %d" % (ti, name, ended))
        out.extend(notes)
    if p != len(b):
        fail("%d bytes after the last track" % (len(b) - p))
    print("\n".join(out))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        fail("usage: Capture.SmfDump.py file.mid")
    main(sys.argv[1])
