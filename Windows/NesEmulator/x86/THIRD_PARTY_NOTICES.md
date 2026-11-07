# x86 console (pool card P3): provenance ledger

Started 2026-11-05 with milestone P3b (the CPU). The x86 console is an **isolated** system (decision of 2026-11-04,
`docs/projects/next-pool-plans.md`, P3): its chips are never slots of another console and no other console's chip is a slot of it.
The policy and the root list are in `/THIRD_PARTY_NOTICES.md`. **Update this file in the same change that adds or ports anything.**

## 1. Taken from source files (direct derivation)

| What | Where | Source | License |
| --- | --- | --- | --- |
| Intel 80386 / 80486 CPU core | `I386.cs`, `I386.Exec.cs`, `I386.System.cs` | tiny386 (github.com/hchunhui/tiny386) `i386.c`, `i386ins.def`, `i386.h`, commit 2c1205c (local pinned copy, read only), Copyright (c) 2024-2025 Chunhui He | BSD-3-Clause (text in the root ledger, section 6). Permissive: no exception to the policy needed. |
| 8259A interrupt controllers (master + slave) | `Pic8259.cs` | tiny386 `i8259.c` @2c1205c, from QEMU, Copyright (c) 2003-2004 Fabrice Bellard | MIT (text in the root ledger, section 6) |
| 8254 interval timer | `Pit8254.cs` | tiny386 `i8254.c` @2c1205c, from QEMU, Copyright (c) 2003-2004 Fabrice Bellard | MIT. Changed: time is the machine's deterministic clock in PIT ticks (instruction count), not host microseconds; the IRQ check compares 64-bit times; `NextIrq0` added (a halted CPU sleeps to it). |
| VGA registers and CPU memory access (chain 4, odd/even, latched write modes 0-3, read modes 0-1), the graphics picture (line offset, double scan, split screen, panning, 16 / 4 / 256 colour shifts) | `Vga.cs` | tiny386 `vga.c` @2c1205c, from QEMU, Copyright (c) 2003-2017 Fabrice Bellard | MIT. Left out: VBE / Bochs DISPI, PCI, the terminal mirror, the scalers, CGA composite. The text picture, the retrace timing (449 lines at 70 Hz on the machine clock), the mode tables (standard IBM VGA register values) and the default 256-colour DAC are written for this project. |
| The 8x16 code page 437 font | `VgaFont.cs` | tiny386 `vgafont.c` @2c1205c (the font QEMU ships), under tiny386's licence | BSD-3-Clause |
| Yamaha YM3812 (OPL2), the AdLib's FM chip (ports 388h / 389h) | `Opl2.cs`, `Opl2.Tables.cs`; verifier `Workshop/X86/X86OplCli.cs` (`--x86opl`) | ymfm (Aaron Giles) commit 81aec25 (local pinned copy `C:\BrokenNes-tools\sega\port-sources\ymfm`, read only): `ymfm_opl.h` / `ymfm_opl.cpp` (`opl_registers_base<2>`: register decode, `opl_key_scale_atten`, the OPL2 waveforms, `cache_operator_data`, `opl_compute_phase_step`, `opl_clock_noise_and_lfo`, the key-on writes; class `ym3812`: reset, status, address / data writes, `generate`), `ymfm_fm.h` / `ymfm_fm.ipp` (`fm_engine_base`: clock, output, prepare, mode write, timers, status / IRQ bits; `fm_channel`: `output_2op` with the OPL modulator delay, `output_rhythm_ch6/7/8`; `fm_operator`: envelope, phase, key state, `compute_volume`), `ymfm.h` (`roundtrip_fp`, the YM3014 DAC truncation). The sine, power and increment tables were copied by script from `sega/Ym2612.Tables.cs` (itself script-extracted from `ymfm_fm.ipp`), not shared: the console is isolated. Copyright (c) 2021 Aaron Giles | BSD-3-Clause (text in the root ledger, section 6). Changed: one class for engine + channels + operators, OPL2 paths only (no SSG-EG, 4-op, OPL3 outputs, depress / reverb states); no `ymfm_interface`: time is the machine clock (the chip at 3 x the PIT clock, a sample every 72 clocks), the timers are chip-clock deadlines run in time order with the samples (so AdLib detection works headless and deterministically), no busy flag, the IRQ line not wired (the AdLib card has none); added: a 44.1 kHz linear-interpolation resampler, mono to stereo, save state, an `Enabled` flag for the sound (timers always run). Verified sample for sample against ymfm itself: 110,000 raw samples of a scripted + fuzzed register sequence (rhythm mode, waveforms, LFO, KSL / KSR, feedback, both algorithms) identical. |

Written for this project (no source copied): the board `PcMachine.cs`, the **C# BIOS** `PcMachine.Bios.cs` / `PcMachine.Video.cs` (no BIOS
ROM file, no SeaBIOS / VGABIOS code: POST, vectors, BIOS data area, INT 10h / 11h / 12h / 13h / 15h / 16h / 1Ah / 19h, built from the
public PC/AT BIOS interface documentation, the way DOSBox answers BIOS calls in its own code), the keyboard controller `Kbc8042.cs`
(tiny386 `i8042.c` was read only for which commands DOS software sends), the CMOS clock `Cmos.cs`, `DiskImage.cs`, `FatImage.cs`,
`I386.State.cs` (save states), the console session `PcSession.cs`, the boot-disk builder `PcDisks.cs`, the PC speaker `PcSpeaker.cs`
(port 61h bit 1 AND the 8254 channel 2 output, drawn on the machine clock), and the AdLib's port wiring and its mixing with the speaker in `PcMachine.cs` / `PcSession.cs`.

