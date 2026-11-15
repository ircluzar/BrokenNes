# NES T2 calibration after the tool fixes (2026-11-15)

The pilot's 19 "scroll-shift" Jank games plus 6 known-good games, one Haiku agent each, on branch cc-t2-fixes:
the shift-tolerant score (+-2 px, +-1 frame), the server's own median and max, the reference offset that takes Mesen's 1-frame
head start, align and offset refused during a recording, the filled prompt with a 60-step budget, and the bridge session default.

| Result | Games |
|---|---|
| OK (was Jank) | SMB1, SMB+Duck Hunt, Contra, Metroid, Mega Man 2, Zelda II, Ninja Gaiden, Kirby, Little Nemo, Skull & Crossbones, Batman, Ice Climber, Galaga, Lifeforce |
| Jank, a different symptom (enemy/sprite positions or a jump landing differ, not scroll phase) | Low G Man (from ~frame 1300), Rygar (jump at frame 1479) |
| Not tested (menus or the game waits on both emulators) | Mega Man 6, Transformers, Excitebike |
| Known-good set | Balloon Fight OK, Castlevania OK; Donkey Kong, Castlevania III, Dragon Warrior, Marble Madness Not tested (menu cap) |

- False Jank from scroll phase: **0 of 25** (pilot: 17-19 of the 19). The known-good games show no false Jank (0/6), and neither does the
  golden SMB1. Raw scores of 0.2-0.4 while scrolling remain, and the tolerant score sits at 0-0.02 on the same steps.
- The 2 Janks left are worth a lockstep with `offset` checked first. They may be RNG or input-phase divergences, not picture faults.
- The new problem is menus. 6 of 25 ran out of the 20-step menu cap, mostly on name grids where one press moves one cell. The cap is now
  30 steps (t2prompt.py), with more recipes in playbook-nes.md.
- Cost: 25 agents, about 80-125k subagent tokens each in this harness (mean ~100k), 4-24 minutes, 17-79 tool calls. This is far
  below the pilot's 3.65M per game, which counted cache reads per turn; on the same accounting, expect roughly 1.5-2.5M per game with
  the 60-step cap.
