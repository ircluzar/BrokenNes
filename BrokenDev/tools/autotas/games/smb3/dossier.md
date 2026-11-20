# SMB3 dossier (start here before any SMB3 TAS chapter)

Variables with addresses, formats and priorities are in dossier.json (priority 1 = left AutoTAS panel).
Verified on BrokenNes FIX cores with the US PRG1 dump unless marked otherwise.

## Controls
D-pad move/duck; A jump (hold for height); B run/fireball/tail; hold B while running on the ground to fill
the P-meter ($03DD, 6 arrows then P). Raccoon/Tanooki with full P: tap A to fly. Start pauses in levels;
on the map A enters the level under Mario, B opens the item box.

## Game flow
Power-on -> title -> Start (1 player) -> Start -> intro walk -> World 1 map ready at frame ~598
(`[$070A]==0 && [$0729]==$0D`). The map needs a couple of directional presses after the intro.
From START, Right moves Mario to the first node, Up reaches 1-1, then A enters.
In a level `$070A` != 0 and `$0729` = $10; timer `$05EE` (3 digits) starts at 300.
Clearing a level returns to the map and sets a bit in the completion table $7D00-$7D3F.

## World 1 structure
1-1, 1-2, 1-3, fortress (Boom-Boom; clearing removes a lock), 1-4 (auto-scroll), 1-5, 1-6 (auto-scroll),
then the airship (Larry Koopa). Toad houses and the spade panel are optional.
World change expected at `$0727` 0 -> 1 (unverified).

## Detect progress
- Back on map: `[$070A]==0 && [$0729]==$0D`.
- Level clear: on map and completion popcount one higher than at chapter start (1-1 = $7D04 bit 7).
- In-level progress: X = [$0075]*256+[$0090]; stuck if it does not grow for 3 s (except auto-scrollers).
- Death: lives `$0736` drops.

## Known tricks / route notes (World 1)
- Run (B) everywhere; a leaf plus full P-meter lets Mario fly over hazards and take sky shortcuts.
- 1-1 has a Super Leaf early: keep raccoon through World 1 if possible.
- Goal card box: jump into it; at high speed you can overshoot it.
- Fortress: Boom-Boom takes 3 stomps; watch Thwomps and Dry Bones.
- Airship: auto-scroll; Larry takes 3 stomps; avoid his wand rings.
- Public TASes use wrong-warp/clipping glitches: NOT for AutoTAS (quality over speed, no glitches).

## Things that trip agents
- Map presses must be spaced (~8 frames held, ~50 apart); input is ignored while Mario walks between nodes.
- On the map $0075 is map position, not level X.
- $00ED is only the in-level form; the map copy is $0746 (clean-up writes $0746).
- Score is stored /10 in two places ($0715 and banked $7D9F).
- P-meter reads 0 while airborne: read it on the ground.
- Power-up sprite on the map and card slots only refresh on the next level entry after RAM writes.
- Hand-edited inventory (e.g. P-wing) is research only, never a TAS route.

## Level kinds by $070A (mode) on entry
$070A is the level's TILESET, not its number: several levels share one (4 = sky: 1-4 AND 1-6; 2 = fortress; 1/3/14 = plains, hills, other). 7, 15, 16 = Toad house / bonus panel rooms (not levels). Cleared levels cannot be entered again, so `nodes` lists only levels still to play: take the one the chapter asks for (World 1 order) - usually the nearest.
