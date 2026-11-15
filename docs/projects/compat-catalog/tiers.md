# Tier rules and grades

The rules every catalog worker (scripted or agent) follows. The grade scale is BrokenNes's own (`Windows/Data/default-db.json` plus the
catalog's `Certified`); the record fields are in [../compat-catalog-db.md](../compat-catalog-db.md).

## Tiers

| Tier | Who | Budget | Goal | Done when |
|---|---|---|---|---|
| T1 | `tools/compat-fleet/tiers/T1.ps1` (no model) | 1800 frames, one Workshop run | boots, picture moves, reacts to a fixed input script | record has `t1.verdict` pass/fail |
| T2 | Haiku 5.5 player ([prompt-t2-player.md](prompt-t2-player.md)) | 120 play_step calls or 18,000 frames (5 min of game), whichever first; at most 40 minutes wall time | play the first level / area side by side with the reference, record the movie, grade | the first level is finished, or the budget is spent, or a give-up rule fires |
| T3 | Haiku 5.5 runner ([prompt-t3-runner.md](prompt-t3-runner.md)) | per session 400 steps; resumable across sessions and boxes | finish the game (or the game's last reachable point) as one movie | credits, or the runner gives up with a resume point |

A game only moves up a tier when the lower tier passed: T1 pass -> T2; T2 grade OK -> eligible for T3 (picked by the coordinator).
A failed T1 (Broken / Nothing) goes straight to the fix wave queue; no agent time is spent on it.

## Grades (what the agent may write)

| Grade | Means | Evidence the agent must give |
|---|---|---|
| Nothing | no picture, a frozen boot, or an unsupported board | screenshot hash unchanged, or the open_rom error text |
| Broken | runs but cannot be played: crash, hang, the game soft-locks, controls do nothing, the screen is garbage | the frame, a mark in the movie, a screenshot path |
| Jank | playable to the end of the tier, with visible or audible faults (a wrong tile row, flicker the reference lacks, shaking split, wrong colours, slowdown the reference lacks) | frame + `reference.score` at the fault + what differs |
| OK | the tier played through and the reference agreed ("same" or "close" that comes and goes) | median and max score, the frame count, the movie |
| Certified | never written by T2/T3 agents: a coordinator grade after a frame-for-frame reference match + a golden case | - |
| Not tested | the run did not reach a verdict (budget spent before the title screen was left, or a tool error) | why, in the notes |

Rules:
- **The reference decides OK vs Jank**, not taste. A score that rises above 0.12 and stays there for 3+ steps is a finding; scores that come
  and go (flicker phase, palette shade, one animation frame) are not, but write them down with the score.
- **The same on both = not our bug.** If the reference shows the same oddity (a game's own flicker, its own slowdown), it is no finding.
- **Never upgrade a grade a human set** (the catalog keeps hand grades; history shows the writer). Lower grades only with evidence.
- **Agents never fix**: no core edits, no write_memory to make progress, no cheats. Reading RAM is fine.
- Tags (pick all that apply): `picture sound timing crash input mapper save`.

## Give-up rules (both T2 and T3)

Stop and grade with what you have when any of these fire:
1. The screen hash has not changed for 10 steps while you pressed different buttons (frozen) - grade Broken (if the reference moved) or note
   "game waits for something" (if the reference is also still).
2. You have tried the same obstacle 5 times (death counter / same RAM position after 5 tries): mark it, write "stuck at <place>", grade from
   what you saw (usually OK or Jank: being bad at the game is not a bug).
3. Lives are gone and the game is over twice: same as 2.
4. The game needs a peripheral the pad cannot be (Zapper, Power Pad, Vaus, mouse, light gun, keyboard): grade what the pad reaches, tag `input`.
5. A tool error twice in a row: close the session, grade Not tested with the error text.
6. The step budget is spent.

## Pilot measurements

Per game per tier, the runner logs: input + output tokens, play_step count, frames played, wall time, the box, the final grade, whether a
give-up rule fired (which), and `reference` median / max score. False-alarm rate = games graded Jank/Broken whose finding a human
(or the fix-wave agent) rejects. Stuck rate = give-up rules 1-3 per 100 games.
