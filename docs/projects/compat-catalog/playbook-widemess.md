# Playbook: WideMess

WideMess is our own fantasy console (400x224, 8 layers, big SPC700; `docs/projects/widemess-plan.md`). It has **no commercial library and
no reference emulator** (the reference is left to the fix waves). The catalog covers it with our own games and test ROMs (`.wms`, under
`WideMess/testroms/`). Open with `open_rom` (console `wms`).

## Controls
SNES pad layout: `Up Down Left Right A B X Y L R Select Start`.

## Grading without a reference
- **Determinism instead of a reference**: record the play, `open_movie` it, require `inSync`. A desync = Broken, tag `timing`.
- **Self-checking test ROMs**: compare the picture hash with the golden suite's case for that ROM; a PASS/FAIL screen is read as is.
- Our games (the Super VRUN WideMess build once M8 exists): play the first room, note anything wrong; a debug HUD built into the game is
  fair to read.
- The best grade is OK, with "no reference" in the report.

## Progress RAM
Load the game's WLA-DX `.sym` with `symbols` on open_rom and read the named variables (lives, room) directly.
