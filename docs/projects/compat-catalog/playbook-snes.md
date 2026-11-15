# Playbook: SNES / Super Famicom

Accuracy core: "SNES (stock)" (SFC family, open_rom's default). Reference: Mesen 2.1.1, offset 0-2; Mesen's 239-line picture is cropped
to lines 7-230 by refstep. Measured step cost with reference: ~500 ms (Super Mario World), ~930 ms (Donkey Kong Country).
Not dry-run yet: run this playbook on 3 SNES games before the SNES swarm.

## Controls
`Up Down Left Right A B X Y L R Select Start`. Conventions: B = jump / confirm in Nintendo games (A in many third-party ones), Y = run /
attack, X/A = menus, L/R = cycle items, Start = pause. Mouse and Super Scope games: give-up rule 4 (tag `input`).

## Starting a game
- Licensor logos and splash screens: 150-400 frames. Many intros run 1,000-3,000 frames: Start skips most of them.
- Press Start for 5 frames, wait 30, look. File-select screens (Super Mario World, Zelda ALttP, EarthBound, Chrono Trigger): pick the
  first empty slot, confirm, accept the default name (move to END / OK).
- Enhancement chips (SuperFX, SA-1, DSP-1..4, Cx4, S-DD1, SPC7110, ST010/011, OBC1, S-RTC) are supported; a chip game showing a black
  screen while the reference plays = Broken, tag `mapper`.

## Progress RAM (bus addresses 7E:xxxx; community addresses, check one against the picture first)
| Game / engine | Watches |
|---|---|
| Super Mario World | `lives=[7E:0DBE]`, `level=[7E:13BF]`, `x={7E:0094}`, `mode=[7E:0100]` (14 = in a level) |
| Zelda: A Link to the Past | `module=[7E:0010]` (7 = dungeon, 9 = overworld) |
| Super Metroid | `room={7E:079B}`, `energy={7E:09C2}`, `state=[7E:0998]` (8 = playing) |
| Donkey Kong Country | picture only (level card / map) |
| Unknown | `search_memory` on the lives digit after a death, at most 5 calls |

## Steps
- 20 frames per decision; SNES menus fade in 16-32 frames: wait 30 after every menu press.
- Mode 7 scenes (F-Zero, Pilotwings, Super Mario Kart): the score jumps while rotating; judge by whether it settles.
- Transparency / HDMA edges differ slightly from Mesen: steady scores up to ~0.05 are "same".

## Findings (SNES specifics)
- Hi-res (512 px) and interlace screens: compare with `scale=1`.
- An SPC700 hang shows as a frozen picture while the reference moves (the game waits on $2140-$2143): Broken, tags `sound crash`.
- SA-1 / SuperFX slowdown that the reference does not have: Jank, tag `timing`.
