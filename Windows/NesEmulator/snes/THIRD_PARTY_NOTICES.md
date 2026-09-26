# SFC core family: provenance and third-party notices

Written 2026-09-25 when the SFC (SNES) cores were created. Every item that did not come from the
author's own work or from public hardware documentation is listed here, graded by how directly it
was taken. Keep this file current: a "from scratch" replacement core should be checked against it.

## 1. Taken from source files (direct derivation)

| What | Where in BrokenNes | Source | License |
|---|---|---|---|
| S-DSP algorithm: envelope (ADSR/GAIN) state machine, BRR decode and filters, Gaussian interpolation arithmetic, noise LFSR, KON/KOFF every-other-sample polling, echo FIR ordering and truncation, echo buffer addressing. Ported to C# and restructured from a per-clock pipeline into a whole-sample loop, but the logic follows the source closely. | `DSP_SFC.cs` | blargg's snes_spc 0.9.0 `SPC_DSP.cpp` (Shay Green, 2007), read from the copy bundled in bsnes inside BizHawk (`!!! DUMP\BizHawkLibretro\waterbox\bsnescore\bsnes\sfc\dsp\SPC_DSP.cpp`) | LGPL 2.1 or later |
| 512-entry Gaussian interpolation table (hardware ROM data, copied verbatim) | `DSP_SFC.cs` `Gauss` | same file | LGPL 2.1 file; the data itself is measured hardware |
| Envelope/noise rate period and offset tables (copied verbatim) | `DSP_SFC.cs` `RatePeriod`, `RateOffset` | same file | as above |
| SPC700 per-opcode cycle-count table (copied verbatim) | `SMP_SFC.cs` `Cycles` | snes9x `apu/bapu/smp/core.cpp` (bsnes-derived S-SMP core), same BizHawk tree | snes9x license (non-commercial) / GPL-3 lineage; the data itself is hardware timing |

## 2. Written from memory, recognizably bsnes/higan's approach (no file opened)

These were written without looking at any source, but the structure or formula matches bsnes/higan
(byuu et al.) closely enough that it should be credited and treated as derived for clean-room work.

| What | Where |
|---|---|
| 65816 decimal-mode ADC/SBC: nibble-by-nibble adjust, V computed before the final adjust | `CPU_SFC.cs` `Adc`, `Sbc` |
| Emulation-mode stack model (6502 page-1 push/pull vs. "N" variants followed by S.h=1) and the `Direct`/`DirectN` split for page-wrapping direct page | `CPU_SFC.cs` |
| MVN/MVP per-byte step, re-executing via PC-=3 | `CPU_SFC.cs` `BlockMove` |
| Mode 7 transform: the `& ~63` pre-rounding and 13-bit clip | `PPU_SFC.cs` `RenderMode7`, `Clip13` |
| Color-window "above/below" region semantics for clip-to-black / math-enable | `PPU_SFC.cs` `ComposePixel` |
| HDMA line-counter reload/advance sequence | `BOARD_SFC.cs` `HdmaReload`, `RunHdmaLine` |
| SPC700 DIV overflow formula, ADDW/SUBW as two chained 8-bit ADC/SBCs, DAA/DAS conditions | `SMP_SFC.cs` `Div`, opcodes 7A/9A/BE/DF |

## 3. Hardware facts (documented behavior; any correct emulator shares them)

Opcode semantics and flag behavior; the memory map and access speeds (6/8/12 master clocks);
DMA transfer patterns; sprite size table; BG mode bit depths and priority order; direct-color
formula; the IPL upload protocol; timer rates; the master clock and scanline constants; the
auto-joypad duration (4224 master clocks); the undocumented 65816 emulation-mode quirks listed in
the cputest README. Sources for these are public documentation (fullsnes, anomie's docs, the WDC
65816 datasheet) plus the test ROMs.

## 4. Deliberately NOT taken

- **The boot loader** (`APU_SFC.cs` `Ipl`) is not Nintendo's IPL ROM. It was written from the
  protocol. Its wait-for-index-0 loop and "cmp y,port / bpl" poll are forced by the protocol and
  resemble the original, which the author has seen. Layout, pointer location and command dispatch
  are different.
- **Nothing from Mesen** has been used so far.

## 5. Test data

- `Windows/Resources/snes-test-roms/`: gilyon/snes-tests v1.4, MIT (license included there).

## Plan: from-scratch reference replacements

The current cores stay as the **reference** implementation (the oracle). Independent cores will be
written from documentation plus black-box differential testing against the reference and the test
ROMs, without reading the sources in section 1. Section 2's items should be re-derived from
documentation in those cores.