**FreeDOS: shipped with the desktop release, never in the repository** (user decision of 2026-11-06, "Ship FreeDOS with the app"). The FreeDOS 1.4
LiteUSB image `FD14LITE.img` (FreeDOS project; the kernel, FreeCOM and most of `\FREEDOS\BIN` GPL-2.0 or later, other programs under their own
free licences, each stated in its package's `APPINFO\*.LSM`) is copied unmodified into the desktop build's `x86\freedos\` by
`Windows/BrokenNes.Windows.csproj` from the developer toolkit (`C:\BrokenNes-tools\dos\freedos`, MSBuild property `FreeDosImage`), beside
`Windows/x86/freedos/README.txt` (what it is, the licences, and that the source is included: GPL section 3(a)) and
`Windows/x86/freedos/GPL-2.0.txt` (the GPL version 2 text, verbatim). It is a **separate, aggregated program**: BrokenNes runs it inside the
emulated PC from a disk image and links nothing of it. `PcDisks.ForProgram` puts a DOS program on an in-memory copy of the image (our own
FDCONFIG.SYS / FDAUTO.BAT, `\PACKAGES` dropped); that copy is the game's save. The complete corresponding source ships beside it in `x86\freedos\source\` (from the toolkit,
`C:\BrokenNes-tools\x86\freedos-1.4-src\source`, MSBuild property `FreeDosSource`): the 65 package zips of the image's `\PACKAGES` plus grep,
doslfn, srdisk and cdrcache from the FreeDOS 1.4 LegacyCD (installed in the image's `\FREEDOS\BIN`), all unmodified, each with its `SOURCE\`;
`SOURCES.txt` there lists their SHA-256 and origin. No written offer is needed.
The tests build their boot disks from the same toolkit image.

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

Workshop `--x86dev` (the board, 2026-11-06): 11 boot-sector programs (NASM, assembled at test time with the toolkit's pinned NASM) on a fresh
machine: PIT + PIC (count read-back, status, 1 kHz rate on the machine clock), the BIOS keyboard (INT 16h waiting in HLT, shift, E0 keys)
and the 8042 directly (own INT 9, self test, identify), CMOS / INT 1Ah, text mode through INT 10h (cells, attributes, cursor, the
rendered glyphs), mode 13h (DAC, pixels, INT 10h 0Ch / 0Dh), Mode X (unchained planes, 320x240, page flip), 70 Hz retrace on 3DAh, INT 13h
(geometry, CHS read / write, missing drives), save state + load into a new machine (identical run): 100/100 checks.
Workshop `--x86disk` + `--x86boot`: FreeDOS 1.4 (kernel 2043, FreeCOM 0.86) boots from a disk built from the LiteUSB image to `C:\>` in
2.1 s of machine time; `mem` and `dir` run.

## AdLib engines (APU harvest, 2026-11-07): ported from other emulators, a documented exception (THIRD_PARTY_NOTICES.md section 2a, docs/KNOWN_GAPS.md FA-1)

Art instruments, ported faithfully with their flaws (listed in each file's header). Sources as fetched, with sha256 manifests: `C:\BrokenNes-tools\apu-harvest\<name>\`.

| x86 APU id | File | Source | License |
|---|---|---|---|
| ADLIB_M036 | `OplEngines.cs` (adapter) + `sega/Mame036Opl.cs` | MAME 0.36 `src/sound/fmopl.c`, (C) 1999 Tatsuyuki Satoh | MAME license of the time (non-commercial) |
| ADLIB_D072 | `Dosbox072Opl.cs` | DOSBox 0.72 (SVN tags/RELEASE_0_72, r2883) `src/hardware/fmopl.c` = MAME fmopl 0.70, (C) 2002-2003 Jarek Burczynski, 1999-2000 Tatsuyuki Satoh; https://sourceforge.net/p/dosbox/code-0/HEAD/tree/dosbox/tags/RELEASE_0_72/src/hardware/ | no license stated in the file (MAME-derived) |
| ADLIB_D074C | `Dosbox074CompatOpl.cs` | DOSBox 0.74 (tags/RELEASE_0_74, r3548) `src/hardware/opl.cpp`, (C) 2002-2010 The DOSBox Team, based on Ken Silverman's ADLIBEMU | LGPL-2.1+ (https://www.gnu.org/licenses/old-licenses/lgpl-2.1.txt) |
| ADLIB_D074F | `Dosbox074Dbopl.cs` | DOSBox 0.74 (tags/RELEASE_0_74, r3582) `src/hardware/dbopl.cpp`, (C) 2002-2010 The DOSBox Team (Edward van der Spek) | GPL-2.0+ (https://www.gnu.org/licenses/old-licenses/gpl-2.0.txt) |
| ADLIB_NUK174 | `Nuked174Opl.cs` | Nuked-OPL3 1.7.4 by Alexey Khokholov (nukeykt), as shipped in AdPlug tag adplug-2.3.1 `src/nukedopl.c`; https://github.com/adplug/adplug/tree/adplug-2.3.1/src | LGPL-2.1+ |
