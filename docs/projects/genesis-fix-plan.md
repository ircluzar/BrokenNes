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
