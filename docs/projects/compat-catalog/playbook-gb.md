# Playbook: Game Boy / Game Boy Color

Accuracy core: "Game Boy (stock)" (PPU_GB, APU_DMG). No boot ROM (no-firmware rule): the game starts at once. Reference: Mesen 2.1.1,
which runs **~80 frames ahead**: open with `align_frames=240` and `align_search 90`. Known open P0 finding: Super Mario Land differs steadily
at ~0.20 (fix-wave queue): a steady score the picture does not explain on a DMG game is that class: note it with the score, don't chase it.
Not dry-run yet.

## Controls
`Up Down Left Right A B Select Start`. A = jump / confirm, B = run / attack / cancel. Camera, Printer, link cable, infrared, tilt (MBC7):
give-up rule 4.

## Starting a game
- No logo scroll (no boot ROM): the game's own logo or title in 0-120 frames.
- Start on the title (5 frames). RPG intros: A through the text, NEW GAME, accept default names.
- A "for Game Boy Color only" screen means the console was detected wrong (the catalog opens GBC games as `gbc`): grade Not tested and say so.
- No Super Game Boy borders in the catalog run.

## Progress RAM ($C000-$DFFF work RAM, $FF80-$FFFE high RAM)
| Game | Watches (community, check first) |
|---|---|
| Pokemon Red / Blue | `map=[$D35E]`, `y=[$D361]`, `x=[$D362]` |
| Unknown | picture only, or a 5-call search on the lives digit |

## Steps and findings
- About 60 fps; 20-frame steps.
- Window / LCDC mid-frame status bars one line off = Jank `picture`.
- GBC: a different but consistent palette is colour correction, not a bug. HDMA garbage during transitions that the reference lacks = Jank.
