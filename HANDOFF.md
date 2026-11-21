# x86 oolat lane (branch x86-cpuoolat) - hand-off 2026-11-21
Commit on branch (NOT merged, no proven win): JIo = IN/OUT out-of-line through a pre-decoded helper (X86_NOIOJIT = old JInterp path);
same post-sequence as JInterp (JStill / NextIp checks), so parity is by construction; timeprobe prints per-port IO ms (iops lines).
## Measured (Garage2 bg runs, AoE st_a23 1500 fr, results g2/results/co_tp0_aoe, co_tp2_aoe, co_js1_aoe)
- Main (cm4) X86_TIMEPROBE: run 36.4 s; out-of-line 5.94M instrs, 1131 ms inside them (3.1%).
- IN/OUT = 1.56M of them, 320 ms, and that time is the DEVICES, unchanged with JIo (co2 also 790+320 ms):
  S3 engine port 9AE8 116 ms/9.4k (blits, real work), PIT 43 latch 88 ms/367k (240 ns each), PIT 40 read 55 ms/735k, SB 220 27 ms.
- Upper bound for the whole oolat lever: OOL-containing hot blocks spend ~1.35 s over 12 ns/instr (~3.5% of the run), incl. the 0.3 s device time.
  Realistic gain from helpers for iret/STI/POPF/xchg/LAR/far call: <= ~2%, below the suite's 2-3% noise per lever.
- Bigger levers seen in the same timeprobe: steps without a compiled block 29.7M = 4520 ms (12%), of which "counting" (warming) 20.6M = 3097 ms
  and pending 6.8M = 793 ms; compiled blocks 19.1 s. Device: PIT latch (Pit8254) 240 ns per OUT 43 is a small device-side lever.
