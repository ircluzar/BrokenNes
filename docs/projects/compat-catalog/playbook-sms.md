# Playbook: Sega Master System

Accuracy core: "Master System (stock)" ("Master System FM (stock)" for FM-sound Japanese games). Reference: Mesen 2.1.1, offset 0-1;
refstep crops Mesen's frame at y 24. P0 scores: "same" on Alex Kidd and Sonic. Not dry-run yet.

## Controls
`Up Down Left Right`, button 1 and button 2, and `Pause` (the console's pause button; some games use it as "start"). See the tool
reference for the play_step names. Conventions: 1 = attack / start, 2 = jump. Light Phaser, Paddle, Sports Pad: give-up rule 4.
3-D Glasses games alternate eye pictures: note it, not a bug.

## Starting a game
- No BIOS (no-firmware rule): the game starts at once; any SEGA logo is the game's own.
- Title: press 1 (5 frames). A few games want Pause or 2: try each once, then wait for the attract demo and retry.

## Progress RAM ($C000-$DFFF)
No verified addresses yet: 5-call search on the lives digit, else picture only.

## Findings
- The blank left 8-pixel column (VDP register 0 bit 5) is the game's choice.
- Sprite flicker past 8 per line: the reference flickers too.
- Known fuzz finding: YM2413 index out of range (Sonic 2 SMS, FM): if the session dies on an FM game, grade Broken, tags `sound crash`.

## T2 bridge notes (smoke test 2026-11-16, ZIRCON)
- play_step button names: `A` (= button 1), `B` (= button 2), `Pause`, `Reset`, d-pad; the GG also has `Start`. `Start` on an SMS game is refused.
- The bridge server cannot open `.7z`: the T2 list (`cc_t2_fleet/games_flat_smsgg.json`) points at unpacked, sha1-checked copies; the archive is in `archive`.
- The reference picture is cropped from Mesen's 256x240 (`diff.cropped`); on the GG a step cropped at "x 0, y 0" compared the wrong window: ignore it.
