# DOS verdict (x86 PC, 2026-11-21)

Scope: is DOS "solved" on BrokenNes's x86 PC (boots, runs right, sound, speed)? Small line, mostly synthetic.
Evidence is from runs already made (bench line, x86 console line, VRUN-DOS floppies); no new downloads yet.

**Verdict: DOS works (boots, real-mode + protected-mode games run, OPL2/SB16 present) but is NOT solved on speed:
the emulated P166 runs FPU/memory-heavy DOS code at ~0.46x a real P166, because of the P5 cost table.**

## Results

| test | where | result | vs real hardware |
|---|---|---|---|
| FreeDOS 1.4 boot to C:\> | FreeDOS | PASS, 2.1 s machine time | - |
| --x86test / --x86dev (11 device tests) | headless | PASS 100/100 | - |
| PHYS.EXE / Vaults DOS game script | FreeDOS | PASS, 0 diffs | - |
| VRUN-DOS bootloader floppies (Vaults, Super VRUN DOS) | FreeDOS + real HW | PASS (same image boots on real HW) | - |
| OPL2 (ymfm YM3812) | FreeDOS | PASS, 0 diff vs ymfm harness; Vaults FM probe finds AdLib | - |
| PC speaker | FreeDOS | PASS | - |
| SB16 (WIN98-P166 machine) | Win98 | PASS (Win98 driver, StarCraft/AoE sound) | DOS-side SB detection by a DOS game: NOT TESTED |
| VESA (VBE) modes | Win98 DOS box | PASS after fix 135c00d3/209b50d1 (ES:DI via Lin, S3 LeaveEnhanced) | - |
| Quake 1.06 timedemo demo3 (own DOS extender, 320x200, -nosound) | FreeDOS | PASS, 17.2 fps | **0.46** of P166 37.3 |
| Quake 1.06 timedemo demo1 | FreeDOS | PASS, 18.6 fps | ~0.5 |
| Quake 1.06 demo1 | Win98 DOS box | PASS, 14.8 fps (0.80 of our DOS; real ~0.79) | ~0.4 |
| Quake demo3 with X86_P5COST=nomem (measurement only) | FreeDOS | 33.3 fps | 0.89 |
| Idle DOS prompt INT 16h spin in Win98 | Win98 DOS box | FIXED 71fa1869 (real HLT) | - |
| DOS/4GW game | - | NOT TESTED (needs a download) | - |
| EMM386 / HIMEM (EMS/XMS) game | - | NOT TESTED | - |
| Mouse driver (CTMOUSE) in a DOS game | - | NOT TESTED | - |
| Doom 1.9 timedemo demo3 | - | NOT TESTED (archive zip on disk is not 1.9) | ref P166 88.08 fps |

## Bugs / gaps
1. Speed: P5 cost table charges +3 units per memory operand and +2 per x87 op (I386.Model.cs); ~85% of the 0.46 gap.
   Big change (re-times every game, more host work); options already with the coordinator. Not fixed here.
2. Quake with sound on, DOS-side SB16 detection, DOS/4GW, EMS/XMS, mouse: unverified; need the shareware downloads below.

## Downloads needed (awaiting user approval, not downloaded)
Doom 1.9 shareware, Duke Nukem 3D 1.3d shareware (Build, DOS/4GW, SB16, mouse), Wolfenstein 3D shareware (real mode),
Descent 1 shareware (DOS/4GW), and the synthetic Speedsys / PCPBench / 3DBench. Sources: archive.org items (URL/size to confirm before fetch).
