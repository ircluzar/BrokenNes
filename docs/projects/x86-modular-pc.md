# Modular x86 PC (386 / 486 / Pentium MMX 166, video card range, Windows 98)

User request 2026-11-15: keep the x86 PC modular (a 386, a 486, a Pentium MMX 166 CPU; a small range of video cards),
add what Windows 98 needs, then climb efficiency until a Pentium MMX 166 + S3 runs at 200% real time on the user's PC.

State on main (ca65ed52): `Windows/NesEmulator/x86/` has the tiny386-derived 386/486 core (`I386*.cs`, `gen` 3/4,
paging, V86) with our block JIT (`I386.Jit.cs`), `PcMachine` with 16 MB `DefaultMemory`, VGA via `Vga` + `IVgaPicture`
(Bochs20Vga / Dosbox072Vga) and PcemCga, sound via `IPcSoundCard`, disks via HLE BIOS INT 13 only.

## 1. Machine config

One immutable record, `PcConfig`, built by presets and editable in the desktop app:

```
PcConfig { CpuModel, ClockKHz, RamMB, Video, Sound[], Storage (Int13Hle | Ide), Bus (Isa | Pci430TX), Bios (Hle | SeaBios) }
```

* Presets: `DOS-386` (386DX-33, 4 MB, VGA, ISA, HLE BIOS) = today's default behaviour, `DOS-486` (486DX2-66, 16 MB, ET4000),
  `Win98-P166` (Pentium MMX 166, 64 MB, S3 Trio64, i430TX PCI, IDE + ATAPI, SB16-compatible later, SeaBIOS).
* The current default config stays byte-identical (battery goldens unchanged): new devices are only built when the config asks.
* Desktop: an "x86 machine" submenu in the core menus (catalog style, short labels), changes apply on reset.
* Save states: the config is serialized first (versioned block); a state loads only into the same config,
  mismatch is a visible error (no silent fallback). Old states without the block load as `DOS-386`/current default.
* `PcMachine` becomes a composition root: `PcMachine(PcConfig)` wires CPU, memory, bus, devices; ports and MMIO
  register through one `IoMap`/`MmioMap` so cards plug in without `PcMachine` knowing their internals.

## 2. CPU models

One core, a `CpuModel` descriptor, no forks:

| Model | gen | FPU | Cache | CPUID | Extras |
|---|---|---|---|---|---|
| 386DX | 3 | optional 387 (off) | none | no | AC flag absent |
| 486DX / DX2 / DX4 | 4 | on | 8 KB modelled as timing only | DX4/late SL: yes | AC flag, BSWAP, CMPXCHG, XADD, INVD/WBINVD, INVLPG |
| Pentium MMX 166 | 5 | on | timing only | yes (family 5 model 4, MMX bit) | RDTSC, CMPXCHG8B, RDMSR/WRMSR (TSC, few safe MSRs), CR4 (PSE, TSD, VME/PVI minimal), MMX regs aliased on FPU stack |

* Feature checks are flags on the descriptor (`HasCpuid`, `HasMmx`, ...) read once into readonly fields so the
  interpreter and JIT specialise without per-op branches; invalid opcodes on a lower model raise #UD exactly.
* MMX: 57 instructions in a new `I386.Mmx.cs` partial, sharing the FPU register file (EMMS/tag semantics).
* Timing: cycles-per-instruction tables per gen (386 / 486 / P5 approx). P5 pairing is NOT modelled; a flat
  per-class cost calibrated so that TopBench/Dhrystone land near real-machine scores. RDTSC = cycle counter scaled
  to ClockKHz, so Windows 98 and games that calibrate on the TSC see a believable 166 MHz.
* Validation: test386 + SingleStepTests 80386 per gen (from the x86-tests lane), plus a CPUID/feature table test.

## 3. Video cards

Common `IPcVideoCard`: ports, legacy A0000 window, optional linear framebuffer (MMIO), optional option ROM,
optional PCI config space, `Render(into frame)`, Save/Load. The current VGA implementations become the base adapter.

* `VGA` (current, default for DOS presets; Bochs20/Dosbox072 pictures unchanged).
* `Tseng ET4000AX` (ISA, SVGA banked modes, very good DOS compatibility, Win98 driver built in).
* `S3 Trio64` (PCI, LFB, 2D accel: BitBLT, rect fill, line, hardware cursor; Win98 built-in S3 driver; VBE 2.0 via its
  BIOS for DOS games). This is the Win98 target card.
* Cirrus GD5446 deferred (second PCI card only if S3 proves hard).
* Shared SVGA render path: 8/15/16/24/32 bpp linear scanout with dirty-line tracking, palette cache.

