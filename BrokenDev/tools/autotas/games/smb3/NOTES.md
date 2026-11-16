# SMB3 AutoTAS research notes (2026-11-15)

Done headless on BrokenNes (CPU/PPU/APU FIX), ROM = the user's PRG1 dump (not in git).

Verified by play: map/level byte $070A, map-ready $0729 (0 title, $0D map, $10 level), lives $0736,
score $0715 (3 bytes BE, x10) + backup $7D9F, coins $7DA2, cards $7D9C, inventory $7D80 (item 8 = P-wing),
map power-up $0746, level power-up $00ED, completions $7D00 (1-1 = $7D04 bit7).

Milestones verified: boot (frame 598 from power-on with Start,Start) and 1-1 clear.
Not yet: per-level bits for 1-2..1-6 and fortress, world change $0727 0->1 after the airship.
The first agent to clear each of these should record the new bit (diff $7D00-$7D3F) into milestones.json.
The generic rule (one more completion bit, back on the map) works without the per-level bits.

Clean-up recipe applied to the post-1-1 state and verified in 1-2 (see cleanup.json).
Research route used a P-wing via inventory writes; that is not a TAS route.
Gotchas: the map needs ~2 directional presses after the intro walk; card box only collects when touched while not
moving right fast enough to fly over it.
