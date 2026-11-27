# N64 library smoke catalog v2: TITLE -> INGAME (lane catalog_v2)

Input: the 188 games that catalog v1 classified as TITLE. That is 181 real games, plus 7 duplicate archives of certified
games, plus 2 Public Domain packs.
Harness: run_one2.sh / batch2.sh. Runs are serial (one emulator) and wait for more than 16 GB of free RAM before each run.
Settings: COR64_PIF_PAK=controller, COR64_VI_SPEEDUP=1, a 225 s deadline and a 240 s hard timeout. 10 PNGs per run, about
15 s apart. Classes were set by eye from contact sheets (analyze2.py --sheets). manual_v2.tsv holds pass 1, manual_v2b.tsv
holds pass 2.

Scripts:
- smoke2.txt (pass 1, all games). About 150 s: Start and A with pauses, B x2 to back out of pak screens, Down+A, then the
  stick held forward with A held, then with B, then with A+B.
- smoke2_name.txt (pass 2, 7 name-entry games): Start, a D-pad walk to END/OK, then A.
- smoke2_start.txt (pass 2, 33 select-loop games plus 2 black-screen games): no B, single A presses about 1.5 s apart, Start
  to confirm, Down+A for "No" prompts.

## Counts (188 v1-TITLE games)

| class_v2 | games |
|---|---|
| INGAME | 151 (143 after pass 1, +8 from pass 2) |
| TITLE | 32 |
| BLACK (worse than v1) | 2 |
| CRASH (worse than v1) | 1 |
| N/A (non-games) | 2 |

Pass 2 gains: Goemon Mononoke Sugoroku, ISS 2000, Mahjong Master, Penny Racers, Pokemon Puzzle League (tutorial),
Pro Mahjong Kiwame 64, Rally Challenge 2000, Super B-Daman.

## Remaining TITLE reasons (32)

| reason | n | games |
|---|---|---|
| select/menu loop | 15 | 64 Oozumou 2, Choro Q 64 II, Dezaemon 3D, Doom 64 (options), Jeopardy, J.League Perfect Striker, LEGO Racers, Monster Truck Madness, Pocket Monsters Stadium, Pokemon Stadium, Power League Baseball, WWF No Mercy, Toukon Road 2, WCW vs nWo World Tour, Mahjong 64 |
| name/profile entry (mostly JP) | 6 | Cruis'n Exotica, Getter Love, Harvest Moon 64, J.League Tactics Soccer, Super Robot Taisen 64, Jinsei Game 64 |
| Controller/Rumble Pak prompt loop | 4 | Deadly Arts, Rainbow Six, Snowboard Kids, Milo's Astro Lanes |
| long intro/dialogue | 3 | Bass Tsuri No.1, Space Station Silicon Valley, Zool |
| suspicious freeze | 2 | FIFA Soccer 64 (empty stadium), Robotech Crystal Dreams (frozen corridor; v1 logged cpu:tlb-exception) |
| reached play, then pause/quit menu | 1 | Asteroids Hyper 64 |
| too slow to get through within the deadline | 1 | Lode Runner 3D |

## Broken or suspicious (for a fix lane)

- Paper Mario (CRASH): reaches Goomba Village, then shows the game's crash screen "THREAD 4 TLB EXCEPTION ON LOAD".
  report.json logs cpu:tlb-exception va=E000E000 pc=E000E000, with no ERET back.
- Transformers Beast Wars Transmetal (BLACK): character select -> "Metal Warriors Reborn!" -> black for 2 minutes. Seen in both passes.
- Robot Ponkottsu 64 (BLACK): black after the save select in pass 1, black from boot in pass 2.
- FIFA Soccer 64 and Robotech: frozen (see above).
- Slow (realtime%, machine shared, so this understates speed): WCW Mayhem 19, SW Ep1 Racer 21, Virtual Pool 23,
  Top Gear Rally 2 25, 1080 28, Mahjong 64 28, Madden 64 29, Wipeout 29, Starshot 33, ASB2000 35, Lode Runner 35,
  Clay Fighter 38, FIFA 99 38, Quake II 38, Quake 39, Hybrid Heaven 40, ASB99 40, Tetrisphere 41, Morita Shougi 42,
  StarCraft 46, Snowboard Kids 46.
- Quake II gameplay is very dark. Hey You Pikachu needs the VRU microphone, which is not emulated.
