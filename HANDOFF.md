# x86 cross-page chaining lane (x86-cpuchain) - hand-off 2026-11-21
Branch x86-cpuchain (from main 57e59406), NOT merged. Commits: X86_LINKDEPTH switch, X86_TRAMP (linked block runs from Exec1's loop, no nested call), candidate default-on of PAGELINK+TRAMP (top commit; revert it if anything here is merged).
Garage2 bins: cc0 = main, cc2 = +LINKDEPTH, cc3 = +TRAMP opt-in, cc4 = PAGELINK+TRAMP default on.
## Findings
- PAGELINK already is the fetch-page cache: AoE 3000 fr jitstat (results cc_js0/cc_js1): other-page exits 123.8M -> 8.6M, linked 25.2M -> 137.3M, exec1 steps 166M -> 57M (linkpage ok 114.5M, notlb 6.7M, pageend 1.9M, depth cap 0.48M).
- Wall clock does NOT follow: clean suite cc_pl (3 rounds, spreads <=3%): geomean +0.9% (worse), aoe +3.1%, quake +3.8%, sc +0.4%, camp -0.8%.
- TRAMP (no nesting) vs main: cc_tr geomean +0.1%; LINKDEPTH 8: cc_d8 -1.5%, cc_d8n +0.6% - but those three ran overlapped (spreads 10-19%): noise, no signal.
- cc4 gate (PAGELINK+TRAMP on): SST 99.09%, t386 f3/f4 pass, Win98 boot + 256-colour, JIT parity OK, AoE parity OK.
## Conclusion
The per-exit slow path costs too little to matter: removing ~110M Step1 round trips on AoE saves nothing measurable. Cross-page chaining is NOT a lever; time is inside the blocks / out-of-line ops / helpers. Next: oolat hot list (xchg [edi+4C],esp, iret, port I/O, popf/popa, pop [esp], far call) as JIT helpers, or block body cost.
