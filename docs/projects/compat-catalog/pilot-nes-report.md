# NES pilot report (Tier 2, Haiku 5.5): 2026-11-15

50 NES games, 22 mappers, one Haiku 5.5 T2 player per game, Mesen 2.1.1 as the live reference, run on this PC through the
cc_pilot bridge (`C:\BrokenNes-work\cc_pilot\bridge`). Every agent finished, so no game came back as null.
Workflow transcripts: `...\a47307b0-...\subagents\workflows\wf_aafc0fab-950` (50 Haiku agents plus 1 Opus coordinator).

## Headline numbers

| | |
|---|---|
| Games | 50 (50 finished, 0 agents died) |
| Grades | **OK 16, Jank 25, Broken 1, Not tested 8** |
| Reached gameplay | 44/50 (88%); 6 never left the menus (5 at name entry, 1 at a Y/N prompt) |
| Stuck | 8/50 flagged stuck (16%); 9/50 gave no verdict (18%, counting Bible Adventures, where the reference lost sync) |
| Wall time | 720 agent-minutes, mean 14.4, median 13, range 5-25 per game |
| Play steps | mean about 39 play_step calls per game (11-89) |
| Tokens | 182.7M total, **mean 3.65M per game** (median 3.38M, 1.50M-11.16M) |
| Token mix | 179.2M cache reads (98%), 2.71M fresh input + cache writes, 0.80M output; 2,304 model turns (46 per game) |
| TAS | 50 .bnm movies in `T:/BrokenDev/Catalog/tas/nes/`; the spot-checked ones replay in sync (20/20 keyframes for SMB1) |

How far the agents got: most stopped in the first area or level (1-1, stage 1, first room), which is what T2 asks for.
Only Castlevania III (stage 1-02) and Mega Man 2 (stage 2) got past their first level. M.C. Kids (118 turns, 11.2M tokens) is the
cost outlier: a long credit crawl, then an overworld where nothing moved.

## Stuck cluster: menus, not emulation

Seven of the 8 stuck games were blocked by input or menu handling. None of them points at an emulation fault:
- **Name entry (5)**: Zelda, Final Fantasy, StarTropics, Uncharted Waters (Y/N prompt), plus Dragon Warrior and Zelda II, which got through only after 15+ extra calls. Each game finishes name entry in its own way, and the playbook only says "Start".
- **Map navigation (1)**: SMB3 world map. Half-size pictures cannot locate Mario, and there is no RAM watch for his map position.
- **Game waits (2)**: M.C. Kids overworld, Pinbot (ball launch; Mesen behaves the same), and Castlevania's gate. Castlevania was graded OK anyway because it was pixel-identical to Mesen.
- Short presses (5-6 frames) were often dropped on titles: Arkanoid, Mega Man 3, Punch-Out, Batman, Ninja Gaiden and Mesen's own Start on Bible Adventures.

## Reference false alarms (the big finding)

Of the 25 Jank games, about 17 have the same symptom: "the background is 1-2 px off while scrolling, sprites and HUD match,
score 0.27-0.40". This includes SMB1, which is golden and Certified. I replayed TASes from the catalog with both emulators
lock-stepped from power-on:

| Game | Replay result |
|---|---|
| Super Mario Bros. (golden) | Same frame: 0.33. **BrokenNes frame N against Mesen frame N+1: 0.005.** This is a one-frame phase difference between the two pictures, so it is a false alarm, and the game is OK. |
| Contra | 0.32 at the same frame, 0.37 at +1. By eye the pictures are identical apart from a 1 px scroll phase. An edge-weighted score of 0.3 for a 1 px shift is oversensitive. Unresolved. |
| Mega Man 6 | 0.32 at the same frame, 0.38 at +1: same pattern as Contra. |
| Duck Tales | 0.37 at the same frame, 0.43 at +1. The agent reported a 5-10 px vertical shift, and that needs a real lockstep (`movie diverge`). The record says OK while the agent's result said Jank, so that record needs a re-check. |
| Dudes With Attitude | 0.90 at every offset. The world map is drawn as yellow squares on BrokenNes and as the intact island map on Mesen. **This is a real bug (mapper 79 CHR banking).** |

Conclusions:
- The **score cannot tell a 1-2 px scroll phase from a real fault**. Every "scroll-shift Jank" is unconfirmed until it is run through
  `movie diverge` against the Mesen run, or the score tolerates small shifts. One of the five spot checks (SMB1) is a confirmed false alarm.
- A probable phase artefact: `reference offset` cannot move backwards, and the agents' align calls can drift the offset by 1.
  Mega Man 2 ended at offset -10, then -50, because `align` advanced BrokenNes about 220 frames without play_step.
  Align also advanced the session in Lifeforce and Bible Adventures, so those movies contain frames the agent never looked at.
- Scroll-shift Janks to re-check: SMB1, SMB+Duck Hunt, Contra, Metroid, Mega Man 2, Mega Man 6, Zelda II, Ninja Gaiden, Rygar, Low G Man, Kirby,
  Little Nemo, Skull & Crossbones, Transformers, Batman, Ice Climber, Galaga, Lifeforce, Excitebike (graded OK, but its scores were also 0.33).
- Probably real, and worth a fixer: **Dudes With Attitude** (mapper 79, Broken, confirmed), **Battletoads** (black sky where Mesen shows blue
  during the rappel, score 0.75), **M.C. Kids** (BrokenNes fades to black where Mesen stays on the text box, score 0.92), **Duck Tales** (vertical playfield shift).
  Also **Death Race** and **Caltron 6-in-1**: their sprites and spawns differ, so they could be RNG or sync differences and need a lockstep first.

