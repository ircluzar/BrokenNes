# x86 cpuwarm2 lane (branch x86-cpuwarm2) - 2026-11-21
Lever under test: code that runs without a compiled block (warming / pending).
## Measured (Garage2 bg runs, AoE st_a23, main 745a296e = bin cw2a; results g2/results/w2_tp_aoe, w2_plain_aoe, w2_nojit_aoe)
- JIT plain 1500 fr 30.2 s (20.1 ms/fr); TIMEPROBE+JITSTAT 41.6 s (probe +38%); NOJIT 300 fr 13.8 s (46 ms/fr) = ~23 ns per interpreted instr.
- At JITHOT 2048: 58.1M steps without a block (53.6M warming, 2.2M pending; 29.7M at 512). Real cost ~1.3 s of 30 s = ~4.5% (not 12%:
  timeprobe's 152 ns/step is mostly Stopwatch overhead). A cheaper first tier could win ~2% at best.
- The worker's .NET JIT is the real cost of compiling more: 7-33 ms/region (~5.4 KB IL for ~20 instrs).
## Experiments
- X86_JITHOTPG=N (commit on this branch, opt-in): pages that already hold code get hot after N visits. N=256: suite w2_pg256 +12.0% geomean WORSE
  (aoe +17, boot +21, desktop +11, camp +6, sc +6). Dead: compiling more code loses.
- X86_JITHOT=4096 vs 2048 (suite w2_h4k, 2 rounds): -3.2% geomean (desktop -11, boot -2.6, sc -2.2, aoe +0.2, camp +0.1). Trend continues, but inside noise apart from desktop.
## Next
- Confirm 4096 (and 8192) with 3+ rounds on Garage2, then PEARL 2-core (ask the coordinator first); if it holds, make it the default (gate + battery).
- The real lever behind thresholds is the cost of compiling a region: smaller IL per region (shared helpers instead of inline expansions) makes every threshold cheaper.
