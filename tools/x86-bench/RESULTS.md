# x86 console: test-program results (compat catalog P6, 2026-11-15)

Reference: DOSBox-X 2026.10.01, core=normal, cputype=486 (C:\BrokenNes-tools\dos\dosbox-x). BrokenNes: Workshop `--x86boot` on a
FreeDOS 1.4 disk, default 60M instr/s, JIT on.

## What was on disk
No ready-made x86 test suite or benchmark is on disk (checked C:\BrokenNes-tools\dos, \x86, the FreeDOS image's \PACKAGES).
What exists: the built-in `--x86test` (13 hand programs + fuzz, optional step-by-step vs tiny386ref), `--x86dev` (11 NASM device tests),
`--x86bench`, the DOS smoke programs (hello / mode13, DOS/32A), NASM, Open Watcom, DOSBox-X. So the test programs were written here:

| program | what | BrokenNes vs DOSBox-X |
|---|---|---|
| FLAGT.COM (flagt.asm) | 136 ALU/flag/bit ops x 882 cases, defined flags only, #DE trapped | PASS after the fixes below; 5 accepted diffs (cmpxchg: DOSBox-X wrong; shld16/shrd16 count>16 undefined) |
| BENCH16.EXE (bench.c, wcl -ml -3) | sieve, crc32 (+CBF43926 check), qsort, div/mod, 64-bit, fib, matrix, strings/sprintf, real mode | all 9 checksums identical |
| BENCH32.EXE (bench.c, wcl386 DOS/32A) | the same in 32-bit protected mode under DOS/32A | all 9 checksums identical |

BENCH32 prints "DOS/32A warning (9004): mouse initialization failed" on BrokenNes: no mouse driver is loaded on the FreeDOS disk
(DOSBox-X has a built-in one). Correct behaviour, not a bug.

## Bugs found and fixed (all inherited tiny386 quirks, I386.Exec.cs)
1. ROL/ROR by a count with count & 31 == 0 but count != 0 (32, 64, 0x80...) changed CF/OF; the 386 masks first and leaves flags alone.
2. BT/BTS/BTR/BTC mem,reg: the bit offset is signed (negative offsets address below the operand).
3. BT/BTS/BTR/BTC mem,imm8: the immediate is modulo the operand size; it must not move the address.

Battery x86 checks (--x86test, --x86dev, --x86engines, --x86cards, --x86session I386 and I386_D074) identical before/after.
`--x86test --ref` (step-by-step vs tiny386) now reports mismatches on these opcodes on purpose: tiny386 has the bugs.

## Speed
See the table in the lane report (BENCH x20, host wall time vs machine time).

## Well-known suites that need a download (for the user's approval)
- test386.asm (Marco Bortolin), 386 CPU test ROM: https://github.com/barotto/test386.asm (git archive of master; the prebuilt
  `test386.bin` in the repo). Runs as the BIOS ROM at F000 and reports on port 0x190/0xE9, needs a small loader mode in PcMachine.
- SingleStepTests 80386 per-opcode JSON (real mode, 1000s of cases per opcode): https://github.com/SingleStepTests/80386 (large; v1 json.gz files).
- SingleStepTests 8088 (subset applies to real-mode semantics): https://github.com/SingleStepTests/8088
- Dhrystone 2.1 C source (netlib, public): https://www.netlib.org/benchmark/dhry-c - gives a DMIPS figure comparable with real-PC tables.
- TopBench (Jim Leonard, freeware) with its database of real-PC scores: http://www.oldskool.org/pc/TopBench (TOPBENCH zip) - the
  "vs real PC" number.
- (Whetstone / FPU tests are pointless: the x86 console has no FPU.)
