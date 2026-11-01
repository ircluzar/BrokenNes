"""
make_template.py: derive an FL Studio test project for BrokenNes2 from one of FL's own project templates.

    python make_template.py --fixture fixture.json --state-dir states --out BrokenNes2_FLTest.flp
           [--base "<FL>\\Data\\Templates\\Utility\\Vocoder\\Vocoder.flp"] [--route-to 0]

FL ships Vocoder.flp (authored by the installed FL version: native generators, patterns, an empty playlist). The
project is changed in five places and nothing else:
  1. the first generator channels become "BrokenNes2", one per NES channel the fixture uses (one instance = one channel);
  2. each one's saved plugin state is replaced by BrokenNes2's state for that channel (state0.bin..., from `FruityHost state --channel N`);
  3. the channels are routed to the given mixer insert (default 0 = master, so no effect colours the audio);
  4. pattern 1 gets the fixture's notes, each in the channel rack row of its instance: normal notes, and slide-flagged notes inside
     their parent for each slide of a chain;
  5. the playlist is a single clip of pattern 1 from bar 1, and the tempo is the fixture's.
All other bytes stay as FL wrote them; FL itself is the judge of whether the result loads (UAT/plugin/fl/fl-certify.ps1).
"""
import argparse
import json
import math
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from flplib import Event, Flp, pack_clip, pack_note, utf16  # noqa: E402

DEFAULT_BASE = r"C:\Program Files\Image-Line\FL Studio 2026\Data\Templates\Utility\Vocoder\Vocoder.flp"


def notes_blob(fixture: dict, ppq: int) -> bytes:
    """Pattern 1's notes. BrokenNes2 is one instance per NES channel: a note for channel k sits in the channel rack row of instance k."""
    recs = []
    for n in fixture["notes"]:
        start = float(n["startBeat"])
        length = float(n["lengthBeats"])
        key = int(round(float(n["key"])))
        rack = int(n.get("channel", 0)) & 3
        colour = 0
        vel = max(1, min(127, int(round(float(n.get("velocity", 0.78)) * 127))))
        slides = n.get("slides") or []
        recs.append((round(start * ppq), pack_note(round(start * ppq), round(length * ppq), key, rack, colour, vel)))
        for seg in slides:
            s0 = round((start + float(seg["startBeat"])) * ppq)
            recs.append((s0, pack_note(s0, round(float(seg["lengthBeats"]) * ppq), int(round(float(seg["toKey"]))), rack, colour, vel, slide=True)))
    recs.sort(key=lambda r: r[0])
    return b"".join(r[1] for r in recs)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default=DEFAULT_BASE)
    ap.add_argument("--fixture", required=True)
    ap.add_argument("--state-dir", required=True, help="a folder with state0.bin .. state3.bin: the plugin's saved state for each channel (FruityHost state --channel N)")
    ap.add_argument("--out", required=True)
    ap.add_argument("--plugin-name", default="BrokenNes2")
    ap.add_argument("--route-to", type=int, default=0)
    a = ap.parse_args()

    fixture = json.load(open(a.fixture, encoding="utf-8"))
    flp = Flp(a.base)
    ppq = flp.ppq

    # 1-3: one BrokenNes2 channel per NES channel the fixture uses (the base project has five generator channels: the first four are used)
    instances = 1 + max(int(n.get("channel", 0)) & 3 for n in fixture["notes"])
    was = []
    for k in range(instances):
        state = open(os.path.join(a.state_dir, f"state{k}.bin"), "rb").read()
        lo, hi = flp.channel_range(k)
        hi = flp.find(64, lo + 1) if any(e.id == 64 for e in flp.events[lo + 1:]) else len(flp.events)
        i201 = flp.find(201, lo, hi)
        if flp.events[flp.find(21, lo, hi)].data != b"\x02":
            raise SystemExit(f"channel {k} of the base project is not a native generator")
        was.append(flp.events[i201].text())
        flp.events[i201] = Event(201, utf16(a.plugin_name))
        flp.events[flp.find(203, lo, hi)] = Event(203, utf16(f"{a.plugin_name} ch{k + 1}"))
        flp.events[flp.find(213, lo, hi)] = Event(213, state)
        flp.events[flp.find(143, lo, hi)] = Event(143, struct.pack("<i", a.route_to))

    # the base project's mixer effects (Vocodex has its own carrier synth, which sounds with no input) would colour or
    # replace the audio: take the effect plugins out of the mixer slots. Each is 201 name .. 213 state, then its slot (98).
    first_insert = flp.find(204)
    dropped = 0
    k = first_insert
    while k < len(flp.events):
        if flp.events[k].id == 201:
            end = flp.find(213, k)
            end = flp.find(98, end) if flp.events[end + 1].id == 98 else end
            del flp.events[k:end + 1]
            dropped += 1
        else:
            k += 1

    # the base project has pattern 2 selected: a render plays the selected pattern, so select ours
    flp.events[flp.find(67)] = Event(67, struct.pack("<H", 1))

    # 5: tempo and playlist
    flp.events[flp.find(156)] = Event(156, struct.pack("<I", int(round(float(fixture["tempo"]) * 1000))))
    bar = ppq * 4
    clip_ticks = math.ceil(float(fixture["lengthBeats"]) * ppq / bar) * bar
    flp.events[flp.find(233)] = Event(233, pack_clip(1, 0, clip_ticks))

    # 4: the notes of pattern 1
    i65 = next(i for i, e in enumerate(flp.events) if e.id == 65 and e.data == b"\x01\x00" and flp.events[i + 1].id == 224)
    flp.events[i65 + 1] = Event(224, notes_blob(fixture, ppq))

    flp.save(a.out)
    n_slides = sum(len(n.get("slides") or []) for n in fixture["notes"])
    print(f"wrote {a.out}: {instances} channels '{', '.join(was)}' -> '{a.plugin_name}', {dropped} mixer effects removed, {len(fixture['notes'])} notes + {n_slides} slide notes, "
          f"tempo {fixture['tempo']}, clip {clip_ticks} ticks, routed to insert {a.route_to}")


if __name__ == "__main__":
    main()
