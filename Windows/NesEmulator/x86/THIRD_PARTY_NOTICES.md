# x86 console (pool card P3): provenance ledger

Started 2026-11-05 with milestone P3b (the CPU). The x86 console is an **isolated** system (decision of 2026-11-04,
`docs/projects/next-pool-plans.md`, P3): its chips are never slots of another console and no other console's chip is a slot of it.
The policy and the root list are in `/THIRD_PARTY_NOTICES.md`. **Update this file in the same change that adds or ports anything.**

## 1. Taken from source files (direct derivation)

| What | Where | Source | License |
| --- | --- | --- | --- |
| Intel 80386 / 80486 CPU core | `I386.cs`, `I386.Exec.cs`, `I386.System.cs` | tiny386 (github.com/hchunhui/tiny386) `i386.c`, `i386ins.def`, `i386.h`, commit 2c1205c (local pinned copy, read only), Copyright (c) 2024-2025 Chunhui He | BSD-3-Clause (text in the root ledger, section 6). Permissive: no exception to the policy needed. |

**What was kept.** The core's design and behaviour, function by function: the lazy flags (`cc` op / dst / src / mask), the TLB and
page walk (accessed and dirty bits, the `pte_lookup` permission table), the instruction-fetch cache, ModR/M + SIB decoding, segment
loading, the protected-mode transfers (`pmcall`, `call_isr` with its four cases, `pmret`, `task_switch`), the I/O permission bitmap,
LAR / LSL / VERR / VERW, the 486 additions (CMPXCHG, XADD, BSWAP, INVLPG, CPUID with tiny386's vendor string), the `try_jcc8` CMP/TEST +
JZ/JNZ fold, and tiny386's quirks where they differ from a real 386 (kept so the port steps exactly like the original): the ROL / ROR flag
test on the unmasked count, BT/BTS/BTR/BTC's memory offset taken as unsigned, the Cyrix / NexGen DIV detection tweaks, BSF / BSR dropping
the lazy flags, no segment-limit checks.

**What was changed.**
- Written as C# methods instead of the macro tables (`i386ins.def` became the `switch` in `I386.Exec.cs`); faults are `bool` returns as in
  the C (no exceptions), so it stays AOT / WebAssembly friendly.
- Where tiny386 calls `abort()` (TRY1, `cpu_abort`) the port throws `I386AbortException`.
- Real-mode IRETD is done the 386 way (tiny386 aborts).
- REP string instructions run element by element: tiny386's page-at-a-time fast path walks on in physical memory when a 16-bit SI / DI
  wraps inside its segment; the port wraps inside the segment like a 386.
- RDTSC returns the instruction count (deterministic; tiny386 reads the host clock).
- BOUND with a register operand, MOV to / from segment register 6 or 7 and INVD raise #UD (tiny386 asserts, or loads LDTR / TR).
- CMOVcc is only decoded for gen > 5 (the port is built as a 486).
- Memory has 16 spare bytes after the end and the page walk ignores tables outside RAM (tiny386 reads past the buffer there).

**What was left out.** The x87 FPU (`fpu.c`: an ESC instruction decodes its operand and does nothing, or raises #NM when CR0.EM / TS ask
for it; CPUID and CR0.ET say no FPU), MMX / SSE (`simd.inc`), long mode (`amd64*`), the `fast/` path, the ESP32 / WASM build switches,
SYSENTER / SYSEXIT / RDMSR / WRMSR. (CMPXCHG8B and the TSC / CX8 CPUID bits are kept, as in tiny386.)

## 2. How it was checked

Workshop `--x86test` (see `Workshop/X86/`):
- 13 hand-written programs (real-mode ALU and flags, MUL / DIV, shifts and rotates, string instructions, the stack, jumps and LOOP, INT /
  IRET, a #DE fault, a hardware interrupt waking HLT, the 486 additions, the protected-mode switch with an IDT and a #GP with its error
  code, paging with a page fault and A/D bits, ring 3 through a TSS), 101 checks against values worked out by hand.
- The differential trace: tiny386's own `i386.c` built from the pinned source with a minimal C harness (MSVC, in the developer's scratch
  folder, never in the repository; one MSVC portability fix to a copy of the file: IDIVd's `-2147483648` literal, which MSVC types as
  unsigned). The programs plus seeded random real-mode instruction streams (16- and 32-bit operand / address sizes, prefixes) run on both,
  and the state after every instruction step plus a hash of the 4 MB of memory must match line for line. 2026-11-05: 4 seeds x 5013 cases,
  all identical.
