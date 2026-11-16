# x86 console: downloaded test suites and benchmarks

Inputs live outside git in `C:\BrokenNes-tools\x86\tests` (URLs, pinned versions, sha256: `SOURCES.md` there).

| what | command | pass |
|---|---|---|
| test386.asm (BIOS-ROM CPU tester) | `Workshop --x86t386 C:\BrokenNes-tools\x86\tests\test386\test386_f3.bin [--gen 4 with _f4] --ee ...\test386\src\test386-EE-reference.txt [--nojit] [--last N]` | POST FFh and the EE dump matches |
| SingleStepTests 80386 real mode | `Workshop --x86sst C:\BrokenNes-tools\x86\tests\sst80386 [--only 00,0FA3] [--show N] [--out report.txt]` | per opcode file |
| SingleStepTests 8088 v2 | `Workshop --x86sst C:\BrokenNes-tools\x86\tests\sst8088 --cpu 8088` | the core is a 386: 8086-only behaviour fails by design |
| Dhrystone 2.1 + TOPBENCH 0.40c | `dhry/build.ps1` once, then `dosbench.sh <Workshop.exe> <outdir> [ips...]` (git bash) | scores vs TOPBENCH's real-PC database (`tbnear.py`) |

Results and the fixes they led to: `RESULTS.md`.
