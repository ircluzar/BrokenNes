# x86 CPU model tests (our own code)

- `cpuvec.asm` -> `cpuvec.bin`: x87 + MMX vectors, expected values worked out by hand. `Workshop --x86cpu --vec tools/x86-cpu/cpuvec.bin`
  also checks opcode gating, CPUID / EFLAGS.AC / EFLAGS.ID, RDTSC + WRMSR 10h and the save-state round trip on every model (the battery's `x86cpu` run).
- `cpudet.asm` -> `CPUDET.COM`: a DOS CPU detector (8086/286/386/486/CPUID, FPU, MMX, TSC MHz). `Workshop --x86cpu --dos tools/x86-cpu/CPUDET.COM [--only P55C]`
  runs it on the FreeDOS disk (BROKENNES_FREEDOS) once per model and prints what it found.
- `--x86cpu --t386 test386_fN.bin [--only ...]`: test386.asm as the BIOS of each model (last POST code).

Build: `nasm -f bin cpuvec.asm -o cpuvec.bin`, `nasm -f bin cpudet.asm -o CPUDET.COM` (NASM 3.02, C:\BrokenNes-tools\dos\nasm).
Models: `CpuModel.Parse("LEGACY" | "386DX" | "386DX+387" | "486DX" | "486DX2" | "486DX4" | "P55C", optionally "@<MHz>")`.
