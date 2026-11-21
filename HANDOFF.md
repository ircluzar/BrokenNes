# x86-p2 handoff (Pentium II 400 rung, HL2 ladder)

Branch x86-p2, worktree C:\BrokenNes-work\wt-p2, commits d9471918 + eba357ce. Not merged, not gated, never pushed.

## Done
- CpuModel.PentiumII ("P2"): CPUID 0652, EDX FPU DE TSC MSR MCE CX8 SEP MTRR CMOV MMX, leaf 2 descriptors (I386.Model.cs).
- CMOV in the interpreter + native IL JIT (DK_CMOV, I386.Cache.cs / I386.Jit.cs; X86_NOCMOVJIT); FCMOV / FCOMI family (I386.Fpu.cs);
  RDPMC, SYSENTER / SYSEXIT, hint NOPs 0F18-1F (I386.Exec.cs); P6 MSRs saved for P6 only.
- Timing: CPI 1.0, memory operand 1 extra clock, P6 column in BuildOpCosts, REP row.
- Presets win98-p2 / -voodoo / -nano (PcConfig.cs). A P166 state is refused by a P2 machine: cold boot only.
- Tests: `--x86p6 --parity` (25 checks + 60 seeds JIT == NOJIT, compiled code confirmed), `--x86cpu` green. Local build C:\BrokenNes-work\p2_build.

## In flight (Garage2, bin p2a = eba357ce)
4 bg runs started by `g2run.sh run`: tags p2sc_P2, p2sc_P166, p2aoe_P2, p2aoe_P166. Each is a cold boot plus steps
(C:\BrokenNes-work\p2work\sc.steps: sysdm.cpl screenshot then STARCRAFT.EXE; aoe.steps: Empires.exe), --frametimes --irqstat, savestate.
Logs C:\BrokenNes-work\p2work\run_*.log; results land in C:\BrokenNes-work\g2\results\<tag>\ (run.txt, *_stepN.png, st_*.bin).

## Next
1. Read the results. Check the sysdm step png says "Pentium(r) II" and the game screenshots. If Win98 shows a new-hardware or restart dialog
   on the P2, add steps (or save a settled disk with --save to {O}).
2. Use the P2 main-menu states to get into a game (st_sbingame2-like); then timed runs P2 vs P166 on Garage2 (ab/suite take bins + env,
   not a machine: either add a -Machine option to ab.ps1/suite.ps1 or run x86boot timed via a timed claim). Report % real time and the
   idle ratio = idle-clock / clock (from --irqstat; compare deltas over the game window).
3. Sandra / WinBench: C:\BrokenNes-work\bench (see project_win98_benchmarks memory) on WIN98-P2 vs WIN98-P166.
4. Gate: x86 gate incl. SST/test386 (g2run.sh gate garage2 TAG p2a) + UAT/battery.ps1 with a private BROKENNES_WORK; then a fast-forward
   merge via `git update-ref refs/heads/main <new> <old>`, sync the primary checkout's index-only paths, rebuild the desktop exe. Never push.
Rules: builds on this PC only under heavylock.sh; timed runs only on Garage2; parity / soaks on the fleet.

## Result of the 4 cold boots (2026-11-21, read after the handoff was written)
- P166 (both disks): normal, 148-152% real time; steps ran; idle-clock 24-25 G of ~31 G clock (HLT idle on the desktop/menus).
- **P2 (both disks): BROKEN.** Win98 shows "It's now safe to turn off your computer" and APM powers off at frame 2204 (31.5 s of machine time),
  so no steps ran. HLT batches 0, 79 M instr/s, 21% real time. Something P6-specific makes Win98 shut down early in boot. Suspects, in order:
  CPUID family 6 code paths in Win98 (VMM / IOS / the CPU-speed or MTRR VxDs touching MSRs we #GP: check with an MSR log, e.g. log Throw in MsrP6);
  SEP reported (Win98 ignores it, but test with SEP masked); CR4 bits Win98 sets on a P6 (PGE / PSE get #GP because they are not reported); the timing
  (400 M IPS) exposing a race. Bisect quickly: boot WIN98-P2 with `--cpu P2` variants (temporarily FeatureEdx = the P55C's, then Signature 0543) to find which one matters.
  Results are in C:\BrokenNes-work\g2\results\p2sc_P2 (sc_step*.png = all the shutdown screen).

## 2026-11-21 evening: shutdown FIXED (d8553ed3), Win98 detects a Pentium II
- Cause: Win98 VMM (0028:C0380901) does RDMSR 1B (APIC_BASE) on family 6; we #GP'd -> shutdown. Now reads FEE00100 (BSP, APIC off), writes accepted.
  `X86_MSRLOG=1` logs every P6 MSR access + CR4 writes; `X86_CPUID_SIG` / `X86_CPUID_EDX` (hex) override CPUID 1 for bisects.
  g2 TRAP: G2ENV takes `K=V;K=V` (semicolons), NOT spaces (the first bisect runs bs_* silently ran without the overrides).
- Garage2 bin p2c (= d8553ed3). Results: p2c_sc, p2c_aoe, p2c_aoe2 (C:\BrokenNes-work\g2\results).
  * System Properties: "GenuineIntel / Pentium(r) II Processor / Intel MMX(TM) Technology / 64.0MB RAM" (p2c_aoe2/aoe_step4.png).
  * First P2 boot: "System Settings Change: new hardware, restart?" (CPU change). AoE disk answered Yes -> "Setup updates configuration" -> settled
    desktop (Welcome + Registration + Channel Bar dialogs pop up, they steal the steps' keys). p2c_aoe/st_aoe.bin = settled P2 desktop (AoE disk).
  * StarCraft reached its MAIN MENU on the P2 (p2c_sc/sc_step7.png) but the end state st_menu.bin is NOT at the menu (black / Registration wizard).
    p2sc_P166/st_menu.bin is NOT at the menu either (desktop + Run dialog). Do not use either for timing.
  * AoE on the P2 settled desktop: 680% real time, halted 2.81 M of 2.93 M batches (idles fine). AoE menu screenshot not reached yet (steps hit dialogs).
- PERF FINDING: from p2c_sc/st_menu.bin the P2 sits in a 16-bit busy loop (CS:EIP 17D7:0C4F, Registration wizard up), halted 0:
  300 fr = 14% real time (87 M instr/s host), 1200 fr = one frame of 1891 s (!) -> investigate (host contention vs an emulator stall).
  A non-idling P2 needs 400 M guest clocks/s vs 151 M on the P166, so busy code runs ~2.65x slower relative to real time by design.
## Next
1. Make settled P2 disks/states per game: boot, answer Yes to the restart, dismiss Welcome (alt+f4), Channel Bar (n), Registration (alt+f4 / Register Later),
   then launch the game; save states AT the menus (shot just before --savestate to verify). Same for the P166 (fresh, from st_sby-like states).
2. Then in-game states (skirmish / AoE random map) and timed P2 vs P166 + idle delta (run 300 and 1200 frames, diff idle-clock and clock).
3. Look at the 1891-s frame (rerun m_sc_P2_1200 alone on an idle slot; --jitstat).
4. Benchmarks + gate + merge as before (nothing merged yet; branch x86-p2 now d8553ed3 + this note).
