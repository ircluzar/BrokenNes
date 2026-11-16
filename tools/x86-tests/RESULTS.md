# Results (2026-11-15, branch x86-tests)

Baseline = main 2d7ef075 with only the harness added; after = x86-tests.

| suite | before | after |
|---|---|---|
| test386 (gen 3 ROM CPU_FAMILY 3, gen 4 ROM CPU_FAMILY 4; JIT and X86_NOJIT) | stuck in POST 0Bh (ring-3 error loop) | POST FFh, EE dump 44926/44926 lines |
| SingleStepTests 80386 real mode, 1,758,699 tests in 941 files | 1,581,526 pass (89.93%), 204 files clean | 1,742,693 pass (99.09%), 509 files clean |
| SingleStepTests 8088 v2, 3,007,000 tests in 323 files | 2,534,356 (84.28%) | 2,537,800 (84.40%) |

What is left in the 80386 set: SHLD/SHRD 16-bit with counts 17-31 (undefined on paper; ~8k tests), CF of 8/16-bit SHR/SHL with counts
above the width, partial register writes before a #SS/#GP (POPAD, LDS), stray cases. The 8088 set fails mostly on behaviour the 386
changed (PUSH SP, unmasked shift counts, undefined-opcode aliases, the 1 MB wrap, the #DE return address): not targets.

Fixes (one commit each): MOV/POP Sreg + LxS protected-mode checks; LOCK #UD; no A bits on a faulting page walk; segment limit and
descriptor type checks (interpreter + JIT, unreal mode kept); POPAD ESP high word; ENTER nesting/frame/stack-top check; SHR OF;
BT memory offset 64 KB wrap; VERW on code; RET/RETF/IRET past the CS limit; REPNE INS/OUTS.

Battery x86 part: identical except the FreeDOS session on I386_D074 (3 M instr/s), which now takes one real-mode #GP (a word read at
DS:FFFF, hardware behaviour) and so ends one audio sample earlier. Vaults game.img / phys.img outputs byte-identical. --x86bench same.

## Benchmarks (machine time = the instruction clock, so the scores are deterministic)

| | 60 M instr/s (desktop default) | 130 M instr/s |
|---|---|---|
| Dhrystone 2.1, DHRY16 (8086 code) | 124,702 /s = 71.0 DMIPS | 269,726 /s = 153.5 DMIPS |
| Dhrystone 2.1, DHRY386 (386 code) | 133,381 /s = 75.9 DMIPS | 288,992 /s = 164.5 DMIPS |
| TOPBENCH 0.40c score | 2279 | 4939 |

TOPBENCH's database tops out at 1541 (Pentium III 1 GHz): our scores are above every real PC in it, and its memory and video-RAM
sub-tests read 0 us. Reason: the machine clock counts instructions, and a REP MOVS/STOS of any length is one instruction, so block
moves cost no time at all (Dhrystone's strcpy/strcmp are inflated the same way). A speed-accurate PC needs a per-instruction cost
model (at least REP iterations), which the x86 -> Windows 98 work will need anyway.