## Failure clusters by mapper

| Mapper | Games | OK | Jank | Broken | Not tested | Notes |
|---|---|---|---|---|---|---|
| 0 NROM | 6 | 3 | 3 | | | SMB1, Galaga and Ice Climber are all scroll/sprite phase cases |
| 1 MMC1 | 9 | 2 | 5 | | 2 | Zelda and FF are stuck at name entry |
| 2 UxROM | 5 | 1 | 4 | | | Duck Tales has the vertical shift |
| 3 CNROM | 2 | 2 | | | | |
| 4 MMC3/6 | 9 | 2 | 4 | | 3 | SMB3 map, M.C. Kids and StarTropics are stuck; Little Nemo and Kirby show background-layer score gaps |
| 5 MMC5 | 2 | 1 | | | 1 | Uncharted Waters is stuck at a Y/N prompt |
| 7 AxROM | 3 | 2 | 1 | | | the Jank is Battletoads |
| other (9, 11, 34, 41, 64, 66, 67, 68, 69, 71, 79, 113, 119) | 14 | 3 | 8 | 1 | 2 | the Broken game is mapper 79; Batman (69) is suspected FME-7 split timing |

By symptom: picture/scroll phase about 17, sprite placement or RNG 3, real picture faults 3-4, menu/input stuck 7, game wait 2.

## Compared with T1

Only 18 of the 50 games have a T1 grade in the catalog history (the T1 checker has not covered the rest yet). On those 18, T2 found more:
- **T1 OK, then T2 Jank (7)**: Contra, Blaster Master, Battletoads, Batman, Deathbots, Deadly Towers, Death Race. Most of these are scroll-phase cases, so expect several to fall back to OK.
- **T1 OK, then T2 Broken (1)**: Dudes With Attitude. T1's title-only run missed the broken map. This is the clearest case for T2.
- T1 Broken, then T2 Jank: Caltron 6-in-1. T1 Jank, then T2 OK: After Burner. T1 Jank, then Not tested: Bible Adventures.
- Agreement (OK both times): Balloon Fight, Arkanoid, Castlevania, Castlevania III, Donkey Kong.
- So T1 cannot be trusted with an OK, and T2 cannot be trusted with a Jank yet. T2 is the tier that finds real breakage.

## Process and tool problems to fix first

1. **Prompt template**: `{{max_steps}}`, `{{max_frames}}`, `{{known}}` and `{{engine_hint}}` reached the agents unfilled (reported by about 8 agents). Fill them in.
   Suggested values: 40 steps, 3,000 frames.
2. **Reference scoring**: make it shift-tolerant (take the best score over a ±2 px search and a ±1 frame window, which the agents already did by hand for Zelda II), and report a
   `phase` field. Grade Jank only when the score stays high after the shift and frame search. This should remove most of the false alarms.
3. **`reference align` must not advance the BrokenNes session.** Or the prompt must forbid align after `record start`. Also let `offset` move backwards.
4. **Auto-session in the bridge**: agents passed `S` or the wrong id several times. Default to the only open session.
5. **RAM watches**: `$` addresses get eaten by bash (Tetris, Uncharted Waters). Document single quotes in the prompt, or accept `0x`.
   Most playbook addresses are unverified (Metroid energy, DK lives, SMB3 world): drop them, or mark them as guesses.
6. **Playbook**: add per-game menu recipes harvested from these 50 notes (name entry for Zelda, FF, DW, Zelda II, StarTropics, Marble Madness;
   Select-then-Start menus for Contra and Micro Machines; long timed intros for Solstice, Mega Man 6 and Caltron). Generic rule: hold presses for 8-12 frames, and retry once after 60 idle frames.
7. **Marks**: let `record mark` take a `frame`, since the agents placed marks late (Gradius, Micro Machines, Deadly Towers).
8. **Catalog writes**: compute median and max from the step log instead of trusting the agent (Low G Man and Transformers stored guesses).
   Check that the result grade matches the record grade (Duck Tales does not). Flag sha1 mismatches (Pinbot's file is 34c38489, not c649505d).
9. **Stuck guard**: a hard cap of about 60 steps or 6M tokens (M.C. Kids used 11M), then a "Not tested (menu)" grade with the blocking screen attached.

## Recommendation: pace and fleet split for the full NES run (752 titles, about 700 left)

- **Fix items 1-3 first** (about half a day), then re-run the 19 scroll-shift Janks as a calibration set. The target is under 10% false Jank on known-good games (SMB1, Contra, Mega Man 2/3/6).
- Cost: about 3.65M tokens per game, which comes to **about 2.6B tokens for 700 games** (98% cache reads; about 11M output in total). The pilot used 183M for 50 games.
- Machines: each slot runs one BrokenNes headless session plus one Mesen. The Core i3-7100T (2 cores, 4 threads, 16 GB) handles **2 slots per box**, so 7 boxes give 14 slots.
  This PC gets **6 slots** plus the fix waves. That is 20 slots at about 14 minutes per game, or **about 85 games an hour, so the NES run takes about 8-9 hours** of wall time.
  The model rate limit, not the CPU, is the likely cap. Ramp up: 1 box with 2 slots for the first hour, check the results, then all 8 machines.
- Order: run the T1 checker (scripted, no tokens) over all 752 first. Send T2 to games that pass T1 boot, in batches of 100, mapper-sorted so that
  fix waves can group by mapper. Batch 1 should be the mapper-0/1/2/4 bulk.
- Fix waves on this PC between batches: Dudes With Attitude (mapper 79) now, then the Battletoads rappel, the M.C. Kids fade, and the Duck Tales vertical shift, each checked with `movie diverge`.
