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

## T2 bridge notes (smoke test 2026-11-16, CORAL, server C:/brokennes/mcp3)
- play_step buttons: `A B Select Start Up Down Left Right`. Super Mario Land: title -> Start (10 frames) -> 1-1 at once; a second Start pauses.
- Mesen's GB/GBC picture is 160x144 with no border (Gameboy model, not SGB): BrokenNes and the reference compare 1:1, no `diff.cropped`.
- The compare equalises each picture's brightness (histogram) on GB/GBC, and Mesen's GBC colour correction and frame blending are off in the
  reference: BrokenNes's green DMG shades vs Mesen's grey, and GBC colours, score 0 when the picture is the same (SML title and 1-1, Link's
  Awakening DX name screen: score 0.000 every step). A steady small score is now a real difference: look at the compare pictures.
- Mesen's lead is about 60 frames on SML, 13 on Link's Awakening DX: `align_frames 240` finds it (old "~80 frames, align_search 90" note above
  is from the T1 harness).
- The T2 list (`cc_t2_fleet/games_flat_gbgbc.json`) points at unpacked, sha1-checked copies of zipped ROMs (`roms_gbgbc/<sha1>.gb|.gbc`); the zip
  is in `archive`.

