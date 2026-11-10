"""Regenerates a VRUN tree's src/game/music_decoders.fab as VRUN's own build_music.py would (its gen_music_decoders.py
templates, the bend / duty decoders where the converted streams in music_data.fab need them), plus the bend AND duty
decoders on every tonal channel of one song (the .flp template's song: any song can be patched over it). Pure Python:
it reads the already converted music_data.fab and never runs the converter.

    python regen_decoders.py <VRUN tree> <song>

Checks that the regeneration without the extra song is byte-identical to the tree's own music_decoders.fab (so nothing
else changes), then writes the file with the extra song's bend + duty decoders.
"""
import sys
from pathlib import Path

tree, song = Path(sys.argv[1]), sys.argv[2]
audio = tree / "engine" / "audio"
sys.path.insert(0, str(audio))
import build_music as bm  # noqa: E402
gen = bm.gen

game = tree / "src" / "game"
roster = game / "music_roster.json"
data = game / "music_data.fab"
dec = game / "music_decoders.fab"
_, tracks = gen.load_roster(roster)
if not any(t["name"] == song for t in tracks):
    raise SystemExit(f"no track {song!r} in {roster}")
hint = f"python engine/audio/build_music.py {roster.name}"
bends = bm.bend_decoder_channels(data, tracks)
duty = bm.duty_decoder_channels(data, tracks) if hasattr(bm, "duty_decoder_channels") else {}
same = gen.generate_decoders(tracks, hint, bends, duty) if duty else gen.generate_decoders(tracks, hint, bends)
if same != dec.read_text(encoding="utf-8"):
    raise SystemExit(f"the regenerated decoders differ from {dec} (the tree's decoders are not what its roster makes)")
bends[song] = set(gen.BEND_CHANNELS)
if not hasattr(gen, "DUTY_CHANNELS"):
    raise SystemExit("this VRUN tree has no pulse-duty engine (older than a36dd5e)")
duty[song] = set(gen.DUTY_CHANNELS)
dec.write_text(gen.generate_decoders(tracks, hint, bends, duty), encoding="utf-8", newline="\n")
print(f"ok    {dec}: {song} has bend decoders on {', '.join(gen.BEND_CHANNELS)} and duty decoders on {', '.join(gen.DUTY_CHANNELS)}")