## 4. Storage, RAM, BIOS

* IDE: `Ide.cs` with primary (1F0-1F7/3F6, IRQ14) and secondary (170/376, IRQ15) channels, ATA PIO + multi-sector,
  LBA28, IDENTIFY; ATAPI CD-ROM on the secondary master reading ISO files (READ(10/12), TOC, mode sense, media change).
  Bus-master DMA (PIIX4) later, only if PIO is a measured bottleneck. HLE INT 13 stays for the DOS presets.
* RAM: config 4-128 MB, Win98 preset 64 MB; CMOS + E820 report it. Memory stays one flat array.
* PCI: i430TX/PIIX4-style host bridge + ISA bridge + IDE function, config mechanism #1 (CF8/CFC), PCI IRQ routing.
* BIOS decision: keep the HLE BIOS for DOS presets (fast boot, already proven); use SeaBIOS for the Win98 preset,
  because Windows 98 setup wants a real PnP/PCI/ACPI-or-APM BIOS and writing that HLE is a dead end.
  Downloads needed (NOT fetched, need the user's approval of each exact file):
  * SeaBIOS 1.16.3 source tarball, https://www.seabios.org/downloads/seabios-1.16.3.tar.gz, LGPLv3 (we build bios.bin ourselves
    with a small BrokenNes config; or the prebuilt `bios-256k.bin` from the same release line).
  * VGA BIOS: SeaVGABIOS is in the same tarball (LGPLv3) for VGA/Bochs-VBE; for the S3 card an S3 Trio64 option ROM
    is copyrighted, so we write a small own VBE option ROM (or HLE INT 10 VBE trap) instead; no download.
  * Optional fallback: Bochs BIOS `BIOS-bochs-latest` + LGPL VGABIOS 0.8b, https://github.com/bochs-emu/Bochs (LGPL), only if SeaBIOS has a blocker.
* Windows 98 media: only from the user. Never downloaded.

## 5. Speed plan

* Metric: `--x86bench <config> <workload> <seconds>` reports emulated cycles / (ClockKHz * wall seconds) = % real time,
  plus host ms per frame split by CPU / video / devices. Run on the user's main PC (the 200% target machine);
  fleet boxes for regression only (relative numbers).
* Workloads: Dhrystone, TopBench, Doom timedemo (when the user gives a Doom WAD... shareware is redistributable but still
  counts as a download needing approval), Quake timedemo, FreeDOS boot, later Win98 boot-to-desktop time.
* Expected biggest wins, in order:
  1. JIT coverage: compile the 32-bit protected/paged path (today the hot Win98 path), flags lazily, block linking,
     invalidation by 4 KB page write bitmap.
  2. Memory fast paths: software TLB (page -> host offset) with RAM/ROM/MMIO tag, so plain RAM accesses are one lookup;
     unaligned split only at page edges.
  3. Video: LFB writes go straight to RAM with a dirty bitmap, render only dirty lines; planar VGA write-mode fast paths;
     S3 accel ops as tight loops.
  4. Interrupts/timers: event scheduler (next-event cycle) instead of polling PIT/PIC/devices per block; batched
     device catch-up on port access.
  5. FPU/MMX: double-precision fast path (80-bit only where a test needs it), MMX as ulong ops.
* Target: Pentium MMX 166 + S3 Trio64 + 64 MB at >= 200% real time in Quake timedemo and Win98 desktop use;
  climb with the playtest checkpoint rule (report each step, hand a build at "near 200%").
* Guard: battery goldens + a frame-hash per DOS game on the default config after every speed commit.

## 6. Test plan

1. Per CPU model: test386, SingleStepTests 80386 (and 8088 for real mode), CPUID/feature table, MMX unit vectors (own).
2. Devices: IDE IDENTIFY/read/write tests via FreeDOS `fdisk`/`format`, ATAPI with a FreeDOS CD driver and an
   ISO we build ourselves; VBE modes via a small own test COM; S3 accel vs a software reference.
3. DOS games on every preset: existing Vaults DOS, Super VRUN DOS, catalog games; default preset frame-hash identical.
4. FreeDOS install from ISO onto an IDE image under SeaBIOS (dry run of the Win98 path).
5. Windows 98 SE setup from the user's ISO -> boot to desktop -> S3 driver at 800x600x16 -> timing/benchmarks.

## Phases

M1 PcConfig + IoMap + save-state block (no behaviour change) · M2 CPU models (486 FPU/CPUID, Pentium + MMX) ·
M3 IDE + ATAPI + RAM · M4 PCI + SeaBIOS (after download approval) · M5 ET4000 + S3 Trio64 + VBE ·
M6 speed climb to 200% · M7 Windows 98 with the user's media.
