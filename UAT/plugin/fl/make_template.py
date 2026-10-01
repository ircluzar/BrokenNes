"""
make_template.py: derive an FL Studio test project for BrokenNes2 from one of FL's own project templates.

    python make_template.py --fixture fixture.json --state state.bin --out BrokenNes2_FLTest.flp
           [--base "<FL>\\Data\\Templates\\Utility\\Vocoder\\Vocoder.flp"] [--route-to 0]

FL ships Vocoder.flp (authored by the installed FL version: native generators, patterns, an empty playlist). The
project is changed in five places and nothing else:
  1. the first generator channel becomes "BrokenNes2" (plugin name and channel name);
  2. its saved plugin state is replaced by BrokenNes2's default state (state.bin, from `FruityHost state`);
  3. the channel is routed to the given mixer insert (default 0 = master, so no effect colours the audio);
  4. pattern 1 gets the fixture's notes: normal notes, slide-flagged notes for each slide of a chain, note colours;
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


def notes_blob(fixture: dict, ppq: int, rack: int) -> bytes:
    recs = []
    for n in fixture["notes"]:
        start = float(n["startBeat"])
        length = float(n["lengthBeats"])
        key = int(round(float(n["key"])))
        colour = int(n.get("color", 0))
        vel = max(1, min(127, int(round(float(n.get("velocity", 0.78)) * 127))))
        slides = n.get("slides") or []
        first_len = float(slides[0]["startBeat"]) if slides else length
        recs.append((round(start * ppq), pack_note(round(start * ppq), round(first_len * ppq), key, rack, colour, vel)))
        for seg in slides:
            s0 = round((start + float(seg["startBeat"])) * ppq)
            recs.append((s0, pack_note(s0, round(float(seg["lengthBeats"]) * ppq), int(round(float(seg["toKey"]))), rack, colour, vel, slide=True)))
    recs.sort(key=lambda r: r[0])
    return b"".join(r[1] for r in recs)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default=DEFAULT_BASE)
    ap.add_argument("--fixture", required=True)
    ap.add_argument("--state", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--plugin-name", default="BrokenNes2")
    ap.add_argument("--route-to", type=int, default=0)
    a = ap.parse_args()

    fixture = json.load(open(a.fixture, encoding="utf-8"))
    state = open(a.state, "rb").read()
    flp = Flp(a.base)
    ppq = flp.ppq

    # 1-3: the channel
    lo, hi = flp.channel_range(0)
    hi = flp.find(64, lo + 1) if any(e.id == 64 for e in flp.events[lo + 1:]) else len(flp.events)
    i201 = flp.find(201, lo, hi)
    if flp.events[flp.find(21, lo, hi)].data != b"\x02":
        raise SystemExit("channel 0 of the base project is not a native generator")
    was = flp.events[i201].text()
    flp.events[i201] = Event(201, utf16(a.plugin_name))
    flp.events[flp.find(203, lo, hi)] = Event(203, utf16(a.plugin_name + " FL test"))
    flp.events[flp.find(213, lo, hi)] = Event(213, state)
    flp.events[flp.find(143, lo, hi)] = Event(143, struct.pack("<i", a.route_to))

    # 5: tempo and playlist
    flp.events[flp.find(156)] = Event(156, struct.pack("<I", int(round(float(fixture["tempo"]) * 1000))))
    bar = ppq * 4
    clip_ticks = math.ceil(float(fixture["lengthBeats"]) * ppq / bar) * bar
    flp.events[flp.find(233)] = Event(233, pack_clip(1, 0, clip_ticks))

    # 4: the notes of pattern 1
    i65 = next(i for i, e in enumerate(flp.events) if e.id == 65 and e.data == b"\x01\x00" and flp.events[i + 1].id == 224)
    flp.events[i65 + 1] = Event(224, notes_blob(fixture, ppq, rack=0))

    flp.save(a.out)
    n_slides = sum(len(n.get("slides") or []) for n in fixture["notes"])
    print(f"wrote {a.out}: channel 0 '{was}' -> '{a.plugin_name}', {len(fixture['notes'])} notes + {n_slides} slide notes, "
          f"tempo {fixture['tempo']}, clip {clip_ticks} ticks, routed to insert {a.route_to}")


if __name__ == "__main__":
    main()
