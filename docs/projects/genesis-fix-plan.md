# Genesis (Mega Drive) fix plan

Catalog: `T:\BrokenDev\Catalog\records\md`, reference BizHawk 2.11.1 Genplus-gx, grader `BrokenDev/tools/compat-t1/t1.py`.

## Round 1 (MVP, md-fix-r1, 2026-11-16): the colour ladder

Six well-known Jank games checked (Streets of Rage, Mortal Kombat, Earthworm Jim, Aladdin, Gunstar Heroes, Contra Hard Corps).
The biggest shared cause was **a harness artefact, not the core**: BrokenNes outputs the measured MD DAC ladder per 3-bit level
(0,49,87,119,146,174,206,255), Genplus-gx the linear one (0,34,...,238). Pixel for pixel the pictures were the same apart from
that mapping (Streets of Rage frame 360: every pixel pair is (BN level, Genplus level)). On dark scenes the step between two levels
crosses picdiff's edge threshold (24 luma) on one ladder but not the other, so whole titles scored 0.05-0.08 ("close, not same")
and graded Jank.

Fix: t1 maps BrokenNes's MD pictures onto the reference ladder (nearest level) before comparing (`MD_LADDER`, `to_ref_ladder`),
in live runs and in `t1.py regrade`. Guard: `BrokenDev/tools/compat-t1/test_md_ladder.py`. The core keeps its ladder (the
measured one is the better-looking, more hardware-like output; goldens untouched).

## Round 2: the other popular games

Re-run `t1.py regrade --console md` after merge, then sort what is still Jank:

1. **"Differs right after Start" (timing/input)**: Gunstar Heroes / Contra Hard Corps style. First rule out the harness again:
   where BizHawk latches the pad vs BrokenNes (the NES lane's `--input-latch` lesson), and the MD 3/6-button auto-config.
   Then compare frame counts to the first title frame on both (a boot offset of a few frames moves every checkpoint).
2. **Real picture differences that survive the mapping** (Aladdin frame 840, Earthworm Jim partly): lockstep against BizHawk
   with `BrokenDev/tools/refstep/bizhawk_refstep.lua` per frame, VDP registers/VRAM/CRAM diff at the first differing frame.
   Suspects: shadow/highlight mid-levels (also check the ladder's nearest-level mapping there), sprite limits/masking, HInt timing.
3. Popular list to certify: Sonic 1/2/3/S&K, Streets of Rage 2/3, Shinobi III, Revenge of Shinobi, Castlevania Bloodlines,
   Ecco 1/2, Sonic Spinball, Mortal Kombat II, Earthworm Jim 2, ToeJam & Earl, Phantasy Star IV, Golden Axe.
4. Use the fleet for comparison sweeps (C:\BrokenNes-work\fleet\HOWTO.md); heavy local work only in heavy slot1/slot2.

Park the long tail (the remaining ~500 MD Jank) until round 2's causes are fixed and the regrade has run.

## SEGA-01: the 71 Jank (2026-11-20, main 087a97c7)
- Merged: J-Cart (Micro Machines II 3/4-player menu) + t1 md ladder maps all 15 shadow/highlight levels. md battery ALL GREEN (120 golden, 145 scenarios, fuzz, replay).
- Recount on the deep runs: 20 already deep-OK + 7 more with the 15-level ladder (Sub-Terrania, Marsupilami, Jim Power, DJ Boy, Toughman, Fatal Fury, Rambo III) = 27 harness artifacts; 32 still deep-Jank; 10 deep "Not tested" (cmdline error 11-19) + 2 never run.
- Targeted re-grade of those 40 (+MM II): keeper entry md_sj1_regrade (only-list C:\BrokenNes-work\md_sj1_regrade.json, --force ingest so grades can rise), Workshop C:\brokennes\ws_sj1, kit t1.py updated on all PCs.
- Raiden Trad probe: without input BrokenNes matches BizHawk's attract (BN ~2 frames ahead at 180, ~5 at 540: slow drift). Start held 8 frames at 180 or at 420: BrokenNes takes it, BizHawk ignores it both times. 6-button vs 3-button pad on BN changes nothing. The game reads the pad 4 NOPs after each TH write ($00ABF6); suspect Genplus's TH-transition latency model. Needs a third opinion (BlastEm, in C:\BrokenNes-tools\sega\oracles) before changing MdPads.
- md_sj1_regrade DONE: 33 of the 40 now OK (incl. Micro Machines II); md Jank 71 -> 39 (OK 716). Still Jank from the list: Super Bubble Bobble, World Trophy Soccer, V.R Fighter vs Taken2, Gauntlet, Trouble Shooter, Blood Shot, Twinkle Tale.
- Probes (no input vs input, BN vs BizHawk): Raiden = no Start bug; the attract demo matches but BN drifts ahead (~2 frames at 180, ~7 at 600: fewer lag frames, a CPU/VDP timing gap), so presses land at different moments. PGA European Tour now matches through Start (the Jank came from an older build; re-grade it). Gaiares = REAL: frame-exact to 840, then from ~870 (after the second Start at 840) the player ship sprite is missing in BN while the beam is drawn; suspect the sprite attribute cache/masking (MdVdpRender.cs ~527, ~57). S3&K / S&K+S2 probes did not run (bad shell quoting); redo.
