# AutoTAS pilot: Super Mario Bros. 3 (2026-11-15/16)

Driver: one Opus agent ran the pilot by hand: it fixed the tools, walked the map, judged results and
stepped in when a player got stuck. Players: Haiku 5.5 subagents at low effort, one chapter attempt each.
Plan: [autotas-plan.md](autotas-plan.md). Package: `T:\BrokenDev\Catalog\tas\nes\smb3\v1\` (not in git:
it holds commercial-ROM states).

## Result

| chapter | frames | deaths kept | score | Haiku sessions | who solved it |
|---|---|---|---|---|---|
| boot | 327 | 0 | A | 0 | the driver (it found the earliest Start presses with a probe sweep) |
| 1-1 | 1797 | 0 | B (Small, no power-up) | 5 (t1-t4, t6, relayed through checkpoints) | Haiku: start to x~1839; the driver: the pipes, the stairs and the card |
| 1-2 | 1967 | 0 | B- (Small) | 7 | Haiku: the stretches between hard spots; the driver: the T-pipe, the goomba hill, the piranha pipe and the card (with search) |
| 1-3 | 1797 | 0 | B (Small; 3 cards gave a 1-up) | 1 | `advance` (the autopilot): 18 hops with no deaths (4 by the driver, 14 by Haiku); the driver: the card |
| 1-4 (not accepted) | - | - | - | 1 | Haiku: start to x=253 (then the daemon was killed from outside); `advance`: 9 hops to x~436 in ~25 min, then stuck (an auto-scroller: the platforms come into view late, and the wait fallback found nothing). Stopped here. |

`full.bnm` (boot to 1-3) replays from power-on and matches every keyframe and every chapter's end hash
(`--autotas verify`: 99/99 keyframes, 4/4 chapters). Clean states pass the play-on
check: from 1-1's clean state, Mario enters 1-2 as Big (the recipe's $0746=1), alive, and the timer reads 297.
The boot clean state is the raw state: there is nothing to clean right after power-on.

The fortress, 1-5, 1-6 and the airship were not reached in this run (see "Cost" below).

## Desktop check
On the integration build, loading the SMB3 ROM over the HTTP API makes the AutoTAS activity available. The panel then lists
the four chapters with thumbnails, lengths and scores, and the variables stripe shows 26 dossier variables.
Jump (clean) to 1-2, Jump (raw) to 1-3, Watch from here (the movie plays) and Return all work. One bug was fixed on the way:
the desktop manifest reader rejected the tools' field names (`clean_seg` as a flag, `segment`/`raw_state`/`clean_state`).
Screenshots are in `T:\BrokenDev\Catalog\tas\nes\smb3\v1\work\ui_shots\` (not in git).
The catalog record `nes/bb894d10...` has `autotas.status = pilot`, 4/4 chapters, quality_avg 2.25. Its core and build
fields are still empty: the manifest has no `core` block yet.

## What Haiku low did well
- It follows a fixed command recipe exactly. It used resume, save and back correctly and reported honestly
  (deaths, what failed, where its checkpoints are).
- Given `advance` (the autopilot), it ran a whole level stretch (1-3 x 617 -> 2531, 14 hops) with no mistakes.
- It found the map route once it was told how map nodes work (1-1 entry).

## What Haiku low did badly
- **Hard spots.** A jump that needs a plan (stand on a ? block, then jump to a pipe top; let a goomba fall, then stomp it;
  wait for a piranha plant) was never solved by Haiku itself, in 12 attempts across 1-1, 1-2 and 1-4. It retries the same
  idea with bigger search grids instead of changing the idea.
- **Reading the picture.** It misread deaths as being "stuck" (timer frozen) until the runner printed DIED. It also could not
  tell a mushroom from a goomba, or see that it had overshot the goal box.
- **Choosing goals.** It picked search targets behind walls (x past a staircase), so 0 of the tries could succeed.
- **Shell slips.** It wrote `$0075` inside double quotes (bash replaced `$0` with the shell's name), and once ran a search in
  the background while it kept stepping. That broke the input log: 44 frames were lost, and the driver had to redo 1-2 from x=1015.

## Tooling problems found and fixed (all in BrokenDev/tools/autotas, branch autotas-integ)
1. lib/ and games/smb3 were split over two branches, and smb3 milestones/cleanup/dossier used two formats: merged into the tools' format.
2. No reliable chapter flow: added **lib/run.py**. It has one command per action, keeps its own input log and never records
   while the player plays. `finish` replays the log from the start state in a fresh session, records the segment, checks that
   the goal is reached on the replay (the determinism gate), marks the milestone and writes the raw state file.
   So retries never leave junk in the movie.
3. Death detection: lives only drop on the map, after the death. The runner now watches Player_IsDying `$00F1` (verified).
4. Map position: `$0079`/`$0075` on the map (verified). The intel said "1-1 is to the right", but the route is Right, then Up.
5. Pictures were too small for Haiku: the runner now saves screenshots at 2x scale.
6. **search**: tries every combination of step timings in a small grid from the current point and keeps the fastest that
   reaches `--want` without dying. It also requires 30 more frames of survival, so it never picks a doomed spot. Its first
   version had a bug (it expected stoppedBy "until", but the server says "condition").
7. **advance**: a greedy autopilot made of search hops toward x+130 that saves a checkpoint after each hop. It halves the
   stride when stuck and tries a wait first (for auto-scrollers). It stops by itself at the goal card (game data `finale`).
8. The bridge started a new `node` process for every call (~0.3 s each): the local path now posts straight to the daemon over HTTP.
   A plain play_step paced itself to real time (60 frames = 1 s); the runner now passes a milestone that never fires,
   which takes the fast per-frame path (~10x faster).
9. `--want` takes RAM names (`'x >= 1340 && air == 0'`), so there are no `$` signs for the shell to eat. A per-chapter lock
   rejects two commands at once, and every checkpoint stores its own copy of the inputs.
10. Two more verified RAM addresses: in-air `$00D8`, so a search can ask for a landing; and card slots `$7D9C-E` as the level-end signal.
11. resume: a new session at any checkpoint. It also stops the chapter's old daemon, which had leaked one daemon per resume.
12. One daemon was killed from outside (exit 143) during 1-4; `resume` recovered it.

Fleet: not used. The local headless server (~100-170 emulated frames/s on this box while other agents ran) was enough for
one chapter at a time. The dotnet-hosted fleet build was not needed, because local runs were faster to debug.

## Cost (approximate)
- Haiku player sessions: 18 sessions, about 70-135k tokens and 6-30 minutes each (about 1.6M tokens in total).
- Wall time: about 8 hours for boot to 1-3 including all tool work. The driver's own work (debugging and solving the hard
  spots) was the larger share.
- After the autopilot, 1-3 took a single Haiku session (~78k tokens, 21 min) and a few driver minutes.

## Recommendations for the next games
1. Give players `advance` from the start, plus a short game recipe (stride, wait pattern, finale bytes). Treat the player as
   the one who handles the spots where advance gets stuck, not as a frame-by-frame pilot.
2. Escalate a stuck spot right away to a stronger model or a scripted solver, with the picture and the RAM. Do not spend
   Haiku retries on the same spot (a Haiku retry at a hard spot never succeeded in this pilot).
3. Per game, first write the death flag, the in-air flag, the level-end signal and the map-entry macro into games/<game>/.
   That intel paid off more than any prompt advice.
4. Popular platformers next, in this order: SMB1 (simpler and well documented), Mega Man 2, Kirby's Adventure.
   Then games with menus (Zelda), where `advance` needs a different "forward" measure (rooms, not x).
5. Search cost grows with grid size × frames: keep grids under ~100 tries. A server-side search tool would cut the time ~5x.

## Round 2 (2026-11-16, branch autotas-r2): SMB3 World 1 complete + SMB1 World 1

Players: Haiku 5.5 low first; on a stall the driver escalated straight to a sonnet player or the search solver (no Haiku retries).

| game | chapter | frames | deaths | who solved the hard spots |
|---|---|---|---|---|
| SMB3 | 1-4 (auto-scroll) | 4641 | 0 | Haiku advance to x~666; **sonnet**: the rest (sinking lifts, the scroll ends at a pipe, goal room) |
| SMB3 | fort | 3053 | 0 | Haiku advance through the first rooms; **sonnet**: two hidden doors (one works only after ~330 idle frames), Boom-Boom |
| SMB3 | 1-5 | 3226 | 0 | Haiku advance to the decoy wall; **sonnet**: slide tunnel, ceiling pipe, piranha, card |
| SMB3 | 1-6 | 2940 | 0 | Haiku advance 2 hops; **sonnet**: upper route, rail lift, card |
| SMB3 | airship | 8249 | 0 | **sonnet** (Haiku could not read the map): Hammer Bro, king scenes, airship pillars, Larry ($0083 hit counter) |
| SMB1 | boot | 194 | 0 | driver (Start sweep: frames 31-37 all reach control at 194) |
| SMB1 | 1-1 | 2284 | 0 | **Haiku** advance, whole level (24 hops); driver: the end-of-level wait |
| SMB1 | 1-2 | 3064 | 0 | Haiku advance; **sonnet**: koopa under a low ceiling (stand back, wait 65, hop) |
| SMB1 | 1-3 | 1951 | 0 | Haiku advance; **sonnet**: two-step treetop climb |
| SMB1 | 1-4 | 1888 | 0 | Haiku advance to Bowser; **search solver**: want = axe ([$0770]==2), one grid, 28 frames |

Both packages verify from power-on (`--autotas verify`). SMB1: T:\BrokenDev\Catalog\tas\nes\smb1\v1 (catalog nes/facee9c5...).
SMB3: 9 chapters, boot to the World 2 map.

Findings:
- Haiku low + `advance` is good at plain running stretches (SMB1 1-1 fully, most of every level), and bad at anything that
  needs a plan or reading the picture: hidden doors/pipes, auto-scroll timing, the SMB3 map. Every SMB3 chapter after 1-3 needed sonnet.
- SMB1 is much easier than SMB3 for the autopilot: 1 of 4 levels fully Haiku, the other 3 one sonnet/solver spot each.
- Sonnet cost: 70-320k tokens and 20 min - 3.6 h per chapter (airship the most). Haiku: ~80k tokens per session.
- Tool changes this round (lib/run.py, generic): `--patience` waits, step-up fallback, stall push, level-end playout
  (`end_expr`, counted as a hop goal), level-load wait (`ready`), `search --settle` (no jump-apex hits), grid cap (400),
  stale-lock pid check, `resume` keeps later checkpoints. Game data: games/smb1 (new), games/smb3/NOTES.md per chapter.
- Next tool wins (from the escalation players): a tile-map reader (SMB3 $6000: doors 0x43), "best near-miss" output for
  search, a boss-hit counter per game in runner.json, and advance progress output so a tool timeout never kills it mid-hop.
