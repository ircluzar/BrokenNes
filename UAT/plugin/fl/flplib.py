"""
flplib.py: just enough of the FL Studio project format (.flp) to derive a test project.

An FLP is a 'FLhd' chunk (format, channel count, PPQ) and a 'FLdt' chunk holding a flat stream of events:
    id byte, then data: id < 64 one byte, < 128 two bytes, < 192 four bytes, >= 192 a varint length and that many bytes.
Everything here edits by splicing whole events and fixing the FLdt length, leaving every other byte as FL wrote it
(FL rejects files re-serialized by writers that normalise the stream; byte-for-byte splicing is what flpkit
and this module do, and FL itself is the oracle that accepts or refuses the result).
"""
import struct
from dataclasses import dataclass


@dataclass
class Event:
    id: int
    data: bytes

    def encode(self) -> bytes:
        if self.id < 64:
            assert len(self.data) == 1
            return bytes([self.id]) + self.data
        if self.id < 128:
            assert len(self.data) == 2
            return bytes([self.id]) + self.data
        if self.id < 192:
            assert len(self.data) == 4
            return bytes([self.id]) + self.data
        n, out = len(self.data), bytearray()
        while True:
            b = n & 0x7F
            n >>= 7
            out.append(b | (0x80 if n else 0))
            if not n:
                break
        return bytes([self.id]) + bytes(out) + self.data

    def text(self) -> str | None:
        """UTF-16 (modern) or ASCII text payload, if it is one."""
        try:
            s = self.data.decode("utf-16-le").rstrip("\x00")
            if s and all(32 <= ord(c) < 0x2FFF for c in s):
                return s
        except UnicodeDecodeError:
            pass
        s = self.data.rstrip(b"\x00").decode("latin1")
        return s if s and all(32 <= ord(c) < 127 for c in s) else None


class Flp:
    def __init__(self, path: str):
        d = open(path, "rb").read()
        if d[:4] != b"FLhd":
            raise ValueError(f"{path}: not an FLP file")
        hl = struct.unpack("<I", d[4:8])[0]
        self.header = d[:8 + hl]
        self.format, self.channels, self.ppq = struct.unpack("<hHH", d[8:14])
        i = 8 + hl
        if d[i:i + 4] != b"FLdt":
            raise ValueError("no FLdt chunk")
        ln = struct.unpack("<I", d[i + 4:i + 8])[0]
        i += 8
        end = i + ln
        self.events: list[Event] = []
        while i < end:
            eid = d[i]
            i += 1
            if eid < 64:
                val = d[i:i + 1]; i += 1
            elif eid < 128:
                val = d[i:i + 2]; i += 2
            elif eid < 192:
                val = d[i:i + 4]; i += 4
            else:
                n = sh = 0
                while True:
                    b = d[i]; i += 1
                    n |= (b & 0x7F) << sh
                    sh += 7
                    if not b & 0x80:
                        break
                val = d[i:i + n]; i += n
            self.events.append(Event(eid, val))
        self.tail = d[end:]

    def save(self, path: str) -> None:
        body = b"".join(e.encode() for e in self.events)
        open(path, "wb").write(self.header + b"FLdt" + struct.pack("<I", len(body)) + body + self.tail)

    # ---- lookups ----
    def channel_range(self, index: int) -> tuple[int, int]:
        """Event index range [start, end) of channel <index>: from its ChannelNew (64) event to the next channel's, or to
        the first event that is neither channel data nor part of the channel block (arrangement, sample list...)."""
        starts = [i for i, e in enumerate(self.events) if e.id == 64]
        want = next(i for i in starts if struct.unpack("<H", self.events[i].data)[0] == index)
        nxt = [i for i in starts if i > want]
        return want, (nxt[0] if nxt else len(self.events))

    def find(self, eid: int, start: int = 0, end: int | None = None) -> int:
        end = len(self.events) if end is None else end
        for i in range(start, end):
            if self.events[i].id == eid:
                return i
        raise KeyError(f"no event {eid} in [{start}, {end})")


def utf16(text: str) -> bytes:
    return (text + "\x00").encode("utf-16-le")


NOTE = struct.Struct("<IHHIHHBBBBBBBB")   # position, flags, rack channel, length, key, group, fine, u1, release, colour, pan, velocity, modx, mody
FLAG_NOTE = 0x4000
FLAG_SLIDE = 0x4008


def pack_note(pos: int, length: int, key: int, rack: int, colour: int, velocity: int, slide: bool = False, pan: int = 64) -> bytes:
    return NOTE.pack(pos, FLAG_SLIDE if slide else FLAG_NOTE, rack, length, key, 0, 120, 0, 64, colour, pan, velocity, 128, 128)


PATTERN_BASE = 20480
PLAYLIST_TRACK_SPACE = 500


def pack_clip(pattern: int, start_ticks: int, length_ticks: int, track: int = 1) -> bytes:
    """A 32-byte playlist record for a pattern clip (layout measured from FL 2026's own projects: head, then the
    constant era bytes, then the clip's cut window start/end in ticks)."""
    head = struct.pack("<IHHIHH", start_ticks, PATTERN_BASE, PATTERN_BASE + pattern, length_ticks, PLAYLIST_TRACK_SPACE - track, 0)
    return head + bytes.fromhex("7800400040648080") + struct.pack("<II", 0, length_ticks)
