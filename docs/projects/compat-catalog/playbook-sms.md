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
