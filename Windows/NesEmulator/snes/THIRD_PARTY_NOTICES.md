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
| µPD77C25 (DSP-1) core, added 2026-09-26: OP/RT/JP/LD decode order, ALU carry taken from the *other* accumulator, SHL2/SHL4 shifting ones in, the OV1/S1 update `s1 = ov1 ^ !sign; ov1 = !ov1`, SGN = `0x8000 - S1(A)`, KLR/KLM (`k = RAM[dp \| 0x40]`), the SR write mask `0x907C`, DR 8/16-bit handshake, DP/RP modifiers skipped when the move targets DP/RP, product M/N computed after every instruction. Structure follows bsnes/higan's `upd96050` from memory. **Unverified:** the SGN/S1 semantics (used by 2 of 2048 DSP-1B instructions). | `NECDSP_SFC.cs` |
| SA-1, added 2026-09-26: register map and bit layouts (fullsnes-level documentation), but the character-conversion loops (type 1 tile-at-a-time conversion into I-RAM on the SNES's BW-RAM reads, type 2 BRF line conversion address formula), the signed-dividend/unsigned-divisor division with non-negative remainder, the variable-length reader stepping, DMA triggers on DDA writes, and the SA-1 access costs (ROM/I-RAM 1 cycle, BW-RAM 2) follow bsnes/higan's `sa1` from memory. Bus conflicts are not modeled; the timer's linear mode is approximated. | `SA1_SFC.cs` |
| Super FX (GSU), added 2026-09-26: the one-byte pipeline model (`pipe()` with R15 pointing one past the pipelined byte, STOP reloading the pipeline with NOP, writes to R15 suppressing the increment), the 2-entry pixel cache and its flush/merge, the character-number formulas per screen height and OBJ mode, the `(n >> 1) << 4 + (n & 1)` bitplane offsets, MERGE's flag rules, the cache-line fill, the locked-out SNES vector table (`00 01 00 01 04 01 ...`), and access costs (5/6 master clocks per ROM/RAM access, 1/2 per cached opcode) follow bsnes/higan's `superfx` from memory. The "branches keep the prefix" rule was found by differential tracing against **Mesen 2** (a black-box run, no Mesen source read). The background ROM/RAM buffer model (R14 write starts a fetch that lands 5/6 clocks later, one-entry RAM write buffer, immediate RAM reads, sync before dependent accesses) follows bsnes's `romcl`/`ramcl` countdowns from memory, and was confirmed by black-box cycle-counter comparison with Mesen 2 (identical clocks over 3,000-instruction windows in Star Fox and Yoshi's Island). | `GSU_SFC.cs` |
| SPC700 catch-up rule (run an instruction only if it ends by the CPU's time) and DRAM refresh (40 clocks at H=538): own design and documented hardware behavior, each kept only after the Mesen 2 timing fingerprint improved. | `APU_SFC.cs`, `BOARD_SFC.cs` |

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
  are different. On 2026-09-27 its byte loop was reordered to acknowledge before storing, so the
  steady-state loop costs 25 SPC cycles like the console's. Games pace uploads on that ack, and
  timing against Mesen 2 required it. This brings the loop closer to the original's shape; the code
  was still written from the protocol and the cycle target, not copied.
- **No Mesen source** has been read. Mesen 2.1.1 has been used only as a black-box reference
  (Lua `--testRunner` logging Super FX registers at chosen addresses, compared event by event).
- **Coprocessor firmware** (DSP-1/DSP-1B program ROMs, NEC/Nintendo copyright) is never bundled or
  committed. The user supplies it (`dsp1b.rom`, 8192 bytes); `Workshop/SnesFirmware.cs` searches
  `$BROKENNES_SNES_FIRMWARE`, the ROM's folder, `firmware\` beside it, `%APPDATA%\BrokenNes\Firmware`
  and `firmware\` beside the executable.

## 5. Test data

- `Windows/Resources/snes-test-roms/`: gilyon/snes-tests v1.4, MIT (license included there).

## Plan: from-scratch reference replacements

The current cores stay as the **reference** implementation (the oracle). Independent cores will be
written from documentation plus black-box differential testing against the reference and the test
ROMs, without reading the sources in section 1. Section 2's items should be re-derived from
documentation in those cores.
