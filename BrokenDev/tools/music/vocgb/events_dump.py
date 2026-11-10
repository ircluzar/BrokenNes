"""The game's own converter on one events file (FruityHost flp-test `vocgb-python`): python events_dump.py <VRUN_VOC_dev tools/music> <song.vocsong.json>
prints {"streams": [hex x4], "instruments": [hex], "waves": [hex], "noise": [int]} as the game's back end (vocmusic.Music.encode) makes them for that song alone."""
import json
import sys

sys.path.insert(0, sys.argv[1])
import vocmusic as vm  # noqa: E402

song = vm.events_song(sys.argv[2], "song", "")
m = vm.Music()
m.add(song)
print(json.dumps({"streams": [d.hex() for d in m.songs[0][1]], "instruments": [bytes(r).hex() for r in m.insts],
                  "waves": [w.hex() for w in m.waves], "noise": m.noise}))
