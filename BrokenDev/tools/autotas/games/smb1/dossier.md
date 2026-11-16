# SMB1 dossier (start here before any SMB1 AutoTAS chapter)

Addresses are in dossier.json. Everything is UNVERIFIED until the emulator confirms it; verify and set `verified: true` with a note.
Source: Data Crystal RAM map (summarized); the wiki table is the authority if a value disagrees.

## Controls
D-pad move/crouch (Down on pipes); A jump (hold for height); B run/fireball (hold B to run faster).

## Game flow
Power-on -> title -> Start (1 player) -> 1-1 gameplay. Timer start value not from dc: check in the emulator.
No map screen: levels are chained directly. Pipes and warp zones exist.

## Detect state
- Normal play: `[$0770]==1` (dc: 01 = normal play). Demo/title: `$0770 == 0`.
- Death: `[$000E]==6` (player dies) or `[$000E]==11` (0B, dying). Lives `[$075A]` drop after. Game over: `[$0770]==3`.
- Pit fall: `[$00B5] > 1` (rises to 5 when falling down a pit). Fall death sets `[$0712]`.
- In air: `[$001D] != 0` (1 jump, 2 walk-off). Landed: `[$001D]==0`.
- Level end: flagpole slide `[$001D]==3`; `[$00FC]==20` (level-end music). Castle/axe handled by the level's end sequence.
- World/level change: `[$075F]`/`[$0760]` change (base unknown; check).

## Things that trip agents
- Level X = [$006D]*256+[$0086]; $0086 alone is screen-relative.
- Score is BCD across six bytes starting at $07DD (not /10 like SMB3).
- $075E "coins" may be wrong (dc also says it is set on the prelevel screen).
- $07F9 middle timer digit is assumed; only $07F8 and $07FA are listed.
- Warp zones: a 1-2 warp pipe to worlds 2-4 is a known route option. Question for the user: is the 4-2 warp allowed by the AutoTAS rules, or should it be off? No glitch warps (quality over speed).
- Do not write $0772, $0770 (except macros that choose a mode), $0787 in routes.

## Verified by the AutoTAS run (2026-11-16, World 1 complete)
- Boot: Start at frame 31-37 all give control at frame 194 (frame rule). Lives byte starts at 2.
- Stage byte is $075C (0-based); $0760 is the AREA (changes at pipes). Goal = `[$075C] == N && [$0772] == 3`.
- The level end (flag + tally + castle) takes ~820 frames of no input; runner.json `end_expr` plays it out.
- 1-2: a koopa under a low ceiling column at x~850: stand back (x~845) and wait ~65 frames, then hop on it.
- 1-3: the climb past x~1063 is two jumps (middle treetop ~64 px up, then the tall one).
- 1-4: the axe ends play mid-air: search for `[$0770] == 2`, not a landed x.
- Route: no warp zone used (World 1 level by level).
