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
| ZSNES 0.15 sound engine (mono 44.1 kHz mixer, BRR decode, envelope state machine, volume and rate tables, key on/off, the original's bugs), ported line by line | `Zs15Dsp.cs` (used by `APU_ZS15.cs`) | zsnes-asm2c `dsp.asm` + `mach.c` (https://gitlab.com/ReverendGumby/zsnes-asm2c), reconstructed ZSNES 0.15 source | GPL-2.0-or-later (`dsp.asm`) / GPL-3.0-or-later (zsnes-asm2c): a documented exception, root `THIRD_PARTY_NOTICES.md` section 2 |
| OSPC: libOpenSPC (2002) S-DSP engine (dsp.c, gauss.h, SPCimpl.c DSP write hook) | `OspcDsp.cs`, `APU_OSPC.cs` | github.com/bmartin427/libopenspc @2f68400 (1-9-2003 snapshot), Brad Martin | LGPL-2.1+ (SPCimpl.c hook: SNEeSe Clarified Artistic License): a documented exception, root `THIRD_PARTY_NOTICES.md` section 2 |
| Snes9x 1.43 sound engine (stereo 32,040 Hz mixer, BRR decode, old envelope state machine with ms rate tables, linear interpolation, private echo/FIR, S9xSetAPUDSP key-on/off logic, the original's quirks), ported line by line | `S9x1Dsp.cs` (used by `APU_S9X1.cs`) | libretro/snes9x2005 `source/soundux.c` + `source/apu.c` (https://github.com/libretro/snes9x2005 @a79dfe9), a C port of Snes9x 1.43 | Snes9x license (non-commercial only), with the ndssfc layer under GPL-2.0-or-later: a documented exception, root `THIRD_PARTY_NOTICES.md` section 2 |
| Snes9x 1.51 sound engine (soundux mixer, gaussian interpolation, counter-table envelopes, its echo, `S9xSetAPUDSP`), the last before 1.52's SNES_SPC | `S951Dsp.cs` (used by `APU_S951.cs`) | Snes9x 1.51 via github.com/TheProjecter/snes9x-rr (`snes9x-151/soundux.cpp`, `soundux.h`, `apu.cpp`, `apu.h`) | Snes9x license (non-commercial only): a documented exception, root `THIRD_PARTY_NOTICES.md` section 2 |
| ZSNES 1.51 S-DSP, hand-ported from x86 asm (its envelopes and bend tables, 127/255-sample key-on/key-off fades, private echo, look-ahead Gaussian, pitch-mod a frame late), default path only | `Zs151Dsp.cs` (used by `APU_ZS151.cs`) | ZSNES 1.51 `src/cpu/dspproc.asm`, `src/cpu/dsp.asm`, `src/ui.c` setnoise, `src/init.asm` MusicVol (github.com/emillon/zsnes, tag upstream/1.510) | GPL-2.0 (ZSNES Team): a documented exception, root `THIRD_PARTY_NOTICES.md` section 2 |
| Snes9x 1.16a sound engine (1998 soundux: 22 kHz stereo, no interpolation, the active PITCH_MOD guess, half-speed envelopes, straight-line GAIN modes, BRR sample cache, private echo) | `S916Dsp.cs` (used by `APU_S916.cs`) | Snes9x 1.16a via sources.debian.org (snes9x 1.16a-1: `soundux.cpp/h`, `apu.cpp/h`, `globals.cpp`), (c) 1996-1998 Gary Henderson, Jerremy Koot | Snes9x license (non-commercial; the notice must stay with derived works): a documented exception, root `THIRD_PARTY_NOTICES.md` section 2 |
| SNEmulDS ARM7 DSP (PocketSPC core by gladius): `dsp.c` + the ARM `dspMixer.S` translated line by line (no interpolation, 0x7800 envelope counter, echo fed by every voice with a self-patching no-op, 63/62 frame blocks, silent noise) | `SnemulDsDsp.cs` (used by `APU_SNDS.cs`) | github.com/cotodevel/snemulds @79762ad `arm7/source/dsp.c`, `dsp.h`, `dspMixer.S` (README credits: Archeide, gladius, Bubble2k, Coto) | GPL-2.0 (no per-file notices): a documented exception, root `THIRD_PARTY_NOTICES.md` section 2 |
| PocketSPC 0.3 ARM7 DSP (gladius): `dsp.cpp` + the ARM `dspMixer.S` translated line by line (24 kHz, no interpolation, no echo: EON adds the echo volume to the voice volume, unclamped 32-bit BRR filters, loop-block bug, right main volume = re-scaled left, silent noise) | `PocketSpc03Dsp.cs` (used by `APU_PSPC3.cs`) | [GameBrew PocketSPC](https://www.gamebrew.org/wiki/PocketSPC), archive `pocketspc.7z` -> `source/pocketspc-src-v3.zip`, `arm7/source/dsp.cpp`, `dsp.h`, `dspMixer.S`, `spcme.h` (gladius; the arm9 snes9x reference copy is the Snes9x team's, not ported) | "Mixed" (GameBrew; no notice in the files): a documented exception, root `THIRD_PARTY_NOTICES.md` section 2a |

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
  committed. Real firmware is optional: when the user supplies it (`dsp1b.rom`, 8192 bytes),
  `Workshop/SnesFirmware.cs` finds it in `$BROKENNES_SNES_FIRMWARE`, the ROM's folder, `firmware\`
  beside it, `%APPDATA%\BrokenNes\Firmware` or `firmware\` beside the executable, and it runs on the
  µPD77C25 core. Without it, the homemade DSP-1 below is used (`SnesChips.Create`).
- **Homemade DSP-1** (`DSP1_SFC.cs`, added 2026-09-26): a high-level reimplementation that contains no
  Nintendo code or data tables. Its sine, square root and reciprocal are plain `Math` calls. The
  sources are:
  - the published description of each command (what goes in and what comes out);
  - the camera geometry, derived from first principles;
  - black-box fitting against transactions recorded from the real firmware running on our own
    µPD77C25 core: a Mario Kart session plus random probes (`Workshop/Dsp1LabCli.cs`).

  The rounding rules came from that fitting, for example:
  - Rotate floors each Q15 product;
  - the raster denominator floors `line*sin` and `Les*cos` separately;
  - the eye height is a whole number;
  - the square root is scaled by `1 - 2^-14`;
  - the horizon hold is at Azs 14533.

  One hint came from memory: HLE emulators of the snes9x lineage form the raster scale as the
  reciprocal of `Vs*sin Azs + VOffset`. That was recalled, not opened, and the formula in the file
  was fitted to the recorded data. Accuracy against the chip on Mario Kart's own traffic:
  - exact on the majority of results, and almost always within 1;
  - Parameter's Vof and Vva are 100% exact;
  - Rotate, Triangle and Distance are about 98% exact;
  - Raster values are within 2.

## 5. Test data

- `Windows/Resources/snes-test-roms/`: gilyon/snes-tests v1.4, MIT (license included there).

## Plan: from-scratch reference replacements

The current cores stay as the **reference** implementation (the oracle). Independent cores will be
written from documentation plus black-box differential testing against the reference and the test
ROMs, without reading the sources in section 1. Section 2's items should be re-derived from
documentation in those cores.
| TYL picture core (PPU harvest round 2): Snes9xTYL approx. software renderer | `Snes9xTylGfx.cs` | Ported from 173210/snes9xTYL @0f82df6 gfx.cpp (S9xUpdateScreen software path, DrawBackgroundNew, DrawOBJSNew, Mode 7 New drawers), Snes9x (c) 1996-2002 Gary Henderson, Jerremy Koot and team, Snes9xTYL by Yoyofr and the PSP port contributors; Snes9x licence (non-commercial, notice kept). Art port, flaws kept. |
| S9X picture core (PPU harvest): gfx.cpp + tile.cpp + clip.cpp renderer | `Snes9xGfx.cs` | Ported from libretro/snes9x @88f3702 (parent of 795542d), (c) 1996-2011 Gary Henderson, Jerremy Koot, Matthew Kendora, Peter Bortas, Joel Yliluoma, John Weidman, funkyass, Kris Bleakley, Brad Jorsch, Nach, zones, nitsuja, BearOso, OV2 and others; Snes9x licence (non-commercial, notice kept). Faithful port, flaws kept. |
| BS63 picture core (PPU harvest): bsnes v063 bPPU scanline renderer | `BsnesV063Render.cs` | Ported from the bsnes_v063.tar.bz2 release (src/ppu/bppu/render), (c) 2004-2010 byuu, mode 7 base algorithm anomie, colour add/sub blargg; GPL-2.0. Faithful port, flaws kept. |
